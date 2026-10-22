#Requires -Version 7.4
<# .SYNOPSIS
Replace one hosted controller image with deliberate downtime and explicit rollback.
Preview is the default. Bicep continues to own initial resources and settings.
#>
[CmdletBinding()]
param(
    [ValidateSet('Update','Rollback','Status')][string]$Action = 'Update',
    [string]$SubscriptionId,
    [string]$ResourceGroup,
    [string]$AppName,
    [string]$Repository,
    [long]$RepositoryId,
    [string]$StateDirectory,
    [string]$Image,
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'host-enrollment.psm1') -DisableNameChecking
Import-Module (Join-Path $PSScriptRoot 'azure/update-state.psm1') -DisableNameChecking
Import-Module (Join-Path $PSScriptRoot 'azure/lifecycle-command.psm1') -DisableNameChecking
$context = $null

function Invoke-Azure([string[]]$Arguments, [int]$Timeout = 30) {
    $text = Invoke-LifecycleCommand az ($Arguments + @('--subscription',$SubscriptionId,'--output','json','--only-show-errors')) $Timeout
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    return ,(ConvertFrom-Json -InputObject $text -AsHashtable -NoEnumerate -Depth 50)
}
function Read-Azure([string]$Url) {
    return Invoke-Azure @('rest','--method','GET','--url',$Url)
}
function Read-Collection([string]$Url) {
    $result = Read-Azure $Url
    if ($result -isnot [Collections.IDictionary] -or $result.value -isnot [array] -or $result.nextLink) {
        throw 'Azure inventory is incomplete or paginated; inspect before changing revisions.'
    }
    return ,$result.value
}
function Read-Revisions {
    $values = Read-Collection "$resourceUrl/revisions?api-version=2026-07-01"
    foreach ($revision in $values) {
        if ($revision.name -isnot [string] -or -not $revision.name.StartsWith("$AppName--", [StringComparison]::Ordinal) -or
                $revision.properties.active -isnot [bool]) { throw 'Malformed revision identity or active state.' }
    }
    return ,$values
}
function Read-Replicas([string]$Revision) {
    return Read-Collection "$resourceUrl/revisions/$Revision/replicas?api-version=2026-07-01"
}
function Convert-Canonical($Value) {
    if ($Value -is [Collections.IDictionary]) {
        $ordered = [ordered]@{}
        foreach ($key in @($Value.Keys | Sort-Object -CaseSensitive)) { $ordered[$key] = Convert-Canonical $Value[$key] }
        return $ordered
    }
    if ($Value -is [array]) { return ,@($Value | ForEach-Object { Convert-Canonical $_ }) }
    return $Value
}
function Read-App {
    $app = Read-Azure "$resourceUrl`?api-version=2026-07-01"
    if ($app.id -isnot [string] -or $app.id -ine $resourceId -or $app.location -isnot [string] -or
            $app.location.Replace(' ', '').ToLowerInvariant() -cne 'swedencentral' -or
            $app.tags.managedBy -cne 'repoagent-controller' -or $app.tags.repository -ine $Repository -or
            $app.tags.purpose -cnotin @('controller-setup','controller-active')) { throw 'Selected app ownership, repository or location differs.' }
    $template = $app.properties.template
    if (@($template.containers).Count -ne 1 -or $template.containers[0].name -cne 'controller' -or
            @($template.initContainers).Count -ne 1 -or $template.initContainers[0].name -cne 'private-layout') {
        throw 'Require the existing single controller and private-layout initializer.'
    }
    $binding = ConvertFrom-Json -InputObject $template.initContainers[0].args[2] -AsHashtable -NoEnumerate
    if ($binding.repository -ine $Repository -or $binding.repositoryId -ne $RepositoryId -or
            ($binding.repositoryId -isnot [long] -and $binding.repositoryId -isnot [int])) { throw 'Retained immutable repository binding differs.' }
    $ingress = $app.properties.configuration.ingress
    if ($ingress.external -isnot [bool] -or -not $ingress.external -or $ingress.allowInsecure -ne $false -or
            $ingress.fqdn -cnotmatch '\A[a-z0-9.-]+\.azurecontainerapps\.io\z') { throw 'Require the retained HTTPS app origin.' }
    foreach ($secret in @($app.properties.configuration.secrets | Where-Object { $null -ne $_ })) {
        if ($secret.keyVaultUrl -cnotmatch '\Ahttps://[a-z0-9-]+\.vault\.azure\.net/secrets/[a-zA-Z0-9-]+/[0-9a-f]{32}\z' -or
                [string]::IsNullOrWhiteSpace($secret.identity)) { throw 'Require versioned managed-identity secret references.' }
    }
    $mounts = @($template.containers[0].volumeMounts)
    foreach ($pair in @(@('/data','runtime/data'),@('/workspace','runtime/workspace'))) {
        $mount = @($mounts | Where-Object { $_.mountPath -ceq $pair[0] })
        if ($mount.Count -ne 1 -or $mount[0].subPath -cne $pair[1] -or
                @($template.volumes | Where-Object { $_.name -ceq $mount[0].volumeName -and $_.storageType -ceq 'NfsAzureFile' }).Count -ne 1) {
            throw 'Retained private persistent storage differs.'
        }
    }
    return $app
}
function Get-ConfigurationHash($App) {
    # These three fields are the deliberate revision/image/traffic transition.
    $snapshot = @{
        location = $App.location; tags = $App.tags; identity = $App.identity
        environmentId = $App.properties.environmentId; workloadProfileName = $App.properties.workloadProfileName
        configuration = $App.properties.configuration; template = $App.properties.template
    } | ConvertTo-Json -Depth 50 -Compress | ConvertFrom-Json -AsHashtable
    $snapshot.template.Remove('revisionSuffix')
    $snapshot.template.containers[0].Remove('image')
    $snapshot.configuration.Remove('activeRevisionsMode')
    $snapshot.configuration.ingress.Remove('traffic')
    return Get-ValueHash ((Convert-Canonical $snapshot) | ConvertTo-Json -Depth 50 -Compress)
}
function Assert-Configuration([string]$Hash) {
    $app = Read-App
    if ((Get-ConfigurationHash $app) -cne $Hash) { throw 'Azure configuration changed; retain the journal and inspect before recovery.' }
    return $app
}
function Assert-Image([string]$Reference, [string]$DockerConfig) {
    Assert-Text $Reference '\Aghcr\.io/repoagent/controller@sha256:[0-9a-f]{64}\z'
    $null = Invoke-LifecycleCommand docker @('--config',$DockerConfig,'pull','--quiet',$Reference) 300
    $text = Invoke-LifecycleCommand docker @('--config',$DockerConfig,'image','inspect',$Reference)
    $imageInfo = ConvertFrom-Json -InputObject $text -AsHashtable -NoEnumerate
    if ($imageInfo -isnot [array] -or $imageInfo.Count -ne 1) { throw 'Expected one immutable Docker image.' }
    $imageInfo = $imageInfo[0]
    if ($imageInfo.Os -cne 'linux' -or $imageInfo.Architecture -cne 'amd64' -or $imageInfo.Config.User -cne '1000' -or
            $imageInfo.Config.Labels.'io.repoagent.role' -cne 'controller' -or
            $imageInfo.Config.Labels.'org.opencontainers.image.source' -cne 'https://github.com/repoagent/controller' -or
            $imageInfo.Config.Labels.'org.opencontainers.image.revision' -cnotmatch '\A[0-9a-f]{40}\z') {
        throw 'Published image provenance, platform or non-root controller role differs.'
    }
}
function Confirm-Stopped([string[]]$AllowedActive = @()) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        $revisions = Read-Revisions
        if (@($revisions | Where-Object { $_.properties.active -and $_.name -cnotin $AllowedActive }).Count) {
            throw 'An unexpected revision is active; no new controller will be started.'
        }
        $remaining = @($revisions | Where-Object { $_.name -cnotin $AllowedActive })
        $replicas = 0
        foreach ($revision in $remaining) {
            $actualReplicas = Read-Replicas $revision.name
            $replicas += $actualReplicas.Count
        }
        if ($replicas -eq 0) { return }
        Start-Sleep -Seconds 2
    } while ($clock.Elapsed.TotalSeconds -lt 120)
    throw 'Old replicas are still present; retain the pending journal and inspect.'
}
function Wait-Ready([string]$Revision, [string]$ExpectedImage, [string]$Hash) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        $app = Assert-Configuration $Hash
        $revisions = Read-Revisions
        if (@($revisions | Where-Object { $_.properties.active -and $_.name -cne $Revision }).Count) { throw 'Another revision became active.' }
        $candidate = @($revisions | Where-Object { $_.name -ceq $Revision })
        if ($candidate.Count -eq 1 -and $candidate[0].properties.active -and
                $candidate[0].properties.template.containers[0].image -ceq $ExpectedImage -and
                $candidate[0].properties.healthState -ceq 'Healthy') {
            # Wake a setup revision which may legitimately have a zero minimum.
            $expectedHost = $Revision + $app.properties.configuration.ingress.fqdn.Substring($AppName.Length)
            if ($candidate[0].properties.fqdn -cne $expectedHost) { throw 'Revision hostname differs from the retained app domain.' }
            $null = Invoke-WebRequest -Uri "https://$expectedHost/health/setup" -SkipHttpErrorCheck -MaximumRedirection 0 -TimeoutSec 45
            $actualReplicas = Read-Replicas $Revision
            if ($actualReplicas.Count -eq 1) { return }
        }
        Start-Sleep -Seconds 2
    } while ($clock.Elapsed.TotalSeconds -lt 180)
    throw 'The selected revision did not become ready; use explicit Rollback after inspection.'
}
function Set-Traffic([string]$Revision) {
    $null = Invoke-Azure @('containerapp','ingress','traffic','set','--name',$AppName,'--resource-group',$ResourceGroup,'--revision-weight',"$Revision=100")
    $app = Read-App
    $traffic = @($app.properties.configuration.ingress.traffic)
    if ($traffic.Count -ne 1 -or $traffic[0].revisionName -cne $Revision -or $traffic[0].weight -ne 100) { throw 'Selected revision traffic is unconfirmed.' }
}

