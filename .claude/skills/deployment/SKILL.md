---
name: deployment
description: How Examinator is deployed to Azure and how to check or troubleshoot it. Covers the GitHub Actions workflow (PR checks, merge to main deploys, changed-files-to-images mapping, manual run), deployment/deploy.ps1 and build.ps1 (-Images, -SkipBuild, -ImageTag), ACR build rules (--no-logs, never --no-wait, az acr task logs), health checks and Windows PowerShell 5.1 constraints. Use for any question or change involving deployment/, ci-cd.yml, container images, ACR, rollback or a failed deploy. Deploying or building in Azure needs explicit user confirmation.
---

# Deployment

Sources of truth: `.github/workflows/ci-cd.yml`, `deployment/README.md`, `deployment/deploy.ps1`, `deployment/build.ps1`. Read the one you are about to rely on; this skill is the map.

## Confirm before acting

Deploying and building in Azure is outward-facing: it changes the shared development environment, spends money (ACR, Foundry, runner minutes) and can overwrite the running images. **Before running anything that deploys, builds in ACR, triggers the workflow, or writes to Azure, tell the user exactly what would run and wait for a yes.** Read-only work is fine without asking: reading the scripts, running `deploy.ps1` or `build.ps1` without `-Deploy`/`-Build` (local preflight; `deploy.ps1` still calls `az account show`), and the offline script tests (see `verify-change`). Never put subscription/tenant GUIDs, `ALLOWED_USER_IDS` or secrets in files or commits; they come from the GitHub `development` environment or from the user.

## Pipeline

- PRs to `main`: the `changes` job diffs against the merge base and decides what to test; `frontend` and `backend` jobs run `npm run build` + `npm test` and `dotnet test`. The ruleset requires `changes`, `frontend`, `backend`. Nothing deploys from a PR.
- Merge of a PR into `main`: the push run deploys (job `deploy`, Windows runner, environment `development`) via `deployment/deploy.ps1 -Images <changed images>`. A push to `main` that is not a merged PR is tested but never deployed. The base for "changed" is the last successfully deployed commit, so a failed deploy is rebuilt next time.
- Manual run (Actions, "CI and deployment", must be on `main`): tests, builds and deploys both images. With the `image_tag` input set, tests are skipped and `deploy.ps1 -SkipBuild` redeploys that existing tag (the rollback path).
- After deploy, the workflow polls the public web `/healthz` (12 attempts, 10 s apart). That proves the web container runs, not sign-in, the private Api or Foundry.

### Changed files to images

Authoritative: the `detect` step in the workflow; summarised in `deployment/README.md`.

| Changed | Tests | Image rebuilt |
| --- | --- | --- |
| `src/`, `Data/`, `package*.json`, `angular.json`, `tsconfig*.json`, `.nvmrc` | frontend | Web |
| `server/Web/` | backend | Web |
| `server/Api/` | backend | Api |
| other `server/` (e.g. `server/Tests/`) | backend | none |
| `.dockerignore` | none | Api and Web |
| `deployment/` except `tests/` and `*.md` | none | none, but deploys |
| `ci-cd.yml` | both | none, but deploys |

So a `Data/` or frontend-only change rebuilds only Web. An image that is not rebuilt keeps what its container app runs now (`Get-DeployedImage` in `deploy.ps1`). If you change the mapping, update the workflow and `deployment/README.md` together.

## Script behaviour

- `deploy.ps1` without `-Deploy` is a preflight that changes nothing in Azure. `-Deploy` is the opt-in for writes; `-WhatIf` is also supported.
- `-Images Api,Web` (default both) chooses what to build; it is ignored with `-SkipBuild`.
- `-ImageTag` defaults to a UTC timestamp; CI uses `gha-<run id>-<attempt>`. `-SkipBuild` requires `-ImageTag`, builds nothing and fails unless both `examinator-api:<tag>` and `examinator-web:<tag>` exist in ACR.
- Tags are immutable per run; keep old tags for rollback. A failed build does not roll back the previous image.
- `-EnableImports` needs `-ImportDeploymentName` and must be passed on every deploy (CI derives it from the `IMPORT_DEPLOYMENT_NAME` variable). The same Api image runs the import worker and reconciler jobs.
- Do not run `deploy.ps1` while AZD (`azd up`) is in use; both target the same resources.

## ACR build rules

Both images build remotely with the repo root as context (`server/Api/Dockerfile`, `server/Web/Dockerfile`); no local Docker is needed. Always use `--no-logs --only-show-errors`, never `--no-wait`: streaming logs crashes Azure CLI on Windows because of Angular's Unicode output, and without waiting a failed build goes unnoticed. `build.ps1` already does this and `build.tests.ps1` enforces it; keep it that way when editing. Read logs afterwards (reading is safe, but still Azure; mention it):

```powershell
az acr task list-runs --registry <registry> --output table
az acr task logs --registry <registry> --run-id <run id>
```

The registry name is `examacr` plus the first 8 characters of the subscription GUID unless overridden with `-RegistryName`.

## Image contract

Both images run as non-root (`USER $APP_UID`), listen on port 8080 and serve an anonymous `GET /healthz` (Api and Web `Program.cs`; EasyAuth excludes that path). Never expose Api ingress publicly; never trust `X-MS-CLIENT-PRINCIPAL` outside EasyAuth.

## Troubleshooting

1. Open the failed workflow run; the `changes` job log lists changed files and the image decision.
2. Build failure: fetch the ACR log with `az acr task logs`.
3. Deploy partially applied: inspect Azure resources before retrying. Fresh Entra role grants and ACR pull rights can take minutes to propagate, so first-start failures often fix themselves.
4. Scripts must stay valid in Windows PowerShell 5.1 (`#Requires -Version 5.1`; no `?:`, `??`, or `&&`). Only the AZD hooks in `deployment/azd/` need `pwsh`.
