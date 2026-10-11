// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Numerics;

namespace NCWE.Core;

// An editor road: a smooth curve through game-space control points, swept with a road-tool
// template mesh repeated along it (one bended segment per template length). Roads created
// from vanilla bended placements list them in Replaces: those placements are deleted and
// the road takes their place.
public sealed record RoadDefinition(string Id, string Name, MeshReference Template, ImmutableArray<Vector3> Points)
{
    // Roll around the direction of travel, in degrees (positive raises the right edge).
    public float Banking { get; init; }
    // Surface preset for the textured preview and export; empty = the template's own look.
    public string Surface { get; init; } = "";
    public ImmutableArray<string> Replaces { get; init; } = [];
    // How the road shapes the game's terrain under it (null: it leaves the ground as it is).
    public RoadGround? Ground { get; init; }
    // A bridge or overpass: it crosses other roads without junctions (RoadJunctions), even at grade.
    public bool Overpass { get; init; }
    // A cable (power line, hanging wire) edited as a curve: a road in everything but what a road
    // does to its surroundings. It hangs in the air: no junctions, no earthworks, no collision, no
    // banking; it is written as the game's own cable node (CableBending).
    [System.Text.Json.Serialization.JsonIgnore] public bool IsCable => CableBending.IsCable(Template.Path);
    // Whether it meets the roads it crosses at junctions.
    [System.Text.Json.Serialization.JsonIgnore] public bool Joins => !Overpass && !IsCable;

    public RoadDefinition Validated()
    {
        if (string.IsNullOrWhiteSpace(Id) || !Id.StartsWith("r:", StringComparison.Ordinal)) throw new InvalidDataException("A road needs an r: identity.");
        if (Template.Hash == 0 || string.IsNullOrWhiteSpace(Template.Path)) throw new InvalidDataException("A road needs a template mesh.");
        if (Points.IsDefault || Points.Length < 2) throw new InvalidDataException("A road needs at least two points.");
        foreach (var p in Points)
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z) || p.Length() > 100_000) throw new InvalidDataException("Road points must be finite world positions.");
        if (!float.IsFinite(Banking) || Math.Abs(Banking) > 60) throw new InvalidDataException("Road banking must be within ±60°.");
        if (RoadSpline.Length(RoadSpline.Sample(Points)) < .5f) throw new InvalidDataException("A road must be at least 0.5 m long.");
        return this with { Replaces = Replaces.IsDefault ? [] : Replaces, Ground = Ground?.Validated() };
    }
}

// Earthworks under a road: the game's terrain is brought just below the road surface over its
// width plus `Margin` metres on each side, then banked back to the ground at `Slope` degrees
// (fill where the road runs above the ground, cut where it runs below), and the game's plants
// on the road are removed. Derived from the road: moving or deleting it moves or removes them.
public sealed record RoadGround(float Slope = 33, float Margin = 1.5f, bool ClearPlants = true)
{
    public RoadGround Validated() => this with
    {
        Slope = float.IsFinite(Slope) ? Math.Clamp(Slope, 10, 80) : 33,
        Margin = float.IsFinite(Margin) ? Math.Clamp(Margin, 0, 30) : 1.5f
    };

    // The terrain shape of a road whose pieces reach `halfWidth` metres on each side of its curve
    // (null when it leaves the ground alone). The editor and the export build it the same way.
    public static Terrain.TerrainShape? Of(RoadDefinition road, float halfWidth) => road.Ground is { } g && !road.IsCable && halfWidth > 0 && !road.Points.IsDefault && road.Points.Length >= 2
        ? new(road.Id, RoadSpline.Sample(road.Points, 1f), halfWidth, g.Slope, g.Margin, g.ClearPlants) : null;
}

// One bended placement of a road: the node origin (game space) and the deformation frames
// in node space, in the game's DeformationData convention: rows X = right, Y = direction
// of travel, Z = up, W = point on the curve (Z.W = 1 as in shipping data).
public sealed record RoadSegment(int Index, Vector3 Origin, ImmutableArray<Matrix4x4> Frames, float Length);

