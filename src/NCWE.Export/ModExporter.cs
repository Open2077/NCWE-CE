// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using NCWE.Core;
using NCWE.Formats;
using NCWE.Formats.Traffic;

namespace NCWE.Export;

public sealed record PackageFile(string RelativePath, string Sha256, long Bytes);
public sealed record ExportManifest(int SchemaVersion, Guid ProjectId, string ProjectName, DateTimeOffset CreatedUtc,
    string GameFileVersion, string GameProductVersion, string ArchiveProfile, string TargetArchiveXlVersion,
    string? InstalledArchiveXlVersion, ResourceIdentity[] SourceBaselines, ExportMeshInfo[] MeshDependencies,
    int Transformed, int Deleted, int Added, PackageFile[] Files, PackedWorld? GeneratedWorld,
    bool VerifiedInGame, string[] Limitations, int Roads = 0, int RoadSegments = 0, int ReplacedRoadPieces = 0, int Elements = 0, int ReplacedElements = 0, int Junctions = 0);
public sealed record ExportResult(string Directory, string ManifestPath, ExportManifest Manifest);

public static class ModExporter
{
    // A road segment's collision actors (64 shapes at most each), at the segment's origin, with
    // identities stable across exports.
    internal static IEnumerable<AuthoredCollision> RoadCollisionActors(RoadDefinition road, int segment, System.Numerics.Vector3 origin, System.Collections.Immutable.ImmutableArray<CollisionShape> shapes)
    {
        string id = road.Id.StartsWith("r:", StringComparison.Ordinal) ? road.Id[2..] : road.Id;
        int n = 0;
        foreach (var chunk in shapes.Chunk(64))
            yield return new AuthoredCollision($"c:road-{id}-{segment:D4}-{n++:D2}", $"{road.Name} {segment + 1} collision", PlacementTransform.Identity with { Position = origin }, [.. chunk]);
    }

    // The stages an export goes through, with their usual share of the time (ExportTracker): the
    // keys ExportAsync begins, the titles a window shows (translated there).
    public static ExportTracker Plan(ProjectData project, GameArchiveIndex index, IProgress<ExportProgress>? stages, IProgress<string>? text)
    {
        var added = project.Changes.Added.Values.Select(a => a.Mesh.Hash).Distinct().ToArray();
        int custom = added.Count(index.IsCustom), roads = project.Changes.Roads.Count, cells = project.Changes.Terrain?.Count ?? 0;
        int shaping = project.Changes.Roads.Values.Count(r => r.Ground is not null), elevators = project.Changes.Elevators?.Count ?? 0;
        int elements = project.Changes.Elements.Values.Count(e => e.Properties.Kind != ElementKind.CustomSound);
        bool anything = project.Changes.Added.Count + roads + elements + project.Changes.Collisions.Count + elevators + cells + (project.Changes.Traffic?.Count ?? 0) > 0;
        return new ExportTracker(stages, text)
            .Plan("sources", "Checking the edited game sectors", .3 * project.Sources.Count)
            .Plan("junctions", "Building the road junctions", project.Changes.Roads.Values.Any(r => r.Joins) ? 10 : 0)
            .Plan("terrain", "Shaping the terrain", cells + shaping > 0 ? 3 + 1.5 * (cells + shaping) : 0)
            .Plan("roads", "Bending the roads", .6 * roads)
            .Plan("elements", "Preparing lights, effects and other elements", .05 * elements)
            .Plan("elevators", "Building the elevators", .6 * elevators)
            .Plan("meshes", "Checking the added meshes", .15 * (added.Length - custom))
            .Plan("custom", "Cooking the custom meshes", 4 * custom)
            .Plan("decals", "Cooking your decals", .6 * DecalsUsed(project).Count)
            .Plan("meshedit", "Cooking the edited meshes", 3 * project.Changes.Added.Values.Count(a => a.MeshEdit is not null))
            .Plan("materials", "Writing the materials and the meshes wearing them", project.Changes.Added.Values.Count(a => !a.Materials.IsDefaultOrEmpty) * .5 + (project.Changes.Materials?.Count ?? 0) * .5)
            .Plan("traffic", "Writing the traffic lanes", (project.Changes.Traffic?.Count ?? 0) > 0 ? 8 : 0)
            .Plan("archive", "Writing and checking the archive", anything ? 3 + .01 * (project.Changes.Added.Count + elements + 5 * roads) : 0)
            .Plan("finish", "Writing the manifest", .5);
    }

