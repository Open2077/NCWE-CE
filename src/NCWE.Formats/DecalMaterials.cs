// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using System.Numerics;
using NCWE.Core;
using WolvenKit.RED4.Types;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace NCWE.Formats;

// The materials of projected decals (worldStaticDecalNode): a .mi over one of the decal
// templates, read with the template's own defaults under the values the instances set.
//   decal, decal_normal, decal_roughness, decal_normal_roughness(_metalness(_2)), decal_puddle,
//   decal_parallax, decal_emissive_color: a picture (DiffuseTexture x DiffuseColor), or only its
//     shape when DiffuseTextureAsMaskTexture; a cell of an atlas (SubUVx x SubUVy, Frame) or a
//     sub-region in pixels (SubRegion over AtlasSize);
//   decal_emissive_color: a picture that glows (Emissive x EmissiveColor), with no colour of its own;
//   decal_gradientmap_recolor(_emissive): a grey picture colored through GradientMap, shaped by MaskTexture;
//   decal_terrain_projected: a ground material (BaseColor) laid MaterialTiling times per terrain cell,
//     shaped by AlphaMask (its levels) and worn through the Microblend's heights.
public sealed partial class GameArchiveIndex
{
    private readonly ConcurrentDictionary<ulong, DecalMaterial?> _decalMaterials = new();
    private readonly ConcurrentDictionary<string, Dictionary<string, IRedType>> _templateDefaults = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<ulong, Vector2> _textureSizes = new();

    public Task<DecalMaterial?> ReadDecalAsync(ulong hash, CancellationToken cancellation = default) => Task.Run(() => ReadDecal(hash), cancellation);

    // The hash the archives know a resource by, from its depot path as typed.
    public static ulong ResourceHash(string? path) => string.IsNullOrWhiteSpace(path) ? 0
        : WolvenKit.Common.FNV1A.FNV1A64HashAlgorithm.HashString(path.Trim().Replace('/', '\\').ToLowerInvariant());

