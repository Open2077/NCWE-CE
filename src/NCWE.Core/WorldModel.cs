// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json.Serialization;

namespace NCWE.Core;

// Persist RED4 world coordinates (meters, Z up). Mesh buffers are already Y up.
public readonly record struct PlacementTransform(Vector3 Position, Quaternion Rotation, Vector3 Scale)
{
    public static PlacementTransform Identity => new(Vector3.Zero, Quaternion.Identity, Vector3.One);
    private static readonly Matrix4x4 Basis = new(1,0,0,0, 0,0,-1,0, 0,1,0,0, 0,0,0,1);
    private static readonly Matrix4x4 InverseBasis = Matrix4x4.Transpose(Basis);
    [JsonIgnore] public Matrix4x4 GameMatrix => Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Position);
    [JsonIgnore] public Matrix4x4 ViewMatrix => InverseBasis * GameMatrix * Basis;
    // From the node's own axes (game axes, not a mesh buffer's) to view space: the box of a decal.
    [JsonIgnore] public Matrix4x4 BoxViewMatrix => GameMatrix * Basis;

    public PlacementTransform Validated()
    {
        static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
        if (!Finite(Position) || !Finite(Scale) || !float.IsFinite(Rotation.LengthSquared()) || Rotation.LengthSquared() < 1e-10f)
            throw new InvalidDataException("Transform contains invalid values.");
        if (Math.Abs(Scale.X) < 1e-5f || Math.Abs(Scale.Y) < 1e-5f || Math.Abs(Scale.Z) < 1e-5f)
            throw new InvalidDataException("Zero scale cannot be edited or exported.");
        return this with { Rotation = Quaternion.Normalize(Rotation) };
    }

    public static PlacementTransform FromViewMatrix(Matrix4x4 matrix)
    {
        var game = Basis * matrix * InverseBasis;
        if (!Matrix4x4.Decompose(game, out var scale, out var rotation, out var translation))
            throw new InvalidDataException("The transform contains shear or cannot be decomposed.");
        var result = new PlacementTransform(translation, rotation, scale).Validated();
        var reconstructed = result.GameMatrix;
        float[] a = [game.M11, game.M12, game.M13, game.M21, game.M22, game.M23, game.M31, game.M32, game.M33];
        float[] b = [reconstructed.M11, reconstructed.M12, reconstructed.M13, reconstructed.M21, reconstructed.M22, reconstructed.M23, reconstructed.M31, reconstructed.M32, reconstructed.M33];
        if (a.Zip(b).Any(v => Math.Abs(v.First - v.Second) > 1e-3f * Math.Max(1, Math.Abs(v.First))))
            throw new InvalidDataException("Sheared transforms are not supported.");
        return result;
    }

    public static PlacementTransform FromGameMatrix(Matrix4x4 game) => FromViewMatrix(InverseBasis * game * Basis);

    // A transform expressed relative to this one (destructible instances, foliage stances).
    public PlacementTransform Apply(PlacementTransform local) => new(Position + Vector3.Transform(local.Position * Scale, Rotation),
        Quaternion.Concatenate(local.Rotation, Rotation), Scale * local.Scale);

    public Bounds3 TransformBounds(Bounds3 meshBounds)
    {
        var matrix = ViewMatrix;
        return Bounds3.From(Enumerable.Range(0, 8).Select(i => Vector3.Transform(new Vector3(
            (i & 1) == 0 ? meshBounds.Min.X : meshBounds.Max.X,
            (i & 2) == 0 ? meshBounds.Min.Y : meshBounds.Max.Y,
            (i & 4) == 0 ? meshBounds.Min.Z : meshBounds.Max.Z), matrix)));
    }
}

