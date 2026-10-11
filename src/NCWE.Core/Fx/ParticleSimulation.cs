// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;

namespace NCWE.Core.Fx;

// One particle. Positions and velocities are in the emitter's simulation space: its own frame
// when it keeps its particles local, game space (metres, Z up) otherwise. Rotations in radians.
public struct FxParticle
{
    public Vector3 Position, BaseVelocity, Velocity;
    public float Age, Life;
    public Vector3 BaseSize, Size;
    public float BaseRotation, Rotation, BaseRotationRate, RotationRate, Spin;
    public Vector3 BaseRotation3D, Rotation3D, RotationRate3D, Spin3D;
    public Vector3 BaseColor, Color;
    public float BaseAlpha, Alpha;
    public float InitialFrame, Frame;
    public uint Seed;
    public Vector3 Turbulence;
    public float TurbulenceLeft;
    public int Mesh;
    // Noise that moves the position without changing where the particle is headed.
    public Vector3 Offset;
}

// What an emitter is given every step: where it stands, where the camera is (distance fades),
// and what the effect that plays it asks for (emission, opacity, size and speed factors).
public struct FxContext
{
    public Matrix4x4 ToWorld;      // rotation and translation, game space
    public Vector3 Camera;         // game space
    public float Emission, Alpha, Size, Velocity;
    public bool Emit;
    public static FxContext At(Matrix4x4 toWorld, Vector3 camera) => new() { ToWorld = toWorld, Camera = camera, Emission = 1, Alpha = 1, Size = 1, Velocity = 1, Emit = true };
}

// An emitter being played: its particles, born at the emitter's rate and bursts, shaped by its
// initializers, moved and aged by its modificators (REDengine's particle modules: the game's
// own files keep them next to the cooked data). What stands in for the engine's randomness is a
// hash of each particle's seed, so a system plays the same way twice.
public sealed class FxEmitterInstance
{
    private const float Turn = MathF.PI * 2;
    public FxEmitter Emitter { get; }
    public FxParticle[] Particles { get; }
    public int Count { get; private set; }
    // No particle left and none to come.
    public bool Finished => _ended && Count == 0;
    public bool Emitting => !_ended;

    private readonly FxModule[] _initializers, _modificators;
    private readonly bool _local;
    private uint _random;
    private float _time, _delay, _duration, _debt;
    private int _loop;
    private bool _ended, _started;
    private readonly float[] _burstNext;

    public FxEmitterInstance(FxEmitter emitter, uint seed)
    {
        Emitter = emitter;
        Particles = new FxParticle[Math.Clamp(emitter.MaxParticles, 1, 4096)];
        _initializers = emitter.Modules.Where(m => m.Kind < FxModuleKind.Acceleration).ToArray();
        _modificators = emitter.Modules.Where(m => m.Kind >= FxModuleKind.Acceleration).ToArray();
        _local = emitter.Local;
        _random = seed == 0 ? 0x9E3779B9 : seed;
        _burstNext = new float[emitter.Bursts.Length];
        BeginLoop();
    }

    private void BeginLoop()
    {
        _time = 0;
        _duration = MathF.Max(1e-3f, Emitter.DurationLow >= 0 ? Emitter.DurationLow + (Emitter.Duration - Emitter.DurationLow) * NextFloat() : Emitter.Duration);
        bool delayed = !Emitter.DelayOnce || _loop == 0;
        _delay = !delayed ? 0 : MathF.Max(0, Emitter.DelayLow >= 0 ? Emitter.DelayLow + (Emitter.Delay - Emitter.DelayLow) * NextFloat() : Emitter.Delay);
        for (int i = 0; i < _burstNext.Length; i++) _burstNext[i] = Emitter.Bursts[i].Time + Emitter.Bursts[i].TimeRange * NextFloat();
    }

    // Emission stops; the particles alive live on.
    public void Stop() => _ended = true;

