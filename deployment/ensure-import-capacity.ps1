#Requires -Version 5.1
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [guid] $SubscriptionId,
    [Parameter(Mandatory = $true)]
    [string] $ResourceGroupName,
    [Parameter(Mandatory = $true)]
    [string] $AccountName,
    [Parameter(Mandatory = $true)]
    [string] $DeploymentName,
    [ValidateRange(1, 1000)]
    [int] $MinimumCapacity = 150
)

# Import requests send every rendered page image in one call. A single request is far larger than
# the 10K tokens/minute of a 10-unit deployment, which Foundry rejects with HTTP 429 on every attempt.
# This only raises capacity; it never lowers it or changes the model, version or SKU.

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

$subscription = $SubscriptionId.ToString()
$deployment = Invoke-AzJson -Arguments @(
    'cognitiveservices', 'account', 'deployment', 'show', '--name', $AccountName,
    '--resource-group', $ResourceGroupName, '--deployment-name', $DeploymentName,
    '--subscription', $subscription
)
if ($null -eq $deployment -or $null -eq $deployment.sku -or $null -eq $deployment.properties.model) {
    throw "Import deployment '$DeploymentName' was not found on Foundry account '$AccountName'."
}
$current = [int]$deployment.sku.capacity
if ($current -ge $MinimumCapacity) {
    Write-Host "Import deployment $DeploymentName has capacity $current (minimum $MinimumCapacity); unchanged."
    return
}

$account = Invoke-AzJson -Arguments @(
    'cognitiveservices', 'account', 'show', '--name', $AccountName,
    '--resource-group', $ResourceGroupName, '--subscription', $subscription
)
# Reading quota needs subscription-scope access, which a resource-group-scoped deploy identity lacks.
# The quota check is then skipped; Azure still rejects the capacity change if quota is insufficient.
$usageName = "OpenAI.$($deployment.sku.name).$($deployment.properties.model.name)"
$quota = $null
try {
    $usage = @(Invoke-AzJson -Arguments @(
        'cognitiveservices', 'usage', 'list', '--location', [string]$account.location, '--subscription', $subscription
    ))
    $quota = @($usage | Where-Object { $null -ne $_ -and $_.name.value -eq $usageName }) | Select-Object -First 1
}
catch {
    $quota = $null
}
if ($null -eq $quota) {
    Write-Warning "Unable to read Foundry quota $usageName; Azure validates the quota when the capacity is changed."
}
elseif (($MinimumCapacity - $current) -gt ([double]$quota.limit - [double]$quota.currentValue)) {
    throw "Insufficient Foundry quota for $usageName`: need $($MinimumCapacity - $current) more units; $([double]$quota.limit - [double]$quota.currentValue) available."
}
if (-not $PSCmdlet.ShouldProcess("$AccountName/$DeploymentName", "Raise capacity from $current to $MinimumCapacity")) {
    return
}
# PATCH only the SKU so model, version, upgrade option and content filter policy stay as they are.
$bodyFile = [IO.Path]::GetTempFileName()
try {
    $body = @{ sku = @{ name = [string]$deployment.sku.name; capacity = $MinimumCapacity } } | ConvertTo-Json -Compress
    [IO.File]::WriteAllText($bodyFile, $body, (New-Object Text.UTF8Encoding($false)))
    $null = Invoke-AzJson -Arguments @(
        'rest', '--method', 'patch',
        '--url', "https://management.azure.com$($deployment.id)?api-version=2024-10-01",
        '--headers', 'Content-Type=application/json', '--body', "@$bodyFile",
        '--subscription', $subscription
    )
}
finally {
    [IO.File]::Delete($bodyFile)
}
Write-Host "Import deployment $DeploymentName capacity raised from $current to $MinimumCapacity."
