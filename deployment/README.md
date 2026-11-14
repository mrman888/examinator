# Examinator Azure deployment

These scripts provision Azure infrastructure and deploy pre-existing .NET API
and web container images. They do not change application source, build Docker
images locally, import the Foundry account, or send invitations to users.
Infrastructure/application changes require `-Deploy`; image builds require
`-Build` (or the orchestration script's `-Deploy`). Without those switches,
the orchestration script performs local and read-only preflight checks.

## Local AZD deployment

`azure.yaml` is the AZD entry point for the existing Development environment.
It reuses the current `examinator` resource group, Container Apps environment,
ACR, managed identities, Foundry account and model deployment. The Bicep entry
point composes the existing `main.bicep` and `imports.bicep`; it does not create
a second Container Apps environment or Foundry model. AZD passes through the
images already running in the API and Web apps; it does not build or retag them.
The post-provision hook reuses the existing identity/EasyAuth setup and
idempotently ensures the API identity has the `Cognitive Services OpenAI User`
role on the existing Foundry account, without changing its model deployment.
This keeps deployment on the existing ACR build path, which disables log
streaming and waits for each image build to complete. Local Docker is not needed.
AZD's PowerShell hooks require PowerShell 7 (`pwsh`).

Use the already-confirmed subscription and Sweden Central location:

```powershell
azd env new development --subscription '<confirmed-subscription-id>' `
    --location swedencentral --no-prompt
azd env set AZURE_RESOURCE_GROUP examinator
azd env set KEY_VAULT_WRITER_OBJECT_ID '<deployer-object-id>'
azd up
```

`azd up` provisions import storage and jobs, then its post-provision hook reuses
the existing Entra, Foundry-access, and EasyAuth setup scripts. The API
service's post-deploy hook updates both jobs to the API image just deployed.
Set `ALLOWED_USER_IDS`
in the local AZD environment only when additional users must be allowlisted;
the hook preserves existing IDs and includes the signed-in user when available.
The hook also merges AZD's `azd-service-name` tags onto the apps so AZD can
distinguish them from same-named managed identities. The AZD environment
directory is ignored by Git and must not be committed.

For an application-code update, build unique ACR image tags with
`deployment/build.ps1 -Build`, then deploy those remote images with
`azd deploy api --from-package <api-image>` and
`azd deploy web --from-package <web-image>`. Run the API deployment first; its
post-deploy hook reconciles both import jobs to the new API image.

The existing GitHub Actions manual deployment remains on `deploy.ps1`; avoid
running it concurrently with AZD because both target the same Development
resources.

## Deployed development instance

Website: <https://examinator-web.mangopond-8c0feacb.swedencentral.azurecontainerapps.io/>

Microsoft sign-in and the hosted prompt -> private API -> Foundry response have
been verified. The API has internal ingress; anonymous website requests are
challenged, and `/healthz` returns a minimal public health response.
The existing Azure CLI user is allowlisted. No guest invitation has been sent.

To redeploy the currently published images without rebuilding:

```powershell
.\deployment\deploy.ps1 `
    -SubscriptionId '46954202-8af2-461f-beb1-2c0cde3191e0' `
    -TenantId '1f200865-acbe-460b-9c07-293d4300e333' `
    -ImageTag 'release-20261110-2044' -SkipBuild -Deploy
```

To redeploy from GitHub Actions without rebuilding, run the workflow manually
from `main` and set the `image_tag` input to an existing tag. Tests are skipped
and `deploy.ps1 -SkipBuild` verifies that both `examinator-api` and
`examinator-web` exist with that tag in the registry. Images built by the
workflow are tagged `gha-<run id>-<attempt>`; the tag is printed in that run's
deploy logs. Leave `image_tag` empty to test, build and deploy new images.

For code changes, omit `-SkipBuild` and choose a new `-ImageTag` (or omit the tag
to generate a timestamp). Both Docker images were successfully built by ACR.
The build script waits for ACR to finish with `--no-logs`; it does not use
`--no-wait`. This avoids Windows Azure CLI encoding failures when Angular emits
Unicode progress output. Failed ACR runs still fail the script. Inspect build
logs in the registry's Tasks run history or with `az acr task logs`.
Run the build-script regression checks without Azure access using
`powershell -NoProfile -File .\deployment\tests\build.tests.ps1`.
JSON management requests use temporary UTF-8 files to avoid Windows command-line
quote removal; those files are deleted after each request.
EasyAuth's hybrid sign-in flow requires ID-token issuance on its web registration;
the application deployment script configures this as well as the callback URL.

## GitHub Actions CI and manual deployment

To enable imports on manual runs, set the `development` environment variable
`IMPORT_DEPLOYMENT_NAME` to an evaluated vision/strict-schema Foundry deployment.
Keep it configured on subsequent deployments. This does not deploy or change
the model used by **AI testen**.

`.github/workflows/ci-cd.yml` runs the Angular build and tests and the .NET
tests on pull requests targeting `main` and on pushes to `main`. A manually
started run on `main` runs those checks again and, only if they pass, invokes
`deploy.ps1` against the existing development deployment. Pull requests and
pushes never deploy. The script still uses remote ACR Build, so GitHub Actions
orchestrates the deployment but does not replace Azure's build service.

Before the first run, an Azure/Entra administrator must:

1. Create a dedicated deployment identity, separate from the application
   managed identities, and configure GitHub OIDC federation. For a
   user-assigned managed identity, add a federated credential with issuer
   `https://token.actions.githubusercontent.com`, audience
   `api://AzureADTokenExchange`, and subject
   `repo:mrman888@13385447/examinator@1406228505:environment:development`.
   Use the exact subject claim printed by `azure/login` in the workflow logs;
   this repository uses GitHub's immutable-ID subject format, not the older
   `repo:mrman888/examinator:environment:development` format. If the repository
   is renamed or moved, verify the subject again. Configure the `development` GitHub
   environment to allow only `main`; enable required reviewers if available
   and desired. Restrict who can change workflows and start deployments.
2. Grant the identity the Azure resource and role-assignment permissions
   described in **Prerequisites and permissions**, scoped as narrowly as
   possible, including ACR build/push. Grant the Microsoft Graph **application
   permissions** required to read and reconcile registrations, service
   principals and app-role assignments, with administrator consent. Azure
   RBAC alone is not sufficient. The script may create or modify Entra
   registrations, credentials and role assignments; review these elevated
   permissions before granting them.
3. In the GitHub `development` environment, set variables `AZURE_CLIENT_ID`
   to the deployment identity's client ID, `AZURE_TENANT_ID` to the tenant ID,
   and `AZURE_SUBSCRIPTION_ID` to the subscription ID. Set the environment
   secret `ALLOWED_USER_IDS` to a comma-separated list of all approved Entra
   **user object IDs**, including the current operator if they should retain
   access. Service-principal sign-in does not automatically add a signed-in
   user; the configured list replaces the gateway allowlist on deployment.
4. Commit and push the workflow to `main`, then select **Actions → CI and
   manual deployment → Run workflow**, choosing `main`. Environment approval,
   if configured, happens before the Azure deployment job. Each attempt uses
   its own `gha-<run-id>-<attempt>` image tag. Deployment concurrency prevents
   overlapping runs, but a failed script can leave partial changes; inspect
   the run and Azure resources before retrying. The workflow retries the public
   web `/healthz` endpoint after deployment; this does not verify sign-in,
   the private API, or Foundry inference end to end.

The workflow uses the script's existing resource group, registry and Foundry
defaults; it does not provision Foundry. It does not configure GitHub
environments, federation, permissions or secrets automatically. Azure
resources, ACR builds and GitHub-hosted runner usage may incur charges.

The development environment uses the dedicated user-assigned managed identity
`examinator-github-deploy` in the `examinator` resource group. Its Contributor
and Role Based Access Control Administrator assignments are scoped to that
resource group, not the subscription. Its Microsoft Graph application grants
are `Application.ReadWrite.All` and `AppRoleAssignment.ReadWrite.All`; these
are tenant-wide privileges. Its federated credential accepts only the
`development` environment subject above, and the GitHub environment permits
only the `main` branch. `ALLOWED_USER_IDS` must preserve the complete approved
gateway user list when changing deployment configuration.

## Architecture

### Exam import storage and jobs

Enable import infrastructure explicitly on **every** deployment:

```powershell
.\deployment\deploy.ps1 -SubscriptionId $subscription -TenantId $tenant `
    -EnableImports -ImportDeploymentName 'your-evaluated-vision-deployment' -Deploy
```

The scripts create Standard GPv2 **LRS** storage with authenticated public HTTPS
endpoints. Anonymous Blob access and Shared Key authorization are disabled.
Containers `exam-documents`, `exam-imports` and `exam-results` are not public.
Tables `ImportJobs`/`ExamCatalog` index recent jobs, subjects, sittings and document
IDs; dispatch and poison queues contain only opaque import IDs. Active files
stay Hot for immediate preview. Blob versioning/7-day delete recovery are enabled;
only historical `exam-imports/` versions expire after seven days. Original PDFs
and current publication manifests are never automatically deleted or archived.

The API identity has Blob/Queue/Table Data Contributor roles and its existing
Foundry permission; the same identity runs the worker and reconciler. The web
identity has no Storage data role. Authenticated document traffic goes through
Web → internal Api, including Range requests. There are **no new VNets, Private
Endpoints, private DNS zones, NAT gateways or firewalls**.

`imports.bicep` provisions a Consumption event job (one concurrent execution,
zero automatic replica retries, four-hour timeout) and a reconciler every five
minutes. KEDA uses managed identity, not a connection string. The worker renews
queue visibility and its lease, checkpoints paid calls, and quarantines invalid/
repeatedly interrupted dispatch. A failed model call is visible in the draft;
retry is an explicit user action with possible additional cost. The 64-call
ceiling is not a monetary cap. Set Azure cost alerts/budgets separately.

Job resources use a stable API instead of the discovered AVM job module's
preview resource API. No model upgrade, Azure deployment or paid quality test
is performed by merely applying these code changes.

### Key Vault and EasyAuth migration

Base provisioning now creates a Standard, RBAC-enabled Key Vault with public
authenticated HTTPS, soft deletion and purge protection. The deployment
identity gets Secrets Officer; the web identity gets Secrets User. Storage,
queue scaling, registry pulls and Foundry need **no stored credentials**.

The unavoidable EasyAuth client secret is written to Key Vault through a
temporary UTF-8 file, never a command-line value, and removed from that file in
`finally`, including on failure. ACA stores only a Key Vault/managed-identity
reference. On upgrade, the existing ACA value is copied to Key Vault before its
reference is switched; Entra is not rotated automatically. Missing/ambiguous
existing credentials stop deployment. Existing secret values never enter logs.
An already-populated Vault value must correspond to the current Entra credential;
verify this before migrating an existing deployment. Allow RBAC to propagate
and retry a failed secret migration after inspecting the current state.

Run secret-write/cleanup checks without Azure:

```powershell
powershell -NoProfile -File .\deployment\tests\imports.tests.ps1
```

Deployment permissions and Key Vault references must be smoke-tested privately
before enabling imports for regular use. Health checks alone do not verify
Foundry processing, import approval or EasyAuth credential validity.

- Resource group: `examinator`; region is constrained to **Sweden Central**
  (`swedencentral`).
- Basic Azure Container Registry with its admin user disabled. The API and web
  have separate user-assigned managed identities and `AcrPull` assignments.
  Container Apps authenticate to the registry using those identities, never
  registry passwords. The registry endpoint remains network-public so the
  Consumption environment can pull images; no private endpoint/VNet is created.
- One Log Analytics workspace (`examinator-logs`, 30-day retention, 1 GB/day
  cap) and a workspace-based Application Insights resource
  (`examinator-insights`). The Container Apps environment sends console and
  system logs to the workspace. The API, web app and both import jobs receive
  `APPLICATIONINSIGHTS_CONNECTION_STRING` and export OpenTelemetry logs, traces
  and metrics through Azure Monitor; without that variable no telemetry is sent.
- One Container Apps **Consumption** environment. The API has internal-only
  ingress. The web app has external HTTPS ingress and Container Apps built-in
  Entra authentication (EasyAuth). On first deployment, the web app remains
  internal until its secret, redirect URI, and EasyAuth configuration are
  installed; external ingress is enabled last.
- Both apps use 0.5 vCPU / 1 GiB, port 8080, HTTP `/healthz` probes, and
  0–1 replicas. The health path is excluded from EasyAuth so probes do not
  redirect to sign-in. The API exposes no public endpoint.
- The web gateway receives an explicit list of allowed Entra **user object
  IDs**. EasyAuth authenticates a single-tenant account; application code must
  enforce the allowlist against the authenticated `tid` and `oid` claims.
  EasyAuth must require authentication on every route except `/healthz` and
  replace/strip any client-supplied `X-MS-CLIENT-PRINCIPAL` header before
  forwarding to the gateway. Authentication alone is not authorization.
- The signed-in Azure CLI user's object ID is included in the allowlist by
  default. Additional approved IDs, including invited-and-accepted guests,
  can be added with `-AllowedUserIds`; they do not replace the signed-in user.
- The gateway's managed identity receives the `Api.Invoke` application role
  on the API registration. The API's managed identity receives
  **Cognitive Services OpenAI User** on the Foundry account. The existing
  developer role is not removed or replaced.
- The API validates v2 access tokens for its registration's client GUID and
  accepts calls only from the configured gateway managed identity. The
  configured Foundry endpoint is
  `https://examinator-46954202.cognitiveservices.azure.com/`, deployment
  `exam-classifier`. Foundry itself is not imported or redeployed by default.

The deployment expects the app containers to listen on 8080, expose an
unauthenticated minimal `GET /healthz`, and implement the agreed API and
gateway runtime contracts. This infrastructure does not modify or validate
those application behaviors.

## Prerequisites and permissions

1. Use Windows PowerShell 5.1 or later and Azure CLI with the Container Apps
   extension/commands available. Sign in with `az login`; set the requested
   subscription with `-SubscriptionId` (scripts do not change the CLI default).
   Install Bicep with `az bicep install` if needed.
2. The caller needs permissions to create resource groups/resources, assign
   `AcrPull` and Foundry roles, and create/update Entra app registrations and
   service principals. Contributor alone cannot create Azure role assignments;
   use a suitably scoped Role Based Access Control Administrator (or
   equivalent). Entra operations use Microsoft Graph application and
   service-principal create/update/list APIs plus app-role assignment
   read/write APIs. Depending on tenant policy and whether delegated or
   application permissions are used, grant/admin-consent the required Graph
   permissions (commonly `Application.ReadWrite.All`,
   `AppRoleAssignment.ReadWrite.All`, and the corresponding application/
   service-principal read permission), or use an appropriately privileged
   Application Administrator/Cloud Application Administrator. Tenant policy
   can restrict these operations.
3. Ensure `Microsoft.App`, `Microsoft.ContainerRegistry`,
   `Microsoft.ManagedIdentity`, and `Microsoft.CognitiveServices` providers
   are registered. Provider registration is a separate authorized operation.
4. An Azure Container Registry Basic SKU is billed even when idle. Consumption
   apps at zero replicas reduce compute charges but can cold-start; the first
   request may wait for image pull/startup. Scale is capped at one replica,
   so this is not a high-availability or throughput configuration. Foundry
   inference is separately billed. No budget amount or notification address
   was provided, so budget alerts are not provisioned by these scripts.
   Configure a Cost Management budget alert separately after deployment;
   notifications do not impose a hard spending cap.
5. The build uses remote ACR Build and requires both `server/Api/Dockerfile`
   and `server/Web/Dockerfile`. The repository root is the ACR build context so
   Dockerfiles may copy solution/project files outside their own directories.
   No local Docker engine is required. The ACR-build caller needs permission
   to build/push images.

## Preflight and deployment

First provide the subscription and tenant GUIDs. The current Azure CLI
signed-in user's object ID is included automatically. This is a read-only/local
preflight; it does not invite users:

```powershell
.\deployment\deploy.ps1 `
    -SubscriptionId '<subscription-guid>' `
    -TenantId '<tenant-guid>'
```

To add extra approved users or guest object IDs, pass them as an array; the
signed-in user's ID is still included:

```powershell
.\deployment\deploy.ps1 `
    -SubscriptionId '<subscription-guid>' `
    -TenantId '<tenant-guid>' `
    -AllowedUserIds @('<additional-user-or-guest-object-guid>')
```

After reviewing the target, registration permissions, user allowlist, and
costs, provision the base infrastructure, Entra registrations/role grant,
Foundry role assignment, ACR images, and apps:

```powershell
.\deployment\deploy.ps1 `
    -SubscriptionId '<subscription-guid>' `
    -TenantId '<tenant-guid>' `
    -AllowedUserIds @('<additional-user-or-guest-object-guid>') `
    -Deploy
```

The default flow uses the existing Foundry account and deployment and only
adds the API managed identity's inference role if missing. It does not import
or alter the Foundry account/model. `-ProvisionFoundry` explicitly invokes
the separate `deployment/foundry` provisioning template and is **not**
recommended for the already-working account unless account reconciliation
and model provisioning are intended:

```powershell
# Add -ProvisionFoundry only after reviewing deployment/foundry/README.md.
.\deployment\deploy.ps1 `
    -SubscriptionId '<subscription-guid>' `
    -TenantId '<tenant-guid>' `
    -AllowedUserIds @('<additional-user-or-guest-object-guid>') `
    -ProvisionFoundry -Deploy
```

The default ACR name is `examacr` plus the first eight subscription GUID
characters; override with `-RegistryName` when that global name is taken.
`-ImageTag` defaults to a UTC timestamp. To deploy images already built and
pushed to this ACR, pass the same tag and `-SkipBuild`:

```powershell
.\deployment\deploy.ps1 `
    -SubscriptionId '<subscription-guid>' `
    -TenantId '<tenant-guid>' `
    -ImageTag 'release-20261110' -SkipBuild -Deploy
```

Individual phases are available for controlled rollout:

```powershell
# Each command below is read-only unless explicitly passed its -Deploy/-Build switch.
.\deployment\infra.ps1 -SubscriptionId '<subscription-guid>'
.\deployment\infra.ps1 -SubscriptionId '<subscription-guid>' -Deploy

.\deployment\build.ps1 -SubscriptionId '<subscription-guid>' -ImageTag 'release-1'
.\deployment\build.ps1 -SubscriptionId '<subscription-guid>' -ImageTag 'release-1' -Build

# After base infrastructure, images, and registrations are in place:
.\deployment\identity.ps1 -TenantId '<tenant-guid>' -SubscriptionId '<subscription-guid>'
.\deployment\apps.ps1 -SubscriptionId '<subscription-guid>' -TenantId '<tenant-guid>' `
    -ApiAppId '<api-client-guid>' -WebAppId '<web-client-guid>' `
    -ApiImage 'examacr....azurecr.io/examinator-api:release-1' `
    -WebImage 'examacr....azurecr.io/examinator-web:release-1' `
    -AllowedUserIds @('<approved-user-object-guid>') -Deploy
```

`identity.ps1` can be run with `-GatewayPrincipalId '<web-uami-principal-guid>'`
to provision the gateway's app-role assignment after the web managed identity
exists. `deploy.ps1` supplies it automatically. Before Graph writes,
`identity.ps1` compares the target subscription tenant with the Azure CLI's
current Graph tenant and fails if they differ; explicitly sign in/select the
correct tenant before retrying.

## Entra registrations, users, and secrets

The scripts create or reconcile two **single-tenant** app registrations with
these persistent names:

- `Examinator API`, identifier URI `api://<API-client-guid>`, v2 access
  tokens, and the `Api.Invoke` **application** role.
- `Examinator Web`, configured by EasyAuth for the deployed web URL's
  `/.auth/login/aad/callback` redirect.

The resolver uses the API identifier URI and the application names. It fails
on ambiguous duplicate registrations instead of selecting one. The web app
registration gets a client secret only when neither the Key Vault secret nor
a reusable ACA value exists. The value is stored in Key Vault; ACA stores an
identity-authenticated reference, not the value. Reruns reuse it and do not generate a
replacement. If credential creation succeeds but secret persistence fails,
the next run fails closed rather than rotating blindly. An administrator
must reconcile that named Entra credential and Key Vault secret securely before
retrying. Plan an explicit, coordinated secret rotation before its two-year
expiry; do not enable PowerShell transcript/debug logging during credential
provisioning and do not put secrets in command history, persistent files, tickets, or
logs.

Application-registration provisioning is fully implemented in
`identity.ps1`: it creates/reconciles both registrations, configures v2 API
tokens and `Api.Invoke`, creates the API service principal, and grants that
role to the web managed identity. On application deployment, the web
registration receives its HTTPS callback and EasyAuth client secret. Azure
permissions listed above are required; registration creation is not a
portal-only/manual step.

No email address was supplied, so guest invitation is deliberately separate.
If an approved person is outside the tenant, an administrator must invite that
person through the organization's normal Entra B2B process and the guest must
accept the invitation. Then add the resulting tenant object ID to the approved
allowlist. An email address alone is not an allowlist identity.

## Deployment safety and operations

- `-Deploy` is the explicit opt-in for Azure writes; `-WhatIf` can be used as
  an additional PowerShell `ShouldProcess` guard. Build and deploy are separate
  phases. A failed operation can leave successfully created resources behind;
  inspect Azure deployment history and rerun after resolving the cause.
- Bicep uses incremental mode and does not delete unrelated resources.
  Container images use immutable tags per deployment invocation; retain prior
  tags for rollback.
- The web EasyAuth token store is disabled. User authorization is expected at
  the gateway; the API accepts only its configured gateway identity. Do not
  expose API ingress publicly or bypass the gateway.
- App container startup and first requests can fail while ACR role assignments
  or Entra app-role grants propagate. Rerun/verify after propagation; do not
  work around this by enabling ACR admin credentials or public API ingress.
- ACA's default HTTP request timeout is retained. The API operation is
  intended for a bounded request/response; long-running work needs a separate
  design rather than increasing ingress timeouts implicitly.
- No request prompts, exam data, tokens, or credential values are logged by
  these deployment scripts. Application logging and Foundry data handling
  must still be reviewed separately.

## Validation

From the repository root:

```powershell
az bicep build --file deployment/main.bicep --stdout
Get-ChildItem deployment -Filter '*.ps1' -Recurse | ForEach-Object {
    $tokens = $null; $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$tokens, [ref]$errors) | Out-Null
    if ($errors.Count) { throw "$($_.FullName): $($errors -join '; ')" }
}
```

The scripts have intentionally not been run against Azure as part of this
change. Azure writes, role assignment propagation, image builds, and runtime
health require an authorized operator and a deployment environment.
