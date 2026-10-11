// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Security.Cryptography;
using NCWE.Core;
using WolvenKit.RED4.Archive.CR2W;
using WolvenKit.RED4.Archive.IO;
using WolvenKit.RED4.Types;
using Archive = WolvenKit.RED4.Archive.Archive;

namespace NCWE.Formats;

public sealed partial class GameArchiveIndex
{
    public ResourceProvenance Provenance(ulong hash, string? fallbackPath = null) => _entries.TryGetValue(hash, out var entry)
        ? new(hash, entry.FileName, Path.GetFileName(entry.Archive.ArchiveAbsolutePath), Convert.ToHexString(entry.SHA1Hash), entry.Size)
        : new(hash, fallbackPath ?? $"{hash}.bin", null, null, 0);

    // The key covers the world and every indexed block fingerprint, so any archive
    // change (patch, profile) rebuilds the catalog from source.
    public async Task<StreamingCatalog> LoadWorldCatalogCachedAsync(ulong worldHash, SnapshotCache cache, IProgress<string>? progress = null,
        CancellationToken cancellation = default)
    {
        var world = Provenance(worldHash);
        string key = SnapshotCache.Key("catalog", world.Hash.ToString("x16"), world.EntrySha1 ?? "",
            Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join(',', Blocks.Select(b => Provenance(b.Hash).EntrySha1))))));
        progress?.Report("Reading cached Night City streaming catalog...");
        if (await Task.Run(() => cache.Read<StreamingCatalog>(key), cancellation).ConfigureAwait(false) is { SchemaVersion: StreamingCatalog.CurrentSchema } cached)
            return cached;
        var catalog = await LoadWorldCatalogAsync(worldHash, progress, cancellation).ConfigureAwait(false);
        cache.Store(key, catalog);
        return catalog;
    }

    public async Task<StreamingCatalog> LoadWorldCatalogAsync(ulong worldHash, IProgress<string>? progress = null,
        CancellationToken cancellation = default)
    {
        await _worker.WaitAsync(cancellation);
        try
        {
            return await Task.Run(() =>
            {
                // The chosen world's references define membership. Parsing every indexed
                // block would mix menus, tests and other worlds into Night City.
                StreamingWorldSnapshot world;
                using (var file = ReadCatalogResource(worldHash, cancellation, out var identity))
                {
                    if (file.RootChunk is not worldStreamingWorld root) throw new InvalidDataException("Resource is not a streaming world.");
                    world = StreamingCatalogReader.ConvertWorld(root, identity, Provenance(worldHash), Provenance);
                }
                var blocks = ImmutableArray.CreateBuilder<StreamingBlockSnapshot>();
                var diagnostics = ImmutableArray.CreateBuilder<string>();
                var seen = new HashSet<ulong>();
                foreach (var reference in world.BlockReferences)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!seen.Add(reference.Hash)) { diagnostics.Add($"Duplicate block reference: {reference.Path}"); continue; }
                    if (!reference.Resolved) { diagnostics.Add($"Block missing from archive profile: {reference.Path}"); continue; }
                    progress?.Report($"Reading streaming block {seen.Count}/{world.BlockReferences.Length}: {reference.Path}");
                    try
                    {
                        using var file = ReadCatalogResource(reference.Hash, cancellation, out var identity);
                        if (file.RootChunk is not worldStreamingBlock block) throw new InvalidDataException("Resource is not a streaming block.");
                        blocks.Add(StreamingCatalogReader.ConvertBlock(block, identity, reference, Provenance, cancellation));
                    }
                    catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException)
                    { diagnostics.Add($"{reference.Path}: {ex.Message}"); }
                }
                return new StreamingCatalog(StreamingCatalog.CurrentSchema, "vanilla-content-v1", world, blocks.ToImmutable(), diagnostics.ToImmutable());
            }, cancellation);
        }
        finally { _worker.Release(); }
    }

    private CR2WFile ReadCatalogResource(ulong hash, CancellationToken cancellation, out ResourceIdentity identity)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!_entries.TryGetValue(hash, out var entry)) throw new FileNotFoundException($"Unknown resource {hash}.");
        if (entry.Size > 256 * 1024 * 1024) throw new NotSupportedException("Streaming metadata exceeds the 256 MiB resource limit.");
        using var data = ExtractEntry(entry, cancellation);
        cancellation.ThrowIfCancellationRequested();
        identity = new(hash, entry.FileName, Convert.ToHexString(SHA256.HashData(data.GetBuffer().AsSpan(0, checked((int)data.Length)))));
        var log = new CatalogReadLog();
        using var reader = new CR2WReader(data) { LoggerService = log };
        var code = reader.ReadFile(out var file);
        // WK can return NoError after skipping an unreadable property. Do not
        // silently publish an empty/partial descriptor list in that case.
        if (code != EFileReadErrorCodes.NoError || file is null || log.Issues.Count != 0)
        { file?.Dispose(); throw new InvalidDataException($"Streaming metadata CR2W read failed: {code}. {string.Join("; ", log.Issues)}"); }
        return file;
    }

    private sealed class CatalogReadLog : WolvenKit.Core.Interfaces.ILoggerService
    {
        public List<string> Issues { get; } = [];
        public WolvenKit.Common.LoggerVerbosity LoggerVerbosity { get; private set; }
        public void SetLoggerVerbosity(WolvenKit.Common.LoggerVerbosity v) => LoggerVerbosity = v;
        public void Info(string s) { }
        public void Warning(string s) => Issues.Add(s);
        public void Error(string s) => Issues.Add(s);
        public void Error(Exception e) => Issues.Add(e.Message);
        public void Success(string s) { }
        public void Debug(string s) { }
    }
}

