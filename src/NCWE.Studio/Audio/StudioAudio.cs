// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using System.Numerics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NCWE.Core;
using NCWE.Formats.CustomSounds;
using NCWE.Formats.GameAudio;

namespace NCWE.Studio.Audio;

// Sounds heard in the Studio.
// - Custom sounds: a one-shot preview (Add tab, Inspector) and the placed ones around the camera,
//   mixed the way Open77 mixes them (linear fall-off between the two distances, camera-relative
//   equal-power pan; no occlusion).
// - The game's own sounds, by event name (GameSounds): a preview in stereo, and the emitters
//   around the camera, each as a point source (the same fall-off, up to the distance the game
//   lists for the event). An event that loops over several recordings plays them one after another.
// Clips are decoded once on a worker to 48 kHz floats; one shared WASAPI output, created and
// released on MTA threads, mixes every voice.
internal sealed class StudioAudio(bool silent = false) : IDisposable
{
    public const int Rate = 48000;
    public const int MaxVoices = 16;       // what one Open77 resource plays at once
    public const int MaxGameSources = 12;  // game emitters heard at once
    private const long CacheBudget = 384L << 20;

    public sealed record Source(string Id, string Path, Vector3 Position, SoundSettings Settings, float MaxDistance);
    // A game emitter: the event it plays, heard up to MaxDistance.
    public sealed record GameSource(string Id, string Event, Vector3 Position, float MaxDistance);

    private sealed record Clip(float[] Samples, int Channels);

