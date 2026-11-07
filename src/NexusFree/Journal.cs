using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NexusFree;

public static partial class Journal
{
    private static readonly object Gate = new();

    private static readonly HashSet<string> KnownExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "dism", "sfc", "whoami", "chkdsk", "schtasks", "net", "reg", "sc", "cmd",
        "taskkill", "tasklist", "robocopy", "bcdedit", "wbadmin", "attrib", "takeown",
        "icacls", "cipher", "diskpart", "gpupdate", "gpresult", "powershell", "pwsh"
    };

    [GeneratedRegex(@"[«""“'’(\[:{]")]
    private static partial Regex CutRegex();

    public static string IdentifyAgent(string? client)
    {
        var configured = McpServer.ConfiguredAgent;
        if (!string.IsNullOrWhiteSpace(configured)) return NormalizeAgent(configured);

        var env = Environment.GetEnvironmentVariable("NEXUS_AGENT");
        if (!string.IsNullOrWhiteSpace(env)) return NormalizeAgent(env);

        var name = (client ?? "").ToLowerInvariant();
        if (name.Contains("codex")) return "codex";
        if (name.Contains("claude")) return "claude";
        if (name.Contains("antigravity") || name.Contains("gemini") || name.Contains("jetski")) return "antigravity";

        return "antigravity";
    }

    public static string NormalizeAgent(string agent)
    {
        var lower = agent.Trim().ToLowerInvariant();
        return lower switch
        {
            "claude" or "claude-code" or "claudecode" => "claude",
            "codex" or "chatgpt" => "codex",
            _ => "antigravity"
        };
    }

    public static string? ExtractApp(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var trimmed = command.Trim();
        if (trimmed.StartsWith("& ", StringComparison.Ordinal)) trimmed = trimmed[2..].TrimStart();

        string firstToken;
        if (trimmed.StartsWith('"'))
        {
            int nextQuote = trimmed.IndexOf('"', 1);
            firstToken = nextQuote > 1 ? trimmed[1..nextQuote] : trimmed[1..];
        }
        else if (trimmed.StartsWith('\''))
        {
            int nextQuote = trimmed.IndexOf('\'', 1);
            firstToken = nextQuote > 1 ? trimmed[1..nextQuote] : trimmed[1..];
        }
        else
        {
            int spaceIdx = trimmed.IndexOfAny([' ', '\t']);
            firstToken = spaceIdx > 0 ? trimmed[..spaceIdx] : trimmed;
        }

        firstToken = Path.GetFileName(firstToken).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(firstToken)) return "powershell.exe";

        if (firstToken.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return firstToken;
        }

        if (KnownExecutables.Contains(firstToken))
        {
            return firstToken + ".exe";
        }

        return "powershell.exe";
    }

    private static readonly char[] LineBreaks = ['\r', '\n'];

    public static string? CleanError(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries);
        var firstLine = parts.Length > 0 ? parts[0].Trim() : text.Trim();
        var debut = CutRegex().Split(firstLine, 2)[0].Trim(' ', '.', ',', ';', '-');
        return debut.Length == 0 ? "error" : (debut.Length <= 80 ? debut : debut[..80]);
    }

    public static void Write(string agent, string action, string? command, string? reqId, bool success, int exitCode, double ms, string? error = null)
    {
        if (Environment.GetEnvironmentVariable("NEXUS_JOURNAL") is "0" or "off" or "false" or "no") return;

        try
        {
            var now = DateTimeOffset.UtcNow;
            string? app = action == "free" ? ExtractApp(command) : null;
            string issue = success ? "✓" : "✗";
            string? errClean = success ? null : CleanError(error ?? (exitCode != 0 ? $"exit code {exitCode}" : null));

            var row = new JsonObject
            {
                ["v"] = 1,
                ["ts"] = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                ["agent"] = agent,
                ["outil"] = McpServer.ServerVersion,
                ["appel"] = reqId ?? Guid.NewGuid().ToString("N"),
                ["app"] = app,
                ["cadre"] = null,
                ["action"] = action,
                ["technique"] = action == "free" ? "elevation:system" : null,
                ["issue"] = issue,
                ["ms"] = (int)Math.Round(ms),
                ["premier_plan"] = false,
                ["erreur"] = errClean
            };

            string root = Environment.GetEnvironmentVariable("NEXUS_JOURNAL_HOME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nexus", "journal");
            string dir = Path.Combine(root, "free", agent);
            string file = Path.Combine(dir, $"{now:yyyy-MM}.jsonl");

            lock (Gate)
            {
                Directory.CreateDirectory(dir);
                File.AppendAllText(file, row.ToJsonString() + "\n", new UTF8Encoding(false));
            }
        }
        catch (Exception)
        {
            // Journaling must never block or throw
        }
    }
}
