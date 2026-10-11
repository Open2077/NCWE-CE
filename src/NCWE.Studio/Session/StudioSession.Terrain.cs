// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using System.Numerics;
using NCWE.Core;
using NCWE.Core.Terrain;
using NCWE.Formats.Terrain;

namespace NCWE.Studio.Session;

// The Terrain tool: brushes over the game's terrain cells (TerrainSculptor), drawn live.
// - An edited cell's mesh is the editor's own (TerrainMesher.Preview), put in place of the game's
//   under the same mesh hash, so the cell's placement draws it; while sculpting, its buffers are
//   updated in place (D3DRenderer.UpdateWorldMeshInPlace).
// - In Material shading, an edited cell wears its game material with the project's paint mask
//   (TerrainMaterialSource answers for it), reloaded as it changes, lit by its mesh's normals.
// - A stroke is one undo step (EditorDocument.UpdateTerrain); the brush applies every frame the
//   button is held, so holding still keeps raising, digging or painting.
internal sealed partial class StudioSession
{
    public TerrainSculptor? Terrain { get; private set; }
    public TerrainBrush TerrainBrush { get; set; } = new(TerrainBrushKind.Raise, 8, 3, .35f);
    public bool TerrainMode { get; set; }
    // The ground under the cursor (game space), null when the cursor is off the terrain.
    public Vector3? TerrainCursor { get; private set; }
    public bool TerrainStroking => _terrainStroke;
    // Bumped when edits or bases change (the panel refreshes on it).
    public int TerrainVersion => (Terrain?.Version ?? 0) + _terrainBasesArrived;

    private ArchiveTerrainSource? _terrainSource;
    private TerrainMaterialSource? _terrainMaterials;
    private bool _terrainStroke;
    private Vector2 _terrainLast;
    private object? _terrainCommitted;
    private volatile int _terrainBasesArrived;
    private double _nextTerrainPrefetch;
    // Cells whose ground changed since their plants were last placed, and the sectors to rebuild
    // for them (BuildSector moves plants with the ground; a few times a second while sculpting).
    private readonly HashSet<TerrainCell> _terrainFollowCells = [];
    private readonly HashSet<ulong> _terrainDirtySectors = [];
    private double _nextTerrainFollow;

    private sealed class TerrainPreview
    {
        public int Generation = -1;
        public MeshAsset? Asset;
        public double BuiltAt = double.NegativeInfinity, TexturesAt = double.NegativeInfinity;
        public int TexturesGeneration = -1;
        public Bounds3 Bounds;
    }
    private readonly Dictionary<TerrainCell, TerrainPreview> _terrainPreviews = [];

    // What the material source reads for an edited cell (any thread): the parts' positions (their UVs
    // come from them) and the synthetic hash of its mask.
    internal sealed record TerrainSurfaceState(TerrainCell Cell, ulong Mask, int[] Counts, Vector2[][] Positions);
    internal readonly ConcurrentDictionary<ulong, TerrainSurfaceState> TerrainSurfaces = new();
    internal readonly ConcurrentDictionary<ulong, MaskData> TerrainMasks = new();

    public static ulong TerrainMeshHash(TerrainCell cell) => WolvenKit.Common.FNV1A.FNV1A64HashAlgorithm.HashString(cell.MeshPath);
    private static ulong TerrainMaskHash(TerrainCell cell) => WolvenKit.Common.FNV1A.FNV1A64HashAlgorithm.HashString("ncwe-terrain-mask:" + cell);

