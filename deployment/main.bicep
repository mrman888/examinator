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
param enableImports bool = false
param keyVaultWriterObjectId string = ''
param logAnalyticsName string = 'examinator-logs'
param appInsightsName string = 'examinator-insights'

var storageName = 'exam${uniqueString(resourceGroup().id)}'
var vaultName = 'examkv-${uniqueString(resourceGroup().id)}'

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: vaultName
  location: location
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    enablePurgeProtection: true
    softDeleteRetentionInDays: 90
    publicNetworkAccess: 'Enabled'
    accessPolicies: []
  }
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = if (enableImports) {
  name: storageName
  location: location
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    publicNetworkAccess: 'Enabled'
  }
}

resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = if (enableImports) {
  parent: storage
  name: 'default'
  properties: {
    isVersioningEnabled: true
    deleteRetentionPolicy: { enabled: true, days: 7 }
    containerDeleteRetentionPolicy: { enabled: true, days: 7 }
  }
}
resource containers 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = [for name in [
  'exam-documents'
  'exam-imports'
  'exam-results'
]: if (enableImports) {
  parent: blobs
  name: name
  properties: { publicAccess: 'None' }
}]
resource lifecycle 'Microsoft.Storage/storageAccounts/managementPolicies@2023-05-01' = if (enableImports) {
  parent: storage
  name: 'default'
  properties: {
    policy: {
      rules: [{
        name: 'expire-historical-draft-versions'
        enabled: true
        type: 'Lifecycle'
        definition: {
          filters: { blobTypes: ['blockBlob'], prefixMatch: ['exam-imports/'] }
          actions: { version: { delete: { daysAfterCreationGreaterThan: 7 } } }
        }
      }]
    }
  }
}
resource tableService 'Microsoft.Storage/storageAccounts/tableServices@2023-05-01' = if (enableImports) {
  parent: storage
  name: 'default'
}
resource tables 'Microsoft.Storage/storageAccounts/tableServices/tables@2023-05-01' = [for name in [
  'ImportJobs'
  'ExamCatalog'
]: if (enableImports) {
  parent: tableService
  name: name
}]
resource queueService 'Microsoft.Storage/storageAccounts/queueServices@2023-05-01' = if (enableImports) {
  parent: storage
  name: 'default'
}
resource queues 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-05-01' = [for name in [
  'exam-imports'
  'exam-imports-poison'
]: if (enableImports) {
  parent: queueService
  name: name
}]

resource storageRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for role in [
  'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
  '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
  '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
]: if (enableImports) {
  name: guid(storage.id, apiIdentity.id, role)
  scope: storage
  properties: {
    principalId: apiIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', role)
  }
}]
resource webVaultReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, webIdentity.id, 'secrets-reader')
  scope: vault
  properties: {
    principalId: webIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
  }
}
resource vaultWriter 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(keyVaultWriterObjectId)) {
  name: guid(vault.id, keyVaultWriterObjectId, 'secrets-writer')
  scope: vault
  properties: {
    principalId: keyVaultWriterObjectId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7')
  }
}

resource registry 'Microsoft.ContainerRegistry/registries@2025-11-01' = {
  name: registryName
  location: location
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: false
    anonymousPullEnabled: false
    dataEndpointEnabled: false
    encryption: {
      status: 'disabled'
    }
    networkRuleBypassAllowedForTasks: false
    publicNetworkAccess: 'Enabled'
    roleAssignmentMode: 'LegacyRegistryPermissions'
    policies: {
      azureADAuthenticationAsArmPolicy: {
        status: 'enabled'
      }
    }
  }
  tags: {
    application: 'examinator'
  }
}

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsName
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    workspaceCapping: { dailyQuotaGb: 1 }
  }
  tags: {
    application: 'examinator'
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
    IngestionMode: 'LogAnalytics'
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
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
    peerAuthentication: {
      mtls: {
        enabled: false
      }
    }
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
output logAnalyticsWorkspaceId string = logAnalytics.properties.customerId
output appInsightsConnectionString string = appInsights.properties.ConnectionString
output keyVaultName string = vault.name
output keyVaultUri string = vault.properties.vaultUri
output storageName string = enableImports ? storage!.name : ''
output blobEndpoint string = enableImports ? storage!.properties.primaryEndpoints.blob : ''
output queueEndpoint string = enableImports ? storage!.properties.primaryEndpoints.queue : ''
output tableEndpoint string = enableImports ? storage!.properties.primaryEndpoints.table : ''
