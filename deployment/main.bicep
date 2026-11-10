targetScope = 'resourceGroup'

@description('The application is intentionally pinned to Sweden Central.')
@allowed([
  'swedencentral'
])
param location string = 'swedencentral'

@description('Globally unique lowercase Azure Container Registry name.')
@minLength(5)
@maxLength(50)
param registryName string

param environmentName string = 'examinator-aca'
param apiIdentityName string = 'examinator-api'
param webIdentityName string = 'examinator-web'

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: registryName
  location: location
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: false
    publicNetworkAccess: 'Enabled'
  }
  tags: {
    application: 'examinator'
  }
}

resource environment 'Microsoft.App/managedEnvironments@2023-05-01' = {
  name: environmentName
  location: location
  properties: {
    appLogsConfiguration: {
      destination: ''
    }
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
  }
  tags: {
    application: 'examinator'
  }
}

resource apiIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: apiIdentityName
  location: location
  tags: {
    application: 'examinator'
  }
}

resource webIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: webIdentityName
  location: location
  tags: {
    application: 'examinator'
  }
}

var acrPullRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '7f951dda-4ed3-4680-a7ca-43fe172d538d'
)

resource apiAcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, apiIdentity.id, acrPullRoleId)
  scope: registry
  properties: {
    roleDefinitionId: acrPullRoleId
    principalId: apiIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource webAcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, webIdentity.id, acrPullRoleId)
  scope: registry
  properties: {
    roleDefinitionId: acrPullRoleId
    principalId: webIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

output registryLoginServer string = registry.properties.loginServer
output registryId string = registry.id
output environmentId string = environment.id
output environmentDefaultDomain string = environment.properties.defaultDomain
output apiIdentityId string = apiIdentity.id
output apiIdentityClientId string = apiIdentity.properties.clientId
output apiIdentityPrincipalId string = apiIdentity.properties.principalId
output webIdentityId string = webIdentity.id
output webIdentityClientId string = webIdentity.properties.clientId
output webIdentityPrincipalId string = webIdentity.properties.principalId
