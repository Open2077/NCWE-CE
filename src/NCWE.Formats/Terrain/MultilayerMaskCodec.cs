// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using BCnEncoder.Decoder;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using WolvenKit.RED4.Types;

namespace NCWE.Formats.Terrain;

// The layers of a multilayer mask, full resolution, one byte per pixel, top row first (as the
// game's own tools see the image: for a terrain cell, north at the top).
public sealed record MaskLayers(int Width, int Height, byte[][] Layers)
{
    public byte this[int layer, int x, int y] => Layers[layer][y * Width + x];
}

// rendRenderMultilayerMaskBlobPC: a BC4 atlas of (tile + 2)-pixel tiles (a one-pixel border
// copied from the neighbours, for filtering) and a tile table of 32-bit words. For each tile
// cell of the high-resolution grid (MaskWidth / tile × MaskHeight / tile), then of the
// low-resolution grid, two words: the index of its first entry and a bit per layer present.
// Each present layer has one entry: atlas tile x (10 bits), y (10 bits), and how many times the
// tile is magnified in x and y (4 bits each, as shifts). A layer absent from a high-resolution
// cell falls back to the low-resolution grid. The atlas is 512 pixels wide.
public static class MultilayerMaskCodec
{
    private const int AtlasWidth = 512;

    public static MaskLayers Decode(rendRenderMultilayerMaskBlob blob)
    {
        var h = blob.Header;
        int atlasWidth = (int)(uint)h.AtlasWidth, atlasHeight = (int)(uint)h.AtlasHeight;
        int width = (int)(uint)h.MaskWidth, height = (int)(uint)h.MaskHeight, tile = (int)(uint)h.MaskTileSize, count = (int)(uint)h.NumLayers;
        if (tile == 0 || width == 0 || height == 0) throw new InvalidDataException("Empty multilayer mask.");
        var atlas = DecodeAtlas(blob.AtlasData.Buffer.GetBytes(), atlasWidth, atlasHeight);
        var table = MemoryMarshal.Cast<byte, uint>(blob.TilesData.Buffer.GetBytes()).ToArray();
        int tilesX = DivCeil(width, tile), lowOffset = tilesX * DivCeil(height, tile);
        int scale = h.MaskWidthLow == 0 || width < h.MaskWidthLow ? 1 : width / (int)(uint)h.MaskWidthLow;
        int lowTilesX = DivCeil(width / scale, tile);
        var layers = new byte[count][];
        for (int layer = 0; layer < count; layer++)
        {
            var values = layers[layer] = new byte[width * height];
            int l = layer;
            Parallel.For(0, height, y =>
            {
                for (int x = 0; x < width; x++)
                    values[y * width + x] = Sample(x, y, l, 0, 1, tilesX, out var v) || Sample(x, y, l, lowOffset, scale, lowTilesX, out v) ? v : (byte)0;
            });
        }
        return new(width, height, layers);

        bool Sample(int x, int y, int layer, int offset, int smallScale, int widthInTiles, out byte value)
        {
            value = 0;
            int cell = widthInTiles * (y / tile / smallScale) + x / tile / smallScale + offset;
            if (cell * 2 + 1 >= table.Length) return false;
            uint start = table[cell * 2], present = table[cell * 2 + 1];
            if ((present & (1u << layer)) == 0) return false;
            uint rank = (uint)System.Numerics.BitOperations.PopCount(present & ((1u << layer) - 1));
            if (start + rank >= table.Length) return false;
            uint entry = table[start + rank];
            int ax = (int)(entry & 0x3FF), ay = (int)((entry >> 10) & 0x3FF), shiftX = (int)((entry >> 20) & 0xF), shiftY = (int)((entry >> 24) & 0xF), stride = tile + 2;
            int px = Math.Min((x >> shiftX) % tile, tile - 1) + 1 + ax * stride;
            int py = Math.Min((y >> shiftY) % tile, tile - 1) + 1 + ay * stride;
            int at = px + py * atlasWidth;
            if (at >= atlas.Length) return false;
            value = atlas[at]; return true;
        }
    }

