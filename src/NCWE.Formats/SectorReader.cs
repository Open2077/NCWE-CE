// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Security.Cryptography;
using NCWE.Core;
using WolvenKit.RED4.Archive.Buffer;
using WolvenKit.RED4.Archive.IO;
using WolvenKit.RED4.Types;
using Vector3 = System.Numerics.Vector3;
using Quaternion = System.Numerics.Quaternion;
using Matrix4x4 = System.Numerics.Matrix4x4;
using Archive = WolvenKit.RED4.Archive.Archive;

namespace NCWE.Formats;

public sealed partial class GameArchiveIndex
{
    public async Task<SectorSnapshot> LoadSectorAsync(ulong hash, CancellationToken cancellation = default, long maximumSourceBytes = 256 * 1024 * 1024)
    {
        await _worker.WaitAsync(cancellation);
        try { return await Task.Run(() => LoadSector(hash, cancellation, maximumSourceBytes), cancellation); }
        finally { _worker.Release(); }
    }

    // Concurrent route used by streaming: extraction is serialized, decoding is not.
    public Task<SectorSnapshot> ReadSectorConcurrentAsync(ulong hash, CancellationToken cancellation = default, long maximumSourceBytes = 256 * 1024 * 1024)
        => Task.Run(() => LoadSector(hash, cancellation, maximumSourceBytes), cancellation);

    private SectorSnapshot LoadSector(ulong hash, CancellationToken cancellation, long maximumSourceBytes)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!_entries.TryGetValue(hash, out var entry)) throw new FileNotFoundException($"Unknown sector {hash}.");
        if (entry.Size > maximumSourceBytes) throw new NotSupportedException("Sector exceeds the configured source resource budget.");
        using var data = ExtractEntry(entry, cancellation);
        var fingerprint = Convert.ToHexString(SHA256.HashData(data.GetBuffer().AsSpan(0, checked((int)data.Length))));
        using var reader = new CR2WReader(data);
        // 2.x sectors serialize some properties with their default value (for example
        // worldConversationData.interruptionOperations). WK 9.0.1 rejects the whole
        // file; the value it read is still assigned, so only this case is tolerated.
        reader.ParsingError += SectorReader.TolerateSectorParsingError;
        var code = reader.ReadFile(out var file);
        if (code != EFileReadErrorCodes.NoError || file is null) throw new InvalidDataException($"Sector CR2W read failed: {code}");
        using (file)
        {
            if (file.RootChunk is not worldStreamingSector sector) throw new InvalidDataException("Resource is not a streaming sector.");
            return SectorReader.Convert(sector, new(hash, entry.FileName, fingerprint), Find, cancellation, foliage: FoliageOf(file), template: BendedTemplate, entity: ResolveEntity);
        }
    }
}

public static class SectorReader
{
    // Accept two benign 2.x serialization quirks; any other parsing error still fails.
    // A sync/async resource-reference flavour mismatch (seen on WorldWidgetComponent)
    // drops that reference: placement conversion never reads widget resources.
    internal static bool TolerateSectorParsingError(ParsingErrorEventArgs e)
    {
        if (e is InvalidDefaultValueEventArgs) return true;
        static bool Reference(string type) => type.StartsWith("rRef:", StringComparison.Ordinal) || type.StartsWith("raRef:", StringComparison.Ordinal);
        if (e is InvalidRTTIEventArgs rtti && Reference(rtti.ExpectedType) && Reference(rtti.ActualType)) { rtti.Value = null; return true; }
        return false;
    }

    public const int MaximumFoliagePerNode = 4096;
    public const int MaximumFoliagePerSector = 60_000;

