// SPDX-License-Identifier: GPL-3.0-only
using System.Security.Cryptography;
using NCWE.Core;
using LoggerVerbosity = WolvenKit.Common.LoggerVerbosity;
using WolvenKit.Common.FNV1A;
using WolvenKit.Common.Services;
using WolvenKit.Core.Interfaces;
using WolvenKit.RED4.Archive.Buffer;
using WolvenKit.RED4.Archive.CR2W;
using WolvenKit.RED4.Archive.IO;
using WolvenKit.RED4.Types;
using Vector3 = System.Numerics.Vector3;

namespace NCWE.Formats;

public sealed record ExportMeshInfo(MeshReference Reference, Bounds3 ViewBounds, string Fingerprint, string SourceArchive);
public sealed record GeneratedResource(string Path, CR2WFile File);
public sealed record PackedResource(string Path, ulong Hash, string Sha256, long Bytes);
public sealed record AuthoredResourceLocation(string ObjectId, string SectorPath, int PlacementIndex, ulong PlacementId);
public sealed record GeneratedWorld(string BlockPath, GeneratedResource[] Resources, AuthoredResourceLocation[] Objects);
// One bended placement of an editor road: node origin (game space), one frame per bone of
// the cooked piece in node space, and the node-space box of the deformed geometry.
public sealed record AuthoredRoadSegment(string Id, string Name, MeshReference Mesh, Vector3 Origin, System.Collections.Immutable.ImmutableArray<System.Numerics.Matrix4x4> Frames, Bounds3 DeformedBox)
{
    // A stretch of cable: its frames are a cable's (CableBending.Frames) and it is written as the
    // game's cable node, with its length along the curve (m).
    public bool Cable { get; init; }
    public float Length { get; init; }
    // How far the game streams it, when its own size does not say (a stretch of a long cable is
    // seen as far as the whole cable); null: from its box, as for meshes.
    public float? StreamingDistance { get; init; }
}
public sealed record PackedWorld(PackedResource[] Resources, AuthoredResourceLocation[] Objects, string[] Diagnostics);

public static class WorldResourceWriter
{
    public static string Namespace(Guid project) => $"ncwe\\{project:N}";
    public static string BlockPath(Guid project) => Namespace(project) + "\\world.streamingblock";

    private sealed record Entry(string Id, Vector3 Position, AuthoredPlacement? Object, AuthoredRoadSegment? Road, AuthoredElement? Element = null, worldNode? Template = null, entEntityTemplate? DoorTemplate = null, AuthoredCollision? Collision = null, PreparedNode? Prepared = null);

