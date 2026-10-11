// SPDX-License-Identifier: GPL-3.0-only
using WolvenKit.RED4.Archive.Buffer;
using WolvenKit.RED4.Archive.IO;
using WolvenKit.RED4.Types;

namespace NCWE.Formats;

public sealed partial class GameArchiveIndex
{
    // Diagnostic: entity/device nodes whose template path contains a filter, with their
    // appearance, quest-prefab flag and instance-data contents.
    public IEnumerable<string> InspectEntities(ulong sectorHash, string contains)
    {
        var entry = EntryOrNull(sectorHash) ?? throw new FileNotFoundException();
        using var data = ExtractEntry(entry, default);
        using var reader = new CR2WReader(data);
        reader.ParsingError += SectorReader.TolerateSectorParsingError;
        if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file is null) yield break;
        using (file)
        {
            var sector = (worldStreamingSector)file.RootChunk;
            var setups = (worldNodeDataBuffer)sector.NodeData.Buffer.Data!;
            for (int i = 0; i < setups.Count; i++)
            {
                if (sector.Nodes[setups[i].NodeIndex].Chunk is not worldEntityNode node) continue;
                string template = node.EntityTemplate.DepotPath.GetResolvedText() ?? node.EntityTemplate.DepotPath.GetRedHash().ToString();
                if (!template.Contains(contains, StringComparison.OrdinalIgnoreCase)) continue;
                string instance = node.InstanceData.Chunk?.Buffer.Data is WolvenKit.RED4.Archive.Buffer.RedPackage package
                    ? string.Join(",", package.Chunks.Select(ch => ch.GetType().Name)) : node.InstanceData.Chunk is null ? "none" : "raw";
                yield return $"{Path.GetFileName(entry.FileName)} #{i} {node.GetType().Name} quest={(ulong)setups[i].QuestPrefabRefHash != 0} app={node.AppearanceName} {template} instance=[{instance}]"
                    + (node is worldDeviceNode device ? $" class={device.DeviceClassName} connections={device.DeviceConnections.Count}" : "");
            }
        }
    }

    // Diagnostic: a resource (.ent, .app, .mi...) as a tree of its RED classes, packages included.
    public IEnumerable<string> DumpResource(ulong hash, int depth = 6)
    {
        var entry = EntryOrNull(hash) ?? throw new FileNotFoundException();
        using var data = ExtractEntry(entry, default);
        using var reader = new CR2WReader(data);
        if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file is null) throw new InvalidDataException();
        using (file)
        {
            var lines = new List<string>();
            Dump(file.RootChunk, "", depth, lines, new HashSet<object>(ReferenceEqualityComparer.Instance));
            foreach (var line in lines) yield return line;
        }
    }

    // Diagnostic: the instance data of one placement in a sector.
    public IEnumerable<string> DumpInstanceData(ulong sectorHash, int placement, int depth = 6)
    {
        var entry = EntryOrNull(sectorHash) ?? throw new FileNotFoundException();
        using var data = ExtractEntry(entry, default);
        using var reader = new CR2WReader(data);
        reader.ParsingError += SectorReader.TolerateSectorParsingError;
        if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file is null) throw new InvalidDataException();
        using (file)
        {
            var sector = (worldStreamingSector)file.RootChunk;
            var setups = (worldNodeDataBuffer)sector.NodeData.Buffer.Data!;
            var node = sector.Nodes[setups[placement].NodeIndex].Chunk!;
            var lines = new List<string>();
            Dump(node, "", depth, lines, new HashSet<object>(ReferenceEqualityComparer.Instance));
            foreach (var line in lines) yield return line;
        }
    }

    public static void Dump(object? value, string indent, int depth, List<string> lines, HashSet<object> seen)
    {
        if (lines.Count > 4000) return;
        switch (value)
        {
            case null: return;
            case WolvenKit.RED4.Archive.Buffer.RedPackage package:
                lines.Add($"{indent}package ({package.Chunks.Count} chunks)");
                for (int i = 0; i < package.Chunks.Count; i++) { lines.Add($"{indent}  chunk {i}: {package.Chunks[i].GetType().Name}"); if (depth > 0) Dump(package.Chunks[i], indent + "    ", depth - 1, lines, seen); }
                return;
            case RedBaseClass cls:
                if (!seen.Add(cls) || depth < 0) return;
                foreach (var p in cls.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly).Concat(
                    cls.GetType().BaseType is { } b ? b.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance) : []).DistinctBy(p => p.Name))
                {
                    if (p.GetIndexParameters().Length != 0) continue;
                    object? v; try { v = p.GetValue(cls); } catch { continue; }
                    switch (v)
                    {
                        case null: continue;
                        case IRedRef r: { var t = r.DepotPath.GetResolvedText(); if (t is { Length: > 0 }) lines.Add($"{indent}{p.Name} = {t}"); continue; }
                        case IRedHandle h: if (h.GetValue() is RedBaseClass hv) { lines.Add($"{indent}{p.Name} -> {hv.GetType().Name}"); Dump(hv, indent + "  ", depth - 1, lines, seen); } continue;
                        case DataBuffer db: lines.Add($"{indent}{p.Name}: DataBuffer"); Dump(db.Data, indent + "  ", depth - 1, lines, seen); continue;
                        case SerializationDeferredDataBuffer sd: lines.Add($"{indent}{p.Name}: DeferredBuffer"); Dump(sd.Data, indent + "  ", depth - 1, lines, seen); continue;
                        case System.Collections.IList list when v is IRedArray:
                            if (list.Count == 0) continue;
                            lines.Add($"{indent}{p.Name} [{list.Count}]");
                            int k = 0; foreach (var item in list) { if (k++ >= 24) { lines.Add($"{indent}  …"); break; } if (item is RedBaseClass or IRedHandle) { lines.Add($"{indent}  [{k - 1}]"); Dump(item is IRedHandle ih ? ih.GetValue() : item, indent + "    ", depth - 1, lines, seen); } else lines.Add($"{indent}  [{k - 1}] {item}"); }
                            continue;
                        case RedBaseClass inner: lines.Add($"{indent}{p.Name}: {inner.GetType().Name}"); Dump(inner, indent + "  ", depth - 1, lines, seen); continue;
                        default:
                            var text = v.ToString(); if (text is null || text == "0" || text == "False" || text == "None" || text.Length == 0) continue;
                            lines.Add($"{indent}{p.Name} = {text}"); continue;
                    }
                }
                return;
        }
    }

    // Diagnostic: the simple-valued properties of the first nodes of one type in a sector.
    // Diagnostics: collision and occlusion content of sectors (shape types, presets, proxy types,
    // materials, node fields, occluder nodes and mesh occluder settings).
    public IEnumerable<string> SurveyCollisionAndOcclusion(IEnumerable<ulong> sectorHashes, bool dumpFirst)
    {
        var shapeTypes = new Dictionary<string, int>(); var presets = new Dictionary<string, int>(); var proxies = new Dictionary<string, int>();
        var materials = new Dictionary<string, int>(); var occluders = new Dictionary<string, int>(); var occluderMeshes = new Dictionary<string, int>();
        var occluderTypes = new Dictionary<string, int>(); var shapeCombos = new Dictionary<string, int>();
        int sectors = 0, nodes = 0, actors = 0; bool dumpedCollision = !dumpFirst, dumpedOccluder = !dumpFirst, dumpedInstanced = !dumpFirst;
        var dumps = new List<string>();
        static void Count(Dictionary<string, int> d, string k) => d[k] = d.GetValueOrDefault(k) + 1;
        foreach (var hash in sectorHashes)
        {
            var entry = EntryOrNull(hash); if (entry is null) continue;
            WolvenKit.RED4.Archive.CR2W.CR2WFile? file;
            using (var data = ExtractEntry(entry, default))
            {
                var reader = new CR2WReader(data); reader.ParsingError += SectorReader.TolerateSectorParsingError;
                if (reader.ReadFile(out file) != EFileReadErrorCodes.NoError || file is null) continue;
            }
            using (file)
            {
                sectors++;
                var sector = (worldStreamingSector)file.RootChunk;
                foreach (var handle in sector.Nodes)
                {
                    var node = handle.Chunk;
                    if (node is worldCollisionNode collision)
                    {
                        nodes++;
                        if (!dumpedCollision) { dumpedCollision = true; var lines = new List<string>(); Dump(collision, "  ", 2, lines, new HashSet<object>(ReferenceEqualityComparer.Instance)); dumps.Add("--- worldCollisionNode fields"); dumps.AddRange(lines.Where(l => !l.Contains("Actors"))); }
                        if (collision.CompiledData.Buffer.Data is not WolvenKit.RED4.Archive.Buffer.CollisionBuffer buffer) continue;
                        foreach (var actor in buffer.Actors)
                        {
                            actors++;
                            Count(shapeCombos, string.Join("+", actor.Shapes.Select(s => s.ShapeType.ToString())));
                            foreach (var shape in actor.Shapes)
                            {
                                Count(shapeTypes, $"{shape.GetType().Name}/{shape.ShapeType}");
                                Count(presets, shape.Preset.ToString() ?? "");
                                Count(proxies, shape.ProxyType.ToString());
                                foreach (var m in shape.Materials) Count(materials, m.ToString() ?? "");
                            }
                        }
                    }
                    else if (node is not null && node.GetType().Name.Contains("Occluder"))
                    {
                        Count(occluders, node.GetType().Name);
                        if (node is worldStaticOccluderMeshNode occ) Count(occluderMeshes, occ.Mesh.DepotPath.GetResolvedText() ?? occ.Mesh.DepotPath.GetRedHash().ToString("x16"));
                        if (node is worldStaticOccluderMeshNode && !dumpedOccluder || node is worldInstancedOccluderNode && !dumpedInstanced)
                        {
                            if (node is worldStaticOccluderMeshNode) dumpedOccluder = true; else dumpedInstanced = true;
                            var lines = new List<string>(); Dump(node, "  ", 4, lines, new HashSet<object>(ReferenceEqualityComparer.Instance));
                            dumps.Add("--- " + node.GetType().Name + " fields"); dumps.AddRange(lines.Take(60));
                        }
                    }
                    if (node is worldMeshNode mesh) Count(occluderTypes, $"{node.GetType().Name}:{mesh.OccluderType}/{(byte)mesh.OccluderAutohideDistanceScale}");
                }
            }
        }
        yield return $"sectors {sectors}, collision nodes {nodes}, actors {actors}";
        string Top(Dictionary<string, int> d, int n) => string.Join(", ", d.OrderByDescending(e => e.Value).Take(n).Select(e => $"{(e.Key.Length == 0 ? "<none>" : e.Key)}={e.Value}"));
        yield return "shape types: " + Top(shapeTypes, 20);
        yield return "actor shape combos: " + Top(shapeCombos, 12);
        yield return "presets: " + Top(presets, 30);
        yield return "proxy types: " + Top(proxies, 20);
        yield return "materials: " + Top(materials, 40);
        yield return "occluder nodes: " + Top(occluders, 10);
        yield return "occluder meshes: " + Top(occluderMeshes, 25);
        yield return "mesh occluder settings: " + Top(occluderTypes, 25);
        foreach (var line in dumps) yield return line;
    }

    // Diagnostics: instanced occluder nodes, their setup and raw instance vectors.
    public IEnumerable<string> DumpInstancedOccluders(ulong sectorHash, int limit)
    {
        var entry = EntryOrNull(sectorHash) ?? throw new FileNotFoundException();
        using var data = ExtractEntry(entry, default);
        using var reader = new CR2WReader(data);
        reader.ParsingError += SectorReader.TolerateSectorParsingError;
        if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file is null) throw new InvalidDataException();
        using (file)
        {
            var sector = (worldStreamingSector)file.RootChunk;
            var setups = (worldNodeDataBuffer)sector.NodeData.Buffer.Data!;
            int shown = 0;
            for (int i = 0; i < setups.Count && shown < limit; i++)
            {
                if (sector.Nodes[setups[i].NodeIndex].Chunk is not worldInstancedOccluderNode node) continue;
                shown++; var s = setups[i];
                yield return $"placement {i} mesh {node.Mesh.DepotPath.GetResolvedText()} type {node.OccluderType} setup pos ({s.Position.X:F2},{s.Position.Y:F2},{s.Position.Z:F2}) rot ({s.Orientation.I:F3},{s.Orientation.J:F3},{s.Orientation.K:F3},{s.Orientation.R:F3}) scale ({s.Scale.X:F2},{s.Scale.Y:F2},{s.Scale.Z:F2}) instances {node.Buffer.Count}";
                foreach (var b in node.Buffer.Take(3))
                    yield return $"   u1 ({b.Unknown1.X:F3},{b.Unknown1.Y:F3},{b.Unknown1.Z:F3},{b.Unknown1.W:F3}) u2 ({b.Unknown2.X:F3},{b.Unknown2.Y:F3},{b.Unknown2.Z:F3},{b.Unknown2.W:F3}) u3 ({b.Unknown3.X:F3},{b.Unknown3.Y:F3},{b.Unknown3.Z:F3},{b.Unknown3.W:F3}) u4 ({b.Unknown4.X:F3},{b.Unknown4.Y:F3},{b.Unknown4.Z:F3},{b.Unknown4.W:F3})";
            }
        }
    }

    // Diagnostics: collision node header fields against their buffer content.
    public IEnumerable<string> DumpCollisionNodes(ulong sectorHash, int limit)
    {
        var entry = EntryOrNull(sectorHash) ?? throw new FileNotFoundException();
        using var data = ExtractEntry(entry, default);
        using var reader = new CR2WReader(data);
        reader.ParsingError += SectorReader.TolerateSectorParsingError;
        if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file is null) throw new InvalidDataException();
        using (file)
        {
            var sector = (worldStreamingSector)file.RootChunk;
            var setups = (worldNodeDataBuffer)sector.NodeData.Buffer.Data!;
            int shown = 0;
            for (int i = 0; i < setups.Count && shown < limit; i++)
            {
                if (sector.Nodes[setups[i].NodeIndex].Chunk is not worldCollisionNode node || node.CompiledData.Buffer.Data is not WolvenKit.RED4.Archive.Buffer.CollisionBuffer buffer) continue;
                shown++;
                var shapes = buffer.Actors.SelectMany(a => a.Shapes).ToArray();
                string categories = string.Join(" | ", node.StaticCollisionShapeCategories.Arr.Select(a => string.Join(",", a.Select(v => (ushort)v))));
                var setup = setups[i];
                yield return $"{node.DebugName} setup pos ({setup.Position.X:F1},{setup.Position.Y:F1},{setup.Position.Z:F1}) scale ({setup.Scale.X:F2}) streaming {(float)setup.MaxStreamingDistance:F0} lod {(byte)node.Lod} ver {(byte)node.ResourceVersion} sectorHash {(ulong)node.SectorHash:x16} extents ({node.Extents.X:F2},{node.Extents.Y:F2},{node.Extents.Z:F2},{node.Extents.W:F2})";
                yield return $"   actors {buffer.Actors.Count} shapes {shapes.Length} (box {shapes.Count(s => s.ShapeType == Enums.physicsShapeType.Box)}, sphere {shapes.Count(s => s.ShapeType == Enums.physicsShapeType.Sphere)}, capsule {shapes.Count(s => s.ShapeType == Enums.physicsShapeType.Capsule)}, convex {shapes.Count(s => s.ShapeType == Enums.physicsShapeType.ConvexMesh)}, tri {shapes.Count(s => s.ShapeType == Enums.physicsShapeType.TriangleMesh)}) presets [{string.Join(",", shapes.Select(s => s.Preset.ToString()).Distinct())}] num: infos {node.NumShapeInfos} presets {node.NumPresets} materials {node.NumMaterials}";
                yield return $"   categories {categories}";
                var bounds = buffer.Actors.Select(a => new System.Numerics.Vector3(a.Position.X, a.Position.Y, a.Position.Z)).ToArray();
                if (bounds.Length > 0)
                {
                    var min = bounds.Aggregate(System.Numerics.Vector3.Min); var max = bounds.Aggregate(System.Numerics.Vector3.Max);
                    yield return $"   actor positions min ({min.X:F1},{min.Y:F1},{min.Z:F1}) max ({max.X:F1},{max.Y:F1},{max.Z:F1}) half ({(max - min).X / 2:F2},{(max - min).Y / 2:F2},{(max - min).Z / 2:F2})";
                }
            }
        }
    }

    // Diagnostics: the collision actors of a sector near a game-space point, with their shapes.
    public IEnumerable<string> DumpCollisionsNear(ulong sectorHash, System.Numerics.Vector3 point, float radius)
    {
        var entry = EntryOrNull(sectorHash) ?? throw new FileNotFoundException();
        using var data = ExtractEntry(entry, default);
        using var reader = new CR2WReader(data);
        reader.ParsingError += SectorReader.TolerateSectorParsingError;
        if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file is null) throw new InvalidDataException();
        using (file)
        {
            var sector = (worldStreamingSector)file.RootChunk;
            var setups = (worldNodeDataBuffer)sector.NodeData.Buffer.Data!;
            for (int i = 0; i < setups.Count; i++)
            {
                if (sector.Nodes[setups[i].NodeIndex].Chunk is not worldCollisionNode node || node.CompiledData.Buffer.Data is not WolvenKit.RED4.Archive.Buffer.CollisionBuffer buffer) continue;
                for (int a = 0; a < buffer.Actors.Count; a++)
                {
                    var actor = buffer.Actors[a];
                    var position = new System.Numerics.Vector3(actor.Position.X, actor.Position.Y, actor.Position.Z);
                    if (System.Numerics.Vector3.Distance(position, point) > radius) continue;
                    yield return $"placement {i} node {setups[i].NodeIndex} {node.DebugName} actor {a}/{buffer.Actors.Count} pos ({position.X:F3},{position.Y:F3},{position.Z:F3}) rot ({actor.Orientation.I:F3},{actor.Orientation.J:F3},{actor.Orientation.K:F3},{actor.Orientation.R:F3}) scale ({actor.Scale.X:F3},{actor.Scale.Y:F3},{actor.Scale.Z:F3}) uk1 {(short)actor.Uk1} uk2 {(ulong)actor.Uk2:x16} shapes {actor.Shapes.Count}";
                    foreach (var shape in actor.Shapes)
                        yield return $"    {shape.GetType().Name} {shape.ShapeType} pos ({shape.Position.X:F2},{shape.Position.Y:F2},{shape.Position.Z:F2}) preset {shape.Preset} proxy {shape.ProxyType}"
                            + (shape is WolvenKit.RED4.Archive.Buffer.CollisionShapeSimple simple ? $" size ({simple.Size.X:F2},{simple.Size.Y:F2},{simple.Size.Z:F2})"
                               : shape is WolvenKit.RED4.Archive.Buffer.CollisionShapeMesh mesh ? $" hash {(ulong)mesh.Hash:x16} cooked " + (CollisionMesh((ulong)node.SectorHash, (ulong)mesh.Hash) is { } cooked ? $"{cooked.Vertices.Length} vertices {cooked.Indices.Length / 3} triangles convex={cooked.Convex}" : "MISSING") : "")
                            + $" uk {(short)shape.Uk1}/{(ushort)shape.Uk2}/{(uint)shape.Uk3}";
                }
            }
        }
    }

    // Diagnostics: a sector's collision mesh shapes (convex and triangle meshes) that reach into a
    // world box, as world-space triangles. The editor's API names these shapes without their
    // geometry; an audit of what really blocks a player needs it.
    public IEnumerable<object> CollisionMeshShapes(ulong sectorHash, System.Numerics.Vector3 min, System.Numerics.Vector3 max)
    {
        var entry = EntryOrNull(sectorHash) ?? throw new FileNotFoundException();
        using var data = ExtractEntry(entry, default);
        using var reader = new CR2WReader(data);
        reader.ParsingError += SectorReader.TolerateSectorParsingError;
        if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file is null) throw new InvalidDataException();
        using (file)
        {
            var sector = (worldStreamingSector)file.RootChunk;
            var setups = (worldNodeDataBuffer)sector.NodeData.Buffer.Data!;
            for (int i = 0; i < setups.Count; i++)
            {
                if (sector.Nodes[setups[i].NodeIndex].Chunk is not worldCollisionNode node || node.CompiledData.Buffer.Data is not CollisionBuffer buffer) continue;
                for (int a = 0; a < buffer.Actors.Count; a++)
                {
                    var actor = buffer.Actors[a];
                    var position = new System.Numerics.Vector3(actor.Position.X, actor.Position.Y, actor.Position.Z);
                    var orientation = new System.Numerics.Quaternion(actor.Orientation.I, actor.Orientation.J, actor.Orientation.K, actor.Orientation.R);
                    orientation = orientation.LengthSquared() > 1e-8f ? System.Numerics.Quaternion.Normalize(orientation) : System.Numerics.Quaternion.Identity;
                    var scale = new System.Numerics.Vector3(actor.Scale.X, actor.Scale.Y, actor.Scale.Z);
                    if (MathF.Abs(scale.X) < 1e-5f || MathF.Abs(scale.Y) < 1e-5f || MathF.Abs(scale.Z) < 1e-5f) scale = System.Numerics.Vector3.One;
                    for (int s = 0; s < actor.Shapes.Count; s++)
                    {
                        if (actor.Shapes[s] is not CollisionShapeMesh shape || CollisionMesh((ulong)node.SectorHash, (ulong)shape.Hash) is not { } mesh) continue;
                        var shapeRotation = new System.Numerics.Quaternion(shape.Rotation.I, shape.Rotation.J, shape.Rotation.K, shape.Rotation.R);
                        shapeRotation = shapeRotation.LengthSquared() > 1e-8f ? System.Numerics.Quaternion.Normalize(shapeRotation) : System.Numerics.Quaternion.Identity;
                        var offset = new System.Numerics.Vector3(shape.Position.X, shape.Position.Y, shape.Position.Z);
                        var world = new float[mesh.Vertices.Length * 3];
                        System.Numerics.Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
                        for (int v = 0; v < mesh.Vertices.Length; v++)
                        {
                            var p = position + System.Numerics.Vector3.Transform(scale * (offset + System.Numerics.Vector3.Transform(mesh.Vertices[v], shapeRotation)), orientation);
                            world[v * 3] = MathF.Round(p.X, 3); world[v * 3 + 1] = MathF.Round(p.Y, 3); world[v * 3 + 2] = MathF.Round(p.Z, 3);
                            lo = System.Numerics.Vector3.Min(lo, p); hi = System.Numerics.Vector3.Max(hi, p);
                        }
                        if (hi.X < min.X || lo.X > max.X || hi.Y < min.Y || lo.Y > max.Y || hi.Z < min.Z || lo.Z > max.Z) continue;
                        yield return new
                        {
                            id = $"s:{sectorHash:x16}:{(int)setups[i].NodeIndex}:{i}:{a}", shape = s, kind = mesh.Convex ? "convexmesh" : "trianglemesh",
                            preset = shape.Preset.GetResolvedText() ?? "", vertices = world, indices = mesh.Indices
                        };
                    }
                }
            }
        }
    }

    // Diagnostics: the sector-level node references and persistent nodes, matched to the setups.
    public IEnumerable<string> DumpSectorRefs(ulong sectorHash, int limit = 12)
    {
        var entry = EntryOrNull(sectorHash) ?? throw new FileNotFoundException();
        using var data = ExtractEntry(entry, default);
        using var reader = new CR2WReader(data);
        reader.ParsingError += SectorReader.TolerateSectorParsingError;
        if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file is null) throw new InvalidDataException();
        using (file)
        {
            var sector = (worldStreamingSector)file.RootChunk;
            var setups = (worldNodeDataBuffer)sector.NodeData.Buffer.Data!;
            var byRef = new Dictionary<ulong, List<int>>();
            for (int i = 0; i < setups.Count; i++) { ulong r = setups[i].QuestPrefabRefHash; if (r != 0) (byRef.TryGetValue(r, out var l) ? l : byRef[r] = []).Add(i); }
            yield return $"setups {setups.Count}, with global ref {byRef.Values.Sum(l => l.Count)} ({byRef.Count} distinct); NodeRefs {sector.NodeRefs.Count}; PersistentNodes {sector.PersistentNodes.Count}; PersisentNodeIndex {sector.PersisentNodeIndex}; VariantNodes {sector.VariantNodes.Count}; VariantIndices [{string.Join(",", sector.VariantIndices.Select(v => (int)v))}]";
            int matched = 0, shown = 0;
            foreach (var nodeRef in sector.NodeRefs)
            {
                string text = nodeRef.GetResolvedText() ?? "";
                ulong hash = (ulong)nodeRef;
                ulong fnv = WolvenKit.Common.FNV1A.FNV1A64HashAlgorithm.HashString(text);
                bool hit = byRef.ContainsKey(hash) || byRef.ContainsKey(fnv);
                if (hit) matched++;
                if (shown++ < limit) yield return $"  ref \"{text}\" hash {hash:x16} fnv {fnv:x16} -> {(byRef.TryGetValue(hash, out var a) ? "setups " + string.Join(",", a) : byRef.TryGetValue(fnv, out var b) ? "setups(fnv) " + string.Join(",", b) : "no setup")}";
            }
            yield return $"NodeRefs matching a setup global ref: {matched}/{sector.NodeRefs.Count}";
            shown = 0;
            foreach (var persistent in sector.PersistentNodes)
            {
                if (shown++ >= limit) break;
                var lines = new List<string>(); Dump(persistent, "  ", 3, lines, new HashSet<object>(ReferenceEqualityComparer.Instance));
                yield return "  persistent: " + string.Join(" | ", lines.Select(l => l.Trim()));
            }
            int range = sector.PersisentNodeIndex;
            if (range >= 0 && range < sector.VariantIndices.Count)
            {
                int start = sector.VariantIndices[range], end = range + 1 < sector.VariantIndices.Count ? sector.VariantIndices[range + 1] : setups.Count;
                var types = Enumerable.Range(start, Math.Max(0, end - start)).Select(i => sector.Nodes[setups[i].NodeIndex].Chunk?.GetType().Name ?? "?").GroupBy(t => t).Select(g => $"{g.Key}={g.Count()}");
                yield return $"persistent range {range}: setups {start}..{end - 1}: {string.Join(", ", types)}";
            }
        }
    }

    public IEnumerable<string> DumpNodes(ulong sectorHash, string type, int limit = 3)
    {
        var entry = EntryOrNull(sectorHash) ?? throw new FileNotFoundException();
        using var data = ExtractEntry(entry, default);
        using var reader = new CR2WReader(data);
        reader.ParsingError += SectorReader.TolerateSectorParsingError;
        if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file is null) throw new InvalidDataException();
        using (file)
        {
            var sector = (worldStreamingSector)file.RootChunk;
            var setups = (worldNodeDataBuffer)sector.NodeData.Buffer.Data!;
            int shown = 0;
            for (int i = 0; i < setups.Count && shown < limit; i++)
            {
                var node = sector.Nodes[setups[i].NodeIndex].Chunk;
                if (node is null || node.GetType().Name != type) continue;
                if (Environment.GetEnvironmentVariable("NCWE_DUMP_FILTER") is { Length: > 0 } filter && !node.GetType().GetProperties().Any(p => { try { return p.GetValue(node)?.ToString() == filter; } catch { return false; } })) continue;
                shown++;
                var s = setups[i];
                var q = new System.Numerics.Quaternion(s.Orientation.I, s.Orientation.J, s.Orientation.K, s.Orientation.R);
                var fwd = System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitY, q); var down = System.Numerics.Vector3.Transform(-System.Numerics.Vector3.UnitZ, q);
                yield return $"--- {type} placement {i} scale ({s.Scale.X:F2},{s.Scale.Y:F2},{s.Scale.Z:F2}) streaming {s.MaxStreamingDistance:F0} +Y->({fwd.X:F2},{fwd.Y:F2},{fwd.Z:F2}) -Z->({down.X:F2},{down.Y:F2},{down.Z:F2})"
                    + $" pos ({s.Position.X:F2},{s.Position.Y:F2},{s.Position.Z:F2}) id {(ulong)s.Id:x16} quest {(ulong)s.QuestPrefabRefHash:x16} uk {(ulong)s.UkHash1:x16}";
                foreach (var p in node.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    if (p.GetIndexParameters().Length != 0) continue;
                    object? v; try { v = p.GetValue(node); } catch { continue; }
                    string text = v switch
                    {
                        null => "null",
                        IRedRef r => r.DepotPath.GetResolvedText() ?? r.DepotPath.GetRedHash().ToString(),
                        CColor col => $"rgba({col.Red},{col.Green},{col.Blue},{col.Alpha})",
                        HDRColor h => $"hdr({(float)h.Red:F2},{(float)h.Green:F2},{(float)h.Blue:F2},{(float)h.Alpha:F2})",
                        RedBaseClass cls => cls.GetType().Name + " {" + string.Join(", ", cls.GetType().GetProperties().Where(q => q.GetIndexParameters().Length == 0).Take(8).Select(q => { try { return q.Name + "=" + q.GetValue(cls); } catch { return q.Name; } })) + "}",
                        _ => v.ToString() ?? ""
                    };
                    yield return $"  {p.Name} = {text}";
                }
            }
        }
    }

    // Diagnostic: how are destructible instance transforms expressed?
    public IEnumerable<string> InspectDestructibles(ulong sectorHash, int limit = 3)
    {
        var entry = EntryOrNull(sectorHash) ?? throw new FileNotFoundException();
        using var data = ExtractEntry(entry, default);
        using var reader = new CR2WReader(data);
        reader.ParsingError += SectorReader.TolerateSectorParsingError;
        if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file is null) throw new InvalidDataException();
        using (file)
        {
            var sector = (worldStreamingSector)file.RootChunk;
            var setups = (worldNodeDataBuffer)sector.NodeData.Buffer.Data!;
            int shown = 0;
            for (int i = 0; i < setups.Count && shown < limit; i++)
            {
                var s = setups[i];
                if (sector.Nodes[s.NodeIndex].Chunk is not worldInstancedDestructibleMeshNode node) continue;
                shown++;
                yield return $"node {s.NodeIndex} setup pos ({s.Position.X:F1},{s.Position.Y:F1},{s.Position.Z:F1}) scale ({s.Scale.X:F2}) mesh {node.Mesh.DepotPath.GetResolvedText()} static {node.StaticMesh.DepotPath.GetResolvedText()}";
                yield return $"   instanceTransforms {node.InstanceTransforms.Count}: " + string.Join(" ", node.InstanceTransforms.Take(3).Select(t => $"({(float)t.Position.X:F1},{(float)t.Position.Y:F1},{(float)t.Position.Z:F1})"));
                var cooked = node.CookedInstanceTransforms;
                if (cooked.SharedDataBuffer.Chunk?.Buffer.Data is WorldTransformsBuffer buffer)
                    yield return $"   cooked start {cooked.StartIndex} count {cooked.NumElements}: " + string.Join(" ", Enumerable.Range((int)(uint)cooked.StartIndex, Math.Min(3, (int)(uint)cooked.NumElements)).Select(k => buffer.Transforms[k]).Select(t => $"({t.Translation.X:F1},{t.Translation.Y:F1},{t.Translation.Z:F1})"));
                else if (cooked.SharedDataBuffer.Chunk?.Buffer.Data is CookedInstanceTransformsBuffer instances)
                    yield return $"   cooked instances start {cooked.StartIndex} count {cooked.NumElements}: " + string.Join(" ", Enumerable.Range((int)(uint)cooked.StartIndex, Math.Min(4, (int)(uint)cooked.NumElements)).Select(k => instances.Transforms[k])
                        .Select(t => $"(pos {(float)t.Position.X:F2},{(float)t.Position.Y:F2},{(float)t.Position.Z:F2} rot {(float)t.Orientation.I:F2},{(float)t.Orientation.J:F2},{(float)t.Orientation.K:F2},{(float)t.Orientation.R:F2})"));
                else yield return $"   cooked buffer: none (start {cooked.StartIndex} count {cooked.NumElements}, handle {cooked.SharedDataBuffer.Chunk?.GetType().Name ?? "null"}, data {cooked.SharedDataBuffer.Chunk?.Buffer.Data?.GetType().Name ?? "null"})";
            }
        }
    }
}