public sealed record ResourceIdentity(ulong Hash, string Path, string Fingerprint);
public sealed record MeshReference(ulong Hash, string Path, string Appearance = "default");
public sealed record SourceAnchor(ResourceIdentity Sector, int NodeIndex, int PlacementIndex, int InstanceIndex,
    ulong PlacementId, string NodeType, int ExpectedNodes, int ExpectedInstances)
{
    // Formatted once per anchor: ids are read for every placement on hot paths (sector rebuilds,
    // links, picking), and formatting them on each read was most of the editor's garbage. A copy
    // made with `with` reuses the cache only while the values it was made from are the same.
    [JsonIgnore] public string ObjectId
    {
        get
        {
            var cache = _id;
            if (cache.Id is null || cache.Hash != Sector.Hash || cache.Node != NodeIndex || cache.Placement != PlacementIndex || cache.Instance != InstanceIndex)
                _id = cache = new(Sector.Hash, NodeIndex, PlacementIndex, InstanceIndex, $"s:{Sector.Hash:x16}:{NodeIndex}:{PlacementIndex}:{InstanceIndex}");
            return cache.Id!;
        }
    }
    private IdCache _id = IdCache.None;

    // Equal to every other cache: it never takes part in the anchor's equality or hash code.
    private sealed class IdCache(ulong hash, int node, int placement, int instance, string? id)
    {
        public static readonly IdCache None = new(0, 0, 0, 0, null);
        public ulong Hash { get; } = hash;
        public int Node { get; } = node;
        public int Placement { get; } = placement;
        public int Instance { get; } = instance;
        public string? Id { get; } = id;
        public override bool Equals(object? obj) => obj is IdCache;
        public override int GetHashCode() => 0;
    }
}
// NodeTransform: the whole node is moved or deleted through its placement setup (ArchiveXL
// node mutation/deletion); nothing inside the node (instances, light data) is rewritten.
// CollisionActor: one actor of a worldCollisionNode (InstanceIndex = actor index). The game
// cooks static mesh collision into these nodes, separately from the mesh; an actor is deleted
// together with the mesh placed at its exact transform (ArchiveXL actorDeletions).
public enum PlacementSupport { Unsupported, StaticMesh, InstancedMesh, NodeTransform, CollisionActor }

public static class NodeFamilies
{
    // Node types edited as a whole node (see PlacementSupport.NodeTransform).
    public static readonly System.Collections.Frozen.FrozenSet<string> WholeNode = System.Collections.Frozen.FrozenSet.ToFrozenSet(
    [
        "worldPhysicalDestructionNode", "worldInstancedDestructibleMeshNode", "worldMeshNode", "worldRotatingMeshNode", "worldDynamicMeshNode",
        "worldWaterPatchNode", "worldAdvertisementNode", "worldStaticLightNode", "worldStaticSoundEmitterNode", "worldStaticParticleNode",
        "worldEffectNode", "worldStaticDecalNode", "worldEntityNode", "worldDeviceNode", "worldEntityProxyMeshNode",
        "worldStaticOccluderMeshNode", "worldInstancedOccluderNode"
    ], StringComparer.Ordinal);
}
// A non-mesh node shown as a debug marker (light color/radius, sound radius, effect).
public sealed record DebugMarker(string Kind, Vector4 Color, float Radius)
{
    // Game-space oriented boxes (collision shapes); empty for point markers.
    public ImmutableArray<DebugBox> Boxes { get; init; } = [];
}
public readonly record struct DebugBox(Vector3 Center, Quaternion Rotation, Vector3 HalfExtents);

public sealed record SourcePlacement(SourceAnchor Source, string Name, MeshReference? Mesh,
    PlacementTransform Transform, PlacementSupport Support, string? Diagnostic = null)
{
    public int RangeIndex { get; init; }
    // The node's global reference (worldNodeData.QuestPrefabRefHash): set on nodes that quests,
    // scripts and proxies address (most entities and devices); 0 otherwise.
    public ulong QuestPrefabRef { get; init; }
    // A worldCollisionNode actor (CollisionActor placements): its shapes.
    public CollisionActorRecord? Collision { get; init; }
    // Mesh nodes: their occlusion settings. Occluder nodes (OccluderNode) are drawn only in the
    // occlusion view.
    public MeshOccluder? Occluder { get; init; }
    public bool OccluderNode { get; init; }
    // worldEntityProxyMeshNode: the global reference (QuestPrefabRef) of the entity it stands for
    // at a distance. The proxy and its entity move and delete together.
    public ulong ProxyOwner { get; init; }
    public float MaxStreamingDistance { get; init; }
    // Distant proxy representations hide when the camera is closer than this (0: never).
    public float NearHideDistance { get; init; }
    public DebugMarker? Marker { get; init; }
    // worldBendedMeshNode frames (game space, node-local); Mesh is then the template.
    public ImmutableArray<Matrix4x4> Deformation { get; init; } = [];
    // Destructible instances, relative to the node placement (empty: one copy at the placement).
    public ImmutableArray<PlacementTransform> InstanceOffsets { get; init; } = [];
    // Editable properties of lights, particles, effects, sounds and decals.
    public ElementProperties? Element { get; init; }
    // worldStaticDecalNode: what draws the decal (its box is the placement's transform).
    public DecalInfo? Decal { get; init; }
    // Entity nodes: template, appearance, door behaviour, and the meshes of the appearance.
    public EntityInfo? Entity { get; init; }
    public ImmutableArray<EntityPart> EntityParts { get; init; } = [];
    // Every mesh this placement draws (its own and its entity parts).
    public IEnumerable<MeshReference> Meshes()
    {
        if (Mesh is not null) yield return Mesh;
        foreach (var part in EntityParts) yield return part.Mesh;
    }
    [JsonIgnore] public string Id => Source.ObjectId;
    // Whole-node families include markers (lights, sounds, effects, decals) without a mesh.
    [JsonIgnore] public bool CanEdit => Support != PlacementSupport.Unsupported && (Mesh is not null || Support is PlacementSupport.NodeTransform or PlacementSupport.CollisionActor);
    [JsonIgnore] public bool IsCollisionActor => Support == PlacementSupport.CollisionActor;
}
// A projected decal as its node sets it: the material (resource hash of its .mi), the picture
// mirrored along its width or height, the distance beyond which the game stops drawing it, and
// its order among overlapping decals (higher over lower). The picture lies in the box's local
// XY plane, seen from +Z, and is projected along Z through the box.
// Stretch: the picture is also drawn on faces along the projection (smeared), not left off them.
public sealed record DecalInfo(ulong Material, bool FlipHorizontal, bool FlipVertical, float HideDistance, int Order, bool Stretch = false)
{
    // The least angle, in degrees, between a surface and the projection for the decal to paint
    // it (the node's NormalThreshold, 1 unless set: every surface turned towards the picture,
    // none turned away, so a decal on a wall does not show on the wall's other face). Negative:
    // the node switches the test off, and surfaces of any direction are painted.
    public float NormalThreshold { get; init; } = 1;
    // Which objects it paints (the node's DecalRenderMode): static ones, those of one render
    // object type (ObjectType: MeshAsset.ObjectType), dynamic ones (characters, vehicles: nothing
    // an editor of the static world shows), or all.
    public DecalObjects Objects { get; init; }
    public byte ObjectType { get; init; }
}
public enum DecalObjects : byte { Static, OfType, Dynamic, All }

