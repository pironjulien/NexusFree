# Changelog

All notable changes to the **NexusFree** product are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Fixed
- Tests: the error-mode check in a child process expects only the two bits always inherited
  (`SEM_FAILCRITICALERRORS | SEM_NOOPENFILEERRORBOX`): the runtime briefly sets exactly those two while it loads a
  native library on another thread, so a child started in that window lacked `SEM_NOGPFAULTERRORBOX` and the check
  failed now and then in CI. The full mode is still checked in the test process right after it is set.

## [1.4.0] - 2026-11-08

### Changed
- The engine is compiled ahead of time (Native AOT): one native `NexusFree.exe` per architecture, no .NET to install,
  no JIT at start-up. Measured on the same PC (x64), 1.3.4 single file → 1.4.0: file 70.2 MB → 4.1 MB; start-up
  (`--version`) 44 ms → 13 ms; an idle MCP server 44.3 MB → 15.5 MB of working set and 13.2 MB → 4.8 MB of private
  bytes; a simple elevated command end to end (`--exec "exit 0"`, request, SYSTEM dispatcher, answer) 413 ms → 292 ms
  (medians). The trimming and AOT analyzers run in every build with warnings as errors.
- The Task Scheduler API goes through source-generated COM interop instead of the runtime COM binder (`dynamic`), and
  every COM object is released when its call ends.
- The installation lives in the engine. `NexusFree.exe --install [--apply]` copies the engine for this account, sets up
  the SYSTEM side and writes Antigravity's plugin (embedded in the engine); `NexusFree.exe --setup-system --user-sid
  <SID>`, run elevated, installs the dispatcher, sets the spool ACLs and registers the SYSTEM task through the Task
  Scheduler API, with the same rights, task definition and checks as before. `install.ps1` only compiles the engine
  from source when needed and starts it. The UAC prompt of a first install now names `NexusFree.exe`.
- Renaming a running engine aside during an update is safe without preloading anything: a native image never reads
  its file again (tested on the release build).

### Removed
- `daemon/setup-system.ps1`, replaced by `NexusFree.exe --setup-system`, and the assembly preloading of 1.3.3
  (`SelfImage`). Updating from 1.3.x: that preloading missed `System.Private.Uri`, so the 1.3.x dispatcher can still
  fail once after the setup replaced it; the installer then sends the setup once more, to the new dispatcher, as it
  already did for 1.3.2 and earlier.

### Fixed
- Tests: no test can show a Windows error box. The test process sets the error mode (no critical-error, crash or
  missing-file box) when it loads, and every process it starts inherits it; a test checks it in a child process.
- Tests: the update-while-running test no longer fails when a scanner briefly opens the engine it has just written
  and started. Only that transient sharing hold is waited out (at most 10 s, as `get.ps1` does); a longer hold fails
  and names the processes holding the file (Restart Manager).

## [1.3.4] - 2026-11-07

### Fixed
- Updating from 1.3.2 or earlier no longer fails with "NexusFree did not answer within 5 minutes". The SYSTEM setup
  runs inside the installed dispatcher and replaces its file; a dispatcher without the 1.3.3 fix then failed on its
  own replaced file and never answered, so the update stopped before Antigravity's plugin. `install.ps1` now reads the
  dispatcher's log: when the request ended without an answer and the dispatcher installed is already this version, it
  sends the setup (idempotent) once more, to the new dispatcher, and carries on.

## [1.3.3] - 2026-11-07

### Fixed
- An AI session opened before an update keeps working after it. The update renames the running engine aside and
  copies the new one to the same path; a self-contained single-file engine loads each assembly from its own path the
  first time it needs it, so it then read the new version's bytes and every later `free` or `free_status` call of that
  session failed with an empty error ("Tool free failed: "), `whoami` included, while sessions started after the update
  worked. The MCP server and the SYSTEM dispatcher now load every assembly they reference as soon as they start, while
  the file is still theirs (about 8 ms). Should a call still fail after its file was replaced, the error says that
  NexusFree was updated while the session was open and to reconnect the nexus-free MCP server.
- `get.ps1` no longer fails now and then with "The package does not start on this PC" under Windows PowerShell 5.1:
  it read only the first line of `NexusFree.exe --version` through `Select-Object -First 1`, which stops the program
  early there, so its exit code read -1. The whole output is read, then its first line.
- Sessions opened before this update still run an engine without that fix: reconnect their nexus-free server (or open
  a new session) once after updating to 1.3.3.

### Added
- A test that starts the self-contained single-file build, replaces its file the way install.ps1 does, and checks that
  its tools still answer.

## [1.3.2] - 2026-11-07

### Fixed
- `get.ps1` no longer fails with "being used by another process" when a scanner briefly opens the freshly
  extracted executable. The package is staged next to the releases folder and never run there: its version comes
  from the executable's version resource, the staged folder is renamed to `releases\<version>`, and only then is
  `--version` run in place (a package that does not answer its own version is removed again). A rename held by such
  an external, transient scan (sharing, lock or access violation) is waited out for at most 10 s; any other error
  still fails the installation. Older versions are removed only after that check.

### Added
- `get.ps1` test variables `NEXUS_FREE_SOURCE` and `NEXUS_FREE_HOME` (a local package in another folder,
  install.ps1 never run), and its tests under PowerShell 7 and Windows PowerShell 5.1, a 2 s scanner hold included.

## [1.3.1] - 2026-11-07

### Fixed
- `get.ps1` works under Windows PowerShell started from PowerShell 7 or from a .NET process that inherited its
  module folders (NexusSystem's maintenance run by hand): it keeps only Windows PowerShell's own module folders, so
  `Get-FileHash` loads again, as in the other Nexus products.
- Reading an answer no longer locks it: the engine opens it shared for writing and deleting, so a dispatcher still
  writing it in place never fails with a sharing violation (it is read again until it parses).

## [1.3.0] - 2026-11-07

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

[Unreleased]: https://github.com/pironjulien/NexusFree/compare/v1.3.4...HEAD
[1.3.4]: https://github.com/pironjulien/NexusFree/compare/v1.3.3...v1.3.4
[1.3.3]: https://github.com/pironjulien/NexusFree/compare/v1.3.2...v1.3.3
[1.3.2]: https://github.com/pironjulien/NexusFree/compare/v1.3.1...v1.3.2
[1.3.1]: https://github.com/pironjulien/NexusFree/compare/v1.3.0...v1.3.1
[1.3.0]: https://github.com/pironjulien/NexusFree/compare/v1.2.5...v1.3.0
[1.2.5]: https://github.com/pironjulien/NexusFree/compare/v1.2.4...v1.2.5
[1.2.4]: https://github.com/pironjulien/NexusFree/compare/v1.2.3...v1.2.4
[1.2.3]: https://github.com/pironjulien/NexusFree/compare/v1.2.2...v1.2.3
[1.2.2]: https://github.com/pironjulien/NexusFree/compare/v1.2.1...v1.2.2
[1.2.1]: https://github.com/pironjulien/NexusFree/compare/v1.2.0...v1.2.1
[1.2.0]: https://github.com/pironjulien/NexusFree/compare/v1.1.0...v1.2.0
[1.1.0]: https://github.com/pironjulien/NexusFree/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/pironjulien/NexusFree/releases/tag/v1.0.0
