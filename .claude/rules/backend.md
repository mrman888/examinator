---
paths:
  - "server/**"
---

- C#: file-scoped namespaces, `Nullable` enabled, Minimal APIs.
- All configuration comes from environment variables (`Azure__*`, `Foundry__*`, `Gateway__*`, `Import__*`, `Storage__*`). `appsettings*.json` only holds logging config.
- Both images build from the repo root, run as non-root on port 8080 and must serve anonymous `GET /healthz`.
- After a change run `dotnet test .\server\Tests\Tests.csproj` (filter while iterating). Tests use fakes and mocked handlers; never real Foundry.
- Import behaviour, limits and states are documented in `server/README.md`; use the `import-test` skill to exercise them.
