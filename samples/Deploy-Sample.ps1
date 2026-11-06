#Requires -Version 7.2
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('WeatherApi', 'FunctionsWebAppHostTest')]
    [string] $Sample,
    [ValidateSet('Provision', 'Deploy', 'Up', 'Remove')]
    [string] $Action,
    [string] $Config,
    [Alias('h')]
    [switch] $Help
)

$ErrorActionPreference = 'Stop'
if ($Help -or !$Sample -or !$Action) {
    Write-Host @'
Deploy an Azure Functions sample

Usage:
  pwsh ./Deploy-Sample.ps1 -Sample <sample> -Action <action> [-Config <path>] [-WhatIf]

Samples:
  WeatherApi                 Minimal weather API with OpenAPI and Scalar
  FunctionsWebAppHostTest     Controllers, minimal APIs and HTTP diagnostics

Actions:
  Provision    Create or update the sample's Azure resources
  Deploy       Publish code to an already provisioned app
  Up           Provision resources and deploy code
  Remove       Delete the sample's resources, with confirmation

Examples (from the samples folder):
  pwsh ./Deploy-Sample.ps1 -Sample WeatherApi -Action Up
  pwsh ./Deploy-Sample.ps1 -Sample FunctionsWebAppHostTest -Action Deploy
  pwsh ./Deploy-Sample.ps1 -Sample WeatherApi -Action Remove -WhatIf

Copy the sample's deploy.config.example.json to deploy.config.json and fill in
your subscription, existing resource group and region before deploying.
Use -Help to show this message.
'@
    if (!$Help -and ($Sample -or $Action)) {
        throw 'Both -Sample and -Action are required.'
    }
    return
}
$root = Split-Path $PSScriptRoot -Parent

function Invoke-Az {
    param([string[]] $Arguments)
    Write-Host "> az $($Arguments -join ' ') --subscription $($settings.subscriptionId) --output json"
    $result = [Collections.Generic.List[string]]::new()
    & az @Arguments --subscription $settings.subscriptionId --output json | ForEach-Object {
        Write-Host $_
        $result.Add([string] $_)
    }
    if ($LASTEXITCODE -ne 0) { throw "Azure CLI failed: $($Arguments -join ' ')" }
    if ($result.Count) { return ($result -join "`n" | ConvertFrom-Json -AsHashtable) }
}

if (!$Config) { $Config = Join-Path $root "samples/$Sample/deploy.config.json" }
Write-Host "[$Action] $Sample"
Write-Host "Reading configuration: $Config"
if (!(Test-Path -LiteralPath $Config)) {
    throw "Copy samples/$Sample/deploy.config.example.json to deploy.config.json and fill in the target."
}
$settings = Get-Content -LiteralPath $Config -Raw | ConvertFrom-Json -AsHashtable
foreach ($field in @('subscriptionId', 'resourceGroup', 'location', 'deploymentName')) {
    if ($settings[$field] -isnot [string] -or [string]::IsNullOrWhiteSpace($settings[$field]) -or $settings[$field].Contains('<')) {
        throw "Fill in $field in $Config."
    }
}
$subscription = [guid]::Empty
if (![guid]::TryParse($settings.subscriptionId, [ref] $subscription) -or $subscription -eq [guid]::Empty) {
    throw 'subscriptionId must be an Azure subscription GUID.'
}
if ($settings.deploymentName -notmatch '^[a-z][a-z0-9-]{2,29}$') {
    throw 'deploymentName must be 3-30 lowercase letters, digits or hyphens, starting with a letter.'
}
foreach ($entry in @{ instanceMemoryMB = 2048; maximumInstanceCount = 40; enableApiDocumentation = $false }.GetEnumerator()) {
    if (!$settings.ContainsKey($entry.Key)) { $settings[$entry.Key] = $entry.Value }
}
if ($settings.enableApiDocumentation -isnot [bool]) { throw 'enableApiDocumentation must be a JSON boolean.' }

$stackName = "$($settings.deploymentName)-$($Sample.ToLowerInvariant())"
$target = "$($settings.subscriptionId)/$($settings.resourceGroup)/$stackName"
Write-Host "Target: $target"
Write-Host 'Checking resource group and existing deployment stack...'
$null = Invoke-Az @('group', 'show', '--name', $settings.resourceGroup)
$stacks = Invoke-Az @('stack', 'group', 'list', '--resource-group', $settings.resourceGroup)
$stack = $stacks | Where-Object { $_.name -eq $stackName } | Select-Object -First 1
if ($stack -and ($stack.tags.Project -ne 'FunctionsWebAppHost' -or $stack.tags.Sample -ne $Sample)) {
    throw "Stack $stackName is not owned by this sample."
}

