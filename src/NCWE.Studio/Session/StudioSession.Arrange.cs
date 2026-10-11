// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using NCWE.Core;
using NCWE.Render;

namespace NCWE.Studio.Session;

public enum PivotMode { Active, Center, Individual }

// Arranging several objects: the gizmo's pivot (the active object, the selection's centre, or
// each object's own origin), align and distribute, duplicate in series, and snapping a move to
// the faces of nearby objects or to a vertex or surface under the cursor.
internal sealed partial class StudioSession
{
    public PivotMode Pivot { get; set; } = PivotMode.Active;

    // --- Gizmo pivot ---------------------------------------------------------------------------------

    private (long Key, Bounds3? Bounds)? _selectionBounds;

    // View-space box of the editable selection (cached per selection and document revision).
    private Bounds3? SelectionViewBounds()
    {
        long key = SelectionVersion * 1_000_003 + Document.Revision;
        if (_selectionBounds is { } cached && cached.Key == key) return cached.Bounds;
        Bounds3? bounds = GroupBounds(_selection) is { } game ? ToView(game) : null;
        _selectionBounds = (key, bounds);
        return bounds;
    }

    private static Bounds3 ToView(Bounds3 game)
    {
        var a = Coordinates.GameToView(game.Min); var b = Coordinates.GameToView(game.Max);
        return new(Vector3.Min(a, b), Vector3.Max(a, b));
    }

    // Where the gizmo sits (view space): the active object, or the selection's centre with the
    // active object's rotation (local) or none (world).
    public Matrix4x4? GizmoPivot(bool local)
    {
        if (Selected is not { } lead) return null;
        var leadMatrix = (Preview ?? lead.Transform).ViewMatrix;
        if (Pivot == PivotMode.Active || _selection.Count < 2 || SelectionViewBounds() is not { } box) return leadMatrix;
        var rotation = Matrix4x4.Identity;
        if (local && Matrix4x4.Decompose(leadMatrix, out _, out var turn, out _)) rotation = Matrix4x4.CreateFromQuaternion(turn);
        return rotation * Matrix4x4.CreateTranslation(box.Center);
    }

    // The gizmo moved its pivot from start to now: every editable selected object follows
    // (Individual: rotations and scales about each object's own origin).
    public void PreviewPivot(Matrix4x4 start, Matrix4x4 now)
    {
        try { _previews = PivotTransforms(start, now, Pivot == PivotMode.Individual); }
        catch (InvalidDataException ex) { Error = ex.Message; }
    }

    private Dictionary<string, PlacementTransform> PivotTransforms(Matrix4x4 start, Matrix4x4 now, bool individual)
    {
        var result = new Dictionary<string, PlacementTransform>();
        if (!Matrix4x4.Invert(start, out var inverse)) return result;
        var delta = inverse * now;
        Matrix4x4.Decompose(start, out var s0, out _, out _); Matrix4x4.Decompose(now, out var s1, out _, out _);
        var ratio = new Vector3(s1.X / Math.Max(s0.X, 1e-6f), s1.Y / Math.Max(s0.Y, 1e-6f), s1.Z / Math.Max(s0.Z, 1e-6f));
        var move = now.Translation - start.Translation;
        foreach (var o in SelectedObjects())
        {
            if (!o.CanEdit || o.Deleted || IsLocked(o.Id)) continue;
            var m = o.Transform.ViewMatrix;
            Matrix4x4 next;
            if (individual)
            {
                // The same turn and scale, about the object's own origin.
                var own = start; own.Translation = m.Translation;
                var moved = now; moved.Translation = m.Translation + move;
                Matrix4x4.Invert(own, out var ownInverse);
                next = m * ownInverse * moved;
            }
            else next = m * delta;
            try { result[o.Id] = PlacementTransform.FromViewMatrix(next); }
            // Non-uniform scaling of differently rotated objects would shear them: scale in place.
            catch (InvalidDataException)
            {
                var position = PlacementTransform.FromViewMatrix(Matrix4x4.CreateTranslation(next.Translation)).Position;
                result[o.Id] = o.Transform with { Position = position, Scale = o.Transform.Scale * ratio };
            }
        }
        return result;
    }

