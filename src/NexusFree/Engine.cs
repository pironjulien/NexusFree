using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace NexusFree;

public sealed class CommandResult
{
    public required string Id { get; init; }
    public bool Success { get; init; }
    public int ExitCode { get; init; }
    public string Output { get; init; } = "";
    public string Error { get; init; } = "";
    public string Started { get; init; } = "";
    public string Finished { get; init; } = "";
}

public sealed class Engine
{
    public const string TaskName = "NexusFree";
    public static readonly string SpoolDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NexusFree");
    public static readonly string InboxDir = Path.Combine(SpoolDir, "inbox");
    public static readonly string OutboxDir = Path.Combine(SpoolDir, "outbox");
    public static readonly string LogDir = Path.Combine(SpoolDir, "logs");
    public static readonly string LogFile = Path.Combine(LogDir, "elevation.log");

    public static JsonObject GetStatus()
    {
        bool inboxReady = Directory.Exists(InboxDir);
        bool taskFound = false;
        string taskStatus = "Inconnue";

        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", $"/query /tn \"{TaskName}\" /fo csv /nh")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p != null)
            {
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                {
                    taskFound = true;
                    taskStatus = output.Trim();
                }
                else
                {
                    taskStatus = "Non installée";
                }
            }
        }
        catch (Exception ex)
        {
            taskStatus = "Erreur: " + ex.Message;
        }

        return new JsonObject
        {
            ["installed"] = taskFound && inboxReady,
            ["taskName"] = TaskName,
            ["taskStatus"] = taskStatus,
            ["spoolDir"] = SpoolDir,
            ["inboxReady"] = inboxReady
        };
    }

    public static async Task<CommandResult> ExecuteAsync(string command, int timeoutSeconds = 600, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return new CommandResult
            {
                Id = "none",
                Success = false,
                ExitCode = 1,
                Error = "La commande à exécuter est vide."
            };
        }

        if (!Directory.Exists(InboxDir))
        {
            return new CommandResult
            {
                Id = "none",
                Success = false,
                ExitCode = 1,
                Error = $"Sas NexusFree introuvable ({InboxDir}). Lancez install.ps1 une fois pour enregistrer la tâche SYSTEM."
            };
        }

        string id = "req-" + Guid.NewGuid().ToString("N");
        string inFile = Path.Combine(InboxDir, $"{id}.json");
        string outFile = Path.Combine(OutboxDir, $"{id}.json");

        var reqObj = new JsonObject
        {
            ["id"] = id,
            ["command"] = command,
            ["created"] = DateTime.UtcNow.ToString("o")
        };

        await File.WriteAllTextAsync(inFile, reqObj.ToJsonString(), Encoding.UTF8, ct);

        // Déclencher la tâche planifiée SYSTEM
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", $"/run /tn \"{TaskName}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p != null)
            {
                await p.WaitForExitAsync(ct);
            }
        }
        catch (Exception ex)
        {
            try { File.Delete(inFile); } catch { }
            return new CommandResult
            {
                Id = id,
                Success = false,
                ExitCode = 1,
                Error = $"Échec du déclenchement de la tâche '{TaskName}': {ex.Message}"
            };
        }

        return await WaitForResultAsync(outFile, id, TimeSpan.FromSeconds(timeoutSeconds), ct);
    }

    /// <summary>
    /// Waits for the dispatcher's answer <paramref name="outFile"/> and returns it once it parses. The dispatcher writes
    /// <c>&lt;id&gt;.json.tmp</c> then renames it, so only the final name is watched; a file that does not parse yet
    /// (an older dispatcher still writing it in place) or is locked is read again until the deadline, and is deleted
    /// only after a successful parse, so an answer is never lost. A FileSystemWatcher wakes the wait at once; polling
    /// stays as the fallback for missed or overflowing notifications.
    /// </summary>
    internal static async Task<CommandResult> WaitForResultAsync(string outFile, string id, TimeSpan timeout, CancellationToken ct)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        using var signal = new SemaphoreSlim(0);
        using var watcher = CreateWatcher(outFile, signal);
        string lastProblem = "";

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                bool retryingSoon = false;
                if (File.Exists(outFile))
                {
                    try
                    {
                        string raw = await File.ReadAllTextAsync(outFile, Encoding.UTF8, ct);
                        if (JsonNode.Parse(raw) is JsonObject node)
                        {
                            var result = new CommandResult
                            {
                                Id = node["id"]?.ToString() ?? id,
                                Success = node["success"]?.GetValue<bool>() ?? false,
                                ExitCode = node["exitCode"]?.GetValue<int>() ?? 0,
                                Output = node["output"]?.ToString() ?? "",
                                Error = node["error"]?.ToString() ?? "",
                                Started = node["started"]?.ToString() ?? "",
                                Finished = node["finished"]?.ToString() ?? ""
                            };
                            try { File.Delete(outFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                            return result;
                        }
                        lastProblem = "réponse JSON qui n'est pas un objet";
                    }
                    catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
                    {
                        lastProblem = ex.GetType().Name + ": " + ex.Message;
                    }
                    retryingSoon = true;
                }

                TimeSpan remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) break;
                TimeSpan pollInterval = retryingSoon ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromMilliseconds(500);
                await signal.WaitAsync(remaining < pollInterval ? remaining : pollInterval, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new CommandResult
            {
                Id = id,
                Success = false,
                ExitCode = 130,
                Error = "Opération annulée."
            };
        }

        string detail = lastProblem.Length > 0
            ? $" La réponse {outFile} est restée illisible ({lastProblem}) ; elle est conservée pour diagnostic."
            : "";
        return new CommandResult
        {
            Id = id,
            Success = false,
            ExitCode = 124,
            Error = $"Délai dépassé ({timeout.TotalSeconds:0.###}s) sans réponse de la tâche SYSTEM pour la requête {id}.{detail}"
        };
    }

    private static FileSystemWatcher? CreateWatcher(string outFile, SemaphoreSlim signal)
    {
        try
        {
            string? directory = Path.GetDirectoryName(outFile);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return null;
            var watcher = new FileSystemWatcher(directory, Path.GetFileName(outFile))
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            void Wake(object? sender, EventArgs e)
            {
                if (signal.CurrentCount == 0) { try { signal.Release(); } catch (ObjectDisposedException) { } catch (SemaphoreFullException) { } }
            }
            watcher.Created += Wake;
            watcher.Changed += Wake;
            watcher.Renamed += Wake;
            watcher.Error += Wake;
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return null; // Polling alone still finds the answer.
        }
    }
}
