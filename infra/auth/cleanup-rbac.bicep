// Operator-only: never invoked by the Contributor workflow or deployment helper.
targetScope = 'resourceGroup'
param appName string
param storageName string
param tableName string
resource cleanup 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = { name: '${appName}-cleanup' }
resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = { name: storageName }
resource tables 'Microsoft.Storage/storageAccounts/tableServices@2023-05-01' existing = { parent: storage, name: 'default' }
resource brokerTable 'Microsoft.Storage/storageAccounts/tableServices/tables@2023-05-01' existing = { parent: tables, name: tableName }
var contributor = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3')
resource access 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(brokerTable.id, cleanup.id, contributor)
  scope: brokerTable
  properties: {
    principalId: cleanup.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: contributor
  }
}
output tableResourceId string = brokerTable.id
output cleanupIdentityPrincipalId string = cleanup.properties.principalId
