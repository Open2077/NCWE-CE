// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Collections;
using System.Numerics;
using NCWE.Core;
using NCWE.Core.Fx;
using WolvenKit.RED4.Types;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace NCWE.Formats.Fx;

// The game's particle systems (.particle: CParticleSystem) and effects (.effect: worldEffect) as
// the editor plays them (NCWE.Core.Fx). Shipping files keep the authored modules of each emitter
// beside the cooked simulation data: those are read. Of an emitter's detail levels, the first.
public static class ParticleReader
{
    public static FxSystem ReadSystem(CParticleSystem system, string path, Func<string, CMaterialInstance?>? instances = null)
    {
        var emitters = new List<FxEmitter>();
        foreach (var handle in system.Emitters)
            if (handle.Chunk is { } emitter && emitter.IsEnabled && Emitter(emitter, instances) is { } read) emitters.Add(read);
        var box = system.BoundingBox;
        return new FxSystem
        {
            Path = path, Emitters = emitters.ToArray(), Prewarm = Math.Clamp(system.PrewarmingTime, 0, 30), AutoHideDistance = system.AutoHideDistance > 0 ? system.AutoHideDistance : 50,
            Bounds = new(new(box.Min.X, box.Min.Y, box.Min.Z), new(box.Max.X, box.Max.Y, box.Max.Z))
        };
    }

    private static FxEmitter? Emitter(CParticleEmitter e, Func<string, CMaterialInstance?>? instances)
    {
        var lod = e.Lods.Count > 0 ? e.Lods[0] : null;
        if (lod is null || !lod.IsEnabled) return null;
        var modules = new List<FxModule>();
        foreach (var handle in e.Modules)
            if (handle.Chunk is { } module && module.IsEnabled && Module(module) is { } read) modules.Add(read);
        var drawer = e.ParticleDrawer?.Chunk;
        // The drawers derive from one another: told apart by their exact type.
        var kind = drawer?.GetType().Name switch
        {
            "CParticleDrawerMotionBlur" => FxDrawer.MotionBlur, "CParticleDrawerMesh" => FxDrawer.Mesh, "CParticleDrawerScreen" => FxDrawer.Screen,
            "CParticleDrawerEmitterOrientation" => FxDrawer.EmitterOrientation, "CParticleDrawerSphereAligned" => FxDrawer.SphereAligned,
            "CParticleDrawerFacingTrail" => FxDrawer.FacingTrail, "CParticleDrawerTrail" => FxDrawer.Trail,
            "CParticleDrawerFacingBeam" => FxDrawer.FacingBeam, "CParticleDrawerBeam" => FxDrawer.Beam, _ => FxDrawer.Billboard
        };
        var duration = lod.EmitterDurationSettings; var delay = lod.EmitterDelaySettings;
        var meshes = drawer is CParticleDrawerMesh mesh ? mesh.Meshes.Select(m => m.DepotPath.GetResolvedText() ?? "").Where(m => m.Length > 0).ToArray() : [];
        return new FxEmitter
        {
            Name = e.EditorName.ToString() ?? "", Modules = modules.ToArray(), Drawer = kind,
            PivotOffset = Number(drawer, "pivotOffset"), StretchPerVelocity = kind == FxDrawer.MotionBlur ? Number(drawer, "stretchPerVelocity") : 0,
            Orientation = drawer is CParticleDrawerEmitterOrientation oriented ? new(oriented.CoordinateSystem.Pitch, oriented.CoordinateSystem.Yaw, oriented.CoordinateSystem.Roll) : default,
            Meshes = meshes, MeshOrientation = drawer is CParticleDrawerMesh m2 ? m2.OrientationMode.ToString() switch { "MPOM_MovementDirection" => FxMeshOrientation.MovementDirection, "MPOM_NoRotation" => FxMeshOrientation.NoRotation, _ => FxMeshOrientation.Normal } : default,
            TexturesPerUnit = kind is FxDrawer.Trail or FxDrawer.FacingTrail or FxDrawer.Beam or FxDrawer.FacingBeam ? Number(drawer, "texturesPerUnit", 1) : 1,
            Material = Material(e, kind, instances),
            MaxParticles = (int)Math.Clamp((uint)e.MaxParticles, 1u, 4096u), Loops = e.EmitterLoops,
            Duration = Math.Max(duration?.EmitterDuration ?? 1, 1e-3f), DurationLow = duration is not null && duration.UseEmitterDurationRange ? duration.EmitterDurationLow : -1,
            Delay = Math.Max(delay?.EmitterDelay ?? 0, 0), DelayLow = delay is not null && delay.UseEmitterDelayRange ? delay.EmitterDelayLow : -1, DelayOnce = delay is not null && delay.UseEmitterDelayOnce,
            BirthRate = Float(lod.BirthRate?.Chunk),
            Bursts = lod.BurstList.Select(b => new FxBurst(b.BurstTime, (int)Math.Min((uint)b.SpawnCount, 4096u), b.SpawnTimeRange, b.RepeatTime)).ToArray(),
            InitialParticles = (int)Math.Min((uint)e.InitialParticleCount, 4096u), Local = e.KeepSimulationLocal, Sorted = lod.SortingMode.ToString() != "PSM_None"
        };
    }

