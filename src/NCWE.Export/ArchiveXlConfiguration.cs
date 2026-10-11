// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using System.Text.Json.Nodes;
using NCWE.Core;

namespace NCWE.Export;

// ArchiveXL 1.27.1 uses yaml-cpp, which also accepts JSON, as does the pinned
// World Builder import script's xlFormat=0 output. No hand-written YAML escaping.
public static class ArchiveXlConfiguration
{
    public const string TargetVersion = "1.27.1";
    // Game road pieces and cables (worldBendedMeshNode, worldCableMeshNode) are only ever deleted,
    // when an editor road or cable replaces them.
    // Whole-node families (destructibles, lights, sounds, effects, decals...) use the same
    // guarded node mutation/deletion as static meshes: only the placement setup changes.
    // extraDeletions: nodes the export replaces outside the project's placement edits (terrain
    // collision height fields), deleted with the same guard (the sector's placement count).
    public static string Build(ProjectData project, IReadOnlyDictionary<ulong, SectorSnapshot> freshSources, string? addedBlock, IReadOnlyDictionary<string, string>? patches = null, string? texts = null,
        IReadOnlyCollection<NCWE.Formats.Terrain.ExtraNodeDeletion>? extraDeletions = null, IReadOnlyCollection<ProxyCorrection>? proxies = null)
    {
        var errors = ExportPreparation.Validate(project, freshSources);
        if (errors.Length != 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        var streaming = new JsonObject();
        if (addedBlock is not null) streaming["blocks"] = new JsonArray(JsonValue.Create(addedBlock));
        var sectors = new JsonArray();
        foreach (var group in project.Changes.Modified.Values.GroupBy(e => e.Source.Sector.Hash).OrderBy(g => g.Key))
        {
            var source = freshSources[group.Key];
            var mutations = new JsonArray(); var deletions = new JsonArray();
            foreach (var placement in group.GroupBy(e => e.Source.PlacementIndex).OrderBy(g => g.Key))
            {
                var anchor = placement.First().Source;
                bool instanced = anchor.NodeType == "worldInstancedMeshNode", bended = ExportPreparation.IsBended(anchor.NodeType);
                bool collision = anchor.NodeType == "worldCollisionNode";
                bool whole = NodeFamilies.WholeNode.Contains(anchor.NodeType);
                if ((!instanced && !bended && !whole && !collision && anchor.NodeType != "worldStaticMeshNode") || (bended && placement.Any(e => !e.Deleted)) ||
                    placement.Any(e => (e.Source.InstanceIndex >= 0) != (instanced || collision) || e.Source.NodeType != anchor.NodeType ||
                        e.Source.ExpectedInstances != anchor.ExpectedInstances || e.Source.NodeIndex != anchor.NodeIndex))
                    throw new NotSupportedException($"Unsupported ArchiveXL edit target: {anchor.ObjectId}");
                if (collision)
                {
                    // Game collision (XL 1.27.1, guarded by the actor count): the node stays.
                    // Deleted actors and switched-off shapes ([actor, shape], given the
                    // NoCollision preset) are actorDeletions; moved actors are actorMutations
                    // (position and orientation; actors are not scaled).
                    var removed = new JsonArray();
                    foreach (var edit in placement.OrderBy(e => e.Source.InstanceIndex))
                    {
                        if (edit.Deleted) removed.Add(JsonValue.Create(edit.Source.InstanceIndex));
                        else foreach (int shape in edit.DisabledShapes.IsDefault ? [] : edit.DisabledShapes) removed.Add(new JsonArray(edit.Source.InstanceIndex, shape));
                    }
                    if (removed.Count != 0) { var entry = Node(anchor); entry["expectedActors"] = anchor.ExpectedInstances; entry["actorDeletions"] = removed; deletions.Add(entry); }
                    var moved = placement.Where(e => !e.Deleted && e.Transform is not null).OrderBy(e => e.Source.InstanceIndex).ToArray();
                    if (moved.Length != 0)
                    {
                        var entry = Node(anchor); entry["expectedActors"] = anchor.ExpectedInstances;
                        var children = new JsonArray();
                        foreach (var edit in moved)
                        {
                            var t = edit.Transform!.Value.Validated();
                            children.Add(new JsonObject { ["index"] = edit.Source.InstanceIndex, ["position"] = new JsonArray(t.Position.X, t.Position.Y, t.Position.Z, 0f), ["orientation"] = new JsonArray(t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W) });
                        }
                        entry["actorMutations"] = children; mutations.Add(entry);
                    }
                }
                else if (instanced)
                {
                    var changed = placement.Where(e => !e.Deleted && e.Transform is not null).OrderBy(e => e.Source.InstanceIndex).ToArray();
                    if (changed.Length != 0)
                    {
                        var entry = Node(anchor); entry["expectedInstances"] = anchor.ExpectedInstances;
                        var children = new JsonArray();
                        foreach (var edit in changed)
                        { var child = new JsonObject { ["index"] = edit.Source.InstanceIndex }; Transform(child, edit.Transform!.Value); children.Add(child); }
                        entry["instanceMutations"] = children; mutations.Add(entry);
                    }
                    var removed = placement.Where(e => e.Deleted).OrderBy(e => e.Source.InstanceIndex).ToArray();
                    if (removed.Length != 0)
                    {
                        var entry = Node(anchor); entry["expectedInstances"] = anchor.ExpectedInstances;
                        entry["instanceDeletions"] = new JsonArray(removed.Select(e => (JsonNode?)JsonValue.Create(e.Source.InstanceIndex)).ToArray());
                        deletions.Add(entry);
                    }
                }
                else
                {
                    var edit = placement.Single(); var entry = Node(anchor);
                    if (edit.Deleted) deletions.Add(entry);
                    else if (edit.Transform is { } transform) { Transform(entry, transform); mutations.Add(entry); }
                }
            }
            if (mutations.Count == 0 && deletions.Count == 0) continue;
            var sector = new JsonObject { ["path"] = source.Resource.Path, ["expectedNodes"] = source.NodeCount };
            if (mutations.Count != 0) sector["nodeMutations"] = mutations;
            if (deletions.Count != 0) sector["nodeDeletions"] = deletions;
            sectors.Add(sector);
        }
        foreach (var group in (extraDeletions ?? []).GroupBy(d => d.SectorPath, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            int expected = group.First().ExpectedNodes;
            if (group.Any(d => d.ExpectedNodes != expected)) throw new InvalidDataException($"Conflicting node counts for {group.Key}.");
            var sector = sectors.OfType<JsonObject>().FirstOrDefault(s => string.Equals((string?)s["path"], group.Key, StringComparison.OrdinalIgnoreCase));
            if (sector is null) { sector = new JsonObject { ["path"] = group.Key, ["expectedNodes"] = expected }; sectors.Add(sector); }
            else if ((int?)sector["expectedNodes"] != expected) throw new InvalidDataException($"The project's edits and the terrain disagree on {group.Key}'s node count.");
            if (sector["nodeDeletions"] is not JsonArray deletions) sector["nodeDeletions"] = deletions = new JsonArray();
            foreach (var deletion in group.OrderBy(d => d.Placement))
            {
                if (deletions.OfType<JsonObject>().Any(d => (int?)d["index"] == deletion.Placement)) continue;
                deletions.Add(new JsonObject { ["type"] = deletion.NodeType, ["index"] = deletion.Placement });
            }
        }
        // Proxy counts (ProxyCorrections): a distant proxy loses what its deleted nodes stood for,
        // or it keeps drawing after they are gone. Written to the proxy's own sector.
        foreach (var group in (proxies ?? []).GroupBy(p => p.SectorPath, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            int expected = group.First().ExpectedNodes;
            if (group.Any(p => p.ExpectedNodes != expected)) throw new InvalidDataException($"Conflicting node counts for {group.Key}.");
            var sector = sectors.OfType<JsonObject>().FirstOrDefault(s => string.Equals((string?)s["path"], group.Key, StringComparison.OrdinalIgnoreCase));
            if (sector is null) { sector = new JsonObject { ["path"] = group.Key, ["expectedNodes"] = expected }; sectors.Add(sector); }
            else if ((int?)sector["expectedNodes"] != expected) throw new InvalidDataException($"The project's edits and the proxies disagree on {group.Key}'s node count.");
            if (sector["nodeMutations"] is not JsonArray sectorMutations) sector["nodeMutations"] = sectorMutations = new JsonArray();
            foreach (var proxy in group.OrderBy(p => p.NodeIndex))
                sectorMutations.Add(new JsonObject { ["type"] = proxy.NodeType, ["index"] = proxy.NodeIndex, ["nodeRefHash"] = proxy.NodeRef.ToString(), ["nbNodesUnderProxyDiff"] = proxy.Diff });
        }
        if (sectors.Count != 0) streaming["sectors"] = sectors;
        if (streaming.Count == 0) throw new InvalidOperationException("The project has no exportable changes.");
        var root = new JsonObject { ["streaming"] = streaming };
        // Resource patches (ArchiveXL 1.20+): the elevators' devices and persistent states are
        // merged into the world's .devices and .psrep, as World Builder imports do.
        if (patches is { Count: > 0 })
        {
            var patch = new JsonObject();
            foreach (var (source, target) in patches.OrderBy(p => p.Key, StringComparer.Ordinal)) patch[source] = new JsonArray(JsonValue.Create(target));
            root["resource"] = new JsonObject { ["patch"] = patch };
        }
        // The elevators' floor names: localization entries, the same file for every language.
        if (texts is not null)
        {
            var onscreens = new JsonObject();
            foreach (var language in NCWE.Formats.ElevatorNodes.Languages) onscreens[language] = texts;
            root["localization"] = new JsonObject { ["onscreens"] = onscreens };
        }
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
    private static JsonObject Node(SourceAnchor anchor) => new() { ["type"] = anchor.NodeType, ["index"] = anchor.PlacementIndex };
    private static void Transform(JsonObject node, PlacementTransform value)
    {
        var t = value.Validated();
        // XL 1.27.1 ParseSubMutations requires four components (top-level
        // mutations accept three or four). The consumer resets W to zero.
        node["position"] = new JsonArray(t.Position.X, t.Position.Y, t.Position.Z, 0f);
        node["orientation"] = new JsonArray(t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W);
        node["scale"] = new JsonArray(t.Scale.X, t.Scale.Y, t.Scale.Z);
    }
}
