// SPDX-License-Identifier: GPL-3.0-only
using WolvenKit.Modkit.RED4.Tools;
using WolvenKit.RED4.Archive.Buffer;
using WolvenKit.RED4.Archive.IO;
using WolvenKit.RED4.Types;

namespace NCWE.Formats;

public sealed partial class GameArchiveIndex
{
    // Diagnostic: the bone rig of a mesh cooked for bending (br_*): bone names, bind poses,
    // and how many vertices each bone influences.
    public IEnumerable<string> InspectMeshRig(ulong meshHash)
    {
        var entry = EntryOrNull(meshHash) ?? throw new FileNotFoundException();
        using var data = ExtractEntry(entry, default);
        using var reader = new CR2WReader(data);
        if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file is null) throw new InvalidDataException();
        using (file)
        {
            var mesh = (CMesh)file.RootChunk;
            yield return $"bbox ({mesh.BoundingBox.Min.X:F2};{mesh.BoundingBox.Min.Y:F2};{mesh.BoundingBox.Min.Z:F2})-({mesh.BoundingBox.Max.X:F2};{mesh.BoundingBox.Max.Y:F2};{mesh.BoundingBox.Max.Z:F2}) bones {mesh.BoneNames.Count} rig {mesh.BoneRigMatrices.Count} vertexBlocks {mesh.BoneVertexEpsilons.Count}";
            for (int i = 0; i < mesh.BoneNames.Count; i++)
            {
                string rig = i < mesh.BoneRigMatrices.Count ? Matrix(mesh.BoneRigMatrices[i]) : "-";
                yield return $"  bone {i} {mesh.BoneNames[i]} rig {rig}";
            }
            foreach (var appearance in mesh.Appearances.Take(4))
                if (appearance.Chunk is { } a) yield return $"  appearance {a.Name}: {string.Join(", ", a.ChunkMaterials.Select(m => m.ToString()))}";
            if (mesh.RenderResourceBlob.Chunk is rendRenderMeshBlob blob)
            {
                var info = MeshTools.GetMeshesinfo(blob, mesh);
                yield return $"  submeshes {info.vertCounts.Length} verts [{string.Join(",", info.vertCounts)}] weights [{string.Join(",", info.weightCounts)}] lods [{string.Join(",", info.LODLvl)}]";
            }
        }
        static string Matrix(CMatrix m) => $"X({m.X.X:F2};{m.X.Y:F2};{m.X.Z:F2}) Y({m.Y.X:F2};{m.Y.Y:F2};{m.Y.Z:F2}) Z({m.Z.X:F2};{m.Z.Y:F2};{m.Z.Z:F2}) W({m.W.X:F2};{m.W.Y:F2};{m.W.Z:F2};{m.W.W:F2})";
    }

    // Diagnostic: every cable (worldCableMeshNode) of a sector, measured against the convention
    // CableBending writes: one row per cable.
    public sealed record CableFacts(string Mesh, bool MeshIndexed, int Frames, float CableLength, float CableRadius, float Polyline,
        float TangentError, float SideTilt, float SideDrift, float Handedness, float RowScale, bool IdentitySetup, string Flags, float BoxSlack, float Streaming,
        float Secondary, float Diagonal, float CentreOffset, int Level);

    public IReadOnlyList<CableFacts> CableSurvey(ulong sectorHash)
    {
        var entry = EntryOrNull(sectorHash) ?? throw new FileNotFoundException();
        using var data = ExtractEntry(entry, default);
        using var reader = new CR2WReader(data);
        reader.ParsingError += SectorReader.TolerateSectorParsingError;
        if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file is null) throw new InvalidDataException();
        var found = new List<CableFacts>();
        using (file)
        {
            var sector = (worldStreamingSector)file.RootChunk;
            if (sector.NodeData.Buffer.Data is not worldNodeDataBuffer setups) return found;
            foreach (var s in setups)
            {
                if (sector.Nodes[s.NodeIndex].Chunk is not worldCableMeshNode node || node.DeformationData.Count < 2) continue;
                var rows = node.DeformationData.Select(m => (X: V(m.X), Y: V(m.Y), Z: V(m.Z), W: V(m.W))).ToArray();
                float polyline = 0, tangent = 0, tilt = 0, drift = 0, hand = 0, scale = 0;
                for (int i = 0; i < rows.Length; i++)
                {
                    var ahead = rows[Math.Min(i + 1, rows.Length - 1)].W - rows[Math.Max(i - 1, 0)].W;
                    if (i > 0) polyline += System.Numerics.Vector3.Distance(rows[i].W, rows[i - 1].W);
                    if (ahead.LengthSquared() > 1e-8f) tangent = MathF.Max(tangent, (System.Numerics.Vector3.Normalize(ahead) + System.Numerics.Vector3.Normalize(rows[i].X)).Length());
                    tilt = MathF.Max(tilt, MathF.Abs(System.Numerics.Vector3.Normalize(rows[i].Z).Z));
                    drift = MathF.Max(drift, (rows[i].Z - rows[0].Z).Length());
                    hand += System.Numerics.Vector3.Dot(System.Numerics.Vector3.Cross(rows[i].X, rows[i].Y), rows[i].Z) / rows.Length;
                    scale = MathF.Max(scale, MathF.Max(MathF.Abs(rows[i].X.Length() - 1), MathF.Max(MathF.Abs(rows[i].Y.Length() - 1), MathF.Abs(rows[i].Z.Length() - 1))));
                }
                var min = rows.Aggregate(new System.Numerics.Vector3(float.MaxValue), (a, r) => System.Numerics.Vector3.Min(a, r.W));
                var max = rows.Aggregate(new System.Numerics.Vector3(float.MinValue), (a, r) => System.Numerics.Vector3.Max(a, r.W));
                float slack = MathF.Max((min - V(node.DeformedBox.Min)).Length(), (V(node.DeformedBox.Max) - max).Length());
                bool identity = MathF.Abs(s.Orientation.R) > .9999f && MathF.Abs(s.Scale.X - 1) < 1e-4f && MathF.Abs(s.Scale.Y - 1) < 1e-4f && MathF.Abs(s.Scale.Z - 1) < 1e-4f;
                ulong hash = node.Mesh.DepotPath.GetRedHash();
                found.Add(new(node.Mesh.DepotPath.GetResolvedText() ?? hash.ToString(), EntryOrNull(hash) is not null, rows.Length, node.CableLength, node.CableRadius, polyline,
                    tangent, tilt, drift, hand, scale, identity,
                    $"road={node.IsBendedRoad} nav={node.NavigationSetting.NavmeshImpact} shadows={node.CastShadows}/{node.CastLocalShadows} rain={node.RemoveFromRainMap} version={node.Version} zw={(float)node.DeformationData[0].Z.W:0.##} ww={(float)node.DeformationData[0].W.W:0.##}",
                    slack, s.MaxStreamingDistance, s.UkFloat1, (max - min).Length(), ((min + max) / 2).Length(), (int)(byte)sector.Level));
            }
        }
        return found;
        static System.Numerics.Vector3 V(WolvenKit.RED4.Types.Vector4 v) => new(v.X, v.Y, v.Z);
    }

    // Diagnostic: how does a bended (deformed) mesh node relate to its mesh?
    public IEnumerable<string> InspectBended(ulong sectorHash, int limit = 2)
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
                if (sector.Nodes[s.NodeIndex].Chunk is not worldBendedMeshNode node) continue;
                shown++;
                yield return $"node {s.NodeIndex} setup pos ({s.Position.X:F1};{s.Position.Y:F1};{s.Position.Z:F1}) scale ({s.Scale.X:F2};{s.Scale.Y:F2};{s.Scale.Z:F2}) road={node.IsBendedRoad} mesh {node.Mesh.DepotPath.GetResolvedText()}";
                yield return $"   appearance {node.MeshAppearance} shadows {node.CastShadows} local {node.CastLocalShadows} rainmap {node.RemoveFromRainMap} version {node.Version} visible {node.IsVisibleInGame} tag {node.Tag} nav {node.NavigationSetting.NavmeshImpact} streaming {s.MaxStreamingDistance:F0}/{s.UkFloat1:F0} uk10 {s.Uk10} uk11 {s.Uk11} bounds ({s.Bounds.Min.X:F1};{s.Bounds.Min.Y:F1};{s.Bounds.Min.Z:F1})-({s.Bounds.Max.X:F1};{s.Bounds.Max.Y:F1};{s.Bounds.Max.Z:F1})";
                yield return $"   deformation matrices {node.DeformationData.Count}; box min ({node.DeformedBox.Min.X:F1};{node.DeformedBox.Min.Y:F1};{node.DeformedBox.Min.Z:F1}) max ({node.DeformedBox.Max.X:F1};{node.DeformedBox.Max.Y:F1};{node.DeformedBox.Max.Z:F1})";
                foreach (var m in node.DeformationData.Take(3))
                    yield return $"   X({m.X.X:F2};{m.X.Y:F2};{m.X.Z:F2};{m.X.W:F2}) Y({m.Y.X:F2};{m.Y.Y:F2};{m.Y.Z:F2};{m.Y.W:F2}) Z({m.Z.X:F2};{m.Z.Y:F2};{m.Z.Z:F2};{m.Z.W:F2}) W({m.W.X:F2};{m.W.Y:F2};{m.W.Z:F2};{m.W.W:F2})";
                var meshEntry = EntryOrNull(node.Mesh.DepotPath.GetRedHash());
                if (meshEntry is null) { yield return "   mesh missing"; continue; }
                using var md = ExtractEntry(meshEntry, default);
                using var mr = new CR2WReader(md);
                if (mr.ReadFile(out var mf) != EFileReadErrorCodes.NoError || mf is null) continue;
                using (mf)
                {
                    var mesh = (CMesh)mf.RootChunk;
                    if (mesh.RenderResourceBlob.Chunk is not rendRenderMeshBlob blob) { yield return "   no blob"; continue; }
                    var info = MeshTools.GetMeshesinfo(blob, mesh);
                    yield return $"   mesh bones {mesh.BoneNames.Count} ({string.Join(",", mesh.BoneNames.Take(6).Select(b => b.ToString()))}) weights per submesh [{string.Join(",", info.weightCounts)}] bbox ({mesh.BoundingBox.Min.X:F1};{mesh.BoundingBox.Min.Y:F1};{mesh.BoundingBox.Min.Z:F1})-({mesh.BoundingBox.Max.X:F1};{mesh.BoundingBox.Max.Y:F1};{mesh.BoundingBox.Max.Z:F1})";
                    if (mesh.BoneRigMatrices.Count > 0) { var r = mesh.BoneRigMatrices[0]; yield return $"   rig0 W({r.W.X:F2};{r.W.Y:F2};{r.W.Z:F2})"; }
                }
            }
        }
    }
}
