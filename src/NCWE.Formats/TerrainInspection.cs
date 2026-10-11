// SPDX-License-Identifier: GPL-3.0-only
using WolvenKit.RED4.Archive.Buffer;
using WolvenKit.RED4.Archive.IO;
using WolvenKit.RED4.Types;

namespace NCWE.Formats;

public sealed partial class GameArchiveIndex
{
    // Diagnostic: terrain nodes reference or embed their mesh?
    public IEnumerable<string> InspectTerrain(ulong sectorHash)
    {
        var entry = EntryOrNull(sectorHash) ?? throw new FileNotFoundException();
        using var data = ExtractEntry(entry, default);
        using var reader = new CR2WReader(data);
        reader.ParsingError += SectorReader.TolerateSectorParsingError;
        if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file is null) yield break;
        using (file)
        {
            var sector = (worldStreamingSector)file.RootChunk;
            var setups = (worldNodeDataBuffer)sector.NodeData.Buffer.Data!;
            foreach (var s in setups.Take(100000))
            {
                if (sector.Nodes[s.NodeIndex].Chunk is worldCableMeshNode cable)
                {
                    var path = cable.Mesh.DepotPath.GetResolvedText() ?? cable.Mesh.DepotPath.GetRedHash().ToString();
                    yield return $"cable {s.NodeIndex} mesh {path} indexed={EntryOrNull(cable.Mesh.DepotPath.GetRedHash()) is not null} frames={cable.DeformationData.Count} len={cable.CableLength}";
                    continue;
                }
                if (sector.Nodes[s.NodeIndex].Chunk is worldTerrainCollisionNode collision)
                {
                    var bytes = collision.HeightfieldGeometry.Buffer.GetBytes();
                    yield return $"collision {s.NodeIndex} pos ({s.Position.X:F1};{s.Position.Y:F1};{s.Position.Z:F1}) actor ({(float)collision.ActorTransform.Position.X:F1};{(float)collision.ActorTransform.Position.Y:F1};{(float)collision.ActorTransform.Position.Z:F1}) rot ({collision.ActorTransform.Orientation.I:F3};{collision.ActorTransform.Orientation.J:F3};{collision.ActorTransform.Orientation.K:F3};{collision.ActorTransform.Orientation.R:F3}) extents ({collision.Extents.X:F1};{collision.Extents.Y:F1};{collision.Extents.Z:F1};{collision.Extents.W:F1}) rows×{collision.RowScale} cols×{collision.ColumnScale} height×{collision.HeightScale} stream {collision.StreamingDistance} materials [{string.Join(", ", collision.Materials.Select(m => m.ToString()))}] indices [{string.Join(", ", collision.MaterialIndices.Take(16))}] buffer {bytes.Length} bytes: {Convert.ToHexString(bytes.AsSpan(0, Math.Min(96, bytes.Length)))}";
                    continue;
                }
                if (sector.Nodes[s.NodeIndex].Chunk is worldTerrainProxyMeshNode proxy)
                {
                    var proxyPath = proxy.Mesh.DepotPath.GetResolvedText() ?? proxy.Mesh.DepotPath.GetRedHash().ToString();
                    yield return $"proxy {s.NodeIndex} pos ({s.Position.X:F1};{s.Position.Y:F1};{s.Position.Z:F1}) rot ({s.Orientation.I:F3};{s.Orientation.J:F3};{s.Orientation.K:F3};{s.Orientation.R:F3}) scale ({s.Scale.X:F2};{s.Scale.Y:F2};{s.Scale.Z:F2}) stream {s.MaxStreamingDistance} nearHide {proxy.NearAutoHideDistance} mesh {proxyPath}";
                    continue;
                }
                if (sector.Nodes[s.NodeIndex].Chunk is not worldTerrainMeshNode node) continue;
                ulong refHash = (ulong)node.MeshRef.DepotPath.GetRedHash();
                var refPath = refHash == 0 ? "-" : node.MeshRef.DepotPath.GetResolvedText() ?? refHash.ToString();
                yield return $"node {s.NodeIndex} pos ({s.Position.X:F0};{s.Position.Y:F0};{s.Position.Z:F0}) rot ({s.Orientation.I:F3};{s.Orientation.J:F3};{s.Orientation.K:F3};{s.Orientation.R:F3}) scale ({s.Scale.X:F2};{s.Scale.Y:F2};{s.Scale.Z:F2}) stream {s.MaxStreamingDistance} bounds ({s.Bounds.Min.X:F0};{s.Bounds.Min.Y:F0};{s.Bounds.Min.Z:F0})-({s.Bounds.Max.X:F0};{s.Bounds.Max.Y:F0};{s.Bounds.Max.Z:F0}) ref {refPath} indexed={EntryOrNull(refHash) is not null} embedded={(node.Mesh?.Chunk is CMesh m ? $"yes blob={m.RenderResourceBlob?.Chunk?.GetType().Name}" : "no")}";
            }
        }
    }

    // Diagnostic: every terrain-related resource of a world folder, counted by extension.
    public IEnumerable<string> TerrainFiles(string folder)
    {
        var groups = AllPaths().Where(p => p.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            .GroupBy(p => Path.GetExtension(p).ToLowerInvariant());
        foreach (var g in groups) yield return $"{g.Key}: {g.Count()} (first {g.Min()}, last {g.Max()})";
    }

    // Diagnostic: a terrain cell's material values and in-place resources (textures, mask).
    public IEnumerable<string> InspectTerrainCell(ulong meshHash)
    {
        var entry = EntryOrNull(meshHash) ?? throw new FileNotFoundException();
        using var data = ExtractEntry(entry, default);
        using var reader = new CR2WReader(data);
        if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file is null) throw new InvalidDataException();
        using (file)
        {
            var mesh = (CMesh)file.RootChunk;
            var lines = new List<string>();
            if (mesh.RenderResourceBlob?.Chunk is rendRenderMeshBlob meshBlob)
            {
                var h = meshBlob.Header;
                lines.Add($"blob v{h.Version} processing {h.DataProcessing} lods [{string.Join(",", h.RenderLODs)}] chunks {h.RenderChunkInfos.Count} renderChunks bytes {h.RenderChunks.Count} topology {h.Topology.Count} topologyData {h.TopologyData.Count} topologyMeta {h.TopologyMetadata.Count} customData {h.CustomData.Count} vb {h.VertexBufferSize} ib {h.IndexBufferSize} @ {h.IndexBufferOffset} buffer {meshBlob.RenderBuffer.Buffer.GetBytes().Length} lodLevels [{string.Join(",", mesh.LodLevelInfo)}] q scale ({h.QuantizationScale.X};{h.QuantizationScale.Y};{h.QuantizationScale.Z};{h.QuantizationScale.W}) offset ({h.QuantizationOffset.X};{h.QuantizationOffset.Y};{h.QuantizationOffset.Z};{h.QuantizationOffset.W})");
                for (int i = 0; i < h.Topology.Count; i++) lines.Add($"  topology {i}: data {h.Topology[i].Data.Count} meta {h.Topology[i].Metadata.Count} strides {h.Topology[i].DataStride}/{h.Topology[i].MetadataStride}");
                foreach (var c in h.RenderChunkInfos)
                    lines.Add($"  chunk vertices {c.NumVertices} @ {c.ChunkVertices.ByteOffsets[0]} indices {c.NumIndices} @ {c.ChunkIndices.TeOffset} factory {c.VertexFactory} lodMask {c.LodMask} renderMask {c.RenderMask} base {c.BaseRenderMask} merged {c.MergedRenderMask} materialIds [{string.Join(",", c.MaterialId)}]");
                foreach (var b in mesh.Parameters.Select(p => p.Chunk).OfType<meshMeshParamTerrain>().SelectMany(p => p.ChunkBoundingBoxes))
                    lines.Add($"  box ({b.Min.X:F1};{b.Min.Y:F1};{b.Min.Z:F1})-({b.Max.X:F1};{b.Max.Y:F1};{b.Max.Z:F1})");
                lines.Add($"mesh flags: objectType {mesh.ObjectType} shadows {mesh.CastGlobalShadowsCachedInCook}/{mesh.CastLocalShadowsCachedInCook} rt {mesh.CastsRayTracedShadowsFromOriginalGeometry} parameters [{string.Join(",", mesh.Parameters.Select(p => p.Chunk?.GetType().Name))}] area ({mesh.SurfaceAreaPerAxis.X:F0};{mesh.SurfaceAreaPerAxis.Y:F0};{mesh.SurfaceAreaPerAxis.Z:F0})");
            }
            foreach (var instance in mesh.PreloadLocalMaterialInstances.Select(h => h.Chunk).OfType<CMaterialInstance>())
            {
                lines.Add($"material {instance.BaseMaterial.DepotPath.GetResolvedText()}");
                foreach (var pair in instance.Values)
                {
                    string value = pair.Value switch
                    {
                        IRedRef r => r.DepotPath.GetResolvedText() ?? $"0x{(ulong)r.DepotPath.GetRedHash():X16}",
                        Vector4 v => $"({v.X}; {v.Y}; {v.Z}; {v.W})",
                        _ => pair.Value?.ToString() ?? "null",
                    };
                    lines.Add($"  {pair.Key} = {value} [{pair.Value?.GetType().Name}]");
                }
            }
            foreach (var embedded in file.EmbeddedFiles)
            {
                lines.Add($"embedded {embedded.FileName.GetResolvedText()} {embedded.Content?.GetType().Name}");
                switch (embedded.Content)
                {
                    case CBitmapTexture xbm when xbm.RenderTextureResource.RenderResourceBlobPC?.Chunk is rendRenderTextureBlobPC blob:
                        lines.Add($"  {xbm.Width}x{xbm.Height} {xbm.Setup.RawFormat} compression {xbm.Setup.Compression} group {xbm.Setup.Group} mips {blob.Header.TextureInfo.MipCount} slices {blob.Header.TextureInfo.SliceCount} bytes {blob.TextureData.Buffer.GetBytes().Length} linear={xbm.Setup.IsGamma == false}");
                        break;
                    case Multilayer_Mask mask when mask.RenderResourceBlob.RenderResourceBlobPC?.Chunk is rendRenderMultilayerMaskBlobPC mblob:
                        lines.Add($"  layers {mblob.Header.NumLayers} atlas {mblob.Header.AtlasWidth}x{mblob.Header.AtlasHeight} mask {mblob.Header.MaskWidth}x{mblob.Header.MaskHeight} lowres {mblob.Header.MaskWidthLow}x{mblob.Header.MaskHeightLow} tile {mblob.Header.MaskTileSize} flags {mblob.Header.Flags} atlas bytes {mblob.AtlasData.Buffer.GetBytes().Length} tiles bytes {mblob.TilesData.Buffer.GetBytes().Length}");
                        break;
                }
            }
            foreach (var line in lines) yield return line;
        }
    }
}
