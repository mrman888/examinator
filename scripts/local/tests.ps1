#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'common.ps1')

$environmentNames = @('DOTNET_ENVIRONMENT', 'ASPNETCORE_ENVIRONMENT', 'Development__AllowAnonymous',
    'Import__Enabled', 'Storage__BlobEndpoint', 'Storage__QueueEndpoint', 'Storage__TableEndpoint',
    'Foundry__Endpoint', 'Foundry__ImportDeployment')
$original = @{}
foreach ($name in $environmentNames) { $original[$name] = [Environment]::GetEnvironmentVariable($name) }
$script:json = '{"Import__Enabled":"false"}'
function Test-Path { return $true }
function Get-Content { return $script:json }
try {
    Initialize-LocalEnvironment
    if ($env:DOTNET_ENVIRONMENT -ne 'Development' -or $env:Development__AllowAnonymous -ne 'true') {
        throw 'Local development authentication was not configured.'
    }
    $script:json = '{"Import__Enabled":"true","Storage__BlobEndpoint":"http://insecure.example/"}'
    $failed = $false
    try { Initialize-LocalEnvironment } catch { $failed = $true }
    if (-not $failed) { throw 'Insecure import configuration was accepted.' }
    $script:json = '{"Azure__ManagedIdentityClientId":"not-an-allowed-local-setting"}'
    $failed = $false
    try { Initialize-LocalEnvironment } catch { $failed = $true }
    if (-not $failed) { throw 'Unknown local settings were accepted.' }
    $script:json = '{"Import__Enabled":"false"}'
    $failed = $false
    try { Initialize-LocalEnvironment -RequireImports } catch { $failed = $true }
    if (-not $failed) { throw 'Worker accepted disabled imports.' }
}
finally {
    Remove-Item Function:\Test-Path
    Remove-Item Function:\Get-Content
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $original[$name], 'Process') }
}

$listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
try {
    $listener.Start()
    $failed = $false
    try { Assert-LocalPortAvailable -Port $listener.LocalEndpoint.Port } catch { $failed = $true }
    if (-not $failed) { throw 'Occupied port was not rejected.' }
}
finally { $listener.Stop() }

$script:requests = 0
function Invoke-WebRequest {
    $script:requests++
    if ($script:requests -eq 1) { throw 'Synthetic startup connection refusal.' }
    return [pscustomobject]@{ StatusCode = 200 }
}
function Start-Sleep {}
try {
    Wait-LocalService -Url 'http://127.0.0.1:1/healthz' -Process ([Diagnostics.Process]::GetCurrentProcess())
    if ($script:requests -ne 2) { throw 'Readiness did not wait for a successful response.' }
}
finally {
    Remove-Item Function:\Invoke-WebRequest
    Remove-Item Function:\Start-Sleep
}

$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot 'start.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Launcher does not parse.' }
$definition = $ast.Find({ param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Start-ServiceTerminal'
}, $true)
. ([scriptblock]::Create($definition.Extent.Text))
$localScriptsRoot = $PSScriptRoot
$script:launchArguments = @()
function Start-Process {
    param($FilePath, $ArgumentList, $WorkingDirectory, [switch] $PassThru)
    $script:launchArguments = $ArgumentList
    if ($WorkingDirectory -ne $repositoryRoot -or $FilePath -ne 'powershell.exe') {
        throw 'Wrong launcher executable or working directory.'
    }
    return [pscustomobject]@{ Id = 123 }
}
try {
    $null = Start-ServiceTerminal -ScriptName 'worker.ps1' -Watch
    $index = [Array]::IndexOf($script:launchArguments, '-EncodedCommand')
    if ($index -lt 0) { throw 'Script path was not safely encoded.' }
    $decoded = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($script:launchArguments[$index + 1]))
    if (-not $decoded.Contains('worker.ps1') -or -not $decoded.EndsWith(' -Watch')) {
        throw 'Worker watcher arguments were lost.'
    }
}
finally { Remove-Item Function:\Start-Process }
Write-Host 'Local configuration, occupied-port, readiness and launcher tests passed.'
