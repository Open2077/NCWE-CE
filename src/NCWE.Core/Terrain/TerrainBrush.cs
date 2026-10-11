// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;

namespace NCWE.Core.Terrain;

public enum TerrainBrushKind
{
    Raise,      // lifts the ground (Strength: metres at the centre per stamp)
    Lower,      // digs it down
    Smooth,     // evens out bumps (Strength: 0–1 per stamp)
    Flatten,    // levels to a height (the one under the stroke's start, or TargetHeight)
    Mountain,   // raises rugged peaks: ridged noise (Strength: metres at the centre)
    Roughen,    // adds natural unevenness (Strength: metres of amplitude)
    Hole,       // cuts the ground open (meshes and collision); Erase fills it again
    Paint,      // paints a material layer of the terrain (Strength: 0–1 per stamp)
    Restore,    // brings back the game's own terrain (heights, holes, paint and plants)
    Terrace,    // cuts the slopes into steps (NoiseScale: their height; Strength: 0–1 per stamp)
    Erode,      // water erosion: droplets run down, dig gullies and leave sediment (Strength: 0–1)
    Ramp,       // a straight slope from the stroke's start to the cursor, Radius wide on each side
    ClearPlants // removes the game's plants (Erase brings them back)
}

// A brush: footprint, intensity and the parameters its kind reads.
public sealed record TerrainBrush(TerrainBrushKind Kind, float Radius = 8, float Strength = 1, float Hardness = .35f)
{
    // Paint: the multilayer setup's layer to lay down.
    public int Layer { get; init; } = 2;
    // Flatten: the height to level to (null: the ground under the stroke's first stamp).
    public float? TargetHeight { get; init; }
    // Mountain and Roughen: size of the features, metres.
    public float NoiseScale { get; init; } = 32;
    public int Seed { get; init; } = 1;
    // Hole: fill instead of cut. Raise/Lower swap, Paint removes the layer.
    public bool Erase { get; init; }
    // Paint: where it lays down, by the ground's slope in degrees (0: everywhere; 30: on slopes
    // steeper than 30°; -30: on ground flatter than 30°).
    public float PaintSlope { get; init; }

    // Paint's weight on ground of this slope (degrees), easing over 4° around the limit.
    public float SlopeWeight(float slope)
    {
        if (PaintSlope == 0) return 1;
        float limit = MathF.Abs(PaintSlope);
        float t = Math.Clamp((slope - (limit - 4)) / 8, 0, 1);
        t = t * t * (3 - 2 * t);
        return PaintSlope > 0 ? t : 1 - t;
    }

    // Terrace: the height a slope's steps bring a height to (flat treads, short risers).
    public static float Terraced(float height, float step)
    {
        float k = height / step, floor = MathF.Floor(k);
        float t = Math.Clamp((k - floor - .7f) / .3f, 0, 1);
        return (floor + t * t * (3 - 2 * t)) * step;
    }

    public const float MinRadius = .5f, MaxRadius = 256;

    // 1 inside the hard core, easing to 0 at the edge.
    public float Falloff(float distance)
    {
        if (distance >= Radius) return 0;
        float core = Math.Clamp(Hardness, 0, .99f) * Radius;
        if (distance <= core) return 1;
        float t = (distance - core) / (Radius - core);
        return 1 - t * t * (3 - 2 * t);
    }

    public TerrainBrush Validated() => this with
    {
        Radius = Math.Clamp(float.IsFinite(Radius) ? Radius : 8, MinRadius, MaxRadius),
        Strength = float.IsFinite(Strength) ? Math.Clamp(Strength, -500, 500) : 1,
        Hardness = float.IsFinite(Hardness) ? Math.Clamp(Hardness, 0, 1) : .35f,
        NoiseScale = float.IsFinite(NoiseScale) ? Math.Clamp(NoiseScale, 1, 2000) : 32,
        Layer = Math.Clamp(Layer, 0, 31),
        PaintSlope = float.IsFinite(PaintSlope) ? Math.Clamp(PaintSlope, -89, 89) : 0
    };
}

// Deterministic 2D gradient noise for the Mountain and Roughen brushes.
public static class TerrainNoise
{
    private static float Hash(int x, int y, int seed)
    {
        uint h = (uint)(x * 374761393 + y * 668265263 + seed * 2147483647);
        h = (h ^ (h >> 13)) * 1274126177u;
        return (h ^ (h >> 16)) / (float)uint.MaxValue;
    }

    private static Vector2 Gradient(int x, int y, int seed)
    {
        float a = Hash(x, y, seed) * MathF.Tau;
        return new(MathF.Cos(a), MathF.Sin(a));
    }

    // Perlin-style noise in about [-0.7, 0.7].
    public static float Perlin(float x, float y, int seed)
    {
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        static float Fade(float t) => t * t * t * (t * (t * 6 - 15) + 10);
        float Dot(int ix, int iy) => Vector2.Dot(Gradient(ix, iy, seed), new(x - ix, y - iy));
        float u = Fade(fx), v = Fade(fy);
        float a = float.Lerp(Dot(x0, y0), Dot(x0 + 1, y0), u), b = float.Lerp(Dot(x0, y0 + 1), Dot(x0 + 1, y0 + 1), u);
        return float.Lerp(a, b, v);
    }

    // Fractal noise in [0, 1].
    public static float Fbm(float x, float y, int seed, int octaves = 5)
    {
        float sum = 0, amplitude = .5f, total = 0;
        for (int o = 0; o < octaves; o++)
        {
            sum += amplitude * Perlin(x, y, seed + o * 101);
            total += amplitude; amplitude *= .5f; x *= 2.03f; y *= 2.03f;
        }
        return Math.Clamp(sum / total / 1.4f + .5f, 0, 1);
    }

    // Ridged multifractal in [0, 1]: sharp crests, as mountain ranges have.
    public static float Ridged(float x, float y, int seed, int octaves = 5)
    {
        float sum = 0, amplitude = .5f, weight = 1, total = 0;
        for (int o = 0; o < octaves; o++)
        {
            float n = 1 - MathF.Abs(Perlin(x, y, seed + o * 131) * 1.4f);
            n *= n * weight;
            weight = Math.Clamp(n * 1.5f, 0, 1);
            sum += n * amplitude; total += amplitude;
            amplitude *= .5f; x *= 2.07f; y *= 2.07f;
        }
        return Math.Clamp(sum / total, 0, 1);
    }
}
