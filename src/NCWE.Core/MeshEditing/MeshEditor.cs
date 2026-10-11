// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Numerics;

namespace NCWE.Core.MeshEditing;

// Applies mesh edit operations. Every operation is deterministic: replaying the same list on the
// same source gives the same vertices and triangles in the same order, which the operations that
// name vertices or faces by index rely on. Indices out of range (an edited source) are skipped.
public static class MeshEditor
{
    public static EditableMesh Apply(EditableMesh source, IEnumerable<MeshEditOp> ops)
    {
        var mesh = source.Clone();
        foreach (var op in ops) Apply(mesh, op);
        return mesh;
    }

    public static void Apply(EditableMesh mesh, MeshEditOp op)
    {
        switch (op)
        {
            case CutBoxOp cut: CutBox(mesh, cut); break;
            case TransformVerticesOp t: TransformVertices(mesh, t); break;
            case DeleteFacesOp d: DeleteFaces(mesh, d.Faces); break;
            case ExtrudeFacesOp e: Extrude(mesh, e); break;
            case SubdivideFacesOp s: Subdivide(mesh, s.Faces); break;
            case FlipFacesOp f: Flip(mesh, f.Faces); break;
            case NormalsOp n: Normals(mesh, n); break;
            case UvProjectOp p: UvProject(mesh, p); break;
            case UvTransformOp t: UvTransform(mesh, t); break;
            case UvMoveOp m: UvMove(mesh, m); break;
            case MoveFacesToPartOp m: MoveFacesToPart(mesh, m); break;
            case AddTrianglesOp a: AddTriangles(mesh, a); break;
            case TwoSidedOp t: TwoSided(mesh, t); break;
        }
    }

    // --- Selections -------------------------------------------------------------------------------

    // The valid, distinct faces of a part named by a selection.
    internal static SortedSet<int> FacesOf(EditableMesh mesh, ImmutableArray<PartIndices> selection, int part)
    {
        var set = new SortedSet<int>();
        if (selection.IsDefault || part >= mesh.Parts.Count) return set;
        int count = mesh.Parts[part].TriangleCount;
        foreach (var s in selection)
            if (s.Part == part && !s.Indices.IsDefault)
                foreach (int f in s.Indices) if (f >= 0 && f < count) set.Add(f);
        return set;
    }

    internal static HashSet<int> VerticesOf(EditableMesh mesh, ImmutableArray<PartIndices> selection, int part)
    {
        var set = new HashSet<int>();
        if (selection.IsDefault || part >= mesh.Parts.Count) return set;
        int count = mesh.Parts[part].VertexCount;
        foreach (var s in selection)
            if (s.Part == part && !s.Indices.IsDefault)
                foreach (int v in s.Indices) if (v >= 0 && v < count) set.Add(v);
        return set;
    }

    // Gives the selected faces their own copies of the vertices they share with other faces, so
    // changing those vertices (UVs, normals) leaves the other faces as they were. Returns the
    // vertices the selected faces use afterwards.
    internal static HashSet<int> Isolate(EditablePart part, SortedSet<int> faces)
    {
        var usedOutside = new HashSet<int>();
        for (int f = 0; f < part.TriangleCount; f++)
            if (!faces.Contains(f)) { usedOutside.Add(part.Indices[f * 3]); usedOutside.Add(part.Indices[f * 3 + 1]); usedOutside.Add(part.Indices[f * 3 + 2]); }
        var copies = new Dictionary<int, int>();
        var result = new HashSet<int>();
        foreach (int f in faces)
            for (int k = 0; k < 3; k++)
            {
                int v = part.Indices[f * 3 + k];
                if (usedOutside.Contains(v))
                {
                    if (!copies.TryGetValue(v, out int copy)) copies[v] = copy = part.AddCopy(v);
                    part.Indices[f * 3 + k] = v = copy;
                }
                result.Add(v);
            }
        return result;
    }

