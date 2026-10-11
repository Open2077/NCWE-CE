// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using NCWE.Core;
using NCWE.Core.Fx;
using NCWE.Render;
using NCWE.Studio.Viewport;

namespace NCWE.Studio.Session;

// Visual effects played in the 3D view: the particle systems and effects of the game's nodes
// and of the project's elements around the camera run as the game's files describe them (read
// by ParticleReader, simulated by NCWE.Core.Fx, drawn by the renderer's particle pass with ports
// of the game's shaders), plus one effect shown on demand in front of the camera (a preview from
// the library or the Add panel). Each is read on a worker the first time it comes within reach.
internal sealed partial class StudioSession
{
    public bool PlayEffects { get => Settings.PlayEffects; set => Settings.PlayEffects = value; }
    // Metres from the camera within which effects play (each also has its own hide distance).
    public float EffectRange { get; set; } = 70;
    private const int MaximumEffects = 40, MaximumParticles = 24000;

    private sealed class PlayingEffect
    {
        public required string Id, Resource;
        public required Task<object?> Load;
        public FxPlayback? Playback;
        public Vector3 Position; public Quaternion Rotation = Quaternion.Identity;
        public float HideDistance = 50;
        public bool Wanted, Failed;
        // One batch per emitter; mesh particles: one per mesh of the emitter.
        public readonly Dictionary<(FxEmitterInstance Emitter, int Mesh), ParticleBatch> Batches = [];
    }
    private readonly Dictionary<string, PlayingEffect> _effects = new(StringComparer.Ordinal);
    private readonly List<ParticleBatch> _effectBatches = [];
    private readonly List<(float Distance, string Id, string Resource, Vector3 Position, Quaternion Rotation)> _effectCandidates = [];
    private double _effectsScanned = -1;
    private PlayingEffect? _effectPreview;
    // Called with each sound event an effect plays (the window plays it when sounds are heard).
    public Action<string, Vector3>? EffectSound { get; set; }

    public int EffectsPlaying => _effects.Values.Count(e => e.Playback is not null) + (_effectPreview?.Playback is not null ? 1 : 0);
    public int EffectParticles { get; private set; }
    public string? EffectPreviewPath => _effectPreview?.Resource;

    private static uint SeedOf(string id) { uint h = 2166136261; foreach (char c in id) { h ^= c; h *= 16777619; } return h | 1; }

    private Task<object?> LoadEffect(string resource)
    {
        var index = Index;
        return Task.Run<object?>(() =>
        {
            if (index is null) return null;
            if (resource.EndsWith(".effect", StringComparison.OrdinalIgnoreCase))
            {
                var effect = index.ReadEffect(resource);
                // Its systems are read now, so that playing never waits on the archives.
                if (effect is not null) foreach (var item in effect.Particles) index.ReadParticleSystem(item.System);
                return effect;
            }
            return index.ReadParticleSystem(resource);
        });
    }

    // Shows one effect or particle system in front of the camera until another is asked for (null: none).
    public void PreviewEffect(string? resource, Vector3? gamePosition = null)
    {
        if (string.IsNullOrEmpty(resource) || Index is null) { _effectPreview = null; return; }
        var forward = Coordinates.ViewToGame(Camera.Forward);
        var position = gamePosition ?? Coordinates.ViewToGame(Camera.Eye) + forward * 6 - new Vector3(0, 0, .8f);
        _effectPreview = new PlayingEffect { Id = "preview", Resource = resource, Load = LoadEffect(resource), Position = position, Wanted = true, HideDistance = 1e6f };
    }

    // Plays an effect of the view again from its start (the Inspector's button).
    public void RestartEffect(string id)
    {
        if (_effects.TryGetValue(id, out var playing)) { playing.Playback?.Restart(); playing.Batches.Clear(); }
    }

    // Once per frame, before the scene is drawn.
    public void UpdateEffects(float dt, double now)
    {
        if (!PlayEffects || Index is null)
        {
            if (_effects.Count > 0 || _effectBatches.Count > 0) { _effects.Clear(); _effectBatches.Clear(); }
            if (_effectPreview is null) { _renderer.Particles = []; EffectParticles = 0; return; }
        }
        var eyeView = Camera.Eye; var eye = Coordinates.ViewToGame(eyeView);
        if (PlayEffects && now - _effectsScanned > .25) { _effectsScanned = now; ScanEffects(eyeView); }
        _effectBatches.Clear(); int particles = 0;
        foreach (var playing in _effects.Values) StepEffect(playing, dt, eye, ref particles);
        if (_effectPreview is { } preview) StepEffect(preview, dt, eye, ref particles);
        EffectParticles = particles;
        _renderer.Particles = _effectBatches;
    }

