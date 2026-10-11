// SPDX-License-Identifier: GPL-3.0-only
using NCWE.Core;
using WolvenKit.RED4.Archive.IO;
using WolvenKit.RED4.Types;

namespace NCWE.Formats;

// Nodes for editor elements. A template (a game node read fresh from its sector) keeps every
// property the editor does not model; otherwise the node starts from the values shipping
// 2.31 nodes use. The modelled properties are applied on top.
public static class ElementNodes
{
    // doorTemplate: the door's .ent read fresh, whose controller component and Door entity are
    // copied into new instance data when the node has none.
    public static worldNode Create(AuthoredElement element, worldNode? template, entEntityTemplate? doorTemplate = null)
    {
        var p = element.Properties;
        worldNode node = template ?? p.Kind switch
        {
            ElementKind.Door or ElementKind.Entity => new worldEntityNode { IoPriority = Enums.entEntitySpawnPriority.Immediate },
            ElementKind.Occluder => new worldStaticOccluderMeshNode { IsVisibleInGame = true },
            ElementKind.Light => NewLight(),
            ElementKind.Particle => new worldStaticParticleNode { ForcedAutoHideDistance = -1, ForcedAutoHideRange = -1 },
            ElementKind.Effect => new worldEffectNode(),
            ElementKind.Decal => new worldStaticDecalNode
            {
                AutoHideDistance = 25, ForcedAutoHideDistance = 25, OrderNo = 1, IsStretchingEnabled = false,
                NormalsBlendingMode = Enums.RenderDecalNormalsBlendingMode.AlphaBlending, DecalRenderMode = Enums.EDecalRenderMode.DRM_AllStatic
            },
            _ => throw new InvalidDataException("A sound emitter needs a template node.")
        };
        if (node.GetType().Name != p.NodeType() && !(p.Kind is ElementKind.Door or ElementKind.Entity && node is worldEntityNode)) throw new InvalidDataException($"Template for {element.Name} is not a {p.NodeType()}.");
        node.DebugName = element.Name;
        switch (node)
        {
            case worldStaticLightNode light:
                light.Type = p.LightType switch { LightKind.Spot => Enums.ELightType.LT_Spot, LightKind.Area => Enums.ELightType.LT_Area, _ => Enums.ELightType.LT_Point };
                light.Color = new CColor { Red = Byte(p.Color.X), Green = Byte(p.Color.Y), Blue = Byte(p.Color.Z), Alpha = 255 };
                light.Temperature = p.Temperature; light.Intensity = p.Intensity; light.Radius = p.Radius;
                light.InnerAngle = p.InnerAngle; light.OuterAngle = p.OuterAngle; light.EnableLocalShadows = p.Shadows;
                if (template is null) light.AutoHideDistance = Math.Max(30, p.Radius * 8);
                break;
            case worldStaticParticleNode particle:
                particle.ParticleSystem = new CResourceAsyncReference<CParticleSystem>(p.Resource);
                break;
            case worldEffectNode effect:
                effect.Effect = new CResourceAsyncReference<worldEffect>(p.Resource);
                break;
            case worldStaticSoundEmitterNode sound:
                sound.Radius = p.Radius;
                break;
            case worldEntityNode door when p.Kind is ElementKind.Door or ElementKind.Entity:
                door.EntityTemplate = new CResourceAsyncReference<entEntityTemplate>(p.Resource);
                door.AppearanceName = p.Appearance.Length > 0 ? p.Appearance : "default";
                if (p.Door is { Decorative: false } settings) ApplyDoor(door, settings, doorTemplate);
                break;
            case worldStaticOccluderMeshNode occluder:
                occluder.Mesh = new CResourceAsyncReference<CMesh>(p.Resource);
                occluder.OccluderType = Enum.Parse<Enums.visWorldOccluderType>(p.OccluderType);
                occluder.AutohideDistanceScale = (byte)p.AutohideScale;
                break;
            case worldStaticDecalNode decal:
                // A new decal is seen as far as its size deserves (a copied node keeps its own distance).
                if (template is null) decal.AutoHideDistance = decal.ForcedAutoHideDistance = DecalDistances.AutoHide(element.Transform.Scale);
                decal.Material = new CResourceAsyncReference<IMaterial>(p.Resource);
                decal.Alpha = p.Alpha;
                decal.DiffuseColorScale = new HDRColor { Red = p.Color.X, Green = p.Color.Y, Blue = p.Color.Z, Alpha = 1 };
                break;
        }
        return node;
    }

    private static byte Byte(float v) => (byte)Math.Clamp(MathF.Round(v * 255), 0, 255);

