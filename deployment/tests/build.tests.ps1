#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$buildScript = Join-Path $PSScriptRoot '..\build.ps1'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$state = @{
    Calls = New-Object System.Collections.ArrayList
    FailBuild = 0
}

function Assert-True {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) { throw $Message }
}

function az {
    $global:LASTEXITCODE = 0
    if ($args[0] -eq 'acr' -and $args[1] -eq 'show') {
        return '{"sku":{"name":"Basic"},"adminUserEnabled":false,"loginServer":"test.azurecr.io"}'
    }
    if ($args[0] -ne 'acr' -or $args[1] -ne 'build') {
        throw "Unexpected Azure CLI call: $($args -join ' ')"
    }
    [void]$state.Calls.Add(@($args))
    if ($state.Calls.Count -eq $state.FailBuild) {
        $global:LASTEXITCODE = 1
    }
}

$parameters = @{
    SubscriptionId = [guid]'11111111-1111-1111-1111-111111111111'
    ImageTag = 'test'
    Confirm = $false
}

foreach ($failBuild in @(0, 1, 2)) {
    $state.Calls.Clear()
    $state.FailBuild = $failBuild
    $result = $null
    $failure = $null
    try {
        $result = & $buildScript @parameters -Build
    }
    catch {
        $failure = $_
    }
    $expectedCalls = if ($failBuild -eq 1) { 1 } else { 2 }
    Assert-True ($state.Calls.Count -eq $expectedCalls) 'Builds must run sequentially and stop on failure.'
    foreach ($call in $state.Calls) {
        Assert-True ($call -contains '--no-logs') 'ACR log streaming must be disabled.'
        Assert-True ($call -notcontains '--no-wait') 'ACR builds must wait for completion.'
        Assert-True ($call -contains $repositoryRoot) 'ACR must use the repository root as its context.'
    }
    Assert-True ($state.Calls[0] -contains 'server/Api/Dockerfile') 'The API image must build first.'
    if ($expectedCalls -eq 2) {
        Assert-True ($state.Calls[1] -contains 'server/Web/Dockerfile') 'The web image must build second.'
    }
    if ($failBuild -eq 0) {
        Assert-True ($null -eq $failure) "Successful builds unexpectedly failed: $failure"
        Assert-True ($result.ApiImage -eq 'test.azurecr.io/examinator-api:test') 'API image output changed.'
        Assert-True ($result.WebImage -eq 'test.azurecr.io/examinator-web:test') 'Web image output changed.'
    }
    else {
        Assert-True ($null -ne $failure) 'A failed build must throw.'
        Assert-True ($failure.Exception.Message -like 'ACR build failed for *') 'Build failures must be explicit.'
        Assert-True ($null -eq $result) 'Failed builds must not return successful image outputs.'
    }
}

$state.FailBuild = 0
foreach ($case in @(
    @{ Images = @('Web'); Dockerfile = 'server/Web/Dockerfile'; Built = 'WebImage'; Kept = 'ApiImage' },
    @{ Images = @('Api'); Dockerfile = 'server/Api/Dockerfile'; Built = 'ApiImage'; Kept = 'WebImage' }
)) {
    $state.Calls.Clear()
    $result = & $buildScript @parameters -Images $case.Images -Build
    Assert-True ($state.Calls.Count -eq 1) "Only the $($case.Images) image must build."
    Assert-True ($state.Calls[0] -contains $case.Dockerfile) "The $($case.Images) build used the wrong Dockerfile."
    Assert-True (-not [string]::IsNullOrWhiteSpace($result.($case.Built))) "The built $($case.Images) image must be returned."
    Assert-True ($null -eq $result.($case.Kept)) 'An image that was not built must not be returned.'
}

$state.Calls.Clear()
$null = & $buildScript @parameters
Assert-True ($state.Calls.Count -eq 0) 'Preflight must not build images.'
$null = & $buildScript @parameters -Build -WhatIf
Assert-True ($state.Calls.Count -eq 0) 'WhatIf must not build images.'
Write-Host 'Passed: successful builds, API/web failures, single-image builds, preflight, and WhatIf.'
