// SPDX-License-Identifier: GPL-3.0-only
using WolvenKit.RED4.Archive.Buffer;
using WolvenKit.RED4.Archive.IO;
using WolvenKit.RED4.Types;

namespace NCWE.Formats;

public sealed partial class GameArchiveIndex
{
    // Diagnostic: availability of bended-node meshes in one sector.
    public (int Found, int Missing, List<string> MissingPaths) CountBended(ulong sectorHash)
    {
        var entry = EntryOrNull(sectorHash) ?? throw new FileNotFoundException();
        using var data = ExtractEntry(entry, default);
        using var reader = new CR2WReader(data);
        reader.ParsingError += SectorReader.TolerateSectorParsingError;
        if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file is null) return (0, 0, []);
        using (file)
        {
            var sector = (worldStreamingSector)file.RootChunk;
            int found = 0, missing = 0; var paths = new List<string>();
            foreach (var handle in sector.Nodes)
            {
                if (handle.Chunk is not worldBendedMeshNode node) continue;
                if (EntryOrNull(node.Mesh.DepotPath.GetRedHash()) is null) { missing++; paths.Add(node.Mesh.DepotPath.GetResolvedText() ?? node.Mesh.DepotPath.GetRedHash().ToString()); }
                else found++;
            }
            return (found, missing, paths);
        }
    }
}
