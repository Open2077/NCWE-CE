// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;

namespace NCWE.Core.MeshEditing;

// One render chunk of a mesh being edited, in the mesh's own space (the editor's Y-up view space,
// as WolvenKit's mesh reader gives it). It carries every vertex attribute the game's static meshes
// have, so an edit writes all of them back: positions, normals, tangents, two UV sets and colours.
// Triangles index the vertices; a vertex on a UV seam exists once per side of the seam.
public sealed class EditablePart
{
    public string Name = "";
    // The source render chunk: its material slot in every appearance.
    public int Chunk;
    public uint LodMask = 1;
    // The game draws the chunk's front faces only (see MeshPart.OneSided).
    public bool OneSided;
    public List<Vector3> Positions = [], Normals = [];
    public List<Vector4>? Tangents;   // xyz direction, w handedness
    public List<Vector2> Uv0 = [];
    public List<Vector2>? Uv1;
    public List<Vector4>? Colors;
    public List<int> Indices = [];

    public int VertexCount => Positions.Count;
    public int TriangleCount => Indices.Count / 3;

    public EditablePart Clone() => new()
    {
        Name = Name, Chunk = Chunk, LodMask = LodMask, OneSided = OneSided,
        Positions = [.. Positions], Normals = [.. Normals], Tangents = Tangents is null ? null : [.. Tangents],
        Uv0 = [.. Uv0], Uv1 = Uv1 is null ? null : [.. Uv1], Colors = Colors is null ? null : [.. Colors], Indices = [.. Indices]
    };

    // A copy of a vertex (every attribute), at the end.
    public int AddCopy(int v)
    {
        Positions.Add(Positions[v]); Normals.Add(Normals[v]); Tangents?.Add(Tangents[v]);
        Uv0.Add(Uv0[v]); Uv1?.Add(Uv1[v]); Colors?.Add(Colors[v]);
        return Positions.Count - 1;
    }

    // A vertex between a and b (every attribute interpolated; directions normalized).
    public int AddLerp(int a, int b, float t)
    {
        Positions.Add(Vector3.Lerp(Positions[a], Positions[b], t));
        Normals.Add(SafeNormalize(Vector3.Lerp(Normals[a], Normals[b], t), Normals[a]));
        if (Tangents is not null)
        {
            var ta = Tangents[a]; var tb = Tangents[b];
            var xyz = SafeNormalize(Vector3.Lerp(new(ta.X, ta.Y, ta.Z), new(tb.X, tb.Y, tb.Z), t), new(ta.X, ta.Y, ta.Z));
            Tangents.Add(new(xyz, ta.W));
        }
        Uv0.Add(Vector2.Lerp(Uv0[a], Uv0[b], t)); Uv1?.Add(Vector2.Lerp(Uv1[a], Uv1[b], t));
        Colors?.Add(Vector4.Lerp(Colors[a], Colors[b], t));
        return Positions.Count - 1;
    }

    // A new vertex with its own position, normal, tangent and UV (other attributes from a model vertex).
    public int AddVertex(Vector3 position, Vector3 normal, Vector3 tangent, Vector2 uv, int model = -1)
    {
        Positions.Add(position); Normals.Add(normal);
        Tangents?.Add(new(tangent, model >= 0 ? Tangents[model].W : 1));
        Uv0.Add(uv);
        Uv1?.Add(model >= 0 ? Uv1[model] : uv);
        Colors?.Add(model >= 0 ? Colors[model] : Vector4.One);
        return Positions.Count - 1;
    }

    public (int A, int B, int C) Triangle(int face) => (Indices[face * 3], Indices[face * 3 + 1], Indices[face * 3 + 2]);

    // Twice the area of a triangle, and its geometric normal (from the index order).
    public Vector3 FaceCross(int face)
    {
        var (a, b, c) = Triangle(face);
        return Vector3.Cross(Positions[b] - Positions[a], Positions[c] - Positions[a]);
    }

    public Vector3 FaceCenter(int face)
    {
        var (a, b, c) = Triangle(face);
        return (Positions[a] + Positions[b] + Positions[c]) / 3;
    }