    public void Step(float dt, in FxContext context)
    {
        if (dt <= 0) return;
        if (_local && !Matrix4x4.Invert(context.ToWorld, out _toLocal)) _toLocal = Matrix4x4.Identity;
        if (!_started)
        {
            _started = true;
            for (int i = 0; i < Emitter.InitialParticles && context.Emit; i++) Spawn(context, 0);
        }
        Advance(dt, context);
        if (!_ended && context.Emit) Emit(dt, context); else if (!context.Emit && !_ended) Idle(dt);
    }

    private void Idle(float dt) { _time += dt; }

    // --- Emission --------------------------------------------------------------------------------

    private void Emit(float dt, in FxContext context)
    {
        float left = dt;
        for (int guard = 0; guard < 64 && left > 0 && !_ended; guard++)
        {
            if (_delay > 0) { float wait = MathF.Min(_delay, left); _delay -= wait; left -= wait; continue; }
            float step = MathF.Min(left, _duration - _time);
            float normalized = _time / _duration;
            float rate = MathF.Max(0, Emitter.BirthRate.Float(normalized, NextFloat())) * context.Emission;
            _debt += rate * step;
            while (_debt >= 1) { _debt -= 1; Spawn(context, normalized); }
            for (int i = 0; i < _burstNext.Length; i++)
            {
                var burst = Emitter.Bursts[i];
                while (_burstNext[i] >= 0 && _burstNext[i] <= _time + step && _burstNext[i] < _duration + 1e-4f)
                {
                    int count = (int)MathF.Round(burst.Count * context.Emission);
                    for (int n = 0; n < count; n++) Spawn(context, normalized);
                    _burstNext[i] = burst.Repeat > 1e-3f ? _burstNext[i] + burst.Repeat : -1;
                }
            }
            _time += step; left -= step;
            if (_time >= _duration - 1e-6f)
            {
                _loop++;
                if (Emitter.Loops > 0 && _loop >= Emitter.Loops) { _ended = true; break; }
                BeginLoop();
            }
        }
    }

