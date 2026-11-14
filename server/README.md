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

Each PDF (`exam`, `answers`) is supplied either as a file part or as an https
link in the form field `examUrl` / `answersUrl`; exactly one source per field is
required and the optional `context` screenshot stays file-only. The Api downloads
links itself (`PdfDownloader`) and the bytes then pass the same validation as
uploads (`%PDF-` header, 20 MiB limit, HAVO-only metadata) before any storage or
Foundry call. Link hardening: https only, no credentials, default port only, at
most 3 redirects (each re-validated), a 30 s deadline, a streamed body aborted
beyond 20 MiB, and a PDF content type or `%PDF-` magic bytes. Name resolution is
checked in `SocketsHttpHandler.ConnectCallback`, so a hostname resolving to any
loopback, private, link-local (including 169.254.169.254), CGNAT, multicast,
reserved or IPv6 ULA/link-local address is refused at connect time, which also
covers DNS rebinding. Failures are Dutch problem details with status 400/413.

Original PDFs are kept at `exam-documents/{id}/original.pdf`. Each stored exam or
answers original also gets a hash index row in the `ExamCatalog` table
(PartitionKey `sha256`, RowKey the lowercase hex digest; document id, role,
import id and first-seen time). The digest covers the final bytes, for uploads and
downloads alike. Before anything is stored or sent to Foundry, an upload whose
exam or answers digest is already indexed is rejected with HTTP 409 problem
details (`title`: "Dit document is al eerder verwerkt.") and a `duplicates` array
of `{ importId, state, year, timevak, role, documentId }` for the earlier
import(s); no second copy is stored. The UI offers to open the earlier import or
to reprocess it.

`POST /api/imports/{id}/reprocess` starts a NEW import job from the stored
originals of import `{id}` without re-uploading them. It takes multipart fields
`metadata` (the same JSON as an upload, HAVO and at least one topic required),
`contextText` and an optional `context` screenshot; PDF parts are rejected. The
new job reuses the existing document ids and sitting, goes through the normal
extraction, definition approval and review flow, and leaves the original import
untouched. Publication still cannot silently replace an earlier publication of
the same sitting. The Web gateway forwards the route with the usual same-origin
and CSRF checks.

`GET /api/imports` always lists imports newest first by `createdAt`, with the
import id (ordinal) as tiebreaker, at most 100 entries. The dialog keeps exactly
that order: it sorts a loaded list once, replaces an updated or reopened record
in place and removes a deleted one, so selecting, saving or a live refresh never
moves it.

`DELETE /api/imports/{id}` (answered with 204; the Web gateway applies the usual
same-origin and CSRF checks) removes an import from the list. It is refused with
a Dutch 409 for published imports, for imports in `extracting`, `aligning` or
`tagging` and for imports whose worker lock is currently leased. Queued, failed,
cancelled, `awaitingDefinitions` and `review` imports can be deleted. The record
blob is deleted conditionally on its current ETag, so a concurrent worker write
makes the delete fail with 409. After that the job index row, the work blob, a
leftover result manifest, the lock blob, an unpublished sitting reservation (only
when it points to this import) and any queued dispatch for the id are removed.
A stored document (and its `documents` row) is deleted together with its `sha256`
hash row only when no other remaining import references that document id;
reprocessed imports share ids. When a document survives, the document row and
the hash row are repointed to a surviving import. A worker that still receives a
deleted id skips it and drops the message.

### Review summary

Once an import reaches `review` (and for published imports) the detail view shows
a "Samenvatting", computed in the browser from the record: per topic, in the
import's topic order, the number of questions with links to them (topics without
questions read "Geen vragen gevonden"), the questions that match none of the
topics, and the total of distinct questions with a topic. A question with several
topics counts under each of them. The tagger has no separate "not relevant"
outcome: a question with no (known) `topicIds` is not relevant. The summary
follows unsaved topic edits in the review. Logic: `import-summary-logic.ts`.

### Live progress

