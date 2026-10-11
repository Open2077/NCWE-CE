// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;

namespace NCWE.Core.Terrain;

// What the game ships for one terrain cell, as the editor needs it: heights on a 0.5 m lattice
// over the cell (257 × 257 samples, corners included), the quads the cell's mesh covers (cells at
// the terrain's edge hold only part of their square), and its paint (multilayer mask).
public sealed class TerrainBase
{
    public const float Step = .5f;
    public const int Samples = 257;               // 128 m / 0.5 m + 1
    public const int Quads = 128;                 // 1 m quads, the edited mesh's resolution
    public const int QuadSamples = 2;             // lattice steps per quad

    public required TerrainCell Cell { get; init; }
    // Heights of the visible surface (the cell's mesh); NaN where the mesh has no triangle.
    public required float[] Visual { get; init; }
    // Heights of the collision height field at the same samples, or null without one.
    public float[]? Collision { get; init; }
    // 1 where the cell's mesh covers a 1 m quad (row-major from the cell's south-west corner).
    public required bool[] Covered { get; init; }
    // The material's texture span: per-cell textures map game xy relative to the cell centre
    // through uv = local / Span + 0.5 (130 m for a full cell).
    public required float UvSpan { get; init; }
    public required TerrainPaintLayers Paint { get; init; }
    // Names of the multilayer setup's layers (their material), for the paint palette.
    public required string[] LayerNames { get; init; }

    public static int Index(int x, int y) => y * Samples + x;

    // Visible height at a cell-local lattice sample, the collision where the mesh has none.
    public float VisualAt(int x, int y)
    {
        float v = Visual[Index(x, y)];
        return float.IsNaN(v) && Collision is { } c ? c[Index(x, y)] : v;
    }

    public float CollisionAt(int x, int y) => Collision is { } c ? c[Index(x, y)] : VisualAt(x, y);
}

// A cell's multilayer mask as editable layers: Width × Height pixels covering UvSpan metres
// around the cell centre, top row north (see MultilayerMaskCodec), one byte per layer pixel.
public sealed class TerrainPaintLayers
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required float Span { get; init; }
    public required byte[][] Layers { get; init; }

    public int Count => Layers.Length;

    // Pixel of a game position (relative to the cell centre), fractional.
    public Vector2 PixelOf(Vector2 local) => new((local.X / Span + .5f) * Width - .5f, (.5f - local.Y / Span) * Height - .5f);
    public Vector2 LocalOf(int x, int y) => new(((x + .5f) / Width - .5f) * Span, (.5f - (y + .5f) / Height) * Span);

    public TerrainPaintLayers Clone() => new() { Width = Width, Height = Height, Span = Span, Layers = Layers.Select(l => (byte[])l.Clone()).ToArray() };

    // Resampled (bilinear) to another resolution over the same span.
    public TerrainPaintLayers Resample(int width, int height)
    {
        if (width == Width && height == Height) return Clone();
        var layers = new byte[Layers.Length][];
        for (int l = 0; l < layers.Length; l++)
        {
            var source = Layers[l]; var target = layers[l] = new byte[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    float sx = (x + .5f) * Width / width - .5f, sy = (y + .5f) * Height / height - .5f;
                    int x0 = Math.Clamp((int)MathF.Floor(sx), 0, Width - 1), y0 = Math.Clamp((int)MathF.Floor(sy), 0, Height - 1);
                    int x1 = Math.Min(x0 + 1, Width - 1), y1 = Math.Min(y0 + 1, Height - 1);
                    float fx = Math.Clamp(sx - x0, 0, 1), fy = Math.Clamp(sy - y0, 0, 1);
                    float v = (source[y0 * Width + x0] * (1 - fx) + source[y0 * Width + x1] * fx) * (1 - fy)
                        + (source[y1 * Width + x0] * (1 - fx) + source[y1 * Width + x1] * fx) * fy;
                    target[y * width + x] = (byte)Math.Clamp(MathF.Round(v), 0, 255);
                }
        }
        return new() { Width = width, Height = height, Span = Span, Layers = layers };
    }

    // The layer drawn on top at a pixel: layers blend in order, each over the ones below
    // through its mask (layer 0 is the ground everywhere).
    public int Dominant(int x, int y)
    {
        int at = y * Width + x; float visible = 1; int best = 0; float bestWeight = -1;
        for (int l = Layers.Length - 1; l >= 0; l--)
        {
            float mask = l == 0 ? 1 : Layers[l][at] / 255f;
            float weight = visible * mask;
            if (weight > bestWeight) { bestWeight = weight; best = l; }
            visible *= 1 - mask;
            if (visible <= bestWeight) break;
        }
        return best;
    }
}

// Reads the game's terrain for the editor (implemented over the archives in NCWE.Formats).
public interface ITerrainSource
{
    // The cell's base, or null when the game has no terrain there. May block (reads archives).
    TerrainBase? Read(TerrainCell cell, CancellationToken cancellation = default);
}
