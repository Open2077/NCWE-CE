// SPDX-License-Identifier: GPL-3.0-only
using WolvenKit.Common.FNV1A;
using WolvenKit.RED4.Archive.CR2W;
using WolvenKit.RED4.Archive.IO;

namespace NCWE.Formats;

// Raw access to vanilla resources by depot path (material templates, textures), for building
// custom assets that reference or imitate them.
public sealed partial class GameArchiveIndex
{
    public bool Contains(string depotPath) => _entries.ContainsKey(FNV1A64HashAlgorithm.HashString(depotPath));

    public byte[]? ReadBytes(string depotPath)
    {
        if (!_entries.TryGetValue(FNV1A64HashAlgorithm.HashString(depotPath), out var entry)) return null;
        using var data = ExtractEntry(entry, default);
        return data.ToArray();
    }

    // The parsed resource (the caller disposes it), or null when it is not in the game.
    public CR2WFile? ReadFile(string depotPath)
    {
        if (ReadBytes(depotPath) is not { } bytes) return null;
        using var stream = new MemoryStream(bytes);
        using var reader = new CR2WReader(stream);
        return reader.ReadFile(out var file) == EFileReadErrorCodes.NoError ? file : null;
    }

    // Depot paths of the indexed resources with one of these extensions.
    public IEnumerable<string> PathsWithExtension(params string[] extensions) =>
        _entries.Values.Where(e => extensions.Any(x => e.Extension.Equals(x, StringComparison.OrdinalIgnoreCase)) && e.FileName is { Length: > 0 }).Select(e => e.FileName!);
}
