// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json.Serialization;

namespace NCWE.Core.MeshEditing;

// Vertices or triangles of one part (by index in the mesh as the operations before left it).
public readonly record struct PartIndices(int Part, ImmutableArray<int> Indices);

// An edited mesh: a game mesh and the operations made on it, in order. The result is replayed from
// the game's geometry (LOD 0 chunks), so the project stores the operations, not the vertices.
// Operations work in the mesh's own space (the editor's Y-up view space): moving the object
// afterwards keeps the edit. Placements draw it through EditedMeshes.DrawKey(Id).
public sealed record MeshEditDefinition(string Id, string Name, MeshReference Source)
{
    // The archive entry the operations were made on (its SHA-1; null when unknown).
    public string? SourceFingerprint { get; init; }
    public ImmutableArray<MeshEditOp> Ops { get; init; } = [];
    // Applied to every LOD chunk (the game's distant LODs then show the edit too); false keeps LOD 0 only.
    public bool AllLods { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(CutBoxOp), "cut")]
[JsonDerivedType(typeof(TransformVerticesOp), "transform")]
[JsonDerivedType(typeof(DeleteFacesOp), "delete")]
[JsonDerivedType(typeof(ExtrudeFacesOp), "extrude")]
[JsonDerivedType(typeof(SubdivideFacesOp), "subdivide")]
[JsonDerivedType(typeof(FlipFacesOp), "flip")]
[JsonDerivedType(typeof(NormalsOp), "normals")]
[JsonDerivedType(typeof(UvProjectOp), "uv-project")]
[JsonDerivedType(typeof(UvTransformOp), "uv-transform")]
[JsonDerivedType(typeof(UvMoveOp), "uv-move")]
[JsonDerivedType(typeof(MoveFacesToPartOp), "set-part")]
[JsonDerivedType(typeof(AddTrianglesOp), "add-triangles")]
[JsonDerivedType(typeof(TwoSidedOp), "two-sided")]
public abstract record MeshEditOp
{
    // A short description for undo steps and the edit's history.
    public abstract string Describe();
}

// Removes what lies inside an oriented box (centre, rotation, half sizes; box Z is the depth: the
// direction the opening goes through). Triangles crossing it are cut along its faces, so the
// opening has clean edges. Jambs: the four inner faces of the opening where the wall has a
// thickness (its front and back surfaces both cut), in the material of the most cut part.
public sealed record CutBoxOp(Vector3 Center, Quaternion Rotation, Vector3 HalfSize, bool Jambs = true) : MeshEditOp
{
    public override string Describe() => $"Cut an opening {HalfSize.X * 2:0.##} × {HalfSize.Y * 2:0.##} m";
}

// Moves, turns or scales vertices (a matrix in mesh space; normals and tangents follow).
public sealed record TransformVerticesOp(ImmutableArray<PartIndices> Vertices, Matrix4x4 Matrix) : MeshEditOp
{
    public override string Describe() => $"Move {Vertices.Sum(v => v.Indices.Length)} vertices";
}

public sealed record DeleteFacesOp(ImmutableArray<PartIndices> Faces) : MeshEditOp
{
    public override string Describe() => $"Delete {Faces.Sum(f => f.Indices.Length)} faces";
}

// Pushes faces out (or in, negative) along their normals or a direction, adding side walls.
public sealed record ExtrudeFacesOp(ImmutableArray<PartIndices> Faces, float Distance, Vector3? Direction = null) : MeshEditOp
{
    public override string Describe() => $"Extrude {Faces.Sum(f => f.Indices.Length)} faces by {Distance:0.###} m";
}

// Splits each face into four; neighbours are split where they meet them (no cracks).
public sealed record SubdivideFacesOp(ImmutableArray<PartIndices> Faces) : MeshEditOp
{
    public override string Describe() => $"Subdivide {Faces.Sum(f => f.Indices.Length)} faces";
}

public sealed record FlipFacesOp(ImmutableArray<PartIndices> Faces) : MeshEditOp
{
    public override string Describe() => $"Flip {Faces.Sum(f => f.Indices.Length)} faces";
}

// Smooth: normals averaged over the faces around each corner; flat: one normal per face.
public sealed record NormalsOp(ImmutableArray<PartIndices> Faces, bool Smooth) : MeshEditOp
{
    public override string Describe() => Smooth ? "Smooth normals" : "Flat normals";
}

// Planar UVs: uv = (dot(p - origin, axisU) * scale.X, dot(p - origin, axisV) * scale.Y) + offset.
public sealed record UvProjectOp(ImmutableArray<PartIndices> Faces, Vector3 Origin, Vector3 AxisU, Vector3 AxisV, Vector2 Scale, Vector2 Offset, int Channel = 0) : MeshEditOp
{
    public override string Describe() => "Project UVs";
}

// An affine change of the faces' UVs (offset, scale, rotation about a pivot, as a 3x2 matrix).
public sealed record UvTransformOp(ImmutableArray<PartIndices> Faces, Matrix3x2 Matrix, int Channel = 0) : MeshEditOp
{
    public override string Describe() => "Transform UVs";
}

// Moves UV vertices (the UV editor's drags).
public sealed record UvMoveOp(ImmutableArray<PartIndices> Vertices, Vector2 Delta, int Channel = 0) : MeshEditOp
{
    public override string Describe() => "Move UVs";
}

// Gives faces the material slot of another part.
public sealed record MoveFacesToPartOp(ImmutableArray<PartIndices> Faces, int Target) : MeshEditOp
{
    public override string Describe() => "Change the faces' material";
}

// New triangles (corner positions, three per triangle) in a part: flat normals in the given
// facing (or from the corners' order) and planar UVs at the part's texel density.
public sealed record AddTrianglesOp(int Part, ImmutableArray<Vector3> Corners, Vector3? Facing = null) : MeshEditOp
{
    public override string Describe() => $"Add {Corners.Length / 3} faces";
}

// The game draws a face from its front only: a wall made to be seen from one side vanishes from the
// other. Each face (or every face, All) gets a back: the same triangle wound the other way, its
// normal and tangent handedness turned over, so it shows from both sides.
public sealed record TwoSidedOp(ImmutableArray<PartIndices> Faces, bool All = false) : MeshEditOp
{
    public override string Describe() => All ? "Two-sided (every face)" : $"Two-sided: {Faces.Sum(f => f.Indices.Length)} faces";
}