    // A number of a drawer, by the game's property name.
    private static float Number(IParticleDrawer? drawer, string property, float fallback = 0)
        => drawer is RedBaseClass red && red.GetProperty(property) is CFloat value ? (float)value : fallback;

    // --- Modules -------------------------------------------------------------------------------

    private static FxModule? Module(IParticleModule module) => module switch
    {
        CParticleInitializerLifeTime m => new() { Kind = FxModuleKind.LifeTime, A = Float(m.LifeTime?.Chunk), Seed = m.Seed },
        CParticleInitializerPosition m => new() { Kind = FxModuleKind.Position, A = Vector(m.Position?.Chunk), WorldSpace = m.WorldSpace, Seed = m.Seed },
        CParticleInitializerSpawnBox m => new() { Kind = FxModuleKind.SpawnBox, A = Vector(m.Extents?.Chunk), SurfaceOnly = m.SurfaceOnly, WorldSpace = m.WorldSpace, Seed = m.Seed },
        CParticleInitializerSpawnSphere m => new()
        {
            Kind = FxModuleKind.SpawnSphere, A = Float(m.InnerRadius?.Chunk), B = Float(m.OuterRadius?.Chunk), C = Float(m.ForceScale?.Chunk), SurfaceOnly = m.SurfaceOnly, WorldSpace = m.WorldSpace,
            Outward = m.Velocity, Seed = m.Seed,
            Sides = (m.SpawnPositiveX ? 1 : 0) | (m.SpawnNegativeX ? 2 : 0) | (m.SpawnPositiveY ? 4 : 0) | (m.SpawnNegativeY ? 8 : 0) | (m.SpawnPositiveZ ? 16 : 0) | (m.SpawnNegativeZ ? 32 : 0)
        },
        CParticleInitializerSpawnCircle m => new()
        {
            Kind = FxModuleKind.SpawnCircle, A = Float(m.InnerRadius?.Chunk), B = Float(m.OuterRadius?.Chunk), SurfaceOnly = m.SurfaceOnly, WorldSpace = m.WorldSpace, Seed = m.Seed,
            Frame = new(m.SpawnToLocal.X.X, m.SpawnToLocal.X.Y, m.SpawnToLocal.X.Z, 0, m.SpawnToLocal.Y.X, m.SpawnToLocal.Y.Y, m.SpawnToLocal.Y.Z, 0, m.SpawnToLocal.Z.X, m.SpawnToLocal.Z.Y, m.SpawnToLocal.Z.Z, 0, 0, 0, 0, 1)
        },
        CParticleInitializerVelocity m => new() { Kind = FxModuleKind.Velocity, A = Vector(m.Velocity?.Chunk), Scale = m.Scale, WorldSpace = m.WorldSpace, Seed = m.Seed },
        CParticleInitializerVelocitySpread m => new() { Kind = FxModuleKind.VelocitySpread, A = Float(m.Scale?.Chunk), Seed = m.Seed },
        CParticleInitializerSize m => new() { Kind = FxModuleKind.Size, A = Vector(m.Size?.Chunk), Scale = m.Scale, Seed = m.Seed },
        CParticleInitializerRotation m => new() { Kind = FxModuleKind.Rotation, A = Float(m.Rotation?.Chunk), Seed = m.Seed },
        CParticleInitializerRotationRate m => new() { Kind = FxModuleKind.RotationRate, A = Float(m.RotationRate?.Chunk), Seed = m.Seed },
        CParticleInitializerRotation3D m => new() { Kind = FxModuleKind.Rotation3D, A = Vector(m.Rotation?.Chunk), Seed = m.Seed },
        CParticleInitializerRotationRate3D m => new() { Kind = FxModuleKind.RotationRate3D, A = Vector(m.RotationRate?.Chunk), Seed = m.Seed },
        CParticleInitializerAlpha m => new() { Kind = FxModuleKind.Alpha, A = Float(m.Alpha?.Chunk), Seed = m.Seed },
        CParticleInitializerColor m => new() { Kind = FxModuleKind.Color, A = Color(m.Color?.Chunk), Seed = m.Seed },
        CParticleModificatorAcceleration m => new() { Kind = FxModuleKind.Acceleration, A = Vector(m.Direction?.Chunk), B = Float(m.Scale?.Chunk), WorldSpace = m.WorldSpace },
        CParticleModificatorDrag m => new() { Kind = FxModuleKind.Drag, A = Float(m.DragCoefficient?.Chunk), Scale = m.Scale },
        CParticleModificatorVelocityOverLife m => new() { Kind = FxModuleKind.VelocityOverLife, A = Vector(m.Velocity?.Chunk), Scale = m.Scale, Modulate = m.Modulate, Absolute = m.Absolute },
        CParticleModificatorSizeOverLife m => new() { Kind = FxModuleKind.SizeOverLife, A = Vector(m.Size?.Chunk), Scale = m.Scale, Modulate = m.Modulate },
        CParticleModificatorAlphaOverLife m => new() { Kind = FxModuleKind.AlphaOverLife, A = Float(m.Alpha?.Chunk), Modulate = m.Modulate },
        CParticleModificatorColorOverLife m => new() { Kind = FxModuleKind.ColorOverLife, A = Color(m.Color?.Chunk), Modulate = m.Modulate },
        CParticleModificatorRotationOverLife m => new() { Kind = FxModuleKind.RotationOverLife, A = Float(m.Rotation?.Chunk), Modulate = m.Modulate },
        CParticleModificatorRotationRateOverLife m => new() { Kind = FxModuleKind.RotationRateOverLife, A = Float(m.RotationRate?.Chunk), Modulate = m.Modulate },
        CParticleModificatorRotation3DOverLife m => new() { Kind = FxModuleKind.Rotation3DOverLife, A = Vector(m.Rotation?.Chunk), Modulate = m.Modulate },
        CParticleModificatorRotationRate3DOverLife m => new() { Kind = FxModuleKind.RotationRate3DOverLife, A = Vector(m.RotationRate?.Chunk) },
        CParticleModificatorTextureAnimation m => new() { Kind = FxModuleKind.TextureAnimation, A = Float(m.InitialFrame?.Chunk), B = Float(m.AnimationSpeed?.Chunk), BySpeed = m.AnimationMode.ToString() == "TAM_Speed" },
        CParticleModificatorAlphaByDistance m => new() { Kind = FxModuleKind.AlphaByDistance, Distances = new(m.NearBlendDistance.X, m.NearBlendDistance.Y, m.FarBlendDistance.X, m.FarBlendDistance.Y) },
        CParticleModificatorSizeByDistance m => new()
        {
            Kind = FxModuleKind.SizeByDistance,
            Distances = new(Float(m.NearDistanceRangeStart?.Chunk).A.X, Float(m.NearDistanceRangeEnd?.Chunk).A.X, Float(m.FarDistanceRangeStart?.Chunk).A.X, Float(m.FarDistanceRangeEnd?.Chunk).A.X),
            Multipliers = new(Float(m.NearDistanceSizeMultiplier?.Chunk).A.X, Float(m.FarDistanceSizeMultiplier?.Chunk).A.X)
        },
        CParticleModificatorAlphaOverEffect => new() { Kind = FxModuleKind.AlphaOverEffect },
        CParticleModificatorVelocityTurbulize m => new() { Kind = FxModuleKind.VelocityTurbulize, A = Vector(m.Scale?.Chunk), B = Float(m.TimelifeLimit?.Chunk), Interval = m.NoiseInterval, Duration = m.Duration, WorldSpace = m.WorldSpace },
        CParticleModificatorNoise m => new()
        {
            Kind = FxModuleKind.Noise, A = Vector(m.Amplitude?.Chunk), B = Vector(m.Frequency?.Chunk), C = Vector(m.Offset?.Chunk), ToPosition = m.ApplyToPosition,
            Interval = MathF.Max(MathF.Abs(m.ChangeRate.X), MathF.Max(MathF.Abs(m.ChangeRate.Y), MathF.Abs(m.ChangeRate.Z)))
        },
        _ => null   // collisions, forces, orbits, events: not played
    };

