targetScope = 'resourceGroup'

@description('Fresh candidate only. Distinct names preserve the existing NFS environment and its data.')
@minLength(3)
@maxLength(21)
param name string = 'storybeam-lean'
param location string = 'swedencentral'
@description('Zero by default. Select one temporarily for interactive setup or an explicit storage check.')
@allowed([0, 1])
param setupMinReplicas int = 0

var tags = {
  managedBy: 'repoagent-controller'
  repository: 'StoryBeam/StoryBeam'
  purpose: 'controller-smb-candidate'
}

// Default managed networking avoids the separately billed custom-VNet LB and IP.
resource appEnvironment 'Microsoft.App/managedEnvironments@2026-07-01' = {
  name: '${name}-environment'
  location: location
  tags: tags
  properties: {
    publicNetworkAccess: 'Enabled'
    zoneRedundant: false
    appLogsConfiguration: { destination: null }
    workloadProfiles: [{ name: 'Consumption', workloadProfileType: 'Consumption' }]
  }
}

resource storage 'Microsoft.Storage/storageAccounts@2026-09-01' = {
  name: 'ral${uniqueString(resourceGroup().id, name)}'
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    allowCrossTenantReplication: false
    // Stable ACA SMB mounts authenticate with this dedicated account's key.
    // ARM passes it directly below; no operator file, output or client argument.
    allowSharedKeyAccess: true
    publicNetworkAccess: 'Enabled'
  }
}

resource fileService 'Microsoft.Storage/storageAccounts/fileServices@2026-09-01' existing = {
  parent: storage
  name: 'default'
}

resource share 'Microsoft.Storage/storageAccounts/fileServices/shares@2026-09-01' = {
  parent: fileService
  name: 'controller'
  properties: {
    enabledProtocols: 'SMB'
    accessTier: 'Hot'
    // A limit, not provisioned capacity: Standard Hot bills used bytes and operations.
    shareQuota: 32
  }
}

resource mountedShare 'Microsoft.App/managedEnvironments/storages@2026-07-01' = {
  parent: appEnvironment
  name: 'controller-state'
  properties: {
    azureFile: {
      accountName: storage.name
      accountKey: storage.listKeys().keys[0].value
      shareName: share.name
      accessMode: 'ReadWrite'
    }
  }
}

// Reuse the metadata-only setup host. No enrollment, account import or native work.
module setup 'stage.bicep' = {
  name: '${name}-setup'
  params: {
    name: name
    location: location
    storageType: 'AzureFile'
    setupMinReplicas: setupMinReplicas
  }
  dependsOn: [mountedShare]
}

output appName string = setup.outputs.appName
output environmentName string = appEnvironment.name
output storageAccountName string = storage.name
output shareName string = share.name
output publicOrigin string = setup.outputs.publicOrigin
