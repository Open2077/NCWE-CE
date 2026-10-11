// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using System.Text.Json.Nodes;

namespace NCWE.Mcp;

// Size of an asset at the identity transform (game space, relative to its pivot).
internal readonly record struct AssetBox(Vector3 Min, Vector3 Max)
{
    public Vector3 Size => Max - Min;
    public Vector3 Center => (Min + Max) / 2;
    public static AssetBox FromJson(JsonNode asset) => new(Vec(asset["min"]!), Vec(asset["max"]!));
    public static Vector3 Vec(JsonNode n) => new(n[0]!.GetValue<float>(), n[1]!.GetValue<float>(), n.AsArray().Count > 2 ? n[2]!.GetValue<float>() : 0);
}

// Game-space conventions: X east, Y north, Z up, metres. Yaw (heading) in degrees about +Z,
// counter-clockwise seen from above; an object with yaw 0 faces +Y.
internal static class Geo
{
    public static float Rad(float degrees) => degrees * MathF.PI / 180;
    public static Vector2 Rotate(Vector2 v, float yaw) { float c = MathF.Cos(Rad(yaw)), s = MathF.Sin(Rad(yaw)); return new(v.X * c - v.Y * s, v.X * s + v.Y * c); }
    public static Vector3 Rotate(Vector3 v, float yaw) { var r = Rotate(new Vector2(v.X, v.Y), yaw); return new(r.X, r.Y, v.Z); }
    // Heading of a direction: 0 along +Y, 90 along -X.
    public static float Heading(Vector2 d) => MathF.Atan2(-d.X, d.Y) * 180 / MathF.PI;
    public static JsonArray Json(Vector3 v) => [MathF.Round(v.X, 3), MathF.Round(v.Y, 3), MathF.Round(v.Z, 3)];
    public static float Wrap(float yaw) { yaw %= 360; return yaw > 180 ? yaw - 360 : yaw <= -180 ? yaw + 360 : yaw; }
}

internal sealed record PatternItem(string Asset, Vector3 Position, float Yaw, Vector3 Scale)
{
    public JsonObject ToJson(string ground, string collision)
    {
        var item = new JsonObject { ["asset"] = Asset, ["position"] = Geo.Json(Position), ["rotation"] = new JsonObject { ["yaw"] = MathF.Round(Geo.Wrap(Yaw), 3) } };
        if (Scale != Vector3.One) item["scale"] = Geo.Json(Scale);
        if (ground != "none") item["ground"] = ground;
        if (collision != "none") item["collision"] = collision;
        return item;
    }
}

internal static class Patterns
{
    // Extent of an asset along a horizontal direction once turned by `yaw` (its footprint).
    public static float Footprint(AssetBox box, float yaw, Vector2 along)
    {
        var a = Vector2.Normalize(along);
        var x = Geo.Rotate(new Vector2(box.Size.X, 0), yaw); var y = Geo.Rotate(new Vector2(0, box.Size.Y), yaw);
        return MathF.Abs(Vector2.Dot(x, a)) + MathF.Abs(Vector2.Dot(y, a));
    }

    // Pivot that puts the asset's box center at `center` (XY) for this yaw and scale.
    public static Vector3 PivotFor(AssetBox box, Vector3 center, float yaw, Vector3 scale)
    {
        var offset = Geo.Rotate(new Vector3(box.Center.X * scale.X, box.Center.Y * scale.Y, 0), yaw);
        return new(center.X - offset.X, center.Y - offset.Y, center.Z);
    }

    public static List<PatternItem> Grid(Func<int, string> asset, Func<string, AssetBox?> boxes, Vector3 origin, int rows, int columns, Vector2? spacing, float gap, float heading, float itemYaw, bool centered)
    {
        var first = boxes(asset(0));
        float sx = spacing?.X ?? (first is { } b ? Footprint(b, itemYaw, Vector2.UnitX) + gap : 2);
        float sy = spacing?.Y ?? (first is { } c ? Footprint(c, itemYaw, Vector2.UnitY) + gap : 2);
        var items = new List<PatternItem>();
        var start = centered ? new Vector2(-(columns - 1) * sx / 2, -(rows - 1) * sy / 2) : Vector2.Zero;
        int n = 0;
        for (int r = 0; r < rows; r++)
            for (int col = 0; col < columns; col++)
            {
                var local = start + new Vector2(col * sx, r * sy);
                var at = Geo.Rotate(local, heading);
                items.Add(new(asset(n++), new(origin.X + at.X, origin.Y + at.Y, origin.Z), heading + itemYaw, Vector3.One));
            }
        return items;
    }

