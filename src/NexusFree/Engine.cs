using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;

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

    /// <summary>An answer nobody collected (it arrived after the client's timeout) is removed after this time.</summary>
    internal static readonly TimeSpan AnswerLifetime = TimeSpan.FromHours(24);

    /// <summary>A temporary answer lives milliseconds; one this old was left by an interrupted dispatcher.</summary>
    internal static readonly TimeSpan TemporaryLifetime = TimeSpan.FromHours(1);

    /// <summary>Status JSON as people read it (console and MCP text, never HTML): accents and "+" kept, not escaped.</summary>
    internal static readonly JsonSerializerOptions ReadableJson = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static JsonObject GetStatus()
    {
        bool inboxReady = Directory.Exists(InboxDir);
        bool taskFound = false;
        string taskStatus;
        JsonNode? lastRun = null, lastResult = null;

        try
        {
            var task = SystemTask.Query(TaskName);
            taskFound = task.Found;
            taskStatus = task.Found ? Strings.TaskStateName(task.State) : Strings.TaskMissing;
            if (task.LastRun is DateTime run) lastRun = new DateTimeOffset(DateTime.SpecifyKind(run, DateTimeKind.Local)).ToString("o", CultureInfo.InvariantCulture);
            if (task.LastResult is int code) lastResult = code;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            taskStatus = Strings.TaskError(ex.Message);
        }

        return new JsonObject
        {
            ["installed"] = taskFound && inboxReady,
            ["taskName"] = TaskName,
            ["taskStatus"] = taskStatus,
            ["lastRun"] = lastRun,
            ["lastResult"] = lastResult,
            ["spoolDir"] = SpoolDir,
            ["inboxReady"] = inboxReady
        };
    }

    public static async Task<CommandResult> ExecuteAsync(string command, int timeoutSeconds = 600, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return new CommandResult { Id = "none", Success = false, ExitCode = 1, Error = Strings.EmptyCommand };
        }

        if (!Directory.Exists(InboxDir))
        {
            return new CommandResult { Id = "none", Success = false, ExitCode = 1, Error = Strings.SpoolMissing(InboxDir) };
        }

        PruneOutbox(OutboxDir, DateTime.UtcNow);

        string id = "req-" + Guid.NewGuid().ToString("N");
        string inFile = Path.Combine(InboxDir, $"{id}.json");
        string outFile = Path.Combine(OutboxDir, $"{id}.json");

        var reqObj = new JsonObject
        {
            ["id"] = id,
            ["command"] = command,
            ["created"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
        };

        await File.WriteAllTextAsync(inFile, reqObj.ToJsonString(), Encoding.UTF8, ct);

        // Start the SYSTEM scheduled task.
        try
        {
            SystemTask.Run(TaskName);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            try { File.Delete(inFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return new CommandResult { Id = id, Success = false, ExitCode = 1, Error = Strings.TriggerFailed(TaskName, ex.Message) };
        }

        return await WaitForResultAsync(outFile, id, TimeSpan.FromSeconds(timeoutSeconds), ct);
    }

    /// <summary>
    /// Removes what no client will ever read from the outbox: answers older than <see cref="AnswerLifetime"/> (they
    /// arrived after their client gave up, or were kept as unreadable for diagnosis) and temporary answers older than
    /// <see cref="TemporaryLifetime"/> (an interrupted write). Best effort: a locked or protected file stays for the
    /// next run. Returns how many files were removed.
    /// </summary>
    internal static int PruneOutbox(string outboxDir, DateTime nowUtc)
    {
        int removed = 0;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(outboxDir, "req-*"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }

        foreach (string file in files)
        {
            TimeSpan lifetime;
            if (file.EndsWith(".json.tmp", StringComparison.OrdinalIgnoreCase)) lifetime = TemporaryLifetime;
            else if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) lifetime = AnswerLifetime;
            else continue;
            try
            {
                if (nowUtc - File.GetLastWriteTimeUtc(file) < lifetime) continue;
                File.Delete(file);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return removed;
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
                        lastProblem = Strings.NotAnObject;
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
                Error = Strings.Cancelled
            };
        }

        string detail = lastProblem.Length > 0 ? Strings.Unreadable(outFile, lastProblem) : "";
        return new CommandResult
        {
            Id = id,
            Success = false,
            ExitCode = 124,
            Error = Strings.TimedOut(timeout.TotalSeconds, id, detail)
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
