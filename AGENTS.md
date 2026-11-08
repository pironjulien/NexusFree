# Development instructions

## Pitfalls

- A self-contained single-file .NET exe loads its assemblies from its own path on first use. Updating it by renaming
  the running exe (`.old-xxxx`) and copying the new one to the same path breaks the processes already running (empty
  error, `FileNotFoundException`). `SelfImage.Pin` loads the whole assembly graph at startup (about 8 ms) to avoid it;
  the move from 1.3.2 or older fails once on the SYSTEM side, then passes at the next maintenance. Any Nexus C#
  single-file product updated by renaming must preload its assemblies the same way: AI sessions keep their MCP server
  running for hours while updates replace the exe.
