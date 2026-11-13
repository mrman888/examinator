#Requires -Version 5.1
[CmdletBinding()]
param([switch] $Worker)
. (Join-Path $PSScriptRoot 'common.ps1')
$localScriptsRoot = $PSScriptRoot
Initialize-LocalEnvironment -RequireImports:$Worker
foreach ($command in @('dotnet', 'npm')) {
    if (-not (Get-Command $command -ErrorAction SilentlyContinue)) { throw "Install $command before starting locally." }
}
foreach ($port in @(5182, 5181, 4200)) { Assert-LocalPortAvailable -Port $port }

function Start-ServiceTerminal {
    param([string] $ScriptName, [switch] $Watch)
    $scriptPath = (Join-Path $localScriptsRoot $ScriptName).Replace("'", "''")
    $command = "& '$scriptPath'"
    if ($Watch) { $command += ' -Watch' }
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    return Start-Process -FilePath 'powershell.exe' -ArgumentList @(
        '-NoProfile', '-NoExit', '-EncodedCommand', $encoded
    ) -WorkingDirectory $repositoryRoot -PassThru
}

try {
    $api = Start-ServiceTerminal -ScriptName 'api.ps1'
    Wait-LocalService -Url 'http://127.0.0.1:5182/healthz' -Process $api
    $web = Start-ServiceTerminal -ScriptName 'web.ps1'
    Wait-LocalService -Url 'http://127.0.0.1:5181/healthz' -Process $web
    $frontend = Start-ServiceTerminal -ScriptName 'frontend.ps1'
    Wait-LocalService -Url 'http://127.0.0.1:4200/' -Process $frontend
    if ($Worker) {
        $workerProcess = Start-ServiceTerminal -ScriptName 'worker.ps1' -Watch
        Write-Host "Worker terminal PID: $($workerProcess.Id). Check its startup output; imports can incur Azure costs."
    }
    Write-Host 'Ready: http://127.0.0.1:4200'
    Write-Host "Terminal PIDs: API $($api.Id), Web $($web.Id), Angular $($frontend.Id)."
    Write-Host 'Use Ctrl+C in each service terminal to stop it, then close the terminal.'
}
catch {
    Write-Warning 'Startup failed. Already-opened service terminals remain available for diagnosis; stop them with Ctrl+C.'
    throw
}
