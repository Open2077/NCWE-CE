// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NCWE.Core;

namespace NCWE.Export;

public sealed record WorldBuilderReport(string JsonPath, string GroupName, int ExportedNodes, int SkippedTransforms, int SkippedDeletions,
    string TargetImporter, string[] Limitations);

// Writes the JSON consumed by World Builder/entSpawner's WolvenKit import script
// (import_object_spawner.wscript 1.0.4, entSpawner commit d9680f4). That importer only
// creates new sectors, so only NCWE's authored objects are representable; edits to
// vanilla placements are reported as skipped, never silently dropped.
public static partial class WorldBuilderExporter
{
    public const string ImporterVersion = "1.0.4";
    public const string TargetImporter = "CP77_entSpawner import_object_spawner.wscript 1.0.4 (commit d9680f4d7caaaf001c214e4614cc6d68cceeb0d3)";
    // entSpawner spawnable defaults.
    private const float PrimaryRange = 120, SecondaryRange = 100;
    private const int Uk10 = 1024, Uk11 = 512;
    private const float StreamingMargin = 150;

    public static string GroupName(string projectName)
    {
        var name = InvalidName().Replace(projectName.Trim().ToLowerInvariant(), "_").Trim('_');
        return name.Length == 0 ? "ncwe_world" : name.Length > 48 ? name[..48] : name;
    }

    public static JsonObject Build(ProjectData project, Func<MeshReference, bool>? resolves = null)
    {
        EditorDocument.ValidateProject(project);
        var added = project.Changes.Added.Values.OrderBy(a => a.Id, StringComparer.Ordinal).ToArray();
        if (added.Length == 0) throw new InvalidOperationException("World Builder export needs at least one added object; it cannot express edits to existing placements.");
        foreach (var item in added)
            if (resolves is not null && !resolves(item.Mesh)) throw new FileNotFoundException("Added mesh is not present in this archive profile: " + item.Mesh.Path);
        string name = GroupName(project.Name);
        var min = added.Select(a => a.Transform.Position).Aggregate(System.Numerics.Vector3.Min) - new System.Numerics.Vector3(StreamingMargin);
        var max = added.Select(a => a.Transform.Position).Aggregate(System.Numerics.Vector3.Max) + new System.Numerics.Vector3(StreamingMargin);
        var nodes = new JsonArray();
        foreach (var item in added)
        {
            var t = item.Transform.Validated();
            nodes.Add(new JsonObject
            {
                ["type"] = "worldMeshNode",
                ["name"] = item.Name,
                ["position"] = Vector(t.Position),
                ["streamingRefPoint"] = Vector(t.Position),
                ["rotation"] = new JsonObject { ["i"] = t.Rotation.X, ["j"] = t.Rotation.Y, ["k"] = t.Rotation.Z, ["r"] = t.Rotation.W },
                ["scale"] = Vector(t.Scale),
                ["primaryRange"] = PrimaryRange, ["secondaryRange"] = SecondaryRange, ["uk10"] = Uk10, ["uk11"] = Uk11,
                ["nodeRef"] = "",
                ["data"] = new JsonObject
                {
                    ["mesh"] = new JsonObject { ["DepotPath"] = new JsonObject { ["$storage"] = "string", ["$value"] = item.Mesh.Path } },
                    ["meshAppearance"] = new JsonObject { ["$storage"] = "string", ["$value"] = string.IsNullOrEmpty(item.Mesh.Appearance) ? "default" : item.Mesh.Appearance }
                }
            });
        }
        return new JsonObject
        {
            ["version"] = ImporterVersion,
            ["name"] = name,
            ["xlFormat"] = 0,
            ["sectors"] = new JsonArray(new JsonObject
            {
                ["name"] = name + "_0",
                ["category"] = "Exterior",
                ["level"] = 1,
                ["min"] = Vector(min), ["max"] = Vector(max),
                ["variantIndices"] = new JsonArray(0),
                ["nodes"] = nodes
            })
        };
    }

    public static WorldBuilderReport Write(ProjectData project, string outputDirectory, string gameDirectory, Func<MeshReference, bool>? resolves = null)
    {
        string root = Path.GetFullPath(outputDirectory);
        string game = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDirectory)) + Path.DirectorySeparatorChar;
        if ((root + Path.DirectorySeparatorChar).StartsWith(game, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Choose an export folder outside the game installation.");
        var json = Build(project, resolves);
        string name = json["name"]!.GetValue<string>();
        Directory.CreateDirectory(root);
        // entSpawner's convention: <group>_exported.json, placed in the WolvenKit project's raw folder.
        string path = Path.Combine(root, name + "_exported.json");
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
        int skippedMoves = project.Changes.Modified.Values.Count(m => !m.Deleted), skippedDeletes = project.Changes.Modified.Values.Count(m => m.Deleted);
        var limitations = new List<string>
        {
            "Only added static meshes are exported, as worldMeshNode entries in one Exterior level-1 sector.",
            "Run the entSpawner import script inside WolvenKit to build the sectors, block and .xl file; NCWE does not pack them for this backend.",
            "Compatibility with the importer is structural until verified in WolvenKit."
        };
        if (skippedMoves + skippedDeletes > 0) limitations.Insert(0, $"Skipped {skippedMoves} moved and {skippedDeletes} deleted vanilla placements: World Builder cannot patch existing sectors. Use the ArchiveXL export for them.");
        int dressed = project.Changes.Added.Values.Count(a => !a.Materials.IsDefaultOrEmpty);
        if (dressed > 0) limitations.Insert(0, $"{dressed} mesh(es) wear project materials: this backend exports them with their own materials. Use the ArchiveXL or Open77 export to keep the materials.");
        return new(path, name, project.Changes.Added.Count, skippedMoves, skippedDeletes, TargetImporter, limitations.ToArray());
    }

    private static JsonObject Vector(System.Numerics.Vector3 v) => new() { ["x"] = v.X, ["y"] = v.Y, ["z"] = v.Z };

    [GeneratedRegex("[^a-z0-9_]+")]
    private static partial Regex InvalidName();
}