    // Vertices welded by position (positions within a millimetre share a key).
    internal static (long, long, long) Weld(Vector3 p) => ((long)MathF.Round(p.X * 1000), (long)MathF.Round(p.Y * 1000), (long)MathF.Round(p.Z * 1000));

    // --- Cut ----------------------------------------------------------------------------------------

    private const float PlaneEpsilon = 1e-5f;

    private static void CutBox(EditableMesh mesh, CutBoxOp op)
    {
        var rotation = Quaternion.Normalize(op.Rotation.LengthSquared() > 0 ? op.Rotation : Quaternion.Identity);
        var inverse = Quaternion.Conjugate(rotation);
        var half = Vector3.Abs(op.HalfSize);
        if (half.X <= 0 || half.Y <= 0 || half.Z <= 0) return;
        Vector3 Local(Vector3 p) => Vector3.Transform(p - op.Center, inverse);
        // Signed distance inside the half-space of each box face: >= 0 inside the box side.
        static float Distance(Vector3 u, Vector3 half, int plane) => plane switch
        {
            0 => half.X - u.X, 1 => half.X + u.X, 2 => half.Y - u.Y, 3 => half.Y + u.Y, 4 => half.Z - u.Z, _ => half.Z + u.Z
        };
        // What was removed, for the jambs: per part the cut area, and the extent of the removed
        // geometry in box space.
        var removedArea = new double[mesh.Parts.Count];
        Vector3 removedMin = new(float.PositiveInfinity), removedMax = new(float.NegativeInfinity);
        for (int partIndex = 0; partIndex < mesh.Parts.Count; partIndex++)
        {
            var part = mesh.Parts[partIndex];
            var local = new List<Vector3>(part.VertexCount);
            foreach (var p in part.Positions) local.Add(Local(p));
            var cache = new Dictionary<(int, int, int), int>();
            int Cross(int a, int b, int plane)
            {
                int lo = Math.Min(a, b), hi = Math.Max(a, b);
                if (cache.TryGetValue((lo, hi, plane), out int v)) return v;
                float dl = Distance(local[lo], half, plane), dh = Distance(local[hi], half, plane);
                float t = Math.Clamp(dl / (dl - dh), 0, 1);
                v = part.AddLerp(lo, hi, t);
                local.Add(Vector3.Lerp(local[lo], local[hi], t));
                cache[(lo, hi, plane)] = v;
                return v;
            }
            var indices = new List<int>(part.Indices.Count);
            var polygon = new List<int>(8); var inside = new List<int>(8); var outside = new List<int>(8);
            int triangles = part.TriangleCount;
            for (int f = 0; f < triangles; f++)
            {
                int a = part.Indices[f * 3], b = part.Indices[f * 3 + 1], c = part.Indices[f * 3 + 2];
                // Wholly outside one face of the box: kept as it is.
                bool outsideOne = false;
                for (int plane = 0; plane < 6 && !outsideOne; plane++)
                    outsideOne = Distance(local[a], half, plane) <= PlaneEpsilon && Distance(local[b], half, plane) <= PlaneEpsilon && Distance(local[c], half, plane) <= PlaneEpsilon;
                if (outsideOne) { indices.Add(a); indices.Add(b); indices.Add(c); continue; }
                polygon.Clear(); polygon.Add(a); polygon.Add(b); polygon.Add(c);
                for (int plane = 0; plane < 6 && polygon.Count >= 3; plane++)
                {
                    inside.Clear(); outside.Clear();
                    for (int i = 0; i < polygon.Count; i++)
                    {
                        int current = polygon[i], next = polygon[(i + 1) % polygon.Count];
                        float dc = Distance(local[current], half, plane), dn = Distance(local[next], half, plane);
                        int sc = dc > PlaneEpsilon ? 1 : dc < -PlaneEpsilon ? -1 : 0, sn = dn > PlaneEpsilon ? 1 : dn < -PlaneEpsilon ? -1 : 0;
                        if (sc >= 0) inside.Add(current);
                        if (sc <= 0) outside.Add(current);
                        if (sc * sn < 0) { int x = Cross(current, next, plane); inside.Add(x); outside.Add(x); }
                    }
                    // The part beyond this face of the box is kept; the rest goes on to the next face.
                    Fan(part, outside, indices);
                    (polygon, inside) = (inside, polygon);
                }
                if (polygon.Count >= 3)
                {
                    for (int i = 1; i + 1 < polygon.Count; i++)
                        removedArea[partIndex] += Vector3.Cross(part.Positions[polygon[i]] - part.Positions[polygon[0]], part.Positions[polygon[i + 1]] - part.Positions[polygon[0]]).Length() / 2;
                    foreach (int v in polygon) { removedMin = Vector3.Min(removedMin, local[v]); removedMax = Vector3.Max(removedMax, local[v]); }
                }
            }
            part.Indices = indices;
        }
        if (op.Jambs && float.IsFinite(removedMin.X)) AddJambs(mesh, op.Center, rotation, half, removedArea, removedMin, removedMax);
    }

