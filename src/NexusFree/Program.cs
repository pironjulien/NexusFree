using System;
using System.Linq;
using System.Threading.Tasks;

namespace NexusFree;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        WriteStatusFile();

        if (args.Contains("--version") || args.Contains("-v"))
        {
            Console.WriteLine($"NexusFree v{McpServer.ServerVersion}");
            return 0;
        }

        if (args.Contains("--status"))
        {
            var status = Engine.GetStatus();
            Console.WriteLine(status.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        int execIndex = Array.IndexOf(args, "--exec");
        if (execIndex >= 0 && execIndex + 1 < args.Length)
        {
            string cmd = args[execIndex + 1];
            var res = await Engine.ExecuteAsync(cmd);
            Console.WriteLine(res.Output);
            if (!string.IsNullOrWhiteSpace(res.Error))
            {
                Console.Error.WriteLine(res.Error);
            }
            return res.ExitCode;
        }

        // Mode par défaut ou avec --mcp : serveur MCP stdio
        await McpServer.RunStdioAsync();
        return 0;
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
