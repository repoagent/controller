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
@description('Keep one setup replica available while completing device authentication.')
@allowed([0, 1])
param setupMinReplicas int = 0
@description('NFS supports the active storage path. SMB is setup-only: native SQLite persistence failed in real Azure checks.')
@allowed(['NfsAzureFile', 'AzureFile'])
param storageType string = 'NfsAzureFile'
@description('Empty for the original setup profile; otherwise use this dedicated queue through system identity.')
param workQueueAccountName string = ''
param workQueueName string = 'controller-work'
@description('First activation only, after supported old-owner settlement and private enrollment persistence.')
param activate bool = false
@secure()
param enrollment string = ''
param controllerImage string = loadJsonContent('image.json').image

var image = controllerImage
var setup = loadTextContent('controller-runtime.py')
// ACA accepts this restricted SMB option set; Unix modes and symlinks still need real acceptance.
// Keep byte-range locking enabled. Never add nobrl to bypass a failed lease.
var mountOptions = storageType == 'AzureFile'
  ? 'vers=3.1.1,uid=1000,gid=1000,dir_mode=0700,file_mode=0600,serverino,mfsymlinks'
  : 'vers=4,minorversion=1,sec=sys'
var executableVolume = storageType == 'AzureFile' ? 'controller-executable' : 'controller-state'
var executableMountOptions = 'vers=3.1.1,uid=1000,gid=1000,dir_mode=0700,file_mode=0700,serverino,mfsymlinks'
var binding = {
  version: 1
  repository: repository
  repositoryId: repositoryId
  appId: appId
  installationId: installationId
  brokerOrigin: brokerOrigin
}
var settings = {
  repository: repository
  repositoryId: repositoryId
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
  azureWorkQueue: 'https://${workQueueAccountName}.queue.${environment().suffixes.storage}/${workQueueName}'
}

resource appEnvironment 'Microsoft.App/managedEnvironments@2026-07-01' existing = {
  name: '${name}-environment'
}

// Never use this first-activation template as an active-controller update path.
resource app 'Microsoft.App/containerApps@2026-07-01' = {
  name: '${name}-controller'
  location: location
  tags: { managedBy: 'repoagent-controller', repository: repository, purpose: activate ? 'controller-active' : 'controller-setup' }
  identity: { type: empty(workQueueAccountName) ? 'None' : 'SystemAssigned' }
  properties: {
    environmentId: appEnvironment.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        allowInsecure: false
        targetPort: 8080
        transport: 'http'
      }
      secrets: activate ? [{ name: 'controller-enrollment', value: enrollment }] : []
    }
    template: {
      initContainers: [{
        name: 'private-layout'
        image: image
        command: ['/usr/bin/python3', '-E', '-c']
        args: [setup, activate ? 'initialize-active' : 'initialize', string(binding)]
        resources: { cpu: json('0.25'), memory: '0.5Gi' }
        volumeMounts: [{ volumeName: 'controller-state', mountPath: '/mnt/controller' }]
      }]
      containers: [{
        name: 'controller'
        image: image
        command: ['/usr/bin/python3', '-E', '-c']
        args: activate ? [setup, 'activate', string(settings)] : [setup, 'serve']
        resources: { cpu: json('0.25'), memory: '0.5Gi' }
        volumeMounts: concat([
          { volumeName: 'controller-state', mountPath: '/data', subPath: 'runtime/data' }
          { volumeName: executableVolume, mountPath: '/workspace', subPath: 'runtime/workspace' }
        ], storageType == 'AzureFile' ? [
          { volumeName: executableVolume, mountPath: '/data/tools', subPath: 'runtime/data/tools' }
          { volumeName: executableVolume, mountPath: '/data/state/bridge.json.github-app/bin', subPath: 'runtime/data/state/bridge.json.github-app/bin' }
          { volumeName: executableVolume, mountPath: '/data/state/bridge.json.github-app/clone-bin', subPath: 'runtime/data/state/bridge.json.github-app/clone-bin' }
        ] : [], activate ? [{ volumeName: 'enrollment', mountPath: '/run/enrollment' }] : [])
        probes: activate ? [{ type: 'Readiness', tcpSocket: { port: 8080 }, periodSeconds: 5, timeoutSeconds: 2 }] : [{
          type: 'Readiness'
          httpGet: { path: '/health/setup', port: 8080 }
          periodSeconds: 5
          timeoutSeconds: 2
        }]
      }]
      scale: union({
        minReplicas: setupMinReplicas
        maxReplicas: 1
        rules: concat([{ name: empty(workQueueAccountName) ? 'setup-http' : 'controller-http', http: { metadata: { concurrentRequests: '10' } } }], empty(workQueueAccountName) ? [] : [{
          name: 'controller-work'
          custom: {
            type: 'azure-queue'
            identity: 'system'
            metadata: {
              accountName: workQueueAccountName
              queueName: workQueueName
              queueLength: '1'
              activationQueueLength: '0'
              queueLengthStrategy: 'all'
            }
          }
        }])
      }, empty(workQueueAccountName) ? {} : { cooldownPeriod: 60, pollingInterval: 15 })
      volumes: concat([{
        name: 'controller-state'
        storageType: storageType
        storageName: 'controller-state'
        mountOptions: mountOptions
      }], storageType == 'AzureFile' ? [{
        name: 'controller-executable'
        storageType: 'AzureFile'
        storageName: 'controller-state'
        mountOptions: executableMountOptions
      }] : [], activate ? [{ name: 'enrollment', storageType: 'Secret', secrets: [{ secretRef: 'controller-enrollment', path: 'enrollment.json' }] }] : [])
    }
  }
}

output appName string = app.name
// App-level ingress, never CONTAINER_APP_HOSTNAME (a revision hostname).
output publicOrigin string = 'https://${app.properties.configuration.ingress.fqdn}'
output principalId string = app.identity.?principalId ?? ''