try {
    Assert-Text $SubscriptionId '\A[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}\z'
    Assert-Text $ResourceGroup '\A[A-Za-z0-9_.()-]{1,90}\z'
    Assert-Text $AppName '\A[a-z][a-z0-9-]{0,30}[a-z0-9]\z'
    Assert-Text $Repository '\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z'
    if ($RepositoryId -le 0 -or [string]::IsNullOrWhiteSpace($StateDirectory)) { throw 'Supply the repository ID and a dedicated private StateDirectory.' }
    if (-not $Image) { $Image = (Get-Content (Join-Path $PSScriptRoot '../infra/controller/image.json') -Raw | ConvertFrom-Json).image }
    Assert-Text $Image '\Aghcr\.io/repoagent/controller@sha256:[0-9a-f]{64}\z'
    $resourceId = "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroup/providers/Microsoft.App/containerApps/$AppName"
    $resourceUrl = 'https://management.azure.com' + $resourceId
    $context = Open-AzureUpdateState $StateDirectory $resourceId
    $journal = Read-AzureUpdateJournal $context
    if ($Action -eq 'Update' -and $journal -and $journal.phase -cnotin @('committed','rolled-back')) {
        throw 'An update is pending; use explicit Rollback with this StateDirectory.'
    }
    if ($Action -eq 'Rollback' -and -not $journal) { throw 'Rollback requires the retained private update journal.' }
    $app = Read-App
    $hash = Get-ConfigurationHash $app
    if ($journal -and $journal.configurationSha256 -cne $hash) { throw 'Selected app configuration differs from its retained journal.' }
    $revisions = Read-Revisions
    $active = @($revisions | Where-Object { $_.properties.active })
    if ($Action -eq 'Status' -or -not $Apply) {
        $selectedImage = if ($Action -eq 'Rollback') { $journal.previousImage } elseif ($Action -eq 'Status') { $app.properties.template.containers[0].image } else { $Image }
        @{ action=$Action; apply=$false; resourceId=$resourceId; image=$selectedImage; journalPhase=$journal.phase
           activeRevisions=@($active.name); nativeAcceptance='requires native and signed-webhook verification' } | ConvertTo-Json -Depth 8
        return
    }
    if ($Action -eq 'Update' -and $active.Count -eq 1 -and $active[0].properties.template.containers[0].image -ceq $Image) {
        Write-Host 'Selected controller image is already active; no revision change was made.'
        return
    }
    $dockerConfig = Initialize-PrivateDirectory (Join-Path ([IO.Path]::GetFullPath($StateDirectory)) ('docker-' + [Guid]::NewGuid().ToString('N')))
    try {
        Write-PrivateJson (Join-Path $dockerConfig 'config.json') @{auths=@{}}
        if ($Action -eq 'Update') {
            if ($active.Count -ne 1) { throw 'Update requires exactly one active previous revision.' }
            $previous = $active[0]
            $previousImage = $previous.properties.template.containers[0].image
            Assert-Image $previousImage $dockerConfig
            Assert-Image $Image $dockerConfig
            $null = Assert-Configuration $hash
            $suffix = 'u' + [Guid]::NewGuid().ToString('N').Substring(0,12)
            $journal = Begin-AzureImageUpdate $context $hash $previous.name $previousImage "$AppName--$suffix" $Image
            $null = Set-AzureUpdatePhase $context draining
            $null = Invoke-Azure @('containerapp','revision','set-mode','--name',$AppName,'--resource-group',$ResourceGroup,'--mode','multiple')
            if ((Assert-Configuration $hash).properties.configuration.activeRevisionsMode -cne 'Multiple') { throw 'Multiple-revision control is unconfirmed.' }
            Write-Host 'Stopping the previous revision before replacement.'
            $null = Invoke-Azure @('containerapp','revision','deactivate','--name',$AppName,'--resource-group',$ResourceGroup,'--revision',$previous.name)
            Confirm-Stopped
            $null = Set-AzureUpdatePhase $context drained
            $null = Assert-Configuration $hash
            Confirm-Stopped
            $null = Set-AzureUpdatePhase $context launching
            Write-Host 'Old replicas are gone; creating the selected replacement revision.'
            $null = Invoke-Azure @('containerapp','revision','copy','--name',$AppName,'--resource-group',$ResourceGroup,
                '--from-revision',$previous.name,'--container-name','controller','--image',$Image,'--revision-suffix',$suffix) 180
            Wait-Ready $journal.candidateRevision $Image $hash
            $null = Set-AzureUpdatePhase $context ready
            Confirm-Stopped @($journal.candidateRevision)
            Set-Traffic $journal.candidateRevision
            $null = Set-AzureUpdatePhase $context committed
        } else {
            if ($journal.phase -ceq 'rolled-back') {
                if ($active.Count -ne 1 -or $active[0].name -cne $journal.previousRevision -or
                        $active[0].properties.template.containers[0].image -cne $journal.previousImage) {
                    throw 'Completed rollback no longer matches Azure; inspect before another operation.'
                }
                Wait-Ready $journal.previousRevision $journal.previousImage $journal.configurationSha256
                Confirm-Stopped @($journal.previousRevision)
                $traffic = @((Read-App).properties.configuration.ingress.traffic)
                if ($traffic.Count -ne 1 -or $traffic[0].revisionName -cne $journal.previousRevision -or $traffic[0].weight -ne 100) {
                    throw 'Completed rollback traffic differs; inspect Azure.'
                }
                Write-Host 'Recorded rollback is confirmed in Azure.'
                return
            }
            if (@($active | Where-Object { $_.name -cnotin @($journal.previousRevision,$journal.candidateRevision) }).Count) { throw 'Unexpected active revision prevents rollback.' }
            $previous = @($revisions | Where-Object { $_.name -ceq $journal.previousRevision })
            if ($previous.Count -ne 1 -or $previous[0].properties.template.containers[0].image -cne $journal.previousImage) { throw 'Recorded rollback revision differs or is absent.' }
            Assert-Image $journal.previousImage $dockerConfig
            $null = Assert-Configuration $journal.configurationSha256
            $null = Set-AzureUpdatePhase $context rollback-draining
            $candidate = @($revisions | Where-Object { $_.name -ceq $journal.candidateRevision })
            if ($candidate.Count -eq 1 -and $candidate[0].properties.active) {
                $null = Invoke-Azure @('containerapp','revision','deactivate','--name',$AppName,'--resource-group',$ResourceGroup,'--revision',$journal.candidateRevision)
            }
            Confirm-Stopped @($journal.previousRevision)
            $null = Assert-Configuration $journal.configurationSha256
            $null = Invoke-Azure @('containerapp','revision','activate','--name',$AppName,'--resource-group',$ResourceGroup,'--revision',$journal.previousRevision)
            Wait-Ready $journal.previousRevision $journal.previousImage $journal.configurationSha256
            $null = Set-AzureUpdatePhase $context rollback-ready
            Confirm-Stopped @($journal.previousRevision)
            Set-Traffic $journal.previousRevision
            $null = Set-AzureUpdatePhase $context rolled-back
        }
        Write-Host 'Revision transition complete. Verify the native controller and signed webhook; persistent data was retained.'
    } finally { Remove-Item -LiteralPath $dockerConfig -Recurse -Force }
} finally {
    if ($context) { $context.Lock.Dispose() }
}