    // --- Align and distribute --------------------------------------------------------------------------

    public enum AlignEdge { Min, Center, Max }

    // Moves the selected objects so their boxes line up with the active object's on one game axis
    // (0 east X, 1 north Y, 2 up Z).
    public void AlignSelection(int axis, AlignEdge edge)
    {
        if (Selected is not { } lead || GameBounds(lead) is not { } target) { Status = Loc.T("Select the objects, the one to align to last."); return; }
        var others = EditableSelection(Loc.T("aligned")).Where(o => o.Id != lead.Id).ToArray();
        if (others.Length == 0) { Status = Loc.T("Select at least two objects: the others line up with the last one selected."); return; }
        float Edge(Bounds3 b) => edge switch { AlignEdge.Min => Get(b.Min, axis), AlignEdge.Max => Get(b.Max, axis), _ => Get(b.Center, axis) };
        var moves = new Dictionary<string, PlacementTransform>();
        foreach (var o in others)
        {
            if (GameBounds(o) is not { } box) continue;
            float shift = Edge(target) - Edge(box);
            if (MathF.Abs(shift) < 1e-5f) continue;
            moves[o.Id] = o.Transform with { Position = o.Transform.Position + Axis(axis) * shift };
        }
        if (moves.Count == 0) { Status = Loc.T("Already aligned."); return; }
        if (Apply(() => Document.TransformMany(moves)))
            Status = Loc.F("Aligned {0} object{1} ({2} {3}) with {4}.", moves.Count, (moves.Count == 1 ? "" : "s"), "XYZ"[axis], edge.ToString().ToLowerInvariant(), DisplayName(lead));
    }

    // Equal gaps between the boxes along one axis; the first and last objects stay.
    public void DistributeSelection(int axis)
    {
        var items = EditableSelection(Loc.T("distributed")).Select(o => (Object: o, Box: GameBounds(o))).Where(x => x.Box is not null)
            .Select(x => (x.Object, Box: x.Box!.Value)).OrderBy(x => Get(x.Box.Center, axis)).ToArray();
        if (items.Length < 3) { Status = Loc.T("Select at least three objects to distribute."); return; }
        float span = Get(items[^1].Box.Max, axis) - Get(items[0].Box.Min, axis);
        float sizes = items.Sum(x => Get(x.Box.Size, axis));
        float gap = (span - sizes) / (items.Length - 1);
        var moves = new Dictionary<string, PlacementTransform>();
        float cursor = Get(items[0].Box.Max, axis) + gap;
        for (int i = 1; i < items.Length - 1; i++)
        {
            var (o, box) = items[i];
            float shift = cursor - Get(box.Min, axis);
            if (MathF.Abs(shift) > 1e-5f) moves[o.Id] = o.Transform with { Position = o.Transform.Position + Axis(axis) * shift };
            cursor += Get(box.Size, axis) + gap;
        }
        if (moves.Count == 0) { Status = Loc.T("Already evenly spaced."); return; }
        if (Apply(() => Document.TransformMany(moves))) Status = Loc.F("Distributed {0} objects along {1}: {2:0.###} m between them.", items.Length, "XYZ"[axis], gap);
    }

    private static float Get(Vector3 v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };
    private static Vector3 Axis(int axis) => axis switch { 0 => Vector3.UnitX, 1 => Vector3.UnitY, _ => Vector3.UnitZ };

    // --- Duplicate in series -------------------------------------------------------------------------------

