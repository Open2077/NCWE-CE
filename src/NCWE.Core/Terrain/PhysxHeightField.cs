// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;

namespace NCWE.Core.Terrain;

// A cooked PhysX height field as worldTerrainCollisionNode.HeightfieldGeometry stores it: "NXS"
// + endianness byte, "HFHF", version 1, then rows, columns, row/column limits and column count as
// floats, thickness, convex edge threshold, flags (u16), format (u32), the unscaled bounds
// (row, height, column), sample stride and count, minimum and maximum height, then one 4-byte
// sample per (row, column): a signed 16-bit height and two material bytes (bit 7 of the first is
// the tessellation flag; material 127 is a hole).
// In the world the actor turns local X (rows) into game Y, local Y (height) into game Z and
// local Z (columns) into game X; the node's RowScale/ColumnScale/HeightScale scale them.
public sealed class PhysxHeightField
{
    public const byte HoleMaterial = 127;
    private const int HeaderSize = 86;

    public int Rows { get; }
    public int Columns { get; }
    public short[] Heights { get; }
    public byte[] Material0 { get; }
    public byte[] Material1 { get; }
    private readonly byte[] _header;

    private PhysxHeightField(int rows, int columns, byte[] header, short[] heights, byte[] m0, byte[] m1)
    {
        Rows = rows; Columns = columns; _header = header; Heights = heights; Material0 = m0; Material1 = m1;
    }

    public PhysxHeightField Clone() => new(Rows, Columns, (byte[])_header.Clone(), (short[])Heights.Clone(), (byte[])Material0.Clone(), (byte[])Material1.Clone());

    public static PhysxHeightField Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize || data[0] != 'N' || data[1] != 'X' || data[2] != 'S' || data[4] != 'H' || data[5] != 'F' || data[6] != 'H' || data[7] != 'F')
            throw new InvalidDataException("Not a cooked PhysX height field.");
        if (data[3] != 1) throw new InvalidDataException("Big-endian height fields are not supported.");
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        if (version != 1) throw new InvalidDataException($"Height field version {version} is not supported.");
        int rows = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[12..]));
        int columns = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[16..]));
        uint format = BinaryPrimitives.ReadUInt32LittleEndian(data[42..]);
        uint stride = BinaryPrimitives.ReadUInt32LittleEndian(data[70..]);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(data[74..]);
        if (format != 1 || stride != 4) throw new InvalidDataException($"Height field format {format}, stride {stride} is not supported.");
        if (rows < 2 || columns < 2 || count != (uint)(rows * columns) || data.Length < HeaderSize + count * 4)
            throw new InvalidDataException("Height field sample count does not match its size.");
        var heights = new short[count]; var m0 = new byte[count]; var m1 = new byte[count];
        var samples = data[HeaderSize..];
        for (int i = 0; i < count; i++)
        {
            heights[i] = BinaryPrimitives.ReadInt16LittleEndian(samples[(i * 4)..]);
            m0[i] = samples[i * 4 + 2]; m1[i] = samples[i * 4 + 3];
        }
        return new(rows, columns, data[..HeaderSize].ToArray(), heights, m0, m1);
    }

    public int Index(int row, int column) => row * Columns + column;

    public bool IsHole(int row, int column) => (Material0[Index(row, column)] & 0x7F) == HoleMaterial;

    // The cooked bytes, bounds and height range recomputed from the samples.
    public byte[] ToBytes()
    {
        var bytes = new byte[HeaderSize + Heights.Length * 4];
        _header.CopyTo(bytes, 0);
        short min = short.MaxValue, max = short.MinValue;
        foreach (var h in Heights) { if (h < min) min = h; if (h > max) max = h; }
        var span = bytes.AsSpan();
        // Bounds: minimum (0, min, 0), maximum (rows - 1, max, columns - 1), in sample units.
        BinaryPrimitives.WriteSingleLittleEndian(span[46..], 0);
        BinaryPrimitives.WriteSingleLittleEndian(span[50..], min);
        BinaryPrimitives.WriteSingleLittleEndian(span[54..], 0);
        BinaryPrimitives.WriteSingleLittleEndian(span[58..], Rows - 1);
        BinaryPrimitives.WriteSingleLittleEndian(span[62..], max);
        BinaryPrimitives.WriteSingleLittleEndian(span[66..], Columns - 1);
        BinaryPrimitives.WriteSingleLittleEndian(span[78..], min);
        BinaryPrimitives.WriteSingleLittleEndian(span[82..], max);
        for (int i = 0; i < Heights.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(span[(HeaderSize + i * 4)..], Heights[i]);
            bytes[HeaderSize + i * 4 + 2] = Material0[i];
            bytes[HeaderSize + i * 4 + 3] = Material1[i];
        }
        return bytes;
    }

    public float HeaderSingle(int offset) => BinaryPrimitives.ReadSingleLittleEndian(_header.AsSpan(offset));
}
