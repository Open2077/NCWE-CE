// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NCWE.Studio.Session;

// Saved prefabs: one "<name>.ncwe-prefab.json" per prefab (the format the MCP server's
// prefab_save writes, so agents and the editor share one library) and an optional
// "<name>.ncwe-prefab.png" thumbnail. The folder is <state>\Prefabs (%LOCALAPPDATA%\NCWE\Prefabs).
internal static partial class PrefabLibrary
{
    public sealed record Entry(string Name, string Description, DateTimeOffset Saved, int Items, string Path, string? Thumbnail, string Contents);

    public static string Directory => System.IO.Path.Combine(StudioSettings.StateDirectory, "Prefabs");

    // The JSON shape of prefabs and clipboard contents (the automation pipe's own settings).
    public static readonly JsonSerializerOptions Json = new()
    {
        IncludeFields = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    [GeneratedRegex(@"[^\w\- ]")] private static partial Regex Unsafe();

    public static string PathOf(string name)
    {
        string safe = Unsafe().Replace(name.Trim(), "_").Trim();
        if (safe.Length == 0) throw new InvalidOperationException("A prefab needs a name.");
        return System.IO.Path.Combine(Directory, safe + ".ncwe-prefab.json");
    }

    public static string ThumbnailOf(string path) => path[..^".json".Length] + ".png";

    public static IReadOnlyList<Entry> List()
    {
        var result = new List<Entry>();
        if (!System.IO.Directory.Exists(Directory)) return result;
        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*.ncwe-prefab.json"))
        {
            try
            {
                var file = JsonNode.Parse(File.ReadAllText(path))!;
                var items = file["prefab"]?["Items"]?.AsArray();
                var kinds = items?.GroupBy(i => i?["Kind"]?.GetValue<string>() ?? "?").Select(g => $"{g.Count()} {g.Key}{(g.Count() == 1 ? "" : g.Key == "mesh" ? "es" : "s")}");
                DateTimeOffset.TryParse(file["saved"]?.GetValue<string>(), out var saved);
                string thumbnail = ThumbnailOf(path);
                result.Add(new(file["name"]?.GetValue<string>() ?? System.IO.Path.GetFileName(path), file["description"]?.GetValue<string>() ?? "", saved,
                    items?.Count ?? 0, path, File.Exists(thumbnail) ? thumbnail : null, kinds is null ? "" : string.Join(" · ", kinds)));
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException) { }
        }
        return result.OrderByDescending(e => e.Saved).ToList();
    }

    public static StudioSession.Prefab Load(string path)
    {
        var file = JsonNode.Parse(File.ReadAllText(path)) ?? throw new InvalidDataException("Empty prefab file.");
        return file["prefab"].Deserialize<StudioSession.Prefab>(Json) ?? throw new InvalidDataException("This prefab file has no prefab.");
    }

    // Writes (or replaces) a prefab; returns its path.
    public static string Save(StudioSession.Prefab prefab, string name, string description)
    {
        System.IO.Directory.CreateDirectory(Directory);
        string path = PathOf(name);
        var file = new JsonObject
        {
            ["name"] = name.Trim(), ["description"] = description, ["saved"] = DateTimeOffset.Now.ToString("O"),
            ["items"] = prefab.Items.Length, ["prefab"] = JsonSerializer.SerializeToNode(prefab with { Name = name.Trim() }, Json)
        };
        File.WriteAllText(path + ".tmp", file.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
        return path;
    }

    public static void Delete(string path)
    {
        File.Delete(path);
        if (File.Exists(ThumbnailOf(path))) File.Delete(ThumbnailOf(path));
    }

    public static string Rename(string path, string name)
    {
        var file = JsonNode.Parse(File.ReadAllText(path)) ?? throw new InvalidDataException("Empty prefab file.");
        string target = PathOf(name);
        if (!string.Equals(target, path, StringComparison.OrdinalIgnoreCase) && File.Exists(target)) throw new InvalidOperationException("A prefab already has this name.");
        file["name"] = name.Trim();
        if (file["prefab"] is JsonObject prefab) prefab["Name"] = name.Trim();
        File.WriteAllText(target + ".tmp", file.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(target + ".tmp", target, true);
        if (!string.Equals(target, path, StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(ThumbnailOf(path))) File.Move(ThumbnailOf(path), ThumbnailOf(target), true);
            File.Delete(path);
        }
        return target;
    }
}
