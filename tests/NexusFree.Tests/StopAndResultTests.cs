using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace NexusFree.Tests;

/// <summary>
/// Stopping a command at its caller's request, and reading a late answer by its id: the engine (client side) and the
/// dispatcher together on a temporary spool, run as the test account. Nothing here goes through the SYSTEM task.
/// </summary>
public sealed class StopAndResultTests : IDisposable
{
    private readonly string _spool = Directory.CreateTempSubdirectory("nexusfree-stop-").FullName;

    public StopAndResultTests()
    {
        foreach (string folder in new[] { "inbox", "staging", "outbox", "logs" }) Directory.CreateDirectory(Path.Combine(_spool, folder));
    }

    private string Inbox => Path.Combine(_spool, "inbox");
    private string Outbox => Path.Combine(_spool, "outbox");
    private string PidFile => Path.Combine(_spool, "child.pid");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Request(string id, string command)
    {
        string file = Path.Combine(Inbox, id + ".json");
        File.WriteAllText(file, new JsonObject { ["id"] = id, ["command"] = command }.ToJsonString(), new UTF8Encoding(false));
        return file;
    }

    private Dispatcher DispatcherFor(string request) => new(_spool, [Dispatcher.OwnerOf(request)], Dispatcher.FindPowerShell());

    /// <summary>A command that starts a child process (its id written to <see cref="PidFile"/>), then waits far longer
    /// than any test: only a stop ends it.</summary>
    private string LongCommandWithChild() =>
        $"$p = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\\ping.exe') -ArgumentList '-n','300','127.0.0.1' -NoNewWindow -PassThru; " +
        $"Set-Content -LiteralPath '{PidFile}' -Value $p.Id; Start-Sleep -Seconds 300";

    private async Task<int> ChildStartedAsync()
    {
        for (int i = 0; i < 300; i++)
        {
            if (File.Exists(PidFile) && int.TryParse(ReadShared(PidFile).Trim(), out int pid)) return pid;
            await Task.Delay(100, Token);
        }
        throw new TimeoutException("The command did not start its child process within 30 s.");
    }

