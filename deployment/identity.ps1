#Requires -Version 5.1
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [guid] $TenantId,
    [Parameter(Mandatory = $true)]
    [guid] $SubscriptionId,
    [guid] $GatewayPrincipalId = [guid]::Empty,
    [string] $ApiAppName = 'Examinator API',
    [string] $WebAppName = 'Examinator Web',
    [switch] $Deploy
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$apiRoleId = [guid]'5b1590e1-4d81-4c7f-9f98-63c797bd7f24'

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

function Find-Application {
    param(
        [Parameter(Mandatory = $true)] $Applications,
        [Parameter(Mandatory = $true)][string] $DisplayName,
        [string] $IdentifierUri
    )

    $uriMatches = @()
    if (-not [string]::IsNullOrWhiteSpace($IdentifierUri)) {
        $uriMatches = @($Applications | Where-Object {
            @($_.identifierUris) -contains $IdentifierUri
        })
    }
    if ($uriMatches.Count -gt 1) {
        throw "Multiple Entra applications use identifier URI '$IdentifierUri'; resolve manually."
    }
    $nameMatches = @($Applications | Where-Object { $_.displayName -eq $DisplayName })
    if ($nameMatches.Count -gt 1) {
        throw "Multiple Entra applications are named '$DisplayName'; resolve manually."
    }
    if ($uriMatches.Count -eq 1) {
        if ($nameMatches.Count -eq 1 -and $nameMatches[0].id -ne $uriMatches[0].id) {
            throw "The URI '$IdentifierUri' and display name '$DisplayName' identify different applications."
        }
        return $uriMatches[0]
    }
    if ($nameMatches.Count -eq 1) {
        return $nameMatches[0]
    }
    return $null
}

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw 'Install Azure CLI and sign in with az login.'
}
$subscription = $SubscriptionId.ToString()
$targetTenant = Invoke-AzJson -Arguments @(
    'account', 'show', '--subscription', $subscription, '--query', 'tenantId'
)
$graphTenant = Invoke-AzJson -Arguments @('account', 'show', '--query', 'tenantId')
if ([guid]$targetTenant -ne $TenantId) {
    throw "Target subscription $subscription belongs to tenant $targetTenant, not requested tenant $TenantId."
}
if ([guid]$graphTenant -ne [guid]$targetTenant) {
    throw "Azure CLI's current Microsoft Graph context is tenant $graphTenant, but subscription $subscription belongs to $targetTenant. Sign in/select the correct tenant before provisioning registrations."
}

$applications = @(Invoke-AzJson -Arguments @('ad', 'app', 'list', '--all'))
$apiApp = Find-Application -Applications $applications -DisplayName $ApiAppName
$apiClientId = if ($apiApp) { [string]$apiApp.appId } else { $null }
$apiUri = if ($apiClientId) { "api://$apiClientId" } else { $null }
$webApp = Find-Application -Applications $applications -DisplayName $WebAppName
if ($apiApp -and $apiApp.signInAudience -ne 'AzureADMyOrg') {
    throw "Existing API registration '$ApiAppName' is not single-tenant."
}
if ($webApp -and $webApp.signInAudience -ne 'AzureADMyOrg') {
    throw "Existing web registration '$WebAppName' is not single-tenant."
}

Write-Host "Tenant: $TenantId"
if ($apiClientId) {
    Write-Host "API registration: $ApiAppName ($apiClientId), v2 access tokens, Api.Invoke application role."
}
else {
    Write-Host "API registration: '$ApiAppName' will be created."
}
if ($webApp) {
    Write-Host "Web registration: $WebAppName ($($webApp.appId)), single tenant."
}
else {
    Write-Host "Web registration: '$WebAppName' will be created."
}
if (-not $Deploy) {
    Write-Host 'Preflight complete. No Entra objects changed; rerun with -Deploy to provision registrations.'
    return
}
if (-not $PSCmdlet.ShouldProcess($TenantId, 'Create or reconcile Entra application registrations and API app role')) {
    return
}

$createdApi = $false
if (-not $apiApp) {
    $apiClientId = [string](Invoke-AzJson -Arguments @(
        'ad', 'app', 'create', '--display-name', $ApiAppName,
        '--sign-in-audience', 'AzureADMyOrg', '--query', 'appId'
    ))
    $apiUri = "api://$apiClientId"
    $createdApi = $true
}
$applications = @(Invoke-AzJson -Arguments @('ad', 'app', 'list', '--all'))
$apiApp = Find-Application -Applications $applications -DisplayName $ApiAppName -IdentifierUri $apiUri
if (-not $apiApp) {
    throw "Unable to resolve the API registration '$ApiAppName'."
}
$webApp = Find-Application -Applications $applications -DisplayName $WebAppName
if ($apiApp.signInAudience -ne 'AzureADMyOrg') {
    throw "API registration '$ApiAppName' must be single-tenant."
}
if ($webApp -and $webApp.signInAudience -ne 'AzureADMyOrg') {
    throw "Web registration '$WebAppName' must be single-tenant."
}