    // Only editor-authored placements and road segments enter this builder. It never accepts vanilla CR2W objects.
    public static GeneratedWorld Build(Guid project, IEnumerable<AuthoredPlacement> objects, IReadOnlyDictionary<ulong, ExportMeshInfo> meshes,
        IEnumerable<AuthoredRoadSegment>? roads = null, IEnumerable<(AuthoredElement Element, worldNode? Template, entEntityTemplate? DoorTemplate)>? elements = null,
        IEnumerable<AuthoredCollision>? collisions = null, IReadOnlyDictionary<string, worldNode>? objectTemplates = null,
        IEnumerable<PreparedNode>? prepared = null, IEnumerable<GeneratedResource>? extraResources = null)
    {
        var input = objects.OrderBy(o => o.Id, StringComparer.Ordinal).Select(o => new Entry(o.Id, o.Transform.Position, o, null, Template: objectTemplates?.GetValueOrDefault(o.Id)))
            .Concat((collisions ?? []).OrderBy(c => c.Id, StringComparer.Ordinal).Select(c => new Entry(c.Id, c.Transform.Position, null, null, Collision: c)))
            .Concat((roads ?? []).OrderBy(r => r.Id, StringComparer.Ordinal).Select(r => new Entry(r.Id, r.Origin, null, r)))
            .Concat((elements ?? []).OrderBy(e => e.Element.Id, StringComparer.Ordinal).Select(e => new Entry(e.Element.Id, e.Element.Transform.Position, null, null, e.Element, e.Template, e.DoorTemplate)))
            .Concat((prepared ?? []).Select(p => new Entry(p.Id, p.Transform.Position, null, null, Prepared: p))).ToArray();
        if (project == Guid.Empty || input.Length == 0) throw new InvalidDataException("A custom world requires a project identity and added objects.");
        var resources = new List<GeneratedResource>(); var locations = new List<AuthoredResourceLocation>();
        var block = new worldStreamingBlock(); int sectorNumber = 0;
        // This grid partitions our own output; source discovery never relies on it.
        foreach (var cell in input.GroupBy(o => (Math.Floor(o.Position.X / 128d), Math.Floor(o.Position.Y / 128d))))
        foreach (var batch in cell.Chunk(ushort.MaxValue))
        {
            string path = Namespace(project) + $"\\sectors\\added_{sectorNumber++:D4}.streamingsector";
            var sector = new worldStreamingSector { Version = 62, Level = 0, Category = Enums.worldStreamingSectorCategory.Exterior };
            sector.VariantIndices.Add(0);
            var setups = new worldNodeDataBuffer(); sector.NodeData.Buffer.Data = setups;
            sector.NodeData.Buffer.Parent = sector; sector.NodeData.Buffer.RootChunk = sector;
            sector.NodeData.Buffer.ParentTypes.Add("worldStreamingSector.transforms");
            Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
            foreach (var entry in batch)
            {
                PlacementTransform t; worldNode node; Bounds3 gameBounds;
                if (entry.Prepared is { } ready)
                {
                    // Elevator parts: built and referenced by ElevatorNodes; their streaming covers the shaft.
                    t = ready.Transform.Validated();
                    node = ready.Node;
                    gameBounds = ready.Bounds;
                }
                else if (entry.Collision is { } collision)
                {
                    // A collision actor: one worldCollisionNode (WolvenKit's writer fills the counts).
                    t = collision.Transform.Validated();
                    node = CollisionNode(project, collision, out var reach);
                    gameBounds = new(t.Position - new Vector3(reach), t.Position + new Vector3(reach));
                }
                else if (entry.Element is { } element)
                {
                    // Lights, effects, sounds and decals: their reach (or a small box) sets the streaming range.
                    t = element.Transform.Validated();
                    node = ElementNodes.Create(element, entry.Template, entry.DoorTemplate);
                    float reach = element.Properties.Kind is ElementKind.Light or ElementKind.Sound ? element.Properties.Radius : 2 * MathF.Max(t.Scale.X, MathF.Max(t.Scale.Y, t.Scale.Z));
                    gameBounds = new(t.Position - new Vector3(reach), t.Position + new Vector3(reach));
                }
                else
                {
                var meshReference = entry.Object?.Mesh ?? entry.Road!.Mesh;
                if (!meshes.TryGetValue(meshReference.Hash, out var mesh)) throw new InvalidDataException($"Mesh was not resolved: {meshReference.Path}");
                var reference = new CResourceAsyncReference<CMesh>(mesh.Reference.Path);
                if (reference.DepotPath.GetRedHash() != meshReference.Hash) throw new InvalidDataException($"Mesh path does not identify the expected resource: {meshReference.Path}");
                if (entry.Object is { } item)
                {
                    t = item.Transform.Validated();
                    // A copy of a game mesh node keeps every property; a new mesh starts from defaults.
                    node = entry.Template is worldMeshNode copied ? copied : new worldStaticMeshNode { Mesh = reference, MeshAppearance = item.Mesh.Appearance };
                    // A copy wearing project materials draws its mesh's variant (MaterialPaths).
                    if (entry.Template is worldMeshNode { } dressed && (MaterialPaths.IsMaterialPath(item.Mesh.Path) || NCWE.Core.MeshEditing.MeshEdits.IsEditedPath(item.Mesh.Path))) dressed.Mesh = reference;
                    node.DebugName = item.Name;
                    if (node is worldMeshNode meshNode && item.Occluder is { } occluder)
                    {
                        meshNode.OccluderType = Enum.Parse<Enums.visWorldOccluderType>(occluder.Type);
                        meshNode.OccluderAutohideDistanceScale = (byte)occluder.AutohideScale;
                    }
                    var transform = t;
                    gameBounds = Bounds3.From(Enumerable.Range(0, 8).Select(i => {
                        var b = mesh.ViewBounds;
                        var p = new Vector3((i & 1) == 0 ? b.Min.X : b.Max.X, (i & 2) == 0 ? b.Min.Y : b.Max.Y, (i & 4) == 0 ? b.Min.Z : b.Max.Z);
                        return Vector3.Transform(Coordinates.ViewToGame(p), transform.GameMatrix);
                    }));
                }
                else
                {
                    var road = entry.Road!;
                    t = (PlacementTransform.Identity with { Position = road.Origin }).Validated();
                    node = BendedRoad(road, reference);
                    gameBounds = new(road.DeformedBox.Min + road.Origin, road.DeformedBox.Max + road.Origin);
                }
                }
                float range = StreamingRange(gameBounds, t.Position, entry.Object?.StreamingDistance ?? entry.Road?.StreamingDistance);
                min = Vector3.Min(min, gameBounds.Min - new Vector3(range)); max = Vector3.Max(max, gameBounds.Max + new Vector3(range));
                ulong placementId = FNV1A64HashAlgorithm.HashString($"ncwe:{project:N}:{entry.Id}");
                int placementIndex = setups.Count;
                // Entities need a global node reference, as every shipping entity has one: the
                // game derives the entity's persistent identity from it, and devices (doors,
                // vending machines, terminals) only create their persistent state, hence their
                // interactions, with it. The sector lists the reference (NodeRefs, sorted by
                // hash) and the setup carries its hash (QuestPrefabRefHash, the field the
                // proxies' OwnerGlobalId points at). Stable across exports for saved states.
                string? nodeReference = entry.Prepared is { } p ? p.GlobalReference : node is worldEntityNode ? (string)NodeReferencePath(project, entry.Id, entry.Element?.Name ?? entry.Id) : null;
                NodeRef globalRef = nodeReference is null ? default(NodeRef) : (NodeRef)nodeReference;
                if (nodeReference is not null) sector.NodeRefs.Add(globalRef);
                setups.Add(new worldNodeData {
                    QuestPrefabRefHash = globalRef,
                    Id = placementId, NodeIndex = checked((ushort)sector.Nodes.Count),
                    // Collision actors carry world transforms; shipping collision setups are unrotated and unscaled.
                    Position = V4(t.Position), Orientation = entry.Collision is null ? new() { I = t.Rotation.X, J = t.Rotation.Y, K = t.Rotation.Z, R = t.Rotation.W } : new() { R = 1 },
                    Scale = entry.Collision is null ? V3(t.Scale) : V3(Vector3.One), Pivot = V3(t.Position),
                    // The node streaming bounds describe its reference point. The descriptor
                    // encloses transformed geometry plus streaming range, including large meshes.
                    Bounds = new() { Min = V4(t.Position), Max = V4(t.Position) },
                    MaxStreamingDistance = range, UkFloat1 = range, Uk10 = 1024, Uk11 = 512
                });
                sector.Nodes.Add(new(node)); locations.Add(new(entry.Id, path, placementIndex, placementId));
            }
            // Shipping sectors keep their references in hash order (looked up by hash).
            var sorted = sector.NodeRefs.OrderBy(r => (ulong)r).ToArray();
            sector.NodeRefs.Clear(); foreach (var reference in sorted) sector.NodeRefs.Add(reference);
            block.Descriptors.Add(new worldStreamingSectorDescriptor {
                Data = new(path), Level = sector.Level, Category = sector.Category, NumNodeRanges = 1,
                StreamingBox = new() { Min = V4(min), Max = V4(max) }
            });
            resources.Add(new(path, new CR2WFile { RootChunk = sector }));
        }
        resources.Add(new(BlockPath(project), new CR2WFile { RootChunk = block }));
        resources.AddRange(extraResources ?? []);
        return new(BlockPath(project), resources.ToArray(), locations.ToArray());
    }

