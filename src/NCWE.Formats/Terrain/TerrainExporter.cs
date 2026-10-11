// SPDX-License-Identifier: GPL-3.0-only
using NCWE.Core;
using NCWE.Core.Terrain;
using NCWE.Formats.CustomMeshes;
using WolvenKit.RED4.Archive.Buffer;
using WolvenKit.RED4.Archive.CR2W;
using WolvenKit.RED4.Archive.IO;
using WolvenKit.RED4.Types;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace NCWE.Formats.Terrain;

// An ArchiveXL node deletion outside the project's placement edits (a terrain collision node the
// export replaces).
public sealed record ExtraNodeDeletion(string SectorPath, int ExpectedNodes, int Placement, string NodeType);

public sealed record TerrainExportSummary(int Cells, int Collisions, int CellsWithoutCollision, int SculptedCells, int PaintedCells, int CutCells, string[] Notes)
{
    // Distant terrain meshes (proxies) rewritten with the edits.
    public int Proxies { get; init; }
    // Plant nodes replaced, plants moved with the ground, plants left out (in holes).
    public int PlantNodes { get; init; }
    public int PlantsMoved { get; init; }
    public int PlantsRemoved { get; init; }
}

// Nodes: the edited cells' collision height fields and the plant nodes that follow the ground.
public sealed record TerrainExport(IReadOnlyList<CookedFile> Files, IReadOnlyList<PreparedNode> Nodes, IReadOnlyList<ExtraNodeDeletion> Deletions, TerrainExportSummary Summary);

// The game files of a project's terrain edits:
// - each edited cell's mesh, at the game's own path (the archive replaces it): a 1 m grid with the
//   edited heights and holes, its normal map recomputed from them, its multilayer mask repainted;
// - each edited cell's collision: the sector's height field node deleted (ArchiveXL) and a copy
//   with the new heights, holes and ground materials added to the project's sectors.
public static class TerrainExporter
{
    public const string NodeType = "worldTerrainCollisionNode";