public static class RoadSpline
{
    // Dense polyline through the points: centripetal Catmull-Rom (no cusps or loops for
    // uneven spacing), ends extended by reflection.
    public static Vector3[] Sample(ImmutableArray<Vector3> points, float step = .25f)
    {
        if (points.IsDefault || points.Length == 0) return [];
        if (points.Length == 1) return [points[0]];
        var result = new List<Vector3> { points[0] };
        for (int i = 0; i + 1 < points.Length; i++)
        {
            Vector3 p1 = points[i], p2 = points[i + 1];
            Vector3 p0 = i > 0 ? points[i - 1] : 2 * p1 - p2;
            Vector3 p3 = i + 2 < points.Length ? points[i + 2] : 2 * p2 - p1;
            int count = Math.Clamp((int)MathF.Ceiling(Vector3.Distance(p1, p2) / step), 1, 4096);
            for (int k = 1; k <= count; k++) result.Add(CatmullRom(p0, p1, p2, p3, k / (float)count));
        }
        return result.ToArray();
    }

    // The same samples, each with the control-point interval it lies in (i: between points i and i+1).
    public static List<(int Segment, Vector3 Point)> SampleSegments(ImmutableArray<Vector3> points, float step = .25f)
    {
        var result = new List<(int, Vector3)>();
        if (points.IsDefault || points.Length < 2) return result;
        result.Add((0, points[0]));
        for (int i = 0; i + 1 < points.Length; i++)
        {
            Vector3 p1 = points[i], p2 = points[i + 1];
            Vector3 p0 = i > 0 ? points[i - 1] : 2 * p1 - p2;
            Vector3 p3 = i + 2 < points.Length ? points[i + 2] : 2 * p2 - p1;
            int count = Math.Clamp((int)MathF.Ceiling(Vector3.Distance(p1, p2) / step), 1, 4096);
            for (int k = 1; k <= count; k++) result.Add((i, CatmullRom(p0, p1, p2, p3, k / (float)count)));
        }
        return result;
    }

    // Direction of travel where the curve leaves its first point and reaches its last one (seen
    // from above and in height), from the points near that end alone.
    public static Vector3 StartDirection(ImmutableArray<Vector3> points) => EndDirection([.. points.Take(3).Reverse()]) * -1;
    public static Vector3 EndDirection(ImmutableArray<Vector3> points)
    {
        if (points.IsDefault || points.Length < 2) return Vector3.UnitY;
        var tail = Sample([.. points.Skip(Math.Max(0, points.Length - 3))], .5f);
        var d = tail[^1] - tail[Math.Max(0, tail.Length - 3)];
        return d.LengthSquared() < 1e-8f ? Vector3.UnitY : Vector3.Normalize(d);
    }

    public static float Length(IReadOnlyList<Vector3> polyline)
    {
        float length = 0;
        for (int i = 1; i < polyline.Count; i++) length += Vector3.Distance(polyline[i - 1], polyline[i]);
        return length;
    }

    // The tightest turn of the curve, seen from above: the smallest radius of the circles through
    // points `spacing` metres apart along it (infinity when it is straight).
    public static float TightestRadius(ImmutableArray<Vector3> points, float spacing = 2)
    {
        var line = Sample(points);
        var even = new List<Vector2>();
        float since = float.MaxValue;
        for (int i = 0; i < line.Length; i++)
        {
            if (i > 0) since += Vector2.Distance(new(line[i - 1].X, line[i - 1].Y), new(line[i].X, line[i].Y));
            if (since >= spacing) { even.Add(new(line[i].X, line[i].Y)); since = 0; }
        }
        // The end, unless it is too close to the last point to measure anything.
        if (line.Length > 1 && since >= spacing / 2) even.Add(new(line[^1].X, line[^1].Y));
        float tightest = float.PositiveInfinity;
        for (int i = 1; i + 1 < even.Count; i++)
        {
            var a = even[i - 1]; var b = even[i]; var c = even[i + 1];
            float cross = MathF.Abs((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X));
            if (cross < 1e-6f) continue;
            tightest = MathF.Min(tightest, Vector2.Distance(a, b) * Vector2.Distance(b, c) * Vector2.Distance(c, a) / (2 * cross));
        }
        return tightest;
    }

