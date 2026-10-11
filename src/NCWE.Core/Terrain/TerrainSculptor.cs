// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using System.Numerics;

namespace NCWE.Core.Terrain;

// Applies terrain brushes in game space over the game's terrain and the project's edits.
// - Heights live on a world-wide 0.5 m lattice: a lattice point on a cell's border belongs to
//   both cells, so each stamp computes the new heights once, in world space, and writes them to
//   every cell holding the point. Holes are 1 m quads (each in one cell). Paint is per cell.
// - Committed edits come from the document (SetCommitted); a stroke works on copies of the cells
//   it touches and EndStroke hands the new cell data to the document as one undo step.
// - Bases are read on demand (Ensure) and cached; stamps skip cells not read yet.
// Not thread-safe: the editor calls it from one thread; Ensure reads bases on the thread pool.
public sealed class TerrainSculptor(ITerrainSource source)
{
    private readonly ConcurrentDictionary<TerrainCell, TerrainBase?> _bases = new();
    private readonly ConcurrentDictionary<TerrainCell, Task> _loading = new();
    private readonly Dictionary<TerrainCell, (string Hash, TerrainCellEdit Edit)> _committed = [];
    private readonly Dictionary<TerrainCell, TerrainCellEdit> _working = [];
    private readonly Dictionary<TerrainCell, int> _generations = [];
    private TerrainBrush? _brush;
    private float? _flattenTarget;

    public int Version { get; private set; }
    public bool Stroking => _brush is not null;
    public TerrainBrush? Brush => _brush;
    public event Action<TerrainCell>? BaseLoaded;

    // --- Game terrain -------------------------------------------------------------------------

    public TerrainBase? Base(TerrainCell cell) => _bases.TryGetValue(cell, out var b) ? b : null;
    public bool IsKnown(TerrainCell cell) => _bases.ContainsKey(cell);
    public IEnumerable<TerrainCell> LoadedCells => _bases.Where(b => b.Value is not null).Select(b => b.Key);

    // Starts reading the cells' bases in the background (once each).
    public void Ensure(IEnumerable<TerrainCell> cells)
    {
        foreach (var cell in cells)
        {
            if (_bases.ContainsKey(cell) || _loading.ContainsKey(cell)) continue;
            _loading[cell] = Task.Run(() =>
            {
                TerrainBase? b = null;
                try { b = source.Read(cell); } catch (Exception) { b = null; }
                _bases[cell] = b;
                _loading.TryRemove(cell, out _);
                if (b is not null) BaseLoaded?.Invoke(cell);
            });
        }
    }

    // Reads the cells' bases now (automation, export, tests).
    public void Load(IEnumerable<TerrainCell> cells, CancellationToken cancellation = default)
    {
        foreach (var cell in cells)
        {
            if (_loading.TryGetValue(cell, out var pending)) pending.Wait(cancellation);
            if (!_bases.ContainsKey(cell)) _bases[cell] = source.Read(cell, cancellation);
        }
    }

    public bool Pending => !_loading.IsEmpty;

    // --- Project edits --------------------------------------------------------------------------

    // Cells the project changes: by its edits, or by roads shaping the ground (Effective).
    public IEnumerable<TerrainCell> EditedCells => _committed.Keys.Union(_working.Keys).Union(_shapeFields.Keys.Where(c => Effective(c) is not null));
    public int Generation(TerrainCell cell) => _generations.GetValueOrDefault(cell);

    // The project's own edits of a cell (what brushes change and the document stores).
    public TerrainCellEdit? Edit(TerrainCell cell) =>
        _working.TryGetValue(cell, out var w) ? w : _committed.TryGetValue(cell, out var c) ? c.Edit : null;

    // --- Roads shaping the ground -----------------------------------------------------------------
    // Shapes (TerrainShaping) are derived from the project's roads and applied over its edits:
    // heights read here (LatticeHeight, HeightAt, DeltaAt), the effective edit of a cell (what is
    // drawn and exported) and the plants follow them; brushes keep working on the edits alone.

    private readonly Dictionary<string, TerrainShape> _shapes = new(StringComparer.Ordinal);
    private readonly Dictionary<TerrainCell, List<ShapeField>> _shapeFields = [];
    private readonly Dictionary<TerrainCell, (int Generation, TerrainCellEdit? Edit)> _effective = [];

    public IReadOnlyCollection<TerrainShape> Shapes => _shapes.Values;
    // Cells some shape reaches (their bases are needed to apply it).
    public IEnumerable<TerrainCell> ShapedCells => _shapeFields.Keys;

    // The roads' shapes now: those gone or replaced (another object) are taken off, new ones laid.
    public void SetShapes(IEnumerable<TerrainShape> shapes)
    {
        var incoming = new Dictionary<string, TerrainShape>(StringComparer.Ordinal);
        foreach (var shape in shapes) incoming[shape.Key] = shape;
        foreach (var (key, old) in _shapes.ToArray())
            if (!incoming.TryGetValue(key, out var now) || !ReferenceEquals(now, old)) RemoveShape(key);
        foreach (var (key, shape) in incoming)
            if (!_shapes.ContainsKey(key)) AddShape(shape);
    }

    private void RemoveShape(string key)
    {
        _shapes.Remove(key);
        foreach (var (cell, fields) in _shapeFields.ToArray())
        {
            if (fields.RemoveAll(f => f.Shape.Key == key) == 0) continue;
            if (fields.Count == 0) _shapeFields.Remove(cell);
            Touch(cell);
        }
    }

