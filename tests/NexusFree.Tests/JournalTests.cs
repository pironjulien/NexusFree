using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;

namespace NexusFree.Tests;

public class JournalTests
{
    [Theory]
    [InlineData("antigravity", "antigravity")]
    [InlineData("jetski", "antigravity")]
    [InlineData("Claude Code", "claude")]
    [InlineData("claude-desktop", "claude")]
    [InlineData("codex-cli", "codex")]
    [InlineData(null, "antigravity")]
    public void IdentifyAgent_RecognizesAgents(string? client, string expected)
    {
        McpServer.ConfiguredAgent = null;
        var identified = Journal.IdentifyAgent(client);
        Assert.Equal(expected, identified);
    }

    [Theory]
    [InlineData("dism /online /cleanup-image /checkhealth", "dism.exe")]
    [InlineData("sfc /scannow", "sfc.exe")]
    [InlineData("whoami", "whoami.exe")]
    [InlineData("& 'C:\\Windows\\System32\\cmd.exe' /c dir", "cmd.exe")]
    [InlineData("\"C:\\Program Files\\app.exe\" --arg", "app.exe")]
    [InlineData("Restart-Service wuauserv", "powershell.exe")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ExtractApp_ReturnsProperBinary(string? cmd, string? expected)
    {
        var app = Journal.ExtractApp(cmd);
        Assert.Equal(expected, app);
    }

    [Fact]
    public void CleanError_SanitizesAndLimitsLength()
    {
        string raw = "Accès refusé: [Détail sensible à l'écran]";
        var cleaned = Journal.CleanError(raw);
        Assert.Equal("Accès refusé", cleaned);

        string longText = new string('A', 120);
        var truncated = Journal.CleanError(longText);
        Assert.NotNull(truncated);
        Assert.Equal(80, truncated.Length);
    }

    [Fact]
    public void Write_CreatesStandardJsonlRow()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "nexusfree-journal-test-" + Guid.NewGuid().ToString("N"));
        string prevHome = Environment.GetEnvironmentVariable("NEXUS_JOURNAL_HOME") ?? "";
        Environment.SetEnvironmentVariable("NEXUS_JOURNAL_HOME", tempDir);

        try
        {
            Journal.Write("antigravity", "free", "dism /online", "req-test-1", true, 0, 42.4, null);

            string nowMonth = DateTime.UtcNow.ToString("yyyy-MM");
            string expectedFile = Path.Combine(tempDir, "free", "antigravity", $"{nowMonth}.jsonl");

            Assert.True(File.Exists(expectedFile), $"Le fichier {expectedFile} doit exister.");
            string[] lines = File.ReadAllLines(expectedFile);
            Assert.Single(lines);

            var json = JsonNode.Parse(lines[0]) as JsonObject;
            Assert.NotNull(json);
            Assert.Equal(1, json["v"]?.GetValue<int>());
            Assert.Equal("antigravity", json["agent"]?.ToString());
            Assert.Equal(McpServer.ServerVersion, json["outil"]?.ToString());
            Assert.Equal("req-test-1", json["appel"]?.ToString());
            Assert.Equal("dism.exe", json["app"]?.ToString());
            Assert.Null(json["cadre"]);
            Assert.Equal("free", json["action"]?.ToString());
            Assert.Equal("elevation:system", json["technique"]?.ToString());
            Assert.Equal("✓", json["issue"]?.ToString());
            Assert.Equal(42, json["ms"]?.GetValue<int>());
            Assert.False(json["premier_plan"]?.GetValue<bool>());
            Assert.Null(json["erreur"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NEXUS_JOURNAL_HOME", string.IsNullOrEmpty(prevHome) ? null : prevHome);
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