    // Barry–Goldman pyramid with centripetal knots (alpha = 0.5), evaluated in [p1, p2].
    private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float u)
    {
        static float Knot(float t, Vector3 a, Vector3 b) => t + MathF.Max(MathF.Sqrt(Vector3.Distance(a, b)), 1e-4f);
        float t0 = 0, t1 = Knot(t0, p0, p1), t2 = Knot(t1, p1, p2), t3 = Knot(t2, p2, p3);
        float t = t1 + (t2 - t1) * u;
        var a1 = (t1 - t) / (t1 - t0) * p0 + (t - t0) / (t1 - t0) * p1;
        var a2 = (t2 - t) / (t2 - t1) * p1 + (t - t1) / (t2 - t1) * p2;
        var a3 = (t3 - t) / (t3 - t2) * p2 + (t - t2) / (t3 - t2) * p3;
        var b1 = (t2 - t) / (t2 - t0) * a1 + (t - t0) / (t2 - t0) * a2;
        var b2 = (t3 - t) / (t3 - t1) * a2 + (t - t1) / (t3 - t1) * a3;
        return (t2 - t) / (t2 - t1) * b1 + (t - t1) / (t2 - t1) * b2;
    }
}

// Drawing aids and the road's profile (its heights along it).
public static class RoadGeometry
{
    // `point` turned around `anchor` (seen from above) to the nearest multiple of `step` degrees
    // from `direction` (north when none), its distance kept.
    public static Vector3 SnapAngle(Vector3 anchor, Vector3? direction, Vector3 point, float step = 15)
    {
        var v = new Vector2(point.X - anchor.X, point.Y - anchor.Y);
        float length = v.Length();
        if (length < 1e-3f) return point;
        float reference = direction is { } d && d.X * d.X + d.Y * d.Y > 1e-8f ? MathF.Atan2(d.Y, d.X) : MathF.PI / 2;
        float radians = step * MathF.PI / 180;
        float turn = MathF.Atan2(v.Y, v.X) - reference;
        turn = MathF.Atan2(MathF.Sin(turn), MathF.Cos(turn));
        float angle = reference + MathF.Round(turn / radians) * radians;
        return new(anchor.X + MathF.Cos(angle) * length, anchor.Y + MathF.Sin(angle) * length, point.Z);
    }

    // Rise over horizontal run from a to b (0 when b is right above a).
    public static float Grade(Vector3 a, Vector3 b)
    {
        float run = new Vector2(b.X - a.X, b.Y - a.Y).Length();
        return run < 1e-3f ? 0 : (b.Z - a.Z) / run;
    }

    // The steepest grade along the curve, over `span` metres runs.
    public static float SteepestGrade(ImmutableArray<Vector3> points, float span = 5)
    {
        var line = RoadSpline.Sample(points, .5f);
        float steepest = 0;
        int ahead = Math.Max(1, (int)(span / .5f));
        for (int i = 0; i + ahead < line.Length; i += Math.Max(1, ahead / 2)) steepest = MathF.Max(steepest, MathF.Abs(Grade(line[i], line[i + ahead])));
        return steepest;
    }

    // Horizontal distances of the control points along their polyline (from the first).
    private static float[] Stations(ImmutableArray<Vector3> points)
    {
        var at = new float[points.Length];
        for (int i = 1; i < points.Length; i++) at[i] = at[i - 1] + new Vector2(points[i].X - points[i - 1].X, points[i].Y - points[i - 1].Y).Length();
        return at;
    }

