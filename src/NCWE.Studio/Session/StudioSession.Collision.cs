// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.CompilerServices;
using NCWE.Core;
using NCWE.Formats;
using NCWE.Render;

namespace NCWE.Studio.Session;

// Collision and occlusion editing. A layer chooses what clicks, rectangles and the gizmo work
// on: the scene (meshes, entities, elements), collision (game actors and project collisions) or
// occlusion (occluder nodes, then meshes for their occlusion settings). Collision is drawn as its
// real shapes (boxes, spheres, capsules, cooked convex and triangle meshes), occluders as their
// meshes: translucent shaded volumes with their outlines (StudioSession.CollisionView.cs).
internal sealed partial class StudioSession
{
    public enum EditLayer { Scene, Collision, Occlusion }
    private EditLayer _layer;
    public EditLayer Layer
    {
        get => _layer;
        set { if (_layer == value) return; _layer = value; LayerVersion++; _collisionKey = default; _occluderKey = default; }
    }
    public long LayerVersion { get; private set; }
    public bool ShowOccluders { get; set; }
    public float CollisionRange { get; set; } = 30;
    public float OccluderRange { get; set; } = 80;
    private const int MaximumCollisionLines = 250_000, MaximumOccluderLines = 60_000;

    // --- Cooked collision meshes ---------------------------------------------------------------

    // Thread-safe: the wireframes are built on the thread pool.
    private readonly ConcurrentDictionary<(ulong Sector, ulong Hash), CollisionMeshData?> _cooked = new();
    private readonly ConcurrentDictionary<(ulong, ulong), byte> _cookedPending = new();
    private int _cookedVersion;

    // Null until decoded on the thread pool (the city geometry cache loads on first use).
    private CollisionMeshData? Cooked(ulong sector, ulong hash)
    {
        if (_cooked.TryGetValue((sector, hash), out var mesh)) return mesh;
        if (Index is not { } index || !_cookedPending.TryAdd((sector, hash), 0)) return null;
        _ = Task.Run(() =>
        {
            CollisionMeshData? data = null;
            try { data = index.CollisionMesh(sector, hash); } catch (Exception) { }
            _cooked[(sector, hash)] = data;
            Interlocked.Increment(ref _cookedVersion);
        });
        return null;
    }

    // --- Collision overlay -------------------------------------------------------------------------

    private (int EyeX, int EyeY, int EyeZ, long Revision, long Selection, int Cooked, float Range, bool On, int Heading, long Display, long Roads) _collisionKey;
    private bool _collisionCapped, _occluderCapped;

    // Distance rank of a point for a line budget: what lies behind the camera comes last.
    private static float Rank(Vector3 point, Vector3 eye, Vector3 forward)
    {
        var d = point - eye;
        return d.LengthSquared() * (Vector3.Dot(d, forward) < 0 ? 9 : 1);
    }

    // A capped build follows the camera's heading (eight sectors, up/down) so what is ahead stays drawn.
    private int Heading(bool capped)
    {
        if (!capped) return -1;
        var f = Coordinates.ViewToGame(Camera.Eye + Camera.Forward) - Coordinates.ViewToGame(Camera.Eye);
        return (int)MathF.Floor((MathF.Atan2(f.Y, f.X) + MathF.PI) / (MathF.PI / 4)) * 3 + (f.Z > .5f ? 1 : f.Z < -.5f ? 2 : 0);
    }
    private List<DebugLine> _collisionLines = [], _collisionSpare = [];
    private List<DebugTriangle> _collisionFills = [];
    private long _xrayVersion, _fillVersion;
    private Task<(List<DebugLine> Lines, List<DebugTriangle> Fills, bool Capped)>? _collisionBuild;
    private (int EyeX, int EyeY, int EyeZ, long Revision, long Selection, int Cooked, float Range, bool On, int Heading, long Display, long Roads) _collisionBuildKey;
    private static readonly Vector4 SelectedCollision = new(1f, .9f, .25f, 1), ProjectCollision = new(.3f, .95f, 1f, 1), DisabledCollision = new(.45f, .45f, .5f, 1);
    private const int MaximumCollisionFills = 150_000;