    // --- Evaluators ----------------------------------------------------------------------------

    public static FxValue Float(IEvaluatorFloat? evaluator) => evaluator switch
    {
        CEvaluatorFloatConst c => FxValue.Constant((float)c.Value),
        CEvaluatorFloatRandomUniform r => new() { Kind = FxValue.Mode.Random, A = new((float)r.Min), B = new((float)r.Max), PerChannel = false },
        CEvaluatorFloatStartEnd s => new() { Kind = FxValue.Mode.StartEnd, A = new((float)s.Start), B = new((float)s.End) },
        CEvaluatorFloatCurve c => Curve(c.Curves, 1),
        _ => FxValue.Zero
    };

    public static FxValue Vector(IEvaluatorVector? evaluator) => evaluator switch
    {
        CEvaluatorVectorConst c => new() { Kind = FxValue.Mode.Constant, A = V(c.Value), FreeAxes = Axes(c.FreeAxes), Spill = c.Spill },
        CEvaluatorVectorRandomUniform r => new()
        {
            Kind = FxValue.Mode.Random, A = V(r.Min), B = V(r.Max), FreeAxes = Axes(r.FreeAxes), Spill = r.Spill, LockX = r.LockX, LockY = r.LockY, LockZ = r.LockZ, LockW = r.LockW
        },
        CEvaluatorVectorStartEnd s => new() { Kind = FxValue.Mode.StartEnd, A = V(s.Start), B = V(s.End), FreeAxes = Axes(s.FreeAxes), Spill = s.Spill },
        CEvaluatorVectorCurve c => With(Curve(c.Curves, 4), Axes(c.FreeAxes), c.Spill),
        CEvaluatorVectorMultiCurve c => With(MultiCurve(c.Curves), Axes(c.FreeAxes), c.Spill),
        _ => FxValue.Zero
    };

