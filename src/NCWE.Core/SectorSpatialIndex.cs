// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Numerics;

namespace NCWE.Core;

public sealed record SectorQuery(ImmutableArray<StreamingDescriptor> Candidates, int TotalMatches, int VisitedNodes);

// Immutable median-split AABB hierarchy. Real descriptors have overlapping,
// nonuniform extents and AlwaysLoaded bounds; no filename/grid-size inference.
public sealed class SectorSpatialIndex
{
    private readonly record struct Item(StreamingDescriptor Descriptor, int Order);
    private readonly record struct Node(Bounds3 Bounds, int Start, int Count, int Left, int Right);
    private readonly Item[] _items;
    private readonly Node[] _nodes;
    public int Count => _items.Length;
    public int ExcludedInvalidBounds { get; }

    public SectorSpatialIndex(IEnumerable<StreamingDescriptor> descriptors)
    {
        var all = descriptors.Select((d, i) => new Item(d, i)).ToArray();
        _items = all.Where(i => i.Descriptor.HasValidBounds).ToArray();
        ExcludedInvalidBounds = all.Length - _items.Length;
        var nodes = new List<Node>();
        if (_items.Length != 0) Build(0, _items.Length);
        _nodes = nodes.ToArray();

        int Build(int start, int count)
        {
            var bounds = _items[start].Descriptor.GameBounds;
            var minCenter = bounds.Center; var maxCenter = minCenter;
            for (int i = start + 1; i < start + count; i++)
            {
                var b = _items[i].Descriptor.GameBounds;
                bounds = new(Vector3.Min(bounds.Min, b.Min), Vector3.Max(bounds.Max, b.Max));
                minCenter = Vector3.Min(minCenter, b.Center); maxCenter = Vector3.Max(maxCenter, b.Center);
            }
            int n = nodes.Count; nodes.Add(default);
            if (count <= 16) { nodes[n] = new(bounds, start, count, -1, -1); return n; }
            double x = (double)maxCenter.X - minCenter.X, y = (double)maxCenter.Y - minCenter.Y, z = (double)maxCenter.Z - minCenter.Z;
            int axis = x >= y && x >= z ? 0 : y >= z ? 1 : 2;
            Array.Sort(_items, start, count, Comparer<Item>.Create((a, b) =>
            {
                int c = a.Descriptor.GameBounds.Center[axis].CompareTo(b.Descriptor.GameBounds.Center[axis]);
                return c != 0 ? c : a.Order.CompareTo(b.Order);
            }));
            int half = count / 2;
            int left = Build(start, half), right = Build(start + half, count - half);
            nodes[n] = new(bounds, 0, 0, left, right); return n;
        }
    }

    public SectorQuery Query(Vector3 gamePosition, float radius, int maximumResults,
        Func<StreamingDescriptor, bool>? include = null)
    {
        if (!float.IsFinite(gamePosition.X) || !float.IsFinite(gamePosition.Y) || !float.IsFinite(gamePosition.Z)
            || !float.IsFinite(radius) || radius < 0 || maximumResults is < 1 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(radius), "Use a finite position/radius and 1–100000 results.");
        if (_nodes.Length == 0) return new([], 0, 0);
        double squaredRadius = (double)radius * radius;
        // A bounded max-heap retains nearest candidates; ties use discovery
        // order, so traversal shape cannot change which requests are admitted.
        var nearest = new PriorityQueue<(Item Item, double Distance), (double Distance, int Order)>();
        var stack = new Stack<int>(); stack.Push(0); int matches = 0, visited = 0;
        while (stack.TryPop(out var id))
        {
            var node = _nodes[id]; visited++;
            if (DistanceSquared(gamePosition, node.Bounds) > squaredRadius) continue;
            if (node.Count == 0) { stack.Push(node.Right); stack.Push(node.Left); continue; }
            for (int i = node.Start; i < node.Start + node.Count; i++)
            {
                var item = _items[i];
                double distance = DistanceSquared(gamePosition, item.Descriptor.GameBounds);
                if (distance > squaredRadius || (include is not null && !include(item.Descriptor))) continue;
                matches++;
                nearest.Enqueue((item, distance), (-distance, -item.Order));
                if (nearest.Count > maximumResults) nearest.Dequeue();
            }
        }
        return new(nearest.UnorderedItems.Select(v => v.Element).OrderBy(v => v.Distance).ThenBy(v => v.Item.Order)
            .Select(v => v.Item.Descriptor).ToImmutableArray(), matches, visited);
    }

    public static double DistanceSquared(Vector3 point, Bounds3 bounds)
    {
        // Double arithmetic avoids overflow for RED4's finite unbounded boxes.
        double x = (double)point.X - Math.Clamp(point.X, bounds.Min.X, bounds.Max.X);
        double y = (double)point.Y - Math.Clamp(point.Y, bounds.Min.Y, bounds.Max.Y);
        double z = (double)point.Z - Math.Clamp(point.Z, bounds.Min.Z, bounds.Max.Z);
        return x * x + y * y + z * z;
    }
}