    // The effects within reach, nearest first, up to the budget; those that left are dropped.
    private void ScanEffects(Vector3 eyeView)
    {
        _effectCandidates.Clear();
        // Never beyond the draw distance (World menu): nothing else is drawn there.
        float range = _renderer.DrawDistance > 0 ? MathF.Min(EffectRange, _renderer.DrawDistance) : EffectRange;
        var modified = Document.Project.Changes.Modified;
        foreach (var entry in _sectorRender.Values)
            foreach (var m in entry.Markers)
            {
                if (m.Properties is not { Kind: ElementKind.Particle or ElementKind.Effect, Resource.Length: > 0 } p) continue;
                var position = m.Position; var rotation = m.Rotation;
                if (_previews is not null && _previews.TryGetValue(m.Id, out var moved)) { position = Coordinates.GameToView(moved.Position); rotation = moved.Rotation; }
                float distance = Vector3.Distance(position, eyeView);
                if (distance > range || !IsShown(m.Id) || (modified.TryGetValue(m.Id, out var edit) && edit.Deleted)) continue;
                _effectCandidates.Add((distance, m.Id, p.Resource, Coordinates.ViewToGame(position), rotation));
            }
        foreach (var element in Elements.Values)
        {
            if (element.Properties is not { Kind: ElementKind.Particle or ElementKind.Effect, Resource.Length: > 0 } p || !IsShown(element.Id)) continue;
            var t = _previews is not null && _previews.TryGetValue(element.Id, out var moved) ? moved : element.Transform;
            float distance = Vector3.Distance(Coordinates.GameToView(t.Position), eyeView);
            if (distance > range) continue;
            _effectCandidates.Add((distance, element.Id, p.Resource, t.Position, t.Rotation));
        }
        _effectCandidates.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        foreach (var playing in _effects.Values) playing.Wanted = false;
        int taken = 0;
        foreach (var (distance, id, resource, position, rotation) in _effectCandidates)
        {
            if (taken >= MaximumEffects) break;
            if (_effects.TryGetValue(id, out var playing) && playing.Resource != resource) { _effects.Remove(id); playing = null; }
            if (playing is null) _effects[id] = playing = new PlayingEffect { Id = id, Resource = resource, Load = LoadEffect(resource) };
            if (playing.Failed || distance > playing.HideDistance) continue;
            playing.Wanted = true; playing.Position = position; playing.Rotation = rotation == default ? Quaternion.Identity : rotation;
            taken++;
        }
        foreach (var id in _effects.Where(e => !e.Value.Wanted).Select(e => e.Key).ToList()) _effects.Remove(id);
    }

    private void StepEffect(PlayingEffect playing, float dt, Vector3 eye, ref int particles)
    {
        if (playing.Playback is null)
        {
            if (!playing.Load.IsCompleted) return;
            var index = Index;
            switch (playing.Load.IsCompletedSuccessfully ? playing.Load.Result : null)
            {
                case FxSystem system when system.Emitters.Length > 0:
                    playing.Playback = new FxPlayback(system, SeedOf(playing.Id)); playing.HideDistance = MathF.Max(system.AutoHideDistance, 8); break;
                case FxEffect effect when effect.Particles.Length > 0 && index is not null:
                    playing.Playback = new FxPlayback(effect, path => index.ReadParticleSystem(path), SeedOf(playing.Id));
                    playing.HideDistance = MathF.Max(8, effect.Particles.Max(p => index.ReadParticleSystem(p.System)?.AutoHideDistance ?? 0)); break;
                default: playing.Failed = true; return;
            }
            if (ReferenceEquals(playing, _effectPreview)) playing.HideDistance = 1e6f;
        }
        var toWorld = Matrix4x4.CreateFromQuaternion(playing.Rotation) * Matrix4x4.CreateTranslation(playing.Position);
        var at = playing.Position;
        playing.Playback.Step(dt, toWorld, eye, EffectSound is { } sound ? name => sound(name, at) : null);
        FxBatches.Collect(playing.Playback, playing.Batches, toWorld, _effectBatches, ref particles, MaximumParticles);
    }

    // Diagnostics (automation).
    public object EffectsState() => new
    {
        on = PlayEffects, range = EffectRange, playing = EffectsPlaying, loading = _effects.Values.Count(e => e.Playback is null && !e.Failed), unreadable = _effects.Values.Count(e => e.Failed),
        particles = EffectParticles, preview = _effectPreview?.Resource,
        effects = _effects.Values.Where(e => e.Playback is not null).OrderBy(e => Vector3.Distance(e.Position, Coordinates.ViewToGame(Camera.Eye))).Take(24).Select(e => new
        {
            id = e.Id, resource = e.Resource, particles = e.Playback!.ParticleCount, time = e.Playback.Time, position = new[] { e.Position.X, e.Position.Y, e.Position.Z },
            drawn = e.Batches.Values.Where(b => _effectBatches.Contains(b)).Select(b => $"{b.Emitter.Name}:{b.Emitter.Drawer}/{b.Emitter.Material.Shader}x{b.Count}").ToArray()
        }).ToArray(),
        renderer = _renderer.ParticleDiagnostic()
    };
}