    private void AddShape(TerrainShape shape)
    {
        _shapes[shape.Key] = shape;
        if (shape.Line.Length < 2) return;
        // The bank reaches as far as the road's largest height above or below the ground needs.
        float difference = 0;
        foreach (var p in shape.Line)
            difference = MathF.Max(difference, UserHeightAt(new(p.X, p.Y)) is { } ground ? MathF.Abs(p.Z - TerrainShape.Below - ground) : 10);
        float reach = TerrainShaping.Reach(shape, difference);
        var lo = new Vector2(shape.Line.Min(p => p.X), shape.Line.Min(p => p.Y)) - new Vector2(reach);
        var hi = new Vector2(shape.Line.Max(p => p.X), shape.Line.Max(p => p.Y)) + new Vector2(reach);
        var first = TerrainCell.At(lo); var last = TerrainCell.At(hi);
        for (int cy = first.Y; cy <= last.Y; cy++)
            for (int cx = first.X; cx <= last.X; cx++)
            {
                var cell = new TerrainCell(cx, cy);
                if (TerrainShaping.Rasterize(shape, cell, reach) is not { } field) continue;
                if (!_shapeFields.TryGetValue(cell, out var fields)) _shapeFields[cell] = fields = [];
                fields.Add(field);
                fields.Sort((a, b) => string.CompareOrdinal(a.Shape.Key, b.Shape.Key));
                Touch(cell);
            }
    }

