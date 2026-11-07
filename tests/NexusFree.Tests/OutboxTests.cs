using System.Text;

namespace NexusFree.Tests;

public sealed class OutboxTests : IDisposable
{
    private const string Complete = "{\"id\":\"req-test\",\"success\":true,\"exitCode\":0,\"output\":\"nt authority\\\\system\",\"error\":\"\",\"started\":\"s\",\"finished\":\"f\"}";
    private readonly string _dir = Directory.CreateTempSubdirectory("nexusfree-outbox-").FullName;

    private string OutFile => Path.Combine(_dir, "req-test.json");

    [Fact]
    public async Task Partial_Answer_Is_Read_Again_Until_Complete_And_Deleted_Only_After_Parsing()
    {
        // An in-place writer (dispatchers before the atomic rename) leaves a truncated answer first.
        await File.WriteAllTextAsync(OutFile, Complete[..25], Encoding.UTF8, TestContext.Current.CancellationToken);
        var waiting = Engine.WaitForResultAsync(OutFile, "req-test", TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await Task.Delay(400, TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted);
        Assert.True(File.Exists(OutFile)); // never deleted before it parses

        await File.WriteAllTextAsync(OutFile, Complete, Encoding.UTF8, TestContext.Current.CancellationToken);
        var result = await waiting;

        Assert.True(result.Success);
        Assert.Equal("req-test", result.Id);
        Assert.Equal("nt authority\\system", result.Output);
        Assert.False(File.Exists(OutFile));
    }

    [Fact]
    public async Task Temporary_File_Is_Ignored_Until_Renamed()
    {
        var waiting = Engine.WaitForResultAsync(OutFile, "req-test", TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        string temp = OutFile + ".tmp";
        await File.WriteAllTextAsync(temp, Complete[..25], Encoding.UTF8, TestContext.Current.CancellationToken);

        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted);

        await File.WriteAllTextAsync(temp, Complete, Encoding.UTF8, TestContext.Current.CancellationToken);
        File.Move(temp, OutFile);
        var result = await waiting;

        Assert.True(result.Success);
        Assert.False(File.Exists(OutFile));
    }

    [Fact]
    public async Task Answer_That_Never_Parses_Times_Out_And_Is_Kept()
    {
        await File.WriteAllTextAsync(OutFile, "{\"id\":", Encoding.UTF8, TestContext.Current.CancellationToken);
        var result = await Engine.WaitForResultAsync(OutFile, "req-test", TimeSpan.FromMilliseconds(600), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(124, result.ExitCode);
        Assert.Contains("illisible", result.Error);
        Assert.True(File.Exists(OutFile));
    }

    [Fact]
    public async Task Cancellation_Returns_130()
    {
        using var cts = new CancellationTokenSource(200);
        var result = await Engine.WaitForResultAsync(OutFile, "req-test", TimeSpan.FromSeconds(10), cts.Token);
        Assert.Equal(130, result.ExitCode);
    }

    public void Dispose()
    {
        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(_dir,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                Microsoft.VisualBasic.FileIO.UICancelOption.DoNothing);
        }
        catch (Exception) { } // Keep the fixture when recycling is unavailable; never delete permanently.
    }
}
