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

function Get-ContainerApp {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $ResourceGroup,
        [Parameter(Mandatory = $true)][string] $Subscription
    )

    $app = Invoke-AzJson -Arguments @(
        'containerapp', 'show', '--name', $Name,
        '--resource-group', $ResourceGroup, '--subscription', $Subscription
    )
    $container = @($app.properties.template.containers)[0]
    if (-not $container -or [string]::IsNullOrWhiteSpace([string]$container.image)) {
        throw "Container App '$Name' has no deployable image."
    }
    return $app
}

function Add-UserIds {
    param(
        [guid[]] $Current = @(),
        [string[]] $Values = @()
    )

    $resolved = @($Current)
    foreach ($value in $Values) {
        foreach ($candidate in ($value -split ',')) {
            $candidate = $candidate.Trim()
            if ([string]::IsNullOrWhiteSpace($candidate)) {
                continue
            }
            $parsed = [guid]::Empty
            if (-not [guid]::TryParse($candidate, [ref]$parsed)) {
                throw 'ALLOWED_USER_IDS contains an invalid GUID.'
            }
            $resolved += $parsed
        }
    }
    return @($resolved | Select-Object -Unique)
}

foreach ($name in @(
    'AZURE_SUBSCRIPTION_ID',
    'AZURE_RESOURCE_GROUP',
    'AZURE_CONTAINER_APPS_ENVIRONMENT_NAME',
    'AZURE_CONTAINER_REGISTRY_NAME',
    'FOUNDRY_ACCOUNT_NAME',
    'FOUNDRY_IMPORT_DEPLOYMENT_NAME'
)) {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) {
        throw "Required AZD environment value '$name' is missing. Run azd provision after configuring the environment."
    }
}

$subscriptionId = [guid]$env:AZURE_SUBSCRIPTION_ID
$resourceGroupName = $env:AZURE_RESOURCE_GROUP
$environmentName = $env:AZURE_CONTAINER_APPS_ENVIRONMENT_NAME
$registryName = $env:AZURE_CONTAINER_REGISTRY_NAME
$foundryAccountName = $env:FOUNDRY_ACCOUNT_NAME
$importDeploymentName = $env:FOUNDRY_IMPORT_DEPLOYMENT_NAME

$tenantId = [guid](Invoke-AzJson -Arguments @(
    'account', 'show', '--subscription', $subscriptionId.ToString(), '--query', 'tenantId'
))
$baseOutputs = Invoke-AzJson -Arguments @(
    'deployment', 'group', 'show', '--name', 'examinator-base',
    '--resource-group', $resourceGroupName, '--subscription', $subscriptionId.ToString(),
    '--query', 'properties.outputs'
)
if (-not $baseOutputs.webIdentityPrincipalId.value) {
    throw 'The base infrastructure deployment did not return the web identity principal ID.'
}

$identityScript = Join-Path $PSScriptRoot '..\identity.ps1'
$identity = & $identityScript -TenantId $tenantId -SubscriptionId $subscriptionId `
    -GatewayPrincipalId ([guid]$baseOutputs.webIdentityPrincipalId.value) -Deploy -Confirm:$false
if (-not $identity.ApiAppId -or -not $identity.WebAppId) {
    throw 'Entra application registration setup did not return both application IDs.'
}

$ensureFoundryAccessScript = Join-Path $PSScriptRoot '..\ensure-foundry-access.ps1'
& $ensureFoundryAccessScript -SubscriptionId $subscriptionId `
    -ResourceGroupName $resourceGroupName -Location $env:AZURE_LOCATION `
    -AccountName $foundryAccountName `
    -PrincipalId ([guid]$baseOutputs.apiIdentityPrincipalId.value)

$api = Get-ContainerApp -Name 'examinator-api' -ResourceGroup $resourceGroupName -Subscription $subscriptionId.ToString()
$web = Get-ContainerApp -Name 'examinator-web' -ResourceGroup $resourceGroupName -Subscription $subscriptionId.ToString()
$allowedUserIds = @()
$existingAllowedUserIds = @(
    @($web.properties.template.containers[0].env) |
        Where-Object { $_.name -match '^Gateway__AllowedUserIds__\d+$' } |
        ForEach-Object { [string]$_.value }
)
if (-not [string]::IsNullOrWhiteSpace($env:ALLOWED_USER_IDS)) {
    $existingAllowedUserIds += $env:ALLOWED_USER_IDS
}
$allowedUserIds = Add-UserIds -Values $existingAllowedUserIds

$appsScript = Join-Path $PSScriptRoot '..\apps.ps1'
& $appsScript -SubscriptionId $subscriptionId -TenantId $tenantId `
    -ApiAppId ([string]$identity.ApiAppId) -WebAppId ([string]$identity.WebAppId) `
    -ApiImage ([string]$api.properties.template.containers[0].image) `
    -WebImage ([string]$web.properties.template.containers[0].image) `
    -AllowedUserIds $allowedUserIds -ResourceGroupName $resourceGroupName `
    -EnvironmentName $environmentName -RegistryName $registryName `
    -FoundryAccountName $foundryAccountName -FoundryDeploymentName 'exam-classifier' `
    -EnableImports -ImportDeploymentName $importDeploymentName -Deploy -Confirm:$false
