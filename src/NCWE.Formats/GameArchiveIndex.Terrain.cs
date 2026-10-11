// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using NCWE.Core;
using NCWE.Core.Terrain;
using NCWE.Formats.Terrain;
using WolvenKit.RED4.Archive.Buffer;
using WolvenKit.RED4.Archive.IO;
using WolvenKit.RED4.Types;
using Vector2 = System.Numerics.Vector2;

namespace NCWE.Formats;

// A game plant node in its sector: placement, stances (offsets from it) and buckets (ranges of its
// stances, relative to its first), and the compiled foliage resource they come from.
public sealed record FoliagePlacement(string SectorPath, int ExpectedNodes, int Placement, PlacementTransform Transform, worldFoliageNode Node,
    FoliageStance[] Stances, (int Sub, int Count)[] Buckets, ulong Resource);

// Compiled terrain: cell meshes and the sectors' height-field collision nodes.
public sealed partial class GameArchiveIndex
{
    // The terrain collision nodes of a streaming sector.
    public IReadOnlyList<TerrainCollision> TerrainCollisions(ulong sectorHash)
    {
        var entry = EntryOrNull(sectorHash);
        if (entry is null) return [];
        using var data = ExtractEntry(entry, default);
        using var reader = new CR2WReader(data);
        reader.ParsingError += SectorReader.TolerateSectorParsingError;
        if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file is null) return [];
        using (file)
        {
            if (file.RootChunk is not worldStreamingSector sector || sector.NodeData.Buffer.Data is not worldNodeDataBuffer setups) return [];
            var result = new List<TerrainCollision>();
            for (int i = 0; i < setups.Count; i++)
            {
                var setup = setups[i];
                if (setup.NodeIndex >= sector.Nodes.Count || sector.Nodes[setup.NodeIndex].Chunk is not worldTerrainCollisionNode node) continue;
                if (TerrainCollisionReader.FromNode(sectorHash, entry.FileName ?? sectorHash.ToString("X16"), i, setup, node) is { } collision)
                    result.Add(collision with { ExpectedNodes = setups.Count, Node = (worldTerrainCollisionNode)node.DeepCopy() });
            }
            return result;
        }
    }

    // A terrain cell's mesh resource, parsed (the caller disposes it), or null when the game has none.
    public WolvenKit.RED4.Archive.CR2W.CR2WFile? ReadTerrainCell(TerrainCell cell) => ReadFile(cell.MeshPath);

    // The textures a terrain cell keeps inside its mesh file (no archive entry of their own, so the
    // material source cannot read them by hash): its multilayer mask, its normal map (world
    // normals) and its tint (BaseColor), north at the top as the editor draws them.
    public (MaskData? Mask, TextureData? Normal, TextureData? Tint) ReadTerrainCellTextures(TerrainCell cell)
    {
        using var file = ReadTerrainCell(cell);
        if (file?.RootChunk is not CMesh mesh) return (null, null, null);
        // Each texture by the material parameter that names it (a cell can also hold others, such
        // as a grayscale "-foliage" density map).
        ulong Named(string key) => mesh.PreloadLocalMaterialInstances.Select(h => h.Chunk).OfType<CMaterialInstance>().SelectMany(i => i.Values)
            .Where(p => p.Key == key && p.Value is IRedRef).Select(p => (ulong)((IRedRef)p.Value!).DepotPath.GetRedHash()).FirstOrDefault();
        ulong tintPath = Named("BaseColor"), normalPath = Named("GlobalNormal");
        MaskData? mask = null; TextureData? normal = null, tint = null;
        foreach (var embedded in file.EmbeddedFiles)
        {
            ulong path = embedded.FileName.GetRedHash();
            if (embedded.Content is Multilayer_Mask m && m.RenderResourceBlob.RenderResourceBlobPC?.Chunk is rendRenderMultilayerMaskBlob blob)
            {
                var layers = MultilayerMaskCodec.Decode(blob);
                mask = TerrainTextures.Mask(new TerrainPaintLayers { Width = layers.Width, Height = layers.Height, Span = 1, Layers = layers.Layers });
            }
            else if (embedded.Content is CBitmapTexture texture && (path == tintPath || path == normalPath) && CustomMeshes.XbmReader.Decode(texture) is { } image)
            {
                if (path == normalPath) normal = TerrainTextures.Rgba8(image.Width, image.Height, image.Pixels);
                else tint = TerrainTextures.Rgba8(image.Width, image.Height, image.Pixels);
            }
        }
        return (mask, normal, tint);
    }

    private StreamingDescriptor[]? _exteriorDescriptors;
    private readonly ConcurrentDictionary<ulong, IReadOnlyList<TerrainCollision>> _terrainCollisionSectors = new();

    private StreamingDescriptor[]? _worldDescriptors;

    private async Task<StreamingDescriptor[]> ExteriorDescriptorsAsync(CancellationToken cancellation)
    {
        if (_exteriorDescriptors is null)
        {
            var all = await WorldDescriptorsAsync(cancellation).ConfigureAwait(false);
            _exteriorDescriptors = all.Where(d => d.Category == "Exterior").ToArray();
        }
        return _exteriorDescriptors;
    }

    // Night City's sectors with valid bounds, every category.
    private async Task<StreamingDescriptor[]> WorldDescriptorsAsync(CancellationToken cancellation)
    {
        if (_worldDescriptors is null)
        {
            var world = Worlds.FirstOrDefault(w => w.Path.EndsWith(@"\03_night_city.streamingworld", StringComparison.OrdinalIgnoreCase));
            if (world is null) return [];
            var catalog = await LoadWorldCatalogAsync(world.Hash, null, cancellation).ConfigureAwait(false);
            _worldDescriptors = catalog.Descriptors.Where(d => d.HasValidBounds && d.Sector.Resolved).ToArray();
        }
        return _worldDescriptors;
    }

    // The plant nodes (worldFoliageNode) standing in a game-space rectangle: each with its sector
    // placement, its stances (offsets from the placement) and its buckets (consecutive ranges of
    // its stances, relative to its first), read from its compiled foliage resource.
    public async Task<IReadOnlyList<FoliagePlacement>> FindFoliageAsync(Vector2 minimum, Vector2 maximum, CancellationToken cancellation = default)
    {
        var descriptors = await WorldDescriptorsAsync(cancellation).ConfigureAwait(false);
        var result = new List<FoliagePlacement>();
        var resources = new Dictionary<ulong, FoliagePopulation?>();
        foreach (var d in descriptors.Where(d => d.GameBounds.Min.X <= maximum.X && d.GameBounds.Max.X >= minimum.X && d.GameBounds.Min.Y <= maximum.Y && d.GameBounds.Max.Y >= minimum.Y)
            .Select(d => d.Sector).DistinctBy(s => s.Hash))
        {
            cancellation.ThrowIfCancellationRequested();
            var entry = EntryOrNull(d.Hash);
            if (entry is null) continue;
            using var data = ExtractEntry(entry, default);
            using var reader = new CR2WReader(data);
            reader.ParsingError += SectorReader.TolerateSectorParsingError;
            if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file is null) continue;
            using (file)
            {
                if (file.RootChunk is not worldStreamingSector sector || sector.NodeData.Buffer.Data is not worldNodeDataBuffer setups) continue;
                // The resources this sector carries itself (outside the city no archive lists them).
                Dictionary<ulong, FoliagePopulation?>? carried = null;
                for (int i = 0; i < setups.Count; i++)
                {
                    var setup = setups[i];
                    if (setup.NodeIndex >= sector.Nodes.Count || sector.Nodes[setup.NodeIndex].Chunk is not worldFoliageNode node) continue;
                    if (setup.Position.X < minimum.X || setup.Position.X > maximum.X || setup.Position.Y < minimum.Y || setup.Position.Y > maximum.Y) continue;
                    ulong hash = node.FoliageResource.DepotPath.GetRedHash();
                    if (!resources.TryGetValue(hash, out var resource)) resources[hash] = resource = ReadFoliageResource(hash);
                    if (resource is null && hash != 0 && !(carried ??= []).TryGetValue(hash, out resource)) carried[hash] = resource = FoliagePopulation.Carried(file, hash);
                    if (resource is not { } r) continue;
                    var span = node.PopulationSpanInfo;
                    int begin = (int)(uint)span.StancesBegin, count = (int)(uint)span.StancesCount, bucketBegin = (int)(uint)span.CketBegin, buckets = (int)(uint)span.CketCount;
                    if (count == 0 || begin < 0 || begin + count > r.Stances.Length || bucketBegin + buckets > r.Buckets.Length) continue;
                    var transform = new PlacementTransform(new(setup.Position.X, setup.Position.Y, setup.Position.Z),
                        System.Numerics.Quaternion.Normalize(new(setup.Orientation.I, setup.Orientation.J, setup.Orientation.K, setup.Orientation.R)),
                        new(setup.Scale.X, setup.Scale.Y, setup.Scale.Z));
                    result.Add(new(entry.FileName ?? d.Path, setups.Count, i, transform, (worldFoliageNode)node.DeepCopy(),
                        r.Stances.AsSpan(begin, count).ToArray(), r.Buckets.AsSpan(bucketBegin, buckets).ToArray(), hash));
                }
            }
        }
        return result;
    }

    // A resource by its path hash, parsed (the caller disposes it), or null.
    internal WolvenKit.RED4.Archive.CR2W.CR2WFile? ReadFile(ulong hash)
    {
        var entry = EntryOrNull(hash);
        if (entry is null) return null;
        using var data = ExtractEntry(entry, default);
        using var reader = new CR2WReader(data);
        return reader.ReadFile(out var file) == EFileReadErrorCodes.NoError ? file : null;
    }

    // A compiled foliage file's stances and buckets (null when the archives do not hold it or it is unreadable).
    private FoliagePopulation? ReadFoliageResource(ulong hash)
    {
        try { return ReadFoliagePopulation(hash); }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or InvalidCastException) { return null; }
    }

    // The collision node of a terrain cell: in one exterior sector of Night City's world (levels 2
    // to 6), found among the sectors whose streaming box holds the cell centre, nearest level first.
    public async Task<TerrainCollision?> FindTerrainCollisionAsync(TerrainCell cell, CancellationToken cancellation = default)
    {
        if ((await ExteriorDescriptorsAsync(cancellation).ConfigureAwait(false)).Length == 0) return null;
        var centre = cell.Centre;
        // Sectors already read first (neighbouring cells share them).
        foreach (var known in _terrainCollisionSectors.Values)
            if (known.FirstOrDefault(c => MathF.Abs(c.NodePosition.X - centre.X) < 1 && MathF.Abs(c.NodePosition.Y - centre.Y) < 1) is { } found) return found;
        var candidates = _exteriorDescriptors
            .Where(d => d.GameBounds.Min.X <= centre.X && d.GameBounds.Max.X >= centre.X && d.GameBounds.Min.Y <= centre.Y && d.GameBounds.Max.Y >= centre.Y)
            .OrderBy(d => d.Level).ThenBy(d => d.GameBounds.Size.X * d.GameBounds.Size.Y)
            .Select(d => d.Sector.Hash).Distinct();
        foreach (var hash in candidates)
        {
            cancellation.ThrowIfCancellationRequested();
            var collisions = _terrainCollisionSectors.GetOrAdd(hash, TerrainCollisions);
            if (collisions.FirstOrDefault(c => MathF.Abs(c.NodePosition.X - centre.X) < 1 && MathF.Abs(c.NodePosition.Y - centre.Y) < 1) is { } found) return found;
        }
        return null;
    }
}
