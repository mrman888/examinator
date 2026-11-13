#Requires -Version 5.1
[CmdletBinding()]
param([switch] $Watch)
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-LocalEnvironment -RequireImports
Write-Warning 'Imports upload documents to Azure and call Foundry. Processing can incur model costs.'
& dotnet build .\server\Api\Api.csproj
if ($LASTEXITCODE -ne 0) { throw 'Worker build failed.' }
do {
    & dotnet .\server\Api\bin\Debug\net10.0\Api.dll --import-worker
    if ($LASTEXITCODE -ne 0) {
        throw "Worker exited with code $LASTEXITCODE. Check the import error; no automatic retry will be attempted."
    }
    if ($Watch) { Start-Sleep -Seconds 5 }
} while ($Watch)
