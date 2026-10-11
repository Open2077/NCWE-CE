// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace NCWE.Core.Terrain;

// A project's changes to one terrain cell, as stored in the project (ChangeSet.Terrain, keyed by
// "x_y"): the compressed TerrainCellEdit. Immutable: every brush stroke makes new data, so undo
// snapshots share it.
public sealed record TerrainCellData(byte[] Data)
{
    [System.Text.Json.Serialization.JsonIgnore] public string Hash => _hash ??= Convert.ToHexString(SHA256.HashData(Data))[..16];
    private string? _hash;
    public TerrainCellEdit Read() => TerrainCellEdit.Deserialize(Data);
    // Same bytes, same data (copies arrive from saved projects and collaboration).
    public bool Equals(TerrainCellData? other) => other is not null && (ReferenceEquals(Data, other.Data) || Data.AsSpan().SequenceEqual(other.Data));
    public override int GetHashCode() => Hash.GetHashCode(StringComparison.Ordinal);
}

// The edits of one cell over the game's terrain (TerrainBase):
// - Height: added to the game's heights, on the base's 0.5 m lattice (null: none);
// - Holes: 1 m quads cut out of the ground, mesh and collision (null: none);
// - Paint: the cell's multilayer mask layers, at 0.5 m over the material's texture span; a null
//   layer is the game's own (null: the game's mask unchanged);
// - NoPlants: 1 m quads whose game plants are removed (null: none).
public sealed class TerrainCellEdit
{
    public required TerrainCell Cell { get; init; }
    public float[]? Height { get; set; }
    public bool[]? Holes { get; set; }
    public TerrainPaintEdit? Paint { get; set; }
    public bool[]? NoPlants { get; set; }

    public bool IsEmpty => Height is null && Holes is null && Paint is null && NoPlants is null;

    public TerrainCellEdit Clone() => new()
    {
        Cell = Cell, Height = (float[]?)Height?.Clone(), Holes = (bool[]?)Holes?.Clone(), NoPlants = (bool[]?)NoPlants?.Clone(),
        Paint = Paint is null ? null : new() { Width = Paint.Width, Height = Paint.Height, Span = Paint.Span, Layers = Paint.Layers.Select(l => (byte[]?)l?.Clone()).ToArray() }
    };

    // Drops parts that no longer change anything (a stroke that undid itself, restored areas).
    public void Normalize(TerrainBase? @base)
    {
        if (Height is { } h && Array.TrueForAll(h, v => MathF.Abs(v) < 5e-4f)) Height = null;
        if (Holes is { } holes && !Array.Exists(holes, x => x)) Holes = null;
        if (NoPlants is { } noPlants && !Array.Exists(noPlants, x => x)) NoPlants = null;
        if (Paint is { } paint)
        {
            if (@base is not null)
            {
                var original = @base.Paint.Resample(paint.Width, paint.Height);
                for (int l = 0; l < paint.Layers.Length; l++)
                    if (paint.Layers[l] is { } layer && (l < original.Count ? layer.AsSpan().SequenceEqual(original.Layers[l]) : !Array.Exists(layer, v => v != 0)))
                        paint.Layers[l] = null;
            }
            if (Array.TrueForAll(paint.Layers, l => l is null)) Paint = null;
        }
    }

    private const uint Magic = 0x4554434E; // "NCTE"
    // Version 2 adds NoPlants; data without it stays version 1 (read by earlier NCWE Studios).
    private const int Version = 1, PlantsVersion = 2;

    public TerrainCellData ToData() => new(Serialize());

