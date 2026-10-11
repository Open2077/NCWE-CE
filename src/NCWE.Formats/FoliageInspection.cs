// SPDX-License-Identifier: GPL-3.0-only
using WolvenKit.RED4.Archive.Buffer;
using WolvenKit.RED4.Archive.IO;
using WolvenKit.RED4.Types;

namespace NCWE.Formats;

public sealed partial class GameArchiveIndex
{
    // Diagnostic: the compiled foliage resource of each plant node of a sector, whether the archives
    // hold it, and where the node stands.
    public IReadOnlyList<(string Resource, ulong Hash, bool Present, bool Embedded, System.Numerics.Vector3 Position, int Stances)> FoliageResources(ulong sectorHash)
    {
        var entry = EntryOrNull(sectorHash) ?? throw new FileNotFoundException();
        using var data = ExtractEntry(entry, default);
        using var reader = new CR2WReader(data);
        reader.ParsingError += SectorReader.TolerateSectorParsingError;
        if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file is null) throw new InvalidDataException();
        var found = new List<(string, ulong, bool, bool, System.Numerics.Vector3, int)>();
        using (file)
        {
            var sector = (worldStreamingSector)file.RootChunk;
            if (sector.NodeData.Buffer.Data is not worldNodeDataBuffer setups) return found;
            var embedded = file.EmbeddedFiles.Where(e => e.Content is worldFoliageCompiledResource).Select(e => (ulong)e.FileName.GetRedHash()).ToHashSet();
            foreach (var s in setups)
            {
                if (sector.Nodes[s.NodeIndex].Chunk is not worldFoliageNode node) continue;
                ulong hash = node.FoliageResource.DepotPath.GetRedHash();
                found.Add((node.FoliageResource.DepotPath.GetResolvedText() ?? hash.ToString(), hash, EntryOrNull(hash) is not null, embedded.Contains(hash),
                    new(s.Position.X, s.Position.Y, s.Position.Z), (int)(uint)node.PopulationSpanInfo.StancesCount));
            }
        }
        return found;
    }

    // Diagnostic: the plant nodes of a sector: mesh, where, how many plants, the node's streaming distance.
    public IReadOnlyList<(string Mesh, System.Numerics.Vector3 Position, int Plants, float Streaming)> FoliageNodes(ulong sectorHash)
    {
        var entry = EntryOrNull(sectorHash) ?? throw new FileNotFoundException();
        using var data = ExtractEntry(entry, default);
        using var reader = new CR2WReader(data);
        reader.ParsingError += SectorReader.TolerateSectorParsingError;
        if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file is null) throw new InvalidDataException();
        var found = new List<(string, System.Numerics.Vector3, int, float)>();
        using (file)
        {
            var sector = (worldStreamingSector)file.RootChunk;
            if (sector.NodeData.Buffer.Data is not worldNodeDataBuffer setups) return found;
            foreach (var s in setups)
                if (sector.Nodes[s.NodeIndex].Chunk is worldFoliageNode node)
                    found.Add((Path.GetFileNameWithoutExtension(node.Mesh.DepotPath.GetResolvedText() ?? "?"), new(s.Position.X, s.Position.Y, s.Position.Z),
                        (int)(uint)node.PopulationSpanInfo.StancesCount, node.StreamingDistance));
        }
        return found;
    }

    // Diagnostic: archive entries whose path the hash list does not know, largest first.
    public IReadOnlyList<(ulong Hash, string Name, string Archive, uint Size)> UnnamedEntries()
        => _entries.Values.Where(e => !e.FileName.Contains('\\')).OrderByDescending(e => e.Size)
            .Select(e => (e.Key, e.FileName, Path.GetFileName(e.Archive.ArchiveAbsolutePath), e.Size)).ToArray();

    // Diagnostic: the resources a file carries inside itself (path, hash, class).
    public IEnumerable<string> EmbeddedResources(ulong hash)
    {
        var entry = EntryOrNull(hash) ?? throw new FileNotFoundException();
        using var data = ExtractEntry(entry, default);
        using var reader = new CR2WReader(data);
        reader.ParsingError += SectorReader.TolerateSectorParsingError;
        if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file is null) throw new InvalidDataException();
        using (file)
        {
            yield return $"{entry.FileName}: root {file.RootChunk.GetType().Name}, {file.EmbeddedFiles.Count} embedded";
            foreach (var embedded in file.EmbeddedFiles)
                yield return $"  {embedded.FileName.GetResolvedText() ?? "?"} #{(ulong)embedded.FileName.GetRedHash():x16} {embedded.Content?.GetType().Name ?? "null"} indexed={EntryOrNull(embedded.FileName.GetRedHash()) is not null}"
                    + (embedded.Content is worldFoliageCompiledResource compiled
                        ? $" version {compiled.Version} populations {compiled.PopulationCount} buckets {compiled.BucketCount} buffer {(compiled.DataBuffer.Buffer.Data is FoliageBuffer b ? $"FoliageBuffer {b.Populations.Count}/{b.Buckets.Count}" : compiled.DataBuffer.Buffer.Data?.GetType().Name ?? $"raw {compiled.DataBuffer.Buffer.GetBytes().Length} bytes")}" : "");
        }
    }

    // Diagnostic: the root class of a resource, or why it could not be read.
    public string RootTypeOf(ulong hash)
    {
        if (EntryOrNull(hash) is not { } entry) return "(missing)";
        try
        {
            using var data = ExtractEntry(entry, default);
            using var reader = new CR2WReader(data);
            reader.ParsingError += SectorReader.TolerateSectorParsingError;
            return reader.ReadFile(out var file) == EFileReadErrorCodes.NoError && file is not null ? Dispose(file) : "(unreadable)";
        }
        catch (Exception ex) { return "(" + ex.GetType().Name + ")"; }
        static string Dispose(WolvenKit.RED4.Archive.CR2W.CR2WFile file) { using (file) return file.RootChunk.GetType().Name; }
    }

    // Diagnostic: where are foliage stances expressed relative to their node placement?
    public IEnumerable<string> InspectFoliage(ulong sectorHash, int limit = 3)
    {
        var entry = EntryOrNull(sectorHash) ?? throw new FileNotFoundException();
        using var data = ExtractEntry(entry, default);
        using var reader = new CR2WReader(data);
        reader.ParsingError += SectorReader.TolerateSectorParsingError;
        if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file is null) throw new InvalidDataException();
        using (file)
        {
            var sector = (worldStreamingSector)file.RootChunk;
            var setups = (worldNodeDataBuffer)sector.NodeData.Buffer.Data!;
            int shown = 0;
            for (int i = 0; i < setups.Count && shown < limit; i++)
            {
                var s = setups[i];
                if (sector.Nodes[s.NodeIndex].Chunk is not worldFoliageNode node) continue;
                shown++;
                var span = node.PopulationSpanInfo;
                yield return $"node {s.NodeIndex} setup ({s.Position.X:F1};{s.Position.Y:F1};{s.Position.Z:F1}) mesh {node.Mesh.DepotPath.GetResolvedText()} stances {span.StancesBegin}+{span.StancesCount} resource {node.FoliageResource.DepotPath.GetResolvedText()}";
                var resourceEntry = EntryOrNull(node.FoliageResource.DepotPath.GetRedHash());
                if (resourceEntry is null) { yield return "   resource missing"; continue; }
                using var rdata = ExtractEntry(resourceEntry, default);
                using var rreader = new CR2WReader(rdata);
                if (rreader.ReadFile(out var rfile) != EFileReadErrorCodes.NoError || rfile is null) { yield return "   unreadable"; continue; }
                using (rfile)
                {
                    var compiled = (worldFoliageCompiledResource)rfile.RootChunk;
                    if (compiled.DataBuffer.Buffer.Data is not FoliageBuffer buffer) { yield return $"   buffer type {compiled.DataBuffer.Buffer.Data?.GetType().Name ?? "null"}"; continue; }
                    yield return $"   populations {buffer.Populations.Count} buckets {buffer.Buckets.Count}";
                    foreach (var p in buffer.Populations.Skip((int)(uint)span.StancesBegin).Take(3))
                        yield return $"   stance ({(float)p.Position.X:F1};{(float)p.Position.Y:F1};{(float)p.Position.Z:F1}) scale {(float)p.Scale:F2} rot ({(float)p.Rotation.X:F2};{(float)p.Rotation.Y:F2};{(float)p.Rotation.Z:F2};{(float)p.Rotation.W:F2})";
                }
            }
        }
    }
}
