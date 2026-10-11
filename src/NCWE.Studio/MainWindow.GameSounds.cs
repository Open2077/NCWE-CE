// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Xaml;
using NCWE.Core;
using NCWE.Formats.GameAudio;
using NCWE.Studio.Audio;
using NCWE.Studio.Session;

namespace NCWE.Studio;

// The game's own sounds in the editor: what a sound emitter plays, to listen to (Inspector, Add ▸
// Sounds, the library), and the emitters around the camera heard from their places (World ▸ Hear
// the game's sounds). Events are played from the game's audio archives as they are; what the
// game adds by situation (rooms, reverb, mixing states) is not reproduced.
public sealed partial class MainWindow
{
    private bool _hearGameSounds;
    private long _gameSoundsUpdated;
    private readonly List<StudioAudio.GameSource> _gameSources = [];

    // Plays a game event (null, or the one playing: stops). The first call reads the game's sound banks.
    // restart: the event is played from its start even when it is already playing (an effect's sound).
    // volume: 0 to 1.
    internal async Task ListenGameEventAsync(string? eventName, bool restart = false, float volume = 1)
    {
        if (Session is not { } s) return;
        if (string.IsNullOrEmpty(eventName) || (!restart && _audioEngine?.PreviewingEvent == eventName)) { _audioEngine?.StopPreview(); return; }
        if (s.GameSoundsReady is null && !s.GameSoundsLoading) s.Status = Loc.T("Reading the game's sounds…");
        if (await s.GameSoundsAsync() is not { } sounds) { s.Error = s.GameSoundsError; return; }
        if (sounds.Find(eventName) is not { } info) { s.Error = Loc.F("The game has no sound event named {0}.", eventName); return; }
        if (!Audio.PreviewEvent(sounds, eventName, volume)) { s.Error = Audio.Error; return; }
        s.Status = Loc.F("Playing {0}: {1}", eventName, GameSoundDetail(info));
    }

    internal static string GameSoundDetail(GameSoundEvent e) => !e.Playable ? Loc.T("plays no recording (it only stops or sets something)")
        : string.Join(" · ", new[]
        {
            e.Looping ? Loc.T("loops") : e.MaxDuration > 0 ? (e.MaxDuration - e.MinDuration < .05f ? Loc.F("{0:0.0} s", e.MaxDuration) : Loc.F("{0:0.0}-{1:0.0} s", e.MinDuration, e.MaxDuration)) : Loc.T("one shot"),
            e.Variations == 1 ? Loc.T("1 recording") : Loc.F("{0} recordings", e.Variations),
            e.MaxAttenuation > 0 ? Loc.F("heard up to {0:0} m", e.MaxAttenuation) : Loc.T("not placed in space")
        });

    // --- Inspector: what a sound emitter plays ------------------------------------------------------

    private string _shownSoundEvent = "\0";
    private void ShowSoundEvent(StudioSession s, ElementProperties p)
    {
        string name = p.SoundEvent;
        SoundEventPlayButton.IsEnabled = name.Length > 0;
        if (name == _shownSoundEvent && (name.Length == 0 || (s.GameSoundsReady is null) == (SoundEventDetail.Text.Length == 0))) return;
        _shownSoundEvent = name;
        SoundEventName.Text = name.Length > 0 ? name : Loc.T("nothing while active");
        SoundEventDetail.Text = name.Length == 0 ? "" : s.GameSoundsReady?.Find(name) is { } info ? GameSoundDetail(info) : "";
        // The detail needs the banks: read in the background the first time an emitter is shown.
        if (name.Length > 0 && s.GameSoundsReady is null && !s.GameSoundsLoading && string.IsNullOrEmpty(s.GameSoundsError)) _ = s.GameSoundsAsync();
    }

    private void OnSoundEventPlay(object sender, RoutedEventArgs e)
    {
        if (Session?.SelectedElementProperties is { Kind: ElementKind.Sound, SoundEvent: { Length: > 0 } name }) _ = ListenGameEventAsync(name);
    }

