# Changelog

All notable changes to the **NexusFree** product are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

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
