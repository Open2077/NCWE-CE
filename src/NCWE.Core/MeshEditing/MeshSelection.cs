// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Numerics;

namespace NCWE.Core.MeshEditing;

// Finding vertices and faces of an edited mesh (mesh space): by box, by facing, by ray, grown to
// the faces linked or coplanar with a seed, and the vertices sharing a position (a vertex on a
// seam exists once per side: moving one side alone would tear the surface).
public static class MeshSelection
{
    public static ImmutableArray<PartIndices> Group(IEnumerable<(int Part, int Index)> items)
        => [.. items.Distinct().GroupBy(i => i.Part).OrderBy(g => g.Key).Select(g => new PartIndices(g.Key, [.. g.Select(i => i.Index).Order()]))];

    public static IEnumerable<(int Part, int Index)> Flatten(ImmutableArray<PartIndices> selection)
    {
        if (selection.IsDefault) yield break;
        foreach (var s in selection) if (!s.Indices.IsDefault) foreach (int i in s.Indices) yield return (s.Part, i);
    }

    public static int Count(ImmutableArray<PartIndices> selection) => selection.IsDefault ? 0 : selection.Sum(s => s.Indices.IsDefault ? 0 : s.Indices.Length);

    // Faces whose centre is inside an oriented box.
    public static ImmutableArray<PartIndices> FacesInBox(EditableMesh mesh, Vector3 center, Quaternion rotation, Vector3 half)
    {
        var inverse = Quaternion.Conjugate(Quaternion.Normalize(rotation));
        var found = new List<(int, int)>();
        for (int p = 0; p < mesh.Parts.Count; p++)
            for (int f = 0; f < mesh.Parts[p].TriangleCount; f++)
            {
                var u = Vector3.Abs(Vector3.Transform(mesh.Parts[p].FaceCenter(f) - center, inverse));
                if (u.X <= half.X && u.Y <= half.Y && u.Z <= half.Z) found.Add((p, f));
            }
        return Group(found);
    }

    public static ImmutableArray<PartIndices> VerticesInBox(EditableMesh mesh, Vector3 center, Quaternion rotation, Vector3 half)
    {
        var inverse = Quaternion.Conjugate(Quaternion.Normalize(rotation));
        var found = new List<(int, int)>();
        for (int p = 0; p < mesh.Parts.Count; p++)
            foreach (int v in UsedVertices(mesh.Parts[p]))
            {
                var u = Vector3.Abs(Vector3.Transform(mesh.Parts[p].Positions[v] - center, inverse));
                if (u.X <= half.X && u.Y <= half.Y && u.Z <= half.Z) found.Add((p, v));
            }
        return Group(found);
    }

    // Faces turned towards a direction (within an angle), among some faces or all of them.
    public static ImmutableArray<PartIndices> FacesFacing(EditableMesh mesh, Vector3 direction, float maxDegrees, ImmutableArray<PartIndices> among = default)
    {
        var d = Vector3.Normalize(direction);
        float cos = MathF.Cos(maxDegrees * MathF.PI / 180);
        var found = new List<(int, int)>();
        foreach (var (p, f) in among.IsDefault ? AllFaces(mesh) : Flatten(among))
        {
            var part = mesh.Parts[p];
            if (f >= part.TriangleCount) continue;
            var n = part.FaceCross(f) * part.WindingSign();
            if (n.LengthSquared() > 0 && Vector3.Dot(Vector3.Normalize(n), d) >= cos) found.Add((p, f));
        }
        return Group(found);
    }

    public static IEnumerable<(int Part, int Face)> AllFaces(EditableMesh mesh)
    {
        for (int p = 0; p < mesh.Parts.Count; p++) for (int f = 0; f < mesh.Parts[p].TriangleCount; f++) yield return (p, f);
    }

    public static IEnumerable<int> UsedVertices(EditablePart part)
    {
        var seen = new HashSet<int>();
        foreach (int v in part.Indices) if (seen.Add(v)) yield return v;
    }

    // The nearest face a ray crosses (either side), and the distance along it.
    public static (int Part, int Face, float Distance)? Raycast(EditableMesh mesh, Vector3 origin, Vector3 direction)
    {
        (int, int, float)? best = null;
        for (int p = 0; p < mesh.Parts.Count; p++)
        {
            var part = mesh.Parts[p];
            for (int f = 0; f < part.TriangleCount; f++)
            {
                var (a, b, c) = part.Triangle(f);
                if (Intersect(origin, direction, part.Positions[a], part.Positions[b], part.Positions[c]) is { } t && (best is null || t < best.Value.Item3)) best = (p, f, t);
            }
        }
        return best;
    }

    public static float? Intersect(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c)
    {
        var e1 = b - a; var e2 = c - a;
        var p = Vector3.Cross(direction, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-12f) return null;
        var t = origin - a;
        float u = Vector3.Dot(t, p) / det;
        if (u < 0 || u > 1) return null;
        var q = Vector3.Cross(t, e1);
        float v = Vector3.Dot(direction, q) / det;
        if (v < 0 || u + v > 1) return null;
        float distance = Vector3.Dot(e2, q) / det;
        return distance >= 0 ? distance : null;
    }

