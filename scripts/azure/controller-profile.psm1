#Requires -Version 7.4
Import-Module (Join-Path $PSScriptRoot '../host-enrollment.psm1') -DisableNameChecking

function Assert-ControllerStorageProfile($Tags, $Template, [object[]]$Secrets) {
    $conversation = $Tags.nativePersistence -ceq 'conversation-rollouts'
    if ($Tags.nativePersistence -and -not $conversation) { throw 'Unknown controller persistence policy.' }
    $secrets = @($Secrets | Where-Object { $null -ne $_ })
    foreach ($secret in $secrets) {
        if ($conversation) {
            if ($secrets.Count -ne 1 -or $secret.name -cne 'controller-enrollment' -or
                    $secret.keyVaultUrl -or $secret.identity) { throw 'Require the dedicated native enrollment secret.' }
        } elseif ($secret.keyVaultUrl -cnotmatch '\Ahttps://[a-z0-9-]+\.vault\.azure\.net/secrets/[a-zA-Z0-9-]+/[0-9a-f]{32}\z' -or
                [string]::IsNullOrWhiteSpace($secret.identity)) { throw 'Require versioned managed-identity secret references.' }
    }
    $storageType = if ($conversation) { 'AzureFile' } else { 'NfsAzureFile' }
    $mounts = @($Template.containers[0].volumeMounts)
    foreach ($pair in @(@('/data','runtime/data'),@('/workspace','runtime/workspace'))) {
        $mount = @($mounts | Where-Object { $_.mountPath -ceq $pair[0] })
        if ($mount.Count -ne 1 -or $mount[0].subPath -cne $pair[1] -or
                @($Template.volumes | Where-Object { $_.name -ceq $mount[0].volumeName -and $_.storageType -ceq $storageType }).Count -ne 1) {
            throw 'Retained private persistent storage differs.'
        }
    }
    if (-not $conversation) { return }
    $environment = @($Template.containers[0].env)
    if ($environment.Count -ne 1 -or $environment[0].name -cne 'CODEX_SQLITE_HOME' -or
            $environment[0].value -cne '/tmp/repoagent-codex-sqlite' -or $environment[0].secretRef) {
        throw 'Conversation persistence requires the retained local SQLite setting.'
    }
    $active = $Tags.purpose -ceq 'controller-active'
    $expectedInitializer = if ($active) { 'initialize-active' } else { 'initialize' }
    $expectedCommand = if ($active) { 'activate' } else { 'serve' }
    if ($Template.initContainers[0].args[1] -cne $expectedInitializer -or
            $Template.containers[0].args[1] -cne $expectedCommand) { throw 'Controller setup/activation selection differs.' }
    $privateMount = @($mounts | Where-Object { $_.mountPath -ceq '/data' })[0]
    $initializerMounts = @($Template.initContainers[0].volumeMounts)
    if ($initializerMounts.Count -ne 1 -or $initializerMounts[0].mountPath -cne '/mnt/controller' -or
            $initializerMounts[0].subPath -or $initializerMounts[0].volumeName -cne $privateMount.volumeName -or
            @($Template.volumes).Count -ne (2 + [int]$active)) { throw 'Initializer must retain the same private SMB share root.' }
    $views = @{
        '/data' = @('runtime/data','0600')
        '/workspace' = @('runtime/workspace','0700')
        '/data/tools' = @('runtime/data/tools','0700')
        '/data/state/bridge.json.github-app/bin' = @('runtime/data/state/bridge.json.github-app/bin','0700')
        '/data/state/bridge.json.github-app/clone-bin' = @('runtime/data/state/bridge.json.github-app/clone-bin','0700')
    }
    if ($mounts.Count -ne ($views.Count + [int]$active)) { throw 'Unexpected controller volume mount.' }
    foreach ($path in $views.Keys) {
        $mount = @($mounts | Where-Object { $_.mountPath -ceq $path })
        if ($mount.Count -ne 1 -or $mount[0].subPath -cne $views[$path][0]) { throw 'Retained SMB view differs.' }
        $volume = @($Template.volumes | Where-Object { $_.name -ceq $mount[0].volumeName })
        if ($volume.Count -ne 1 -or $volume[0].storageType -cne 'AzureFile' -or $volume[0].storageName -cne 'controller-state') {
            throw 'Retained SMB storage differs.'
        }
        $expected = 'vers=3.1.1,uid=1000,gid=1000,dir_mode=0700,file_mode=' + $views[$path][1] + ',serverino,mfsymlinks'
        if ((@($volume[0].mountOptions.Split(',') | Sort-Object -CaseSensitive) -join ',') -cne
                (@($expected.Split(',') | Sort-Object -CaseSensitive) -join ',')) { throw 'Retained SMB permission or locking options differ.' }
    }
    if ($active) {
        $mount = @($mounts | Where-Object { $_.mountPath -ceq '/run/enrollment' })
        $volume = @($Template.volumes | Where-Object { $_.name -ceq $mount[0].volumeName })
        if ($secrets.Count -ne 1 -or $mount.Count -ne 1 -or $mount[0].subPath -or
                $volume.Count -ne 1 -or $volume[0].storageType -cne 'Secret' -or
                @($volume[0].secrets).Count -ne 1 -or $volume[0].secrets[0].secretRef -cne 'controller-enrollment' -or
                $volume[0].secrets[0].path -cne 'enrollment.json') { throw 'Retained native enrollment mount differs.' }
    }
}