    private void BuildCollisionOverlay()
    {
        _renderer.SceneDim = DimWorld && Layer != EditLayer.Scene ? .5f : 0;
        bool on = ShowCollision || Layer == EditLayer.Collision;
        if (!on)
        {
            _collisionKey = default; _renderer.SetStaticXray(0, null, 0); _renderer.SetStaticFill(1, null, 0);
            return;
        }
        var eye = Coordinates.ViewToGame(Camera.Eye);
        var key = ((int)MathF.Floor(eye.X), (int)MathF.Floor(eye.Y), (int)MathF.Floor(eye.Z), Document.Revision, SelectionVersion, Volatile.Read(ref _cookedVersion), CollisionRange, on, Heading(_collisionCapped), LayerDisplayVersion, RoadVersion);
        // Built on the thread pool from immutable inputs, the previous one staying on screen until
        // the new one is ready.
        if (_collisionBuild is { IsCompleted: true } done)
        {
            _collisionBuild = null;
            if (done.IsCompletedSuccessfully)
            {
                _collisionSpare = _collisionLines;
                (_collisionLines, _collisionFills, _collisionCapped) = done.Result;
                _collisionKey = _collisionBuildKey; _collisionKey.Heading = Heading(_collisionCapped);
                _renderer.SetStaticXray(0, _collisionLines, ++_xrayVersion, dimmed: CollisionXray);
                _renderer.SetStaticFill(1, _collisionFills, ++_fillVersion);
            }
        }
        if (key != _collisionKey && _collisionBuild is null)
        {
            var sectors = Document.ResidentSectors.Values.ToArray();
            var changes = Document.Project.Changes;
            var selected = new HashSet<string>(_selectionSet, StringComparer.Ordinal);
            var forward = Coordinates.ViewToGame(Camera.Eye + Camera.Forward) - eye;
            float range = CollisionRange;
            bool fade = FadeUnselected && Layer == EditLayer.Collision && selected.Count > 0, game = ShowGameCollision, project = ShowProjectCollision;
            var hidden = new HashSet<string>(HiddenCollisionPresets, StringComparer.Ordinal);
            _collisionBuildKey = key;
            var target = _collisionSpare; _collisionSpare = [];
            // Editor roads: the collision the export generates for each segment.
            // Roads get collision boxes on export; cables hang clear of everything.
            var roads = _roadMeshes.Where(r => IsShown(r.Key) && !r.Value.Road.IsCable).SelectMany(r => r.Value.Segments.Select(s => (s.Mesh, s.Origin, s.Frames))).ToArray();
            _collisionBuild = Task.Run(() => CollisionGeometry(target, sectors, changes, selected, eye, forward, range, fade, game, project, hidden, roads));
        }
        // The selected collision: bright faces and outline on top; the one under the cursor too.
        foreach (var item in CachedSelection())
        {
            if (!item.IsCollision || item.Deleted) continue;
            var transform = _previews is not null && _previews.TryGetValue(item.Id, out var preview) ? preview : item.Transform;
            ulong cooked = item.AuthoredCollision?.CookedSector ?? item.Original?.Collision?.CookedSector ?? 0;
            var disabled = Document.Project.Changes.Modified.GetValueOrDefault(item.Id)?.DisabledShapes ?? [];
            AddCollisionActor(_renderer.OverlayLines, _renderer.OverlayFills, transform, item.CollisionShapes, cooked, SelectedCollision, disabled, SelectedAlpha, 1, eye);
        }
        if (Layer == EditLayer.Collision && _hoverShape is { } hover && !_selectionSet.Contains(hover) && Document.TryGetObject(hover) is { IsCollision: true, Deleted: false } hovered)
        {
            ulong cooked = hovered.AuthoredCollision?.CookedSector ?? hovered.Original?.Collision?.CookedSector ?? 0;
            AddCollisionActor(_renderer.OverlayLines, _renderer.OverlayFills, hovered.Transform, hovered.CollisionShapes, cooked, HoverOutline, [], HoverAlpha, 1, eye);
        }
    }