    // Along a polyline: evenly spaced (or `count` items spread over it), turned to follow it.
    public static List<PatternItem> Path(Func<int, string> asset, Func<string, AssetBox?> boxes, IReadOnlyList<Vector3> points, float? spacing, int? count, float gap, float itemYaw, bool follow, bool closed)
    {
        var pts = closed ? points.Append(points[0]).ToList() : points.ToList();
        if (pts.Count < 2) throw new InvalidDataException("A path needs at least two points.");
        float length = 0; for (int i = 1; i < pts.Count; i++) length += Vector2.Distance(Xy(pts[i - 1]), Xy(pts[i]));
        var direction = Xy(pts[1]) - Xy(pts[0]);
        float step = spacing ?? (count is int c && c > 1 ? length / (closed ? c : c - 1)
            : boxes(asset(0)) is { } box ? Footprint(box, itemYaw, new Vector2(0, 1)) + gap : 2);
        if (step <= .01f) throw new InvalidDataException("Spacing must be positive.");
        int total = count ?? (int)MathF.Floor(length / step + (closed ? 0 : 1) + 1e-3f);
        total = Math.Clamp(total, 1, 2000);
        var items = new List<PatternItem>();
        for (int k = 0; k < total; k++)
        {
            float distance = Math.Min(k * step, length);
            var (point, tangent) = AlongPath(pts, distance);
            float yaw = (follow ? Geo.Heading(tangent) : 0) + itemYaw;
            items.Add(new(asset(k), point, yaw, Vector3.One));
        }
        return items;
    }

    public static List<PatternItem> Circle(Func<int, string> asset, Vector3 center, float radius, int count, float start, float end, string face, float itemYaw)
    {
        var items = new List<PatternItem>();
        bool full = MathF.Abs(end - start) >= 359.9f;
        for (int k = 0; k < count; k++)
        {
            float t = count == 1 ? 0 : k / (float)(full ? count : count - 1);
            float angle = start + (end - start) * t;   // 0 = +Y from the center, counter-clockwise
            var offset = Geo.Rotate(new Vector2(0, radius), angle);
            float yaw = face switch { "center" => angle + 180, "outward" => angle, "tangent" => angle + 90, _ => 0 } + itemYaw;
            items.Add(new(asset(k), center + new Vector3(offset, 0), yaw, Vector3.One));
        }
        return items;
    }

    // Random points in a disc or box, at least `minDistance` apart (rejection sampling).
    public static List<PatternItem> Scatter(Func<int, string> asset, Random random, Vector3? center, float radius, Vector3? min, Vector3? max, int count, float minDistance, bool randomYaw, float itemYaw, float scaleMin, float scaleMax)
    {
        var items = new List<PatternItem>(); var taken = new List<Vector2>();
        float z = center?.Z ?? min?.Z ?? 0;
        for (int attempt = 0; items.Count < count && attempt < count * 60; attempt++)
        {
            Vector2 p;
            if (min is { } lo && max is { } hi) p = new(lo.X + random.NextSingle() * (hi.X - lo.X), lo.Y + random.NextSingle() * (hi.Y - lo.Y));
            else
            {
                var c = center ?? Vector3.Zero;
                float r = radius * MathF.Sqrt(random.NextSingle()), a = random.NextSingle() * MathF.Tau;
                p = new(c.X + r * MathF.Cos(a), c.Y + r * MathF.Sin(a));
            }
            if (taken.Any(q => Vector2.DistanceSquared(p, q) < minDistance * minDistance)) continue;
            taken.Add(p);
            float scale = scaleMin + random.NextSingle() * Math.Max(0, scaleMax - scaleMin);
            items.Add(new(asset(items.Count), new(p, z), (randomYaw ? random.NextSingle() * 360 : 0) + itemYaw, new(scale)));
        }
        return items;
    }

