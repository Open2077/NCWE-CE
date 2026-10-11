// SPDX-License-Identifier: GPL-3.0-only
using NCWE.Core;
using NCWE.Formats.CustomMeshes;
using WolvenKit.RED4.Archive.CR2W;
using WolvenKit.RED4.Archive.IO;
using WolvenKit.RED4.Types;
using Num = System.Numerics;
using RedVector2 = WolvenKit.RED4.Types.Vector2;
using RedVector3 = WolvenKit.RED4.Types.Vector3;

namespace NCWE.Formats.Traffic;

public sealed record TrafficExportSummary(int Edited, int Deleted, int Created, int Total);

// The project's traffic changes written into the game's traffic resources, which the export puts
// in the mod archive at their own depot paths (they replace the game's whole files):
//   - an edited game lane keeps its index: outline, polygon, lengths, width, speed, flags and links
//     are rewritten (crowd fragments, road materials and the dead end move with the new length);
//   - a deleted game lane keeps its index and geometry, gets TrafficDisabled and loses every link;
//   - a drawn lane is appended (a copy of a linked game lane for the data the editor does not model),
//     with an identity record and the spatial groups of the lanes it links to.
public static class TrafficExporter
{
    public static (IReadOnlyList<CookedFile> Files, TrafficExportSummary Summary) Build(GameArchiveIndex index, IReadOnlyDictionary<string, TrafficLaneEdit> edits)
    {
        var persistentFile = Read(index, TrafficNetwork.PersistentPath);
        var polygonFile = Read(index, TrafficNetwork.PolygonsPath);
        var connectionFile = Read(index, TrafficNetwork.ConnectionsPath);
        var spatialFile = Read(index, TrafficNetwork.SpatialPath);
        var summary = Apply((worldTrafficPersistentResource)persistentFile.RootChunk, (worldTrafficPersistentLanePolygonResource)polygonFile.RootChunk,
            (worldTrafficPersistentLaneConnectionsResource)connectionFile.RootChunk, (worldTrafficPersistentSpatialResource)spatialFile.RootChunk, edits);
        var files = new List<CookedFile>
        {
            new(TrafficNetwork.PersistentPath, Write(persistentFile)), new(TrafficNetwork.PolygonsPath, Write(polygonFile)),
            new(TrafficNetwork.ConnectionsPath, Write(connectionFile)), new(TrafficNetwork.SpatialPath, Write(spatialFile))
        };
        // Read back: the files must parse and hold every lane.
        foreach (var file in files)
        {
            using var check = CustomCr2w.Read(file.Bytes);
            int lanes = check.RootChunk switch
            {
                worldTrafficPersistentResource p => p.Data.Lanes.Count, worldTrafficPersistentLanePolygonResource p => p.Data.Count,
                worldTrafficPersistentLaneConnectionsResource c => c.Data.Count, worldTrafficPersistentSpatialResource s => BitConverter.ToInt32((byte[])s.Buffer, 0),
                _ => -1
            };
            if (lanes != summary.Total) throw new InvalidDataException($"{Path.GetFileName(file.Path)} holds {lanes} lanes after writing, {summary.Total} expected.");
        }
        return (files, summary);
    }

    private static CR2WFile Read(GameArchiveIndex index, string path) => index.ReadFile(path) ?? throw new FileNotFoundException("The game's traffic data is missing: " + path);

    private static byte[] Write(CR2WFile file)
    {
        using var stream = new MemoryStream();
        using (var writer = new CR2WWriter(stream, System.Text.Encoding.UTF8, true)) writer.WriteFile(file);
        return stream.ToArray();
    }

    public static ulong NodeHash(string key)
    {
        ulong hash = 14695981039346656037UL;
        foreach (char c in "ncwe:traffic:" + key) { hash ^= c; hash *= 1099511628211UL; }
        return hash;
    }

