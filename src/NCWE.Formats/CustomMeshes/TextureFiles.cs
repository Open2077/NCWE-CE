// SPDX-License-Identifier: GPL-3.0-only
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace NCWE.Formats.CustomMeshes;

// An RGBA8 image, rows top to bottom.
public sealed record RgbaImage(int Width, int Height, byte[] Pixels)
{
    public bool HasAlpha()
    {
        for (int i = 3; i < Pixels.Length; i += 4) if (Pixels[i] < 250) return true;
        return false;
    }
}

// Reading the images a model refers to, and fitting them to what the game expects.
public static class TextureFiles
{
    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".tga", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".pbm"];
    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    // The editor's surface shader cuts out alpha below one half: a cut-out material keeps its
    // transparent texels (alpha against its own threshold, like the game's AlphaThreshold), any other
    // material draws opaque whatever its texture's alpha.
    public static RgbaImage PreviewAlpha(RgbaImage image, bool cutout, float threshold)
    {
        var pixels = (byte[])image.Pixels.Clone();
        byte limit = (byte)Math.Clamp(MathF.Round(threshold * 255), 0, 255);
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = !cutout || pixels[i] >= limit ? (byte)255 : (byte)0;
        return image with { Pixels = pixels };
    }

    public static RgbaImage Load(string path)
    {
        using var image = Image.Load<Rgba32>(path);
        return ToRgba(image);
    }

    public static RgbaImage Load(byte[] encoded)
    {
        using var image = Image.Load<Rgba32>(encoded);
        return ToRgba(image);
    }

    // Header-only read for listings.
    public static (int Width, int Height)? Size(string path)
    {
        try { var info = Image.Identify(path); return (info.Width, info.Height); }
        catch (Exception ex) when (ex is IOException or UnknownImageFormatException or InvalidImageContentException or NotSupportedException) { return null; }
    }

    private static RgbaImage ToRgba(Image<Rgba32> image)
    {
        var pixels = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(pixels);
        return new(image.Width, image.Height, pixels);
    }

    // Power-of-two sides (block compression and mip chains), at most maxSize, at least 4.
    public static RgbaImage Fit(RgbaImage source, int maxSize)
    {
        static int Pow2(int v, int max) { int p = 4; while (p * 2 <= Math.Min(v * 1.5, max)) p *= 2; return p; }
        int w = Pow2(source.Width, maxSize), h = Pow2(source.Height, maxSize);
        if (w == source.Width && h == source.Height) return source;
        using var image = Image.LoadPixelData<Rgba32>(source.Pixels, source.Width, source.Height);
        image.Mutate(x => x.Resize(new ResizeOptions { Size = new Size(w, h), Mode = ResizeMode.Stretch, Sampler = KnownResamplers.Lanczos3 }));
        return ToRgba(image);
    }

    // A smaller copy for previews (keeps the aspect ratio).
    public static RgbaImage Shrink(RgbaImage source, int maxSide)
    {
        if (Math.Max(source.Width, source.Height) <= maxSide) return source;
        float k = maxSide / (float)Math.Max(source.Width, source.Height);
        using var image = Image.LoadPixelData<Rgba32>(source.Pixels, source.Width, source.Height);
        image.Mutate(x => x.Resize(Math.Max(1, (int)(source.Width * k)), Math.Max(1, (int)(source.Height * k))));
        return ToRgba(image);
    }

    // One channel copied into R, G and B (roughness, metalness masks), optionally inverted.
    public static RgbaImage Channel(RgbaImage source, int channel, bool invert)
    {
        var pixels = new byte[source.Pixels.Length];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte v = source.Pixels[i + Math.Clamp(channel, 0, 3)];
            if (invert) v = (byte)(255 - v);
            pixels[i] = pixels[i + 1] = pixels[i + 2] = v; pixels[i + 3] = 255;
        }
        return source with { Pixels = pixels };
    }

    // Colour times a tint (sRGB values), alpha times the tint's alpha.
    public static RgbaImage Tint(RgbaImage source, System.Numerics.Vector4 tint)
    {
        if (tint == System.Numerics.Vector4.One) return source;
        var pixels = (byte[])source.Pixels.Clone();
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)Math.Clamp(pixels[i] * tint.X, 0, 255); pixels[i + 1] = (byte)Math.Clamp(pixels[i + 1] * tint.Y, 0, 255);
            pixels[i + 2] = (byte)Math.Clamp(pixels[i + 2] * tint.Z, 0, 255); pixels[i + 3] = (byte)Math.Clamp(pixels[i + 3] * tint.W, 0, 255);
        }
        return source with { Pixels = pixels };
    }

    // The colours of the visible texels spread into the transparent ones (whose alpha stays as it
    // is), nearest first. A texel barely there (resampling leaves some) has no colour to trust either. Filtering and smaller mip levels then mix a cut-out's edge with its own
    // colours, not with what the image editor left under the transparency (black or white: a dark
    // or pale fringe around the picture).
    public static RgbaImage Bleed(RgbaImage source)
    {
        int w = source.Width, h = source.Height, count = w * h;
        var pixels = (byte[])source.Pixels.Clone();
        var known = new bool[count];
        int visible = 0;
        for (int i = 0; i < count; i++) if (pixels[i * 4 + 3] >= 8) { known[i] = true; visible++; }
        if (visible == 0 || visible == count) return source;
        // Breadth first from the picture's edge: each ring takes the mean of its neighbours already coloured.
        var ring = new List<int>(); var next = new List<int>(); var queued = new bool[count];
        ReadOnlySpan<int> dx = [-1, 1, 0, 0, -1, 1, -1, 1], dy = [0, 0, -1, 1, -1, -1, 1, 1];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (known[i]) continue;
                for (int k = 0; k < 4; k++)
                {
                    int nx = x + dx[k], ny = y + dy[k];
                    if (nx >= 0 && ny >= 0 && nx < w && ny < h && known[ny * w + nx]) { ring.Add(i); queued[i] = true; break; }
                }
            }
        while (ring.Count > 0)
        {
            foreach (int i in ring)
            {
                int x = i % w, y = i / w, r = 0, g = 0, b = 0, n = 0;
                for (int k = 0; k < 8; k++)
                {
                    int nx = x + dx[k], ny = y + dy[k];
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int j = ny * w + nx;
                    if (known[j]) { r += pixels[j * 4]; g += pixels[j * 4 + 1]; b += pixels[j * 4 + 2]; n++; }
                }
                if (n > 0) { pixels[i * 4] = (byte)(r / n); pixels[i * 4 + 1] = (byte)(g / n); pixels[i * 4 + 2] = (byte)(b / n); }
            }
            foreach (int i in ring) known[i] = true;
            next.Clear();
            foreach (int i in ring)
            {
                int x = i % w, y = i / w;
                for (int k = 0; k < 4; k++)
                {
                    int nx = x + dx[k], ny = y + dy[k];
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int j = ny * w + nx;
                    if (!known[j] && !queued[j]) { queued[j] = true; next.Add(j); }
                }
            }
            (ring, next) = (next, ring);
        }
        return source with { Pixels = pixels };
    }

    public static RgbaImage FlipGreen(RgbaImage source)
    {
        var pixels = (byte[])source.Pixels.Clone();
        for (int i = 1; i < pixels.Length; i += 4) pixels[i] = (byte)(255 - pixels[i]);
        return source with { Pixels = pixels };
    }

    // Upside down: the game's .xbm files keep the bottom row first.
    public static RgbaImage FlipRows(RgbaImage source)
    {
        var pixels = new byte[source.Pixels.Length];
        int row = source.Width * 4;
        for (int y = 0; y < source.Height; y++) source.Pixels.AsSpan(y * row, row).CopyTo(pixels.AsSpan((source.Height - 1 - y) * row));
        return source with { Pixels = pixels };
    }

    public static void SavePng(RgbaImage source, string path)
    {
        using var image = Image.LoadPixelData<Rgba32>(source.Pixels, source.Width, source.Height);
        image.SaveAsPng(path);
    }
}