    // Colors: 0..1 per channel; only their red, green and blue reach the particles.
    public static FxValue Color(IEvaluatorColor? evaluator) => evaluator switch
    {
        CEvaluatorColorConst c => FxValue.Constant(C(c.Value)),
        CEvaluatorColorRandom r => new() { Kind = FxValue.Mode.Random, A = C(r.Min), B = C(r.Max), PerChannel = r.RandomPerChannel },
        CEvaluatorColorStartEnd s => new() { Kind = FxValue.Mode.StartEnd, A = C(s.Start), B = C(s.End) },
        CEvaluatorColorCurve c => Curve(c.Curves, 4),
        CEvaluatorColorMultiCurve c => MultiCurve(c.Curves),
        _ => FxValue.One
    };

    private static FxValue With(FxValue value, int axes, bool spill) => new()
    {
        Kind = value.Kind, A = value.A, B = value.B, Times = value.Times, Values = value.Values, FreeAxes = axes, Spill = spill
    };

    private static int Axes(Enum free) => free.ToString() switch { "FVA_One" => 1, "FVA_Two" => 2, "FVA_Three" => 3, _ => 4 };
    private static Vector4 V(WolvenKit.RED4.Types.Vector4 v) => new(v.X, v.Y, v.Z, v.W);
    private static Vector4 C(CColor c) => new(c.Red / 255f, c.Green / 255f, c.Blue / 255f, c.Alpha / 255f);

