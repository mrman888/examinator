# Containerized API, gateway and exam imports

## Import availability

Exam import is opt-in: set `Import__Enabled=true`, all three HTTPS storage
endpoints, and an evaluated `Foundry__ImportDeployment`. Without this setting,
import/catalog/document routes return explicit HTTP 503 problem details and
the bundled trainer remains usable. Invalid enabled configuration fails startup.
All approved website users can upload, review, cancel, retry and publish; there
is no separate importer role or per-owner isolation.

The biology canonical IDs are `biology-cells`, `biology-dna`,
`biology-cell-division`, `biology-cell-culture`, and `biology-organisms`.
These are independent of chapter/paragraph numbers; the frontend maps them to
legacy paragraphs 2.1–2.5. New topics use stable IDs, not paragraph numbers.
Chapters and paragraphs are optional grouping hints. Topic title/alias search
and `/api/questions?subjectId=biology&topicId=...` work across published exams.
Only HAVO biology is enabled now; other subjects require an explicit profile,
validation and model-quality expansion, not just a new dropdown value.

Upload stores originals before creating a durable queued job. The event-driven
worker extracts physical PDF pages and coordinates, renders every page with
Poppler, verifies document identity independently (including year and timevak
from the document headers), compares exam codes and
complete question inventories, and aligns correction entries by question number.
The strict-schema import model receives actual page images and text. Tagging
requires tested knowledge, not incidental keywords or chapter membership, and
rejects unknown question/topic/evidence IDs. It does not generate solutions.
Instructions and checkpoint provenance are in `server/Api/ImportModel.cs`.

Optional chapter text/screenshot produces proposed definitions and pauses at
`awaitingDefinitions`. Explicit approval resumes tagging. Every extracted
question then requires human confirmation in `review`, including its answer
and page links. Saving a draft is not publication. Publishing commits an
immutable application-level manifest and catalog pointer, becoming visible only after the conditional
published record commits; only published
imports enter the trainer. The same sitting cannot silently replace a previous
publication. Source PDFs remain authenticated even after publication.

If publication is interrupted before this commit, the draft stays unpublished.
Retry the same revision; if that staged version has subsequently been edited,
cancel and re-import. Cancellation releases unpublished sitting reservations
during reconciliation, while retaining source files.

Record updates use Blob ETags; stale changes return 409. Queue visibility and a
separate worker lease are renewed. A scheduled reconciler restores lost dispatch.
Paid calls are checkpointed before sending: an interrupted call requires explicit
retry, keeps the spent call count, and never triggers an automatic paid retry.
Worker starts also have a durable ten-attempt ceiling, so new dispatch hints
cannot reset crash/poison protection. Explicit retry resets that start counter
but not the reserved model-call budget. Successful question checkpoints are reused after restart. Model/prompt changes
require a new import rather than reusing incompatible results.

Limits are 20 MiB per PDF, 100 physical pages, 5 MiB for an
optional screenshot, 30 topics and 200 questions. The gateway caps a complete
multipart request at 46 MiB and review mutations at 4 MiB (complete question
inventories can exceed 256 KiB). Classification output is capped at 16 KiB per
question. Upload metadata remains capped at 256 KiB. These limits
include signature/PNG-JPEG dimension validation. PDF extraction has a word
limit; rendering has a three-minute timeout and 64 MiB output limit per PDF.
Model calls have bounded input/output and a default 64-call/import ceiling
(`Import__MaximumModelCalls`, 1–256). This is not a monetary or per-user Azure
spending cap. Imports exceeding the budget fail visibly. The import list
returns the latest 100 jobs using a reverse-time Table index. The catalog
is subject/sitting indexed and bounded at 1000 exams. The existing `/api/test`
limits and model deployment are unchanged.

Runtime configuration:

