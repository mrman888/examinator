# Examinator

A Dutch HAVO biology exam trainer. There are three parts:
- Angular 22 frontend in `src/`
- .NET 10 Web gateway in `server/Web`, which serves the Angular build and forwards calls to the Api
- Private .NET Api in `server/Api`, which calls Azure Foundry

Request flow: browser → Web (EasyAuth, allowlist, CSRF) → Api (JWT) → Foundry.

## Commands
- Node 26 (`.nvmrc`): `npm ci`, `npm start`, `npm run build`, `npm test`
- Run one frontend spec: `npm test -- src\app\services\ai-test-client.spec.ts`
- Backend: `dotnet build .\server\Examinator.slnx`, `dotnet test .\server\Tests\Tests.csproj`
- Run one backend test: `dotnet test .\server\Tests\Tests.csproj --filter "FullyQualifiedName~GatewayTests"`
- Deployment script tests: `powershell -NoProfile -File .\deployment\tests\build.tests.ps1`
- Running locally takes two terminals: Api on 5182 and Web on 5181. `ng serve` proxies `/api` to 5181. The env vars are in `server/README.md`.
- After a change: frontend → `npm run build` and `npm test`; backend → `dotnet test`. There is no linter or formatter.

## Frontend conventions
@.github/instructions/angular.instructions.md
- UI text and domain strings are Dutch. Question ids look like `h25-t1-q12`.
- Question data is hardcoded in `src/app/services/exam-data.service.ts`. Pure logic goes in `exam-data-logic.ts`.
- A new PDF in `Data/` is only served after you add it to the `assets` globs in `angular.json`.
- Never modify the source PDFs in `Data/`. They may not be licensed for redistribution.

## Backend conventions
- Use file-scoped namespaces, `Nullable` enabled and Minimal APIs.
- All config comes from env vars (`Azure__*`, `Foundry__*`, `Gateway__*`). `appsettings` only holds logging config.
- Production must stay fail-closed. `Development__AllowAnonymous` works only in Development and only on loopback.
- Never expose the Api ingress publicly. Never trust `X-MS-CLIENT-PRINCIPAL` outside EasyAuth.
- Tests must not call Foundry, so they don't spend tokens.

## Deployment
- CI is `.github/workflows/ci-cd.yml`. It builds and tests on PRs and pushes. `main` only accepts PRs. Merging one deploys via `deployment/deploy.ps1`, and only the images whose files changed are rebuilt (`-Images Api,Web`). A manual run from `main` deploys everything. The mapping from changed files to images is in `deployment/README.md`.
- Scripts target Windows PowerShell 5.1.
- Run ACR builds with `--no-logs --only-show-errors` and never with `--no-wait`. Streaming the logs crashes Azure CLI on Windows because of Angular's Unicode output. Read the logs afterwards with `az acr task logs`.
- Both Docker images build from the repo root, run as non-root on port 8080, and need `/healthz`.

## Secrets
- Never commit the EasyAuth client secret, `ALLOWED_USER_IDS` values, tokens, or tenant or subscription GUIDs.
