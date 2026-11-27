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
    public const string ServerVersion = "1.6.0";
    public static string? ConfiguredAgent { get; set; }
    public static string CurrentAgent { get; set; } = "antigravity";
    private static readonly HashSet<string> Protocols = ["2024-11-05", "2025-03-26", "2025-06-18", "2025-11-25"];

    public static async Task RunStdioAsync(CancellationToken ct = default)
    {
        var utf8 = new UTF8Encoding(false);
        using var reader = new StreamReader(Console.OpenStandardInput(), utf8);
        using var writer = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true };
        await RunAsync(reader, writer, HandleRequestAsync, ct);
    }

    /// <summary>
    /// The server loop: one JSON-RPC message per line, each request handled on its own task. A
    /// <c>notifications/cancelled</c> cancels the token of the request it names, which stops a running <c>free</c>
    /// command; as MCP asks, a cancelled request gets no response (its outcome is in the journal and the SYSTEM log).
    /// </summary>
    internal static async Task RunAsync(TextReader reader, TextWriter writer, Func<JsonObject, CancellationToken, Task<JsonObject?>> handle, CancellationToken ct)
    {
        var lockObj = new object();
        var inFlight = new System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.Ordinal);

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

            if (method == "notifications/cancelled")
            {
                // The id as written (1 and "1" are two requests).
                if (request["params"]?["requestId"] is { } cancelled && inFlight.TryGetValue(cancelled.ToJsonString(), out var target))
                {
                    try { target.Cancel(); } catch (ObjectDisposedException) { } // it has just ended
                }
                continue;
            }
            if (method == "notifications/initialized")
            {
                continue;
            }

            string? key = request["id"]?.ToJsonString();
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (key != null) inFlight[key] = cancellation;

            _ = Task.Run(async () =>
            {
                try
                {
                    var response = await handle(request, cancellation.Token);
                    if (response != null && !cancellation.IsCancellationRequested)
                    {
                        string json = response.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
                        lock (lockObj)
                        {
                            writer.WriteLine(json);
                        }
                    }
                }
                finally
                {
                    if (key != null) inFlight.TryRemove(new KeyValuePair<string, CancellationTokenSource>(key, cancellation));
                    cancellation.Dispose();
                }
            }, CancellationToken.None);
        }
    }

    /// <summary>A command's result as the tools show it: exit code, output, then the error if any.</summary>
    internal static string FormatResult(CommandResult res)
    {
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
        return sb.ToString();
    }

    /// <summary>What <c>free_result</c> and <c>--result</c> show for a request, and whether it is an error.</summary>
    internal static (string Text, bool IsError) FormatStatus(string id, RequestStatus status) => status.State switch
    {
        RequestState.Done => (FormatResult(status.Result!), !status.Result!.Success),
        RequestState.Running => (Strings.StillRunning(id, (status.SinceUtc ?? DateTime.UtcNow).ToLocalTime()), false),
        RequestState.Queued => (Strings.StillQueued(id), false),
        RequestState.Unknown => (Strings.UnknownRequest(id), true),
        _ => (Strings.InvalidRequestId, true),
    };

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
                    resultText = FormatResult(res);

                    Journal.Write(CurrentAgent, "free", command, res.Id, res.Success, res.ExitCode, sw.Elapsed.TotalMilliseconds, res.Error);
                }
                else if (toolName == "free_result")
                {
                    string requestId = (args["id"]?.GetValue<string>() ?? "").Trim();
                    var status = Engine.ReadResult(requestId);
                    sw.Stop();
                    (resultText, isError) = FormatStatus(requestId, status);
                    // Reading is the action journaled: its own success, never the command's (journaled by its free call).
                    Journal.Write(CurrentAgent, "free_result", null, status.State == RequestState.Invalid ? null : requestId, !isError || status.State == RequestState.Done,
                        status.State is RequestState.Unknown or RequestState.Invalid ? 1 : 0, sw.Elapsed.TotalMilliseconds,
                        status.State is RequestState.Unknown or RequestState.Invalid ? resultText : null);
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
                    ["content"] = new JsonArray(
                        new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = resultText
                        }),
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
        return new JsonArray(
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
                    ["required"] = new JsonArray("command")
                }
            },
            new JsonObject
            {
                ["name"] = "free_result",
                ["description"] = Strings.ResultDescription,
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["id"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = Strings.ResultIdDescription
                        }
                    },
                    ["required"] = new JsonArray("id")
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
            });
    }
}
