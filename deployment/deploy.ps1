#Requires -Version 5.1
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [guid] $SubscriptionId,
    [Parameter(Mandatory = $true)]
    [guid] $TenantId,
    [guid[]] $AllowedUserIds = @(),
    [string] $ResourceGroupName = 'examinator',
    [ValidateSet('swedencentral')]
    [string] $Location = 'swedencentral',
    [string] $RegistryName,
    [string] $EnvironmentName = 'examinator-aca',
    [string] $ImageTag,
    [string] $FoundryAccountName = 'examinator-46954202',
    [string] $FoundryDeploymentName = 'exam-classifier',
    [switch] $EnableImports,
    [string] $ImportDeploymentName,
    [switch] $SkipBuild,
    [switch] $ProvisionFoundry,
    [switch] $Deploy
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($EnableImports -and [string]::IsNullOrWhiteSpace($ImportDeploymentName)) {
    throw 'Supply -ImportDeploymentName for an evaluated vision/structured-output model. No model is deployed automatically.'
}

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

function Resolve-AllowedUserIds {
    param([guid[]] $AdditionalUserIds = @())

    $resolved = @($AdditionalUserIds | ForEach-Object { $_.ToString() } | Select-Object -Unique)
    $currentUser = & az ad signed-in-user show --query id --only-show-errors --output tsv 2>$null
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace([string]$currentUser)) {
        $currentUserId = [guid]([string]$currentUser).Trim()
        $resolved += $currentUserId.ToString()
    }
    if ($resolved.Count -eq 0) {
        throw 'No signed-in user was resolved. Supply at least one approved user object ID when using a non-user identity.'
    }
    return @($resolved | Select-Object -Unique | ForEach-Object { [guid]$_ })
}

if ([string]::IsNullOrWhiteSpace($RegistryName)) {
    $RegistryName = "examacr$($SubscriptionId.ToString('N').Substring(0, 8))"
}
if ($SkipBuild -and [string]::IsNullOrWhiteSpace($ImageTag)) {
    throw 'Supply -ImageTag when using -SkipBuild.'
}
if ([string]::IsNullOrWhiteSpace($ImageTag)) {
    $ImageTag = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
}
if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw 'Install Azure CLI and sign in with az login.'
}
$AllowedUserIds = Resolve-AllowedUserIds -AdditionalUserIds $AllowedUserIds

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
foreach ($dockerfile in @(
    (Join-Path $repositoryRoot 'server\Api\Dockerfile'),
    (Join-Path $repositoryRoot 'server\Web\Dockerfile')
)) {
    if (-not (Test-Path -LiteralPath $dockerfile -PathType Leaf)) {
        throw "Required Dockerfile is missing: $dockerfile. No resources or images were changed."
    }
}
$subscription = $SubscriptionId.ToString()
$account = Invoke-AzJson -Arguments @('account', 'show', '--subscription', $subscription)
if ($account.state -ne 'Enabled' -or $account.tenantId -ne $TenantId.ToString()) {
    throw 'The target subscription must be enabled and belong to the supplied TenantId.'
}
$baseTemplate = Join-Path $PSScriptRoot 'main.bicep'
& az bicep build --file $baseTemplate --stdout --only-show-errors | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'Base Bicep compilation failed. Install/update Bicep with az bicep install.'
}
if ($ProvisionFoundry) {
    & az bicep build --file (Join-Path $PSScriptRoot 'foundry\main.bicep') --stdout --only-show-errors | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Foundry Bicep compilation failed.'
    }
}

Write-Host "Target: $subscription / $ResourceGroupName / $Location / tenant $TenantId"
Write-Host "Registry: $RegistryName; ACA environment: $EnvironmentName; image tag: $ImageTag"
Write-Host "Approved user object IDs: $($AllowedUserIds.Count)"
if ($ProvisionFoundry) {
    Write-Host "Foundry will be provisioned explicitly as $FoundryAccountName / $FoundryDeploymentName."
}
else {
    Write-Host "Uses existing Foundry account $FoundryAccountName and deployment $FoundryDeploymentName; no account import or model deployment."
}
if (-not $Deploy) {
    Write-Host 'Preflight complete; no Azure resources, registrations, role grants, images, or containers changed.'
    Write-Host 'Rerun with -Deploy only after reviewing deployment/README.md, permissions, and cost.'
    return
}
if (-not $PSCmdlet.ShouldProcess("$subscription/$ResourceGroupName", 'Provision identities and infrastructure, build images and deploy Examinator')) {
    return
}

$infraScript = Join-Path $PSScriptRoot 'infra.ps1'
$identityScript = Join-Path $PSScriptRoot 'identity.ps1'
$buildScript = Join-Path $PSScriptRoot 'build.ps1'
$appsScript = Join-Path $PSScriptRoot 'apps.ps1'

