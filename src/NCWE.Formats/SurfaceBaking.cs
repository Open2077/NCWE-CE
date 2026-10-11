// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Collections.Concurrent;
using CP77.Common.Image;
using NCWE.Core;
using WolvenKit.Common.DDS;
using WolvenKit.RED4.Archive.CR2W;
using WolvenKit.RED4.Archive.IO;
using WolvenKit.RED4.Types;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace NCWE.Formats;

// Multilayered materials (engine\materials\multilayered.mt) baked to one color image, as
// the engine composites them: each layer's color texture, tiled by MatTile × the template's
// TilingMultiplier and offset, times its colorScale override, is blended over the layers
// below by its mlmask layer × opacity. Microblends, normals, roughness and metalness are
// left out: an albedo preview that shows asphalt, lane markings, curbs and sidewalk tiles.
public sealed partial class GameArchiveIndex
{
    private readonly ConcurrentDictionary<string, SurfaceTexture?> _surfaces = new();
    // Why a surface could not be baked (diagnostics; the part keeps its flat color).
    public ConcurrentQueue<string> SurfaceErrors { get; } = new();
    private readonly ConcurrentDictionary<ulong, LinearImage?> _layerColors = new();
    private readonly ConcurrentDictionary<ulong, LayerTemplate?> _layerTemplates = new();
    private sealed record LinearImage(int Width, int Height, Vector3[] Pixels);
    private sealed record MaskImage(int Width, int Height, byte[] Values);
    private sealed record LayerTemplate(ulong ColorTexture, float Tiling, Dictionary<string, Vector3> Scales);
    private sealed record Layer(LinearImage? Color, Vector3 Scale, Vector2 Tile, Vector2 Offset, float Opacity, MaskImage? Mask);

    // Detail textures are sampled from a mip no larger than this (the bake is ~1 k pixels).
    private const int LayerTextureSize = 512;

    // The materials of one mesh appearance (falling back to "default", then the first).
    private IReadOnlyList<SurfaceTexture?>? BakeSurfaces(CMesh mesh, List<int> kept, int size, CancellationToken cancellation, string appearanceName = "default")
    {
        var appearances = mesh.Appearances.Select(a => a.Chunk).OfType<meshMeshAppearance>().ToArray();
        var appearance = appearances.FirstOrDefault(a => a.Name.ToString() == appearanceName) ?? appearances.FirstOrDefault(a => a.Name.ToString() == "default") ?? appearances.FirstOrDefault();
        if (appearance is null) return null;
        var result = new SurfaceTexture?[kept.Count]; bool any = false;
        for (int k = 0; k < kept.Count; k++)
        {
            if (kept[k] >= appearance.ChunkMaterials.Count) continue;
            string slot = appearance.ChunkMaterials[kept[k]].ToString() ?? "";
            var entry = mesh.MaterialEntries.FirstOrDefault(e => e.Name.ToString() == slot);
            if (entry is null || !entry.IsLocalInstance) continue;
            int i = entry.Index;
            // Cooked road pieces keep their instances in the preload list.
            var instance = (i < mesh.LocalMaterialBuffer.Materials.Count ? mesh.LocalMaterialBuffer.Materials[i]
                : i < mesh.LocalMaterialInstances.Count ? mesh.LocalMaterialInstances[i].Chunk
                : i < mesh.PreloadLocalMaterialInstances.Count ? mesh.PreloadLocalMaterialInstances[i].Chunk : null) as CMaterialInstance;
            if (instance is null) continue;
            ulong setup = 0, mask = 0;
            foreach (var pair in instance.Values)
            {
                if (pair.Value is not IRedRef reference) continue;
                string key = pair.Key.ToString() ?? "";
                if (key == "MultilayerSetup") setup = reference.DepotPath.GetRedHash();
                else if (key == "MultilayerMask") mask = reference.DepotPath.GetRedHash();
            }
            if (setup == 0 || mask == 0) continue;
            result[k] = Surface($"ml:{setup:x16}:{mask:x16}:{size}", setup, mask, size, cancellation);
            any |= result[k] is not null;
        }
        return any ? result : null;
    }

