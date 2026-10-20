// Operator-only. Never called by deploy.sh or the Contributor workflow.
// Inspect the exact scopes/definitions/assignments before explicit application.
targetScope = 'resourceGroup'
param appName string
param storageName string
param vaultName string
param signingKeyName string
param dataProtectionKeyName string
param oauthClientSecretName string

resource runtime 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: '${appName}-runtime'
}
resource vault 'Microsoft.KeyVault/vaults@2023-07-01' existing = { name: vaultName }
resource signing 'Microsoft.KeyVault/vaults/keys@2023-07-01' existing = { parent: vault, name: signingKeyName }
resource wrapping 'Microsoft.KeyVault/vaults/keys@2023-07-01' existing = { parent: vault, name: dataProtectionKeyName }
resource oauth 'Microsoft.KeyVault/vaults/secrets@2023-07-01' existing = { parent: vault, name: oauthClientSecretName }
resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = { name: storageName }
resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' existing = { parent: storage, name: 'default' }
resource keyRing 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' existing = { parent: blobs, name: 'data-protection' }

var permissions = [
  { role: 'sign', operations: ['Microsoft.KeyVault/vaults/keys/read', 'Microsoft.KeyVault/vaults/keys/sign/action'] }
  { role: 'wrap', operations: ['Microsoft.KeyVault/vaults/keys/read', 'Microsoft.KeyVault/vaults/keys/wrap/action', 'Microsoft.KeyVault/vaults/keys/unwrap/action'] }
  { role: 'oauth', operations: ['Microsoft.KeyVault/vaults/secrets/readMetadata/action', 'Microsoft.KeyVault/vaults/secrets/getSecret/action'] }
  { role: 'keyring', operations: ['Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read', 'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/write'] }
]
resource roles 'Microsoft.Authorization/roleDefinitions@2022-04-01' = [for permission in permissions: {
  name: guid(resourceGroup().id, '1426154477', appName, permission.role)
  properties: {
    roleName: 'repoagent-1426154477-${appName}-${permission.role}'
    description: 'Repoagent auth runtime ${permission.role}; operator managed, no workflow authorization.'
    type: 'CustomRole'
    assignableScopes: [resourceGroup().id]
    permissions: [{ actions: [], notActions: [], dataActions: permission.operations, notDataActions: [] }]
  }
}]
resource signingAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(signing.id, runtime.id, roles[0].id)
  scope: signing
  properties: { principalId: runtime.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: roles[0].id }
}
resource wrappingAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(wrapping.id, runtime.id, roles[1].id)
  scope: wrapping
  properties: { principalId: runtime.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: roles[1].id }
}
resource oauthAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(oauth.id, runtime.id, roles[2].id)
  scope: oauth
  properties: { principalId: runtime.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: roles[2].id }
}
resource keyRingAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyRing.id, runtime.id, roles[3].id)
  scope: keyRing
  properties: { principalId: runtime.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: roles[3].id }
}
