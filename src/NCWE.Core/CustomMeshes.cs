// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Numerics;
using System.Text;

namespace NCWE.Core;

// Custom meshes: models imported from FBX, glTF, OBJ... into the user's library, placed like
// game props, and exported as real game resources (mesh, textures) inside the project's archive.
// Each lives under the depot folder ncwe\custom\<id>\ so its paths never collide with the game's.
public static class CustomMeshPaths
{
    public const string Root = @"ncwe\custom";
    public static string Folder(string id) => $@"{Root}\{id}";
    public static string Mesh(string id) => $@"{Root}\{id}\{id}.mesh";
    // One texture per material slot and channel: d (colour), n (normal), r (roughness), m (metalness), e (emissive).
    public static string Texture(string id, int material, char channel) => $@"{Root}\{id}\textures\m{material:00}_{channel}.xbm";
    public static bool IsCustom(string? path) => path?.StartsWith(Root + @"\", StringComparison.OrdinalIgnoreCase) == true;
    public static string? IdOf(string? path) => IsCustom(path) ? path!.Substring(Root.Length + 1).Split('\\')[0] : null;

    // A short, readable, unique id: the name in lowercase ascii plus a random suffix.
    public static string NewId(string name, string fallback = "mesh")
    {
        var slug = new StringBuilder();
        foreach (char c in name.Normalize(NormalizationForm.FormD).ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') slug.Append(c);
            else if ((c is ' ' or '-' or '_' or '.') && slug.Length > 0 && slug[^1] != '_') slug.Append('_');
            if (slug.Length >= 40) break;
        }
        string stem = slug.ToString().Trim('_');
        return (stem.Length == 0 ? fallback : stem) + "_" + Guid.NewGuid().ToString("N")[..6];
    }

    public static bool IsValidId(string id) => id.Length is > 0 and <= 64 && id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
}

public enum CustomUpAxis { Y, Z }
public enum CustomPivot { BottomCenter, Center, Original }

// How the source file was turned into the library mesh (kept to re-import with the same result).
public sealed record CustomImportSettings
{
    // Source units to metres (FBX in centimetres: 0.01).
    public float Scale { get; init; } = 1;
    public CustomUpAxis UpAxis { get; init; } = CustomUpAxis.Y;
    public CustomPivot Pivot { get; init; } = CustomPivot.BottomCenter;
    // Turn about the up axis, degrees.
    public float Yaw { get; init; }
    public bool ImportMaterials { get; init; } = true;
    // Largest texture side once converted (power of two).
    public int MaxTextureSize { get; init; } = 2048;
}

public enum CustomMaterialKind
{
    Standard,   // engine\materials\metal_base.remt: colour, roughness, metalness, normal, emissive
    Glass,      // base\materials\glass.mt
    Game,       // an existing game material (.mi / .mt) used as it is
    Multilayer, // engine\materials\multilayered.mt with a game multilayer setup and mask (road junctions)
    Instance    // any base material (.mt / .remt / .mi) with its parameters set (MaterialParameter)
}

// One material slot of a custom mesh. Textures are file names inside the item's textures folder.
public sealed record CustomMaterial(string Name)
{
    public CustomMaterialKind Kind { get; init; } = CustomMaterialKind.Standard;
    // CustomMaterialKind.Game: the depot path of the game material.
    public string? GameMaterial { get; init; }
    // CustomMaterialKind.Multilayer: the depot paths of the game's setup (.mlsetup) and mask (.mlmask).
    public string? MultilayerSetup { get; init; }
    public string? MultilayerMask { get; init; }
    // sRGB colour multiplied with the colour texture (or alone without one); alpha = opacity.
    public Vector4 BaseColor { get; init; } = Vector4.One;
    public string? BaseColorTexture { get; init; }
    public float Roughness { get; init; } = .6f;
    public string? RoughnessTexture { get; init; }
    // Channel of the roughness / metalness texture: 0 R, 1 G, 2 B, 3 A (glTF: roughness G, metalness B).
    public int RoughnessChannel { get; init; }
    public bool InvertRoughness { get; init; }   // the texture holds glossiness/smoothness
    public float Metalness { get; init; }
    public string? MetalnessTexture { get; init; }
    public int MetalnessChannel { get; init; }
    public string? NormalTexture { get; init; }
    public float NormalStrength { get; init; } = 1;
    public bool FlipNormalGreen { get; init; }   // DirectX-style normal map
    public Vector3 EmissiveColor { get; init; } = Vector3.Zero;
    public float EmissiveEV { get; init; }
    public string? EmissiveTexture { get; init; }
    // Cutout transparency from the colour alpha (below the threshold: not drawn).
    public bool AlphaCutout { get; init; }
    public float AlphaThreshold { get; init; } = .5f;
    public bool DoubleSided { get; init; }
    // Glass: tint and opacity.
    public float GlassOpacity { get; init; } = .25f;
    // CustomMaterialKind.Instance: the material it derives from and the values it sets.
    public string? BaseMaterial { get; init; }
    public ImmutableArray<MaterialParameter> Parameters { get; init; } = [];

