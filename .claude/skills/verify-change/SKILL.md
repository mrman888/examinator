---
name: verify-change
description: Run the right checks after code changes in the Examinator repo. Maps changed paths (git status/diff) to frontend (npm run build, npm test), backend (dotnet build/test) and deployment PowerShell tests (deployment/tests/*.tests.ps1, scripts/local/tests.ps1). Use after finishing any edit under src/, server/, deployment/, infra/ or scripts/, before reporting a task as done, or when asked "does it still work", "run the tests" or "verify".
---

# Verify a change

Decide what to run from what changed, not from habit. Run only the checks that apply, but run all of them for the parts you touched.

## 1. Find the changed paths

```powershell
git status --short
git diff --name-only HEAD
```

The working tree may contain unrelated edits from the user; judge by the files relevant to your task, and say so if other areas are dirty.

## 2. Map paths to checks

| Changed | Run |
| --- | --- |
| `src/`, `Data/`, `angular.json`, `package*.json`, `tsconfig*.json` | frontend |
| `server/Api/`, `server/Web/`, `server/Tests/`, `server/*.slnx` | backend |
| `deployment/*.ps1`, `deployment/azd/`, `deployment/*.bicep`, `infra/` | deployment tests (table below) |
| `scripts/local/` | `powershell -NoProfile -File .\scripts\local\tests.ps1` |
| `.github/workflows/ci-cd.yml` | nothing locally; CI runs frontend and backend and deploys on merge. Re-read the change-detection logic you edited. |
| Markdown, `.claude/` only | nothing to run |

### Frontend

```powershell
npm run build
npm test
npm test -- src\app\services\ai-test-client.spec.ts   # one spec while iterating
```

`npm test` is `ng test --watch=false` (Vitest through the Angular builder). Run both build and test: the build catches template and type errors that specs may not. Run `npm ci` first only if `node_modules` is missing or `package-lock.json` changed (Node 26, see `.nvmrc`).

### Backend

```powershell
dotnet build .\server\Examinator.slnx
dotnet test .\server\Tests\Tests.csproj
dotnet test .\server\Tests\Tests.csproj --filter "FullyQualifiedName~GatewayTests"
```

CI runs `dotnet test server/Tests/Tests.csproj --configuration Release`. Test classes are named after areas (`GatewayTests`, `ApiTests`, `FoundryClientTests`, `Import*Tests`, `PdfExtractionTests`, `QuestionAlignmentTests`); filter by the one matching your change, then run the full suite once.

### Deployment script tests

Windows PowerShell 5.1 syntax, run from the repo root: `powershell -NoProfile -File .\deployment\tests\<file>`. They mock `az` and never touch Azure.

| Test file | Covers |
| --- | --- |
| `build.tests.ps1` | `build.ps1`: `--no-logs`, no `--no-wait`, repo-root context, Api before Web, `-Images`, preflight and `-WhatIf` build nothing |
| `imports.tests.ps1` | `apps.ps1` `Set-KeyVaultValue`: secret goes via temp file, never in args or errors, file deleted |
| `azd-tags.tests.ps1` | `apps.ps1` `Set-AzdServiceTag` |
| `foundry-access.tests.ps1` | `ensure-foundry-access.ps1` |
| `import-capacity.tests.ps1` | `ensure-import-capacity.ps1` |
| `monitoring.tests.ps1` | Compiles `main.bicep`, `imports.bicep`, `infra/main.bicep` with `az bicep build` (needs Azure CLI with Bicep installed, no login) and checks telemetry wiring in `apps.ps1`, `deploy.ps1`, `azd/update-import-jobs.ps1` |

Changes to `deploy.ps1`, `identity.ps1`, `infra.ps1` or `azd/*.ps1` have no dedicated test: run `monitoring.tests.ps1` plus the parse check from `deployment/README.md` ("Validation" section), and say that the rest is unverified without Azure. When in doubt, run all six.

## Rules

- Tests must never call Foundry or any Azure service, so they spend no tokens. Use fakes and mocked handlers (see `FoundryClientTests`, `ImportProcessorTests`). If a new test needs a real call, stop and redesign it.
- There is no linter or formatter. Do not run or add one, and do not reformat untouched code.
- Do not run deployments or ACR builds as "verification"; see the `deployment` skill.
- Report faithfully: state which commands ran, the pass/fail counts, and quote the first real error of any failure. Never summarise a failing or skipped check as passing, never retry silently until green, and list checks you could not run (for example `az` missing) with the reason.
