#Requires -Version 5.1
<#
.SYNOPSIS
Starts the whole local environment: Api, Web gateway, Angular dev server, the
import worker (when imports are switched on) and the Aspire dashboard.

.DESCRIPTION
Runs the Aspire AppHost in server\AppHost. Settings come from the AppHost's user
secrets; see "Local run with the Aspire AppHost" in server\README.md.
Stop everything with Ctrl+C.

.PARAMETER NoImports
Start without imports and without the import worker, so nothing calls Foundry
for queued imports.

.EXAMPLE
.\start.ps1

.EXAMPLE
.\start.ps1 -NoImports
#>
[CmdletBinding()]
param([switch] $NoImports)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# A terminal keeps the PATH it was opened with. Add the current machine and user
# PATH so tools installed since then (for example Poppler's pdftoppm) are found.
$env:Path = @(
    $env:Path
    [Environment]::GetEnvironmentVariable('Path', 'Machine')
    [Environment]::GetEnvironmentVariable('Path', 'User')
) -join ';'

foreach ($command in @('dotnet', 'npm')) {
    if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
        throw "Install $command before starting locally."
    }
}

if (-not (Test-Path (Join-Path $root 'node_modules'))) {
    Write-Host 'node_modules is missing; running npm ci.'
    Push-Location $root
    try {
        & npm ci
        if ($LASTEXITCODE -ne 0) { throw 'npm ci failed.' }
    }
    finally { Pop-Location }
}

$dotnetArguments = @('run', '--project', (Join-Path $root 'server\AppHost'))
if ($NoImports) { $dotnetArguments += @('--', '--Examinator:Imports=false') }

Write-Host 'Starting the AppHost. App: http://127.0.0.1:4200. The dashboard link is printed below.'
& dotnet @dotnetArguments
exit $LASTEXITCODE
