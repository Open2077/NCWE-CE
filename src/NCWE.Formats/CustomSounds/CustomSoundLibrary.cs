// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using System.Text.Json.Serialization;
using NCWE.Core;

namespace NCWE.Formats.CustomSounds;

// The custom sound library: one folder per sound (sound.json and the Open77-ready sound.mp3 or
// sound.wav), shared by every project of this user.
public sealed class CustomSoundLibrary
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals, Converters = { new JsonStringEnumConverter() }
    };
    private readonly object _gate = new();
    private Dictionary<string, CustomSoundInfo>? _items;

    public CustomSoundLibrary(string directory) { Directory = directory; System.IO.Directory.CreateDirectory(directory); }

    public string Directory { get; }
    public long Revision { get; private set; }
    public event Action? Changed;

    public string Folder(string id) => Path.Combine(Directory, id);
    public string FilePath(CustomSoundInfo info) => Path.Combine(Folder(info.Id), info.File);
    private string InfoPath(string id) => Path.Combine(Folder(id), "sound.json");

    private Dictionary<string, CustomSoundInfo> Items()
    {
        lock (_gate)
        {
            if (_items is not null) return _items;
            var items = new Dictionary<string, CustomSoundInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in System.IO.Directory.EnumerateDirectories(Directory))
            {
                string id = Path.GetFileName(folder);
                if (!CustomMeshPaths.IsValidId(id) || !File.Exists(InfoPath(id))) continue;
                try
                {
                    if (JsonSerializer.Deserialize<CustomSoundInfo>(File.ReadAllText(InfoPath(id)), Json) is { } info && info.Id == id && File.Exists(Path.Combine(folder, info.File)))
                        items[id] = info;
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
            }
            return _items = items;
        }
    }

    public IReadOnlyList<CustomSoundInfo> List() { lock (_gate) return Items().Values.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList(); }
    public CustomSoundInfo? Get(string id) { lock (_gate) return Items().GetValueOrDefault(id); }

    // Copies (or converts) an audio file into the library. Slow for conversions: call off the UI thread.
    public CustomSoundInfo Import(string path, string? name = null, IProgress<string>? progress = null)
    {
        if (!AudioFiles.IsAudio(path)) throw new InvalidDataException($"{Path.GetFileName(path)}: import MP3, WAV, OGG, FLAC, M4A, AAC or WMA files.");
        name = string.IsNullOrWhiteSpace(name) ? Pretty(Path.GetFileNameWithoutExtension(path)) : name.Trim();
        progress?.Report("Reading " + Path.GetFileName(path));
        var probe = AudioFiles.Probe(path);
        string id = CustomMeshPaths.NewId(name, "sound"), folder = Folder(id), work = folder + ".partial";
        System.IO.Directory.CreateDirectory(work);
        try
        {
            var notes = new List<string>();
            string file;
            if (AudioFiles.Open77Problem(probe) is { } problem)
            {
                progress?.Report($"Converting {Path.GetFileName(path)}: {problem}");
                file = AudioFiles.ConvertForOpen77(path, Path.Combine(work, "sound"), notes);
                notes[^1] = problem + ": " + notes[^1];
                probe = AudioFiles.Probe(file);
                file = Path.GetFileName(file);
            }
            else
            {
                file = "sound" + probe.Extension;
                File.Copy(path, Path.Combine(work, file));
            }
            var info = new CustomSoundInfo(id, name)
            {
                File = file, SourceFile = Path.GetFileName(path), Duration = Math.Round(probe.Duration, 3), SampleRate = probe.SampleRate, Channels = probe.Channels,
                Bytes = probe.Bytes, Converted = notes.Count > 0, Warnings = [.. notes]
            };
            File.WriteAllText(Path.Combine(work, "sound.json"), JsonSerializer.Serialize(info, Json));
            System.IO.Directory.Move(work, folder);
            lock (_gate) Items()[id] = info;
            Touched();
            return info;
        }
        catch
        {
            TryDelete(work);
            throw;
        }
    }

    public CustomSoundInfo Rename(string id, string name)
    {
        if (Get(id) is not { } current) throw new InvalidOperationException("This sound is no longer in the library.");
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A sound needs a name.");
        var saved = current with { Name = name.Trim(), Modified = DateTimeOffset.UtcNow };
        string path = InfoPath(id);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(saved, Json));
        File.Move(path + ".tmp", path, true);
        lock (_gate) Items()[id] = saved;
        Touched();
        return saved;
    }

    public void Delete(string id)
    {
        lock (_gate) Items().Remove(id);
        TryDelete(Folder(id));
        Touched();
    }

    // Sounds written into the folder from elsewhere (a project package) are listed again.
    public void Reload()
    {
        lock (_gate) _items = null;
        Touched();
    }

    private void Touched() { Revision++; Changed?.Invoke(); }

    private static string Pretty(string name) => name.Replace('_', ' ').Replace('-', ' ').Trim() is { Length: > 0 } n ? char.ToUpperInvariant(n[0]) + n[1..] : "Sound";

    private static void TryDelete(string folder)
    {
        try { if (System.IO.Directory.Exists(folder)) System.IO.Directory.Delete(folder, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
