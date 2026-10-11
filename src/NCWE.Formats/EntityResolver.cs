// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using System.Collections.Immutable;
using NCWE.Core;
using WolvenKit.RED4.Archive.Buffer;
using WolvenKit.RED4.Archive.IO;
using WolvenKit.RED4.Types;
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;

namespace NCWE.Formats;

// What an entity node looks like: the meshes of its appearance (template components plus the
// appearance's components from its .app), composed through their parent bindings, and the
// door behaviour when the entity is a door.
public sealed record EntityVisual(ImmutableArray<EntityPart> Parts, string Appearance, ImmutableArray<string> Appearances, string EntityClass, DoorSettings? Door);

public sealed partial class GameArchiveIndex
{
    // Platform: a moving platform (an elevator cabin's root): what is bound to it travels with it.
    // Slot: the slot of the parent it hangs from (a slot component's, mapped to a bone of a rig);
    // Slots: a slot component's slot → bone; Rig: an animated component's rig.
    private sealed record ComponentInfo(string Name, MeshReference? Mesh, PlacementTransform Local, string? Parent, bool Platform = false)
    {
        public string? Slot { get; init; }
        public IReadOnlyDictionary<string, string>? Slots { get; init; }
        public ulong Rig { get; init; }
    }
    private readonly ConcurrentDictionary<ulong, Lazy<Dictionary<string, PlacementTransform?>?>> _rigBones = new();
    private sealed record TemplateInfo(string EntityClass, DoorSettings? Door, string DefaultAppearance,
        (string Name, ulong App, string AppName)[] Appearances, ComponentInfo[] Components);

    private readonly ConcurrentDictionary<(ulong, string), Lazy<EntityVisual?>> _entityVisuals = new();
    private readonly ConcurrentDictionary<ulong, Lazy<TemplateInfo?>> _entityTemplates = new();
    private readonly ConcurrentDictionary<ulong, Lazy<Dictionary<string, ComponentInfo[]>?>> _appearanceResources = new();
    // Why an entity could not be resolved (diagnostics).
    public ConcurrentQueue<string> EntityErrors { get; } = new();

    // Thread-safe and cached; null when the template is missing or unreadable.
    public EntityVisual? ResolveEntity(ulong template, string appearance)
        => template == 0 ? null : _entityVisuals.GetOrAdd((template, appearance ?? ""), key => new(() => Resolve(key.Item1, key.Item2))).Value;

    private EntityVisual? Resolve(ulong templateHash, string appearance)
    {
        try
        {
            if (TemplateOf(templateHash) is not { } template) return null;
            string requested = appearance is "" or "default" ? template.DefaultAppearance : appearance;
            var chosen = template.Appearances.FirstOrDefault(a => a.Name == requested);
            if (chosen.Name is null) chosen = template.Appearances.FirstOrDefault(a => a.Name == template.DefaultAppearance);
            if (chosen.Name is null && template.Appearances.Length > 0) chosen = template.Appearances[0];
            var components = new List<ComponentInfo>(template.Components);
            if (chosen.App != 0 && AppearanceComponents(chosen.App) is { } definitions)
            {
                if (definitions.TryGetValue(chosen.AppName, out var list) || (definitions.Count == 1 && definitions.Values.First() is var only && (list = only) is not null))
                    components.AddRange(list);
            }
            return new(Compose(components, RigBones), chosen.Name ?? requested, [.. template.Appearances.Select(a => a.Name)], template.EntityClass, template.Door);
        }
        catch (Exception ex) { EntityErrors.Enqueue($"{templateHash:x16}/{appearance}: {ex.GetType().Name}: {ex.Message} {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}"); return null; }
    }