    private void Spawn(in FxContext context, float normalized)
    {
        if (Count >= Particles.Length) return;
        ref var p = ref Particles[Count];
        p = default;
        p.Seed = NextSeed(); p.Life = 1; p.BaseSize = Vector3.One; p.BaseColor = Vector3.One; p.BaseAlpha = 1;
        var position = Vector3.Zero; var velocity = Vector3.Zero;
        for (int i = 0; i < _initializers.Length; i++)
        {
            var m = _initializers[i];
            var r = Random4(p.Seed, (uint)i * 7 + m.Seed);
            switch (m.Kind)
            {
                case FxModuleKind.LifeTime: p.Life = MathF.Max(1e-3f, m.A.Float(normalized, r.X)); break;
                case FxModuleKind.Position: position += Orient(Xyz(m.A.Evaluate(normalized, r)), m.WorldSpace, context); break;
                case FxModuleKind.SpawnBox:
                {
                    var extents = Xyz(m.A.Evaluate(normalized, Random4(p.Seed, (uint)i * 7 + 3)));
                    var u = new Vector3(r.X, r.Y, r.Z) * 2 - Vector3.One;
                    if (m.SurfaceOnly)
                    {
                        // On one of the six faces.
                        int axis = (int)(r.W * 3) % 3; float side = Hash(p.Seed + (uint)i * 31) > .5f ? 1 : -1;
                        if (axis == 0) u.X = side; else if (axis == 1) u.Y = side; else u.Z = side;
                    }
                    position += Orient(u * extents, m.WorldSpace, context); break;
                }
                case FxModuleKind.SpawnSphere:
                {
                    float inner = MathF.Max(0, m.A.Float(normalized, r.W)), outer = MathF.Max(inner, m.B.Float(normalized, r.W));
                    // A direction evenly spread over the sphere, kept to the half-spaces the module fills.
                    float z = r.X * 2 - 1, angle = r.Y * Turn, ring = MathF.Sqrt(MathF.Max(0, 1 - z * z));
                    var direction = new Vector3(ring * MathF.Cos(angle), ring * MathF.Sin(angle), z);
                    if (direction.X > 0 ? (m.Sides & 1) == 0 : (m.Sides & 2) == 0) direction.X = (m.Sides & 3) == 0 ? 0 : -direction.X;
                    if (direction.Y > 0 ? (m.Sides & 4) == 0 : (m.Sides & 8) == 0) direction.Y = (m.Sides & 12) == 0 ? 0 : -direction.Y;
                    if (direction.Z > 0 ? (m.Sides & 16) == 0 : (m.Sides & 32) == 0) direction.Z = (m.Sides & 48) == 0 ? 0 : -direction.Z;
                    if (direction.LengthSquared() > 1e-8f) direction = Vector3.Normalize(direction);
                    float radius = m.SurfaceOnly ? outer : MathF.Cbrt(inner * inner * inner + (outer * outer * outer - inner * inner * inner) * r.Z);
                    position += Orient(direction * radius, m.WorldSpace, context);
                    if (m.Outward) velocity += Orient(direction, m.WorldSpace, context) * m.C.Float(normalized, Hash(p.Seed + (uint)i * 131));
                    break;
                }
                case FxModuleKind.SpawnCircle:
                {
                    float inner = MathF.Max(0, m.A.Float(normalized, r.W)), outer = MathF.Max(inner, m.B.Float(normalized, r.W));
                    float radius = m.SurfaceOnly ? outer : MathF.Sqrt(inner * inner + (outer * outer - inner * inner) * r.Z), angle = r.X * Turn;
                    var flat = Vector3.TransformNormal(new Vector3(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius, 0), m.Frame);
                    position += Orient(flat, m.WorldSpace, context); break;
                }
                case FxModuleKind.Velocity: velocity += Orient(Xyz(m.A.Evaluate(normalized, r)) * m.Scale, m.WorldSpace, context); break;
                case FxModuleKind.VelocitySpread:
                {
                    float speed = velocity.Length();
                    if (speed < 1e-6f) break;
                    float amount = m.A.Float(normalized, r.W);
                    var push = new Vector3(r.X * 2 - 1, r.Y * 2 - 1, r.Z * 2 - 1) * speed * amount;
                    velocity += push;
                    float after = velocity.Length();
                    if (after > 1e-6f) velocity *= speed / after;   // the speed is kept, the heading spreads
                    break;
                }
                case FxModuleKind.Size: p.BaseSize = Xyz(m.A.Evaluate(normalized, r)) * m.Scale; break;
                case FxModuleKind.Rotation: p.BaseRotation = m.A.Float(normalized, r.X) * Turn; break;
                case FxModuleKind.RotationRate: p.BaseRotationRate = m.A.Float(normalized, r.X) * Turn; break;
                case FxModuleKind.Rotation3D: p.BaseRotation3D = Xyz(m.A.Evaluate(normalized, r)) * Turn; break;
                case FxModuleKind.RotationRate3D: p.RotationRate3D = Xyz(m.A.Evaluate(normalized, r)) * Turn; break;
                case FxModuleKind.Alpha: p.BaseAlpha = m.A.Float(normalized, r.X); break;
                case FxModuleKind.Color: p.BaseColor = Xyz(m.A.Evaluate(normalized, r)); break;
            }
        }
        // Texture animation chooses its first frame at birth.
        for (int i = 0; i < _modificators.Length; i++)
            if (_modificators[i].Kind == FxModuleKind.TextureAnimation) p.InitialFrame = _modificators[i].A.Float(normalized, Hash(p.Seed + 977 + (uint)i));
        if (Emitter.Meshes.Length > 1) p.Mesh = (int)(Hash(p.Seed + 4441) * Emitter.Meshes.Length) % Emitter.Meshes.Length;
        p.Position = _local ? position : position + context.ToWorld.Translation;
        p.BaseVelocity = velocity; p.Velocity = velocity;
        p.Size = p.BaseSize * context.Size; p.Rotation = p.BaseRotation; p.RotationRate = p.BaseRotationRate; p.Rotation3D = p.BaseRotation3D;
        p.Color = p.BaseColor; p.Alpha = p.BaseAlpha * context.Alpha; p.Frame = p.InitialFrame;
        Count++;
    }