    // A node's streaming distance, measured by the game from the node position (its bounds are that
    // point): the chosen distance, else twice the radius (120 m at least) plus how far the geometry's
    // centre lies from that point (a mesh whose pivot is off its geometry still streams in around it).
    public static float StreamingRange(Bounds3 gameBounds, Vector3 position, float? chosen) =>
        chosen is float distance && distance > 0 ? distance : Math.Max(120, gameBounds.Radius * 2) + Vector3.Distance(position, gameBounds.Center);

    // "$/mod/ncwe_<project>/#<name>_<element>" (shipping form: "$/03_night_city/.../#q001_door_mb_v_floor").
    public static string NodeReferencePath(Guid project, string elementId, string name)
    {
        var slug = new System.Text.StringBuilder();
        foreach (char ch in name.ToLowerInvariant()) slug.Append(ch is >= 'a' and <= 'z' or >= '0' and <= '9' ? ch : '_');
        string clean = System.Text.RegularExpressions.Regex.Replace(slug.ToString(), "_+", "_").Trim('_');
        string id = elementId.StartsWith("e:", StringComparison.Ordinal) ? elementId[2..] : elementId;
        return $"$/mod/ncwe_{project:N}/#{(clean.Length == 0 ? "entity" : clean[..Math.Min(clean.Length, 40)])}_{id[..Math.Min(id.Length, 12)]}";
    }

