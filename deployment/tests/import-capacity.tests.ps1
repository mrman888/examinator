#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$path = (Resolve-Path (Join-Path $PSScriptRoot '..\ensure-import-capacity.ps1')).Path
$state = @{ Capacity = 10; QuotaUsed = 10; QuotaLimit = 300; QuotaVisible = $true; Patches = New-Object System.Collections.ArrayList }

function Assert-True {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) { throw $Message }
}

function az {
    $arguments = @($args)
    $global:LASTEXITCODE = 0
    $command = ($arguments[0..3] -join ' ')
    if ($command -eq 'cognitiveservices account deployment show') {
        return (@{
            id = '/subscriptions/synthetic/resourceGroups/synthetic/providers/Microsoft.CognitiveServices/accounts/synthetic/deployments/synthetic-import'
            sku = @{ name = 'DataZoneStandard'; capacity = $state.Capacity }
            properties = @{ model = @{ format = 'OpenAI'; name = 'gpt-5-mini'; version = '2025-08-07' } }
        } | ConvertTo-Json -Compress -Depth 5)
    }
    if ($command -like 'cognitiveservices account show*') {
        return '{"location":"swedencentral"}'
    }
    if ($command -like 'cognitiveservices usage list*') {
        if (-not $state.QuotaVisible) { return '[]' }
        return (ConvertTo-Json -Compress -Depth 5 -InputObject @(
            @{ name = @{ value = 'OpenAI.DataZoneStandard.gpt-5-mini' }; currentValue = $state.QuotaUsed; limit = $state.QuotaLimit }
        ))
    }
    if ($arguments[0] -eq 'rest' -and $arguments[2] -eq 'patch') {
        $bodyIndex = [Array]::IndexOf($arguments, '--body')
        $body = [IO.File]::ReadAllText(([string]$arguments[$bodyIndex + 1]).TrimStart('@')) | ConvertFrom-Json
        [void]$state.Patches.Add($body)
        return '{}'
    }
    throw "Unexpected Azure CLI call: $($arguments -join ' ')"
}

$parameters = @{
    SubscriptionId = [guid]'22222222-2222-2222-2222-222222222222'
    ResourceGroupName = 'synthetic'
    AccountName = 'synthetic'
    DeploymentName = 'synthetic-import'
    MinimumCapacity = 150
    Confirm = $false
}

& $path @parameters
Assert-True ($state.Patches.Count -eq 1) 'Insufficient capacity must be raised once.'
Assert-True ($state.Patches[0].sku.capacity -eq 150 -and $state.Patches[0].sku.name -eq 'DataZoneStandard') 'Only the SKU capacity may change.'
Assert-True ($null -eq $state.Patches[0].PSObject.Properties['properties']) 'Model properties must not be patched.'

$state.Patches.Clear()
$state.Capacity = 200
& $path @parameters
Assert-True ($state.Patches.Count -eq 0) 'Sufficient or larger capacity must not change.'

$state.Capacity = 10
& $path @parameters -WhatIf
Assert-True ($state.Patches.Count -eq 0) 'WhatIf must not change capacity.'

$state.QuotaUsed = 250
$failed = $false
try { & $path @parameters } catch { $failed = $_.Exception.Message -like 'Insufficient Foundry quota*' }
Assert-True $failed 'Missing quota must fail explicitly.'
Assert-True ($state.Patches.Count -eq 0) 'Missing quota must not change capacity.'

$state.QuotaVisible = $false
& $path @parameters -WarningAction SilentlyContinue
Assert-True ($state.Patches.Count -eq 1) 'Unreadable quota must still attempt the capacity raise.'
Write-Host 'Passed: import capacity raise, idempotence, WhatIf, quota and unreadable-quota checks.'
