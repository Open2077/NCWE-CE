// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using NCWE.Core.Terrain;
using WolvenKit.RED4.Archive.CR2W;
using WolvenKit.RED4.Types;
using Vector3 = System.Numerics.Vector3;

namespace NCWE.Formats.Terrain;

// The geometry of a compiled terrain cell (terrain_*_cell_<x>_<y>.mesh): render chunks (a full
// cell has 9, about 43 m each; an edge cell one) of 16-bit normalized positions (PT_Short4N,
// 8 bytes, quantized by the blob header's scale and offset) and 16-bit indices, no normals (the
// material's GlobalNormal texture lights it) and no UVs (the material derives them from the
// position). The game simplified the triangles where the ground is flat. Positions are game space
// relative to the cell centre, heights absolute.
public sealed class TerrainCellMesh
{
    public sealed record ChunkGeometry(Vector3[] Positions, ushort[] Indices);

    public required ChunkGeometry[] Chunks { get; init; }

    public IEnumerable<(Vector3 A, Vector3 B, Vector3 C)> Triangles()
    {
        foreach (var chunk in Chunks)
            for (int t = 0; t + 2 < chunk.Indices.Length; t += 3)
                yield return (chunk.Positions[chunk.Indices[t]], chunk.Positions[chunk.Indices[t + 1]], chunk.Positions[chunk.Indices[t + 2]]);
    }