    public static SectorSnapshot Convert(worldStreamingSector sector, ResourceIdentity source,
        Func<ulong, ArchiveResource?> resolve, CancellationToken cancellation = default, int maximumPlacements = 100_000,
        Func<ulong, FoliageStance[]?>? foliage = null, Func<string, ArchiveResource?>? template = null, Func<ulong, string, EntityVisual?>? entity = null)
    {
        if (sector.NodeData.Buffer.Data is not worldNodeDataBuffer setups)
            throw new NotSupportedException("WolvenKit did not decode this sector's node placement buffer.");
        if (maximumPlacements <= 0 || setups.Count > maximumPlacements) throw new NotSupportedException("Sector exceeds the placement expansion limit.");
        var placements = ImmutableArray.CreateBuilder<SourcePlacement>();
        var actors = ImmutableArray.CreateBuilder<CollisionActorRecord>();
        var diagnostics = ImmutableArray.CreateBuilder<string>();
        int plants = 0;
        var usage = setups.GroupBy(s => (int)s.NodeIndex).ToDictionary(g => g.Key, g => g.Count());
        var overlapping = FindOverlappingInstanceRanges(sector);
        var ranges = sector.VariantIndices.Select(v => (int)v).ToImmutableArray();
        if (ranges.Any(v => v < 0 || v > setups.Count) || !ranges.SequenceEqual(ranges.Order()))
            throw new InvalidDataException("Sector has invalid placement range boundaries.");
        for (int placementIndex = 0; placementIndex < setups.Count; placementIndex++)
        {
            cancellation.ThrowIfCancellationRequested();
            var setup = setups[placementIndex];
            int nodeIndex = setup.NodeIndex;
            if (nodeIndex >= sector.Nodes.Count)
            { diagnostics.Add($"Placement {placementIndex} refers to missing node definition {nodeIndex}."); continue; }
            var node = sector.Nodes[nodeIndex].Chunk;
            if (node is null) { diagnostics.Add($"Node {nodeIndex} has a null definition."); continue; }
            string type = node.GetType().Name;
            var anchor = new SourceAnchor(source, nodeIndex, placementIndex, -1, setup.Id, type, setups.Count, 0);
            string name = node.DebugName.ToString() ?? "";
            if (string.IsNullOrEmpty(name) || name == "None") name = $"{type} #{placementIndex}";
            // Nodes with a global reference (QuestPrefabRefHash: entities, devices, their proxies) are
            // editable like the rest; the editor warns that quests and scripts can address them.
            string? restriction = null;
            MeshReference? mesh = null;
            var support = PlacementSupport.Unsupported;
            var transform = new PlacementTransform(new(setup.Position.X, setup.Position.Y, setup.Position.Z),
                new(setup.Orientation.I, setup.Orientation.J, setup.Orientation.K, setup.Orientation.R),
                new(setup.Scale.X, setup.Scale.Y, setup.Scale.Z));

            if (node.GetType() == typeof(worldStaticMeshNode))
            {
                var staticNode = (worldStaticMeshNode)node;
                mesh = Mesh(staticNode.Mesh, staticNode.MeshAppearance.ToString() ?? "default", resolve);
                support = restriction is null ? PlacementSupport.StaticMesh : PlacementSupport.Unsupported;
            }
            else if (node.GetType() == typeof(worldInstancedMeshNode))
            {
                var instanced = (worldInstancedMeshNode)node;
                mesh = Mesh(instanced.Mesh, instanced.MeshAppearance.ToString() ?? "default", resolve);
                var range = instanced.WorldTransformsBuffer;
                uint startValue = range.StartIndex, countValue = range.NumElements;
                if (range.SharedDataBuffer.Chunk?.Buffer.Data is not WorldTransformsBuffer buffer || (ulong)startValue + countValue > (ulong)buffer.Transforms.Count)
                    restriction = "Instanced mesh has an unsupported or invalid shared transform range.";
                else
                {
                    int start = (int)startValue, count = (int)countValue;
                    if (usage[nodeIndex] != 1) restriction = "Shared instanced node has multiple placement owners; read-only.";
                    if (overlapping.Contains(nodeIndex)) restriction = "Instanced transform range overlaps another node; read-only to preserve independent targets.";
                    // These are already world-space transforms. Applying setup again doubles the placement.
                    for (int instance = 0; instance < count; instance++)
                    {
                        var t = buffer.Transforms[start + instance];
                        var instanceTransform = new PlacementTransform(new(t.Translation.X, t.Translation.Y, t.Translation.Z),
                            new(t.Rotation.I, t.Rotation.J, t.Rotation.K, t.Rotation.R), new(t.Scale.X, t.Scale.Y, t.Scale.Z));
                        Add(anchor with { InstanceIndex = instance, ExpectedInstances = count }, $"{name} / instance {instance}", mesh,
                            instanceTransform, restriction is null ? PlacementSupport.InstancedMesh : PlacementSupport.Unsupported, restriction);
                    }
                    continue;
                }
            }
            else if (node is worldEntityProxyMeshNode entityProxy)
            {
                // The mesh an entity shows beyond its attach distance (the game swaps them at
                // EntityAttachDistance): hidden closer than that, edited together with its entity.
                mesh = Mesh(entityProxy.Mesh, entityProxy.MeshAppearance.ToString() ?? "default", resolve);
                ulong owner = entityProxy.OwnerGlobalId.Hash;
                Add(anchor, name, mesh, transform, owner != 0 ? PlacementSupport.NodeTransform : PlacementSupport.Unsupported,
                    owner != 0 ? null : "Entity proxy without an owner; display only.", Math.Max(1, (float)entityProxy.EntityAttachDistance), proxyOwner: owner);
                continue;
            }
            else if (node is worldPrefabProxyMeshNode proxy)
            {
                // Display-only distant representation of a whole area (generic, road, terrain).
                mesh = Mesh(proxy.Mesh, proxy.MeshAppearance.ToString() ?? "default", resolve);
                restriction ??= "Distant proxy representation; display only.";
                Add(anchor, name, mesh, transform, PlacementSupport.Unsupported, restriction, proxy.NearAutoHideDistance);
                continue;
            }
            else if (node is worldBendedMeshNode bended)
            {
                // The referenced br_* mesh is generated by the engine; bend its template instead.
                var generated = bended.Mesh.DepotPath.GetResolvedText() ?? "";
                var frames = bended.DeformationData.Select(m => new Matrix4x4(m.X.X, m.X.Y, m.X.Z, m.X.W, m.Y.X, m.Y.Y, m.Y.Z, m.Y.W,
                    m.Z.X, m.Z.Y, m.Z.Z, m.Z.W, m.W.X, m.W.Y, m.W.Z, m.W.W)).ToImmutableArray();
                // Some br_* meshes ship in the archives (Pacifica, EP1, quest locations): straight
                // pieces along Y with their real materials, which the engine bends with the frames.
                if (RoadBending.TemplateFileName(generated) is not null && resolve(bended.Mesh.DepotPath.GetRedHash()) is not null && !frames.IsEmpty)
                {
                    Add(anchor, name, Mesh(bended.Mesh, bended.MeshAppearance.ToString() ?? "default", resolve), transform,
                        PlacementSupport.Unsupported, "Bended mesh from the archives; display only.", deformation: frames);
                    continue;
                }
                // Cables (worldCableMeshNode) reference a real tube template along X; bend it directly.
                var resource = RoadBending.TemplateFileName(generated) is { } fileName ? template?.Invoke(fileName)
                    : resolve(bended.Mesh.DepotPath.GetRedHash());
                if (resource is null || frames.IsEmpty)
                { Add(anchor, name, null, transform, PlacementSupport.Unsupported, $"Bended mesh template not found for {Path.GetFileName(generated)}."); continue; }
                Add(anchor, name, new(resource.Hash, resource.Path, bended.MeshAppearance.ToString() ?? "default"), transform,
                    PlacementSupport.Unsupported, node is worldCableMeshNode ? "Cable bent along its curve; display only." : "Bended (road) mesh rebuilt from its template; display only.", deformation: frames);
                continue;
            }
            else if (node is worldFoliageNode plant)
            {
                // Stances are offsets from the node placement (verified on 2.31 data).
                mesh = Mesh(plant.Mesh, plant.MeshAppearance.ToString() ?? "default", resolve);
                var stances = foliage?.Invoke(plant.FoliageResource.DepotPath.GetRedHash());
                int begin = (int)(uint)plant.PopulationSpanInfo.StancesBegin, count = (int)(uint)plant.PopulationSpanInfo.StancesCount;
                if (mesh.Hash == 0 || stances is null || begin < 0 || begin + count > stances.Length)
                { Add(anchor, name, null, transform, PlacementSupport.Unsupported, "Foliage resource unavailable; not displayed."); continue; }
                count = Math.Min(count, MaximumFoliagePerNode);
                // No sector of 2.31 comes near this (the densest holds 34,387 plants): a sector
                // beyond it still loads, without the plants over the limit.
                if (plants + count > MaximumFoliagePerSector)
                {
                    if (plants <= MaximumFoliagePerSector) diagnostics.Add($"More than {MaximumFoliagePerSector} plants: the rest is not displayed.");
                    plants = MaximumFoliagePerSector + 1;
                    Add(anchor, name, null, transform, PlacementSupport.Unsupported, "Plants over the sector's display limit; not displayed."); continue;
                }
                plants += count;
                // The game stops streaming a plant node beyond its own distance (a few tens of metres
                // for sticks, stones and grass; its setup's is unlimited).
                float streaming = (float)plant.StreamingDistance;
                if (!float.IsFinite(streaming) || streaming <= 0) streaming = 0;
                for (int k = 0; k < count; k++)
                {
                    var stance = stances[begin + k];
                    var local = transform.Scale * stance.Scale;
                    var world = new PlacementTransform(
                        transform.Position + Vector3.Transform(stance.Position * transform.Scale, transform.Rotation),
                        Quaternion.Concatenate(stance.Rotation, transform.Rotation), local);
                    Add(anchor with { InstanceIndex = k, ExpectedInstances = count }, $"{name} / plant {k}", mesh, world,
                        PlacementSupport.Unsupported, "Foliage instance; display only.", streaming: streaming);
                }
                continue;
            }
            else if (node is worldEntityNode entityNode && entity?.Invoke(entityNode.EntityTemplate.DepotPath.GetRedHash(), entityNode.AppearanceName.ToString() ?? "") is { } visual)
            {
                // Entities (doors, devices, props): drawn from their appearance's meshes, moved and
                // deleted as whole nodes.
                var door = visual.Door is { } settings ? GameArchiveIndex.ApplyInstanceData(settings, entityNode) : null;
                bool editable = restriction is null;
                string templatePath = entityNode.EntityTemplate.DepotPath.GetResolvedText() ?? "";
                var info = new EntityInfo(templatePath, visual.Appearance) { EntityClass = visual.EntityClass, Door = door };
                // Doors are editable elements (appearance, behaviour) and get a state-colored icon.
                var doorElement = door is null || templatePath.Length == 0 ? null : new ElementProperties(ElementKind.Door) { Resource = templatePath, Appearance = visual.Appearance, Door = door };
                Add(anchor, name, null, transform, editable ? PlacementSupport.NodeTransform : PlacementSupport.Unsupported, editable ? null : restriction,
                    marker: door is null ? null : new DebugMarker("Door", DoorColor(door), 0), element: doorElement, entityInfo: info, parts: visual.Parts);
                continue;
            }
            else if (node is worldStaticOccluderMeshNode occluderNode)
            {
                // Occluders hide what is behind them from the renderer; invisible in the game,
                // drawn in the occlusion view. Edited as elements (type, autohide scale) or whole nodes.
                mesh = Mesh(occluderNode.Mesh, "default", resolve);
                string occluderMesh = occluderNode.Mesh.DepotPath.GetResolvedText() ?? "";
                var properties = new ElementProperties(ElementKind.Occluder) { Resource = occluderMesh, OccluderType = occluderNode.OccluderType.ToString(), AutohideScale = (byte)occluderNode.AutohideDistanceScale };
                Add(anchor, name, mesh, transform, mesh.Hash == 0 ? PlacementSupport.Unsupported : PlacementSupport.NodeTransform, null, element: occluderMesh.Length > 0 ? properties : null, occluderNode: true);
                continue;
            }
            else if (node is worldInstancedOccluderNode instancedOccluder)
            {
                // Each instance carries its world matrix (rows: scaled axes, then position); the
                // setup is the identity. ArchiveXL edits only the node as a whole: deletion.
                mesh = Mesh(instancedOccluder.Mesh, "default", resolve);
                var offsets = ImmutableArray.CreateBuilder<PlacementTransform>(instancedOccluder.Buffer.Count);
                foreach (var b in instancedOccluder.Buffer)
                {
                    var matrix = new Matrix4x4(b.Unknown1.X, b.Unknown1.Y, b.Unknown1.Z, 0, b.Unknown2.X, b.Unknown2.Y, b.Unknown2.Z, 0, b.Unknown3.X, b.Unknown3.Y, b.Unknown3.Z, 0, b.Unknown4.X, b.Unknown4.Y, b.Unknown4.Z, 1);
                    try { offsets.Add(PlacementTransform.FromGameMatrix(matrix)); } catch (InvalidDataException) { }
                }
                Add(anchor, name, mesh, transform, mesh.Hash == 0 ? PlacementSupport.Unsupported : PlacementSupport.NodeTransform,
                    mesh.Hash == 0 ? null : $"Instanced occluder ({offsets.Count} occluders): deleted as a whole, or turned into individual occluders.", offsets: offsets.ToImmutable(), occluderNode: true);
                continue;
            }
            else if (DisplayMesh(node) is { } display)
            {
                // Other mesh families: rendered at the placement transform; the whole-node ones
                // (destructibles, rotating, dynamic, advertisements...) move and delete as one node.
                mesh = Mesh(display.Mesh, display.Appearance, resolve);
                if (mesh.Hash == 0) mesh = null; // An empty reference has nothing to display.
                bool whole = restriction is null && mesh is not null && NodeFamilies.WholeNode.Contains(type);
                if (!whole) restriction ??= display.Note ?? $"{type} is displayed but not editable.";
                support = whole ? PlacementSupport.NodeTransform : PlacementSupport.Unsupported;
                if (node is worldInstancedDestructibleMeshNode destructible)
                { Add(anchor, name, mesh, transform, support, restriction, offsets: DestructibleOffsets(destructible)); continue; }
            }
            else if (node is worldCollisionNode collisionNode && collisionNode.CompiledData.Buffer.Data is CollisionBuffer collisionBuffer && collisionBuffer.Actors.Count > 0)
            {
                // Static mesh collision is cooked here, apart from the meshes: each actor is kept at
                // its own transform and follows the deletion of the mesh placed there.
                for (int a = 0; a < collisionBuffer.Actors.Count; a++)
                {
                    var (actorTransform, shapes) = CollisionActor(collisionBuffer.Actors[a]);
                    try { actorTransform = actorTransform.Validated(); } catch (InvalidDataException) { continue; }
                    actors.Add(new(nodeIndex, placementIndex, setup.Id, a, collisionBuffer.Actors.Count, name, actorTransform, shapes, (ulong)collisionNode.SectorHash));
                }
                Add(anchor, name, null, transform, PlacementSupport.Unsupported, "Collision node: its actors are deleted with the meshes at their transforms.");
                continue;
            }
            else if (Marker(node) is { } marker)
            {
                bool whole = restriction is null && NodeFamilies.WholeNode.Contains(type);
                if (!whole) restriction ??= $"{type} is shown as a debug marker; not editable.";
                Add(anchor, name, null, transform, whole ? PlacementSupport.NodeTransform : PlacementSupport.Unsupported, restriction, 0, marker, element: Element(node));
                continue;
            }
            else restriction ??= $"{type} is not editable in the static-placement adapter.";
            Add(anchor, name, mesh, transform, support, restriction);
        }
        // NodeCount means placement count, matching ArchiveXL expectedNodes/GetInstanceCount.
        return new(source, setups.Count, sector.Level, sector.Category.ToString(), placements.ToImmutable(), diagnostics.ToImmutable())
            { RangeStarts = ranges, PersistentRangeIndex = sector.PersisentNodeIndex, CollisionActors = actors.ToImmutable() };

        // streaming: the distance beyond which the game no longer streams it, when its node says so itself (0: its setup's).
        void Add(SourceAnchor anchor, string name, MeshReference? mesh, PlacementTransform transform, PlacementSupport support, string? diagnostic, float nearHide = 0, DebugMarker? marker = null, ImmutableArray<Matrix4x4> deformation = default, ImmutableArray<PlacementTransform> offsets = default, ElementProperties? element = null, EntityInfo? entityInfo = null, ImmutableArray<EntityPart> parts = default, ulong proxyOwner = 0, bool occluderNode = false, float streaming = 0)
        {
            // Plants have their own limit: they never crowd the sector's other placements out.
            if (placements.Count - Math.Min(plants, MaximumFoliagePerSector) >= maximumPlacements) throw new NotSupportedException("Sector exceeds the placement expansion limit.");
            try { transform = transform.Validated(); }
            catch (InvalidDataException ex)
            {
                support = PlacementSupport.Unsupported; diagnostic = ex.Message;
                transform = PlacementTransform.Identity with { Position = FinitePosition(transform.Position) };
            }
            if (mesh?.Hash == 0) { support = PlacementSupport.Unsupported; diagnostic = "Mesh reference is empty."; }
            else if (mesh is not null && JunctionProxy(mesh, resolve) is { } junction)
            {
                // Road junction meshes (road_meshes\i_roadintersection_*\s0_*.mesh) are generated by
                // the engine; the archives only ship the junction proxy next to them.
                mesh = junction; support = PlacementSupport.Unsupported;
                diagnostic = "Road junction generated by the engine; its proxy mesh is shown.";
            }
            int range = 0;
            for (int i = 0; i < ranges.Length && ranges[i] <= anchor.PlacementIndex; i++) range = i;
            placements.Add(new(anchor, name, mesh, transform, support, diagnostic) { RangeIndex = range,
                QuestPrefabRef = setups[anchor.PlacementIndex].QuestPrefabRefHash,
                MaxStreamingDistance = streaming > 0 ? streaming : setups[anchor.PlacementIndex].MaxStreamingDistance, NearHideDistance = nearHide, Marker = marker, Deformation = deformation.IsDefault ? [] : deformation, InstanceOffsets = offsets.IsDefault ? [] : offsets, Element = element, Entity = entityInfo, EntityParts = parts.IsDefault ? [] : parts, ProxyOwner = proxyOwner, OccluderNode = occluderNode,
                Decal = sector.Nodes[anchor.NodeIndex].Chunk is worldStaticDecalNode decalNode
                    ? new(decalNode.Material.DepotPath.GetRedHash(), decalNode.HorizontalFlip, decalNode.VerticalFlip, float.IsFinite(decalNode.AutoHideDistance) ? Math.Max(0, decalNode.AutoHideDistance) : 0, (int)decalNode.OrderNo, decalNode.IsStretchingEnabled)
                    {
                        NormalThreshold = !decalNode.EnableNormalTreshold ? -1 : float.IsFinite(decalNode.NormalThreshold) ? Math.Clamp(decalNode.NormalThreshold, 0, 90) : 1,
                        Objects = (Enums.EDecalRenderMode)decalNode.DecalRenderMode switch
                        {
                            Enums.EDecalRenderMode.DRM_ObjectType => DecalObjects.OfType, Enums.EDecalRenderMode.DRM_AllDynamic => DecalObjects.Dynamic,
                            Enums.EDecalRenderMode.DRM_All => DecalObjects.All, _ => DecalObjects.Static
                        },
                        ObjectType = (byte)(int)(Enums.ERenderObjectType)decalNode.SurfaceType
                    } : null,
                Occluder = sector.Nodes[anchor.NodeIndex].Chunk switch
                {
                    worldMeshNode meshNode => new MeshOccluder(meshNode.OccluderType.ToString(), (byte)meshNode.OccluderAutohideDistanceScale),
                    worldStaticOccluderMeshNode occluder => new MeshOccluder(occluder.OccluderType.ToString(), (byte)occluder.AutohideDistanceScale),
                    worldInstancedOccluderNode instanced => new MeshOccluder(instanced.OccluderType.ToString(), (byte)instanced.AutohideDistanceScale),
                    _ => null
                } });
        }
    }