    // An authored collision as the game cooks one: one actor at the collision transform, shapes in
    // actor space (simple shapes with their size; copied mesh shapes keep their cooked mesh and
    // the source group as SectorHash). Header values follow shipping nodes: Lod 1, version 2,
    // per-type shape counts in categories 0 and 3, half extents around the actor.
    public static worldCollisionNode CollisionNode(Guid project, AuthoredCollision collision, out float reach)
    {
        var c = collision.Validated(); var t = c.Transform;
        var actor = new CollisionActor { Position = new WorldPosition(), Orientation = new WolvenKit.RED4.Types.Quaternion { I = t.Rotation.X, J = t.Rotation.Y, K = t.Rotation.Z, R = t.Rotation.W }, Scale = V3(t.Scale) };
        actor.Position.X = t.Position.X; actor.Position.Y = t.Position.Y; actor.Position.Z = t.Position.Z;
        var counts = new int[6]; var extent = Vector3.Zero;
        foreach (var shape in c.Shapes)
        {
            WolvenKit.RED4.Archive.Buffer.CollisionShape written = shape.IsMesh
                ? new CollisionShapeMesh { Hash = shape.MeshHash }
                : new CollisionShapeSimple { Size = V3(shape.Size) };
            written.ShapeType = shape.Kind switch
            {
                CollisionShapeKind.Box => Enums.physicsShapeType.Box, CollisionShapeKind.Sphere => Enums.physicsShapeType.Sphere, CollisionShapeKind.Capsule => Enums.physicsShapeType.Capsule,
                CollisionShapeKind.ConvexMesh => Enums.physicsShapeType.ConvexMesh, _ => Enums.physicsShapeType.TriangleMesh
            };
            written.Position = V3(shape.Position);
            written.Rotation = new WolvenKit.RED4.Types.Quaternion { I = shape.Rotation.X, J = shape.Rotation.Y, K = shape.Rotation.Z, R = shape.Rotation.W };
            written.Preset = shape.Preset;
            written.ProxyType = Enums.physicsProxyType.CharacterObstacle;
            written.Materials.Add(shape.Material.Length > 0 ? shape.Material : "default_material.physmat");
            actor.Shapes.Add(written);
            counts[(int)shape.Kind]++;
            var half = shape.IsMesh ? new Vector3(1) : shape.HalfExtents;
            extent = Vector3.Max(extent, (Vector3.Abs(shape.Position) + new Vector3(half.Length())) * Vector3.Abs(t.Scale));
        }
        var buffer = new CollisionBuffer(); buffer.Actors.Add(actor);
        var node = new worldCollisionNode
        {
            CompiledData = new DataBuffer { Data = buffer }, DebugName = c.Name, Lod = 1, ResourceVersion = 2,
            SectorHash = c.CookedSector != 0 ? c.CookedSector : FNV1A64HashAlgorithm.HashString($"ncwe:{project:N}:collision:{c.Id}"),
            Extents = V4(extent.X, extent.Y, extent.Z, 0), StaticCollisionShapeCategories = new worldStaticCollisionShapeCategories_CollisionNode()
        };
        // WolvenKit's collision writer reads the node (its counts) through the buffer's parent.
        node.CompiledData.Buffer.Parent = node;
        foreach (int category in new[] { 0, 3 })
            for (int k = 0; k < counts.Length && k < node.StaticCollisionShapeCategories.Arr[category].Count; k++) node.StaticCollisionShapeCategories.Arr[category][k] = (ushort)counts[k];
        reach = MathF.Max(2, extent.Length());
        return node;
    }