    // shapes: the roads that shape the ground (RoadGround), applied over the edits.
    public static async Task<TerrainExport> BuildAsync(GameArchiveIndex index, Guid project, IReadOnlyDictionary<string, TerrainCellData> terrain,
        IProgress<string>? progress = null, CancellationToken cancellation = default, IReadOnlyCollection<TerrainShape>? shapes = null)
    {
        var source = new ArchiveTerrainSource(index);
        var sculptor = new TerrainSculptor(source);
        sculptor.SetCommitted(terrain);
        if (shapes is { Count: > 0 })
        {
            progress?.Report($"Terrain under {shapes.Count} road(s)...");
            // The ground along the roads is read first: how far their banks reach depends on it.
            sculptor.Load(shapes.SelectMany(s => s.Line).Select(p => TerrainCell.At(p.X, p.Y)).Distinct()
                .SelectMany(c => Enumerable.Range(-1, 3).SelectMany(dy => Enumerable.Range(-1, 3).Select(dx => new TerrainCell(c.X + dx, c.Y + dy)))).Distinct(), cancellation);
            sculptor.SetShapes(shapes);
            sculptor.Load(sculptor.ShapedCells.ToArray(), cancellation);
        }
        var cells = sculptor.EditedCells.Order().ToArray();
        // The cells and their neighbours (normals and collision borders read across the edge).
        sculptor.Load(cells.SelectMany(c => Enumerable.Range(-1, 3).SelectMany(dy => Enumerable.Range(-1, 3).Select(dx => new TerrainCell(c.X + dx, c.Y + dy)))).Distinct(), cancellation);
        var files = new List<CookedFile>(); var nodes = new List<PreparedNode>(); var deletions = new List<ExtraNodeDeletion>();
        int sculpted = 0, painted = 0, cut = 0, missing = 0;
        var notes = new List<string>();
        foreach (var cell in cells)
        {
            cancellation.ThrowIfCancellationRequested();
            if (sculptor.Base(cell) is not { } @base || sculptor.Effective(cell) is not { } edit) { notes.Add($"Cell {cell}: no game terrain there; skipped."); continue; }
            // Plants removed and nothing else: the ground stays the game's (the plants go below).
            if (edit.Height is null && edit.Holes is null && edit.Paint is null) continue;
            progress?.Report($"Terrain cell {cell}: mesh...");
            if (edit.Height is not null) sculpted++;
            if (edit.Paint is not null) painted++;
            if (edit.Holes is not null) cut++;
            files.Add(new(cell.MeshPath, await Task.Run(() => CellMesh(index, sculptor, @base, edit), cancellation)));
            progress?.Report($"Terrain cell {cell}: collision...");
            var collision = await index.FindTerrainCollisionAsync(cell, cancellation);
            if (collision is null || !collision.Standard || collision.Node is null) { missing++; notes.Add($"Cell {cell}: no terrain collision node found; only its look changes."); continue; }
            var (node, transform, bounds) = CollisionNode(sculptor, @base, edit, collision);
            nodes.Add(new PreparedNode($"terrain-collision-{cell}", node, transform, null, bounds));
            deletions.Add(new(collision.SectorPath, collision.ExpectedNodes, collision.Placement, NodeType));
        }
        // The distant terrain (proxies of 2 to 32 cells drawn beyond ~360 m) follows the edits.
        int proxies = 0;
        foreach (var path in ProxyPaths(cells).Where(index.Contains))
        {
            cancellation.ThrowIfCancellationRequested();
            progress?.Report("Distant terrain: " + Path.GetFileNameWithoutExtension(path) + "...");
            if (await Task.Run(() => ProxyMesh(index, sculptor, path), cancellation) is { } bytes) { files.Add(new(path, bytes)); proxies++; }
        }
        int collisions = nodes.Count;
        // The plants on the edited ground follow it.
        progress?.Report("Terrain: plants on the edited ground...");
        var plants = await FoliageAsync(index, project, sculptor, cells, cancellation);
        if (plants.File is { } plantFile) files.Add(plantFile);
        nodes.AddRange(plants.Nodes); deletions.AddRange(plants.Deletions);
        return new(files, nodes, deletions, new(cells.Length, collisions, missing, sculpted, painted, cut, notes.ToArray())
        {
            Proxies = proxies, PlantNodes = plants.Deletions.Count, PlantsMoved = plants.Moved, PlantsRemoved = plants.Removed
        });
    }

    // --- Distant terrain ---------------------------------------------------------------------------

    public const int ProxyLevels = 5;

    // terrain_aaayaai_prefab_lod<L>_<x>_<y>.mesh covers 2^L × 2^L cells from cell (x, y); its node
    // sits at the block's centre.
    public static IEnumerable<string> ProxyPaths(IEnumerable<TerrainCell> cells) => cells
        .SelectMany(c => Enumerable.Range(1, ProxyLevels).Select(level => (level, Block(c, level))))
        .Distinct().Select(p => ProxyPath(p.level, p.Item2));

    private static TerrainCell Block(TerrainCell cell, int level)
    {
        int size = 1 << level;
        return new((int)Math.Floor((cell.X - TerrainCell.Origin) / (double)size) * size + TerrainCell.Origin, (int)Math.Floor((cell.Y - TerrainCell.Origin) / (double)size) * size + TerrainCell.Origin);
    }

    public static string ProxyPath(int level, TerrainCell block) => $@"base\worlds\03_night_city\sectors\_global\terrain\terrain_aaayaai_prefab_lod{level}_{block.X}_{block.Y}.mesh";