    // The multilayers a mesh wears (its local multilayered material instances, cooked road pieces'
    // in the preload list), with their depot paths, in slot order: junctions take a road type's
    // street materials from them (JunctionStyle.Of).
    public IReadOnlyList<StreetLayer> StreetLayers(ulong meshHash) => WithResource(meshHash, file =>
    {
        if (file.RootChunk is not CMesh mesh) return null;
        var layers = new List<StreetLayer>();
        foreach (var entry in mesh.MaterialEntries.Where(e => e.IsLocalInstance))
        {
            int i = entry.Index;
            var instance = (i < mesh.LocalMaterialBuffer.Materials.Count ? mesh.LocalMaterialBuffer.Materials[i]
                : i < mesh.LocalMaterialInstances.Count ? mesh.LocalMaterialInstances[i].Chunk
                : i < mesh.PreloadLocalMaterialInstances.Count ? mesh.PreloadLocalMaterialInstances[i].Chunk : null) as CMaterialInstance;
            if (instance is null) continue;
            string? setup = null, mask = null;
            foreach (var pair in instance.Values)
            {
                if (pair.Value is not IRedRef reference) continue;
                string key = pair.Key.ToString() ?? "";
                if (key == "MultilayerSetup") setup = reference.DepotPath.GetResolvedText();
                else if (key == "MultilayerMask") mask = reference.DepotPath.GetResolvedText();
            }
            if (!string.IsNullOrEmpty(setup) && !string.IsNullOrEmpty(mask)) layers.Add(new(setup, mask));
        }
        return layers;
    }) ?? [];

    // Bounded caches: previews and library thumbnails bake many materials once each; meshes
    // keep the textures they use alive, a cleared entry is only baked again when asked.
    private long _surfaceBytes, _layerBytes;
    private const long SurfaceCacheBytes = 384L << 20, LayerCacheBytes = 512L << 20;

    private SurfaceTexture? Surface(string key, ulong setup, ulong mask, int size, CancellationToken cancellation)
    {
        if (_surfaces.TryGetValue(key, out var cached)) return cached;
        var baked = TryBake(key, setup, mask, size, cancellation);
        if (Interlocked.Add(ref _surfaceBytes, (long)size * size * 4) > SurfaceCacheBytes) { _surfaces.Clear(); Interlocked.Exchange(ref _surfaceBytes, 0); }
        return _surfaces.GetOrAdd(key, baked);
    }

    private LinearImage? LayerColor(ulong hash)
    {
        if (_layerColors.TryGetValue(hash, out var cached)) return cached;
        var image = DecodeLayerColor(hash);
        if (Interlocked.Add(ref _layerBytes, (image?.Pixels.Length ?? 0) * 12L) > LayerCacheBytes) { _layerColors.Clear(); Interlocked.Exchange(ref _layerBytes, 0); }
        return _layerColors.GetOrAdd(hash, image);
    }

    private SurfaceTexture? TryBake(string key, ulong setupHash, ulong maskHash, int size, CancellationToken cancellation)
    {
        try { return BakeMultilayer(key, setupHash, maskHash, size, cancellation); }
        catch (Exception ex) when (!cancellation.IsCancellationRequested) { SurfaceErrors.Enqueue($"{key}: {ex.GetType().Name}: {ex.Message}"); return null; }
    }

