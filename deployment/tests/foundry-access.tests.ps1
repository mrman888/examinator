#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$path = (Resolve-Path (Join-Path $PSScriptRoot '..\ensure-foundry-access.ps1')).Path
$global:FoundryAccessTestPrincipalId = '11111111-1111-1111-1111-111111111111'
$global:FoundryAccessTestAccountName = 'synthetic-account'
$global:FoundryAccessTestAccountLocation = 'swedencentral'
$global:FoundryAccessTestRoleExists = $true
$global:FoundryAccessTestRoleListCount = 0
$global:FoundryAccessTestRoleCreateCount = 0

function az {
    $arguments = @($args)
    $global:LASTEXITCODE = 0

    if ($arguments[0] -eq 'cognitiveservices' -and $arguments[1] -eq 'account') {
        return (@{
            id = '/subscriptions/synthetic/resourceGroups/synthetic/providers/Microsoft.CognitiveServices/accounts/synthetic-account'
            kind = 'AIServices'
            location = $global:FoundryAccessTestAccountLocation
            properties = @{ endpoint = "https://$global:FoundryAccessTestAccountName.cognitiveservices.azure.com/" }
        } | ConvertTo-Json -Compress -Depth 5)
    }
    if ($arguments[0] -eq 'role' -and $arguments[1] -eq 'assignment' -and $arguments[2] -eq 'list') {
        $global:FoundryAccessTestRoleListCount++
        if ($global:FoundryAccessTestRoleExists) {
            return (ConvertTo-Json -InputObject @(@{
                principalId = $global:FoundryAccessTestPrincipalId
                roleDefinitionName = 'Cognitive Services OpenAI User'
            }) -Compress)
        }
        return '[]'
    }
    if ($arguments[0] -eq 'role' -and $arguments[1] -eq 'assignment' -and $arguments[2] -eq 'create') {
        $global:FoundryAccessTestRoleCreateCount++
        return '{}'
    }
    throw 'Unexpected Azure CLI command in Foundry access test.'
}

$parameters = @{
    SubscriptionId = [guid]'22222222-2222-2222-2222-222222222222'
    ResourceGroupName = 'synthetic-resource-group'
    Location = 'swedencentral'
    AccountName = $global:FoundryAccessTestAccountName
    PrincipalId = [guid]$global:FoundryAccessTestPrincipalId
}

& $path @parameters
if ($global:FoundryAccessTestRoleCreateCount -ne 0) {
    throw 'Existing Foundry access was assigned a second time.'
}

$global:FoundryAccessTestRoleExists = $false
& $path @parameters
if ($global:FoundryAccessTestRoleCreateCount -ne 1) {
    throw 'Missing Foundry access was not assigned.'
}

$global:FoundryAccessTestAccountLocation = 'northeurope'
$failed = $false
try {
    & $path @parameters
}
catch {
    $failed = $true
}
if (-not $failed -or $global:FoundryAccessTestRoleCreateCount -ne 1) {
    throw 'Foundry account validation did not block the role assignment.'
}

Write-Host 'Foundry access idempotence and account validation tests passed.'
