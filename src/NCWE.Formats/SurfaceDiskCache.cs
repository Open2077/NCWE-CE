// SPDX-License-Identifier: GPL-3.0-only
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NCWE.Core;
using Vector2 = System.Numerics.Vector2;

namespace NCWE.Formats;

// Mesh surfaces (UV0 and the materials of each appearance) in the user's local cache. Reading
// one from the archives parses the mesh and its material chain with WolvenKit, which was most
// of the garbage made while game materials stream in, and again on every revisit. Entries are
// keyed by the mesh's archive entry and the archive set (a material changed by an update or a
// mod changes the set). Like the mesh cache: a folder per format version, written atomically,
// checked by a digest, trimmed least recently used first down to 85 % of the budget.
public sealed class SurfaceDiskCache
{
    // v2: terrain cells carry their tint (BaseColor). v3: masked materials take their template's alpha threshold.
    // v4: textures whose levels declare a garbage row pitch are read (most multilayer colour textures).
    // v5: light gradients (light_gradients.mt) carry their parameters instead of standing as blank screens.
    // v6: the alpha test is the masked instances' only, on the texture's alpha alone (building proxies kept their walls).
    public const string Conversion = "ncwe-surface-v6";
    private const string VersionPrefix = "ncwe-surface-";
    private const uint Magic = 0x3153434E; // "NCS1"
    private static readonly JsonSerializerOptions Options = new() { IncludeFields = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
    private readonly string _root, _directory;
    private readonly long _budget;
    private long _bytes, _writes, _hits, _misses;
    private int _trimming;
    public long Hits => Interlocked.Read(ref _hits);
    public long Misses => Interlocked.Read(ref _misses);
    public string Directory => _directory;

    public SurfaceDiskCache(string directory, long budget)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)); _budget = Math.Max(budget, 64L << 20);
        _directory = Path.Combine(_root, Conversion);
        System.IO.Directory.CreateDirectory(_directory);
        _ = Task.Run(() =>
        {
            try
            {
                long total = 0;
                foreach (var file in new DirectoryInfo(_directory).EnumerateFiles("*.surface")) total += file.Length;
                Interlocked.Add(ref _bytes, total);
                foreach (var stale in new DirectoryInfo(_root).EnumerateDirectories(VersionPrefix + "*"))
                    if (!string.Equals(stale.FullName, _directory, StringComparison.OrdinalIgnoreCase)) try { stale.Delete(true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                foreach (var temporary in new DirectoryInfo(_directory).EnumerateFiles("*.tmp-*"))
                    if (DateTime.UtcNow - temporary.LastWriteTimeUtc > TimeSpan.FromMinutes(10)) try { temporary.Delete(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                if (Interlocked.Read(ref _bytes) > _budget) Trim();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        });
    }

    public static string Key(ResourceProvenance source, string archiveSet)
    {
        if (source.EntrySha1 is not { Length: 40 }) throw new InvalidDataException("A cache key requires an archive-entry fingerprint.");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Conversion}|{source.Hash:x16}|{source.EntrySha1.ToUpperInvariant()}|{archiveSet}")));
    }

    // The stored surface, or null (absent, unreadable or from another key: it is then deleted).
    public MeshSurface? Read(string key, ulong mesh)
    {
        var path = Path.Combine(_directory, key + ".surface");
        if (!File.Exists(path)) { Interlocked.Increment(ref _misses); return null; }
        byte[]? bytes = null;
        try
        {
            var info = new FileInfo(path);
            if (info.Length is < 64 or > int.MaxValue) throw new InvalidDataException("Invalid surface cache size.");
            int length = (int)info.Length;
            bytes = System.Buffers.ArrayPool<byte>.Shared.Rent(length);
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan)) file.ReadExactly(bytes, 0, length);
            if (!SHA256.HashData(bytes.AsSpan(0, length - 32)).AsSpan().SequenceEqual(bytes.AsSpan(length - 32, 32))) throw new InvalidDataException("Surface cache checksum mismatch.");
            using var stream = new MemoryStream(bytes, 0, length - 32, false);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            if (reader.ReadUInt32() != Magic || reader.ReadString() != key || reader.ReadUInt64() != mesh) throw new InvalidDataException("Surface cache key mismatch.");
            int parts = reader.ReadInt32();
            if (parts is < 0 or > 65536) throw new InvalidDataException("Invalid cached part count.");
            var uvs = new Vector2[]?[parts]; var counts = new int[parts];
            for (int i = 0; i < parts; i++)
            {
                counts[i] = reader.ReadInt32(); int uv = reader.ReadInt32();
                if (uv < -1 || (long)uv * 8 > stream.Length - stream.Position) throw new InvalidDataException("Invalid cached UV count.");
                if (uv < 0) continue;
                var array = GC.AllocateUninitializedArray<Vector2>(uv);   // short-lived: uploaded, then dropped
                stream.ReadExactly(System.Runtime.InteropServices.MemoryMarshal.AsBytes(array.AsSpan()));
                uvs[i] = array;
            }
            int json = reader.ReadInt32();
            if (json < 0 || json != stream.Length - stream.Position) throw new InvalidDataException("Invalid cached material data.");
            var appearances = JsonSerializer.Deserialize<Dictionary<string, SurfaceMaterial?[]>>(bytes.AsSpan((int)stream.Position, json), Options)
                ?? throw new InvalidDataException("Missing cached materials.");
            if (appearances.Values.Any(m => m.Length != parts)) throw new InvalidDataException("Cached materials do not match the parts.");
            Interlocked.Increment(ref _hits);
            if (DateTime.UtcNow - info.LastWriteTimeUtc > TimeSpan.FromHours(12))
                try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch (Exception touch) when (touch is IOException or UnauthorizedAccessException) { }
            return new(mesh, uvs, counts, new Dictionary<string, SurfaceMaterial?[]>(appearances, StringComparer.Ordinal));
        }
        catch (UnauthorizedAccessException) { Interlocked.Increment(ref _misses); return null; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ArgumentException or NotSupportedException or OverflowException)
        {
            Interlocked.Increment(ref _misses);
            try { File.Delete(path); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            return null;
        }
        finally { if (bytes is not null) System.Buffers.ArrayPool<byte>.Shared.Return(bytes); }
    }

    public void Store(string key, MeshSurface surface)
    {
        var path = Path.Combine(_directory, key + ".surface");
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using var data = new MemoryStream();
            using (var writer = new BinaryWriter(data, Encoding.UTF8, true))
            {
                writer.Write(Magic); writer.Write(key); writer.Write(surface.Mesh); writer.Write(surface.Uvs.Length);
                for (int i = 0; i < surface.Uvs.Length; i++)
                {
                    writer.Write(i < surface.VertexCounts.Length ? surface.VertexCounts[i] : 0);
                    var uv = surface.Uvs[i];
                    writer.Write(uv?.Length ?? -1);
                    if (uv is not null) { writer.Flush(); data.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(uv.AsSpan())); }
                }
                var json = JsonSerializer.SerializeToUtf8Bytes(surface.Appearances.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal), Options);
                writer.Write(json.Length); writer.Write(json);
            }
            var digest = SHA256.HashData(data.GetBuffer().AsSpan(0, checked((int)data.Length)));
            data.Write(digest);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.None))
                output.Write(data.GetBuffer(), 0, checked((int)data.Length));
            File.Move(temporary, path, true);
            long total = Interlocked.Add(ref _bytes, data.Length);
            if (total > _budget || Interlocked.Increment(ref _writes) % 1024 == 0) ScheduleTrim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { }
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

    private void Trim()
    {
        try
        {
            var files = new DirectoryInfo(_directory).EnumerateFiles("*.surface").ToArray();
            long bytes = files.Sum(f => f.Length);
            if (bytes > _budget)
            {
                long target = _budget / 100 * 85;
                foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc))
                {
                    if (bytes <= target) break;
                    long length = file.Length;
                    try { file.Delete(); bytes -= length; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
            Interlocked.Exchange(ref _bytes, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
