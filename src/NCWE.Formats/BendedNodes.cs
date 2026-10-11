// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using WolvenKit.RED4.Archive.Buffer;
using WolvenKit.RED4.Archive.IO;
using WolvenKit.RED4.Types;
using Vector3 = System.Numerics.Vector3;
using Quaternion = System.Numerics.Quaternion;

namespace NCWE.Formats;

public sealed record BendedNodeInfo(int NodeIndex, Vector3 Position, Quaternion Rotation, Vector3 Scale, string MeshPath, ulong MeshHash,
    Matrix4x4[] Deformation, Vector3 BoxMin, Vector3 BoxMax, bool IsRoad);

public sealed partial class GameArchiveIndex
{
    public List<BendedNodeInfo> ReadBended(ulong sectorHash)
    {
        var result = new List<BendedNodeInfo>();
        var entry = EntryOrNull(sectorHash) ?? throw new FileNotFoundException();
        using var data = ExtractEntry(entry, default);
        using var reader = new CR2WReader(data);
        reader.ParsingError += SectorReader.TolerateSectorParsingError;
        if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file is null) return result;
        using (file)
        {
            var sector = (worldStreamingSector)file.RootChunk;
            var setups = (worldNodeDataBuffer)sector.NodeData.Buffer.Data!;
            foreach (var s in setups)
            {
                if (sector.Nodes[s.NodeIndex].Chunk is not worldBendedMeshNode node) continue;
                var matrices = node.DeformationData.Select(m => new Matrix4x4(m.X.X, m.X.Y, m.X.Z, m.X.W, m.Y.X, m.Y.Y, m.Y.Z, m.Y.W, m.Z.X, m.Z.Y, m.Z.Z, m.Z.W, m.W.X, m.W.Y, m.W.Z, m.W.W)).ToArray();
                result.Add(new(s.NodeIndex, new(s.Position.X, s.Position.Y, s.Position.Z), new(s.Orientation.I, s.Orientation.J, s.Orientation.K, s.Orientation.R),
                    new(s.Scale.X, s.Scale.Y, s.Scale.Z), node.Mesh.DepotPath.GetResolvedText() ?? "", node.Mesh.DepotPath.GetRedHash(), matrices,
                    new(node.DeformedBox.Min.X, node.DeformedBox.Min.Y, node.DeformedBox.Min.Z), new(node.DeformedBox.Max.X, node.DeformedBox.Max.Y, node.DeformedBox.Max.Z), node.IsBendedRoad));
            }
        }
        return result;
    }
}
