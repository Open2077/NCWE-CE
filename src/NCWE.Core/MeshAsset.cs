// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace NCWE.Core;

[StructLayout(LayoutKind.Sequential)]
public readonly record struct MeshVertex(Vector3 Position, Vector3 Normal);

public readonly record struct Bounds3(Vector3 Min, Vector3 Max)
{
    [JsonIgnore] public Vector3 Center => Min * 0.5f + Max * 0.5f;
    [JsonIgnore] public Vector3 Size => Max - Min;
    [JsonIgnore] public float Radius => Math.Max(Size.Length() * 0.5f, 0.01f);
    public static Bounds3 From(IEnumerable<Vector3> points)
    {
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        foreach (var p in points)
        {
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z))
                throw new InvalidDataException("Mesh contains non-finite coordinates.");
            min = Vector3.Min(min, p); max = Vector3.Max(max, p);
        }
        if (!float.IsFinite(min.X)) throw new InvalidDataException("Mesh has no vertices.");
        return new(min, max);
    }
}

public sealed record MeshPart(string Name, uint LodMask, MeshVertex[] Vertices, uint[] Indices)
{
    // Texture coordinates (UV0), kept only for meshes drawn with baked surfaces (road pieces).
    public Vector2[]? Uv { get; init; }
    // The game draws this part from its front only (its back is culled): what the chunk's render
    // mask and its material's template say. False for generated geometry: both sides are drawn.
    public bool OneSided { get; init; }
    public long CpuBytes => Vertices.LongLength * 24 + Indices.LongLength * 4 + (Uv?.LongLength ?? 0) * 8;
    public void Validate()
    {
        if (Vertices.Length == 0 || Indices.Length == 0 || Indices.Length % 3 != 0)
            throw new InvalidDataException($"Invalid triangle data in {Name}.");
        // Tight loops: this runs for every streamed mesh part.
        uint count = (uint)Vertices.Length;
        foreach (var i in Indices) if (i >= count) throw new InvalidDataException($"Index outside vertex buffer in {Name}.");
        foreach (var v in Vertices)
        {
            if (!float.IsFinite(v.Normal.X) || !float.IsFinite(v.Normal.Y) || !float.IsFinite(v.Normal.Z))
                throw new InvalidDataException($"Non-finite normal in {Name}.");
            if (!float.IsFinite(v.Position.X) || !float.IsFinite(v.Position.Y) || !float.IsFinite(v.Position.Z))
                throw new InvalidDataException($"Non-finite position in {Name}.");
        }
    }
}

public sealed record MeshAsset(string ResourcePath, string SourceHash, MeshPart[] Parts, Bounds3 Bounds,
    string SourceArchive, double LoadMilliseconds)
{
    // What the mesh is to the game's renderer (CMesh.ObjectType, ERenderObjectType): decals that
    // paint one kind of object look at it (a road marking paints roads, not what stands on them).
    public byte ObjectType { get; init; }
    public const byte StaticObject = 0, TerrainObject = 1, RoadObject = 2;
    // The types the game moves (characters, the player, skinned things, vehicles, weapons,
    // enemies): "static" decals leave them alone, "dynamic" ones paint only them.
    public static bool IsDynamicObject(byte type) => type is >= 12 and <= 15 or 20 or 21 or 24 or 25 or >= 27;
    // Approximate flat color per part for each appearance (w = 1 when known, 0 otherwise).
    public IReadOnlyDictionary<string, Vector4[]>? AppearanceColors { get; init; }
    // Per part: a surface baked from its material (default appearance), sampled with Uv.
    public IReadOnlyList<SurfaceTexture?>? PartTextures { get; init; }
    // Sums over the parts, computed once per parts array: streaming budgets read them for every
    // wanted mesh, often per frame. Kept beside the record, so equality and copies are unchanged.
    // LodMasks is shared: callers must not change it.
    public long CpuBytes => Measured.GetValue(Parts, Measure).Bytes;
    public uint[] LodMasks => Measured.GetValue(Parts, Measure).Lods;
    public int TriangleCount => Measured.GetValue(Parts, Measure).Triangles;

    private sealed record Measures(long Bytes, uint[] Lods, int Triangles);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MeshPart[], Measures> Measured = new();
    private static Measures Measure(MeshPart[] parts)
    {
        long bytes = 0; int triangles = 0;
        foreach (var part in parts) { bytes += part.CpuBytes; triangles += part.Indices.Length / 3; }
        return new(bytes, parts.Select(p => p.LodMask).Distinct().Order().ToArray(), triangles);
    }
}

// An RGBA8 image in display (sRGB) encoding, baked from a mesh material. Key identifies the
// material and bake settings, so identical surfaces share one GPU texture.
public sealed record SurfaceTexture(string Key, int Width, int Height, byte[] Rgba);

public static class Coordinates
{
    // WolvenKit's mesh adapter already produces this Y-up representation.
    public static Vector3 GameToView(Vector3 p) => new(p.X, p.Z, -p.Y);
    public static Vector3 ViewToGame(Vector3 p) => new(p.X, -p.Z, p.Y);
}

public static class SyntheticMesh
{
    public static MeshAsset Cube()
    {
        List<MeshVertex> vertices = []; List<uint> indices = [];
        Vector3[] normals = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];
        foreach (var n in normals)
        {
            var a = Math.Abs(n.Y) > .5f ? Vector3.UnitX : Vector3.UnitY;
            var b = Vector3.Cross(n, a);
            uint offset = (uint)vertices.Count;
            foreach (var p in new[] { n-a-b, n+a-b, n+a+b, n-a+b }) vertices.Add(new(p, n));
            indices.AddRange([offset, offset+1, offset+2, offset, offset+2, offset+3]);
        }
        return new("Synthetic / cube", "synthetic-v1", [new("Cube", 1, vertices.ToArray(), indices.ToArray())],
            new(new(-1), new(1)), "Generated in code", 0);
    }
}

// Streamed geometry is long-lived and reference-free: large arrays go to the pinned object
// heap, which the GC never copies or compacts, instead of being promoted (copied) through
// gen0 and gen1 while streaming workers allocate. Small arrays stay ordinary.
public static class GeometryArrays
{
    public static T[] Allocate<T>(int length) where T : unmanaged
        => length >= 512 ? GC.AllocateUninitializedArray<T>(length, pinned: true) : new T[length];
}