if (@($apiApp.identifierUris) -notcontains $apiUri) {
    if (@($apiApp.identifierUris).Count -gt 0) {
        throw "Existing API app has a different identifier URI. Refusing to replace it; expected '$apiUri'."
    }
    $null = Invoke-AzJson -Arguments @(
        'ad', 'app', 'update', '--id', $apiApp.id, '--identifier-uris', $apiUri
    )
}

$role = @($apiApp.appRoles | Where-Object { $_.value -eq 'Api.Invoke' })
if ($role.Count -gt 1) {
    throw "API registration has duplicate Api.Invoke roles."
}
if ($role.Count -eq 1 -and (
        [guid]$role[0].id -ne $apiRoleId -or
        @($role[0].allowedMemberTypes) -notcontains 'Application' -or
        -not $role[0].isEnabled
    )) {
    throw 'An incompatible Api.Invoke role already exists; resolve the registration manually.'
}
if ($role.Count -eq 0) {
    $roles = @($apiApp.appRoles)
    if ($null -eq $apiApp.appRoles) {
        $roles = @()
    }
    $roles += [pscustomobject]@{
        allowedMemberTypes = @('Application')
        description = 'Allows the Examinator web gateway to invoke the API.'
        displayName = 'Invoke Examinator API'
        id = $apiRoleId.ToString()
        isEnabled = $true
        value = 'Api.Invoke'
    }
    $api = [ordered]@{
        requestedAccessTokenVersion = 2
    }
    $body = [ordered]@{
        api = $api
        appRoles = $roles
        identifierUris = @($apiUri)
    } | ConvertTo-Json -Depth 20 -Compress
    $null = Invoke-AzJson -Arguments @(
        'rest', '--method', 'patch',
        '--url', "https://graph.microsoft.com/v1.0/applications/$($apiApp.id)",
        '--headers', 'Content-Type=application/json', '--body', $body
    )
}
else {
    $body = @{ api = @{ requestedAccessTokenVersion = 2 } } | ConvertTo-Json -Depth 5 -Compress
    $null = Invoke-AzJson -Arguments @(
        'rest', '--method', 'patch',
        '--url', "https://graph.microsoft.com/v1.0/applications/$($apiApp.id)",
        '--headers', 'Content-Type=application/json', '--body', $body
    )
}

if (-not $webApp) {
    $webAppId = [string](Invoke-AzJson -Arguments @(
        'ad', 'app', 'create', '--display-name', $WebAppName,
        '--sign-in-audience', 'AzureADMyOrg', '--query', 'appId'
    ))
    $applications = @(Invoke-AzJson -Arguments @('ad', 'app', 'list', '--all'))
    $webApp = Find-Application -Applications $applications -DisplayName $WebAppName
    if (-not $webApp -or $webApp.appId -ne $webAppId) {
        throw 'Unable to uniquely resolve the newly created web registration.'
    }
}

$apiSp = @(Invoke-AzJson -Arguments @('ad', 'sp', 'list', '--all', '--filter', "appId eq '$apiClientId'"))
if ($apiSp.Count -gt 1) {
    throw 'Multiple service principals exist for the API registration.'
}
if ($apiSp.Count -eq 0) {
    $null = Invoke-AzJson -Arguments @('ad', 'sp', 'create', '--id', $apiClientId)
    $apiSp = @(Invoke-AzJson -Arguments @('ad', 'sp', 'list', '--all', '--filter', "appId eq '$apiClientId'"))
}
if ($apiSp.Count -ne 1) {
    throw 'Unable to uniquely resolve the API service principal.'
}

if ($GatewayPrincipalId -ne [guid]::Empty) {
    $assignments = Invoke-AzJson -Arguments @(
        'rest', '--method', 'get',
        '--url', "https://graph.microsoft.com/v1.0/servicePrincipals/$($apiSp[0].id)/appRoleAssignedTo"
    )
    $existingAssignment = @($assignments.value | Where-Object {
        $_.principalId -eq $GatewayPrincipalId.ToString() -and
        $_.appRoleId -eq $apiRoleId.ToString()
    })
    if ($existingAssignment.Count -gt 1) {
        throw 'Multiple Api.Invoke grants exist for the web gateway managed identity.'
    }
    if ($existingAssignment.Count -eq 0) {
        $assignmentBody = @{
            principalId = $GatewayPrincipalId.ToString()
            resourceId = [string]$apiSp[0].id
            appRoleId = $apiRoleId.ToString()
        } | ConvertTo-Json -Compress
        $null = Invoke-AzJson -Arguments @(
            'rest', '--method', 'post',
            '--url', "https://graph.microsoft.com/v1.0/servicePrincipals/$($apiSp[0].id)/appRoleAssignedTo",
            '--headers', 'Content-Type=application/json', '--body', $assignmentBody
        )
    }
}

[pscustomobject]@{
    TenantId = $TenantId.ToString()
    ApiAppId = [string]$apiApp.appId
    ApiObjectId = [string]$apiApp.id
    ApiServicePrincipalId = [string]$apiSp[0].id
    ApiIdentifierUri = $apiUri
    ApiRoleId = $apiRoleId.ToString()
    ApiCreated = $createdApi
    WebAppId = [string]$webApp.appId
    WebObjectId = [string]$webApp.id
}