    private (List<DebugLine> Lines, List<DebugTriangle> Fills, bool Capped) CollisionGeometry(List<DebugLine> lines, SectorSnapshot[] sectors, ChangeSet changes,
        HashSet<string> selected, Vector3 eye, Vector3 forward, float range, bool fade, bool showGame, bool showProject, HashSet<string> hidden,
        (MeshAsset Mesh, Vector3 Origin, ImmutableArray<Matrix4x4> Frames)[] roads)
    {
        lines.Clear();
        var fills = new List<DebugTriangle>(65536);
        float range2 = range * range;
        float alpha = fade ? FadedAlpha : FillAlpha, projectAlpha = fade ? FadedAlpha : ProjectFillAlpha, dim = fade ? .3f : .9f;
        // Project collisions first (never dropped), then game actors nearest first.
        if (showProject)
            foreach (var collision in changes.Collisions.Values)
                if (!selected.Contains(collision.Id) && Vector3.DistanceSquared(collision.Transform.Position, eye) <= range2)
                    AddCollisionActor(lines, fills, collision.Transform, collision.Shapes, collision.CookedSector, ProjectCollision, [], projectAlpha, dim, eye, hidden);
        if (showProject)
            foreach (var (mesh, origin, frames) in roads)
                if (Vector3.DistanceSquared(origin, eye) <= range2)
                    AddCollisionActor(lines, fills, PlacementTransform.Identity with { Position = origin }, RoadShapes(mesh, frames), 0, ProjectCollision, [], projectAlpha, dim, eye, hidden);
        if (!showGame) return (lines, fills, false);
        var candidates = new List<(float Rank, SectorSnapshot Sector, CollisionActorRecord Actor)>();
        foreach (var sector in sectors)
        {
            if (sector.CollisionActors.IsDefaultOrEmpty) continue;
            foreach (var actor in sector.CollisionActors)
                if (Vector3.DistanceSquared(actor.Transform.Position, eye) <= range2) candidates.Add((Rank(actor.Transform.Position, eye, forward), sector, actor));
        }
        candidates.Sort((a, b) => a.Rank.CompareTo(b.Rank));
        foreach (var (_, sector, actor) in candidates)
        {
            if (lines.Count >= MaximumCollisionLines) return (lines, fills, true);
            string id = ActorId(sector, actor);
            changes.Modified.TryGetValue(id, out var edit);
            if (edit?.Deleted == true || selected.Contains(id)) continue;
            AddCollisionActor(lines, fills.Count < MaximumCollisionFills ? fills : null, edit?.Transform ?? actor.Transform, actor.Shapes, actor.CookedSector, null, edit?.DisabledShapes ?? [], alpha, dim, eye, hidden);
        }
        return (lines, fills, false);
    }

    // A bent road segment's collision boxes (as exported), computed once per bent mesh.
    private sealed record RoadShapeSet(ImmutableArray<CollisionShape> Shapes);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MeshAsset, RoadShapeSet> RoadShapeCache = new();
    private static ImmutableArray<CollisionShape> RoadShapes(MeshAsset mesh, ImmutableArray<Matrix4x4> frames)
        => RoadShapeCache.GetValue(mesh, m => new(RoadCollision.Shapes(m, frames))).Shapes;

    internal static string ActorId(SectorSnapshot sector, CollisionActorRecord actor) => $"s:{sector.Resource.Hash:x16}:{actor.NodeIndex}:{actor.PlacementIndex}:{actor.ActorIndex}";

    // --- Collision picking ---------------------------------------------------------------------------

    // Game-space ray under a pixel (from the last rendered camera).
    private (Vector3 Origin, Vector3 Direction)? GameRay(Vector2 pixel, Vector2 viewport)
    {
        if (_renderer.RenderedWorldCamera is not { } frame) return null;
        var ray = ScenePicking.ViewRay(pixel, viewport, frame.View, frame.Projection);
        var origin = Coordinates.ViewToGame(ray.Origin + frame.Origin);
        var direction = Coordinates.ViewToGame(ray.Origin + frame.Origin + ray.Direction) - origin;
        return (origin, Vector3.Normalize(direction));
    }

    // Nearest collision (game actor or project collision) under the cursor, within range.
    public string? PickCollision(Vector2 pixel, Vector2 viewport)
    {
        if (GameRay(pixel, viewport) is not var (origin, direction)) return null;
        string? best = null; float nearest = float.PositiveInfinity;
        float range2 = (CollisionRange + 20) * (CollisionRange + 20);
        var modified = Document.Project.Changes.Modified;
        // Only what is drawn can be picked.
        if (ShowGameCollision)
        foreach (var sector in Document.ResidentSectors.Values)
            foreach (var actor in sector.CollisionActors)
            {
                if (Vector3.DistanceSquared(actor.Transform.Position, origin) > range2) continue;
                string id = ActorId(sector, actor);
                modified.TryGetValue(id, out var edit);
                if (edit?.Deleted == true) continue;
                if (HitActor(origin, direction, edit?.Transform ?? actor.Transform, actor.Shapes, actor.CookedSector) is { } t && t < nearest) { nearest = t; best = id; }
            }
        if (ShowProjectCollision)
        foreach (var collision in Document.Project.Changes.Collisions.Values)
            if (HitActor(origin, direction, collision.Transform, collision.Shapes, collision.CookedSector) is { } t && t < nearest) { nearest = t; best = collision.Id; }
        return best;
    }

