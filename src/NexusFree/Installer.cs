using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;

namespace NexusFree;

/// <summary>
/// The installation for the current Windows account (<c>NexusFree.exe --install [--apply]</c>, run by install.ps1 as
/// the account that will use NexusFree, never from an elevated prompt of another account: that account is the only
/// one authorized). It copies this engine to %USERPROFILE%\.nexus\nexus-free, then, with <c>--apply</c>, the SYSTEM side
/// (<see cref="SystemSetup"/>) and Antigravity's plugin. Claude Code and Codex get the plugin from the NexusSystem
/// catalog. The SYSTEM side needs one elevation: through NexusFree itself when it is already installed for this account
/// (an update), otherwise one UAC prompt.
/// </summary>
internal static class Installer
{
    /// <summary>How long the SYSTEM setup may take through an installed NexusFree.</summary>
    private static readonly TimeSpan SetupTimeout = TimeSpan.FromMinutes(5);

    /// <summary>After the dispatcher logged a request as ended, how long its answer may take before it is lost.</summary>
    internal static readonly TimeSpan LostAfter = TimeSpan.FromSeconds(15);

    internal static string UserEngineDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nexus", "nexus-free");

    internal static async Task<int> RunAsync(bool apply)
    {
        string sid = CurrentSid();
        string source = Environment.ProcessPath ?? throw new InvalidOperationException("The engine's own path is unknown.");
        string engine = Path.Combine(UserEngineDir, "NexusFree.exe");

        Console.WriteLine("=== NexusFree engine ===");
        Directory.CreateDirectory(UserEngineDir);
        // Engines of open AI sessions keep running the old file: renamed aside, removed by a later run.
        if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(engine), StringComparison.OrdinalIgnoreCase))
            SystemSetup.Replace(source, engine);
        Console.WriteLine($"Engine: {engine}");

        if (apply)
        {
            Console.WriteLine("=== SYSTEM side ===");
            await SetupSystemAsync(engine, sid);
            Console.WriteLine($"SYSTEM task '{Engine.TaskName}' configured for this account ({sid}).");

            Console.WriteLine("=== Antigravity plugin ===");
            Console.WriteLine($"Antigravity plugin: {WriteAntigravityPlugin(engine)}");

            JsonObject status = Engine.GetStatus();
            if (status["installed"]?.GetValue<bool>() != true)
                throw new InstallException($"NexusFree is not ready: {status["taskStatus"]}");
        }

        Console.WriteLine("NexusFree installed.");
        return 0;
    }

    private static string CurrentSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User!.Value;
    }

    /// <summary>The SYSTEM side: in a child process when this account is already elevated, else through the NexusFree
    /// installed for this account, else after one UAC prompt.</summary>
    private static async Task SetupSystemAsync(string engine, string sid)
    {
        if (SystemSetup.IsElevated())
        {
            int code = await RunSetupProcessAsync(engine, sid, elevate: false);
            if (code != 0) throw new InstallException($"SYSTEM setup failed ({code}).");
            return;
        }

        string command = SetupCommand(engine, sid);
        CommandResult? viaFree = await ThroughNexusFreeAsync(command, sid);
        if (viaFree is { ExitCode: Lost })
        {
            // The dispatcher ended without answering. When the dispatcher installed now is this version, the setup did
            // replace it, and an older one (1.3.2 or earlier) failed on its own replaced file: the setup (idempotent) is
            // sent once more, to the new dispatcher.
            string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NexusFree", "NexusFree.exe");
            if (ProductVersion(installed) != ProductVersion(engine)) throw new InstallException("SYSTEM setup through NexusFree ended without an answer.");
            Console.WriteLine("The previous dispatcher ended without answering; setup sent again to the new one.");
            viaFree = await ThroughNexusFreeAsync(command, sid);
            if (viaFree is { ExitCode: Lost }) throw new InstallException("SYSTEM setup through NexusFree ended without an answer.");
        }
        if (viaFree is not null)
        {
            if (!string.IsNullOrWhiteSpace(viaFree.Output)) Console.WriteLine(viaFree.Output.TrimEnd());
            if (!viaFree.Success) throw new InstallException($"SYSTEM setup through NexusFree failed: {viaFree.Error} {viaFree.Output}".TrimEnd());
            return;
        }

        int exit;
        try { exit = await RunSetupProcessAsync(engine, sid, elevate: true); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: the UAC prompt was declined
        {
            throw new InstallException("SYSTEM setup declined at the UAC prompt.");
        }
        if (exit != 0) throw new InstallException($"SYSTEM setup failed or was declined ({exit}).");
    }

    /// <summary>The PowerShell command the installed dispatcher runs: the engine's setup, its exit code passed on.
    /// Single quotes only (1.0's dispatcher lost double quotes on the way to its PowerShell).</summary>
    internal static string SetupCommand(string engine, string sid) =>
        $"& '{engine.Replace("'", "''", StringComparison.Ordinal)}' --setup-system --user-sid {new SecurityIdentifier(sid).Value}; exit $LASTEXITCODE";

    private static async Task<int> RunSetupProcessAsync(string engine, string sid, bool elevate)
    {
        var start = new ProcessStartInfo(engine, $"--setup-system --user-sid {new SecurityIdentifier(sid).Value}")
        {
            UseShellExecute = elevate,
            Verb = elevate ? "runas" : "",
        };
        using var process = Process.Start(start) ?? throw new InstallException($"Could not start {engine}.");
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private const int Lost = -2;

    /// <summary>
    /// The setup sent through the NexusFree already installed for this account (the same protocol as <c>free</c>), or
    /// null when there is none, or when it authorizes another account. A request that the dispatcher logged as ended
    /// but never answered comes back with exit code <see cref="Lost"/>.
    /// </summary>
    private static async Task<CommandResult?> ThroughNexusFreeAsync(string command, string sid)
    {
        if (!Directory.Exists(Engine.InboxDir)) return null;
        string config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NexusFree", "config.json");
        // 1.0 had no config.json and accepted every account: usable once, to install this version's restrictions.
        if (File.Exists(config) && Dispatcher.ReadAuthorizedSid(config) != sid) return null;

        string id = Engine.NewRequestId();
        using var lost = new CancellationTokenSource();
        Task watch = WatchForLostAnswerAsync(id, lost);
        CommandResult result = await Engine.ExecuteAsync(command, id, SetupTimeout, lost.Token);
        await lost.CancelAsync();
        await watch;
        return result.ExitCode == Engine.CancelledExitCode ? new CommandResult { Id = id, ExitCode = Lost } : result;
    }

    /// <summary>The dispatcher logs "Request &lt;id&gt; finished|failed" before writing the answer. A dispatcher of 1.3.2
    /// or earlier that this very setup replaced can fail right after and never answer: once that line is logged, the
    /// answer gets <see cref="LostAfter"/>, then the wait is cancelled.</summary>
    private static async Task WatchForLostAnswerAsync(string id, CancellationTokenSource lost)
    {
        try
        {
            while (!lost.IsCancellationRequested)
            {
                if (LogEnded(Engine.LogFile, id))
                {
                    await Task.Delay(LostAfter, lost.Token);
                    await lost.CancelAsync();
                    return;
                }
                await Task.Delay(300, lost.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>True when the last 64 KB of the dispatcher's log hold the end line of request <paramref name="id"/>.</summary>
    internal static bool LogEnded(string logFile, string id)
    {
        try
        {
            using var stream = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, stream.Length - 65536);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string tail = reader.ReadToEnd();
            return tail.Contains($"Request {id} ", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string ProductVersion(string file)
    {
        try { return (FileVersionInfo.GetVersionInfo(file).ProductVersion ?? "").Split('+')[0]; }
        catch (FileNotFoundException) { return ""; }
    }

    /// <summary>Antigravity's plugin in %USERPROFILE%\.gemini\config\plugins\nexus-free: manifest, logo, the guide in the
    /// Windows display language (French when Windows is in French, English otherwise) and the MCP server, the engine's
    /// absolute path for this account (Antigravity starts the command as written). Returns the plugin's folder.</summary>
    internal static string WriteAntigravityPlugin(string engine, string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string plugin = Path.Combine(home, ".gemini", "config", "plugins", "nexus-free");
        string skills = Directory.CreateDirectory(Path.Combine(plugin, "skills", "nexus-free")).FullName;
        CopyResource("antigravity/plugin.json", Path.Combine(plugin, "plugin.json"));
        CopyResource("antigravity/logo.png", Path.Combine(plugin, "logo.png"));
        bool french = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fr";
        CopyResource(french ? "antigravity/fr/SKILL.md" : "antigravity/SKILL.md", Path.Combine(skills, "SKILL.md"));
        var mcp = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["nexus-free"] = new JsonObject { ["command"] = engine, ["args"] = new JsonArray("--mcp") },
            },
        };
        File.WriteAllText(Path.Combine(plugin, "mcp_config.json"), mcp.ToJsonString(Engine.ReadableJson), new UTF8Encoding(false));
        return plugin;
    }

    private static void CopyResource(string name, string target)
    {
        using Stream source = typeof(Installer).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Resource {name} missing from the engine.");
        using var file = File.Create(target);
        source.CopyTo(file);
    }

    /// <summary>An installation step that failed, reported without a stack trace.</summary>
    internal sealed class InstallException(string message) : Exception(message);
}