The leased worker writes `exam-imports/progress/{id}.json` (`ImportProgress`:
attempt, monotonic sequence, phase, step/total/unit, model calls and budget,
Dutch message, `updatedAt`, `heartbeatAt`, plus the step log described below).
Phases are `starting`, `extracting`,
`definitions`, `rendering`, `inventory`, `aligning`, `tagging`, `throttled` (a
Foundry HTTP 429 wait) and `finishing`. Phase changes are written immediately,
steps within a phase at most once per 2 seconds (flushed on the next phase and at
the end), and the 25-second lease renewal adds a heartbeat. Progress writes are
unconditional, never touch the import record and never fail an import; storage
errors are only logged. The blob is kept until the import is deleted. The record
stays authoritative: progress is shown only while the record is `queued`,
`extracting`, `aligning` or `tagging` and only when its attempt equals the
record's `processingAttempts`.

`GET /api/imports/events` is one server-sent-event stream for the whole list. It
starts with `retry: 3000`, then sends a `progress` snapshot for every processing
import, and afterwards only changes (record ETag, progress sequence or stale flip),
found by conditional reads every 1.5 s; newly queued imports are discovered from
the job index every 5 s. Events:

| Event | Payload |
| --- | --- |
| `progress` | `{ importId, state, revision, phase, step, total, unit, modelCalls, modelCallBudget, message, updatedAt, heartbeatAt, serverTime, stale, timing }`; `timing` holds only the current run (see below) |
| `settled` | `{ importId, state, revision }` once an import leaves the processing states (`state` is `deleted` when it disappeared) |
| `ping` | `{ serverTime }` every 15 s |

