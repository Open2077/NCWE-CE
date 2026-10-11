using System.Collections.Concurrent;
using System.Collections.Immutable;
// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using NCWE.Core;

namespace NCWE.Formats;

// Converted, game-derived data stays in the user's local cache. It is never a
// project dependency or distributed asset. One streamer job owns each read/write.
public sealed class CachedStreamingSource : IStreamingSource
{
    private readonly GameArchiveIndex _index;
    private readonly ConvertedMeshCache _cache;
    private readonly SnapshotCache? _sectors;
    private readonly bool _concurrent;
    public long CacheHits => _cache.Hits + (_sectors?.Hits ?? 0);
    public long CacheMisses => _cache.Misses + (_sectors?.Misses ?? 0);
    public long CorruptEntries => _cache.CorruptEntries + (_sectors?.CorruptEntries ?? 0);
    // Cumulated worker time per step (diagnostics): [mesh cache read, mesh decode, mesh store, sector cache read, sector decode, sector store].
    private readonly long[] _ticks = new long[6], _counts = new long[6];
    public static readonly string[] StepNames = ["meshCacheRead", "meshDecode", "meshStore", "sectorCacheRead", "sectorDecode", "sectorStore"];
    public object Timings => StepNames.Select((name, i) => new { name, count = Interlocked.Read(ref _counts[i]),
        totalMs = Math.Round(System.Diagnostics.Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _ticks[i])).TotalMilliseconds),
        avgMs = Interlocked.Read(ref _counts[i]) == 0 ? 0 : Math.Round(System.Diagnostics.Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _ticks[i])).TotalMilliseconds / Interlocked.Read(ref _counts[i]), 2) }).ToArray();
    private void Time(int step, long started) { Interlocked.Add(ref _ticks[step], System.Diagnostics.Stopwatch.GetTimestamp() - started); Interlocked.Increment(ref _counts[step]); }

    // With a sector cache directory and concurrent=true, several streamer workers may
    // call this source at once: archive extraction is serialized inside the index.
    // maximumDecodes bounds concurrent cold decodes (WolvenKit allocates heavily: fewer live
    // decodes mean shorter GC pauses for the UI); cache reads are not limited by it.
    public CachedStreamingSource(GameArchiveIndex index, string directory, long diskBudget = 2L * 1024 * 1024 * 1024,
        string? sectorDirectory = null, bool concurrent = false, int maximumDecodes = int.MaxValue)
    {
        _index = index; _cache = new(directory, diskBudget); _sectors = sectorDirectory is null ? null : new(sectorDirectory); _concurrent = concurrent;
        _decodes = maximumDecodes == int.MaxValue ? null : new SemaphoreSlim(maximumDecodes, maximumDecodes);
    }
    private readonly SemaphoreSlim? _decodes;
    public ConvertedMeshCache MeshCache => _cache;

    // Preflight: converts a mesh into the disk cache unless it is already there. Nothing
    // stays in memory. Returns whether a conversion happened.
    public async Task<bool> WarmMeshAsync(ulong hash, long maximumBytes, CancellationToken cancellation)
    {
        if (_index.IsCustom(hash)) return false;   // custom meshes are read from the user's library, never cached
        var source = _index.Provenance(hash);
        if (!source.Resolved || source.SourceBytes > maximumBytes || source.EntrySha1 is not { Length: 40 } || _cache.Contains(source)) return false;
        if (_decodes is not null) await _decodes.WaitAsync(cancellation).ConfigureAwait(false);
        MeshAsset mesh;
        try { mesh = await _index.ReadMeshConcurrentAsync(hash, cancellation, maximumBytes, maximumBytes).ConfigureAwait(false); }
        finally { _decodes?.Release(); }
        await _cache.StoreAsync(source, mesh, cancellation).ConfigureAwait(false);
        return true;
    }

    public async Task<SectorSnapshot> ReadSectorAsync(ulong hash, long maximumSourceBytes, CancellationToken cancellation)
    {
        var source = _index.Provenance(hash);
        string? key = _sectors is not null && source.EntrySha1 is { Length: 40 } ? SnapshotCache.SectorKey(source) : null;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (key is not null && await Task.Run(() => _sectors!.Read<SectorSnapshot>(key) is { } read ? Canonicalize(read) : null, cancellation).ConfigureAwait(false) is { } cached
            && cached.Resource.Hash == hash) { Time(3, started); return cached; }
        if (_decodes is not null) await _decodes.WaitAsync(cancellation).ConfigureAwait(false);
        SectorSnapshot sector;
        started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            sector = _concurrent ? await _index.ReadSectorConcurrentAsync(hash, cancellation, maximumSourceBytes).ConfigureAwait(false)
                : await _index.LoadSectorAsync(hash, cancellation, maximumSourceBytes).ConfigureAwait(false);
        }
        finally { _decodes?.Release(); }
        Time(4, started); started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (key is not null) _sectors!.Store(key, sector);
        Time(5, started);
        return sector;
    }

    // JSON deserialization gives every placement its own copies of shared values. Restore
    // the sharing SectorReader produces: one identity per sector, one reference per
    // mesh/appearance, interned node types. Fewer objects make every full GC cheaper.
    private static readonly ConcurrentDictionary<string, string> NodeTypes = new(StringComparer.Ordinal);
    public static SectorSnapshot Canonicalize(SectorSnapshot sector)
    {
        var resource = sector.Resource;
        var meshes = new Dictionary<(ulong, string), MeshReference>();
        var placements = ImmutableArray.CreateBuilder<SourcePlacement>(sector.Placements.Length);
        foreach (var p in sector.Placements)
        {
            var anchor = p.Source with { Sector = p.Source.Sector == resource ? resource : p.Source.Sector, NodeType = NodeTypes.GetOrAdd(p.Source.NodeType, p.Source.NodeType) };
            var mesh = p.Mesh;
            if (mesh is not null)
            {
                if (meshes.TryGetValue((mesh.Hash, mesh.Appearance), out var shared) && shared == mesh) mesh = shared;
                else meshes[(mesh.Hash, mesh.Appearance)] = mesh;
            }
            placements.Add(p with { Source = anchor, Mesh = mesh });
        }
        var nodeNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var actors = sector.CollisionActors.IsDefaultOrEmpty ? sector.CollisionActors
            : sector.CollisionActors.Select(a => a with { Node = nodeNames.TryGetValue(a.Node, out var shared) ? shared : nodeNames[a.Node] = a.Node }).ToImmutableArray();
        return sector with { Placements = placements.MoveToImmutable(), CollisionActors = actors.IsDefault ? [] : actors };
    }

    public async Task<MeshAsset> ReadMeshAsync(ulong hash, long maximumMeshBytes, long maximumSourceBytes, CancellationToken cancellation)
    {
        if (_index.IsCustom(hash)) return await _index.ReadMeshConcurrentAsync(hash, cancellation).ConfigureAwait(false);
        var source = _index.Provenance(hash);
        if (!source.Resolved) throw new FileNotFoundException($"Mesh {hash} is missing from the selected archive profile.");
        if (source.SourceBytes > maximumSourceBytes) throw new NotSupportedException("Mesh exceeds the configured source budget.");
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        var cached = await _cache.ReadAsync(source, maximumMeshBytes, cancellation).ConfigureAwait(false);
        Time(0, started);
        if (cached is not null) return cached;
        if (_decodes is not null) await _decodes.WaitAsync(cancellation).ConfigureAwait(false);
        MeshAsset mesh;
        started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            mesh = _concurrent ? await _index.ReadMeshConcurrentAsync(hash, cancellation, maximumMeshBytes, maximumSourceBytes).ConfigureAwait(false)
                : await _index.LoadMeshAsync(hash, cancellation, maximumMeshBytes, maximumSourceBytes).ConfigureAwait(false);
        }
        finally { _decodes?.Release(); }
        Time(1, started); started = System.Diagnostics.Stopwatch.GetTimestamp();
        await _cache.StoreAsync(source, mesh, cancellation).ConfigureAwait(false);
        Time(2, started);
        return mesh;
    }
}