    public IEnumerable<string> TextureFiles() =>
        new[] { BaseColorTexture, RoughnessTexture, MetalnessTexture, NormalTexture, EmissiveTexture }.OfType<string>()
            .Concat(Kind == CustomMaterialKind.Instance && !Parameters.IsDefault ? Parameters.Select(p => p.File).OfType<string>() : []);
}

// A library item: everything but the geometry and texture files beside it.
public sealed record CustomMeshInfo(string Id, string Name)
{
    public int Version { get; init; } = 1;
    public string SourceFile { get; init; } = "";
    // The model inside the item's source folder (relative path), for re-imports.
    public string SourceModel { get; init; } = "";
    public DateTimeOffset Created { get; init; }
    public DateTimeOffset Modified { get; init; }
    public CustomImportSettings Import { get; init; } = new();
    public ImmutableArray<CustomMaterial> Materials { get; init; } = [];
    // View-space (Y up) bounds of the converted geometry, metres.
    public Bounds3 Bounds { get; init; }
    public int Vertices { get; init; }
    public int Triangles { get; init; }
    public int Submeshes { get; init; }
    public ImmutableArray<string> Warnings { get; init; } = [];
    public string Credits { get; init; } = "";

    public string MeshPath => CustomMeshPaths.Mesh(Id);
    public ulong Hash => Fnv1a64(MeshPath);

    // The game's resource-path hash (FNV-1a 64 of the lowercase path).
    public static ulong Fnv1a64(string path)
    {
        ulong hash = 14695981039346656037UL;
        foreach (char c in path.ToLowerInvariant()) { hash ^= c; hash *= 1099511628211UL; }
        return hash;
    }
}

// Geometry of a custom mesh, in view space (Y up, glTF conventions: counter-clockwise front
// faces, V down from the top of the texture), already scaled, turned and pivoted.
public sealed record CustomSubmesh(int Material, Vector3[] Positions, Vector3[] Normals, Vector4[] Tangents, Vector2[] Uv, uint[] Indices)
{
    public int TriangleCount => Indices.Length / 3;
}

public sealed record CustomGeometry(ImmutableArray<CustomSubmesh> Submeshes)
{
    public Bounds3 Bounds => Bounds3.From(Submeshes.SelectMany(s => s.Positions));

    public void Write(Stream stream)
    {
        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        w.Write(0x4F45434E); w.Write(1); w.Write(Submeshes.Length);   // "NCEO", version
        foreach (var s in Submeshes)
        {
            w.Write(s.Material); w.Write(s.Positions.Length); w.Write(s.Indices.Length);
            foreach (var p in s.Positions) { w.Write(p.X); w.Write(p.Y); w.Write(p.Z); }
            foreach (var n in s.Normals) { w.Write(n.X); w.Write(n.Y); w.Write(n.Z); }
            foreach (var t in s.Tangents) { w.Write(t.X); w.Write(t.Y); w.Write(t.Z); w.Write(t.W); }
            foreach (var uv in s.Uv) { w.Write(uv.X); w.Write(uv.Y); }
            foreach (var i in s.Indices) w.Write(i);
        }
    }

    public static CustomGeometry Read(Stream stream)
    {
        using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (r.ReadInt32() != 0x4F45434E || r.ReadInt32() != 1) throw new InvalidDataException("Not an NCWE custom geometry file.");
        int count = r.ReadInt32();
        if (count is < 0 or > 4096) throw new InvalidDataException("Invalid submesh count.");
        var submeshes = ImmutableArray.CreateBuilder<CustomSubmesh>(count);
        for (int k = 0; k < count; k++)
        {
            int material = r.ReadInt32(), vertices = r.ReadInt32(), indices = r.ReadInt32();
            if (vertices is < 0 or > 10_000_000 || indices is < 0 or > 60_000_000) throw new InvalidDataException("Invalid submesh size.");
            var p = new Vector3[vertices]; var n = new Vector3[vertices]; var t = new Vector4[vertices]; var uv = new Vector2[vertices]; var ix = new uint[indices];
            for (int i = 0; i < vertices; i++) p[i] = new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            for (int i = 0; i < vertices; i++) n[i] = new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            for (int i = 0; i < vertices; i++) t[i] = new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            for (int i = 0; i < vertices; i++) uv[i] = new(r.ReadSingle(), r.ReadSingle());
            for (int i = 0; i < indices; i++) { ix[i] = r.ReadUInt32(); if (ix[i] >= vertices) throw new InvalidDataException("Index out of range."); }
            submeshes.Add(new(material, p, n, t, uv, ix));
        }
        return new(submeshes.MoveToImmutable());
    }
}