# Detect ambiguous or incompatible registrations before provisioning any Azure resources.
& $identityScript -TenantId $TenantId -SubscriptionId $SubscriptionId

& $infraScript -SubscriptionId $SubscriptionId -ResourceGroupName $ResourceGroupName `
    -Location $Location -RegistryName $RegistryName -EnvironmentName $EnvironmentName -EnableImports:$EnableImports -Deploy
$base = Invoke-AzJson -Arguments @(
    'deployment', 'group', 'show', '--name', 'examinator-base',
    '--resource-group', $ResourceGroupName, '--subscription', $subscription,
    '--query', 'properties.outputs'
)
if (-not $base.apiIdentityPrincipalId.value -or -not $base.webIdentityPrincipalId.value) {
    throw 'Base infrastructure deployed but managed identity outputs could not be resolved.'
}

$identity = & $identityScript -TenantId $TenantId -SubscriptionId $SubscriptionId `
    -GatewayPrincipalId ([guid]$base.webIdentityPrincipalId.value) -Deploy
if (-not $identity -or -not $identity.ApiAppId -or -not $identity.WebAppId) {
    throw 'Entra registrations or gateway API role assignment did not complete.'
}

if ($ProvisionFoundry) {
    $foundryScript = Join-Path $PSScriptRoot 'foundry\deploy.ps1'
    & $foundryScript -SubscriptionId $SubscriptionId -ResourceGroupName $ResourceGroupName `
        -Location $Location -AccountName $FoundryAccountName -DeploymentName $FoundryDeploymentName `
        -PrincipalId ([guid]$base.apiIdentityPrincipalId.value) -PrincipalType ServicePrincipal -Deploy
}
else {
    $ensureFoundryAccessScript = Join-Path $PSScriptRoot 'ensure-foundry-access.ps1'
    & $ensureFoundryAccessScript -SubscriptionId $SubscriptionId `
        -ResourceGroupName $ResourceGroupName -Location $Location `
        -AccountName $FoundryAccountName `
        -PrincipalId ([guid]$base.apiIdentityPrincipalId.value)
}

if (-not $SkipBuild) {
    $images = & $buildScript -SubscriptionId $SubscriptionId -ResourceGroupName $ResourceGroupName `
        -RegistryName $RegistryName -ImageTag $ImageTag -Build
    if (-not $images.ApiImage -or -not $images.WebImage) {
        throw 'Image build did not return both image references.'
    }
    $apiImage = [string]$images.ApiImage
    $webImage = [string]$images.WebImage
}
else {
    $registry = Invoke-AzJson -Arguments @(
        'acr', 'show', '--name', $RegistryName, '--resource-group', $ResourceGroupName,
        '--subscription', $subscription
    )
    $apiImage = "$($registry.loginServer)/examinator-api:$ImageTag"
    $webImage = "$($registry.loginServer)/examinator-web:$ImageTag"
}

& $appsScript -SubscriptionId $SubscriptionId -TenantId $TenantId `
    -ApiAppId ([string]$identity.ApiAppId) -WebAppId ([string]$identity.WebAppId) `
    -ApiImage $apiImage -WebImage $webImage -AllowedUserIds $AllowedUserIds `
    -ResourceGroupName $ResourceGroupName -EnvironmentName $EnvironmentName `
    -RegistryName $RegistryName -FoundryAccountName $FoundryAccountName `
    -FoundryDeploymentName $FoundryDeploymentName -EnableImports:$EnableImports `
    -ImportDeploymentName $ImportDeploymentName -Deploy

if ($EnableImports) {
    $null = Invoke-AzJson -Arguments @(
        'deployment', 'group', 'create', '--name', 'examinator-imports',
        '--resource-group', $ResourceGroupName, '--subscription', $subscription,
        '--template-file', (Join-Path $PSScriptRoot 'imports.bicep'),
        '--parameters', "location=$Location", "environmentId=$($base.environmentId.value)",
        "apiIdentityId=$($base.apiIdentityId.value)", "apiIdentityClientId=$($base.apiIdentityClientId.value)",
        "registryServer=$($base.registryLoginServer.value)", "apiImage=$apiImage",
        "storageName=$($base.storageName.value)", "blobEndpoint=$($base.blobEndpoint.value)",
        "queueEndpoint=$($base.queueEndpoint.value)", "tableEndpoint=$($base.tableEndpoint.value)",
        "foundryEndpoint=https://$FoundryAccountName.cognitiveservices.azure.com/",
        "importDeployment=$ImportDeploymentName"
    )
}

Write-Host 'Deployment complete. Allow Entra role grants and ACR pull permissions to propagate before first-use checks.'
