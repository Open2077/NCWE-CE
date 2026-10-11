// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;

namespace NCWE.Core.Fx;

// A light an effect shines at this moment (game space offset from the effect, linear color).
public readonly record struct FxLightState(Vector3 Offset, Vector3 Color, float Radius, float Intensity);

// An effect or a bare particle system being played at a place: the effect's time line starts and
// stops its particle systems, scales what they emit over each item's time, names the sounds to
// play as it reaches them, and repeats its loop while it is kept alive, as the game does for the
// effects placed in the world. A system that ends by itself (an explosion) starts again after a
// pause, so that it can be watched.
public sealed class FxPlayback
{
    private readonly FxEffect? _effect;
    private readonly Func<string, FxSystem?> _systems;
    private readonly FxSystemInstance?[] _instances;
    private readonly bool[] _soundPlayed;
    private readonly uint _seed;
    private float _pause;
    private int _plays;

    public float Time { get; private set; }
    public string Path { get; }
    // Seconds between two plays of an effect that ends by itself.
    public float ReplayPause { get; set; } = 1.5f;
    public bool Replay { get; set; } = true;
    public List<FxLightState> Lights { get; } = [];

    public FxPlayback(FxSystem system, uint seed = 1)
    {
        Path = system.Path; _seed = seed; _systems = _ => system;
        _instances = [new FxSystemInstance(system, seed)]; _soundPlayed = [];
        _bare = system;
    }
    private readonly FxSystem? _bare;

    public FxPlayback(FxEffect effect, Func<string, FxSystem?> systems, uint seed = 1)
    {
        Path = effect.Path; _effect = effect; _systems = systems; _seed = seed;
        _instances = new FxSystemInstance?[effect.Particles.Length]; _soundPlayed = new bool[effect.Sounds.Length];
    }

    public IEnumerable<FxSystemInstance> Systems { get { foreach (var i in _instances) if (i is not null) yield return i; foreach (var i in _retired) yield return i; } }
    public int ParticleCount { get { int n = 0; foreach (var i in Systems) n += i.ParticleCount; return n; } }
    public bool Idle => _pause > 0;

    // sound: called with each event the effect plays as its time passes it.
    public void Step(float dt, Matrix4x4 toWorld, Vector3 camera, Action<string>? sound = null)
    {
        dt = Math.Clamp(dt, 0, .25f);
        if (_pause > 0) { _pause -= dt; if (_pause > 0) return; Restart(); }
        Lights.Clear();
        if (_effect is null)
        {
            var only = _instances[0]!;
            if (_plays == 0 && Time == 0 && _bare!.Prewarm > 0) only.Prewarm(_bare.Prewarm, FxContext.At(toWorld, camera));
            only.Step(dt, FxContext.At(toWorld, camera)); Time += dt;
            if (only.Finished && Replay) _pause = ReplayPause;
            return;
        }
        float before = Time; Time += dt;
        // The loop's part plays again while the effect is kept alive: back to its start before
        // anything is asked whether it is still on.
        if (_effect.Loop is { } loop && Time >= loop.End)
        {
            float length = MathF.Max(loop.End - loop.Start, 1e-3f);
            Time = loop.Start + (Time - loop.End) % length; before = loop.Start - 1e-4f;
            for (int i = 0; i < _effect.Sounds.Length; i++) if (_effect.Sounds[i].Begin >= loop.Start) _soundPlayed[i] = false;
            for (int i = 0; i < _instances.Length; i++)
            {
                // A system that stopped inside the loop starts anew; what it left burns out apart.
                if (_instances[i] is not { } ended || ended.Emitters.Any(e => e.Emitting)) continue;
                if (!ended.Finished && _retired.Count < 16) _retired.Add(ended);
                _instances[i] = null;
            }
        }
        for (int i = 0; i < _effect.Sounds.Length; i++)
            if (!_soundPlayed[i] && _effect.Sounds[i].Begin <= Time && (_effect.Sounds[i].Begin > before || before <= 0)) { _soundPlayed[i] = true; sound?.Invoke(_effect.Sounds[i].Event); }
        bool anyAlive = false;
        for (int i = 0; i < _instances.Length; i++)
        {
            var item = _effect.Particles[i];
            float end = item.Begin + item.Duration;
            bool active = Time >= item.Begin && (Time < end || item.Duration <= 0 && Time < _effect.Length);
            if (active && _instances[i] is null && _systems(item.System) is { } system)
            {
                var created = new FxSystemInstance(system, _seed + (uint)i * 7919 + (uint)_plays * 104729);
                if (system.Prewarm > 0) created.Prewarm(system.Prewarm, FxContext.At(toWorld, camera));
                _instances[i] = created;
            }
            if (_instances[i] is not { } instance) { anyAlive |= Time < item.Begin; continue; }
            float t = item.Duration > 1e-4f ? Math.Clamp((Time - item.Begin) / item.Duration, 0, 1) : 0;
            var context = new FxContext
            {
                ToWorld = toWorld, Camera = camera, Emit = active, Emission = MathF.Max(0, item.EmissionScale.Float(t, .5f)), Alpha = MathF.Max(0, item.Alpha.Float(t, .5f)),
                Size = MathF.Max(0, item.Size.Float(t, .5f)), Velocity = item.Velocity.Float(t, .5f)
            };
            if (!active && instance.Emitters.Any(e => e.Emitting)) instance.Stop();
            instance.Step(dt, context);
            anyAlive |= active || !instance.Finished;
        }
        for (int i = _retired.Count - 1; i >= 0; i--)
        {
            _retired[i].Step(dt, FxContext.At(toWorld, camera) with { Emit = false });
            if (_retired[i].Finished) _retired.RemoveAt(i); else anyAlive = true;
        }
        foreach (var light in _effect.Lights)
        {
            if (Time < light.Begin || Time >= light.Begin + light.Duration) continue;
            float t = light.Duration > 1e-4f ? (Time - light.Begin) / light.Duration : 0;
            float radius = light.Radius.Float(t, .5f), intensity = light.Intensity.Float(t, .5f);
            if (radius > 0 && intensity > 0) Lights.Add(new(light.Offset, light.Color, radius, intensity * MathF.Pow(2, light.Ev)));
        }
        if (_effect.Loop is null && !anyAlive && Time >= _effect.Length && Replay) _pause = ReplayPause;
    }

    private readonly List<FxSystemInstance> _retired = [];

    public bool Finished => _effect is null ? _instances[0]!.Finished : _effect.Loop is null && Time >= _effect.Length && _instances.All(i => i is null || i.Finished);

    public void Restart()
    {
        _plays++; Time = 0; _pause = 0;
        Array.Clear(_soundPlayed);
        if (_effect is null) _instances[0] = new FxSystemInstance(_bare!, _seed + (uint)_plays * 104729);
        else { Array.Clear(_instances); _retired.Clear(); }
    }
}
