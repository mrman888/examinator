#Requires -Version 5.1
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [guid] $SubscriptionId,
    [Parameter(Mandatory = $true)]
    [guid] $TenantId,
    [Parameter(Mandatory = $true)]
    [string] $ApiAppId,
    [Parameter(Mandatory = $true)]
    [string] $WebAppId,
    [Parameter(Mandatory = $true)]
    [string] $ApiImage,
    [Parameter(Mandatory = $true)]
    [string] $WebImage,
    [guid[]] $AllowedUserIds = @(),
    [string] $ResourceGroupName = 'examinator',
    [string] $EnvironmentName = 'examinator-aca',
    [string] $RegistryName,
    [string] $FoundryAccountName = 'examinator-46954202',
    [string] $FoundryDeploymentName = 'exam-classifier',
    [switch] $EnableImports,
    [string] $ImportDeploymentName,
    [switch] $Deploy
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-AzJson {
    param([Parameter(Mandatory = $true)][string[]] $Arguments)

    $bodyFile = $null
    try {
        $bodyIndex = [Array]::IndexOf($Arguments, '--body')
        if ($bodyIndex -ge 0 -and $Arguments[$bodyIndex + 1].StartsWith('{')) {
            $bodyFile = [IO.Path]::GetTempFileName()
            [IO.File]::WriteAllText($bodyFile, $Arguments[$bodyIndex + 1], (New-Object Text.UTF8Encoding($false)))
            $Arguments[$bodyIndex + 1] = "@$bodyFile"
        }
        $result = & az @Arguments --only-show-errors --output json
        if ($LASTEXITCODE -ne 0) {
            throw "Azure CLI command failed: az $($Arguments -join ' ')"
        }
        if ($result) {
            return (($result -join "`n") | ConvertFrom-Json)
        }
    }
    finally {
        if ($null -ne $bodyFile) { [IO.File]::Delete($bodyFile) }
    }
}

