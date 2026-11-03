using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace NexusFree.Tests;

public class ProtocolTests
{
    [Fact]
    public async Task Initialize_Returns_ValidCapabilities_And_ServerInfo()
    {
        var initReq = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["protocolVersion"] = "2025-11-25"
            }
        };

        var resp = await McpServer.HandleRequestAsync(initReq, CancellationToken.None);
        Assert.NotNull(resp);
        Assert.Equal("2.0", resp["jsonrpc"]?.ToString());
        Assert.Equal(1, resp["id"]?.GetValue<int>());

        var result = resp["result"] as JsonObject;
        Assert.NotNull(result);
        Assert.Equal("nexus-free", result["serverInfo"]?["name"]?.ToString());
        Assert.NotNull(result["capabilities"]?["tools"]);
    }

    [Fact]
    public async Task ToolsList_Contains_Free_And_FreeStatus()
    {
        var listReq = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 2,
            ["method"] = "tools/list"
        };

        var resp = await McpServer.HandleRequestAsync(listReq, CancellationToken.None);
        Assert.NotNull(resp);

        var tools = resp["result"]?["tools"] as JsonArray;
        Assert.NotNull(tools);
        Assert.True(tools.Count >= 2);

        bool hasFree = false;
        bool hasStatus = false;

        foreach (var t in tools)
        {
            string name = t?["name"]?.ToString() ?? "";
            if (name == "free") hasFree = true;
            if (name == "free_status") hasStatus = true;
        }

        Assert.True(hasFree, "L'outil 'free' doit être présent.");
        Assert.True(hasStatus, "L'outil 'free_status' doit être présent.");
    }

    [Fact]
    public async Task Ping_Returns_EmptyObject()
    {
        var pingReq = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "test-ping",
            ["method"] = "ping"
        };

        var resp = await McpServer.HandleRequestAsync(pingReq, CancellationToken.None);
        Assert.NotNull(resp);
        Assert.NotNull(resp["result"]);
    }

    [Fact]
    public async Task FreeStatus_ToolCall_Returns_Json_Status()
    {
        Assert.Equal(TestEnvironment.JournalHome, Environment.GetEnvironmentVariable("NEXUS_JOURNAL_HOME"));
        var callReq = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 3,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "free_status"
            }
        };

        var resp = await McpServer.HandleRequestAsync(callReq, CancellationToken.None);
        Assert.NotNull(resp);
        var content = resp["result"]?["content"] as JsonArray;
        Assert.NotNull(content);
        Assert.NotEmpty(content);
        string text = content[0]?["text"]?.ToString() ?? "";
        Assert.Contains("taskName", text);
        var journal = Path.Combine(TestEnvironment.JournalHome, "free", McpServer.CurrentAgent, $"{DateTime.UtcNow:yyyy-MM}.jsonl");
        Assert.True(File.Exists(journal), "The real tool call must journal only inside the test host's temporary home.");
        Assert.Contains(File.ReadLines(journal), line => JsonNode.Parse(line)?["action"]?.GetValue<string>() == "free_status");
    }
}
