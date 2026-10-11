// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;

namespace NCWE.Core;

public readonly record struct CameraFrame(Vector3 Origin, Matrix4x4 View, Matrix4x4 Projection)
{
    public Matrix4x4 ViewProjection => View * Projection;
    public Matrix4x4 Relative(Matrix4x4 world) { world.Translation -= Origin; return world; }
    public Matrix4x4 Absolute(Matrix4x4 relative) { relative.Translation += Origin; return relative; }
}

// Center/extents avoids losing small extents when min/max are added to a large
// world translation. Instances keep these immutable until their transform changes.
public readonly record struct InstanceBounds(Vector3 Center, Vector3 Extents)
{
    public static InstanceBounds From(Bounds3 local, Matrix4x4 model)
    {
        var e = local.Size * .5f;
        return new(Vector3.Transform(local.Center, model), new(
            Math.Abs(model.M11) * e.X + Math.Abs(model.M21) * e.Y + Math.Abs(model.M31) * e.Z,
            Math.Abs(model.M12) * e.X + Math.Abs(model.M22) * e.Y + Math.Abs(model.M32) * e.Z,
            Math.Abs(model.M13) * e.X + Math.Abs(model.M23) * e.Y + Math.Abs(model.M33) * e.Z));
    }
    public float ProjectedDiameter(CameraFrame camera, int viewportHeight)
    {
        var view = Vector3.Transform(Center - camera.Origin, camera.View);
        float radius = Extents.Length();
        if (-view.Z <= radius) return float.PositiveInfinity;
        return radius * viewportHeight * Math.Abs(camera.Projection.M22) / (-view.Z - radius);
    }
}

public readonly struct ViewFrustum
{
    private readonly Vector4 _left, _right, _bottom, _top, _zero, _one;
    public ViewFrustum(Matrix4x4 m)
    {
        var x = new Vector4(m.M11, m.M21, m.M31, m.M41);
        var y = new Vector4(m.M12, m.M22, m.M32, m.M42);
        var z = new Vector4(m.M13, m.M23, m.M33, m.M43);
        var w = new Vector4(m.M14, m.M24, m.M34, m.M44);
        _left = Normalize(w + x); _right = Normalize(w - x);
        _bottom = Normalize(w + y); _top = Normalize(w - y);
        // D3D clip interval is 0 <= z <= w, including reverse depth.
        _zero = Normalize(z); _one = Normalize(w - z);
    }
    private static Vector4 Normalize(Vector4 p)
    { float length = new Vector3(p.X, p.Y, p.Z).Length(); return length > 0 ? p / length : p; }
    public bool Intersects(InstanceBounds bounds, Vector3 origin)
    {
        var center = bounds.Center - origin;
        return Inside(_left, center, bounds.Extents) && Inside(_right, center, bounds.Extents)
            && Inside(_bottom, center, bounds.Extents) && Inside(_top, center, bounds.Extents)
            && Inside(_zero, center, bounds.Extents) && Inside(_one, center, bounds.Extents);
    }
    private static bool Inside(Vector4 p, Vector3 center, Vector3 extents)
        => p.X * center.X + p.Y * center.Y + p.Z * center.Z + p.W
            + Math.Abs(p.X) * extents.X + Math.Abs(p.Y) * extents.Y + Math.Abs(p.Z) * extents.Z >= -.0005f;
}

public static class MeshLodPolicy
{
    // Editor screen-size policy, not a claim to reproduce RED4's runtime LOD.
    // Always select an available source mask. Selected objects retain full detail.
    public static uint Choose(ReadOnlySpan<uint> masks, float diameterPixels, bool selected, uint? previous = null)
    {
        if (masks.IsEmpty) throw new ArgumentException("A mesh needs an available LOD group.", nameof(masks));
        if (selected || float.IsNaN(diameterPixels)) return masks[0];
        int target = 0;
        while (target + 1 < masks.Length && diameterPixels < Threshold(target)) target++;
        int old = previous.HasValue ? masks.IndexOf(previous.Value) : -1;
        if (old >= 0 && target > old && diameterPixels >= Threshold(old) * .85f) return masks[old];
        if (old > 0 && target < old && diameterPixels <= Threshold(old - 1) * 1.15f) return masks[old];
        return masks[target];
    }
    // 200 × 0.4^index, precomputed: Choose runs for every visible instance every frame.
    private static readonly float[] Thresholds = Enumerable.Range(0, 32).Select(i => 200 * MathF.Pow(.4f, i)).ToArray();
    private static float Threshold(int index) => Thresholds[Math.Min(index, Thresholds.Length - 1)];
}
