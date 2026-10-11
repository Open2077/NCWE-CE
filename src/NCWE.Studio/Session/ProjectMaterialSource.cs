// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using NCWE.Core;
using NCWE.Formats;
using NCWE.Formats.CustomMeshes;

namespace NCWE.Studio.Session;

// The materials the project and the bank make, drawn with the game's:
// - a mesh wearing project materials is drawn under a key of its own ("dressed"): its surface is
//   its mesh's, with the slots it changes wearing their materials;
// - a definition's own textures (files of the material store or of a custom mesh's folder) are
//   served under depot paths of their own, prepared as the material uses them (a channel of a
//   roughness map, a normal map's green flipped) and with their mips.
// partSlots: the slots of drawables the index does not know (meshes edited in place), else null.
internal sealed class ProjectMaterialSource(IMaterialSource inner, GameArchiveIndex index, Func<ulong, IReadOnlyDictionary<string, string[]>?>? partSlots = null) : IMaterialSource
{
    public sealed record Dressing(ulong Base, IReadOnlyDictionary<string, SurfaceMaterial?> Slots, MeshAsset? Custom);
    private sealed record OwnTexture(string File, Func<RgbaImage, RgbaImage> Prepare, bool Srgb);

    private readonly ConcurrentDictionary<ulong, Dressing> _dressings = new();
    private readonly ConcurrentDictionary<ulong, OwnTexture> _textures = new();

    public void Dress(ulong key, Dressing dressing) => _dressings[key] = dressing;
    public void Undress(ulong key) => _dressings.TryRemove(key, out _);

    // The depot path a definition's own texture is drawn from (`code`: the channel and how it is prepared).
    public string Texture(string file, string code, Func<RgbaImage, RgbaImage> prepare, bool srgb)
    {
        string path = $@"ncwe\preview\{CustomMeshInfo.Fnv1a64(file):x16}\{code}.xbm";
        _textures[CustomMeshInfo.Fnv1a64(path)] = new(file, prepare, srgb);
        return path;
    }

    public async Task<MeshSurface?> ReadSurfaceAsync(ulong mesh, CancellationToken cancellation)
    {
        if (!_dressings.TryGetValue(mesh, out var dressing)) return await inner.ReadSurfaceAsync(mesh, cancellation).ConfigureAwait(false);
        var basis = dressing.Custom is { } custom ? CustomSurface(dressing.Base, custom) : await inner.ReadSurfaceAsync(dressing.Base, cancellation).ConfigureAwait(false);
        if (basis is null) return null;
        var slots = await Task.Run(() => partSlots?.Invoke(dressing.Base) ?? index.PartSlots(dressing.Base), cancellation).ConfigureAwait(false);
        if (slots is null) return basis with { Mesh = mesh };
        var appearances = new Dictionary<string, SurfaceMaterial?[]>(StringComparer.Ordinal);
        foreach (var (name, parts) in basis.Appearances)
        {
            var map = slots.GetValueOrDefault(name) ?? slots.GetValueOrDefault("default") ?? slots.Values.FirstOrDefault();
            appearances[name] = map is null || map.Length != parts.Length ? parts
                : [.. parts.Select((material, k) => dressing.Slots.TryGetValue(map[k], out var worn) ? worn : material)];
        }
        return basis with { Mesh = mesh, Appearances = appearances };
    }

    // A custom mesh has no game surface: its parts' UVs, its own materials left to its baked look.
    private static MeshSurface CustomSurface(ulong hash, MeshAsset asset) =>
        new(hash, [.. asset.Parts.Select(p => p.Uv is { Length: > 0 } uv ? uv : null)], [.. asset.Parts.Select(p => p.Vertices.Length)],
            new Dictionary<string, SurfaceMaterial?[]> { ["default"] = new SurfaceMaterial?[asset.Parts.Length] });

    public Task<TextureData?> ReadTextureAsync(ulong texture, int maximumSize, CancellationToken cancellation)
    {
        if (!_textures.TryGetValue(texture, out var own)) return inner.ReadTextureAsync(texture, maximumSize, cancellation);
        return Task.Run(() =>
        {
            try { return Mips(own.Prepare(TextureFiles.Fit(TextureFiles.Load(own.File), Math.Clamp(maximumSize, 16, 2048))), own.Srgb); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException or NotSupportedException) { return null; }
        }, cancellation);
    }
    public Task<DecalMaterial?> ReadDecalAsync(ulong material, CancellationToken cancellation) => inner.ReadDecalAsync(material, cancellation);

    public Task<MaskData?> ReadMaskAsync(ulong mask, int maximumSize, CancellationToken cancellation) => inner.ReadMaskAsync(mask, maximumSize, cancellation);

    // RGBA pixels with their mip chain (each level a 2×2 box of the one above).
    private static TextureData Mips(RgbaImage image, bool srgb)
    {
        var levels = new List<byte[]> { image.Pixels }; var pitches = new List<int> { image.Width * 4 };
        int w = image.Width, h = image.Height; var pixels = image.Pixels;
        while (w > 1 || h > 1)
        {
            int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
            var next = new byte[nw * nh * 4];
            for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                    for (int c = 0; c < 4; c++)
                    {
                        int sum = 0;
                        for (int dy = 0; dy < 2; dy++)
                            for (int dx = 0; dx < 2; dx++)
                                sum += pixels[(Math.Min(h - 1, y * 2 + dy) * w + Math.Min(w - 1, x * 2 + dx)) * 4 + c];
                        next[(y * nw + x) * 4 + c] = (byte)((sum + 2) / 4);
                    }
            levels.Add(next); pitches.Add(nw * 4);
            w = nw; h = nh; pixels = next;
        }
        return new(image.Width, image.Height, TextureEncoding.Rgba8, srgb, [.. levels], [.. pitches]);
    }
}