    private readonly MixingSampleProvider _mixer = new(WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2)) { ReadFully = true };
    private readonly ConcurrentDictionary<string, Task<Clip?>> _clips = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Key, Voice Voice)> _spatial = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EventPlayback> _gameSpatial = new(StringComparer.Ordinal);
    private Task<WasapiOut?>? _output;
    private Task? _pump;
    private Voice? _preview;
    private string? _previewPath;
    private EventPlayback? _previewEvent;
    private bool _disposed;

    public string Error { get; private set; } = "";
    // The file the preview is playing (null once it has ended).
    public string? Previewing => _preview is { Finished: false } ? _previewPath : null;
    // The game event the preview is playing (null once it has ended).
    public string? PreviewingEvent => _previewEvent is { Finished: false } playback ? playback.Event : null;
    public int SpatialVoices => _spatial.Count;
    public int GameVoices => _gameSpatial.Count;
    // Seconds of the event preview's first clip, once decoded (0 before).
    public double PreviewEventSeconds => _previewEvent?.Seconds ?? 0;

    // --- Preview ---------------------------------------------------------------------------------

    public void Preview(string path, float volume = 1)
    {
        StopPreview();
        if (!Start()) return;
        float gain = Math.Clamp(volume, 0, 1) * MathF.Sqrt(.5f);
        var voice = new Voice(FileClip(path), SoundPlayback.Loop, 0, 0, once: true, startDelay: 0) { TargetLeft = gain, TargetRight = gain };
        voice.Snap();
        _preview = voice; _previewPath = path;
        _mixer.AddMixerInput(voice);
    }

    // One play of a game event, in stereo (a looping event plays until stopped). False when it plays nothing.
    public bool PreviewEvent(GameSounds sounds, string eventName, float volume = 1)
    {
        StopPreview();
        if (sounds.Find(eventName) is not { Playable: true }) { Error = Loc.F("{0} plays no recording.", eventName); return false; }
        if (!Start()) return false;
        float gain = Math.Clamp(volume, 0, 1);
        var playback = new EventPlayback(this, sounds, eventName, stereo: true) { Left = gain, Right = gain };
        if (!playback.Begin()) { Error = Loc.F("{0} plays no recording.", eventName); return false; }
        _previewEvent = playback;
        return true;
    }

    public void StopPreview()
    {
        if (_preview is { } voice) _mixer.RemoveMixerInput(voice);
        _preview = null; _previewPath = null;
        _previewEvent?.Stop(); _previewEvent = null;
    }

    // --- Placed sounds ---------------------------------------------------------------------------

    // Called a few times a second: the nearest sources within reach play, the others stop.
    public void UpdateSpatial(IReadOnlyList<Source> sources, Vector3 listener, Vector3 listenerRight)
    {
        var wanted = sources.Select(s => (Source: s, Distance: Vector3.Distance(s.Position, listener)))
            .Where(x => x.Distance <= x.Source.MaxDistance + (_spatial.ContainsKey(x.Source.Id) ? 10 : 2))
            .OrderBy(x => x.Distance).Take(MaxVoices).ToDictionary(x => x.Source.Id, x => x.Source, StringComparer.Ordinal);
        foreach (var id in _spatial.Keys.Where(id => !wanted.ContainsKey(id)).ToList()) Remove(id);
        if (wanted.Count == 0) return;
        if (!Start()) return;
        foreach (var source in wanted.Values)
        {
            var s = source.Settings;
            string key = $"{source.Path}|{s.Playback}|{s.IntervalMin}|{s.IntervalMax}";
            if (_spatial.TryGetValue(source.Id, out var current) && current.Key != key) { Remove(source.Id); current = default; }
            var (left, right, _) = Open77Audio.Spatial(source.Position, listener, listenerRight, s.Volume, s.MinDistance, source.MaxDistance);
            if (current.Voice is null)
            {
                // Repeated sounds start after a random part of their pause, as in game.
                float delay = s.Playback == SoundPlayback.Interval ? Random.Shared.NextSingle() * s.IntervalMax : 0;
                var voice = new Voice(FileClip(source.Path), s.Playback, s.IntervalMin, s.IntervalMax, once: false, delay) { TargetLeft = left, TargetRight = right };
                voice.Snap();
                _spatial[source.Id] = (key, voice);
                _mixer.AddMixerInput(voice);
            }
            else { current.Voice.TargetLeft = left; current.Voice.TargetRight = right; }
        }
    }

    public void StopSpatial() { foreach (var id in _spatial.Keys.ToList()) Remove(id); }

    private void Remove(string id)
    {
        if (_spatial.Remove(id, out var entry)) _mixer.RemoveMixerInput(entry.Voice);
    }

    // The game's emitters around the camera: the nearest within reach play their event, each from
    // its place (full within a tenth of its reach, silent at its end).
    public void UpdateGameSpatial(GameSounds sounds, IReadOnlyList<GameSource> sources, Vector3 listener, Vector3 listenerRight)
    {
        var wanted = sources.Select(s => (Source: s, Distance: Vector3.Distance(s.Position, listener)))
            .Where(x => x.Distance <= x.Source.MaxDistance + (_gameSpatial.ContainsKey(x.Source.Id) ? 4 : 0))
            .OrderBy(x => x.Distance / Math.Max(1, x.Source.MaxDistance)).Take(MaxGameSources).ToDictionary(x => x.Source.Id, x => x.Source, StringComparer.Ordinal);
        foreach (var id in _gameSpatial.Keys.Where(id => !wanted.TryGetValue(id, out var s) || s.Event != _gameSpatial[id].Event).ToList())
        { _gameSpatial[id].Stop(); _gameSpatial.Remove(id); }
        if (wanted.Count == 0 || !Start()) return;
        foreach (var source in wanted.Values)
        {
            var (left, right, _) = Open77Audio.Spatial(source.Position, listener, listenerRight, 1, Math.Min(1, source.MaxDistance * .1f), source.MaxDistance);
            if (_gameSpatial.TryGetValue(source.Id, out var playback)) { playback.Left = left; playback.Right = right; playback.Apply(); continue; }
            playback = new EventPlayback(this, sounds, source.Event, stereo: false) { Left = left, Right = right, Repeat = true };
            if (playback.Begin()) _gameSpatial[source.Id] = playback;
        }
    }

    public void StopGameSpatial()
    {
        foreach (var playback in _gameSpatial.Values) playback.Stop();
        _gameSpatial.Clear();
        foreach (var playback in _oneShots) playback.Stop();
        _oneShots.Clear();
    }

    // One play of a game event from a place (what an effect plays as it runs): heard once, a
    // looping event included, with the gains of the moment it starts.
    private readonly List<EventPlayback> _oneShots = [];
    public void PlayAt(GameSounds sounds, string eventName, Vector3 position, Vector3 listener, Vector3 listenerRight, float maxDistance)
    {
        _oneShots.RemoveAll(p => p.Finished);
        if (_oneShots.Count >= 8) return;
        var (left, right, gain) = Open77Audio.Spatial(position, listener, listenerRight, 1, Math.Min(2, maxDistance * .1f), maxDistance);
        if (gain <= .002f || !Start()) return;
        var playback = new EventPlayback(this, sounds, eventName, stereo: false) { Left = left, Right = right, Once = true };
        if (playback.Begin()) _oneShots.Add(playback);
    }
    public int OneShotVoices => _oneShots.Count(p => !p.Finished);

    // Per UI tick: events that loop over several recordings start their next one.
    public void Tick()
    {
        _previewEvent?.Continue();
        foreach (var playback in _gameSpatial.Values) playback.Continue();
        if (_oneShots.Count > 0) _oneShots.RemoveAll(p => { if (!p.Finished) return false; p.Stop(); return true; });
    }

    // --- Clips and output ------------------------------------------------------------------------

    private Task<Clip?> FileClip(string path)
    {
        var file = new FileInfo(path);
        string key = $"{file.FullName}|{(file.Exists ? file.Length : 0)}|{(file.Exists ? file.LastWriteTimeUtc.Ticks : 0)}";
        return Cached(key, () => DecodeFile(path));
    }

    private Task<Clip?> GameClip(GameSounds sounds, uint media, bool stereo) => Cached($"game:{media}:{(stereo ? 2 : 1)}", () =>
    {
        try
        {
            var pcm = sounds.Decode(media, 300);
            pcm = stereo ? pcm.ToStereo() : pcm.ToMono();
            return new Clip(Resample(pcm.Samples, pcm.Channels, pcm.Rate), pcm.Channels);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException or DllNotFoundException or InvalidOperationException)
        { Error = Loc.F("Game sound {0}: {1}", media, ex.Message); return null; }
    });

    private Task<Clip?> Cached(string key, Func<Clip?> decode)
    {
        if (_clips.Count > 0 && _clips.Values.Where(t => t.IsCompletedSuccessfully).Sum(t => (long)(t.Result?.Samples.Length ?? 0) * 4) > CacheBudget)
            foreach (var old in _clips.Keys.Where(k => k != key).ToList()) _clips.TryRemove(old, out _);   // voices keep their arrays
        return _clips.GetOrAdd(key, _ => Task.Run(decode));
    }

    private Clip? DecodeFile(string path)
    {
        try
        {
            using var reader = AudioFiles.Open(path);
            var samples = AudioFiles.Mono(reader, Rate);
            var all = new List<float>((int)Math.Min(int.MaxValue / 2, reader.TotalTime.TotalSeconds * Rate + Rate));
            var buffer = new float[Rate];
            for (int read; (read = samples.Read(buffer, 0, buffer.Length)) > 0;) all.AddRange(buffer.AsSpan(0, read));
            return new(all.ToArray(), 1);
        }
        catch (Exception ex) { Error = $"{Path.GetFileName(path)}: {ex.Message}"; return null; }
    }

    // Linear interpolation to the mixer's rate (the game's music is 44.1 kHz; its effects already 48).
    private static float[] Resample(float[] samples, int channels, int rate)
    {
        if (rate == Rate || rate <= 0 || samples.Length == 0) return samples;
        int frames = samples.Length / channels, target = (int)((long)frames * Rate / rate);
        var result = new float[target * channels]; double step = (double)rate / Rate;
        for (int i = 0; i < target; i++)
        {
            double at = i * step; int a = Math.Min((int)at, frames - 1), b = Math.Min(a + 1, frames - 1); float t = (float)(at - a);
            for (int c = 0; c < channels; c++) result[i * channels + c] = samples[a * channels + c] + (samples[b * channels + c] - samples[a * channels + c]) * t;
        }
        return result;
    }

    // The output is created on a pool (MTA) thread: WASAPI objects made on the UI thread cannot be
    // used by the playback thread. Silent (off-screen instances, tests): the mix runs without a device.
    private bool Start()
    {
        if (_disposed) return false;
        if (silent) { _pump ??= Task.Run(Pump); return true; }
        _output ??= Task.Run(() =>
        {
            try
            {
                var output = new WasapiOut(AudioClientShareMode.Shared, true, 80);
                output.Init(_mixer);
                output.Play();
                return output;
            }
            catch (Exception ex) { Error = Loc.F("No audio output: {0}", ex.Message); return null; }
        });
        return !_output.IsCompleted || _output.Result is not null;
    }

    private async Task Pump()
    {
        var buffer = new float[Rate / 10 * 2];
        while (!_disposed) { _mixer.Read(buffer, 0, buffer.Length); await Task.Delay(100); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopPreview(); StopSpatial(); StopGameSpatial();
        if (_output is { } output) _ = output.ContinueWith(t => { try { t.Result?.Dispose(); } catch (Exception) { } }, TaskScheduler.Default);
    }

    // One play of a game event: the voices of its clips, and the next play when the event loops
    // over several recordings (or is asked to repeat).
    private sealed class EventPlayback(StudioAudio audio, GameSounds sounds, string eventName, bool stereo)
    {
        private readonly List<Voice> _voices = [];
        private bool _loop, _rerolls, _stopped;
        public string Event { get; } = eventName;
        public float Left, Right;
        // Emitters keep sounding: a one-shot event plays again after a pause.
        public bool Repeat { get; init; }
        // Heard once whatever the event says (an effect's sound).
        public bool Once { get; init; }
        public bool Finished => _stopped || (!Continuing && _voices.TrueForAll(v => v.Finished));
        private bool Continuing => !Once && (_loop || Repeat);
        public double Seconds => _voices.Count > 0 ? _voices[0].Seconds : 0;

        public bool Begin(float delay = 0)
        {
            if (sounds.Plan(Event, Random.Shared) is not { } plan) return false;
            _loop = plan.Loop; _rerolls = plan.Rerolls;
            // Seamless loops only where one play is all there is to hear; else each play ends and the next is drawn.
            bool seamless = !Once && plan.Loop && (!plan.Rerolls || plan.Clips.Any(c => c.Loop));
            foreach (var clip in plan.Clips)
            {
                var voice = new Voice(audio.GameClip(sounds, clip.Media, stereo), SoundPlayback.Loop, 0, 0, once: !seamless, (float)clip.PlayAt + delay)
                { Gain = clip.Gain, TrimStart = (int)(clip.TrimStart * Rate), TrimEnd = (int)(clip.TrimEnd * Rate) };
                voice.TargetLeft = Left * voice.Gain; voice.TargetRight = Right * voice.Gain; voice.Snap();
                _voices.Add(voice); audio._mixer.AddMixerInput(voice);
            }
            return _voices.Count > 0;
        }

        public void Apply() { foreach (var v in _voices) { v.TargetLeft = Left * v.Gain; v.TargetRight = Right * v.Gain; } }

        // The next play, once this one has ended.
        public void Continue()
        {
            if (_stopped || !Continuing || _voices.Count == 0 || !_voices.TrueForAll(v => v.Finished)) return;
            foreach (var v in _voices) audio._mixer.RemoveMixerInput(v);
            _voices.Clear();
            // A one-shot that repeats (an emitter's) waits a little, as scattered ambience does.
            if (!Begin(_loop ? 0 : 1.5f + Random.Shared.NextSingle() * 4)) _stopped = true;
        }

        public void Stop()
        {
            _stopped = true;
            foreach (var v in _voices) audio._mixer.RemoveMixerInput(v);
            _voices.Clear();
        }
    }

    // One sound: a mono clip to stereo, or a stereo clip, with gains eased toward their targets (no
    // clicks while the camera moves). Loops, plays once (preview), or plays then waits a random pause.
    private sealed class Voice(Task<Clip?> load, SoundPlayback playback, float pauseMin, float pauseMax, bool once, float startDelay) : ISampleProvider
    {
        private float[]? _clip;
        private int _channels = 1, _position, _end, _silence = (int)(startDelay * Rate);
        private float _left, _right;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);
        public volatile float TargetLeft, TargetRight;
        public volatile bool Finished;
        public float Gain = 1;
        // Frames skipped at the start and left out at the end (music clips).
        public int TrimStart, TrimEnd;
        public double Seconds => _clip is null ? 0 : (double)(_end - TrimStart) / Rate;

        public void Snap() { _left = TargetLeft; _right = TargetRight; }

        public int Read(float[] buffer, int offset, int count)
        {
            if (Finished) return 0;
            if (_clip is null)
            {
                if (load.IsCompletedSuccessfully && load.Result is { } clip)
                {
                    _clip = clip.Samples; _channels = clip.Channels;
                    int frames = clip.Samples.Length / clip.Channels;
                    _end = Math.Max(0, frames - Math.Max(0, TrimEnd)); _position = Math.Clamp(TrimStart, 0, _end);
                }
                else
                {
                    if (load.IsCompleted) { Finished = true; return 0; }   // could not be decoded
                    Array.Clear(buffer, offset, count);
                    return count;
                }
            }
            int frameCount = count / 2;
            float targetLeft = TargetLeft, targetRight = TargetRight;
            bool stereo = _channels == 2;
            for (int i = 0; i < frameCount; i++)
            {
                _left += (targetLeft - _left) * .001f; _right += (targetRight - _right) * .001f;
                float l = 0, r = 0;
                if (_silence > 0) _silence--;
                else
                {
                    if (_position >= _end)
                    {
                        if (once || _end <= TrimStart) { Finished = true; return i * 2; }   // fewer samples: the mixer drops the voice
                        _position = Math.Clamp(TrimStart, 0, _end);
                        if (playback == SoundPlayback.Interval) { _silence = (int)((pauseMin + Random.Shared.NextSingle() * (pauseMax - pauseMin)) * Rate); goto write; }
                    }
                    if (stereo) { l = _clip[2 * _position]; r = _clip[2 * _position + 1]; } else l = r = _clip[_position];
                    _position++;
                }
            write:
                buffer[offset + 2 * i] = l * _left;
                buffer[offset + 2 * i + 1] = r * _right;
            }
            return count;
        }
    }
}
