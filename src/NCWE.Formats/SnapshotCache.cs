// SPDX-License-Identifier: GPL-3.0-only
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NCWE.Core;

namespace NCWE.Formats;

// Local, game-derived metadata cache (sector placements, streaming catalogs). Entries are
// keyed by archive-entry fingerprints and the conversion version, stored as gzip JSON
// with the key embedded, written atomically, and discarded on any mismatch or damage.
public sealed class SnapshotCache
{
    // Bump when SectorReader/StreamingCatalogReader output or the record shapes change.
    public const string Conversion = "ncwe-snapshot-v27-wk9.0.1";
    private static readonly JsonSerializerOptions Options = new() { IncludeFields = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
    private readonly string _directory;
    public string Directory => _directory;
    private long _hits, _misses, _corrupt;
    public long Hits => Interlocked.Read(ref _hits);
    public long Misses => Interlocked.Read(ref _misses);
    public long CorruptEntries => Interlocked.Read(ref _corrupt);

    // Entries live in a folder named after the conversion version; older version folders
    // and the flat pre-versioning layout are unusable and deleted in the background.
    public SnapshotCache(string directory)
    {
        var root = Path.GetFullPath(directory);
        _directory = Path.Combine(root, Conversion);
        System.IO.Directory.CreateDirectory(_directory);
        _ = Task.Run(() =>
        {
            try
            {
                foreach (var stale in new DirectoryInfo(root).EnumerateDirectories("ncwe-snapshot-*"))
                    if (!string.Equals(stale.FullName, _directory, StringComparison.OrdinalIgnoreCase)) try { stale.Delete(true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                foreach (var legacy in new DirectoryInfo(root).EnumerateFiles("*.json.gz*")) try { legacy.Delete(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        });
    }

    public static string Key(string kind, params string[] identity)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Conversion + "|vanilla-content-v1|" + kind + "|" + string.Join('|', identity))));

    public static string SectorKey(ResourceProvenance source)
    {
        if (source.EntrySha1 is not { Length: 40 }) throw new InvalidDataException("A cache key requires an archive-entry fingerprint.");
        return Key("sector", source.Hash.ToString("x16"), source.EntrySha1.ToUpperInvariant());
    }

    public T? Read<T>(string key) where T : class
    {
        var path = Path.Combine(_directory, key + ".json.gz");
        if (!File.Exists(path)) { Interlocked.Increment(ref _misses); return null; }
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            var entry = JsonSerializer.Deserialize<Entry<T>>(gzip, Options);
            if (entry is null || entry.Key != key || entry.Value is null) throw new InvalidDataException("Snapshot cache key mismatch.");
            Interlocked.Increment(ref _hits);
            return entry.Value;
        }
        catch (UnauthorizedAccessException) { Interlocked.Increment(ref _misses); return null; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or NotSupportedException)
        {
            Interlocked.Increment(ref _corrupt);
            try { File.Delete(path); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            return null;
        }
    }

    public void Store<T>(string key, T value) where T : class
    {
        var path = Path.Combine(_directory, key + ".json.gz");
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
                JsonSerializer.Serialize(gzip, new Entry<T>(key, value), Options);
            File.Move(temporary, path, true);
        }
        // A cache failure never blocks loading or editing.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException) { }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }

    private sealed record Entry<T>(string Key, T Value);
}
