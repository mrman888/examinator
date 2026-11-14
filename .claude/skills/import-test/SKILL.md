---
name: import-test
description: Test the Examinator exam import flow (upload or https link, HAVO-only validation, definitions approval, worker, review, publish, retry/delete) locally or end to end. Covers test documents (alleexamens.nl HAVO Biologie, full correctievoorschrift), the dedicated test storage rule, how local runs and the import worker are started, and which tests are mocked and free versus real runs that call Foundry and cost money. Use when working on or debugging anything under server/Api/Import*, the import dialog in src/app/components/, or when asked to try, smoke-test or reproduce an import.
---

# Testing the exam import

Read the import sections of `server/README.md` first; this skill covers the traps.

## Cost and safety first

A real import calls Azure Foundry (vision model, up to 64 calls per import by default, `Import__MaximumModelCalls`) and costs money; an interrupted call is never retried automatically. **Ask the user before starting any real import or retry**, say which documents you will use, and prefer the mocked tests when they answer the question. No offline import mode exists.

Mocked, free checks (no Foundry, no Azure): `dotnet test .\server\Tests\Tests.csproj --filter "FullyQualifiedName~Import"` plus `PdfExtractionTests` and `QuestionAlignmentTests`. They use fakes such as `IImportModel`/`IPdfRenderer` in `ImportProcessorTests`. Frontend: `npm test -- src\app\components\exam-import.component.spec.ts` and the `import-*.spec.ts` files. For a new behaviour, extend these first.

Local runs must use the **dedicated test storage account**, never production storage or its catalog. The account name is the AppHost parameter `storage-account` in user secrets (`dotnet user-secrets list --project .\server\AppHost`), and the AppHost refuses production-style names; confirm with the user which account it points at if unsure, and never copy account names or hostnames into files you commit.

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

`.\start.ps1` runs the Aspire AppHost (`server\AppHost`): Api, Web, Angular on 4200 and the dashboard. Details are in "Local run with the Aspire AppHost" in `server\README.md`.

- Imports are on only when the AppHost setting `Examinator:Imports` is `true`. Then the user secrets must also hold `Parameters:storage-account` and `Parameters:foundry-import-deployment`, `pdftoppm` (Poppler) must be on PATH, and `az login` needs data permissions on the test account and Foundry access.
- With imports on, the `import-worker` resource starts by itself and polls, so it processes anything already queued in the test storage and spends tokens. Use `.\start.ps1 -NoImports` when the task doesn't need a real import.
- `import-reconcile` (restores lost dispatch) is started by hand from the dashboard.

Where the worker fits: the Api only stores and enqueues. The worker is the same Api binary started with `--import-worker` (one dispatch then exit; the AppHost adds `--poll` to repeat it) and `--reconcile`. With no worker running, an upload stays `queued` ("Wachten op de achtergrondtaak"). The polling worker stops at the first failure and never retries; read the error in the import's "Verwerkingsstappen" and retry explicitly. Settings are read at startup, so restart the AppHost after changing them. Stop it with Ctrl+C; do not kill unrelated processes.

## Reporting

State whether the run was mocked or real, which documents, what it cost in model calls (progress shows `modelCalls`/budget), the final state, and anything left in the test storage that the user may want to delete.
