// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Numerics;
using NCWE.Core;
using NCWE.Formats;
using NCWE.Render;

namespace NCWE.Studio.Session;

// Scripted world building (automation API, MCP server). Everything here speaks game space:
// metres, Z up; rotations as yaw/pitch/roll degrees (yaw about +Z, counter-clockwise seen
// from above, 0 = facing +Y). Batch edits are one undo step each.
internal sealed partial class StudioSession
{
    // --- Rays and ground ------------------------------------------------------------------------

    // Nearest loaded geometry along a game-space ray (only what is streamed around the camera).
    // frontOnly: as the view draws faces (what a pixel shows); otherwise a ray hits a one-sided part from either side.
    public (float Distance, string Id)? RaycastGame(Vector3 origin, Vector3 direction, float maximum, IReadOnlySet<string>? ignore = null, bool frontOnly = false)
    {
        if (!float.IsFinite(direction.LengthSquared()) || direction.LengthSquared() < 1e-12f) throw new InvalidDataException("A ray needs a direction.");
        var ray = new Ray3(Coordinates.GameToView(origin), Coordinates.GameToView(Vector3.Normalize(direction)));
        return Grid.RaycastHit(ray, maximum, ignoreIds: ignore, frontOnly: frontOnly);
    }

    // Height of the first surface below a game-space point (probing from `above` metres over it).
    public (float Z, string Id)? GroundBelow(Vector3 point, float above = 2, float depth = 600, IReadOnlySet<string>? ignore = null)
        => RaycastGame(point + new Vector3(0, 0, above), -Vector3.UnitZ, above + depth, ignore) is { } hit ? (point.Z + above - hit.Distance, hit.Id) : null;

    // --- Bounds ---------------------------------------------------------------------------------

    // Game-space box of a placement's loaded geometry (mesh or entity parts), or null.
    public Bounds3? GameBounds(ScenePlacement item)
    {
        Bounds3? view = null;
        var parts = item.Original?.EntityParts is { IsDefaultOrEmpty: false } own ? own : item.Element is { } element ? ElementParts(element) : default;
        if (!parts.IsDefaultOrEmpty)
        {
            Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
            foreach (var part in parts)
            {
                if (!_assets.TryGetValue(part.Mesh.Hash, out var partMesh)) continue;
                var b = item.Transform.Apply(part.Local).TransformBounds(partMesh.Bounds);
                min = Vector3.Min(min, b.Min); max = Vector3.Max(max, b.Max);
            }
            if (float.IsFinite(min.X)) view = new(min, max);
        }
        else if (item.Mesh is not null && _assets.TryGetValue(MeshKeyFor(item.Id, item.Mesh), out var mesh)) view = item.Transform.TransformBounds(mesh.Bounds);
        return view is { } v ? GameBox(v) : null;
    }

    internal static Bounds3 GameBox(Bounds3 view)
    {
        var a = Coordinates.ViewToGame(view.Min); var b = Coordinates.ViewToGame(view.Max);
        return new(Vector3.Min(a, b), Vector3.Max(a, b));
    }

    // --- Asset catalog and facts ----------------------------------------------------------------

    private Task<List<LibraryItem>>? _apiCatalog;
    private Task<string[]>? _apiPaths;
    public Task<List<LibraryItem>> AssetCatalog()
        => _apiCatalog ??= Index is { } index ? Task.Run(() => LibraryCatalog.Build(index)) : throw new InvalidOperationException("Open the game first.");
    // Every indexed resource path with a folder, sorted (folder browsing).
    public Task<string[]> AllAssetPaths()
        => _apiPaths ??= Index is { } index ? Task.Run(() => index.AllPaths().Where(p => p.Contains('\\')).Order(StringComparer.OrdinalIgnoreCase).ToArray()) : throw new InvalidOperationException("Open the game first.");

    public sealed record AssetFacts(string Path, string Kind, Vector3 Min, Vector3 Max, ImmutableArray<string> Appearances, string DefaultAppearance,
        int Parts, int Triangles, string EntityClass, DoorSettings? Door);
    private readonly Dictionary<(string, string), Task<AssetFacts>> _assetFacts = [];

