using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NexusFree;

/// <summary>
/// The SYSTEM side of NexusFree (<c>NexusFree.exe --dispatch</c>), the only action of the scheduled task "NexusFree".
/// For each request of the inbox: moved to staging (out of the authorized account's reach), refused unless its file
/// belongs to the authorized account, Administrators or SYSTEM, run in PowerShell, and answered in the outbox under a
/// temporary name renamed once complete. The spool is never created here: without the ACLs of setup-system.ps1, any
/// account could write to it.
/// </summary>
internal sealed class Dispatcher
{
    internal const string SystemSid = "S-1-5-18";
    internal const string AdministratorsSid = "S-1-5-32-544";

    /// <summary>After the shell exits, how long its output pipes may stay open (a background grandchild that inherited
    /// them) before the answer is written with what arrived.</summary>
    internal static readonly TimeSpan OutputGrace = TimeSpan.FromSeconds(5);

    // Sent before the command: no progress records (they would reach stderr as CLIXML), and UTF-8 output, the encoding
    // the dispatcher reads, for PowerShell and for the native programs that follow the console code page.
    private const string Prelude = "$ProgressPreference = 'SilentlyContinue'; $OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); chcp.com 65001 > $null; ";

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly string _inbox, _staging, _outbox, _logs, _logFile;
    private readonly HashSet<string> _owners;
    private readonly string _shell;

    internal Dispatcher(string spoolDir, IEnumerable<string> allowedOwners, string shell)
    {
        _inbox = Path.Combine(spoolDir, "inbox");
        _staging = Path.Combine(spoolDir, "staging");
        _outbox = Path.Combine(spoolDir, "outbox");
        _logs = Path.Combine(spoolDir, "logs");
        _logFile = Path.Combine(_logs, "elevation.log");
        _owners = new HashSet<string>(allowedOwners, StringComparer.OrdinalIgnoreCase);
        _shell = shell;
    }

    /// <summary>
    /// The installed dispatcher: the real spool, the account authorized in the <c>config.json</c> next to the engine
    /// (%ProgramFiles%\NexusFree, writable by SYSTEM and Administrators only), Administrators and SYSTEM (the owner of
    /// a file created from an elevated session). Returns 1 without running anything when no account is authorized.
    /// </summary>
    internal static async Task<int> RunInstalledAsync()
    {
        string configFile = Path.Combine(AppContext.BaseDirectory, "config.json");
        string? authorized = ReadAuthorizedSid(configFile);
        var dispatcher = new Dispatcher(Engine.SpoolDir, authorized is null ? [] : [authorized, AdministratorsSid, SystemSid], FindPowerShell());
        if (!dispatcher.SpoolReady()) return 1;
        if (authorized is null)
        {
            dispatcher.Log($"No authorized account in {configFile}: run install.ps1 -Apply again.", "ERROR");
            return 1;
        }
        return await dispatcher.RunAsync();
    }