    // Writes layers into the blob (header sizes, atlas and table). Uniform tiles are shared; the
    // low-resolution grid holds every layer at the coarsest magnification that covers the mask.
    // Every layer used anywhere has an entry in every high-resolution cell, so the low-resolution
    // fallback never shows through.
    public static void Encode(rendRenderMultilayerMaskBlob blob, MaskLayers mask, int tileSize = 14)
    {
        int width = mask.Width, height = mask.Height, count = mask.Layers.Length, tile = tileSize, stride = tile + 2;
        if (count is 0 or > 32) throw new ArgumentException("A multilayer mask holds 1 to 32 layers.");
        int tilesX = DivCeil(width, tile), tilesY = DivCeil(height, tile);
        int tilesPerRow = AtlasWidth / stride;
        var atlasTiles = new List<byte[]>();
        var shared = new Dictionary<byte, int>();
        int AddTile(byte[] pixels)
        {
            // Uniform tiles (the common case: all 0 or all 255) are stored once.
            byte first = pixels[0];
            bool uniform = Array.TrueForAll(pixels, p => p == first);
            if (uniform && shared.TryGetValue(first, out int existing)) return existing;
            atlasTiles.Add(pixels);
            if (uniform) shared[first] = atlasTiles.Count - 1;
            return atlasTiles.Count - 1;
        }
        bool[] used = new bool[count];
        for (int l = 0; l < count; l++) used[l] = Array.Exists(mask.Layers[l], v => v != 0);
        if (!used.Any(u => u)) used[0] = true;
        uint present = 0;
        for (int l = 0; l < count; l++) if (used[l]) present |= 1u << l;

        // Low-resolution grid: one cell whose tiles magnify the whole mask.
        int shift = 0;
        while ((tile << shift) < Math.Max(width, height)) shift++;
        int lowSide = 4, lowScale = Math.Max(1, width / lowSide), lowTilesX = DivCeil(width / lowScale, tile), lowTilesY = DivCeil(height / lowScale, tile);
        int highCells = tilesX * tilesY, lowCells = lowTilesX * lowTilesY;
        var table = new List<uint>(new uint[(highCells + lowCells) * 2]);
        int setLayers = System.Numerics.BitOperations.PopCount(present);
        for (int cy = 0; cy < tilesY; cy++)
            for (int cx = 0; cx < tilesX; cx++)
            {
                int cell = cy * tilesX + cx;
                table[cell * 2] = (uint)table.Count; table[cell * 2 + 1] = present;
                for (int l = 0; l < count; l++)
                {
                    if (!used[l]) continue;
                    int index = AddTile(Tile(mask.Layers[l], width, height, cx * tile, cy * tile, tile, 0));
                    table.Add(Entry(index, 0, 0));
                }
            }
        for (int cy = 0; cy < lowTilesY; cy++)
            for (int cx = 0; cx < lowTilesX; cx++)
            {
                int cell = highCells + cy * lowTilesX + cx;
                table[cell * 2] = (uint)table.Count; table[cell * 2 + 1] = present;
                for (int l = 0; l < count; l++)
                {
                    if (!used[l]) continue;
                    int index = AddTile(Tile(mask.Layers[l], width, height, cx * (tile << shift), cy * (tile << shift), tile, shift));
                    table.Add(Entry(index, shift, shift));
                }
            }
        uint Entry(int index, int sx, int sy)
        {
            int ax = index % tilesPerRow, ay = index / tilesPerRow;
            if (ay > 0x3FF) throw new InvalidOperationException("The multilayer mask needs more atlas tiles than its table can address.");
            return (uint)ax | ((uint)ay << 10) | ((uint)sx << 20) | ((uint)sy << 24);
        }

        int rows = DivCeil(atlasTiles.Count, tilesPerRow);
        int atlasHeight = Math.Max(4, rows * stride);
        var atlas = new byte[AtlasWidth * atlasHeight];
        for (int i = 0; i < atlasTiles.Count; i++)
        {
            int ox = (i % tilesPerRow) * stride, oy = (i / tilesPerRow) * stride;
            for (int y = 0; y < stride; y++) atlasTiles[i].AsSpan(y * stride, stride).CopyTo(atlas.AsSpan((oy + y) * AtlasWidth + ox));
        }
        var h = blob.Header;
        h.AtlasWidth = AtlasWidth; h.AtlasHeight = (uint)atlasHeight;
        h.NumLayers = (uint)count;
        h.MaskWidth = (uint)width; h.MaskHeight = (uint)height;
        h.MaskWidthLow = (uint)Math.Min(lowSide, width); h.MaskHeightLow = (uint)Math.Min(lowSide, height);
        h.MaskTileSize = (uint)tile;
        // The buffers keep the game's flags (131072, as its own masks and textures have).
        uint atlasFlags = blob.AtlasData?.Buffer.Flags is > 0 and var a ? a : 131072, tilesFlags = blob.TilesData?.Buffer.Flags is > 0 and var t ? t : 131072;
        blob.AtlasData = new SerializationDeferredDataBuffer(EncodeAtlas(atlas, AtlasWidth, atlasHeight));
        blob.TilesData = new SerializationDeferredDataBuffer(MemoryMarshal.AsBytes(table.ToArray().AsSpan()).ToArray());
        blob.AtlasData.Buffer.Flags = atlasFlags; blob.TilesData.Buffer.Flags = tilesFlags;
    }

