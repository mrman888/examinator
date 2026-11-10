# Examinator Foundry deployment

Provisions a Microsoft Foundry account, project, GPT-5 mini model deployment,
and an account-scoped **Cognitive Services OpenAI User** role assignment.
This does not deploy the .NET backend or change the Angular application.

## Configuration

- Resource group: `examinator` (created if missing).
- Region: `swedencentral`.
- Deployment: `exam-classifier`, model `gpt-5-mini`, version `2025-08-07`.
- SKU: **DataZoneStandard**. Inference is processed within the EU data zone,
  not necessarily only in Sweden. Global deployments are not used.
- Initial capacity: 10 quota units, not a spending limit. Model-specific
  request/token rates are shown in Foundry.
- Authentication: Entra ID only; API keys are disabled.
- Networking: public HTTPS endpoint with Entra authorization. Private endpoints
  and backend hosting are outside this deployment's scope.

Region and SKU are constrained in the template. The script checks subscription
model availability and quota, including capacity already assigned to the same
deployment. These checks do not reserve capacity or guarantee deployment success.
The model version is pinned; review its retirement date and plan upgrades.

## Prerequisites

1. Install Azure CLI and run `az login`. Signing in at portal.azure.com alone
   does not sign in the CLI.
2. Run `az bicep install` if Bicep is missing.
3. Use an identity allowed to create resources and role assignments.
   Contributor alone cannot create role assignments; an appropriately scoped
   Role Based Access Control Administrator (or equivalent permission) is also
   needed. Creating a missing resource group requires subscription-level access.
4. Ensure the `Microsoft.CognitiveServices` resource provider is registered.
   If necessary, an authorized administrator can run:

   ```powershell
   az provider register --namespace Microsoft.CognitiveServices --subscription '<subscription-id>' --wait
   ```

No secrets belong in these files, command arguments, or chat.

## Check first (no Azure changes)

Run from the repository root in PowerShell:

```powershell
.\deployment\foundry\deploy.ps1 -SubscriptionId '<subscription-id>'
```

The script compiles Bicep, checks model/SKU availability and quota, and resolves
the signed-in user's object ID for inference access. It does not change the
CLI's default subscription. An account name is derived from the subscription;
override `-AccountName` if the name is taken or deploying another environment.

## Provision explicitly

After reviewing pricing, permissions, and the check output:

```powershell
.\deployment\foundry\deploy.ps1 -SubscriptionId '<subscription-id>' -Deploy
```

Use the same overrides on both commands. `-Deploy -WhatIf` runs the preflight
checks but skips resource creation; it is not an ARM resource-level what-if.

Deployment is incremental and can be rerun. It does not delete unrelated
resources, but an existing account's declared properties are reconciled.
Use a dedicated account. Azure deployments are not transactional: on failure,
inspect the resource group's deployment history for partial resources before
retrying. The script deliberately rejects replacing an existing deployment
with a different model/version/SKU.

For a service principal or a future backend managed identity, supply its
**object/principal ID**, not application/client ID:

```powershell
.\deployment\foundry\deploy.ps1 -SubscriptionId '<subscription-id>' `
    -PrincipalId '<principal-object-id>' -PrincipalType ServicePrincipal -Deploy
```

Changing the principal adds another role assignment; it does not revoke previous
assignments. Review and remove obsolete access explicitly.

## After deployment

The command prints the account endpoint, deployment name, account resource ID,
and project resource ID. These are non-secret configuration values for the
future .NET API. Use the account endpoint for inference, not the project URL.

Open [Foundry](https://ai.azure.com/) and select the project. Resource-management
permissions and project/playground permissions are distinct from inference;
ask an administrator for appropriate project access if the portal denies it.
Role assignments can take a few minutes to propagate.

The future local .NET backend can use `DefaultAzureCredential` with your
`az login` identity and token scope `https://cognitiveservices.azure.com/.default`.
Hosted Azure backends should use their managed identity with the inference role.
Test a small synthetic classification request before uploading exam content.
No inference requests are made by this provisioning script.

Set budget alerts in Azure Cost Management separately. Alerts do not impose a
hard spending cap. Review the provider's data-processing terms, including abuse
monitoring, before treating deployment location as a complete compliance policy.
No storage, search, agent service, or reserved/provisioned throughput is created.
Remove only these named resources when no longer needed; never delete a shared
resource group as a shortcut.

## References

- [Create Foundry resources](https://learn.microsoft.com/en-us/azure/foundry/tutorials/quickstart-create-foundry-resources)
- [Deployment types and processing boundaries](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/concepts/deployment-types)
- [Foundry project Bicep reference](https://learn.microsoft.com/en-us/azure/templates/microsoft.cognitiveservices/2025-06-01/accounts/projects)
