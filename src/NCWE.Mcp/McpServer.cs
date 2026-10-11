// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NCWE.Mcp;

// Model Context Protocol over stdio: newline-delimited JSON-RPC 2.0 messages. Implements the
// server side of lifecycle (initialize), tools, resources and prompts. Requests run concurrently
// (Studio calls are serialized by the link); notifications/cancelled cancels a running call.
internal sealed class McpServer(ToolCatalog catalog)
{
    public const string Name = "ncwe";
    public static readonly string Version = typeof(McpServer).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    // The AI client that initialized this server ("claude-code 2.1"), sent along with every Studio command.
    public static string Client { get; private set; } = "mcp";
    private static readonly string[] Supported = ["2025-06-18", "2025-03-26", "2024-11-05"];
    private readonly object _write = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();
    private Stream _output = Stream.Null;
    private string _protocol = Supported[0];

    public async Task RunAsync(Stream input, Stream output)
    {
        _output = output;
        using var reader = new StreamReader(input, new UTF8Encoding(false));
        var pending = new List<Task>();
        while (await reader.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode? message;
            try { message = JsonNode.Parse(line); }
            catch (JsonException) { Send(ErrorReply(null, -32700, "Parse error")); continue; }
            if (message is JsonArray batch) { foreach (var item in batch.OfType<JsonObject>()) pending.Add(HandleAsync(item)); }
            else if (message is JsonObject single) pending.Add(HandleAsync(single));
            else Send(ErrorReply(null, -32600, "Invalid request"));
            pending.RemoveAll(t => t.IsCompleted);
        }
        // stdin closed: the client is gone; let running calls finish briefly.
        await Task.WhenAny(Task.WhenAll(pending), Task.Delay(TimeSpan.FromSeconds(10)));
    }

    // Exposed for tests: one message in, the reply (null for notifications) out.
    public async Task<JsonObject?> HandleForTestAsync(JsonObject message)
    {
        JsonObject? reply = null;
        await HandleCoreAsync(message, r => reply = r);
        return reply;
    }

    private Task HandleAsync(JsonObject message) => HandleCoreAsync(message, Send);

    private async Task HandleCoreAsync(JsonObject message, Action<JsonObject> reply)
    {
        var id = message["id"]?.DeepClone();
        string? method = message["method"]?.GetValue<string>();
        var parameters = message["params"] as JsonObject ?? new JsonObject();
        if (method is null) return;   // a response to us (we send no requests)
        bool notification = id is null;
        try
        {
            switch (method)
            {
                case "notifications/cancelled":
                    if (parameters["requestId"]?.ToJsonString() is { } key && _running.TryRemove(key, out var cts)) cts.Cancel();
                    return;
                case var _ when method.StartsWith("notifications/", StringComparison.Ordinal): return;
            }
            if (notification) return;
            JsonNode result = method switch
            {
                "initialize" => Initialize(parameters),
                "ping" => new JsonObject(),
                "tools/list" => new JsonObject { ["tools"] = catalog.ListJson() },
                "tools/call" => await CallToolAsync(id!, parameters),
                "resources/list" => new JsonObject { ["resources"] = catalog.ResourcesJson() },
                "resources/templates/list" => new JsonObject { ["resourceTemplates"] = new JsonArray() },
                "resources/read" => await catalog.ReadResourceAsync(parameters["uri"]?.GetValue<string>() ?? "", CancellationToken.None),
                "prompts/list" => new JsonObject { ["prompts"] = catalog.PromptsJson() },
                "prompts/get" => catalog.GetPrompt(parameters["name"]?.GetValue<string>() ?? "", parameters["arguments"] as JsonObject),
                "logging/setLevel" => new JsonObject(),
                "completion/complete" => new JsonObject { ["completion"] = new JsonObject { ["values"] = new JsonArray(), ["hasMore"] = false } },
                _ => throw new RpcException(-32601, "Method not found: " + method)
            };
            reply(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });
        }
        catch (RpcException ex) { if (!notification) reply(ErrorReply(id, ex.Code, ex.Message)); }
        catch (Exception ex)
        {
            Log.Write($"{method} failed: {ex}");
            if (!notification) reply(ErrorReply(id, -32603, ex.Message));
        }
    }

    private JsonObject Initialize(JsonObject parameters)
    {
        string requested = parameters["protocolVersion"]?.GetValue<string>() ?? Supported[0];
        _protocol = Supported.Contains(requested) ? requested : Supported[0];
        // Studio's MCP window shows which agent sends each command.
        if (parameters["clientInfo"]?["name"]?.ToString() is { Length: > 0 } client)
            Client = parameters["clientInfo"]?["version"]?.ToString() is { Length: > 0 } version ? $"{client} {version}" : client;
        Log.Write($"initialize: client {parameters["clientInfo"]?["name"]} {parameters["clientInfo"]?["version"]}, protocol {requested} -> {_protocol}");
        return new JsonObject
        {
            ["protocolVersion"] = _protocol,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = false },
                ["resources"] = new JsonObject { ["listChanged"] = false, ["subscribe"] = false },
                ["prompts"] = new JsonObject { ["listChanged"] = false },
                ["logging"] = new JsonObject()
            },
            ["serverInfo"] = new JsonObject { ["name"] = Name, ["title"] = "NCWE world editor", ["version"] = Version },
            ["instructions"] = Guide.Instructions
        };
    }

    private async Task<JsonNode> CallToolAsync(JsonNode id, JsonObject parameters)
    {
        string name = parameters["name"]?.GetValue<string>() ?? throw new RpcException(-32602, "Missing tool name.");
        var arguments = parameters["arguments"] as JsonObject ?? new JsonObject();
        using var cts = new CancellationTokenSource();
        string key = id.ToJsonString();
        _running[key] = cts;
        try
        {
            var result = await catalog.CallAsync(name, arguments, cts.Token);
            return result.ToJson();
        }
        catch (UnknownToolException) { throw new RpcException(-32602, "Unknown tool: " + name); }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { return ToolResult.Fail("Cancelled.").ToJson(); }
        catch (Exception ex) when (ex is not RpcException)
        {
            // Tool failures are results the model can read and act on, not protocol errors.
            Log.Write($"tool {name} failed: {ex.Message}");
            return ToolResult.Fail(ex.Message).ToJson();
        }
        finally { _running.TryRemove(key, out _); }
    }

    private static JsonObject ErrorReply(JsonNode? id, int code, string message)
        => new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private void Send(JsonObject message)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(message.ToJsonString(JsonOptions.Compact) + "\n");
        lock (_write) { _output.Write(bytes); _output.Flush(); }
    }
}

internal sealed class RpcException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };
    public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
}
