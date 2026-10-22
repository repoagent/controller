targetScope = 'resourceGroup'

@description('Fresh candidate only. Distinct names preserve the existing NFS environment and its data.')
@minLength(3)
@maxLength(21)
param name string = 'storybeam-lean'
param location string = 'swedencentral'
@description('Zero by default. Select one temporarily for interactive setup or an explicit storage check.')
@allowed([0, 1])
param setupMinReplicas int = 0
@description('Setup only: real SMB native SQLite persistence failed. Activation remains blocked under issue #37.')
@allowed([false])
param activate bool = false
@secure()
param enrollment string = ''
@description('Select an immutable public image containing the queue mode, bootstrap wiring and SMB fixes before activation.')
param controllerImage string = loadJsonContent('image.json').image

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
resource queueService 'Microsoft.Storage/storageAccounts/queueServices@2026-09-01' existing = {
  parent: storage
  name: 'default'
}
resource workQueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2026-09-01' = {
  parent: queueService
  name: 'controller-work'
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

// Setup does no account or native work. Activation is a separate explicit selection.
module setup 'stage.bicep' = {
  name: '${name}-setup'
  params: {
    name: name
    location: location
    storageType: 'AzureFile'
    setupMinReplicas: setupMinReplicas
    workQueueAccountName: storage.name
    workQueueName: workQueue.name
    activate: activate
    enrollment: enrollment
    controllerImage: controllerImage
  }
  dependsOn: [mountedShare]
}
module queueAccess 'queue-access.bicep' = {
  name: '${name}-queue-access'
  params: {
    accountName: storage.name
    queueName: workQueue.name
    principalId: setup.outputs.principalId
  }
  dependsOn: [workQueue]
}

output appName string = setup.outputs.appName
output environmentName string = appEnvironment.name
output storageAccountName string = storage.name
output shareName string = share.name
output publicOrigin string = setup.outputs.publicOrigin
output workQueueEndpoint string = 'https://${storage.name}.queue.${environment().suffixes.storage}/${workQueue.name}'
output principalId string = setup.outputs.principalId
