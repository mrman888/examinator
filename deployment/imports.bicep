targetScope = 'resourceGroup'

param location string = 'swedencentral'
param environmentId string
param apiIdentityId string
param apiIdentityClientId string
param registryServer string
param apiImage string
param storageName string
param blobEndpoint string
param queueEndpoint string
param tableEndpoint string
param foundryEndpoint string
param importDeployment string

var environment = [
  { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
  { name: 'DOTNET_ENVIRONMENT', value: 'Production' }
  { name: 'Azure__ManagedIdentityClientId', value: apiIdentityClientId }
  { name: 'Import__Enabled', value: 'true' }
  { name: 'Import__MaximumModelCalls', value: '64' }
  { name: 'Storage__BlobEndpoint', value: blobEndpoint }
  { name: 'Storage__QueueEndpoint', value: queueEndpoint }
  { name: 'Storage__TableEndpoint', value: tableEndpoint }
  { name: 'Foundry__Endpoint', value: foundryEndpoint }
  { name: 'Foundry__ImportDeployment', value: importDeployment }
]

// Stable API keeps identity-authenticated scaling without the AVM module's preview API.
resource jobs 'Microsoft.App/jobs@2025-07-01' = [for mode in ['worker', 'reconcile']: {
  name: 'examinator-import-${mode}'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${apiIdentityId}': {} }
  }
  properties: {
    environmentId: environmentId
    workloadProfileName: 'Consumption'
    configuration: {
      triggerType: mode == 'worker' ? 'Event' : 'Schedule'
      replicaTimeout: mode == 'worker' ? 14400 : 600
      replicaRetryLimit: 0
      registries: [{ server: registryServer, identity: apiIdentityId }]
      eventTriggerConfig: mode == 'worker' ? {
        parallelism: 1
        replicaCompletionCount: 1
        scale: {
          pollingInterval: 30
          minExecutions: 0
          maxExecutions: 1
          rules: [{
            name: 'exam-import-queue'
            type: 'azure-queue'
            identity: apiIdentityId
            metadata: { accountName: storageName, queueName: 'exam-imports', queueLength: '1' }
          }]
        }
      } : null
      scheduleTriggerConfig: mode == 'reconcile' ? {
        cronExpression: '*/5 * * * *'
        parallelism: 1
        replicaCompletionCount: 1
      } : null
    }
    template: {
      containers: [{
        name: 'import'
        image: apiImage
        command: ['dotnet', 'Api.dll']
        args: [mode == 'worker' ? '--import-worker' : '--reconcile']
        env: environment
        resources: { cpu: 1, memory: '2Gi' }
      }]
    }
  }
}]
