targetScope = 'resourceGroup'

param location string
param appName string
param storageName string
param vaultName string
param deployApp bool = false
param image string = ''
param signingKeyUri string = ''
param dataProtectionKeyUri string = ''
param oauthClientSecretUri string = ''
param storeResourceId string = ''

var ownership = {
  repositoryId: '1426154477'
  ownerId: '345029828'
  managedBy: 'repoagent-auth'
}

resource runtime 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${appName}-runtime'
  location: location
  tags: union(ownership, { role: 'auth-runtime' })
}

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${appName}-logs'
  location: location
  tags: union(ownership, { role: 'auth-logs' })
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    workspaceCapping: { dailyQuotaGb: 1 }
  }
}

resource environment 'Microsoft.App/managedEnvironments@2025-01-01' = {
  name: '${appName}-environment'
  location: location
  tags: union(ownership, { role: 'auth-environment' })
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
    workloadProfiles: [{ name: 'Consumption', workloadProfileType: 'Consumption' }]
  }
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  tags: union(ownership, { role: 'auth-data-protection' })
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    allowCrossTenantReplication: false
  }
}

// The default service is platform-owned; only the tagged parent and named
// container are changed. No lifecycle policy may expire the protection key ring.
resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' existing = {
  parent: storage
  name: 'default'
}
resource keyRing 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'data-protection'
  properties: {
    publicAccess: 'None'
    metadata: union(ownership, { role: 'auth-data-protection' })
  }
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: vaultName
  location: location
  tags: union(ownership, { role: 'auth-security' })
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    enablePurgeProtection: true
    accessPolicies: []
  }
}

resource app 'Microsoft.App/containerApps@2025-01-01' = if (deployApp) {
  name: appName
  location: location
  tags: union(ownership, { role: 'auth-service', 'repoagent-role': 'auth-service' })
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${runtime.id}': {} }
  }
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      maxInactiveRevisions: 3
      ingress: {
        external: true
        allowInsecure: false
        targetPort: 8080
        transport: 'http'
      }
      secrets: [{ name: 'oauth-client-secret', keyVaultUrl: oauthClientSecretUri, identity: runtime.id }]
    }
    template: {
      containers: [{
        name: 'auth-service'
        image: image
        resources: { cpu: json('0.25'), memory: '0.5Gi' }
        env: [
          { name: 'ASPNETCORE_HTTP_PORTS', value: '8080' }
          { name: 'AZURE_CLIENT_ID', value: runtime.properties.clientId }
          { name: 'Auth__SigningKeyUri', value: signingKeyUri }
          { name: 'Auth__DataProtectionKeyUri', value: dataProtectionKeyUri }
          { name: 'Auth__DataProtectionBlobUri', value: '${storage.properties.primaryEndpoints.blob}data-protection/keyring.xml' }
          { name: 'Auth__DataProtectionApplicationName', value: 'repoagent-auth-${appName}' }
          { name: 'Auth__StoreResourceId', value: storeResourceId }
          { name: 'Auth__OAuthClientSecret', secretRef: 'oauth-client-secret' }
        ]
        probes: [
          { type: 'Startup', httpGet: { path: '/health/live', port: 8080 }, periodSeconds: 5, failureThreshold: 60 }
          { type: 'Liveness', httpGet: { path: '/health/live', port: 8080 }, periodSeconds: 30, failureThreshold: 3 }
          { type: 'Readiness', httpGet: { path: '/health/ready', port: 8080 }, periodSeconds: 10, failureThreshold: 3 }
        ]
      }]
      scale: {
        minReplicas: 0
        maxReplicas: 1
        rules: [{ name: 'http', http: { metadata: { concurrentRequests: '10' } } }]
      }
    }
  }
}

output appName string = appName
output runtimeIdentityId string = runtime.id
output keyVaultName string = vault.name
output dataProtectionBlobUri string = '${storage.properties.primaryEndpoints.blob}data-protection/keyring.xml'
