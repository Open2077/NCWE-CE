// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using NCWE.Core;

namespace NCWE.Studio.Session;

// Horizontal spatial hash of published placements (view space, Y up). Used for
// placement under the cursor and walk-mode ground probes without scanning the city.
internal sealed class PlacementGrid
{
    private const float Cell = 32;
    private readonly record struct Entry(string Id, MeshAsset Mesh, Matrix4x4 Model, Bounds3 Bounds);
    private readonly Dictionary<(int, int), List<int>> _cells = [];
    private readonly List<Entry> _entries = [];
    public int Count => _entries.Count;

    public void Add(string id, MeshAsset mesh, Matrix4x4 model)
    {
        var bounds = TransformBounds(mesh.Bounds, model);
        if (!SpatialBounds.IsValid(bounds) || bounds.Size.X > 4000 || bounds.Size.Z > 4000) return;
        int index = _entries.Count; _entries.Add(new(id, mesh, model, bounds));
        for (int x = Key(bounds.Min.X); x <= Key(bounds.Max.X); x++)
        for (int z = Key(bounds.Min.Z); z <= Key(bounds.Max.Z); z++)
        {
            if (!_cells.TryGetValue((x, z), out var list)) _cells[(x, z)] = list = [];
            list.Add(index);
        }
    }

    private static int Key(float v) => (int)MathF.Floor(v / Cell);

    // Nearest hit distance along a normalized world-space ray.
    // ignorePrefix skips a family of ids (for example editor roads when snapping onto the ground).
    public float? Raycast(Ray3 ray, float maximum, string? ignore = null, string? ignorePrefix = null, bool frontOnly = false)
        => RaycastHit(ray, maximum, ignore, ignorePrefix, null, frontOnly)?.Distance;

    // Nearest hit and the placement it belongs to.
    // frontOnly: rays from the cursor see what the view shows (the back of a one-sided part is not hit).
    public (float Distance, string Id)? RaycastHit(Ray3 ray, float maximum, string? ignore = null, string? ignorePrefix = null, IReadOnlySet<string>? ignoreIds = null, bool frontOnly = false)
    {
        float best = maximum; bool found = false; string hitId = "";
        var visited = new HashSet<int>();
        foreach (var cell in Cells(ray, maximum))
        {
            if (!_cells.TryGetValue(cell, out var list)) continue;
            foreach (int index in list)
            {
                if (!visited.Add(index)) continue;
                var entry = _entries[index];
                if (entry.Id == ignore || (ignorePrefix is not null && entry.Id.StartsWith(ignorePrefix, StringComparison.Ordinal)) || ignoreIds?.Contains(entry.Id) == true || !RayBox(ray, entry.Bounds, best)) continue;
                var hit = ScenePicking.Intersect(ray, entry.Mesh, entry.Model, entry.Mesh.LodMasks[0], frontOnly);
                if (hit is { } distance && distance < best) { best = distance; found = true; hitId = entry.Id; }
            }
        }
        return found ? (best, hitId) : null;
    }

    // Height of the first surface below a view-space point, if any.
    public float? Ground(Vector3 point, float above = 2, float depth = 600, string? ignorePrefix = null)
    {
        var origin = point + new Vector3(0, above, 0);
        var hit = Raycast(new(origin, -Vector3.UnitY), depth + above, null, ignorePrefix);
        return hit is { } d ? origin.Y - d : null;
    }

    private static IEnumerable<(int, int)> Cells(Ray3 ray, float maximum)
    {
        // 2D DDA over the XZ grid.
        int x = Key(ray.Origin.X), z = Key(ray.Origin.Z);
        var d = new Vector2(ray.Direction.X, ray.Direction.Z);
        float length = d.Length();
        yield return (x, z);
        if (length < 1e-6f) yield break;
        int stepX = d.X > 0 ? 1 : -1, stepZ = d.Y > 0 ? 1 : -1;
        float nextX = ((x + (stepX > 0 ? 1 : 0)) * Cell - ray.Origin.X) / (d.X == 0 ? 1e-9f : d.X);
        float nextZ = ((z + (stepZ > 0 ? 1 : 0)) * Cell - ray.Origin.Z) / (d.Y == 0 ? 1e-9f : d.Y);
        float deltaX = MathF.Abs(Cell / (d.X == 0 ? 1e-9f : d.X)), deltaZ = MathF.Abs(Cell / (d.Y == 0 ? 1e-9f : d.Y));
        for (int i = 0; i < 4096; i++)
        {
            float t = Math.Min(nextX, nextZ);
            if (t > maximum) yield break;
            if (nextX < nextZ) { x += stepX; nextX += deltaX; } else { z += stepZ; nextZ += deltaZ; }
            yield return (x, z);
        }
    }

    private static bool RayBox(Ray3 ray, Bounds3 box, float maximum)
    {
        float near = 0, far = maximum;
        for (int axis = 0; axis < 3; axis++)
        {
            float origin = ray.Origin[axis], direction = ray.Direction[axis];
            if (MathF.Abs(direction) < 1e-9f) { if (origin < box.Min[axis] || origin > box.Max[axis]) return false; continue; }
            float a = (box.Min[axis] - origin) / direction, b = (box.Max[axis] - origin) / direction;
            if (a > b) (a, b) = (b, a);
            near = Math.Max(near, a); far = Math.Min(far, b);
            if (near > far) return false;
        }
        return true;
    }

    private static Bounds3 TransformBounds(Bounds3 local, Matrix4x4 model)
    {
        var b = InstanceBounds.From(local, model);
        return new(b.Center - b.Extents, b.Center + b.Extents);
    }
}