    // Triangles of a convex polygon (degenerate ones left out).
    private static void Fan(EditablePart part, List<int> polygon, List<int> indices)
    {
        for (int i = 1; i + 1 < polygon.Count; i++)
        {
            int a = polygon[0], b = polygon[i], c = polygon[i + 1];
            if (a == b || b == c || a == c) continue;
            if (Vector3.Cross(part.Positions[b] - part.Positions[a], part.Positions[c] - part.Positions[a]).LengthSquared() < 1e-14f) continue;
            indices.Add(a); indices.Add(b); indices.Add(c);
        }
    }

    // The opening's inner faces where the cut went through a thickness: on each side of the box the
    // removed geometry reached, a quad from the front to the back of what was removed.
    private static void AddJambs(EditableMesh mesh, Vector3 center, Quaternion rotation, Vector3 half, double[] removedArea, Vector3 min, Vector3 max)
    {
        const float minimumThickness = .01f, reach = 1e-3f;
        if (max.Z - min.Z < minimumThickness) return;   // a single surface: no thickness to close
        int target = 0;
        for (int i = 1; i < removedArea.Length; i++) if (removedArea[i] > removedArea[target]) target = i;
        if (removedArea[target] <= 0) return;
        var part = mesh.Parts[target];
        int winding = part.WindingSign();
        float density = part.TexelDensity();
        Vector3 World(float x, float y, float z) => center + Vector3.Transform(new Vector3(x, y, z), rotation);
        Vector3 Axis(Vector3 v) => Vector3.Transform(v, rotation);
        // The wall's own vertices at the opening's edge lend each jamb their UVs, second UVs and
        // colours, continued over its depth at the wall's texel density (the texture turns the
        // corner): a jamb wears the wall's look, not a corner of its texture sheet or mask.
        var kept = part.Indices.Distinct().ToArray();
        (int Vertex, float Gap) Nearest(Vector3 p)
        {
            int best = -1; float gap = float.MaxValue;
            foreach (int k in kept) { float d = Vector3.DistanceSquared(part.Positions[k], p); if (d < gap) { gap = d; best = k; } }
            return (best, MathF.Sqrt(gap));
        }
        // Each side: where it is (box space), the direction along it, and the normal facing the opening.
        void Side(bool present, Vector3 a, Vector3 b, Vector3 normal)
        {
            if (!present) return;
            // a and b: the side's ends at the front (min Z); the back is the same at max Z.
            var along = Axis(Vector3.Normalize(b - a)); var n = Axis(normal);
            float length = Vector3.Distance(a, b), depth = max.Z - min.Z;
            var corners = new[] { World(a.X, a.Y, min.Z), World(b.X, b.Y, min.Z), World(b.X, b.Y, max.Z), World(a.X, a.Y, max.Z) };
            // The surface the jamb starts from: the end (front or back) the wall's vertices are nearest.
            var (fa, ga) = Nearest(corners[0]); var (fb, gb) = Nearest(corners[1]);
            var (ba, gc) = Nearest(corners[3]); var (bb, gd) = Nearest(corners[2]);
            bool front = ga + gb <= gc + gd;
            int ma = front ? fa : ba, mb = front ? fb : bb;
            Vector2 ua = ma >= 0 ? part.Uv0[ma] : Vector2.Zero, ub = mb >= 0 ? part.Uv0[mb] : new Vector2(length * density, 0);
            if (Vector2.DistanceSquared(ua, ub) < 1e-12f) ub = ua + new Vector2(length * density, 0);
            var edge = ub - ua;
            var deep = Vector2.Normalize(new Vector2(-edge.Y, edge.X)) * (depth * density);
            var uvs = front ? new[] { ua, ub, ub + deep, ua + deep } : new[] { ua + deep, ub + deep, ub, ua };
            int[] models = [ma, mb, mb, ma];
            int[] v = new int[4];
            for (int i = 0; i < 4; i++) v[i] = part.AddVertex(corners[i], n, along, uvs[i], models[i]);
            AddQuad(part, v, n, winding);
        }
        Side(max.X >= half.X - reach, new(half.X, min.Y, 0), new(half.X, max.Y, 0), -Vector3.UnitX);
        Side(min.X <= -half.X + reach, new(-half.X, max.Y, 0), new(-half.X, min.Y, 0), Vector3.UnitX);
        Side(max.Y >= half.Y - reach, new(max.X, half.Y, 0), new(min.X, half.Y, 0), -Vector3.UnitY);
        Side(min.Y <= -half.Y + reach, new(min.X, -half.Y, 0), new(max.X, -half.Y, 0), Vector3.UnitY);
    }