    // Diagnostic and test entry: bake one setup + mask pair.
    public SurfaceTexture? BakeMultilayer(string key, ulong setupHash, ulong maskHash, int size, CancellationToken cancellation = default)
    {
        var masks = WithResource(maskHash, file => file.RootChunk is Multilayer_Mask mask ? ReadMasks(mask, size) : null) ?? throw new InvalidDataException("Unreadable multilayer mask.");
        var layers = WithResource(setupHash, file => file.RootChunk is Multilayer_Setup setup ? setup.Layers.Select((layer, i) =>
        {
            var template = Template(layer.Material.DepotPath.GetRedHash());
            float tiling = (float)layer.MatTile * (template?.Tiling ?? 1);
            var scale = template?.Scales.GetValueOrDefault(layer.ColorScale.ToString() ?? "", Vector3.One) ?? Vector3.One;
            var color = template is { ColorTexture: not 0 } t ? LayerColor(t.ColorTexture) : null;
            return new Layer(color, scale, new(tiling), new((float)layer.OffsetU, (float)layer.OffsetV), (float)layer.Opacity, i < masks.Length ? masks[i] : null);
        }).ToArray() : null) ?? throw new InvalidDataException("Unreadable multilayer setup.");
        cancellation.ThrowIfCancellationRequested();
        var rgba = new byte[size * size * 4];
        Parallel.For(0, size, new ParallelOptions { CancellationToken = cancellation }, y =>
        {
            for (int x = 0; x < size; x++)
            {
                var uv = new Vector2((x + .5f) / size, (y + .5f) / size);
                var color = Vector3.Zero;
                for (int i = 0; i < layers.Length; i++)
                {
                    var layer = layers[i];
                    // Layer 0 is the base and covers everything.
                    float weight = (i == 0 ? 1 : layer.Mask is { } m ? Sample(m, uv) : 0) * layer.Opacity;
                    if (weight <= 0) continue;
                    var value = layer.Scale * (layer.Color is { } image ? Sample(image, uv * layer.Tile + layer.Offset) : Vector3.One);
                    color = Vector3.Lerp(color, value, Math.Min(weight, 1));
                }
                int o = (y * size + x) * 4;
                rgba[o] = Encode(color.X); rgba[o + 1] = Encode(color.Y); rgba[o + 2] = Encode(color.Z); rgba[o + 3] = 255;
            }
        });
        return new(key, size, size, rgba);
    }

    private static byte Encode(float linear) => (byte)Math.Clamp(MathF.Pow(Math.Clamp(linear, 0, 1), 1 / 2.2f) * 255 + .5f, 0, 255);

    private LayerTemplate? Template(ulong hash) => hash == 0 ? null : _layerTemplates.GetOrAdd(hash, h =>
    {
        try
        {
            return WithResource(h, file =>
            {
                if (file.RootChunk is not Multilayer_LayerTemplate template) return null;
                var scales = new Dictionary<string, Vector3>(StringComparer.Ordinal);
                foreach (var entry in template.Overrides.ColorScale)
                    if (entry.V.Count >= 3) scales[entry.N.ToString() ?? ""] = new((float)entry.V[0], (float)entry.V[1], (float)entry.V[2]);
                float tiling = (float)template.TilingMultiplier;
                return new LayerTemplate(template.ColorTexture.DepotPath.GetRedHash(), tiling > 0 ? tiling : 1, scales);
            });
        }
        catch (Exception) { return null; }
    });

    private LinearImage? DecodeLayerColor(ulong hash)
    {
        try
        {
            return WithResource(hash, file =>
            {
                if (file.RootChunk is not CBitmapTexture texture || DecodeBitmap(texture, LayerTextureSize) is not var (width, height, pixels)) return null;
                bool gamma = texture.Setup.IsGamma;
                var linear = new Vector3[width * height];
                for (int i = 0; i < linear.Length; i++)
                {
                    var c = new Vector3(pixels[i * 4], pixels[i * 4 + 1], pixels[i * 4 + 2]) / 255f;
                    linear[i] = gamma ? new(MathF.Pow(c.X, 2.2f), MathF.Pow(c.Y, 2.2f), MathF.Pow(c.Z, 2.2f)) : c;
                }
                return new LinearImage(width, height, linear);
            });
        }
        catch (Exception) { return null; }
    }

