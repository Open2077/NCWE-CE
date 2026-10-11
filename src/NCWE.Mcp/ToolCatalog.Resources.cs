// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NCWE.Mcp;

// Resources (the guide, the live project, the prefab library), prompts and prefab storage.
internal sealed partial class ToolCatalog
{
    public JsonArray ResourcesJson() =>
    [
        new JsonObject { ["uri"] = "ncwe://guide", ["name"] = "guide", ["title"] = "How to build worlds with NCWE", ["description"] = "Conventions, workflows, asset folders and pitfalls (collision, occlusion, streaming).", ["mimeType"] = "text/markdown" },
        new JsonObject { ["uri"] = "ncwe://project", ["name"] = "project", ["title"] = "Current project", ["description"] = "What the open project changes (live from Studio).", ["mimeType"] = "application/json" },
        new JsonObject { ["uri"] = "ncwe://prefabs", ["name"] = "prefabs", ["title"] = "Prefab library", ["description"] = "Saved prefabs and their contents.", ["mimeType"] = "application/json" },
    ];

    public async Task<JsonObject> ReadResourceAsync(string uri, CancellationToken cancellation)
    {
        (string mime, string text) = uri switch
        {
            "ncwe://guide" => ("text/markdown", Guide.Text),
            "ncwe://project" => ("application/json", (await Call("api.project", null, cancellation)).ToJsonString(JsonOptions.Indented)),
            "ncwe://prefabs" => ("application/json", PrefabList().ToJsonString(JsonOptions.Indented)),
            _ => throw new RpcException(-32002, "Resource not found: " + uri)
        };
        return new JsonObject { ["contents"] = new JsonArray(new JsonObject { ["uri"] = uri, ["mimeType"] = mime, ["text"] = text }) };
    }

    public JsonArray PromptsJson() => new(Guide.Prompts.Select(p => (JsonNode)new JsonObject
    {
        ["name"] = p.Name, ["title"] = p.Title, ["description"] = p.Description,
        ["arguments"] = new JsonArray(p.Arguments.Select(x => (JsonNode)new JsonObject { ["name"] = x.Name, ["description"] = x.Description, ["required"] = x.Required }).ToArray())
    }).ToArray());

    public JsonObject GetPrompt(string name, JsonObject? arguments)
    {
        var prompt = Guide.Prompts.FirstOrDefault(p => p.Name == name) ?? throw new RpcException(-32602, "Unknown prompt: " + name);
        var values = new Dictionary<string, string>();
        if (arguments is not null) foreach (var (key, value) in arguments) values[key] = value?.ToString() ?? "";
        foreach (var argument in prompt.Arguments.Where(x => x.Required && !values.ContainsKey(x.Name)))
            throw new RpcException(-32602, $"Missing argument {argument.Name}.");
        return new JsonObject
        {
            ["description"] = prompt.Description,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = new JsonObject { ["type"] = "text", ["text"] = prompt.Build(values) } })
        };
    }

    // --- Prefabs (files in the prefab folder; the item format belongs to Studio) ---------------------------

    private string PrefabPath(string name)
    {
        string safe = Regex.Replace(name.Trim(), @"[^\w\- ]", "_").Trim();
        if (safe.Length == 0) throw new StudioException("A prefab needs a name.");
        return Path.Combine(_options.PrefabDirectory, safe + ".ncwe-prefab.json");
    }

    private async Task<ToolResult> PrefabSaveAsync(JsonObject a, CancellationToken ct)
    {
        string name = a["name"]!.GetValue<string>();
        var reply = await Call("api.prefab.capture", new JsonObject { ["ids"] = a["ids"]!.DeepClone(), ["name"] = name, ["pivot"] = a["pivot"]?.DeepClone() }, ct);
        var prefab = reply["prefab"]!;
        var file = new JsonObject
        {
            ["name"] = name, ["description"] = a["description"]?.GetValue<string>() ?? "", ["saved"] = DateTimeOffset.Now.ToString("O"),
            ["items"] = prefab["Items"]?.AsArray().Count ?? 0, ["prefab"] = prefab.DeepClone()
        };
        Directory.CreateDirectory(_options.PrefabDirectory);
        string path = PrefabPath(name);
        await File.WriteAllTextAsync(path + ".tmp", file.ToJsonString(JsonOptions.Indented), ct);
        File.Move(path + ".tmp", path, true);
        return ToolResult.Json(new JsonObject { ["saved"] = name, ["file"] = path, ["items"] = file["items"]!.DeepClone(), ["skipped"] = reply["skipped"]?.DeepClone() });
    }

    private async Task<ToolResult> PrefabPlaceAsync(JsonObject a, CancellationToken ct)
    {
        string path = PrefabPath(a["name"]!.GetValue<string>());
        if (!File.Exists(path)) return ToolResult.Fail($"No prefab named {a["name"]} (prefab_list).");
        var file = JsonNode.Parse(await File.ReadAllTextAsync(path, ct))!;
        var request = new JsonObject { ["prefab"] = file["prefab"]!.DeepClone(), ["position"] = a["position"]!.DeepClone(), ["ground"] = a["ground"]?.DeepClone() };
        if (a["rotation"] is { } rotation) request["rotation"] = rotation.DeepClone();
        else if (a["yaw"] is { } yaw) request["yaw"] = yaw.DeepClone();
        return ToolResult.Json(await Call("api.prefab.place", request, ct, TimeSpan.FromMinutes(2)));
    }

    private JsonObject PrefabList()
    {
        var list = new JsonArray();
        if (Directory.Exists(_options.PrefabDirectory))
            foreach (var path in Directory.EnumerateFiles(_options.PrefabDirectory, "*.ncwe-prefab.json").Order(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var file = JsonNode.Parse(File.ReadAllText(path))!;
                    var kinds = file["prefab"]?["Items"]?.AsArray().GroupBy(i => i?["Kind"]?.GetValue<string>() ?? "?").ToDictionary(g => g.Key, g => g.Count());
                    list.Add(new JsonObject
                    {
                        ["name"] = file["name"]?.GetValue<string>(), ["description"] = file["description"]?.GetValue<string>(), ["saved"] = file["saved"]?.GetValue<string>(),
                        ["items"] = file["items"]?.DeepClone(), ["contents"] = kinds is null ? null : new JsonObject(kinds.Select(k => KeyValuePair.Create(k.Key, (JsonNode?)k.Value)))
                    });
                }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { list.Add(new JsonObject { ["file"] = path, ["error"] = ex.Message }); }
            }
        return new JsonObject { ["folder"] = _options.PrefabDirectory, ["prefabs"] = list };
    }

    private Task<ToolResult> PrefabDeleteAsync(JsonObject a, CancellationToken ct)
    {
        string path = PrefabPath(a["name"]!.GetValue<string>());
        if (!File.Exists(path)) return Task.FromResult(ToolResult.Fail($"No prefab named {a["name"]}."));
        File.Delete(path);
        return Task.FromResult(ToolResult.Json(new JsonObject { ["deleted"] = a["name"]!.DeepClone() }));
    }
}
