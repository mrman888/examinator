targetScope = 'resourceGroup'

@description('EU Data Zone is enforced by fixing both region and deployment SKU.')
@allowed([
  'swedencentral'
])
param location string = 'swedencentral'

param accountName string
param projectName string = 'examinator'
param deploymentName string = 'exam-classifier'
param modelName string = 'gpt-5-mini'
param modelVersion string = '2025-08-07'

@minValue(1)
param capacity int = 10

@description('Object ID of the developer or backend identity allowed to call the model.')
param principalId string

@allowed([
  'User'
  'ServicePrincipal'
])
param principalType string = 'User'

resource account 'Microsoft.CognitiveServices/accounts@2025-06-01' = {
  name: accountName
  location: location
  kind: 'AIServices'
  sku: {
    name: 'S0'
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    customSubDomainName: accountName
    allowProjectManagement: true
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
  }
  tags: {
    application: 'examinator'
    environment: 'development'
  }
}

resource project 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' = {
  parent: account
  name: projectName
  location: location
  // Account child mutations must not overlap in the Cognitive Services control plane.
  dependsOn: [
    model
  ]
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    displayName: 'Examinator'
    description: 'Exam import and topic classification.'
  }
}

resource model 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = {
  parent: account
  name: deploymentName
  sku: {
    name: 'DataZoneStandard'
    capacity: capacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: modelName
      version: modelVersion
    }
    versionUpgradeOption: 'NoAutoUpgrade'
  }
}

var openAIUserRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')

resource inferenceAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(account.id, principalId, openAIUserRoleId)
  scope: account
  properties: {
    roleDefinitionId: openAIUserRoleId
    principalId: principalId
    principalType: principalType
  }
}

output endpoint string = account.properties.endpoint
output deploymentName string = model.name
output projectResourceId string = project.id
output accountResourceId string = account.id
