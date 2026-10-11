// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;

namespace NCWE.Formats.GameAudio;

// What a Wwise object is in a sound bank's hierarchy (the item types of bank version 150).
public enum WwiseKind : byte
{
    Sound = 2, Action = 3, Event = 4, Random = 5, Switch = 6, Mixer = 7, Layer = 9,
    MusicSegment = 10, MusicTrack = 11, MusicSwitch = 12, MusicRandom = 13
}

// One piece of audio a sound or a music track plays: its media id (an entry of the opus
// container, a streamed .wem, or media embedded in a bank). PlayAt and the trims are a music
// track's (seconds); SubTrack tells its alternatives apart.
public readonly record struct WwiseSource(uint Media, uint Plugin, byte Stream, double PlayAt = 0, double TrimStart = 0, double TrimEnd = 0, double Duration = 0, int SubTrack = 0)
{
    // A codec's media (PCM, Vorbis...), not a synthesizer or an external voice line.
    public bool IsMedia => (Plugin & 0xF) == 1 && Plugin >> 16 != 8;
}

// A node of the playback hierarchy. Volume: its own offset in dB. Loop: -1 none, 0 forever, else a count.
public sealed class WwiseNode
{
    public uint Id; public WwiseKind Kind; public uint Parent;
    public float Volume; public int Loop = -1;
    public WwiseSource[] Sources = [];
    // Random and music containers: played in order rather than drawn. Switch: its default value and
    // the children of each value. Children in the container's own order when it lists them.
    public bool Sequence; public uint DefaultSwitch;
    public (uint Value, uint[] Nodes)[] Switches = [];
    public uint[] Children = [];
}

public readonly record struct WwiseAction(ushort Type, uint Target)
{
    public bool Plays => Type >> 8 == 0x04;
    public bool Stops => Type >> 8 == 0x01;
}

// The playback hierarchy of the game's sound banks (Wwise 2023, bank version 150): events, their
// actions, the containers and sounds they start. Read for what an editor needs to play a sound:
// which media, together or one of several, how loud, looping or not. Layouts were checked on
// every object of the game's 202 banks (each node's parent is a known container). Buses, effects,
// attenuations, states and game parameters are not modelled.
public sealed class WwiseHierarchy
{
    public const uint SupportedVersion = 150;
    private const int VolumeProperty = 0x00, LoopProperty = 0x54;

    public Dictionary<uint, uint[]> Events { get; } = [];
    public Dictionary<uint, WwiseAction> Actions { get; } = [];
    public Dictionary<uint, WwiseNode> Nodes { get; } = [];
    // Media embedded in a bank: id -> (bank index, offset in the bank's bytes, length).
    public Dictionary<uint, (int Bank, int Offset, int Length)> Embedded { get; } = [];
    public int Banks { get; private set; }
    public List<string> Diagnostics { get; } = [];

    // Adds a bank's objects. Returns false (and notes why) when the bank is of another version.
    public bool Add(string name, ReadOnlySpan<byte> bank)
    {
        int index = Banks++;
        int position = 0, dataStart = -1; var media = new List<(uint Id, int Offset, int Length)>();
        uint version = 0;
        while (position + 8 <= bank.Length)
        {
            uint tag = BinaryPrimitives.ReadUInt32LittleEndian(bank[position..]);
            int size = BinaryPrimitives.ReadInt32LittleEndian(bank[(position + 4)..]);
            int body = position + 8;
            if (size < 0 || body + size > bank.Length) { Diagnostics.Add($"{name}: truncated section"); break; }
            var section = bank.Slice(body, size);
            switch (tag)
            {
                case 0x44484B42:   // BKHD
                    version = BinaryPrimitives.ReadUInt32LittleEndian(section);
                    if (version != SupportedVersion) { Diagnostics.Add($"{name}: bank version {version} is not read (expected {SupportedVersion})"); return false; }
                    break;
                case 0x58444944:   // DIDX
                    for (int i = 0; i + 12 <= size; i += 12)
                        media.Add((BinaryPrimitives.ReadUInt32LittleEndian(section[i..]), BinaryPrimitives.ReadInt32LittleEndian(section[(i + 4)..]), BinaryPrimitives.ReadInt32LittleEndian(section[(i + 8)..])));
                    break;
                case 0x41544144: dataStart = body; break;   // DATA
                case 0x43524948: ReadObjects(name, section); break;   // HIRC
            }
            position = body + size;
        }
        if (dataStart >= 0) foreach (var (id, offset, length) in media) Embedded.TryAdd(id, (index, dataStart + offset, length));
        return true;
    }

