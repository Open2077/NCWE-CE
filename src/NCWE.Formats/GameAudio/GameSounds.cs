// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Text.Json;
using WolvenKit.Common.FNV1A;
using WolvenKit.Common.Services;
using WolvenKit.RED4.Archive.IO;
using EFileReadErrorCodes = WolvenKit.RED4.Archive.IO.EFileReadErrorCodes;
using WolvenKit.RED4.Types;
using Archive = WolvenKit.RED4.Archive.Archive;
using FileEntry = WolvenKit.RED4.Archive.FileEntry;

namespace NCWE.Formats.GameAudio;

// A sound event of the game (base\sound\event\eventsmetadata.json): its name is what emitters,
// effects and scripts play. Durations in seconds (0 when the game does not know: loops);
// MaxAttenuation: metres it is heard from. Variations: the different recordings it can play.
public sealed record GameSoundEvent(string Name, uint Id, float MinDuration, float MaxDuration, float MaxAttenuation, bool Looping, string[] Tags)
{
    public int Variations { get; init; }
    public bool Playable => Variations > 0;
}

// One recording of a play: Gain is linear; PlayAt and the trims place a music clip (seconds).
public sealed record GameSoundClip(uint Media, float Gain, double PlayAt = 0, double TrimStart = 0, double TrimEnd = 0, bool Loop = false);

// What one play of an event sounds: its clips are heard together. Rerolls: another play may
// pick other recordings.
public sealed record GameSoundPlan(GameSoundEvent Event, GameSoundClip[] Clips, bool Loop, bool Rerolls);

// The game's own sounds, read from its audio archives: the event list, the Wwise banks that say
// what each event plays (WwiseHierarchy), and the recordings themselves: effects are Opus
// streams in sfx_container_*.opuspak (indexed by sfx_container.opusinfo), music and long
// ambiences are streamed Wwise Vorbis .wem files, a few are embedded in the banks.
// Thread-safe once opened; decoding allocates the whole clip.
public sealed class GameSounds : IDisposable
{
    private const string Folder = @"base\sound\soundbanks\";
    private readonly List<Archive> _archives = [];
    private readonly Dictionary<ulong, FileEntry> _entries = [];
    private readonly Dictionary<Archive, MemoryMappedFile> _mappings = [];
    private readonly WwiseHierarchy _hierarchy = new();
    private readonly Dictionary<string, GameSoundEvent> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, int> _opus = [];
    private readonly List<string> _bankPaths = [], _loadedBanks = [];
    private byte[] _opusInfo = [];
    private int _opusCount;

    public IReadOnlyList<GameSoundEvent> Events { get; private set; } = [];
    public IReadOnlyList<string> Diagnostics => _hierarchy.Diagnostics;
    public int BankCount => _hierarchy.Banks;

    private GameSounds() { }

    public static string[] ArchivePaths(string gameDirectory) =>
    [
        Path.Combine(gameDirectory, "archive", "pc", "content", "audio_1_general.archive"),
        Path.Combine(gameDirectory, "archive", "pc", "content", "audio_2_soundbanks.archive")
    ];
    public static bool Installed(string gameDirectory) => ArchivePaths(gameDirectory).All(File.Exists);

    // Reads the event list and every bank (a second or two). Throws when the audio archives are not there.
    public static GameSounds Open(string gameDirectory, IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        var sounds = new GameSounds();
        try
        {
            OodleBootstrap.LoadFromGame(gameDirectory);
            var hashes = new HashService(false);   // entries are found by the hash of their path: no name dictionary
            foreach (var path in ArchivePaths(gameDirectory))
            {
                if (!File.Exists(path)) throw new FileNotFoundException("The game's audio archive is missing: " + Path.GetFileName(path), path);
                cancellation.ThrowIfCancellationRequested();
                var code = new ArchiveReader().ReadArchive(path, hashes, out var archive);
                if (code != EFileReadErrorCodes.NoError) { archive?.Dispose(); throw new InvalidDataException($"Archive read failed: {path}: {code}"); }
                sounds._archives.Add(archive);
                foreach (var file in archive.Files.Values.OfType<FileEntry>()) sounds._entries.TryAdd(file.Key, file);
            }
            progress?.Report("Reading the game's sound events...");
            sounds.ReadEvents();
            sounds.ReadOpusIndex();
            progress?.Report("Reading the game's sound banks...");
            sounds.ReadBanks(cancellation);
            sounds.CountVariations();
            return sounds;
        }
        catch { sounds.Dispose(); throw; }
    }

