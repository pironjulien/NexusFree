# NexusFree

Administrator and `SYSTEM` commands for your AI coding agents on Windows, without a UAC prompt each time: one MCP
server shared by Claude Code, Codex and Antigravity.

An agent working in the background cannot answer a UAC prompt, so `sfc`, `dism`, services, `HKLM` keys, drivers or
installers fail. NexusFree runs those commands under `SYSTEM` for **one Windows account, the one that installed it**,
after a single elevation at install time.

> **What this means.** Installing NexusFree turns that account into one that can act as `SYSTEM` without confirming
> each time: any program running under it, not only your AI agents, can use it. Install it only on a PC you own, for
> an account you already use as an administrator, and treat the agents' commands as you would your own. Other Windows
> accounts on the PC get no access. See [SECURITY.md](SECURITY.md).

## Tools

- `free(command, timeout_seconds=600)`: runs a PowerShell command line under `SYSTEM` and returns its exit code and
  output (stdout, then stderr).
- `free_status()`: whether the `SYSTEM` task and the spool are installed.

## How it works

```text
agent ─► NexusFree.exe (MCP, your account) ─► %ProgramData%\NexusFree\inbox\<id>.json
                                              schtasks /run NexusFree
          SYSTEM task "NexusFree" ─► Dispatch-NexusFree.ps1 ─► outbox\<id>.json ─► answer to the agent
```

- **Engine** (`src/NexusFree`): native C# (.NET 10) stdio MCP server, also `--status`, `--exec "<command>"` and
  `--version`.
- **Dispatcher** (`daemon/Dispatch-NexusFree.ps1`), installed in `%ProgramFiles%\NexusFree`: the scheduled task's
  only action, under `SYSTEM`. It moves each request out of the inbox, checks that its file belongs to the authorized
  account, runs it, and writes the answer to the outbox. Every command is logged in `logs\elevation.log`.
- **Access** (`daemon/setup-system.ps1`, by SID so any Windows language works):

  | Path | SYSTEM, Administrators | Authorized account | Other accounts |
  |---|---|---|---|
  | `%ProgramFiles%\NexusFree` (dispatcher, `config.json`) | full | read | read |
  | `%ProgramData%\NexusFree\inbox`, `outbox` | full | modify | none |
  | `%ProgramData%\NexusFree\logs` | full | read | none |
  | `%ProgramData%\NexusFree\staging` | full | none | none |
  | Scheduled task `NexusFree` | full | read, run | none |

## Install

Windows 10 or 11, as the account that will use it (not another account's elevated prompt):

```powershell
git clone https://github.com/pironjulien/NexusFree
powershell -NoProfile -ExecutionPolicy Bypass -File NexusFree\install.ps1 -Apply
```

From the source it builds the engine with the .NET 10 SDK; from a release package (`NexusFree-win-x64.zip`,
`NexusFree-win-arm64.zip`, checked against the release's `SHA256SUMS.txt`), the engine is already built. The `SYSTEM`
side needs one UAC prompt; later runs (updates) go through NexusFree itself. Claude Code and Codex get the plugin from
the [NexusSystem](https://github.com/pironjulien/NexusSystem) catalog; `install.ps1` declares it to Antigravity.

## Uninstall

From an elevated prompt:

```powershell
Unregister-ScheduledTask -TaskName NexusFree -Confirm:$false
Remove-Item -Recurse -Force "$env:ProgramFiles\NexusFree", "$env:ProgramData\NexusFree"
```

Then remove `%USERPROFILE%\.nexus\nexus-free` and the `nexus-free` plugin from your AI apps.

## Development

`test.ps1` runs the xUnit suite (MCP protocol, no elevation needed). Build outputs go to
`%USERPROFILE%\.nexus\build\nexusfree`. Changes are listed in [CHANGELOG.md](CHANGELOG.md).

## License

MIT