    // A vector given in world axes or in the emitter's, brought to the simulation space.
    private Vector3 Orient(Vector3 v, bool worldSpace, in FxContext context)
    {
        if (worldSpace == !_local) return v;
        return Vector3.TransformNormal(v, _local ? _toLocal : context.ToWorld);
    }
    private Matrix4x4 _toLocal = Matrix4x4.Identity;

    // --- Life ------------------------------------------------------------------------------------

    private void Advance(float dt, in FxContext context)
    {
        for (int index = 0; index < Count; index++)
        {
            ref var p = ref Particles[index];
            p.Age += dt;
            float t = p.Age / p.Life;
            if (t >= 1) { Particles[index] = Particles[--Count]; index--; continue; }
            var velocity = p.BaseVelocity; var offset = Vector3.Zero;
            var size = p.BaseSize; float alpha = p.BaseAlpha; var color = p.BaseColor;
            float rotationRate = p.BaseRotationRate; float rotationSet = float.NaN; Vector3 rotation3DSet = new(float.NaN);
            var rate3D = p.RotationRate3D; float fade = 1, grow = 1;
            for (int i = 0; i < _modificators.Length; i++)
            {
                var m = _modificators[i];
                switch (m.Kind)
                {
                    case FxModuleKind.Acceleration:
                        p.BaseVelocity += Orient(Xyz(m.A.Evaluate(t, Random4(p.Seed, 40 + (uint)i))), m.WorldSpace, context) * (m.B.Float(t, Hash(p.Seed + 41 + (uint)i)) * dt);
                        velocity = p.BaseVelocity; break;
                    case FxModuleKind.Drag:
                        p.BaseVelocity *= MathF.Exp(-MathF.Max(0, m.A.Float(t, Hash(p.Seed + 43 + (uint)i)) * m.Scale) * dt);
                        velocity = p.BaseVelocity; break;
                    case FxModuleKind.VelocityTurbulize:
                    {
                        if (t > m.B.Float(t, Hash(p.Seed + 47 + (uint)i))) break;   // only over the first part of its life
                        p.TurbulenceLeft -= dt;
                        if (p.TurbulenceLeft <= 0)
                        {
                            // A new push, drawn within the module's range, either way along each axis.
                            uint draw = p.Seed + (uint)(p.Age * 1000) * 2654435761u + (uint)i;
                            var range = Xyz(m.A.Evaluate(t, Random4(draw, 5)));
                            var sign = new Vector3(Hash(draw + 1) < .5f ? -1 : 1, Hash(draw + 2) < .5f ? -1 : 1, Hash(draw + 3) < .5f ? -1 : 1);
                            p.Turbulence = Orient(range * sign, m.WorldSpace, context);
                            p.TurbulenceLeft = MathF.Max(m.Interval, 1e-2f);
                        }
                        p.BaseVelocity += p.Turbulence * (dt / MathF.Max(m.Duration, 1e-2f));
                        velocity = p.BaseVelocity; break;
                    }
                    case FxModuleKind.Noise:
                    {
                        var amplitude = Xyz(m.A.Evaluate(t, Random4(p.Seed, 50 + (uint)i)));
                        var frequency = Xyz(m.B.Evaluate(t, Random4(p.Seed, 51 + (uint)i)));
                        var shift = Xyz(m.C.Evaluate(t, Random4(p.Seed, 52 + (uint)i)));
                        var sample = p.Position * frequency + shift + new Vector3(p.Age * m.Interval);
                        var noise = new Vector3(Noise(sample, p.Seed), Noise(sample + new Vector3(19.1f, 7.3f, 3.7f), p.Seed + 1), Noise(sample + new Vector3(5.9f, 31.7f, 11.3f), p.Seed + 2)) * amplitude;
                        if (m.ToPosition) offset += noise; else velocity += noise;
                        break;
                    }
                    case FxModuleKind.VelocityOverLife:
                    {
                        var curve = Xyz(m.A.Evaluate(t, Random4(p.Seed, 53 + (uint)i))) * m.Scale;
                        velocity = m.Absolute ? Orient(curve, false, context) : m.Modulate ? velocity * curve : velocity + Orient(curve, false, context);
                        break;
                    }
                    case FxModuleKind.SizeOverLife:
                    {
                        var curve = Xyz(m.A.Evaluate(t, Random4(p.Seed, 54 + (uint)i))) * m.Scale;
                        size = m.Modulate ? size * curve : curve; break;
                    }
                    case FxModuleKind.AlphaOverLife:
                    {
                        float curve = m.A.Float(t, Hash(p.Seed + 55 + (uint)i));
                        alpha = m.Modulate ? alpha * curve : curve; break;
                    }
                    case FxModuleKind.ColorOverLife:
                    {
                        var curve = Xyz(m.A.Evaluate(t, Random4(p.Seed, 56 + (uint)i)));
                        color = m.Modulate ? color * curve : curve; break;
                    }
                    case FxModuleKind.RotationOverLife:
                    {
                        float curve = m.A.Float(t, Hash(p.Seed + 57 + (uint)i)) * Turn;
                        rotationSet = m.Modulate ? p.BaseRotation + curve : curve; break;
                    }
                    case FxModuleKind.RotationRateOverLife:
                    {
                        float curve = m.A.Float(t, Hash(p.Seed + 58 + (uint)i));
                        rotationRate = m.Modulate ? rotationRate * curve : curve * Turn; break;
                    }
                    case FxModuleKind.Rotation3DOverLife:
                    {
                        var curve = Xyz(m.A.Evaluate(t, Random4(p.Seed, 59 + (uint)i))) * Turn;
                        rotation3DSet = m.Modulate ? p.BaseRotation3D + curve : curve; break;
                    }
                    case FxModuleKind.RotationRate3DOverLife:
                        rate3D = p.RotationRate3D * Xyz(m.A.Evaluate(t, Random4(p.Seed, 60 + (uint)i))); break;
                    case FxModuleKind.TextureAnimation:
                    {
                        float speed = m.B.Float(t, Hash(p.Seed + 61 + (uint)i));
                        p.Frame = p.InitialFrame + (m.BySpeed ? speed * p.Age : speed * t); break;
                    }
                    case FxModuleKind.AlphaByDistance or FxModuleKind.SizeByDistance:
                    {
                        var world = _local ? Vector3.Transform(p.Position, context.ToWorld) : p.Position;
                        float distance = Vector3.Distance(world, context.Camera);
                        var d = m.Distances;
                        if (m.Kind == FxModuleKind.AlphaByDistance)
                        {
                            // In between the near pair, out between the far pair.
                            if (d.Y > d.X) fade *= Math.Clamp((distance - d.X) / (d.Y - d.X), 0, 1);
                            if (d.W > d.Z) fade *= 1 - Math.Clamp((distance - d.Z) / (d.W - d.Z), 0, 1);
                        }
                        else
                        {
                            float near = d.Y > d.X ? Math.Clamp((distance - d.X) / (d.Y - d.X), 0, 1) : 1, far = d.W > d.Z ? Math.Clamp((distance - d.Z) / (d.W - d.Z), 0, 1) : 0;
                            grow *= (m.Multipliers.X + (1 - m.Multipliers.X) * near) * (1 + (m.Multipliers.Y - 1) * far);
                        }
                        break;
                    }
                }
            }
            p.Velocity = velocity * context.Velocity;
            p.Position += p.Velocity * dt;
            p.Offset = offset;
            p.Size = size * (context.Size * grow);
            p.Alpha = MathF.Max(0, alpha * fade * context.Alpha);
            p.Color = color;
            p.Spin += rotationRate * dt; p.RotationRate = rotationRate;
            p.Rotation = float.IsNaN(rotationSet) ? p.BaseRotation + p.Spin : rotationSet + p.Spin;
            p.Spin3D += rate3D * dt;
            p.Rotation3D = float.IsNaN(rotation3DSet.X) ? p.BaseRotation3D + p.Spin3D : rotation3DSet + p.Spin3D;
        }
    }