    public static TrafficExportSummary Apply(worldTrafficPersistentResource persistent, worldTrafficPersistentLanePolygonResource polygons,
        worldTrafficPersistentLaneConnectionsResource connections, worldTrafficPersistentSpatialResource? spatial, IReadOnlyDictionary<string, TrafficLaneEdit> edits)
    {
        var lanes = persistent.Data.Lanes;
        int baseCount = lanes.Count;
        if (polygons.Data.Count != baseCount || connections.Data.Count != baseCount) throw new InvalidDataException("The game's traffic files do not hold the same lanes.");
        for (int i = 0; i < baseCount; i++)
            if ((ushort)polygons.Data[i].Index != i || (ushort)connections.Data[i].Index != i) throw new InvalidDataException("Unexpected traffic lane order.");

        // Guards: an edited game lane must still be the lane it was.
        foreach (var edit in edits.Values.Where(e => !e.IsCreated))
        {
            int i = TrafficKeys.GameIndex(edit.Key);
            if (i < 0 || i >= baseCount || edit.Game is not { } id || Id(lanes[i]) != id)
                throw new InvalidDataException($"Traffic lane {edit.Key} no longer matches the game's data (game update?). Revert it and edit it again.");
        }
        var created = edits.Values.Where(e => e.IsCreated).OrderBy(e => e.Key, StringComparer.Ordinal).ToList();
        if (baseCount + created.Count > ushort.MaxValue) throw new InvalidDataException("Too many traffic lanes for the game's 16-bit indices.");
        var indexOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int k = 0; k < created.Count; k++) indexOf[created[k].Key] = baseCount + k;
        int Index(string key) => TrafficKeys.GameIndex(key) is var g and >= 0 ? g : indexOf.GetValueOrDefault(key, -1);
        var deleted = edits.Values.Where(e => e.Deleted).Select(e => Index(e.Key)).Where(i => i >= 0).ToHashSet();

        // Lanes drawn in the editor: appended, started from a game lane they link to.
        foreach (var edit in created)
        {
            var template = TemplateFor(edit.State, lanes, baseCount);
            var lane = (worldTrafficLanePersistent)template.DeepCopy();
            var id = new TrafficLaneId(NodeHash(edit.Key), 0, 0, false);
            lane.NodeRefHash = id.Node; lane.LaneNumber = id.Lane; lane.SeqNumber = id.Sequence; lane.IsReversed = id.Reversed;
            lane.DeadEndStart = edit.State.Out.IsDefaultOrEmpty ? 0 : float.PositiveInfinity;
            lanes.Add(lane);
            polygons.Data.Add(new worldTrafficPersistentLanePolygonResource_Data { Index = (ushort)(lanes.Count - 1), Value = new worldTrafficLanePolygonRepresentation() });
            connections.Data.Add(new worldTrafficPersistentLaneConnectionsResource_Data { Index = (ushort)(lanes.Count - 1), Value = new worldTrafficPersistentLaneConnections() });
        }

        // Lane states: geometry, properties, links (to lanes that still exist).
        foreach (var edit in edits.Values)
        {
            int i = Index(edit.Key);
            var lane = lanes[i];
            var state = edit.State;
            float oldLength = edit.IsCreated ? 0 : (float)lane.Length;
            WriteLane(lane, state, edit.Deleted, oldLength);
            var polygon = polygons.Data[i].Value;
            polygon.Outline = new CArray<RedVector3>(state.Outline.Select(p => new RedVector3 { X = p.X, Y = p.Y, Z = p.Z }).ToList());
            polygon.Polygon = new CArray<RedVector2>(TrafficGeometry.Polygon(state.Outline, state.Width).Select(p => new RedVector2 { X = p.X, Y = p.Y }).ToList());
            var links = connections.Data[i].Value;
            float length = TrafficGeometry.Length(state.Outline);
            links.Outlanes = new CArray<worldTrafficConnectivityOutLane>(edit.Deleted ? [] : state.Out.Select(l => (Link: l, Next: Index(l.Lane))).Where(x => x.Next >= 0 && !deleted.Contains(x.Next))
                .Select(x => new worldTrafficConnectivityOutLane
                {
                    LaneIndex = (ushort)x.Next, ExitProbabilityCompressed = x.Link.Probability, IsSharpAngle = x.Link.Sharp,
                    ThisLaneExitPosition = Math.Clamp(x.Link.ExitPosition, 0, length), NextLaneEntryPosition = Math.Max(0, x.Link.EntryPosition)
                }).ToList());
            links.InLanes = new CArray<worldTrafficConnectivityInLane>(edit.Deleted ? [] : state.In.Select(Index).Where(p => p >= 0 && !deleted.Contains(p))
                .Select(p => new worldTrafficConnectivityInLane { LaneIndex = (ushort)p }).ToList());
        }

        // No lane leads into or comes from a deleted lane.
        if (deleted.Count > 0)
            foreach (var entry in connections.Data)
            {
                var value = entry.Value;
                if (value.Outlanes.Any(l => deleted.Contains((ushort)l.LaneIndex)))
                    value.Outlanes = new CArray<worldTrafficConnectivityOutLane>(value.Outlanes.Where(l => !deleted.Contains((ushort)l.LaneIndex)).ToList());
                if (value.InLanes.Any(l => deleted.Contains((ushort)l.LaneIndex)))
                    value.InLanes = new CArray<worldTrafficConnectivityInLane>(value.InLanes.Where(l => !deleted.Contains((ushort)l.LaneIndex)).ToList());
            }

