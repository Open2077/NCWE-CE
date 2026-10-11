// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using NCWE.Core;
using NCWE.Render;

namespace NCWE.Studio.Session;

// Marker nodes (lights, sounds, effects, decals) are shown as screen-space icons, like gizmo
// icons in Unity: one icon per node, tinted (lights take their color), always on top, fading
// out with distance. Icons are picked before geometry and take part in rectangle selection.
internal sealed partial class StudioSession
{
    public readonly record struct MarkerIcon(string Id, Vector2 Screen, float Radius, Vector4 Color, string Kind, bool Selected, bool Editable, float Alpha, float Depth)
    {
        // A project element (drawn with an accent rim).
        public bool Authored { get; init; }
    }
    // Elements tool active: game markers are shown even when the View option is off.
    public bool ElementsMode { get; set; }
    private readonly List<MarkerIcon> _icons = [];
    public IReadOnlyList<MarkerIcon> Icons => _icons;
    // The game's icons show within this radius (View > Icons), fading over its last 30 %; decals
    // (very numerous) within 60 % of it, 40 m at most.
    private float IconRange => Math.Clamp(Settings.IconRange, 10, 500);
    // The project's own elements have their option and their distance (View > Icons): a built
    // interior holds hundreds of them. Selected ones always show, and the Elements tool shows
    // them whatever the option (it works on them).
    public bool ShowProjectIcons { get => Settings.ShowProjectIcons; set => Settings.ShowProjectIcons = value; }
    public float ProjectIconRange { get => Math.Clamp(Settings.ProjectIconRange, 2, 1000); set => Settings.ProjectIconRange = Math.Clamp(value, 2, 1000); }
    private const float DecalIconRange = 40;
    private const int MaximumIcons = 2500;
    // Door icons have their own View option (doors are not lights or effects).
    public bool ShowDoorIcons { get => Settings.ShowDoorIcons; set => Settings.ShowDoorIcons = value; }

    // Once per frame, before the overlay draws them.
    public void UpdateIcons(CameraFrame frame, Vector2 viewport)
    {
        _icons.Clear();
        PollDoorVisuals();
        // Collision and occlusion layers: clicks go to wireframes, icons would only hide them.
        if (Layer != EditLayer.Scene) return;
        AddElementIcons(frame, viewport);
        if (ShowMarkers || ShowDoorIcons || ElementsMode) AddGameIcons(frame, viewport);
        // Far icons first, so the nearest ones are drawn on top and win picking ties.
        _icons.Sort((a, b) => b.Depth.CompareTo(a.Depth));
        if (_icons.Count > MaximumIcons) _icons.RemoveRange(0, _icons.Count - MaximumIcons);
    }

    private void AddGameIcons(CameraFrame frame, Vector2 viewport)
    {
        var eye = Camera.Eye;
        foreach (var entry in _sectorRender.Values)
            foreach (var m in entry.Markers)
            {
                if (m.Marker.Kind == "Collision") continue;
                if (_isolated is not null && !_isolated.Contains(m.Id)) continue;
                bool door = m.Marker.Kind == "Door";
                if (!ElementsMode && !(door ? ShowDoorIcons : ShowMarkers)) continue;
                bool selected = _selectionSet.Contains(m.Id);
                var position = selected && _previews is not null && _previews.TryGetValue(m.Id, out var moved) ? Coordinates.GameToView(moved.Position) : m.Position;
                float range = m.Marker.Kind == "Decal" ? Math.Min(DecalIconRange, IconRange * .6f) : IconRange, distance = Vector3.Distance(position, eye);
                if (distance > range && !selected) continue;
                if (ProjectView(position, frame, viewport) is not { } screen || screen.X < -20 || screen.Y < -20 || screen.X > viewport.X + 20 || screen.Y > viewport.Y + 20) continue;
                float alpha = selected ? 1 : Math.Clamp((range - distance) / (range * .3f), 0, 1);
                float radius = (m.Marker.Kind == "Decal" ? 7 : 10) * (distance < 12 ? 1.2f : 1);
                _icons.Add(new(m.Id, screen, radius, m.Marker.Color, m.Marker.Kind, selected, m.Editable, alpha, distance));
            }
    }

    private static Vector2? ProjectView(Vector3 view, CameraFrame frame, Vector2 viewport)
    {
        var clip = Vector4.Transform(new Vector4(view - frame.Origin, 1), frame.ViewProjection);
        if (clip.W <= .05f) return null;
        return new((clip.X / clip.W * .5f + .5f) * viewport.X, (.5f - clip.Y / clip.W * .5f) * viewport.Y);
    }

