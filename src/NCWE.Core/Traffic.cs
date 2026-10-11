// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json.Serialization;

namespace NCWE.Core;

// Traffic paths: the lanes the game's vehicles and pedestrians follow (base\worlds\03_night_city\
// sectors\_generated\traffic\all.*). Each lane is a polyline in game space with a width, a speed,
// flags (road, pavement, intersection...) and links to the lanes it leads to. Bidirectional paths
// are two lanes, one reversed. Game lanes are keyed "g:<index>" (their index in the game's
// arrays), lanes drawn in the editor "t:<id>".
[Flags]
public enum TrafficLaneFlags : ushort
{
    None = 0,
    FromRoadSpline = 1, Bidirectional = 2, PatrolRoute = 4, Pavement = 8, Road = 16, Intersection = 32, NeverDeadEnd = 64,
    TrafficDisabled = 128, CrossWalk = 256, GPSOnly = 512, ShowDebug = 1024, Blockade = 2048, Yield = 4096, NoAIDriving = 8192,
    Highway = 16384, NoAutodrive = 32768
}

// The game's identity of a lane (worldTrafficLaneUID): the traffic spline node it was generated
// from, its lane number, its sequence and its direction.
public readonly record struct TrafficLaneId(ulong Node, ushort Lane, ushort Sequence, bool Reversed)
{
    public TrafficLaneId Twin() => this with { Reversed = !Reversed };
}

// A link to the next lane: where it leaves this lane and where it enters the next one (metres
// along each), its share of the traffic (255 = all) and whether the turn is sharp.
public sealed record TrafficLink(string Lane)
{
    public float ExitPosition { get; init; }
    public float EntryPosition { get; init; }
    public byte Probability { get; init; } = 255;
    public bool Sharp { get; init; }
}

public sealed record TrafficLaneState(ImmutableArray<Vector3> Outline, float Width, byte MaxSpeed, TrafficLaneFlags Flags)
{
    public ImmutableArray<TrafficLink> Out { get; init; } = [];
    public ImmutableArray<string> In { get; init; } = [];

    [JsonIgnore] public float Length => TrafficGeometry.Length(Outline);
    [JsonIgnore] public bool IsRoad => (Flags & TrafficLaneFlags.Road) != 0;
    [JsonIgnore] public bool IsPavement => (Flags & TrafficLaneFlags.Pavement) != 0;

    public TrafficLaneState Validated()
    {
        if (Outline.IsDefault || Outline.Length < 2) throw new InvalidDataException("A traffic lane needs at least two points.");
        foreach (var p in Outline)
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)) throw new InvalidDataException("Traffic lane points must be finite.");
        if (TrafficGeometry.Length(Outline) < .05f) throw new InvalidDataException("A traffic lane must be at least 5 cm long.");
        if (!float.IsFinite(Width) || Width < .2f || Width > 40) throw new InvalidDataException("Traffic lane width must be between 0.2 and 40 m.");
        if (MaxSpeed == 0) throw new InvalidDataException("A traffic lane needs a speed.");
        var links = Out.IsDefault ? [] : Out;
        var into = In.IsDefault ? [] : In;
        foreach (var link in links) if (!TrafficKeys.IsValid(link.Lane)) throw new InvalidDataException("Invalid traffic link: " + link.Lane);
        foreach (var lane in into) if (!TrafficKeys.IsValid(lane)) throw new InvalidDataException("Invalid traffic link: " + lane);
        return this with { Out = links, In = into };
    }
}

// A lane of the project: a game lane edited (Game is its identity, checked against the game's data
// at load and export) or deleted, or a lane drawn in the editor (Game null).
public sealed record TrafficLaneEdit(string Key, TrafficLaneState State)
{
    public TrafficLaneId? Game { get; init; }
    public bool Deleted { get; init; }
    public string Name { get; init; } = "";
    // A drawn lane's reverse direction (drawn both ways), edited with it.
    public string? Twin { get; init; }

    [JsonIgnore] public bool IsCreated => TrafficKeys.IsCreated(Key);

    public TrafficLaneEdit Validated()
    {
        if (!TrafficKeys.IsValid(Key)) throw new InvalidDataException("Invalid traffic lane key: " + Key);
        if (TrafficKeys.IsCreated(Key) == Game is not null) throw new InvalidDataException("A game lane keeps its game identity; a drawn lane has none.");
        if (Twin is not null && (!TrafficKeys.IsCreated(Twin) || Twin == Key)) throw new InvalidDataException("Invalid twin lane: " + Twin);
        return this with { State = State.Validated() };
    }
}

