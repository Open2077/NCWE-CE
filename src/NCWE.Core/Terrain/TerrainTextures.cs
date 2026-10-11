// SPDX-License-Identifier: GPL-3.0-only
namespace NCWE.Core.Terrain;

// Textures the editor makes for terrain cells in Material shading (normals, masks), as the
// renderer takes them.
public static class TerrainTextures
{
    // RGBA8 with a box-filtered mip chain.
    public static TextureData Rgba8(int width, int height, byte[] rgba, bool srgb = false)
    {
        var mips = new List<byte[]> { rgba }; var pitches = new List<int> { width * 4 };
        int w = width, h = height;
        while (w > 1 || h > 1)
        {
            int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
            var source = mips[^1]; var down = new byte[nw * nh * 4];
            for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                {
                    int x0 = Math.Min(x * 2, w - 1), x1 = Math.Min(x * 2 + 1, w - 1), y0 = Math.Min(y * 2, h - 1), y1 = Math.Min(y * 2 + 1, h - 1);
                    for (int c = 0; c < 4; c++)
                        down[(y * nw + x) * 4 + c] = (byte)((source[(y0 * w + x0) * 4 + c] + source[(y0 * w + x1) * 4 + c] + source[(y1 * w + x0) * 4 + c] + source[(y1 * w + x1) * 4 + c] + 2) / 4);
                }
            mips.Add(down); pitches.Add(nw * 4); w = nw; h = nh;
        }
        return new TextureData(width, height, TextureEncoding.Rgba8, srgb, mips.ToArray(), pitches.ToArray());
    }

    // A cell's paint as the renderer's mask array (one image per layer, layer after layer).
    public static MaskData Mask(TerrainPaintLayers layers)
    {
        var pixels = new byte[layers.Width * layers.Height * layers.Count];
        for (int l = 0; l < layers.Count; l++) layers.Layers[l].CopyTo(pixels, l * layers.Width * layers.Height);
        return new MaskData(layers.Width, layers.Height, layers.Count, pixels);
    }
}
