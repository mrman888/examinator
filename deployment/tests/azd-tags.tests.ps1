#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$path = (Resolve-Path (Join-Path $PSScriptRoot '..\apps.ps1')).Path
$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Deployment script parsing failed.' }
$function = $ast.Find({ param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Set-AzdServiceTag'
}, $true)
if ($null -eq $function) { throw 'AZD service tag helper is missing.' }
. ([scriptblock]::Create($function.Extent.Text))

$script:capturedArguments = @()
function Invoke-AzJson {
    param([Parameter(Mandatory = $true)][string[]] $Arguments)
    $script:capturedArguments = @($Arguments)
}

$app = [pscustomobject]@{ id = '/subscriptions/synthetic/resourceGroups/synthetic/providers/Microsoft.App/containerApps/api' }
Set-AzdServiceTag -App $app -Service api -Subscription 'synthetic-subscription'
if ($script:capturedArguments -notcontains 'Merge' -or
    ($script:capturedArguments -join ' ') -notmatch 'azd-service-name=api' -or
    ($script:capturedArguments -join ' ') -notmatch [regex]::Escape($app.id)) {
    throw 'AZD service tag must merge onto the target Container App.'
}

$script:capturedArguments = @()
$failed = $false
try {
    Set-AzdServiceTag -App ([pscustomobject]@{ id = '' }) -Service web -Subscription 'synthetic-subscription'
}
catch {
    $failed = $true
}
if (-not $failed -or $script:capturedArguments.Count -ne 0) {
    throw 'Missing Container App resource ID did not fail before the Azure CLI call.'
}

Write-Host 'AZD service tag merge tests passed.'
