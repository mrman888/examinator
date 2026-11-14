#Requires -Version 5.1
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [guid] $SubscriptionId,
    [string] $ResourceGroupName = 'examinator',
    [string] $RegistryName,
    [string] $ImageTag,
    [ValidateSet('Api', 'Web')]
    [string[]] $Images = @('Api', 'Web'),
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

$apiImage = "$($registry.loginServer)/examinator-api:$ImageTag"
$webImage = "$($registry.loginServer)/examinator-web:$ImageTag"
$entries = @(
    @{ Name = 'Api'; Path = 'server/Api/Dockerfile'; Image = $apiImage },
    @{ Name = 'Web'; Path = 'server/Web/Dockerfile'; Image = $webImage }
) | Where-Object { $Images -contains $_.Name }

Write-Host "Repository root build context: $repositoryRoot"
foreach ($entry in $entries) {
    Write-Host "$($entry.Name) image: $($entry.Image) ($($entry.Path))"
}
if (-not $Build) {
    Write-Host 'Preflight complete; no images built. Rerun with -Build to use az acr build.'
    return
}
if (-not $PSCmdlet.ShouldProcess($registry.loginServer, "Build and push images: $($Images -join ', ')")) {
    return
}

# Keep the repository root as the remote build context so existing Dockerfiles
# can copy solution/project files outside their own directories.
foreach ($entry in $entries) {
    Write-Host "Building $($entry.Path); waiting for ACR completion without streaming logs."
    & az acr build --registry $RegistryName --file $entry.Path --image $entry.Image `
        --subscription $SubscriptionId.ToString() $repositoryRoot --no-logs --only-show-errors --output none | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "ACR build failed for $($entry.Path). See the ACR run details and logs. The previous image, if any, was not rolled back."
    }
}

[pscustomobject]@{
    RegistryLoginServer = [string]$registry.loginServer
    ImageTag = $ImageTag
    ApiImage = if ($Images -contains 'Api') { $apiImage } else { $null }
    WebImage = if ($Images -contains 'Web') { $webImage } else { $null }
}
