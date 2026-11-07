using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;

namespace NexusFree.Tests;

/// <summary>The public installer get.ps1 on a local package (NEXUS_FREE_SOURCE) in a temporary install folder
/// (NEXUS_FREE_HOME), under PowerShell 7 and Windows PowerShell 5.1. Never the network, the real profile or install.ps1
/// (which would register the SYSTEM dispatcher).</summary>
public sealed class GetScriptTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("nexusfree-get-").FullName;
    private string Home => Path.Join(root, "home");
    private string Zip => Path.Join(root, "NexusFree-win-x64.zip");

    public GetScriptTests()
    {
        // The engine the tests run (framework-dependent here, self-contained in a release) and a stand-in install.ps1.
        string engine = AppContext.BaseDirectory;
        using var zip = ZipFile.Open(Zip, ZipArchiveMode.Create);
        foreach (string name in new[] { "NexusFree.exe", "NexusFree.dll", "NexusFree.runtimeconfig.json", "NexusFree.deps.json" })
            zip.CreateEntryFromFile(Path.Join(engine, name), name);
        using var writer = new StreamWriter(zip.CreateEntry("install.ps1").Open());
        writer.Write("throw 'install.ps1 must not run in a test.'");
    }

    public void Dispose() => TestEnvironment.DeleteOwnTemporary(root);

    // The build output lives outside the repository: the repository is found from this source file, or (a CI build
    // maps source paths) from the folder test.ps1 runs in.
    internal static string Repository([CallerFilePath] string source = "")
    {
        foreach (string start in new[] { Path.Join(Path.GetDirectoryName(source) ?? "", "..", ".."), Environment.CurrentDirectory, Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? "" })
        {
            for (var dir = start.Length > 0 && Directory.Exists(start) ? new DirectoryInfo(Path.GetFullPath(start)) : null; dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Join(dir.FullName, "get.ps1")) && Directory.Exists(Path.Join(dir.FullName, "src", "NexusFree"))) return dir.FullName;
        }
        throw new InvalidOperationException("The NexusFree repository (get.ps1) was not found.");
    }

    private (int Code, string Out) Get(string shell)
    {
        var info = new ProcessStartInfo(shell)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        foreach (string a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Join(Repository(), "get.ps1") })
            info.ArgumentList.Add(a);
        // Windows PowerShell started from a .NET 10 process must not inherit PowerShell 7's module folders.
        info.Environment.Remove("PSModulePath");
        info.Environment["NEXUS_FREE_SOURCE"] = Zip;
        info.Environment["NEXUS_FREE_HOME"] = Home;
        using var process = Process.Start(info)!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(120_000), "get.ps1 did not finish");
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    [Theory]
    [InlineData("pwsh")]
    [InlineData("powershell")]
    public void Installs_the_package_into_its_version_folder(string shell)
    {
        var (code, output) = Get(shell);
        Assert.True(code == 0, output);
        Assert.True(File.Exists(Path.Join(Home, "releases", McpServer.ServerVersion, "NexusFree.exe")), output);
        var (again, againOutput) = Get(shell);
        Assert.True(again == 0, "a second run (update) must succeed: " + againOutput);
        Assert.Single(Directory.GetDirectories(Path.Join(Home, "releases")));
        Assert.Empty(Directory.GetDirectories(Home, "staging-*"));
    }

    [Theory]
    [InlineData("pwsh")]
    [InlineData("powershell")]
    public void A_scanner_briefly_holding_the_fresh_executable_does_not_fail_the_installation(string shell)
    {
        // What an antivirus does to a freshly written executable: opens it for a moment (every sharing mode allowed),
        // which blocks renaming its folder. get.ps1 waits that hold out instead of failing ("being used by another
        // process").
        Directory.CreateDirectory(Home);
        string? held = null;
        bool stagedWhileHeld = false;
        using var released = new ManualResetEventSlim();
        using var watcher = new FileSystemWatcher(Home, "NexusFree.exe") { IncludeSubdirectories = true };
        var scanner = new Thread(() =>
        {
            string path = watcher.WaitForChanged(WatcherChangeTypes.Created, 60_000) is { TimedOut: false } change ? Path.Join(Home, change.Name) : "";
            for (int i = 0; i < 400 && held is null && path.Length > 0; i++)
            {
                try
                {
                    using var scan = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    held = path;
                    Thread.Sleep(2_000);
                    stagedWhileHeld = File.Exists(path);
                }
                catch (IOException) { Thread.Sleep(5); }  // still being extracted
            }
            released.Set();
        }) { IsBackground = true };
        watcher.EnableRaisingEvents = true;
        scanner.Start();

        var (code, output) = Get(shell);
        Assert.True(released.Wait(60_000, TestContext.Current.CancellationToken));
        Assert.True(code == 0, output);
        Assert.NotNull(held);
        Assert.Contains("staging-", held, StringComparison.OrdinalIgnoreCase);
        Assert.True(stagedWhileHeld, "the hold must have overlapped the staged folder");
        Assert.True(File.Exists(Path.Join(Home, "releases", McpServer.ServerVersion, "NexusFree.exe")), output);
        Assert.Empty(Directory.GetDirectories(Home, "staging-*"));
    }
}
