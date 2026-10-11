// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Numerics;

namespace NCWE.Core;

// Collision as the game cooks it (worldCollisionNode): actors at world transforms, each with
// shapes in actor space. Simple shapes carry their size; mesh shapes (convex, triangle) name a
// cooked PhysX mesh in the city geometry cache, grouped by the node's SectorHash.
public enum CollisionShapeKind { Box, Sphere, Capsule, ConvexMesh, TriangleMesh }

// Size: box half extents; sphere radius (X); capsule radius (X) and half length of its
// cylinder (Y), along the actor's X axis (PhysX convention).
public sealed record CollisionShape(CollisionShapeKind Kind, Vector3 Position, Quaternion Rotation, Vector3 Size)
{
    public ulong MeshHash { get; init; }
    public string Preset { get; init; } = CollisionPresets.Default;
    public string Material { get; init; } = "concrete.physmat";
    public bool IsMesh => Kind is CollisionShapeKind.ConvexMesh or CollisionShapeKind.TriangleMesh;

    public CollisionShape Validated()
    {
        static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
        if (!Finite(Position) || !Finite(Size) || !float.IsFinite(Rotation.LengthSquared()) || Rotation.LengthSquared() < 1e-10f) throw new InvalidDataException("Collision shape values must be finite.");
        if (IsMesh && MeshHash == 0) throw new InvalidDataException("A mesh collision shape needs its cooked mesh.");
        if (!IsMesh && (Size.X <= 0 || (Kind == CollisionShapeKind.Box && (Size.Y <= 0 || Size.Z <= 0)) || (Kind == CollisionShapeKind.Capsule && Size.Y < 0)))
            throw new InvalidDataException("Collision shape sizes must be positive.");
        if (Size.X > 2000 || Size.Y > 2000 || Size.Z > 2000) throw new InvalidDataException("Collision shapes are limited to 2 km.");
        if (string.IsNullOrWhiteSpace(Preset) && !IsMesh) throw new InvalidDataException("A collision shape needs a preset.");
        return this with { Rotation = Quaternion.Normalize(Rotation) };
    }

    // Shape-space box enclosing a simple shape (meshes use their cooked bounds instead).
    public Vector3 HalfExtents => Kind switch
    {
        CollisionShapeKind.Sphere => new(Size.X),
        CollisionShapeKind.Capsule => new(Size.Y + Size.X, Size.X, Size.X),
        _ => Size
    };

    public static CollisionShape Box(Vector3 halfExtents, Vector3 offset = default) => new(CollisionShapeKind.Box, offset, Quaternion.Identity, halfExtents);
}

public static class CollisionPresets
{
    public const string Default = "World Static";
    // The presets and materials shipping 2.31 collision uses (by frequency around Japantown).
    public static readonly ImmutableArray<string> Presets =
    [
        "World Static", "Simple Environment Collision", "Complex Environment Collision", "Player Blocker",
        "Block Player and Vehicles", "Vehicle Blocker", "World Dynamic", "Destructible", "Shooting", "Debris"
    ];
    public static readonly ImmutableArray<string> Materials =
    [
        "concrete.physmat", "metal.physmat", "metal_painted.physmat", "plaster.physmat", "plastic.physmat", "glass.physmat",
        "metal_techpiercable.physmat", "default_material.physmat", "metal_hollow.physmat", "metal_heavypiercable.physmat",
        "metal_transparent.physmat", "metal_catwalk.physmat", "plastic_electronics.physmat", "cardboard.physmat", "plexiglass.physmat",
        "wood.physmat", "asphalt.physmat", "stone.physmat", "grass.physmat", "sand.physmat", "dirt.physmat", "ceramic.physmat", "neon.physmat"
    ];
    public static readonly ImmutableArray<string> Descriptions =
    [
        "Blocks everything (walls, floors, props)", "Simplified world collision", "Detailed world collision", "Blocks the player only",
        "Blocks the player and vehicles", "Blocks vehicles only", "Movable objects", "Destructible objects", "Shot detection only", "Small debris"
    ];

    // Wire color by preset family (blockers stand out).
    public static Vector4 Color(string preset) => preset switch
    {
        "Player Blocker" or "Block Player and Vehicles" => new(1f, .3f, .3f, 1),
        "Vehicle Blocker" => new(.35f, .6f, 1f, 1),
        "World Dynamic" or "Destructible" or "Debris" => new(.95f, .85f, .3f, 1),
        "Shooting" => new(.75f, .45f, 1f, 1),
        "Complex Environment Collision" => new(.35f, .85f, .55f, 1),
        _ => new(1f, .55f, .25f, 1)
    };
}

// A collision authored in the editor: a new actor (simple shapes, or copies of vanilla mesh
// shapes that keep their cooked mesh group), written as a worldCollisionNode at export.
public sealed record AuthoredCollision(string Id, string Name, PlacementTransform Transform, ImmutableArray<CollisionShape> Shapes)
{
    // Vanilla actors this copy replaces (deleted while it exists).
    public ImmutableArray<string> Replaces { get; init; } = [];
    // A mesh placement the collision follows (moved and deleted with it).
    public string? AttachedTo { get; init; }
    // Geometry cache group of copied mesh shapes (the source node's SectorHash).
    public ulong CookedSector { get; init; }

    public static string NewId() => "c:" + Guid.NewGuid().ToString("N");

    public AuthoredCollision Validated()
    {
        if (!Id.StartsWith("c:", StringComparison.Ordinal) || Id.Length < 4) throw new InvalidDataException("A collision needs a c: identity.");
        if (string.IsNullOrWhiteSpace(Name)) throw new InvalidDataException("A collision needs a name.");
        if (Shapes.IsDefaultOrEmpty) throw new InvalidDataException("A collision needs at least one shape.");
        if (Shapes.Length > 64) throw new InvalidDataException("A collision is limited to 64 shapes.");
        var shapes = Shapes.Select(s => s.Validated()).ToImmutableArray();
        if (shapes.Any(s => s.IsMesh) && CookedSector == 0) throw new InvalidDataException("Copied mesh shapes need their geometry cache group.");
        return this with { Shapes = shapes, Transform = Transform.Validated(), Replaces = Replaces.IsDefault ? [] : Replaces };
    }
}

// Occlusion settings of a mesh node (visWorldOccluderType + autohide distance scale, 255 = default).
public sealed record MeshOccluder(string Type, int AutohideScale = 255)
{
    public static readonly ImmutableArray<string> Types = ["Default", "None", "Detail", "MinorInterior", "MajorInterior", "Exterior"];
    public static readonly ImmutableArray<string> Descriptions =
    [
        "Default: the mesh occludes what is behind it", "None: never hides anything", "Detail: small occluder", "Minor interior",
        "Major interior", "Exterior: building shell"
    ];
    public MeshOccluder Validated()
    {
        if (!Types.Contains(Type)) throw new InvalidDataException($"Unknown occluder type {Type}.");
        if (AutohideScale is < 0 or > 255) throw new InvalidDataException("The occluder autohide scale is 0 to 255.");
        return this;
    }
}