    // Component transforms are relative to their parent binding (by component name); a component
    // bound to a slot that names a bone of a rig (sliding door leaves) hangs from that bone.
    private static ImmutableArray<EntityPart> Compose(List<ComponentInfo> components, Func<ulong, Dictionary<string, PlacementTransform?>?>? rigs = null)
    {
        var byName = new Dictionary<string, ComponentInfo>(StringComparer.Ordinal);
        foreach (var c in components) byName.TryAdd(c.Name, c);
        PlacementTransform World(ComponentInfo c, int depth)
        {
            if (c.Parent is not { Length: > 0 } parent || depth >= 16 || !byName.TryGetValue(parent, out var p) || ReferenceEquals(p, c)) return c.Local;
            var at = World(p, depth + 1);
            if (BonePose(c, p) is { } pose) at = at.Apply(pose);
            return at.Apply(c.Local);
        }
        // The bone a slot-bound part hangs from: null when it hangs from no bone; Unknown when the
        // bone's pose cannot be told (the part is left out rather than drawn somewhere wrong).
        PlacementTransform? BonePose(ComponentInfo c, ComponentInfo slotComponent)
        {
            if (c.Slot is not { } slot || slotComponent.Slots?.GetValueOrDefault(slot) is not { } bone || slotComponent.Parent is not { } animated
                || !byName.TryGetValue(animated, out var a) || a.Rig == 0 || rigs?.Invoke(a.Rig) is not { } bones || !bones.TryGetValue(bone, out var pose)) return null;
            return pose ?? Unknown;
        }
        bool Hidden(ComponentInfo c, int depth)
            => c.Parent is { Length: > 0 } parent && depth < 16 && byName.TryGetValue(parent, out var p) && !ReferenceEquals(p, c)
               && (BonePose(c, p) == Unknown || Hidden(p, depth + 1));
        bool OnPlatform(ComponentInfo c, int depth)
            => c.Platform || (c.Parent is { Length: > 0 } parent && depth < 16 && byName.TryGetValue(parent, out var p) && !ReferenceEquals(p, c) && OnPlatform(p, depth + 1));
        return [.. components.Where(c => c.Mesh is not null && !Hidden(c, 0)).Take(96).Select(c => new EntityPart(c.Mesh!, World(c, 0)) { Moving = OnPlatform(c, 0) })];
    }

