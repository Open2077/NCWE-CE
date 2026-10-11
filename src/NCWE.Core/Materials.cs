// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Numerics;

namespace NCWE.Core;

// Game materials for the realistic view, read only for meshes drawn near the camera: what a
// mesh part looks like (its RED4 material template reduced to a kind and its parameters) and
// the texture coordinates to sample it. Texture fields are resource hashes (0: none). Colors
// are linear.
public enum SurfaceKind : byte
{
    Standard,     // metal_base family: base color, normal, roughness, metalness, emissive
    Multilayer,   // multilayered.mt: layers of shared surfaces blended through a mask
    Glass,        // glass, glass_onesided...: transparent, reflective
    Decal,        // mesh_decal*: alpha-blended over the surface below
    Emissive,     // neons, signs, screens: their color is light
    Foliage,      // speedtree: alpha-tested leaves
    Window,       // window_parallax_interior: reflective windows, rooms lit at night
    Water,        // water patches and the ocean
    Hidden,       // never seen in the game (invisible helpers, overlays)
    LightGradient // light_gradients.mt: a plane that lights what stands behind it (no surface of its own)
}

// The parameters of the game's light_gradients.mt, as its pixel stage reads them. The plane is
// not seen by itself: it adds a gradient of light (bottom to top color, by height above the
// ground position or by its round shape) onto the scene within a few metres behind it, and
// fades as the camera comes nearer than DistanceToSurface. Colors are linear.
public sealed record LightGradient(Vector3 Bottom, Vector3 Top, float AdditiveAlphaBlend, float LerpGradient, float Multiplier,
    float MinProximityAlpha, float MaxProximityAlpha, float GroundPosition, float TopPosition, float GradientDirection,
    float RoundGradientPosition, float RoundGradientScale, float DistanceToSurface)
{
    public const string MeshPath = @"base\fx\environment\lights_env\fx_light_gradient.mesh", CyberspaceMeshPath = @"base\fx\environment\lights_env\fx_light_gradient_cyberspace.mesh";
    public static readonly ulong MeshHash = CustomMeshInfo.Fnv1a64(MeshPath), CyberspaceMeshHash = CustomMeshInfo.Fnv1a64(CyberspaceMeshPath);
    // The game's meshes that wear nothing else: known before their materials are read, so that
    // they are never drawn as solid planes, however far.
    public static bool IsMesh(ulong hash) => hash == MeshHash || hash == CyberspaceMeshHash;
    public static bool IsMesh(string? path) => path is not null && (path.Equals(MeshPath, StringComparison.OrdinalIgnoreCase) || path.Equals(CyberspaceMeshPath, StringComparison.OrdinalIgnoreCase));
}

public sealed record SurfaceMaterial(string Key, SurfaceKind Kind)
{
    public ulong BaseColor { get; init; }
    public ulong Normal { get; init; }
    public ulong Roughness { get; init; }
    public ulong Metalness { get; init; }
    public ulong Emissive { get; init; }
    // Base color multiplier (rgb) and opacity (a).
    public Vector4 BaseColorScale { get; init; } = Vector4.One;
    // UV scale (xy) and offset (zw) of the textures.
    public Vector4 UvTransform { get; init; } = new(1, 1, 0, 0);
    public float RoughnessScale { get; init; } = 1;
    public float RoughnessBias { get; init; }
    public float MetalnessScale { get; init; } = 1;
    public float MetalnessBias { get; init; }
    public float NormalStrength { get; init; } = 1;
    // Alpha test threshold on the base color's alpha (0: opaque).
    public float AlphaThreshold { get; init; }
    public Vector3 EmissiveColor { get; init; }
    // Display intensity of the emissive color (0: none).
    public float EmissiveIntensity { get; init; }
    // Glass, water, decals: coverage; glass and water tint.
    public float Opacity { get; init; } = 1;
    public Vector3 Tint { get; init; } = Vector3.One;
    // Multilayered: the per-mesh mask (one layer per setup layer), the layers, the mesh's own
    // normal map and its UV scale (xy) / bias (zw).
    public ulong Mask { get; init; }
    public ImmutableArray<MaterialLayer> Layers { get; init; } = [];
    public ulong GlobalNormal { get; init; }
    public Vector4 GlobalNormalTransform { get; init; } = new(1, 1, 0, 0);
    // Screens: their picture is read top-down (w = 1), and an atlas of x columns by y rows plays
    // one frame at a time at z frames per second (x, y = 0: a single picture).
    public Vector4 Screen { get; init; }
    // Meshes without UVs whose material generates them from the position (terrain cells):
    // uv = game xy * scale (xy) + offset (zw). Zero: the mesh's own UV0.
    public Vector4 GeneratedUv { get; init; }
    // Standard surfaces over a low-resolution bake (bent road pieces, road proxies): Layers[0]
    // (the street asphalt) tiled in world space, DetailSize metres per UV unit, stands where the
    // bake shows DetailBase (its asphalt, linear); the bake's departures from it (markings,
    // curbs, sidewalks) brighten or tint it. 0: none.
    public float DetailSize { get; init; }
    public Vector3 DetailBase { get; init; }
    // Light gradients: their parameters (null for every other kind).
    public LightGradient? Gradient { get; init; }