    // Faces reached from the seeds through shared edges (by position), within each seed's part.
    public static ImmutableArray<PartIndices> Linked(EditableMesh mesh, ImmutableArray<PartIndices> seeds)
        => Grow(mesh, seeds, (_, _, _) => true);

    // Faces reached from the seeds through shared edges while they stay in the seed face's plane.
    public static ImmutableArray<PartIndices> Coplanar(EditableMesh mesh, ImmutableArray<PartIndices> seeds, float maxDegrees = 2, float maxDistance = .01f)
    {
        float cos = MathF.Cos(maxDegrees * MathF.PI / 180);
        return Grow(mesh, seeds, (part, seed, f) =>
        {
            var n0 = Vector3.Normalize(part.FaceCross(seed)); var n = part.FaceCross(f);
            if (n.LengthSquared() < 1e-14f) return true;
            n = Vector3.Normalize(n);
            return Vector3.Dot(n0, n) >= cos && MathF.Abs(Vector3.Dot(part.FaceCenter(f) - part.FaceCenter(seed), n0)) <= maxDistance;
        });
    }

    private static ImmutableArray<PartIndices> Grow(EditableMesh mesh, ImmutableArray<PartIndices> seeds, Func<EditablePart, int, int, bool> accept)
    {
        var found = new List<(int, int)>();
        foreach (var group in seeds.IsDefault ? [] : seeds)
        {
            if (group.Part < 0 || group.Part >= mesh.Parts.Count) continue;
            var part = mesh.Parts[group.Part];
            // Faces by welded edge.
            var byEdge = new Dictionary<((long, long, long), (long, long, long)), List<int>>();
            for (int f = 0; f < part.TriangleCount; f++)
                foreach (var key in Edges(part, f))
                {
                    if (!byEdge.TryGetValue(key, out var list)) byEdge[key] = list = [];
                    list.Add(f);
                }
            var seen = new HashSet<int>();
            foreach (int seed in group.Indices)
            {
                if (seed < 0 || seed >= part.TriangleCount || !seen.Add(seed)) continue;
                var queue = new Queue<int>(); queue.Enqueue(seed); found.Add((group.Part, seed));
                while (queue.TryDequeue(out int f))
                    foreach (var key in Edges(part, f))
                        foreach (int next in byEdge[key])
                            if (!seen.Contains(next) && accept(part, seed, next)) { seen.Add(next); found.Add((group.Part, next)); queue.Enqueue(next); }
            }
        }
        return Group(found);
    }

    private static IEnumerable<((long, long, long), (long, long, long))> Edges(EditablePart part, int f)
    {
        for (int k = 0; k < 3; k++)
        {
            var a = MeshEditor.Weld(part.Positions[part.Indices[f * 3 + k]]); var b = MeshEditor.Weld(part.Positions[part.Indices[f * 3 + (k + 1) % 3]]);
            yield return a.CompareTo(b) <= 0 ? (a, b) : (b, a);
        }
    }

    public static ImmutableArray<PartIndices> VerticesOfFaces(EditableMesh mesh, ImmutableArray<PartIndices> faces)
    {
        var found = new List<(int, int)>();
        foreach (var (p, f) in Flatten(faces))
        {
            if (p >= mesh.Parts.Count || f >= mesh.Parts[p].TriangleCount) continue;
            var (a, b, c) = mesh.Parts[p].Triangle(f);
            found.Add((p, a)); found.Add((p, b)); found.Add((p, c));
        }
        return Group(found);
    }

    // The vertices plus every used vertex at the same position (in any part), so they move together.
    public static ImmutableArray<PartIndices> Coincident(EditableMesh mesh, ImmutableArray<PartIndices> vertices)
    {
        var keys = new HashSet<(long, long, long)>();
        foreach (var (p, v) in Flatten(vertices))
            if (p < mesh.Parts.Count && v < mesh.Parts[p].VertexCount) keys.Add(MeshEditor.Weld(mesh.Parts[p].Positions[v]));
        var found = new List<(int, int)>();
        for (int p = 0; p < mesh.Parts.Count; p++)
            foreach (int v in UsedVertices(mesh.Parts[p]))
                if (keys.Contains(MeshEditor.Weld(mesh.Parts[p].Positions[v]))) found.Add((p, v));
        return Group(found);
    }

    // The centre of the vertices' positions (mesh space).
    public static Vector3? Center(EditableMesh mesh, ImmutableArray<PartIndices> vertices)
    {
        Vector3 sum = default; int count = 0;
        foreach (var (p, v) in Flatten(vertices))
            if (p < mesh.Parts.Count && v < mesh.Parts[p].VertexCount) { sum += mesh.Parts[p].Positions[v]; count++; }
        return count > 0 ? sum / count : null;
    }
}
