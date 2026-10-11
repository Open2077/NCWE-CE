// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;
using NCWE.Core;
using NCWE.Studio.Session;

namespace NCWE.Studio;

// api.traffic.*: traffic lanes for agents and tests (game space: X east, Y north, Z up).
internal sealed partial class Automation
{
    private static object LaneJson(StudioSession s, string key, TrafficLaneState lane, bool detail) => new
    {
        key, kind = StudioSession.LaneKind(lane.Flags), flags = lane.Flags.ToString(), width = lane.Width, speed = lane.MaxSpeed, length = MathF.Round(lane.Length, 2),
        points = detail ? lane.Outline.Select(p => new[] { R(p.X), R(p.Y), R(p.Z) }).ToArray() : null, point_count = lane.Outline.Length,
        start = A(lane.Outline[0]), end = A(lane.Outline[^1]),
        leads_to = lane.Out.Select(l => detail ? (object)new { lane = l.Lane, exit = l.ExitPosition, entry = l.EntryPosition, sharp = l.Sharp } : l.Lane).ToArray(),
        comes_from = lane.In.ToArray(), twin = s.TwinOf(key), changed = s.LaneEdit(key) is not null, deleted = s.IsLaneDeleted(key)
    };

    private static TrafficLaneFlags? FlagsFrom(JsonElement c)
    {
        if (!c.TryGetProperty("flags", out var f)) return null;
        if (f.ValueKind == JsonValueKind.Number) return (TrafficLaneFlags)f.GetUInt16();
        var flags = TrafficLaneFlags.None;
        foreach (var name in f.ValueKind == JsonValueKind.Array ? f.EnumerateArray().Select(e => e.GetString()!) : f.GetString()!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            flags |= Enum.Parse<TrafficLaneFlags>(name, true);
        return flags;
    }

    private static ImmutableArray<Vector3>? PointsFrom(JsonElement c) =>
        c.TryGetProperty("points", out var p) && p.ValueKind == JsonValueKind.Array ? [.. p.EnumerateArray().Select(V3)] : null;

    private async Task<object> TrafficOp(StudioSession s, string op, JsonElement c)
    {
        s.EnsureTraffic();
        for (int i = 0; i < 600 && s.TrafficLoading; i++) await Task.Delay(50);
        if (s.Traffic is not { } network) throw new InvalidOperationException("Traffic lanes unavailable: " + (s.TrafficError.Length > 0 ? s.TrafficError : "open the game first."));
        string Key() => Str(c, "key") ?? throw new ArgumentException("key is required (g:<index> for game lanes, t:<id> for drawn ones).");
        TrafficLaneState Lane(string key) => s.Lane(key) ?? throw new KeyNotFoundException("No traffic lane " + key);
        switch (op)
        {
            case "api.traffic.status":
                return new { ok = true, lanes = network.Count, changed = s.TrafficEditCount, shown = s.TrafficShown, range = s.TrafficRange, selected = s.SelectedLane, point = s.SelectedLanePoint };
            case "api.traffic.query":
            {
                var center = OptV3(c, "center") ?? Coordinates.ViewToGame(s.Camera.Eye);
                float radius = OptFloat(c, "radius") ?? 40;
                int limit = Int(c, "limit", 100);
                string? kind = Str(c, "kind");
                var lanes = s.LanesNear(center, radius).Select(k => (Key: k, Lane: s.Lane(k)!)).Where(x => x.Lane is not null)
                    .Where(x => kind is null || StudioSession.LaneKind(x.Lane.Flags).Contains(kind, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => DistanceTo(x.Lane.Outline, center)).Take(limit).ToList();
                return new { ok = true, count = lanes.Count, lanes = lanes.Select(x => LaneJson(s, x.Key, x.Lane, false)).ToArray() };
            }
            case "api.traffic.lane":
                return new { ok = true, lane = LaneJson(s, Key(), Lane(Key()), true) };
            case "api.traffic.select":
                s.SelectLane(Str(c, "key"), Int(c, "point", -1));
                return new { ok = true, selected = s.SelectedLane };
            case "api.traffic.edit":
            {
                string key = Key(); _ = Lane(key);
                byte? speed = c.TryGetProperty("speed", out var sp) ? (byte)Math.Clamp(sp.GetDouble(), 1, 255) : null;
                bool ok = s.EditLane(key, PointsFrom(c), OptFloat(c, "width"), speed, FlagsFrom(c), !c.TryGetProperty("both_ways", out var b) || b.GetBoolean());
                return new { ok, error = ok ? "" : s.Error, lane = LaneJson(s, key, Lane(key), true) };
            }
            case "api.traffic.link":
            {
                string from = Str(c, "from") ?? throw new ArgumentException("from is required."), to = Str(c, "to") ?? throw new ArgumentException("to is required.");
                bool ok = s.LinkLanes(from, to, entry: OptFloat(c, "entry"));
                return new { ok, status = s.Status, from = LaneJson(s, from, Lane(from), false), to = LaneJson(s, to, Lane(to), false) };
            }
            case "api.traffic.unlink":
            {
                string from = Str(c, "from") ?? throw new ArgumentException("from is required."), to = Str(c, "to") ?? throw new ArgumentException("to is required.");
                return new { ok = s.UnlinkLanes(from, to) };
            }
            case "api.traffic.delete":
            {
                string key = Key(); _ = Lane(key);
                s.DeleteLane(key, c.TryGetProperty("both_ways", out var b) && b.GetBoolean());
                return new { ok = true, status = s.Status, exists = s.Lane(key) is not null, deleted = s.IsLaneDeleted(key) };
            }
            case "api.traffic.revert":
                s.RevertLane(Key());
                return new { ok = true, changed = s.LaneEdit(Key()) is not null };
            case "api.traffic.create":
            {
                var points = PointsFrom(c) ?? throw new ArgumentException("points are required: [[x,y,z], …] in the direction of travel.");
                string kind = (Str(c, "kind") ?? "road").ToLowerInvariant();
                var flags = kind switch
                {
                    "pavement" => TrafficLaneFlags.Pavement | TrafficLaneFlags.FromRoadSpline | TrafficLaneFlags.NoAIDriving | TrafficLaneFlags.NoAutodrive,
                    "crosswalk" => TrafficLaneFlags.Pavement | TrafficLaneFlags.CrossWalk | TrafficLaneFlags.Intersection | TrafficLaneFlags.NoAIDriving | TrafficLaneFlags.NoAutodrive,
                    "highway" => TrafficLaneFlags.Road | TrafficLaneFlags.Highway | TrafficLaneFlags.FromRoadSpline,
                    "intersection" => TrafficLaneFlags.Road | TrafficLaneFlags.Intersection | TrafficLaneFlags.FromRoadSpline,
                    _ => TrafficLaneFlags.Road | TrafficLaneFlags.FromRoadSpline
                };
                bool pavement = (flags & TrafficLaneFlags.Pavement) != 0;
                var settings = new StudioSession.TrafficDrawSettings(FlagsFrom(c) ?? flags, OptFloat(c, "width") ?? (pavement ? 3 : 3.5f),
                    (byte)Math.Clamp(OptFloat(c, "speed") ?? (pavement ? 1 : 15), 1, 255), c.TryGetProperty("both_ways", out var both) && both.GetBoolean(),
                    !c.TryGetProperty("auto_connect", out var auto) || auto.GetBoolean());
                var key = s.CreateLane([.. points], settings, Str(c, "name"));
                if (key is null) return new { ok = false, error = s.Error };
                return new { ok = true, status = s.Status, lane = LaneJson(s, key, Lane(key), true) };
            }
            case "api.traffic.along_road":
            {
                string road = Str(c, "road") ?? s.SelectedRoadId ?? throw new ArgumentException("road (an editor road id) is required.");
                int count = s.CreateLanesAlongRoad(road, Int(c, "forward", 1), Int(c, "backward", 1), OptFloat(c, "width") ?? 3.5f, (byte)Math.Clamp(OptFloat(c, "speed") ?? 15, 1, 255));
                return new { ok = count > 0, lanes = count, status = s.Status, error = s.Error };
            }
            case "api.traffic.show":
            {
                if (c.TryGetProperty("on", out var on)) s.ShowTrafficOverlay = on.GetBoolean();
                if (OptFloat(c, "range") is { } range) s.TrafficRange = range;
                return new { ok = true, shown = s.TrafficShown, range = s.TrafficRange };
            }
            default: throw new ArgumentException("Unknown command " + op);
        }
    }

    private static float DistanceTo(ImmutableArray<Vector3> outline, Vector3 p)
    {
        float best = float.MaxValue;
        for (int i = 0; i + 1 < outline.Length; i++)
        {
            var a = outline[i]; var ab = outline[i + 1] - a;
            float t = ab.LengthSquared() < 1e-8f ? 0 : Math.Clamp(Vector3.Dot(p - a, ab) / ab.LengthSquared(), 0, 1);
            best = Math.Min(best, Vector3.Distance(a + ab * t, p));
        }
        return best;
    }
}
