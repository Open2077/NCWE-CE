// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using NCWE.Core;
using NCWE.Formats.GameAudio;
using NCWE.Studio.Session;

namespace NCWE.Studio;

// api.gamesounds.*: the game's own sound events for agents and tests: search them, see what one
// plays, play it in the Studio, hear the emitters around the camera, list the emitters loaded.
internal sealed partial class Automation
{
    private static object GameSoundInfo(GameSoundEvent e) => new
    {
        name = e.Name, playable = e.Playable, recordings = e.Variations, looping = e.Looping, min_seconds = e.MinDuration, max_seconds = e.MaxDuration,
        heard_up_to = e.MaxAttenuation, tags = e.Tags
    };

    private async Task<object> GameSoundsApi(StudioSession s, string op, JsonElement c)
    {
        if (op == "api.gamesounds.state")
        {
            var (playing, voices, seconds, hearing) = _window.GameAudioState();
            return new { ok = true, loaded = s.GameSoundsReady is not null, loading = s.GameSoundsLoading, error = s.GameSoundsError, playing, seconds, hearing, emitters_heard = voices };
        }
        if (op == "api.gamesounds.stop") { await _window.ListenGameEventAsync(null); return new { ok = true }; }
        if (op == "api.gamesounds.hear")
        {
            _window.SetHearGameSounds(!c.TryGetProperty("on", out var on) || on.GetBoolean());
            return new { ok = true };
        }
        var sounds = await s.GameSoundsAsync() ?? throw new InvalidOperationException(s.GameSoundsError);
        GameSoundEvent Event() => sounds.Find(Str(c, "event") ?? Str(c, "name") ?? throw new ArgumentException("event is required."))
            ?? throw new KeyNotFoundException("The game has no sound event named " + (Str(c, "event") ?? Str(c, "name")) + " (api.gamesounds.search finds them).");
        switch (op)
        {
            case "api.gamesounds.search":
            {
                string text = Str(c, "text") ?? ""; string? tag = Str(c, "tag");
                int limit = c.TryGetProperty("limit", out var l) ? Math.Clamp(l.GetInt32(), 1, 500) : 60;
                bool all = c.TryGetProperty("unplayable", out var u) && u.GetBoolean();
                var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var found = sounds.Events.Where(e => (all || e.Playable) && words.All(w => e.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || e.Tags.Any(t => t.Contains(w, StringComparison.OrdinalIgnoreCase)))
                    && (tag is null || e.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))).OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
                return new { ok = true, total = found.Count, events = found.Take(limit).Select(GameSoundInfo).ToArray(), hint = found.Count > limit ? "More match: narrow the text, or raise limit." : null };
            }
            case "api.gamesounds.info":
            {
                var e = Event(); var plan = sounds.Plan(e.Name, Random.Shared);
                return new { ok = true, sound = GameSoundInfo(e), one_play = plan?.Clips.Select(clip => new { media = clip.Media, gain = clip.Gain, starts_at = clip.PlayAt, loops = clip.Loop }).ToArray(), another_play_may_differ = plan?.Rerolls };
            }
            case "api.gamesounds.play":
            {
                var e = Event();
                if (!e.Playable) throw new InvalidOperationException(e.Name + " plays no recording (it only stops or sets something).");
                if (_window.GameAudioState().Event == e.Name) await _window.ListenGameEventAsync(null);
                float volume = c.TryGetProperty("volume", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number ? Math.Clamp(v.GetSingle(), 0, 1) : 1;
                await _window.ListenGameEventAsync(e.Name, volume: volume);
                return new { ok = true, sound = GameSoundInfo(e), hint = e.Looping ? "It loops: api.gamesounds.stop ends it." : null };
            }
            case "api.gamesounds.emitters":
            {
                // The emitters loaded around the camera with the event each plays.
                float range = c.TryGetProperty("range", out var r) ? Math.Clamp(r.GetSingle(), 1, 2000) : 150;
                var eye = Coordinates.ViewToGame(s.Camera.Eye);
                var list = s.SoundEmitters(eye, range).OrderBy(e => System.Numerics.Vector3.Distance(e.Position, eye)).Take(200).Select(e => new
                {
                    id = e.Id, plays = e.Event, playable = sounds.Find(e.Event)?.Playable ?? false, position = new[] { e.Position.X, e.Position.Y, e.Position.Z },
                    distance = System.Numerics.Vector3.Distance(e.Position, eye), radius = e.Radius
                }).ToArray();
                return new { ok = true, emitters = list };
            }
            case "api.gamesounds.decode":
            {
                // Diagnostic: decodes one play of the event and measures it (nothing is played).
                var e = Event(); var plan = sounds.Plan(e.Name, Random.Shared) ?? throw new InvalidOperationException(e.Name + " plays no recording.");
                var clips = await Task.Run(() => plan.Clips.Select(clip =>
                {
                    var pcm = sounds.Decode(clip.Media, 60); double peak = 0; foreach (float v in pcm.Samples) peak = Math.Max(peak, Math.Abs(v));
                    return new { media = clip.Media, seconds = pcm.Duration, channels = pcm.Channels, rate = pcm.Rate, peak };
                }).ToArray());
                return new { ok = true, sound = GameSoundInfo(e), clips };
            }
            default: throw new ArgumentException("Unknown command " + op);
        }
    }
}
