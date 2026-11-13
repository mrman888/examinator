#Requires -Version 5.1
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-LocalEnvironment
Assert-LocalPortAvailable -Port 5182
& dotnet run --project .\server\Api --no-launch-profile --urls http://127.0.0.1:5182
if ($LASTEXITCODE -ne 0) { throw "API exited with code $LASTEXITCODE." }
