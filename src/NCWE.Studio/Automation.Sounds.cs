// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using NCWE.Core;
using NCWE.Studio.Session;

namespace NCWE.Studio;

// api.sounds.*: the custom sound library for agents and tests (import, list, rename, delete,
// preview). Placing one is api.elements.add with kind "customsound" and "sound": id or name.
internal sealed partial class Automation
{
    private static object SoundInfo(StudioSession s, CustomSoundInfo info) => new
    {
        id = info.Id, name = info.Name, file = info.File, source = info.SourceFile, duration = info.Duration, sample_rate = info.SampleRate, channels = info.Channels,
        kb = info.Bytes / 1024, converted = info.Converted, warnings = info.Warnings, published = info.PublishedPath, uses_in_project = s.CustomSoundUses(info.Id)
    };

    private async Task<object> Sounds(StudioSession s, string op, JsonElement c)
    {
        var library = s.CustomSounds;
        CustomSoundInfo Item() => library.Get(Str(c, "id") ?? throw new ArgumentException("id is required.")) ?? throw new KeyNotFoundException("No custom sound " + Str(c, "id") + " (api.sounds.list lists them).");
        switch (op)
        {
            case "api.sounds.list":
                return new { ok = true, folder = library.Directory, sounds = library.List().Select(i => SoundInfo(s, i)).ToArray() };
            case "api.sounds.import":
            {
                string path = Str(c, "path") ?? throw new ArgumentException("path is required.");
                var info = await Task.Run(() => library.Import(path, Str(c, "name")));
                _window.CustomSoundsChanged();
                return new { ok = true, sound = SoundInfo(s, info), hint = "Place it with api.elements.add: kind customsound, sound = its id." };
            }
            case "api.sounds.rename":
            {
                var info = library.Rename(Item().Id, Str(c, "name") ?? throw new ArgumentException("name is required."));
                _window.CustomSoundsChanged();
                return new { ok = true, sound = SoundInfo(s, info) };
            }
            case "api.sounds.delete":
            {
                var info = Item();
                library.Delete(info.Id);
                _window.CustomSoundsChanged();
                return new { ok = true, deleted = info.Id };
            }
            case "api.sounds.preview":
            {
                // Plays the sound once in the Studio (stop: true stops it).
                if (c.TryGetProperty("stop", out var stop) && stop.GetBoolean()) { _window.PreviewSound(null); return new { ok = true }; }
                _window.PreviewSound(Item());
                return new { ok = true };
            }
            case "api.sounds.state":
            {
                var (previewing, voices, error) = _window.AudioState();
                return new { ok = true, previewing, spatial_voices = voices, error };
            }
            case "api.sounds.hear":
            {
                // Plays the placed sounds around the camera (World ▸ Hear custom sounds).
                _window.SetHearSounds(!c.TryGetProperty("on", out var on) || on.GetBoolean());
                return new { ok = true };
            }
            case "api.sounds.open77":
            {
                // What an Open77 export would write for the project's sounds (scripts and manifest block).
                var set = s.SoundsForOpen77();
                return new
                {
                    ok = true, files = set.Files.Select(f => f.PublishedPath).ToArray(), emitters = set.Emitters.Count, synced = set.Synced,
                    manifest = set.IsEmpty ? "" : NCWE.Export.Open77Sounds.ManifestBlock(set), client = NCWE.Export.Open77Sounds.Client(set, s.Document.Project.Name),
                    server = set.Synced > 0 ? NCWE.Export.Open77Sounds.Server(set, s.Document.Project.Name) : null
                };
            }
            default: throw new ArgumentException("Unknown command " + op);
        }
    }
}