    // Nearest icon under the cursor.
    public MarkerIcon? IconAt(Vector2 pixel)
    {
        MarkerIcon? best = null;
        foreach (var icon in _icons)
            if (Vector2.Distance(icon.Screen, pixel) <= icon.Radius + 3 && (best is null || icon.Depth < best.Value.Depth) && !IsLocked(icon.Id)) best = icon;
        return best;
    }

    // Shift + drag: the editable objects and icons that are VISIBLE inside the rectangle and
    // within 300 m. An object counts when its center (or one of four inner points of its
    // screen box) is not hidden by nearer geometry; an icon when nothing stands more than a
    // metre in front of it. Read-only objects (proxies, terrain, foliage) are skipped.
    private const float MarqueeRange = 300;

    public int SelectInRectangle(Vector2 a, Vector2 b, Vector2 viewport, bool additive)
    {
        if (_renderer.RenderedWorldCamera is not { } frame) return 0;
        var min = Vector2.Min(a, b); var max = Vector2.Max(a, b);
        if (Layer != EditLayer.Scene)
        {
            var layered = Layer == EditLayer.Collision ? CollisionInRectangle(min, max, frame, viewport) : OccludersInRectangle(min, max, frame, viewport);
            if (SelectedRoadId is not null) SelectRoad(null);
            SelectMany(additive ? _selection.Concat(layered).ToArray() : layered);
            Status = layered.Count == 0 ? Loc.F("No {0} in the rectangle.", (Layer == EditLayer.Collision ? Loc.T("collision") : Loc.T("occluder"))) : Loc.F("{0} {1} object{2} selected · Move/Rotate tools, Del, Ctrl+D", _selection.Count, (Layer == EditLayer.Collision ? Loc.T("collision") : Loc.T("occluder")), (_selection.Count == 1 ? "" : "s"));
            return layered.Count;
        }
        bool Inside(Vector2 p) => p.X >= min.X && p.Y >= min.Y && p.X <= max.X && p.Y <= max.Y;
        var occluders = new MarqueeOccluders(_renderer.DrawnPlacements, _assets, frame, viewport, min, max, PickFronts);
        Matrix4x4.Invert(frame.View, out var inverseView); var eye = inverseView.Translation;   // camera-relative eye

        // Candidates: one entry per id (an entity's parts share it), the nearest drawn part.
        var placements = new Dictionary<string, (int Index, float Distance)>(StringComparer.Ordinal);
        for (int i = 0; i < occluders.Count; i++)
        {
            var item = occluders.Item(i);
            if (item.Id.StartsWith(RoadPrefix, StringComparison.Ordinal)) continue;
            var center = Vector3.Transform(occluders.Mesh(i).Bounds.Center, item.RelativeModel);
            if (ProjectRelative(center, frame, viewport) is not { } screen || !Inside(screen)) continue;
            float distance = Vector3.Distance(center, eye);
            if (distance > MarqueeRange) continue;
            if (!placements.TryGetValue(item.Id, out var known) || distance < known.Distance) placements[item.Id] = (i, distance);
        }
        var icons = _icons.Where(icon => Inside(icon.Screen) && icon.Depth <= MarqueeRange).ToArray();

        // Visibility rays run in parallel (read-only geometry, one ray per test point).
        var placementList = placements.ToArray();
        var visiblePlacements = new bool[placementList.Length];
        var visibleIcons = new bool[icons.Length];
        Parallel.For(0, placementList.Length, k => visiblePlacements[k] = occluders.PlacementVisible(placementList[k].Value.Index, min, max));
        Parallel.For(0, icons.Length, k => visibleIcons[k] = icons[k].Selected || occluders.PointVisible(icons[k].Screen, icons[k].Depth, Math.Max(1f, icons[k].Depth * .04f)));

        var candidates = new HashSet<string>(StringComparer.Ordinal);
        int hidden = 0;
        for (int k = 0; k < placementList.Length; k++) if (visiblePlacements[k]) candidates.Add(placementList[k].Key); else hidden++;
        for (int k = 0; k < icons.Length; k++) if (visibleIcons[k]) candidates.Add(icons[k].Id); else hidden++;
        candidates.RemoveWhere(IsLocked);   // locked objects are never picked
        int skipped = 0;
        var chosen = new List<string>();
        foreach (var id in candidates)
        {
            if (Document.TryGetObject(id) is { CanEdit: true, Deleted: false }) chosen.Add(id);
            else skipped++;
        }
        if (SelectedRoadId is not null) SelectRoad(null);
        SelectMany(additive ? _selection.Concat(chosen).ToArray() : chosen);
        string extra = (skipped > 0 ? Loc.F(" · {0} read-only skipped", skipped) : "") + (hidden > 0 ? Loc.F(" · {0} hidden behind others ignored", hidden) : "");
        Status = chosen.Count == 0 ? (skipped > 0 ? Loc.F("Nothing editable in the rectangle ({0} read-only objects).", skipped) : Loc.T("Nothing visible in the rectangle.")) + (hidden > 0 ? Loc.F(" {0} hidden objects ignored.", hidden) : "")
            : Loc.F("{0} object{1} selected{2} · Move/Rotate tools, Del, Ctrl+D", _selection.Count, (_selection.Count == 1 ? "" : "s"), extra);
        return chosen.Count;
    }

