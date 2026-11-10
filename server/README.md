# Containerized AI connection test

The existing exam viewer is unchanged. **AI testen** opens a short prompt form:

```text
Browser -> website container (.NET 10 static host + gateway)
        -> private .NET 10 Minimal API -> Azure Foundry
```

This is a connection test only: no import, extraction, categorization, database,
or chat history. Each request is independent. Prompts are sent to Azure but
are not deliberately persisted or logged by either application.

## Local test without containers

Requirements: .NET 10 SDK, Node/npm for the existing Angular project, Azure CLI
signed in as an identity with inference access on the Foundry account.

First build the UI and backend:

```powershell
npm run build
dotnet build .\server\Examinator.slnx
```

In one PowerShell terminal:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Development__AllowAnonymous = 'true'
$env:Foundry__Endpoint = 'https://examinator-46954202.cognitiveservices.azure.com/'
$env:Foundry__Deployment = 'exam-classifier'
dotnet run --project .\server\Api --no-launch-profile --urls http://127.0.0.1:5182
```

In another terminal, from the repository root:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Development__AllowAnonymous = 'true'
$env:Gateway__ApiBaseUrl = 'http://127.0.0.1:5182'
$env:ASPNETCORE_WEBROOT = Join-Path (Get-Location) 'dist\examinator\browser'
dotnet run --project .\server\Web --no-launch-profile --urls http://127.0.0.1:5181
```

Open `http://127.0.0.1:5181` and select **AI testen**. Stopping either command
stops that service. The anonymous switch is honored **only in Development** and
only accepts loopback connections. It is not a deployment setting.
Local inference uses `AzureCliCredential`; hosted inference uses managed identity.
Do not expose a Development process to the internet.

For Angular hot reload, run `npm start` as well. Its development proxy forwards
`/api` to the gateway on port 5181, so both backend commands must still be
running. Restart an already-running Angular dev server after changing the proxy
configuration. Otherwise use the gateway URL above; rebuild Angular and refresh
that page after frontend changes.

## Build containers

Run from the repository root with Docker and a Linux container engine:

```powershell
docker build -f .\server\Api\Dockerfile -t examinator-api .
docker build -f .\server\Web\Dockerfile -t examinator-web .
```

Both images listen on port 8080 as a non-root user. The web image compiles
Angular and contains the existing exam PDFs and PDF.js worker in `wwwroot`.
Do not publish the image publicly if its exam content is not licensed for
redistribution. Azure deployment uses a private registry and supports remote
builds when Docker is unavailable locally.

Production configuration is fail-closed. It requires the Azure identity and
gateway settings described below; neither image includes credentials.

## Production settings

API:

| Environment variable | Purpose |
| --- | --- |
| `Azure__TenantId` | Single Entra tenant |
| `Azure__ApiClientId` | API app registration client ID, token audience |
| `Azure__GatewayPrincipalId` | Website managed identity's **principal/object** ID |
| `Azure__ManagedIdentityClientId` | API user-assigned managed identity client ID |
| `Foundry__Endpoint` | Account inference endpoint, not project URL |
| `Foundry__Deployment` | Model deployment name |

Website:

| Environment variable | Purpose |
| --- | --- |
| `Azure__TenantId` | Same tenant |
| `Azure__ApiClientId` | API application ID; token scope is `api://<id>/.default` |
| `Azure__ManagedIdentityClientId` | Website managed identity client ID |
| `Gateway__ApiBaseUrl` | Private API HTTPS URL |
| `Gateway__PublicOrigin` | Exact public website origin, no trailing slash |
| `Gateway__AllowedUserIds__0`, `__1`, ... | Approved tenant user object IDs |

The infrastructure must enable Container Apps Easy Auth on the website, require
Microsoft sign-in for all paths except `/healthz`, and keep the API ingress
internal-only. The gateway's `X-MS-CLIENT-PRINCIPAL` header is trusted **only
behind that authentication layer**, which must strip/replace caller-supplied
identity headers. Do not deploy the website as an unprotected public container
or expose its container port directly. A valid header in isolation is not proof
of identity.

The API separately validates a signed Entra v2 JWT (issuer, tenant, audience,
expiration), the `Api.Invoke` app role, and the website identity's object ID.
No browser-held token or shared API key grants direct backend access.

Guest users accept an invitation into the tenant using their own Microsoft
account. Add the accepted guest's object ID **from this tenant**, not their
home tenant ID, to the website allowlist. Email addresses are not authorization
identifiers. The guest invitation/acceptance is separate from application
registration.

The website's prompt endpoint requires an exact matching `Origin`, JSON, and
`X-Examinator-Request: 1`. There is no cross-origin CORS policy. This prevents
cross-site cookie-authenticated POSTs; it does not prevent an approved user
from automating their own requests.

## Limits and failure behavior

- Prompt: 1-2000 non-whitespace characters, request body at most 32 KiB.
- Website: 10 requests/minute per running process; API: one concurrent inference,
  no queue. These are not persistent per-user quotas or hard Azure spending caps.
- Inference output: at most 2048 completion tokens (including model reasoning).
- API timeout: 90 seconds; gateway: 110 seconds; browser: 150 seconds.
- No automatic paid inference retries, avoiding accidental duplicate charges.
- API/provider failures are explicit HTTP errors; no fake successful replies.
- Minimal anonymous `/healthz` endpoints expose no model or tenant information.
- OpenAPI is available only in API Development at `/openapi/v1.json`.
- No inference request bodies, responses, tokens, or provider error bodies are
  added to application logs. Review Azure platform logging settings separately.

## Validation

```powershell
dotnet test .\server\Tests\Tests.csproj
npm test -- src\app\services\ai-test-client.spec.ts
```

Tests cover gateway allowlisting and CSRF, signed API token validation, allowed
gateway role/identity, rate limiting, invalid input, explicit provider failures,
and the Foundry HTTP contract without spending model tokens.