    // A (tile + 2)-pixel tile: the cell's pixels magnified 2^shift times, with a one-pixel border
    // from the neighbouring pixels (clamped at the mask's edge).
    private static byte[] Tile(byte[] layer, int width, int height, int x0, int y0, int tile, int shift)
    {
        int stride = tile + 2;
        var pixels = new byte[stride * stride];
        for (int y = 0; y < stride; y++)
            for (int x = 0; x < stride; x++)
            {
                // Pixel (x - 1, y - 1) of the tile covers mask pixels starting at x0 + (x - 1) << shift.
                int mx = Math.Clamp(x0 + ((x - 1) << shift) + ((1 << shift) >> 1), 0, width - 1);
                int my = Math.Clamp(y0 + ((y - 1) << shift) + ((1 << shift) >> 1), 0, height - 1);
                pixels[y * stride + x] = layer[my * width + mx];
            }
        return pixels;
    }

    private static byte[] DecodeAtlas(byte[] data, int width, int height)
    {
        var decoded = new BcDecoder().DecodeRaw(new MemoryStream(data), width, height, CompressionFormat.Bc4);
        var result = new byte[width * height];
        for (int i = 0; i < result.Length && i < decoded.Length; i++) result[i] = decoded[i].r;
        return result;
    }

    private static byte[] EncodeAtlas(byte[] atlas, int width, int height)
    {
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < atlas.Length; i++) { rgba[i * 4] = atlas[i]; rgba[i * 4 + 1] = atlas[i]; rgba[i * 4 + 2] = atlas[i]; rgba[i * 4 + 3] = 255; }
        var encoder = new BcEncoder(CompressionFormat.Bc4);
        encoder.OutputOptions.GenerateMipMaps = false;
        encoder.OutputOptions.Quality = CompressionQuality.Balanced;
        return encoder.EncodeToRawBytes(rgba, width, height, PixelFormat.Rgba32)[0];
    }

    // Diagnostics: how the game laid out a mask.
    public static IEnumerable<string> Describe(rendRenderMultilayerMaskBlob blob)
    {
        var h = blob.Header;
        var table = MemoryMarshal.Cast<byte, uint>(blob.TilesData.Buffer.GetBytes()).ToArray();
        int tile = (int)(uint)h.MaskTileSize, tilesX = DivCeil((int)(uint)h.MaskWidth, tile), tilesY = DivCeil((int)(uint)h.MaskHeight, tile);
        int scale = h.MaskWidthLow == 0 ? 1 : (int)(uint)h.MaskWidth / (int)(uint)h.MaskWidthLow;
        int lowX = DivCeil((int)(uint)h.MaskWidth / scale, tile), lowY = DivCeil((int)(uint)h.MaskHeight / scale, tile);
        yield return $"v{h.Version} flags {h.Flags} layers {h.NumLayers} mask {h.MaskWidth}x{h.MaskHeight} low {h.MaskWidthLow}x{h.MaskHeightLow} tile {tile} atlas {h.AtlasWidth}x{h.AtlasHeight} high cells {tilesX}x{tilesY} low cells {lowX}x{lowY} table words {table.Length}";
        var shifts = new Dictionary<string, int>(); var layerCounts = new int[32]; int high = tilesX * tilesY;
        for (int cell = 0; cell < high + lowX * lowY && cell * 2 + 1 < table.Length; cell++)
        {
            uint start = table[cell * 2], present = table[cell * 2 + 1];
            int n = System.Numerics.BitOperations.PopCount(present);
            for (int k = 0; k < n && start + k < table.Length; k++)
            {
                uint e = table[start + k];
                string key = $"{(cell < high ? "high" : "low")} shift {(e >> 20) & 0xF}/{(e >> 24) & 0xF} top {e >> 28}";
                shifts[key] = shifts.GetValueOrDefault(key) + 1;
            }
            if (cell < high) for (int l = 0; l < 32; l++) if ((present & (1u << l)) != 0) layerCounts[l]++;
        }
        foreach (var s in shifts.OrderBy(s => s.Key)) yield return $"  {s.Key}: {s.Value}";
        yield return $"  high cells per layer: {string.Join(" ", layerCounts.Take((int)(uint)h.NumLayers).Select((c, i) => $"{i}:{c}"))}";
    }

    private static int DivCeil(int l, int r) => (l + r - 1) / r;
}