// One actor of a worldCollisionNode, kept compact (a sector holds up to ~1,200): it becomes a
// placement (PlacementSupport.CollisionActor) only when an edit or a lookup needs one.
public sealed record CollisionActorRecord(int NodeIndex, int PlacementIndex, ulong PlacementId, int ActorIndex, int ActorCount, string Node,
    PlacementTransform Transform, ImmutableArray<CollisionShape> Shapes, ulong CookedSector);

public sealed record SectorSnapshot(ResourceIdentity Resource, int NodeCount, byte Level, string Category,
    ImmutableArray<SourcePlacement> Placements, ImmutableArray<string> Diagnostics)
{
    public ImmutableArray<CollisionActorRecord> CollisionActors { get; init; } = [];
    public static readonly System.Numerics.Vector4 CollisionColor = new(1f, .42f, .25f, 1);
    public SourcePlacement CollisionPlacement(CollisionActorRecord a) =>
        new(new SourceAnchor(Resource, a.NodeIndex, a.PlacementIndex, a.ActorIndex, a.PlacementId, "worldCollisionNode", NodeCount, a.ActorCount),
            $"{a.Node} / actor {a.ActorIndex}", null, a.Transform, PlacementSupport.CollisionActor) { Marker = new DebugMarker("Collision", CollisionColor, 0), Collision = a };
    // A placement or a collision actor of this sector, by object id.
    public SourcePlacement? Find(string id)
    {
        foreach (var p in Placements) if (p.Id == id) return p;
        foreach (var a in CollisionActors)
            if (id == new SourceAnchor(Resource, a.NodeIndex, a.PlacementIndex, a.ActorIndex, a.PlacementId, "worldCollisionNode", NodeCount, a.ActorCount).ObjectId) return CollisionPlacement(a);
        return null;
    }
    public ImmutableArray<int> RangeStarts { get; init; } = [];
    public int PersistentRangeIndex { get; init; } = -1;
    // Conservative per-placement allowance includes source metadata, prepared
    // render matrices/bounds, draw/picking records and batch references.
    [JsonIgnore] public long EstimatedBytes => 1024L + Placements.Sum(p => 1024L + 2L * (p.Name.Length + (p.Mesh?.Path.Length ?? 0) + (p.Diagnostic?.Length ?? 0)));
}
public sealed record PlacementEdit(SourceAnchor Source, PlacementTransform? Transform = null, bool Deleted = false)
{
    // Collision actors: shapes switched off (ArchiveXL [actor, shape] deletions).
    public ImmutableArray<int> DisabledShapes { get; init; } = [];
}
public sealed record AuthoredPlacement(string Id, string Name, MeshReference Mesh, PlacementTransform Transform)
{
    // A game mesh node copied at export (every property kept) that this object replaces.
    public SourceAnchor? Template { get; init; }
    public ImmutableArray<string> Replaces { get; init; } = [];
    // Project materials on some of its mesh's slots (ChangeSet.Materials; empty: the mesh's own).
    public ImmutableArray<SlotMaterial> Materials { get; init; } = [];