    // Node families that carry one mesh at the placement transform. Occluders are invisible
    // in the game and bended meshes need their deformation, so both stay hidden.
    private static (CResourceAsyncReference<CMesh> Mesh, string Appearance, string? Note)? DisplayMesh(worldNode node) => node switch
    {
        worldStaticOccluderMeshNode or worldInstancedOccluderNode => null,
        worldInstancedDestructibleMeshNode d => (d.Mesh, d.MeshAppearance.ToString() ?? "default", "Destructible mesh; display only."),
        worldStaticMeshNode s => (s.Mesh, s.MeshAppearance.ToString() ?? "default", $"{node.GetType().Name} is displayed; only exact worldStaticMeshNode is editable."),
        worldMeshNode m => (m.Mesh, m.MeshAppearance.ToString() ?? "default", null),
        worldPhysicalDestructionNode p => (p.Mesh, p.MeshAppearance.ToString() ?? "default", "Physical destruction mesh; display only."),
        // Terrain cells are ordinary meshes (sectors\_global\terrain) placed by level-2 sectors.
        worldTerrainMeshNode t => (t.MeshRef, "default", "Terrain cell; display only."),
        _ => null
    };

    private static MeshReference? JunctionProxy(MeshReference mesh, Func<ulong, ArchiveResource?> resolve)
    {
        if (resolve(mesh.Hash) is not null || !mesh.Path.Contains(@"\road_meshes\i_roadintersection", StringComparison.OrdinalIgnoreCase)) return null;
        var proxyPath = Path.Combine(Path.GetDirectoryName(mesh.Path) ?? "", "prx.mesh");
        var proxy = resolve(WolvenKit.Common.FNV1A.FNV1A64HashAlgorithm.HashString(proxyPath));
        return proxy is null ? null : new MeshReference(proxy.Hash, proxy.Path, "default");
    }