    private static string ReadShared(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException) { return ""; }
    }

    private static async Task<bool> ExitedAsync(int pid)
    {
        for (int i = 0; i < 100; i++)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited) return true;
            }
            catch (ArgumentException) { return true; } // no such process any more
            await Task.Delay(100, Token);
        }
        return false;
    }

    private static void EndLeftover(int pid)
    {
        try { using var process = Process.GetProcessById(pid); process.Kill(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    [Fact]
    public async Task Stop_Request_Ends_The_Whole_Process_Tree_And_Is_Answered_As_Stopped()
    {
        string id = Engine.NewRequestId();
        string request = Request(id, "'before the stop'; " + LongCommandWithChild());
        Task<int> run = DispatcherFor(request).RunAsync();
        int child = await ChildStartedAsync();
        try
        {
            Assert.Equal(RequestState.Running, Engine.ReadResult(Inbox, Outbox, id).State);

            string stopRequest = Path.Combine(Inbox, id + Engine.CancelExtension);
            File.WriteAllBytes(stopRequest, []);
            Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(30), Token));

            Assert.True(await ExitedAsync(child), "The child process of the stopped command is still running.");
            var answer = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(Path.Combine(Outbox, id + ".json"), Encoding.UTF8)));
            Assert.True(answer["stopped"]?.GetValue<bool>());
            Assert.False(answer["success"]?.GetValue<bool>());
            Assert.Equal(Engine.CancelledExitCode, answer["exitCode"]?.GetValue<int>());
            Assert.Contains("before the stop", answer["output"]?.ToString() ?? "", StringComparison.Ordinal); // what it wrote is kept
            Assert.NotEqual("", answer["error"]?.ToString());

            Assert.False(File.Exists(stopRequest)); // consumed
            Assert.Empty(Directory.GetFiles(Path.Combine(_spool, "staging")));
            Assert.Single(Directory.GetFiles(Outbox)); // the running marker went with the run
            string log = File.ReadAllText(Path.Combine(_spool, "logs", "elevation.log"), Encoding.UTF8);
            Assert.Contains($"Request {id} stopped at its caller's request", log, StringComparison.Ordinal);
            Assert.DoesNotContain("finished", log, StringComparison.Ordinal); // never logged as a normal end

            // The same answer, read by its id as often as needed.
            for (int i = 0; i < 2; i++)
            {
                var status = Engine.ReadResult(Inbox, Outbox, id);
                Assert.Equal(RequestState.Done, status.State);
                Assert.True(status.Result!.Stopped);
                Assert.False(status.Result.Success);
            }
        }
        finally { EndLeftover(child); }
    }

    [Fact]
    public async Task Cancelling_The_Callers_Wait_Stops_The_Command_And_Says_So()
    {
        string id = Engine.NewRequestId();
        string request = Request(id, LongCommandWithChild());
        Task<int> run = DispatcherFor(request).RunAsync();
        using var stop = new CancellationTokenSource();
        Task<CommandResult> waiting = Engine.WaitOrStopAsync(Inbox, Outbox, id, TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(30), CancellationToken.None, stop.Token);
        int child = await ChildStartedAsync();
        try
        {
            Assert.False(waiting.IsCompleted);
            await stop.CancelAsync();
            CommandResult result = await waiting.WaitAsync(TimeSpan.FromSeconds(40), Token);

            Assert.True(result.Stopped);
            Assert.False(result.Success);
            Assert.Equal(Engine.CancelledExitCode, result.ExitCode);
            Assert.Equal(Strings.Stopped, result.Error);
            Assert.True(await ExitedAsync(child));
            Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10), Token));
            Assert.Empty(Directory.GetFiles(Inbox));
            Assert.Empty(Directory.GetFiles(Outbox)); // answer collected, marker gone
        }
        finally { EndLeftover(child); }
    }

    [Fact]
    public async Task A_Stop_Request_Already_There_Keeps_The_Command_From_Starting()
    {
        string id = Engine.NewRequestId();
        string marker = Path.Combine(_spool, "ran.txt");
        string request = Request(id, $"Set-Content -LiteralPath '{marker}' -Value x");
        File.WriteAllBytes(Path.Combine(Inbox, id + Engine.CancelExtension), []);

        Assert.Equal(0, await DispatcherFor(request).RunAsync());

        Assert.False(File.Exists(marker));
        var status = Engine.ReadResult(Inbox, Outbox, id);
        Assert.Equal(RequestState.Done, status.State);
        Assert.True(status.Result!.Stopped);
        Assert.Equal(Engine.CancelledExitCode, status.Result.ExitCode);
        Assert.Empty(Directory.GetFiles(Inbox));
    }

    [Fact]
    public async Task A_Request_Still_In_The_Inbox_Is_Withdrawn_And_Never_Runs()
    {
        string id = Engine.NewRequestId();
        string request = Request(id, "'never'");

        CommandResult result = await Engine.StopAsync(Inbox, Path.Combine(Outbox, id + ".json"), id, TimeSpan.FromSeconds(5));

        Assert.True(result.Stopped);
        Assert.False(result.Success);
        Assert.Equal(Strings.StoppedBeforeStart, result.Error);
        Assert.False(File.Exists(request));
        Assert.Empty(Directory.GetFiles(Inbox)); // no stop request left for nobody
    }

    [Fact]
    public async Task A_Stop_Nobody_Confirms_Is_Not_Reported_As_Stopped_And_Stays_Asked()
    {
        string id = Engine.NewRequestId(); // taken by a dispatcher that never answers

        CommandResult result = await Engine.StopAsync(Inbox, Path.Combine(Outbox, id + ".json"), id, TimeSpan.FromMilliseconds(400));

        Assert.False(result.Stopped);
        Assert.False(result.Success);
        Assert.Contains(id, result.Error, StringComparison.Ordinal);
        Assert.Contains("free_result", result.Error, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(Inbox, id + Engine.CancelExtension)));
    }

    [Fact]
    public async Task A_Command_That_Ended_By_Itself_Before_The_Stop_Keeps_Its_Real_Result()
    {
        string id = Engine.NewRequestId();
        string outFile = Path.Combine(Outbox, id + ".json");
        File.WriteAllText(outFile, new JsonObject { ["id"] = id, ["success"] = true, ["exitCode"] = 0, ["output"] = "done", ["error"] = "" }.ToJsonString());

        CommandResult result = await Engine.StopAsync(Inbox, outFile, id, TimeSpan.FromSeconds(5));

        Assert.False(result.Stopped);
        Assert.True(result.Success);
        Assert.Equal("done", result.Output);
        Assert.Empty(Directory.GetFiles(Inbox));
    }

    [Fact]
    public async Task A_Wait_That_Times_Out_Stops_Nothing_And_Names_How_To_Read_The_Result()
    {
        string id = Engine.NewRequestId();

        CommandResult result = await Engine.WaitOrStopAsync(Inbox, Outbox, id, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(5), CancellationToken.None, CancellationToken.None);

        Assert.True(result.WaitTimedOut);
        Assert.Equal(124, result.ExitCode);
        Assert.Contains(id, result.Error, StringComparison.Ordinal);
        Assert.Contains("free_result", result.Error, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(Inbox)); // no stop request: the command keeps running
    }

    [Fact]
    public async Task An_Abandoned_Wait_Stops_Nothing()
    {
        string id = Engine.NewRequestId();
        using var abandon = new CancellationTokenSource(200);

        CommandResult result = await Engine.WaitOrStopAsync(Inbox, Outbox, id, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5), abandon.Token, CancellationToken.None);

        Assert.True(result.WaitCancelled);
        Assert.Equal(Engine.CancelledExitCode, result.ExitCode);
        Assert.Empty(Directory.GetFiles(Inbox));
    }

    [Fact]
    public async Task Late_Answer_Is_Read_By_Its_Id_With_Each_State_On_The_Way()
    {
        string id = Engine.NewRequestId();
        Assert.Equal(RequestState.Unknown, Engine.ReadResult(Inbox, Outbox, id).State);

        string gate = Path.Combine(_spool, "gate.txt");
        string request = Request(id, $"'late answer'; while (-not (Test-Path -LiteralPath '{gate}')) {{ Start-Sleep -Milliseconds 100 }}; exit 7");
        Assert.Equal(RequestState.Queued, Engine.ReadResult(Inbox, Outbox, id).State);

        Task<int> run = DispatcherFor(request).RunAsync();
        // The caller gave up long before the end.
        CommandResult waited = await Engine.WaitOrStopAsync(Inbox, Outbox, id, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5), CancellationToken.None, CancellationToken.None);
        Assert.True(waited.WaitTimedOut);
        var running = Engine.ReadResult(Inbox, Outbox, id);
        Assert.Equal(RequestState.Running, running.State);
        Assert.NotNull(running.SinceUtc);

        File.WriteAllText(gate, "go");
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(30), Token));

        var done = Engine.ReadResult(Inbox, Outbox, id);
        Assert.Equal(RequestState.Done, done.State);
        Assert.Equal(7, done.Result!.ExitCode);
        Assert.False(done.Result.Success);
        Assert.False(done.Result.Stopped);
        Assert.Contains("late answer", done.Result.Output, StringComparison.Ordinal);
        Assert.Equal(RequestState.Done, Engine.ReadResult(Inbox, Outbox, id).State); // still there for a second read
    }

    [Theory]
    [InlineData("")]
    [InlineData("req-test")]
    [InlineData("..\\..\\logs\\elevation")]
    [InlineData("req-0123456789abcdef0123456789abcdeF")]
    [InlineData("req-0123456789abcdef0123456789abcde/")]
    [InlineData("xxx-0123456789abcdef0123456789abcdef")]
    public void Only_A_Request_Id_Names_A_File_Of_The_Spool(string id)
    {
        Assert.False(Engine.IsRequestId(id));
        Assert.Equal(RequestState.Invalid, Engine.ReadResult(Inbox, Outbox, id).State);
    }

    [Fact]
    public void A_Running_Marker_Nobody_Holds_Is_Not_Running_And_Is_Removed()
    {
        string id = Engine.NewRequestId();
        string marker = Path.Combine(Outbox, id + Engine.RunningExtension);
        File.WriteAllBytes(marker, []); // left by an interrupted dispatcher

        Assert.Equal(RequestState.Unknown, Engine.ReadResult(Inbox, Outbox, id).State);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void Prune_Removes_Stale_Stop_Requests_And_Abandoned_Running_Markers_Only()
    {
        string Make(string folder, string name, TimeSpan age)
        {
            string path = Path.Combine(folder, name);
            File.WriteAllBytes(path, []);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
            return path;
        }
        string staleStop = Make(Inbox, "req-old.cancel", Engine.AnswerLifetime + TimeSpan.FromMinutes(5));
        string freshStop = Make(Inbox, "req-new.cancel", TimeSpan.FromMinutes(5));
        string request = Make(Inbox, "req-old.json", Engine.AnswerLifetime + TimeSpan.FromMinutes(5));
        string staleMarker = Make(Outbox, "req-gone.running", Engine.TemporaryLifetime + TimeSpan.FromMinutes(5));
        string heldMarker = Make(Outbox, "req-live.running", Engine.TemporaryLifetime + TimeSpan.FromMinutes(5));

        using (new FileStream(heldMarker, FileMode.Open, FileAccess.Write, FileShare.Read)) // a live dispatcher's hold
        {
            Assert.Equal(1, Engine.PruneStopRequests(Inbox, DateTime.UtcNow));
            Assert.Equal(1, Engine.PruneOutbox(Outbox, DateTime.UtcNow));
            Assert.True(File.Exists(heldMarker));
        }
        Assert.False(File.Exists(staleStop));
        Assert.True(File.Exists(freshStop));
        Assert.True(File.Exists(request)); // a request is never pruned here
        Assert.False(File.Exists(staleMarker));
    }

    [Fact]
    public async Task Cancelled_Notification_Cancels_Only_Its_Request_Which_Gets_No_Response()
    {
        var input = new LineFeed();
        var text = new StringWriter();
        var output = TextWriter.Synchronized(text);
        string Written() { lock (output) return text.ToString(); } // the synchronized writer locks itself
        var cancelledSeen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<JsonObject?> Handle(JsonObject request, CancellationToken ct)
        {
            string id = request["id"]!.ToJsonString();
            try { await release.Task.WaitAsync(ct); }
            catch (OperationCanceledException) { cancelledSeen.TrySetResult(id); }
            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone(), ["result"] = new JsonObject() };
        }

        Task server = McpServer.RunAsync(input, output, Handle, Token);
        input.Send("""{"jsonrpc":"2.0","id":1,"method":"tools/call"}""");
        input.Send("""{"jsonrpc":"2.0","id":"1","method":"tools/call"}""");
        input.Send("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":99}}"""); // unknown: ignored
        input.Send("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":1,"reason":"user"}}""");

        Assert.Equal("1", await cancelledSeen.Task.WaitAsync(TimeSpan.FromSeconds(10), Token)); // the number 1, not the string "1"
        release.SetResult();
        for (int i = 0; i < 100 && !Written().Contains("\"id\":\"1\"", StringComparison.Ordinal); i++) await Task.Delay(50, Token);
        input.Close();
        await server.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await Task.Delay(200, Token);

        string written = Written();
        Assert.Contains("\"id\":\"1\"", written, StringComparison.Ordinal);
        Assert.DoesNotContain("\"id\":1", written, StringComparison.Ordinal); // a cancelled request is not answered
    }

    [Fact]
    public async Task FreeResult_Is_Listed_And_Refuses_What_Is_Not_A_Request_Id()
    {
        var list = await McpServer.HandleRequestAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/list" }, Token);
        var tools = Assert.IsType<JsonArray>(list?["result"]?["tools"]);
        var tool = Assert.Single(tools, t => t?["name"]?.ToString() == "free_result");
        Assert.Equal("id", tool!["inputSchema"]?["required"]?[0]?.ToString());

        foreach (string id in new[] { "..\\..\\logs\\elevation", Engine.NewRequestId() }) // not an id, then an id nobody used
        {
            var call = await McpServer.HandleRequestAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = "free_result", ["arguments"] = new JsonObject { ["id"] = id } },
            }, Token);
            Assert.True(call?["result"]?["isError"]?.GetValue<bool>());
            Assert.NotEqual("", call?["result"]?["content"]?[0]?["text"]?.ToString());
        }
    }

    [Theory]
    [InlineData("en-US", "Stopped at the caller's request", "is still running")]
    [InlineData("fr-FR", "Arrêtée à la demande de l'appelant", "est encore en cours")]
    public void Stop_And_State_Texts_Follow_The_Windows_Display_Language(string culture, string stopped, string running)
    {
        CultureInfo previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo(culture);
        try
        {
            string id = Engine.NewRequestId();
            var (text, isError) = McpServer.FormatStatus(id, new RequestStatus(RequestState.Done,
                new CommandResult { Id = id, ExitCode = Engine.CancelledExitCode, Stopped = true, Error = Strings.Stopped, Output = "partial" }));
            Assert.True(isError); // a stopped command is never shown as a success
            Assert.Contains(stopped, text, StringComparison.Ordinal);
            Assert.Contains("partial", text, StringComparison.Ordinal);

            (text, isError) = McpServer.FormatStatus(id, new RequestStatus(RequestState.Running, SinceUtc: DateTime.UtcNow));
            Assert.False(isError);
            Assert.Contains(running, text, StringComparison.Ordinal);
            Assert.Contains(id, text, StringComparison.Ordinal);
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    public void Dispose() => TestEnvironment.DeleteOwnTemporary(_spool);

    /// <summary>The server's input, fed line by line by the test and left open in between, as an AI app's pipe is.</summary>
    private sealed class LineFeed : TextReader
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();

        internal void Send(string line) => _lines.Writer.TryWrite(line);

        public override void Close() => _lines.Writer.TryComplete();

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            while (await _lines.Reader.WaitToReadAsync(cancellationToken))
            {
                if (_lines.Reader.TryRead(out string? line)) return line;
            }
            return null;
        }
    }
}