    // A road segment as the game stores one: the piece cooked for bending, one deformation
    // matrix per bone (node space), and the deformed box. Values follow shipping 2.31 nodes.
    private static worldBendedMeshNode BendedRoad(AuthoredRoadSegment road, CResourceAsyncReference<CMesh> mesh)
    {
        var data = new CArray<CMatrix>();
        foreach (var f in road.Frames)
            data.Add(new CMatrix { X = V4(f.M11, f.M12, f.M13, f.M14), Y = V4(f.M21, f.M22, f.M23, f.M24), Z = V4(f.M31, f.M32, f.M33, f.M34), W = V4(f.M41, f.M42, f.M43, f.M44) });
        // A cable as the game stores its own (2,752 of the 3,012 shipping ones carry exactly these
        // values): one matrix per joint of its tube, its length and its radius in centimetres.
        if (road.Cable)
            return new worldCableMeshNode
            {
                Mesh = mesh, MeshAppearance = road.Mesh.Appearance, DebugName = road.Name, DeformationData = data,
                DeformedBox = new() { Min = V4(road.DeformedBox.Min.X, road.DeformedBox.Min.Y, road.DeformedBox.Min.Z, 1), Max = V4(road.DeformedBox.Max.X, road.DeformedBox.Max.Y, road.DeformedBox.Max.Z, 1) },
                IsBendedRoad = false, CastShadows = Enums.shadowsShadowCastingMode.Always, CastLocalShadows = Enums.shadowsShadowCastingMode.Always,
                NavigationSetting = new() { NavmeshImpact = Enums.NavGenNavmeshImpact.Blocking }, Version = 1,
                CableLength = road.Length, CableRadius = CableBending.RadiusOf(road.Mesh.Path)
            };
        return new worldBendedMeshNode
        {
            Mesh = mesh, MeshAppearance = road.Mesh.Appearance, DebugName = road.Name, DeformationData = data,
            DeformedBox = new() { Min = V4(road.DeformedBox.Min), Max = V4(road.DeformedBox.Max) },
            IsBendedRoad = true, CastShadows = Enums.shadowsShadowCastingMode.Always, CastLocalShadows = Enums.shadowsShadowCastingMode.Always,
            NavigationSetting = new() { NavmeshImpact = Enums.NavGenNavmeshImpact.Road }, Version = 1
        };
    }