    // Destructible instances are stored relative to the node placement (verified on 2.31: a
    // two-cup node holds offsets of ±0.5 m). Shown at node ∘ offset; edited as one node.
    private const int MaximumDestructibleInstances = 1024;
    private static ImmutableArray<PlacementTransform> DestructibleOffsets(worldInstancedDestructibleMeshNode node)
    {
        static PlacementTransform Local(Transform t) => new(new(t.Position.X, t.Position.Y, t.Position.Z),
            new(t.Orientation.I, t.Orientation.J, t.Orientation.K, t.Orientation.R), Vector3.One);
        var cooked = node.CookedInstanceTransforms;
        uint start = cooked.StartIndex, count = cooked.NumElements;
        if (cooked.SharedDataBuffer.Chunk?.Buffer.Data is CookedInstanceTransformsBuffer buffer && count > 0 && (ulong)start + count <= (ulong)buffer.Transforms.Count)
            return Enumerable.Range((int)start, (int)Math.Min(count, MaximumDestructibleInstances)).Select(i => Local(buffer.Transforms[i])).ToImmutableArray();
        return node.InstanceTransforms.Count > 0 ? node.InstanceTransforms.Take(MaximumDestructibleInstances).Select(Local).ToImmutableArray() : [];
    }

    // Door icon color: its initial state (decorative doors grey).
    internal static System.Numerics.Vector4 DoorColor(DoorSettings door) => door.Decorative ? new(.62f, .64f, .7f, 1) : door.InitialState switch
    {
        "LOCKED" => new(1f, .62f, .2f, 1), "SEALED" => new(1f, .32f, .3f, 1), "OPENED" => new(.4f, .9f, .5f, 1), _ => new(.88f, .9f, .95f, 1)
    };

