// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;

namespace NCWE.Core.Fx;

// A value of a particle module, as the game's evaluators give it (CEvaluatorFloat*, Vector*, Color*):
// a constant, a random draw between two values, a start-to-end ramp or a curve. Floats live in X.
// Axes: an evaluator authored for fewer axes than it feeds (FreeAxes) spills its last one into
// the others (a uniform size from one number); locked axes of a random draw share one draw.
public sealed class FxValue
{
    public enum Mode { Constant, Random, StartEnd, Curve }
    public Mode Kind { get; init; }
    public Vector4 A { get; init; }
    public Vector4 B { get; init; }
    // Curve keys: one time array per channel (multi-channel curves key each on its own).
    public float[][] Times { get; init; } = [];
    public float[][] Values { get; init; } = [];
    public int FreeAxes { get; init; } = 4;
    public bool Spill { get; init; } = true;
    public bool LockX { get; init; }
    public bool LockY { get; init; }
    public bool LockZ { get; init; }
    public bool LockW { get; init; }
    // Colors: one draw for the whole color (false) or one per channel.
    public bool PerChannel { get; init; } = true;

    public static FxValue Constant(float value) => new() { Kind = Mode.Constant, A = new(value) };
    public static FxValue Constant(Vector4 value) => new() { Kind = Mode.Constant, A = value };
    public static readonly FxValue Zero = Constant(0), One = Constant(1);

    public bool IsConstant => Kind == Mode.Constant;
    // Whether the value changes along its parameter (a module then follows it over time).
    public bool Varies => Kind is Mode.StartEnd or Mode.Curve;

    // t: where along the curve or the ramp (0..1); random: this particle's draws (0..1 each).
    public Vector4 Evaluate(float t, Vector4 random)
    {
        Vector4 v;
        switch (Kind)
        {
            case Mode.Random:
                if (!PerChannel) random = new(random.X);
                else
                {
                    // Locked axes follow the first locked one.
                    float shared = LockX ? random.X : LockY ? random.Y : LockZ ? random.Z : random.W;
                    if (LockX) random.X = shared; if (LockY) random.Y = shared; if (LockZ) random.Z = shared; if (LockW) random.W = shared;
                }
                v = A + (B - A) * random; break;
            case Mode.StartEnd: v = Vector4.Lerp(A, B, Math.Clamp(t, 0, 1)); break;
            case Mode.Curve: v = new(Sample(0, t), Sample(1, t), Sample(2, t), Sample(3, t)); break;
            default: v = A; break;
        }
        if (Spill && FreeAxes < 4)
        {
            float last = FreeAxes <= 1 ? v.X : FreeAxes == 2 ? v.Y : v.Z;
            if (FreeAxes <= 1) v.Y = last;
            if (FreeAxes <= 2) v.Z = last;
            v.W = last;
        }
        return v;
    }

    public float Float(float t, float random) => Evaluate(t, new(random)).X;

    private float Sample(int channel, float t)
    {
        if (Times.Length == 0) return 0;
        int c = Math.Min(channel, Times.Length - 1);
        var times = Times[c]; var values = Values[c];
        int n = Math.Min(times.Length, values.Length);
        if (n == 0) return 0;
        if (t <= times[0]) return values[0];
        if (t >= times[n - 1]) return values[n - 1];
        int lo = 0, hi = n - 1;
        while (hi - lo > 1) { int mid = (lo + hi) >> 1; if (times[mid] <= t) lo = mid; else hi = mid; }
        float span = times[hi] - times[lo];
        return span <= 1e-7f ? values[hi] : values[lo] + (values[hi] - values[lo]) * ((t - times[lo]) / span);
    }