    // Door behaviour lives in the node's instance data: the DoorController component (its
    // DoorControllerPS: type, initial state, automatic close, speed, lock toggle) and the Door
    // entity (opening type, close delay). Missing chunks are copied from the template.
    public static void ApplyDoor(worldEntityNode node, DoorSettings door, entEntityTemplate? template)
    {
        var instance = node.InstanceData?.Chunk;
        if (instance is null) { instance = new entEntityInstanceData(); node.InstanceData = new CHandle<entEntityInstanceData>(instance); }
        instance.Buffer ??= new DataBuffer();
        if (instance.Buffer.Data is not WolvenKit.RED4.Archive.Buffer.RedPackage package)
            instance.Buffer.Data = package = new WolvenKit.RED4.Archive.Buffer.RedPackage();
        var controller = package.Chunks.OfType<DoorController>().FirstOrDefault();
        if (controller is null && template?.Components?.OfType<DoorController>().FirstOrDefault() is { } copied) { package.Chunks.Add(copied); controller = copied; }
        if (controller?.PersistentState?.Chunk is DoorControllerPS ps)
        {
            var setup = ps.DoorProperties ??= new DoorSetup();
            setup.DoorType = Enum.Parse<Enums.EDoorType>(door.DoorType);
            setup.InitialDoorState = Enum.Parse<Enums.EDoorStatus>(door.InitialState);
            setup.AutomaticallyClosesItself = door.ClosesAutomatically;
            setup.OpeningSpeed = door.OpeningSpeed;
            setup.CanPlayerToggleLockState = door.PlayerCanToggleLock;
        }
        // The Door entity is only carried when it overrides the template (13 of 20 shipping doors
        // around Watson have the controller alone), and shipping packages list it first:
        // [Door, DoorController, ...].
        var entity = package.Chunks.OfType<Door>().FirstOrDefault();
        if (entity is null && template?.Entity?.Chunk is Door templateEntity
            && (templateEntity.DoorOpeningType.ToString() != door.Opening || MathF.Abs(templateEntity.AutomaticCloseDelay - door.CloseDelay) > 1e-3f))
        { package.Chunks.Insert(0, templateEntity); entity = templateEntity; }
        if (entity is not null)
        {
            entity.DoorOpeningType = Enum.Parse<Enums.EDoorOpeningType>(door.Opening);
            entity.AutomaticCloseDelay = door.CloseDelay;
            if (package.Chunks.IndexOf(entity) > 0) { package.Chunks.Remove(entity); package.Chunks.Insert(0, entity); }
        }
    }

    // Values of shipping point lights (2.31), so a new light behaves like the game's own.
    private static worldStaticLightNode NewLight()
    {
        var light = new worldStaticLightNode
        {
            Unit = Enums.ELightUnit.LU_Lumen, SceneDiffuse = true, SceneSpecularScale = 100, ScaleEnvProbes = 100, UseInTransparents = true, UseInParticles = true,
            Attenuation = Enums.rendLightAttenuation.LA_InverseSquare, ClampAttenuation = true, Group = Enums.rendLightGroup.LG_Group0,
            AreaShape = Enums.EAreaLightShape.ALS_Capsule, AreaTwoSided = true, SourceRadius = .05f, CapsuleLength = 1, AreaRectSideA = 1, AreaRectSideB = 1,
            Softness = 2, ContactShadows = Enums.rendContactShadowReciever.CSR_None, ShadowAngle = -1, ShadowRadius = -1, ShadowFadeDistance = 10, ShadowFadeRange = 5,
            ShadowSoftnessMode = Enums.ELightShadowSoftnessMode.LSSM_Default, RayTracedShadowsPlatform = Enums.rendRayTracedShadowsPlatform.RLSP_All,
            RayTracingLightSourceRadius = -1, RayTracingContactShadowRange = -1, EnvColorGroup = Enums.EEnvColorGroup.ECG_Default, ColorGroupSaturation = 100,
            RayTracingIntensityScale = 1, PathTracingLightUsage = Enums.rendEPathTracingLightUsage.PTLU_Everywhere, PathTracingOverrideScaleGI = true,
            RtxdiShadowStartingDistance = -1, IsVisibleInGame = true
        };
        Enums.rendLightChannel channels = 0;
        foreach (var channel in Enum.GetValues<Enums.rendLightChannel>()) channels |= channel;
        light.LightChannel = channels;
        light.Flicker = new rendSLightFlickering { PositionOffset = 0, FlickerStrength = 0, FlickerPeriod = .2f };
        return light;
    }
}

public sealed partial class GameArchiveIndex
{
    // An entity template read fresh (its objects are copied into instance data).
    public entEntityTemplate? ReadEntityTemplate(ulong hash)
    {
        if (EntryOrNull(hash) is not { } entry) return null;
        using var data = ExtractEntry(entry, default);
        var reader = new CR2WReader(data);
        return reader.ReadFile(out var file) == EFileReadErrorCodes.NoError ? file?.RootChunk as entEntityTemplate : null;
    }

    // A game node read fresh from its sector, to be copied into an authored sector. The
    // anchor guards (node type, placement count) must still match the installed sector.
    public worldNode ReadTemplateNode(SourceAnchor anchor)
    {
        var entry = EntryOrNull(anchor.Sector.Hash) ?? throw new FileNotFoundException("Template sector is not installed: " + anchor.Sector.Path);
        using var data = ExtractEntry(entry, default);
        var reader = new CR2WReader(data);
        reader.ParsingError += SectorReader.TolerateSectorParsingError;
        if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file?.RootChunk is not worldStreamingSector sector)
            throw new InvalidDataException("Template sector could not be read: " + anchor.Sector.Path);
        if (sector.NodeData.Buffer.Data is not WolvenKit.RED4.Archive.Buffer.worldNodeDataBuffer setups || setups.Count != anchor.ExpectedNodes
            || anchor.PlacementIndex >= setups.Count || setups[anchor.PlacementIndex].NodeIndex != anchor.NodeIndex)
            throw new InvalidDataException("Template placement guards changed: " + anchor.ObjectId);
        var node = sector.Nodes[anchor.NodeIndex].Chunk ?? throw new InvalidDataException("Template node is empty: " + anchor.ObjectId);
        if (node.GetType().Name != anchor.NodeType) throw new InvalidDataException("Template node type changed: " + anchor.ObjectId);
        // The file is not disposed: the node graph (sound settings...) stays owned by it.
        return node;
    }
}
