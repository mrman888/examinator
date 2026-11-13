#Requires -Version 5.1
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-LocalEnvironment -RequireImports
& dotnet run --project .\server\Api --no-launch-profile -- --reconcile
if ($LASTEXITCODE -ne 0) { throw "Import reconciliation exited with code $LASTEXITCODE." }
