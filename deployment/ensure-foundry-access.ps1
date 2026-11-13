#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [guid] $SubscriptionId,
    [Parameter(Mandatory = $true)]
    [string] $ResourceGroupName,
    [Parameter(Mandatory = $true)]
    [ValidateSet('swedencentral')]
    [string] $Location,
    [Parameter(Mandatory = $true)]
    [string] $AccountName,
    [Parameter(Mandatory = $true)]
    [guid] $PrincipalId
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

$subscription = $SubscriptionId.ToString()
$foundry = Invoke-AzJson -Arguments @(
    'cognitiveservices', 'account', 'show', '--name', $AccountName,
    '--resource-group', $ResourceGroupName, '--subscription', $subscription
)
if ($foundry.kind -ne 'AIServices' -or $foundry.location -ne $Location) {
    throw "Existing Foundry account must be AIServices in $Location. No account is imported or changed."
}
if ($foundry.properties.endpoint -ne "https://$AccountName.cognitiveservices.azure.com/") {
    throw 'Existing Foundry account endpoint differs from the configured runtime endpoint.'
}

$roleAssignments = @(Invoke-AzJson -Arguments @(
    'role', 'assignment', 'list', '--scope', $foundry.id,
    '--subscription', $subscription
))
$foundryRole = @($roleAssignments | Where-Object {
    $_.principalId -eq $PrincipalId.ToString() -and
    $_.roleDefinitionName -eq 'Cognitive Services OpenAI User'
})
if ($foundryRole.Count -eq 0) {
    $null = Invoke-AzJson -Arguments @(
        'role', 'assignment', 'create',
        '--assignee-object-id', $PrincipalId.ToString(),
        '--assignee-principal-type', 'ServicePrincipal',
        '--role', 'Cognitive Services OpenAI User',
        '--scope', $foundry.id, '--subscription', $subscription
    )
}
