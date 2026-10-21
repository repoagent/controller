// Separate Incremental write: bootstrap identity first, then the scheduled Job.
targetScope = 'resourceGroup'
param location string
param appName string
param storageName string
param tableName string = 'brokerstate'
param identityOnly bool
param image string = ''
param deploymentEpoch string = ''
var ownership = { repositoryId: '1426154477', ownerId: '345029828', managedBy: 'repoagent-auth' }
resource createdCleanupIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = if (identityOnly) {
  name: '${appName}-cleanup'
  location: location
  tags: union(ownership, { role: 'auth-cleanup-identity' })
}
resource existingCleanupIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: '${appName}-cleanup'
}
resource environment 'Microsoft.App/managedEnvironments@2025-01-01' existing = { name: '${appName}-environment' }
resource insights 'Microsoft.Insights/components@2020-02-02' existing = { name: '${appName}-insights' }
var monitoringConnectionString = identityOnly ? '' : insights.properties.ConnectionString
resource job 'Microsoft.App/jobs@2025-01-01' = if (!identityOnly) {
  name: '${appName}-cleanup'
  location: location
  tags: union(ownership, { role: 'auth-cleanup' })
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${existingCleanupIdentity.id}': {} } }
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      triggerType: 'Schedule'
      replicaRetryLimit: 0
      replicaTimeout: 90
      scheduleTriggerConfig: { cronExpression: '* * * * *', parallelism: 1, replicaCompletionCount: 1 }
    }
    template: {
      containers: [{
        name: 'cleanup'
        image: image
        command: ['dotnet', 'RepoAgent.Auth.dll']
        args: ['cleanup', '--once']
        resources: { cpu: json('0.25'), memory: '0.5Gi' }
        env: [
          { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: monitoringConnectionString }
          { name: 'APPLICATIONINSIGHTS_STATSBEAT_DISABLED', value: 'true' }
          #disable-next-line no-hardcoded-env-urls // Approved destination is Azure public cloud only.
          { name: 'Broker__TableEndpoint', value: 'https://${storageName}.table.core.windows.net/' }
          { name: 'Broker__TableName', value: tableName }
          { name: 'Broker__DeploymentEpoch', value: deploymentEpoch }
          { name: 'Broker__ManagedIdentityClientId', value: identityOnly ? '' : existingCleanupIdentity.properties.clientId }
        ]
      }]
    }
  }
}
output cleanupIdentityId string = existingCleanupIdentity.id
output cleanupIdentityClientId string = identityOnly ? createdCleanupIdentity!.properties.clientId : existingCleanupIdentity.properties.clientId
output cleanupIdentityPrincipalId string = identityOnly ? createdCleanupIdentity!.properties.principalId : existingCleanupIdentity.properties.principalId
output cleanupJobId string = resourceId('Microsoft.App/jobs', '${appName}-cleanup')
