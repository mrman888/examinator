#Requires -Version 5.1
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-LocalEnvironment
Assert-LocalPortAvailable -Port 5181
$env:Gateway__ApiBaseUrl = 'http://127.0.0.1:5182'
& dotnet run --project .\server\Web --no-launch-profile --urls http://127.0.0.1:5181
if ($LASTEXITCODE -ne 0) { throw "Web gateway exited with code $LASTEXITCODE." }