    private static Vector2 Xy(Vector3 v) => new(v.X, v.Y);

    private static (Vector3 Point, Vector2 Tangent) AlongPath(List<Vector3> pts, float distance)
    {
        for (int i = 1; i < pts.Count; i++)
        {
            float segment = Vector2.Distance(Xy(pts[i - 1]), Xy(pts[i]));
            if (distance <= segment || i == pts.Count - 1)
            {
                float t = segment < 1e-5f ? 0 : Math.Clamp(distance / segment, 0, 1);
                return (Vector3.Lerp(pts[i - 1], pts[i], t), Xy(pts[i]) - Xy(pts[i - 1]));
            }
            distance -= segment;
        }
        return (pts[^1], Xy(pts[^1]) - Xy(pts[^2]));
    }
}

// A rectangular room from modules: floor tiles, wall modules (stacked to the height, a gap at
// each door), optional ceiling, lights and collision boxes, all fitted to their measured boxes.
internal sealed class RoomBuilder
{
    public required Vector3 Center { get; init; }       // floor level, middle of the room
    public required float Width { get; init; }          // along the room's local X
    public required float Depth { get; init; }          // along local Y
    public float Height { get; init; } = 3;
    public float Heading { get; init; }                 // room rotation (local +Y = "north" wall side)
    public string? FloorAsset { get; init; }
    public string? WallAsset { get; init; }
    public string? CeilingAsset { get; init; }
    public bool FlipWalls { get; init; }
    public bool Stretch { get; init; } = true;
    public List<Door> Doors { get; init; } = [];
    public bool Collision { get; init; } = true;
    public int Lights { get; init; } = -1;              // -1: one per ~16 m²
    public float LightTemperature { get; init; } = 3600;
    public float LightIntensity { get; init; } = 300;

    public sealed record Door(string Wall, float Offset, float Width, float Height, string? Asset, string? Appearance);

