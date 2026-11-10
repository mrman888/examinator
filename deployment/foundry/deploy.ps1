#Requires -Version 5.1
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [guid] $SubscriptionId,
    [string] $ResourceGroupName = 'examinator',
    [ValidateSet('swedencentral')]
    [string] $Location = 'swedencentral',
    [ValidatePattern('^[a-z0-9][a-z0-9-]{1,62}[a-z0-9]$')]
    [string] $AccountName,
    [string] $ProjectName = 'examinator',
    [string] $DeploymentName = 'exam-classifier',
    [string] $ModelName = 'gpt-5-mini',
    [string] $ModelVersion = '2025-08-07',
    [ValidateRange(1, 1000000)]
    [int] $Capacity = 10,
    [guid] $PrincipalId = [guid]::Empty,
    [ValidateSet('User', 'ServicePrincipal')]
    [string] $PrincipalType = 'User',
    [switch] $Deploy
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-AzureJson {
    param([Parameter(Mandatory = $true)][string[]] $Arguments)

    $result = & az @Arguments --only-show-errors --output json
    if ($LASTEXITCODE -ne 0) {
        throw "Azure CLI command failed: az $($Arguments -join ' ')"
    }
    if ($result) {
        return (($result -join "`n") | ConvertFrom-Json)
    }
}

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw 'Install Azure CLI, then run az login. Portal login does not sign in Azure CLI.'
}
if ([string]::IsNullOrWhiteSpace($AccountName)) {
    $AccountName = "examinator-$($SubscriptionId.ToString('N').Substring(0, 8))"
}

$subscription = $SubscriptionId.ToString()
$account = Invoke-AzureJson -Arguments @('account', 'show', '--subscription', $subscription)
if ($account.state -ne 'Enabled') {
    throw 'The target subscription is not enabled.'
}

& az bicep version --only-show-errors
if ($LASTEXITCODE -ne 0) {
    throw 'Bicep is missing. Run az bicep install, then rerun this script.'
}
$template = Join-Path $PSScriptRoot 'main.bicep'
& az bicep build --file $template --stdout --only-show-errors | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'Bicep compilation failed.'
}

$provider = Invoke-AzureJson -Arguments @(
    'provider', 'show', '--namespace', 'Microsoft.CognitiveServices', '--subscription', $subscription
)
if ($provider.registrationState -ne 'Registered') {
    throw 'Register Microsoft.CognitiveServices in the target subscription before deployment.'
}

$modelsUrl = "https://management.azure.com/subscriptions/$subscription/providers/Microsoft.CognitiveServices/locations/$Location/models?api-version=2025-06-01"
$catalog = Invoke-AzureJson -Arguments @('rest', '--method', 'get', '--url', $modelsUrl)
$matches = @($catalog.value | Where-Object {
    $_.model.format -eq 'OpenAI' -and $_.model.name -eq $ModelName -and $_.model.version -eq $ModelVersion
})
$sku = $null
foreach ($match in $matches) {
    $sku = @($match.model.skus | Where-Object { $_.name -eq 'DataZoneStandard' }) | Select-Object -First 1
    if ($null -ne $sku) { break }
}
if ($null -eq $sku) {
    throw "$ModelName version $ModelVersion is unavailable with DataZoneStandard in $Location. No Global fallback is permitted."
}
foreach ($bound in @('minimum', 'maximum')) {
    $property = $sku.capacity.PSObject.Properties[$bound]
    if ($null -ne $property) {
        if (($bound -eq 'minimum' -and $Capacity -lt $property.Value) -or
            ($bound -eq 'maximum' -and $Capacity -gt $property.Value)) {
            throw "Capacity $Capacity is outside the model SKU's $bound bound ($($property.Value))."
        }
    }
}
$stepProperty = $sku.capacity.PSObject.Properties['step']
if ($null -ne $stepProperty) {
    $minimumProperty = $sku.capacity.PSObject.Properties['minimum']
    $minimum = 0
    if ($null -ne $minimumProperty) { $minimum = $minimumProperty.Value }
    if (($Capacity - $minimum) % $stepProperty.Value -ne 0) {
        throw "Capacity must increase in steps of $($stepProperty.Value) from $minimum."
    }
}

