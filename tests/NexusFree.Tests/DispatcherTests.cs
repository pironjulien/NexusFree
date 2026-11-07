using System.Text;
using System.Text.Json.Nodes;

namespace NexusFree.Tests;

/// <summary>The SYSTEM dispatcher (<c>NexusFree.exe --dispatch</c>) on a temporary spool, run as the test account.</summary>
public sealed class DispatcherTests : IDisposable
{
    private const string Stranger = "S-1-5-21-1000000001-1000000002-1000000003-1001";

    private readonly string _spool = Directory.CreateTempSubdirectory("nexusfree-dispatch-").FullName;

    public DispatcherTests()
    {
        foreach (string folder in new[] { "inbox", "staging", "outbox", "logs" }) Directory.CreateDirectory(Path.Combine(_spool, folder));
    }

    private string Folder(string name) => Path.Combine(_spool, name);

    private string Request(string id, string json)
    {
        string file = Path.Combine(Folder("inbox"), id + ".json");
        File.WriteAllText(file, json, new UTF8Encoding(false));
        return file;
    }

    private static string RequestJson(string command) => new JsonObject { ["id"] = "x", ["command"] = command }.ToJsonString();

    private JsonObject Answer(string id)
    {
        string file = Path.Combine(Folder("outbox"), id + ".json");
        Assert.True(File.Exists(file), $"No answer {file}.");
        return Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(file, Encoding.UTF8)));
    }

    private Dispatcher For(params string[] owners) => new(_spool, owners, Dispatcher.FindPowerShell());

    [Fact]
    public async Task Request_Of_An_Allowed_Owner_Runs_And_Is_Answered_Atomically()
    {
        string request = Request("req-run", RequestJson("Write-Output 'été \"quoted\"'; cmd.exe /c echo native; [Console]::Error.WriteLine('on stderr'); exit 3"));
        var dispatcher = For(Dispatcher.OwnerOf(request));

        Assert.Equal(0, await dispatcher.RunAsync());

        var answer = Answer("req-run");
        Assert.Equal("req-run", answer["id"]?.ToString());
        Assert.False(answer["success"]?.GetValue<bool>());
        Assert.Equal(3, answer["exitCode"]?.GetValue<int>());
        string output = answer["output"]?.ToString() ?? "";
        Assert.Contains("été \"quoted\"", output, StringComparison.Ordinal); // UTF-8 and quotes kept
        Assert.Contains("native", output, StringComparison.Ordinal);
        Assert.Contains("on stderr", output, StringComparison.Ordinal);
        Assert.True(output.IndexOf("native", StringComparison.Ordinal) < output.IndexOf("on stderr", StringComparison.Ordinal));
        Assert.Equal("", answer["error"]?.ToString());
        Assert.True(DateTimeOffset.Parse(answer["started"]!.ToString(), System.Globalization.CultureInfo.InvariantCulture)
            <= DateTimeOffset.Parse(answer["finished"]!.ToString(), System.Globalization.CultureInfo.InvariantCulture));

        Assert.Empty(Directory.GetFiles(Folder("inbox")));
        Assert.Empty(Directory.GetFiles(Folder("staging")));
        Assert.Single(Directory.GetFiles(Folder("outbox"))); // no temporary answer left beside it

        string log = File.ReadAllText(Path.Combine(Folder("logs"), "elevation.log"), Encoding.UTF8);
        Assert.Contains("Running request req-run", log, StringComparison.Ordinal);
        Assert.Contains("ExitCode: 3", log, StringComparison.Ordinal);
        Assert.DoesNotContain("quoted", log, StringComparison.Ordinal); // never the command itself
    }

    [Fact]
    public async Task Successful_Command_Reports_Success()
    {
        string request = Request("req-ok", RequestJson("'fine'"));
        Assert.Equal(0, await For(Dispatcher.OwnerOf(request)).RunAsync());

        var answer = Answer("req-ok");
        Assert.True(answer["success"]?.GetValue<bool>());
        Assert.Equal(0, answer["exitCode"]?.GetValue<int>());
        Assert.Equal("fine", answer["output"]?.ToString().Trim());
    }

    [Fact]
    public async Task Request_Of_Another_Owner_Is_Refused_Without_Running()
    {
        string marker = Path.Combine(_spool, "ran.txt");
        Request("req-foreign", RequestJson($"Set-Content -LiteralPath '{marker}' -Value x"));

        Assert.Equal(0, await For(Stranger).RunAsync());

        var answer = Answer("req-foreign");
        Assert.False(answer["success"]?.GetValue<bool>());
        Assert.Equal(1, answer["exitCode"]?.GetValue<int>());
        Assert.Contains("S-1-5-", answer["error"]?.ToString() ?? "", StringComparison.Ordinal); // names the file's owner
        Assert.False(File.Exists(marker));
        Assert.Empty(Directory.GetFiles(Folder("staging")));
    }

    [Theory]
    [InlineData("{\"id\":\"x\"}")]
    [InlineData("{\"id\":\"x\",\"command\":\"   \"}")]
    [InlineData("{\"id\":\"x\",\"command\":42}")]
    [InlineData("not json")]
    public async Task Request_Without_A_Usable_Command_Is_Answered_With_An_Error(string json)
    {
        string request = Request("req-bad", json);
        Assert.Equal(0, await For(Dispatcher.OwnerOf(request)).RunAsync());

        var answer = Answer("req-bad");
        Assert.False(answer["success"]?.GetValue<bool>());
        Assert.Equal(1, answer["exitCode"]?.GetValue<int>());
        Assert.NotEqual("", answer["error"]?.ToString());
        Assert.Empty(Directory.GetFiles(Folder("staging")));
    }

    [Fact]
    public async Task Incomplete_Spool_Returns_1_And_Creates_Nothing()
    {
        Directory.Delete(Folder("staging"));
        string request = Request("req-wait", RequestJson("'x'"));

        Assert.Equal(1, await For(Dispatcher.OwnerOf(request)).RunAsync());

        Assert.False(Directory.Exists(Folder("staging"))); // never recreated without setup-system.ps1's ACLs
        Assert.True(File.Exists(request));
    }

    [Fact]
    public async Task Empty_Inbox_Returns_0()
    {
        Assert.Equal(0, await For(Stranger).RunAsync());
        Assert.Empty(Directory.GetFiles(Folder("outbox")));
    }

    [Fact]
    public void Output_Reads_Utf8_Lines_And_Falls_Back_To_The_Oem_Page_Per_Line()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var oem = Encoding.GetEncoding(850);
        byte[] bytes = [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. Encoding.UTF8.GetBytes("é utf8\r\n"), .. oem.GetBytes("é oem\r\n"), .. Encoding.UTF8.GetBytes("fin")];

        Assert.Equal("é utf8\r\né oem\r\nfin", Dispatcher.DecodeOutput(bytes, oem));
        Assert.Equal("", Dispatcher.DecodeOutput([], oem));
    }

    [Fact]
    public void Authorized_Account_Comes_From_A_Valid_Sid_Only()
    {
        string config = Path.Combine(_spool, "config.json");
        Assert.Null(Dispatcher.ReadAuthorizedSid(config)); // missing

        File.WriteAllText(config, "{\"userSid\":\"" + Stranger + "\"}");
        Assert.Equal(Stranger, Dispatcher.ReadAuthorizedSid(config));

        File.WriteAllText(config, "{\"userSid\":\"Everyone\"}");
        Assert.Null(Dispatcher.ReadAuthorizedSid(config));

        File.WriteAllText(config, "{\"userSid\":12}");
        Assert.Null(Dispatcher.ReadAuthorizedSid(config));

        File.WriteAllText(config, "{");
        Assert.Null(Dispatcher.ReadAuthorizedSid(config));
    }

    public void Dispose() => TestEnvironment.DeleteOwnTemporary(_spool);
}