    // Two triangles over four corners, wound so they face `normal` in this part's convention.
    private static void AddQuad(EditablePart part, int[] v, Vector3 normal, int winding)
    {
        var cross = Vector3.Cross(part.Positions[v[1]] - part.Positions[v[0]], part.Positions[v[2]] - part.Positions[v[0]]);
        bool forward = Vector3.Dot(cross, normal) * winding >= 0;
        if (forward) part.Indices.AddRange([v[0], v[1], v[2], v[0], v[2], v[3]]);
        else part.Indices.AddRange([v[0], v[2], v[1], v[0], v[3], v[2]]);
    }

    // --- Vertices and faces ---------------------------------------------------------------------------

    private static void TransformVertices(EditableMesh mesh, TransformVerticesOp op)
    {
        var m = op.Matrix;
        var linear = m with { M41 = 0, M42 = 0, M43 = 0 };
        var normalMatrix = Matrix4x4.Invert(linear, out var inverse) ? Matrix4x4.Transpose(inverse) : linear;
        for (int p = 0; p < mesh.Parts.Count; p++)
        {
            var part = mesh.Parts[p];
            foreach (int v in VerticesOf(mesh, op.Vertices, p))
            {
                part.Positions[v] = Vector3.Transform(part.Positions[v], m);
                part.Normals[v] = EditablePart.SafeNormalize(Vector3.TransformNormal(part.Normals[v], normalMatrix), part.Normals[v]);
                if (part.Tangents is { } tangents)
                {
                    var t = tangents[v];
                    var xyz = EditablePart.SafeNormalize(Vector3.TransformNormal(new Vector3(t.X, t.Y, t.Z), linear), new Vector3(t.X, t.Y, t.Z));
                    tangents[v] = new(xyz, t.W);
                }
            }
        }
    }

    private static void DeleteFaces(EditableMesh mesh, ImmutableArray<PartIndices> selection)
    {
        for (int p = 0; p < mesh.Parts.Count; p++)
        {
            var faces = FacesOf(mesh, selection, p);
            if (faces.Count == 0) continue;
            var part = mesh.Parts[p];
            var kept = new List<int>(part.Indices.Count);
            for (int f = 0; f < part.TriangleCount; f++)
                if (!faces.Contains(f)) kept.AddRange([part.Indices[f * 3], part.Indices[f * 3 + 1], part.Indices[f * 3 + 2]]);
            part.Indices = kept;
        }
    }