| Variable | Purpose |
| --- | --- |
| `Import__Enabled` | Explicitly enable imports |
| `Storage__BlobEndpoint` | `https://<account>.blob.core.windows.net/` |
| `Storage__QueueEndpoint` | `https://<account>.queue.core.windows.net/` |
| `Storage__TableEndpoint` | `https://<account>.table.core.windows.net/` |
| `Foundry__ImportDeployment` | Evaluated vision + strict-schema deployment; no automatic default |
| `Import__MaximumModelCalls` | Per-import count ceiling, default 64 |

The same API image runs `dotnet Api.dll --import-worker` for one queue message
and `dotnet Api.dll --reconcile` for recovery. Local worker development needs
`DOTNET_ENVIRONMENT=Development`, the settings above, Azure CLI data permissions,
and `pdftoppm` on PATH. Docker installs Poppler. Hosted workers use the API's
managed identity; the website has no storage data permissions.

Storage consists of three non-public Blob containers (`exam-documents`,
`exam-imports`, `exam-results`), Tables `ImportJobs`/`ExamCatalog`, and dispatch/
poison queues. Download URLs are protected `/api/documents/{id}`, never SAS or
public Blob URLs. Source hashes, independent inventories, approved definitions,
classification evidence, prompt/model version and call counts are retained.
Upload failures before job creation can leave unreachable originals; review
these manually before deletion. There is no automatic source-file deletion.

See [deployment instructions](../deployment/README.md) and
[HTTP examples](./Api/imports.http). Local tests use synthetic PDFs and mocked
storage/model/rendering; infrastructure compilation does not verify live Azure
permissions, queue scaling, image quality or paid inference. Evaluate the chosen
model on representative biology exams and perform a private deployment smoke
test before operational use.

**AI testen** still opens a short prompt form:

```text
Browser -> website container (.NET 10 static host + gateway)
        -> private .NET 10 Minimal API -> Azure Foundry
```

This separate endpoint is a connection test only, not the import workflow.
Each request is independent. Prompts are sent to Azure but
are not deliberately persisted or logged by either application.

## Local test without containers

### Ready-to-run Windows scripts

From the repository root, start the Api, Web gateway and Angular in separate
PowerShell windows with one command:

```powershell
.\scripts\local\start.ps1
```

The launcher checks ports and waits for readiness. Open `http://127.0.0.1:4200`.
It uses loopback-only Development authentication; do not expose these processes
to the internet. Dependencies must already be installed (`npm ci` on initial
setup). No Angular production build is needed for the dev server.

Individual commands are also available:

```powershell
.\scripts\local\api.ps1
.\scripts\local\web.ps1
.\scripts\local\frontend.ps1
```

For real import testing, first create the ignored configuration file:

```powershell
Copy-Item .\scripts\local\settings.example.json .\scripts\local\settings.local.json
```

Replace the placeholder endpoints/deployment names, set `Import__Enabled` to
`"true"`, sign in with `az login`, and ensure import storage is provisioned.
The scripts do not provision Azure resources, grant permissions or install
dependencies. Use separate test storage, not a production catalog. Keep tokens
and secrets out of this file: authentication uses Azure CLI.

With Poppler's `pdftoppm` on PATH, start the same services plus a polling worker:

```powershell
.\scripts\local\start.ps1 -Worker
```

Or run the worker separately after an upload/definition approval:

```powershell
.\scripts\local\worker.ps1          # One dispatch, then exit
.\scripts\local\worker.ps1 -Watch   # Poll until Ctrl+C; stops on failure
.\scripts\local\reconcile.ps1       # Restore lost dispatch, once
```

The watcher does not automatically retry failed imports. Review the error and
explicitly retry through the UI when appropriate. Real uploads and Foundry
calls use Azure and incur costs; no fully offline import mode is provided.

Stop each service with **Ctrl+C in its terminal**, then close that window.
If startup fails, existing terminals are left open for diagnosis and must be
stopped the same way. The launcher refuses ports already in use rather than
stopping unrelated processes. Settings are read at startup; restart the affected
service after changing them.

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