    internal static PackedWorld Pack(string gameDirectory, GeneratedWorld world, string rawDirectory, string archivePath, CancellationToken cancellation,
        IReadOnlyCollection<CustomMeshes.CookedFile>? extraFiles = null)
    {
        OodleBootstrap.LoadFromGame(gameDirectory);
        var logger = new ExportLogger(); var expected = new Dictionary<ulong, (GeneratedResource Resource, byte[] Bytes)>();
        // Custom meshes and their textures, already written and checked by their cooker.
        foreach (var extra in extraFiles ?? [])
        {
            cancellation.ThrowIfCancellationRequested();
            ulong hash = FNV1A64HashAlgorithm.HashString(extra.Path);
            if (expected.ContainsKey(hash)) continue;
            using (var check = new MemoryStream(extra.Bytes)) using (var reader = new CR2WReader(check))
                if (reader.ReadFile(out var parsed) != EFileReadErrorCodes.NoError || parsed?.RootChunk is not (CMesh or CBitmapTexture or CMaterialInstance or worldTrafficPersistentResource
                    or worldTrafficPersistentLanePolygonResource or worldTrafficPersistentLaneConnectionsResource or worldTrafficPersistentSpatialResource
                    or worldFoliageCompiledResource))
                    throw new InvalidDataException("A generated resource is invalid: " + extra.Path);
                else parsed.Dispose();
            var target = Path.Combine(rawDirectory, extra.Path); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, extra.Bytes);
            expected.Add(hash, (new GeneratedResource(extra.Path, new CR2WFile()), extra.Bytes));
        }
        foreach (var resource in world.Resources)
        {
            cancellation.ThrowIfCancellationRequested();
            using var stream = new MemoryStream();
            using (var writer = new CR2WWriter(stream, System.Text.Encoding.UTF8, true) { LoggerService = logger }) writer.WriteFile(resource.File);
            byte[] bytes = stream.ToArray();
            VerifyResource(resource, bytes);
            var target = Path.Combine(rawDirectory, resource.Path); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, bytes);
            expected.Add(FNV1A64HashAlgorithm.HashString(resource.Path), (resource, bytes));
        }
        cancellation.ThrowIfCancellationRequested();
        using (var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            if (!new ArchiveWriter(new HashService(false), logger).WriteArchive(new(rawDirectory), output))
                throw new InvalidDataException("WolvenKit could not pack the generated world archive.");
            output.Flush(true);
        }
        // Read the packed archive, extract every resource, and compare uncompressed bytes.
        // This proves packaging integrity, not that the game streams the sector correctly.
        var code = new ArchiveReader().ReadArchive(archivePath, new HashService(false), out var archive);
        if (code != EFileReadErrorCodes.NoError) { archive?.Dispose(); throw new InvalidDataException($"Generated archive could not be read: {code}"); }
        using (archive)
        {
            if (!archive.Files.Keys.Order().SequenceEqual(expected.Keys.Order())) throw new InvalidDataException("Generated archive contains missing or unexpected resources.");
            foreach (var (hash, entry) in archive.Files)
            {
                cancellation.ThrowIfCancellationRequested(); using var data = new MemoryStream(); entry.Extract(data);
                if (!data.ToArray().AsSpan().SequenceEqual(expected[hash].Bytes)) throw new InvalidDataException($"Packed resource differs: {expected[hash].Resource.Path}");
            }
        }
        return new(expected.Select(e => new PackedResource(e.Value.Resource.Path, e.Key, Convert.ToHexString(SHA256.HashData(e.Value.Bytes)), e.Value.Bytes.Length)).ToArray(), world.Objects, logger.Messages.ToArray());
    }

    private static void VerifyResource(GeneratedResource original, byte[] bytes)
    {
        using var stream = new MemoryStream(bytes); using var reader = new CR2WReader(stream);
        var code = reader.ReadFile(out var file);
        if (code != EFileReadErrorCodes.NoError || file is null) throw new InvalidDataException($"Generated CR2W read failed: {code}");
        using (file)
        {
            if (original.File.RootChunk is worldStreamingSector expected)
            {
                if (file.RootChunk is not worldStreamingSector actual || actual.Version != expected.Version || actual.Nodes.Count != expected.Nodes.Count ||
                    actual.NodeData.Buffer.Data is not worldNodeDataBuffer setups || setups.Count != ((worldNodeDataBuffer)expected.NodeData.Buffer.Data!).Count)
                    throw new InvalidDataException("Generated sector lost node definitions or placement data.");
                var identity = new ResourceIdentity(1, original.Path, "roundtrip");
                var before = SectorReader.Convert(expected, identity, _ => null); var after = SectorReader.Convert(actual, identity, _ => null);
                if (!before.Placements.SequenceEqual(after.Placements)) throw new InvalidDataException("Generated sector placements changed during serialization.");
                if (before.CollisionActors.Length != after.CollisionActors.Length || before.CollisionActors.Zip(after.CollisionActors).Any(p => p.First.Shapes.Length != p.Second.Shapes.Length || Vector3.Distance(p.First.Transform.Position, p.Second.Transform.Position) > .01f))
                    throw new InvalidDataException("Generated collision changed during serialization.");
            }
            else if (original.File.RootChunk is worldStreamingBlock block)
            {
                if (file.RootChunk is not worldStreamingBlock actual || actual.Descriptors.Count != block.Descriptors.Count)
                    throw new InvalidDataException("Generated block lost its sector descriptors.");
                for (int i = 0; i < block.Descriptors.Count; i++)
                {
                    var a = actual.Descriptors[i]; var b = block.Descriptors[i];
                    if (!a.Data.Equals(b.Data) || !a.StreamingBox.Equals(b.StreamingBox) || a.NumNodeRanges != b.NumNodeRanges || a.Level != b.Level || !a.Category.Equals(b.Category))
                        throw new InvalidDataException("Generated streaming descriptor changed during serialization.");
                }
            }
            else if (original.File.RootChunk is gameDeviceResource devices)
            {
                // The device graph patch: every entry survives (hash, class, parents, children).
                if (file.RootChunk is not gameDeviceResource actual || actual.Data.Chunk is not { } read || devices.Data.Chunk is not { } written || read.Unk1.Count != written.Unk1.Count
                    || read.Unk1.Zip(written.Unk1).Any(p => p.First.Hash != p.Second.Hash || p.First.ClassName != p.Second.ClassName || p.First.Children.Count != p.Second.Children.Count || p.First.Parents.Count != p.Second.Parents.Count))
                    throw new InvalidDataException("Generated device graph changed during serialization.");
            }
            else if (original.File.RootChunk is gamePersistentStateDataResource states)
            {
                // The persistent states patch: every state survives under its persistent id.
                if (file.RootChunk is not gamePersistentStateDataResource actual || actual.Buffer.Data is not RedPackage read || states.Buffer.Data is not RedPackage written
                    || read.Chunks.Count != written.Chunks.Count || read.Chunks.Zip(written.Chunks).Any(p => p.First.GetType() != p.Second.GetType()))
                    throw new InvalidDataException("Generated persistent states changed during serialization.");
            }
            else if (original.File.RootChunk is JsonResource { Root.Chunk: localizationPersistenceOnScreenEntries texts })
            {
                // The elevators' floor names: every entry survives with its key and text.
                if (file.RootChunk is not JsonResource { Root.Chunk: localizationPersistenceOnScreenEntries read } || read.Entries.Count != texts.Entries.Count
                    || read.Entries.Zip(texts.Entries).Any(p => p.First.SecondaryKey != p.Second.SecondaryKey || p.First.FemaleVariant != p.Second.FemaleVariant))
                    throw new InvalidDataException("Generated floor names changed during serialization.");
            }
            else throw new NotSupportedException("Only authored sectors, their block and the elevators' device patches may enter this package.");
        }
    }

    private static WolvenKit.RED4.Types.Vector3 V3(Vector3 v) => new() { X = v.X, Y = v.Y, Z = v.Z };
    private static Vector4 V4(Vector3 v) => new() { X = v.X, Y = v.Y, Z = v.Z, W = 0 };
    private static Vector4 V4(float x, float y, float z, float w) => new() { X = x, Y = y, Z = z, W = w };
    private sealed class ExportLogger : ILoggerService
    {
        public List<string> Messages { get; } = [];
        public LoggerVerbosity LoggerVerbosity { get; private set; }
        public void SetLoggerVerbosity(LoggerVerbosity verbosity) => LoggerVerbosity = verbosity;
        public void Info(string s) => Messages.Add(s);
        public void Warning(string s) => Messages.Add("Warning: " + s);
        public void Error(string s) => throw new InvalidDataException(s);
        public void Error(Exception exception) => throw new InvalidDataException("WolvenKit export failed.", exception);
        public void Success(string s) => Messages.Add(s);
        public void Debug(string s) { }
    }
}