    private static (int Level, TerrainCell Block)? ParseProxy(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var m = System.Text.RegularExpressions.Regex.Match(name, @"_prefab_lod(\d+)_(\d+)_(\d+)$");
        return m.Success ? (int.Parse(m.Groups[1].Value), new TerrainCell(int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value))) : null;
    }

    // A proxy with the edits: its vertices moved by the height change (same triangles), its normals
    // tilted by the change's slope, its mask repainted where cells were painted. Null when the
    // edits do not reach it.
    public static byte[]? ProxyMesh(GameArchiveIndex index, TerrainSculptor sculptor, string path)
    {
        if (ParseProxy(path) is not { } proxy) return null;
        using var file = index.ReadFile(path);
        if (file?.RootChunk is not CMesh mesh) return null;
        int size = 1 << proxy.Level;
        var centre = proxy.Block.Minimum + new Vector2(size * TerrainCell.Size / 2);
        var geometry = TerrainCellMesh.Read(mesh);
        float moved = 0;
        var chunks = geometry.Chunks.Select(c => new TerrainCellMesh.ChunkGeometry(c.Positions.Select(p =>
        {
            float d = sculptor.DeltaAt(centre + new Vector2(p.X, p.Y));
            moved = Math.Max(moved, MathF.Abs(d));
            return p with { Z = p.Z + d };
        }).ToArray(), c.Indices)).ToArray();
        var painted = sculptor.EditedCells.Where(c => sculptor.Edit(c)?.Paint is not null && sculptor.Base(c) is not null
            && Block(c, proxy.Level) == proxy.Block).ToArray();
        if (moved < 1e-3f && painted.Length == 0) return null;
        float span = ArchiveTerrainSource.UvSpan(mesh);
        if (moved >= 1e-3f)
        {
            TerrainCellMesh.Write(mesh, chunks);
            if (file.EmbeddedFiles.Select(e => e.Content).OfType<CBitmapTexture>().FirstOrDefault(t => t.Setup.Compression == Enums.ETextureCompression.TCM_Normalmap) is { } normal
                && XbmReader.Decode(normal) is { } image)
                Replace(normal, XbmWriter.Build(TiltNormals(image, sculptor, centre, span), TextureKind.Normal));
        }
        if (painted.Length > 0 && file.EmbeddedFiles.Select(e => e.Content).OfType<Multilayer_Mask>().FirstOrDefault() is { } mask
            && mask.RenderResourceBlob.RenderResourceBlobPC?.Chunk is rendRenderMultilayerMaskBlob blob)
        {
            var layers = MultilayerMaskCodec.Decode(blob);
            var resolved = painted.ToDictionary(c => c, c => (Edit: sculptor.Edit(c)!.Paint!, Layers: sculptor.Edit(c)!.Paint!.Resolve(sculptor.Base(c)!)));
            int count = Math.Max(layers.Layers.Length, resolved.Values.Max(r => r.Layers.Count));
            var output = Enumerable.Range(0, count).Select(l => l < layers.Layers.Length ? layers.Layers[l] : new byte[layers.Width * layers.Height]).ToArray();
            for (int y = 0; y < layers.Height; y++)
                for (int x = 0; x < layers.Width; x++)
                {
                    var world = centre + new Vector2(((x + .5f) / layers.Width - .5f) * span, (.5f - (y + .5f) / layers.Height) * span);
                    if (!resolved.TryGetValue(TerrainCell.At(world), out var cell)) continue;
                    var px = cell.Layers.PixelOf(world - TerrainCell.At(world).Centre);
                    int cx = Math.Clamp((int)MathF.Round(px.X), 0, cell.Layers.Width - 1), cy = Math.Clamp((int)MathF.Round(px.Y), 0, cell.Layers.Height - 1);
                    for (int l = 0; l < cell.Layers.Count; l++)
                        if (l < cell.Edit.Layers.Length && cell.Edit.Layers[l] is not null) output[l][y * layers.Width + x] = cell.Layers.Layers[l][cy * cell.Layers.Width + cx];
                }
            MultilayerMaskCodec.Encode(blob, new MaskLayers(layers.Width, layers.Height, output));
        }
        using var stream = new MemoryStream();
        using (var writer = new CR2WWriter(stream, System.Text.Encoding.UTF8, true)) writer.WriteFile(file);
        var bytes = stream.ToArray();
        Verify(bytes, chunks.Select(c => new TerrainMesher.Chunk(c.Positions, c.Indices)).ToArray(), painted.Length > 0);
        return bytes;
    }

    // A height field's normal (-hx, -hy, 1) with the edits' slope added to it.
    private static RgbaImage TiltNormals(RgbaImage image, TerrainSculptor sculptor, Vector2 centre, float span)
    {
        var pixels = (byte[])image.Pixels.Clone();
        float texel = span / image.Width;
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
            {
                var world = centre + new Vector2(((x + .5f) / image.Width - .5f) * span, (.5f - (y + .5f) / image.Height) * span);
                float dx = (sculptor.DeltaAt(world + new Vector2(texel, 0)) - sculptor.DeltaAt(world - new Vector2(texel, 0))) / (2 * texel);
                float dy = (sculptor.DeltaAt(world + new Vector2(0, texel)) - sculptor.DeltaAt(world - new Vector2(0, texel))) / (2 * texel);
                if (dx == 0 && dy == 0) continue;
                int at = (y * image.Width + x) * 4;
                float nx = pixels[at] / 127.5f - 1, ny = pixels[at + 1] / 127.5f - 1, nz = MathF.Sqrt(Math.Max(1e-4f, 1 - nx * nx - ny * ny));
                var n = Vector3.Normalize(new(-(-nx / nz + dx), -(-ny / nz + dy), 1));
                pixels[at] = (byte)Math.Clamp(MathF.Round((n.X * .5f + .5f) * 255), 0, 255);
                pixels[at + 1] = (byte)Math.Clamp(MathF.Round((n.Y * .5f + .5f) * 255), 0, 255);
                pixels[at + 2] = (byte)Math.Clamp(MathF.Round((n.Z * .5f + .5f) * 255), 0, 255);
            }
        return new RgbaImage(image.Width, image.Height, pixels);
    }

    // --- Mesh -----------------------------------------------------------------------------------------

    public static byte[] CellMesh(GameArchiveIndex index, TerrainSculptor sculptor, TerrainBase @base, TerrainCellEdit edit)
    {
        var cell = @base.Cell;
        using var file = index.ReadTerrainCell(cell) ?? throw new FileNotFoundException(cell.MeshPath);
        var mesh = (CMesh)file.RootChunk;
        var chunks = TerrainMesher.Build(@base, edit);
        if (chunks.Count == 0)
        {
            // Every quad cut out: one degenerate triangle far below keeps the resource valid.
            chunks = [new TerrainMesher.Chunk([new(0, 0, -5000), new(.01f, 0, -5000), new(0, .01f, -5000)], [0, 1, 2])];
        }
        TerrainCellMesh.Write(mesh, chunks.Select(c => new TerrainCellMesh.ChunkGeometry(c.Positions, c.Indices)).ToArray());
        var textures = file.EmbeddedFiles.Select(e => e.Content).OfType<CBitmapTexture>().ToArray();
        if (edit.Height is not null && textures.FirstOrDefault(t => t.Setup.Compression == Enums.ETextureCompression.TCM_Normalmap) is { } normal)
            Replace(normal, XbmWriter.Build(NormalMap(sculptor, @base, Math.Max((int)(uint)normal.Width, RoundUp4(@base.UvSpan))), TextureKind.Normal));
        if (edit.Paint is { } paint && file.EmbeddedFiles.Select(e => e.Content).OfType<Multilayer_Mask>().FirstOrDefault() is { } mask
            && mask.RenderResourceBlob.RenderResourceBlobPC?.Chunk is rendRenderMultilayerMaskBlob blob)
        {
            var layers = paint.Resolve(@base);
            MultilayerMaskCodec.Encode(blob, new MaskLayers(layers.Width, layers.Height, layers.Layers));
        }
        byte[] bytes;
        using (var stream = new MemoryStream())
        {
            using (var writer = new CR2WWriter(stream, System.Text.Encoding.UTF8, true)) writer.WriteFile(file);
            bytes = stream.ToArray();
        }
        Verify(bytes, chunks, edit.Paint is not null);
        return bytes;
    }

    private static int RoundUp4(float value) => ((int)MathF.Ceiling(value) + 3) / 4 * 4;

    private static void Replace(CBitmapTexture target, CBitmapTexture built)
    {
        target.Width = built.Width; target.Height = built.Height; target.Depth = built.Depth;
        target.Setup = built.Setup;
        target.HistBiasMulCoef = built.HistBiasMulCoef; target.HistBiasAddCoef = built.HistBiasAddCoef;
        target.RenderTextureResource = built.RenderTextureResource;
    }

    // The world-space normals of the edited ground over the material's span (red: x, green: y),
    // north at the top, as the game's GlobalNormal textures.
    public static RgbaImage NormalMap(TerrainSculptor sculptor, TerrainBase @base, int size)
    {
        var pixels = new byte[size * size * 4];
        var centre = @base.Cell.Centre;
        float half = TerrainCell.Size / 2;
        Parallel.For(0, size, y =>
        {
            for (int x = 0; x < size; x++)
            {
                var local = new Vector2(((x + .5f) / size - .5f) * @base.UvSpan, (.5f - (y + .5f) / size) * @base.UvSpan);
                var p = centre + local;
                float H(Vector2 at)
                {
                    if (sculptor.HeightAt(at) is { } h) return h;
                    // Outside the read terrain: the nearest point of this cell.
                    var clamped = centre + Vector2.Clamp(at - centre, new(-half), new(half));
                    return sculptor.HeightAt(clamped) ?? 0;
                }
                float dx = (H(p + new Vector2(1, 0)) - H(p - new Vector2(1, 0))) / 2, dy = (H(p + new Vector2(0, 1)) - H(p - new Vector2(0, 1))) / 2;
                var n = Vector3.Normalize(new(-dx, -dy, 1));
                int at = (y * size + x) * 4;
                pixels[at] = (byte)Math.Clamp(MathF.Round((n.X * .5f + .5f) * 255), 0, 255);
                pixels[at + 1] = (byte)Math.Clamp(MathF.Round((n.Y * .5f + .5f) * 255), 0, 255);
                pixels[at + 2] = (byte)Math.Clamp(MathF.Round((n.Z * .5f + .5f) * 255), 0, 255);
                pixels[at + 3] = 255;
            }
        });
        return new RgbaImage(size, size, pixels);
    }

    // The written mesh reads back with the same chunks (and a mask that decodes).
    private static void Verify(byte[] bytes, IReadOnlyList<TerrainMesher.Chunk> chunks, bool painted)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = new CR2WReader(stream);
        if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file?.RootChunk is not CMesh mesh) throw new InvalidDataException("A terrain cell mesh could not be read back.");
        using (file)
        {
            var read = TerrainCellMesh.Read(mesh);
            if (read.Chunks.Length != chunks.Count) throw new InvalidDataException("A terrain cell mesh lost chunks when written.");
            for (int i = 0; i < chunks.Count; i++)
            {
                if (read.Chunks[i].Positions.Length != chunks[i].Positions.Length || !read.Chunks[i].Indices.AsSpan().SequenceEqual(chunks[i].Indices))
                    throw new InvalidDataException("A terrain cell mesh's triangles changed when written.");
                for (int v = 0; v < chunks[i].Positions.Length; v++)
                    if (Vector3.Distance(read.Chunks[i].Positions[v], chunks[i].Positions[v]) > .05f) throw new InvalidDataException("A terrain cell mesh's heights changed when written.");
            }
            if (painted && file.EmbeddedFiles.Select(e => e.Content).OfType<Multilayer_Mask>().FirstOrDefault()?.RenderResourceBlob.RenderResourceBlobPC?.Chunk is rendRenderMultilayerMaskBlob blob)
                MultilayerMaskCodec.Decode(blob);
        }
    }

    // --- Collision --------------------------------------------------------------------------------

    public static (worldTerrainCollisionNode Node, PlacementTransform Transform, Bounds3 Bounds) CollisionNode(TerrainSculptor sculptor, TerrainBase @base, TerrainCellEdit edit, TerrainCollision collision)
    {
        var field = collision.Field.Clone();
        int rows = field.Rows, columns = field.Columns;
        var heights = new float[rows * columns];
        TerrainPaintLayers? paint = edit.Paint?.Resolve(@base);
        TerrainPaintLayers? original = paint is null ? null : @base.Paint.Resample(paint.Width, paint.Height);
        var centre = @base.Cell.Centre;
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < columns; c++)
            {
                int i = field.Index(r, c);
                var p = collision.SamplePosition(r, c);
                long gx = (long)MathF.Round(p.X / TerrainBase.Step), gy = (long)MathF.Round(p.Y / TerrainBase.Step);
                float h = collision.ActorPosition.Z + field.Heights[i] * collision.HeightScale + (sculptor.LatticeDelta(gx, gy) ?? 0);
                heights[i] = h;
                byte tess = (byte)(field.Material0[i] & 0x80);
                // The triangles of the sample's square: cut where the 1 m quad holding its centre is.
                if (sculptor.IsHole(p + new Vector2(.25f)))
                {
                    field.Material0[i] = (byte)(tess | PhysxHeightField.HoleMaterial); field.Material1[i] = PhysxHeightField.HoleMaterial;
                    continue;
                }
                if ((field.Material0[i] & 0x7F) == PhysxHeightField.HoleMaterial)
                {
                    // A hole of the game's own that the edits did not ask for stays.
                    continue;
                }
                // Ground material: the layer painted on top, where painting changed it.
                if (paint is not null && original is not null)
                {
                    var px = paint.PixelOf(p + new Vector2(.25f) - centre);
                    int x = (int)MathF.Round(px.X), y = (int)MathF.Round(px.Y);
                    if (x < 0 || y < 0 || x >= paint.Width || y >= paint.Height) continue;
                    int now = paint.Dominant(x, y), before = original.Dominant(x, y);
                    if (now != before && now < collision.MaterialIndices.Length)
                    {
                        field.Material0[i] = (byte)(tess | now); field.Material1[i] = (byte)now;
                    }
                }
            }
        float min = heights.Min(), max = heights.Max();
        float scale = Math.Max((max - min) / 32766f, 1e-5f);
        for (int i = 0; i < heights.Length; i++) field.Heights[i] = (short)Math.Clamp(MathF.Round((heights[i] - min) / scale), 0, 32766);
        var node = (worldTerrainCollisionNode)collision.Node!.DeepCopy();
        node.HeightfieldGeometry = new SerializationDeferredDataBuffer(field.ToBytes());
        node.HeightScale = scale;
        node.ActorTransform.Position.Z = min;
        float halfHeight = (max - min) / 2;
        if (halfHeight + 1 > node.Extents.Z) node.Extents = new Vector4 { X = node.Extents.X, Y = node.Extents.Y, Z = halfHeight + 1, W = node.Extents.W };
        var position = new Vector3(collision.NodePosition.X, collision.NodePosition.Y, (min + max) / 2);
        var bounds = new Bounds3(new(centre.X - TerrainCell.Size / 2 - 2, centre.Y - TerrainCell.Size / 2 - 2, min), new(centre.X + TerrainCell.Size / 2 + 2, centre.Y + TerrainCell.Size / 2 + 2, max));
        return (node, PlacementTransform.Identity with { Position = position }, bounds);
    }

    // --- Plants -------------------------------------------------------------------------------------

    public const string FoliageNodeType = "worldFoliageNode";
    public static string FoliagePath(Guid project) => WorldResourceWriter.Namespace(project) + @"\terrain\plants.cfoliage";

    public sealed record FoliageExport(CookedFile? File, IReadOnlyList<PreparedNode> Nodes, IReadOnlyList<ExtraNodeDeletion> Deletions, int Moved, int Removed);

    // The game's plants standing on the edited ground follow it (TerrainFollow, as the editor draws
    // them): each plant node with a plant moved or in a hole is deleted from its sector (ArchiveXL)
    // and a copy added to the project's sectors, its stances in one compiled resource of the
    // project's (the game's shared resources stay untouched), its plants in holes left out.
    public static async Task<FoliageExport> FoliageAsync(GameArchiveIndex index, Guid project, TerrainSculptor sculptor, IReadOnlyCollection<TerrainCell> cells,
        CancellationToken cancellation = default)
    {
        if (cells.Count == 0) return new(null, [], [], 0, 0);
        // A plant node's stances reach a few tens of metres from its placement.
        const float reach = 64;
        var minimum = new Vector2(cells.Min(c => c.Minimum.X) - reach, cells.Min(c => c.Minimum.Y) - reach);
        var maximum = new Vector2(cells.Max(c => c.Minimum.X) + TerrainCell.Size + reach, cells.Max(c => c.Minimum.Y) + TerrainCell.Size + reach);
        var found = await index.FindFoliageAsync(minimum, maximum, cancellation);
        var stances = new List<FoliageStance>(); var buckets = new List<(int Sub, int Count)>();
        var nodes = new List<PreparedNode>(); var deletions = new List<ExtraNodeDeletion>();
        int moved = 0, removed = 0;
        ulong template = 0;
        string path = FoliagePath(project);
        foreach (var plant in found.OrderBy(f => f.SectorPath, StringComparer.Ordinal).ThenBy(f => f.Placement))
        {
            cancellation.ThrowIfCancellationRequested();
            var t = plant.Transform;
            var inverse = System.Numerics.Quaternion.Inverse(t.Rotation);
            var placed = new FoliageStance[plant.Stances.Length];
            var kept = new bool[plant.Stances.Length];
            int nodeMoved = 0, nodeRemoved = 0;
            for (int k = 0; k < placed.Length; k++)
            {
                var s = plant.Stances[k];
                var world = t.Position + Vector3.Transform(s.Position * t.Scale, t.Rotation);
                placed[k] = s; kept[k] = true;
                if (TerrainFollow.OnEditedGround(sculptor, world, TerrainFollow.FoliageBelow, TerrainFollow.FoliageAbove) is not { } follow) continue;
                if (follow.Gone) { kept[k] = false; nodeRemoved++; continue; }
                var local = Vector3.Transform(new Vector3(0, 0, follow.Delta), inverse) / Vector3.Max(Vector3.Abs(t.Scale), new(1e-4f));
                placed[k] = s with { Position = s.Position + local };
                nodeMoved++;
            }
            if (nodeMoved == 0 && nodeRemoved == 0) continue;
            deletions.Add(new(plant.SectorPath, plant.ExpectedNodes, plant.Placement, FoliageNodeType));
            moved += nodeMoved; removed += nodeRemoved;
            // The node's buckets: consecutive ranges covering its stances. Plants in holes leave
            // them (and an emptied bucket goes); buckets of another shape keep every plant, those
            // in holes shrunk to nothing.
            bool partition = plant.Buckets.Length > 0 && plant.Buckets[0].Sub == 0
                && plant.Buckets.Zip(plant.Buckets.Skip(1)).All(p => p.First.Sub + p.First.Count == p.Second.Sub)
                && plant.Buckets[^1].Sub + plant.Buckets[^1].Count == placed.Length;
            int stanceBegin = stances.Count, bucketBegin = buckets.Count;
            if (partition)
                foreach (var (sub, count) in plant.Buckets)
                {
                    int start = stances.Count - stanceBegin;
                    for (int k = sub; k < sub + count; k++) if (kept[k]) stances.Add(placed[k]);
                    if (stances.Count - stanceBegin > start) buckets.Add((start, stances.Count - stanceBegin - start));
                }
            else
            {
                for (int k = 0; k < placed.Length; k++) stances.Add(kept[k] ? placed[k] : placed[k] with { Scale = 1e-4f });
                buckets.AddRange(plant.Buckets);
            }
            int stanceCount = stances.Count - stanceBegin;
            if (stanceCount == 0) continue;   // every plant in a hole: the node only goes
            template = template == 0 ? plant.Resource : template;
            var node = plant.Node;
            node.FoliageResource = new CResourceAsyncReference<worldFoliageCompiledResource>(path);
            node.PopulationSpanInfo = new worldFoliagePopulationSpanInfo
            {
                StancesBegin = (uint)stanceBegin, StancesCount = (uint)stanceCount, CketBegin = (uint)bucketBegin, CketCount = (uint)(buckets.Count - bucketBegin)
            };
            // Local bounds (stance offsets) grown to the moved plants.
            var mine = stances.Skip(stanceBegin).Take(stanceCount).Select(s => s.Position).ToArray();
            var lo = mine.Aggregate(Vector3.Min); var hi = mine.Aggregate(Vector3.Max);
            var box = node.FoliageLocalBounds;
            node.FoliageLocalBounds = new Box
            {
                Min = new Vector4 { X = MathF.Min(box.Min.X, lo.X), Y = MathF.Min(box.Min.Y, lo.Y), Z = MathF.Min(box.Min.Z, lo.Z), W = box.Min.W },
                Max = new Vector4 { X = MathF.Max(box.Max.X, hi.X), Y = MathF.Max(box.Max.Y, hi.Y), Z = MathF.Max(box.Max.Z, hi.Z), W = box.Max.W }
            };
            var worlds = mine.Select(p => t.Position + Vector3.Transform(p * t.Scale, t.Rotation)).ToArray();
            float height = (float)node.MeshHeight * 4 + 2;
            var bounds = new Bounds3(worlds.Aggregate(Vector3.Min) - new Vector3(2, 2, 1), worlds.Aggregate(Vector3.Max) + new Vector3(2, 2, height));
            nodes.Add(new PreparedNode($"terrain-plants-{WolvenKit.Common.FNV1A.FNV1A64HashAlgorithm.HashString(plant.SectorPath):x16}-{plant.Placement}", node, t, null, bounds));
        }
        if (deletions.Count == 0) return new(null, [], [], 0, 0);
        // Written from a foliage file of the archives: the plants' own, or (outside the city, where
        // the sectors carry their plants inside themselves) any of them.
        CookedFile? file = stances.Count == 0 ? null : new(path, FoliageResource(index, index.FoliageTemplate(template), stances, buckets));
        return new(file, nodes, deletions, moved, removed);
    }

    // A compiled foliage resource with these stances and buckets, written from a game one (its
    // version and buffer layout), then read back.
    public static byte[] FoliageResource(GameArchiveIndex index, ulong template, IReadOnlyList<FoliageStance> stances, IReadOnlyList<(int Sub, int Count)> buckets)
    {
        using var file = index.ReadFile(template) ?? throw new InvalidDataException("The game's foliage resource could not be read.");
        if (file.RootChunk is not worldFoliageCompiledResource { DataBuffer.Buffer.Data: FoliageBuffer buffer } resource) throw new InvalidDataException("Not a compiled foliage resource.");
        buffer.Populations.Clear(); buffer.Buckets.Clear();
        foreach (var s in stances)
            buffer.Populations.Add(new Foliage_Class1
            {
                Position = new WolvenKit.RED4.Types.Vector3 { X = s.Position.X, Y = s.Position.Y, Z = s.Position.Z }, Scale = s.Scale,
                Rotation = new Vector4 { X = s.Rotation.X, Y = s.Rotation.Y, Z = s.Rotation.Z, W = s.Rotation.W }
            });
        foreach (var (sub, count) in buckets) buffer.Buckets.Add(new Foliage_BucketClass { PopulationSubIndex = (uint)sub, PopulationCount = (uint)count });
        resource.PopulationCount = (uint)stances.Count; resource.BucketCount = (uint)buckets.Count;
        byte[] bytes;
        using (var stream = new MemoryStream())
        {
            using (var writer = new CR2WWriter(stream, System.Text.Encoding.UTF8, true)) writer.WriteFile(file);
            bytes = stream.ToArray();
        }
        using var back = new MemoryStream(bytes);
        using var reader = new CR2WReader(back);
        if (reader.ReadFile(out var read) != EFileReadErrorCodes.NoError || read?.RootChunk is not worldFoliageCompiledResource { DataBuffer.Buffer.Data: FoliageBuffer check }
            || check.Populations.Count != stances.Count || check.Buckets.Count != buckets.Count)
            throw new InvalidDataException("The plants' foliage resource could not be read back.");
        using (read)
            for (int i = 0; i < stances.Count; i++)
                if (MathF.Abs(check.Populations[i].Position.Z - stances[i].Position.Z) > .01f) throw new InvalidDataException("The plants' heights changed when written.");
        return bytes;
    }
}
