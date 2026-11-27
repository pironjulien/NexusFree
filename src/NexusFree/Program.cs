using System;
using System.Linq;
using System.Threading.Tasks;

namespace NexusFree;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // The SYSTEM task's action: answers the spool's requests. First, before anything writes to a user profile.
        if (args.Contains("--dispatch"))
        {
            return await Dispatcher.RunInstalledAsync();
        }

        // The privileged setup, run elevated by the installer (or by an administrator): nothing in a user profile.
        if (args.Contains("--setup-system"))
        {
            int sidIndex = Array.IndexOf(args, "--user-sid");
            return Report(() => SystemSetup.Run(sidIndex >= 0 && sidIndex + 1 < args.Length ? args[sidIndex + 1] : null));
        }

        // A person at a terminal: --help answers, and an unknown argument is refused instead of starting the MCP server,
        // which would wait on stdin with no output (`NexusFree.exe --help` looked frozen, 27/11/2026).
        if (args.Contains("--help") || args.Contains("-h") || args.Contains("/?"))
        {
            Console.WriteLine(Usage);
            return 0;
        }
        if (UnknownArgument(args) is { } unknown)
        {
            Console.Error.WriteLine($"NexusFree: unknown argument '{unknown}'.");
            Console.Error.WriteLine(Usage);
            return 2;
        }

        int agentIndex = Array.IndexOf(args, "--agent");
        if (agentIndex >= 0 && agentIndex + 1 < args.Length)
        {
            McpServer.ConfiguredAgent = args[agentIndex + 1];
            McpServer.CurrentAgent = Journal.NormalizeAgent(args[agentIndex + 1]);
        }

        WriteStatusFile();

        if (args.Contains("--version") || args.Contains("-v"))
        {
            Console.WriteLine($"NexusFree v{McpServer.ServerVersion}");
            return 0;
        }

        if (args.Contains("--install"))
        {
            try { return await Installer.RunAsync(apply: args.Contains("--apply")); }
            catch (Exception ex) when (ex is Installer.InstallException or IOException or UnauthorizedAccessException or InvalidOperationException
                or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
            {
                Console.Error.WriteLine($"NexusFree installation failed: {ex.Message}");
                return 1;
            }
        }

        if (args.Contains("--status"))
        {
            var status = Engine.GetStatus();
            Console.WriteLine(status.ToJsonString(Engine.ReadableJson));
            return 0;
        }

        int execIndex = Array.IndexOf(args, "--exec");
        if (execIndex >= 0 && execIndex + 1 < args.Length)
        {
            string cmd = args[execIndex + 1];
            // Ctrl+C stops the SYSTEM command too, instead of leaving it running without anyone to read it.
            using var stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                try { stop.Cancel(); } catch (ObjectDisposedException) { }
            };
            var res = await Engine.ExecuteAsync(cmd, stop: stop.Token);
            Console.WriteLine(res.Output);
            if (!string.IsNullOrWhiteSpace(res.Error))
            {
                Console.Error.WriteLine(res.Error);
            }
            return res.ExitCode;
        }

        int resultIndex = Array.IndexOf(args, "--result");
        if (resultIndex >= 0 && resultIndex + 1 < args.Length)
        {
            string requestId = args[resultIndex + 1];
            var status = Engine.ReadResult(requestId);
            var (text, isError) = McpServer.FormatStatus(requestId, status);
            (isError ? Console.Error : Console.Out).WriteLine(text.TrimEnd());
            return status.State switch
            {
                RequestState.Done => status.Result!.ExitCode,
                RequestState.Running or RequestState.Queued => StillActive,
                _ => 1,
            };
        }

        // Default, or with --mcp: stdio MCP server, which lives as long as its AI session, updates included (a native
        // image: renaming its file aside for an update never takes anything from a running engine).
        await McpServer.RunStdioAsync();
        return 0;
    }

    internal const string Usage = """
        Usage: NexusFree.exe [option]
          --mcp [--agent <name>]   stdio MCP server for an AI app (also the default with no argument)
          --exec "<command>"       run one command elevated (SYSTEM) and print its output (Ctrl+C stops it)
          --result <id>            result of a request whose wait timed out (exit code 259 while it still runs)
          --status                state of the elevation bridge, as JSON
          --install [--apply]      install for this account (--apply writes the AI apps' configuration)
          --version, -v            version
          --help, -h               this help
        """;

    private static readonly string[] Flags = ["--dispatch", "--setup-system", "--mcp", "--install", "--apply", "--status", "--version", "-v"];
    private static readonly string[] WithValue = ["--agent", "--exec", "--result", "--user-sid"];

    /// <summary>Windows' STILL_ACTIVE: the exit code of <c>--result</c> for a request that has not ended yet.</summary>
    internal const int StillActive = 259;

    /// <summary>The first argument the engine does not know (a value after --agent, --exec or --user-sid is not one), or null.</summary>
    internal static string? UnknownArgument(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (WithValue.Contains(args[i])) { i++; continue; }
            if (!Flags.Contains(args[i])) return args[i];
        }
        return null;
    }

    /// <summary>Runs a setup step; a failure is printed as one line and exit code 1, never as an unhandled crash.</summary>
    private static int Report(Func<int> step)
    {
        try { return step(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.Runtime.InteropServices.COMException or System.Security.Principal.IdentityNotMappedException)
        {
            Console.Error.WriteLine($"NexusFree SYSTEM setup failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    public static void WriteStatusFile()
    {
        try
        {
            var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dir = System.IO.Path.Combine(user, ".nexus", "status");
            System.IO.Directory.CreateDirectory(dir);
            var file = System.IO.Path.Combine(dir, "free.json");
            var json = $$"""
            {
              "product": "free",
              "version": "{{McpServer.ServerVersion}}",
              "latest": "{{McpServer.ServerVersion}}",
              "state": "idle",
              "checked_at": "{{DateTimeOffset.UtcNow:o}}",
              "message": "up to date"
            }
            """;
            System.IO.File.WriteAllText(file, json);
        }
        catch { }
    }
}