    public (JsonArray Items, JsonArray Elements, JsonArray Collisions, List<string> Notes) Build(Func<string, AssetBox?> boxes)
    {
        var items = new JsonArray(); var elements = new JsonArray(); var collisions = new JsonArray(); var notes = new List<string>();
        Vector3 World(Vector2 local, float z) { var w = Geo.Rotate(local, Heading); return new(Center.X + w.X, Center.Y + w.Y, z); }
        float floorZ = Center.Z;

        // Floor and ceiling: tiles over the whole rectangle (stretched to fit exactly).
        void Tiles(string asset, bool ceiling)
        {
            if (boxes(asset) is not { } box || box.Size.X < .05f || box.Size.Y < .05f) { notes.Add($"{asset}: size unknown, skipped."); return; }
            int nx = Math.Max(1, (int)MathF.Round(Width / box.Size.X)), ny = Math.Max(1, (int)MathF.Round(Depth / box.Size.Y));
            var scale = Stretch ? new Vector3(Width / (nx * box.Size.X), Depth / (ny * box.Size.Y), 1) : Vector3.One;
            if (nx * ny > 900) { notes.Add($"{asset}: {nx * ny} tiles is too many; use a bigger floor module."); return; }
            for (int i = 0; i < nx; i++)
                for (int j = 0; j < ny; j++)
                {
                    var local = new Vector2(-Width / 2 + (i + .5f) * Width / nx, -Depth / 2 + (j + .5f) * Depth / ny);
                    // Floor: top of the tile at floor level; ceiling: bottom of the tile at the height.
                    float z = ceiling ? floorZ + Height - box.Min.Z * scale.Z : floorZ - box.Max.Z * scale.Z;
                    var pivot = Patterns.PivotFor(box, World(local, z), Heading, scale);
                    items.Add(new PatternItem(asset, pivot, Heading, scale).ToJson("none", "none"));
                }
        }
        if (FloorAsset is { } floor) Tiles(floor, false);
        if (CeilingAsset is { } ceilingAsset) Tiles(ceilingAsset, true);

        // Walls: the module's long horizontal axis runs along each side.
        var sides = new (string Name, Vector2 Start, Vector2 End, Vector2 Inward)[]
        {
            ("south", new(-Width / 2, -Depth / 2), new(Width / 2, -Depth / 2), Vector2.UnitY),
            ("east", new(Width / 2, -Depth / 2), new(Width / 2, Depth / 2), -Vector2.UnitX),
            ("north", new(Width / 2, Depth / 2), new(-Width / 2, Depth / 2), -Vector2.UnitY),
            ("west", new(-Width / 2, Depth / 2), new(-Width / 2, -Depth / 2), Vector2.UnitX),
        };
        foreach (var door in Doors)
            if (!sides.Any(s => s.Name == door.Wall)) throw new InvalidDataException($"Door wall must be north, south, east or west (got {door.Wall}).");
        AssetBox? wallBox = WallAsset is { } wa ? boxes(wa) : null;
        if (WallAsset is not null && wallBox is null) notes.Add($"{WallAsset}: size unknown, walls skipped.");
        foreach (var side in sides)
        {
            float length = Vector2.Distance(side.Start, side.End);
            var along = Vector2.Normalize(side.End - side.Start);
            var openings = Doors.Where(d => d.Wall == side.Name).Select(d => (From: length / 2 + d.Offset - d.Width / 2, To: length / 2 + d.Offset + d.Width / 2, d)).ToList();
            float sideHeading = Geo.Heading(along);   // +Y of a turned module runs along the side
            if (wallBox is { } box && WallAsset is { } wall)
            {
                bool runsAlongX = box.Size.X >= box.Size.Y;
                float runLength = runsAlongX ? box.Size.X : box.Size.Y;
                // A module whose long axis is X is turned so X follows the side; one whose long axis is Y, so Y does.
                float yaw = sideHeading + (runsAlongX ? -90 : 0) + (FlipWalls ? 180 : 0);
                int n = Math.Max(1, (int)MathF.Round(length / runLength));
                int rows = Math.Max(1, (int)MathF.Round(Height / Math.Max(.1f, box.Size.Z)));
                float runScale = Stretch ? length / (n * runLength) : 1, zScale = Stretch ? Height / (rows * box.Size.Z) : 1;
                var scale = runsAlongX ? new Vector3(runScale, 1, zScale) : new Vector3(1, runScale, zScale);
                // One module per slot and row; around a door the slot is cut into scaled pieces
                // (both sides of the opening and a lintel above it) so the opening is exactly the door's.
                void Piece(float a, float b, float bottom, float top)
                {
                    if (b - a < .05f || top - bottom < .05f) return;
                    float run = (b - a) / runLength, rise = (top - bottom) / box.Size.Z;
                    var pieceScale = runsAlongX ? new Vector3(run, 1, rise) : new Vector3(1, run, rise);
                    var local = side.Start + along * ((a + b) / 2);
                    float z = floorZ + bottom - box.Min.Z * pieceScale.Z;
                    var pivot = Patterns.PivotFor(box, World(local, z), yaw + Heading, pieceScale);
                    items.Add(new PatternItem(wall, pivot, yaw + Heading, pieceScale).ToJson("none", "none"));
                }
                for (int k = 0; k < n; k++)
                {
                    float from = k * length / n, to = (k + 1) * length / n;
                    for (int row = 0; row < rows; row++)
                    {
                        float bottom = row * Height / rows, top = (row + 1) * Height / rows;
                        var cuts = openings.Where(o => o.From < to - .05f && o.To > from + .05f && bottom < o.d.Height - .05f).OrderBy(o => o.From).ToList();
                        if (cuts.Count == 0)
                        {
                            if (Stretch) Piece(from, to, bottom, top);
                            else
                            {
                                var local = side.Start + along * ((from + to) / 2);
                                var pivot = Patterns.PivotFor(box, World(local, floorZ + bottom - box.Min.Z), yaw + Heading, Vector3.One);
                                items.Add(new PatternItem(wall, pivot, yaw + Heading, Vector3.One).ToJson("none", "none"));
                            }
                            continue;
                        }
                        float cursor = from;
                        foreach (var (o1, o2, door) in cuts)
                        {
                            Piece(cursor, Math.Clamp(o1, from, to), bottom, top);
                            Piece(Math.Clamp(o1, from, to), Math.Clamp(o2, from, to), Math.Max(bottom, door.Height), top);
                            cursor = Math.Max(cursor, Math.Clamp(o2, from, to));
                        }
                        Piece(cursor, to, bottom, top);
                    }
                }
            }
            foreach (var (from, to, door) in openings)
            {
                if (door.Asset is not { } doorAsset) continue;
                var local = side.Start + along * ((from + to) / 2);
                // Centred in the opening by its measured box, its long side along the wall.
                float doorYaw = sideHeading + Heading + 90;
                var position = World(local, floorZ);
                if (boxes(doorAsset) is { } doorBox)
                {
                    if (doorBox.Size.Y > doorBox.Size.X) doorYaw -= 90;
                    position = Patterns.PivotFor(doorBox, position, doorYaw, Vector3.One);
                }
                var item = new PatternItem(doorAsset, position, doorYaw, Vector3.One).ToJson("none", "none");
                if (door.Appearance is { } look) item["appearance"] = look;
                items.Add(item);
            }
            if (Collision)
            {
                // Wall collision, split around door openings (a lintel above each).
                var cuts = openings.OrderBy(o => o.From).ToList();
                float cursor = 0;
                void Segment(float a, float b, float bottom, float top)
                {
                    if (b - a < .05f || top - bottom < .05f) return;
                    var local = side.Start + along * ((a + b) / 2);
                    collisions.Add(Box($"{side.Name} wall collision", World(local, floorZ + (bottom + top) / 2), sideHeading + Heading, new Vector3(.2f, b - a, top - bottom)));
                }
                foreach (var (from, to, door) in cuts)
                {
                    Segment(cursor, Math.Max(cursor, from), 0, Height);
                    Segment(Math.Max(0, from), Math.Min(length, to), Math.Min(Height, door.Height), Height);
                    cursor = Math.Max(cursor, to);
                }
                Segment(cursor, length, 0, Height);
            }
        }
        if (Collision)
        {
            collisions.Add(Box("floor collision", World(Vector2.Zero, floorZ - .1f), Heading, new Vector3(Width, Depth, .2f)));
            if (CeilingAsset is not null) collisions.Add(Box("ceiling collision", World(Vector2.Zero, floorZ + Height + .1f), Heading, new Vector3(Width, Depth, .2f)));
        }
        // Lights: a grid of warm ceiling lights.
        int lights = Lights >= 0 ? Lights : Math.Max(1, (int)MathF.Round(Width * Depth / 16));
        if (lights > 0)
        {
            int columns = Math.Max(1, (int)MathF.Round(MathF.Sqrt(lights * Width / Math.Max(.1f, Depth)))), lightRows = Math.Max(1, (int)MathF.Ceiling(lights / (float)columns));
            for (int i = 0; i < columns; i++)
                for (int j = 0; j < lightRows && elements.Count < lights; j++)
                {
                    var local = new Vector2(-Width / 2 + (i + .5f) * Width / columns, -Depth / 2 + (j + .5f) * Depth / lightRows);
                    elements.Add(new JsonObject
                    {
                        ["kind"] = "light", ["light_type"] = "point", ["name"] = "Room light", ["position"] = Geo.Json(World(local, floorZ + Height - .35f)),
                        ["temperature"] = LightTemperature, ["intensity"] = LightIntensity, ["radius"] = MathF.Round(Math.Max(4, 1.6f * Math.Max(Width / columns, Depth / lightRows)), 2)
                    });
                }
        }
        return (items, elements, collisions, notes);
    }

    private static JsonObject Box(string name, Vector3 center, float yaw, Vector3 size) => new()
    {
        ["name"] = name, ["position"] = Geo.Json(center), ["rotation"] = new JsonObject { ["yaw"] = MathF.Round(Geo.Wrap(yaw), 3) },
        ["shapes"] = new JsonArray(new JsonObject { ["kind"] = "box", ["size"] = Geo.Json(size) })
    };
}