$usage = @(Invoke-AzureJson -Arguments @(
    'cognitiveservices', 'usage', 'list', '--location', $Location, '--subscription', $subscription
))
$quota = @($usage | Where-Object { $_.name.value -eq $sku.usageName }) | Select-Object -First 1
if ($null -eq $quota) {
    throw "Unable to verify quota for $($sku.usageName). Check Foundry quota before proceeding."
}

$groupExists = Invoke-AzureJson -Arguments @(
    'group', 'exists', '--name', $ResourceGroupName, '--subscription', $subscription
)
$existingCapacity = 0
if ($groupExists) {
    $accounts = @(Invoke-AzureJson -Arguments @(
        'cognitiveservices', 'account', 'list', '--resource-group', $ResourceGroupName,
        '--subscription', $subscription
    ))
    $existingAccount = @($accounts | Where-Object { $_.name -eq $AccountName }) | Select-Object -First 1
    if ($null -ne $existingAccount) {
        if ($existingAccount.kind -ne 'AIServices' -or $existingAccount.location -ne $Location) {
            throw 'The existing account has an incompatible kind or region. Use a new AccountName.'
        }
        $deployments = @(Invoke-AzureJson -Arguments @(
            'cognitiveservices', 'account', 'deployment', 'list', '--name', $AccountName,
            '--resource-group', $ResourceGroupName, '--subscription', $subscription
        ))
        $existing = @($deployments | Where-Object { $_.name -eq $DeploymentName }) | Select-Object -First 1
        if ($null -ne $existing) {
            if ($existing.sku.name -ne 'DataZoneStandard' -or
                $existing.properties.model.name -ne $ModelName -or
                $existing.properties.model.version -ne $ModelVersion) {
                throw 'The existing deployment differs from the requested model/version/SKU. Use a new DeploymentName.'
            }
            $existingCapacity = $existing.sku.capacity
        }
    }
}
$additionalCapacity = [Math]::Max(0, $Capacity - $existingCapacity)
$remaining = $quota.limit - $quota.currentValue
if ($additionalCapacity -gt $remaining) {
    throw "Insufficient EU Data Zone quota: need $additionalCapacity additional units; $remaining available. Request a quota increase in Foundry."
}

if ($PrincipalId -eq [guid]::Empty) {
    if ($PrincipalType -ne 'User') {
        throw 'Supply PrincipalId explicitly when using a service principal or managed identity.'
    }
    $user = Invoke-AzureJson -Arguments @('ad', 'signed-in-user', 'show', '--query', 'id')
    $PrincipalId = [guid]$user
}

Write-Host "Target: subscription $subscription / $ResourceGroupName / $Location"
Write-Host "Foundry account: $AccountName; project: $ProjectName"
Write-Host "Model: $ModelName $ModelVersion; deployment: $DeploymentName"
Write-Host "SKU: DataZoneStandard (EU); capacity: $Capacity; quota remaining: $remaining"
Write-Host 'Authentication: Entra ID only; API keys disabled.'
if (-not $Deploy) {
    Write-Host 'Checks passed. No Azure resources changed. Rerun with -Deploy to provision.'
    return
}
if (-not $PSCmdlet.ShouldProcess("$subscription/$ResourceGroupName", 'Provision Foundry account, project, model deployment and inference role assignment')) {
    return
}

if (-not $groupExists) {
    $null = Invoke-AzureJson -Arguments @(
        'group', 'create', '--name', $ResourceGroupName, '--location', $Location,
        '--subscription', $subscription
    )
}
$parameters = @(
    "location=$Location", "accountName=$AccountName", "projectName=$ProjectName",
    "deploymentName=$DeploymentName", "modelName=$ModelName", "modelVersion=$ModelVersion",
    "capacity=$Capacity", "principalId=$PrincipalId", "principalType=$PrincipalType"
)
$deployment = Invoke-AzureJson -Arguments (@(
    'deployment', 'group', 'create', '--name', 'examinator-foundry',
    '--resource-group', $ResourceGroupName, '--subscription', $subscription,
    '--template-file', $template, '--mode', 'Incremental', '--parameters'
) + $parameters)

$deployment.properties.outputs | ConvertTo-Json -Depth 10
Write-Host 'Provisioning completed. Allow a few minutes for Entra role propagation before testing inference.'
