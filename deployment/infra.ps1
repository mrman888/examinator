#Requires -Version 5.1
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [guid] $SubscriptionId,
    [string] $ResourceGroupName = 'examinator',
    [ValidateSet('swedencentral')]
    [string] $Location = 'swedencentral',
    [string] $RegistryName,
    [string] $EnvironmentName = 'examinator-aca',
    [switch] $EnableImports,
    [switch] $Deploy
)

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

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw 'Install Azure CLI and sign in with az login.'
}
if ([string]::IsNullOrWhiteSpace($RegistryName)) {
    $RegistryName = "examacr$($SubscriptionId.ToString('N').Substring(0, 8))"
}
if ($RegistryName -notmatch '^[a-z0-9]{5,50}$') {
    throw 'RegistryName must be 5-50 lowercase letters and digits.'
}

$subscription = $SubscriptionId.ToString()
$account = Invoke-AzJson -Arguments @('account', 'show', '--subscription', $subscription)
$template = Join-Path $PSScriptRoot 'main.bicep'
& az bicep build --file $template --stdout --only-show-errors | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'Bicep compilation failed. Install/update Bicep with az bicep install.'
}

$exists = Invoke-AzJson -Arguments @(
    'group', 'exists', '--name', $ResourceGroupName, '--subscription', $subscription
)
if ($exists) {
    $registries = @(Invoke-AzJson -Arguments @(
        'acr', 'list', '--resource-group', $ResourceGroupName, '--subscription', $subscription
    ))
    $current = @($registries | Where-Object { $_.name -eq $RegistryName }) | Select-Object -First 1
    if ($current -and ($current.sku.name -ne 'Basic' -or $current.adminUserEnabled)) {
        throw 'The existing registry is not Basic/private-identity-pull compatible.'
    }
}

Write-Host "Target: $subscription / $ResourceGroupName / $Location"
Write-Host "Base infrastructure: ACR Basic ($RegistryName), Consumption ACA ($EnvironmentName), two user-assigned identities."
Write-Host 'ACR admin credentials and persistent log analytics are disabled.'
if (-not $Deploy) {
    Write-Host 'Preflight complete; no Azure resources changed. Rerun with -Deploy to provision.'
    return
}
if (-not $PSCmdlet.ShouldProcess("$subscription/$ResourceGroupName", 'Provision deployment base infrastructure')) {
    return
}
$writer = if ($account.user.type -eq 'servicePrincipal') {
    Invoke-AzJson -Arguments @('ad', 'sp', 'show', '--id', $account.user.name, '--query', 'id')
} else {
    Invoke-AzJson -Arguments @('ad', 'signed-in-user', 'show', '--query', 'id')
}
if ([string]::IsNullOrWhiteSpace([string]$writer)) { throw 'Resolve the deployer object ID for Key Vault secret write access.' }

if (-not $exists) {
    $null = Invoke-AzJson -Arguments @(
        'group', 'create', '--name', $ResourceGroupName, '--location', $Location,
        '--subscription', $subscription
    )
}
$deployment = Invoke-AzJson -Arguments @(
    'deployment', 'group', 'create', '--name', 'examinator-base',
    '--resource-group', $ResourceGroupName, '--subscription', $subscription,
    '--template-file', $template, '--mode', 'Incremental',
    '--parameters', "location=$Location", "registryName=$RegistryName",
    "environmentName=$EnvironmentName", "enableImports=$($EnableImports.IsPresent.ToString().ToLowerInvariant())",
    "keyVaultWriterObjectId=$writer"
)

$deployment.properties.outputs | ConvertTo-Json -Depth 10
Write-Host 'Base infrastructure deployed. No container images or application registrations were created.'