    // A particle's place in game space.
    public Vector3 WorldPosition(in FxParticle p, in Matrix4x4 toWorld) => _local ? Vector3.Transform(p.Position + p.Offset, toWorld) : p.Position + p.Offset;
    // A direction of the simulation in game space.
    public Vector3 WorldDirection(Vector3 v, in Matrix4x4 toWorld) => _local ? Vector3.TransformNormal(v, toWorld) : v;

    // --- Randomness ------------------------------------------------------------------------------

    private static Vector3 Xyz(Vector4 v) => new(v.X, v.Y, v.Z);
    private uint NextSeed() { _random ^= _random << 13; _random ^= _random >> 17; _random ^= _random << 5; return _random; }
    private float NextFloat() => (NextSeed() >> 8) * (1f / 16777216f);
    private static uint Mix(uint x) { x ^= x >> 16; x *= 0x7FEB352D; x ^= x >> 15; x *= 0x846CA68B; x ^= x >> 16; return x; }
    private static float Hash(uint x) => (Mix(x) >> 8) * (1f / 16777216f);
    private static Vector4 Random4(uint seed, uint salt)
    {
        uint b = Mix(seed + salt * 0x9E3779B1);
        return new(Hash(b), Hash(b + 0x68E31DA4), Hash(b + 0xB5297A4D), Hash(b + 0x1B56C4E9));
    }

