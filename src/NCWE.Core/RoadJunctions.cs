// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Numerics;

namespace NCWE.Core;

// Where roads cross, or one ends on another, the game's road tool lays a junction: the roads stop
// at its edge and one surface joins them (asphalt with curb returns at the corners, the sidewalks
// going round them). NCWE does the same with the project's roads. Junctions are derived from the
// roads (found again whenever one changes): the roads are built with gaps there (RoadBuilder) and
// each junction is a generated mesh wearing the game's street materials, with box collision.

// A road type's cross-section: its carriageway (asphalt, at the road's level) and its whole width
// with sidewalks, each side of its axis looking along the road, and how high the sidewalks stand.
public readonly record struct RoadProfile(float CarriageLeft, float CarriageRight, float OuterLeft, float OuterRight, float Curb)
{
    public bool SidewalkLeft => Curb > .05f && OuterLeft > CarriageLeft + .3f;
    public bool SidewalkRight => Curb > .05f && OuterRight > CarriageRight + .3f;
    public float Carriage => MathF.Max(CarriageLeft, CarriageRight);
    public float Outer => MathF.Max(OuterLeft, OuterRight);
    // Seen the other way along the road.
    public RoadProfile Mirrored => new(CarriageRight, CarriageLeft, OuterRight, OuterLeft, Curb);
    public static RoadProfile Flat(float half) => new(half, half, half, half, 0);

    // The highest a raised band may stand and still count as a sidewalk (higher: a barrier or a
    // wall, outside the road).
    private const float SidewalkHeight = .35f;

    // From the boxes RoadCollision gives the template laid straight: the lowest boxes are the
    // carriageway, bands raised a little beside it the sidewalks.
    public static RoadProfile Of(MeshAsset template, RoadBending.Axis axis)
    {
        float length = RoadBending.Length(template, axis);
        if (!(length > .1f)) return Flat(MathF.Max(.5f, RoadBending.HalfWidth(template, axis)));
        int count = Math.Max(2, (int)MathF.Ceiling(length / 2.5f) + 1);
        var frames = Enumerable.Range(0, count).Select(k => new Matrix4x4(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 1, 0, length * k / (count - 1), 0, 1)).ToArray();
        var shapes = RoadCollision.Shapes(RoadBending.Deform(template, frames, "profile", axis), frames);
        if (shapes.IsDefaultOrEmpty) return Flat(MathF.Max(.5f, RoadBending.HalfWidth(template, axis)));
        float Top(CollisionShape s) => s.Position.Z + s.Size.Z;
        float road = shapes.Min(Top);
        var low = shapes.Where(s => Top(s) <= road + .08f).ToArray();
        var walks = shapes.Where(s => Top(s) > road + .08f && Top(s) <= road + SidewalkHeight).ToArray();
        float carriageLeft = MathF.Max(.5f, -low.Min(s => s.Position.X - s.Size.X)), carriageRight = MathF.Max(.5f, low.Max(s => s.Position.X + s.Size.X));
        var surface = low.Concat(walks).ToArray();
        float outerLeft = MathF.Max(carriageLeft, -surface.Min(s => s.Position.X - s.Size.X)), outerRight = MathF.Max(carriageRight, surface.Max(s => s.Position.X + s.Size.X));
        float curb = walks.Length == 0 ? 0 : walks.Average(Top) - road;
        return new(carriageLeft, carriageRight, outerLeft, outerRight, curb);
    }
}

// A road taking part in junctions: its points, its cross-section and whether it is cut at them
// (a project road), or only crossed (a game road, the junction laid on top of it).
public sealed record JunctionRoad(string Id, ImmutableArray<Vector3> Points, RoadProfile Profile, bool Trimmed = true)
{
    // A game road's surface: its up vector at each point (banking; empty: flat across).
    public ImmutableArray<Vector3> Ups { get; init; } = [];
    // The street materials of its type (null: unknown, or none).
    public JunctionStyle? Style { get; init; }
}

// One of the game's street multilayers (engine\materials\multilayered.mt): a setup and its mask.
public readonly record struct StreetLayer(string Setup, string Mask);

// The street materials a junction wears, per part (RoadJunctions.Materials: asphalt, sidewalk,
// curb): those of a road type (its pieces' own multilayers), so a junction looks like its roads.
public sealed record JunctionStyle(StreetLayer Asphalt, StreetLayer Sidewalk, StreetLayer Curb)
{
    // The game's plain street (when no road type tells).
    public static readonly JunctionStyle Default = new(Layer(0), Layer(1), Layer(2));
    private static StreetLayer Layer(int part) => new(RoadJunctions.Materials[part].Setup, RoadJunctions.Materials[part].Mask);

    public StreetLayer this[int part] => part switch { 0 => Asphalt, 1 => Sidewalk, _ => Curb };