    private static void Flip(EditableMesh mesh, ImmutableArray<PartIndices> selection)
    {
        for (int p = 0; p < mesh.Parts.Count; p++)
        {
            var faces = FacesOf(mesh, selection, p);
            if (faces.Count == 0) continue;
            var part = mesh.Parts[p];
            foreach (int v in Isolate(part, faces))
            {
                part.Normals[v] = -part.Normals[v];
                if (part.Tangents is { } t) t[v] = t[v] with { W = -t[v].W };
            }
            foreach (int f in faces) (part.Indices[f * 3 + 1], part.Indices[f * 3 + 2]) = (part.Indices[f * 3 + 2], part.Indices[f * 3 + 1]);
        }
    }

    // Backs for faces: each one again, wound the other way, on copies of its corners whose normal and
    // tangent handedness are turned over (as Flip turns a face). Faces added here are not doubled again.
    private static void TwoSided(EditableMesh mesh, TwoSidedOp op)
    {
        for (int p = 0; p < mesh.Parts.Count; p++)
        {
            var part = mesh.Parts[p];
            var faces = op.All ? new SortedSet<int>(Enumerable.Range(0, part.TriangleCount)) : FacesOf(mesh, op.Faces, p);
            if (faces.Count == 0) continue;
            var backs = new Dictionary<int, int>();
            int Back(int v)
            {
                if (backs.TryGetValue(v, out int copy)) return copy;
                copy = part.AddCopy(v);
                part.Normals[copy] = -part.Normals[copy];
                if (part.Tangents is { } t) t[copy] = t[copy] with { W = -t[copy].W };
                return backs[v] = copy;
            }
            foreach (int f in faces)
            {
                int a = part.Indices[f * 3], b = part.Indices[f * 3 + 1], c = part.Indices[f * 3 + 2];
                int ba = Back(a), bb = Back(b), bc = Back(c);
                part.Indices.Add(ba); part.Indices.Add(bc); part.Indices.Add(bb);
            }
        }
    }

    private static void Normals(EditableMesh mesh, NormalsOp op)
    {
        for (int p = 0; p < mesh.Parts.Count; p++)
        {
            var faces = FacesOf(mesh, op.Faces, p);
            if (faces.Count == 0) continue;
            var part = mesh.Parts[p];
            int winding = part.WindingSign();
            if (!op.Smooth)
            {
                // One vertex per face corner, each with its face's normal.
                foreach (int f in faces)
                {
                    var n = EditablePart.SafeNormalize(part.FaceCross(f) * winding, Vector3.UnitY);
                    for (int k = 0; k < 3; k++)
                    {
                        int v = part.AddCopy(part.Indices[f * 3 + k]);
                        part.Normals[v] = n;
                        part.Indices[f * 3 + k] = v;
                    }
                }
                continue;
            }
            // Each corner position: the faces around it, weighted by their area.
            var sums = new Dictionary<(long, long, long), Vector3>();
            foreach (int f in faces)
            {
                var cross = part.FaceCross(f) * winding;
                for (int k = 0; k < 3; k++)
                {
                    var key = Weld(part.Positions[part.Indices[f * 3 + k]]);
                    sums[key] = sums.GetValueOrDefault(key) + cross;
                }
            }
            foreach (int v in Isolate(part, faces))
                if (sums.TryGetValue(Weld(part.Positions[v]), out var sum)) part.Normals[v] = EditablePart.SafeNormalize(sum, part.Normals[v]);
        }
    }

    // --- Extrude ------------------------------------------------------------------------------------