function Get-NativeEnrollmentFingerprint([object[]]$Declared, [object[]]$Secrets) {
    if ($Declared.Count -ne 1 -or $Declared[0].name -cne 'controller-enrollment' -or
            $Declared[0].keyVaultUrl -or $Declared[0].identity -or
            $Secrets.Count -ne 1 -or $Secrets[0].name -cne 'controller-enrollment' -or
            $Secrets[0].keyVaultUrl -or $Secrets[0].identity -or $Secrets[0].value -isnot [string] -or
            $Secrets[0].value.Length -lt 1 -or $Secrets[0].value.Length -gt 65536) {
        throw 'Native enrollment secret inventory is incomplete or differs.'
    }
    return Get-ValueHash $Secrets[0].value
}

function Assert-ControllerTemplateAnchor($App, [string]$Revision) {
    if ($App.properties.latestRevisionName -cne $Revision -or $App.properties.latestReadyRevisionName -cne $Revision) {
        throw 'The initial retained template is not anchored to the sole active ready revision.'
    }
}

function Get-ComparableControllerTemplate($Template, $RetainedTemplate) {
    if (@($Template.containers).Count -ne 1 -or $Template.containers[0].name -cne 'controller' -or
            @($Template.initContainers).Count -ne 1 -or $Template.initContainers[0].name -cne 'private-layout') {
        throw 'Revision does not have the retained controller and initializer.'
    }
    $snapshot = $Template | ConvertTo-Json -Depth 50 -Compress | ConvertFrom-Json -AsHashtable
    $snapshot.Remove('revisionSuffix')
    $snapshot.containers[0].Remove('image')
    # Revision GET can omit explicit scaling values; only its anchored retained
    # app template may supply them. Explicit revision values are never replaced.
    foreach ($field in @('pollingInterval','cooldownPeriod')) {
        $expected = $RetainedTemplate.scale[$field]
        if ($expected -isnot [int] -and $expected -isnot [long]) { throw 'Retained scaling interval is absent or invalid.' }
        if ($null -eq $snapshot.scale[$field]) { $snapshot.scale[$field] = $expected }
    }
    $rules = @($snapshot.scale.rules)
    $names = @($rules | ForEach-Object { $_.name })
    if (@($names | Where-Object { $_ -isnot [string] -or -not $_ }).Count -or
            @($names | Sort-Object -CaseSensitive -Unique).Count -ne $rules.Count) { throw 'Scale rule names must be unique.' }
    $snapshot.scale.rules = @($rules | Sort-Object -Property name -CaseSensitive)
    $cpu = $snapshot.containers[0].resources.cpu
    $defaultStorage = if ($cpu -le 0.25) { '1Gi' } elseif ($cpu -le 0.5) { '2Gi' } elseif ($cpu -le 1) { '4Gi' } else { '8Gi' }
    foreach ($container in @($snapshot.containers) + @($snapshot.initContainers)) {
        if ($null -eq $container.resources.ephemeralStorage) {
            if ($container.resources.cpu -ne $cpu) { throw 'Unreported storage allocation cannot be compared for different initializer resources.' }
            $container.resources.ephemeralStorage = $defaultStorage
        }
    }
    return $snapshot
}

function New-ControllerImagePatch($Template, [string]$Image, [string]$Suffix, [string]$Location) {
    $copy = $Template | ConvertTo-Json -Depth 50 -Compress | ConvertFrom-Json -AsHashtable
    $copy.containers[0].image = $Image
    $copy.revisionSuffix = $Suffix
    return @{ location=$Location; properties=@{template=$copy} }
}

function Write-ControllerImagePatch([string]$Path, $Value) {
    Assert-PrivatePath ([IO.Path]::GetDirectoryName($Path)) -Directory
    $text = $Value | ConvertTo-Json -Depth 50 -Compress -WarningAction Stop
    # This temporary Azure request supports fractional CPU, unlike enrollment JSON.
    $document = [Text.Json.JsonDocument]::Parse($text)
    $document.Dispose()
    $options = [IO.FileStreamOptions]::new()
    $options.Mode = [IO.FileMode]::CreateNew; $options.Access = [IO.FileAccess]::Write; $options.Share = [IO.FileShare]::None
    if ($IsLinux) { $options.UnixCreateMode = [IO.UnixFileMode]384 }
    $stream = [IO.FileStream]::new($Path, $options)
    try {
        Set-PrivateFileAcl $Path
        Assert-PrivatePath $Path
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($text)
        $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true)
    } finally { $stream.Dispose() }
    Assert-PrivatePath $Path
    if ([IO.File]::ReadAllText($Path) -cne $text) { throw 'Private Azure request differs from its prepared template.' }
}

Export-ModuleMember -Function Assert-ControllerStorageProfile, Get-NativeEnrollmentFingerprint, Assert-ControllerTemplateAnchor, Get-ComparableControllerTemplate, New-ControllerImagePatch, Write-ControllerImagePatch