    // A cell as drawn and exported: the project's edits with the roads' shapes applied (heights,
    // plants cleared under the roads). Null when neither changes it.
    public TerrainCellEdit? Effective(TerrainCell cell)
    {
        var user = Edit(cell);
        if (!_shapeFields.TryGetValue(cell, out var fields)) return user;
        int generation = Generation(cell);
        if (_effective.TryGetValue(cell, out var cached) && cached.Generation == generation) return cached.Edit;
        if (Base(cell) is not { } b) return user;
        const int n = TerrainBase.Samples;
        float[]? height = null;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int i = TerrainBase.Index(x, y);
                float v = b.VisualAt(x, y);
                if (float.IsNaN(v)) continue;
                float own = user?.Height?[i] ?? 0;
                float shaped = TerrainShaping.Apply(fields, i, v + own) - v;
                if (MathF.Abs(shaped - own) < 1e-4f) continue;
                height ??= user?.Height is { } h ? (float[])h.Clone() : new float[n * n];
                height[i] = shaped;
            }
        bool[]? plants = null;
        for (int qy = 0; qy < TerrainBase.Quads; qy++)
            for (int qx = 0; qx < TerrainBase.Quads; qx++)
            {
                int q = qy * TerrainBase.Quads + qx;
                if (user?.NoPlants?[q] == true || !TerrainShaping.ClearsPlants(fields, TerrainBase.Index(2 * qx + 1, 2 * qy + 1))) continue;
                plants ??= user?.NoPlants is { } own ? (bool[])own.Clone() : new bool[TerrainBase.Quads * TerrainBase.Quads];
                plants[q] = true;
            }
        var result = height is null && plants is null ? user
            : new TerrainCellEdit { Cell = cell, Height = height ?? user?.Height, Holes = user?.Holes, Paint = user?.Paint, NoPlants = plants ?? user?.NoPlants };
        _effective[cell] = (generation, result);
        return result;
    }

    // The document's terrain (after an edit, undo, redo or opening a project).
    public void SetCommitted(IReadOnlyDictionary<string, TerrainCellData> terrain)
    {
        var seen = new HashSet<TerrainCell>();
        foreach (var (key, data) in terrain)
        {
            TerrainCell cell;
            try { cell = TerrainCell.Parse(key); } catch (FormatException) { continue; }
            seen.Add(cell);
            if (_committed.TryGetValue(cell, out var known) && known.Hash == data.Hash) continue;
            TerrainCellEdit edit;
            try { edit = data.Read(); } catch (InvalidDataException) { continue; }
            _committed[cell] = (data.Hash, edit);
            Touch(cell);
        }
        foreach (var cell in _committed.Keys.Where(c => !seen.Contains(c)).ToArray()) { _committed.Remove(cell); Touch(cell); }
    }

    private void Touch(TerrainCell cell) { _generations[cell] = _generations.GetValueOrDefault(cell) + 1; Version++; }

    // --- Heights ----------------------------------------------------------------------------------

    private const int CellSteps = TerrainBase.Samples - 1;   // 256 lattice steps per cell

    // The cell owning a world lattice point (its south-west quadrant), and the point's local index.
    private static (TerrainCell Cell, int X, int Y) Owner(long gx, long gy)
    {
        int cx = (int)Math.Floor(gx / (double)CellSteps), cy = (int)Math.Floor(gy / (double)CellSteps);
        return (new(cx + TerrainCell.Origin, cy + TerrainCell.Origin), (int)(gx - (long)cx * CellSteps), (int)(gy - (long)cy * CellSteps));
    }

    // Every cell holding a world lattice point (up to four on corners).
    private static IEnumerable<(TerrainCell Cell, int X, int Y)> Holders(long gx, long gy)
    {
        var (cell, x, y) = Owner(gx, gy);
        yield return (cell, x, y);
        if (x == 0) yield return (cell with { X = cell.X - 1 }, CellSteps, y);
        if (y == 0) yield return (cell with { Y = cell.Y - 1 }, x, CellSteps);
        if (x == 0 && y == 0) yield return (new(cell.X - 1, cell.Y - 1), CellSteps, CellSteps);
    }

    // The height change at a world lattice point, the edits' and the roads' (null: no terrain read there).
    public float? LatticeDelta(long gx, long gy) => LatticeDelta(gx, gy, false);

    private float? LatticeDelta(long gx, long gy, bool committed)
    {
        foreach (var (cell, x, y) in Holders(gx, gy))
        {
            if (Base(cell) is not { } b) continue;
            var edit = committed ? (_committed.TryGetValue(cell, out var c) ? c.Edit : null) : Edit(cell);
            int i = TerrainBase.Index(x, y);
            float delta = edit?.Height is { } h ? h[i] : 0;
            if (_shapeFields.TryGetValue(cell, out var fields) && b.VisualAt(x, y) is var v && !float.IsNaN(v))
                delta = TerrainShaping.Apply(fields, i, v + delta) - v;
            return delta;
        }
        return null;
    }

    // The edits' own height change at a world lattice point (what brushes add to).
    private float? UserLatticeDelta(long gx, long gy)
    {
        foreach (var (cell, x, y) in Holders(gx, gy))
        {
            if (Base(cell) is null) continue;
            return Edit(cell)?.Height is { } h ? h[TerrainBase.Index(x, y)] : 0;
        }
        return null;
    }

    // The cells the running stroke changed.
    public IEnumerable<TerrainCell> StrokeCells => _working.Keys;

    // The committed edits' height change at a game position (the ground before the running stroke).
    public float CommittedDeltaAt(Vector2 position) => DeltaAt(position, true);

    // Current visible height at a world lattice point (null: no terrain read there).
    public float? LatticeHeight(long gx, long gy)
    {
        foreach (var (cell, x, y) in Holders(gx, gy))
        {
            if (Base(cell) is not { } b) continue;
            float v = b.VisualAt(x, y);
            if (float.IsNaN(v)) continue;
            int i = TerrainBase.Index(x, y);
            float ground = v + (Edit(cell)?.Height is { } h ? h[i] : 0);
            return _shapeFields.TryGetValue(cell, out var fields) ? TerrainShaping.Apply(fields, i, ground) : ground;
        }
        return null;
    }

    // The game's height with the edits alone at a world lattice point (brushes work on it).
    private float? UserLatticeHeight(long gx, long gy)
    {
        foreach (var (cell, x, y) in Holders(gx, gy))
        {
            if (Base(cell) is not { } b) continue;
            float v = b.VisualAt(x, y);
            if (float.IsNaN(v)) continue;
            return v + (Edit(cell)?.Height is { } h ? h[TerrainBase.Index(x, y)] : 0);
        }
        return null;
    }

    // The game's ground with the project's edits, the roads' earthworks aside (bilinear).
    public float? EditedGroundAt(Vector2 position) => UserHeightAt(position);

    private float? UserHeightAt(Vector2 position)
    {
        float fx = position.X / TerrainBase.Step, fy = position.Y / TerrainBase.Step;
        long x0 = (long)MathF.Floor(fx), y0 = (long)MathF.Floor(fy);
        float tx = fx - x0, ty = fy - y0;
        if (UserLatticeHeight(x0, y0) is not { } h00 || UserLatticeHeight(x0 + 1, y0) is not { } h10
            || UserLatticeHeight(x0, y0 + 1) is not { } h01 || UserLatticeHeight(x0 + 1, y0 + 1) is not { } h11) return null;
        return float.Lerp(float.Lerp(h00, h10, tx), float.Lerp(h01, h11, tx), ty);
    }

    // The height change at a game position (bilinear; 0 where nothing is read or changed).
    public float DeltaAt(Vector2 position) => DeltaAt(position, false);

    private float DeltaAt(Vector2 position, bool committed)
    {
        float fx = position.X / TerrainBase.Step, fy = position.Y / TerrainBase.Step;
        long x0 = (long)MathF.Floor(fx), y0 = (long)MathF.Floor(fy);
        float tx = fx - x0, ty = fy - y0;
        float D(long x, long y) => LatticeDelta(x, y, committed) ?? 0;
        return float.Lerp(float.Lerp(D(x0, y0), D(x0 + 1, y0), tx), float.Lerp(D(x0, y0 + 1), D(x0 + 1, y0 + 1), tx), ty);
    }

    // Current visible height at a game position (bilinear), null where no terrain is read.
    public float? HeightAt(Vector2 position)
    {
        float fx = position.X / TerrainBase.Step, fy = position.Y / TerrainBase.Step;
        long x0 = (long)MathF.Floor(fx), y0 = (long)MathF.Floor(fy);
        float tx = fx - x0, ty = fy - y0;
        if (LatticeHeight(x0, y0) is not { } h00 || LatticeHeight(x0 + 1, y0) is not { } h10
            || LatticeHeight(x0, y0 + 1) is not { } h01 || LatticeHeight(x0 + 1, y0 + 1) is not { } h11) return null;
        return float.Lerp(float.Lerp(h00, h10, tx), float.Lerp(h01, h11, tx), ty);
    }

    public bool IsHole(Vector2 position)
    {
        var cell = TerrainCell.At(position);
        if (Edit(cell)?.Holes is not { } holes) return false;
        var local = position - cell.Minimum;
        int qx = Math.Clamp((int)local.X, 0, TerrainBase.Quads - 1), qy = Math.Clamp((int)local.Y, 0, TerrainBase.Quads - 1);
        return holes[qy * TerrainBase.Quads + qx];
    }

    // First hit of a game-space ray on the read terrain (holes let it through), or null.
    public Vector3? Raycast(Vector3 origin, Vector3 direction, float maxDistance = 2000)
    {
        if (direction.LengthSquared() < 1e-12f) return null;
        direction = Vector3.Normalize(direction);
        float step = .5f, previous = 0;
        bool? wasAbove = null;
        for (float t = 0; t <= maxDistance; t += step)
        {
            var p = origin + direction * t;
            if (HeightAt(new(p.X, p.Y)) is not { } h) { wasAbove = null; previous = t; step = Math.Min(step * 1.5f, 4); continue; }
            step = .5f;
            bool above = p.Z >= h;
            if (wasAbove == true && !above)
            {
                float lo = previous, hi = t;
                for (int i = 0; i < 20; i++)
                {
                    float mid = (lo + hi) / 2; var m = origin + direction * mid;
                    if (HeightAt(new(m.X, m.Y)) is { } hm && m.Z >= hm) lo = mid; else hi = mid;
                }
                var hit = origin + direction * hi;
                if (!IsHole(new(hit.X, hit.Y))) return hit with { Z = HeightAt(new(hit.X, hit.Y)) ?? hit.Z };
            }
            wasAbove = above; previous = t;
        }
        return null;
    }

    // --- Strokes ------------------------------------------------------------------------------------

    public void BeginStroke(TerrainBrush brush, Vector2 start)
    {
        CancelStroke();
        _brush = brush.Validated();
        _flattenTarget = _brush.TargetHeight ?? HeightAt(start);
        _rampStart = start; _rampStartHeight = HeightAt(start); _rampCells.Clear();
        _erosionSeed = _brush.Seed * 7919;
    }

    // Ramp: where the running stroke started (null for the other brushes).
    public Vector2? RampStart => _brush?.Kind == TerrainBrushKind.Ramp ? _rampStart : null;
    private Vector2 _rampStart;
    private float? _rampStartHeight;
    private readonly HashSet<TerrainCell> _rampCells = [];
    private int _erosionSeed;

    public void CancelStroke()
    {
        if (_brush is null) return;
        _brush = null;
        foreach (var cell in _working.Keys.ToArray()) { _working.Remove(cell); Touch(cell); }
    }

    // The stroke's copy of a cell (made on first touch), or null when its base is not read.
    private TerrainCellEdit? Working(TerrainCell cell)
    {
        if (_working.TryGetValue(cell, out var w)) return w;
        if (Base(cell) is null) return null;
        var copy = _committed.TryGetValue(cell, out var c) ? c.Edit.Clone() : new TerrainCellEdit { Cell = cell };
        _working[cell] = copy;
        return copy;
    }

    // One stamp of the stroke's brush at a game position. Amount scales the brush strength (an
    // interactive drag passes its frame time; automation 1). Returns the cells it changed.
    public IReadOnlyCollection<TerrainCell> Stamp(Vector2 centre, float amount = 1)
    {
        if (_brush is not { } brush) throw new InvalidOperationException("Begin a stroke first.");
        if (!float.IsFinite(centre.X) || !float.IsFinite(centre.Y) || !float.IsFinite(amount) || amount <= 0) return [];
        var changed = new HashSet<TerrainCell>();
        switch (brush.Kind)
        {
            case TerrainBrushKind.Hole: StampHoles(brush, centre, changed); break;
            case TerrainBrushKind.Paint: StampPaint(brush, centre, amount, changed); break;
            case TerrainBrushKind.Restore:
                StampHeights(brush, centre, amount, changed);
                StampHoles(brush with { Erase = true }, centre, changed);
                StampPaint(brush, centre, amount, changed);
                StampPlants(brush with { Erase = true }, centre, changed);
                break;
            case TerrainBrushKind.ClearPlants: StampPlants(brush, centre, changed); break;
            case TerrainBrushKind.Erode: StampErosion(brush, centre, amount, changed); break;
            case TerrainBrushKind.Ramp: StampRamp(brush, centre, changed); break;
            default: StampHeights(brush, centre, amount, changed); break;
        }
        foreach (var cell in changed) Touch(cell);
        return changed;
    }

    private void StampHeights(TerrainBrush brush, Vector2 centre, float amount, HashSet<TerrainCell> changed)
    {
        float r = brush.Radius;
        // Smoothing averages over a kernel proportional to the brush (at least 1 m).
        int kernel = brush.Kind == TerrainBrushKind.Smooth ? Math.Clamp((int)MathF.Round(r / 6 / TerrainBase.Step), 2, 24) : 0;
        long gx0 = (long)MathF.Floor((centre.X - r) / TerrainBase.Step), gx1 = (long)MathF.Ceiling((centre.X + r) / TerrainBase.Step);
        long gy0 = (long)MathF.Floor((centre.Y - r) / TerrainBase.Step), gy1 = (long)MathF.Ceiling((centre.Y + r) / TerrainBase.Step);
        int w = (int)(gx1 - gx0 + 1), h = (int)(gy1 - gy0 + 1);
        if ((long)w * h > 4_000_000) return;
        // Current heights over the footprint plus the kernel margin.
        int pw = w + 2 * kernel, ph = h + 2 * kernel;
        var heights = new float[pw * ph];
        for (int y = 0; y < ph; y++)
            for (int x = 0; x < pw; x++)
                heights[y * pw + x] = UserLatticeHeight(gx0 - kernel + x, gy0 - kernel + y) ?? float.NaN;
        float[]? smoothed = kernel > 0 ? BoxBlur(heights, pw, ph, kernel) : null;
        var updates = new List<(long X, long Y, float Delta)>();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                long gx = gx0 + x, gy = gy0 + y;
                var p = new Vector2(gx * TerrainBase.Step, gy * TerrainBase.Step);
                float weight = brush.Falloff(Vector2.Distance(p, centre));
                if (weight <= 0) continue;
                float current = heights[(y + kernel) * pw + x + kernel];
                if (float.IsNaN(current) || UserLatticeDelta(gx, gy) is not { } delta) continue;
                float change = brush.Kind switch
                {
                    TerrainBrushKind.Raise => (brush.Erase ? -1 : 1) * brush.Strength * amount * weight,
                    TerrainBrushKind.Lower => (brush.Erase ? 1 : -1) * brush.Strength * amount * weight,
                    TerrainBrushKind.Smooth => float.IsNaN(smoothed![(y + kernel) * pw + x + kernel]) ? 0
                        : (smoothed[(y + kernel) * pw + x + kernel] - current) * Math.Clamp(brush.Strength * amount * weight, 0, 1),
                    TerrainBrushKind.Flatten => _flattenTarget is { } target ? (target - current) * Math.Clamp(brush.Strength * amount * weight, 0, 1) : 0,
                    TerrainBrushKind.Mountain => brush.Strength * amount * weight * (.25f + .75f * TerrainNoise.Ridged(p.X / brush.NoiseScale, p.Y / brush.NoiseScale, brush.Seed)),
                    TerrainBrushKind.Roughen => brush.Strength * amount * weight * (TerrainNoise.Fbm(p.X / brush.NoiseScale, p.Y / brush.NoiseScale, brush.Seed) - .5f) * 2,
                    TerrainBrushKind.Restore => -delta * Math.Clamp(brush.Strength * amount * weight, 0, 1),
                    TerrainBrushKind.Terrace => (TerrainBrush.Terraced(current, brush.NoiseScale) - current) * Math.Clamp(brush.Strength * amount * weight, 0, 1),
                    _ => 0
                };
                if (change != 0 && float.IsFinite(change)) updates.Add((gx, gy, delta + change));
            }
        foreach (var (gx, gy, value) in updates)
            foreach (var (cell, x, y) in Holders(gx, gy))
            {
                if (Working(cell) is not { } edit) continue;
                (edit.Height ??= new float[TerrainBase.Samples * TerrainBase.Samples])[TerrainBase.Index(x, y)] = value;
                changed.Add(cell);
            }
    }

    // Box blur (separable) that ignores missing samples.
    private static float[] BoxBlur(float[] source, int width, int height, int radius)
    {
        var horizontal = new float[source.Length];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float sum = 0; int n = 0;
                for (int k = Math.Max(0, x - radius); k <= Math.Min(width - 1, x + radius); k++)
                    if (!float.IsNaN(source[y * width + k])) { sum += source[y * width + k]; n++; }
                horizontal[y * width + x] = n > 0 ? sum / n : float.NaN;
            }
        var result = new float[source.Length];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float sum = 0; int n = 0;
                for (int k = Math.Max(0, y - radius); k <= Math.Min(height - 1, y + radius); k++)
                    if (!float.IsNaN(horizontal[k * width + x])) { sum += horizontal[k * width + x]; n++; }
                result[y * width + x] = n > 0 ? sum / n : float.NaN;
            }
        return result;
    }

    private void StampHoles(TerrainBrush brush, Vector2 centre, HashSet<TerrainCell> changed)
    {
        float r = brush.Radius;
        foreach (var cell in TerrainCell.Covering(centre, r))
        {
            if (Base(cell) is not { } b) continue;
            var min = cell.Minimum;
            int qx0 = Math.Max(0, (int)MathF.Floor(centre.X - r - min.X)), qx1 = Math.Min(TerrainBase.Quads - 1, (int)MathF.Floor(centre.X + r - min.X));
            int qy0 = Math.Max(0, (int)MathF.Floor(centre.Y - r - min.Y)), qy1 = Math.Min(TerrainBase.Quads - 1, (int)MathF.Floor(centre.Y + r - min.Y));
            TerrainCellEdit? edit = null;
            for (int qy = qy0; qy <= qy1; qy++)
                for (int qx = qx0; qx <= qx1; qx++)
                {
                    var p = min + new Vector2(qx + .5f, qy + .5f);
                    if (Vector2.Distance(p, centre) > r || !b.Covered[qy * TerrainBase.Quads + qx]) continue;
                    edit ??= Working(cell);
                    if (edit is null) break;
                    bool current = edit.Holes?[qy * TerrainBase.Quads + qx] ?? false;
                    if (current == !brush.Erase) continue;
                    (edit.Holes ??= new bool[TerrainBase.Quads * TerrainBase.Quads])[qy * TerrainBase.Quads + qx] = !brush.Erase;
                    changed.Add(cell);
                }
        }
    }

    // Paint resolution: 0.5 m per pixel over the material's span.
    private static int PaintSize(float span) => Math.Clamp((int)MathF.Round(span / TerrainBase.Step), 8, 1024);

    private void StampPaint(TerrainBrush brush, Vector2 centre, float amount, HashSet<TerrainCell> changed)
    {
        float r = brush.Radius;
        // Cells whose textures (a little wider than the cell) reach the footprint.
        foreach (var cell in TerrainCell.Covering(centre, r + 8))
        {
            if (Base(cell) is not { } b) continue;
            var local = centre - cell.Centre;
            float half = b.UvSpan / 2;
            if (MathF.Abs(local.X) > half + r || MathF.Abs(local.Y) > half + r) continue;
            var edit = Working(cell);
            if (edit is null) continue;
            int size = PaintSize(b.UvSpan);
            TerrainPaintLayers layers;
            if (edit.Paint is { } existing) layers = existing.Resolve(b);
            else layers = b.Paint.Resample(size, size);
            if (brush.Kind == TerrainBrushKind.Paint && brush.Layer >= layers.Count)
            {
                // A layer the cell's mask does not have yet (empty up to it).
                var grown = layers.Layers.ToList();
                while (grown.Count <= brush.Layer) grown.Add(new byte[layers.Width * layers.Height]);
                layers = new() { Width = layers.Width, Height = layers.Height, Span = layers.Span, Layers = grown.ToArray() };
            }
            TerrainPaintLayers? original = brush.Kind == TerrainBrushKind.Restore ? b.Paint.Resample(layers.Width, layers.Height) : null;
            var p0 = layers.PixelOf(local - new Vector2(r)); var p1 = layers.PixelOf(local + new Vector2(r));
            int x0 = Math.Max(0, (int)MathF.Floor(Math.Min(p0.X, p1.X))), x1 = Math.Min(layers.Width - 1, (int)MathF.Ceiling(Math.Max(p0.X, p1.X)));
            int y0 = Math.Max(0, (int)MathF.Floor(Math.Min(p0.Y, p1.Y))), y1 = Math.Min(layers.Height - 1, (int)MathF.Ceiling(Math.Max(p0.Y, p1.Y)));
            bool any = false;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    var world = cell.Centre + layers.LocalOf(x, y);
                    float a = Math.Clamp(brush.Falloff(Vector2.Distance(world, centre)) * Math.Clamp(brush.Strength, 0, 1) * amount, 0, 1);
                    if (a > 0 && brush.PaintSlope != 0 && brush.Kind == TerrainBrushKind.Paint) a *= brush.SlopeWeight(SlopeAt(world));
                    if (a <= 0) continue;
                    int at = y * layers.Width + x;
                    if (original is not null)
                    {
                        for (int l = 0; l < layers.Count; l++)
                        {
                            byte target = l < original.Count ? original.Layers[l][at] : (byte)0;
                            layers.Layers[l][at] = (byte)MathF.Round(float.Lerp(layers.Layers[l][at], target, a));
                        }
                    }
                    else if (brush.Erase)
                    {
                        if (brush.Layer > 0) layers.Layers[brush.Layer][at] = (byte)MathF.Round(layers.Layers[brush.Layer][at] * (1 - a));
                    }
                    else
                    {
                        // The layer covers what is below it, and the layers above it give way.
                        if (brush.Layer > 0) layers.Layers[brush.Layer][at] = (byte)MathF.Round(float.Lerp(layers.Layers[brush.Layer][at], 255, a));
                        for (int l = brush.Layer + 1; l < layers.Count; l++) layers.Layers[l][at] = (byte)MathF.Round(layers.Layers[l][at] * (1 - a));
                    }
                    any = true;
                }
            if (!any) continue;
            edit.Paint = new TerrainPaintEdit { Width = layers.Width, Height = layers.Height, Span = layers.Span, Layers = layers.Layers.Select(l => (byte[]?)l).ToArray() };
            changed.Add(cell);
        }
    }

    // The ground's slope at a game position, degrees (0 where nothing is read).
    public float SlopeAt(Vector2 position)
    {
        const float d = TerrainBase.Step;
        if (HeightAt(position + new Vector2(d, 0)) is not { } e || HeightAt(position - new Vector2(d, 0)) is not { } w
            || HeightAt(position + new Vector2(0, d)) is not { } n || HeightAt(position - new Vector2(0, d)) is not { } s) return 0;
        float gx = (e - w) / (2 * d), gy = (n - s) / (2 * d);
        return MathF.Atan(MathF.Sqrt(gx * gx + gy * gy)) * 180 / MathF.PI;
    }

    // --- Plants --------------------------------------------------------------------------------------

    // Whether the game's plants are removed at a game position (ClearPlants brush).
    public bool PlantsCleared(Vector2 position)
    {
        var cell = TerrainCell.At(position);
        if (Effective(cell)?.NoPlants is not { } cleared) return false;
        var local = position - cell.Minimum;
        int qx = Math.Clamp((int)local.X, 0, TerrainBase.Quads - 1), qy = Math.Clamp((int)local.Y, 0, TerrainBase.Quads - 1);
        return cleared[qy * TerrainBase.Quads + qx];
    }

    private void StampPlants(TerrainBrush brush, Vector2 centre, HashSet<TerrainCell> changed)
    {
        float r = brush.Radius;
        foreach (var cell in TerrainCell.Covering(centre, r))
        {
            if (Base(cell) is null) continue;
            var min = cell.Minimum;
            int qx0 = Math.Max(0, (int)MathF.Floor(centre.X - r - min.X)), qx1 = Math.Min(TerrainBase.Quads - 1, (int)MathF.Floor(centre.X + r - min.X));
            int qy0 = Math.Max(0, (int)MathF.Floor(centre.Y - r - min.Y)), qy1 = Math.Min(TerrainBase.Quads - 1, (int)MathF.Floor(centre.Y + r - min.Y));
            TerrainCellEdit? edit = null;
            for (int qy = qy0; qy <= qy1; qy++)
                for (int qx = qx0; qx <= qx1; qx++)
                {
                    if (Vector2.Distance(min + new Vector2(qx + .5f, qy + .5f), centre) > r) continue;
                    edit ??= Working(cell);
                    if (edit is null) break;
                    bool current = edit.NoPlants?[qy * TerrainBase.Quads + qx] ?? false;
                    if (current == !brush.Erase) continue;
                    if (brush.Erase && edit.NoPlants is null) continue;
                    (edit.NoPlants ??= new bool[TerrainBase.Quads * TerrainBase.Quads])[qy * TerrainBase.Quads + qx] = !brush.Erase;
                    changed.Add(cell);
                }
        }
    }

    // --- Ramp -----------------------------------------------------------------------------------------

    // The ground before the running stroke at a world lattice point (game heights and committed edits).
    private float? CommittedLatticeHeight(long gx, long gy)
    {
        foreach (var (cell, x, y) in Holders(gx, gy))
        {
            if (Base(cell) is not { } b) continue;
            float v = b.VisualAt(x, y);
            if (float.IsNaN(v)) continue;
            return v + (_committed.TryGetValue(cell, out var c) && c.Edit.Height is { } h ? h[TerrainBase.Index(x, y)] : 0);
        }
        return null;
    }

    private float? CommittedHeightAt(Vector2 position)
    {
        float fx = position.X / TerrainBase.Step, fy = position.Y / TerrainBase.Step;
        long x0 = (long)MathF.Floor(fx), y0 = (long)MathF.Floor(fy);
        float tx = fx - x0, ty = fy - y0;
        if (CommittedLatticeHeight(x0, y0) is not { } h00 || CommittedLatticeHeight(x0 + 1, y0) is not { } h10
            || CommittedLatticeHeight(x0, y0 + 1) is not { } h01 || CommittedLatticeHeight(x0 + 1, y0 + 1) is not { } h11) return null;
        return float.Lerp(float.Lerp(h00, h10, tx), float.Lerp(h01, h11, tx), ty);
    }

    // A straight slope from the stroke's start (its ground then) to `end` (the ground there before
    // the stroke), `Radius` wide on each side, its edges easing into the ground (Hardness).
    // Re-derived from the committed ground each time, so dragging the end shows it live.
    private void StampRamp(TerrainBrush brush, Vector2 end, HashSet<TerrainCell> changed)
    {
        // The previous preview goes: its cells are back to their committed heights.
        foreach (var cell in _rampCells)
        {
            if (!_working.TryGetValue(cell, out var w)) continue;
            w.Height = _committed.TryGetValue(cell, out var c) && c.Edit.Height is { } h ? (float[])h.Clone() : null;
            changed.Add(cell);
        }
        _rampCells.Clear();
        var start = _rampStart;
        if (_rampStartHeight is not { } from || Vector2.Distance(start, end) < .5f || CommittedHeightAt(end) is not { } to) return;
        float r = brush.Radius, strength = Math.Clamp(brush.Strength, 0, 1);
        var axis = end - start; float length2 = axis.LengthSquared();
        long gx0 = (long)MathF.Floor((MathF.Min(start.X, end.X) - r) / TerrainBase.Step), gx1 = (long)MathF.Ceiling((MathF.Max(start.X, end.X) + r) / TerrainBase.Step);
        long gy0 = (long)MathF.Floor((MathF.Min(start.Y, end.Y) - r) / TerrainBase.Step), gy1 = (long)MathF.Ceiling((MathF.Max(start.Y, end.Y) + r) / TerrainBase.Step);
        if ((gx1 - gx0 + 1) * (gy1 - gy0 + 1) > 4_000_000) return;
        for (long gy = gy0; gy <= gy1; gy++)
            for (long gx = gx0; gx <= gx1; gx++)
            {
                var p = new Vector2(gx * TerrainBase.Step, gy * TerrainBase.Step);
                float t = Math.Clamp(Vector2.Dot(p - start, axis) / length2, 0, 1);
                float weight = brush.Falloff(Vector2.Distance(p, start + axis * t)) * strength;
                if (weight <= 0 || CommittedLatticeHeight(gx, gy) is not { } ground) continue;
                float target = float.Lerp(from, to, t);
                foreach (var (cell, x, y) in Holders(gx, gy))
                {
                    if (Working(cell) is not { } edit) continue;
                    var heights = edit.Height ??= new float[TerrainBase.Samples * TerrainBase.Samples];
                    float committed = _committed.TryGetValue(cell, out var c) && c.Edit.Height is { } h ? h[TerrainBase.Index(x, y)] : 0;
                    heights[TerrainBase.Index(x, y)] = committed + (target - ground) * weight;
                    _rampCells.Add(cell); changed.Add(cell);
                }
            }
    }

    // --- Erosion -------------------------------------------------------------------------------------

    // Hydraulic erosion: water droplets dropped in the brush run downhill, picking up ground where
    // they speed up and dropping it where they slow down, which carves gullies and fans out
    // sediment. The change is faded to the brush's edge.
    private void StampErosion(TerrainBrush brush, Vector2 centre, float amount, HashSet<TerrainCell> changed)
    {
        float r = brush.Radius;
        const int margin = 6;
        long gx0 = (long)MathF.Floor((centre.X - r) / TerrainBase.Step) - margin, gx1 = (long)MathF.Ceiling((centre.X + r) / TerrainBase.Step) + margin;
        long gy0 = (long)MathF.Floor((centre.Y - r) / TerrainBase.Step) - margin, gy1 = (long)MathF.Ceiling((centre.Y + r) / TerrainBase.Step) + margin;
        int w = (int)(gx1 - gx0 + 1), h = (int)(gy1 - gy0 + 1);
        if ((long)w * h > 2_000_000) return;
        var heights = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) heights[y * w + x] = UserLatticeHeight(gx0 + x, gy0 + y) ?? float.NaN;
        var before = (float[])heights.Clone();
        float cells = MathF.PI * r * r / (TerrainBase.Step * TerrainBase.Step);
        int droplets = Math.Clamp((int)MathF.Round(cells * .15f * Math.Clamp(brush.Strength, 0, 1) * amount), 1, 40_000);
        var random = new Random(_erosionSeed++);
        int life = Math.Clamp((int)(r / TerrainBase.Step * 1.5f), 30, 240);
        const float inertia = .1f, capacityFactor = 6, minCapacity = .01f, erodeSpeed = .3f, depositSpeed = .2f, evaporate = .01f, gravity = 4;
        // Ground is taken and laid over a disc a few metres wide (a twelfth of the brush): gullies,
        // not the needles a single sample would make on a 0.5 m lattice.
        int spread = Math.Clamp((int)MathF.Round(r / 12 / TerrainBase.Step), 2, 8);
        var kernel = new List<(int Offset, float Weight)>();
        float kernelTotal = 0;
        for (int oy = -spread; oy <= spread; oy++)
            for (int ox = -spread; ox <= spread; ox++)
            {
                float weight = spread + .5f - MathF.Sqrt(ox * ox + oy * oy);
                if (weight > 0) { kernel.Add((oy * w + ox, weight)); kernelTotal += weight; }
            }
        float At(float px, float py)
        {
            int ix = (int)px, iy = (int)py; float fx = px - ix, fy = py - iy;
            int i = iy * w + ix;
            return float.Lerp(float.Lerp(heights[i], heights[i + 1], fx), float.Lerp(heights[i + w], heights[i + w + 1], fx), fy);
        }
        for (int d = 0; d < droplets; d++)
        {
            float a = random.NextSingle() * MathF.Tau, rr = r * MathF.Sqrt(random.NextSingle());
            float px = (centre.X + MathF.Cos(a) * rr) / TerrainBase.Step - gx0, py = (centre.Y + MathF.Sin(a) * rr) / TerrainBase.Step - gy0;
            float dx = 0, dy = 0, speed = 1, water = 1, sediment = 0;
            for (int step = 0; step < life; step++)
            {
                int ix = (int)px, iy = (int)py;
                if (ix < spread || iy < spread || ix >= w - 1 - spread || iy >= h - 1 - spread) break;
                float fx = px - ix, fy = py - iy;
                int i = iy * w + ix;
                float h00 = heights[i], h10 = heights[i + 1], h01 = heights[i + w], h11 = heights[i + w + 1];
                if (float.IsNaN(h00 + h10 + h01 + h11)) break;
                float gradX = (h10 - h00) * (1 - fy) + (h11 - h01) * fy, gradY = (h01 - h00) * (1 - fx) + (h11 - h10) * fx;
                float height = float.Lerp(float.Lerp(h00, h10, fx), float.Lerp(h01, h11, fx), fy);
                dx = dx * inertia - gradX * (1 - inertia); dy = dy * inertia - gradY * (1 - inertia);
                float len = MathF.Sqrt(dx * dx + dy * dy);
                if (len < 1e-6f) break;
                dx /= len; dy /= len;
                float nx = px + dx, ny = py + dy;
                if (nx < 1 || ny < 1 || nx >= w - 2 || ny >= h - 2) break;
                float next = At(nx, ny);
                if (float.IsNaN(next)) break;
                float dh = next - height;
                float capacity = MathF.Max(-dh * speed * water * capacityFactor, minCapacity);
                float change;
                if (sediment > capacity || dh > 0)
                {
                    change = dh > 0 ? MathF.Min(dh, sediment) : (sediment - capacity) * depositSpeed;
                    sediment -= change;
                }
                else
                {
                    change = -MathF.Min((capacity - sediment) * erodeSpeed, -dh);
                    sediment -= change;
                }
                foreach (var (offset, weight) in kernel)
                    if (!float.IsNaN(heights[i + offset])) heights[i + offset] += change * weight / kernelTotal;
                speed = MathF.Min(MathF.Sqrt(MathF.Max(0, speed * speed - dh * gravity)), 12);
                water *= 1 - evaporate;
                px = nx; py = ny;
            }
        }
        // The change, lightly blurred (what is left of single droplets' marks).
        var changes = new float[w * h];
        for (int i = 0; i < changes.Length; i++) changes[i] = float.IsNaN(heights[i]) || float.IsNaN(before[i]) ? float.NaN : heights[i] - before[i];
        changes = BoxBlur(changes, w, h, 1);
        var updates = new List<(long X, long Y, float Delta)>();
        for (int y = margin; y < h - margin; y++)
            for (int x = margin; x < w - margin; x++)
            {
                float change = changes[y * w + x];
                if (!float.IsFinite(change) || MathF.Abs(change) < 1e-4f) continue;
                long gx = gx0 + x, gy = gy0 + y;
                float weight = brush.Falloff(Vector2.Distance(new(gx * TerrainBase.Step, gy * TerrainBase.Step), centre));
                if (weight <= 0 || UserLatticeDelta(gx, gy) is not { } delta) continue;
                updates.Add((gx, gy, delta + change * weight));
            }
        foreach (var (gx, gy, value) in updates)
            foreach (var (cell, x, y) in Holders(gx, gy))
            {
                if (Working(cell) is not { } edit) continue;
                (edit.Height ??= new float[TerrainBase.Samples * TerrainBase.Samples])[TerrainBase.Index(x, y)] = value;
                changed.Add(cell);
            }
    }

    // Ends the stroke: the new data of every cell it changed (null: back to the game's terrain).
    public IReadOnlyDictionary<string, TerrainCellData?> EndStroke()
    {
        var result = new Dictionary<string, TerrainCellData?>();
        if (_brush is null) return result;
        _brush = null;
        foreach (var (cell, edit) in _working)
        {
            edit.Normalize(Base(cell));
            var data = edit.IsEmpty ? null : edit.ToData();
            bool had = _committed.TryGetValue(cell, out var before);
            if (data is null && !had) continue;
            if (data is not null && had && before.Hash == data.Hash) continue;
            result[cell.ToString()] = data;
            if (data is null) _committed.Remove(cell); else _committed[cell] = (data.Hash, edit);
        }
        foreach (var cell in _working.Keys.ToArray()) { _working.Remove(cell); Touch(cell); }
        return result;
    }
}
