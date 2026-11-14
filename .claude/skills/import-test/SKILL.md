---
name: import-test
description: Test the Examinator exam import flow (upload or https link, HAVO-only validation, definitions approval, worker, review, publish, retry/delete) locally or end to end. Covers test documents (alleexamens.nl HAVO Biologie, full correctievoorschrift), the dedicated test storage rule, how local runs and the import worker are started, and which tests are mocked and free versus real runs that call Foundry and cost money. Use when working on or debugging anything under server/Api/Import*, the import dialog in src/app/components/, or when asked to try, smoke-test or reproduce an import.
---

# Testing the exam import

Read the import sections of `server/README.md` first; this skill covers the traps.

## Cost and safety first

A real import calls Azure Foundry (vision model, up to 64 calls per import by default, `Import__MaximumModelCalls`) and costs money; an interrupted call is never retried automatically. **Ask the user before starting any real import or retry**, say which documents you will use, and prefer the mocked tests when they answer the question. No offline import mode exists.

Mocked, free checks (no Foundry, no Azure): `dotnet test .\server\Tests\Tests.csproj --filter "FullyQualifiedName~Import"` plus `PdfExtractionTests` and `QuestionAlignmentTests`. They use fakes such as `IImportModel`/`IPdfRenderer` in `ImportProcessorTests`. Frontend: `npm test -- src\app\components\exam-import.component.spec.ts` and the `import-*.spec.ts` files. For a new behaviour, extend these first.

Local runs must use the **dedicated test storage account**, never production storage or its catalog. The three `Storage__*Endpoint` values live in the git-ignored `scripts\local\settings.local.json` (template: `settings.example.json`); confirm with the user which account it points at if unsure, and never copy account names or hostnames into files you commit.

## Test documents

- Source: `https://www.alleexamens.nl/examens/HAVO/Biologie/`. Download into the session scratchpad or another temp folder, never into `Data/`.
- Exam = `opgaven`; answers = the **full `correctievoorschrift`**. Not the "aanvulling correctievoorschrift" (a one-page addendum, wrong document).
- Only HAVO Biologie, nothing before 2015 (product decision; the code only enforces year 2000-2100 and tijdvak 1-3, so you must pick sensible documents yourself).
- VWO must not be importable. Upload metadata must say `subjectId` `biology` and `level` `havo`, otherwise the Api answers 400 before anything is stored. On this checkout a VWO PDF wrongly labelled HAVO is only caught by the worker's identity check (inventory `Level` mismatch in `ImportProcessing.cs`), i.e. after a paid model call. Check whether a deterministic cover-page check exists in `ImportValidation.cs`/`ImportProcessing.cs` before claiming "rejected early"; if you add one, test that no Foundry call is made.
- Limits: 20 MiB per PDF, 100 pages, 200 questions, `%PDF-` header required.

## Flow and states

1. **Upload** `POST /api/imports` (multipart: `metadata` JSON, `exam`, `answers`; optional `context` screenshot). Each PDF is a file part or an https link in `examUrl`/`answersUrl` (exactly one per field; the Api downloads it with `PdfDownloader`: https only, no private/loopback addresses, 20 MiB, 3 redirects). Validation, then SHA-256 duplicate check (409 with `duplicates`; reprocess via `POST /api/imports/{id}/reprocess`), then storage, then queued dispatch. All before any Foundry call.
2. **Worker** picks the queued message: `queued` -> `extracting` -> (with context screenshot) `awaitingDefinitions` -> you approve with `confirm-definitions` -> `aligning` -> `tagging` -> `review`. Without a screenshot it goes straight on. Progress is in `exam-imports/progress/{id}.json`, streamed to the dialog over `GET /api/imports/events`.
3. **Review**: confirm every question (or "Alles goedkeuren"), save the draft (`PATCH .../draft`, revision-checked, 409 if stale), then `publish`, an explicit separate step. Only `published` imports show up in the trainer; the same sitting cannot be silently replaced.
4. Terminal or side states: `failed`, `cancelled`, `published`.

What you can do per state:

| Action | Allowed |
| --- | --- |
| `retry` | `failed` only; resets attempts, costs again, never automatic |
| `cancel` | any state except `published`/`cancelled` |
| `DELETE /api/imports/{id}` | `queued`, `failed`, `cancelled`, `awaitingDefinitions`, `review`; 409 for `published`, for `extracting`/`aligning`/`tagging`, or while the worker lock is leased |
| `reprocess` | starts a new job from stored originals (HAVO and at least one topic required) |

Request examples (needing a gateway JWT, never commit one): `server/Api/imports.http`. Through the browser, the Web gateway adds EasyAuth, the allowlist and CSRF (`X-Examinator-Request: 1`, exact `Origin`); locally `Development__AllowAnonymous` replaces login on loopback only.

## Starting a local run

The launcher is changing (`scripts\local\*.ps1` is being replaced by an Aspire AppHost on another branch). Do not assume either:

1. Check `Test-Path .\server\AppHost`.
2. Read the "Local test" section of `server\README.md` and follow the commands it gives for this checkout. On a checkout with `scripts\local` the README describes `start.ps1` (`-Worker` adds the polling worker; frontend on 4200, Web 5181, Api 5182) and separate `api.ps1`, `web.ps1`, `frontend.ps1`, `worker.ps1` (`-Watch` to poll), `reconcile.ps1`.
3. Whatever the launcher, imports need: `Import__Enabled=true`, the three `Storage__*Endpoint` values, `Foundry__ImportDeployment`, `az login` (data permissions on the test account and Foundry access), and `pdftoppm` (Poppler) on PATH for the worker.

Where the worker fits: the Api only stores and enqueues. The worker is the same Api binary started with `--import-worker` (one dispatch then exit) and `--reconcile` (restores lost dispatch). With no worker running, an upload stays `queued` ("Wachten op de achtergrondtaak"). The watcher stops on failure and never retries; read the error in the import's "Verwerkingsstappen" and retry explicitly. Settings are read at startup, so restart the affected process after changing them. Stop services with Ctrl+C; do not kill unrelated processes on 4200/5181/5182.

## Reporting

State whether the run was mocked or real, which documents, what it cost in model calls (progress shows `modelCalls`/budget), the final state, and anything left in the test storage that the user may want to delete.