    // One even grade from the first point's height to the last one's.
    public static ImmutableArray<Vector3> EvenGrade(ImmutableArray<Vector3> points)
    {
        if (points.Length < 3) return points;
        var at = Stations(points);
        float total = at[^1];
        if (total < 1e-3f) return points;
        return [.. points.Select((p, i) => p with { Z = float.Lerp(points[0].Z, points[^1].Z, at[i] / total) })];
    }

    // Heights evened out (the ends stay): each pass averages a point with its neighbours,
    // weighted by how close they are.
    public static ImmutableArray<Vector3> SmoothProfile(ImmutableArray<Vector3> points, int passes = 4)
    {
        if (points.Length < 3) return points;
        var at = Stations(points);
        var z = points.Select(p => p.Z).ToArray();
        for (int pass = 0; pass < passes; pass++)
        {
            var next = (float[])z.Clone();
            for (int i = 1; i + 1 < z.Length; i++)
            {
                float a = MathF.Max(at[i] - at[i - 1], .1f), b = MathF.Max(at[i + 1] - at[i], .1f);
                // The straight line between the neighbours, at this point's station.
                float line = float.Lerp(z[i - 1], z[i + 1], a / (a + b));
                next[i] = float.Lerp(z[i], line, .6f);
            }
            z = next;
        }
        return [.. points.Select((p, i) => p with { Z = z[i] })];
    }

    // The tightest turn a road `halfWidth` metres wide each side keeps comfortably (its inner
    // edge folds below halfWidth): what connections to road ends keep to.
    public static float TurnRadius(float halfWidth) => MathF.Max(MathF.Max(halfWidth * 1.25f, halfWidth + 3), 6);

    // A drivable way from `from` to `to`, seen from above: it leaves `from` along `leave` and
    // reaches `to` along `arrive` (either may be null: free), in straight stretches and arcs no
    // tighter than `radius` (the shortest such way, a Dubins path). With both directions given,
    // the way that leaves `from` free (the spline bends the road at it, over the stretch from
    // `behind`, the point before it) is taken instead when the road turns wider or as wide over a
    // shorter way, or when the Dubins way loops round (one of its arcs turns over 225°: no room
    // to turn into the end). It starts and ends with a short straight
    // along the given directions, so the road's spline leaves and arrives exactly along them.
    // Returns the points in between (neither end), evenly spaced along it (about 20° of arc apart,
    // a long straight's middle left out: even spacing keeps the spline from overshooting where
    // arcs meet straights), their heights evenly from one end's to the other's. Empty: a straight
    // line will do.
    public static Vector3[] Connect(Vector3 from, Vector3? leave, Vector3 to, Vector3? arrive, float radius, Vector3? behind = null)
    {
        Vector2 a = new(from.X, from.Y), b = new(to.X, to.Y);
        float? h0 = Heading(leave), h1 = Heading(arrive);
        float distance = Vector2.Distance(a, b);
        if (distance < 1 || !(radius > 0) || h0 is null && h1 is null) return [];
        float spacing = Math.Clamp(radius * .35f, 2.5f, 5), lead = MathF.Min(spacing, distance / 4);
        static Vector2 Toward(float heading) => new(MathF.Cos(heading), MathF.Sin(heading));
        Vector2 a1 = h0 is { } s0 ? a + Toward(s0) * lead : a, b1 = h1 is { } s1 ? b - Toward(s1) * lead : b;
        // The way's points, walked from `from` (or from `to` when `backward`).
        Vector3[] Points(TurnPath way, bool backward)
        {
            float total = way.Length;
            var samples = way.Samples(spacing);
            if (backward) samples.Reverse();
            return [.. samples.Select(p => new Vector3(p.Point, float.Lerp(from.Z, to.Z, total > 0 ? (backward ? total - p.At : p.At) / total : 0)))];
        }
        if (h0 is { } start && h1 is { } end)
        {
            var free = Points(TurnThenStraight(b1, end + MathF.PI, a, radius).Lengthened(lead, 0), true);
            if (Dubins(a1, start, b1, end, radius) is not { } path) return free;
            var kept = Points(path.Lengthened(lead, lead), false);
            // Each as the road will be built: from the point before `from` to the road it meets.
            var before = behind ?? from - Vector3.Normalize(leave!.Value with { Z = 0 }) * 2 * radius;
            var after = to + Vector3.Normalize(arrive!.Value with { Z = 0 }) * 2 * radius;
            float Turn(Vector3[] points) => RoadSpline.TightestRadius([before, from, .. points, to, after]);
            float Run(Vector3[] points) => RoadSpline.Length([from, .. points, to]);
            if (path.LongestArc > Loop) return free;
            float keptTurn = MathF.Min(Turn(kept), radius), freeTurn = MathF.Min(Turn(free), radius);
            return freeTurn > keptTurn * 1.05f || freeTurn >= keptTurn * .95f && Run(free) < Run(kept) ? free : kept;
        }
        if (h0 is { } leaving) return Points(TurnThenStraight(a1, leaving, b, radius).Lengthened(lead, 0), false);
        return Points(TurnThenStraight(b1, h1!.Value + MathF.PI, a, radius).Lengthened(lead, 0), true);
    }

