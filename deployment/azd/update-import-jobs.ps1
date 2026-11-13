#Requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-AzJson {
    param([Parameter(Mandatory = $true)][string[]] $Arguments)

    $result = & az @Arguments --only-show-errors --output json
    if ($LASTEXITCODE -ne 0) {
        throw "Azure CLI command failed: az $($Arguments -join ' ')"
    }
    if ($result) {
        return (($result -join "`n") | ConvertFrom-Json)
    }
}

foreach ($name in @(
    'AZURE_SUBSCRIPTION_ID',
    'AZURE_RESOURCE_GROUP',
    'AZURE_LOCATION',
    'FOUNDRY_ACCOUNT_NAME',
    'FOUNDRY_IMPORT_DEPLOYMENT_NAME'
)) {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) {
        throw "Required AZD environment value '$name' is missing. Run azd provision before deploying the API."
    }
}

$subscriptionId = [guid]$env:AZURE_SUBSCRIPTION_ID
$resourceGroupName = $env:AZURE_RESOURCE_GROUP
$api = Invoke-AzJson -Arguments @(
    'containerapp', 'show', '--name', 'examinator-api',
    '--resource-group', $resourceGroupName, '--subscription', $subscriptionId.ToString()
)
$apiImage = [string]@($api.properties.template.containers)[0].image
if ([string]::IsNullOrWhiteSpace($apiImage)) {
    throw 'The deployed API Container App has no image; import jobs were not updated.'
}

$baseOutputs = Invoke-AzJson -Arguments @(
    'deployment', 'group', 'show', '--name', 'examinator-base',
    '--resource-group', $resourceGroupName, '--subscription', $subscriptionId.ToString(),
    '--query', 'properties.outputs'
)
foreach ($name in @(
    'environmentId',
    'apiIdentityId',
    'apiIdentityClientId',
    'registryLoginServer',
    'storageName',
    'blobEndpoint',
    'queueEndpoint',
    'tableEndpoint'
)) {
    if ([string]::IsNullOrWhiteSpace([string]$baseOutputs.$name.value)) {
        throw "Base infrastructure output '$name' is missing; import jobs were not updated."
    }
}

$template = Join-Path $PSScriptRoot '..\imports.bicep'
$arguments = @(
    'deployment', 'group', 'create',
    '--name', 'examinator-imports',
    '--resource-group', $resourceGroupName,
    '--subscription', $subscriptionId.ToString(),
    '--template-file', $template,
    '--mode', 'Incremental',
    '--parameters',
    "location=$($env:AZURE_LOCATION)",
    "environmentId=$($baseOutputs.environmentId.value)",
    "apiIdentityId=$($baseOutputs.apiIdentityId.value)",
    "apiIdentityClientId=$($baseOutputs.apiIdentityClientId.value)",
    "registryServer=$($baseOutputs.registryLoginServer.value)",
    "apiImage=$apiImage",
    "storageName=$($baseOutputs.storageName.value)",
    "blobEndpoint=$($baseOutputs.blobEndpoint.value)",
    "queueEndpoint=$($baseOutputs.queueEndpoint.value)",
    "tableEndpoint=$($baseOutputs.tableEndpoint.value)",
    "foundryEndpoint=https://$($env:FOUNDRY_ACCOUNT_NAME).cognitiveservices.azure.com/",
    "importDeployment=$($env:FOUNDRY_IMPORT_DEPLOYMENT_NAME)"
)
& az @arguments --only-show-errors --output none
if ($LASTEXITCODE -ne 0) {
    throw 'Updating the import worker and reconciler jobs failed.'
}