    // The largest magnitude the value can take on any axis (bounds, budgets).
    public float Magnitude()
    {
        float m = 0;
        void Take(Vector4 v) { m = MathF.Max(m, MathF.Max(MathF.Max(MathF.Abs(v.X), MathF.Abs(v.Y)), MathF.Max(MathF.Abs(v.Z), MathF.Abs(v.W)))); }
        if (Kind == Mode.Curve) foreach (var channel in Values) foreach (float v in channel) m = MathF.Max(m, MathF.Abs(v));
        else { Take(A); if (Kind != Mode.Constant) Take(B); }
        return m;
    }
}

public enum FxModuleKind
{
    // Initializers (at birth)
    LifeTime, Position, SpawnBox, SpawnSphere, SpawnCircle, Velocity, VelocitySpread, VelocityInherit,
    Size, Rotation, RotationRate, Rotation3D, RotationRate3D, Alpha, Color,
    // Modificators (every frame)
    Acceleration, Drag, VelocityOverLife, SizeOverLife, AlphaOverLife, ColorOverLife, RotationOverLife, RotationRateOverLife,
    Rotation3DOverLife, RotationRate3DOverLife, TextureAnimation, AlphaByDistance, SizeByDistance, AlphaOverEffect,
    VelocityTurbulize, Noise, Orbit, Collision
}

// One module of an emitter (CParticleInitializer*, CParticleModificator*), with the game's field
// names kept in the comments of ParticleReader. A, B, C: its evaluators, in the module's own order.
public sealed class FxModule
{
    public FxModuleKind Kind { get; init; }
    public FxValue A { get; init; } = FxValue.Zero;
    public FxValue B { get; init; } = FxValue.Zero;
    public FxValue C { get; init; } = FxValue.Zero;
    public float Scale { get; init; } = 1;
    public bool WorldSpace { get; init; }
    public bool Modulate { get; init; } = true;
    public bool Absolute { get; init; }
    public bool SurfaceOnly { get; init; }
    // Spawn sphere: the half-spaces it fills (+X -X +Y -Y +Z -Z), and whether particles leave its centre.
    public int Sides { get; init; } = 63;
    public bool Outward { get; init; }
    // Texture animation: frames per second (false: frames over the particle's life).
    public bool BySpeed { get; init; }
    // Distance fades: near start/end, far start/end (metres); size by distance: its two multipliers.
    public Vector4 Distances { get; init; }
    public Vector2 Multipliers { get; init; } = Vector2.One;
    // Turbulence and noise: seconds between two draws, seconds a draw acts, and whether noise moves positions.
    public float Interval { get; init; }
    public float Duration { get; init; }
    public bool ToPosition { get; init; }
    // Spawn circle: its plane (rows: the circle's axes in emitter space).
    public Matrix4x4 Frame { get; init; } = Matrix4x4.Identity;
    public uint Seed { get; init; }
}

public enum FxDrawer { Billboard, MotionBlur, Mesh, Screen, EmitterOrientation, SphereAligned, Trail, FacingTrail, Beam, FacingBeam }
public enum FxMeshOrientation { Normal, MovementDirection, NoRotation }

// Which of the game's particle shaders draws an emitter, with the parameters it reads (names of
// the material templates; their formulas are ported in the renderer from the game's own shaders).
public enum FxShader { Blackbody, Generic, Mesh, Unsupported }