    // Size and pivot of a mesh or entity template placed at the identity transform, its
    // appearances, and (entities) its class and door behaviour. Cached per path and appearance.
    public Task<AssetFacts> AssetFactsAsync(string path, string appearance = "")
    {
        var index = Index ?? throw new InvalidOperationException("Open the game first.");
        var key = (path.ToLowerInvariant(), appearance);
        if (_assetFacts.TryGetValue(key, out var known) && !known.IsFaulted) return known;
        ulong hash = WolvenKit.Common.FNV1A.FNV1A64HashAlgorithm.HashString(path);
        if (index.Find(hash) is null) throw new FileNotFoundException("Not in the game archives: " + path);
        var task = Task.Run(async () =>
        {
            if (path.EndsWith(".ent", StringComparison.OrdinalIgnoreCase))
            {
                var visual = index.ResolveEntity(hash, appearance);
                string look = visual?.Appearance ?? appearance;
                var mesh = await index.ReadEntityMeshAsync(hash, look, false, 0, _shutdown.Token);
                var box = mesh.Parts.Length == 0 ? new Bounds3(Vector3.Zero, Vector3.Zero) : GameBox(mesh.Bounds);
                return new AssetFacts(path, visual?.Door is not null ? "door" : "entity", box.Min, box.Max, visual?.Appearances ?? [], look,
                    mesh.Parts.Length, mesh.TriangleCount, visual?.EntityClass ?? "", visual?.Door);
            }
            if (path.EndsWith(".mesh", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".w2mesh", StringComparison.OrdinalIgnoreCase))
            {
                var mesh = await index.LoadMeshAsync(hash, _shutdown.Token);
                var box = GameBox(mesh.Bounds);
                ImmutableArray<string> looks = [];
                try { looks = index.DescribeMaterials(hash).Appearances.Select(a => a.Name).Where(n => n.Length > 0).ToImmutableArray(); } catch (Exception) { }
                return new AssetFacts(path, "mesh", box.Min, box.Max, looks, looks.FirstOrDefault() ?? "default", mesh.Parts.Length, mesh.TriangleCount, "", null);
            }
            return new AssetFacts(path, Path.GetExtension(path).TrimStart('.'), Vector3.Zero, Vector3.Zero, [], "", 0, 0, "", null);
        });
        _assetFacts[key] = task;
        return task;
    }

    // --- Region queries ---------------------------------------------------------------------------

    public sealed record RegionQuery(Vector3? Center, float Radius, Vector3? Min, Vector3? Max, IReadOnlySet<string>? Kinds, string[] Terms,
        bool Game, bool Project, bool IncludeDeleted, bool EditableOnly);

    public static string KindOf(ScenePlacement o)
    {
        if (o.AuthoredCollision is not null || o.Original?.IsCollisionActor == true) return "collision";
        if (o.Original?.OccluderNode == true) return o.Original.InstanceOffsets.IsDefaultOrEmpty ? "occluder" : "occluder_instanced";
        if (o.ElementProperties is { } e) return e.Kind.ToString().ToLowerInvariant();
        if (o.Original?.Entity is { } entity) return entity.IsDoor ? "door" : "entity";
        if (o.Original is { } original)
        {
            if (original.Source.NodeType == "worldInstancedMeshNode") return "instanced_mesh";
            if (ExportPreparation.IsBended(original.Source.NodeType)) return "road_piece";
            if (original.ProxyOwner != 0 || original.NearHideDistance > 0) return "proxy";
            if (original.Source.NodeType is "worldEntityNode" or "worldDeviceNode") return "entity";
        }
        if (o.Id.StartsWith("r:", StringComparison.Ordinal)) return "road";
        return o.Mesh is not null ? "mesh" : "other";
    }