    private float? HitActor(Vector3 origin, Vector3 direction, PlacementTransform actor, ImmutableArray<CollisionShape> shapes, ulong cookedSector)
    {
        float? best = null;
        foreach (var shape in shapes)
        {
            if (HiddenCollisionPresets.Count > 0 && HiddenCollisionPresets.Contains(PresetOf(shape))) continue;
            var model = Matrix4x4.CreateFromQuaternion(shape.Rotation) * Matrix4x4.CreateTranslation(shape.Position) * actor.GameMatrix;
            if (!Matrix4x4.Invert(model, out var inverse)) continue;
            var o = Vector3.Transform(origin, inverse); var d = Vector3.TransformNormal(direction, inverse);
            float? hit = shape.Kind switch
            {
                CollisionShapeKind.Sphere => RaySphere(o, d, shape.Size.X),
                CollisionShapeKind.Box or CollisionShapeKind.Capsule => RayBox(o, d, shape.HalfExtents),
                _ => Cooked(cookedSector, shape.MeshHash) is { } mesh ? RayMesh(o, d, mesh) : RayBox(o, d, new(.25f))
            };
            if (hit is { } t && (best is null || t < best)) best = t;
        }
        return best;
    }

    private static float? RayBox(Vector3 o, Vector3 d, Vector3 half)
    {
        float near = 0, far = float.PositiveInfinity;
        for (int axis = 0; axis < 3; axis++)
        {
            float origin = o[axis], dir = d[axis];
            if (MathF.Abs(dir) < 1e-10f) { if (origin < -half[axis] || origin > half[axis]) return null; continue; }
            float a = (-half[axis] - origin) / dir, b = (half[axis] - origin) / dir;
            near = MathF.Max(near, MathF.Min(a, b)); far = MathF.Min(far, MathF.Max(a, b));
            if (far < near) return null;
        }
        // A shape around the camera (a building's box) would take every click: skipped.
        return near > 0 ? near : null;
    }

    private static float? RaySphere(Vector3 o, Vector3 d, float r)
    {
        float a = Vector3.Dot(d, d), b = 2 * Vector3.Dot(o, d), c = Vector3.Dot(o, o) - r * r, disc = b * b - 4 * a * c;
        if (disc < 0) return null;
        float t = (-b - MathF.Sqrt(disc)) / (2 * a);
        return t > 0 ? t : null;   // the camera inside the sphere: not picked (as boxes)
    }

    private static float? RayMesh(Vector3 o, Vector3 d, CollisionMeshData mesh)
    {
        float best = float.PositiveInfinity;
        var v = mesh.Vertices; var idx = mesh.Indices;
        for (int i = 0; i + 2 < idx.Length; i += 3)
        {
            var a = v[idx[i]]; var e1 = v[idx[i + 1]] - a; var e2 = v[idx[i + 2]] - a;
            var p = Vector3.Cross(d, e2); float det = Vector3.Dot(e1, p);
            if (MathF.Abs(det) < 1e-9f) continue;
            var t0 = o - a; float u = Vector3.Dot(t0, p) / det; if (u < 0 || u > 1) continue;
            var q = Vector3.Cross(t0, e1); float w = Vector3.Dot(d, q) / det; if (w < 0 || u + w > 1) continue;
            float t = Vector3.Dot(e2, q) / det; if (t >= 0 && t < best) best = t;
        }
        return float.IsFinite(best) ? best : null;
    }

