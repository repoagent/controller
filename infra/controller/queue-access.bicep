targetScope = 'resourceGroup'

param accountName string
param queueName string
param principalId string

resource account 'Microsoft.Storage/storageAccounts@2026-09-01' existing = {
  name: accountName
}
resource service 'Microsoft.Storage/storageAccounts/queueServices@2026-09-01' existing = {
  parent: account
  name: 'default'
}
resource queue 'Microsoft.Storage/storageAccounts/queueServices/queues@2026-09-01' existing = {
  parent: service
  name: queueName
}

// A nested deployment receives the newly created system principal as an input.
// Including that principal avoids reusing an immutable assignment after app recreation.
resource access 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(queue.id, principalId, 'queue-data')
  scope: queue
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '974c5e8b-45b9-4653-ba55-5f855dd0fb88')
  }
}