    private static float? Heading(Vector3? direction) => direction is { } d && d.X * d.X + d.Y * d.Y > 1e-8f ? MathF.Atan2(d.Y, d.X) : null;
    private static float Mod(float angle) => angle - MathF.Tau * MathF.Floor(angle / MathF.Tau);
    // An arc rounded to a whole turn is no turn (an end straight ahead must not loop round).
    private static float Arc(float angle) => angle > MathF.Tau - 1e-3f ? 0 : angle;

    // A way from Start (heading Heading, radians from +X) in steps: turn +1 left, -1 right, 0
    // straight, each `Length` metres; its arcs have Radius.
    private sealed record TurnPath(Vector2 Start, float Heading, float Radius, (int Turn, float Length)[] Steps)
    {
        public float Length => Steps.Sum(s => s.Length);
        public float Turning => Steps.Where(s => s.Turn != 0).Sum(s => s.Length) / Radius;
        public float LongestArc => Steps.Where(s => s.Turn != 0).Select(s => s.Length / Radius).DefaultIfEmpty(0).Max();

        public (Vector2 Point, float Heading) End()
        {
            var (p, h) = (Start, Heading);
            foreach (var (turn, length) in Steps) (p, h) = Advance(p, h, turn, length);
            return (p, h);
        }

        // The same way with straights added before its start and after its end.
        public TurnPath Lengthened(float before, float after)
            => new(Start - new Vector2(MathF.Cos(Heading), MathF.Sin(Heading)) * before, Heading, Radius, [(0, before), .. Steps, (0, after)]);

        // Points `spacing` metres apart at most along the way (its ends left out) with how far
        // along they are; a straight's middle (over `spacing` from both its ends) left out.
        public List<(Vector2 Point, float At)> Samples(float spacing)
        {
            float total = Length;
            int count = Math.Max(1, (int)MathF.Ceiling(total / spacing - 1e-3f));
            float step = total / count, done = 0;
            var result = new List<(Vector2, float)>();
            var (p, h) = (Start, Heading);
            int index = 0;
            for (int i = 1; i < count; i++)
            {
                float at = i * step;
                while (index < Steps.Length - 1 && done + Steps[index].Length < at)
                { (p, h) = Advance(p, h, Steps[index].Turn, Steps[index].Length); done += Steps[index].Length; index++; }
                var (turn, length) = Steps[index];
                float into = at - done;
                if (turn == 0 && into > step * 1.01f && length - into > step * 1.01f) continue;
                result.Add((Advance(p, h, turn, into).Item1, at));
            }
            return result;
        }

        private (Vector2, float) Advance(Vector2 p, float h, int turn, float length)
        {
            if (turn == 0) return (p + new Vector2(MathF.Cos(h), MathF.Sin(h)) * length, h);
            var centre = p + Radius * turn * new Vector2(-MathF.Sin(h), MathF.Cos(h));
            float next = h + turn * length / Radius;
            return (centre - Radius * turn * new Vector2(-MathF.Sin(next), MathF.Cos(next)), next);
        }
    }