    // count copies of the selection, each one offset (game metres) and turned (degrees about Z,
    // around the selection's pivot) from the previous; one undo step, next to the originals in the outliner.
    public IReadOnlyList<string>? DuplicateSeries(int count, Vector3 offset, float yawStep)
    {
        var items = EditableSelection(Loc.T("copied")).ToArray();
        if (items.Length == 0) { Status = Loc.T("Select what to repeat first."); return null; }
        if (count is < 1 or > 500) { Error = Loc.T("Between 1 and 500 copies."); return null; }
        try
        {
            var pivot = GroupPivot(items);
            var (prefab, skipped) = CapturePrefab(items.Select(o => o.Id), "Series", pivot);
            var places = Enumerable.Range(1, count).Select(i => new PlacementTransform(pivot + offset * i,
                Quaternion.CreateFromAxisAngle(Vector3.UnitZ, yawStep * i * MathF.PI / 180), Vector3.One)).ToArray();
            // The copies go where the active object is filed.
            string? folder = Outliner.FolderOf(SelectedId ?? items[0].Id), previous = Document.DefaultFolder;
            Document.DefaultFolder = folder;
            IReadOnlyList<string>? created;
            try { created = PlacePrefabs(prefab, places, Loc.F("Duplicate in series ×{0}", count)); }
            finally { Document.DefaultFolder = previous; }
            if (created is not null) Status = Loc.F("{0} cop{1} made ({2} objects){3}", count, (count == 1 ? "y" : "ies"), created.Count, (skipped.Count == 0 ? "." : Loc.F("; {0} left out.", skipped.Count)));
            return created;
        }
        catch (InvalidOperationException ex) { Error = ex.Message; return null; }
    }

    // Size of the selection's box (game metres), for default series offsets.
    public Vector3? SelectionSize() => GroupBounds(_selection)?.Size;

    // --- Snapping a move to nearby objects --------------------------------------------------------------

    private List<Bounds3>? _snapTargets;
    private Bounds3? _snapStart;

    // At the start of a gizmo move: the boxes of the drawn objects around the selection (view space).
    public void BeginObjectSnap()
    {
        _snapTargets = null; _snapStart = SelectionViewBounds();
        if (_snapStart is not { } start || _renderer.RenderedWorldCamera is not { } frame) return;
        float reach = start.Radius + 15;
        var targets = new List<Bounds3>();
        foreach (var item in _renderer.DrawnPlacements)
        {
            if (_selectionSet.Contains(item.Id) || !_assets.TryGetValue(item.MeshHash, out var mesh)) continue;
            var box = Transformed(mesh.Bounds, item.RelativeModel, frame.Origin);
            if (box.Size.X > 80 || box.Size.Y > 80 || box.Size.Z > 80) continue;   // terrain, whole buildings
            if (Vector3.Distance(box.Center, start.Center) > reach + box.Radius) continue;
            targets.Add(box);
            if (targets.Count > 4000) break;
        }
        _snapTargets = targets;
    }

    public void EndObjectSnap() { _snapTargets = null; _snapStart = null; }

    // Adjusts a gizmo move (view space) so the selection's box touches or lines up with a
    // neighbour's faces on the axes being moved, within threshold metres.
    public Vector3 SnapMove(Vector3 move, float threshold)
    {
        if (_snapTargets is not { Count: > 0 } targets || _snapStart is not { } start) return move;
        var result = move;
        for (int axis = 0; axis < 3; axis++)
        {
            if (MathF.Abs(Get(move, axis)) < 1e-4f) continue;
            float lo = Get(start.Min, axis) + Get(move, axis), hi = Get(start.Max, axis) + Get(move, axis);
            float best = threshold, correction = 0;
            foreach (var box in targets)
            {
                // Only neighbours facing the selection on the two other axes.
                bool facing = true;
                for (int other = 0; other < 3 && facing; other++)
                    if (other != axis && (Get(box.Max, other) < Get(start.Min, other) + Get(move, other) - threshold || Get(box.Min, other) > Get(start.Max, other) + Get(move, other) + threshold)) facing = false;
                if (!facing) continue;
                float bLo = Get(box.Min, axis), bHi = Get(box.Max, axis);
                foreach (float d in stackalloc float[] { bHi - lo, bLo - hi, bLo - lo, bHi - hi })
                    if (MathF.Abs(d) < best) { best = MathF.Abs(d); correction = d; }
            }
            if (best < threshold) result = Set(result, axis, Get(move, axis) + correction);
        }
        return result;
    }

    private static Vector3 Set(Vector3 v, int axis, float value) => axis switch { 0 => v with { X = value }, 1 => v with { Y = value }, _ => v with { Z = value } };