    // rendRenderMultilayerMaskBlobPC: a BC4 atlas of (tile + 2)-pixel tiles and a tile table;
    // per tile cell, a start index and a bit per layer present; each present layer has one
    // entry (atlas tile x/y, mip shift x/y). High-resolution cells come first, then the
    // low-resolution grid. Decoded straight to `target` pixels (nearest), one byte per pixel.
    private static MaskImage[] ReadMasks(Multilayer_Mask mask, int target)
    {
        if (mask.RenderResourceBlob.RenderResourceBlobPC.Chunk is not rendRenderMultilayerMaskBlobPC blob) throw new InvalidDataException("No mask blob.");
        var h = blob.Header;
        uint atlasWidth = h.AtlasWidth, atlasHeight = h.AtlasHeight, maskWidth = h.MaskWidth, maskHeight = h.MaskHeight, tileSize = h.MaskTileSize;
        if (tileSize == 0 || maskWidth == 0 || maskHeight == 0) throw new InvalidDataException("Empty mask.");
        byte[] atlas = new byte[atlasWidth * atlasHeight];
        if (!BlockCompression.DecodeBC(blob.AtlasData.Buffer.GetBytes(), ref atlas, atlasWidth, atlasHeight, BlockCompression.BlockCompressionType.BC4))
            throw new InvalidDataException("Mask atlas decompression failed.");
        uint[] tiles = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(blob.TilesData.Buffer.GetBytes()).ToArray();
        static uint DivCeil(uint l, uint r) => (l + r - 1) / r;
        uint tilesX = DivCeil(maskWidth, tileSize), lowOffset = tilesX * DivCeil(maskHeight, tileSize);
        uint scale = h.MaskWidthLow == 0 || maskWidth < h.MaskWidthLow ? 1 : maskWidth / h.MaskWidthLow;
        uint lowTilesX = DivCeil(maskWidth / scale, tileSize);
        int width = (int)Math.Min(maskWidth, (uint)target), height = (int)Math.Min(maskHeight, (uint)target);
        var result = new MaskImage[h.NumLayers];
        for (int layer = 0; layer < result.Length; layer++)
        {
            var values = new byte[width * height];
            int l = layer;
            Parallel.For(0, height, y =>
            {
                uint sy = (uint)((long)y * maskHeight / height);
                for (int x = 0; x < width; x++)
                {
                    uint sx = (uint)((long)x * maskWidth / width);
                    values[y * width + x] = Single(sx, sy, l, 0, 1, tilesX, out var v) || Single(sx, sy, l, lowOffset, scale, lowTilesX, out v) ? v : (byte)0;
                }
            });
            result[layer] = new(width, height, values);
        }
        return result;

        bool Single(uint x, uint y, int layer, uint offset, uint smallScale, uint widthInTiles, out byte value)
        {
            value = 0;
            uint cell = widthInTiles * (y / tileSize / smallScale) + x / tileSize / smallScale + offset;
            if (cell * 2 + 1 >= tiles.Length) return false;
            uint start = tiles[cell * 2], present = tiles[cell * 2 + 1];
            if ((present & (1u << layer)) == 0) return false;
            uint rank = (uint)System.Numerics.BitOperations.PopCount(present & ((1u << layer) - 1));
            if (start + rank >= tiles.Length) return false;
            uint entry = tiles[start + rank];
            uint ax = entry & 0x3FF, ay = (entry >> 10) & 0x3FF, shiftX = (entry >> 20) & 0xF, shiftY = (entry >> 24) & 0xF, stride = tileSize + 2;
            uint px = Math.Min((x >> (int)shiftX) % tileSize, tileSize - 1) + 1 + ax * stride;
            uint py = Math.Min((y >> (int)shiftY) % tileSize, tileSize - 1) + 1 + ay * stride;
            uint at = px + py * atlasWidth;
            if (at >= atlas.Length) return false;
            value = atlas[at]; return true;
        }
    }