`stale` is computed on the server: no heartbeat for 75 s while processing, or a
queued import that has not started for 3 minutes ("De achtergrondtaak is nog niet
gestart"; otherwise queued imports report "Wachten op de achtergrondtaak (kan tot
een minuut duren)", since the worker job polls the queue every 30 s). A stream
closes after 180 s, below the Container Apps ingress timeout, and EventSource
reconnects. Each Api replica accepts four concurrent streams (429 beyond that);
the route answers 503 when imports are disabled. The Web gateway allows the route
for GET only behind the usual EasyAuth allowlist (no CSRF header, because
EventSource cannot send one), uses a separate HttpClient without timeout, flushes
every read and cancels the upstream request when the browser disconnects.

The import dialog opens one stream while at least one listed import processes and
the tab is visible; it closes on a hidden tab, when the panel closes and when
nothing processes. A `progress` event with a newer revision, or a `settled`
event, reloads only that record, in place. After a closed stream (401, 403, 404,
503, redirect), three errors without a message within 30 s, or 45 s without any
event, the dialog falls back to reloading the list every 5 seconds and offers
"Opnieuw live verbinden". An open stream keeps an Api replica awake, which costs
a little while the dialog shows processing imports. Locally, `ng serve` proxies
the stream like any other `/api` call.

### Step log and processing time

The progress blob also keeps a step log, so the processing steps can be reviewed
after an import has finished:

- `attemptStartedAt`, `completedAt` and `outcome` describe the current run;
  `run` numbers the runs of the import (1, 2, ...), also across a retry that
  resets the record's `processingAttempts`.
- `phases` is the current run's step list: `{ phase, startedAt, endedAt,
  message, step, total, modelCalls, outcome, error }`. Every phase change closes
  the open entry (`outcome` `ok`) and opens the next one; the entry keeps the
  latest message and step/total of that step (for example "1 vraag gevonden" or
  "32 van 32 antwoorden gekoppeld") and the model calls started in it. A Foundry
  rate-limit wait is a step of its own (`phase`/`outcome` `throttled`);
  afterwards the interrupted phase opens a new entry. At most 100 entries per run.
- The worker completes a run when it reaches `awaitingDefinitions`, `review` or
  `failed` (and on cancellation): `completedAt` is set and the open step closes as
  `ok`, `failed` (with the short Dutch error, at most 300 characters) or
  `cancelled`.
- When a new run starts (the resume after `awaitingDefinitions`, an automatic
  re-dispatch, or a retry after `failed`), the stored run moves into
  `earlierRuns` (`{ number, attempt, startedAt, endedAt, outcome, phases }`). A
  run that never completed because the worker stopped ends at its last heartbeat
  as `interrupted`. The last 20 earlier runs are kept; when older runs are
  dropped, or the earlier progress predates the step log, `earlierRunsUnknown`
  is set.

All step-log fields are optional, so progress blobs from before the step log
still deserialize; such imports have no `timing` and the UI shows "onbekend"
and "Geen stapgeschiedenis beschikbaar voor deze import".

The Api derives `timing` from the progress blob: `{ processingStartedAt,
completedAt, outcome, processingMilliseconds, earlierProcessingMilliseconds,
earlierRunsUnknown, runs }`. `processingMilliseconds` sums the run durations, so
time between runs (waiting on the user at `awaitingDefinitions`, a failed import
waiting for a retry, the queue) is excluded; it is null until the latest run has
completed or when earlier runs are unknown. `earlierProcessingMilliseconds` lets
the browser add the running run for a live total. `GET /api/imports/{id}` and
the action responses carry all runs; `GET /api/imports` carries the summary
without runs (one extra progress read per listed import, eight in parallel);
the SSE `progress` event carries only the current run, which the browser merges
into the record's runs by run number. An unreadable progress blob leaves
`timing` null and never fails the request.

Every import record also has `updatedAt`, set by `ImportStorage` on every record
write; records written before it existed report the blob's last-modified time.
`timing` is response-only and never stored in the record.

The detail view shows "Verwerkingsstappen" for every import in every state: per
run a heading "Poging 2 · 14-11-2026 15:02 · 6 min 12 s · Geslaagd" and a table
with step, start time, duration, details, model calls and result (a failed step
is highlighted with its error), newest run expanded and earlier runs collapsed.
It shows "Totale verwerkingstijd" and, when it differs, "Doorlooptijd sinds
upload" (upload until `completedAt`). While processing, the running step and
the total tick every second; the timer stops when the import settles, on a
hidden tab and on destroy, and nothing is announced per second. List rows show
"Bijgewerkt: 14-11-2026 15:02" and "Verwerkt in 6 min 12 s" (processing rows
show the elapsed time next to their live progress). Logic: `import-timing.ts`.

### Review and publication

Optional chapter text/screenshot produces proposed definitions and pauses at
`awaitingDefinitions`. Explicit approval resumes tagging. Every extracted
question is confirmed in `review` (`needsReview` false), including its answer
and page links. "Alles goedkeuren" confirms every unconfirmed question with its
proposed values in one revision-checked draft save (stale revision: 409); questions
without answer pages or with invalid page ranges or topics stay unconfirmed and are
listed. Single questions can still be (un)confirmed. Saving a draft is not publication,
and publishing remains a separate explicit action. Publishing commits an
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
Throttled requests (HTTP 429) are rejected before inference and are not billed:
the model client waits for `Retry-After` (at most 60 seconds) up to three times,
then fails the import with an explicit capacity error. Other provider HTTP
errors fail immediately with their status code. One inventory request carries
every rendered page image, so the import deployment needs far more than 10K
tokens per minute; `deployment/deploy.ps1` raises it to at least 150 units.
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
the AppHost import settings below, Azure CLI data permissions, and `pdftoppm`
on PATH. Docker installs Poppler. Hosted workers use the API's
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

## Local run with the Aspire AppHost

`server/AppHost` is a .NET Aspire AppHost for local development only. One
command starts the Api, the Web gateway and the Angular dev server, and shows
their logs, traces and metrics in the Aspire dashboard. It refuses to start
outside the Development environment or in publish mode; Azure deployment stays
with `deployment/deploy.ps1`.

Requirements: .NET 10 SDK, Node 26 with `npm ci` done, and Azure CLI signed in
(`az login`) as an identity with inference access on the Foundry account.
Docker and the Aspire CLI are not needed. Trust the ASP.NET Core development
certificate once with `dotnet dev-certs https --trust`: the dashboard and its
telemetry endpoint use HTTPS, and without a trusted certificate the apps cannot
send telemetry to it.

Settings are AppHost parameters kept in user secrets, outside the repository.
Set them once:

```powershell
dotnet user-secrets set 'Parameters:foundry-endpoint' 'https://<foundry-account>.cognitiveservices.azure.com/' --project .\server\AppHost
dotnet user-secrets set 'Parameters:foundry-deployment' '<prompt-deployment>' --project .\server\AppHost
```

Start everything from the repository root:

```powershell
.\start.ps1
```

The script reloads PATH (so a terminal opened before Poppler was installed
still finds `pdftoppm`), runs `npm ci` when `node_modules` is missing and then
runs `dotnet run --project .\server\AppHost`. `.\start.ps1 -NoImports` starts
without imports and without the import worker.

| Resource | What runs |
| --- | --- |
| `api` | `server/Api` on a port chosen by Aspire |
| `web` | `server/Web` on a port chosen by Aspire, forwarding to `api` |
| `frontend` | `npm run start` (`ng serve`) on `http://127.0.0.1:4200`, proxying `/api` to `web` |

