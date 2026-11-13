#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$tokens = $null
$errors = $null
$path = (Resolve-Path (Join-Path $PSScriptRoot '..\apps.ps1')).Path
$ast = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Deployment script parsing failed.' }
$function = $ast.Find({ param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Set-KeyVaultValue'
}, $true)
if ($null -eq $function) { throw 'Key Vault secret writer is missing.' }
$parent = $function.Parent
while ($null -ne $parent) {
    if ($parent -is [System.Management.Automation.Language.FunctionDefinitionAst]) {
        throw 'Key Vault secret writer must be available at script scope.'
    }
    $parent = $parent.Parent
}
. ([scriptblock]::Create($function.Extent.Text))
$script:exitCode = 0
$script:capturedArguments = @()
$script:capturedFile = ''
$script:capturedValue = ''
function az {
    $script:capturedArguments = @($args)
    $index = [Array]::IndexOf($script:capturedArguments, '--file')
    if ($index -lt 0) { throw 'Secret must be passed through a file.' }
    $script:capturedFile = [string]$script:capturedArguments[$index + 1]
    $script:capturedValue = [IO.File]::ReadAllText($script:capturedFile)
    $global:LASTEXITCODE = $script:exitCode
}
foreach ($code in @(0, 1)) {
    $script:exitCode = $code
    $failed = $false
    try {
        Set-KeyVaultValue -VaultName 'synthetic-vault' -SecretName 'synthetic-secret' `
            -Value 'synthetic-test-password' -Subscription 'synthetic-subscription'
    }
    catch {
        $failed = $true
        if ($_.Exception.Message.Contains('synthetic-test-password')) { throw 'Secret leaked in error output.' }
    }
    if ($failed -ne ($code -ne 0)) { throw 'Azure CLI failure was not surfaced correctly.' }
    if ($script:capturedValue -ne 'synthetic-test-password') { throw 'Secret content changed.' }
    if (($script:capturedArguments -join ' ').Contains('synthetic-test-password')) { throw 'Secret appeared in process arguments.' }
    if (Test-Path -LiteralPath $script:capturedFile) { throw 'Temporary secret file was not removed.' }
}
Write-Host 'Key Vault secret persistence and failure cleanup tests passed.'
