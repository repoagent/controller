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
Import-Module (Join-Path $PSScriptRoot 'azure/controller-profile.psm1') -DisableNameChecking
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
function Get-TemplateHash($Template) {
    $snapshot = Get-ComparableControllerTemplate $Template $retainedTemplate
    return Get-ValueHash ((Convert-Canonical $snapshot) | ConvertTo-Json -Depth 50 -Compress)
}
function Read-RecordedRevision([string]$Name, [string]$ExpectedImage) {
    $revision = Read-Azure "$resourceUrl/revisions/$Name`?api-version=2026-07-01"
    if ($revision.id -ine "$resourceId/revisions/$Name" -or $revision.name -cne $Name -or
            $revision.properties.active -isnot [bool] -or
            $revision.properties.provisioningState -cnotin @('Provisioned','Failed','Deprovisioned')) {
        throw 'Recorded revision is missing or its provisioning outcome remains unresolved.'
    }
    if ((Get-TemplateHash $revision.properties.template) -cne $templateHash -or
            $revision.properties.template.containers[0].image -cne $ExpectedImage) {
        throw 'Recorded revision configuration differs from the validated retained template.'
    }
    return $revision
}
function Assert-RecordedRevisions($Revisions) {
    $record = Read-AzureUpdateJournal $context
    $app = Assert-Configuration $record.configurationSha256
    if ($app.properties.provisioningState -cnotin @('Succeeded','Failed','Canceled')) {
        throw 'Azure provisioning remains unresolved; retain the journal and inspect before recovery.'
    }
    $required = [ordered]@{}
    $required[$record.previousRevision] = $record.previousImage
    if ($record.launchAttempted) { $required[$record.candidateRevision] = $record.candidateImage }
    elseif (@($Revisions | Where-Object { $_.name -ceq $record.candidateRevision }).Count) {
        throw 'A candidate exists without a recorded launch; inspect before recovery.'
    }
    foreach ($name in $required.Keys) {
        if (@($Revisions | Where-Object { $_.name -ceq $name }).Count -ne 1) {
            throw 'A recorded revision is missing from inventory; its remote outcome remains unknown.'
        }
        $null = Read-RecordedRevision $name $required[$name]
    }
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
    Assert-ControllerStorageProfile $app.tags $template @($app.properties.configuration.secrets)
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
    $nativeSecrets = @($App.properties.configuration.secrets | Where-Object { $null -ne $_ })
    if ($App.tags.nativePersistence -ceq 'conversation-rollouts' -and $nativeSecrets.Count) {
        # App GET exposes names, not native values. Protect their observed value
        # without writing it into the journal, CLI arguments or console output.
        try {
            $inventory = Invoke-Azure @('rest','--method','POST','--url',"$resourceUrl/listSecrets?api-version=2026-07-01")
            if ($inventory -isnot [Collections.IDictionary] -or $inventory.value -isnot [array] -or $inventory.nextLink) {
                throw 'Incomplete native secret response.'
            }
            $snapshot.nativeEnrollmentSha256 = Get-NativeEnrollmentFingerprint $nativeSecrets $inventory.value
        } catch { throw 'Native enrollment fingerprint could not be read; provider details were withheld.' }
    }
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
        Assert-RecordedRevisions $revisions
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
            $null = Read-RecordedRevision $Revision $ExpectedImage
            # Wake a setup revision which may legitimately have a zero minimum.
            $expectedHost = $Revision + $app.properties.configuration.ingress.fqdn.Substring($AppName.Length)
            if ($candidate[0].properties.fqdn -cne $expectedHost) { throw 'Revision hostname differs from the retained app domain.' }
            $null = Invoke-WebRequest -Uri "https://$expectedHost/health/setup" -SkipHttpErrorCheck -MaximumRedirection 0 -TimeoutSec 45
            $actualReplicas = Read-Replicas $Revision
            if ($actualReplicas.Count -eq 1) {
                $controller = @($actualReplicas[0].properties.containers | Where-Object { $_.name -ceq 'controller' })
                if ($controller.Count -eq 1 -and $controller[0].ready -is [bool] -and $controller[0].ready) { return }
            }
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
    $retainedTemplate = $app.properties.template
    $templateHash = Get-TemplateHash $retainedTemplate
    if ($journal -and $journal.configurationSha256 -cne $hash) { throw 'Selected app configuration differs from its retained journal.' }
    $revisions = Read-Revisions
    $active = @($revisions | Where-Object { $_.properties.active })
    if ($Action -eq 'Status') {
        $selectedImage = if ($active.Count -eq 1) { $active[0].properties.template.containers[0].image } else { $null }
        @{ action=$Action; apply=$false; resourceId=$resourceId; image=$selectedImage; journalPhase=$journal.phase
           configurationSha256=$hash; activeRevisions=@($active.name)
           nativeAcceptance='requires native and signed-webhook verification' } | ConvertTo-Json -Depth 8
        return
    }
    $dockerConfig = Initialize-PrivateDirectory (Join-Path ([IO.Path]::GetFullPath($StateDirectory)) ('docker-' + [Guid]::NewGuid().ToString('N')))
    try {
        Write-PrivateJson (Join-Path $dockerConfig 'config.json') @{auths=@{}}
        if ($Action -eq 'Update') {
            if ($active.Count -ne 1) { throw 'Update requires exactly one active previous revision.' }
            $previous = $active[0]
            if (-not $journal) { Assert-ControllerTemplateAnchor $app $previous.name }
            $previousImage = $previous.properties.template.containers[0].image
            $previous = Read-RecordedRevision $previous.name $previousImage
            if (-not $previous.properties.active -or $previous.properties.provisioningState -cne 'Provisioned' -or
                    $app.properties.provisioningState -cne 'Succeeded') { throw 'Previous revision or app provisioning is unresolved.' }
            Assert-AzureUpdateSource $context $hash $previous.name $previousImage
            Assert-Image $previousImage $dockerConfig
            Assert-Image $Image $dockerConfig
            $null = Assert-Configuration $hash
            if (-not $Apply) {
                @{ action=$Action; apply=$false; resourceId=$resourceId; image=$Image; journalPhase=$journal.phase
                   previousRevision=$previous.name; activeRevisions=@($active.name)
                   nativeAcceptance='requires native and signed-webhook verification' } | ConvertTo-Json -Depth 8
                return
            }
            if ($previousImage -ceq $Image) {
                Write-Host 'Selected controller image is already active; no revision change was made.'
                return
            }
            $suffix = 'u' + [Guid]::NewGuid().ToString('N').Substring(0,12)
            $journal = Begin-AzureImageUpdate $context $hash $previous.name $previousImage "$AppName--$suffix" $Image
            $null = Set-AzureUpdatePhase $context draining
            $null = Invoke-Azure @('containerapp','revision','set-mode','--name',$AppName,'--resource-group',$ResourceGroup,'--mode','multiple')
            if ((Assert-Configuration $hash).properties.configuration.activeRevisionsMode -cne 'Multiple') { throw 'Multiple-revision control is unconfirmed.' }
            Set-Traffic $previous.name
            Write-Host 'Stopping the previous revision before replacement.'
            $null = Invoke-Azure @('containerapp','revision','deactivate','--name',$AppName,'--resource-group',$ResourceGroup,'--revision',$previous.name)
            Confirm-Stopped
            $null = Set-AzureUpdatePhase $context drained
            $null = Assert-Configuration $hash
            Confirm-Stopped
            $null = Set-AzureUpdatePhase $context launching
            Write-Host 'Old replicas are gone; creating the selected replacement revision.'
            $payloadPath = Join-Path $dockerConfig 'replacement.json'
            Write-PrivateJson $payloadPath (New-ControllerImagePatch $retainedTemplate $Image $suffix $app.location)
            $null = Invoke-Azure @('rest','--method','PATCH','--url',"$resourceUrl`?api-version=2026-07-01",
                '--body',("@" + $payloadPath)) 180
            Wait-Ready $journal.candidateRevision $Image $hash
            $null = Set-AzureUpdatePhase $context ready
            Confirm-Stopped @($journal.candidateRevision)
            Set-Traffic $journal.candidateRevision
            $null = Set-AzureUpdatePhase $context committed
        } else {
            Assert-RecordedRevisions $revisions
            if (@($active | Where-Object { $_.name -cnotin @($journal.previousRevision,$journal.candidateRevision) }).Count) { throw 'Unexpected active revision prevents rollback.' }
            $previous = @($revisions | Where-Object { $_.name -ceq $journal.previousRevision })
            if ($previous.Count -ne 1 -or $previous[0].properties.template.containers[0].image -cne $journal.previousImage) { throw 'Recorded rollback revision differs or is absent.' }
            Assert-Image $journal.previousImage $dockerConfig
            $null = Assert-Configuration $journal.configurationSha256
            if ($journal.phase -ceq 'rolled-back') {
                if ($active.Count -ne 1 -or $active[0].name -cne $journal.previousRevision -or
                        $active[0].properties.template.containers[0].image -cne $journal.previousImage) {
                    throw 'Completed rollback no longer matches Azure; inspect before another operation.'
                }
                $traffic = @((Read-App).properties.configuration.ingress.traffic)
                if ($traffic.Count -ne 1 -or $traffic[0].revisionName -cne $journal.previousRevision -or $traffic[0].weight -ne 100) {
                    throw 'Completed rollback traffic differs; inspect Azure.'
                }
            }
            if (-not $Apply) {
                @{ action=$Action; apply=$false; resourceId=$resourceId; image=$journal.previousImage; journalPhase=$journal.phase
                   previousRevision=$journal.previousRevision; activeRevisions=@($active.name)
                   nativeAcceptance='requires native and signed-webhook verification' } | ConvertTo-Json -Depth 8
                return
            }
            if ($journal.phase -ceq 'rolled-back') {
                Wait-Ready $journal.previousRevision $journal.previousImage $journal.configurationSha256
                Confirm-Stopped @($journal.previousRevision)
                Write-Host 'Recorded rollback is confirmed in Azure.'
                return
            }
            $null = Set-AzureUpdatePhase $context rollback-draining
            $candidate = @($revisions | Where-Object { $_.name -ceq $journal.candidateRevision })
            if ($candidate.Count -eq 1 -and $candidate[0].properties.active) {
                $null = Invoke-Azure @('containerapp','revision','deactivate','--name',$AppName,'--resource-group',$ResourceGroup,'--revision',$journal.candidateRevision)
            }
            Confirm-Stopped @($journal.previousRevision)
            $null = Assert-Configuration $journal.configurationSha256
            if (-not $previous[0].properties.active) {
                $null = Invoke-Azure @('containerapp','revision','activate','--name',$AppName,'--resource-group',$ResourceGroup,'--revision',$journal.previousRevision)
            }
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