    public DecalMaterial? ReadDecal(ulong hash)
    {
        if (hash == 0) return null;
        // The user's own decals: never kept here, they change with the bank.
        if (!_entries.ContainsKey(hash) && Decals?.Material(hash) is { } own) return own;
        if (_decalMaterials.TryGetValue(hash, out var known)) return known;
        DecalMaterial? material = null;
        try { if (ExternalChain(hash, 0) is { } chain) material = DecalOf(chain, $"decal:{hash:x16}"); }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or InvalidCastException or ArgumentException) { }
        return _decalMaterials[hash] = material;
    }

    private DecalMaterial? DecalOf(MaterialChain chain, string key)
    {
        string t = chain.Template;
        if (!t.Contains("decal")) return null;
        // What the instances set, over the template's defaults.
        var v = new Dictionary<string, IRedType>(TemplateDefaults(t), StringComparer.Ordinal);
        foreach (var (name, value) in chain.Values) v[name] = value;

        if (t.Contains("terrain_projected"))
        {
            var scale = V4(v, "BaseColorScale") ?? (Color(v, "BaseColorScale") is { } c ? new Vector4(c, 1) : Vector4.One);
            return new(key)
            {
                Diffuse = Tex(v, "BaseColor"), Mask = Tex(v, "AlphaMask"), Color = new(scale.X, scale.Y, scale.Z),
                Tiling = Math.Clamp(F(v, "MaterialTiling", 1), .01f, 4096), MaskLevels = new(F(v, "AlphaMaskBlackPoint", 0), F(v, "AlphaMaskWhitePoint", 1)),
                Microblend = Tex(v, "Microblend"), MicroblendTiling = Math.Clamp(F(v, "MicroblendTiling", 1), .01f, 256), MicroblendContrast = Math.Clamp(F(v, "MicroblendContrast", 1), 0, 1)
            };
        }
        // decal_emissive_color has no diffuse picture: what glows is the picture.
        bool glowOnly = t.Contains("emissive_color");
        ulong diffuse = glowOnly ? Tex(v, "Emissive") : Tex(v, "DiffuseTexture");
        if (diffuse == 0) return null;
        var color = Color(v, "DiffuseColor") ?? Vector3.One;
        bool emissive = t.Contains("emissive");
        var material = new DecalMaterial(key)
        {
            Diffuse = diffuse, Color = emissive ? Color(v, "EmissiveColor") ?? color : color, Alpha = Math.Clamp(F(v, "DiffuseAlpha", 1), 0, 1),
            DiffuseAsMask = F(v, "DiffuseTextureAsMaskTexture", 0) > .5f, UvRect = PictureRect(v, diffuse),
            Emissive = emissive ? Intensity(F(v, "EmissiveEV", 4)) : 0
        };
        if (t.Contains("gradientmap")) material = material with { Gradient = Tex(v, "GradientMap"), Mask = Tex(v, "MaskTexture") };
        return material;
    }

    // The part of the picture a decal shows: a sub-region in pixels of an atlas, or a cell of a
    // flipbook grid; all of it otherwise.
    private Vector4 PictureRect(Dictionary<string, IRedType> v, ulong texture)
    {
        if (V4(v, "SubRegion") is { Z: > 0, W: > 0 } region)
        {
            // AtlasSize: one number (a square atlas) or a vector; 0: the texture's own size.
            var atlas = V4(v, "AtlasSize") is { X: > 0, Y: > 0 } given ? new Vector2(given.X, given.Y) : F(v, "AtlasSize", 0) is > 0 and var side ? new Vector2(side) : TextureSize(texture);
            if (atlas.X > 0 && atlas.Y > 0 && region.X + region.Z <= atlas.X * 1.001f && region.Y + region.W <= atlas.Y * 1.001f)
                return new(region.Z / atlas.X, region.W / atlas.Y, region.X / atlas.X, region.Y / atlas.Y);
        }
        int columns = (int)Math.Clamp(MathF.Round(F(v, "SubUVx", 1)), 1, 64), rows = (int)Math.Clamp(MathF.Round(F(v, "SubUVy", 1)), 1, 64);
        if (columns * rows > 1)
        {
            int frame = (int)Math.Clamp(MathF.Round(F(v, "Frame", 0)), 0, columns * rows - 1);
            return new(1f / columns, 1f / rows, frame % columns / (float)columns, frame / columns / (float)rows);
        }
        return new(1, 1, 0, 0);
    }

    // A texture's full size in pixels (its header), (0, 0) when it cannot be read.
    private Vector2 TextureSize(ulong hash) => hash == 0 ? default : _textureSizes.GetOrAdd(hash, h =>
    {
        try { return WithResource<object>(h, file => file.RootChunk is CBitmapTexture t ? new Vector2((uint)t.Width, (uint)t.Height) : null) as Vector2? ?? default; }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or InvalidCastException) { return default; }
    });

    // The default values of a template's parameters, as the values an instance sets.
    private Dictionary<string, IRedType> TemplateDefaults(string template) => _templateDefaults.GetOrAdd(template, path =>
    {
        var values = new Dictionary<string, IRedType>(StringComparer.Ordinal);
        try
        {
            WithResource<object>(WolvenKit.Common.FNV1A.FNV1A64HashAlgorithm.HashString(path), file =>
            {
                if (file.RootChunk is not CMaterialTemplate t) return null;
                foreach (var handle in t.Parameters.SelectMany(group => group))
                {
                    if (handle.Chunk is not CMaterialParameter parameter || parameter.ParameterName.ToString() is not { Length: > 0 } name) continue;
                    IRedType? value = parameter switch
                    {
                        CMaterialParameterScalar s => s.Scalar,
                        CMaterialParameterColor c => c.Color,
                        CMaterialParameterVector q => q.Vector,
                        CMaterialParameterTexture texture => texture.Texture,
                        _ => null
                    };
                    if (value is not null) values[name] = value;
                }
                return file;
            });
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or InvalidCastException) { }
        return values;
    });

    // Diagnostics (probe): a decal material as the view draws it.
    public IEnumerable<string> DumpDecalMaterial(ulong hash)
    {
        if (ReadDecal(hash) is not { } m) { yield return "not a decal material"; yield break; }
        yield return $"diffuse {TexturePath(m.Diffuse) ?? m.Diffuse.ToString("x16")}{(m.DiffuseAsMask ? " (as mask)" : "")} size {TextureSize(m.Diffuse)}";
        if (m.Mask != 0) yield return $"mask {TexturePath(m.Mask) ?? m.Mask.ToString("x16")} levels {m.MaskLevels}";
        if (m.Gradient != 0) yield return $"gradient {TexturePath(m.Gradient) ?? m.Gradient.ToString("x16")}";
        if (m.Microblend != 0) yield return $"microblend {TexturePath(m.Microblend) ?? m.Microblend.ToString("x16")} tiling {m.MicroblendTiling} contrast {m.MicroblendContrast}";
        yield return $"color {m.Color} alpha {m.Alpha} rect {m.UvRect} tiling {m.Tiling} emissive {m.Emissive}";
    }
}