    // CBitmapTexture → RGBA8 of the largest mip no bigger than `maximum` (first slice).
    internal static (int Width, int Height, byte[] Rgba)? DecodeBitmap(CBitmapTexture texture, int maximum)
    {
        if (texture.RenderTextureResource.RenderResourceBlobPC.Chunk is not rendRenderTextureBlobPC blob) return null;
        var header = blob.Header;
        int width = header.SizeInfo.Width, height = header.SizeInfo.Height, mips = header.MipMapInfo.Count;
        if (width <= 0 || height <= 0 || mips == 0) return null;
        Enums.ETextureCompression compression = texture.Setup.Compression; Enums.ETextureRawFormat raw = texture.Setup.RawFormat;
        var format = compression switch
        {
            Enums.ETextureCompression.TCM_None => raw switch
            {
                Enums.ETextureRawFormat.TRF_Grayscale => DXGI_FORMAT.DXGI_FORMAT_R8_UNORM,
                Enums.ETextureRawFormat.TRF_TrueColor => DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
                _ => throw new NotSupportedException($"Raw texture format {raw} is not supported.")
            },
            Enums.ETextureCompression.TCM_DXTNoAlpha => DXGI_FORMAT.DXGI_FORMAT_BC1_UNORM,
            Enums.ETextureCompression.TCM_DXTAlpha or Enums.ETextureCompression.TCM_DXTAlphaLinear => DXGI_FORMAT.DXGI_FORMAT_BC3_UNORM,
            Enums.ETextureCompression.TCM_QualityR => DXGI_FORMAT.DXGI_FORMAT_BC4_UNORM,
            Enums.ETextureCompression.TCM_Normalmap or Enums.ETextureCompression.TCM_QualityRG or Enums.ETextureCompression.TCM_Normals => DXGI_FORMAT.DXGI_FORMAT_BC5_UNORM,
            Enums.ETextureCompression.TCM_QualityColor => DXGI_FORMAT.DXGI_FORMAT_BC7_UNORM,
            _ => throw new NotSupportedException($"Texture compression {compression} is not supported.")
        };
        int level = 0;
        while (level + 1 < mips && Math.Max(width >> level, height >> level) > maximum) level++;
        int w = Math.Max(1, width >> level), hgt = Math.Max(1, height >> level);
        var mip = header.MipMapInfo[level];
        var all = blob.TextureData.Buffer.GetBytes();
        var (block, bytes) = Layout(format);
        int rows = block ? Math.Max(1, (hgt + 3) / 4) : hgt, rowBytes = block ? Math.Max(1, (w + 3) / 4) * bytes : w * bytes;
        int pitch = RowPitch((uint)mip.Layout.RowPitch, rowBytes, rows, (uint)mip.Placement.Size);
        var data = new byte[rows * rowBytes];
        for (int r = 0; r < rows; r++) all.AsSpan((int)(uint)mip.Placement.Offset + r * pitch, rowBytes).CopyTo(data.AsSpan(r * rowBytes));
        return (w, hgt, DecodePixels(format, w, hgt, data));
    }
    private T? WithResource<T>(ulong hash, Func<CR2WFile, T?> work) where T : class
    {
        if (EntryOrNull(hash) is not { } entry) return null;
        using var data = ExtractEntry(entry, default);
        using var reader = new CR2WReader(data);
        if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file is null) return null;
        using (file) return work(file);
    }

    // Bilinear, clamped (masks).
    private static float Sample(MaskImage image, Vector2 uv)
    {
        float x = Math.Clamp(uv.X * image.Width - .5f, 0, image.Width - 1), y = Math.Clamp(uv.Y * image.Height - .5f, 0, image.Height - 1);
        int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, image.Width - 1), y1 = Math.Min(y0 + 1, image.Height - 1);
        float fx = x - x0, fy = y - y0;
        var v = image.Values;
        float top = v[y0 * image.Width + x0] * (1 - fx) + v[y0 * image.Width + x1] * fx;
        float bottom = v[y1 * image.Width + x0] * (1 - fx) + v[y1 * image.Width + x1] * fx;
        return (top * (1 - fy) + bottom * fy) / 255f;
    }

    // Bilinear, wrapped (tiled detail textures).
    private static Vector3 Sample(LinearImage image, Vector2 uv)
    {
        float x = (uv.X - MathF.Floor(uv.X)) * image.Width - .5f, y = (uv.Y - MathF.Floor(uv.Y)) * image.Height - .5f;
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        int W(int i, int n) => ((i % n) + n) % n;
        int ax = W(x0, image.Width), bx = W(x0 + 1, image.Width), ay = W(y0, image.Height), by = W(y0 + 1, image.Height);
        var p = image.Pixels;
        var top = Vector3.Lerp(p[ay * image.Width + ax], p[ay * image.Width + bx], fx);
        var bottom = Vector3.Lerp(p[by * image.Width + ax], p[by * image.Width + bx], fx);
        return Vector3.Lerp(top, bottom, fy);
    }

    // DDS (legacy or DX10 header) → RGBA8 of the largest mip no bigger than `maximum`,
    // first array slice. Block-compressed data is decoded by WolvenKit's managed decoder.
    internal static (int Width, int Height, byte[] Rgba) DecodeDds(byte[] dds, int maximum)
    {
        if (dds.Length < 128 || BinaryPrimitives.ReadUInt32LittleEndian(dds) != 0x20534444) throw new InvalidDataException("Not a DDS image.");
        int height = BinaryPrimitives.ReadInt32LittleEndian(dds.AsSpan(12)), width = BinaryPrimitives.ReadInt32LittleEndian(dds.AsSpan(16));
        int mips = Math.Max(1, BinaryPrimitives.ReadInt32LittleEndian(dds.AsSpan(28)));
        uint fourCc = BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(84));
        int bitCount = BinaryPrimitives.ReadInt32LittleEndian(dds.AsSpan(88));
        int offset = 128;
        DXGI_FORMAT format;
        if (fourCc == 0x30315844) { format = (DXGI_FORMAT)BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(128)); offset = 148; }
        else format = fourCc switch
        {
            0x31545844 => DXGI_FORMAT.DXGI_FORMAT_BC1_UNORM, 0x33545844 => DXGI_FORMAT.DXGI_FORMAT_BC2_UNORM, 0x35545844 => DXGI_FORMAT.DXGI_FORMAT_BC3_UNORM,
            0x31495441 or 0x55344342 => DXGI_FORMAT.DXGI_FORMAT_BC4_UNORM, 0x32495441 or 0x55354342 => DXGI_FORMAT.DXGI_FORMAT_BC5_UNORM,
            0 when bitCount == 8 => DXGI_FORMAT.DXGI_FORMAT_R8_UNORM,
            0 when bitCount == 32 => DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
            _ => throw new NotSupportedException($"DDS format {fourCc:x8} is not supported.")
        };
        var (block, bytes) = Layout(format);
        int w = width, h = height, level = 0;
        int Size(int lw, int lh) => block ? Math.Max(1, (lw + 3) / 4) * Math.Max(1, (lh + 3) / 4) * bytes : lw * lh * bytes;
        while (level + 1 < mips && Math.Max(w, h) > maximum) { offset += Size(w, h); w = Math.Max(1, w / 2); h = Math.Max(1, h / 2); level++; }
        return (w, h, DecodePixels(format, w, h, dds.AsSpan(offset, Size(w, h)).ToArray()));
    }

    private static byte[] DecodePixels(DXGI_FORMAT format, int w, int h, byte[] data)
    {
        var (block, _) = Layout(format);
        var rgba = new byte[w * h * 4];
        if (block)
        {
            var type = format switch
            {
                DXGI_FORMAT.DXGI_FORMAT_BC1_UNORM or DXGI_FORMAT.DXGI_FORMAT_BC1_UNORM_SRGB or DXGI_FORMAT.DXGI_FORMAT_BC1_TYPELESS => BlockCompression.BlockCompressionType.BC1,
                DXGI_FORMAT.DXGI_FORMAT_BC2_UNORM or DXGI_FORMAT.DXGI_FORMAT_BC2_UNORM_SRGB or DXGI_FORMAT.DXGI_FORMAT_BC2_TYPELESS => BlockCompression.BlockCompressionType.BC2,
                DXGI_FORMAT.DXGI_FORMAT_BC3_UNORM or DXGI_FORMAT.DXGI_FORMAT_BC3_UNORM_SRGB or DXGI_FORMAT.DXGI_FORMAT_BC3_TYPELESS => BlockCompression.BlockCompressionType.BC3,
                DXGI_FORMAT.DXGI_FORMAT_BC4_UNORM or DXGI_FORMAT.DXGI_FORMAT_BC4_TYPELESS or DXGI_FORMAT.DXGI_FORMAT_BC4_SNORM => BlockCompression.BlockCompressionType.BC4,
                DXGI_FORMAT.DXGI_FORMAT_BC5_UNORM or DXGI_FORMAT.DXGI_FORMAT_BC5_TYPELESS or DXGI_FORMAT.DXGI_FORMAT_BC5_SNORM => BlockCompression.BlockCompressionType.BC5,
                _ => BlockCompression.BlockCompressionType.BC7
            };
            // The decoder works on whole 4×4 blocks.
            int bw = Math.Max(4, (w + 3) / 4 * 4), bh = Math.Max(4, (h + 3) / 4 * 4);
            byte[] raw = [];
            if (!BlockCompression.DecodeBC(data, ref raw, (uint)bw, (uint)bh, type)) throw new InvalidDataException("Block decompression failed.");
            int channels = raw.Length / (bw * bh);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int s = (y * bw + x) * channels, d = (y * w + x) * 4;
                    byte r = raw[s], g = channels > 1 ? raw[s + 1] : r, b = channels > 2 ? raw[s + 2] : r;
                    rgba[d] = r; rgba[d + 1] = g; rgba[d + 2] = b; rgba[d + 3] = 255;
                }
        }
        else if (format == DXGI_FORMAT.DXGI_FORMAT_R8_UNORM) for (int i = 0; i < w * h; i++) { rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = data[i]; rgba[i * 4 + 3] = 255; }
        else if (format is DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM or DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM_SRGB)
            for (int i = 0; i < w * h; i++) { rgba[i * 4] = data[i * 4 + 2]; rgba[i * 4 + 1] = data[i * 4 + 1]; rgba[i * 4 + 2] = data[i * 4]; rgba[i * 4 + 3] = data[i * 4 + 3]; }
        else data.AsSpan(0, rgba.Length).CopyTo(rgba);
        return rgba;
    }

    private static (bool Block, int Bytes) Layout(DXGI_FORMAT format) => format switch
    {
        DXGI_FORMAT.DXGI_FORMAT_BC1_UNORM or DXGI_FORMAT.DXGI_FORMAT_BC1_UNORM_SRGB or DXGI_FORMAT.DXGI_FORMAT_BC1_TYPELESS
            or DXGI_FORMAT.DXGI_FORMAT_BC4_UNORM or DXGI_FORMAT.DXGI_FORMAT_BC4_SNORM or DXGI_FORMAT.DXGI_FORMAT_BC4_TYPELESS => (true, 8),
        DXGI_FORMAT.DXGI_FORMAT_BC2_UNORM or DXGI_FORMAT.DXGI_FORMAT_BC2_UNORM_SRGB or DXGI_FORMAT.DXGI_FORMAT_BC2_TYPELESS
            or DXGI_FORMAT.DXGI_FORMAT_BC3_UNORM or DXGI_FORMAT.DXGI_FORMAT_BC3_UNORM_SRGB or DXGI_FORMAT.DXGI_FORMAT_BC3_TYPELESS
            or DXGI_FORMAT.DXGI_FORMAT_BC5_UNORM or DXGI_FORMAT.DXGI_FORMAT_BC5_SNORM or DXGI_FORMAT.DXGI_FORMAT_BC5_TYPELESS
            or DXGI_FORMAT.DXGI_FORMAT_BC7_UNORM or DXGI_FORMAT.DXGI_FORMAT_BC7_UNORM_SRGB or DXGI_FORMAT.DXGI_FORMAT_BC7_TYPELESS => (true, 16),
        DXGI_FORMAT.DXGI_FORMAT_R8_UNORM => (false, 1),
        DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM or DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM_SRGB or DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM or DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM_SRGB => (false, 4),
        _ => throw new NotSupportedException($"Texture format {format} is not supported.")
    };
}