    // A keyed curve: its points in time order; every channel shares the keys.
    private static FxValue Curve(IEnumerable? points, int channels)
    {
        if (points is null) return channels == 1 ? FxValue.Zero : FxValue.Constant(Vector4.Zero);
        var keys = new List<(float Time, Vector4 Value)>();
        foreach (var item in points)
        {
            if (item is not IRedCurvePoint point) continue;
            Vector4 value = point.GetValue() switch
            {
                CFloat f => new((float)f), WolvenKit.RED4.Types.Vector4 v => V(v), WolvenKit.RED4.Types.Vector3 v => new(v.X, v.Y, v.Z, 0),
                HDRColor h => new(h.Red, h.Green, h.Blue, h.Alpha), CColor c => C(c), _ => default
            };
            keys.Add(((float)point.GetPoint(), value));
        }
        if (keys.Count == 0) return FxValue.Zero;
        keys.Sort((a, b) => a.Time.CompareTo(b.Time));
        if (keys.Count == 1) return FxValue.Constant(keys[0].Value);
        var times = keys.Select(k => k.Time).ToArray();
        var values = new float[channels][];
        for (int c = 0; c < channels; c++) values[c] = keys.Select(k => c switch { 0 => k.Value.X, 1 => k.Value.Y, 2 => k.Value.Z, _ => k.Value.W }).ToArray();
        return new() { Kind = FxValue.Mode.Curve, Times = Enumerable.Repeat(times, channels).ToArray(), Values = values, FreeAxes = channels == 1 ? 1 : 4 };
    }

