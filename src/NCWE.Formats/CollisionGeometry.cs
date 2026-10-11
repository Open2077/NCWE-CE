// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using WolvenKit.Common.PhysX;
using WolvenKit.Common.RED4;
using WolvenKit.RED4.Archive.CR2W;
using WolvenKit.RED4.Archive.IO;
using WolvenKit.RED4.Types;
using Vector3 = System.Numerics.Vector3;

namespace NCWE.Formats;

// Cooked collision geometry. A CollisionShapeMesh (triangle or convex mesh) names its PhysX
// mesh by hash; the meshes live in one file for the whole city, grouped by the collision
// node's SectorHash (WolvenKit GeometryCacheService layout). The file (≈195 MB) is read once
// on first use; each group is decoded on demand and kept.
public sealed record CollisionMeshData(Vector3[] Vertices, int[] Indices, bool Convex);

public sealed partial class GameArchiveIndex
{
    public const string GeometryCachePath = @"base\worlds\03_night_city\sectors\_generated\collisions\03_night_city.geometry_cache";
    private readonly object _geometryGate = new();
    private (physicsGeometryCache Root, CR2WFile File, Dictionary<ulong, int> Sectors, int[] FirstKey)? _geometryCache;
    private bool _geometryTried;
    private readonly ConcurrentDictionary<ulong, IReadOnlyDictionary<ulong, CollisionMeshData>> _collisionGroups = new();
    public ConcurrentQueue<string> CollisionErrors { get; } = new();

    // The cooked mesh of a collision shape (null when absent or undecodable).
    public CollisionMeshData? CollisionMesh(ulong sectorHash, ulong shapeHash)
    {
        if (CollisionGroup(sectorHash).TryGetValue(shapeHash, out var mesh)) return mesh;
        return CollisionGroup(0).GetValueOrDefault(shapeHash);
    }

    public bool CollisionMeshesAvailable => EntryOrNull(WolvenKit.Common.FNV1A.FNV1A64HashAlgorithm.HashString(GeometryCachePath)) is not null;

    private IReadOnlyDictionary<ulong, CollisionMeshData> CollisionGroup(ulong sectorHash) => _collisionGroups.GetOrAdd(sectorHash, hash =>
    {
        var cache = GeometryCache();
        if (cache is not { } c) return new Dictionary<ulong, CollisionMeshData>();
        try
        {
            RedBuffer buffer; int first;
            if (hash == 0) { buffer = c.Root.AlwaysLoadedSectorDDB.Buffer; first = c.FirstKey[^1]; }
            else if (c.Sectors.TryGetValue(hash, out int index)) { buffer = c.Root.BufferTableSectors[index].Buffer; first = c.FirstKey[index]; }
            else return new Dictionary<ulong, CollisionMeshData>();
            List<PhysXMesh> meshes;
            lock (_geometryGate) meshes = GeometryCacheReader.ReadBuffer(buffer);
            var group = new Dictionary<ulong, CollisionMeshData>(meshes.Count);
            for (int i = 0; i < meshes.Count && first + i < c.Root.SectorGeometries.Count; i++)
            {
                var key = c.Root.SectorGeometries[first + i];
                ulong shape = 0;
                for (int k = 0; k < 8; k++) shape |= (ulong)(byte)key.Ta[k] << (k * 8);
                if (ToCollisionMesh(meshes[i]) is { } data) group[shape] = data;
            }
            return group;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            CollisionErrors.Enqueue($"{hash:x16}: {ex.GetType().Name}: {ex.Message}");
            return new Dictionary<ulong, CollisionMeshData>();
        }
    });

    private (physicsGeometryCache Root, CR2WFile File, Dictionary<ulong, int> Sectors, int[] FirstKey)? GeometryCache()
    {
        lock (_geometryGate)
        {
            if (_geometryTried) return _geometryCache;
            _geometryTried = true;
            try
            {
                var entry = EntryOrNull(WolvenKit.Common.FNV1A.FNV1A64HashAlgorithm.HashString(GeometryCachePath));
                if (entry is null) return null;
                var data = ExtractEntry(entry, default);
                var reader = new CR2WReader(data);
                if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file?.RootChunk is not physicsGeometryCache root) return null;
                // Group i's meshes have the keys [entryOffset, entryOffset + entrySize) (checked:
                // the counts match the decoded buffers); the always-loaded group has its own entry.
                var sectors = new Dictionary<ulong, int>();
                var first = new int[root.BufferTableSectors.Count + 1];
                for (int i = 0; i < root.BufferTableSectors.Count && i < root.SectorEntries.Count; i++)
                { sectors[root.SectorEntries[i].SectorHash] = i; first[i] = (int)(uint)root.SectorEntries[i].EntryOffset; }
                first[^1] = (int)(uint)root.AlwaysLoadedSector.EntryOffset;
                _geometryCache = (root, file, sectors, first);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { CollisionErrors.Enqueue("geometry cache: " + ex.Message); }
            return _geometryCache;
        }
    }

    private static int CountMeshes(RedBuffer buffer) => GeometryCacheReader.ReadBuffer(buffer).Count;

    // Diagnostics: the index fields against the real mesh counts of the first groups.
    public IEnumerable<string> GeometryCacheReport(int groups)
    {
        var cache = GeometryCache();
        if (cache is not { } c) { yield return "no geometry cache"; foreach (var e in CollisionErrors) yield return e; yield break; }
        yield return $"buffers {c.Root.BufferTableSectors.Count}, sector entries {c.Root.SectorEntries.Count}, keys {c.Root.SectorGeometries.Count}, cache entries {c.Root.SectorCacheEntries.Count}";
        int cumulative = 0;
        for (int i = 0; i < Math.Min(groups, c.Root.BufferTableSectors.Count); i++)
        {
            var s = c.Root.SectorEntries[i];
            int count = CountMeshes(c.Root.BufferTableSectors[i].Buffer);
            yield return $"[{i}] sector {(ulong)s.SectorHash:x16} entryOffset {(uint)s.EntryOffset} entrySize {(uint)s.EntrySize} meshes {count} cumulative {cumulative} bytes {c.Root.BufferTableSectors[i].Buffer.MemSize}";
            cumulative += count;
        }
    }

    private static CollisionMeshData? ToCollisionMesh(PhysXMesh mesh)
    {
        switch (mesh)
        {
            case BV4TriangleMesh triangles:
            {
                var vertices = triangles.Vertices.Select(v => new Vector3(v.X, v.Y, v.Z)).ToArray();
                var indices = new int[triangles.Triangles.Count * 3];
                for (int t = 0; t < triangles.Triangles.Count; t++)
                    for (int k = 0; k < 3; k++) indices[t * 3 + k] = (int)triangles.Triangles[t][k];
                return new(vertices, indices, false);
            }
            case ConvexMesh convex:
            {
                var hull = convex.HullData;
                var vertices = hull.HullVertices.Select(v => new Vector3(v.X, v.Y, v.Z)).ToArray();
                var indices = new List<int>();
                foreach (var polygon in hull.Polygons)
                {
                    int start = polygon.VRef8;
                    for (int k = 1; k + 1 < polygon.NbVerts; k++)
                    {
                        if (start + k + 1 >= hull.VertexData8.Length) break;
                        indices.Add(hull.VertexData8[start]); indices.Add(hull.VertexData8[start + k]); indices.Add(hull.VertexData8[start + k + 1]);
                    }
                }
                return new(vertices, indices.ToArray(), true);
            }
            default: return null;
        }
    }
}