public sealed partial class GameArchiveIndex
{
    // An archive of already written resources only (custom meshes, traffic), without sectors.
    public async Task<PackedWorld> PackFilesAsync(IReadOnlyCollection<CustomMeshes.CookedFile> files, string rawDirectory, string archivePath, CancellationToken cancellation = default)
    {
        await _worker.WaitAsync(cancellation);
        try { return await Task.Run(() => WorldResourceWriter.Pack(GameDirectory, new GeneratedWorld("", [], []), rawDirectory, archivePath, cancellation, files), cancellation); }
        finally { _worker.Release(); }
    }

    public async Task<PackedWorld> PackAuthoredWorldAsync(Guid project, IReadOnlyCollection<AuthoredPlacement> objects,
        IReadOnlyDictionary<ulong, ExportMeshInfo> meshes, string rawDirectory, string archivePath, CancellationToken cancellation = default,
        IReadOnlyCollection<AuthoredRoadSegment>? roads = null, IReadOnlyCollection<(AuthoredElement Element, worldNode? Template, entEntityTemplate? DoorTemplate)>? elements = null,
        IReadOnlyCollection<AuthoredCollision>? collisions = null, IReadOnlyDictionary<string, worldNode>? objectTemplates = null,
        IReadOnlyCollection<CustomMeshes.CookedFile>? customFiles = null, IReadOnlyCollection<PreparedNode>? prepared = null, IReadOnlyCollection<GeneratedResource>? extraResources = null)
    {
        await _worker.WaitAsync(cancellation);
        try { return await Task.Run(() => {
            var world = WorldResourceWriter.Build(project, objects, meshes, roads, elements, collisions, objectTemplates, prepared, extraResources);
            try { return WorldResourceWriter.Pack(GameDirectory, world, rawDirectory, archivePath, cancellation, customFiles); }
            finally { foreach (var resource in world.Resources) resource.File.Dispose(); }
        }, cancellation); }
        finally { _worker.Release(); }
    }
}
