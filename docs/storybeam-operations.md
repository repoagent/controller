# Operate the StoryBeam controller

StoryBeam/StoryBeam uses the lean Azure controller in Sweden Central. Post a
Discussion comment mentioning `@repo-agent-connector`; the controller wakes and
replies as the App bot, retaining one native conversation per Discussion.
An idle cold start can exceed GitHub's delivery timeout. Startup reconciliation
recovers activity still present in GitHub; a webhook timeout alone does not mean
the native request was lost. Check for the reply before requesting another turn.

The stable origin is
https://storybeam-lean-controller.whiteriver-110a4df2.swedencentral.azurecontainerapps.io.
The selected resources in `rg-storybeam-repoagent-controller` are the
`storybeam-lean-controller` app, `storybeam-lean-environment` environment and
`ralt57tozxutzf22` storage account. Queue/share/storage definitions and the scoped
system-identity grant are children of those resources. No private ACR is needed.

The app has minimum 0/maximum 1 replicas, a 15-second queue poll and 60-second
cooldown. Actual cold conversation recall, a 120-second native turn staying alive
beyond cooldown, and return to zero have passed. Storage and request charges
continue at zero replicas. The separately retained original NFS account
`raccvldjd5jofski` also has provisioned charges; it is not required by the lean app.

## Update or recover this existing app

Run from this controller checkout in PowerShell 7.4+, with authenticated host
Azure CLI and Docker available. Keep the existing private recovery directory:

```powershell
$selection = @{
    SubscriptionId = '46f7e5d6-05f3-435b-8089-6c63a203dbfa'
    ResourceGroup = 'rg-storybeam-repoagent-controller'
    AppName = 'storybeam-lean-controller'
    Repository = 'StoryBeam/StoryBeam'
    RepositoryId = 1069791767
    StateDirectory = "$HOME/.repoagent/instances/storybeam-azure-lean-1/azure-update"
}
./scripts/update-azure-controller.ps1 @selection -Action Status
./scripts/update-azure-controller.ps1 @selection                 # preview
./scripts/update-azure-controller.ps1 @selection -Apply
./scripts/update-azure-controller.ps1 @selection -Action Rollback # preview
./scripts/update-azure-controller.ps1 @selection -Action Rollback -Apply
```

Update defaults to the public immutable image in `infra/controller/image.json`.
Pass `-Image` with another accepted public digest from the
[controller image workflow](ci-cd.md#public-controller-container-image) to select it.
Updates deliberately interrupt service and retain the same logical enrollment,
data and conversation history. Preserve the journal if an operation fails; use
the explicit rollback path after inspecting its recorded revision names.
The [lifecycle guide](../infra/controller/README.md#subsequent-controller-image-updates)
describes the checks and operator permissions. Do not reapply setup Bicep or
re-enroll/reset private state to repair an image update.

Inspect active revisions without sending HTTP requests that wake the app:

```powershell
$azApp = @('--subscription', $selection.SubscriptionId,
    '--resource-group', $selection.ResourceGroup, '--name', $selection.AppName)
foreach ($revision in (az containerapp revision list @azApp --query '[?properties.active].name' -o tsv)) {
    az containerapp replica list @azApp --revision $revision --query '[].{name:name,ready:properties.containers[0].ready}' -o json
}
```

## State, local development and new deployments

Private account/configuration, checkout, checkpoints and native conversation
rollouts persist on SMB. Native SQLite remains local to each replica; arbitrary
SQLite-backed UI/plugin/goal metadata is outside the verified
[conversation persistence boundary](../infra/controller/SMB.md#conversation-persistence-boundary).
The cloud account was signed in directly; host account credentials are not needed.

The original local Docker image/container/data remain retained and stopped after
supported owner settlement. Its old device is revoked; restarting it is not a
cloud rollback. Use the same app's image rollback for that purpose. For local
setup or native Linux/WSL debugging, follow the
[local installer](local-installation.md) and [native auth guide](github-app-auth.md#run-or-debug-the-authentication-check-on-linux--wsl).

For a new Azure deployment, follow the [SMB setup and first activation](../infra/controller/SMB.md)
instead of adopting this app's recovery directory or enrollment. The maintainer
[auth API and cleanup deploy automatically](ci-cd.md#automatic-auth-service-delivery) on
relevant merges to `main`; ordinary controller image replacement uses the
explicit command above.
