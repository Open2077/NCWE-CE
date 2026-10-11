// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Numerics;

namespace NCWE.Core;

// Non-mesh world elements the editor can place and edit: lights, particle systems, effects,
// sound emitters and decals. Each becomes one node of its game type at export.
// Door: an entity node (worldEntityNode) from a door template (.ent) with an appearance.
// Entity: any other entity template (props, devices, vehicles…) placed with an appearance.
// Occluder: a worldStaticOccluderMeshNode (box or plane occluder mesh, scaled by its transform).
// CustomSound: an audio file of the user's custom sound library, played by Open77 (no game node:
// only an Open77 export carries it, as resource files and a generated Lua script).
public enum ElementKind { Light, Particle, Effect, Sound, Decal, Door, Entity, Occluder, CustomSound }
public enum LightKind { Point, Spot, Area }

// The editable properties, in game units. Everything else comes from the template node (a
// game node copied at export) or from shipping defaults.
public sealed record ElementProperties(ElementKind Kind)
{
    public LightKind LightType { get; init; } = LightKind.Point;
    // Light color (sRGB 0..1), used when Temperature is not positive; decal tint.
    public Vector3 Color { get; init; } = Vector3.One;
    // Color temperature in kelvin; a positive value replaces the color (as in the game).
    public float Temperature { get; init; } = -1;
    public float Intensity { get; init; } = 100;   // lumen
    public float Radius { get; init; } = 6;        // light / sound reach, metres (custom sound: silent beyond)
    public float InnerAngle { get; init; } = 30;   // spot cone, degrees
    public float OuterAngle { get; init; } = 45;
    public bool Shadows { get; init; }
    // .particle (Particle), .effect (Effect), decal material .mi (Decal) or entity template .ent (Door).
    public string Resource { get; init; } = "";
    // Door: the template appearance and the door behaviour.
    public string Appearance { get; init; } = "";
    public DoorSettings? Door { get; init; }
    public float Alpha { get; init; } = 1;         // decal opacity
    // Occluder: visWorldOccluderType and autohide distance scale (255 = default).
    public string OccluderType { get; init; } = "Default";
    public int AutohideScale { get; init; } = 255;
    // CustomSound: which sound and how Open77 plays it.
    public SoundSettings? Audio { get; init; }
    // Sound: the game event the emitter plays while it is active (read from the node it copies;
    // "" when it plays none). The editor plays it for listening; the export keeps the template's own.
    public string SoundEvent { get; init; } = "";
    public static readonly string BoxOccluder = @"engine\meshes\editor\box_occluder.w2mesh", PlaneOccluder = @"engine\meshes\editor\plane_occluder_twosided.mesh";

    public static string NodeType(ElementKind kind) => kind switch
    {
        ElementKind.Light => "worldStaticLightNode",
        ElementKind.Particle => "worldStaticParticleNode",
        ElementKind.Effect => "worldEffectNode",
        ElementKind.Sound => "worldStaticSoundEmitterNode",
        ElementKind.Door or ElementKind.Entity => "worldEntityNode",
        ElementKind.Occluder => "worldStaticOccluderMeshNode",
        ElementKind.CustomSound => "",
        _ => "worldStaticDecalNode"
    };
    public string NodeType() => NodeType(Kind);

    public string ResourceExtension => Kind switch { ElementKind.Particle => ".particle", ElementKind.Effect => ".effect", ElementKind.Decal => ".mi", ElementKind.Door or ElementKind.Entity => ".ent", _ => "" };

