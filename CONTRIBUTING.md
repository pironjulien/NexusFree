# Contributing

## Environment

- Windows 10 or 11, the .NET SDK pinned by `global.json`, PowerShell 7;
- no elevation is needed by the tests; an administrator account only to install the SYSTEM dispatcher for real
  (`install.ps1`).

## Rules

1. Open an issue or a pull request against `main`. A change is accepted only when `./test.ps1` passes: the CI
   (`.github/workflows/tests.yml`) runs that same script on every push and pull request.
2. **Every new feature and every bug fix comes with its test** in `tests/NexusFree.Tests` (xUnit): MCP protocol,
   dispatcher, outbox, journal, `get.ps1`. A fix starts with a test that fails without it.
3. Keep the safeguards: the dispatcher runs only requests whose file belongs to the authorized account, the access
   rights of `daemon/setup-system.ps1` stay by SID, and the log records a request's id and exit code, never its
   command. Read [SECURITY.md](SECURITY.md) before changing them.
4. Never commit a secret, a token, a log or a build output: builds go to `%USERPROFILE%\.nexus\build\nexusfree`,
   outside the repository.
5. Every PowerShell script of the repository must parse (`test.ps1` checks them all). Comments, commit messages and
   `CHANGELOG.md` entries (Keep a Changelog, under `[Unreleased]`) are written in English.

## Checks

```powershell
./test.ps1    # every PowerShell script parses, then dotnet test (xUnit); no elevation needed
git diff --check
```

## Version

One version everywhere: `src/NexusFree/NexusFree.csproj` sets it; `src/NexusFree/McpServer.cs` and the plugin
manifests follow, and `CHANGELOG.md` has its `[X.Y.Z]` section. The `Release` workflow (`.github/workflows/release.yml`)
checks them, runs `test.ps1`, builds, attests and publishes a development build on every push to `main`.