    // Collision actors whose position projects inside a screen rectangle (within range).
    private List<string> CollisionInRectangle(Vector2 min, Vector2 max, CameraFrame frame, Vector2 viewport)
    {
        var found = new List<string>(); var eye = Coordinates.ViewToGame(Camera.Eye); float range2 = CollisionRange * CollisionRange;
        var modified = Document.Project.Changes.Modified;
        bool Inside(Vector3 game) => ProjectView(Coordinates.GameToView(game), frame, viewport) is { } s && s.X >= min.X && s.Y >= min.Y && s.X <= max.X && s.Y <= max.Y;
        foreach (var sector in Document.ResidentSectors.Values)
            foreach (var actor in sector.CollisionActors)
            {
                if (Vector3.DistanceSquared(actor.Transform.Position, eye) > range2) continue;
                string id = ActorId(sector, actor);
                modified.TryGetValue(id, out var edit);
                if (edit?.Deleted != true && Inside((edit?.Transform ?? actor.Transform).Position)) found.Add(id);
            }
        foreach (var collision in Document.Project.Changes.Collisions.Values) if (Inside(collision.Transform.Position)) found.Add(collision.Id);
        return found;
    }

    // --- Collision commands ----------------------------------------------------------------------------

    public string? AddCollisionAt(CollisionShapeKind kind, Vector3 gamePosition)
    {
        var shape = kind switch
        {
            CollisionShapeKind.Sphere => new CollisionShape(kind, Vector3.Zero, Quaternion.Identity, new(.5f, 0, 0)),
            CollisionShapeKind.Capsule => new CollisionShape(kind, Vector3.Zero, Quaternion.Identity, new(.4f, .6f, 0)) { },
            _ => CollisionShape.Box(new(.5f))
        };
        // Boxes rest on the surface; capsules stand up (their axis is X: turned to Z).
        var rotation = kind == CollisionShapeKind.Capsule ? Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2) : Quaternion.Identity;
        float lift = kind == CollisionShapeKind.Capsule ? 1f : .5f;
        var collision = new AuthoredCollision(AuthoredCollision.NewId(), UniqueCollisionName(Loc.F("{0} collision", kind)), PlacementTransform.Identity with { Position = gamePosition + new Vector3(0, 0, lift), Rotation = rotation }, [shape]);
        string? id = null;
        if (Apply(() => id = Document.AddCollision(collision))) { Select(id); Status = Loc.F("{0} added. Move it with the gizmo; edit its shapes in the Collision tab.", collision.Name); }
        return id;
    }

    private string UniqueCollisionName(string name)
    {
        var used = Document.Project.Changes.Collisions.Values.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(name)) return name;
        for (int i = 2; ; i++) if (!used.Contains($"{name} {i}")) return $"{name} {i}";
    }

    // A box collision fitted to each selected mesh (its local bounds), attached to it.
    public int FitCollisionToSelection()
    {
        var created = new List<AuthoredCollision>();
        foreach (var item in SelectedObjects())
        {
            if (item.Deleted || item.IsCollision || item.Mesh is null || !_assets.TryGetValue(MeshKeyFor(item.Id, item.Mesh), out var mesh)) continue;
            var a = Coordinates.ViewToGame(mesh.Bounds.Min); var b = Coordinates.ViewToGame(mesh.Bounds.Max);
            var lo = Vector3.Min(a, b); var hi = Vector3.Max(a, b);
            var half = Vector3.Max((hi - lo) / 2, new(.02f));
            created.Add(new AuthoredCollision(AuthoredCollision.NewId(), UniqueCollisionName(Loc.F("{0} collision", item.Name)), item.Transform, [CollisionShape.Box(half, (hi + lo) / 2)]) { AttachedTo = item.Id });
        }
        if (created.Count == 0) { Status = Loc.T("Select meshes (loaded) to fit a collision box to them."); return 0; }
        var ids = new List<string>();
        foreach (var collision in created) if (Apply(() => ids.Add(Document.AddCollision(collision, Loc.T("Fit collision"))))) { }
        SelectMany(ids);
        Status = ids.Count == 1 ? Loc.T("Collision box fitted; it follows its mesh.") : Loc.F("{0} collision boxes fitted; each follows its mesh.", ids.Count);
        return ids.Count;
    }

    public string? MakeCollisionEditable(string id)
    {
        string? created = null;
        if (Apply(() => created = Document.ReplaceCollisionWithCopy(id))) { Select(created); Status = Loc.T("This collision is now a project collision: resize, reshape and restyle it (Ctrl+Z restores the game one)."); }
        return created;
    }

    public void SetCollisionShapeEnabled(string id, int shape, bool enabled)
    {
        var current = Document.Project.Changes.Modified.GetValueOrDefault(id)?.DisabledShapes ?? [];
        var next = enabled ? current.Where(s => s != shape) : current.Append(shape);
        if (Apply(() => Document.SetDisabledShapes(id, next))) Status = enabled ? Loc.T("Shape switched back on.") : Loc.T("Shape switched off (it no longer blocks anything in the game).");
    }

    public bool UpdateCollision(AuthoredCollision collision, string name = "Edit collision")
    {
        name = Loc.T(name);
        try { collision = collision.Validated(); }
        catch (InvalidDataException ex) { Error = ex.Message; return false; }
        return Apply(() => Document.UpdateCollision(collision, name));
    }

    // --- Occluders -----------------------------------------------------------------------------------

    private (int EyeX, int EyeY, int EyeZ, long Revision, long Selection, int Assets, float Range, bool On, int Heading, long Display) _occluderKey, _occluderBuildKey;
    private List<DebugLine> _occluderLines = [];
    private List<DebugTriangle> _occluderFills = [];
    private Task<(List<DebugLine> Lines, List<DebugTriangle> Fills, bool Capped)>? _occluderBuild;
    private readonly Dictionary<ulong, MeshAsset?> _occluderMeshes = [];
    private readonly ConcurrentQueue<(ulong Hash, MeshAsset? Mesh)> _occluderArrivals = new();
    private readonly HashSet<ulong> _occluderPending = [];
    private int _occluderVersion;

    public static Vector4 OccluderColor(string type) => type switch
    {
        "Exterior" => new(1f, .3f, .85f, 1), "MajorInterior" => new(.45f, .55f, 1f, 1), "MinorInterior" => new(.35f, .85f, 1f, 1),
        "Detail" => new(.45f, 1f, .55f, 1), "None" => new(.5f, .5f, .55f, 1), _ => new(.75f, .45f, 1f, 1)
    };

    private MeshAsset? OccluderMesh(ulong hash)
    {
        if (_assets.TryGetValue(hash, out var loaded)) return loaded;
        if (_occluderMeshes.TryGetValue(hash, out var mesh)) return mesh;
        if (Index is not { } index || !_occluderPending.Add(hash)) return null;
        _ = Task.Run(async () =>
        {
            MeshAsset? asset = null;
            try { asset = await index.ReadMeshConcurrentAsync(hash, _shutdown.Token); } catch (Exception) { }
            _occluderArrivals.Enqueue((hash, asset));
        });
        return null;
    }

    private void BuildOccluderOverlay()
    {
        bool any = false;
        while (_occluderArrivals.TryDequeue(out var arrival)) { _occluderMeshes[arrival.Hash] = arrival.Mesh; _occluderPending.Remove(arrival.Hash); any = true; }
        if (any) _occluderVersion++;
        bool on = ShowOccluders || Layer == EditLayer.Occlusion;
        if (!on)
        {
            _occluderKey = default; _renderer.SetStaticXray(1, null, 0); _renderer.SetStaticFill(2, null, 0);
            return;
        }
        var eye = Coordinates.ViewToGame(Camera.Eye);
        var key = ((int)MathF.Floor(eye.X / 2), (int)MathF.Floor(eye.Y / 2), (int)MathF.Floor(eye.Z / 2), Document.Revision, SelectionVersion, _occluderVersion + _assets.Count, OccluderRange, on, Heading(_occluderCapped), LayerDisplayVersion);
        if (_occluderBuild is { IsCompleted: true } done)
        {
            _occluderBuild = null;
            if (done.IsCompletedSuccessfully)
            {
                (_occluderLines, _occluderFills, _occluderCapped) = done.Result;
                _occluderKey = _occluderBuildKey; _occluderKey.Heading = Heading(_occluderCapped);
                _renderer.SetStaticXray(1, _occluderLines, ++_xrayVersion, dimmed: OccluderXray);
                _renderer.SetStaticFill(2, _occluderFills, ++_fillVersion);
            }
        }
        if (key != _occluderKey && _occluderBuild is null)
        {
            // Inputs gathered here (mesh loading is UI-thread state), geometry on the thread pool.
            float range2 = OccluderRange * OccluderRange;
            var forward = Coordinates.ViewToGame(Camera.Eye + Camera.Forward) - eye;
            var candidates = new List<(float Rank, ShapeMesh Mesh, PlacementTransform Instance, Vector4 Color)>();
            foreach (var item in OccluderObjects())
            {
                if (_selectionSet.Contains(item.Id)) continue;
                string type = OccluderType(item);
                if (HiddenOccluderTypes.Contains(type) || OccluderMesh(OccluderMeshHash(item)) is not { } mesh) continue;
                var shape = OccluderShape(mesh);
                foreach (var instance in OccluderInstances(item))
                    if (Vector3.DistanceSquared(instance.Position, eye) <= range2 && (MaxOccluderSize <= 0 || OccluderSize(shape, instance) <= MaxOccluderSize))
                        candidates.Add((Rank(instance.Position, eye, forward), shape, instance, OccluderColor(type)));
            }
            bool fade = FadeUnselected && Layer == EditLayer.Occlusion && _selectionSet.Count > 0;
            _occluderBuildKey = key;
            _occluderBuild = Task.Run(() =>
            {
                candidates.Sort((a, b) => a.Rank.CompareTo(b.Rank));
                var lines = new List<DebugLine>(); var fills = new List<DebugTriangle>();
                foreach (var (_, shape, instance, color) in candidates)
                {
                    if (lines.Count >= MaximumOccluderLines) return (lines, fills, true);
                    AddShape(lines, fills, shape, instance.GameMatrix, Dimmed(color, fade ? .3f : .9f), color, fade ? FadedAlpha : FillAlpha, eye);
                }
                return (lines, fills, false);
            });
        }
        foreach (var item in CachedSelection())
            if (!item.Deleted && IsOccluder(item))
                AddOccluder(_previews is not null && _previews.TryGetValue(item.Id, out var preview) ? item with { Transform = preview } : item, SelectedCollision, SelectedAlpha, eye);
        if (Layer == EditLayer.Occlusion && _hoverShape is { } hover && !_selectionSet.Contains(hover) && Document.TryGetObject(hover) is { Deleted: false } hovered && IsOccluder(hovered))
            AddOccluder(hovered, HoverOutline, HoverAlpha, eye);
    }

    private void AddOccluder(ScenePlacement item, Vector4 color, float alpha, Vector3 eye)
    {
        if (OccluderMesh(OccluderMeshHash(item)) is not { } mesh) return;
        var shape = OccluderShape(mesh);
        foreach (var instance in OccluderInstances(item)) AddShape(_renderer.OverlayLines, _renderer.OverlayFills, shape, instance.GameMatrix, color, color, alpha, eye);
    }

    private readonly ConditionalWeakTable<SectorSnapshot, SourcePlacement[]> _sectorOccluders = new();

    private static bool IsOccluder(ScenePlacement item) => item.Original?.OccluderNode == true || item.Element?.Properties.Kind == ElementKind.Occluder;

    // Game occluder nodes (resident, not deleted, moved where edited) and project occluders.
    private IEnumerable<ScenePlacement> OccluderObjects()
    {
        var modified = Document.Project.Changes.Modified;
        foreach (var sector in Document.ResidentSectors.Values)
            foreach (var p in _sectorOccluders.GetValue(sector, x => x.Placements.Where(p => p.OccluderNode).ToArray()))
            {
                modified.TryGetValue(p.Id, out var edit);
                if (edit?.Deleted == true) continue;
                yield return new(p.Id, p.Name, p.Mesh, edit?.Transform ?? p.Transform, p, false, false, p.Diagnostic);
            }
        foreach (var element in Document.Project.Changes.Elements.Values)
            if (element.Properties.Kind == ElementKind.Occluder) yield return new(element.Id, element.Name, null, element.Transform, null, false, false, null) { Element = element };
    }

    private ulong OccluderMeshHash(ScenePlacement item) => item.Mesh?.Hash ?? (item.Element is { } e ? WolvenKit.Common.FNV1A.FNV1A64HashAlgorithm.HashString(e.Properties.Resource) : 0);
    private string OccluderType(ScenePlacement item) => item.Element?.Properties.OccluderType ?? item.Original?.Occluder?.Type ?? item.Original?.Element?.OccluderType ?? "Default";

    // World transforms of an occluder's boxes (instanced nodes: one per instance).
    private static IEnumerable<PlacementTransform> OccluderInstances(ScenePlacement item)
        => item.Original?.InstanceOffsets is { IsDefaultOrEmpty: false } offsets ? offsets.Select(o => item.Transform.Apply(o)) : [item.Transform];

    // Nearest occluder under the cursor (game occluder nodes and project occluders).
    public string? PickOccluder(Vector2 pixel, Vector2 viewport)
    {
        if (_renderer.RenderedWorldCamera is not { } frame) return null;
        var ray = ScenePicking.ViewRay(pixel, viewport, frame.View, frame.Projection);
        var eye = Coordinates.ViewToGame(Camera.Eye); float range2 = OccluderRange * OccluderRange;
        string? best = null; float nearest = float.PositiveInfinity;
        foreach (var item in OccluderObjects())
        {
            if (HiddenOccluderTypes.Contains(OccluderType(item)) || OccluderMesh(OccluderMeshHash(item)) is not { } mesh) continue;
            foreach (var instance in OccluderInstances(item))
            {
                if (Vector3.DistanceSquared(instance.Position, eye) > range2 || MaxOccluderSize > 0 && OccluderSize(OccluderShape(mesh), instance) > MaxOccluderSize) continue;
                var model = instance.ViewMatrix * Matrix4x4.CreateTranslation(-frame.Origin);
                if (ScenePicking.Intersect(ray, mesh, model) is { } t && t < nearest) { nearest = t; best = item.Id; }
            }
        }
        return best;
    }

    private List<string> OccludersInRectangle(Vector2 min, Vector2 max, CameraFrame frame, Vector2 viewport)
    {
        var eye = Coordinates.ViewToGame(Camera.Eye); float range2 = OccluderRange * OccluderRange;
        return OccluderObjects().Where(o => OccluderInstances(o).Any(i => Vector3.DistanceSquared(i.Position, eye) <= range2
            && ProjectView(Coordinates.GameToView(i.Position), frame, viewport) is { } s && s.X >= min.X && s.Y >= min.Y && s.X <= max.X && s.Y <= max.Y)).Select(o => o.Id).ToList();
    }

    public string? AddOccluderAt(bool plane, Vector3 gamePosition)
    {
        var properties = ElementProperties.Preset(ElementKind.Occluder) with { Resource = plane ? ElementProperties.PlaneOccluder : ElementProperties.BoxOccluder };
        var scale = plane ? new Vector3(4, 1, 3) : new Vector3(4, 4, 3);
        var element = new AuthoredElement(AuthoredElement.NewId(), UniqueName(plane ? Loc.T("Plane occluder") : Loc.T("Box occluder")), properties, PlacementTransform.Identity with { Position = gamePosition + new Vector3(0, 0, plane ? 1.5f : 1.5f), Scale = scale });
        string? id = null;
        if (Apply(() => id = Document.AddElement(element))) { Select(id); ElementVersion++; Status = Loc.F("{0} added: scale it to the volume it should hide things behind.", element.Name); }
        return id;
    }

    // The occlusion settings of the selected mesh (a game mesh becomes a project copy of its node).
    public string? SetMeshOccluder(MeshOccluder occluder)
    {
        if (SelectedId is not { } id) return null;
        string? result = null;
        if (Apply(() => result = Document.SetOccluder(id, occluder)))
        {
            if (result != id) Select(result);
            Status = Loc.F("Mesh occlusion: {0}{1}", occluder.Type, (result != id ? Loc.T(" (the game mesh is now a project copy of its node; Ctrl+Z restores it).") : "."));
        }
        return result;
    }

    // An instanced occluder node becomes one project occluder per instance (then each is editable).
    public int ExpandInstancedOccluder(string id)
    {
        if (Document.TryGetObject(id) is not { Original: { OccluderNode: true, InstanceOffsets.IsDefaultOrEmpty: false } original } item || item.Deleted) return 0;
        string resource = original.Mesh?.Path ?? "";
        var settings = original.Occluder ?? new MeshOccluder("Default");
        var elements = original.InstanceOffsets.Select((offset, i) => new AuthoredElement(AuthoredElement.NewId(), $"{original.Name} {i + 1}",
            ElementProperties.Preset(ElementKind.Occluder) with { Resource = resource, OccluderType = settings.Type, AutohideScale = settings.AutohideScale }, item.Transform.Apply(offset))).ToArray();
        IReadOnlyList<string> created = [];
        if (Apply(() => created = Document.ReplaceWithElements(id, elements))) { SelectMany(created); ElementVersion++; Status = Loc.F("{0} occluders created from the instanced node (Ctrl+Z restores it).", created.Count); }
        return created.Count;
    }
}