    // A road piece's style from the multilayers it wears: the street's sidewalk and misc (curbs)
    // folders tell those apart, its asphalt is the rest; missing parts are the defaults'. Null
    // when it wears no street multilayer (an untextured shape, a deco piece).
    public static JunctionStyle? Of(IEnumerable<StreetLayer> layers)
    {
        StreetLayer? asphalt = null, sidewalk = null, curb = null;
        foreach (var layer in layers)
        {
            string setup = layer.Setup.Replace('/', '\\');
            if (string.IsNullOrEmpty(layer.Mask) || !setup.Contains(@"\street\", StringComparison.OrdinalIgnoreCase)) continue;
            if (setup.Contains(@"\sidewalk\", StringComparison.OrdinalIgnoreCase)) sidewalk ??= layer;
            else if (setup.Contains(@"\misc\", StringComparison.OrdinalIgnoreCase)) curb ??= layer;
            else if (setup.Contains("asphalt", StringComparison.OrdinalIgnoreCase)) asphalt ??= layer;
        }
        return asphalt is null && sidewalk is null && curb is null ? null : new(asphalt ?? Default.Asphalt, sidewalk ?? Default.Sidewalk, curb ?? Default.Curb);
    }
}

// A game road a junction lies on: its points and up vectors there, and its half width. The
// junction's asphalt follows its surface (crown and banking included) over it.
public sealed record JunctionBed(ImmutableArray<Vector3> Points, ImmutableArray<Vector3> Ups, float Half)
{
    // The bed's surface height under a point (seen from above), when the point is on it.
    public float? HeightAt(Vector2 p)
    {
        float best = float.MaxValue; Vector3 q = default, up = Vector3.UnitZ;
        for (int i = 0; i + 1 < Points.Length; i++)
        {
            Vector2 a = new(Points[i].X, Points[i].Y), ab = new Vector2(Points[i + 1].X, Points[i + 1].Y) - a;
            float t = ab.LengthSquared() < 1e-10f ? 0 : Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0, 1);
            float d = Vector2.DistanceSquared(p, a + ab * t);
            if (d >= best) continue;
            best = d; q = Vector3.Lerp(Points[i], Points[i + 1], t);
            up = Ups.Length == Points.Length ? Vector3.Lerp(Ups[i], Ups[i + 1], t) : Vector3.UnitZ;
        }
        if (best == float.MaxValue || MathF.Sqrt(best) > Half + .3f || up.Z < .2f) return null;
        // On the plane through the nearest centre point, square to its up vector.
        return q.Z - (up.X * (p.X - q.X) + up.Y * (p.Y - q.Y)) / up.Z;
    }
}

// One road leaving a junction: along it (Forward) or back from the station where it meets the
// junction's centre, its direction out of it seen from above (towards where the junction ends
// along it: the road may bend inside it), its cross-section seen going out, where the junction
// ends along it (Cut, metres along the road from the centre) and its centre line every metre out
// (straight on past the road's end).
public sealed record JunctionArm(string RoadId, bool Forward, float Station, Vector2 Direction, RoadProfile Profile, bool Trimmed, float Cut, ImmutableArray<Vector3> Line)
{
    // The road's centre `t` metres out along it.
    public Vector3 PointAt(float t)
    {
        if (Line.IsDefaultOrEmpty) return default;
        float f = Math.Clamp(t, 0, Line.Length - 1);
        int i = Math.Min((int)f, Line.Length - 2);
        return i < 0 ? Line[0] : Vector3.Lerp(Line[i], Line[i + 1], f - i);
    }

    public float HeightAt(float t) => PointAt(t).Z;

    // The road's direction going out there, seen from above.
    public Vector2 TangentAt(float t)
    {
        Vector3 a = PointAt(MathF.Max(0, t - .75f)), b = PointAt(t + .75f);
        var d = new Vector2(b.X - a.X, b.Y - a.Y);
        return d.LengthSquared() < 1e-8f ? Direction : Vector2.Normalize(d);
    }

    // The point `offset` metres to the road's left there (negative: to its right), seen from above:
    // across the road where its pieces end at a cut `t` metres out.
    public Vector2 Edge(float t, float offset)
    {
        var p = PointAt(t); var u = TangentAt(t);
        return new Vector2(p.X, p.Y) + new Vector2(-u.Y, u.X) * offset;
    }
}

// Arms in counter-clockwise order (seen from above) round the centre; Radius: the curb returns'.
public sealed record Junction(string Key, Vector3 Center, ImmutableArray<JunctionArm> Arms, float Radius)
{
    // The game roads it lies on.
    public ImmutableArray<JunctionBed> Beds { get; init; } = [];
    // Its materials: those of its widest project road's type.
    public JunctionStyle Style { get; init; } = JunctionStyle.Default;
}

// The junctions of a set of roads and the stretches (arc length, as RoadBuilder lays pieces)
// each trimmed road leaves to them.
public sealed record JunctionPlan(ImmutableArray<Junction> Junctions, ImmutableDictionary<string, ImmutableArray<(float From, float To)>> Gaps)
{
    public static readonly JunctionPlan Empty = new([], ImmutableDictionary<string, ImmutableArray<(float, float)>>.Empty);
    public ImmutableArray<(float From, float To)> GapsOf(string roadId) => Gaps.TryGetValue(roadId, out var gaps) ? gaps : [];
}

public static class RoadJunctions
{
    // Roads closer than this in height where they cross meet (higher: one passes over the other).
    public const float MaxStep = 2f;
    // Curb returns: radius from the widest carriageway at the junction, within these.
    public const float MinRadius = 3, MaxRadius = 9;
    private const float Margin = .5f;
    // Neighbouring arms closer than this make no junction; no junction reaches further out.
    private const float MinArmAngle = 20 * MathF.PI / 180, MaxCut = 60;
    // A junction laid on a game road stands this much over its surface.
    public const float OverGameRoad = .05f;
    // A sidewalk corner's arc steps; steps along a road's edge.
    private const float ArcStep = MathF.PI / 12, EdgeStep = 2;

    public static float RadiusFor(IEnumerable<RoadProfile> profiles) => Math.Clamp(.6f * profiles.Max(p => p.Carriage), MinRadius, MaxRadius);

    // --- Finding junctions -------------------------------------------------------------------------

    // A road's centre line as RoadBuilder lays pieces along it: points every 0.25 m and their arc
    // length, in chunks with their boxes (seen from above) to test crossings quickly.
    private sealed class Line
    {
        public readonly JunctionRoad Road;
        public readonly Vector3[] Points;
        public readonly float[] Arc;
        public readonly List<(int From, int To, Vector2 Min, Vector2 Max)> Chunks = [];
        public float Length => Arc.Length == 0 ? 0 : Arc[^1];

        public Line(JunctionRoad road)
        {
            Road = road;
            Points = RoadSpline.Sample(road.Points);
            Arc = new float[Points.Length];
            for (int i = 1; i < Points.Length; i++) Arc[i] = Arc[i - 1] + Vector3.Distance(Points[i - 1], Points[i]);
            for (int from = 0; from + 1 < Points.Length; from += 16)
            {
                int to = Math.Min(Points.Length - 1, from + 16);
                Vector2 min = new(float.MaxValue), max = new(float.MinValue);
                for (int i = from; i <= to; i++) { var p = Flat(Points[i]); min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
                Chunks.Add((from, to, min, max));
            }
        }

        public Vector3 At(float s)
        {
            if (Points.Length == 0) return default;
            if (s <= 0) return Points[0];
            if (s >= Length) return Points[^1];
            int hi = Array.BinarySearch(Arc, s); if (hi >= 0) return Points[hi];
            hi = ~hi; int lo = hi - 1; float span = Arc[hi] - Arc[lo];
            return span <= 1e-6f ? Points[lo] : Vector3.Lerp(Points[lo], Points[hi], (s - Arc[lo]) / span);
        }

        // Travel direction at a station, seen from above.
        public Vector2 Tangent(float s)
        {
            var d = Flat(At(Math.Min(Length, s + 1.5f)) - At(Math.Max(0, s - 1.5f)));
            return d.LengthSquared() < 1e-8f ? Vector2.UnitY : Vector2.Normalize(d);
        }

        // The line at a station, going straight on (level) past its ends.
        public Vector3 Extended(float s)
        {
            if (Points.Length == 0 || (s >= 0 && s <= Length)) return At(s);
            bool before = s < 0;
            var u = Tangent(before ? 0 : Length);
            return (before ? Points[0] : Points[^1]) + new Vector3(u * (before ? s : s - Length), 0);
        }

        // The nearest point of the line to a point, seen from above: its station and distance.
        public (float Station, float Distance) Nearest(Vector2 p)
        {
            float best = float.MaxValue, station = 0;
            for (int i = 0; i + 1 < Points.Length; i++)
            {
                Vector2 a = Flat(Points[i]), b = Flat(Points[i + 1]), ab = b - a;
                float t = ab.LengthSquared() < 1e-10f ? 0 : Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0, 1);
                float d = Vector2.Distance(p, a + ab * t);
                if (d < best) { best = d; station = float.Lerp(Arc[i], Arc[i + 1], t); }
            }
            return (station, best);
        }
    }

    private static Vector2 Flat(Vector3 v) => new(v.X, v.Y);
    private static Vector2 Left(Vector2 u) => new(-u.Y, u.X);

    // A place where roads meet, and each road's station there.
    private sealed class Meeting(Vector3 point)
    {
        public Vector3 Point = point;
        public readonly List<(int Line, float Station)> Members = [];
        public void Add(int line, float station) { if (!Members.Any(m => m.Line == line && MathF.Abs(m.Station - station) < 1)) Members.Add((line, station)); }
    }

    // The junctions of these roads: at grade, where two cross (an X), or where one ends on another
    // (a T, the ending road's tip within the other's width), with each trimmed road's gaps.
    public static JunctionPlan Find(IReadOnlyList<JunctionRoad> roads)
    {
        var lines = roads.Where(r => !r.Points.IsDefault && r.Points.Length >= 2).Select(r => new Line(r)).Where(l => l.Length > 1).ToArray();
        if (lines.Length == 0) return JunctionPlan.Empty;
        var meetings = new List<Meeting>();
        void Meet(Vector3 point, int a, float sa, int b, float sb)
        {
            var near = meetings.FirstOrDefault(m => Vector2.Distance(Flat(m.Point), Flat(point)) < 1.5f);
            if (near is null) meetings.Add(near = new Meeting(point));
            near.Add(a, sa); near.Add(b, sb);
        }
        // Crossings (X).
        // (Two game roads already have their junction in the game.)
        for (int a = 0; a < lines.Length; a++)
            for (int b = a; b < lines.Length; b++)
                if (lines[a].Road.Trimmed || lines[b].Road.Trimmed)
                foreach (var (sa, sb, point) in Crossings(lines[a], lines[b], a == b))
                    if (MathF.Abs(lines[a].At(sa).Z - lines[b].At(sb).Z) <= MaxStep) Meet(point, a, sa, b, sb);
        // Ends on another road (T).
        for (int a = 0; a < lines.Length; a++)
            foreach (bool start in (ReadOnlySpan<bool>)[true, false])
            {
                var line = lines[a];
                float endStation = start ? 0 : line.Length;
                var tip = line.At(endStation);
                // Direction of travel into the tip.
                var into = start ? -line.Tangent(0) : line.Tangent(line.Length);
                for (int b = 0; b < lines.Length; b++)
                {
                    var other = lines[b];
                    if (!line.Road.Trimmed && !other.Road.Trimmed) continue;
                    var (sb, distance) = other.Nearest(Flat(tip));
                    float reach = other.Road.Profile.Outer + 1.5f, keep = line.Road.Profile.Outer + 2;
                    if (distance > reach || sb < keep || sb > other.Length - keep) continue;
                    if (a == b && MathF.Abs(sb - endStation) < 3 * (line.Road.Profile.Outer + 2)) continue;
                    var tangent = other.Tangent(sb);
                    if (MathF.Abs(Vector2.Dot(into, tangent)) > MathF.Cos(25 * MathF.PI / 180)) continue;
                    var q = other.At(sb);
                    if (MathF.Abs(q.Z - tip.Z) > MaxStep) continue;
                    // The centre: where the ending road's line meets the other's (its station past the tip, or short of it).
                    var n = Left(tangent);
                    float denom = Vector2.Dot(into, n);
                    float ahead = MathF.Abs(denom) < 1e-4f ? 0 : Vector2.Dot(Flat(q) - Flat(tip), n) / denom;
                    var center = Flat(tip) + into * ahead;
                    var (sc, _) = other.Nearest(center);
                    float stationOnA = start ? -ahead : line.Length + ahead;
                    Meet(new Vector3(center, (other.At(sc).Z + tip.Z) / 2), a, stationOnA, b, sc);
                }
            }
        var junctions = ImmutableArray.CreateBuilder<Junction>();
        var gaps = new Dictionary<string, List<(float, float)>>(StringComparer.Ordinal);
        void Gap(string road, float from, float to)
        {
            if (!gaps.TryGetValue(road, out var list)) gaps[road] = list = [];
            list.Add((from, to));
        }
        foreach (var meeting in meetings)
        {
            if (Build(meeting, lines) is not { } built) continue;
            var (junction, ends) = built;
            junctions.Add(junction);
            foreach (var arm in junction.Arms.Where(a => a.Trimmed))
                if (arm.Forward) Gap(arm.RoadId, arm.Station, arm.Station + arm.Cut); else Gap(arm.RoadId, arm.Station - arm.Cut, arm.Station);
            // A road ending inside the junction: its tip goes too.
            foreach (var (road, from, to) in ends) Gap(road, from, to);
        }
        return new(junctions.ToImmutable(), gaps.ToImmutableDictionary(p => p.Key, p => Merge(p.Value), StringComparer.Ordinal));
    }

    private static ImmutableArray<(float From, float To)> Merge(List<(float From, float To)> gaps)
    {
        var merged = new List<(float From, float To)>();
        foreach (var g in gaps.OrderBy(g => g.From))
            if (merged.Count > 0 && g.From <= merged[^1].To) merged[^1] = (merged[^1].From, MathF.Max(merged[^1].To, g.To));
            else merged.Add(g);
        return [.. merged];
    }

    // Where two centre lines cross, seen from above (for a road with itself, away from the stretch
    // around each station), not nearly parallel.
    private static IEnumerable<(float A, float B, Vector3 Point)> Crossings(Line a, Line b, bool same)
    {
        float apart = 3 * (a.Road.Profile.Outer + 2);
        foreach (var ca in a.Chunks)
            foreach (var cb in b.Chunks)
            {
                if (ca.Max.X < cb.Min.X || cb.Max.X < ca.Min.X || ca.Max.Y < cb.Min.Y || cb.Max.Y < ca.Min.Y) continue;
                for (int i = ca.From; i < ca.To; i++)
                    for (int j = cb.From; j < cb.To; j++)
                    {
                        if (same && (j <= i || MathF.Abs(a.Arc[i] - b.Arc[j]) < apart)) continue;
                        Vector2 p = Flat(a.Points[i]), r = Flat(a.Points[i + 1]) - p, q = Flat(b.Points[j]), s = Flat(b.Points[j + 1]) - q;
                        float denom = r.X * s.Y - r.Y * s.X;
                        if (MathF.Abs(denom) < 1e-9f) continue;
                        float t = ((q.X - p.X) * s.Y - (q.Y - p.Y) * s.X) / denom, u = ((q.X - p.X) * r.Y - (q.Y - p.Y) * r.X) / denom;
                        if (t < 0 || t >= 1 || u < 0 || u >= 1) continue;
                        // Nearly parallel roads merge rather than cross.
                        if (MathF.Abs(Vector2.Dot(Vector2.Normalize(r), Vector2.Normalize(s))) > MathF.Cos(15 * MathF.PI / 180)) continue;
                        float sa = float.Lerp(a.Arc[i], a.Arc[i + 1], t), sb = float.Lerp(b.Arc[j], b.Arc[j + 1], u);
                        var point = Vector3.Lerp(a.Points[i], a.Points[i + 1], t);
                        yield return (sa, sb, point with { Z = (point.Z + Vector3.Lerp(b.Points[j], b.Points[j + 1], u).Z) / 2 });
                    }
            }
    }

    // A meeting's arms (each road, both ways from its station, unless it ends inside the
    // junction: then that tip is trimmed), counter-clockwise, and where the junction ends along each.
    private static (Junction Junction, List<(string Road, float From, float To)> Ends)? Build(Meeting meeting, Line[] lines)
    {
        var arms = new List<(Line Line, bool Forward, float Station, Vector2 Direction, RoadProfile Profile)>();
        var ends = new List<(string Road, float From, float To)>();
        float widest = meeting.Members.Max(m => lines[m.Line].Road.Profile.Outer);
        foreach (var (index, station) in meeting.Members)
        {
            var line = lines[index];
            var tangent = line.Tangent(Math.Clamp(station, 0, line.Length));
            float stub = .6f * widest;
            if (line.Length - station > stub) arms.Add((line, true, station, tangent, line.Road.Profile));
            else if (line.Road.Trimmed && station < line.Length) ends.Add((line.Road.Id, station, line.Length));
            if (station > stub) arms.Add((line, false, station, -tangent, line.Road.Profile.Mirrored));
            else if (line.Road.Trimmed && station > 0) ends.Add((line.Road.Id, 0, station));
        }
        if (arms.Count < 3) return null;
        float radius = RadiusFor(arms.Select(a => a.Profile));
        var P = Flat(meeting.Point);
        float[] cuts = [];
        // Each arm points at where the junction ends along its road (a road bending inside the
        // junction: the corners are laid out between those chords), found again as the cuts move.
        for (int pass = 0; ; pass++)
        {
            arms.Sort((x, y) => MathF.Atan2(x.Direction.Y, x.Direction.X).CompareTo(MathF.Atan2(y.Direction.Y, y.Direction.X)));
            // Arms nearly along each other: the roads merge or graze rather than cross (a junction
            // there would run along both for tens of metres).
            for (int i = 0; i < arms.Count; i++)
                if (Vector2.Dot(arms[i].Direction, arms[(i + 1) % arms.Count].Direction) > MathF.Cos(MinArmAngle)) return null;
            cuts = Cuts([.. arms.Select(a => (a.Direction, a.Profile))], radius);
            if (pass == 2) break;
            for (int i = 0; i < arms.Count; i++)
            {
                var (line, forward, station, _, profile) = arms[i];
                var chord = Flat(line.Extended(forward ? station + cuts[i] : station - cuts[i])) - P;
                if (chord.LengthSquared() > 1e-4f) arms[i] = (line, forward, station, Vector2.Normalize(chord), profile);
            }
        }
        var built = ImmutableArray.CreateBuilder<JunctionArm>(arms.Count);
        for (int i = 0; i < arms.Count; i++)
        {
            var (line, forward, station, direction, profile) = arms[i];
            int count = (int)MathF.Ceiling(cuts[i]) + 3;
            var points = ImmutableArray.CreateRange(Enumerable.Range(0, count).Select(t => line.Extended(forward ? station + t : station - t)));
            built.Add(new(line.Road.Id, forward, station, direction, profile, line.Road.Trimmed, cuts[i], points));
        }
        // On a game road, at its height (the project road meets it there).
        var level = built.Where(a => !a.Trimmed).ToList();
        var center = meeting.Point with { Z = (level.Count > 0 ? level : [.. built]).Average(a => a.HeightAt(0)) };
        string key = string.Join("+", meeting.Members.OrderBy(m => lines[m.Line].Road.Id, StringComparer.Ordinal).ThenBy(m => m.Station)
            .Select(m => $"{lines[m.Line].Road.Id}@{MathF.Round(m.Station)}"));
        // The game roads it lies on, around it.
        var beds = meeting.Members.Select(m => lines[m.Line].Road).Where(r => !r.Trimmed).DistinctBy(r => r.Id).Select(r =>
        {
            var keep = Enumerable.Range(0, r.Points.Length).Where(i => Vector2.Distance(Flat(r.Points[i]), Flat(meeting.Point)) < 80).ToArray();
            if (keep.Length < 2) return null;
            int from = Math.Max(0, keep[0] - 1), to = Math.Min(r.Points.Length - 1, keep[^1] + 1);
            return new JunctionBed(r.Points[from..(to + 1)], r.Ups.Length == r.Points.Length ? r.Ups[from..(to + 1)] : [], r.Profile.Outer);
        }).OfType<JunctionBed>().ToImmutableArray();
        var style = arms.Where(a => a.Line.Road.Trimmed && a.Line.Road.Style is not null)
            .OrderByDescending(a => a.Profile.CarriageLeft + a.Profile.CarriageRight).ThenBy(a => a.Line.Road.Id, StringComparer.Ordinal)
            .Select(a => a.Line.Road.Style).FirstOrDefault() ?? JunctionStyle.Default;
        return (new(key, center, built.MoveToImmutable(), radius) { Beds = beds, Style = style }, ends);
    }

    // --- The junction's outline --------------------------------------------------------------------

    // The corner between an arm (its left edge) and the next one counter-clockwise (its right edge):
    // straight across when they are nearly opposite, else the curb return of `radius`: where it
    // leaves each edge (metres out along the arm) and its centre; and where the outer edges (the
    // sidewalks' far sides) meet, metres out along each arm.
    private readonly record struct Corner(bool Straight, float Angle, float LeftOut, float RightOut, Vector2 Center, float OuterLeftOut, float OuterRightOut);

    private static Corner CornerOf(Vector2 ui, RoadProfile pi, Vector2 uj, RoadProfile pj, float radius)
    {
        float angle = MathF.Atan2(ui.X * uj.Y - ui.Y * uj.X, Vector2.Dot(ui, uj));
        if (angle <= 0) angle += MathF.Tau;
        if (angle >= MathF.PI - .2f) return new(true, angle, 0, 0, default, 0, 0);
        Vector2 ni = Left(ui), nj = Left(uj);
        float det = ui.X * -uj.Y - -uj.X * ui.Y;
        // Where ni·a + t·ui meets -nj·b + s·uj.
        (float T, float S) Meet(float a, float b)
        {
            var rhs = -nj * b - ni * a;
            return ((rhs.X * -uj.Y - -uj.X * rhs.Y) / det, (ui.X * rhs.Y - rhs.X * ui.Y) / det);
        }
        var (t, s) = Meet(pi.CarriageLeft, pj.CarriageRight);
        var (to, so) = Meet(pi.OuterLeft, pj.OuterRight);
        float tangent = radius / MathF.Tan(angle / 2);
        float left = MathF.Max(0, t + tangent), right = MathF.Max(0, s + tangent);
        return new(false, angle, left, right, ni * (pi.CarriageLeft + radius) + ui * left, MathF.Max(0, to), MathF.Max(0, so));
    }

    private static bool Sidewalks(RoadProfile left, RoadProfile right) => left.SidewalkLeft && right.SidewalkRight;

    // The curb line from an arm's cut (its left edge) to the next arm's (its right edge), with the
    // sidewalk's far side beside it: in along the first road's edge to where the curb return
    // leaves it, round the return (tangent to both edges, a circle's arc where the roads run
    // straight), out along the next road's edge. Nearly opposite arms: along both edges through
    // the centre. The edges are the roads' own, bends included.
    private static List<CornerPoint> CornerPath(JunctionArm a, JunctionArm b, Corner corner, float radius)
    {
        var path = new List<CornerPoint>();
        float wi = a.Profile.OuterLeft - a.Profile.CarriageLeft, wj = b.Profile.OuterRight - b.Profile.CarriageRight;
        // A sidewalk wider than the curb return: its outer corner is the point where the outer
        // edges meet, the outer side runs along each edge up to it (an offset arc would fold).
        bool sharp = !corner.Straight && radius - (wi + wj) / 2 < .2f;
        var outerCorner = (a.Edge(corner.OuterLeftOut, a.Profile.OuterLeft) + b.Edge(corner.OuterRightOut, -b.Profile.OuterRight)) / 2;
        float outerCornerZ = (a.HeightAt(corner.OuterLeftOut) + b.HeightAt(corner.OuterRightOut)) / 2;
        void Add(Vector2 curb, Vector2 outer, float z)
        {
            if (path.Count == 0 || Vector2.DistanceSquared(path[^1].Curb, curb) > 1e-4f)
                path.Add(new(curb, outer, z, sharp && Vector2.DistanceSquared(outer, outerCorner) < 1e-6f ? outerCornerZ : z));
        }
        Vector2 OuterA(float t) => sharp && t < corner.OuterLeftOut ? outerCorner : a.Edge(t, a.Profile.OuterLeft);
        Vector2 OuterB(float t) => sharp && t < corner.OuterRightOut ? outerCorner : b.Edge(t, -b.Profile.OuterRight);
        float inA = corner.Straight ? 0 : MathF.Min(corner.LeftOut, a.Cut), inB = corner.Straight ? 0 : MathF.Min(corner.RightOut, b.Cut);
        // Straight across: the edges meet at the centre, where each arm's direction is one-sided
        // (a bending road's two arms disagree a little): one point between them there.
        float near = corner.Straight ? 1 : .05f;
        for (float t = a.Cut; t > inA + near; t -= EdgeStep) Add(a.Edge(t, a.Profile.CarriageLeft), OuterA(t), a.HeightAt(t));
        if (corner.Straight)
            Add((a.Edge(0, a.Profile.CarriageLeft) + b.Edge(0, -b.Profile.CarriageRight)) / 2, (OuterA(0) + OuterB(0)) / 2, (a.HeightAt(0) + b.HeightAt(0)) / 2);
        else
        {
            Add(a.Edge(inA, a.Profile.CarriageLeft), OuterA(inA), a.HeightAt(inA));
            Vector2 curbA = a.Edge(inA, a.Profile.CarriageLeft), curbB = b.Edge(inB, -b.Profile.CarriageRight);
            // Back along each edge from where the return leaves it: where they meet is its control point.
            Vector2 backA = -a.TangentAt(inA), backB = -b.TangentAt(inB);
            var control = Meet(curbA, backA, curbB, backB) ?? (curbA + curbB) / 2;
            Vector2 outerA = OuterA(inA), outerB = OuterB(inB);
            var outerControl = sharp ? outerCorner : Meet(outerA, backA, outerB, backB) ?? (outerA + outerB) / 2;
            float turn = MathF.PI - corner.Angle, weight = MathF.Cos(turn / 2);
            int steps = Math.Max(2, (int)MathF.Ceiling(turn / ArcStep));
            float zA = a.HeightAt(inA), zB = b.HeightAt(inB);
            for (int k = 1; k < steps; k++)
            {
                float s = (float)k / steps;
                Add(Conic(curbA, control, curbB, weight, s), sharp ? outerCorner : Conic(outerA, outerControl, outerB, weight, s), float.Lerp(zA, zB, s));
            }
        }
        var outward = new List<float>();
        for (float t = b.Cut; t > inB + near; t -= EdgeStep) outward.Add(t);
        if (!corner.Straight) outward.Add(inB);
        for (int k = outward.Count - 1; k >= 0; k--) Add(b.Edge(outward[k], -b.Profile.CarriageRight), OuterB(outward[k]), b.HeightAt(outward[k]));
        return path;
    }

    // A point of a corner's path: on the curb line, across from it on the sidewalk's outer side
    // (a sharp corner's outer side has one point and one height), the road's height at each.
    private readonly record struct CornerPoint(Vector2 Curb, Vector2 Outer, float Z, float OuterZ);

    // Every corner's path, from each arm to the next counter-clockwise.
    private static List<CornerPoint>[] CornerPaths(Junction junction)
    {
        var arms = junction.Arms;
        return [.. arms.Select((a, i) =>
        {
            var b = arms[(i + 1) % arms.Length];
            return CornerPath(a, b, CornerOf(a.Direction, a.Profile, b.Direction, b.Profile, junction.Radius), junction.Radius);
        })];
    }

    // The asphalt's outline: per arm its cut (across the road where its pieces end: right then left
    // carriageway edge), then the corner to the next arm (up to the next arm's cut, which adds it);
    // over a game road at its surface's height.
    private static List<(Vector2 Point, float Z)> Outline(Junction junction, List<CornerPoint>[] corners)
    {
        var outline = new List<(Vector2 Point, float Z)>();
        for (int i = 0; i < junction.Arms.Length; i++)
        {
            var a = junction.Arms[i];
            outline.Add((a.Edge(a.Cut, -a.Profile.CarriageRight), a.HeightAt(a.Cut)));
            for (int k = 0; k + 1 < corners[i].Count; k++) outline.Add((corners[i][k].Curb, corners[i][k].Z));
        }
        for (int k = 0; k < outline.Count; k++) if (BedHeight(junction, outline[k].Point) is { } z) outline[k] = (outline[k].Point, z);
        return outline;
    }

    // The height of the game roads a junction lies on under a point (the highest), if any.
    private static float? BedHeight(Junction junction, Vector2 p)
    {
        float? z = null;
        if (!junction.Beds.IsDefaultOrEmpty)
            foreach (var bed in junction.Beds) if (bed.HeightAt(p) is { } h) z = z is { } other ? MathF.Max(other, h) : h;
        return z;
    }

    // A rational quadratic Bézier: with ends as far from the control point and the weight the
    // cosine of half its turn, a circle's arc.
    private static Vector2 Conic(Vector2 a, Vector2 control, Vector2 b, float weight, float t)
    {
        float wa = (1 - t) * (1 - t), wc = 2 * weight * t * (1 - t), wb = t * t;
        return (a * wa + control * wc + b * wb) / (wa + wc + wb);
    }

    // Where the lines from p along d and from q along e meet, ahead of both (not parallel).
    private static Vector2? Meet(Vector2 p, Vector2 d, Vector2 q, Vector2 e)
    {
        float denom = d.X * e.Y - d.Y * e.X;
        if (MathF.Abs(denom) < 1e-3f) return null;
        var pq = q - p;
        float s = (pq.X * e.Y - pq.Y * e.X) / denom, t = (pq.X * d.Y - pq.Y * d.X) / denom;
        return s < -.5f || t < -.5f || s > MaxCut || t > MaxCut ? null : p + d * s;
    }

    // How far out along each arm the junction reaches: past its curb returns, a margin beyond.
    public static float[] Cuts(IReadOnlyList<(Vector2 Direction, RoadProfile Profile)> arms, float radius)
    {
        int n = arms.Count;
        var cuts = new float[n];
        for (int i = 0; i < n; i++) cuts[i] = MathF.Max(1, arms[i].Profile.Carriage);
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            var corner = CornerOf(arms[i].Direction, arms[i].Profile, arms[j].Direction, arms[j].Profile, radius);
            if (corner.Straight) continue;
            cuts[i] = MathF.Max(cuts[i], corner.LeftOut);
            cuts[j] = MathF.Max(cuts[j], corner.RightOut);
            // The sidewalk corner reaches the outer edges' meeting point: the arms' sidewalks start beyond it.
            if (Sidewalks(arms[i].Profile, arms[j].Profile)) { cuts[i] = MathF.Max(cuts[i], corner.OuterLeftOut); cuts[j] = MathF.Max(cuts[j], corner.OuterRightOut); }
        }
        for (int i = 0; i < n; i++) cuts[i] = MathF.Min(cuts[i] + Margin, MaxCut);
        return cuts;
    }

    // --- The junction's mesh -----------------------------------------------------------------------

    // Game space (Z up), triangles counter-clockwise seen from their front; one index list per
    // material: the asphalt, the sidewalks' tops and the curbs' faces.
    public sealed class JunctionMesh
    {
        public readonly List<Vector3> Positions = [], Normals = [];
        public readonly List<Vector2> Uv = [];
        public readonly List<uint> Asphalt = [], Sidewalk = [], Curb = [];
        public readonly List<CollisionShape> Collision = [];

        public uint Add(Vector3 position, Vector3 normal, Vector2 uv) { Positions.Add(position); Normals.Add(normal); Uv.Add(uv); return (uint)(Positions.Count - 1); }

        public Bounds3 Bounds => Bounds3.From(Positions);
    }

    // A junction's parts and the game's plain street multilayers they wear when no road type
    // tells (JunctionStyle.Default): the editor bakes them, the export writes them as the mesh's
    // own multilayered material instances.
    public static readonly ImmutableArray<(string Name, string Setup, string Mask)> Materials =
    [
        ("asphalt", @"base\environment\street\common\textures\default\ml_street_a_asphalt_plane.mlsetup", @"base\environment\street\common\textures\default\ml_street_a_asphalt_masksset.mlmask"),
        ("sidewalk", @"base\environment\street\common\textures\sidewalk\ml_street_a_sidewalk_default.mlsetup", @"base\environment\street\common\textures\sidewalk\ml_street_a_sidewalk_default_masksset.mlmask"),
        ("curb", @"base\environment\street\common\textures\misc\ml_street_a_misc.mlsetup", @"base\environment\street\common\textures\misc\ml_street_a_misc_masksset.mlmask"),
    ];

    // Texture scales and places in the street atlases, as the game's road pieces use them (UVs in
    // the editor's convention, v down; the cooker writes the game's). Asphalt: the lane strip (the
    // atlas' left third, 30 m per unit across, 24 m along) carries lanes between markings (lines
    // at u 0.01, 0.16 to 0.175 and 0.325); right of it lies a plain asphalt much lighter than the
    // lanes. The junction's asphalt wears one lane, worn by traffic as the road's are, mirrored
    // from strip to strip across the main road (no seam, no marking). Sidewalk: tiles over the
    // whole atlas. Curbs: a strip along u (metres per unit along the curb), the cross-section
    // down v: the face from its top to the gutter, the sidewalk's outer side from its top to
    // 0.2 m under the road.
    public const float SidewalkScale = 8.5f, CurbScale = 3.9f;
    private const float LaneU0 = .025f, LaneU1 = .145f, LaneAcross = 30, LaneAlong = 24;
    private const float CurbFaceTop = .343f, CurbFaceBottom = .349f, OuterSideBottom = .358f;
    // The gutter along the curbs, on the road (the pieces' curb strip goes on 0.58 m into it):
    // from the curb's foot to the asphalt down v, laid a centimetre over the asphalt.
    private const float GutterCurb = .350f, GutterRoad = .373f, GutterWidth = .58f, GutterLift = .01f;
    // How far the sidewalks' outer sides reach under the road's level (into the ground, shaped
    // TerrainShape.Below under it).
    private const float Skirt = .4f;

    // The surface of a junction: the asphalt (fanned from the centre: the outline is star-shaped
    // round it), the sidewalks round the corners where both roads have one, the curbs' faces.
    public static JunctionMesh Mesh(Junction junction)
    {
        var mesh = new JunctionMesh();
        var arms = junction.Arms;
        int n = arms.Length;
        var P = new Vector2(junction.Center.X, junction.Center.Y);
        // The asphalt's texture axes: along the widest road going through the junction (two arms
        // of it nearly opposite), else along its widest arm.
        var main = arms.MaxBy(a => a.Profile.CarriageLeft + a.Profile.CarriageRight)!;
        var through = arms.SelectMany((a, i) => arms.Skip(i + 1).Where(b => b.RoadId == a.RoadId && Vector2.Dot(a.Direction, b.Direction) < -.7f).Select(b => (A: a, B: b)))
            .OrderByDescending(p => p.A.Profile.CarriageLeft + p.A.Profile.CarriageRight).FirstOrDefault();
        Vector2 along = through.A is { } first ? Vector2.Normalize(first.Direction - through.B.Direction) : main.Direction, across = Left(along);
        Vector2 SidewalkUv(Vector2 p) => new(Vector2.Dot(p - P, across) / SidewalkScale, Vector2.Dot(p - P, along) / SidewalkScale);

        var corners = CornerPaths(junction);
        var outline = Outline(junction, corners);
        float? BedHeight(Vector2 p) => RoadJunctions.BedHeight(junction, p);
        float centerZ = BedHeight(P) ?? junction.Center.Z;

        // Asphalt: a fan from the centre (in rings over a game road, so it follows its surface).
        int rings = junction.Beds.IsDefaultOrEmpty ? 1 : 4;
        var center = new Vector3(P, centerZ);
        var levels = new Vector3[rings][];
        for (int l = 0; l < rings; l++)
        {
            float f = (l + 1f) / rings;
            levels[l] = [.. outline.Select(p =>
            {
                var at = Vector2.Lerp(P, p.Point, f);
                return new Vector3(at, l == rings - 1 ? p.Z : (BedHeight(at) ?? float.Lerp(centerZ, p.Z, f)));
            })];
        }
        var asphalt = new List<(Vector3 A, Vector3 B, Vector3 C)>();
        for (int k = 0; k < outline.Count; k++)
        {
            int next = (k + 1) % outline.Count;
            if (Vector2.DistanceSquared(outline[k].Point, outline[next].Point) < 1e-6f) continue;
            asphalt.Add((center, levels[0][k], levels[0][next]));
            for (int l = 0; l + 1 < rings; l++)
            {
                asphalt.Add((levels[l][k], levels[l + 1][k], levels[l + 1][next]));
                asphalt.Add((levels[l][k], levels[l + 1][next], levels[l][next]));
            }
        }
        AddAsphalt(mesh, asphalt, P, along, across);

        // Sidewalks round the corners where both sides have one, and their curbs.
        for (int i = 0; i < n; i++)
        {
            var a = arms[i]; var b = arms[(i + 1) % n]; var path = corners[i];
            if (!a.Profile.SidewalkLeft || !b.Profile.SidewalkRight || path.Count < 2) continue;
            float curb = (a.Profile.Curb + b.Profile.Curb) / 2;
            uint previousInner = 0, previousOuter = 0, previousBottom = 0, previousTop = 0, previousSkirtTop = 0, previousSkirtBottom = 0, previousGutterCurb = 0, previousGutterRoad = 0;
            float run = 0, outerRun = 0;
            for (int k = 0; k < path.Count; k++)
            {
                var (inner, outer, z, outerZ) = path[k];
                if (k > 0) { run += Vector2.Distance(path[k - 1].Curb, inner); outerRun += Vector2.Distance(path[k - 1].Outer, outer); }
                uint vi = mesh.Add(new Vector3(inner, z + curb), Vector3.UnitZ, SidewalkUv(inner));
                uint vo = mesh.Add(new Vector3(outer, outerZ + curb), Vector3.UnitZ, SidewalkUv(outer));
                // The curb face looks at the road (towards the centre side of the path).
                var tangent = k + 1 < path.Count ? path[k + 1].Curb - inner : inner - path[k - 1].Curb;
                var facing = tangent.LengthSquared() < 1e-8f ? Vector3.UnitZ : new Vector3(Vector2.Normalize(new Vector2(tangent.Y, -tangent.X)) * -1, 0);
                uint vb = mesh.Add(new Vector3(inner, z), facing, new(run / CurbScale, CurbFaceBottom));
                uint vt = mesh.Add(new Vector3(inner, z + curb), facing, new(run / CurbScale, CurbFaceTop));
                // The sidewalk's outer side, down into the ground (shaped just under the road's
                // level): it looks away from the road.
                var outerTangent = k + 1 < path.Count ? path[k + 1].Outer - outer : outer - path[k - 1].Outer;
                var away = outerTangent.LengthSquared() < 1e-8f ? -facing : new Vector3(Vector2.Normalize(new Vector2(outerTangent.Y, -outerTangent.X)), 0);
                uint st = mesh.Add(new Vector3(outer, outerZ + curb), away, new(outerRun / CurbScale, CurbFaceTop));
                uint sb = mesh.Add(new Vector3(outer, outerZ - Skirt), away, new(outerRun / CurbScale, OuterSideBottom));
                // The gutter at the curb's foot, towards the road.
                uint gc = mesh.Add(new Vector3(inner, z + GutterLift), Vector3.UnitZ, new(run / CurbScale, GutterCurb));
                uint gr = mesh.Add(new Vector3(inner + new Vector2(facing.X, facing.Y) * GutterWidth, z + GutterLift), Vector3.UnitZ, new(run / CurbScale, GutterRoad));
                if (k > 0)
                {
                    mesh.Sidewalk.AddRange([previousInner, previousOuter, vo]);
                    mesh.Sidewalk.AddRange([previousInner, vo, vi]);
                    mesh.Curb.AddRange([previousBottom, previousTop, vt]);
                    mesh.Curb.AddRange([previousBottom, vt, vb]);
                    // (The road lies left of the path: curb, along it, then towards the road.)
                    mesh.Curb.AddRange([previousGutterCurb, gc, gr]);
                    mesh.Curb.AddRange([previousGutterCurb, gr, previousGutterRoad]);
                    if (Vector2.DistanceSquared(path[k - 1].Outer, outer) > 1e-6f)
                    {
                        mesh.Curb.AddRange([previousSkirtBottom, st, previousSkirtTop]);
                        mesh.Curb.AddRange([previousSkirtBottom, sb, st]);
                    }
                }
                (previousInner, previousOuter, previousBottom, previousTop, previousSkirtTop, previousSkirtBottom, previousGutterCurb, previousGutterRoad) = (vi, vo, vb, vt, st, sb, gc, gr);
            }
            AddSidewalkCollision(mesh, path, curb);
        }
        AddAsphaltCollision(mesh, junction, outline, along, across);
        // Laid on a game road: a little above its surface (which goes on under it).
        if (junction.Arms.Any(a => !a.Trimmed))
        {
            for (int i = 0; i < mesh.Positions.Count; i++) mesh.Positions[i] += new Vector3(0, 0, OverGameRoad);
            for (int i = 0; i < mesh.Collision.Count; i++) mesh.Collision[i] = mesh.Collision[i] with { Position = mesh.Collision[i].Position + new Vector3(0, 0, OverGameRoad) };
        }
        return mesh;
    }

    // The asphalt's triangles, cut along the main road into strips one atlas lane wide: the lane
    // runs along the main road, mirrored from one strip to the next across it (the texture goes
    // on without a seam, never reaching the markings beside the lane).
    private static void AddAsphalt(JunctionMesh mesh, List<(Vector3 A, Vector3 B, Vector3 C)> triangles, Vector2 P, Vector2 along, Vector2 across)
    {
        const float Width = (LaneU1 - LaneU0) * LaneAcross;
        float Across(Vector3 p) => Vector2.Dot(Flat(p) - P, across);
        Vector2 Uv(Vector3 p)
        {
            float t = Across(p) / Width, k = MathF.Floor(t), f = t - k;
            bool mirrored = ((long)k & 1) != 0;
            return new(float.Lerp(LaneU0, LaneU1, mirrored ? 1 - f : f), Vector2.Dot(Flat(p) - P, along) / LaneAlong);
        }
        foreach (var (a, b, c) in triangles)
        {
            float lo = MathF.Min(Across(a), MathF.Min(Across(b), Across(c))), hi = MathF.Max(Across(a), MathF.Max(Across(b), Across(c)));
            for (long k = (long)MathF.Floor(lo / Width); k * Width < hi; k++)
            {
                var piece = Clip(Clip([a, b, c], Across, k * Width, true), Across, (k + 1) * Width, false);
                if (piece.Count < 3) continue;
                var first = mesh.Add(piece[0], Vector3.UnitZ, Uv(piece[0]));
                for (int i = 1; i + 1 < piece.Count; i++)
                {
                    if (MathF.Abs(Cross(Flat(piece[i] - piece[0]), Flat(piece[i + 1] - piece[0]))) < 1e-6f) continue;
                    mesh.Asphalt.AddRange([first, mesh.Add(piece[i], Vector3.UnitZ, Uv(piece[i])), mesh.Add(piece[i + 1], Vector3.UnitZ, Uv(piece[i + 1]))]);
                }
            }
        }
    }

    // The part of a convex polygon on one side of a line across it (`side` of each point against
    // `at`: above or below); points on the cut are interpolated (heights too).
    private static List<Vector3> Clip(List<Vector3> polygon, Func<Vector3, float> side, float at, bool above)
    {
        var result = new List<Vector3>(polygon.Count + 2);
        for (int i = 0; i < polygon.Count; i++)
        {
            Vector3 p = polygon[i], q = polygon[(i + 1) % polygon.Count];
            float dp = (side(p) - at) * (above ? 1 : -1), dq = (side(q) - at) * (above ? 1 : -1);
            if (dp >= 0) result.Add(p);
            if ((dp >= 0) != (dq >= 0)) result.Add(Vector3.Lerp(p, q, dp / (dp - dq)));
        }
        return result;
    }

    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    // Boxes under the asphalt: strips across the main road, each spanning the outline there,
    // tilted with the surface along it.
    private static void AddAsphaltCollision(JunctionMesh mesh, Junction junction, List<(Vector2 Point, float Z)> outline, Vector2 along, Vector2 across)
    {
        var P = Flat(junction.Center);
        const float Strip = 1.5f, Thickness = .5f;
        var local = outline.Select(p => (X: Vector2.Dot(p.Point - P, along), Y: Vector2.Dot(p.Point - P, across), p.Z)).ToArray();
        float minY = local.Min(p => p.Y), maxY = local.Max(p => p.Y);
        for (float y0 = minY; y0 < maxY - .05f; y0 += Strip)
        {
            float y1 = MathF.Min(maxY, y0 + Strip), y = (y0 + y1) / 2;
            // Where the outline crosses the strip's middle line: intervals in pairs.
            var hits = new List<(float X, float Z)>();
            for (int k = 0; k < local.Length; k++)
            {
                var a = local[k]; var b = local[(k + 1) % local.Length];
                if ((a.Y <= y) == (b.Y <= y)) continue;
                float t = (y - a.Y) / (b.Y - a.Y);
                hits.Add((float.Lerp(a.X, b.X, t), float.Lerp(a.Z, b.Z, t)));
            }
            hits.Sort((p, q) => p.X.CompareTo(q.X));
            for (int k = 0; k + 1 < hits.Count; k += 2)
            {
                var (x0, z0) = hits[k]; var (x1, z1) = hits[k + 1];
                if (x1 - x0 < .1f) continue;
                Vector3 a = new(P + along * x0 + across * y, z0), b = new(P + along * x1 + across * y, z1);
                AddBox(mesh, a, b, new Vector3(across, 0), (y1 - y0) / 2 + .05f, Thickness, "asphalt.physmat");
            }
        }
    }

    // Boxes under the sidewalk corners: one per step of the path, at the sidewalk's top.
    private static void AddSidewalkCollision(JunctionMesh mesh, List<CornerPoint> path, float curb)
    {
        for (int k = 0; k + 1 < path.Count; k++)
        {
            var (i0, o0, z0, oz0) = path[k]; var (i1, o1, z1, oz1) = path[k + 1];
            Vector2 mid0 = (i0 + o0) / 2, mid1 = (i1 + o1) / 2;
            float width = MathF.Max(Vector2.Distance(i0, o0), Vector2.Distance(i1, o1));
            if (Vector2.Distance(mid0, mid1) < .05f || width < .1f || (o0 + o1 - i0 - i1).LengthSquared() < 1e-6f) continue;
            var side = Vector2.Normalize(o0 + o1 - i0 - i1);
            AddBox(mesh, new Vector3(mid0, (z0 + oz0) / 2 + curb), new Vector3(mid1, (z1 + oz1) / 2 + curb), new Vector3(side, 0), width / 2, .5f, "concrete.physmat");
        }
    }

    // A box whose top runs from `a` to `b` (its length), `half` either side along `side`.
    private static void AddBox(JunctionMesh mesh, Vector3 a, Vector3 b, Vector3 side, float half, float thickness, string material)
    {
        var x = b - a; float length = x.Length();
        if (length < .05f || half < .05f) return;
        x /= length;
        var y = Vector3.Normalize(side - x * Vector3.Dot(side, x));
        var z = Vector3.Cross(x, y);
        if (z.Z < 0) { y = -y; z = -z; }
        var rotation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1)));
        var center = (a + b) / 2 - z * (thickness / 2);
        mesh.Collision.Add(new CollisionShape(CollisionShapeKind.Box, center, rotation, new(length / 2 + .02f, half, thickness / 2)) { Material = material });
    }

    // --- Earthworks ----------------------------------------------------------------------------------

    // Junction shapes' keys: after the roads' ("r:..."), so they are applied last (TerrainSculptor
    // applies shapes in key order) and the ground lies under the junction wherever roads meet.
    public const string GroundPrefix = "~junction:";
    // The rays covering a junction's footprint are this far apart at its edge.
    private const float RayStep = 1;

    // The earthworks under a junction (`ground`: its roads' settings): the ground just under its
    // surface everywhere, as the roads' own (TerrainShape corridors, close enough to cover it).
    // The asphalt: rays from the centre to its outline, each in one of its fan's flat triangles
    // (at its exact height). Each sidewalk corner: rungs from its curb to its outer side.
    public static IEnumerable<Terrain.TerrainShape> Ground(Junction junction, RoadGround ground)
    {
        var corners = CornerPaths(junction);
        var outline = Outline(junction, corners);
        var P = Flat(junction.Center);
        var center = new Vector3(P, BedHeight(junction, P) ?? junction.Center.Z);
        float half = RayStep / 2 + .05f;
        Terrain.TerrainShape Shape(string key, List<Vector3> line) => new(GroundPrefix + key, [.. line], half, ground.Slope, ground.Margin, ground.ClearPlants);
        var rays = new List<Vector3> { center };
        for (int k = 0; k < outline.Count; k++)
        {
            var (a, za) = outline[k]; var (b, zb) = outline[(k + 1) % outline.Count];
            int steps = Math.Max(1, (int)MathF.Ceiling(Vector2.Distance(a, b) / RayStep));
            for (int s = 0; s < steps; s++)
            {
                float f = (float)s / steps;
                rays.Add(new(Vector2.Lerp(a, b, f), float.Lerp(za, zb, f))); rays.Add(center);
            }
        }
        yield return Shape(junction.Key, rays);
        for (int i = 0; i < corners.Length; i++)
        {
            var path = corners[i];
            if (!Sidewalks(junction.Arms[i].Profile, junction.Arms[(i + 1) % corners.Length].Profile) || path.Count < 2) continue;
            var rungs = new List<Vector3>();
            void Rung(Vector2 curb, float z, Vector2 outer, float outerZ)
            {
                rungs.Add(new(curb, BedHeight(junction, curb) ?? z)); rungs.Add(new(outer, BedHeight(junction, outer) ?? outerZ));
            }
            for (int k = 0; k + 1 < path.Count; k++)
            {
                var (c0, o0, z0, oz0) = path[k]; var (c1, o1, z1, oz1) = path[k + 1];
                int steps = Math.Max(1, (int)MathF.Ceiling(MathF.Max(Vector2.Distance(c0, c1), Vector2.Distance(o0, o1)) / RayStep));
                for (int s = 0; s < steps; s++)
                {
                    float f = (float)s / steps;
                    Rung(Vector2.Lerp(c0, c1, f), float.Lerp(z0, z1, f), Vector2.Lerp(o0, o1, f), float.Lerp(oz0, oz1, f));
                }
            }
            Rung(path[^1].Curb, path[^1].Z, path[^1].Outer, path[^1].OuterZ);
            yield return Shape($"{junction.Key}#{i}", rungs);
        }
    }

    // A junction's earthworks settings: those of the first of its project roads that shapes the
    // ground (null: none does, the junction leaves it alone too).
    public static RoadGround? GroundOf(Junction junction, IReadOnlyDictionary<string, RoadDefinition> roads) =>
        junction.Arms.Where(a => a.Trimmed).Select(a => roads.TryGetValue(a.RoadId, out var road) ? road.Ground : null).OfType<RoadGround>().FirstOrDefault();

    // The earthworks under a plan's junctions (the editor and the export build them the same way).
    public static IEnumerable<Terrain.TerrainShape> Grounds(JunctionPlan plan, IReadOnlyDictionary<string, RoadDefinition> roads) =>
        plan.Junctions.SelectMany(j => GroundOf(j, roads) is { } ground ? Ground(j, ground) : []);
}
