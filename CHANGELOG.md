# Changelog

All notable changes to the **NexusFree** product are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

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