    /// <summary>The authorized account's SID from <c>config.json</c> (<c>{"userSid": "S-1-5-21-..."}</c>), or null when
    /// the file is missing, unreadable or does not hold a valid SID.</summary>
    internal static string? ReadAuthorizedSid(string configFile)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(configFile, Encoding.UTF8)) is not JsonObject config) return null;
            if (config["userSid"] is not JsonValue value || value.GetValueKind() != JsonValueKind.String) return null;
            return new SecurityIdentifier(value.GetValue<string>()).Value; // throws on a malformed SID
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }

    /// <summary>PowerShell 7 when installed (its machine-wide folder, then the system PATH), else Windows PowerShell.</summary>
    internal static string FindPowerShell()
    {
        string programFiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        if (File.Exists(programFiles)) return programFiles;
        foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string directory = entry.Trim('"');
            if (!Path.IsPathFullyQualified(directory)) continue; // never a relative PATH entry under SYSTEM
            string candidate = Path.Combine(directory, "pwsh.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
    }

    internal bool SpoolReady() =>
        Directory.Exists(_inbox) && Directory.Exists(_staging) && Directory.Exists(_outbox) && Directory.Exists(_logs);

    /// <summary>Answers every request waiting in the inbox. Returns 1 when the spool is incomplete, else 0.</summary>
    internal async Task<int> RunAsync()
    {
        if (!SpoolReady()) return 1;
        string[] pending;
        try { pending = Directory.GetFiles(_inbox, "*.json"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"Inbox unreadable ({ex.GetType().Name}): {ex.Message}", "ERROR");
            return 1;
        }

        foreach (string request in pending)
        {
            if (!request.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            await HandleAsync(request);
        }
        return 0;
    }

    private async Task HandleAsync(string request)
    {
        string id = Path.GetFileNameWithoutExtension(request);
        string staged = Path.Combine(_staging, id + ".json");
        try
        {
            // Out of the inbox first: the authorized account can no longer change or replace it.
            File.Move(request, staged, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Another dispatcher instance took it (the task runs instances in parallel), or it is still being written.
            Log($"Could not move {Path.GetFileName(request)} to staging ({ex.GetType().Name}): {ex.Message}", "ERROR");
            return;
        }

        DateTimeOffset started = DateTimeOffset.Now;
        bool success = false;
        int exitCode = 1;
        string output = "", error = "";
        try
        {
            string owner = OwnerOf(staged);
            if (!_owners.Contains(owner)) throw new DispatchException(Strings.RequestRefused(owner));

            string command;
            try { command = ReadCommand(File.ReadAllText(staged, Encoding.UTF8)); }
            catch (JsonException ex) { throw new DispatchException(Strings.RequestUnreadable(ex.Message)); }

            // Never the command itself: it can hold credentials or private administrative data.
            Log($"Running request {id}");
            (exitCode, output) = await RunCommandAsync(command);
            success = exitCode == 0;
            Log($"Request {id} finished (ExitCode: {exitCode})");
        }
        catch (Exception ex) when (ex is DispatchException or IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            success = false;
            exitCode = 1;
            error = ex is System.ComponentModel.Win32Exception ? Strings.ShellNotStarted(ex.Message) : ex.Message;
            Log($"Request {id} failed ({ex.GetType().Name})", "ERROR");
        }
        finally
        {
            WriteAnswer(id, success, exitCode, output, error, started, DateTimeOffset.Now);
            TryDelete(staged);
        }
    }

    internal static string OwnerOf(string file)
    {
        var owner = new FileInfo(file).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier));
        return owner?.Value ?? "";
    }

    /// <summary>The <c>command</c> of a request, or a <see cref="DispatchException"/> when it is missing or empty.</summary>
    internal static string ReadCommand(string json)
    {
        if (JsonNode.Parse(json) is JsonObject request
            && request["command"] is JsonValue value
            && value.GetValueKind() == JsonValueKind.String
            && value.GetValue<string>() is { } command
            && !string.IsNullOrWhiteSpace(command))
        {
            return command;
        }
        throw new DispatchException(Strings.RequestWithoutCommand);
    }

    /// <summary>Runs the command in PowerShell (encoded, so quotes reach it unchanged) and returns its exit code and
    /// its output, standard output then standard error.</summary>
    internal async Task<(int ExitCode, string Output)> RunCommandAsync(string command)
    {
        var start = new ProcessStartInfo(_shell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true, // closed at once: a command waiting for input ends instead of hanging
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(Prelude + command)) })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException(Strings.ShellNotStarted(_shell));
        process.StandardInput.Close();
        var stdout = new OutputSink(process.StandardOutput.BaseStream);
        var stderr = new OutputSink(process.StandardError.BaseStream);
        await process.WaitForExitAsync();
        await Task.WhenAny(Task.WhenAll(stdout.Completion, stderr.Completion), Task.Delay(OutputGrace));

        string outText = DecodeOutput(stdout.Snapshot(), OemEncoding());
        string errText = DecodeOutput(stderr.Snapshot(), OemEncoding());
        string output = outText.Length > 0 && errText.Length > 0 ? outText + "\n" + errText : outText.Length > 0 ? outText : errText;
        return (process.ExitCode, output);
    }

    /// <summary>The system's OEM code page, used by native programs that ignore the console's UTF-8 without a real
    /// console (dism, netsh...).</summary>
    internal static Encoding OemEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
    }

    /// <summary>Output read line by line in strict UTF-8; a line that is not valid UTF-8 is read in <paramref name="oem"/>
    /// instead of being shown as replacement characters. A leading byte order mark is dropped.</summary>
    internal static string DecodeOutput(byte[] bytes, Encoding oem)
    {
        var utf8 = new UTF8Encoding(false, true);
        var text = new StringBuilder(bytes.Length);
        int start = 0;
        while (start < bytes.Length)
        {
            int end = Array.IndexOf(bytes, (byte)'\n', start);
            if (end < 0) end = bytes.Length - 1;
            int count = end - start + 1;
            try { text.Append(utf8.GetString(bytes, start, count)); }
            catch (DecoderFallbackException) { text.Append(oem.GetString(bytes, start, count)); }
            start = end + 1;
        }
        return text.ToString().TrimStart('﻿');
    }

    /// <summary>The answer under a temporary name, then renamed in the same folder (atomic on one volume): the client
    /// watches only <c>&lt;id&gt;.json</c>, so it never reads a half-written answer.</summary>
    internal void WriteAnswer(string id, bool success, int exitCode, string output, string error, DateTimeOffset started, DateTimeOffset finished)
    {
        var answer = new JsonObject
        {
            ["id"] = id,
            ["success"] = success,
            ["exitCode"] = exitCode,
            ["output"] = output,
            ["error"] = error,
            ["started"] = started.ToString("o", CultureInfo.InvariantCulture),
            ["finished"] = finished.ToString("o", CultureInfo.InvariantCulture),
        };
        string final = Path.Combine(_outbox, id + ".json");
        string temporary = final + ".tmp";
        try
        {
            File.WriteAllText(temporary, answer.ToJsonString(Engine.ReadableJson), Utf8NoBom);
            File.Move(temporary, final);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"Could not write the answer to {id} ({ex.GetType().Name}): {ex.Message}", "ERROR");
            TryDelete(temporary);
        }
    }

    internal void Log(string message, string level = "INFO")
    {
        string line = $"[{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}] [{level}] {message}{Environment.NewLine}";
        try { File.AppendAllText(_logFile, line, Utf8NoBom); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // the answer matters more than its log line
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>A request that is answered with an error instead of being run.</summary>
    internal sealed class DispatchException(string message) : Exception(message);

    /// <summary>Collects a pipe's bytes as they arrive, so the output written so far can be taken even while a
    /// grandchild still holds the pipe open.</summary>
    private sealed class OutputSink
    {
        private readonly ArrayBufferWriter<byte> _buffer = new();
        private readonly Lock _lock = new();

        internal OutputSink(Stream source) => Completion = PumpAsync(source);

        internal Task Completion { get; }

        private async Task PumpAsync(Stream source)
        {
            byte[] chunk = new byte[8192];
            try
            {
                int read;
                while ((read = await source.ReadAsync(chunk)) > 0)
                {
                    lock (_lock) _buffer.Write(chunk.AsSpan(0, read));
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }

        internal byte[] Snapshot()
        {
            lock (_lock) return _buffer.WrittenSpan.ToArray();
        }
    }
}