    public bool Transparent => Kind is SurfaceKind.Glass or SurfaceKind.Decal or SurfaceKind.Water;
}

// One layer of a multilayered material: a shared surface (color, normal, roughness,
// metalness textures) tiled over the mesh and recolored. Levels map a texture value v to
// lerp(Out.y, Out.x, saturate((v - In.y) / (In.x - In.y))), as (In.x, In.y, Out.x, Out.y).
public sealed record MaterialLayer(ulong Color, ulong Normal, ulong Roughness, ulong Metalness, Vector3 ColorScale, float Tile, Vector2 Offset,
    float Opacity, float NormalStrength, Vector4 RoughLevels, Vector4 MetalLevels);

// Per drawn part (the same parts, in the same order, as the streamed MeshAsset): UV0 and
// the vertex count it was read for; per appearance, the material of each part (null: keep
// the flat color).
public sealed record MeshSurface(ulong Mesh, Vector2[]?[] Uvs, int[] VertexCounts, IReadOnlyDictionary<string, SurfaceMaterial?[]> Appearances)
{
    public SurfaceMaterial?[]? For(string? appearance)
        => Appearances.TryGetValue(appearance ?? "default", out var named) ? named
            : Appearances.TryGetValue("default", out var fallback) ? fallback : Appearances.Values.FirstOrDefault();
}

// The material of a projected decal (worldStaticDecalNode: the decal*.mt templates), reduced to
// what the view draws: a picture projected through the node's box onto what is inside it.
// Texture fields are resource hashes (0: none); Color is linear.
public sealed record DecalMaterial(string Key)
{
    // The picture: its color and, unless Mask or DiffuseAsMask says otherwise, its alpha.
    public ulong Diffuse { get; init; }
    // A separate opacity mask (its red channel), spanning the box.
    public ulong Mask { get; init; }
    // Recolors the picture: its red channel picks a color along the gradient (gradient-map recolor).
    public ulong Gradient { get; init; }
    public Vector3 Color { get; init; } = Vector3.One;
    public float Alpha { get; init; } = 1;
    // The picture is only an opacity mask (its red channel); the decal has the plain Color.
    public bool DiffuseAsMask { get; init; }
    // The part of the picture the decal shows: scale (xy) and offset (zw) in texture coordinates
    // (an atlas cell or sub-region; 1, 1, 0, 0: all of it).
    public Vector4 UvRect { get; init; } = new(1, 1, 0, 0);
    // > 0: the picture is a ground material (terrain decals: road lines, patches), shaped by the
    // mask and laid like the ground's own: this many times across a terrain cell (GroundCell
    // metres), wherever the box is.
    public float Tiling { get; init; }
    public const float GroundCell = 128;
    // The paint is worn through a height texture (the material's microblend, its alpha), as the
    // layers of multilayered surfaces are: where the mask is weak, only its heights keep the
    // paint. 0: none. MicroblendTiling: its repeats per repeat of the ground material;
    // MicroblendContrast: the softness of the worn edge (lower is sharper).
    public ulong Microblend { get; init; }
    public float MicroblendTiling { get; init; } = 1;
    public float MicroblendContrast { get; init; } = 1;
    // The mask's black and white points (its value is stretched between them).
    public Vector2 MaskLevels { get; init; } = new(0, 1);
    // Light of its own (emissive decals): display intensity, 0 for none.
    public float Emissive { get; init; }
}

public enum TextureEncoding : byte { BC1, BC2, BC3, BC4, BC5, BC7, R8, Rgba8 }

// A texture as the GPU takes it: block-compressed data uploaded as is. Mips[0] is the
// largest level kept; RowPitches are bytes per row of pixels (per row of 4×4 blocks).
public sealed record TextureData(int Width, int Height, TextureEncoding Encoding, bool Srgb, byte[][] Mips, int[] RowPitches)
{
    public long Bytes => Mips.Sum(m => (long)m.Length);
}

// A multilayer mask: one 8-bit image per layer, layer after layer.
public sealed record MaskData(int Width, int Height, int Layers, byte[] Pixels);

public interface IMaterialSource
{
    Task<MeshSurface?> ReadSurfaceAsync(ulong mesh, CancellationToken cancellation);
    Task<TextureData?> ReadTextureAsync(ulong texture, int maximumSize, CancellationToken cancellation);
    Task<MaskData?> ReadMaskAsync(ulong mask, int maximumSize, CancellationToken cancellation);
    // The material of a projected decal (a .mi over a decal template), or null when it is not one.
    Task<DecalMaterial?> ReadDecalAsync(ulong material, CancellationToken cancellation);
}