    public ElementProperties Validated()
    {
        static bool Finite(float v) => float.IsFinite(v);
        if (!Finite(Color.X) || !Finite(Color.Y) || !Finite(Color.Z) || Color.X < 0 || Color.Y < 0 || Color.Z < 0) throw new InvalidDataException("Element color must be finite and positive.");
        if (!Finite(Temperature) || Temperature > 40000) throw new InvalidDataException("Light temperature must be at most 40,000 K.");
        if (!Finite(Intensity) || Intensity < 0 || Intensity > 1_000_000) throw new InvalidDataException("Light intensity must be between 0 and 1,000,000 lm.");
        if (!Finite(Radius) || Radius <= 0 || Radius > 1000) throw new InvalidDataException("Radius must be between 0 and 1,000 m.");
        // Shipping lights use angles up to ~210° (area lights); only spots read them as a cone.
        if (!Finite(InnerAngle) || !Finite(OuterAngle) || InnerAngle < 0 || OuterAngle < 0 || InnerAngle > 360 || OuterAngle > 360) throw new InvalidDataException("Light angles must be between 0 and 360°.");
        if (!Finite(Alpha) || Alpha < 0 || Alpha > 1) throw new InvalidDataException("Decal opacity must be between 0 and 1.");
        if (ResourceExtension.Length > 0 && !Resource.EndsWith(ResourceExtension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"A {Kind} element needs a {ResourceExtension} resource.");
        if (Kind == ElementKind.Occluder)
        {
            if (!Resource.EndsWith(".mesh", StringComparison.OrdinalIgnoreCase) && !Resource.EndsWith(".w2mesh", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("An occluder needs an occluder mesh.");
            _ = new MeshOccluder(OccluderType, AutohideScale).Validated();
        }
        if (Kind == ElementKind.Door) return this with { Door = (Door ?? new DoorSettings()).Validated() };
        if (Kind == ElementKind.CustomSound) return this with { Audio = (Audio ?? throw new InvalidDataException("A custom sound needs a sound of the library.")).Validated(Radius) };
        return this;
    }

    // Color the game uses: the temperature when positive, otherwise the color.
    public Vector3 DisplayColor => Kind == ElementKind.Light && Temperature > 0 ? Kelvin(Temperature) : Color;

    // Blackbody approximation (Tanner Helland), sRGB 0..1.
    public static Vector3 Kelvin(float kelvin)
    {
        float t = Math.Clamp(kelvin, 1000, 40000) / 100;
        float r = t <= 66 ? 255 : 329.698727446f * MathF.Pow(t - 60, -.1332047592f);
        float g = t <= 66 ? 99.4708025861f * MathF.Log(t) - 161.1195681661f : 288.1221695283f * MathF.Pow(t - 60, -.0755148492f);
        float b = t >= 66 ? 255 : t <= 19 ? 0 : 138.5177312231f * MathF.Log(t - 10) - 305.0447927307f;
        return Vector3.Clamp(new Vector3(r, g, b) / 255, Vector3.Zero, Vector3.One);
    }

    // Defaults for a new element of each kind.
    public static ElementProperties Preset(ElementKind kind, LightKind light = LightKind.Point) => kind switch
    {
        ElementKind.Light => new(kind) { LightType = light, Temperature = 4000, Intensity = light == LightKind.Spot ? 400 : 200, Radius = light == LightKind.Spot ? 10 : 6 },
        ElementKind.Sound => new(kind) { Radius = 5 },
        ElementKind.Occluder => new(kind) { Resource = BoxOccluder },
        _ => new(kind)
    };
}

public sealed record AuthoredElement(string Id, string Name, ElementProperties Properties, PlacementTransform Transform)
{
    // A game node copied at export for every property not modelled here (sound settings,
    // light channels, flicker...). Sounds always need one.
    public SourceAnchor? Template { get; init; }
    // Game nodes this element replaces (they are deleted while it exists).
    public ImmutableArray<string> Replaces { get; init; } = [];

    public static string NewId() => "e:" + Guid.NewGuid().ToString("N");

    public AuthoredElement Validated()
    {
        if (!Id.StartsWith("e:", StringComparison.Ordinal) || Id.Length < 4) throw new InvalidDataException("An element needs an e: identity.");
        if (string.IsNullOrWhiteSpace(Name)) throw new InvalidDataException("An element needs a name.");
        var properties = Properties.Validated();
        if (properties.Kind == ElementKind.Sound && Template is null) throw new InvalidDataException("A sound emitter is created from an existing one (its audio settings are copied).");
        if (properties.Kind == ElementKind.CustomSound && Template is not null) throw new InvalidDataException("A custom sound is not a copy of a game node.");
        if (Template is { } template && template.NodeType != properties.NodeType() && !(properties.Kind is ElementKind.Door or ElementKind.Entity && template.NodeType == "worldDeviceNode"))
            throw new InvalidDataException("The template node is not of the element's type.");
        return this with { Properties = properties, Transform = Transform.Validated(), Replaces = Replaces.IsDefault ? [] : Replaces };
    }
}

public enum SoundPlayback { Loop, Interval }

// How Open77 plays a custom sound: within MinDistance at full volume, fading linearly to silence
// at the element's Radius (Open77 PackageAudio). Looped, or played again after a random pause.
// Synced loops run on the server, one timeline for every player (Open77 allows 16 per resource);
// the others are started by each client near its player.
public sealed record SoundSettings
{
    public string Sound { get; init; } = "";        // custom sound library id
    public float Volume { get; init; } = 1;         // 0..1
    public float MinDistance { get; init; } = 1;    // metres
    public SoundPlayback Playback { get; init; } = SoundPlayback.Loop;
    public float IntervalMin { get; init; } = 10;   // seconds of silence between two plays (Interval)
    public float IntervalMax { get; init; } = 30;
    public bool Synced { get; init; }

    public SoundSettings Validated(float maxDistance)
    {
        if (!CustomMeshPaths.IsValidId(Sound)) throw new InvalidDataException("A custom sound needs a sound of the library.");
        if (!float.IsFinite(Volume) || Volume < 0 || Volume > 1) throw new InvalidDataException("Sound volume must be between 0 and 1.");
        if (!float.IsFinite(maxDistance) || maxDistance > Open77Audio.MaxDistance) throw new InvalidDataException($"A custom sound is heard up to {Open77Audio.MaxDistance:0} m at most.");
        if (!float.IsFinite(MinDistance) || MinDistance < 0 || MinDistance >= maxDistance) throw new InvalidDataException("The full-volume distance must be smaller than the reach.");
        if (Playback == SoundPlayback.Interval && (!float.IsFinite(IntervalMin) || !float.IsFinite(IntervalMax) || IntervalMin < 0 || IntervalMax < IntervalMin || IntervalMax > 3600))
            throw new InvalidDataException("Pauses between plays are 0 to 3,600 seconds, the shortest first.");
        if (!Enum.IsDefined(Playback)) throw new InvalidDataException("Unknown sound playback.");
        return this with { Synced = Synced && Playback == SoundPlayback.Loop };
    }
}
