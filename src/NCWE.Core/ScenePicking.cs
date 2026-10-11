// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;

namespace NCWE.Core;

public readonly record struct Ray3(Vector3 Origin, Vector3 Direction);
public static class ScenePicking
{
    public static Ray3 ViewRay(Vector2 pixel, Vector2 size, Matrix4x4 view, Matrix4x4 projection)
    {
        if (!Matrix4x4.Invert(view * projection, out var inverse)) throw new InvalidOperationException("Camera matrix is singular.");
        float x = pixel.X / size.X * 2 - 1, y = 1 - pixel.Y / size.Y * 2;
        var a = Vector4.Transform(new Vector4(x, y, 1, 1), inverse);
        var b = Vector4.Transform(new Vector4(x, y, 0, 1), inverse);
        var origin = new Vector3(a.X, a.Y, a.Z) / a.W;
        var far = new Vector3(b.X, b.Y, b.Z) / b.W;
        return new(origin, Vector3.Normalize(far - origin));
    }

    public static float? Intersect(Ray3 ray, MeshAsset mesh, Matrix4x4 model, uint? lod = null, bool frontOnly = false) => IntersectPart(ray, mesh, model, lod, frontOnly)?.Distance;

    // The nearest hit and the part it is on (its index in mesh.Parts: the part a material slot dresses).
    // frontOnly: as the game draws faces, the back of a one-sided part is not there (MeshPart.OneSided;
    // fronts wind clockwise seen from outside, the other way under a mirroring transform).
    public static (float Distance, int Part)? IntersectPart(Ray3 ray, MeshAsset mesh, Matrix4x4 model, uint? lod = null, bool frontOnly = false)
    {
        if (!Matrix4x4.Invert(model, out var inverse)) return null;
        bool mirrored = frontOnly && model.GetDeterminant() < 0;
        var local = new Ray3(Vector3.Transform(ray.Origin, inverse), Vector3.TransformNormal(ray.Direction, inverse));
        if (!Bounds(local, mesh.Bounds)) return null;
        float closest = float.PositiveInfinity; int hitPart = -1;
        uint mask = lod ?? mesh.LodMasks[0];
        for (int index = 0; index < mesh.Parts.Length; index++)
        {
            var part = mesh.Parts[index];
            if (part.LodMask != mask) continue;
            bool culls = frontOnly && part.OneSided;
            for (int i = 0; i < part.Indices.Length; i += 3)
            {
                var a = part.Vertices[part.Indices[i]].Position;
                var e1 = part.Vertices[part.Indices[i + 1]].Position - a;
                var e2 = part.Vertices[part.Indices[i + 2]].Position - a;
                var p = Vector3.Cross(local.Direction, e2);
                var determinant = Vector3.Dot(e1, p);
                if (Math.Abs(determinant) < 1e-8f) continue;
                // A positive determinant: the ray reaches the triangle from its counter-clockwise side, the back.
                if (culls && (determinant > 0) != mirrored) continue;
                var t = local.Origin - a;
                var u = Vector3.Dot(t, p) / determinant;
                if (u < 0 || u > 1) continue;
                var q = Vector3.Cross(t, e1);
                var v = Vector3.Dot(local.Direction, q) / determinant;
                if (v < 0 || u + v > 1) continue;
                var distance = Vector3.Dot(e2, q) / determinant;
                if (distance >= 0 && distance < closest) { closest = distance; hitPart = index; }
            }
        }
        return float.IsFinite(closest) ? (closest, hitPart) : null;
    }

    // Where a ray enters an axis-aligned box given by its center and half size (0 when it starts
    // inside); null when it misses. The cheap test that comes before the exact one.
    public static float? Enter(Ray3 ray, Vector3 center, Vector3 extents)
    {
        float near = 0, far = float.PositiveInfinity;
        for (int axis = 0; axis < 3; axis++)
        {
            float origin = ray.Origin[axis], direction = ray.Direction[axis], min = center[axis] - extents[axis], max = center[axis] + extents[axis];
            if (Math.Abs(direction) < 1e-10f)
            { if (origin < min || origin > max) return null; continue; }
            float a = (min - origin) / direction, b = (max - origin) / direction;
            near = Math.Max(near, Math.Min(a, b)); far = Math.Min(far, Math.Max(a, b));
            if (far < near) return null;
        }
        return near;
    }

    // The nearest exact hit among candidates whose boxes the ray enters: exact tests run from the
    // nearest box out and stop once the next box starts beyond the best hit (most never run).
    public static (int Index, float Distance)? Nearest(List<(int Index, float Enter)> boxes, Func<int, float?> exact)
    {
        boxes.Sort((a, b) => a.Enter.CompareTo(b.Enter));
        float best = float.PositiveInfinity; int found = -1;
        foreach (var (index, enter) in boxes)
        {
            if (enter > best) break;
            if (exact(index) is { } d && d < best) { best = d; found = index; }
        }
        return found >= 0 ? (found, best) : null;
    }

    private static bool Bounds(Ray3 ray, Bounds3 bounds)
    {
        float near = 0, far = float.PositiveInfinity;
        for (int axis = 0; axis < 3; axis++)
        {
            float origin = ray.Origin[axis], direction = ray.Direction[axis];
            if (Math.Abs(direction) < 1e-10f)
            { if (origin < bounds.Min[axis] || origin > bounds.Max[axis]) return false; continue; }
            float a = (bounds.Min[axis] - origin) / direction, b = (bounds.Max[axis] - origin) / direction;
            near = Math.Max(near, Math.Min(a, b)); far = Math.Min(far, Math.Max(a, b));
            if (far < near) return false;
        }
        return true;
    }
}