    // The material source the renderer reads (the archives', with the edited cells answered here).
    private IMaterialSource TerrainMaterials(IMaterialSource inner)
    {
        var index = Index!;
        return _terrainMaterials = new TerrainMaterialSource(inner, this,
            mesh => TerrainCell.TryParseMeshPath(index.Find(mesh)?.Path, out var cell) ? cell : null,
            cell => { try { return index.ReadTerrainCellTextures(cell); } catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException) { return (null, null, null); } });
    }

    // A new game index (installation opened): bases are read from it.
    private void ResetTerrain()
    {
        foreach (var cell in _terrainPreviews.Keys.ToArray()) RemoveTerrainPreview(cell);
        _terrainPreviews.Clear(); TerrainSurfaces.Clear(); TerrainMasks.Clear();
        _terrainStroke = false; TerrainCursor = null; _terrainCommitted = null;
        if (Index is null) { Terrain = null; _terrainSource = null; return; }
        _terrainSource = new ArchiveTerrainSource(Index);
        Terrain = new TerrainSculptor(_terrainSource);
        Terrain.BaseLoaded += _ => Interlocked.Increment(ref _terrainBasesArrived);
    }

    // The multilayer setup's layers (paint palette), once a base is read.
    public IReadOnlyList<string> TerrainLayerNames => _terrainSource?.LayerNames() ?? [];

    // --- Per frame ------------------------------------------------------------------------------

    private void UpdateTerrain(double now)
    {
        if (Index is not null && Terrain is null) ResetTerrain();
        _renderer.PumpReloadedTextures();
        if (Terrain is not { } sculptor) return;
        // Roads that shape the ground (RoadGround) lay their earthworks over the edits.
        UpdateRoadShapes(sculptor, now);
        var terrain = Document.Project.Changes.Terrain ?? System.Collections.Immutable.ImmutableDictionary<string, TerrainCellData>.Empty;
        if (!ReferenceEquals(terrain, _terrainCommitted) && !_terrainStroke)
        {
            sculptor.SetCommitted(terrain);
            _terrainCommitted = terrain;
        }
        if (now >= _nextTerrainPrefetch)
        {
            _nextTerrainPrefetch = now + .5;
            sculptor.Ensure(sculptor.EditedCells);
            sculptor.Ensure(sculptor.ShapedCells);
            if (TerrainMode)
            {
                var eye = Coordinates.ViewToGame(Camera.Eye);
                sculptor.Ensure(TerrainCell.Covering(new(eye.X, eye.Y), 320).OrderBy(c => Vector2.DistanceSquared(c.Centre, new(eye.X, eye.Y))));
            }
        }
        if (TerrainCursor is { } cursor) sculptor.Ensure(TerrainCell.Covering(new(cursor.X, cursor.Y), TerrainBrush.Radius + 8));
        UpdateTerrainPreviews(now);
        if (_terrainFollowCells.Count != 0 && now >= _nextTerrainFollow)
        {
            foreach (var (hash, entry) in _sectorRender)
                if (entry.FoliageCells is { } cells && cells.Overlaps(_terrainFollowCells)) _terrainDirtySectors.Add(hash);
            _terrainFollowCells.Clear();
            _nextTerrainFollow = now + (_terrainStroke ? .4 : 0);
            if (_terrainDirtySectors.Count != 0) _sceneDirty = true;
        }
    }

    private void UpdateTerrainPreviews(double now)
    {
        var sculptor = Terrain!;
        bool changed = false;
        foreach (var cell in sculptor.EditedCells.ToArray())
        {
            if (sculptor.Base(cell) is not { } b) continue;
            if (!_terrainPreviews.TryGetValue(cell, out var preview)) _terrainPreviews[cell] = preview = new();
            int generation = sculptor.Generation(cell);
            if (preview.Generation == generation) continue;
            // While a stroke runs, a cell is rebuilt about 12 times a second.
            if (_terrainStroke && now - preview.BuiltAt < .08) continue;
            var edit = sculptor.Effective(cell);
            ulong hash = TerrainMeshHash(cell);
            var asset = TerrainMesher.Preview(b, edit, cell.MeshPath + "#ncwe", generation, keepVertices: true);
            if (preview.Asset is null)
            {
                // Room above and below for what the brushes will do: the bounds stay put while sculpting.
                preview.Bounds = new(asset.Bounds.Min - new Vector3(0, 300, 0), asset.Bounds.Max + new Vector3(0, 600, 0));
                TerrainSurfaces[hash] = new(cell, TerrainMaskHash(cell), asset.Parts.Select(p => p.Vertices.Length).ToArray(),
                    asset.Parts.Select(p => p.Vertices.Select(v => { var g = Coordinates.ViewToGame(v.Position); return new Vector2(g.X, g.Y); }).ToArray()).ToArray());
            }
            asset = asset with { Bounds = preview.Bounds };
            _terrainFollowCells.Add(cell);
            bool inPlace = preview.Asset is not null && _assets.TryGetValue(hash, out var shown) && ReferenceEquals(shown, preview.Asset) && _renderer.UpdateWorldMeshInPlace(hash, asset);
            preview.Asset = asset; preview.Generation = generation; preview.BuiltAt = now;
            _assets[hash] = asset;
            if (!inPlace) changed = true;
            _gridDirty = true;
        }
        // Cells back to the game's terrain: the streamed mesh again.
        foreach (var cell in _terrainPreviews.Keys.Where(c => sculptor.Effective(c) is null).ToArray()) { RemoveTerrainPreview(cell); changed = true; }
        if (changed) { SetWorldAssets(); _sceneDirty = true; }
        // Paint for Material shading, a few times a second while it changes.
        foreach (var (cell, preview) in _terrainPreviews)
        {
            if (preview.TexturesGeneration == preview.Generation || (_terrainStroke && now - preview.TexturesAt < .25)) continue;
            if (sculptor.Base(cell) is not { } b) continue;
            UpdateTerrainTextures(cell, b, sculptor.Effective(cell));
            preview.TexturesGeneration = preview.Generation; preview.TexturesAt = now;
        }
    }

    private void RemoveTerrainPreview(TerrainCell cell)
    {
        _terrainPreviews.Remove(cell);
        _terrainFollowCells.Add(cell);
        ulong hash = TerrainMeshHash(cell);
        TerrainSurfaces.TryRemove(hash, out _);
        TerrainMasks.TryRemove(TerrainMaskHash(cell), out _);
        var original = _streamer?.DrawableMeshes.FirstOrDefault(m => m.Key == hash).Value;
        if (original is not null) _assets[hash] = original; else _assets.Remove(hash);
        _gridDirty = true;
    }

    // Streaming rebuilt the drawable set: the edited cells go back in.
    private bool AddTerrainAssets()
    {
        bool changed = false;
        foreach (var (cell, preview) in _terrainPreviews)
        {
            if (preview.Asset is null) continue;
            ulong hash = TerrainMeshHash(cell);
            if (_assets.TryGetValue(hash, out var current) && ReferenceEquals(current, preview.Asset)) continue;
            _assets[hash] = preview.Asset; changed = true;
        }
        return changed;
    }

    private void UpdateTerrainTextures(TerrainCell cell, TerrainBase b, TerrainCellEdit? edit)
    {
        // The mask: the painted layers, or the game's. (The ground is lit by its mesh's normals.)
        ulong mask = TerrainMaskHash(cell);
        TerrainMasks[mask] = TerrainTextures.Mask(edit?.Paint?.Resolve(b) ?? b.Paint);
        _renderer.ReloadTexture(mask, true);
    }

    // --- Cursor and strokes -----------------------------------------------------------------------------

    // Where the brush is (every frame with the Terrain tool): the read terrain under the cursor.
    public void UpdateTerrainHover(Vector2? pixel, Vector2 viewport)
    {
        if (Terrain is not { } sculptor || pixel is not { } p || GameRay(p, viewport) is not var (origin, direction)) { if (!_terrainStroke) TerrainCursor = null; return; }
        var hit = sculptor.Raycast(origin, direction, 3000);
        // Off the read terrain (still loading): where the scene is hit, to start reading there.
        if (hit is null && !_terrainStroke)
        {
            var view = new Ray3(Coordinates.GameToView(origin), Coordinates.GameToView(direction));
            if (Grid.Raycast(view, 3000) is { } distance)
            {
                var scene = origin + direction * distance;
                sculptor.Ensure(TerrainCell.Covering(new Vector2(scene.X, scene.Y), 64));
            }
        }
        TerrainCursor = hit ?? (_terrainStroke ? TerrainCursor : null);
    }

    public bool BeginTerrainStroke(bool alternate)
    {
        if (Terrain is not { } sculptor || TerrainCursor is not { } at) return false;
        if (Document.ReadOnly) { Error = Loc.T("This project is read-only."); return false; }
        var brush = TerrainBrush with { Erase = alternate != TerrainBrush.Erase };
        // Holes: Alt fills; Raise and Lower swap with Alt (the brush's Erase flag).
        sculptor.BeginStroke(brush, new(at.X, at.Y));
        _terrainStroke = true; _terrainLast = new(at.X, at.Y);
        App.Breadcrumb("terrain stroke " + brush.Kind);
        TerrainStrokeTick(1 / 60f, first: true);
        return true;
    }

    // Every frame while the button is held: stamps from the last point to the cursor.
    public void TerrainStrokeTick(float dt, bool first = false)
    {
        if (!_terrainStroke || Terrain is not { } sculptor || sculptor.Brush is not { } brush || TerrainCursor is not { } at) return;
        var to = new Vector2(at.X, at.Y);
        // A ramp is drawn again from its start to the cursor.
        if (brush.Kind == TerrainBrushKind.Ramp) { sculptor.Stamp(to, 1); _terrainLast = to; return; }
        float rate = brush.Kind switch
        {
            TerrainBrushKind.Raise or TerrainBrushKind.Lower or TerrainBrushKind.Mountain or TerrainBrushKind.Roughen => 1,
            TerrainBrushKind.Hole or TerrainBrushKind.ClearPlants => 1,
            _ => 6
        };
        float amount = brush.Kind is TerrainBrushKind.Hole or TerrainBrushKind.ClearPlants ? 1 : Math.Clamp(dt, 0, .1f) * rate;
        float spacing = Math.Max(.25f, brush.Radius * .25f);
        float distance = Vector2.Distance(_terrainLast, to);
        int steps = first ? 1 : Math.Clamp((int)MathF.Ceiling(distance / spacing), 1, 64);
        for (int i = 1; i <= steps; i++)
        {
            var point = first ? to : Vector2.Lerp(_terrainLast, to, i / (float)steps);
            sculptor.Stamp(point, brush.Kind is TerrainBrushKind.Hole or TerrainBrushKind.ClearPlants ? 1 : amount / steps);
        }
        _terrainLast = to;
    }

    public void EndTerrainStroke()
    {
        if (!_terrainStroke || Terrain is not { } sculptor) return;
        var kind = sculptor.Brush?.Kind; bool erase = sculptor.Brush?.Erase == true;
        _terrainStroke = false;
        // Read before the stroke ends: the ground before it (committed) and after (working).
        var moved = TerrainFollowers(sculptor);
        var changes = sculptor.EndStroke();
        if (changes.Count == 0) return;
        string name = kind switch
        {
            TerrainBrushKind.Raise => erase ? Loc.T("Lower terrain") : Loc.T("Raise terrain"),
            TerrainBrushKind.Lower => erase ? Loc.T("Raise terrain") : Loc.T("Lower terrain"),
            TerrainBrushKind.Smooth => Loc.T("Smooth terrain"),
            TerrainBrushKind.Flatten => Loc.T("Flatten terrain"),
            TerrainBrushKind.Mountain => Loc.T("Raise mountains"),
            TerrainBrushKind.Roughen => Loc.T("Roughen terrain"),
            TerrainBrushKind.Hole => erase ? Loc.T("Fill terrain holes") : Loc.T("Cut terrain"),
            TerrainBrushKind.Paint => erase ? Loc.T("Remove terrain paint") : Loc.T("Paint terrain"),
            TerrainBrushKind.Terrace => Loc.T("Terrace terrain"),
            TerrainBrushKind.Erode => Loc.T("Erode terrain"),
            TerrainBrushKind.Ramp => Loc.T("Build a ramp"),
            TerrainBrushKind.ClearPlants => erase ? Loc.T("Bring plants back") : Loc.T("Remove plants"),
            _ => Loc.T("Restore terrain")
        };
        if (Apply(() => Document.UpdateTerrain(changes, name, moved)))
        {
            _terrainCommitted = Document.Project.Changes.Terrain;
            Status = changes.Count == 1 ? Loc.F("{0}: 1 cell of 128 m changed.", name) : Loc.F("{0}: {1} cells of 128 m changed.", name, changes.Count);
            if (moved.Count > 0) Status += " " + (moved.Count == 1 ? Loc.T("1 object on that ground moved with it.") : Loc.F("{0} objects on that ground moved with it.", moved.Count));
            LastTerrainFollowers = moved.Count;
        }
        else sculptor.SetCommitted(Document.Project.Changes.Terrain ?? System.Collections.Immutable.ImmutableDictionary<string, TerrainCellData>.Empty);
    }

    // Objects follow the ground they stand on (the panel's switch); how many the last stroke moved.
    public bool TerrainObjectsFollow { get; set; } = true;
    public int LastTerrainFollowers { get; private set; }

    // The objects standing on the ground the running stroke changed, with their new transforms:
    // the game's editable objects, the project's objects and elements; not plants (they follow on
    // their own), collision and occluders (they go with what they belong to), nor objects wider
    // than 40 m (buildings stay). An object with a mesh rests on the ground by the bottom of its
    // bounds, a marker (decal, light, sound) by its position.
    private Dictionary<string, PlacementTransform> TerrainFollowers(TerrainSculptor sculptor)
    {
        var moved = new Dictionary<string, PlacementTransform>(StringComparer.Ordinal);
        if (!TerrainObjectsFollow) return moved;
        var cells = sculptor.StrokeCells.ToHashSet();
        if (cells.Count == 0) return moved;
        bool Inside(Vector3 p) => cells.Contains(TerrainCell.At(new(p.X, p.Y)));
        void Consider(ScenePlacement item)
        {
            if (!item.CanEdit || item.Deleted || item.IsCollision || item.Original?.OccluderNode == true) return;
            if (item.Original?.Source.NodeType is "worldFoliageNode" or "worldTerrainMeshNode" or "worldTerrainCollisionNode" or "worldRoadProxyMeshNode") return;
            var t = item.Transform;
            var at = t.Position;
            if (GameBounds(item) is { } b)
            {
                if (MathF.Max(b.Max.X - b.Min.X, b.Max.Y - b.Min.Y) > 40) return;
                at = new((b.Min.X + b.Max.X) / 2, (b.Min.Y + b.Max.Y) / 2, b.Min.Z);
            }
            if (TerrainFollow.GroundMoved(sculptor, sculptor.CommittedDeltaAt, at, TerrainFollow.ObjectBelow, TerrainFollow.ObjectAbove) is { } dz)
                moved[item.Id] = t with { Position = t.Position + new Vector3(0, 0, dz) };
        }
        var edits = Document.Project.Changes.Modified;
        foreach (var sector in Document.ResidentSectors.Values)
            foreach (var p in sector.Placements)
            {
                if (!p.CanEdit) continue;
                var position = edits.TryGetValue(p.Id, out var e) ? e.Deleted ? (Vector3?)null : (e.Transform ?? p.Transform).Position : p.Transform.Position;
                if (position is { } at && Inside(at) && Document.TryGetObject(p.Id) is { } item) Consider(item);
            }
        foreach (var added in Document.Project.Changes.Added.Values)
            if (Inside(added.Transform.Position) && Document.TryGetObject(added.Id) is { } item) Consider(item);
        foreach (var element in Document.Project.Changes.Elements.Values)
            if (Inside(element.Transform.Position) && Document.TryGetObject(element.Id) is { } item) Consider(item);
        return moved;
    }

    public void CancelTerrainStroke()
    {
        if (!_terrainStroke) return;
        _terrainStroke = false;
        Terrain?.CancelStroke();
    }

    // Brush size from the keyboard and the wheel (Shift+wheel; Ctrl+Shift+wheel: strength).
    public void ResizeTerrainBrush(float factor) => TerrainBrush = (TerrainBrush with { Radius = TerrainBrush.Radius * factor }).Validated();
    public void ScaleTerrainStrength(float factor)
    {
        var b = TerrainBrush;
        float limit = b.Kind is TerrainBrushKind.Raise or TerrainBrushKind.Lower or TerrainBrushKind.Mountain or TerrainBrushKind.Roughen ? 50 : 1;
        TerrainBrush = b with { Strength = Math.Clamp(b.Strength * factor, .01f, limit) };
    }

    // Screen polylines of the brush: its edge and its hard core, following the ground.
    public IReadOnlyList<Vector2[]> TerrainBrushOutline(Vector2 viewport)
    {
        if (Terrain is not { } sculptor || TerrainCursor is not { } at) return [];
        var result = new List<Vector2[]>();
        foreach (float radius in new[] { TerrainBrush.Radius, TerrainBrush.Radius * Math.Clamp(TerrainBrush.Hardness, 0, .99f) })
        {
            if (radius < .3f) continue;
            const int segments = 64;
            var points = new List<Vector2>(segments + 1);
            for (int i = 0; i <= segments; i++)
            {
                float a = i * MathF.Tau / segments;
                var p = new Vector2(at.X + MathF.Cos(a) * radius, at.Y + MathF.Sin(a) * radius);
                float z = (sculptor.HeightAt(p) ?? at.Z) + .15f;
                if (ScreenOf(Coordinates.GameToView(new(p.X, p.Y, z)), viewport) is { } s) points.Add(s);
                else if (points.Count > 1) { result.Add(points.ToArray()); points.Clear(); }
            }
            if (points.Count > 1) result.Add(points.ToArray());
        }
        // A ramp: its axis, from its start to the cursor, over the ground.
        if (sculptor.RampStart is { } start)
        {
            var end = new Vector2(at.X, at.Y);
            int segments = Math.Clamp((int)(Vector2.Distance(start, end) / 2), 1, 128);
            var line = new List<Vector2>(segments + 1);
            for (int i = 0; i <= segments; i++)
            {
                var p = Vector2.Lerp(start, end, i / (float)segments);
                if (ScreenOf(Coordinates.GameToView(new(p.X, p.Y, (sculptor.HeightAt(p) ?? at.Z) + .3f)), viewport) is { } s) line.Add(s);
            }
            if (line.Count > 1) result.Add(line.ToArray());
        }
        return result;
    }

    // --- Automation and panel ----------------------------------------------------------------------------

    // One stroke of stamps at game positions (automation): returns the cells changed.
    public int TerrainStroke(TerrainBrush brush, IReadOnlyList<Vector2> points, float amount = 1)
    {
        if (Terrain is not { } sculptor) throw new InvalidOperationException(Loc.T("Open the game first."));
        if (Document.ReadOnly) throw new InvalidOperationException(Loc.T("This project is read-only."));
        if (points.Count == 0) return 0;
        var cells = points.SelectMany(p => TerrainCell.Covering(p, brush.Radius + 8)).Distinct().ToArray();
        sculptor.Load(cells);
        if (cells.All(c => sculptor.Base(c) is null)) throw new InvalidOperationException(Loc.T("There is no game terrain there."));
        CancelTerrainStroke();
        sculptor.BeginStroke(brush, points[0]);
        foreach (var point in points) sculptor.Stamp(point, amount);
        _terrainStroke = true;
        var previous = TerrainBrush;
        try { EndTerrainStroke(); } finally { TerrainBrush = previous; }
        return Document.Project.Changes.Terrain?.Count ?? 0;
    }

    // Every terrain edit of the project goes (one undo step).
    public void ClearTerrain()
    {
        var terrain = Document.Project.Changes.Terrain;
        if (terrain is not { Count: > 0 }) return;
        var changes = terrain.Keys.ToDictionary(k => k, k => (TerrainCellData?)null);
        if (Apply(() => Document.UpdateTerrain(changes, Loc.T("Clear terrain edits")))) Status = Loc.F("Terrain edits cleared ({0} cells).", changes.Count);
    }

    // The panel's summary: edited cells by kind of change.
    public (int Cells, int Sculpted, int Painted, int Cut) TerrainSummary()
    {
        if (Terrain is not { } sculptor) return default;
        int cells = 0, sculpted = 0, painted = 0, cut = 0;
        foreach (var cell in sculptor.EditedCells)
        {
            if (sculptor.Edit(cell) is not { } e) continue;
            cells++;
            if (e.Height is not null) sculpted++;
            if (e.Paint is not null) painted++;
            if (e.Holes is not null) cut++;
        }
        return (cells, sculpted, painted, cut);
    }
}
