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

var image = loadJsonContent('image.json').image
var setup = loadTextContent('controller-runtime.py')
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

// Fresh setup only. Never use this template as an active-controller update path.
resource app 'Microsoft.App/containerApps@2026-07-01' = {
  name: '${name}-controller'
  location: location
  tags: { managedBy: 'repoagent-controller', repository: repository, purpose: 'controller-setup' }
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
    }
    template: {
      initContainers: [{
        name: 'private-layout'
        image: image
        command: ['/usr/bin/python3', '-E', '-c']
        args: [setup, 'initialize', string(binding)]
        resources: { cpu: json('0.25'), memory: '0.5Gi' }
        volumeMounts: [{ volumeName: 'controller-state', mountPath: '/mnt/controller' }]
      }]
      containers: [{
        name: 'controller'
        image: image
        command: ['/usr/bin/python3', '-E', '-c']
        args: [setup, 'serve']
        resources: { cpu: json('0.25'), memory: '0.5Gi' }
        volumeMounts: [
          { volumeName: 'controller-state', mountPath: '/data', subPath: 'runtime/data' }
          { volumeName: 'controller-state', mountPath: '/workspace', subPath: 'runtime/workspace' }
        ]
        probes: [{
          type: 'Readiness'
          httpGet: { path: '/health/setup', port: 8080 }
          periodSeconds: 5
          timeoutSeconds: 2
        }]
      }]
      scale: {
        minReplicas: 0
        maxReplicas: 1
        rules: [{ name: 'setup-http', http: { metadata: { concurrentRequests: '10' } } }]
      }
      volumes: [{
        name: 'controller-state'
        storageType: 'NfsAzureFile'
        storageName: 'controller-state'
        mountOptions: 'vers=4,minorversion=1,sec=sys'
      }]
    }
  }
}

output appName string = app.name
// App-level ingress, never CONTAINER_APP_HOSTNAME (a revision hostname).
output publicOrigin string = 'https://${app.properties.configuration.ingress.fqdn}'
