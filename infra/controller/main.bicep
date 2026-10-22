targetScope = 'resourceGroup'

@description('Dedicated controller foundation; deploy into a new, selected resource group.')
@minLength(3)
@maxLength(26)
param name string = 'storybeam-repoagent'
param location string = 'swedencentral'

// Public release accepted from main 0e1ac3405aaf88a1ebe2ad37585de8e883bfb4ec.
var image = loadJsonContent('image.json').image
var tags = {
  managedBy: 'repoagent-controller'
  repository: 'StoryBeam/StoryBeam'
  purpose: 'controller-foundation'
}

resource networkSecurityGroup 'Microsoft.Network/networkSecurityGroups@2025-05-01' = {
  name: '${name}-nsg'
  location: location
  tags: tags
  properties: { securityRules: [] }
}

module network 'br/public:avm/res/network/virtual-network:0.10.2' = {
  name: 'controller-network'
  params: {
    name: '${name}-vnet'
    location: location
    tags: tags
    enableTelemetry: false
    addressPrefixes: ['10.74.0.0/16']
    subnets: [{
      name: 'container-apps'
      addressPrefix: '10.74.0.0/23'
      delegation: 'Microsoft.App/environments'
      serviceEndpoints: ['Microsoft.Storage']
      networkSecurityGroupResourceId: networkSecurityGroup.id
    }]
  }
}

resource storage 'Microsoft.Storage/storageAccounts@2026-09-01' = {
  name: 'rac${uniqueString(resourceGroup().id)}'
  location: location
  tags: tags
  kind: 'FileStorage'
  sku: { name: 'PremiumV2_LRS' }
  properties: {
    minimumTlsVersion: 'TLS1_2'
    // ACA cannot mount encrypted NFS. Only this dedicated subnet may reach it.
    supportsHttpsTrafficOnly: false
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    allowCrossTenantReplication: false
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      bypass: 'None'
      defaultAction: 'Deny'
      ipRules: []
      // A deterministic ID also lets Storage validate this rule during redeployment.
      virtualNetworkRules: [{
        id: resourceId('Microsoft.Network/virtualNetworks/subnets', '${name}-vnet', 'container-apps')
        action: 'Allow'
      }]
    }
  }
  dependsOn: [network]
}

resource fileService 'Microsoft.Storage/storageAccounts/fileServices@2026-09-01' existing = {
  parent: storage
  name: 'default'
}
resource share 'Microsoft.Storage/storageAccounts/fileServices/shares@2026-09-01' = {
  parent: fileService
  name: 'controller'
  properties: {
    enabledProtocols: 'NFS'
    rootSquash: 'RootSquash'
    shareQuota: 32
    provisionedIops: 3000
    provisionedBandwidthMibps: 100
  }
}

module appEnvironment 'br/public:avm/res/app/managed-environment:0.16.0' = {
  name: 'controller-environment'
  params: {
    name: '${name}-environment'
    location: location
    tags: tags
    enableTelemetry: false
    zoneRedundant: false
    publicNetworkAccess: 'Enabled'
    infrastructureSubnetResourceId: network.outputs.subnetResourceIds[0]
    infrastructureResourceGroupName: '${resourceGroup().name}-managed'
    workloadProfiles: [{ name: 'Consumption', workloadProfileType: 'Consumption' }]
  }
}

resource mountedShare 'Microsoft.App/managedEnvironments/storages@2026-07-01' = {
  name: '${name}-environment/controller-state'
  properties: {
    nfsAzureFile: {
      server: '${storage.name}.file.${environment().suffixes.storage}'
      shareName: '/${storage.name}/${share.name}'
      accessMode: 'ReadWrite'
    }
  }
  dependsOn: [appEnvironment]
}

// Manual and credential-free: this never runs the controller or contacts the broker.
resource smoke 'Microsoft.App/jobs@2026-07-01' = {
  name: '${name}-smoke'
  location: location
  tags: tags
  properties: {
    environmentId: appEnvironment.outputs.resourceId
    workloadProfileName: 'Consumption'
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 300
      replicaRetryLimit: 0
      manualTriggerConfig: { parallelism: 1, replicaCompletionCount: 1 }
    }
    template: {
      containers: [{
        name: 'image-and-mount'
        image: image
        command: ['/usr/bin/python3', '-E', '-c']
        args: ['''
import json, os, pathlib, subprocess
assert os.getuid() == 1000, 'Controller must run as UID 1000'
mounts = [line.split() for line in pathlib.Path('/proc/mounts').read_text().splitlines()]
mount = next(m for m in mounts if m[1] == '/mnt/controller')
assert mount[2] == 'nfs4', 'Expected the real NFS 4.1 share'
assert 'nobrl' not in mount[3].split(','), 'Locking cannot be disabled'
subprocess.run(['/usr/bin/python3', '-E', '/opt/repoagent/runtime.py', 'versions'], check=True)
print(json.dumps({'uid': os.getuid(), 'filesystem': mount[2], 'mount': mount[1], 'capacityBytes': os.statvfs('/mnt/controller').f_blocks * os.statvfs('/mnt/controller').f_frsize}))
''']
        resources: { cpu: json('0.25'), memory: '0.5Gi' }
        volumeMounts: [{ volumeName: 'controller-state', mountPath: '/mnt/controller' }]
      }]
      volumes: [{
        name: 'controller-state'
        storageType: 'NfsAzureFile'
        storageName: 'controller-state'
        mountOptions: 'vers=4,minorversion=1,sec=sys'
      }]
    }
  }
  dependsOn: [mountedShare]
}

output environmentName string = appEnvironment.outputs.name
output storageAccountName string = storage.name
output shareName string = share.name
output smokeJobName string = smoke.name
output controllerImage string = image