    // Objects inside a sphere or box, nearest to the center first. Collision actors are included
    // when asked for ("collision" kind).
    public (int Total, List<ScenePlacement> Items) QueryRegion(RegionQuery q, int limit)
    {
        Vector3 center = q.Center ?? (q.Min is { } lo && q.Max is { } hi ? (lo + hi) / 2 : Coordinates.ViewToGame(Camera.Eye));
        float r2 = q.Radius * q.Radius;
        bool Inside(Vector3 p) => q.Min is { } a && q.Max is { } b
            ? p.X >= a.X && p.Y >= a.Y && p.Z >= a.Z && p.X <= b.X && p.Y <= b.Y && p.Z <= b.Z
            : Vector3.DistanceSquared(p, center) <= r2;
        bool Matches(ScenePlacement o)
        {
            if (o.Deleted && !q.IncludeDeleted) return false;
            if (q.EditableOnly && !o.CanEdit) return false;
            bool project = o.Original is null;
            if (project ? !q.Project : !q.Game) return false;
            if (q.Kinds is { Count: > 0 } kinds && !kinds.Contains(KindOf(o))) return false;
            if (q.Terms.Length == 0) return true;
            string text = (o.Name + " " + (o.Mesh?.Path ?? o.ElementProperties?.Resource ?? "") + " " + (o.Original?.Source.NodeType ?? "")).ToLowerInvariant();
            return q.Terms.All(text.Contains);
        }
        var found = new List<ScenePlacement>();
        foreach (var o in Document.Objects(q.IncludeDeleted))
            if (Inside(o.Transform.Position) && Matches(o)) found.Add(o);
        if (q.Kinds?.Contains("collision") == true && q.Game)
            foreach (var sector in Document.ResidentSectors.Values)
                foreach (var actor in sector.CollisionActors)
                    if (Inside(actor.Transform.Position) && Document.TryGetObject(ActorId(sector, actor)) is { } item && Inside(item.Transform.Position) && Matches(item)) found.Add(item);
        found.Sort((a, b) => Vector3.DistanceSquared(a.Transform.Position, center).CompareTo(Vector3.DistanceSquared(b.Transform.Position, center)));
        return (found.Count, found.Count > limit ? found.GetRange(0, limit) : found);
    }

    // --- Batch building -----------------------------------------------------------------------------

    public IReadOnlyList<string>? AddBatch(IReadOnlyList<AuthoredPlacement> objects, IReadOnlyList<AuthoredElement> elements, IReadOnlyList<AuthoredCollision> collisions, string name, bool select = true,
        IReadOnlyCollection<ProjectMaterial>? materials = null)
    {
        IReadOnlyList<string> created = [];
        if (!Apply(() => created = Document.AddBatch(objects, elements, collisions, materials ?? [], name))) return null;
        foreach (var element in elements)
            if (element.Properties.Kind is ElementKind.Door or ElementKind.Entity) DoorVisual(element.Properties.Resource, element.Properties.Appearance);
        if (elements.Count != 0) ElementVersion++;
        if (select && created.Count != 0) SelectMany(created);
        _nextPlan = 0;
        Status = created.Count == 1 ? Loc.T("1 object added.") : Loc.F("{0} objects added.", created.Count);
        return created;
    }

    public bool TransformBatch(IReadOnlyDictionary<string, PlacementTransform> transforms)
    {
        _previews = null;
        return transforms.Count != 0 && !RefuseRemoteLocked(transforms.Keys) && Apply(() => Document.TransformMany(transforms));
    }

    public bool DeleteBatch(IEnumerable<string> ids)
    {
        var targets = ids.Distinct().ToArray();
        if (targets.Length == 0 || RefuseRemoteLocked(targets) || !Apply(() => Document.DeleteMany(targets))) return false;
        if (targets.Any(_selectionSet.Contains)) Select(null);
        return true;
    }

    public IReadOnlyList<string>? DuplicateBatch(IEnumerable<string> ids)
    {
        IReadOnlyList<string> created = [];
        var targets = ids.Distinct().ToArray();
        return targets.Length != 0 && Apply(() => created = Document.DuplicateMany(targets)) ? created : null;
    }

    public bool ApplyDocument(Action action) => Apply(action);

    // Collaboration: what someone else is editing (their selection holds it) is refused to scripts too.
    private bool RefuseRemoteLocked(IEnumerable<string> ids)
    {
        if (RemoteLockOwner is not { } owner) return false;
        foreach (var id in ids)
            if (owner(id) is { } who) { Error = Loc.F("{0} is editing {1}: wait until they select something else.", who, id); return true; }
        return false;
    }