    // The shortest way from (a, h0) to (b, h1) turning no tighter than `radius` (Dubins: three
    // arcs, or two with a straight between; Shkel & Lumelsky's closed forms). Every candidate is
    // walked and must land on b along h1.
    private static TurnPath? Dubins(Vector2 a, float h0, Vector2 b, float h1, float radius)
    {
        var delta = b - a;
        float d = delta.Length() / radius, d2 = d * d;
        float theta = d > 0 ? Mod(MathF.Atan2(delta.Y, delta.X)) : 0;
        float alpha = Mod(h0 - theta), beta = Mod(h1 - theta);
        float sa = MathF.Sin(alpha), sb = MathF.Sin(beta), ca = MathF.Cos(alpha), cb = MathF.Cos(beta), cab = MathF.Cos(alpha - beta);
        TurnPath? best = null;
        void Try(int first, int middle, int last, float t, float p, float q)
        {
            var path = new TurnPath(a, h0, radius, [(first, Arc(t) * radius), (middle, (middle == 0 ? p : Arc(p)) * radius), (last, Arc(q) * radius)]);
            var (end, heading) = path.End();
            float miss = MathF.Abs(MathF.IEEERemainder(heading - h1, MathF.Tau));
            if (Vector2.Distance(end, b) > .01f + .001f * delta.Length() || miss > .01f) return;
            if (best is null || path.Length < best.Length) best = path;
        }
        float sq = 2 + d2 - 2 * cab + 2 * d * (sa - sb);
        if (sq >= 0) { float t1 = MathF.Atan2(cb - ca, d + sa - sb); Try(1, 0, 1, Mod(t1 - alpha), MathF.Sqrt(sq), Mod(beta - t1)); }
        sq = 2 + d2 - 2 * cab + 2 * d * (sb - sa);
        if (sq >= 0) { float t1 = MathF.Atan2(ca - cb, d - sa + sb); Try(-1, 0, -1, Mod(alpha - t1), MathF.Sqrt(sq), Mod(t1 - beta)); }
        sq = -2 + d2 + 2 * cab + 2 * d * (sa + sb);
        if (sq >= 0) { float p = MathF.Sqrt(sq), t0 = MathF.Atan2(-ca - cb, d + sa + sb) - MathF.Atan2(-2, p); Try(1, 0, -1, Mod(t0 - alpha), p, Mod(t0 - beta)); }
        sq = -2 + d2 + 2 * cab - 2 * d * (sa + sb);
        if (sq >= 0) { float p = MathF.Sqrt(sq), t0 = MathF.Atan2(ca + cb, d - sa - sb) - MathF.Atan2(2, p); Try(-1, 0, 1, Mod(alpha - t0), p, Mod(beta - t0)); }
        float c = (6 - d2 + 2 * cab + 2 * d * (sa - sb)) / 8;
        if (MathF.Abs(c) <= 1)
        {
            float p = Mod(MathF.Tau - MathF.Acos(c)), t = Mod(alpha - MathF.Atan2(ca - cb, d - sa + sb) + p / 2);
            Try(-1, 1, -1, t, p, Mod(alpha - beta - t + p));
        }
        c = (6 - d2 + 2 * cab + 2 * d * (sb - sa)) / 8;
        if (MathF.Abs(c) <= 1)
        {
            float p = Mod(MathF.Tau - MathF.Acos(c)), t = Mod(-alpha - MathF.Atan2(ca - cb, d + sa - sb) + p / 2);
            Try(1, -1, 1, t, p, Mod(beta - alpha - t + p));
        }
        return best;
    }