    private static void Extrude(EditableMesh mesh, ExtrudeFacesOp op)
    {
        if (op.Distance == 0) return;
        for (int p = 0; p < mesh.Parts.Count; p++)
        {
            var faces = FacesOf(mesh, op.Faces, p);
            if (faces.Count == 0) continue;
            var part = mesh.Parts[p];
            int winding = part.WindingSign();
            float density = part.TexelDensity();
            // The edges of the region (met once among the selected faces, by welded position).
            var edgeUse = new Dictionary<((long, long, long), (long, long, long)), int>();
            static ((long, long, long), (long, long, long)) Key((long, long, long) a, (long, long, long) b) => a.CompareTo(b) <= 0 ? (a, b) : (b, a);
            foreach (int f in faces)
                for (int k = 0; k < 3; k++)
                {
                    var key = Key(Weld(part.Positions[part.Indices[f * 3 + k]]), Weld(part.Positions[part.Indices[f * 3 + (k + 1) % 3]]));
                    edgeUse[key] = edgeUse.GetValueOrDefault(key) + 1;
                }
            // Offset per welded corner: along the region's normals there, or the given direction.
            var offsets = new Dictionary<(long, long, long), Vector3>();
            foreach (int f in faces)
            {
                var n = EditablePart.SafeNormalize(part.FaceCross(f) * winding, Vector3.UnitY);
                for (int k = 0; k < 3; k++)
                {
                    var key = Weld(part.Positions[part.Indices[f * 3 + k]]);
                    offsets[key] = offsets.GetValueOrDefault(key) + n;
                }
            }
            var direction = op.Direction is { } d && d.LengthSquared() > 1e-12f ? Vector3.Normalize(d) : (Vector3?)null;
            foreach (var key in offsets.Keys.ToArray())
                offsets[key] = (direction ?? EditablePart.SafeNormalize(offsets[key], Vector3.UnitY)) * op.Distance;
            // The cap: the selected faces on moved copies of their vertices.
            var moved = new Dictionary<int, int>();
            var original = new List<(int A, int B, int Face)>();
            foreach (int f in faces)
            {
                for (int k = 0; k < 3; k++)
                {
                    int a = part.Indices[f * 3 + k], b = part.Indices[f * 3 + (k + 1) % 3];
                    if (edgeUse[Key(Weld(part.Positions[a]), Weld(part.Positions[b]))] == 1) original.Add((a, b, f));
                }
            }
            var centers = faces.ToDictionary(f => f, part.FaceCenter);
            foreach (int f in faces)
                for (int k = 0; k < 3; k++)
                {
                    int v = part.Indices[f * 3 + k];
                    if (!moved.TryGetValue(v, out int copy))
                    {
                        copy = part.AddCopy(v);
                        part.Positions[copy] += offsets[Weld(part.Positions[v])];
                        moved[v] = copy;
                    }
                    part.Indices[f * 3 + k] = copy;
                }
            // The side walls, facing out of the region (into it for a recess).
            foreach (var (a, b, f) in original)
            {
                var pa = part.Positions[a]; var pb = part.Positions[b];
                var top = part.Positions[moved[a]] - pa;
                var outward = (pa + pb) / 2 - centers[f];
                var faceNormal = EditablePart.SafeNormalize(part.FaceCross(f) * winding, Vector3.UnitY);
                outward -= faceNormal * Vector3.Dot(outward, faceNormal);
                var normal = EditablePart.SafeNormalize(outward, Vector3.UnitX) * MathF.Sign(op.Distance);
                var along = EditablePart.SafeNormalize(pb - pa, Vector3.UnitX);
                float length = Vector3.Distance(pa, pb) * density, height = top.Length() * density;
                var uvA = part.Uv0[a]; var uvB = uvA + new Vector2(length, 0);
                int[] v =
                [
                    part.AddVertex(pa, normal, along, uvA, a), part.AddVertex(pb, normal, along, uvB, b),
                    part.AddVertex(part.Positions[moved[b]], normal, along, uvB + new Vector2(0, height), b),
                    part.AddVertex(part.Positions[moved[a]], normal, along, uvA + new Vector2(0, height), a)
                ];
                AddQuad(part, v, normal, winding);
            }
        }
    }

    // --- Subdivide ----------------------------------------------------------------------------------