    // +1 when a triangle's index order turns around its vertex normals (cross(b - a, c - a) along
    // them), -1 the other way: the mesh's winding convention, read from its own faces.
    public int WindingSign()
    {
        double sum = 0;
        for (int f = 0; f < TriangleCount; f++)
        {
            var (a, b, c) = Triangle(f);
            sum += Vector3.Dot(FaceCross(f), Normals[a] + Normals[b] + Normals[c]);
        }
        return sum < 0 ? -1 : 1;
    }

    // UV units per metre on UV0 (from the triangles' areas); 1 for a mesh without UVs.
    public float TexelDensity()
    {
        double world = 0, uv = 0;
        for (int f = 0; f < TriangleCount; f++)
        {
            var (a, b, c) = Triangle(f);
            world += FaceCross(f).Length();
            var e1 = Uv0[b] - Uv0[a]; var e2 = Uv0[c] - Uv0[a];
            uv += Math.Abs(e1.X * e2.Y - e1.Y * e2.X);
        }
        return world > 1e-9 && uv > 1e-12 ? (float)Math.Sqrt(uv / world) : 1;
    }

    // A unit direction (up for a zero or broken vector).
    public static Vector3 Unit(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) ? SafeNormalize(v, Vector3.UnitY) : Vector3.UnitY;

    internal static Vector3 SafeNormalize(Vector3 v, Vector3 fallback)
    {
        float length = v.Length();
        return length > 1e-12f ? v / length : fallback;
    }
}

public sealed class EditableMesh
{
    public List<EditablePart> Parts = [];
    // The source mesh's render object type (MeshAsset.ObjectType): the edited copy keeps it.
    public byte ObjectType;

    public EditableMesh Clone() => new() { Parts = Parts.Select(p => p.Clone()).ToList(), ObjectType = ObjectType };
    public int VertexCount => Parts.Sum(p => p.VertexCount);
    public int TriangleCount => Parts.Sum(p => p.TriangleCount);

    public Bounds3 Bounds
    {
        get
        {
            var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
            foreach (var part in Parts)
                foreach (int v in part.Indices) { min = Vector3.Min(min, part.Positions[v]); max = Vector3.Max(max, part.Positions[v]); }
            return float.IsFinite(min.X) ? new(min, max) : new(Vector3.Zero, Vector3.Zero);
        }
    }

    // The mesh as the editor draws it: positions, normals and UV0 per part (unused vertices dropped).
    public MeshAsset ToAsset(string resourcePath, string sourceHash)
    {
        var parts = new List<MeshPart>();
        foreach (var part in Parts)
        {
            if (part.TriangleCount == 0) continue;
            var remap = new int[part.VertexCount]; Array.Fill(remap, -1);
            var vertices = new List<MeshVertex>(); var uvs = new List<Vector2>(); var indices = new uint[part.Indices.Count];
            for (int i = 0; i < indices.Length; i++)
            {
                int v = part.Indices[i];
                if (remap[v] < 0) { remap[v] = vertices.Count; vertices.Add(new(part.Positions[v], part.Normals[v])); uvs.Add(part.Uv0[v]); }
                indices[i] = (uint)remap[v];
            }
            parts.Add(new MeshPart(part.Name, part.LodMask, [.. vertices], indices) { Uv = [.. uvs], OneSided = part.OneSided });
        }
        if (parts.Count == 0) parts.Add(new MeshPart("empty", 1, [new(Vector3.Zero, Vector3.UnitY), new(Vector3.Zero, Vector3.UnitY), new(Vector3.Zero, Vector3.UnitY)], [0, 1, 2]));
        var bounds = Bounds;
        if (bounds.Min == bounds.Max) bounds = new(bounds.Min - new Vector3(.01f), bounds.Max + new Vector3(.01f));
        return new MeshAsset(resourcePath, sourceHash, [.. parts], bounds, "NCWE mesh edit", 0) { ObjectType = ObjectType };
    }
}