    // Smooth noise in -1..1 (value noise on a lattice, three octaves are not needed for a wobble).
    private static float Noise(Vector3 p, uint seed)
    {
        var cell = new Vector3(MathF.Floor(p.X), MathF.Floor(p.Y), MathF.Floor(p.Z)); var f = p - cell;
        f = f * f * (new Vector3(3) - 2 * f);
        float Corner(int x, int y, int z) => Hash(seed + (uint)((int)cell.X + x) * 73856093u ^ (uint)((int)cell.Y + y) * 19349663u ^ (uint)((int)cell.Z + z) * 83492791u) * 2 - 1;
        float x00 = Corner(0, 0, 0) + (Corner(1, 0, 0) - Corner(0, 0, 0)) * f.X, x10 = Corner(0, 1, 0) + (Corner(1, 1, 0) - Corner(0, 1, 0)) * f.X;
        float x01 = Corner(0, 0, 1) + (Corner(1, 0, 1) - Corner(0, 0, 1)) * f.X, x11 = Corner(0, 1, 1) + (Corner(1, 1, 1) - Corner(0, 1, 1)) * f.X;
        float y0 = x00 + (x10 - x00) * f.Y, y1 = x01 + (x11 - x01) * f.Y;
        return y0 + (y1 - y0) * f.Z;
    }
}

// A particle system being played: one instance per emitter.
public sealed class FxSystemInstance
{
    public FxSystem System { get; }
    public FxEmitterInstance[] Emitters { get; }
    public float Time { get; private set; }
    public bool Finished => Emitters.All(e => e.Finished);
    public int ParticleCount { get { int n = 0; foreach (var e in Emitters) n += e.Count; return n; } }

    public FxSystemInstance(FxSystem system, uint seed = 1)
    {
        System = system;
        Emitters = system.Emitters.Select((e, i) => new FxEmitterInstance(e, seed * 2654435761u + (uint)i * 40503u + 1)).ToArray();
    }

    // Fixed steps: the same motion whatever the frame rate, and no tunnelling through long frames.
    public const float MaxStep = 1f / 30;
    public void Step(float dt, in FxContext context)
    {
        dt = Math.Clamp(dt, 0, .25f);
        while (dt > 1e-6f)
        {
            float step = MathF.Min(dt, MaxStep);
            foreach (var emitter in Emitters) emitter.Step(step, context);
            Time += step; dt -= step;
        }
    }

    // What the system looks like after running for a while (the game's own prewarming, or a library picture).
    public void Prewarm(float seconds, in FxContext context)
    {
        for (float t = 0; t < seconds; t += MaxStep) { foreach (var emitter in Emitters) emitter.Step(MaxStep, context); Time += MaxStep; }
    }

    public void Stop() { foreach (var emitter in Emitters) emitter.Stop(); }
}
