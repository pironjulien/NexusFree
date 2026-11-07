using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace NexusFree.Tests;

/// <summary>
/// An AI session keeps its MCP engine for hours; an update renames that running engine aside and copies the new one to
/// the same path. A self-contained single-file engine loads its assemblies from its own path on first use, so before
/// 1.3.3 every later tool call of that session failed with an empty error. Here the release build (self-contained,
/// single file) is started, its file replaced the way install.ps1 does, and its tools must still answer.
/// </summary>
public sealed class UpdateWhileRunningTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("nexusfree-update-").FullName;

    public void Dispose() => TestEnvironment.DeleteOwnTemporary(root);

    private string PublishSingleFile()
    {
        string rid = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        string output = Path.Join(root, "publish");
        var info = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string a in new[] { "publish", Path.Join(GetScriptTests.Repository(), "src", "NexusFree", "NexusFree.csproj"),
            "-c", "Release", "-r", rid, "--self-contained", "true", "-p:PublishSingleFile=true", "-o", output, "--nologo" })
            info.ArgumentList.Add(a);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(600_000), "dotnet publish did not finish");
        Assert.True(process.ExitCode == 0, stdout.Result + stderr.Result);
        return Path.Join(output, "NexusFree.exe");
    }

    /// <summary>
    /// install.ps1 renames an engine that has run for a while. Here the file was written and started seconds ago, and
    /// antivirus or indexing services open a freshly written executable for a moment to scan it without sharing delete:
    /// the rename then fails with "being used by another process" (the Release run of d411487), while the engine itself
    /// holds nothing that forbids it. Only that external, transient hold (a sharing or lock violation) is waited out, at
    /// most 10 s, as get.ps1 does for its staged folder; a longer hold fails and names the processes holding the file.
    /// </summary>
    private static void MoveAsideOnceReleased(string path, string destination)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                File.Move(path, destination);
                return;
            }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33) // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION
            {
                if (elapsed.Elapsed >= TimeSpan.FromSeconds(10))
                    throw new IOException($"{path} is still held after 10 s by: {string.Join(", ", Holders(path))}", ex);
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>The processes that have <paramref name="path"/> open, as the Restart Manager sees them.</summary>
    private static string[] Holders(string path)
    {
        if (RmStartSession(out uint session, 0, Guid.NewGuid().ToString("N")) != 0) return ["(Restart Manager unavailable)"];
        try
        {
            if (RmRegisterResources(session, 1, [path], 0, IntPtr.Zero, 0, null) != 0) return ["(Restart Manager refused the file)"];
            uint count = 0;
            var processes = Array.Empty<RmProcessInfo>();
            int result;
            do
            {
                processes = new RmProcessInfo[count];
                result = RmGetList(session, out uint needed, ref count, processes, out _);
                count = needed;
            } while (result == 234); // ERROR_MORE_DATA
            if (result != 0) return [$"(Restart Manager error {result})"];
            var names = processes.Take((int)count).Select(p => $"{p.AppName} (pid {p.Process.ProcessId})").ToArray();
            return names.Length > 0 ? names : ["(no process listed)"];
        }
        finally { _ = RmEndSession(session); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME StartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RmStartSession(out uint session, int flags, string key);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RmRegisterResources(uint session, uint fileCount, string[] files, uint applicationCount,
        IntPtr applications, uint serviceCount, string[]? services);

    [DllImport("rstrtmgr.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] RmProcessInfo[] processes, out uint rebootReasons);

    [DllImport("rstrtmgr.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RmEndSession(uint session);

    [Fact]
    public async Task A_running_engine_keeps_answering_after_its_file_is_replaced()
    {
        string install = Directory.CreateDirectory(Path.Join(root, "nexus-free")).FullName;
        string engine = Path.Join(install, "NexusFree.exe");
        File.Copy(PublishSingleFile(), engine);

        var info = new ProcessStartInfo(engine, "--mcp")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
        };
        info.Environment["NEXUS_JOURNAL_HOME"] = TestEnvironment.JournalHome;
        using var server = Process.Start(info)!;
        try
        {
            async Task<JsonObject> Call(int id, string method, JsonObject? parameters = null)
            {
                var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
                if (parameters is not null) request["params"] = parameters;
                await server.StandardInput.WriteLineAsync(request.ToJsonString());
                await server.StandardInput.FlushAsync();
                string? line = await server.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
                return Assert.IsType<JsonObject>(JsonNode.Parse(line ?? "null"));
            }

            Assert.NotNull((await Call(1, "initialize", new JsonObject { ["protocolVersion"] = "2025-11-25" }))["result"]);

            // What install.ps1 does to an engine in use: renamed aside, the new version copied to its path. Here the
            // "new version" is unrelated bytes: anything read from that path now is wrong.
            MoveAsideOnceReleased(engine, engine + ".old-test");
            var junk = new byte[4 * 1024 * 1024];
            Random.Shared.NextBytes(junk);
            await File.WriteAllBytesAsync(engine, junk, TestContext.Current.CancellationToken);

            JsonObject status = await Call(2, "tools/call", new JsonObject { ["name"] = "free_status", ["arguments"] = new JsonObject() });
            string statusText = status["result"]?["content"]?[0]?["text"]?.ToString() ?? "";
            Assert.False(status["result"]?["isError"]?.GetValue<bool>() ?? true, statusText);
            Assert.Contains("taskStatus", statusText, StringComparison.Ordinal);

            // An empty command never reaches the spool: the engine's own path is exercised without elevating anything.
            JsonObject free = await Call(3, "tools/call", new JsonObject { ["name"] = "free", ["arguments"] = new JsonObject { ["command"] = "" } });
            string freeText = free["result"]?["content"]?[0]?["text"]?.ToString() ?? "";
            Assert.Contains("[NexusFree]", freeText, StringComparison.Ordinal);
            Assert.DoesNotContain("failed", freeText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Erreur d'exécution", freeText, StringComparison.Ordinal);
        }
        finally
        {
            server.StandardInput.Close();
            if (!server.WaitForExit(10_000)) server.Kill();
        }
    }
}
