targetScope = 'subscription'

@description('The approved Azure region for the Development environment.')
@allowed([
  'swedencentral'
])
param location string

@description('The existing resource group that contains the Development environment.')
param resourceGroupName string

@description('The deployer object ID that receives Key Vault Secrets Officer.')
param keyVaultWriterObjectId string

@description('The existing Azure Container Apps environment.')
param environmentName string = 'examinator-aca'

@description('The existing Foundry account.')
param foundryAccountName string = 'examinator-46954202'

@description('The existing Foundry deployment used by exam imports.')
param foundryImportDeploymentName string = 'exam-classifier'

@description('The existing API Container App.')
param apiAppName string = 'examinator-api'

@description('The existing Web Container App.')
param webAppName string = 'examinator-web'

var registryName = 'examacr${take(replace(subscription().subscriptionId, '-', ''), 8)}'

resource targetResourceGroup 'Microsoft.Resources/resourceGroups@2023-07-01' existing = {
  name: resourceGroupName
}

resource apiApp 'Microsoft.App/containerApps@2025-07-01' existing = {
  name: apiAppName
  scope: targetResourceGroup
}

resource webApp 'Microsoft.App/containerApps@2025-07-01' existing = {
  name: webAppName
  scope: targetResourceGroup
}

module base '../deployment/main.bicep' = {
  name: 'examinator-base'
  scope: targetResourceGroup
  params: {
    location: location
    registryName: registryName
    environmentName: environmentName
    enableImports: true
    keyVaultWriterObjectId: keyVaultWriterObjectId
  }
}

module imports '../deployment/imports.bicep' = {
  name: 'examinator-imports'
  scope: targetResourceGroup
  params: {
    location: location
    environmentId: base.outputs.environmentId
    apiIdentityId: base.outputs.apiIdentityId
    apiIdentityClientId: base.outputs.apiIdentityClientId
    registryServer: base.outputs.registryLoginServer
    apiImage: apiApp.properties.template.containers[0].image
    storageName: base.outputs.storageName
    blobEndpoint: base.outputs.blobEndpoint
    queueEndpoint: base.outputs.queueEndpoint
    tableEndpoint: base.outputs.tableEndpoint
    foundryEndpoint: 'https://${foundryAccountName}.cognitiveservices.azure.com/'
    importDeployment: foundryImportDeploymentName
    appInsightsConnectionString: base.outputs.appInsightsConnectionString
  }
}

output AZURE_RESOURCE_GROUP string = targetResourceGroup.name
output AZURE_LOCATION string = location
output AZURE_TENANT_ID string = subscription().tenantId
output AZURE_CONTAINER_REGISTRY_NAME string = registryName
output AZURE_CONTAINER_REGISTRY_ENDPOINT string = base.outputs.registryLoginServer
output AZURE_CONTAINER_APPS_ENVIRONMENT_NAME string = environmentName
output API_IMAGE string = apiApp.properties.template.containers[0].image
output WEB_IMAGE string = webApp.properties.template.containers[0].image
output FOUNDRY_ACCOUNT_NAME string = foundryAccountName
output FOUNDRY_IMPORT_DEPLOYMENT_NAME string = foundryImportDeploymentName