    // The custom decals (their material paths) the project's decal elements use.
    internal static IReadOnlyList<string> DecalsUsed(ProjectData project) => project.Changes.Elements.Values
        .Where(e => e.Properties.Kind == ElementKind.Decal && CustomDecalPaths.IsCustom(e.Properties.Resource))
        .Select(e => e.Properties.Resource.ToLowerInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    public static async Task<ExportResult> ExportAsync(ProjectData project, GameArchiveIndex index, string outputDirectory,
        IProgress<string>? progress = null, CancellationToken cancellation = default, ExportTracker? tracker = null)
    {
        EditorDocument.ValidateProject(project);
        string root = Path.GetFullPath(outputDirectory);
        if (IsWithin(root, index.GameDirectory)) throw new InvalidOperationException("Choose an export folder outside the game installation.");
        tracker ??= Plan(project, index, null, progress);
        progress = tracker.Text;
        var current = new Dictionary<ulong, SectorSnapshot>();
        tracker.Begin("sources", project.Sources.Count);
        foreach (var source in project.Sources.Values.OrderBy(s => s.Hash))
        {
            tracker.Item(Path.GetFileNameWithoutExtension(source.Path), "Validating source: " + source.Path);
            current.Add(source.Hash, await index.LoadSectorAsync(source.Hash, cancellation));
        }
        // Custom sounds have no game node: only an Open77 export carries them (Open77ResourceExporter).
        var gameElements = project.Changes.Elements.Values.Where(e => e.Properties.Kind != ElementKind.CustomSound).OrderBy(e => e.Id, StringComparer.Ordinal).ToArray();
        var elevators = (project.Changes.Elevators ?? System.Collections.Immutable.ImmutableDictionary<string, ElevatorDefinition>.Empty).Values.OrderBy(e => e.Id, StringComparer.Ordinal).ToArray();
        // Terrain: each edited cell's mesh replaces the game's (same path); its collision height
        // field is deleted from its sector and a copy with the edits goes into the project's sectors.
        NCWE.Formats.Terrain.TerrainExport? terrain = null;
        // Junctions where roads cross or meet: the roads leave gaps there, filled by generated meshes.
        tracker.Begin("junctions");
        var junctions = await JunctionExport.BuildAsync(project, index, tracker, cancellation);
        // Roads shaping the ground (RoadGround): their earthworks join the terrain's edits, then
        // their junctions' (applied after them).
        var shapes = new List<NCWE.Core.Terrain.TerrainShape>();
        foreach (var road in project.Changes.Roads.Values.Where(r => r.Ground is not null && !r.IsCable).OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            var resource = index.Find(road.Template.Hash) ?? throw new FileNotFoundException("Road type is not present in this archive profile: " + road.Template.Path);
            var template = await index.ReadMeshConcurrentAsync(resource.Hash, cancellation);
            if (RoadGround.Of(road, RoadBending.HalfWidth(template, RoadBending.AxisOf(resource.Path))) is { } shape) shapes.Add(shape);
        }
        shapes.AddRange(RoadJunctions.Grounds(junctions.Plan, project.Changes.Roads));
        var cells = project.Changes.Terrain ?? System.Collections.Immutable.ImmutableDictionary<string, NCWE.Core.Terrain.TerrainCellData>.Empty;
        if (cells.Count > 0 || shapes.Count > 0)
        {
            // Its parts report each cell (mesh, collision), the distant terrain and the plants.
            int touched = cells.Keys.Concat(shapes.SelectMany(s => s.Line).Select(p => NCWE.Core.Terrain.TerrainCell.At(p.X, p.Y).ToString())).Distinct().Count();
            tracker.Begin("terrain", 2 * touched + 4, estimated: true);
            progress?.Report($"Building the terrain ({cells.Count} cells, {shapes.Count} roads shaping it)...");
            terrain = await NCWE.Formats.Terrain.TerrainExporter.BuildAsync(index, project.Id, cells, tracker.Messages(), cancellation, shapes);
        }
        int terrainNodes = terrain?.Nodes.Count ?? 0;
        string? block = project.Changes.Added.Count == 0 && project.Changes.Roads.Count == 0 && gameElements.Length == 0 && project.Changes.Collisions.Count == 0 && elevators.Length == 0 && terrainNodes == 0 ? null : WorldResourceWriter.BlockPath(project.Id);
        // Elevators: the device graph and the call panels' states are merged into the world's.
        string? texts = elevators.Length == 0 ? null : ElevatorNodes.TextsPath(project.Id);
        var patches = elevators.Length == 0 ? null : new Dictionary<string, string>
        {
            [ElevatorNodes.DevicesPath(project.Id)] = ElevatorNodes.WorldDevices,
            [ElevatorNodes.StatesPath(project.Id)] = ElevatorNodes.WorldStates
        };
        // Traffic-only projects have no streaming change: the archive alone carries them (so do
        // terrain cells without a collision node to replace).
        string? configuration = null;
        if (block is not null || project.Changes.Modified.Count > 0 || (terrain?.Deletions.Count ?? 0) > 0
            || ((project.Changes.Traffic?.Count ?? 0) == 0 && terrain is null))
        {
            // Deleted nodes lower the count of the distant proxies that stood for them.
            var proxies = await ProxyCorrections.ComputeAsync(project, index, cancellation);
            configuration = ArchiveXlConfiguration.Build(project, current, block, patches, texts, terrain?.Deletions, proxies);
        }
        var meshes = new Dictionary<ulong, ExportMeshInfo>();
        var segments = new List<AuthoredRoadSegment>();
        // The game cooks no collision for bended road nodes: each segment gets its own actors.
        var collisions = project.Changes.Collisions.Values.OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
        collisions.AddRange(junctions.Collisions);
        tracker.Begin("roads", project.Changes.Roads.Count);
        foreach (var road in project.Changes.Roads.Values.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            tracker.Item(road.Name, (road.IsCable ? "Hanging cable: " : "Bending road: ") + road.Name);
            var resource = index.Find(road.Template.Hash) ?? throw new FileNotFoundException("Road type is not present in this archive profile: " + road.Template.Path);
            var rig = index.ReadRoadRig(resource.Hash) ?? throw new NotSupportedException(road.IsCable
                ? $"Cable \"{road.Name}\": {Path.GetFileName(resource.Path)} is not a tube the game can hang."
                : $"Road \"{road.Name}\": {Path.GetFileName(resource.Path)} is not rigged for bending. Choose a road type with game textures.");
            // Pieces cooked for bending are skinned to their road bones.
            var template = await index.ReadMeshConcurrentAsync(resource.Hash, cancellation);
            if (road.IsCable)
            {
                // A cable: its tube stretched over each length of it, one frame per joint, written as
                // the game's cable node. It hangs clear of everything: no junction, no collision.
                if (!rig.Cable) throw new NotSupportedException($"Cable \"{road.Name}\": {Path.GetFileName(resource.Path)} is not a tube the game can hang.");
                var stretches = new List<AuthoredRoadSegment>();
                foreach (var segment in RoadBuilder.Build(road, rig.Length, rig.Bones))
                {
                    var frames = CableBending.Frames(segment.Frames);
                    var hung = CableBending.Deform(template, frames, road.Name, rig.Thickness);
                    var box = Bounds3.From(hung.Parts.SelectMany(p => p.Vertices.ToArray()).Select(v => Coordinates.ViewToGame(v.Position)));
                    stretches.Add(new($"{road.Id}:{segment.Index:D4}", $"{road.Name} {segment.Index + 1}", road.Template with { Path = resource.Path }, segment.Origin, frames, box) { Cable = true, Length = segment.Length });
                }
                // Seen as far as the game sees its own: a shipping cable streams to about 3.1 times
                // the diagonal of its box (probe --cable-survey), each stretch here as its whole cable.
                if (stretches.Count > 0)
                {
                    var whole = Bounds3.From(stretches.SelectMany(s => new[] { s.Origin + s.DeformedBox.Min, s.Origin + s.DeformedBox.Max }));
                    float reach = MathF.Max(120, MathF.PI * whole.Size.Length());
                    segments.AddRange(stretches.Select(s => s with { StreamingDistance = MathF.Min(AuthoredPlacement.MaxStreamingDistance, reach + System.Numerics.Vector3.Distance(s.Origin, whole.Center)) }));
                }
                meshes.TryAdd(road.Template.Hash, new(road.Template with { Path = resource.Path }, template.Bounds, template.SourceHash, template.SourceArchive));
                continue;
            }
            var axis = RoadBending.AxisOf(resource.Path);
            foreach (var segment in RoadBuilder.Build(road, rig.Length, rig.Bones, junctions.Plan.GapsOf(road.Id)))
            {
                var bent = RoadBending.Deform(template, segment.Frames, road.Name, axis);
                var box = Bounds3.From(bent.Parts.SelectMany(p => p.Vertices.ToArray()).Select(v => Coordinates.ViewToGame(v.Position)));
                segments.Add(new($"{road.Id}:{segment.Index:D4}", $"{road.Name} {segment.Index + 1}", road.Template with { Path = resource.Path }, segment.Origin, segment.Frames, box));
                collisions.AddRange(RoadCollisionActors(road, segment.Index, segment.Origin, RoadCollision.Shapes(bent, segment.Frames)));
            }
            meshes.TryAdd(road.Template.Hash, new(road.Template with { Path = resource.Path }, template.Bounds, template.SourceHash, template.SourceArchive));
        }
        var elements = new List<(AuthoredElement, WolvenKit.RED4.Types.worldNode?, WolvenKit.RED4.Types.entEntityTemplate?)>();
        tracker.Begin("elements", gameElements.Length);
        foreach (var element in gameElements)
        {
            tracker.Item(element.Name, "Preparing element: " + element.Name);
            if (element.Properties.Resource.Length > 0 && index.Find(WolvenKit.Common.FNV1A.FNV1A64HashAlgorithm.HashString(element.Properties.Resource.ToLowerInvariant())) is null)
                throw new FileNotFoundException(CustomDecalPaths.IsCustom(element.Properties.Resource)
                    ? $"Decal \"{element.Name}\": its picture is no longer in your decal bank. Give it another decal, or delete it."
                    : $"Element \"{element.Name}\": {element.Properties.Resource} is not in this archive profile.");
            elements.Add((element, element.Template is { } template ? index.ReadTemplateNode(template) : null,
                element.Properties.Kind == ElementKind.Door ? index.ReadEntityTemplate(WolvenKit.Common.FNV1A.FNV1A64HashAlgorithm.HashString(element.Properties.Resource)) : null));
        }
        // Elevators: lift, floor markers, call panels, landing doors, shaft collision, device patches.
        var elevatorExports = new List<ElevatorExport>();
        tracker.Begin("elevators", elevators.Length);
        foreach (var elevator in elevators)
        {
            tracker.Item(elevator.Name, "Building elevator: " + elevator.Name);
            elevatorExports.Add(ElevatorNodes.Build(project.Id, elevator, path => index.ReadEntityTemplate(WolvenKit.Common.FNV1A.FNV1A64HashAlgorithm.HashString(path))));
        }
        collisions.AddRange(elevatorExports.Select(x => x.Shaft).OfType<AuthoredCollision>());
        var prepared = elevatorExports.SelectMany(x => x.Nodes).Concat(terrain?.Nodes ?? []).Concat(junctions.Nodes).ToArray();
        var deviceResources = elevatorExports.Count == 0 ? [] : new GeneratedResource[]
        {
            new(ElevatorNodes.DevicesPath(project.Id), ElevatorNodes.DeviceResource(elevatorExports.SelectMany(x => x.Devices))),
            new(ElevatorNodes.StatesPath(project.Id), ElevatorNodes.StateResource(elevatorExports.SelectMany(x => x.States))),
            new(ElevatorNodes.TextsPath(project.Id), ElevatorNodes.TextResource(elevatorExports.SelectMany(x => x.Texts)))
        };
        // Meshes that copy a game node (occlusion settings changed) are written from it, read fresh.
        var objectTemplates = new Dictionary<string, WolvenKit.RED4.Types.worldNode>(StringComparer.Ordinal);
        foreach (var item in project.Changes.Added.Values.Where(a => a.Template is not null)) objectTemplates[item.Id] = index.ReadTemplateNode(item.Template!);
        tracker.Begin("meshes", project.Changes.Added.Values.Select(a => a.Mesh.Hash).Where(h => !meshes.ContainsKey(h) && !index.IsCustom(h)).Distinct().Count());
        foreach (var item in project.Changes.Added.Values.OrderBy(o => o.Id, StringComparer.Ordinal))
        {
            if (meshes.ContainsKey(item.Mesh.Hash)) continue;
            if (!index.IsCustom(item.Mesh.Hash)) tracker.Item(Path.GetFileNameWithoutExtension(item.Mesh.Path), "Validating added mesh: " + item.Mesh.Path);
            var resource = index.Find(item.Mesh.Hash) ?? throw new FileNotFoundException("Added mesh is not present in this archive profile: " + item.Mesh.Path);
            var asset = await index.LoadMeshAsync(resource.Hash, cancellation);
            meshes.Add(item.Mesh.Hash, new(item.Mesh with { Path = resource.Path }, asset.Bounds, asset.SourceHash, asset.SourceArchive));
        }
        // Custom meshes: their game resources (mesh, textures) go into the archive beside the sectors.
        var customFiles = new List<NCWE.Formats.CustomMeshes.CookedFile>();
        var customHashes = project.Changes.Added.Values.Select(a => a.Mesh.Hash).Distinct().Order().Where(index.IsCustom).ToArray();
        tracker.Begin("custom", customHashes.Length);
        foreach (var hash in customHashes)
        {
            string? path = index.Find(hash)?.Path;
            tracker.Item(project.Changes.Added.Values.FirstOrDefault(a => a.Mesh.Hash == hash)?.Name ?? Path.GetFileNameWithoutExtension(path ?? hash.ToString()),
                "Building custom mesh: " + (path ?? hash.ToString()));
            customFiles.AddRange(await Task.Run(() => index.Custom!.Cook(hash, index, progress, cancellation), cancellation));
        }
        // Custom decals: each one's material instance and texture, once.
        var decalPaths = DecalsUsed(project);
        tracker.Begin("decals", decalPaths.Count);
        foreach (var path in decalPaths)
        {
            tracker.Item(Path.GetFileNameWithoutExtension(path), "Cooking decal: " + path);
            var decals = index.Decals ?? throw new FileNotFoundException("The decal bank is not available: " + path);
            customFiles.AddRange(await Task.Run(() => decals.Cook(path, cancellation), cancellation));
        }
        var exported = project.Changes.Added.Values.ToArray();
        // Meshes edited in place: the game's file with the edited geometry (its materials and slots
        // kept) at ncwe\meshedit\<project>\...; the copy's node draws it. The same edit of the same
        // mesh is cooked once.
        var editedFiles = new Dictionary<string, (MeshReference Reference, byte[] Bytes)>(StringComparer.OrdinalIgnoreCase);
        tracker.Begin("meshedit", exported.Count(a => a.MeshEdit is not null));
        for (int k = 0; k < exported.Length; k++)
        {
            var item = exported[k];
            if (item.MeshEdit is not { } edit) continue;
            tracker.Item(item.Name, "Cooking edited mesh: " + item.Name);
            string sourcePath = index.Find(item.Mesh.Hash)?.Path ?? throw new FileNotFoundException("Edited mesh is not present in this archive profile: " + item.Mesh.Path);
            string path = NCWE.Core.MeshEditing.MeshEdits.ExportPath(project.Id, edit, sourcePath);
            if (!editedFiles.TryGetValue(path, out var cooked))
            {
                var source = await Task.Run(() => index.ReadEditableMesh(item.Mesh.Hash, cancellation: cancellation), cancellation);
                if (edit.SourceFingerprint is { Length: > 0 } fingerprint && !string.Equals(fingerprint, source.Fingerprint, StringComparison.OrdinalIgnoreCase))
                    progress?.Report($"\"{item.Name}\": the game's mesh changed since it was edited (a game update): check the edit in the editor.");
                var result = NCWE.Core.MeshEditing.MeshEditor.Apply(source.Mesh, NCWE.Core.MeshEditing.MeshEdits.Ops(edit));
                string identity = edit.Id + ":" + NCWE.Core.MeshEditing.MeshEdits.Signature(edit);
                var bytes = await Task.Run(() => NCWE.Formats.MeshEditing.EditedMeshCooker.Cook(index, source.Path, result, Path.Combine(Path.GetTempPath(), "ncwe-meshedit"), identity), cancellation);
                var reference = new MeshReference(CustomMeshInfo.Fnv1a64(path), path);
                cooked = (reference, bytes);
                editedFiles[path] = cooked;
                customFiles.Add(new(path, bytes));
                if (!meshes.TryGetValue(item.Mesh.Hash, out var original)) throw new InvalidDataException("Mesh was not resolved: " + item.Mesh.Path);
                meshes[reference.Hash] = original with { Reference = reference, ViewBounds = result.Bounds };
            }
            exported[k] = item with { Mesh = cooked.Reference with { Appearance = item.Mesh.Appearance } };
        }
        // Project materials: each one worn becomes a .mi (with its own textures), each mesh wearing
        // some a variant of its file whose slots refer to them; the original stays the dependency.
        var wearing = exported.Where(a => !a.Materials.IsDefaultOrEmpty).ToArray();
        var worn = wearing.SelectMany(a => a.Materials).Select(s => s.Material).Distinct(StringComparer.Ordinal).Select(id => project.Changes.Materials[id]).ToArray();
        tracker.Begin("materials", worn.Length + wearing.Length);
        foreach (var material in worn)
        {
            tracker.Item(material.Name, "Writing material: " + material.Name);
            customFiles.AddRange(await Task.Run(() => NCWE.Formats.CustomMeshes.ProjectMaterialCooker.Cook(project.Id, material, index.MaterialStore, cancellation), cancellation));
        }
        var variants = new Dictionary<string, MeshReference>(StringComparer.OrdinalIgnoreCase);
        for (int k = 0; k < exported.Length; k++)
        {
            var item = exported[k];
            if (item.Materials.IsDefaultOrEmpty) continue;
            tracker.Item(item.Name, "Dressing mesh: " + item.Name);
            string path = MaterialPaths.Variant(project.Id, item.Mesh, item.Materials);
            if (!variants.TryGetValue(path, out var variant))
            {
                var slots = item.Materials.ToDictionary(s => s.Slot, s => MaterialPaths.Instance(project.Id, project.Changes.Materials[s.Material]), StringComparer.Ordinal);
                var baseMesh = item.Mesh;
                byte[] bytes = index.IsCustom(baseMesh.Hash) || NCWE.Core.MeshEditing.MeshEdits.IsEditedPath(baseMesh.Path)
                    ? MaterialVariants.Write(customFiles.FirstOrDefault(f => string.Equals(f.Path, baseMesh.Path, StringComparison.OrdinalIgnoreCase))?.Bytes
                        ?? throw new InvalidDataException("The custom or edited mesh of \"" + item.Name + "\" was not cooked."), slots)
                    : await Task.Run(() => index.MeshVariant(baseMesh.Hash, slots), cancellation);
                customFiles.Add(new(path, bytes));
                variant = new MeshReference(CustomMeshInfo.Fnv1a64(path), path);
                variants[path] = variant;
                if (!meshes.TryGetValue(baseMesh.Hash, out var source)) throw new InvalidDataException("Mesh was not resolved: " + baseMesh.Path);
                meshes[variant.Hash] = source with { Reference = variant };
            }
            exported[k] = item with { Mesh = variant with { Appearance = item.Mesh.Appearance } };
        }
        if (terrain is not null) customFiles.AddRange(terrain.Files);
        customFiles.AddRange(junctions.Files);
        // Traffic lanes: the game's traffic files with the project's lanes, at their own depot paths.
        TrafficExportSummary? traffic = null;
        if (project.Changes.Traffic is { Count: > 0 } lanes)
        {
            tracker.Begin("traffic");
            progress?.Report($"Writing traffic lanes ({lanes.Count} changed)...");
            var (trafficFiles, summary) = await Task.Run(() => TrafficExporter.Build(index, lanes), cancellation);
            customFiles.AddRange(trafficFiles); traffic = summary;
        }
        cancellation.ThrowIfCancellationRequested();
        // Each export is a new package. Failed generations stay marked partial and
        // cannot be installed (a cancelled one is removed); publishing is one same-volume
        // directory rename.
        Directory.CreateDirectory(root);
        string name = $"ncwe-{project.Id:N}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
        string working = Path.Combine(root, name + ".partial"), destination = Path.Combine(root, name);
        try { return await Write(); }
        catch (OperationCanceledException)
        {
            try { if (Directory.Exists(working)) Directory.Delete(working, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }

        async Task<ExportResult> Write()
        {
            string modDirectory = Path.Combine(working, "archive", "pc", "mod"); Directory.CreateDirectory(modDirectory);
            string modName = $"ncwe_{project.Id:N}";
            PackedWorld? packed = null;
            tracker.Begin("archive");
            if (block is not null)
            {
                progress?.Report("Writing and checking custom sectors and archive...");
                packed = await index.PackAuthoredWorldAsync(project.Id, exported, meshes,
                    Path.Combine(working, "generated"), Path.Combine(modDirectory, modName + ".archive"), cancellation, segments, elements,
                    collisions.ToArray(), objectTemplates, customFiles, prepared, deviceResources);
            }
            else if (customFiles.Count > 0)
            {
                progress?.Report("Writing and checking the archive...");
                packed = await index.PackFilesAsync(customFiles, Path.Combine(working, "generated"), Path.Combine(modDirectory, modName + ".archive"), cancellation);
            }
            tracker.Begin("finish");
            if (configuration is not null) File.WriteAllText(Path.Combine(modDirectory, modName + ".archive.xl"), configuration);
            ProjectStorage.WriteAtomic(Path.Combine(working, "project.ncwe.json"), project);
            var game = FileVersionInfo.GetVersionInfo(Path.Combine(index.GameDirectory, "bin", "x64", "Cyberpunk2077.exe"));
            string xl = Path.Combine(index.GameDirectory, "red4ext", "plugins", "ArchiveXL", "ArchiveXL.dll");
            var files = Directory.EnumerateFiles(modDirectory).Order(StringComparer.Ordinal).Select(path =>
                new PackageFile(Path.GetRelativePath(working, path).Replace('\\', '/'), HashFile(path), new FileInfo(path).Length)).ToArray();
            var manifest = new ExportManifest(1, project.Id, project.Name, DateTimeOffset.UtcNow, game.FileVersion ?? "unknown", game.ProductVersion ?? "unknown",
                project.ArchiveProfile, ArchiveXlConfiguration.TargetVersion, File.Exists(xl) ? FileVersionInfo.GetVersionInfo(xl).FileVersion : null,
                project.Sources.Values.OrderBy(s => s.Hash).ToArray(),
                meshes.Values.Where(m => !MaterialPaths.IsMaterialPath(m.Reference.Path) && !NCWE.Core.MeshEditing.MeshEdits.IsEditedPath(m.Reference.Path)).OrderBy(m => m.Reference.Hash).ToArray(),
                project.Changes.Modified.Values.Count(e => !e.Deleted && e.Transform is not null), project.Changes.Modified.Values.Count(e => e.Deleted),
                project.Changes.Added.Count, files, packed, false,
                ["Only static visual meshes and the supported instanced mesh subset are patched. Collision, navigation, entities, quests and distant proxy representations are separate.",
                 "Archive profile: vanilla content. Expansion and mod overrides are excluded.",
                 "Revalidate this project against the current installation after a game update. ArchiveXL does not check NCWE source fingerprints.",
                 "CR2W and archive round trips passed; in-game appearance, streaming, travel and restart acceptance remain separate runtime checks.",
                 "Lights, particles, effects, sounds and decals are new nodes in the authored sectors; an edited game element is a copy of its node (all other properties kept) that replaces it.",
                 "Roads are bended road pieces (worldBendedMeshNode) with the game's own materials; the game road pieces they replace are removed. Where roads cross or one ends on another, they stop at a junction: a generated mesh (the game's asphalt, sidewalk and curb materials) with box collision. Traffic, navigation and road markings across junctions are not regenerated. Cables are the game's cable nodes (worldCableMeshNode), their tube hung along the curve in stretches of its length: no collision, no junctions, the game cables they replace removed.",
                 "Custom sounds are played by Open77 only: they are not in this archive (an Open77 export adds them to its resource).",
                 decalPaths.Count == 0 ? "No custom decal." : $"Custom decals ({decalPaths.Count}): each is a material instance of the game's decal template (base\\materials\\decal.remt, or decal_emissive_color.mt for glowing ones) with its own texture, at ncwe\\decals\\.",
                 "Project materials are material instances (.mi) of this archive; a mesh wearing one is a copy of its mesh file (a variant) whose slots refer to it: the game's own meshes and materials are not changed.",
                 editedFiles.Count == 0 ? "No mesh is edited in place." : $"Meshes edited in place ({editedFiles.Count}): each is a copy of the game's mesh file with the edited geometry (one level of detail, its materials and slots kept) at ncwe\\meshedit\\; the node drawing it is a copy of the game's node that replaces it. The game's cooked collision of that node stays (openings cut in a mesh are not opened in its collision: remove or replace it in the editor). In-game check pending.",
                 elevators.Length == 0 ? "No elevator." : $"Elevators ({elevators.Length}): a lift (LiftDevice) per elevator, a call panel and a landing door per floor, floor markers and invisible shaft walls; their device graph and the lift's and call panels' states are merged into the world's .devices and .psrep (ArchiveXL resource patches, 1.20 or later); floor names are localization entries (every language). In-game check pending.",
                 terrain is null ? "The terrain is the game's." : $"Terrain: {terrain.Summary.Cells} cells edited ({terrain.Summary.SculptedCells} sculpted, {terrain.Summary.PaintedCells} painted, {terrain.Summary.CutCells} cut open). Each edited cell's mesh replaces the game's own file (another mod replacing the same cells conflicts); {terrain.Summary.Collisions} collision height fields are replaced (deleted from their sector, an edited copy added){(terrain.Summary.CellsWithoutCollision > 0 ? $", {terrain.Summary.CellsWithoutCollision} cells had none to replace" : "")}; {terrain.Summary.Proxies} distant terrain meshes (seen beyond ~360 m) follow the heights and paint (holes stay closed there); {terrain.Summary.PlantsMoved} plants follow the ground and {terrain.Summary.PlantsRemoved} are removed (in holes or cleared) ({terrain.Summary.PlantNodes} plant nodes replaced). Objects on the ground follow it when you sculpt (they are edits of the project). Navigation (NPC and vehicle paths) is not regenerated.",
                 traffic is null ? "Traffic lanes are the game's." : $"Traffic lanes: {traffic.Edited} edited, {traffic.Deleted} disabled, {traffic.Created} drawn ({traffic.Total} in all). The archive replaces the game's traffic files (all.traffic_persistent, all.lane_polygons, all.lane_connections, all.spatial_representation): another mod replacing them conflicts. Traffic collisions are not regenerated."],
                project.Changes.Roads.Count, segments.Count, project.Changes.Roads.Values.Sum(r => r.Replaces.Length),
                gameElements.Length, gameElements.Sum(e => e.Replaces.Length), junctions.Plan.Junctions.Length);
            File.WriteAllText(Path.Combine(working, "manifest.json"), JsonSerializer.Serialize(manifest, ProjectStorage.JsonOptions));
            File.WriteAllText(Path.Combine(working, "README.txt"), $"NCWE project: {project.Name}\nTarget: Cyberpunk 2077 2.31 / ArchiveXL {ArchiveXlConfiguration.TargetVersion}\n\n" +
                "The archive/pc/mod folder contains this mod's installation files. Keep the manifest and project for revalidation.\n" +
                "Never replace another mod's files. Remove only the files listed in manifest.json to uninstall this export.\n\n" + string.Join("\n", manifest.Limitations));
            cancellation.ThrowIfCancellationRequested();
            Directory.Move(working, destination);
            tracker.Next("Export ready: " + destination);
            return new(destination, Path.Combine(destination, "manifest.json"), manifest);
        }
    }

    public static string HashFile(string path)
    { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    internal static bool IsWithin(string path, string parent)
    {
        string relative = Path.GetRelativePath(Path.GetFullPath(parent), Path.GetFullPath(path));
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
}
