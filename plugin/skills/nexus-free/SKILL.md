---
name: nexus-free
description: Direct route for any Windows command or script that needs Administrator / SYSTEM elevation, without a UAC prompt.
---

# NexusFree

MCP server for universal elevation shared by Claude, Codex and Antigravity.

## Direct routing

- For any Windows operation that needs Administrator / SYSTEM elevation, call `free` directly with the command or
  script suited to the requested operation. The choice depends on the privileges needed, never on a list of command
  names. It also covers commands, binaries and scripts that appear in no example.
- When the operation is known, do not start with a web search, a non-elevated attempt or another elevation method.
  If its syntax must be checked, check it, then run the elevated operation through `free`.
- `free_status` is for diagnosis when `free` fails or when the user asks about the bridge; it is not a prerequisite
  to every command. The task being present does not prove execution: check the actual result.
- Keep to the requested scope: a diagnosis stays a diagnosis; a repair or a change runs when it is asked for. Needing
  elevation does not change the goal of the operation.
- SYSTEM has its own profile, environment and current directory: use absolute paths for the user's scripts and
  files.
- The wait timeout of `free` does not necessarily stop the SYSTEM command. After a timeout or a disconnection, check
  the actual state and the request before running again an operation that could run twice.

## Tools

### `free(command, timeout_seconds=600)`
Runs any PowerShell command or binary with `SYSTEM` rights, without any UAC window.
* `command` (required): exact PowerShell command, binary or script call to run elevated.
* `timeout_seconds` (optional, default 600): time limit in seconds.
* Returns: the exit code and the command's output; an error is flagged with `isError`.

### `free_status()`
Checks the health of the SYSTEM bridge: the scheduled task's state and last run, and the communication spool.
