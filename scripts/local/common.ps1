#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location -LiteralPath $repositoryRoot

function Initialize-LocalEnvironment {
    param([switch] $RequireImports)

    $configPath = Join-Path $PSScriptRoot 'settings.local.json'
    if (Test-Path -LiteralPath $configPath) {
        $settings = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
        $allowed = @(
            'Foundry__Endpoint', 'Foundry__Deployment', 'Foundry__ImportDeployment',
            'Storage__BlobEndpoint', 'Storage__QueueEndpoint', 'Storage__TableEndpoint',
            'Import__Enabled', 'Import__MaximumModelCalls'
        )
        foreach ($property in $settings.PSObject.Properties) {
            if ($property.Name -notin $allowed -or $null -eq $property.Value -or
                $property.Value -isnot [string]) {
                throw "Invalid local setting '$($property.Name)'. Use string values from settings.example.json."
            }
            [Environment]::SetEnvironmentVariable($property.Name, $property.Value, 'Process')
        }
    }
    $env:DOTNET_ENVIRONMENT = 'Development'
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:Development__AllowAnonymous = 'true'
    $enabled = $false
    if ($env:Import__Enabled -and -not [bool]::TryParse($env:Import__Enabled, [ref]$enabled)) {
        throw 'Import__Enabled must be true or false.'
    }
    if ($RequireImports -and -not $enabled) {
        throw 'Enable imports in scripts\local\settings.local.json before starting a worker.'
    }
    if ($enabled) {
        foreach ($name in @('Storage__BlobEndpoint', 'Storage__QueueEndpoint', 'Storage__TableEndpoint', 'Foundry__Endpoint')) {
            $uri = $null
            if (-not [Uri]::TryCreate([Environment]::GetEnvironmentVariable($name), [UriKind]::Absolute, [ref]$uri) -or
                $uri.Scheme -ne 'https') {
                throw "Configure a valid HTTPS $name in scripts\local\settings.local.json."
            }
        }
        if ([string]::IsNullOrWhiteSpace($env:Foundry__ImportDeployment)) {
            throw 'Configure Foundry__ImportDeployment in scripts\local\settings.local.json.'
        }
        if ($RequireImports -and -not (Get-Command pdftoppm -ErrorAction SilentlyContinue)) {
            throw 'Install Poppler and put pdftoppm on PATH before starting the import worker.'
        }
    }
}

function Assert-LocalPortAvailable {
    param([int] $Port)
    $listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, $Port)
    try { $listener.Start() }
    catch { throw "Port $Port is already in use. Stop the existing service before launching another instance." }
    finally { $listener.Stop() }
}

function Wait-LocalService {
    param([string] $Url, [System.Diagnostics.Process] $Process, [int] $TimeoutSeconds = 120)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if ($Process.HasExited) { throw "The service terminal exited before $Url was ready." }
        try {
            $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 2
            if ($response.StatusCode -eq 200) { return }
        }
        catch {
            # Connection refusal is expected while the service compiles and starts.
            Start-Sleep -Seconds 1
        }
    }
    throw "Service did not become ready at $Url. Check its terminal for errors."
}
