# Examinator

Dutch HAVO biology exam trainer. Request flow: browser -> Web gateway (EasyAuth, allowlist, CSRF) -> private Api (JWT) -> Azure Foundry.
- `src/`: Angular 22 frontend (UI text and domain strings are Dutch; question ids look like `h25-t1-q12`)
- `server/Web`: .NET 10 gateway, serves the Angular build and forwards calls
- `server/Api`: .NET 10 Minimal API, calls Foundry, runs import jobs
- `deployment/`, `infra/`: Azure scripts and Bicep. `Data/`: source exam PDFs

## Hard rules
- Never commit secrets: the EasyAuth client secret, `ALLOWED_USER_IDS` values, tokens, tenant or subscription GUIDs, storage account names or Foundry hostnames.
- Production stays fail-closed. `Development__AllowAnonymous` works only in the Development environment and only for loopback connections; never use it as a deployment setting.
- Never expose the Api ingress publicly. Never trust `X-MS-CLIENT-PRINCIPAL` outside EasyAuth (only valid behind it, which strips caller-supplied copies).
- Tests must never call Foundry or Azure (they cost tokens); use fakes.
- Never modify the PDFs in `Data/` (possibly not licensed for redistribution). Adding new files is a separate step, see `add-exam`.
- ACR builds always use `--no-logs --only-show-errors` and never `--no-wait` (streaming logs crashes Azure CLI on Windows).
- Deployment scripts must run in Windows PowerShell 5.1.
- Anything that deploys, builds in ACR or otherwise writes to Azure, and every real exam import (calls Foundry, costs money), needs the user's confirmation first.

## Commands
- Frontend (Node 26, `.nvmrc`): `npm ci`, `npm run build`, `npm test`; one spec: `npm test -- src\app\services\ai-test-client.spec.ts`
- Backend: `dotnet build .\server\Examinator.slnx`, `dotnet test .\server\Tests\Tests.csproj`; one class: `--filter "FullyQualifiedName~GatewayTests"`
- Local run (Api, Web, Angular, import worker, Aspire dashboard): `.\start.ps1`; setup is in the local-run section of `server/README.md`.
- `server/AppHost.Tests` runs in CI only; do not add it to the local test command.
- No linter or formatter exists.

## Skills (`.claude/skills/`)
- `verify-change`: after any code change, to pick and run the right checks and report results faithfully.
- `deployment`: CI/CD, `deploy.ps1`, image tags, ACR builds, troubleshooting a deploy.
- `import-test`: testing or debugging the exam import flow, test documents, local worker.
- `add-exam`: adding a hardcoded exam sitting (PDFs, `angular.json` assets, question data).

Path-scoped conventions load from `.claude/rules/` when you touch `src/`, `server/` or `deployment/`.

## Working economically
- Answer questions, do lookups and make small edits yourself; do not spawn subagents for them. Delegate only broad work across many files.
- Never read big files whole; grep for the id or section, then read that range. Big ones: `src/app/services/exam-data.service.ts` (hardcoded question data), `src/app/components/exam-import.component.spec.ts`, `exam-import.component.ts`, `server/README.md`, `deployment/README.md`, `deployment/apps.ps1`, `server/Api/ImportStorage.cs`, `ImportProcessing.cs`, `ImportModel.cs`.
- While iterating run only the affected spec or filtered test; run the full frontend and backend suites once at the end.
- Keep replies short. Edit files to their end state; no change history in working files.
