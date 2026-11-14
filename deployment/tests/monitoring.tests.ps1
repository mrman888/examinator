#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-True {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) { throw $Message }
}

function Get-CompiledTemplate {
    param([string] $RelativePath)
    $file = (Resolve-Path (Join-Path $PSScriptRoot $RelativePath)).Path
    $json = & az bicep build --file $file --stdout --only-show-errors
    if ($LASTEXITCODE -ne 0) { throw "Bicep compilation failed for $RelativePath." }
    return (($json -join "`n") | ConvertFrom-Json)
}

function Get-Resource {
    param($Template, [string] $Type)
    $resources = $Template.resources
    if ($resources -isnot [array]) { $resources = @($resources.PSObject.Properties | ForEach-Object { $_.Value }) }
    return @($resources | Where-Object { $_.type -eq $Type })
}

$base = Get-CompiledTemplate '..\main.bicep'
$workspace = @(Get-Resource $base 'Microsoft.OperationalInsights/workspaces')
$insights = @(Get-Resource $base 'Microsoft.Insights/components')
$environment = @(Get-Resource $base 'Microsoft.App/managedEnvironments')
Assert-True ($workspace.Count -eq 1) 'The base template must provision one Log Analytics workspace.'
Assert-True ($insights.Count -eq 1) 'The base template must provision one Application Insights resource.'
Assert-True ([string]$insights[0].properties.WorkspaceResourceId -like '*Microsoft.OperationalInsights/workspaces*') 'Application Insights must be workspace-based.'
Assert-True ($environment[0].properties.appLogsConfiguration.destination -eq 'log-analytics') 'The Container Apps environment must send logs to Log Analytics.'
Assert-True ($null -ne $base.outputs.appInsightsConnectionString) 'The base template must output the Application Insights connection string.'

$imports = Get-CompiledTemplate '..\imports.bicep'
Assert-True ($null -ne $imports.parameters.appInsightsConnectionString) 'Import jobs must accept the Application Insights connection string.'
Assert-True (($imports.variables | ConvertTo-Json -Depth 20 -Compress).Contains('APPLICATIONINSIGHTS_CONNECTION_STRING')) 'Import jobs must receive APPLICATIONINSIGHTS_CONNECTION_STRING.'

$infra = Get-CompiledTemplate '..\..\infra\main.bicep'
Assert-True (($infra | ConvertTo-Json -Depth 40 -Compress).Contains('appInsightsConnectionString')) 'AZD must pass the connection string to the import jobs.'

$tokens = $null
$errors = $null
$apps = [System.Management.Automation.Language.Parser]::ParseFile(
    (Resolve-Path (Join-Path $PSScriptRoot '..\apps.ps1')).Path, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'apps.ps1 parsing failed.' }
foreach ($name in @('apiEnvironment', 'webEnvironment')) {
    $assignments = @($apps.FindAll({ param($node)
        $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
        $node.Left.Extent.Text -eq "`$$name" -and $node.Operator -eq 'Equals'
    }, $true))
    Assert-True ($assignments.Count -gt 0) "apps.ps1 must define `$$name."
    foreach ($assignment in $assignments) {
        Assert-True ($assignment.Right.Extent.Text.StartsWith('$telemetryEnvironment +')) "`$$name must include the telemetry environment."
    }
}
foreach ($script in @('..\deploy.ps1', '..\azd\update-import-jobs.ps1')) {
    $text = [IO.File]::ReadAllText((Resolve-Path (Join-Path $PSScriptRoot $script)).Path)
    Assert-True ($text.Contains('appInsightsConnectionString=')) "$script must pass the connection string to the import jobs."
}
Write-Host 'Passed: Log Analytics, workspace-based Application Insights, environment logs and app/job telemetry wiring.'