public sealed class FxMaterial
{
    public FxShader Shader { get; init; }
    public string Template { get; init; } = "";
    public Dictionary<string, float> Scalars { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Vector4> Vectors { get; } = new(StringComparer.OrdinalIgnoreCase);
    // Texture parameters: depot paths.
    public Dictionary<string, string> Textures { get; } = new(StringComparer.OrdinalIgnoreCase);
    public float Scalar(string name, float fallback) => Scalars.TryGetValue(name, out float v) ? v : fallback;
    public Vector4 Vector(string name, Vector4 fallback) => Vectors.TryGetValue(name, out var v) ? v : fallback;
    public string Texture(string name) => Textures.GetValueOrDefault(name, "");
}

public readonly record struct FxBurst(float Time, int Count, float TimeRange, float Repeat);

// One emitter of a particle system (CParticleEmitter, its first detail level).
public sealed class FxEmitter
{
    public string Name { get; init; } = "";
    public FxModule[] Modules { get; init; } = [];
    public FxDrawer Drawer { get; init; }
    public float PivotOffset { get; init; }
    public float StretchPerVelocity { get; init; }
    // Emitter-orientation quads: the plane they lie in (pitch, yaw, roll in degrees).
    public Vector3 Orientation { get; init; }
    public string[] Meshes { get; init; } = [];
    public FxMeshOrientation MeshOrientation { get; init; }
    public FxMaterial Material { get; init; } = new();
    public int MaxParticles { get; init; } = 100;
    // 0: loops forever.
    public int Loops { get; init; }
    public float Duration { get; init; } = 1;
    public float DurationLow { get; init; } = -1;
    public float Delay { get; init; }
    public float DelayLow { get; init; } = -1;
    public bool DelayOnce { get; init; }
    public FxValue BirthRate { get; init; } = FxValue.Zero;
    public FxBurst[] Bursts { get; init; } = [];
    public int InitialParticles { get; init; }
    // Particles stay in the emitter's frame (they follow it when it moves).
    public bool Local { get; init; }
    public bool Sorted { get; init; }
    // Trails: metres of texture, and whether the ribbon faces the camera.
    public float TexturesPerUnit { get; init; } = 1;

    // Whether the editor draws the emitter: quads and meshes of the templates whose shaders are
    // ported. Screen effects, trails, beams and the other templates (heat haze, liquids...) are not.
    public bool Drawable => Drawer switch
    {
        FxDrawer.Mesh => Meshes.Length > 0 && Material.Shader == FxShader.Mesh,
        FxDrawer.Screen or FxDrawer.Trail or FxDrawer.FacingTrail or FxDrawer.Beam or FxDrawer.FacingBeam => false,
        _ => Material.Shader is FxShader.Blackbody or FxShader.Generic
    };
}

// A particle system (.particle).
public sealed class FxSystem
{
    public string Path { get; init; } = "";
    public FxEmitter[] Emitters { get; init; } = [];
    public float Prewarm { get; init; }
    public float AutoHideDistance { get; init; } = 50;
    public Bounds3 Bounds { get; init; }
    // Whether every emitter ends by itself (a one-shot: an explosion, an impact).
    public bool OneShot => Emitters.Length > 0 && Emitters.All(e => e.Loops > 0);
}

// What an effect (.effect) does over its time line.
public sealed class FxEffectParticles
{
    public string System { get; init; } = "";
    public float Begin { get; init; }
    public float Duration { get; init; }
    // Over the item's time (0..1): how much is emitted, opacity, size and speed factors.
    public FxValue EmissionScale { get; init; } = FxValue.One;
    public FxValue Alpha { get; init; } = FxValue.One;
    public FxValue Size { get; init; } = FxValue.One;
    public FxValue Velocity { get; init; } = FxValue.One;
}
public sealed class FxEffectSound { public string Event { get; init; } = ""; public float Begin { get; init; } }
public sealed class FxEffectLight
{
    public float Begin { get; init; }
    public float Duration { get; init; }
    public Vector3 Offset { get; init; }
    public Vector3 Color { get; init; } = Vector3.One;
    public FxValue Radius { get; init; } = FxValue.One;
    public FxValue Intensity { get; init; } = FxValue.One;
    public float Ev { get; init; }
}

public sealed class FxEffect
{
    public string Path { get; init; } = "";
    public float Length { get; init; } = 1;
    // The part repeated while the effect is kept alive (start, end); none: it plays once.
    public (float Start, float End)? Loop { get; init; }
    public FxEffectParticles[] Particles { get; init; } = [];
    public FxEffectSound[] Sounds { get; init; } = [];
    public FxEffectLight[] Lights { get; init; } = [];
    // What the editor does not show (screen effects, decals, material parameters), by kind.
    public string[] Skipped { get; init; } = [];
}