    private static Vector2? ProjectRelative(Vector3 relative, CameraFrame frame, Vector2 viewport)
    {
        var clip = Vector4.Transform(new Vector4(relative, 1), frame.ViewProjection);
        if (clip.W <= 1e-4f) return null;
        return new((clip.X / clip.W * .5f + .5f) * viewport.X, (.5f - clip.Y / clip.W * .5f) * viewport.Y);
    }

    // The drawn geometry that can hide something inside the marquee, binned by screen cell so a
    // ray only tests what covers its pixel.
    private sealed class MarqueeOccluders
    {
        private const int Cell = 32;
        private readonly DrawnPlacement[] _items;
        private readonly MeshAsset?[] _meshes;
        private readonly Rect[] _rects;
        private readonly Dictionary<(int, int), List<int>> _bins = [];
        private readonly CameraFrame _frame;
        private readonly Vector2 _viewport;
        // As the view draws faces: the culled back of a one-sided part hides nothing.
        private readonly bool _fronts;
        private readonly record struct Rect(Vector2 Min, Vector2 Max);
        public int Count => _items.Length;
        public DrawnPlacement Item(int i) => _items[i];
        public MeshAsset Mesh(int i) => _meshes[i]!;

        public MarqueeOccluders(IReadOnlyList<DrawnPlacement> drawn, IReadOnlyDictionary<ulong, MeshAsset> assets, CameraFrame frame, Vector2 viewport, Vector2 min, Vector2 max, bool fronts = false)
        {
            _frame = frame; _viewport = viewport; _fronts = fronts;
            var items = new List<DrawnPlacement>(); var meshes = new List<MeshAsset?>(); var rects = new List<Rect>();
            foreach (var item in drawn)
            {
                if (!assets.TryGetValue(item.MeshHash, out var mesh) || mesh.SourceHash != item.MeshFingerprint) continue;
                var rect = ScreenRect(mesh.Bounds, item.RelativeModel);
                int index = items.Count;
                items.Add(item); meshes.Add(mesh); rects.Add(rect);
                if (rect.Max.X < min.X || rect.Max.Y < min.Y || rect.Min.X > max.X || rect.Min.Y > max.Y) continue;
                int x0 = (int)MathF.Floor(MathF.Max(rect.Min.X, min.X) / Cell), x1 = (int)MathF.Floor(MathF.Min(rect.Max.X, max.X) / Cell);
                int y0 = (int)MathF.Floor(MathF.Max(rect.Min.Y, min.Y) / Cell), y1 = (int)MathF.Floor(MathF.Min(rect.Max.Y, max.Y) / Cell);
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                        (_bins.TryGetValue((x, y), out var list) ? list : _bins[(x, y)] = []).Add(index);
            }
            _items = items.ToArray(); _meshes = meshes.ToArray(); _rects = rects.ToArray();
        }

        // Screen box of a transformed bounding box; a box reaching behind the camera covers everything.
        private Rect ScreenRect(Bounds3 bounds, Matrix4x4 model)
        {
            Vector2 lo = new(float.PositiveInfinity), hi = new(float.NegativeInfinity);
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3((i & 1) == 0 ? bounds.Min.X : bounds.Max.X, (i & 2) == 0 ? bounds.Min.Y : bounds.Max.Y, (i & 4) == 0 ? bounds.Min.Z : bounds.Max.Z);
                var clip = Vector4.Transform(new Vector4(Vector3.Transform(corner, model), 1), _frame.ViewProjection);
                if (clip.W <= 1e-3f) return new(new(float.NegativeInfinity), new(float.PositiveInfinity));
                var p = new Vector2((clip.X / clip.W * .5f + .5f) * _viewport.X, (.5f - clip.Y / clip.W * .5f) * _viewport.Y);
                lo = Vector2.Min(lo, p); hi = Vector2.Max(hi, p);
            }
            return new(lo, hi);
        }

