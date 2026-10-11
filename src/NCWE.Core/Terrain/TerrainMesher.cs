// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;

namespace NCWE.Core.Terrain;

// The ground of an edited cell as triangles: a 1 m grid (129 × 129 vertices) over the cell, its
// heights the game's plus the edits, holes and the parts the game's mesh does not cover left out,
// in 2 × 2 chunks of 64 m (at most 4,225 vertices each, 16-bit indices). Triangles wind like the
// game's (clockwise seen from above).
public static class TerrainMesher
{
    public const int Grid = TerrainBase.Quads + 1;   // 129 vertices per side
    public const int ChunkQuads = 64;

    public sealed record Chunk(Vector3[] Positions, ushort[] Indices);

    // Heights of the 1 m grid (cell-local, row-major from the south-west corner).
    public static float[] Heights(TerrainBase @base, TerrainCellEdit? edit)
    {
        var heights = new float[Grid * Grid];
        for (int j = 0; j < Grid; j++)
            for (int i = 0; i < Grid; i++)
            {
                int x = i * TerrainBase.QuadSamples, y = j * TerrainBase.QuadSamples;
                float h = @base.VisualAt(x, y);
                if (float.IsNaN(h)) h = Nearest(@base, x, y);
                if (edit?.Height is { } delta) h += delta[TerrainBase.Index(x, y)];
                heights[j * Grid + i] = h;
            }
        return heights;
    }

    // A height for a lattice sample the game's mesh does not cover (only corners of the quads
    // it covers can land here): the nearest covered sample's.
    private static float Nearest(TerrainBase @base, int x, int y)
    {
        for (int r = 1; r < 16; r++)
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int sx = x + dx, sy = y + dy;
                    if (sx < 0 || sy < 0 || sx >= TerrainBase.Samples || sy >= TerrainBase.Samples) continue;
                    float v = @base.VisualAt(sx, sy);
                    if (!float.IsNaN(v)) return v;
                }
        return 0;
    }

    public static bool Drawn(TerrainBase @base, TerrainCellEdit? edit, int qx, int qy)
    {
        int at = qy * TerrainBase.Quads + qx;
        return @base.Covered[at] && edit?.Holes?[at] != true;
    }

    // The cell's chunks in cell-local game space (x east, y north, z absolute height).
    // keepVertices: every vertex of a chunk the game's mesh covers stays, whatever is cut out (the
    // editor updates the drawn mesh in place, which needs the same vertices); otherwise only the
    // vertices of drawn quads.
    public static IReadOnlyList<Chunk> Build(TerrainBase @base, TerrainCellEdit? edit, bool keepVertices = false)
    {
        var heights = Heights(@base, edit);
        var chunks = new List<Chunk>();
        for (int cy = 0; cy < TerrainBase.Quads; cy += ChunkQuads)
            for (int cx = 0; cx < TerrainBase.Quads; cx += ChunkQuads)
            {
                var remap = new int[(ChunkQuads + 1) * (ChunkQuads + 1)];
                Array.Fill(remap, -1);
                var positions = new List<Vector3>(); var indices = new List<ushort>();
                if (keepVertices)
                {
                    bool covered = false;
                    for (int qy = cy; qy < cy + ChunkQuads && !covered; qy++)
                        for (int qx = cx; qx < cx + ChunkQuads && !covered; qx++) covered = @base.Covered[qy * TerrainBase.Quads + qx];
                    if (!covered) continue;
                    for (int j = cy; j <= cy + ChunkQuads; j++)
                        for (int i = cx; i <= cx + ChunkQuads; i++) Vertex(i, j);
                }
                ushort Vertex(int i, int j)
                {
                    int local = (j - cy) * (ChunkQuads + 1) + (i - cx);
                    if (remap[local] < 0)
                    {
                        remap[local] = positions.Count;
                        positions.Add(new(i - TerrainCell.Size / 2, j - TerrainCell.Size / 2, heights[j * Grid + i]));
                    }
                    return (ushort)remap[local];
                }
                for (int qy = cy; qy < cy + ChunkQuads; qy++)
                    for (int qx = cx; qx < cx + ChunkQuads; qx++)
                    {
                        if (!Drawn(@base, edit, qx, qy)) continue;
                        ushort a = Vertex(qx, qy), b = Vertex(qx + 1, qy), c = Vertex(qx, qy + 1), d = Vertex(qx + 1, qy + 1);
                        indices.AddRange([a, c, b, b, c, d]);
                    }
                if (indices.Count > 0 || (keepVertices && positions.Count > 0))
                {
                    // A chunk cut out entirely still draws one degenerate triangle (its buffers stay valid).
                    if (indices.Count == 0) indices.AddRange([0, 0, 0]);
                    chunks.Add(new(positions.ToArray(), indices.ToArray()));
                }
            }
        return chunks;
    }

    // Game-space normal of the grid at a vertex (central differences).
    public static Vector3 Normal(float[] heights, int i, int j)
    {
        float l = heights[j * Grid + Math.Max(0, i - 1)], r = heights[j * Grid + Math.Min(Grid - 1, i + 1)];
        float d = heights[Math.Max(0, j - 1) * Grid + i], u = heights[Math.Min(Grid - 1, j + 1) * Grid + i];
        float dx = (r - l) / ((Math.Min(Grid - 1, i + 1) - Math.Max(0, i - 1)) * 1f), dy = (u - d) / ((Math.Min(Grid - 1, j + 1) - Math.Max(0, j - 1)) * 1f);
        return Vector3.Normalize(new(-dx, -dy, 1));
    }

    // The editor's mesh for the cell (view space, Y up, relative to the node at the cell centre).
    public static MeshAsset Preview(TerrainBase @base, TerrainCellEdit? edit, string resourcePath, int generation, bool keepVertices = false)
    {
        var heights = Heights(@base, edit);
        var parts = new List<MeshPart>();
        foreach (var chunk in Build(@base, edit, keepVertices))
        {
            var vertices = new MeshVertex[chunk.Positions.Length];
            for (int v = 0; v < vertices.Length; v++)
            {
                var p = chunk.Positions[v];
                int i = (int)MathF.Round(p.X + TerrainCell.Size / 2), j = (int)MathF.Round(p.Y + TerrainCell.Size / 2);
                var n = Normal(heights, i, j);
                vertices[v] = new(Coordinates.GameToView(p), Coordinates.GameToView(n));
            }
            var part = new MeshPart($"Terrain chunk {parts.Count}", 1, vertices, chunk.Indices.Select(i => (uint)i).ToArray());
            parts.Add(part);
        }
        if (parts.Count == 0)
        {
            // Every quad cut out: an empty-looking sliver keeps the asset valid.
            var v = Coordinates.GameToView(new Vector3(0, 0, -10000));
            parts.Add(new("Terrain (all cut)", 1, [new(v, Vector3.UnitY), new(v, Vector3.UnitY), new(v, Vector3.UnitY)], [0, 1, 2]));
        }
        var bounds = Bounds3.From(parts.SelectMany(p => p.Vertices).Select(v => v.Position));
        var brown = new Vector4(.34f, .27f, .19f, 1);
        return new(resourcePath, $"terrain-edit:{@base.Cell}:{generation}", parts.ToArray(), bounds, "NCWE terrain edit", 0)
        { AppearanceColors = new Dictionary<string, Vector4[]> { ["default"] = Enumerable.Repeat(brown, parts.Count).ToArray() } };
    }
}