    // From (a, heading) to the point b, its arrival free: an arc no tighter than `radius`, then
    // straight to b (the shorter side). When b is too close aside for that (only a way looping
    // round, or none), one arc through b, tighter than `radius`, unless that one loops too.
    private static TurnPath TurnThenStraight(Vector2 a, float heading, Vector2 b, float radius)
    {
        TurnPath? best = null;
        foreach (int turn in (ReadOnlySpan<int>)[1, -1])
        {
            var centre = a + radius * turn * new Vector2(-MathF.Sin(heading), MathF.Cos(heading));
            var v = b - centre;
            float dc = v.Length();
            if (dc <= radius * 1.0001f) continue;
            float straight = MathF.Sqrt(dc * dc - radius * radius);
            float leaving = MathF.Atan2(v.Y, v.X) + turn * MathF.Atan2(radius, straight);
            float arc = Arc(turn > 0 ? Mod(leaving - heading) : Mod(heading - leaving));
            var path = new TurnPath(a, heading, radius, [(turn, arc * radius), (0, straight)]);
            if (best is null || path.Length < best.Length) best = path;
        }
        if (best is not null && best.LongestArc <= Loop) return best;
        var w = b - a;
        float ahead = Vector2.Dot(w, new(MathF.Cos(heading), MathF.Sin(heading))), aside = Vector2.Dot(w, new(-MathF.Sin(heading), MathF.Cos(heading)));
        if (MathF.Abs(aside) < 1e-3f) return best ?? new(a, heading, radius, [(0, w.Length())]);
        float r = (ahead * ahead + aside * aside) / (2 * MathF.Abs(aside));
        var through = new TurnPath(a, heading, r, [(MathF.Sign(aside), 2 * MathF.Atan2(MathF.Abs(aside), ahead) * r)]);
        return best is not null && through.LongestArc > Loop ? best : through;
    }

    // An arc turning over this goes round on itself (more than a U-turn and a bit): a loop.
    private const float Loop = 1.25f * MathF.PI;
}

public static class RoadBuilder
{
    // Frames are spaced at most this far apart along a segment (the game uses ~5 m).
    public const float FrameSpacing = 2.5f;

    // Splits the road into whole template lengths (stretched evenly so they fit exactly).
    // Pieces cooked for bending need one frame per bone: pass their bone count as frameCount.
    // Gaps (arc length, junctions: RoadJunctions) get no pieces: each stretch between them is
    // split on its own.
    public static ImmutableArray<RoadSegment> Build(RoadDefinition road, float templateLength, int frameCount = 0, IReadOnlyList<(float From, float To)>? gaps = null)
    {
        if (!(templateLength > .1f)) throw new ArgumentOutOfRangeException(nameof(templateLength));
        var line = RoadSpline.Sample(road.Points);
        var arc = new float[line.Length];
        for (int i = 1; i < line.Length; i++) arc[i] = arc[i - 1] + Vector3.Distance(line[i - 1], line[i]);
        float total = arc[^1];
        if (total < .5f) return [];
        // A cable has no side to raise.
        float bank = road.IsCable ? 0 : road.Banking * MathF.PI / 180;
        var result = ImmutableArray.CreateBuilder<RoadSegment>();
        foreach (var (spanStart, spanEnd) in Spans(total, gaps))
        {
            float spanLength = spanEnd - spanStart;
            if (spanLength < .5f) continue;
            int count = Math.Max(1, (int)MathF.Round(spanLength / templateLength));
            float length = spanLength / count;
            int frames = frameCount >= 2 ? frameCount : Math.Max(2, (int)MathF.Ceiling(length / FrameSpacing) + 1);
            for (int k = 0; k < count; k++) result.Add(Segment(result.Count, spanStart + k * length, length, frames));
        }
        return result.ToImmutable();

        RoadSegment Segment(int index, float from, float length, int frames)
        {
            var origin = At(line, arc, from);
            var built = ImmutableArray.CreateBuilder<Matrix4x4>(frames);
            for (int j = 0; j < frames; j++)
            {
                float s = from + length * j / (frames - 1);
                var position = At(line, arc, s);
                var tangent = Tangent(line, arc, s);
                var up = Vector3.UnitZ - Vector3.Dot(Vector3.UnitZ, tangent) * tangent;
                up = up.LengthSquared() < 1e-6f ? Vector3.UnitX : Vector3.Normalize(up);
                if (bank != 0) up = Vector3.Normalize(Vector3.Transform(up, Quaternion.CreateFromAxisAngle(tangent, bank)));
                var right = Vector3.Normalize(Vector3.Cross(tangent, up));
                var w = position - origin;
                built.Add(new Matrix4x4(right.X, right.Y, right.Z, 0, tangent.X, tangent.Y, tangent.Z, 0, up.X, up.Y, up.Z, 1, w.X, w.Y, w.Z, 1));
            }
            return new(index, origin, built.MoveToImmutable(), length);
        }
    }