    private void ReadObjects(string name, ReadOnlySpan<byte> section)
    {
        int count = BinaryPrimitives.ReadInt32LittleEndian(section), position = 4;
        for (int i = 0; i < count && position + 9 <= section.Length; i++)
        {
            byte type = section[position]; int length = BinaryPrimitives.ReadInt32LittleEndian(section[(position + 1)..]);
            if (length < 4 || position + 5 + length > section.Length) { Diagnostics.Add($"{name}: truncated object"); return; }
            var item = section.Slice(position + 5, length);
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(item);
            try { ReadObject((WwiseKind)type, id, item); }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException) { Diagnostics.Add($"{name}: object {id} ({(WwiseKind)type}) could not be read"); }
            position += 5 + length;
        }
    }

    private void ReadObject(WwiseKind kind, uint id, ReadOnlySpan<byte> d)
    {
        switch (kind)
        {
            case WwiseKind.Event:
            {
                int n = d[4]; var actions = new uint[n];
                for (int i = 0; i < n; i++) actions[i] = U32(d, 5 + 4 * i);
                Events.TryAdd(id, actions); break;
            }
            case WwiseKind.Action: Actions.TryAdd(id, new(BinaryPrimitives.ReadUInt16LittleEndian(d[4..]), U32(d, 6))); break;
            case WwiseKind.Sound:
            {
                int p = 4; var source = Source(d, ref p);
                var node = new WwiseNode { Id = id, Kind = kind, Sources = [source] };
                Base(d, p, node); Nodes.TryAdd(id, node); break;
            }
            case WwiseKind.Random or WwiseKind.Switch or WwiseKind.Mixer or WwiseKind.Layer:
            {
                var node = new WwiseNode { Id = id, Kind = kind };
                Base(d, 4, node);
                // What follows the children (order, switch values) is read once they are known.
                if (Nodes.TryAdd(id, node) && kind is WwiseKind.Random or WwiseKind.Switch) _tails[id] = d.ToArray();
                break;
            }
            case WwiseKind.MusicSegment or WwiseKind.MusicSwitch or WwiseKind.MusicRandom:
            {
                var node = new WwiseNode { Id = id, Kind = kind };
                Base(d, 5, node); Nodes.TryAdd(id, node); break;   // after the music flags byte
            }
            case WwiseKind.MusicTrack:
            {
                int p = 5; int n = I32(d, p); p += 4;
                var sources = new WwiseSource[n];
                for (int i = 0; i < n; i++) sources[i] = Source(d, ref p);
                int items = I32(d, p); p += 4;
                var placed = new List<WwiseSource>(items);
                for (int i = 0; i < items; i++, p += 44)
                {
                    int subTrack = I32(d, p); uint media = U32(d, p + 4);
                    double playAt = F64(d, p + 12), begin = F64(d, p + 20), end = F64(d, p + 28), duration = F64(d, p + 36);
                    var source = Array.Find(sources, s => s.Media == media);
                    if (source.Media == 0) continue;
                    placed.Add(source with { PlayAt = playAt / 1000, TrimStart = begin / 1000, TrimEnd = end / 1000, Duration = duration / 1000, SubTrack = subTrack });
                }
                if (items > 0) p += 4;   // sub-track count
                int automations = I32(d, p); p += 4;
                for (int i = 0; i < automations; i++) p += 12 + I32(d, p + 8) * 12;
                var node = new WwiseNode { Id = id, Kind = kind, Sources = placed.Count > 0 ? placed.ToArray() : sources };
                Base(d, p, node); Nodes.TryAdd(id, node); break;
            }
        }
    }

    // AkBankSourceData: plug-in, stream type, media id, in-memory size, flags; a source plug-in (a
    // synthesizer) carries its parameters after them.
    private static WwiseSource Source(ReadOnlySpan<byte> d, ref int p)
    {
        uint plugin = U32(d, p); byte stream = d[p + 4]; uint media = U32(d, p + 5);
        p += 14;
        if ((plugin & 0xF) == 2) p += 4 + I32(d, p);
        return new(media, plugin, stream);
    }

    // NodeBaseParams: effects, metadata, bus, parent, a flags byte, then the property bundle
    // (ids, then values) the volume and the loop count come from.
    private static void Base(ReadOnlySpan<byte> d, int p, WwiseNode node)
    {
        p++; int effects = d[p++];
        if (effects > 0) p += 1 + effects * 6;
        p++; int metadata = d[p++];
        p += metadata * 6;
        p += 4;
        node.Parent = U32(d, p); p += 4;
        p++;
        int properties = d[p++];
        for (int i = 0; i < properties; i++)
        {
            int value = p + properties + 4 * i;
            if (d[p + i] == VolumeProperty) node.Volume = BinaryPrimitives.ReadSingleLittleEndian(d[value..]);
            else if (d[p + i] == LoopProperty) node.Loop = I32(d, value);
        }
    }

    private readonly Dictionary<uint, byte[]> _tails = [];
    private bool _linked;
    // Children of each container, in the container's own order where its list is found (a child
    // count followed by exactly its children), with what random and switch containers add.
    // Called once every bank is in.
    public void Link()
    {
        if (_linked) return;
        _linked = true;
        var children = new Dictionary<uint, List<uint>>();
        foreach (var node in Nodes.Values)
        {
            if (node.Parent == 0) continue;
            if (!children.TryGetValue(node.Parent, out var list)) children[node.Parent] = list = [];
            list.Add(node.Id);
        }
        foreach (var (parent, list) in children)
            if (Nodes.TryGetValue(parent, out var node)) { list.Sort(); node.Children = list.ToArray(); }
        foreach (var (id, bytes) in _tails) ReadContainerTail(id, bytes);
        _tails.Clear();
    }

    // What a container says around its children: a random container's order, a switch container's values.
    private void ReadContainerTail(uint id, ReadOnlySpan<byte> d)
    {
        if (!Nodes.TryGetValue(id, out var node) || node.Children.Length == 0) return;
        int at = FindChildren(d, node.Children);
        if (at < 0) return;
        int count = node.Children.Length;
        var ordered = new uint[count];
        for (int i = 0; i < count; i++) ordered[i] = U32(d, at + 4 + 4 * i);
        node.Children = ordered;
        int after = at + 4 + 4 * count;
        if (node.Kind == WwiseKind.Random && at >= 2) node.Sequence = d[at - 2] == 1;
        else if (node.Kind == WwiseKind.Switch && at >= 10)
        {
            node.DefaultSwitch = U32(d, at - 5);
            if (after + 4 > d.Length) return;
            int groups = I32(d, after); int p = after + 4;
            if (groups is < 0 or > 4096) return;
            var switches = new List<(uint, uint[])>(groups);
            for (int g = 0; g < groups && p + 8 <= d.Length; g++)
            {
                uint value = U32(d, p); int n = I32(d, p + 4); p += 8;
                if (n < 0 || p + 4 * n > d.Length) return;
                var nodes = new uint[n];
                for (int i = 0; i < n; i++) nodes[i] = U32(d, p + 4 * i);
                p += 4 * n;
                switches.Add((value, nodes));
            }
            node.Switches = switches.ToArray();
        }
    }

    // Where "count, then the children in any order" stands in a container.
    private static int FindChildren(ReadOnlySpan<byte> d, uint[] children)
    {
        int count = children.Length, size = 4 + 4 * count;
        var set = new HashSet<uint>(children);
        for (int at = d.Length - size; at >= 12; at--)
        {
            if (I32(d, at) != count) continue;
            bool all = true;
            for (int i = 0; i < count && all; i++) all = set.Contains(U32(d, at + 4 + 4 * i));
            if (all) return at;
        }
        return -1;
    }

    private static uint U32(ReadOnlySpan<byte> d, int p) => BinaryPrimitives.ReadUInt32LittleEndian(d[p..]);
    private static int I32(ReadOnlySpan<byte> d, int p) => BinaryPrimitives.ReadInt32LittleEndian(d[p..]);
    private static double F64(ReadOnlySpan<byte> d, int p) => BinaryPrimitives.ReadDoubleLittleEndian(d[p..]);

    // Wwise names events by the 32-bit FNV-1 hash of their lower-case name.
    public static uint Hash(string name)
    {
        uint hash = 2166136261;
        foreach (char c in name) { hash *= 16777619; hash ^= (byte)char.ToLowerInvariant(c); }
        return hash;
    }
}
