# Changelog

All notable changes to the **NexusFree** product are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added
- French when Windows is in French, English otherwise: the MCP instructions, tool descriptions, results and errors
  follow the Windows display language. The plugin guide is in English; Antigravity's copy is installed in French on a
  French Windows (`fr/plugin/antigravity`).
- The outbox cleans itself: answers nobody collected (arrived after the client's timeout, or kept as unreadable) are
  removed after 24 hours, interrupted temporary answers after one hour.
- `free_status` reports the task's last run and its result.

### Changed
- The SYSTEM dispatcher is native: the scheduled task runs `NexusFree.exe --dispatch` (the engine, copied to
  `%ProgramFiles%\NexusFree`) instead of PowerShell and `Dispatch-NexusFree.ps1`, which is removed. Same protocol and
  checks: request moved to staging, refused unless its file belongs to the authorized account, Administrators or SYSTEM,
  run in PowerShell with UTF-8 output (lines that are not UTF-8 read in the OEM code page), answer written to `.tmp` then
  renamed, request id and exit code logged. A command waiting for input ends at once (its input is closed), and a
  background program that keeps the output open no longer holds the answer back beyond 5 seconds.
- Tests delete their own temporary folders directly instead of sending them to the Recycle Bin.
- CodeQL runs as a workflow file (`codeql.yml`, actions and C#) instead of the default setup, so OpenSSF Scorecard
  credits it (SAST).
- The engine reads and starts the SYSTEM task through the Task Scheduler API (COM `Schedule.Service`) instead of
  running `schtasks.exe` and returning its localized CSV: `taskStatus` is now a state name (Ready, Running...).
- Strict build: recommended .NET analyzers, code style enforced, warnings as errors (`.editorconfig` added).
- Release workflow on NexusSystem's shared workflows v1.6.54, pinned by commit SHA.

### Fixed
- Dependabot can restore the NuGet packages again: `Directory.Build.props` takes the user profile from .NET instead of
  `USERPROFILE`, which is unset on its Linux runner (the build folder fell to `/.nexus`, access denied).
- A SYSTEM answer can no longer be lost. The dispatcher writes `<id>.json.tmp` and renames it to `<id>.json` in the
  same folder (atomic on one volume), so the client never sees a half-written answer. The client watches only the
  final file with a `FileSystemWatcher` (polling kept as the fallback), parses it before deleting it, and reads it
  again on `JsonException` or `IOException` until the timeout; an answer that never parses is kept for diagnosis and
  named in the timeout error.

## [1.2.5] - 2026-11-04

### Changed
- Display name written NexusFree, without a space, like the repository and the NexusSystem catalog.

## [1.2.4] - 2026-11-04

### Changed
- Product icon now shows the julienpiron.fr medallion on a transparent background at every size (no dark square), with the knot badge at the bottom of the ring kept whole.
- Same `.gitattributes` as every Nexus repository (2026-11-04): LF in Git and on disk, CRLF only for `.cmd`/`.bat`; files stored with CRLF were renormalized once.

### Fixed
- The test host isolates all journal writes in a temporary home, including the `free_status` protocol test.
  Tests sharing global environment and static agent state run serially, so journal overrides cannot race or
  append synthetic calls to the real user's history. Existing journals are left intact. The test host sends only
  its validated temporary journal fixture to the Recycle Bin and keeps it if recycling is unavailable.

## [1.2.3] - 2026-11-03

### Changed
- Describe NexusFree consistently as the direct route for every Windows operation requiring Administrator / SYSTEM
  elevation, based on required privileges rather than a list of example command names. Remove task-specific wording
  from the skill and MCP instructions, and preserve the requested diagnostic, repair or modification scope.

## [1.2.2] - 2026-11-03

### Security
- Enable immutable GitHub releases for the repository. This release carries GitHub's release attestation in addition
  to the existing asset checksums and build provenance, so its published assets and tag cannot be replaced.

## [1.2.1] - 2026-11-03

### Changed
- The skill, MCP initialization instructions and execution tool description route known DISM/SFC and elevated
  Windows commands straight to `free`, without a redundant status call, failed unelevated attempt or web search.
- Document SYSTEM's separate environment and that a client timeout does not necessarily cancel the command.

### Security
- The privileged dispatcher's diagnostic log no longer stores command text or exception messages that may contain
  credentials or private arguments. Request IDs, exit codes and exception types remain available for diagnosis.

## [1.2.0] - 2026-11-03

### Added
- Standard Nexus unified logging: tool calls (`free`, `free_status`) are now logged to the central audit journal (`%USERPROFILE%\.nexus\journal\free\<agent>\<YYYY-MM>.jsonl`) following the schema v1 format with execution latency, exit code, agent attribution, and sanitized errors.
- Support for `--agent <agent>` argument on startup, environment variable `NEXUS_AGENT`, and client attribution on MCP initialize.
- Comprehensive journal test suite (`JournalTests.cs`).

### Fixed
- Accents in the output of native programs that ignore `chcp 65001` without a console (`dism`, `netsh`...) are no
  longer garbled: lines that are not valid UTF-8 are read in the system's OEM code page.
- Cmdlet progress bars no longer leak into the output as a CLIXML block.

## [1.1.0] - 2026-11-03

### Security
- Only the Windows account that ran `install.ps1` can use NexusFree. The spool in `%ProgramData%\NexusFree` used to
  inherit `%ProgramData%`'s ACL, which lets every local user create files: any account on the PC could get a command
  run as `SYSTEM` and read the answers. Its folders now have protected ACLs set by SID (`SYSTEM`, Administrators and
  the authorized account only), the scheduled task can be started by that account only (no longer by every
  authenticated user), and the dispatcher refuses a request whose file belongs to anyone else.
- The dispatcher no longer recreates a missing spool folder with inherited (open) rights.

### Fixed
- `setup-system.ps1` works on any Windows display language (SIDs instead of French account names) and from any
  folder (no fixed path to the author's PC).
- Scripts run on Windows PowerShell 5.1 as well as PowerShell 7.
- Commands containing double quotes reach PowerShell intact (sent encoded), and their output, native programs'
  included, comes back in UTF-8 (accents were garbled).

### Changed
- `install.ps1` installs a release package's prebuilt engine or builds it from the source; the `SYSTEM` side goes
  through the installed NexusFree for updates, otherwise one UAC prompt. It no longer edits Claude Desktop's chat
  configuration (Claude Code and Codex use the NexusSystem catalog plugin) nor rewrites the repository's plugin files.
- README and SECURITY.md describe the access model as it is.

### Added
- GitHub workflows: tests, release (Windows x64 and Arm64 packages, `get.ps1`, `SHA256SUMS.txt`, build provenance),
  security (gitleaks, zizmor) and OpenSSF Scorecard; Dependabot for actions and NuGet.

## [1.0.0] - 2026-11-02

### Added
- Native C# .NET 10 console engine and stdio MCP server (`NexusFree.exe`) exposing universal elevation tools:
  - `free`: executes any PowerShell or native command under `SYSTEM` without UAC prompts.
  - `free_status`: inspects scheduled task health and sas inbox/outbox status.
- Privileged SYSTEM backend dispatcher (`daemon\Dispatch-NexusFree.ps1`) executing under scheduled task `NexusFree`.
- Secure spool communication directory in `%ProgramData%\NexusFree` with strict ACLs (anti-TOCTOU, admin/SYSTEM staging).
- Universal installation script `install.ps1 -Apply` with auto-bootstrap and plugin registration.
- Multi-agent plugins for Google Antigravity, Claude Code, and OpenAI Codex.
- Offline unit and protocol test suite (`tests/NexusFree.Tests`).

[Unreleased]: https://github.com/pironjulien/NexusFree/compare/v1.2.5...HEAD
[1.2.5]: https://github.com/pironjulien/NexusFree/compare/v1.2.4...v1.2.5
[1.2.4]: https://github.com/pironjulien/NexusFree/compare/v1.2.3...v1.2.4
[1.2.3]: https://github.com/pironjulien/NexusFree/compare/v1.2.2...v1.2.3
[1.2.2]: https://github.com/pironjulien/NexusFree/compare/v1.2.1...v1.2.2
[1.2.1]: https://github.com/pironjulien/NexusFree/compare/v1.2.0...v1.2.1
[1.2.0]: https://github.com/pironjulien/NexusFree/compare/v1.1.0...v1.2.0
[1.1.0]: https://github.com/pironjulien/NexusFree/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/pironjulien/NexusFree/releases/tag/v1.0.0