Open `http://127.0.0.1:4200`. The terminal prints the dashboard login URL
(`https://localhost:17250/login?t=...`). Stop everything with Ctrl+C. Settings
are read at startup, so restart the AppHost after changing them.

Api and Web run in Development with `Development__AllowAnonymous=true`. That
switch is honored only in Development and only for loopback connections; it is
not a deployment setting. Every process, including Aspire's own endpoints,
listens on loopback only. Do not expose them to the internet. Local inference
uses `AzureCliCredential`; hosted inference uses managed identity.

Optional AppHost switches go after `--` or in user secrets:

| Setting | Default | Purpose |
| --- | --- | --- |
| `Examinator:Imports` | `false` | Enable exam imports, the import worker and reconciliation |
| `Examinator:Frontend` | `true` | Start the Angular dev server |
| `Examinator:FrontendPort` | `4200` | Port of the Angular dev server |

For example `dotnet run --project .\server\AppHost -- --Examinator:FrontendPort=4300`.

The Angular proxy (`server/dev-proxy.mjs`) reads the gateway address from
`EXAMINATOR_WEB_URL`, which the AppHost sets, and only accepts an `http://`
address on `localhost`, `127.0.0.1` or `[::1]`. A plain `npm start` without
that variable serves the UI with a warning and does not proxy `/api`.

### Imports

Real uploads and Foundry calls use Azure and cost money; there is no offline
import mode. Use the dedicated test storage account, never a production
catalog: the AppHost refuses account names that match the production pattern
(`exam` followed by 13 characters). It does not provision Azure resources or
grant permissions. Poppler's `pdftoppm` must be on PATH.

```powershell
dotnet user-secrets set 'Parameters:foundry-import-deployment' '<vision-import-deployment>' --project .\server\AppHost
dotnet user-secrets set 'Parameters:storage-account' '<test-storage-account>' --project .\server\AppHost
dotnet user-secrets set 'Examinator:Imports' 'true' --project .\server\AppHost
```

With imports enabled the AppHost sets `Import__Enabled`, the three storage
endpoints of that account and `Foundry__ImportDeployment` on the Api, and adds
two more resources that run the same Api project:

| Resource | What runs |
| --- | --- |
| `import-worker` | `--import-worker --poll`: takes queued imports until it is stopped, checking the queue every 5 seconds |
| `import-reconcile` | `--reconcile`: restores lost dispatch once; start it from the dashboard when needed |

The worker stops at the first failed import and does not retry it. Review the
error, retry the import explicitly in the UI when appropriate, and start
`import-worker` again from the dashboard. To run without a worker, stop the
resource in the dashboard.

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

`server/AppHost.Tests` starts the real Api and Web processes through the
AppHost, without the frontend, imports or Azure, and checks both health
endpoints and the gateway-to-Api route. It runs in CI only.
