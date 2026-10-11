// SPDX-License-Identifier: GPL-3.0-only
using WolvenKit.RED4.Archive.IO;
using WolvenKit.RED4.Types;

namespace NCWE.Formats;

public sealed partial class GameArchiveIndex
{
    /// Runs `read` on a streaming sector's root chunk while its file is open, for reads
    /// that need the raw nodes (LoadSectorAsync gives placements only). Null when the sector
    /// is not in the archives or does not parse.
    public T? ReadStreamingSector<T>(ulong sectorHash, Func<worldStreamingSector, T> read) where T : class
    {
        if (EntryOrNull(sectorHash) is not { } entry) return null;
        using var data = ExtractEntry(entry, default);
        using var reader = new CR2WReader(data);
        reader.ParsingError += SectorReader.TolerateSectorParsingError;
        if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file is null) return null;
        using (file) return file.RootChunk is worldStreamingSector sector ? read(sector) : null;
    }
}