    // A multi-channel curve's buffer: per channel a key count and the offsets of its times and
    // of its values, then the floats. Keys are read as straight segments: curved kinds (Bezier,
    // Hermite) keep their control values beside each key, of which the first is the key's own.
    private static FxValue MultiCurve(IRedMultiChannelCurve? curve)
    {
        if (curve is null) return FxValue.Zero;
        var data = curve.Data; int channels = (int)Math.Min(curve.NumChannels, 4u);
        if (data is null || channels == 0 || data.Length < channels * 12) return FxValue.Zero;
        var times = new float[channels][]; var values = new float[channels][];
        var offsets = new List<int> { data.Length };
        for (int c = 0; c < (int)curve.NumChannels && c * 12 + 12 <= data.Length; c++)
        { offsets.Add(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(c * 12 + 4))); offsets.Add(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(c * 12 + 8))); }
        offsets.Sort();
        for (int c = 0; c < channels; c++)
        {
            int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(c * 12)), timeAt = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(c * 12 + 4)), valueAt = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(c * 12 + 8));
            if (count <= 0 || count > 4096 || timeAt < 0 || valueAt < 0 || timeAt + 4 * count > data.Length) { times[c] = [0]; values[c] = [0]; continue; }
            int next = offsets.First(o => o > valueAt), stride = Math.Max(1, (next - valueAt) / 4 / count);
            times[c] = new float[count]; values[c] = new float[count];
            for (int i = 0; i < count; i++)
            {
                times[c][i] = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(timeAt + 4 * i));
                int at = valueAt + 4 * i * stride;
                values[c][i] = at + 4 <= data.Length ? BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(at)) : 0;
            }
        }
        return new() { Kind = FxValue.Mode.Curve, Times = times, Values = values };
    }

    // --- Materials -----------------------------------------------------------------------------

    // The emitter's own material instance over what it derives from (instances first, then templates).
    private static FxMaterial Material(CParticleEmitter e, FxDrawer drawer, Func<string, CMaterialInstance?>? instances)
    {
        var chain = new List<CMaterialInstance>();
        string template = e.Material.DepotPath.GetResolvedText() ?? "";
        if (e.LocalMaterialInstance?.Chunk is CMaterialInstance local) { chain.Add(local); template = local.BaseMaterial.DepotPath.GetResolvedText() ?? template; }
        for (int depth = 0; depth < 6 && template.EndsWith(".mi", StringComparison.OrdinalIgnoreCase) && instances?.Invoke(template) is { } parent; depth++)
        { chain.Add(parent); template = parent.BaseMaterial.DepotPath.GetResolvedText() ?? ""; }
        string name = System.IO.Path.GetFileNameWithoutExtension(template).ToLowerInvariant();
        // Only the templates whose shaders the renderer ports (and their variants with the same
        // parameters); the others (lightning, liquids, heat haze, holograms...) are not drawn.
        // Mesh particles: the solid ones (debris, on the game's metal_base surfaces). Meshes worn by
        // lightning, holograms, glass or particle shaders are not solid things: left out.
        var shader = drawer == FxDrawer.Mesh ? (name.StartsWith("metal_base", StringComparison.Ordinal) ? FxShader.Mesh : FxShader.Unsupported) : name switch
        {
            "blackbodyradiation" or "blackbodyradiation_notxaa" or "blackbodyradiation_distant" or "balckbodyradiation_v2" or "blackbody_lit" or "blackbody_simple" => FxShader.Blackbody,
            "particles_generic" or "particles_generic_expanded" => FxShader.Generic,
            _ => FxShader.Unsupported
        };
        var material = new FxMaterial { Shader = shader, Template = template };
        for (int i = chain.Count - 1; i >= 0; i--)
            foreach (var pair in chain[i].Values)
            {
                string key = pair.Key.ToString() ?? "";
                switch (pair.Value)
                {
                    case CFloat f: material.Scalars[key] = f; break;
                    case CColor c: material.Vectors[key] = C(c); break;
                    case WolvenKit.RED4.Types.Vector4 v: material.Vectors[key] = V(v); break;
                    case IRedResourceReference reference when reference.DepotPath.GetResolvedText() is { Length: > 0 } path: material.Textures[key] = path; break;
                }
            }
        return material;
    }

    // --- Effects -------------------------------------------------------------------------------

    public static FxEffect ReadEffect(worldEffect effect, string path)
    {
        var particles = new List<FxEffectParticles>(); var sounds = new List<FxEffectSound>(); var lights = new List<FxEffectLight>(); var skipped = new SortedSet<string>(StringComparer.Ordinal);
        var items = new List<effectTrackItem>();
        void Walk(effectTrackBase? track)
        {
            switch (track)
            {
                case effectTrackGroup group: foreach (var child in group.Tracks) Walk(child.Chunk); break;
                case effectTrack leaf: foreach (var item in leaf.Items) if (item.Chunk is { } i) items.Add(i); break;
            }
        }
        Walk(effect.TrackRoot?.Chunk);
        if (items.Count == 0) foreach (var item in effect.Events) if (item.Chunk is { } i) items.Add(i);
        foreach (var item in items)
        {
            switch (item)
            {
                case effectTrackItemParticles p when p.ParticleSystem.DepotPath.GetResolvedText() is { Length: > 0 } system:
                    particles.Add(new()
                    {
                        System = system, Begin = Math.Max(0, p.TimeBegin), Duration = Math.Max(0, p.TimeDuration),
                        EmissionScale = Float(p.EmissionScale.Evaluator?.Chunk, 1), Alpha = Float(p.Alpha.Evaluator?.Chunk, 1), Size = Float(p.Size.Evaluator?.Chunk, 1), Velocity = Float(p.Velocity.Evaluator?.Chunk, 1)
                    });
                    break;
                case effectTrackItemSound s when s.EventName.ToString() is { Length: > 0 } name && name != "None":
                    sounds.Add(new() { Event = name, Begin = Math.Max(0, s.TimeBegin) }); break;
                case effectTrackItemPointLight l:
                    lights.Add(new()
                    {
                        Begin = Math.Max(0, l.TimeBegin), Duration = Math.Max(0, l.TimeDuration), Offset = new(l.Offset.X, l.Offset.Y, l.Offset.Z),
                        Color = new(l.Color.Red / 255f, l.Color.Green / 255f, l.Color.Blue / 255f), Radius = Float(l.Radius.Evaluator?.Chunk, 1), Intensity = Float(l.Intensity.Evaluator?.Chunk, 1), Ev = l.EV
                    });
                    break;
                case effectTrackItemLoopMarker: break;
                default: skipped.Add(item.GetType().Name.Replace("effectTrackItem", "")); break;
            }
        }
        (float, float)? loop = effect.EffectLoops.Count > 0 && effect.EffectLoops[0].EndTime > effect.EffectLoops[0].StartTime
            ? (Math.Max(0, effect.EffectLoops[0].StartTime), effect.EffectLoops[0].EndTime) : null;
        return new FxEffect
        {
            Path = path, Length = Math.Max(effect.Length, 1e-2f), Loop = loop, Particles = particles.ToArray(), Sounds = sounds.ToArray(), Lights = lights.ToArray(), Skipped = skipped.ToArray()
        };
    }

    private static FxValue Float(IEvaluatorFloat? evaluator, float fallback) => evaluator is null ? FxValue.Constant(fallback) : Float(evaluator);
}
