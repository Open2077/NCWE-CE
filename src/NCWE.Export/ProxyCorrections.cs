// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using NCWE.Core;
using NCWE.Formats;
using WolvenKit.RED4.Archive.Buffer;
using WolvenKit.RED4.Types;

namespace NCWE.Export;

// A distant proxy mesh (HLOD) keeps drawing until nbNodesUnderProxy of the nodes it stands for
// have streamed in. When the project deletes those nodes, the proxy's count must drop by as
// many, or the proxy stays visible after its collision is gone.
public sealed record DeletedNode(ulong LinkRef, int Count);
public sealed record ProxyNode(string SectorPath, ulong SectorHash, int ExpectedNodes, int NodeIndex, string NodeType, ulong NodeRef, int UnderProxy);
public sealed record ProxyCorrection(string SectorPath, ulong SectorHash, int ExpectedNodes, int NodeIndex, string NodeType, ulong NodeRef, int Diff);

public static class ProxyCorrections
{
    // Pure: what each proxy loses is the deleted nodes linked to it, clamped to its count.
    public static ImmutableArray<ProxyCorrection> Plan(IEnumerable<DeletedNode> deleted, IEnumerable<ProxyNode> proxies)
    {
        var lost = new Dictionary<ulong, int>();
        foreach (var node in deleted)
        {
            if (node.LinkRef == 0 || node.Count <= 0) continue;
            lost[node.LinkRef] = lost.GetValueOrDefault(node.LinkRef) + node.Count;
        }
        var result = ImmutableArray.CreateBuilder<ProxyCorrection>();
        foreach (var proxy in proxies)
        {
            if (proxy.UnderProxy <= 0 || !lost.TryGetValue(proxy.NodeRef, out int count)) continue;
            int take = Math.Min(count, proxy.UnderProxy);
            result.Add(new ProxyCorrection(proxy.SectorPath, proxy.SectorHash, proxy.ExpectedNodes, proxy.NodeIndex,
                proxy.NodeType, proxy.NodeRef, -take));
        }
        return result.ToImmutable();
    }

    // The proxy of a sector's nodes is in the same sector, or in a coarser one above it: the
    // level number is one of the path's parts, and each step up halves the grid coordinates.
    private static readonly Regex SectorName = new(@"^(?<prefix>.*exterior_)(?<x>-?\d+)_(?<y>-?\d+)_(?<z>-?\d+)_(?<l>\d+)(?<suffix>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private const int MaxLevelsUp = 3;

    public static async Task<ImmutableArray<ProxyCorrection>> ComputeAsync(ProjectData project, GameArchiveIndex index, CancellationToken cancellation)
    {
        var deleted = new List<DeletedNode>();
        var sectorsWithDeletions = project.Changes.Modified.Values
            .Where(e => e.Deleted && e.Source.NodeType != "worldCollisionNode")
            .GroupBy(e => e.Source.Sector.Hash)
            .ToList();
        foreach (var group in sectorsWithDeletions)
        {
            // Each deleted node links to a proxy through its node-data reference (UkHash1).
            var links = index.ReadStreamingSector(group.Key, sector =>
            {
                var setups = (worldNodeDataBuffer)sector.NodeData.Buffer.Data!;
                var linkByNode = new Dictionary<int, ulong>();
                for (int i = 0; i < setups.Count; i++) linkByNode.TryAdd(setups[i].NodeIndex, (ulong)setups[i].UkHash1);
                return group.GroupBy(e => e.Source.NodeIndex)
                    .Select(g => new DeletedNode(linkByNode.GetValueOrDefault(g.Key), g.Count()))
                    .ToList();
            }) ?? throw new InvalidDataException($"Cannot read the sector {group.First().Source.Sector.Path} to update its proxies.");
            deleted.AddRange(links);
        }
        var wanted = deleted.Where(d => d.LinkRef != 0).Select(d => d.LinkRef).ToHashSet();
        if (wanted.Count == 0) return [];

        var proxies = new List<ProxyNode>();
        foreach (var group in sectorsWithDeletions)
        {
            var sectorPath = group.First().Source.Sector.Path;
            foreach (var candidate in CandidateSectors(sectorPath, index))
            {
                cancellation.ThrowIfCancellationRequested();
                var found = index.ReadStreamingSector(candidate.Hash, sector => ProxiesIn(sector, wanted)) ?? [];
                if (found.Count == 0) continue;
                int expected = (await index.LoadSectorAsync(candidate.Hash, cancellation)).NodeCount;
                proxies.AddRange(found.Select(p => p with { SectorPath = candidate.Path, SectorHash = candidate.Hash, ExpectedNodes = expected }));
            }
        }
        // Several sectors can name the same proxy: count it once.
        return Plan(deleted, proxies.DistinctBy(p => (p.SectorHash, p.NodeIndex))).ToImmutableArray();
    }

    // The sector itself, then the coarser sectors above it, nearest first.
    private static IEnumerable<(string Path, ulong Hash)> CandidateSectors(string sectorPath, GameArchiveIndex index)
    {
        var match = SectorName.Match(sectorPath);
        if (!match.Success) yield break;
        int x = int.Parse(match.Groups["x"].Value), y = int.Parse(match.Groups["y"].Value), level = int.Parse(match.Groups["l"].Value);
        for (int up = 0; up <= MaxLevelsUp; up++)
        {
            int scale = 1 << up;
            int cx = (int)Math.Floor(x / (double)scale), cy = (int)Math.Floor(y / (double)scale);
            string path = $"{match.Groups["prefix"].Value}{cx}_{cy}_{match.Groups["z"].Value}_{level + up}{match.Groups["suffix"].Value}";
            ulong hash = WolvenKit.Common.FNV1A.FNV1A64HashAlgorithm.HashString(path);
            if (index.Find(hash) is not null) yield return (path, hash);
        }
    }

    // Proxies in a sector whose node-data reference is one of the deleted nodes' links.
    private static List<ProxyNode> ProxiesIn(worldStreamingSector sector, HashSet<ulong> wanted)
    {
        var setups = (worldNodeDataBuffer)sector.NodeData.Buffer.Data!;
        var found = new List<ProxyNode>();
        for (int i = 0; i < setups.Count; i++)
        {
            ulong reference = (ulong)setups[i].QuestPrefabRefHash;
            if (reference == 0 || !wanted.Contains(reference)) continue;
            var chunk = sector.Nodes[setups[i].NodeIndex].Chunk;
            if (chunk is null || !chunk.GetType().Name.EndsWith("ProxyMeshNode", StringComparison.Ordinal)) continue;
            var count = chunk.GetType().GetProperty("NbNodesUnderProxy")?.GetValue(chunk);
            if (count is null) continue;
            // NbNodesUnderProxy is a RED CUInt32, which Convert.ToInt32 cannot read.
            int nodes = count is CUInt32 u ? (int)(uint)u : Convert.ToInt32(count);
            found.Add(new ProxyNode(string.Empty, 0, 0, setups[i].NodeIndex, chunk.GetType().Name, reference, nodes));
        }
        return found;
    }
}