public static class StreamingCatalogReader
{
    public static StreamingWorldSnapshot ConvertWorld(worldStreamingWorld world, ResourceIdentity identity,
        ResourceProvenance provenance, Func<ulong, string?, ResourceProvenance> resolve) => new(identity, provenance,
        world.Version, Bounds(world.WorldBoundingBox), world.BlockRefs.Select(r => resolve(r.DepotPath.GetRedHash(), r.DepotPath.GetResolvedText())).ToImmutableArray());

    public static StreamingBlockSnapshot ConvertBlock(worldStreamingBlock block, ResourceIdentity identity,
        ResourceProvenance provenance, Func<ulong, string?, ResourceProvenance> resolve, CancellationToken cancellation = default)
    {
        var descriptors = ImmutableArray.CreateBuilder<StreamingDescriptor>();
        var diagnostics = ImmutableArray.CreateBuilder<string>();
        for (int i = 0; i < block.Descriptors.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var d = block.Descriptors[i];
            var resource = resolve(d.Data.DepotPath.GetRedHash(), d.Data.DepotPath.GetResolvedText());
            var bounds = Bounds(d.StreamingBox);
            if (!SpatialBounds.IsValid(bounds)) diagnostics.Add($"Descriptor {i} has invalid streaming bounds: {resource.Path}");
            if (!resource.Resolved) diagnostics.Add($"Descriptor {i} sector missing from archive profile: {resource.Path}");
            // Keep every variant, including disabled/quest variants, for explicit policy
            // and diagnostics. Discovery does not imply all variants should be rendered.
            var variants = d.Variants.Select(v => new StreamingVariant(v.NodeRef, v.VariantId, v.ParentVariantID,
                v.Name.ToString() ?? "", v.RangeIndex, v.EnabledByDefault)).ToImmutableArray();
            descriptors.Add(new(identity.Hash, i, resource, bounds, d.Level, d.Category.ToString(), Index(d.BlockIndex),
                d.QuestPrefabNodeRef, d.NumNodeRanges, variants));
        }
        return new(identity, provenance, Index(block.Index), descriptors.ToImmutable(), diagnostics.ToImmutable());
    }

    private static Bounds3 Bounds(Box box) => new(new(box.Min.X, box.Min.Y, box.Min.Z), new(box.Max.X, box.Max.Y, box.Max.Z));
    private static StreamingBlockIndex Index(worldStreamingBlockIndex index) => new(index.RldGridCell, index.Oup.ToString());
}
