---
paths:
  - "deployment/**"
  - "infra/**"
  - ".github/workflows/**"
  - ".dockerignore"
  - "server/**/Dockerfile"
---

- Read the `deployment` skill first; it holds the pipeline, the changed-files-to-images table and the ACR rules.
- Changing the changed-files-to-images logic in `ci-cd.yml` means updating the table in `deployment/README.md` in the same change.
- Scripts target Windows PowerShell 5.1 (`#Requires -Version 5.1`; no `?:`, `??`, `&&`). Only `deployment/azd/` hooks use `pwsh`.
- Keep `build.ps1` on `--no-logs --only-show-errors` without `--no-wait`; `deployment/tests/build.tests.ps1` enforces it.
- Offline script tests: `powershell -NoProfile -File .\deployment\tests\<file>.tests.ps1` (mapping in the `verify-change` skill).
- Never run deploy or build commands against Azure without the user's confirmation.