    // --- Prefabs ------------------------------------------------------------------------------------

    // Objects relative to a pivot: meshes (game or project), elements (game or project, sounds
    // excluded: their audio settings stay with the game node), entities and collisions.
    // Materials: the project materials its meshes wear (added to the project placing it, unless it
    // has them already).
    public sealed record Prefab(int Version, string Name, ImmutableArray<PrefabItem> Items)
    {
        public ImmutableArray<ProjectMaterial> Materials { get; init; } = [];
    }
    public sealed record PrefabItem(string Kind, string Name, PlacementTransform Transform)
    {
        public MeshReference? Mesh { get; init; }
        public ElementProperties? Element { get; init; }
        public ImmutableArray<CollisionShape> Shapes { get; init; } = [];
        public ulong CookedSector { get; init; }
        public MeshOccluder? Occluder { get; init; }
        public ImmutableArray<SlotMaterial> Materials { get; init; } = [];
    }

    public (Prefab Prefab, List<string> Skipped) CapturePrefab(IEnumerable<string> ids, string name, Vector3? pivot)
    {
        var items = ids.Distinct().Select(Document.TryGetObject).OfType<ScenePlacement>().Where(o => !o.Deleted).ToList();
        if (items.Count == 0) throw new InvalidOperationException("Nothing to capture: none of these objects is loaded.");
        var at = pivot ?? new Vector3(items.Average(o => o.Transform.Position.X), items.Average(o => o.Transform.Position.Y), items.Min(o => o.Transform.Position.Z));
        Matrix4x4.Invert(Matrix4x4.CreateTranslation(at), out var inverse);
        PlacementTransform Local(PlacementTransform t)
        {
            try { return PlacementTransform.FromGameMatrix(t.GameMatrix * inverse); }
            catch (InvalidDataException) { return t with { Position = t.Position - at }; }
        }
        var result = new List<PrefabItem>(); var skipped = new List<string>();
        foreach (var o in items)
        {
            var local = Local(o.Transform);
            if (o.IsCollision)
            {
                var shapes = o.CollisionShapes;
                if (o.Original is not null && Document.Project.Changes.Modified.GetValueOrDefault(o.Id)?.DisabledShapes is { IsDefaultOrEmpty: false } off)
                    shapes = shapes.Where((_, i) => !off.Contains(i)).ToImmutableArray();
                ulong cooked = o.AuthoredCollision?.CookedSector ?? o.Original?.Collision?.CookedSector ?? 0;
                if (shapes.IsDefaultOrEmpty) { skipped.Add(Loc.F("{0} (no shape)", o.Id)); continue; }
                result.Add(new("collision", o.Name, local) { Shapes = shapes, CookedSector = shapes.Any(s => s.IsMesh) ? cooked : 0 });
            }
            else if (o.ElementProperties is { } properties)
            {
                if (properties.Kind == ElementKind.Sound) { skipped.Add(Loc.F("{0} (sound emitters stay with their game node)", o.Id)); continue; }
                result.Add(new("element", o.Name, local) { Element = properties });
            }
            else if (o.Original?.Entity is { } entity)
                result.Add(new("element", o.Name, local) { Element = ElementProperties.Preset(entity.IsDoor ? ElementKind.Door : ElementKind.Entity) with { Resource = entity.Template, Appearance = entity.Appearance, Door = entity.Door } });
            else if (o.Mesh is not null)
                result.Add(new("mesh", o.Name, local) { Mesh = o.Mesh, Occluder = o.Added?.Occluder, Materials = o.Added?.Materials is { IsDefaultOrEmpty: false } worn ? worn : [] });
            else skipped.Add(o.Id + " (" + KindOf(o) + ")");
        }
        if (result.Count == 0) throw new InvalidOperationException("Nothing in this selection can be part of a prefab.");
        var materials = result.SelectMany(i => i.Materials).Select(s => s.Material).Distinct(StringComparer.Ordinal)
            .Select(id => Document.Materials.GetValueOrDefault(id)).OfType<ProjectMaterial>().ToImmutableArray();
        return (new Prefab(1, name, result.ToImmutableArray()) { Materials = materials }, skipped);
    }

