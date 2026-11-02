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

        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested)
            {
                return new CommandResult
                {
                    Id = id,
                    Success = false,
                    ExitCode = 130,
                    Error = "Opération annulée."
                };
            }

            if (File.Exists(outFile))
            {
                await Task.Delay(100, ct);
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    try
                    {
                        string raw = await File.ReadAllTextAsync(outFile, Encoding.UTF8, ct);
                        try { File.Delete(outFile); } catch { }

                        var node = JsonNode.Parse(raw) as JsonObject;
                        if (node != null)
                        {
                            return new CommandResult
                            {
                                Id = node["id"]?.ToString() ?? id,
                                Success = node["success"]?.GetValue<bool>() ?? false,
                                ExitCode = node["exitCode"]?.GetValue<int>() ?? 0,
                                Output = node["output"]?.ToString() ?? "",
                                Error = node["error"]?.ToString() ?? "",
                                Started = node["started"]?.ToString() ?? "",
                                Finished = node["finished"]?.ToString() ?? ""
                            };
                        }
                    }
                    catch (IOException)
                    {
                        await Task.Delay(200, ct);
                    }
                }
            }

            await Task.Delay(250, ct);
        }

        return new CommandResult
        {
            Id = id,
            Success = false,
            ExitCode = 124,
            Error = $"Délai dépassé ({timeoutSeconds}s) sans réponse de la tâche SYSTEM pour la requête {id}."
        };
    }
}
