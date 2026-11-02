# NexusFree

Sovereign privilege elevation bridge for the Trinity (Antigravity, Claude Code, OpenAI Codex).

## Overview

NexusFree allows AI coding agents running in isolated or headless desktop environments to execute required administrative commands (system file repairs, Windows service controls, hardware configuration, network adapters, `HKLM` registry modifications) under `SYSTEM` without failing on interactive UAC consent prompts.

## Architecture

- **Backend Daemon**: Privileged scheduled task `NexusFree` running as `SYSTEM` with SDDL `D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;FRFX;;;AU)` consuming requests from `%ProgramData%\NexusFree\inbox`.
- **MCP Server**: High-performance native C# (.NET 10) stdio server (`NexusFree.exe`) exposing tools `free` and `free_status`.
- **Plugins**: Shared plugin packaging for Google Antigravity, Claude Code, and OpenAI Codex.

## Tools

- `free(command, timeout_seconds=600)`: Runs any command elevated under SYSTEM and returns stdout, stderr, and exit code.
- `free_status()`: Verifies the health of the SYSTEM task and spool directories.

## Installation

```powershell
pwsh -NoProfile -File install.ps1 -Apply
```