function Set-KeyVaultValue {
    param(
        [Parameter(Mandatory = $true)][string] $VaultName,
        [Parameter(Mandatory = $true)][string] $SecretName,
        [Parameter(Mandatory = $true)][string] $Value,
        [Parameter(Mandatory = $true)][string] $Subscription
    )
    $secretFile = [IO.Path]::GetTempFileName()
    $previousErrorPreference = $ErrorActionPreference
    try {
        [IO.File]::WriteAllText($secretFile, $Value, (New-Object Text.UTF8Encoding($false)))
        $Value = $null
        $ErrorActionPreference = 'Continue'
        $null = & az keyvault secret set --vault-name $VaultName --name $SecretName --file $secretFile `
            --subscription $Subscription --only-show-errors --output none 2>$null
        if ($LASTEXITCODE -ne 0) {
            throw 'Unable to persist the EasyAuth credential in Key Vault. Check permissions and Entra/Key Vault state before retrying.'
        }
    }
    finally {
        $ErrorActionPreference = $previousErrorPreference
        $Value = $null
        [IO.File]::Delete($secretFile)
    }
}

function Set-HealthProbes {
    param(
        [Parameter(Mandatory = $true)] $App,
        [Parameter(Mandatory = $true)][string] $ResourceGroup,
        [Parameter(Mandatory = $true)][guid] $Subscription
    )

    $container = @($App.properties.template.containers)[0]
    if (-not $container) {
        throw "Container app '$($App.name)' has no container definition."
    }

    $probes = @(
        @{
            type = 'Startup'
            httpGet = @{ path = '/healthz'; port = 8080; scheme = 'HTTP' }
            initialDelaySeconds = 5
            periodSeconds = 10
            timeoutSeconds = 3
            failureThreshold = 12
        },
        @{
            type = 'Readiness'
            httpGet = @{ path = '/healthz'; port = 8080; scheme = 'HTTP' }
            initialDelaySeconds = 5
            periodSeconds = 10
            timeoutSeconds = 3
            failureThreshold = 3
        },
        @{
            type = 'Liveness'
            httpGet = @{ path = '/healthz'; port = 8080; scheme = 'HTTP' }
            initialDelaySeconds = 30
            periodSeconds = 20
            timeoutSeconds = 3
            failureThreshold = 3
        }
    )
    $container | Add-Member -MemberType NoteProperty -Name probes -Value $probes -Force
    $body = @{ properties = @{ template = $App.properties.template } } | ConvertTo-Json -Depth 40 -Compress
    $url = "https://management.azure.com$($App.id)?api-version=2025-07-01"
    $null = Invoke-AzJson -Arguments @(
        'rest', '--method', 'patch', '--url', $url,
        '--headers', 'Content-Type=application/json', '--body', $body,
        '--subscription', $Subscription.ToString()
    )
}

function Test-UserAssignedIdentity {
    param(
        [Parameter(Mandatory = $true)] $App,
        [Parameter(Mandatory = $true)][string] $IdentityResourceId
    )

    if ($null -eq $App.identity -or $null -eq $App.identity.userAssignedIdentities) {
        return $false
    }
    return $null -ne $App.identity.userAssignedIdentities.PSObject.Properties[$IdentityResourceId]
}

function Test-EasyAuthEnabled {
    param(
        [Parameter(Mandatory = $true)][string] $AppName,
        [Parameter(Mandatory = $true)][string] $ResourceGroup,
        [Parameter(Mandatory = $true)][string] $Subscription
    )

    $auth = Invoke-AzJson -Arguments @(
        'containerapp', 'auth', 'show', '--name', $AppName,
        '--resource-group', $ResourceGroup, '--subscription', $Subscription
    )
    return (
        $auth.platform.enabled -eq $true -and
        $auth.globalValidation.unauthenticatedClientAction -eq 'RedirectToLoginPage' -and
        $auth.globalValidation.excludedPaths -contains '/healthz' -and
        $auth.identityProviders.azureActiveDirectory.enabled -eq $true
    )
}

function Set-AzdServiceTag {
    param(
        [Parameter(Mandatory = $true)] $App,
        [Parameter(Mandatory = $true)][ValidateSet('api', 'web')][string] $Service,
        [Parameter(Mandatory = $true)][string] $Subscription
    )

    if ([string]::IsNullOrWhiteSpace([string]$App.id)) {
        throw "Container App for AZD service '$Service' has no resource ID."
    }
    $null = Invoke-AzJson -Arguments @(
        'tag', 'update', '--resource-id', [string]$App.id,
        '--operation', 'Merge', '--tags', "azd-service-name=$Service",
        '--subscription', $Subscription
    )
}

function Resolve-AllowedUserIds {
    param([guid[]] $AdditionalUserIds = @())

    $resolved = @($AdditionalUserIds | ForEach-Object { $_.ToString() } | Select-Object -Unique)
    $currentUser = & az ad signed-in-user show --query id --only-show-errors --output tsv 2>$null
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace([string]$currentUser)) {
        $resolved += ([guid]([string]$currentUser).Trim()).ToString()
    }
    if ($resolved.Count -eq 0) {
        throw 'No signed-in user was resolved. Supply an approved user object ID when using a non-user identity.'
    }
    return @($resolved | Select-Object -Unique | ForEach-Object { [guid]$_ })
}

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw 'Install Azure CLI and sign in with az login.'
}
$AllowedUserIds = Resolve-AllowedUserIds -AdditionalUserIds $AllowedUserIds
if ([string]::IsNullOrWhiteSpace($RegistryName)) {
    $RegistryName = "examacr$($SubscriptionId.ToString('N').Substring(0, 8))"
}
$subscription = $SubscriptionId.ToString()
$activeTenant = Invoke-AzJson -Arguments @('account', 'show', '--query', 'tenantId')
if ([guid]$activeTenant -ne $TenantId) {
    throw "Azure CLI is signed into tenant $activeTenant, not requested tenant $TenantId."
}
$identityOutputs = Invoke-AzJson -Arguments @(
    'deployment', 'group', 'show', '--name', 'examinator-base',
    '--resource-group', $ResourceGroupName, '--subscription', $subscription,
    '--query', 'properties.outputs'
)
if (-not $identityOutputs.environmentId.value -or -not $identityOutputs.apiIdentityId.value -or
    -not $identityOutputs.environmentDefaultDomain.value) {
    throw 'Base infrastructure outputs are missing; run infra.ps1 first.'
}
$registry = Invoke-AzJson -Arguments @(
    'acr', 'show', '--name', $RegistryName, '--resource-group', $ResourceGroupName,
    '--subscription', $subscription
)
$environmentId = [string]$identityOutputs.environmentId.value
$apiIdentityId = [string]$identityOutputs.apiIdentityId.value
$webIdentityId = [string]$identityOutputs.webIdentityId.value
$apiIdentityClientId = [string]$identityOutputs.apiIdentityClientId.value
$webIdentityClientId = [string]$identityOutputs.webIdentityClientId.value
$apiName = 'examinator-api'
$webName = 'examinator-web'
$apiUri = "https://$FoundryAccountName.cognitiveservices.azure.com/"
$environmentDefaultDomain = [string]$identityOutputs.environmentDefaultDomain.value
$expectedWebFqdn = "$webName.$environmentDefaultDomain"

Write-Host "API: internal ingress, image $ApiImage, scale 0-1."
Write-Host "Web: external HTTPS ingress with Entra EasyAuth, image $WebImage, scale 0-1."
Write-Host 'Both containers use 8080 and unauthenticated /healthz probes.'
Write-Host "Approved user object IDs: $($AllowedUserIds.Count)"
if (-not $Deploy) {
    Write-Host 'Preflight complete; no container apps or EasyAuth configuration changed. Rerun with -Deploy.'
    return
}
if (-not $PSCmdlet.ShouldProcess("$subscription/$ResourceGroupName", 'Deploy internal API and authenticated web gateway')) {
    return
}

$telemetryEnvironment = @()
$appInsightsOutput = $identityOutputs.PSObject.Properties['appInsightsConnectionString']
$appInsightsConnectionString = if ($appInsightsOutput) { [string]$appInsightsOutput.Value.value } else { '' }
if (-not [string]::IsNullOrWhiteSpace($appInsightsConnectionString)) {
    $telemetryEnvironment += "APPLICATIONINSIGHTS_CONNECTION_STRING=$appInsightsConnectionString"
}

$apiEnvironment = $telemetryEnvironment + @(
    "Azure__TenantId=$TenantId",
    "Azure__ApiClientId=$ApiAppId",
    "Azure__GatewayPrincipalId=$($identityOutputs.webIdentityPrincipalId.value)",
    "Foundry__Endpoint=$apiUri",
    "Foundry__Deployment=$FoundryDeploymentName",
    "Azure__ManagedIdentityClientId=$apiIdentityClientId",
    'ASPNETCORE_ENVIRONMENT=Production'
)
if ($EnableImports) {
    if ([string]::IsNullOrWhiteSpace($ImportDeploymentName) -or -not $identityOutputs.blobEndpoint.value) {
        throw 'Provision import storage and specify an evaluated ImportDeploymentName first.'
    }
    $apiEnvironment += @(
        'Import__Enabled=true', "Foundry__ImportDeployment=$ImportDeploymentName",
        "Storage__BlobEndpoint=$($identityOutputs.blobEndpoint.value)",
        "Storage__QueueEndpoint=$($identityOutputs.queueEndpoint.value)",
        "Storage__TableEndpoint=$($identityOutputs.tableEndpoint.value)"
    )
}

$existingApps = @(Invoke-AzJson -Arguments @(
    'containerapp', 'list', '--resource-group', $ResourceGroupName,
    '--subscription', $subscription
))
$existingApi = @($existingApps | Where-Object { $_.name -eq $apiName }) | Select-Object -First 1
$existingWeb = @($existingApps | Where-Object { $_.name -eq $webName }) | Select-Object -First 1
foreach ($existing in @($existingApi, $existingWeb) | Where-Object { $null -ne $_ }) {
    if ($existing.properties.managedEnvironmentId -ne $environmentId) {
        throw "Existing app '$($existing.name)' belongs to another Container Apps environment."
    }
}
if ($existingApi -and -not (Test-UserAssignedIdentity -App $existingApi -IdentityResourceId $apiIdentityId)) {
    throw 'Existing API app is missing its configured API managed identity; refusing an unsafe update.'
}
if ($existingWeb -and -not (Test-UserAssignedIdentity -App $existingWeb -IdentityResourceId $webIdentityId)) {
    throw 'Existing web app is missing its configured web managed identity; refusing an unsafe update.'
}
if ($existingApi -and $existingApi.properties.configuration.ingress.external) {
    throw 'The existing API Container App has public ingress; refusing to update it.'
}
if ($existingWeb -and $existingWeb.properties.configuration.ingress.external -and
    -not (Test-EasyAuthEnabled -AppName $webName -ResourceGroup $ResourceGroupName -Subscription $subscription)) {
    throw 'Existing web app has public ingress without the required EasyAuth protection; refusing to update it.'
}

if (-not $existingApi) {
    $null = Invoke-AzJson -Arguments (@(
        'containerapp', 'create', '--name', $apiName, '--resource-group', $ResourceGroupName,
        '--subscription', $subscription, '--environment', $environmentId,
        '--image', $ApiImage, '--container-name', 'api',
        '--cpu', '0.5', '--memory', '1.0Gi',
        '--ingress', 'internal', '--target-port', '8080', '--transport', 'http',
        '--min-replicas', '0', '--max-replicas', '1',
        '--user-assigned', $apiIdentityId,
        '--registry-server', $registry.loginServer, '--registry-identity', $apiIdentityId,
        '--env-vars'
    ) + $apiEnvironment)
}
else {
    $null = Invoke-AzJson -Arguments (@(
        'containerapp', 'update', '--name', $apiName, '--resource-group', $ResourceGroupName,
        '--subscription', $subscription, '--image', $ApiImage,
        '--cpu', '0.5', '--memory', '1.0Gi',
        '--min-replicas', '0', '--max-replicas', '1',
        '--replace-env-vars'
    ) + $apiEnvironment)
}
$apiApp = Invoke-AzJson -Arguments @(
    'containerapp', 'show', '--name', $apiName, '--resource-group', $ResourceGroupName,
    '--subscription', $subscription
)
Set-HealthProbes -App $apiApp -ResourceGroup $ResourceGroupName -Subscription $SubscriptionId
$apiApp = Invoke-AzJson -Arguments @(
    'containerapp', 'show', '--name', $apiName, '--resource-group', $ResourceGroupName,
    '--subscription', $subscription
)
$apiFqdn = [string]$apiApp.properties.configuration.ingress.fqdn
if ([string]::IsNullOrWhiteSpace($apiFqdn)) {
    throw 'Internal API ingress did not return an FQDN.'
}

$webEnvironment = $telemetryEnvironment + @(
    "Azure__TenantId=$TenantId",
    "Azure__ApiClientId=$ApiAppId",
    "Azure__ManagedIdentityClientId=$webIdentityClientId",
    "Gateway__ApiBaseUrl=https://$apiFqdn",
    "Gateway__PublicOrigin=https://$expectedWebFqdn",
    'ASPNETCORE_ENVIRONMENT=Production'
)
for ($index = 0; $index -lt $AllowedUserIds.Count; $index++) {
    $webEnvironment += "Gateway__AllowedUserIds__$index=$($AllowedUserIds[$index])"
}

if (-not $existingWeb) {
    $null = Invoke-AzJson -Arguments (@(
        'containerapp', 'create', '--name', $webName, '--resource-group', $ResourceGroupName,
        '--subscription', $subscription, '--environment', $environmentId,
        '--image', $WebImage, '--container-name', 'web',
        '--cpu', '0.5', '--memory', '1.0Gi',
        '--ingress', 'internal', '--target-port', '8080', '--transport', 'http',
        '--min-replicas', '0', '--max-replicas', '1',
        '--user-assigned', $webIdentityId,
        '--registry-server', $registry.loginServer, '--registry-identity', $webIdentityId,
        '--env-vars'
    ) + $webEnvironment)
}
else {
    $null = Invoke-AzJson -Arguments (@(
        'containerapp', 'update', '--name', $webName, '--resource-group', $ResourceGroupName,
        '--subscription', $subscription, '--image', $WebImage,
        '--cpu', '0.5', '--memory', '1.0Gi',
        '--min-replicas', '0', '--max-replicas', '1',
        '--replace-env-vars'
    ) + $webEnvironment)
}
$webApp = Invoke-AzJson -Arguments @(
    'containerapp', 'show', '--name', $webName, '--resource-group', $ResourceGroupName,
    '--subscription', $subscription
)
Set-HealthProbes -App $webApp -ResourceGroup $ResourceGroupName -Subscription $SubscriptionId
$webApp = Invoke-AzJson -Arguments @(
    'containerapp', 'show', '--name', $webName, '--resource-group', $ResourceGroupName,
    '--subscription', $subscription
)

$callback = "https://$expectedWebFqdn/.auth/login/aad/callback"
$null = Invoke-AzJson -Arguments @(
    'ad', 'app', 'update', '--id', $WebAppId, '--web-redirect-uris', $callback
)
$null = Invoke-AzJson -Arguments @(
    'ad', 'app', 'update', '--id', $WebAppId, '--enable-id-token-issuance', 'true'
)

$secretName = 'easyauth-client-secret'
$secrets = @(Invoke-AzJson -Arguments @(
    'containerapp', 'secret', 'list', '--name', $webName,
    '--resource-group', $ResourceGroupName, '--subscription', $subscription
))
$storedSecret = @($secrets | Where-Object { $_.name -eq $secretName })
$vaultName = [string]$identityOutputs.keyVaultName.value
$vaultUri = ([string]$identityOutputs.keyVaultUri.value).TrimEnd('/')
if ([string]::IsNullOrWhiteSpace($vaultName) -or [string]::IsNullOrWhiteSpace($vaultUri)) {
    throw 'Provision Key Vault before configuring EasyAuth.'
}
$vaultSecrets = @(Invoke-AzJson -Arguments @('keyvault', 'secret', 'list', '--vault-name', $vaultName))
$vaultSecret = @($vaultSecrets | Where-Object { $_.name -eq $secretName })
$credentialValue = $null
if ($vaultSecret.Count -eq 0 -and $storedSecret.Count -gt 0) {
    # Migrate the existing value without rotating Entra or interrupting EasyAuth.
    $values = @(Invoke-AzJson -Arguments @(
        'containerapp', 'secret', 'list', '--name', $webName, '--show-values',
        '--resource-group', $ResourceGroupName, '--subscription', $subscription
    ))
    $value = @($values | Where-Object { $_.name -eq $secretName }) | Select-Object -First 1
    if ($null -eq $value -or $null -eq $value.PSObject.Properties['value'] -or
        [string]::IsNullOrWhiteSpace([string]$value.value)) {
        throw 'The existing EasyAuth value cannot be migrated. Recover the secret in Key Vault explicitly; no automatic rotation.'
    }
    $credentialValue = [string]$value.value
    $values = $null
    $value = $null
}
elseif ($vaultSecret.Count -eq 0) {
    $credentials = @(Invoke-AzJson -Arguments @('ad', 'app', 'credential', 'list', '--id', $WebAppId))
    $orphaned = @($credentials | Where-Object { $_.displayName -eq 'Examinator ACA EasyAuth' })
    if ($orphaned.Count -gt 0) {
        throw 'An EasyAuth credential already exists but its ACA secret is missing. Refusing to rotate or recreate it automatically; follow deployment README recovery steps.'
    }
    $credential = Invoke-AzJson -Arguments @(
        'ad', 'app', 'credential', 'reset', '--id', $WebAppId, '--append',
        '--display-name', 'Examinator ACA EasyAuth', '--years', '2'
    )
    if ([string]::IsNullOrWhiteSpace([string]$credential.password)) {
        throw 'Entra did not return the new EasyAuth credential; refusing to continue.'
    }
    $credentialValue = [string]$credential.password
    $credential = $null
}
if ($null -ne $credentialValue) {
    try {
        Set-KeyVaultValue -VaultName $vaultName -SecretName $secretName -Value $credentialValue -Subscription $subscription
    }
    finally { $credentialValue = $null }
    $null = Invoke-AzJson -Arguments @(
        'containerapp', 'secret', 'set', '--name', $webName, '--resource-group', $ResourceGroupName,
        '--subscription', $subscription, '--secrets',
        "$secretName=keyvaultref:$vaultUri/secrets/$secretName,identityref:$webIdentityId"
    )
}

$authUrl = "https://management.azure.com$($webApp.id)/authConfigs/current?api-version=2024-03-01"
$authBody = @{
    properties = @{
        platform = @{ enabled = $true }
        globalValidation = @{
            unauthenticatedClientAction = 'RedirectToLoginPage'
            redirectToProvider = 'azureactivedirectory'
            excludedPaths = @('/healthz')
        }
        identityProviders = @{
            azureActiveDirectory = @{
                enabled = $true
                registration = @{
                    clientId = $WebAppId
                    clientSecretSettingName = $secretName
                    openIdIssuer = "https://login.microsoftonline.com/$TenantId/v2.0"
                }
            }
        }
        login = @{
            tokenStore = @{ enabled = $false }
        }
        httpSettings = @{
            requireHttps = $true
            forwardProxy = @{ convention = 'Standard' }
        }
    }
} | ConvertTo-Json -Depth 20 -Compress
$null = Invoke-AzJson -Arguments @(
    'rest', '--method', 'put', '--url', $authUrl,
    '--headers', 'Content-Type=application/json', '--body', $authBody,
    '--subscription', $subscription
)

# Keep the gateway private until EasyAuth is active and the web origin, redirect,
# and secret are all configured. This avoids an externally reachable
# X-MS-CLIENT-PRINCIPAL spoofing window on initial deployment.
if (-not $webApp.properties.configuration.ingress.external) {
    $null = Invoke-AzJson -Arguments @(
        'containerapp', 'ingress', 'enable', '--name', $webName,
        '--resource-group', $ResourceGroupName, '--subscription', $subscription,
        '--type', 'external', '--allow-insecure', 'false',
        '--target-port', '8080', '--transport', 'http'
    )
}
$webApp = Invoke-AzJson -Arguments @(
    'containerapp', 'show', '--name', $webName, '--resource-group', $ResourceGroupName,
    '--subscription', $subscription
)
if (-not $webApp.properties.configuration.ingress.external) {
    throw 'Web ingress did not become external after EasyAuth was configured.'
}
$webFqdn = [string]$webApp.properties.configuration.ingress.fqdn
if ([string]::IsNullOrWhiteSpace($webFqdn)) {
    throw 'External web ingress did not return an FQDN.'
}
if ($webFqdn -ne $expectedWebFqdn) {
    # EasyAuth remains enabled throughout this correction; the gateway also
    # rejects every request whose Origin does not match its configured value.
    $callback = "https://$webFqdn/.auth/login/aad/callback"
    $null = Invoke-AzJson -Arguments @(
        'ad', 'app', 'update', '--id', $WebAppId, '--web-redirect-uris', $callback
    )
    $webEnvironment = $telemetryEnvironment + @(
        "Azure__TenantId=$TenantId",
        "Azure__ApiClientId=$ApiAppId",
        "Azure__ManagedIdentityClientId=$webIdentityClientId",
        "Gateway__ApiBaseUrl=https://$apiFqdn",
        "Gateway__PublicOrigin=https://$webFqdn",
        'ASPNETCORE_ENVIRONMENT=Production'
    )
    for ($index = 0; $index -lt $AllowedUserIds.Count; $index++) {
        $webEnvironment += "Gateway__AllowedUserIds__$index=$($AllowedUserIds[$index])"
    }
    $null = Invoke-AzJson -Arguments (@(
        'containerapp', 'update', '--name', $webName, '--resource-group', $ResourceGroupName,
        '--subscription', $subscription, '--replace-env-vars'
    ) + $webEnvironment)
}
if (-not (Test-EasyAuthEnabled -AppName $webName -ResourceGroup $ResourceGroupName -Subscription $subscription)) {
    throw 'External web app does not have the required EasyAuth configuration; refusing to report success.'
}

foreach ($entry in @(
    @{ App = $apiApp; Service = 'api' },
    @{ App = $webApp; Service = 'web' }
)) {
    Set-AzdServiceTag -App $entry.App -Service $entry.Service -Subscription $subscription
}

[pscustomobject]@{
    ApiInternalUrl = "https://$apiFqdn"
    WebPublicUrl = "https://$webFqdn"
    ApiIngress = 'internal'
    MinReplicas = 0
    MaxReplicas = 1
}
