# Development instructions

## Pitfalls

- A self-contained single-file .NET exe loads its assemblies from its own path on first use. Updating it by renaming
  the running exe (`.old-xxxx`) and copying the new one to the same path breaks the processes already running (empty
  error, `FileNotFoundException`). Up to 1.3.x, `SelfImage.Pin` loaded the whole assembly graph at startup to avoid it;
  since 1.4.0 the engine is Native AOT (one native image mapped by Windows, nothing loaded later from its file), so
  renaming a running engine aside is safe (`UpdateWhileRunningTests` proves it on the release build). Preloading the
  reference graph is not enough: the 1.3.4 dispatcher still crashed after its replacement on `System.Private.Uri`,
  loaded later by System.Text.Json through a type forward (08/11/2026). Any Nexus C# product updated by renaming its
  running exe should be Native AOT: AI sessions keep their MCP server running for hours while updates replace the exe.
- The installation lives in the engine (`Installer.cs`, `SystemSetup.cs`); `install.ps1` only compiles from source when
  needed and starts `NexusFree.exe --install`. The elevated test of `SetupTests` registers a real task with its own
  name and folders: it runs on the (elevated) CI runners; locally, run the built test exe through `free`
  (`NexusFree.Tests.exe -class NexusFree.Tests.SetupTests`), never against the installed NexusFree.
- Native AOT needs the C++ linker, found through `vswhere`: outside a developer prompt, add
  `%ProgramFiles(x86)%\Microsoft Visual Studio\Installer` to `PATH` (install.ps1, release.yml and the tests do).
