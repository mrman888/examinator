#Requires -Version 5.1
. (Join-Path $PSScriptRoot 'common.ps1')
Assert-LocalPortAvailable -Port 4200
& npm start -- --host 127.0.0.1 --port 4200
if ($LASTEXITCODE -ne 0) { throw "Angular exited with code $LASTEXITCODE." }