    // --- Files -----------------------------------------------------------------------------------

    private static ulong Key(string depotPath) => FNV1A64HashAlgorithm.HashString(depotPath.ToLowerInvariant());
    private bool Has(string depotPath) => _entries.ContainsKey(Key(depotPath));

    private byte[]? Read(string depotPath)
    {
        if (!_entries.TryGetValue(Key(depotPath), out var entry)) return null;
        var archive = (Archive)entry.Archive;
        MemoryMappedFile mapping;
        lock (_mappings)
        {
            if (!_mappings.TryGetValue(archive, out mapping!))
            {
                var stream = new FileStream(archive.ArchiveAbsolutePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.RandomAccess);
                _mappings[archive] = mapping = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
            }
        }
        using var data = new MemoryStream(checked((int)Math.Max(0, entry.Size)));
        archive.CopyFileToStream(data, entry.Key, false, mapping);
        return data.ToArray();
    }

    // --- Index -----------------------------------------------------------------------------------

    private void ReadEvents()
    {
        var bytes = Read(@"base\sound\event\eventsmetadata.json") ?? throw new InvalidDataException("The game's sound event list is missing.");
        using var stream = new MemoryStream(bytes);
        using var reader = new CR2WReader(stream);
        if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file?.RootChunk is not JsonResource { Root.Chunk: audioAudioEventArray array })
            throw new InvalidDataException("The game's sound event list could not be read.");
        var events = new List<GameSoundEvent>(array.Events.Count);
        foreach (var e in array.Events)
        {
            string? name = e.RedId.ToString();
            if (string.IsNullOrEmpty(name) || name == "None") continue;
            var item = new GameSoundEvent(name, e.WwiseId, e.MinDuration, e.MaxDuration, e.MaxAttenuation, e.IsLooping,
                e.Tags.Select(t => t.ToString() ?? "").Where(t => t.Length > 0).ToArray());
            if (_byName.TryAdd(name, item)) events.Add(item);
        }
        Events = events;
        if (Read(@"base\sound\event\soundbanks.json") is { } banks)
        {
            using var json = JsonDocument.Parse(banks);
            foreach (var bank in json.RootElement.EnumerateArray())
                if (bank.TryGetProperty("ResourcePath", out var path) && path.GetString() is { Length: > 0 } text) _bankPaths.Add(text);
        }
        foreach (var extra in new[] { Folder + "sfx_container.bnk", Folder + "init.bnk", @"base\sound\no_spatial\init.bnk" })
            if (!_bankPaths.Contains(extra, StringComparer.OrdinalIgnoreCase)) _bankPaths.Add(extra);
    }

    // sfx_container.opusinfo: "SND ", a version, the count, then per sound its media id, pack
    // number, offset in the pack, size of the RIFF header kept before the Opus stream, stream
    // and decoded sizes (parallel arrays).
    private void ReadOpusIndex()
    {
        if (Read(Folder + "sfx_container.opusinfo") is not { Length: > 20 } info || info[0] != 'S' || info[1] != 'N' || info[2] != 'D') return;
        int count = BinaryPrimitives.ReadInt32LittleEndian(info.AsSpan(12));
        if (count <= 0 || 20 + (long)count * 20 > info.Length) return;
        _opusInfo = info; _opusCount = count;
        for (int i = 0; i < count; i++) _opus.TryAdd(BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(20 + 4 * i)), i);
    }

    private void ReadBanks(CancellationToken cancellation)
    {
        foreach (var path in _bankPaths)
        {
            cancellation.ThrowIfCancellationRequested();
            // Kept in step with the hierarchy's bank numbers (its embedded media name their bank).
            if (Read(path) is { } bank) { _hierarchy.Add(Path.GetFileName(path), bank); _loadedBanks.Add(path); }
        }
        _hierarchy.Link();
    }

    private void CountVariations()
    {
        var counted = new List<GameSoundEvent>(Events.Count);
        var media = new HashSet<uint>();
        foreach (var e in Events)
        {
            media.Clear();
            foreach (uint target in Targets(e)) Leaves(target, media, 0);
            var item = e with { Variations = media.Count };
            counted.Add(item); _byName[e.Name] = item;
        }
        Events = counted;
    }

    private IEnumerable<uint> Targets(GameSoundEvent e)
    {
        if (!_hierarchy.Events.TryGetValue(e.Id, out var actions)) yield break;
        foreach (uint action in actions)
            if (_hierarchy.Actions.TryGetValue(action, out var a) && a.Plays) yield return a.Target;
    }

    private void Leaves(uint id, HashSet<uint> media, int depth)
    {
        if (depth > 32 || !_hierarchy.Nodes.TryGetValue(id, out var node)) return;
        foreach (var source in node.Sources) if (source.IsMedia && HasMedia(source.Media)) media.Add(source.Media);
        foreach (uint child in node.Children) Leaves(child, media, depth + 1);
    }

    public bool HasMedia(uint media) => _opus.ContainsKey(media) || _hierarchy.Embedded.ContainsKey(media) || Has($@"{Folder}media\{media}.wem");

    // --- Events ----------------------------------------------------------------------------------

    public GameSoundEvent? Find(string name) => _byName.GetValueOrDefault(name);

    // One play of the event: random containers draw one of their recordings, blend containers
    // play all theirs, switch containers their default value (the game sets them from the
    // surface, the weapon...), music segments their tracks. Null when it plays nothing.
    public GameSoundPlan? Plan(string eventName, Random random)
    {
        if (Find(eventName) is not { } e) return null;
        var clips = new List<GameSoundClip>(); bool rerolls = false;
        foreach (uint target in Targets(e))
        {
            float volume = 0; uint parent = _hierarchy.Nodes.GetValueOrDefault(target)?.Parent ?? 0;
            for (int depth = 0; depth < 32 && parent != 0 && _hierarchy.Nodes.TryGetValue(parent, out var up); depth++) { volume += up.Volume; parent = up.Parent; }
            Collect(target, volume, clips, random, ref rerolls, 0);
        }
        if (clips.Count == 0) return null;
        return new(e, clips.ToArray(), e.Looping || clips.Any(c => c.Loop), rerolls);
    }

    private void Collect(uint id, float volume, List<GameSoundClip> clips, Random random, ref bool rerolls, int depth)
    {
        if (depth > 32 || clips.Count >= 24 || !_hierarchy.Nodes.TryGetValue(id, out var node)) return;
        volume += node.Volume;
        float gain = MathF.Pow(10, Math.Clamp(volume, -48, 12) / 20);
        switch (node.Kind)
        {
            case WwiseKind.Sound:
                if (node.Sources is [{ IsMedia: true } source] && HasMedia(source.Media)) clips.Add(new(source.Media, gain, Loop: node.Loop == 0));
                break;
            case WwiseKind.MusicTrack:
            {
                // One of the track's alternatives (its lowest sub-track), each clip at its place in the segment.
                var playable = node.Sources.Where(s => s.IsMedia && HasMedia(s.Media)).ToArray();
                if (playable.Length == 0) break;
                int subTrack = playable.Min(s => s.SubTrack);
                rerolls |= playable.Any(s => s.SubTrack != subTrack);
                foreach (var s in playable.Where(s => s.SubTrack == subTrack))
                    clips.Add(new(s.Media, gain, Math.Max(0, s.PlayAt + s.TrimStart), Math.Max(0, s.TrimStart), Math.Max(0, -s.TrimEnd)));
                break;
            }
            case WwiseKind.Random or WwiseKind.MusicRandom:
            {
                var children = node.Children.Where(Sounds).ToArray();
                if (children.Length == 0) break;
                rerolls |= children.Length > 1;
                Collect(node.Sequence ? children[0] : children[random.Next(children.Length)], volume, clips, random, ref rerolls, depth + 1);
                break;
            }
            case WwiseKind.Switch:
            {
                var group = node.Switches.FirstOrDefault(s => s.Value == node.DefaultSwitch && s.Nodes.Any(Sounds));
                if (group.Nodes is null) group = node.Switches.FirstOrDefault(s => s.Nodes.Any(Sounds));
                rerolls |= node.Switches.Count(s => s.Nodes.Any(Sounds)) > 1;
                foreach (uint child in group.Nodes ?? node.Children.Where(Sounds).Take(1).ToArray()) Collect(child, volume, clips, random, ref rerolls, depth + 1);
                break;
            }
            case WwiseKind.MusicSwitch:
            {
                var children = node.Children.Where(Sounds).ToArray();
                rerolls |= children.Length > 1;
                if (children.Length > 0) Collect(children[0], volume, clips, random, ref rerolls, depth + 1);
                break;
            }
            default:   // blend containers, mixers, music segments: everything they hold
                foreach (uint child in node.Children) Collect(child, volume, clips, random, ref rerolls, depth + 1);
                break;
        }
    }

    private readonly Dictionary<uint, bool> _sounds = [];
    // Whether anything under the node can be heard.
    private bool Sounds(uint id)
    {
        lock (_sounds)
        {
            if (_sounds.TryGetValue(id, out bool known)) return known;
            var media = new HashSet<uint>(); Leaves(id, media, 0);
            return _sounds[id] = media.Count > 0;
        }
    }

    // --- Recordings ------------------------------------------------------------------------------

    // The recording, decoded. maxSeconds cuts long streams (music) there.
    public GamePcm Decode(uint media, double maxSeconds = 600)
    {
        if (_opus.TryGetValue(media, out int i))
        {
            var info = _opusInfo.AsSpan(); int n = _opusCount;
            int pack = BinaryPrimitives.ReadUInt16LittleEndian(info[(20 + 4 * n + 2 * i)..]);
            int offset = BinaryPrimitives.ReadInt32LittleEndian(info[(20 + 6 * n + 4 * i)..]);
            int header = BinaryPrimitives.ReadUInt16LittleEndian(info[(20 + 10 * n + 2 * i)..]);
            int length = BinaryPrimitives.ReadInt32LittleEndian(info[(20 + 12 * n + 4 * i)..]);
            var pak = Read($"{Folder}sfx_container_{pack}.opuspak") ?? throw new FileNotFoundException($"Sound pack {pack} is missing.");
            if (offset < 0 || header < 0 || length <= header || (long)offset + length > pak.Length) throw new InvalidDataException($"Sound {media} lies outside its pack.");
            return OggOpus.Decode(pak.AsMemory(offset + header, length - header), maxSeconds);
        }
        if (Read($@"{Folder}media\{media}.wem") is { } wem) return DecodeWem(wem, maxSeconds);
        if (_hierarchy.Embedded.TryGetValue(media, out var where) && where.Bank < _loadedBanks.Count && Read(_loadedBanks[where.Bank]) is { } bank
            && where.Offset >= 0 && (long)where.Offset + where.Length <= bank.Length)
            return DecodeWem(bank.AsSpan(where.Offset, where.Length).ToArray(), maxSeconds);
        throw new FileNotFoundException($"Sound {media} is not in the game's audio archives.");
    }

    // A Wwise media file: RIFF with a "fmt " tag saying how "data" is encoded. 0xFFFF: Wwise
    // Vorbis (headers stripped, packets repacked: WolvenKit's wwtools rebuilds a standard Ogg
    // Vorbis stream, NVorbis decodes it); 0xFFFE or 1: 16-bit PCM; Opus in Ogg is decoded here.
    public static GamePcm DecodeWem(byte[] wem, double maxSeconds = 600)
    {
        var d = wem.AsSpan();
        if (d.Length < 20 || d[0] != 'R' || d[1] != 'I' || d[2] != 'F') throw new InvalidDataException("Not a Wwise media file.");
        int position = 12, format = 0, channels = 0, rate = 0, bits = 16, dataStart = -1, dataLength = 0;
        while (position + 8 <= d.Length)
        {
            uint tag = BinaryPrimitives.ReadUInt32LittleEndian(d[position..]); int size = BinaryPrimitives.ReadInt32LittleEndian(d[(position + 4)..]);
            int body = position + 8;
            if (size < 0) break;
            if (tag == 0x20746D66 && body + 16 <= d.Length)   // "fmt "
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(d[body..]); channels = BinaryPrimitives.ReadUInt16LittleEndian(d[(body + 2)..]);
                rate = BinaryPrimitives.ReadInt32LittleEndian(d[(body + 4)..]); bits = BinaryPrimitives.ReadUInt16LittleEndian(d[(body + 14)..]);
            }
            else if (tag == 0x61746164) { dataStart = body; dataLength = Math.Min(size, d.Length - body); break; }   // "data"
            position = body + size + (size & 1);
        }
        if (dataStart < 0 || channels is < 1 or > 8 || rate <= 0) throw new InvalidDataException("Unreadable Wwise media header.");
        long maxFrames = maxSeconds >= long.MaxValue / rate ? long.MaxValue : (long)(maxSeconds * rate);
        switch (format)
        {
            case 0xFFFF:
            {
                if (!WolvenKit.Core.Wwise.Wem.TryConvert(wem, out var ogg) || ogg is null) throw new InvalidDataException("Wwise Vorbis media could not be converted.");
                using var reader = new NVorbis.VorbisReader(new MemoryStream(ogg), true);
                long frames = Math.Min(Math.Max(reader.TotalSamples, 0), maxFrames);
                var all = new List<float>((int)Math.Min(frames * reader.Channels, 256 << 20));
                var buffer = new float[reader.SampleRate * reader.Channels];
                long left = maxFrames * reader.Channels;
                for (int read; left > 0 && (read = reader.ReadSamples(buffer, 0, (int)Math.Min(buffer.Length, left))) > 0; left -= read) all.AddRange(buffer.AsSpan(0, read));
                return new(all.ToArray(), reader.Channels, reader.SampleRate);
            }
            case 0xFFFE or 0x0001 when bits is 16 or 0:
            {
                int frames = (int)Math.Min(dataLength / (2 * channels), maxFrames);
                var samples = new float[frames * channels];
                var pcm = d.Slice(dataStart, frames * channels * 2);
                for (int i = 0; i < samples.Length; i++) samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm[(2 * i)..]) / 32768f;
                return new(samples, channels, rate);
            }
            default:
                if (OggOpus.IsOpus(d.Slice(dataStart, dataLength))) return OggOpus.Decode(wem.AsMemory(dataStart, dataLength), maxSeconds);
                throw new NotSupportedException($"Wwise media format 0x{format:X4} is not decoded.");
        }
    }

    public void Dispose()
    {
        lock (_mappings) { foreach (var mapping in _mappings.Values) mapping.Dispose(); _mappings.Clear(); }
        foreach (var archive in _archives) archive.Dispose();
        _archives.Clear(); _entries.Clear();
    }
}
