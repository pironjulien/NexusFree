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

    /// <summary>The command was stopped at its caller's request (never a success).</summary>
    public bool Stopped { get; init; }

    /// <summary>No answer came within the wait: the command may still be running.</summary>
    internal bool WaitTimedOut { get; init; }

    /// <summary>The wait itself was cancelled; nothing is known about the command.</summary>
    internal bool WaitCancelled { get; init; }
}

/// <summary>Where a request is, read from the spool by its id.</summary>
internal enum RequestState
{
    /// <summary>Answered: the result is there.</summary>
    Done,
    /// <summary>A dispatcher holds it: its command is running.</summary>
    Running,
    /// <summary>Still in the inbox: nothing ran yet.</summary>
    Queued,
    /// <summary>Neither answered nor running: unknown id, answer removed, or dispatcher interrupted.</summary>
    Unknown,
    /// <summary>Not a request id.</summary>
    Invalid,
}

internal sealed record RequestStatus(RequestState State, CommandResult? Result = null, DateTime? SinceUtc = null);

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
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException or InvalidCastException or FileNotFoundException)
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

    /// <summary>The exit code of a wait cancelled by its caller, and of a command stopped at its caller's request.</summary>
    internal const int CancelledExitCode = 130;

    /// <summary>After a stop request, how long the dispatcher's answer (the command's end) is waited for.</summary>
    internal static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(15);

    /// <summary>The dispatcher's sign that a command is running: <c>outbox\&lt;id&gt;.running</c>, held open while it runs.</summary>
    internal const string RunningExtension = ".running";

    /// <summary>The caller's stop request: <c>inbox\&lt;id&gt;.cancel</c>, honoured by the dispatcher running that request.</summary>
    internal const string CancelExtension = ".cancel";

    internal static string NewRequestId() => "req-" + Guid.NewGuid().ToString("N");

    /// <summary>True for an id as <see cref="NewRequestId"/> makes them: nothing else ever names a file of the spool.</summary>
    internal static bool IsRequestId(string? id)
    {
        if (id is not { Length: 36 } || !id.StartsWith("req-", StringComparison.Ordinal)) return false;
        foreach (char c in id.AsSpan(4))
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f'))) return false;
        }
        return true;
    }

    /// <summary>Runs <paramref name="command"/> under SYSTEM and waits for its answer. Cancelling <paramref name="stop"/>
    /// stops the command and the processes it started; a wait that times out leaves it running.</summary>
    public static Task<CommandResult> ExecuteAsync(string command, int timeoutSeconds = 600, CancellationToken stop = default) =>
        ExecuteAsync(command, NewRequestId(), TimeSpan.FromSeconds(timeoutSeconds), CancellationToken.None, stop);

    /// <summary>Drops request <paramref name="id"/> in the inbox, starts the SYSTEM task and waits for its answer.
    /// <paramref name="ct"/> only abandons the wait; <paramref name="stop"/> stops the command.</summary>
    internal static async Task<CommandResult> ExecuteAsync(string command, string id, TimeSpan timeout, CancellationToken ct, CancellationToken stop = default)
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
        PruneStopRequests(InboxDir, DateTime.UtcNow);

        string inFile = Path.Combine(InboxDir, $"{id}.json");

        var reqObj = new JsonObject
        {
            ["id"] = id,
            ["command"] = command,
            ["created"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
        };

        await File.WriteAllTextAsync(inFile, reqObj.ToJsonString(), Encoding.UTF8, ct);
        if (stop.IsCancellationRequested)
        {
            // Stopped before the task was even started: the request is taken back, nothing runs.
            try { File.Delete(inFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return new CommandResult { Id = id, Success = false, ExitCode = CancelledExitCode, Stopped = true, Error = Strings.StoppedBeforeStart };
        }

        // Start the SYSTEM scheduled task.
        try
        {
            SystemTask.Run(TaskName);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException or InvalidCastException or FileNotFoundException)
        {
            try { File.Delete(inFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return new CommandResult { Id = id, Success = false, ExitCode = 1, Error = Strings.TriggerFailed(TaskName, ex.Message) };
        }

        return await WaitOrStopAsync(InboxDir, OutboxDir, id, timeout, StopGrace, ct, stop);
    }

    /// <summary>Waits for the answer to <paramref name="id"/>; when <paramref name="stop"/> is cancelled first, asks the
    /// dispatcher to stop the command and reports what became of it.</summary>
    internal static async Task<CommandResult> WaitOrStopAsync(string inboxDir, string outboxDir, string id, TimeSpan timeout, TimeSpan grace, CancellationToken ct, CancellationToken stop)
    {
        string outFile = Path.Combine(outboxDir, $"{id}.json");
        using var either = CancellationTokenSource.CreateLinkedTokenSource(ct, stop);
        CommandResult result = await WaitForResultAsync(outFile, id, timeout, either.Token);
        if (!result.WaitCancelled || ct.IsCancellationRequested || !stop.IsCancellationRequested) return result;
        return await StopAsync(inboxDir, outFile, id, grace);
    }

    /// <summary>
    /// Stops request <paramref name="id"/> for its caller. Still in the inbox: it is withdrawn and never runs. Taken by
    /// a dispatcher: a stop request is left in the inbox (the same folder, so the same accounts as a request), the
    /// dispatcher ends the command's process tree and answers; that answer is waited for <paramref name="grace"/>. A
    /// command that ended by itself first keeps its real result. Never reported as a success by this method itself.
    /// </summary>
    internal static async Task<CommandResult> StopAsync(string inboxDir, string outFile, string id, TimeSpan grace)
    {
        try
        {
            // Opened alone and deleted on close: either this takes the request, or a dispatcher already moved it.
            new FileStream(Path.Combine(inboxDir, $"{id}.json"), FileMode.Open, FileAccess.Read, FileShare.None, 1, FileOptions.DeleteOnClose).Dispose();
            return new CommandResult { Id = id, Success = false, ExitCode = CancelledExitCode, Stopped = true, Error = Strings.StoppedBeforeStart };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // taken by a dispatcher

        string marker = Path.Combine(inboxDir, id + CancelExtension);
        try { await File.WriteAllBytesAsync(marker, []); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CommandResult { Id = id, Success = false, ExitCode = CancelledExitCode, Error = Strings.StopNotSent(id, ex.Message) };
        }

        CommandResult answer = await WaitForResultAsync(outFile, id, grace, CancellationToken.None);
        if (answer.WaitTimedOut)
        {
            // The request stays: a dispatcher still busy with an earlier request honours it when it gets there.
            return new CommandResult { Id = id, Success = false, ExitCode = CancelledExitCode, Error = Strings.StopUnconfirmed(id, grace.TotalSeconds) };
        }
        try { File.Delete(marker); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return answer;
    }

    /// <summary>Where request <paramref name="id"/> is, with its result once answered. Reads the spool only, as the
    /// caller's own account: nothing is run and nothing is asked of the SYSTEM side.</summary>
    internal static RequestStatus ReadResult(string id) => ReadResult(InboxDir, OutboxDir, id);

    /// <summary>The answer stays in the outbox (read again at will) until <see cref="PruneOutbox"/> removes it.</summary>
    internal static RequestStatus ReadResult(string inboxDir, string outboxDir, string id)
    {
        if (!IsRequestId(id)) return new RequestStatus(RequestState.Invalid);
        string outFile = Path.Combine(outboxDir, $"{id}.json");

        if (TryReadAnswer(outFile, id) is { } done) return new RequestStatus(RequestState.Done, done);
        // The dispatcher holds its marker from before it takes the request until after it answered: no gap in between.
        DateTime? since = RunningSince(Path.Combine(outboxDir, id + RunningExtension));
        bool queued = File.Exists(Path.Combine(inboxDir, $"{id}.json"));
        if (queued) return new RequestStatus(RequestState.Queued);
        if (since is not null) return new RequestStatus(RequestState.Running, SinceUtc: since);
        // Answered between the two looks.
        if (TryReadAnswer(outFile, id) is { } late) return new RequestStatus(RequestState.Done, late);
        return new RequestStatus(RequestState.Unknown);
    }

    /// <summary>When the dispatcher took the request whose marker is <paramref name="marker"/>, or null when no
    /// dispatcher holds it: a live dispatcher keeps the marker open for writing, so it cannot be opened here; a marker
    /// that opens was left by an interrupted dispatcher and is removed.</summary>
    internal static DateTime? RunningSince(string marker)
    {
        try
        {
            using (new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete)) { }
            try { File.Delete(marker); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return null;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (IOException) { return File.GetLastWriteTimeUtc(marker); } // sharing violation: held by its dispatcher
    }

    private static CommandResult? TryReadAnswer(string outFile, string id)
    {
        try
        {
            // Shared for writing and deleting: reading never makes a writer still filling the answer fail.
            using var stream = new FileStream(outFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return JsonNode.Parse(reader.ReadToEnd()) is JsonObject node ? ToResult(node, id) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static CommandResult ToResult(JsonObject node, string id)
    {
        bool stopped = node["stopped"]?.GetValue<bool>() ?? false;
        return new CommandResult
        {
            Id = node["id"]?.ToString() ?? id,
            Success = !stopped && (node["success"]?.GetValue<bool>() ?? false),
            ExitCode = node["exitCode"]?.GetValue<int>() ?? 0,
            Output = node["output"]?.ToString() ?? "",
            // Said in the reader's language: the dispatcher writes in SYSTEM's.
            Error = stopped ? Strings.Stopped : node["error"]?.ToString() ?? "",
            Started = node["started"]?.ToString() ?? "",
            Finished = node["finished"]?.ToString() ?? "",
            Stopped = stopped,
        };
    }

    /// <summary>Removes the stop requests no dispatcher consumed (it ended first) once they are older than
    /// <see cref="AnswerLifetime"/>. Best effort, like <see cref="PruneOutbox"/>.</summary>
    internal static int PruneStopRequests(string inboxDir, DateTime nowUtc)
    {
        int removed = 0;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(inboxDir, "req-*" + CancelExtension); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }

        foreach (string file in files)
        {
            if (!file.EndsWith(CancelExtension, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (nowUtc - File.GetLastWriteTimeUtc(file) < AnswerLifetime) continue;
                File.Delete(file);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return removed;
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
            // Left by an interrupted dispatcher; a live one holds its marker open, so deleting it fails and it stays.
            else if (file.EndsWith(RunningExtension, StringComparison.OrdinalIgnoreCase)) lifetime = TemporaryLifetime;
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
                        // Shared for writing and deleting: reading never makes a writer still filling the answer fail.
                        string raw;
                        using (var stream = new FileStream(outFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous))
                        using (var reader = new StreamReader(stream, Encoding.UTF8))
                            raw = await reader.ReadToEndAsync(ct);
                        if (JsonNode.Parse(raw) is JsonObject node)
                        {
                            var result = ToResult(node, id);
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
                ExitCode = CancelledExitCode,
                Error = Strings.Cancelled,
                WaitCancelled = true
            };
        }

        string detail = lastProblem.Length > 0 ? Strings.Unreadable(outFile, lastProblem) : "";
        return new CommandResult
        {
            Id = id,
            Success = false,
            ExitCode = 124,
            WaitTimedOut = true,
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
