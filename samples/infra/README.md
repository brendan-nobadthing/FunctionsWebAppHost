# Deploy samples to Azure

This infrastructure is only for trying the samples. Each deployment creates a
Linux Flex Consumption (FC1) Function app, its own plan, storage, managed
identity, Application Insights and Log Analytics workspace.

Requires PowerShell 7.2+, Azure CLI with Bicep and deployment-stack support,
and the .NET 10 SDK. Sign in with `az login`. Your account needs permission to
deploy resources and assign roles in the target resource group.

## Configure

Copy `deploy.config.example.json` to `deploy.config.json` in the sample folder.
Fill in the subscription ID, **existing** resource group and Flex-supported
region. The local configuration is ignored by Git.

Each sample has a separate configuration and Azure deployment stack.
Keep `deploymentName` stable when redeploying; changing it creates another
deployment without removing the old one.

## Provision and deploy

From the repository root:

```powershell
pwsh ./samples/Deploy-Sample.ps1 -Sample WeatherApi -Action Up
```

Run the script without arguments or with `-Help` to see usage and examples.
The script prints its current step, commands and command output as it runs.

Use `-Action Provision` to create infrastructure only, or `-Action Deploy` to
publish code to an already provisioned app. Use
`-Sample FunctionsWebAppHostTest` for the diagnostic sample.
`-Config <path>` selects an alternative JSON configuration.
`-WhatIf` previews an operation without provisioning, uploading or deleting.

WeatherApi's example enables OpenAPI and Scalar in Azure using
`enableApiDocumentation`, while keeping the app in Production. Set it to false
and provision again to disable cloud documentation.

## Remove

```powershell
pwsh ./samples/Deploy-Sample.ps1 -Sample WeatherApi -Action Remove -WhatIf
pwsh ./samples/Deploy-Sample.ps1 -Sample WeatherApi -Action Remove
```

Removal lists the stack's resources and asks for confirmation. It deletes only
resources managed by that sample's deployment stack, **not the resource group**.
Do not add unrelated resources to these stacks or reuse their generated
resources for other applications.

Provisioning uses non-destructive `detachAll` behavior. If you manually remove
a resource from the Bicep template, an update leaves it behind; remove the
sample before changing the template's resource set.

These are public, anonymous demo APIs, not a production security setup.
Storage and monitoring incur charges even when there are no requests.
There are no always-ready instances. Remove test deployments when finished.
If provisioning or upload fails, the command reports the error and leaves
resources available for inspection, retry or explicit removal.