if ($Action -eq 'Remove') {
    if (!$stack) { Write-Host "Stack $stackName is already absent."; return }
    $details = if ($stack.ContainsKey('properties')) { $stack.properties } else { $stack }
    Write-Host "Removing sample resources from $target (resource group will be preserved):"
    $details.resources | ForEach-Object { Write-Host "  $($_.id)" }
    if ($PSCmdlet.ShouldProcess($target, 'Delete the sample stack and its resources') -and
        $PSCmdlet.ShouldContinue('This deletes the sample app, storage and monitoring resources.', "Remove $stackName?")) {
        $null = Invoke-Az @('stack', 'group', 'delete', '--resource-group', $settings.resourceGroup,
            '--name', $stackName, '--action-on-unmanage', 'deleteResources', '--yes')
        Write-Host "Removed $stackName."
    }
    return
}

$artifacts = Join-Path $root "artifacts/azure/$stackName"
if ($Action -in @('Provision', 'Up')) {
    if (!$PSCmdlet.ShouldProcess($target, 'Provision sample resources')) { return }
    Write-Host "Provisioning infrastructure for $stackName..."
    $null = New-Item -ItemType Directory -Path $artifacts -Force
    $parameters = @{}
    foreach ($field in @('location', 'deploymentName', 'instanceMemoryMB', 'maximumInstanceCount', 'enableApiDocumentation')) {
        $parameters[$field] = @{ value = $settings[$field] }
    }
    $parameters.sample = @{ value = $Sample }
    $parameterFile = Join-Path $artifacts 'parameters.json'
    @{ parameters = $parameters; contentVersion = '1.0.0.0'; '$schema' = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#' } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $parameterFile
    $stack = Invoke-Az @('stack', 'group', 'create', '--resource-group', $settings.resourceGroup,
        '--name', $stackName, '--template-file', (Join-Path $root 'samples/infra/samples.bicep'),
        '--parameters', "@$parameterFile", '--action-on-unmanage', 'detachAll', '--deny-settings-mode', 'none',
        '--tags', 'Project=FunctionsWebAppHost', "Sample=$Sample", '--yes')
    Write-Host "Provisioned $stackName."
}

if ($Action -in @('Deploy', 'Up')) {
    if (!$stack) { throw 'Run Provision or Up first.' }
    $details = if ($stack.ContainsKey('properties')) { $stack.properties } else { $stack }
    $appName = $details.outputs.appName.value
    $appUrl = $details.outputs.appUrl.value
    if (!$appName -or !$appUrl) { throw 'Stack outputs are missing. Run Provision first.' }
    if (!$PSCmdlet.ShouldProcess($target, 'Publish and deploy sample code')) { return }
    $run = Join-Path $artifacts ([guid]::NewGuid().ToString())
    $publish = Join-Path $run 'publish'
    $null = New-Item -ItemType Directory -Path $publish -Force
    $project = Join-Path $root "samples/$Sample/$Sample.csproj"
    Write-Host 'Building and publishing sample code...'
    Write-Host "> dotnet publish $project -c Release -o $publish --no-self-contained"
    & dotnet publish $project -c Release -o $publish --no-self-contained
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
    foreach ($required in @('host.json', 'worker.config.json', '.azurefunctions')) {
        if (!(Test-Path -LiteralPath (Join-Path $publish $required))) { throw "Publish output is missing $required." }
    }
    # Compress-Archive skips hidden files on Unix, including .azurefunctions.
    $package = Join-Path $run 'sample.zip'
    Write-Host "Creating deployment package: $package"
    $archive = [IO.Compression.ZipFile]::Open($package, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in [IO.Directory]::EnumerateFiles($publish, '*', [IO.SearchOption]::AllDirectories)) {
            if ([IO.Path]::GetFileName($file) -in @('local.settings.json', 'deploy.config.json')) { continue }
            $relative = [IO.Path]::GetRelativePath($publish, $file).Replace('\', '/')
            $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file, $relative)
        }
    }
    finally { $archive.Dispose() }
    Write-Host "Uploading package to $appName..."
    $null = Invoke-Az @('functionapp', 'deployment', 'source', 'config-zip', '--resource-group', $settings.resourceGroup,
        '--name', $appName, '--src', $package, '--build-remote', 'false', '--timeout', '600')
    Write-Host "Deployed: $appUrl"
}