    private TemplateInfo? TemplateOf(ulong hash) => _entityTemplates.GetOrAdd(hash, h => new(() =>
    {
        if (EntryOrNull(h) is not { } entry) return null;
        using var data = ExtractEntry(entry, default);
        using var reader = new CR2WReader(data);
        if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file is null) return null;
        using (file)
        {
            if (file.RootChunk is not entEntityTemplate template) return null;
            var entity = template.Entity?.Chunk;
            var components = (template.Components ?? []).Select(c => Component(c)).OfType<ComponentInfo>().ToArray();
            DoorSettings? door = entity switch
            {
                FakeDoor => new() { Decorative = true, DoorType = "NONE" },
                Door d => ApplyEntity(new DoorSettings(), d),
                _ => null
            };
            if (door is { Decorative: false } && template.Components.OfType<DoorController>().FirstOrDefault()?.PersistentState.Chunk is DoorControllerPS ps)
                door = ApplySetup(door, ps.DoorProperties);
            var appearances = (template.Appearances ?? []).OfType<entTemplateAppearance>()
                .Select(a => (a.Name.ToString() ?? "", (ulong)a.AppearanceResource.DepotPath.GetRedHash(), a.AppearanceName.ToString() ?? "")).ToArray();
            return new TemplateInfo(entity?.GetType().Name ?? "", door, template.DefaultAppearance.ToString() ?? "", appearances, components);
        }
    })).Value;

    private Dictionary<string, ComponentInfo[]>? AppearanceComponents(ulong hash) => _appearanceResources.GetOrAdd(hash, h => new(() =>
    {
        if (EntryOrNull(h) is not { } entry) return null;
        using var data = ExtractEntry(entry, default);
        using var reader = new CR2WReader(data);
        if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file is null) return null;
        using (file)
        {
            if (file.RootChunk is not appearanceAppearanceResource resource) return null;
            var result = new Dictionary<string, ComponentInfo[]>(StringComparer.Ordinal);
            // Some definitions (elevator call panels) have no component list of their own.
            foreach (var definition in (resource.Appearances ?? []).Select(a => a.Chunk).OfType<appearanceAppearanceDefinition>())
                result.TryAdd(definition.Name.ToString() ?? "", (definition.Components ?? []).Select(c => Component(c)).OfType<ComponentInfo>().ToArray());
            return result;
        }
    })).Value;

    private ComponentInfo? Component(IRedType? value)
    {
        if (value is not entIPlacedComponent placed) return null;
        // Default-valued parts of a transform may be absent (null) in the file.
        var t = placed.LocalTransform;
        static float Fixed(FixedPoint? p) => p is null ? 0 : p.Bits / 131072f;
        var q = t?.Orientation;
        var local = new PlacementTransform(
            new Vector3(Fixed(t?.Position?.X), Fixed(t?.Position?.Y), Fixed(t?.Position?.Z)),
            q is null ? Quaternion.Identity : Normalize(new Quaternion(q.I, q.J, q.K, q.R)), Vector3.One);
        string? parent = placed.ParentTransform?.Chunk is entIBinding binding ? binding.BindName.ToString() : null;
        string? slot = placed.ParentTransform?.Chunk is entHardTransformBinding hard && hard.SlotName.ToString() is { Length: > 0 } slotName && slotName != "None" ? slotName : null;
        MeshReference? mesh = null;
        switch (value)
        {
            case entMeshComponent m:
                mesh = MeshRef(m.Mesh.DepotPath, m.MeshAppearance.ToString());
                var scale = m.VisualScale is null ? Vector3.One : new Vector3(m.VisualScale.X, m.VisualScale.Y, m.VisualScale.Z);
                if (scale.X > 0 && scale.Y > 0 && scale.Z > 0) local = local with { Scale = scale };
                break;
            case entSkinnedMeshComponent s:
                mesh = MeshRef(s.Mesh.DepotPath, s.MeshAppearance.ToString());
                break;
            // Destructible parts (street lamp and traffic light poles, sign posts) show their
            // intact mesh until they are broken.
            case entPhysicalDestructionComponent d:
                mesh = MeshRef(d.Mesh.DepotPath, d.MeshAppearance.ToString());
                break;
        }
        return new(placed.Name.ToString() ?? "", mesh, local, parent, value is gameMovingPlatform)
        {
            Slot = slot,
            Slots = value is entSlotComponent slots ? slots.Slots.Where(x => x.BoneName.ToString() is { Length: > 0 } b && b != "None")
                .GroupBy(x => x.SlotName.ToString() ?? "").ToDictionary(g => g.Key, g => g.First().BoneName.ToString()!, StringComparer.Ordinal) : null,
            Rig = value is entAnimatedComponent animated ? (ulong)animated.Rig.DepotPath.GetRedHash() : 0
        };
    }

    // Marks a bone whose pose is not known (see RigBones).
    private static readonly PlacementTransform Unknown = new(new(float.MaxValue), Quaternion.Identity, Vector3.One);

    // A rig's bones as door leaves hang from them, by name. Bones under the root (sliding door rigs
    // such as int_doors_002, the lift landing doors): their offset turned by their own rotation (the
    // two leaves meet in the middle, closed) and only the heading of the rotation (they stand
    // upright), which matches the game's closed doors. Deeper bones (the cabins' own door leaves,
    // under a converted "base" bone): Unknown, the reference pose does not tell where they stand.
    private Dictionary<string, PlacementTransform?>? RigBones(ulong hash) => _rigBones.GetOrAdd(hash, h => new(() =>
    {
        if (EntryOrNull(h) is not { } entry) return null;
        using var data = ExtractEntry(entry, default);
        using var reader = new CR2WReader(data);
        if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file is null) return null;
        using (file)
        {
            if (file.RootChunk is not animRig rig || rig.BoneNames.Count != rig.BoneTransforms.Count) return null;
            int n = rig.BoneNames.Count;
            var position = new Vector3[n]; var rotation = new Quaternion[n];
            var bones = new Dictionary<string, PlacementTransform?>(StringComparer.Ordinal);
            for (int i = 0; i < n; i++)
            {
                var b = rig.BoneTransforms[i];
                var q = Normalize(new Quaternion(b.Rotation.I, b.Rotation.J, b.Rotation.K, b.Rotation.R));
                var t = Vector3.Transform(new Vector3(b.Translation.X, b.Translation.Y, b.Translation.Z), q);
                int parentIndex = i < rig.BoneParentIndexes.Count ? (short)rig.BoneParentIndexes[i] : -1;
                if (parentIndex >= 0 && parentIndex < i) { position[i] = position[parentIndex] + Vector3.Transform(t, rotation[parentIndex]); rotation[i] = rotation[parentIndex] * q; }
                else { position[i] = t; rotation[i] = q; }
                var heading = Vector3.Transform(Vector3.UnitX, rotation[i]);
                float yaw = MathF.Atan2(heading.Y, heading.X);
                bool underRoot = parentIndex <= 0;
                bones.TryAdd(rig.BoneNames[i].ToString() ?? "", underRoot ? new PlacementTransform(position[i], Quaternion.CreateFromAxisAngle(Vector3.UnitZ, yaw), Vector3.One) : null);
            }
            return bones;
        }
    })).Value;

    private MeshReference? MeshRef(ResourcePath path, string? appearance)
    {
        ulong hash = path.GetRedHash();
        if (hash == 0 || EntryOrNull(hash) is not { } entry) return null;
        return new(hash, path.GetResolvedText() ?? entry.FileName, appearance is null or "" or "None" ? "default" : appearance);
    }

    // One mesh for an entity appearance (previews, thumbnails); surfaced: textured materials.
    public async Task<MeshAsset> ReadEntityMeshAsync(ulong template, string appearance, bool surfaced, int surfaceSize = 512, CancellationToken cancellation = default)
    {
        var visual = await Task.Run(() => ResolveEntity(template, appearance), cancellation).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The entity template could not be read.");
        var loads = visual.Parts.Select(async part =>
        {
            try
            {
                var mesh = surfaced ? await ReadSurfacedMeshAsync(part.Mesh.Hash, surfaceSize, cancellation, part.Mesh.Appearance).ConfigureAwait(false)
                    : await ReadMeshConcurrentAsync(part.Mesh.Hash, cancellation, 96L << 20, 128L << 20).ConfigureAwait(false);
                return ((MeshAsset Mesh, PlacementTransform Local, string Appearance)?)(mesh, part.Local, part.Mesh.Appearance);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
        }).ToArray();
        var loaded = (await Task.WhenAll(loads).ConfigureAwait(false)).OfType<(MeshAsset, PlacementTransform, string)>().ToArray();
        return MeshMerge.Merge(Find(template)?.Path ?? template.ToString(), loaded);
    }

    private static Quaternion Normalize(Quaternion q) => q.LengthSquared() < 1e-8f ? Quaternion.Identity : Quaternion.Normalize(q);

    // --- Door settings -------------------------------------------------------------------------

    internal static DoorSettings ApplyEntity(DoorSettings door, Door entity) => door with
    {
        Opening = entity.DoorOpeningType.ToString() is { Length: > 0 } opening && DoorSettings.Openings.Contains(opening) ? opening : door.Opening,
        CloseDelay = entity.AutomaticCloseDelay >= 0 ? entity.AutomaticCloseDelay : door.CloseDelay
    };

    internal static DoorSettings ApplySetup(DoorSettings door, DoorSetup? setup)
    {
        if (setup is null) return door;
        string type = setup.DoorType.ToString(), state = setup.InitialDoorState.ToString();
        return door with
        {
            DoorType = DoorSettings.Types.Contains(type) ? type : door.DoorType,
            InitialState = DoorSettings.States.Contains(state) ? state : door.InitialState,
            ClosesAutomatically = setup.AutomaticallyClosesItself,
            OpeningSpeed = setup.OpeningSpeed > 0 ? setup.OpeningSpeed : door.OpeningSpeed,
            PlayerCanToggleLock = setup.CanPlayerToggleLockState
        };
    }

    // The node's instance data overrides the template (DoorController persistent state, Door entity).
    public static DoorSettings ApplyInstanceData(DoorSettings door, worldEntityNode node)
    {
        // Instance data can be absent, or a handle without its buffer: the template's settings stand
        // (a throw here would lose the whole sector).
        if (door.Decorative || node.InstanceData?.Chunk?.Buffer?.Data is not RedPackage package) return door;
        foreach (var chunk in package.Chunks)
        {
            if (chunk is DoorController { PersistentState.Chunk: DoorControllerPS ps }) door = ApplySetup(door, ps.DoorProperties);
            else if (chunk is Door entity) door = ApplyEntity(door, entity);
        }
        return door;
    }
}
