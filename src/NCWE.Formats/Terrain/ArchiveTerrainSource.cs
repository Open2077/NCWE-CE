// SPDX-License-Identifier: GPL-3.0-only
using NCWE.Core.Terrain;
using WolvenKit.RED4.Types;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace NCWE.Formats.Terrain;

// The game's terrain cells for the editor: the cell mesh rasterized onto the 0.5 m lattice, the
// quads it covers, its material's texture span and its multilayer mask.
public sealed class ArchiveTerrainSource(GameArchiveIndex index) : ITerrainSource
{
    public const string SetupPath = @"base\worlds\03_night_city\multilayer_terrain.mlsetup";
    private string[]? _layerNames;

    public TerrainBase? Read(TerrainCell cell, CancellationToken cancellation = default)
    {
        using var file = index.ReadTerrainCell(cell);
        if (file?.RootChunk is not CMesh mesh) return null;
        cancellation.ThrowIfCancellationRequested();
        var geometry = TerrainCellMesh.Read(mesh);
        var visual = Rasterize(geometry);
        var covered = new bool[TerrainBase.Quads * TerrainBase.Quads];
        for (int qy = 0; qy < TerrainBase.Quads; qy++)
            for (int qx = 0; qx < TerrainBase.Quads; qx++)
                covered[qy * TerrainBase.Quads + qx] = !float.IsNaN(visual[TerrainBase.Index(qx * 2 + 1, qy * 2 + 1)]);
        float span = UvSpan(mesh);
        var mask = file.EmbeddedFiles.Select(e => e.Content).OfType<Multilayer_Mask>().FirstOrDefault();
        TerrainPaintLayers paint;
        if (mask?.RenderResourceBlob.RenderResourceBlobPC?.Chunk is rendRenderMultilayerMaskBlob blob)
        {
            var layers = MultilayerMaskCodec.Decode(blob);
            paint = new() { Width = layers.Width, Height = layers.Height, Span = span, Layers = layers.Layers };
        }
        else
        {
            // No mask: the base layer everywhere.
            var full = new byte[16 * 16]; Array.Fill(full, (byte)255);
            paint = new() { Width = 16, Height = 16, Span = span, Layers = [full] };
        }
        return new TerrainBase { Cell = cell, Visual = visual, Covered = covered, UvSpan = span, Paint = paint, LayerNames = LayerNames() };
    }

    // The material's uv = local / span + 0.5 (UVGenScaleOffset), 130 m for a full cell.
    public static float UvSpan(CMesh mesh)
    {
        foreach (var instance in mesh.PreloadLocalMaterialInstances.Select(h => h.Chunk).OfType<CMaterialInstance>())
            foreach (var pair in instance.Values)
                if (pair.Key == "UVGenScaleOffset" && pair.Value is Vector4 v && v.X > 1e-5f) return 1 / v.X;
        return 130;
    }

    public string[] LayerNames()
    {
        if (_layerNames is not null) return _layerNames;
        string[] names = [];
        try
        {
            using var setup = index.ReadFile(SetupPath);
            if (setup?.RootChunk is Multilayer_Setup s)
                names = s.Layers.Select(l => Path.GetFileNameWithoutExtension(l.Material.DepotPath.GetResolvedText() ?? "")).ToArray();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidCastException) { }
        return _layerNames = names;
    }

    // Heights of the cell's triangles at the 0.5 m lattice points they cover (NaN elsewhere).
    public static float[] Rasterize(TerrainCellMesh geometry)
    {
        var heights = new float[TerrainBase.Samples * TerrainBase.Samples];
        Array.Fill(heights, float.NaN);
        const float half = TerrainCell.Size / 2, step = TerrainBase.Step, epsilon = 1e-4f;
        foreach (var (a, b, c) in geometry.Triangles())
        {
            // Lattice coordinates.
            Vector2 pa = new((a.X + half) / step, (a.Y + half) / step), pb = new((b.X + half) / step, (b.Y + half) / step), pc = new((c.X + half) / step, (c.Y + half) / step);
            float area = (pb.X - pa.X) * (pc.Y - pa.Y) - (pc.X - pa.X) * (pb.Y - pa.Y);
            if (MathF.Abs(area) < 1e-9f) continue;
            int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(pa.X, MathF.Min(pb.X, pc.X)))), x1 = Math.Min(TerrainBase.Samples - 1, (int)MathF.Ceiling(MathF.Max(pa.X, MathF.Max(pb.X, pc.X))));
            int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(pa.Y, MathF.Min(pb.Y, pc.Y)))), y1 = Math.Min(TerrainBase.Samples - 1, (int)MathF.Ceiling(MathF.Max(pa.Y, MathF.Max(pb.Y, pc.Y))));
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float w0 = ((pb.X - x) * (pc.Y - y) - (pc.X - x) * (pb.Y - y)) / area;
                    float w1 = ((pc.X - x) * (pa.Y - y) - (pa.X - x) * (pc.Y - y)) / area;
                    float w2 = 1 - w0 - w1;
                    if (w0 < -epsilon || w1 < -epsilon || w2 < -epsilon) continue;
                    heights[TerrainBase.Index(x, y)] = w0 * a.Z + w1 * b.Z + w2 * c.Z;
                }
        }
        return heights;
    }
}
