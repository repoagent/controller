// Operator-only; never invoked by the Contributor deployment entrypoint.
targetScope = 'resourceGroup'
param appName string
resource insights 'Microsoft.Insights/components@2020-02-02' existing = { name: '${appName}-insights' }
resource runtime 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = { name: '${appName}-runtime' }
resource cleanup 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = { name: '${appName}-cleanup' }
var publisher = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '3913510d-42f4-4e42-8a64-420c390055eb')
resource runtimePublisher 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(insights.id, runtime.id, publisher)
  scope: insights
  properties: { principalId: runtime.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: publisher }
}
resource cleanupPublisher 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(insights.id, cleanup.id, publisher)
  scope: insights
  properties: { principalId: cleanup.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: publisher }
}
