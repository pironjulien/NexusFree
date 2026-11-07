using System.Globalization;
using System.Text.Json.Nodes;

namespace NexusFree.Tests;

public sealed class PruneAndLanguageTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("nexusfree-prune-").FullName;

    private string Make(string name, TimeSpan age)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, "{}");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    [Fact]
    public void Prune_Removes_Only_Stale_Answers_And_Temporary_Files()
    {
        string staleAnswer = Make("req-old.json", Engine.AnswerLifetime + TimeSpan.FromMinutes(5));
        string freshAnswer = Make("req-new.json", TimeSpan.FromMinutes(5));
        string staleTemp = Make("req-cut.json.tmp", Engine.TemporaryLifetime + TimeSpan.FromMinutes(5));
        string freshTemp = Make("req-writing.json.tmp", TimeSpan.FromSeconds(1));
        string foreign = Make("notes.json", TimeSpan.FromDays(30));

        int removed = Engine.PruneOutbox(_dir, DateTime.UtcNow);

        Assert.Equal(2, removed);
        Assert.False(File.Exists(staleAnswer));
        Assert.False(File.Exists(staleTemp));
        Assert.True(File.Exists(freshAnswer));
        Assert.True(File.Exists(freshTemp));
        Assert.True(File.Exists(foreign)); // only the bridge's own request files are touched
    }

    [Fact]
    public void Prune_Of_A_Missing_Outbox_Is_Harmless()
    {
        Assert.Equal(0, Engine.PruneOutbox(Path.Combine(_dir, "missing"), DateTime.UtcNow));
    }

    [Theory]
    [InlineData("en-US", "Exit code", "Checks that the NexusFree elevation bridge works")]
    [InlineData("de-DE", "Exit code", "Checks that the NexusFree elevation bridge works")]
    [InlineData("fr-FR", "Code de sortie", "Vérifie l'état opérationnel")]
    public async Task Texts_Follow_The_Windows_Display_Language(string culture, string exitCode, string statusDescription)
    {
        CultureInfo previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo(culture);
        try
        {
            Assert.Contains(exitCode, Strings.ExitCodeLine(0), StringComparison.Ordinal);
            var list = await McpServer.HandleRequestAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/list" }, TestContext.Current.CancellationToken);
            var tools = Assert.IsType<JsonArray>(list?["result"]?["tools"]);
            Assert.Contains(tools, t => t?["description"]?.ToString().StartsWith(statusDescription, StringComparison.Ordinal) == true);
            var unknown = await McpServer.HandleRequestAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "nope" }, TestContext.Current.CancellationToken);
            Assert.Equal(culture.StartsWith("fr", StringComparison.Ordinal) ? "Méthode non supportée : nope" : "Unsupported method: nope", unknown?["error"]?["message"]?.ToString());
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public void Status_Reads_The_Task_Through_The_Task_Scheduler_Api()
    {
        var status = Engine.GetStatus();
        string text = status["taskStatus"]?.ToString() ?? "";
        Assert.False(text.StartsWith("Error", StringComparison.Ordinal) || text.StartsWith("Erreur", StringComparison.Ordinal), text);
        Assert.DoesNotContain("\"NexusFree\",", text, StringComparison.Ordinal); // no raw schtasks.exe CSV any more
        Assert.Equal(Engine.TaskName, status["taskName"]?.ToString());
    }

    [Fact]
    public void A_Missing_Task_Is_Reported_Not_Thrown()
    {
        var info = SystemTask.Query("NexusFree-test-missing-" + Guid.NewGuid().ToString("N"));
        Assert.False(info.Found);
    }

    public void Dispose() => TestEnvironment.DeleteOwnTemporary(_dir);
}