    public byte[] Serialize()
    {
        using var raw = new MemoryStream();
        using (var w = new BinaryWriter(raw, System.Text.Encoding.UTF8, true))
        {
            w.Write(Magic); w.Write(NoPlants is null ? Version : PlantsVersion); w.Write(Cell.X); w.Write(Cell.Y);
            w.Write((byte)((Height is not null ? 1 : 0) | (Holes is not null ? 2 : 0) | (Paint is not null ? 4 : 0) | (NoPlants is not null ? 8 : 0)));
            if (Height is { } height)
            {
                if (height.Length != TerrainBase.Samples * TerrainBase.Samples) throw new InvalidDataException("Terrain height edits hold 257 × 257 samples.");
                // 16-bit steps of the largest change, predicted from the previous sample (smooth
                // fields compress well).
                float largest = height.Max(MathF.Abs);
                float scale = Math.Max(largest / 32000f, 1e-4f);
                w.Write(scale);
                short previous = 0;
                for (int i = 0; i < height.Length; i++)
                {
                    short q = (short)Math.Clamp(MathF.Round(height[i] / scale), -32767, 32767);
                    w.Write((short)(q - previous)); previous = q;
                }
            }
            if (Holes is { } holes)
            {
                if (holes.Length != TerrainBase.Quads * TerrainBase.Quads) throw new InvalidDataException("Terrain holes cover 128 × 128 quads.");
                w.Write(Bits(holes));
            }
            if (Paint is { } paint)
            {
                w.Write(paint.Width); w.Write(paint.Height); w.Write(paint.Span); w.Write(paint.Layers.Length);
                foreach (var layer in paint.Layers)
                {
                    w.Write(layer is not null);
                    if (layer is null) continue;
                    if (layer.Length != paint.Width * paint.Height) throw new InvalidDataException("A paint layer does not match its size.");
                    var coded = new byte[layer.Length];
                    for (int i = 0; i < layer.Length; i++) coded[i] = (byte)(layer[i] - (i % paint.Width == 0 ? (i >= paint.Width ? layer[i - paint.Width] : 0) : layer[i - 1]));
                    w.Write(coded);
                }
            }
            if (NoPlants is { } noPlants) w.Write(Bits(noPlants));
        }
        using var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Optimal, true)) { raw.Position = 0; raw.CopyTo(z); }
        return packed.ToArray();
    }

    public static TerrainCellEdit Deserialize(byte[] data)
    {
        using var unpacked = new MemoryStream();
        using (var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress)) z.CopyTo(unpacked);
        unpacked.Position = 0;
        using var r = new BinaryReader(unpacked);
        if (r.ReadUInt32() != Magic) throw new InvalidDataException("Not NCWE terrain data.");
        int version = r.ReadInt32();
        if (version is not (Version or PlantsVersion)) throw new InvalidDataException($"Terrain data version {version} is newer than this NCWE Studio.");
        var edit = new TerrainCellEdit { Cell = new(r.ReadInt32(), r.ReadInt32()) };
        byte flags = r.ReadByte();
        if ((flags & 1) != 0)
        {
            float scale = r.ReadSingle();
            var height = new float[TerrainBase.Samples * TerrainBase.Samples];
            short previous = 0;
            for (int i = 0; i < height.Length; i++) { previous = (short)(previous + r.ReadInt16()); height[i] = previous * scale; }
            edit.Height = height;
        }
        if ((flags & 2) != 0)
        {
            edit.Holes = Quads(r);
        }
        if ((flags & 4) != 0)
        {
            int width = r.ReadInt32(), height = r.ReadInt32(); float span = r.ReadSingle(); int count = r.ReadInt32();
            if (width is < 1 or > 4096 || height is < 1 or > 4096 || count is < 1 or > 32) throw new InvalidDataException("Invalid terrain paint size.");
            var layers = new byte[]?[count];
            for (int l = 0; l < count; l++)
            {
                if (!r.ReadBoolean()) continue;
                var coded = r.ReadBytes(width * height);
                var layer = new byte[coded.Length];
                for (int i = 0; i < layer.Length; i++) layer[i] = (byte)(coded[i] + (i % width == 0 ? (i >= width ? layer[i - width] : 0) : layer[i - 1]));
                layers[l] = layer;
            }
            edit.Paint = new() { Width = width, Height = height, Span = span, Layers = layers };
        }
        if ((flags & 8) != 0) edit.NoPlants = Quads(r);
        return edit;
    }

    // 128 × 128 quad flags as bits.
    private static byte[] Bits(bool[] quads)
    {
        if (quads.Length != TerrainBase.Quads * TerrainBase.Quads) throw new InvalidDataException("Terrain quad masks cover 128 × 128 quads.");
        var bits = new byte[quads.Length / 8];
        for (int i = 0; i < quads.Length; i++) if (quads[i]) bits[i >> 3] |= (byte)(1 << (i & 7));
        return bits;
    }

    private static bool[] Quads(BinaryReader r)
    {
        var bits = r.ReadBytes(TerrainBase.Quads * TerrainBase.Quads / 8);
        if (bits.Length != TerrainBase.Quads * TerrainBase.Quads / 8) throw new InvalidDataException("Truncated terrain data.");
        var quads = new bool[TerrainBase.Quads * TerrainBase.Quads];
        for (int i = 0; i < quads.Length; i++) quads[i] = (bits[i >> 3] & (1 << (i & 7))) != 0;
        return quads;
    }
}

// Painted multilayer mask layers of a cell (null layer: the game's).
public sealed class TerrainPaintEdit
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required float Span { get; init; }
    public required byte[]?[] Layers { get; set; }

    // Every layer, the game's filling the gaps (resampled to this resolution).
    public TerrainPaintLayers Resolve(TerrainBase @base)
    {
        // Resampling the game's mask is only needed for the layers this edit does not hold.
        bool complete = Layers.Length >= @base.Paint.Count && Array.TrueForAll(Layers, l => l is not null);
        var original = complete ? null : @base.Paint.Resample(Width, Height);
        if (original is null) return new() { Width = Width, Height = Height, Span = Span, Layers = Layers.Select(l => l!).ToArray() };
        int count = Math.Max(Layers.Length, original.Count);
        var layers = new byte[count][];
        for (int l = 0; l < count; l++)
            layers[l] = l < Layers.Length && Layers[l] is { } own ? own : l < original.Count ? original.Layers[l] : new byte[Width * Height];
        return new() { Width = Width, Height = Height, Span = Span, Layers = layers };
    }
}