    private static void Subdivide(EditableMesh mesh, ImmutableArray<PartIndices> selection)
    {
        for (int p = 0; p < mesh.Parts.Count; p++)
        {
            var faces = FacesOf(mesh, selection, p);
            if (faces.Count == 0) continue;
            var part = mesh.Parts[p];
            static ((long, long, long), (long, long, long)) Key((long, long, long) a, (long, long, long) b) => a.CompareTo(b) <= 0 ? (a, b) : (b, a);
            var split = new HashSet<((long, long, long), (long, long, long))>();
            foreach (int f in faces)
                for (int k = 0; k < 3; k++)
                    split.Add(Key(Weld(part.Positions[part.Indices[f * 3 + k]]), Weld(part.Positions[part.Indices[f * 3 + (k + 1) % 3]])));
            var middles = new Dictionary<(int, int), int>();
            int Middle(int a, int b)
            {
                var key = (Math.Min(a, b), Math.Max(a, b));
                if (!middles.TryGetValue(key, out int m)) middles[key] = m = part.AddLerp(key.Item1, key.Item2, .5f);
                return m;
            }
            var indices = new List<int>(part.Indices.Count * 2);
            for (int f = 0; f < part.TriangleCount; f++)
            {
                int[] v = [part.Indices[f * 3], part.Indices[f * 3 + 1], part.Indices[f * 3 + 2]];
                // The middle of each edge that is split (-1 for the others).
                var m = new int[3];
                int count = 0;
                for (int k = 0; k < 3; k++)
                {
                    bool cut = split.Contains(Key(Weld(part.Positions[v[k]]), Weld(part.Positions[v[(k + 1) % 3]])));
                    m[k] = cut ? Middle(v[k], v[(k + 1) % 3]) : -1;
                    if (cut) count++;
                }
                switch (count)
                {
                    case 0: indices.AddRange(v); break;
                    case 3:
                        indices.AddRange([v[0], m[0], m[2], m[0], v[1], m[1], m[2], m[1], v[2], m[0], m[1], m[2]]);
                        break;
                    case 1:
                    {
                        int k = Array.FindIndex(m, x => x >= 0);
                        int a = v[k], b = v[(k + 1) % 3], c = v[(k + 2) % 3], mid = m[k];
                        indices.AddRange([a, mid, c, mid, b, c]);
                        break;
                    }
                    default:
                    {
                        // Two edges split: the corner between them gets its own triangle.
                        int k = Array.FindIndex(m, x => x < 0);   // the edge kept whole: v[k] → v[k+1]
                        int a = v[k], b = v[(k + 1) % 3], c = v[(k + 2) % 3];
                        int mbc = m[(k + 1) % 3], mca = m[(k + 2) % 3];
                        indices.AddRange([mbc, c, mca, a, b, mbc, a, mbc, mca]);
                        break;
                    }
                }
            }
            part.Indices = indices;
        }
    }

    // --- UVs ------------------------------------------------------------------------------------------

    private static List<Vector2>? Channel(EditablePart part, int channel)
    {
        if (channel == 0) return part.Uv0;
        if (part.Uv1 is null) part.Uv1 = [.. part.Uv0];
        return part.Uv1;
    }

    private static void UvProject(EditableMesh mesh, UvProjectOp op)
    {
        for (int p = 0; p < mesh.Parts.Count; p++)
        {
            var faces = FacesOf(mesh, op.Faces, p);
            if (faces.Count == 0) continue;
            var part = mesh.Parts[p];
            var vertices = Isolate(part, faces);
            var uvs = Channel(part, op.Channel)!;
            foreach (int v in vertices)
            {
                var d = part.Positions[v] - op.Origin;
                uvs[v] = new Vector2(Vector3.Dot(d, op.AxisU) * op.Scale.X, Vector3.Dot(d, op.AxisV) * op.Scale.Y) + op.Offset;
            }
        }
    }

    private static void UvTransform(EditableMesh mesh, UvTransformOp op)
    {
        for (int p = 0; p < mesh.Parts.Count; p++)
        {
            var faces = FacesOf(mesh, op.Faces, p);
            if (faces.Count == 0) continue;
            var part = mesh.Parts[p];
            var vertices = Isolate(part, faces);
            var uvs = Channel(part, op.Channel)!;
            foreach (int v in vertices) uvs[v] = Vector2.Transform(uvs[v], op.Matrix);
        }
    }