    public IReadOnlyList<string>? PlacePrefab(Prefab prefab, PlacementTransform at, bool select = true) => PlacePrefabs(prefab, [at], Loc.F("Place prefab {0}", prefab.Name), select);

    // Several copies of a prefab in one undo step.
    public IReadOnlyList<string>? PlacePrefabs(Prefab prefab, IReadOnlyList<PlacementTransform> places, string undoName, bool select = true)
    {
        var objects = new List<AuthoredPlacement>(); var elements = new List<AuthoredElement>(); var collisions = new List<AuthoredCollision>();
        var materials = prefab.Materials.IsDefault ? [] : prefab.Materials;
        // Element and collision names stay unique, also between the copies of one batch.
        var elementNames = Elements.Values.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var collisionNames = Document.Project.Changes.Collisions.Values.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        static string Unique(string name, HashSet<string> used)
        {
            string result = name;
            for (int i = 2; used.Contains(result); i++) result = $"{name} {i}";
            used.Add(result);
            return result;
        }
        foreach (var at in places)
        foreach (var item in prefab.Items)
        {
            var t = at.Apply(item.Transform);
            switch (item.Kind)
            {
                case "mesh" when item.Mesh is { } mesh:
                    objects.Add(new AuthoredPlacement(EditorDocument.NewObjectId(), item.Name, mesh, t) { Occluder = item.Occluder, Materials = Worn(item.Materials) }); break;
                case "element" when item.Element is { } properties:
                    elements.Add(new AuthoredElement(AuthoredElement.NewId(), Unique(item.Name, elementNames), properties, t)); break;
                case "collision" when !item.Shapes.IsDefaultOrEmpty:
                    collisions.Add(new AuthoredCollision(AuthoredCollision.NewId(), Unique(item.Name, collisionNames), t, item.Shapes) { CookedSector = item.CookedSector }); break;
                default: throw new InvalidDataException("Unknown prefab item: " + item.Kind);
            }
        }
        return AddBatch(objects, elements, collisions, undoName, select, materials);

        // The slots wearing a material the prefab or the project has (an older prefab's may be gone).
        ImmutableArray<SlotMaterial> Worn(ImmutableArray<SlotMaterial> slots) => slots.IsDefaultOrEmpty ? []
            : [.. slots.Where(s => Document.Materials.ContainsKey(s.Material) || materials.Any(m => m.Id == s.Material))];
    }

    // --- Roads ---------------------------------------------------------------------------------------

    public bool EditRoad(string id, Func<RoadDefinition, RoadDefinition> change, string name)
    {
        if (!Roads.TryGetValue(id, out var road)) { Error = Loc.F("No such road: {0}", id); return false; }
        var changed = change(road);
        if (changed == road) return true;
        CommitRoad(changed, name);
        return Error.Length == 0;
    }

    // --- Camera -------------------------------------------------------------------------------------

    // heading: degrees about +Z, 0 = looking along +Y, 90 = along -X; pitch: degrees, up positive.
    public void SetCameraGame(Vector3 eye, float headingDegrees, float pitchDegrees)
    {
        Camera.Yaw = headingDegrees * MathF.PI / 180;
        Camera.Pitch = -Math.Clamp(pitchDegrees, -89.5f, 89.5f) * MathF.PI / 180;
        Camera.SetEye(Coordinates.GameToView(eye));
        _nextPlan = 0;
    }

    public (Vector3 Eye, float Heading, float Pitch, Vector3 Forward) CameraGame()
    {
        var forward = Coordinates.ViewToGame(Camera.Forward);
        return (Coordinates.ViewToGame(Camera.Eye), Camera.Yaw * 180 / MathF.PI, -Camera.Pitch * 180 / MathF.PI, forward);
    }

    public static (float Heading, float Pitch) HeadingTowards(Vector3 from, Vector3 to)
    {
        var d = to - from;
        if (d.LengthSquared() < 1e-8f) return (0, 0);
        d = Vector3.Normalize(d);
        return (MathF.Atan2(-d.X, d.Y) * 180 / MathF.PI, MathF.Asin(Math.Clamp(d.Z, -1, 1)) * 180 / MathF.PI);
    }
}
