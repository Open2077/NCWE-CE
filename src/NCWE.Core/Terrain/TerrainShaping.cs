// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;

namespace NCWE.Core.Terrain;

// A corridor along which the ground is brought to a road (RoadGround): within `HalfWidth` +
// `Margin` of its axis the ground lies `Below` metres under the road's surface; beyond, a bank at
// `Slope` degrees leads back to the ground (fill where the road is above it, cut where below).
// `Line` is the road's axis in game space (its surface height in Z), densely sampled.
public sealed class TerrainShape(string key, Vector3[] line, float halfWidth, float slope = 33, float margin = 1.5f, bool clearPlants = true)
{
    // The ground stays this far under the road's surface (the road mesh covers it).
    public const float Below = .2f;
    // Plants within this distance of the road's edge go too.
    public const float PlantMargin = .5f;
    public string Key { get; } = key;
    public Vector3[] Line { get; } = line;
    public float HalfWidth { get; } = halfWidth;
    public float Slope { get; } = slope;
    public float Margin { get; } = margin;
    public bool ClearPlants { get; } = clearPlants;
    public float Inner => HalfWidth + Margin;
    public float Tangent => MathF.Tan(Math.Clamp(Slope, 5, 85) * MathF.PI / 180);
}

public static class TerrainShaping
{
    // How far the bank's crease at its foot and top is rounded (metres of height).
    private const float Round = .6f;

    // The ground at `distance` from a road whose surface is at `roadHeight` there.
    public static float Apply(TerrainShape shape, float ground, float distance, float roadHeight)
    {
        float target = roadHeight - TerrainShape.Below;
        float beyond = distance - shape.Inner;
        if (beyond <= 0) return target;
        float rise = beyond * shape.Tangent;
        return ground < target ? SmoothMax(ground, target - rise) : SmoothMin(ground, target + rise);
    }

    private static float SmoothMin(float a, float b)
    {
        float h = MathF.Max(Round - MathF.Abs(a - b), 0) / Round;
        return MathF.Min(a, b) - h * h * Round * .25f;
    }

    private static float SmoothMax(float a, float b) => -SmoothMin(-a, -b);

    // How far from its axis a shape can change the ground: its corridor and the widest bank its
    // height above or below the ground needs (`heightDifference`, the largest along it).
    public static float Reach(TerrainShape shape, float heightDifference)
        => shape.Inner + Math.Min(150, (MathF.Abs(heightDifference) + TerrainShape.Below + Round) / shape.Tangent) + 1;

    // The shape over one cell's lattice (0.5 m samples from the cell's minimum): for each sample
    // within `reach`, its distance to the axis seen from above and the road's height at the
    // closest point; null when the shape does not reach the cell.
    public static ShapeField? Rasterize(TerrainShape shape, TerrainCell cell, float reach)
    {
        var line = shape.Line;
        if (line.Length < 2) return null;
        var min = cell.Minimum;
        const int n = TerrainBase.Samples;
        float[]? distance = null, height = null;
        for (int s = 0; s + 1 < line.Length; s++)
        {
            var a = new Vector2(line[s].X, line[s].Y); var b = new Vector2(line[s + 1].X, line[s + 1].Y);
            var lo = Vector2.Min(a, b) - new Vector2(reach) - min; var hi = Vector2.Max(a, b) + new Vector2(reach) - min;
            int x0 = Math.Max(0, (int)MathF.Floor(lo.X / TerrainBase.Step)), x1 = Math.Min(n - 1, (int)MathF.Ceiling(hi.X / TerrainBase.Step));
            int y0 = Math.Max(0, (int)MathF.Floor(lo.Y / TerrainBase.Step)), y1 = Math.Min(n - 1, (int)MathF.Ceiling(hi.Y / TerrainBase.Step));
            if (x0 > x1 || y0 > y1) continue;
            var ab = b - a; float length2 = ab.LengthSquared();
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    var p = min + new Vector2(x * TerrainBase.Step, y * TerrainBase.Step);
                    float t = length2 < 1e-8f ? 0 : Math.Clamp(Vector2.Dot(p - a, ab) / length2, 0, 1);
                    float d = Vector2.Distance(p, a + ab * t);
                    if (d > reach) continue;
                    if (distance is null)
                    {
                        distance = new float[n * n]; height = new float[n * n];
                        Array.Fill(distance, float.PositiveInfinity); Array.Fill(height, float.NaN);
                    }
                    int i = TerrainBase.Index(x, y);
                    if (d >= distance[i]) continue;
                    distance[i] = d; height![i] = float.Lerp(line[s].Z, line[s + 1].Z, t);
                }
        }
        return distance is null ? null : new ShapeField(shape, distance, height!);
    }

    // The ground at a lattice sample once every shape reaching it is applied (in their order).
    public static float Apply(IReadOnlyList<ShapeField> fields, int index, float ground)
    {
        foreach (var f in fields)
            if (!float.IsNaN(f.Height[index])) ground = Apply(f.Shape, ground, f.Distance[index], f.Height[index]);
        return ground;
    }

    // Whether a shape clears the plants of the 1 m quad whose centre is lattice sample `index`.
    public static bool ClearsPlants(IReadOnlyList<ShapeField> fields, int index)
    {
        foreach (var f in fields)
            if (f.Shape.ClearPlants && f.Distance[index] <= f.Shape.HalfWidth + TerrainShape.PlantMargin) return true;
        return false;
    }
}

// A shape over one cell's lattice (TerrainShaping.Rasterize).
public sealed record ShapeField(TerrainShape Shape, float[] Distance, float[] Height);
