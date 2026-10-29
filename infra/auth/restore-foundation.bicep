// Operator-only isolated foundation; no API, scheduled Job, RBAC or Table data writes.
targetScope = 'resourceGroup'
@minLength(15)
@maxLength(15)
param runId string

var location = 'swedencentral'
// Keep the full run ID while leaving room for the cleanup Job's 32-character limit.
var appName = 'r230-${runId}'
var storageName = 'ra230${replace(runId, '-', '')}'
var wrappingKeyName = 'restore230-${runId}-wrap'
var vaultName = 'repoagent-auth-kv'

module foundation 'main.bicep' = {
  name: 'restore-foundation'
  params: {
    location: location
    appName: appName
    storageName: storageName
    vaultName: vaultName
    provisionVault: false
    deployApp: false
  }
}
module cleanup 'cleanup.bicep' = {
  name: 'restore-cleanup-identity'
  params: {
    location: location
    appName: appName
    storageName: storageName
    identityOnly: true
  }
}
resource vault 'Microsoft.KeyVault/vaults@2023-07-01' existing = { name: vaultName }
resource wrapping 'Microsoft.KeyVault/vaults/keys@2025-05-01' = {
  parent: vault
  name: wrappingKeyName
  properties: {
    kty: 'RSA'
    keySize: 2048
    keyOps: [ 'wrapKey', 'unwrapKey' ]
    attributes: { enabled: true, exportable: false }
  }
}
resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${appName}-insights'
  location: location
  kind: 'web'
  tags: { repositoryId: '1426154477', ownerId: '345029828', managedBy: 'repoagent-auth', role: 'auth-monitoring' }
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: resourceId('Microsoft.OperationalInsights/workspaces', '${appName}-logs')
    DisableLocalAuth: true
    DisableIpMasking: false
    IngestionMode: 'LogAnalytics'
  }
  dependsOn: [ foundation ]
}

output runtimeIdentityId string = foundation.outputs.runtimeIdentityId
output runtimeIdentityPrincipalId string = foundation.outputs.runtimeIdentityPrincipalId
output runtimeIdentityClientId string = foundation.outputs.runtimeIdentityClientId
output cleanupIdentityId string = cleanup.outputs.cleanupIdentityId
output cleanupIdentityPrincipalId string = cleanup.outputs.cleanupIdentityPrincipalId
output cleanupIdentityClientId string = cleanup.outputs.cleanupIdentityClientId
output tableResourceId string = foundation.outputs.tableResourceId
output dataProtectionBlobUri string = foundation.outputs.dataProtectionBlobUri
output wrappingKeyUri string = wrapping.properties.keyUriWithVersion
