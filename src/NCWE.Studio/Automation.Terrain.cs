// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using System.Text.Json;
using NCWE.Core;
using NCWE.Core.Terrain;
using NCWE.Studio.Session;

namespace NCWE.Studio;

// api.terrain.*: sculpting and painting the game's terrain for agents and tests (game space: X east,
// Y north, Z up). A stroke is a list of stamps of one brush and one undo step.
internal sealed partial class Automation
{
    private static Vector2[] Points2(JsonElement c, string name)
    {
        if (!c.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Array) return [];
        return p.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Array && e.GetArrayLength() >= 2
            ? new Vector2(e[0].GetSingle(), e[1].GetSingle()) : throw new ArgumentException($"{name}: [x, y] pairs in game space")).ToArray();
    }

    private static TerrainBrush BrushFrom(JsonElement c, TerrainBrush current)
    {
        var brush = current;
        if (Str(c, "brush") is { } kind)
            brush = brush with
            {
                Kind = kind.ToLowerInvariant() switch
                {
                    "dig" => TerrainBrushKind.Lower, "mountains" => TerrainBrushKind.Mountain, "cut" => TerrainBrushKind.Hole,
                    "terraces" => TerrainBrushKind.Terrace, "erosion" => TerrainBrushKind.Erode, "clear_plants" or "plants" => TerrainBrushKind.ClearPlants,
                    _ => Enum.Parse<TerrainBrushKind>(kind, true)
                },
                Erase = false
            };
        if (OptFloat(c, "radius") is { } radius) brush = brush with { Radius = radius };
        if (OptFloat(c, "strength") is { } strength) brush = brush with { Strength = strength };
        if (OptFloat(c, "hardness") is { } hardness) brush = brush with { Hardness = hardness };
        if (c.TryGetProperty("layer", out var layer)) brush = brush with { Layer = layer.GetInt32() };
        if (c.TryGetProperty("level", out var level)) brush = brush with { TargetHeight = level.ValueKind == JsonValueKind.Null ? null : level.GetSingle() };
        if (OptFloat(c, "feature_size") is { } size) brush = brush with { NoiseScale = size };
        if (c.TryGetProperty("seed", out var seed)) brush = brush with { Seed = seed.GetInt32() };
        if (c.TryGetProperty("erase", out var erase)) brush = brush with { Erase = erase.GetBoolean() };
        // Paint by slope (degrees): > 0 only steeper ground, < 0 only flatter ground, 0 everywhere.
        if (OptFloat(c, "slope") is { } slope) brush = brush with { PaintSlope = slope };
        if (OptFloat(c, "step") is { } step) brush = brush with { NoiseScale = step };
        return brush.Validated();
    }

    private static object BrushJson(TerrainBrush b) => new
    {
        brush = b.Kind.ToString(), radius = b.Radius, strength = b.Strength, hardness = b.Hardness, layer = b.Layer, level = b.TargetHeight, feature_size = b.NoiseScale, seed = b.Seed, erase = b.Erase,
        slope = b.PaintSlope
    };

    private async Task<object> TerrainOp(StudioSession s, Viewport.ViewportController v, string op, JsonElement c)
    {
        if (s.Terrain is not { } sculptor) throw new InvalidOperationException("Open the game first.");
        switch (op)
        {
            case "api.terrain.drag":
            {
                // The mouse path of the Terrain tool (tests): the button held at viewport pixel
                // `from`, moved to `to` over `seconds`, released.
                static Vector2? Pixel(JsonElement c, string name) => c.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Array && p.GetArrayLength() >= 2 ? new Vector2(p[0].GetSingle(), p[1].GetSingle()) : null;
                var from = Pixel(c, "from") ?? throw new ArgumentException("from: [x, y] viewport pixel");
                var to = Pixel(c, "to") ?? from;
                float seconds = OptFloat(c, "seconds") ?? 1;
                bool alternate = c.TryGetProperty("alt", out var alt) && alt.GetBoolean();
                // Optional window captures while the button is held: [[seconds, path], ...].
                var shots = c.TryGetProperty("shots", out var shotList) && shotList.ValueKind == JsonValueKind.Array
                    ? shotList.EnumerateArray().Select(e => (At: e[0].GetDouble(), Path: e[1].GetString()!)).OrderBy(e => e.At).ToList() : [];
                _window.SetTool(Viewport.EditorTool.Terrain);
                v.SimulatePointer(from);
                for (int i = 0; i < 300 && s.TerrainCursor is null; i++) { await Task.Delay(30); v.SimulatePointer(from); }
                if (!v.SimulateTerrainButton(true, alternate)) throw new InvalidOperationException("No terrain under that pixel.");
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var surfaces = new List<object[]>();
                while (watch.Elapsed.TotalSeconds < seconds)
                {
                    v.SimulatePointer(Vector2.Lerp(from, to, (float)Math.Min(1, watch.Elapsed.TotalSeconds / Math.Max(.01, seconds))));
                    await Task.Delay(16);
                    if (shots.Count != 0 && watch.Elapsed.TotalSeconds >= shots[0].At)
                    {
                        Capture(_window, shots[0].Path); shots.RemoveAt(0);
                        surfaces.Add(sculptor.EditedCells.Select(cell => (object)new { cell = cell.ToString(), surface = v.Renderer.SurfaceDiagnostic(StudioSession.TerrainMeshHash(cell), null) }).ToArray());
                    }
                }
                v.SimulatePointer(to);
                await Task.Delay(32);
                v.SimulateTerrainButton(false);
                return new { ok = true, undo = s.Document.UndoName, edited = s.TerrainSummary().Cells, cursor = s.TerrainCursor is { } at ? new[] { R(at.X), R(at.Y), R(at.Z) } : null, surfaces };
            }
            case "api.terrain.status":
            {
                var (cells, sculpted, painted, cut) = s.TerrainSummary();
                return new { ok = true, edited = cells, sculpted, painted, cut, loaded = sculptor.LoadedCells.Count(), pending = sculptor.Pending, stroking = s.TerrainStroking,
                    cursor = s.TerrainCursor is { } at ? new[] { R(at.X), R(at.Y), R(at.Z) } : null, interactive = BrushJson(s.TerrainBrush),
                    cells = sculptor.EditedCells.Select(cell => cell.ToString()).Order().ToArray() };
            }
            case "api.terrain.surface":
            {
                // Diagnostics: how the renderer draws a cell in Material shading.
                var cell = TerrainCell.Parse(Str(c, "cell") ?? throw new ArgumentException("cell: \"x_y\""));
                ulong hash = StudioSession.TerrainMeshHash(cell);
                return new { ok = true, mesh = hash.ToString("x16"), edited = s.TerrainSurfaces.ContainsKey(hash), surface = v.Renderer.SurfaceDiagnostic(hash, null) };
            }
            case "api.terrain.layers":
                return new { ok = true, layers = s.TerrainLayerNames.Select((name, i) => new { layer = i, name }).ToArray() };
            case "api.terrain.brush":
                s.TerrainBrush = BrushFrom(c, s.TerrainBrush);
                if (c.TryGetProperty("objects_follow", out var follows)) s.TerrainObjectsFollow = follows.GetBoolean();
                return new { ok = true, interactive = BrushJson(s.TerrainBrush), objects_follow = s.TerrainObjectsFollow };
            case "api.terrain.height":
            {
                var points = Points2(c, "points");
                var cells = points.SelectMany(p => TerrainCell.Covering(p, 2)).Distinct().ToArray();
                await Task.Run(() => sculptor.Load(cells));
                return new { ok = true, heights = points.Select(p => sculptor.HeightAt(p) is { } h ? (float?)R(h) : null).ToArray(), holes = points.Select(sculptor.IsHole).ToArray() };
            }
            case "api.terrain.stroke":
            {
                var points = Points2(c, "points");
                if (points.Length == 0 && OptV3(c, "center") is { } center) points = [new(center.X, center.Y)];
                if (points.Length == 0) throw new ArgumentException("points: [[x, y], ...] (or center) in game space");
                if (points.Length > 4096) throw new ArgumentException("A stroke has 4,096 stamps at most.");
                var brush = BrushFrom(c, new TerrainBrush(TerrainBrushKind.Raise));
                float amount = OptFloat(c, "amount") ?? 1;
                // Bases are read on the thread pool; the stroke itself runs here (the UI thread).
                var cells = points.SelectMany(p => TerrainCell.Covering(p, brush.Radius + 8)).Distinct().ToArray();
                await Task.Run(() => sculptor.Load(cells));
                bool follow = s.TerrainObjectsFollow;
                if (c.TryGetProperty("objects_follow", out var objectsFollow)) s.TerrainObjectsFollow = objectsFollow.GetBoolean();
                int edited;
                try { edited = s.TerrainStroke(brush, points, amount); }
                finally { s.TerrainObjectsFollow = follow; }
                return new { ok = true, edited_cells = edited, brush = BrushJson(brush), stamps = points.Length, undo = s.Document.UndoName, objects_moved = s.LastTerrainFollowers,
                    heights = points.Take(16).Select(p => sculptor.HeightAt(p) is { } h ? (float?)R(h) : null).ToArray() };
            }
            case "api.terrain.clear":
                s.ClearTerrain();
                return new { ok = true, edited = s.TerrainSummary().Cells };
            default:
                throw new ArgumentException("Unknown terrain operation: " + op);
        }
    }
}
