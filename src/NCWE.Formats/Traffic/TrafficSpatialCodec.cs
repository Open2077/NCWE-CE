// SPDX-License-Identifier: GPL-3.0-only
using NCWE.Core;

namespace NCWE.Formats.Traffic;

// The appendix of all.spatial_representation (worldTrafficPersistentSpatialResource.buffer), which
// WolvenKit keeps as raw bytes. Measured on 2.31 (Night City, 31,717 lanes, 814,698 bytes):
//   u32 lane count, then one 15-byte record per lane: u64 node hash, u16 lane number, u16 sequence,
//     u8 reversed, u16 lane index (the identity -> index table);
//   VLQ group count, then per group: VLQ n, n × u16 lane indices (ascending), i32 -1;
//   the rest (a cell table of [u32 group, i16, i16] entries and a trailer) is kept as it is.
// Lanes added by the editor get an identity record and join the groups of the lanes they link to.
public sealed class TrafficSpatialData
{
    public List<(TrafficLaneId Id, int Lane)> Identities { get; } = [];
    public List<(List<ushort> Lanes, int Tail)> Groups { get; } = [];
    public byte[] Rest { get; set; } = [];

    public static TrafficSpatialData Parse(byte[] data)
    {
        var result = new TrafficSpatialData();
        using var reader = new BinaryReader(new MemoryStream(data));
        int count = reader.ReadInt32();
        if (count < 0 || 4L + 15L * count > data.Length) throw new InvalidDataException("Unexpected traffic spatial data.");
        for (int i = 0; i < count; i++)
        {
            ulong node = reader.ReadUInt64(); ushort lane = reader.ReadUInt16(), sequence = reader.ReadUInt16();
            bool reversed = reader.ReadByte() != 0; int index = reader.ReadUInt16();
            result.Identities.Add((new TrafficLaneId(node, lane, sequence, reversed), index));
        }
        int groups = ReadVlq(reader);
        if (groups < 0 || groups > 1_000_000) throw new InvalidDataException("Unexpected traffic spatial groups.");
        for (int g = 0; g < groups; g++)
        {
            int n = ReadVlq(reader);
            if (n < 0 || n > 65536) throw new InvalidDataException("Unexpected traffic spatial group size.");
            var lanes = new List<ushort>(n);
            for (int k = 0; k < n; k++) lanes.Add(reader.ReadUInt16());
            result.Groups.Add((lanes, reader.ReadInt32()));
        }
        result.Rest = reader.ReadBytes((int)(data.Length - reader.BaseStream.Position));
        return result;
    }

    public byte[] Write()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Identities.Count);
            foreach (var (id, lane) in Identities)
            {
                writer.Write(id.Node); writer.Write(id.Lane); writer.Write(id.Sequence); writer.Write((byte)(id.Reversed ? 1 : 0));
                writer.Write(checked((ushort)lane));
            }
            WriteVlq(writer, Groups.Count);
            foreach (var (lanes, tail) in Groups)
            {
                WriteVlq(writer, lanes.Count);
                foreach (var lane in lanes) writer.Write(lane);
                writer.Write(tail);
            }
            writer.Write(Rest);
        }
        return stream.ToArray();
    }

    // Adds a lane to every group that holds one of `neighbours` (kept ascending, no duplicates).
    public void JoinGroupsOf(int lane, IReadOnlyCollection<int> neighbours)
    {
        if (neighbours.Count == 0) return;
        var wanted = neighbours.Select(n => (ushort)n).ToHashSet();
        ushort value = checked((ushort)lane);
        foreach (var (lanes, _) in Groups)
        {
            if (!lanes.Any(wanted.Contains)) continue;
            int at = lanes.BinarySearch(value);
            if (at < 0) lanes.Insert(~at, value);
        }
    }

    // WolvenKit's variable-length integers: the first byte holds the sign (0x80), a continuation
    // bit (0x40) and 6 bits; the next ones a continuation bit (0x80) and 7 bits.
    internal static int ReadVlq(BinaryReader reader)
    {
        byte b = reader.ReadByte();
        bool negative = (b & 0x80) != 0;
        int value = b & 0x3F, shift = 6;
        bool more = (b & 0x40) != 0;
        while (more)
        {
            b = reader.ReadByte();
            value |= (b & 0x7F) << shift; shift += 7;
            more = (b & 0x80) != 0;
        }
        return negative ? -value : value;
    }

    internal static void WriteVlq(BinaryWriter writer, int value)
    {
        bool negative = value < 0;
        uint v = (uint)Math.Abs((long)value);
        byte first = (byte)(v & 0x3F);
        if (negative) first |= 0x80;
        v >>= 6;
        if (v != 0) first |= 0x40;
        writer.Write(first);
        while (v != 0)
        {
            byte next = (byte)(v & 0x7F);
            v >>= 7;
            if (v != 0) next |= 0x80;
            writer.Write(next);
        }
    }
}
