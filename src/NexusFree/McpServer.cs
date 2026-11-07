using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace NexusFree;

public sealed class McpServer
{
    public const string ServerName = "nexus-free";
    public const string ServerVersion = "1.3.1";
    public static string? ConfiguredAgent { get; set; }
    public static string CurrentAgent { get; set; } = "antigravity";
    private static readonly HashSet<string> Protocols = ["2024-11-05", "2025-03-26", "2025-06-18", "2025-11-25"];

    public static async Task RunStdioAsync(CancellationToken ct = default)
    {
        var utf8 = new UTF8Encoding(false);
        using var reader = new StreamReader(Console.OpenStandardInput(), utf8);
        using var writer = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true };

        var lockObj = new object();

        while (!ct.IsCancellationRequested && await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonObject? request;
            try
            {
                request = JsonNode.Parse(line) as JsonObject;
            }
            catch
            {
                continue;
            }

            if (request == null) continue;

            string? method = request["method"]?.GetValue<string>();
            if (string.IsNullOrEmpty(method)) continue;

            if (method == "notifications/cancelled" || method == "notifications/initialized")
            {
                continue;
            }

            _ = Task.Run(async () =>
            {
                var response = await HandleRequestAsync(request, ct);
                if (response != null)
                {
                    string json = response.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
                    lock (lockObj)
                    {
                        writer.WriteLine(json);
                    }
                }
            }, ct);
        }
    }

    public static async Task<JsonObject?> HandleRequestAsync(JsonObject req, CancellationToken ct)
    {
        string? method = req["method"]?.GetValue<string>();
        var id = req["id"];

        if (string.IsNullOrEmpty(method)) return null;

        if (method == "initialize")
        {
            string? clientName = req["params"]?["clientInfo"]?["name"]?.ToString();
            CurrentAgent = Journal.IdentifyAgent(clientName);

            return new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id?.DeepClone(),
                ["result"] = new JsonObject
                {
                    ["protocolVersion"] = Protocols.Contains(req["params"]?["protocolVersion"]?.ToString() ?? "")
                        ? req["params"]!["protocolVersion"]!.ToString()
                        : "2025-11-25",
                    ["capabilities"] = new JsonObject
                    {
                        ["tools"] = new JsonObject()
                    },
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = ServerName,
                        ["version"] = ServerVersion
                    },
                    ["instructions"] = Strings.Instructions
                }
            };
        }

        if (method == "ping")
        {
            return new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id?.DeepClone(),
                ["result"] = new JsonObject()
            };
        }

        if (method == "tools/list")
        {
            return new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id?.DeepClone(),
                ["result"] = new JsonObject
                {
                    ["tools"] = GetToolsSchema()
                }
            };
        }

        if (method == "tools/call")
        {
            var paramsObj = req["params"] as JsonObject;
            string toolName = paramsObj?["name"]?.GetValue<string>() ?? "";
            var args = paramsObj?["arguments"] as JsonObject ?? new JsonObject();

            bool isError = false;
            string resultText;
            var sw = Stopwatch.StartNew();

            try
            {
                if (toolName == "free" || toolName == "nexus_free_exec")
                {
                    string command = args["command"]?.GetValue<string>() ?? "";
                    int timeout = args["timeout_seconds"]?.GetValue<int>() ?? 600;

                    var res = await Engine.ExecuteAsync(command, timeout, ct);
                    sw.Stop();
                    isError = !res.Success;

                    var sb = new StringBuilder();
                    sb.AppendLine(Strings.ExitCodeLine(res.ExitCode));
                    if (!string.IsNullOrWhiteSpace(res.Output))
                    {
                        sb.AppendLine(res.Output.TrimEnd());
                    }
                    if (!string.IsNullOrWhiteSpace(res.Error))
                    {
                        sb.AppendLine(Strings.ErrorLine(res.Error.TrimEnd()));
                    }
                    resultText = sb.ToString();

                    Journal.Write(CurrentAgent, "free", command, res.Id, res.Success, res.ExitCode, sw.Elapsed.TotalMilliseconds, res.Error);
                }
                else if (toolName == "free_status")
                {
                    var status = Engine.GetStatus();
                    sw.Stop();
                    resultText = status.ToJsonString(Engine.ReadableJson);
                    bool installed = status["installed"]?.GetValue<bool>() ?? false;
                    Journal.Write(CurrentAgent, "free_status", null, null, installed, installed ? 0 : 1, sw.Elapsed.TotalMilliseconds, null);
                }
                else
                {
                    sw.Stop();
                    isError = true;
                    resultText = Strings.UnknownTool(toolName);
                    Journal.Write(CurrentAgent, toolName, null, null, false, 1, sw.Elapsed.TotalMilliseconds, "Unknown tool");
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                isError = true;
                resultText = Strings.ToolFailed(toolName, ex.Message);
                Journal.Write(CurrentAgent, toolName, null, null, false, 1, sw.Elapsed.TotalMilliseconds, ex.Message);
            }

            return new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id?.DeepClone(),
                ["result"] = new JsonObject
                {
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = resultText
                        }
                    },
                    ["isError"] = isError
                }
            };
        }

        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject
            {
                ["code"] = -32601,
                ["message"] = Strings.UnsupportedMethod(method)
            }
        };
    }

    private static JsonArray GetToolsSchema()
    {
        return new JsonArray
        {
            new JsonObject
            {
                ["name"] = "free",
                ["description"] = Strings.FreeDescription,
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["command"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = Strings.CommandDescription
                        },
                        ["timeout_seconds"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["description"] = Strings.TimeoutDescription
                        }
                    },
                    ["required"] = new JsonArray { "command" }
                }
            },
            new JsonObject
            {
                ["name"] = "free_status",
                ["description"] = Strings.StatusDescription,
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject()
                }
            }
        };
    }
}