    // Editable properties of the element families (lights, particles, effects, sounds, decals).
    internal static ElementProperties? Element(worldNode node) => node switch
    {
        worldStaticLightNode l => new(ElementKind.Light)
        {
            LightType = l.Type.ToString() switch { "LT_Spot" => LightKind.Spot, "LT_Area" => LightKind.Area, _ => LightKind.Point },
            Color = new(l.Color.Red / 255f, l.Color.Green / 255f, l.Color.Blue / 255f), Temperature = l.Temperature, Intensity = l.Intensity,
            Radius = l.Radius > 0 ? l.Radius : .01f, InnerAngle = l.InnerAngle, OuterAngle = l.OuterAngle, Shadows = l.EnableLocalShadows
        },
        worldStaticParticleNode p => new(ElementKind.Particle) { Resource = p.ParticleSystem.DepotPath.GetResolvedText() ?? "" },
        worldEffectNode e => new(ElementKind.Effect) { Resource = e.Effect.DepotPath.GetResolvedText() ?? "" },
        worldStaticSoundEmitterNode s => new(ElementKind.Sound) { Radius = s.Radius > 0 ? s.Radius : .01f, SoundEvent = SoundEvent(s) },
        worldStaticDecalNode d => new(ElementKind.Decal)
        {
            Resource = d.Material.DepotPath.GetResolvedText() ?? "", Alpha = d.Alpha,
            Color = new(d.DiffuseColorScale.Red, d.DiffuseColorScale.Green, d.DiffuseColorScale.Blue)
        },
        _ => null
    };

