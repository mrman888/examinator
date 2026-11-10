#Requires -Version 5.1
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [guid] $SubscriptionId,
    [string] $ResourceGroupName = 'examinator',
    [string] $RegistryName,
    [string] $ImageTag,
    [switch] $Build
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-AzJson {
    param([Parameter(Mandatory = $true)][string[]] $Arguments)

    $result = & az @Arguments --only-show-errors --output json
    if ($LASTEXITCODE -ne 0) {
        throw "Azure CLI command failed: az $($Arguments -join ' ')"
    }
    if ($result) {
        return (($result -join "`n") | ConvertFrom-Json)
    }
}

if ([string]::IsNullOrWhiteSpace($RegistryName)) {
    $RegistryName = "examacr$($SubscriptionId.ToString('N').Substring(0, 8))"
}
if ([string]::IsNullOrWhiteSpace($ImageTag)) {
    $ImageTag = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
}
if ($ImageTag -notmatch '^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$') {
    throw 'ImageTag contains characters that are not valid in an OCI image tag.'
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$apiDockerfile = Join-Path $repositoryRoot 'server\Api\Dockerfile'
$webDockerfile = Join-Path $repositoryRoot 'server\Web\Dockerfile'
foreach ($dockerfile in @($apiDockerfile, $webDockerfile)) {
    if (-not (Test-Path -LiteralPath $dockerfile -PathType Leaf)) {
        throw "Required Dockerfile is missing: $dockerfile. No image was built."
    }
}
if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw 'Install Azure CLI and sign in with az login.'
}
$registry = Invoke-AzJson -Arguments @(
    'acr', 'show', '--name', $RegistryName, '--resource-group', $ResourceGroupName,
    '--subscription', $SubscriptionId.ToString()
)
if ($registry.sku.name -ne 'Basic' -or $registry.adminUserEnabled) {
    throw 'Build target must be the configured Basic ACR with admin credentials disabled.'
}

Write-Host "Repository root build context: $repositoryRoot"
Write-Host "API image: $($registry.loginServer)/examinator-api:$ImageTag (server/Api/Dockerfile)"
Write-Host "Web image: $($registry.loginServer)/examinator-web:$ImageTag (server/Web/Dockerfile)"
if (-not $Build) {
    Write-Host 'Preflight complete; no images built. Rerun with -Build to use az acr build.'
    return
}
if (-not $PSCmdlet.ShouldProcess($registry.loginServer, 'Build and push API and web images')) {
    return
}

# Keep the repository root as the remote build context so existing Dockerfiles
# can copy solution/project files outside their own directories.
foreach ($entry in @(
    @{ Path = 'server/Api/Dockerfile'; Image = "$($registry.loginServer)/examinator-api:$ImageTag" },
    @{ Path = 'server/Web/Dockerfile'; Image = "$($registry.loginServer)/examinator-web:$ImageTag" }
)) {
    $previousEncoding = $env:PYTHONIOENCODING
    try {
        $env:PYTHONIOENCODING = 'utf-8'
        & az acr build --registry $RegistryName --file $entry.Path --image $entry.Image `
            --subscription $SubscriptionId.ToString() $repositoryRoot --only-show-errors --output none | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "ACR build failed for $($entry.Path). The previous image, if any, was not rolled back."
        }
    }
    finally {
        $env:PYTHONIOENCODING = $previousEncoding
    }
}

[pscustomobject]@{
    RegistryLoginServer = [string]$registry.loginServer
    ImageTag = $ImageTag
    ApiImage = "$($registry.loginServer)/examinator-api:$ImageTag"
    WebImage = "$($registry.loginServer)/examinator-web:$ImageTag"
}
