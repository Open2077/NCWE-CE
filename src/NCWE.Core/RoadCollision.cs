// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Numerics;

namespace NCWE.Core;

// Collision for a bent road piece. The game cooks none for a worldBendedMeshNode (the player sinks
// through editor roads), so the export adds a worldCollisionNode at the node's origin. Between
// consecutive frames (one per bone, about 3 m apart), boxes follow the piece's top surface, slope
// and banking included: across the width, one box per run of equal height (carriageway, raised
// sidewalks, curbs), its top on the surface and half a metre thick.
public static class RoadCollision
{
    private const float Step = .5f, Thickness = .5f, Tolerance = .05f, Probe = 8, Overlap = .03f;
    private static readonly float[] Rows = [.15f, .5f, .85f];

    private readonly record struct Triangle(Vector3 A, Vector3 B, Vector3 C);

    // Node-space (game) boxes for a bent piece: its mesh (editor view space, node-local, as
    // RoadBending.Deform returns it) and the frames it was bent along (node space).
    public static ImmutableArray<CollisionShape> Shapes(MeshAsset bent, IReadOnlyList<Matrix4x4> frames)
    {
        var triangles = Triangles(bent);
        if (triangles.Length == 0 || frames.Count < 2) return [];
        var shapes = ImmutableArray.CreateBuilder<CollisionShape>();
        for (int i = 0; i + 1 < frames.Count; i++)
        {
            var p0 = frames[i].Translation; var along = frames[i + 1].Translation - p0;
            float length = along.Length();
            if (length < .05f) continue;
            // The slice's frame: y along the road, x across it (the frames' width axis), z up.
            var y = along / length;
            var side = Row(frames[i], 0) + Row(frames[i + 1], 0);
            side -= y * Vector3.Dot(side, y);
            if (side.LengthSquared() < 1e-8f) continue;
            var x = Vector3.Normalize(side); var z = Vector3.Cross(x, y);
            if (z.Z < 0) { x = -x; z = -z; }
            // The slice is a trapezoid in curves: it starts and ends along each frame's own width
            // axis (the outer side longer than the inner one).
            var x0 = Width(frames[i], x); var x1 = Width(frames[i + 1], x); var p1 = frames[i + 1].Translation;
            // The triangles near this slice and its width.
            var near = new List<Triangle>(); float wMin = float.MaxValue, wMax = float.MinValue;
            foreach (var t in triangles)
            {
                float a = Vector3.Dot(t.A - p0, y), b = Vector3.Dot(t.B - p0, y), c = Vector3.Dot(t.C - p0, y);
                if (MathF.Max(a, MathF.Max(b, c)) < -1 || MathF.Min(a, MathF.Min(b, c)) > length + 1) continue;
                near.Add(t);
                // Pieces have few vertices (long triangles across slices): every vertex of a
                // nearby triangle bounds the width; bins the surface does not reach find no hit.
                foreach (var v in (ReadOnlySpan<Vector3>)[t.A, t.B, t.C])
                {
                    float w = Vector3.Dot(v - p0, x);
                    wMin = MathF.Min(wMin, w); wMax = MathF.Max(wMax, w);
                }
            }
            if (near.Count == 0 || wMax - wMin < Step) continue;
            // The top surface across the width (highest hit of three rows along the slice).
            int bins = (int)MathF.Ceiling((wMax - wMin) / Step);
            var tops = new float?[bins];
            for (int b = 0; b < bins; b++)
            {
                float w = MathF.Min(wMin + (b + .5f) * Step, wMax - .05f);
                foreach (float row in Rows)
                    if (Hit(near, p0 + y * (length * row) + x * w + z * Probe, -z) is { } d && d < 2 * Probe)
                        tops[b] = MathF.Max(tops[b] ?? float.MinValue, Probe - d);
            }
            float lowest = tops.Where(h => h is not null).Select(h => h!.Value).DefaultIfEmpty(0).Min();
            var rotation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1)));
            // A run's box encloses its part of the trapezoid: consecutive boxes leave no gap on the
            // outer side of a curve (they overlap on the inner side, at the same height).
            void Box(int first, int last, float top)
            {
                float from = wMin + first * Step, to = MathF.Min(wMin + (last + 1) * Step, wMax);
                float start = MathF.Min(Vector3.Dot(x0 * from, y), Vector3.Dot(x0 * to, y)) - Overlap;
                float end = MathF.Max(Vector3.Dot(p1 + x1 * from - p0, y), Vector3.Dot(p1 + x1 * to - p0, y)) + Overlap;
                // A last bin cut to nothing by the edge (a width just over a whole number of bins).
                if (to - from < .02f || end - start < .02f) return;
                var center = p0 + y * ((start + end) / 2) + x * ((from + to) / 2) + z * (top - Thickness / 2);
                shapes.Add(new CollisionShape(CollisionShapeKind.Box, center, rotation, new((to - from) / 2, (end - start) / 2, Thickness / 2))
                    { Material = top > lowest + .08f ? "concrete.physmat" : "asphalt.physmat" });
            }
            // Runs of equal height become one box each.
            int start = -1; float runTop = 0, runLow = 0;
            for (int b = 0; b <= bins; b++)
            {
                float? h = b < bins ? tops[b] : null;
                if (start >= 0 && (h is null || MathF.Abs(h.Value - runLow) > Tolerance || MathF.Abs(h.Value - runTop) > Tolerance))
                { Box(start, b - 1, runTop); start = -1; }
                if (h is { } height && start < 0) { start = b; runTop = runLow = height; }
                else if (h is { } more) { runTop = MathF.Max(runTop, more); runLow = MathF.Min(runLow, more); }
            }
        }
        return shapes.ToImmutable();
    }

    // The most detailed LOD's triangles in game space.
    private static Triangle[] Triangles(MeshAsset mesh)
    {
        if (mesh.Parts.Length == 0) return [];
        uint best = mesh.Parts.Min(p => p.LodMask == 0 ? uint.MaxValue : p.LodMask & (~p.LodMask + 1));
        var result = new List<Triangle>();
        foreach (var part in mesh.Parts.Where(p => p.LodMask == 0 || (p.LodMask & best) != 0))
            for (int i = 0; i + 2 < part.Indices.Length; i += 3)
                result.Add(new(Coordinates.ViewToGame(part.Vertices[part.Indices[i]].Position), Coordinates.ViewToGame(part.Vertices[part.Indices[i + 1]].Position),
                    Coordinates.ViewToGame(part.Vertices[part.Indices[i + 2]].Position)));
        return [.. result];
    }

    // Distance to the nearest triangle along a ray (Möller–Trumbore, both faces).
    private static float? Hit(List<Triangle> triangles, Vector3 origin, Vector3 direction)
    {
        float? best = null;
        foreach (var t in triangles)
        {
            var e1 = t.B - t.A; var e2 = t.C - t.A;
            var p = Vector3.Cross(direction, e2); float det = Vector3.Dot(e1, p);
            if (MathF.Abs(det) < 1e-9f) continue;
            float inv = 1 / det; var s = origin - t.A;
            float u = Vector3.Dot(s, p) * inv; if (u < 0 || u > 1) continue;
            var q = Vector3.Cross(s, e1); float v = Vector3.Dot(direction, q) * inv; if (v < 0 || u + v > 1) continue;
            float d = Vector3.Dot(e2, q) * inv;
            if (d > 0 && (best is null || d < best)) best = d;
        }
        return best;
    }

    // A frame's width axis, unit length, on the same side as `along`.
    private static Vector3 Width(Matrix4x4 frame, Vector3 along)
    {
        var w = Row(frame, 0);
        if (w.LengthSquared() < 1e-10f) return along;
        w = Vector3.Normalize(w);
        return Vector3.Dot(w, along) < 0 ? -w : w;
    }

    private static Vector3 Row(Matrix4x4 m, int row) => row == 0 ? new(m.M11, m.M12, m.M13) : row == 1 ? new(m.M21, m.M22, m.M23) : new(m.M31, m.M32, m.M33);
}
