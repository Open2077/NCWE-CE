// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Numerics;
using NCWE.Core;
using WolvenKit.RED4.Types;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace NCWE.Formats.Traffic;

// The game's traffic lanes as the editor reads them: all.traffic_persistent (lane properties),
// all.lane_polygons (centre lines and outlines) and all.lane_connections (links), one array index
// per lane in all three. A 64 m grid finds the lanes around a point without walking all of them.
public sealed class TrafficNetwork
{
    public const string Folder = @"base\worlds\03_night_city\sectors\_generated\traffic\";
    public const string PersistentPath = Folder + "all.traffic_persistent", PolygonsPath = Folder + "all.lane_polygons",
        ConnectionsPath = Folder + "all.lane_connections", SpatialPath = Folder + "all.spatial_representation";
    public const float CellSize = 64;

    private readonly Dictionary<(int X, int Y), List<int>> _grid = [];
    private readonly Dictionary<TrafficLaneId, int> _byId = [];

    public TrafficNetwork(IReadOnlyList<TrafficLaneState> lanes, IReadOnlyList<TrafficLaneId> ids)
    {
        if (lanes.Count != ids.Count) throw new ArgumentException("One identity per lane.");
        Lanes = [.. lanes]; Ids = [.. ids];
        Bounds = new (Vector2 Min, Vector2 Max)[Lanes.Length];
        for (int i = 0; i < Lanes.Length; i++)
        {
            Bounds[i] = Flat(Lanes[i].Outline, Lanes[i].Width);
            foreach (var cell in Cells(Bounds[i].Min, Bounds[i].Max))
            {
                if (!_grid.TryGetValue(cell, out var list)) _grid[cell] = list = [];
                list.Add(i);
            }
            _byId.TryAdd(Ids[i], i);
        }
    }

    public ImmutableArray<TrafficLaneState> Lanes { get; }
    public ImmutableArray<TrafficLaneId> Ids { get; }
    public (Vector2 Min, Vector2 Max)[] Bounds { get; }
    public int Count => Lanes.Length;

    public int IndexOf(TrafficLaneId id) => _byId.GetValueOrDefault(id, -1);
    public int TwinOf(int index) => index >= 0 && index < Ids.Length ? IndexOf(Ids[index].Twin()) : -1;

    // Game lanes whose outline comes within `radius` of `center` (2D), each once.
    public IEnumerable<int> Near(Vector3 center, float radius)
    {
        var c = new Vector2(center.X, center.Y);
        var min = c - new Vector2(radius); var max = c + new Vector2(radius);
        var seen = new HashSet<int>();
        foreach (var cell in Cells(min, max))
            if (_grid.TryGetValue(cell, out var list))
                foreach (int lane in list)
                    if (seen.Add(lane) && Distance(Bounds[lane], c) <= radius) yield return lane;
    }

    public static float Distance((Vector2 Min, Vector2 Max) box, Vector2 point)
    {
        var d = Vector2.Max(Vector2.Max(box.Min - point, point - box.Max), Vector2.Zero);
        return d.Length();
    }

    public static (Vector2 Min, Vector2 Max) Flat(IReadOnlyList<Vector3> points, float width)
    {
        var min = new Vector2(float.MaxValue); var max = new Vector2(float.MinValue);
        foreach (var p in points) { min = Vector2.Min(min, new(p.X, p.Y)); max = Vector2.Max(max, new(p.X, p.Y)); }
        return (min - new Vector2(width / 2), max + new Vector2(width / 2));
    }

    private static IEnumerable<(int, int)> Cells(Vector2 min, Vector2 max)
    {
        int x0 = (int)MathF.Floor(min.X / CellSize), y0 = (int)MathF.Floor(min.Y / CellSize), x1 = (int)MathF.Floor(max.X / CellSize), y1 = (int)MathF.Floor(max.Y / CellSize);
        if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > 40_000) { x1 = x0 + 199; y1 = y0 + 199; }   // degenerate data
        for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++) yield return (x, y);
    }

    // --- Reading the game's files ---------------------------------------------------------------

    public static TrafficNetwork Load(GameArchiveIndex index, CancellationToken cancellation = default)
    {
        using var persistentFile = index.ReadFile(PersistentPath) ?? throw new FileNotFoundException("The game's traffic lanes are missing: " + PersistentPath);
        using var polygonFile = index.ReadFile(PolygonsPath) ?? throw new FileNotFoundException("Missing " + PolygonsPath);
        using var connectionFile = index.ReadFile(ConnectionsPath) ?? throw new FileNotFoundException("Missing " + ConnectionsPath);
        cancellation.ThrowIfCancellationRequested();
        var persistent = (worldTrafficPersistentResource)persistentFile.RootChunk;
        var polygons = (worldTrafficPersistentLanePolygonResource)polygonFile.RootChunk;
        var connections = (worldTrafficPersistentLaneConnectionsResource)connectionFile.RootChunk;
        return FromResources(persistent, polygons, connections);
    }

    public static TrafficNetwork FromResources(worldTrafficPersistentResource persistent, worldTrafficPersistentLanePolygonResource polygons,
        worldTrafficPersistentLaneConnectionsResource connections)
    {
        var lanes = persistent.Data.Lanes;
        int count = lanes.Count;
        var outlines = new IReadOnlyList<Vector3>?[count];
        foreach (var entry in polygons.Data)
            if ((int)(ushort)entry.Index is var i && i >= 0 && i < count) outlines[i] = entry.Value.Outline.Select(v => new Vector3(v.X, v.Y, v.Z)).ToArray();
        var links = new worldTrafficPersistentLaneConnections?[count];
        foreach (var entry in connections.Data)
            if ((int)(ushort)entry.Index is var i && i >= 0 && i < count) links[i] = entry.Value;
        var states = new TrafficLaneState[count];
        var ids = new TrafficLaneId[count];
        for (int i = 0; i < count; i++)
        {
            var lane = lanes[i];
            ids[i] = new((ulong)lane.NodeRefHash, (ushort)lane.LaneNumber, (ushort)lane.SeqNumber, (bool)lane.IsReversed);
            var outline = outlines[i] is { Count: >= 2 } o ? o : Placeholder(lane);
            var connection = links[i];
            states[i] = new TrafficLaneState([.. outline], (float)lane.Width, (byte)lane.MaxSpeed, (TrafficLaneFlags)(ushort)lane.Flags)
            {
                Out = connection is null ? [] : [.. connection.Outlanes.Select(l => new TrafficLink(TrafficKeys.Game((ushort)l.LaneIndex))
                {
                    ExitPosition = l.ThisLaneExitPosition, EntryPosition = l.NextLaneEntryPosition, Probability = l.ExitProbabilityCompressed, Sharp = l.IsSharpAngle
                })],
                In = connection is null ? [] : [.. connection.InLanes.Select(l => TrafficKeys.Game((ushort)l.LaneIndex))]
            };
        }
        return new TrafficNetwork(states, ids);
    }

    // A lane without a stored outline (none on 2.31) still needs two points.
    private static Vector3[] Placeholder(worldTrafficLanePersistent lane) => [Vector3.Zero, new Vector3(Math.Max(.1f, (float)lane.Length), 0, 0)];
}