    // Occlusion settings written on the node (null: the template's, or the default).
    public MeshOccluder? Occluder { get; init; }
    // How far from its position the game streams the node in, metres (null: from its size).
    public float? StreamingDistance { get; init; }
    // Its mesh edited in place (vertices, faces, openings, UVs): the operations on Mesh, a game mesh.
    public MeshEditing.MeshEditDefinition? MeshEdit { get; init; }

    public const float MaxStreamingDistance = 10_000;
}
public sealed record ChangeSet(ImmutableDictionary<string, PlacementEdit> Modified, ImmutableDictionary<string, AuthoredPlacement> Added)
{
    public static ChangeSet Empty => new(ImmutableDictionary<string, PlacementEdit>.Empty, ImmutableDictionary<string, AuthoredPlacement>.Empty);
    // Roads authored or rebuilt in the editor (absent in older projects: empty).
    public ImmutableDictionary<string, RoadDefinition> Roads { get; init; } = ImmutableDictionary<string, RoadDefinition>.Empty;
    // Lights, effects, sounds and decals placed or edited in the editor.
    public ImmutableDictionary<string, AuthoredElement> Elements { get; init; } = ImmutableDictionary<string, AuthoredElement>.Empty;
    // Collision actors authored in the editor.
    public ImmutableDictionary<string, AuthoredCollision> Collisions { get; init; } = ImmutableDictionary<string, AuthoredCollision>.Empty;
    // Folders, colour tags and display names (organisation only; absent in older projects: empty).
    public ProjectOutliner Outliner { get; init; } = ProjectOutliner.Empty;
    // Traffic lanes edited, deleted or drawn (absent in older projects: empty).
    public ImmutableDictionary<string, TrafficLaneEdit> Traffic { get; init; } = ImmutableDictionary<string, TrafficLaneEdit>.Empty;
    // Elevators built in the editor (absent in older projects: empty).
    public ImmutableDictionary<string, ElevatorDefinition> Elevators { get; init; } = ImmutableDictionary<string, ElevatorDefinition>.Empty;
    // Terrain cells sculpted, cut or painted, by cell ("x_y"; absent in older projects: empty).
    public ImmutableDictionary<string, NCWE.Core.Terrain.TerrainCellData> Terrain { get; init; } = ImmutableDictionary<string, NCWE.Core.Terrain.TerrainCellData>.Empty;
    // Materials put on meshes, by id (ProjectMaterial; absent in older projects: empty).
    public ImmutableDictionary<string, ProjectMaterial> Materials { get; init; } = ImmutableDictionary<string, ProjectMaterial>.Empty;
    // Notes left in the 3D view for the agent, by id (ProjectNote; absent in older projects: empty).
    public ImmutableDictionary<string, ProjectNote> Notes { get; init; } = ImmutableDictionary<string, ProjectNote>.Empty;
}
public sealed record ProjectData(int SchemaVersion, Guid Id, string Name, string ArchiveProfile,
    ImmutableDictionary<ulong, ResourceIdentity> Sources, ChangeSet Changes)
{
    public static ProjectData Create(string name) => new(1, Guid.NewGuid(), name, "vanilla-content-v1", ImmutableDictionary<ulong, ResourceIdentity>.Empty, ChangeSet.Empty);
}
public sealed record ScenePlacement(string Id, string Name, MeshReference? Mesh, PlacementTransform Transform,
    SourcePlacement? Original, bool Deleted, bool SourceMismatch, string? Diagnostic)
{
    public bool CanEdit => !SourceMismatch && (Original is null || Original.CanEdit);
    public AuthoredElement? Element { get; init; }
    public AuthoredCollision? AuthoredCollision { get; init; }
    public AuthoredPlacement? Added { get; init; }
    // Properties of an element, authored or from a game node.
    public ElementProperties? ElementProperties => Element?.Properties ?? Original?.Element;
    // Collision (a vanilla actor or an authored collision) and its shapes.
    public bool IsCollision => AuthoredCollision is not null || Original?.IsCollisionActor == true;
    public ImmutableArray<CollisionShape> CollisionShapes => AuthoredCollision?.Shapes ?? Original?.Collision?.Shapes ?? [];
    // Occlusion settings of a mesh (an authored override first).
    public MeshOccluder? Occluder => Added?.Occluder ?? Original?.Occluder;
    // Project materials on its mesh's slots (a game mesh wears its own).
    public ImmutableArray<SlotMaterial> Materials => Added?.Materials is { IsDefault: false } m ? m : [];
}