        private Ray3 Ray(Vector2 pixel) => ScenePicking.ViewRay(pixel, _viewport, _frame.View, _frame.Projection);

        // Nearest hit along the pixel's ray among the geometry covering it (optionally ignoring one id).
        private (float Distance, string? Id) Nearest(Ray3 ray, Vector2 pixel, string? ignore, float limit)
        {
            if (!_bins.TryGetValue(((int)MathF.Floor(pixel.X / Cell), (int)MathF.Floor(pixel.Y / Cell)), out var list)) return (float.PositiveInfinity, null);
            float best = limit; string? id = null;
            foreach (int i in list)
            {
                var item = _items[i];
                if (ignore is not null && item.Id == ignore) continue;
                var r = _rects[i];
                if (pixel.X < r.Min.X || pixel.Y < r.Min.Y || pixel.X > r.Max.X || pixel.Y > r.Max.Y) continue;
                if (ScenePicking.Intersect(ray, _meshes[i]!, item.RelativeModel, item.LodMask, _fronts) is { } hit && hit < best) { best = hit; id = item.Id; }
            }
            return (best, id);
        }

        // An icon (a point) is visible when nothing stands more than `tolerance` in front of it.
        public bool PointVisible(Vector2 pixel, float distance, float tolerance)
            => Nearest(Ray(pixel), pixel, null, distance - tolerance).Id is null;

        // A placement is visible when, at one of its test points, the ray reaches it (its own
        // surface, or its box when the ray passes through a gap) before anything else.
        public bool PlacementVisible(int index, Vector2 min, Vector2 max)
        {
            var item = _items[index]; var mesh = _meshes[index]!; var rect = _rects[index];
            var lo = Vector2.Max(rect.Min, min); var hi = Vector2.Min(rect.Max, max);
            var center = Vector3.Transform(mesh.Bounds.Center, item.RelativeModel);
            var points = new List<Vector2>(5);
            if (ProjectRelative(center, _frame, _viewport) is { } c) points.Add(c);
            if (float.IsFinite(lo.X) && float.IsFinite(hi.X) && float.IsFinite(lo.Y) && float.IsFinite(hi.Y))
                foreach (var (u, v) in new[] { (.25f, .25f), (.75f, .25f), (.25f, .75f), (.75f, .75f) })
                    points.Add(new(lo.X + (hi.X - lo.X) * u, lo.Y + (hi.Y - lo.Y) * v));
            foreach (var pixel in points)
            {
                var ray = Ray(pixel);
                float own = ScenePicking.Intersect(ray, mesh, item.RelativeModel, item.LodMask, _fronts) ?? EntryDistance(ray, mesh.Bounds, item.RelativeModel);
                if (!float.IsFinite(own)) continue;
                if (Nearest(ray, pixel, item.Id, own - MathF.Max(.25f, own * .02f)).Id is null) return true;
            }
            return false;
        }

        // Where the ray enters the transformed box (∞ when it misses it).
        private static float EntryDistance(Ray3 ray, Bounds3 bounds, Matrix4x4 model)
        {
            if (!Matrix4x4.Invert(model, out var inverse)) return float.PositiveInfinity;
            var o = Vector3.Transform(ray.Origin, inverse); var d = Vector3.TransformNormal(ray.Direction, inverse);
            float near = 0, far = float.PositiveInfinity;
            for (int axis = 0; axis < 3; axis++)
            {
                if (MathF.Abs(d[axis]) < 1e-10f) { if (o[axis] < bounds.Min[axis] || o[axis] > bounds.Max[axis]) return float.PositiveInfinity; continue; }
                float t0 = (bounds.Min[axis] - o[axis]) / d[axis], t1 = (bounds.Max[axis] - o[axis]) / d[axis];
                near = MathF.Max(near, MathF.Min(t0, t1)); far = MathF.Min(far, MathF.Max(t0, t1));
                if (far < near) return float.PositiveInfinity;
            }
            return near;
        }
    }
}