    // [0, total] without the gaps.
    private static List<(float From, float To)> Spans(float total, IReadOnlyList<(float From, float To)>? gaps)
    {
        var spans = new List<(float, float)>();
        float at = 0;
        foreach (var (from, to) in (gaps ?? []).Where(g => g.To > 0 && g.From < total).OrderBy(g => g.From))
        {
            if (from > at) spans.Add((at, from));
            at = MathF.Max(at, to);
        }
        if (at < total) spans.Add((at, total));
        return spans;
    }

    private static Vector3 At(Vector3[] line, float[] arc, float s)
    {
        if (s <= 0) return line[0];
        if (s >= arc[^1]) return line[^1];
        int hi = Array.BinarySearch(arc, s); if (hi >= 0) return line[hi];
        hi = ~hi; int lo = hi - 1;
        float span = arc[hi] - arc[lo];
        return span <= 1e-6f ? line[lo] : Vector3.Lerp(line[lo], line[hi], (s - arc[lo]) / span);
    }

    private static Vector3 Tangent(Vector3[] line, float[] arc, float s)
    {
        float h = .5f;
        var d = At(line, arc, Math.Min(arc[^1], s + h)) - At(line, arc, Math.Max(0, s - h));
        return d.LengthSquared() < 1e-10f ? Vector3.UnitY : Vector3.Normalize(d);
    }

    // Editable control points for a vanilla bended placement: its deformation frames in game
    // space, kept about every `spacing` metres (ends always kept).
    public static ImmutableArray<Vector3> ControlPoints(PlacementTransform node, IReadOnlyList<Matrix4x4> frames, float spacing = 8)
    {
        var world = frames.Select(f => Vector3.Transform(f.Translation, node.GameMatrix)).ToArray();
        var kept = new List<Vector3> { world[0] };
        float run = 0;
        for (int i = 1; i < world.Length - 1; i++)
        {
            run += Vector3.Distance(world[i - 1], world[i]);
            if (run >= spacing) { kept.Add(world[i]); run = 0; }
        }
        if (world.Length > 1) kept.Add(world[^1]);
        return kept.ToImmutableArray();
    }

    // Average roll of vanilla frames around their direction of travel (degrees).
    public static float Banking(PlacementTransform node, IReadOnlyList<Matrix4x4> frames)
    {
        var rotation = node.GameMatrix; rotation.Translation = Vector3.Zero;
        float sum = 0; int n = 0;
        foreach (var f in frames)
        {
            var tangent = Vector3.TransformNormal(new(f.M21, f.M22, f.M23), rotation);
            var actual = Vector3.TransformNormal(new(f.M31, f.M32, f.M33), rotation);
            if (tangent.LengthSquared() < 1e-6f || actual.LengthSquared() < 1e-6f) continue;
            tangent = Vector3.Normalize(tangent); actual = Vector3.Normalize(actual);
            var level = Vector3.UnitZ - Vector3.Dot(Vector3.UnitZ, tangent) * tangent;
            if (level.LengthSquared() < 1e-6f) continue;
            level = Vector3.Normalize(level);
            float angle = MathF.Atan2(Vector3.Dot(Vector3.Cross(level, actual), tangent), Vector3.Dot(level, actual));
            sum += angle; n++;
        }
        return n == 0 ? 0 : sum / n * 180 / MathF.PI;
    }
}
