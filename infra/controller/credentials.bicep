targetScope = 'resourceGroup'

param name string = 'storybeam-repoagent'
param location string = 'swedencentral'
@description('Signed-in operator who uploads a fresh enrollment from a private file.')
param operatorPrincipalId string

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: '${name}-identity'
  location: location
  tags: { managedBy: 'repoagent-controller', purpose: 'controller-enrollment' }
}

resource vault 'Microsoft.KeyVault/vaults@2026-05-15' = {
  name: 'rac${uniqueString(resourceGroup().id)}kv'
  location: location
  tags: { managedBy: 'repoagent-controller', purpose: 'controller-enrollment-only' }
  properties: {
    tenantId: tenant().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    enablePurgeProtection: true
    softDeleteRetentionInDays: 7
    enabledForDeployment: false
    enabledForDiskEncryption: false
    enabledForTemplateDeployment: false
    publicNetworkAccess: 'Enabled'
  }
}

var readerRole = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
resource reader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: vault
  name: guid(vault.id, identity.id, readerRole)
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: readerRole
  }
}

var writerRole = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7')
resource operator 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: vault
  name: guid(vault.id, operatorPrincipalId, writerRole)
  properties: {
    principalId: operatorPrincipalId
    principalType: 'User'
    roleDefinitionId: writerRole
  }
}

output vaultName string = vault.name
output identityResourceId string = identity.id
