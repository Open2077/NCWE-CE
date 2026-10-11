// SPDX-License-Identifier: GPL-3.0-only
using NCWE.Core;
using NCWE.Export;
using NCWE.Formats.CustomSounds;
using NCWE.Formats.GameAudio;

namespace NCWE.Studio.Session;

// Custom sounds: the user's sound library (MP3, WAV, OGG, FLAC... made Open77-ready on import),
// placed as CustomSound elements and exported with an Open77 resource only.
internal sealed partial class StudioSession
{
    public CustomSoundLibrary CustomSounds { get; } = new(Path.Combine(StudioSettings.StateDirectory, "CustomSounds"));

    // The game's own sounds (events, banks, recordings), read from its audio archives the first
    // time something asks to hear one; null when the installation has no audio archives.
    private Task<GameSounds?>? _gameSounds;
    public string GameSoundsError { get; private set; } = "";
    public Task<GameSounds?> GameSoundsAsync()
    {
        if (_gameSounds is not null) return _gameSounds;
        string? game = Index?.GameDirectory;
        return _gameSounds = Task.Run(() =>
        {
            if (game is null) { GameSoundsError = Loc.T("Open a game installation first."); return null; }
            if (!GameSounds.Installed(game)) { GameSoundsError = Loc.T("The game's audio archives (audio_1_general, audio_2_soundbanks) are not installed."); return null; }
            try { return GameSounds.Open(game); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or DllNotFoundException)
            { GameSoundsError = Loc.F("The game's sounds could not be read: {0}", ex.Message); return (GameSounds?)null; }
        });
    }
    // The sounds once read (null before, or when they could not be).
    public GameSounds? GameSoundsReady => _gameSounds is { IsCompletedSuccessfully: true } task ? task.Result : null;
    public bool GameSoundsLoading => _gameSounds is { IsCompleted: false };

    // The game's emitters and the project's copies around a point, with the event each plays.
    public IEnumerable<(string Id, string Event, System.Numerics.Vector3 Position, float Radius)> SoundEmitters(System.Numerics.Vector3 around, float range)
    {
        float limit = range * range;
        var modified = Document.Project.Changes.Modified;
        foreach (var sector in Document.ResidentSectors.Values)
            foreach (var p in sector.Placements)
                if (p.Element is { Kind: ElementKind.Sound, SoundEvent.Length: > 0 } e && System.Numerics.Vector3.DistanceSquared(p.Transform.Position, around) <= limit && !(modified.TryGetValue(p.Source.ObjectId, out var edit) && edit.Deleted))
                    yield return (p.Source.ObjectId, e.SoundEvent, p.Transform.Position, e.Radius);
        foreach (var element in Elements.Values)
            if (element.Properties is { Kind: ElementKind.Sound, SoundEvent.Length: > 0 } e && System.Numerics.Vector3.DistanceSquared(element.Transform.Position, around) <= limit)
                yield return (element.Id, e.SoundEvent, element.Transform.Position, e.Radius);
    }

    // What a click places for a library sound: heard up to 15 m, full volume within 2 m, looped.
    public static ElementStamp SoundStamp(CustomSoundInfo info) =>
        new(info.Name, new ElementProperties(ElementKind.CustomSound) { Radius = 15, Audio = new SoundSettings { Sound = info.Id, MinDistance = 2 } });

    public int CustomSoundUses(string id) => Elements.Values.Count(e => e.Properties.Audio?.Sound == id);
    public int CustomSoundCount => Elements.Values.Count(e => e.Properties.Kind == ElementKind.CustomSound);

    // Anything an ArchiveXL archive carries (custom sounds aside).
    public bool HasWorldChanges
    {
        get
        {
            var changes = Document.Project.Changes;
            return changes.Modified.Count > 0 || changes.Added.Count > 0 || changes.Roads.Count > 0 || changes.Collisions.Count > 0
                || (changes.Traffic?.Count ?? 0) > 0 || changes.Elements.Values.Any(e => e.Properties.Kind != ElementKind.CustomSound);
        }
    }

    // The project's sounds for an Open77 export (throws when one is missing from the library or
    // more than 16 are synced).
    public Open77SoundSet SoundsForOpen77() => Open77Sounds.Collect(Document.Project, CustomSounds);

    // A sound removed from the library while placed: its elements stay (shown as missing) until deleted.
    public bool IsMissingSound(ElementProperties p) => p.Kind == ElementKind.CustomSound && (p.Audio is null || CustomSounds.Get(p.Audio.Sound) is null);
}