    private static Bounds3 Transformed(Bounds3 local, Matrix4x4 model, Vector3 origin)
    {
        Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3((i & 1) == 0 ? local.Min.X : local.Max.X, (i & 2) == 0 ? local.Min.Y : local.Max.Y, (i & 4) == 0 ? local.Min.Z : local.Max.Z);
            var p = Vector3.Transform(corner, model) + origin;
            min = Vector3.Min(min, p); max = Vector3.Max(max, p);
        }
        return new(min, max);
    }

    // --- Vertex and surface snapping (hold V) ------------------------------------------------------------

    // The vertex nearest to the cursor (within radius pixels, view space): of the selection
    // (selected: true), or of what is under the cursor; otherwise, for targets, the surface point
    // under the cursor. Vertex tells which it is.
    public (Vector3 Point, bool Vertex)? SnapPoint(Vector2 pixel, Vector2 viewport, bool selected, float radius = 22)
    {
        if (_renderer.RenderedWorldCamera is not { } frame) return null;
        var drawn = _renderer.DrawnPlacements;
        var candidates = new List<DrawnPlacement>();
        Vector3? surface = null;
        if (selected)
        {
            foreach (var item in drawn) if (_selectionSet.Contains(item.Id)) candidates.Add(item);
        }
        else
        {
            // What the cursor ray hits first (the selection excluded), and its close neighbours.
            var ray = ScenePicking.ViewRay(pixel, viewport, frame.View, frame.Projection);
            float nearest = float.PositiveInfinity; DrawnPlacement? hitItem = null;
            foreach (var item in drawn)
            {
                if (_selectionSet.Contains(item.Id) || !_assets.TryGetValue(item.MeshHash, out var mesh) || mesh.SourceHash != item.MeshFingerprint) continue;
                if (ScenePicking.Intersect(ray, mesh, item.RelativeModel, item.LodMask) is { } d && d < nearest) { nearest = d; hitItem = item; }
            }
            if (hitItem is not { } hit) return null;
            var point = ray.Origin + ray.Direction * nearest;
            surface = point + frame.Origin;
            candidates.Add(hit);
            foreach (var item in drawn)
            {
                if (candidates.Count > 24) break;
                if (item.Id == hit.Id || _selectionSet.Contains(item.Id) || !_assets.TryGetValue(item.MeshHash, out var mesh)) continue;
                var box = Transformed(mesh.Bounds, item.RelativeModel, Vector3.Zero);
                var closest = Vector3.Clamp(point, box.Min, box.Max);
                if (Vector3.DistanceSquared(closest, point) < .25f && box.Radius < 40) candidates.Add(item);
            }
        }
        var viewProjection = frame.View * frame.Projection;
        float bestDistance = radius * radius; Vector3? best = null;
        foreach (var item in candidates)
        {
            if (!_assets.TryGetValue(item.MeshHash, out var mesh)) continue;
            var model = item.RelativeModel * viewProjection;
            foreach (var part in mesh.Parts)
            {
                if (part.LodMask != item.LodMask && mesh.LodMasks.Contains(item.LodMask)) continue;
                foreach (var vertex in part.Vertices)
                {
                    var clip = Vector4.Transform(new Vector4(vertex.Position, 1), model);
                    if (clip.W <= .01f) continue;
                    var screen = new Vector2((clip.X / clip.W * .5f + .5f) * viewport.X, (.5f - clip.Y / clip.W * .5f) * viewport.Y);
                    float d = Vector2.DistanceSquared(screen, pixel);
                    if (d < bestDistance) { bestDistance = d; best = Vector3.Transform(vertex.Position, item.RelativeModel) + frame.Origin; }
                }
            }
        }
        if (best is { } v) return (v, true);
        return surface is { } s ? (s, false) : null;
    }

    // A pure translation of the editable selection (vertex/surface drag preview).
    public void PreviewMove(Vector3 viewOffset) => PreviewPivot(Matrix4x4.Identity, Matrix4x4.CreateTranslation(viewOffset));

    // Screen position of a view-space point in the last drawn frame.
    public Vector2? ScreenOf(Vector3 view, Vector2 viewport) =>
        _renderer.RenderedWorldCamera is { } frame ? ProjectView(view, frame, viewport) : null;
}