public static class TrafficKeys
{
    public static string Game(int index) => "g:" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public static string NewLane() => "t:" + Guid.NewGuid().ToString("N");
    public static bool IsCreated(string key) => key.StartsWith("t:", StringComparison.Ordinal);
    public static int GameIndex(string key) => key.StartsWith("g:", StringComparison.Ordinal) && int.TryParse(key.AsSpan(2), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int i) ? i : -1;
    public static bool IsValid(string? key) => key is not null && (GameIndex(key) >= 0 || (IsCreated(key) && key.Length is > 2 and <= 40));
}

// Lane geometry as the game stores it: cumulative lengths per point, the lane's 2D outline polygon
// (the left edge forwards, then the right edge backwards, half the width from the centre line).
public static class TrafficGeometry
{
    public static float Length(IReadOnlyList<Vector3> points)
    {
        float length = 0;
        for (int i = 1; i < points.Count; i++) length += Vector3.Distance(points[i - 1], points[i]);
        return length;
    }

    // Lengths at points 1..n-1 (the game stores n-1 values; the last one is the lane's length).
    public static float[] Accumulated(IReadOnlyList<Vector3> points)
    {
        var result = new float[Math.Max(0, points.Count - 1)];
        float length = 0;
        for (int i = 1; i < points.Count; i++) { length += Vector3.Distance(points[i - 1], points[i]); result[i - 1] = length; }
        return result;
    }

    // Left of the travel direction (game space, Z up): +90° around Z.
    private static Vector2 Left(Vector2 direction) => new(-direction.Y, direction.X);

    private static Vector2 Direction(IReadOnlyList<Vector3> points, int i)
    {
        Vector2 Flat(Vector3 v) => new(v.X, v.Y);
        var before = i > 0 ? Flat(points[i]) - Flat(points[i - 1]) : Vector2.Zero;
        var after = i < points.Count - 1 ? Flat(points[i + 1]) - Flat(points[i]) : Vector2.Zero;
        if (before.LengthSquared() > 1e-8f) before = Vector2.Normalize(before);
        if (after.LengthSquared() > 1e-8f) after = Vector2.Normalize(after);
        var sum = before + after;
        return sum.LengthSquared() > 1e-8f ? Vector2.Normalize(sum) : after.LengthSquared() > 0 ? after : before.LengthSquared() > 0 ? before : Vector2.UnitX;
    }

    public static Vector2[] Polygon(IReadOnlyList<Vector3> points, float width)
    {
        int n = points.Count;
        var polygon = new Vector2[2 * n];
        for (int i = 0; i < n; i++)
        {
            var offset = Left(Direction(points, i)) * (width / 2);
            var p = new Vector2(points[i].X, points[i].Y);
            polygon[i] = p + offset;
            polygon[2 * n - 1 - i] = p - offset;
        }
        return polygon;
    }

    // The distance along a lane of the point of its centre line nearest to `target` (2D).
    public static float Station(IReadOnlyList<Vector3> points, Vector3 target)
    {
        float best = float.MaxValue, station = 0, walked = 0;
        for (int i = 1; i < points.Count; i++)
        {
            var a = points[i - 1]; var b = points[i]; var ab = b - a;
            float length = ab.Length();
            float t = length < 1e-6f ? 0 : Math.Clamp(Vector3.Dot(target - a, ab) / (length * length), 0, 1);
            float d = Vector3.DistanceSquared(a + ab * t, target);
            if (d < best) { best = d; station = walked + t * length; }
            walked += length;
        }
        return station;
    }

    // The line moved sideways (positive: to the left of its direction), heights kept.
    public static Vector3[] Offset(IReadOnlyList<Vector3> points, float left)
    {
        var result = new Vector3[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            var side = Left(Direction(points, i)) * left;
            result[i] = points[i] + new Vector3(side.X, side.Y, 0);
        }
        return result;
    }

    // Points about every `step` metres along a dense line (first and last kept).
    public static Vector3[] Resample(IReadOnlyList<Vector3> points, float step)
    {
        var result = new List<Vector3> { points[0] };
        float walked = 0;
        for (int i = 1; i < points.Count; i++)
        {
            walked += Vector3.Distance(points[i - 1], points[i]);
            if (walked >= step && i < points.Count - 1) { result.Add(points[i]); walked = 0; }
        }
        if (points.Count > 1) result.Add(points[^1]);
        return [.. result];
    }

    // The direction a lane travels at its first and last points (2D, normalised).
    public static Vector2 StartDirection(IReadOnlyList<Vector3> points) => Direction(points, 0);
    public static Vector2 EndDirection(IReadOnlyList<Vector3> points) => Direction(points, points.Count - 1);
}