    // --- Add ▸ Sounds: a game emitter chosen in the list --------------------------------------------

    private string? ChosenGameSoundEvent() =>
        _addCategory == "Sound" && ElementCatalogList.SelectedItem is ElementResourceItem { Kind: ElementKind.Sound, Template.Element.SoundEvent: { Length: > 0 } name } ? name : null;

    // --- Hearing the game's emitters ----------------------------------------------------------------

    private void OnHearGameSoundsMenu(object sender, RoutedEventArgs e) => SetHearGameSounds(HearGameSoundsMenu.IsChecked);

    internal void SetHearGameSounds(bool on)
    {
        _hearGameSounds = on;
        if (HearGameSoundsMenu.IsChecked != on) HearGameSoundsMenu.IsChecked = on;
        if (!on) _audioEngine?.StopGameSpatial();
        if (Session is not { } s) return;
        s.Status = on ? Loc.T("Hearing the game's sound emitters around the camera (camera = listener).") : Loc.T("Game sounds muted.");
        if (on && s.GameSoundsReady is null) _ = s.GameSoundsAsync().ContinueWith(t => { if (t.Result is null) DispatcherQueue.Post(() => { s.Error = s.GameSoundsError; SetHearGameSounds(false); }); }, TaskScheduler.Default);
    }

    // Per UI tick: the play buttons, looping events, and the emitters around the camera.
    private void UpdateGameSounds(StudioSession s)
    {
        string? playing = _audioEngine?.PreviewingEvent;
        SoundEventPlayIcon.Glyph = playing is not null && s.SelectedElementProperties is { Kind: ElementKind.Sound } p && p.SoundEvent == playing ? StopGlyph : PlayGlyph;
        if (s.SelectedElementProperties is { Kind: ElementKind.Sound } shown) ShowSoundEvent(s, shown);
        if (s.EffectSound is null) ConnectEffectSounds(s);
        _audioEngine?.Tick();
        if (!_hearGameSounds || s.GameSoundsReady is not { } sounds) return;
        var listener = Coordinates.ViewToGame(s.Camera.Eye);
        // The emitters in reach are listed four times a second; their gains follow the camera every tick.
        if (Stopwatch.GetElapsedTime(_gameSoundsUpdated).TotalMilliseconds > 250)
        {
            _gameSoundsUpdated = Stopwatch.GetTimestamp();
            _gameSources.Clear();
            foreach (var (id, name, position, radius) in s.SoundEmitters(listener, 160))
            {
                if (sounds.Find(name) is not { Playable: true } info) continue;
                float reach = Math.Clamp(info.MaxAttenuation > 0 ? info.MaxAttenuation : radius, 2, 150);
                _gameSources.Add(new(id, name, position, reach));
            }
        }
        var forward = Coordinates.ViewToGame(s.Camera.Forward);
        var right = Vector3.Cross(forward, Vector3.UnitZ);
        right = right.LengthSquared() > 1e-6f ? Vector3.Normalize(right) : Vector3.UnitX;
        Audio.UpdateGameSpatial(sounds, _gameSources, listener, right);
    }

    // For the library window: the game's sounds (read on first use), and what the preview plays.
    internal Task<GameSounds?> GameSoundsAsync() => Session is { } s ? s.GameSoundsAsync() : Task.FromResult<GameSounds?>(null);
    internal string GameSoundsError => Session?.GameSoundsError is { Length: > 0 } error ? error : Loc.T("The game's sounds are not available.");
    internal string? PlayingGameEvent => _audioEngine?.PreviewingEvent;

    internal (string? Event, int Voices, double Seconds, bool Hearing) GameAudioState() =>
        (_audioEngine?.PreviewingEvent, _audioEngine?.GameVoices ?? 0, _audioEngine?.PreviewEventSeconds ?? 0, _hearGameSounds);
}