// Independently testable using synthetic geometry: no archives, Oodle or GPU.
// Entries live in a folder named after the conversion version, so a version bump never
// leaves unusable files counting against the budget: older version folders (and the flat
// pre-versioning layout) are deleted in the background. The size is tracked in memory;
// writes never scan the folder. When the budget is exceeded, one background trim deletes
// the least recently used entries down to 85 % of it (hysteresis: trims stay rare).
public sealed class ConvertedMeshCache
{
    public const string Conversion = "ncwe-mesh-v13-wk9.0.1-yup-clay-colors-skinned-sides-kind";
    private const string VersionPrefix = "ncwe-mesh-";
    private readonly string _root, _directory;
    private readonly long _diskBudget;
    private readonly string _mutexName;
    private long _hits, _misses, _corrupt, _bytes, _writes;
    private int _trimming;
    private readonly Task _initialized;
    public long Hits => Interlocked.Read(ref _hits);
    public long Misses => Interlocked.Read(ref _misses);
    public long CorruptEntries => Interlocked.Read(ref _corrupt);
    // Estimated bytes of this version's entries (exact after a scan or trim).
    public long EstimatedBytes => Interlocked.Read(ref _bytes);
    public string Directory => _directory;
    public Task Initialized => _initialized;

    public ConvertedMeshCache(string directory, long diskBudget = 2L * 1024 * 1024 * 1024)
    {
        if (diskBudget < 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(diskBudget));
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)); _diskBudget = diskBudget;
        _directory = Path.Combine(_root, Conversion);
        _mutexName = "Local\\NCWE.MeshCache." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_directory.ToUpperInvariant())));
        System.IO.Directory.CreateDirectory(_directory);
        _initialized = Task.Run(Initialize);
    }

    private void Initialize()
    {
        try
        {
            long total = 0;
            foreach (var file in new DirectoryInfo(_directory).EnumerateFiles("*.meshcache")) total += file.Length;
            Interlocked.Add(ref _bytes, total);
            if (Interlocked.Read(ref _bytes) > _diskBudget) Trim();
            // Stale data: other conversion versions and the flat legacy layout.
            foreach (var stale in new DirectoryInfo(_root).EnumerateDirectories(VersionPrefix + "*"))
                if (!string.Equals(stale.FullName, _directory, StringComparison.OrdinalIgnoreCase)) DeleteQuietly(stale);
            foreach (var legacy in new DirectoryInfo(_root).EnumerateFiles("*.meshcache")) DeleteQuietly(legacy);
            foreach (var temporary in new DirectoryInfo(_root).EnumerateFiles("*.meshcache.tmp-*")) DeleteQuietly(temporary);
            // Interrupted writes (killed process); recent ones may belong to a live writer.
            foreach (var temporary in new DirectoryInfo(_directory).EnumerateFiles("*.meshcache.tmp-*"))
                if (DateTime.UtcNow - temporary.LastWriteTimeUtc > TimeSpan.FromMinutes(10)) DeleteQuietly(temporary);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* A cache failure never blocks loading. */ }
    }

    private static void DeleteQuietly(FileSystemInfo item)
    {
        try { if (item is DirectoryInfo folder) folder.Delete(true); else item.Delete(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string Key(ResourceProvenance source)
    {
        if (source.EntrySha1 is not { Length: 40 } || !source.EntrySha1.All(Uri.IsHexDigit)) throw new InvalidDataException("A cache key requires an archive-entry fingerprint.");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Conversion}|vanilla-content-v1|{source.Hash:x16}|{source.EntrySha1.ToUpperInvariant()}")));
    }
    public bool Contains(ResourceProvenance source) => File.Exists(Path.Combine(_directory, Key(source) + ".meshcache"));
    public Task<MeshAsset?> ReadAsync(ResourceProvenance source, long maximumBytes, CancellationToken cancellation = default)
    {
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var key = Key(source);
        return Task.Run(() => { var mesh = TryRead(Path.Combine(_directory, key + ".meshcache"), key, maximumBytes, cancellation);
            if (mesh is null) Interlocked.Increment(ref _misses); else Interlocked.Increment(ref _hits); return mesh; }, cancellation);
    }
    public Task StoreAsync(ResourceProvenance source, MeshAsset mesh, CancellationToken cancellation = default)
    { var key = Key(source); return Task.Run(() => Write(Path.Combine(_directory, key + ".meshcache"), key, mesh, cancellation), cancellation); }

    private MeshAsset? TryRead(string path, string key, long maximumBytes, CancellationToken cancellation)
    {
        if (!File.Exists(path)) return null;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if (info.Length < 64) throw new InvalidDataException("Truncated cache file.");
            if (info.Length - 4 * 1024 * 1024 > maximumBytes) return null;
            if (info.Length > int.MaxValue) throw new InvalidDataException("Cache file too large.");
            // A pooled buffer: cache reads are frequent and large, and fresh arrays per read
            // land on the large-object heap and trigger blocking collections.
            int length = (int)info.Length;
            var bytes = System.Buffers.ArrayPool<byte>.Shared.Rent(length);
            try
            {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan)) file.ReadExactly(bytes, 0, length);
            if (!SHA256.HashData(bytes.AsSpan(0, length - 32)).AsSpan().SequenceEqual(bytes.AsSpan(length - 32, 32))) throw new InvalidDataException("Cache checksum mismatch.");
            using var stream = new MemoryStream(bytes, 0, length - 32, false);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            if (reader.ReadUInt32() != 0x314D434E || ReadString(reader) != key) throw new InvalidDataException("Cache version/key mismatch.");
            var resourcePath = ReadString(reader); var fingerprint = ReadString(reader); var archive = ReadString(reader);
            var min = ReadVector(reader); var max = ReadVector(reader); byte objectType = reader.ReadByte();
            int count = reader.ReadInt32(); if (count is < 1 or > 65536) throw new InvalidDataException("Invalid cached part count.");
            var parts = new MeshPart[count]; long owned = 0;
            for (int i = 0; i < count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                string name = ReadString(reader); uint lod = reader.ReadUInt32(); bool oneSided = reader.ReadBoolean(); int vertices = reader.ReadInt32(), indices = reader.ReadInt32();
                if (vertices <= 0 || indices <= 0 || indices % 3 != 0) throw new InvalidDataException("Invalid cached mesh counts.");
                long size = checked(vertices * 24L + indices * 4L); owned = checked(owned + size);
                if (size > stream.Length - stream.Position) throw new InvalidDataException("Cached geometry exceeds file length.");
                if (owned > maximumBytes) return null; // Healthy cache entry, insufficient current admission budget.
                var vs = GeometryArrays.Allocate<MeshVertex>(vertices); var ix = GeometryArrays.Allocate<uint>(indices);
                // Same little-endian layout as written: position xyz, normal xyz; uint indices.
                stream.ReadExactly(System.Runtime.InteropServices.MemoryMarshal.AsBytes(vs.AsSpan()));
                stream.ReadExactly(System.Runtime.InteropServices.MemoryMarshal.AsBytes(ix.AsSpan()));
                parts[i] = new(name, lod, vs, ix) { OneSided = oneSided }; parts[i].Validate();
            }
            int appearanceCount = reader.ReadInt32();
            if (appearanceCount is < 0 or > 4096) throw new InvalidDataException("Invalid cached appearance count.");
            Dictionary<string, System.Numerics.Vector4[]>? appearances = appearanceCount == 0 ? null : new(StringComparer.Ordinal);
            for (int a = 0; a < appearanceCount; a++)
            {
                string appearance = ReadString(reader); int colorCount = reader.ReadInt32();
                if (colorCount != count) throw new InvalidDataException("Cached appearance colors do not match the parts.");
                var colors = new System.Numerics.Vector4[colorCount];
                for (int k = 0; k < colorCount; k++) colors[k] = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                appearances![appearance] = colors;
            }
            if (stream.Position != stream.Length || !SpatialBounds.IsValid(new(min, max))) throw new InvalidDataException("Invalid cached bounds or trailing data.");
            var bounds = BoundsOf(parts);
            if (bounds != new Bounds3(min, max)) throw new InvalidDataException("Cached bounds differ from geometry.");
            // Recency for trimming; a metadata write per hit is avoided when it was refreshed lately.
            if (DateTime.UtcNow - info.LastWriteTimeUtc > TimeSpan.FromHours(12))
                try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch (Exception touch) when (touch is IOException or UnauthorizedAccessException) { }
            return new(resourcePath, fingerprint, parts, bounds, archive, 0) { AppearanceColors = appearances, ObjectType = objectType };
            }
            finally { System.Buffers.ArrayPool<byte>.Shared.Return(bytes); }
        }
        catch (UnauthorizedAccessException) { return null; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OverflowException or ArgumentException)
        {
            Interlocked.Increment(ref _corrupt);
            try { File.Delete(path); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            return null;
        }
    }

    private void Write(string path, string key, MeshAsset mesh, CancellationToken cancellation)
    {
        if (mesh.CpuBytes > _diskBudget) return;
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using var data = new MemoryStream(checked((int)Math.Min(int.MaxValue, mesh.CpuBytes + 4096)));
            using (var writer = new BinaryWriter(data, Encoding.UTF8, true))
            {
                writer.Write(0x314D434Eu); WriteString(writer, key); WriteString(writer, mesh.ResourcePath); WriteString(writer, mesh.SourceHash); WriteString(writer, mesh.SourceArchive);
                WriteVector(writer, mesh.Bounds.Min); WriteVector(writer, mesh.Bounds.Max); writer.Write(mesh.ObjectType); writer.Write(mesh.Parts.Length);
                foreach (var part in mesh.Parts)
                {
                    cancellation.ThrowIfCancellationRequested();
                    WriteString(writer, part.Name); writer.Write(part.LodMask); writer.Write(part.OneSided); writer.Write(part.Vertices.Length); writer.Write(part.Indices.Length);
                    writer.Flush();
                    // Same little-endian layout the reader maps back: position xyz, normal xyz; uint indices.
                    data.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(part.Vertices.AsSpan()));
                    data.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(part.Indices.AsSpan()));
                }
                var appearances = mesh.AppearanceColors ?? new Dictionary<string, System.Numerics.Vector4[]>();
                writer.Write(appearances.Count);
                foreach (var (appearance, colors) in appearances)
                {
                    WriteString(writer, appearance); writer.Write(colors.Length);
                    foreach (var color in colors) { writer.Write(color.X); writer.Write(color.Y); writer.Write(color.Z); writer.Write(color.W); }
                }
            }
            var digest = SHA256.HashData(data.GetBuffer().AsSpan(0, checked((int)data.Length)));
            data.Write(digest); cancellation.ThrowIfCancellationRequested();
            if (data.Length > _diskBudget) return;
            // Atomic publish: readers see the old entry or the complete new one, never a partial file.
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.None))
                output.Write(data.GetBuffer(), 0, checked((int)data.Length));
            File.Move(temporary, path, true);
            long total = Interlocked.Add(ref _bytes, data.Length);
            // Other processes may share this folder: rescan now and then to correct the estimate.
            if (total > _diskBudget || Interlocked.Increment(ref _writes) % 1024 == 0) ScheduleTrim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Cache failure does not prevent editing. */ }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
        }
    }

    private void ScheduleTrim()
    {
        if (Interlocked.CompareExchange(ref _trimming, 1, 0) != 0) return;
        Task.Run(() => { try { Trim(); } finally { Volatile.Write(ref _trimming, 0); } });
    }

    // Rescans the folder; past the budget, deletes least recently used entries down to 85 %.
    // Cross-process: one trimmer at a time; a busy mutex means another process is trimming.
    public void Trim()
    {
        using var mutex = new Mutex(false, _mutexName);
        bool locked;
        try { locked = mutex.WaitOne(TimeSpan.FromSeconds(5)); } catch (AbandonedMutexException) { locked = true; }
        if (!locked) return;
        try
        {
            var files = new DirectoryInfo(_directory).EnumerateFiles("*.meshcache").ToArray();
            long bytes = files.Sum(f => f.Length);
            if (bytes > _diskBudget)
            {
                long target = _diskBudget / 100 * 85;
                foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc))
                {
                    if (bytes <= target) break;
                    long length = file.Length; // FileInfo is invalidated by Delete
                    try { file.Delete(); bytes -= length; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
            Interlocked.Exchange(ref _bytes, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally { mutex.ReleaseMutex(); }
    }

    private static Bounds3 BoundsOf(MeshPart[] parts)
    {
        var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
        foreach (var part in parts) foreach (var v in part.Vertices) { min = Vector3.Min(min, v.Position); max = Vector3.Max(max, v.Position); }
        return new(min, max);
    }
    private static Vector3 ReadVector(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    private static void WriteVector(BinaryWriter w, Vector3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
    private static string ReadString(BinaryReader r)
    {
        int length = r.ReadInt32(); if (length is < 0 or > 65536) throw new InvalidDataException("Invalid cache string length.");
        var bytes = r.ReadBytes(length); if (bytes.Length != length) throw new EndOfStreamException(); return Encoding.UTF8.GetString(bytes);
    }
    private static void WriteString(BinaryWriter w, string s) { var bytes = Encoding.UTF8.GetBytes(s); w.Write(bytes.Length); w.Write(bytes); }
}