        // The spatial representation: an identity record and the groups of the lanes they link to.
        if (spatial is not null && created.Count > 0)
        {
            var data = TrafficSpatialData.Parse((byte[])spatial.Buffer);
            if (data.Identities.Count != baseCount) throw new InvalidDataException("The traffic spatial data does not match the lanes.");
            foreach (var edit in created)
            {
                int i = Index(edit.Key);
                var lane = lanes[i];
                data.Identities.Add((Id(lane), i));
                var linked = edit.State.Out.Select(l => Index(l.Lane)).Concat(edit.State.In.Select(Index)).Where(n => n >= 0 && n < baseCount).ToList();
                data.JoinGroupsOf(i, linked);
                lane.NeighborGroupIndex = (ushort)spatial.NeighborGroups.Count;
                spatial.NeighborGroups.Add(new CArray<CUInt16>([(ushort)i]));
            }
            spatial.Buffer = data.Write();
        }
        return new(edits.Values.Count(e => !e.IsCreated && !e.Deleted), deleted.Count(i => i < baseCount), created.Count, lanes.Count);
    }

    public static TrafficLaneId Id(worldTrafficLanePersistent lane) => new((ulong)lane.NodeRefHash, (ushort)lane.LaneNumber, (ushort)lane.SeqNumber, (bool)lane.IsReversed);

    // A game lane the new one links to (same kind first), else the first game lane of its kind.
    private static worldTrafficLanePersistent TemplateFor(TrafficLaneState state, CArray<worldTrafficLanePersistent> lanes, int baseCount)
    {
        var kind = state.Flags & (TrafficLaneFlags.Road | TrafficLaneFlags.Pavement);
        var linked = state.Out.Select(l => TrafficKeys.GameIndex(l.Lane)).Concat(state.In.Select(TrafficKeys.GameIndex)).Where(i => i >= 0 && i < baseCount).ToList();
        foreach (int i in linked) if (((TrafficLaneFlags)(ushort)lanes[i].Flags & kind) == kind) return lanes[i];
        if (linked.Count > 0) return lanes[linked[0]];
        for (int i = 0; i < baseCount; i++) if (((TrafficLaneFlags)(ushort)lanes[i].Flags & kind) == kind && kind != 0) return lanes[i];
        return lanes[0];
    }

    private static void WriteLane(worldTrafficLanePersistent lane, TrafficLaneState state, bool deleted, float oldLength)
    {
        var flags = state.Flags | (deleted ? TrafficLaneFlags.TrafficDisabled : 0);
        float length = TrafficGeometry.Length(state.Outline);
        float scale = oldLength > 1e-3f ? length / oldLength : 0;
        lane.Flags = (ushort)flags;
        lane.MaxSpeed = state.MaxSpeed;
        lane.Width = state.Width;
        lane.Length = length;
        lane.Area = length * state.Width;
        lane.AccumulatedLengths = new CArray<CFloat>(TrafficGeometry.Accumulated(state.Outline).Select(a => (CFloat)a).ToList());
        if (float.IsFinite(lane.DeadEndStart)) lane.DeadEndStart = Math.Clamp(scale > 0 ? lane.DeadEndStart * scale : 0, 0, length);
        // Crowd density fragments and road materials are ranges along the lane: they follow its length
        // (a drawn lane keeps the first ones of its template, stretched over it).
        var fragments = lane.CrowdCreationInfo.ConnectedFragments;
        if (scale <= 0 && fragments.Count > 1) lane.CrowdCreationInfo.ConnectedFragments = fragments = new CArray<worldTrafficLaneCrowdFragment>([fragments[0]]);
        foreach (var fragment in fragments)
        {
            fragment.LaneX1 = scale > 0 ? Math.Min(fragment.LaneX1 * scale, length) : 0;
            fragment.LaneX2 = scale > 0 ? Math.Min(fragment.LaneX2 * scale, length) : length;
        }
        var materials = lane.RoadMaterials;
        if (scale <= 0 && materials.Count > 1) lane.RoadMaterials = materials = new CArray<worldRoadMaterialInfo>([materials[0]]);
        foreach (var material in materials) material.StartOffset = scale > 0 ? Math.Min(material.StartOffset * scale, length) : 0;
    }
}