    private static void UvMove(EditableMesh mesh, UvMoveOp op)
    {
        for (int p = 0; p < mesh.Parts.Count; p++)
        {
            var uvs = Channel(mesh.Parts[p], op.Channel)!;
            foreach (int v in VerticesOf(mesh, op.Vertices, p)) uvs[v] += op.Delta;
        }
    }

    // --- Parts ----------------------------------------------------------------------------------------

    private static void MoveFacesToPart(EditableMesh mesh, MoveFacesToPartOp op)
    {
        if (op.Target < 0 || op.Target >= mesh.Parts.Count) return;
        var target = mesh.Parts[op.Target];
        for (int p = 0; p < mesh.Parts.Count; p++)
        {
            if (p == op.Target) continue;
            var faces = FacesOf(mesh, op.Faces, p);
            if (faces.Count == 0) continue;
            var part = mesh.Parts[p];
            var copies = new Dictionary<int, int>();
            int Copy(int v)
            {
                if (copies.TryGetValue(v, out int c)) return c;
                target.Positions.Add(part.Positions[v]); target.Normals.Add(part.Normals[v]);
                target.Tangents?.Add(part.Tangents?[v] ?? new Vector4(1, 0, 0, 1));
                target.Uv0.Add(part.Uv0[v]); target.Uv1?.Add(part.Uv1?[v] ?? part.Uv0[v]);
                target.Colors?.Add(part.Colors?[v] ?? Vector4.One);
                return copies[v] = target.Positions.Count - 1;
            }
            foreach (int f in faces) target.Indices.AddRange([Copy(part.Indices[f * 3]), Copy(part.Indices[f * 3 + 1]), Copy(part.Indices[f * 3 + 2])]);
            var kept = new List<int>(part.Indices.Count);
            for (int f = 0; f < part.TriangleCount; f++) if (!faces.Contains(f)) kept.AddRange([part.Indices[f * 3], part.Indices[f * 3 + 1], part.Indices[f * 3 + 2]]);
            part.Indices = kept;
        }
    }

    private static void AddTriangles(EditableMesh mesh, AddTrianglesOp op)
    {
        if (op.Part < 0 || op.Part >= mesh.Parts.Count || op.Corners.IsDefault) return;
        var part = mesh.Parts[op.Part];
        int winding = part.Indices.Count > 0 ? part.WindingSign() : 1;
        float density = part.Indices.Count > 0 ? part.TexelDensity() : 1;
        for (int i = 0; i + 2 < op.Corners.Length; i += 3)
        {
            Vector3 a = op.Corners[i], b = op.Corners[i + 1], c = op.Corners[i + 2];
            var cross = Vector3.Cross(b - a, c - a);
            if (cross.LengthSquared() < 1e-14f) continue;
            var normal = op.Facing is { } facing && facing.LengthSquared() > 0 ? Vector3.Normalize(facing) : Vector3.Normalize(cross) * winding;
            // Planar UVs on the triangle's plane: U along the horizontal of the plane where there is one.
            var u = Vector3.Cross(Vector3.UnitY, normal);
            u = u.LengthSquared() < 1e-6f ? Vector3.UnitX : Vector3.Normalize(u);
            var v = Vector3.Cross(normal, u);
            Vector2 Uv(Vector3 p) => new Vector2(Vector3.Dot(p, u), Vector3.Dot(p, v)) * density;
            int[] idx = [part.AddVertex(a, normal, u, Uv(a)), part.AddVertex(b, normal, u, Uv(b)), part.AddVertex(c, normal, u, Uv(c))];
            bool forward = Vector3.Dot(cross, normal) * winding >= 0;
            part.Indices.AddRange(forward ? idx : [idx[0], idx[2], idx[1]]);
        }
    }
}