    // The event a sound emitter plays while the player is within reach (the first of its active events).
    internal static string SoundEvent(worldStaticSoundEmitterNode node)
    {
        if (node.Settings?.Chunk is not { } settings) return "";
        foreach (var active in settings.EventsOnActive)
            if (active.Event.ToString() is { Length: > 0 } name && name != "None") return name;
        return "";
    }

    private static DebugMarker? Marker(worldNode node) => node switch
    {
        worldStaticLightNode light => new("Light", new(Element(light)!.DisplayColor, 1), light.Radius),
        worldStaticSoundEmitterNode sound => new("Sound", new(.35f, .75f, 1f, 1), sound.Radius),
        worldStaticParticleNode or worldEffectNode => new("Effect", new(1f, .45f, .85f, 1), 0),
        worldStaticDecalNode => new("Decal", new(.78f, .82f, .58f, 1), 0),
        _ => null
    };

    // One actor: its world transform and its shapes (actor space). Mesh shapes keep the hash of
    // their cooked mesh (geometry cache group = the node's SectorHash).
    private static (PlacementTransform Transform, ImmutableArray<NCWE.Core.CollisionShape> Shapes) CollisionActor(CollisionActor actor)
    {
        var position = new Vector3(actor.Position.X, actor.Position.Y, actor.Position.Z);
        var orientation = new Quaternion(actor.Orientation.I, actor.Orientation.J, actor.Orientation.K, actor.Orientation.R);
        var rotation = orientation.LengthSquared() > 1e-8f ? Quaternion.Normalize(orientation) : Quaternion.Identity;
        var scale = new Vector3(actor.Scale.X, actor.Scale.Y, actor.Scale.Z);
        if (MathF.Abs(scale.X) < 1e-5f || MathF.Abs(scale.Y) < 1e-5f || MathF.Abs(scale.Z) < 1e-5f) scale = Vector3.One;
        var shapes = ImmutableArray.CreateBuilder<NCWE.Core.CollisionShape>(actor.Shapes.Count);
        foreach (var shape in actor.Shapes)
        {
            var kind = (Enums.physicsShapeType)shape.ShapeType switch
            {
                Enums.physicsShapeType.Box => CollisionShapeKind.Box, Enums.physicsShapeType.Sphere => CollisionShapeKind.Sphere,
                Enums.physicsShapeType.Capsule => CollisionShapeKind.Capsule, Enums.physicsShapeType.ConvexMesh => CollisionShapeKind.ConvexMesh,
                Enums.physicsShapeType.TriangleMesh => CollisionShapeKind.TriangleMesh, _ => (CollisionShapeKind?)null
            };
            if (kind is not { } k) continue;
            var shapeRotation = new Quaternion(shape.Rotation.I, shape.Rotation.J, shape.Rotation.K, shape.Rotation.R);
            var size = shape is CollisionShapeSimple simple ? new Vector3(simple.Size.X, simple.Size.Y, simple.Size.Z) : Vector3.Zero;
            shapes.Add(new NCWE.Core.CollisionShape(k, new(shape.Position.X, shape.Position.Y, shape.Position.Z), shapeRotation.LengthSquared() > 1e-8f ? Quaternion.Normalize(shapeRotation) : Quaternion.Identity, size)
            {
                MeshHash = shape is CollisionShapeMesh meshShape ? (ulong)meshShape.Hash : 0,
                Preset = shape.Preset.GetResolvedText() ?? "",
                Material = shape.Materials.Count > 0 ? shape.Materials[0].GetResolvedText() ?? "" : ""
            });
        }
        return (new PlacementTransform(position, rotation, scale), shapes.ToImmutable());
    }
    private static HashSet<int> FindOverlappingInstanceRanges(worldStreamingSector sector)
    {
        var groups = new Dictionary<WorldTransformsBuffer, List<(int Node, ulong Start, ulong End)>>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < sector.Nodes.Count; i++)
        {
            if (sector.Nodes[i].Chunk is not worldInstancedMeshNode node || node.WorldTransformsBuffer.SharedDataBuffer.Chunk?.Buffer.Data is not WorldTransformsBuffer buffer) continue;
            ulong start = (uint)node.WorldTransformsBuffer.StartIndex, count = (uint)node.WorldTransformsBuffer.NumElements;
            if (count == 0) continue;
            if (!groups.TryGetValue(buffer, out var entries)) groups[buffer] = entries = [];
            entries.Add((i, start, start + count));
        }
        HashSet<int> overlaps = [];
        foreach (var ranges in groups.Values)
        {
            ulong end = 0; int owner = -1;
            foreach (var range in ranges.OrderBy(r => r.Start))
            {
                if (range.Start < end) { overlaps.Add(owner); overlaps.Add(range.Node); }
                if (range.End > end) { end = range.End; owner = range.Node; }
            }
        }
        return overlaps;
    }

    private static Vector3 FinitePosition(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) ? v : Vector3.Zero;
    private static MeshReference Mesh(CResourceAsyncReference<CMesh> resource, string appearance, Func<ulong, ArchiveResource?> resolve)
    {
        var hash = resource.DepotPath.GetRedHash();
        var path = resolve(hash)?.Path ?? resource.DepotPath.GetResolvedText() ?? $"{hash}.mesh";
        return new(hash, path, appearance);
    }
}