    public static TerrainCellMesh Read(CMesh mesh)
    {
        var blob = Blob(mesh);
        var header = blob.Header;
        var bytes = blob.RenderBuffer.Buffer.GetBytes();
        Vector3 scale = new(header.QuantizationScale.X, header.QuantizationScale.Y, header.QuantizationScale.Z);
        Vector3 origin = new(header.QuantizationOffset.X, header.QuantizationOffset.Y, header.QuantizationOffset.Z);
        var chunks = new ChunkGeometry[header.RenderChunkInfos.Count];
        for (int c = 0; c < chunks.Length; c++)
        {
            var chunk = header.RenderChunkInfos[c];
            Check(chunk);
            int count = chunk.NumVertices, offset = (int)(uint)chunk.ChunkVertices.ByteOffsets[0];
            var positions = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                var at = bytes.AsSpan(offset + i * 8);
                positions[i] = origin + scale * new Vector3(Unpack(BinaryPrimitives.ReadInt16LittleEndian(at)),
                    Unpack(BinaryPrimitives.ReadInt16LittleEndian(at[2..])), Unpack(BinaryPrimitives.ReadInt16LittleEndian(at[4..])));
            }
            int indexAt = (int)(uint)header.IndexBufferOffset + (int)(uint)chunk.ChunkIndices.TeOffset;
            var indices = new ushort[chunk.NumIndices];
            for (int i = 0; i < indices.Length; i++) indices[i] = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(indexAt + i * 2));
            chunks[c] = new(positions, indices);
        }
        return new() { Chunks = chunks };
    }

    // Replaces the mesh's geometry with new chunks (cell-local positions, at most 65,535 vertices
    // each): quantization, render buffer, chunk table, per-chunk bounds and material slots.
    public static void Write(CMesh mesh, IReadOnlyList<ChunkGeometry> chunks)
    {
        if (chunks.Count == 0) throw new ArgumentException("A terrain cell needs at least one chunk.");
        var blob = Blob(mesh);
        var header = blob.Header;
        var template = header.RenderChunkInfos[0];
        Check(template);
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (var chunk in chunks)
        {
            if (chunk.Positions.Length is 0 or > ushort.MaxValue) throw new ArgumentException("A terrain chunk holds 1 to 65,535 vertices.");
            foreach (var p in chunk.Positions) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        }
        Vector3 origin = (min + max) / 2, scale = Vector3.Max((max - min) / 2, new Vector3(1e-3f));
        // The cell's own grid quantization for X and Y (as the game: ±64 m around the centre).
        origin = origin with { X = 0, Y = 0 }; scale = scale with { X = Math.Max(64, MathF.Max(MathF.Abs(min.X), MathF.Abs(max.X))), Y = Math.Max(64, MathF.Max(MathF.Abs(min.Y), MathF.Abs(max.Y))) };
        int vertexBytes = chunks.Sum(c => c.Positions.Length) * 8;
        int indexOffset = (vertexBytes + 255) / 256 * 256;
        int indexBytes = chunks.Sum(c => c.Indices.Length) * 2;
        var bytes = new byte[indexOffset + indexBytes];
        int vertexAt = 0, indexAt = 0;
        var infos = new CArray<rendChunk>();
        var boxes = new List<Box>();
        foreach (var chunk in chunks)
        {
            Vector3 cmin = new(float.MaxValue), cmax = new(float.MinValue);
            for (int i = 0; i < chunk.Positions.Length; i++)
            {
                var p = chunk.Positions[i];
                cmin = Vector3.Min(cmin, p); cmax = Vector3.Max(cmax, p);
                var q = (p - origin) / scale;
                var at = bytes.AsSpan(vertexAt + i * 8);
                BinaryPrimitives.WriteInt16LittleEndian(at, Pack(q.X));
                BinaryPrimitives.WriteInt16LittleEndian(at[2..], Pack(q.Y));
                BinaryPrimitives.WriteInt16LittleEndian(at[4..], Pack(q.Z));
                BinaryPrimitives.WriteInt16LittleEndian(at[6..], short.MaxValue);   // 1, as the game's cells
            }
            for (int i = 0; i < chunk.Indices.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(indexOffset + indexAt + i * 2), chunk.Indices[i]);
            var info = (rendChunk)template.DeepCopy();
            info.ChunkVertices.ByteOffsets[0] = (uint)vertexAt;
            info.ChunkIndices.TeOffset = (uint)indexAt;
            info.NumVertices = (ushort)chunk.Positions.Length;
            info.NumIndices = (uint)chunk.Indices.Length;
            infos.Add(info);
            boxes.Add(Box(cmin, cmax));
            vertexAt += chunk.Positions.Length * 8; indexAt += chunk.Indices.Length * 2;
        }
        // The buffer keeps the game's flags (4063232 on the cells: written as they are).
        uint flags = blob.RenderBuffer.Buffer.Flags;
        blob.RenderBuffer = new DataBuffer(bytes);
        blob.RenderBuffer.Buffer.Flags = flags;
        header.RenderChunkInfos = infos;
        var topology = new CArray<rendTopologyData>();
        for (int i = 0; i < chunks.Count; i++) topology.Add(new rendTopologyData());
        header.Topology = topology;
        header.QuantizationScale = new Vector4 { X = scale.X, Y = scale.Y, Z = scale.Z, W = 0 };
        header.QuantizationOffset = new Vector4 { X = origin.X, Y = origin.Y, Z = origin.Z, W = 1 };
        header.VertexBufferSize = (uint)vertexBytes;
        header.IndexBufferOffset = (uint)indexOffset;
        header.IndexBufferSize = (uint)indexBytes;

        mesh.BoundingBox = Box(min, max);
        foreach (var parameter in mesh.Parameters.Select(p => p.Chunk).OfType<meshMeshParamTerrain>())
        {
            var list = new CArray<Box>();
            foreach (var box in boxes) list.Add(box);
            parameter.ChunkBoundingBoxes = list;
        }
        // Every chunk draws the cell's one material.
        foreach (var appearance in mesh.Appearances.Select(a => a.Chunk).OfType<meshMeshAppearance>())
        {
            var material = appearance.ChunkMaterials.Count > 0 ? appearance.ChunkMaterials[0] : (CName)"Terrain_material";
            var slots = new CArray<CName>();
            for (int i = 0; i < chunks.Count; i++) slots.Add(material);
            appearance.ChunkMaterials = slots;
        }
        // Projected area on each axis (the game uses it for texture streaming).
        Vector3 area = default;
        foreach (var chunk in chunks)
            for (int t = 0; t + 2 < chunk.Indices.Length; t += 3)
            {
                var a = chunk.Positions[chunk.Indices[t]];
                area += Vector3.Abs(Vector3.Cross(chunk.Positions[chunk.Indices[t + 1]] - a, chunk.Positions[chunk.Indices[t + 2]] - a)) / 2;
            }
        mesh.SurfaceAreaPerAxis = new WolvenKit.RED4.Types.Vector3 { X = area.X, Y = area.Y, Z = area.Z };
    }

    private static Box Box(Vector3 min, Vector3 max) => new()
    {
        Min = new Vector4 { X = min.X, Y = min.Y, Z = min.Z, W = 1 },
        Max = new Vector4 { X = max.X, Y = max.Y, Z = max.Z, W = 1 }
    };

    private static rendRenderMeshBlob Blob(CMesh mesh) =>
        mesh.RenderResourceBlob?.Chunk as rendRenderMeshBlob ?? throw new InvalidDataException("The terrain cell has no render blob.");

    private static void Check(rendChunk chunk)
    {
        var element = chunk.ChunkVertices.VertexLayout.Elements[0];
        if (element.Usage != Enums.GpuWrapApiVertexPackingePackingUsage.PS_Position || element.Type != Enums.GpuWrapApiVertexPackingePackingType.PT_Short4N)
            throw new NotSupportedException("The terrain cell's positions are not 16-bit normalized.");
        if (chunk.ChunkIndices.Pe != Enums.GpuWrapApieIndexBufferChunkType.IBCT_IndexUShort) throw new NotSupportedException("The terrain cell's indices are not 16-bit.");
    }

    private static float Unpack(short value) => Math.Max(value / 32767f, -1f);
    private static short Pack(float value) => (short)Math.Clamp(MathF.Round(value * 32767f), -32767, 32767);
}
