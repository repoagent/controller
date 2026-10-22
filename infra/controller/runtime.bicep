targetScope = 'resourceGroup'

@minLength(3)
@maxLength(21)
param name string = 'storybeam-repoagent'
param location string = 'swedencentral'
param repository string = 'StoryBeam/StoryBeam'
@minValue(1)
param repositoryId int = 1069791767
@minValue(1)
param appId int = 5387625
@minValue(1)
param installationId int = 173558660
param brokerOrigin string = 'https://repoagent-auth.livelydune-309701b4.swedencentral.azurecontainerapps.io'
@description('Pinned version of controller-enrollment uploaded through a private file.')
@minLength(32)
@maxLength(32)
param enrollmentVersion string

var image = loadJsonContent('image.json').image
var script = loadTextContent('controller-runtime.py')
var binding = {
  version: 1
  repository: repository
  repositoryId: repositoryId
  appId: appId
  installationId: installationId
  brokerOrigin: brokerOrigin
}
resource appEnvironment 'Microsoft.App/managedEnvironments@2026-07-01' existing = {
  name: '${name}-environment'
}
resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: '${name}-identity'
}
resource vault 'Microsoft.KeyVault/vaults@2026-05-15' existing = {
  name: 'rac${uniqueString(resourceGroup().id)}kv'
}

var settings = {
  repository: repository
  appId: string(appId)
  installationId: installationId
  brokerUrl: brokerOrigin
  clone: true
  mode: 'fixed-url'
  publicUrl: 'https://${name}-controller.${appEnvironment.properties.defaultDomain}'
  mentionUser: 'repoagent'
  model: 'gpt-6.1-sol'
  webSearch: 'live'
  toolUpdates: 'pinned'
  listenPort: 8080
  reconcileInterval: 300
}

// First activation only: settle the old broker owner before enrolling this device.
resource app 'Microsoft.App/containerApps@2026-07-01' = {
  name: '${name}-controller'
  location: location
  tags: { managedBy: 'repoagent-controller', repository: repository, purpose: 'controller-active' }
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${identity.id}': {} } }
  properties: {
    environmentId: appEnvironment.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: { external: true, allowInsecure: false, targetPort: 8080, transport: 'http' }
      secrets: [{
        name: 'controller-enrollment'
        identity: identity.id
        keyVaultUrl: '${vault.properties.vaultUri}secrets/controller-enrollment/${enrollmentVersion}'
      }]
    }
    template: {
      initContainers: [{
        name: 'private-layout'
        image: image
        command: ['/usr/bin/python3', '-E', '-c']
        args: [script, 'initialize-active', string(binding)]
        resources: { cpu: json('0.25'), memory: '0.5Gi' }
        volumeMounts: [{ volumeName: 'controller-state', mountPath: '/mnt/controller' }]
      }]
      containers: [{
        name: 'controller'
        image: image
        command: ['/usr/bin/python3', '-E', '-c']
        args: [script, 'activate', string(settings)]
        resources: { cpu: json('0.25'), memory: '0.5Gi' }
        volumeMounts: [
          { volumeName: 'controller-state', mountPath: '/data', subPath: 'runtime/data' }
          { volumeName: 'controller-state', mountPath: '/workspace', subPath: 'runtime/workspace' }
          { volumeName: 'enrollment', mountPath: '/run/enrollment' }
        ]
        probes: [{ type: 'Readiness', tcpSocket: { port: 8080 }, periodSeconds: 5, timeoutSeconds: 2 }]
      }]
      scale: { minReplicas: 1, maxReplicas: 1 }
      volumes: [
        {
          name: 'controller-state'
          storageType: 'NfsAzureFile'
          storageName: 'controller-state'
          mountOptions: 'vers=4,minorversion=1,sec=sys'
        }
        {
          name: 'enrollment'
          storageType: 'Secret'
          secrets: [{ secretRef: 'controller-enrollment', path: 'enrollment.json' }]
        }
      ]
    }
  }
}

output publicOrigin string = 'https://${app.properties.configuration.ingress.fqdn}'
