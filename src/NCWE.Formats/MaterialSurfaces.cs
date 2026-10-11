// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using System.Collections.Immutable;
using NCWE.Core;
using WolvenKit.Modkit.RED4.Tools;
using WolvenKit.RED4.Archive.CR2W;
using WolvenKit.RED4.Archive.IO;
using WolvenKit.RED4.Types;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace NCWE.Formats;

// Game materials for the realistic view (see NCWE.Core.Materials): a mesh's UV0 and, per
// appearance and part, its material reduced to a SurfaceKind and parameters. Material
// instance chains (.mi / .w2mi) are followed to their template, child values overriding
// parent ones. Multilayered setups and layer templates are shared by thousands of meshes,
// so they are read once. Textures are handed over block-compressed, as the GPU samples them.
public sealed partial class GameArchiveIndex
{
    // Masked: the instance the surface uses, or one it derives from, cuts its alpha out (CMaterialInstance.enableMask).
    private sealed record MaterialChain(string Template, Dictionary<string, IRedType> Values) { public bool Masked { get; init; } }
    private sealed record SurfaceLayerTemplate(ulong Color, ulong Normal, ulong Roughness, ulong Metalness, float Tiling,
        Dictionary<string, Vector3> ColorScales, Dictionary<string, float> NormalStrengths,
        Dictionary<string, Vector2> RoughIn, Dictionary<string, Vector2> RoughOut, Dictionary<string, Vector2> MetalIn, Dictionary<string, Vector2> MetalOut);

    private readonly ConcurrentDictionary<ulong, MaterialChain?> _materialChains = new();
    private readonly ConcurrentDictionary<ulong, MaterialLayer[]?> _surfaceSetups = new();
    private readonly ConcurrentDictionary<ulong, SurfaceLayerTemplate?> _surfaceTemplates = new();

    public Task<MeshSurface?> ReadSurfaceAsync(ulong hash, CancellationToken cancellation = default) => Task.Run(() => ReadSurface(hash, cancellation), cancellation);

    // Surfaces read before come from this cache (set by the host) instead of a WolvenKit parse.
    public SurfaceDiskCache? SurfaceCache { get; set; }
    private string? _archiveSet;
    // Every archive of the index, with its size and time: an update or a mod changes it.
    private string ArchiveSet => _archiveSet ??= Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('|',
        _archives.Select(a => { var f = new FileInfo(a.ArchiveAbsolutePath); return $"{f.FullName.ToUpperInvariant()}:{f.Length}:{f.LastWriteTimeUtc.Ticks}"; }).Order(StringComparer.Ordinal)))));

    private MeshSurface? ReadSurface(ulong hash, CancellationToken cancellation)
    {
        if (!_entries.ContainsKey(hash)) return null;   // custom meshes keep their baked surfaces
        var cache = SurfaceCache;
        var source = Provenance(hash);
        string? key = cache is not null && source.EntrySha1 is { Length: 40 } ? SurfaceDiskCache.Key(source, ArchiveSet) : null;
        if (key is not null && cache!.Read(key, hash) is { } cached) return NoteEmbedded(cached);
        var surface = ReadSurfaceFromArchive(hash, cancellation);
        if (key is not null && surface is not null) cache!.Store(key, surface);
        return NoteEmbedded(surface);
    }

    // Textures a mesh keeps in its own file instead of the archives (the bakes of most building
    // proxies): the mesh that holds each, noted as its surface is handed over, from the archives
    // or from the cache. ReadTextureAsync finds them there.
    private readonly ConcurrentDictionary<ulong, ulong> _embeddedTextures = new();

    private MeshSurface? NoteEmbedded(MeshSurface? surface)
    {
        if (surface is null) return null;
        void Note(ulong texture) { if (texture != 0 && !_entries.ContainsKey(texture)) _embeddedTextures.TryAdd(texture, surface.Mesh); }
        foreach (var materials in surface.Appearances.Values)
            foreach (var m in materials)
            {
                if (m is null) continue;
                Note(m.BaseColor); Note(m.Normal); Note(m.Roughness); Note(m.Metalness); Note(m.Emissive); Note(m.GlobalNormal);
            }
        return surface;
    }

    private MeshSurface? ReadSurfaceFromArchive(ulong hash, CancellationToken cancellation)
    {
        if (!_entries.TryGetValue(hash, out var entry)) return null;
        using var data = ExtractEntry(entry, cancellation);
        using var reader = new CR2WReader(data);
        reader.ParsingError += SectorReader.TolerateSectorParsingError;
        if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file is null) return null;
        using (file)
        {
            if (file.RootChunk is not CMesh { RenderResourceBlob.Chunk: rendRenderMeshBlob blob } mesh) return null;
            var info = MeshTools.GetMeshesinfo(blob, mesh);
            using var buffer = new MemoryStream(blob.RenderBuffer.Buffer.GetBytes());
            var raw = MeshTools.ContainRawMesh(buffer, info, false);
            // The parts LoadMesh keeps, in the same order.
            var kept = new List<int>(); var uvs = new List<Vector2[]?>(); var counts = new List<int>();
            for (int r = 0; r < raw.Count; r++)
            {
                cancellation.ThrowIfCancellationRequested();
                var part = raw[r];
                if (part.positions is not { Length: > 0 } || part.indices is not { Length: > 0 }) continue;
                kept.Add(r); counts.Add(part.positions.Length);
                // Some helper parts store infinite UVs (junction proxies' lambert1): zero, not NaN downstream.
                uvs.Add(part.texCoords0?.Length == part.positions.Length
                    ? part.texCoords0.Select(t => float.IsFinite(t.X) && float.IsFinite(t.Y) ? new Vector2(t.X, 1 - t.Y) : Vector2.Zero).ToArray() : null);
            }
            var slots = new Dictionary<string, SurfaceMaterial?>(StringComparer.Ordinal);
            // Road and intersection proxies: the cut-outs of their bakes are filled with asphalt (see below), masked or not.
            bool roadBake = entry.FileName?.Contains(@"\_external\road_meshes\", StringComparison.OrdinalIgnoreCase) == true;
            SurfaceMaterial? Slot(string name)
            {
                if (slots.TryGetValue(name, out var known)) return known;
                SurfaceMaterial? material = null;
                try { material = ResolveSlot(mesh, name, hash, out _, roadBake); } catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or InvalidCastException) { }
                return slots[name] = material;
            }
            var appearances = new Dictionary<string, SurfaceMaterial?[]>(StringComparer.Ordinal);
            foreach (var appearance in mesh.Appearances.Select(a => a.Chunk).OfType<meshMeshAppearance>())
            {
                if (appearance.ChunkMaterials.Count < raw.Count) continue;
                var materials = new SurfaceMaterial?[kept.Count];
                for (int k = 0; k < kept.Count; k++) materials[k] = Slot(appearance.ChunkMaterials[kept[k]].ToString() ?? "");
                appearances.TryAdd(appearance.Name.ToString() ?? "default", materials);
            }
            // Street-tool road templates wear a placeholder (the engine dresses the road meshes it
            // generates from them); the textures baked for their distant proxies carry that look.
            if (RoadTemplateMaterial(entry.FileName) is { } road)
            {
                foreach (var name in appearances.Keys.ToArray()) appearances[name] = [.. appearances[name].Select(_ => road)];
                if (appearances.Count == 0) appearances["default"] = [.. kept.Select(_ => road)];
            }
            // Road and intersection proxies are the street where no other mesh is (pieces missing
            // from the archives, junctions): they wear the same tiny bakes and get the same asphalt.
            // Their scale of (1, 1, 1, 0) is not an opacity (the game draws them). An untextured
            // part (lambert1: the middle of a junction, without UVs) is plain asphalt.
            else if (entry.FileName?.Contains(@"\_external\road_meshes\", StringComparison.OrdinalIgnoreCase) == true)
                foreach (var name in appearances.Keys.ToArray())
                    appearances[name] = [.. appearances[name].Select(m => m is not { Kind: SurfaceKind.Standard } ? m
                        : m.BaseColor == 0 ? RoadDetail(new SurfaceMaterial(m.Key + ":road", SurfaceKind.Standard) { BaseColorScale = new(PlainAsphalt, 1) }, PlainAsphalt)
                        : RoadDetail(m with { Key = m.Key + ":road", BaseColorScale = m.BaseColorScale with { W = 1 } }))];
            // Terrain cells carry no UVs: their material derives them from the position (positions
            // are the editor's Y-up space: game x = x, game y = -z).
            for (int k = 0; k < kept.Count; k++)
            {
                if (uvs[k] is not null || appearances.Values.Select(m => m[k]?.GeneratedUv).FirstOrDefault(g => g is { } v && v != default) is not { } gen) continue;
                uvs[k] = [.. raw[kept[k]].positions.Select(p => new Vector2(p.X * gen.X + gen.Z, 1 - (-p.Z * gen.Y + gen.W)))];
            }
            return new(hash, uvs.ToArray(), counts.ToArray(), appearances);
        }
    }

    // Proxy textures baked from road templates (<template>_d / _r.xbm), by name key.
    private Dictionary<string, ulong>? _bakedProxyTextures;

    private SurfaceMaterial? RoadTemplateMaterial(string? meshPath)
    {
        if (meshPath is null || !meshPath.Contains(@"\street\tool\", StringComparison.OrdinalIgnoreCase)) return null;
        var baked = LazyInitializer.EnsureInitialized(ref _bakedProxyTextures, () => _entries.Values
            .Where(e => e.FileName is { } n && n.Contains(@"\_external\proxy\", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".xbm", StringComparison.OrdinalIgnoreCase))
            .GroupBy(e => NameKey(Path.GetFileNameWithoutExtension(e.FileName!)), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Key, StringComparer.Ordinal));
        string stem = Path.GetFileNameWithoutExtension(meshPath);
        if (stem.EndsWith("_mproxy", StringComparison.OrdinalIgnoreCase)) stem = stem[..^7];
        if (!baked.TryGetValue(NameKey(stem + "_d"), out var color)) return null;
        baked.TryGetValue(NameKey(stem + "_r"), out var roughness);
        // The bakes are tiny (64 px for a 12 x 44 m piece): the street asphalt the road tool lays
        // (ml_street_a_asphalt, base layer: asphalt_generic, 3 m) adds the grain and relief up
        // close; roughness stays matte (painted bands in the bake read as mirrors once magnified).
        return RoadDetail(new($"road:{color:x16}", SurfaceKind.Standard) { BaseColor = color, Roughness = roughness });
    }

    // Up close the bakes are mush (64 px for a 12 x 44 m piece): the asphalt the road tool lays on
    // real streets (ml_street_a_asphalt_no_line, base layer: texture, color scale, tiling and
    // roughness levels) is drawn where the bake shows asphalt, so bent pieces match the real
    // street meshes beside them; markings, curbs and sidewalks keep the bake's departures from it.
    private static readonly Vector3 PlainAsphalt = new(.06f, .06f, .062f);

    private SurfaceMaterial RoadDetail(SurfaceMaterial bake, Vector3? known = null)
    {
        if (SurfaceSetup(Hash(@"base\environment\street\common\textures\default\ml_street_a_asphalt_no_line.mlsetup")) is not { Length: > 0 } layers
            || (known ?? BakeAsphalt(bake.BaseColor)) is not { } asphalt) return bake;
        // Matches the grain of the real street meshes beside them (asphalt every 3 m).
        return bake with { Layers = [layers[0]], DetailSize = 18, DetailBase = asphalt, RoughnessScale = .35f, RoughnessBias = .6f };
    }

    // The asphalt color of a road bake (linear): the darker third of its pixels, where markings,
    // curbs and sidewalks are lighter.
    private readonly ConcurrentDictionary<ulong, Vector3?> _bakeAsphalt = new();
    private Vector3? BakeAsphalt(ulong texture) => texture == 0 ? null : _bakeAsphalt.GetOrAdd(texture, t =>
    {
        try
        {
            var image = WithResource(t, file => file.RootChunk is CBitmapTexture bitmap ? CustomMeshes.XbmReader.Decode(bitmap, 64) : null);
            if (image is null || image.Width * image.Height == 0) return null;
            static float L(byte b) => MathF.Pow(b / 255f, 2.2f);
            var pixels = Enumerable.Range(0, image.Width * image.Height)
                .Select(i => new Vector3(L(image.Pixels[i * 4]), L(image.Pixels[i * 4 + 1]), L(image.Pixels[i * 4 + 2])))
                .OrderBy(c => c.X * .3f + c.Y * .59f + c.Z * .11f).ToArray();
            var band = pixels.Skip(pixels.Length / 5).Take(Math.Max(1, pixels.Length / 5)).ToArray();
            return band.Aggregate(Vector3.Zero, (a, c) => a + c) / band.Length;
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or InvalidCastException or ArgumentException) { return null; }
    });

    private static ulong Hash(string path) => WolvenKit.Common.FNV1A.FNV1A64HashAlgorithm.HashString(path);

    // The viewer shows what the renderer draws: a road template's dressing, not its placeholder.
    private SurfaceMaterial? ViewedSlot(CMesh mesh, string name, ulong hash, string? meshPath, out string template)
    {
        if (RoadTemplateMaterial(meshPath) is { } road) { template = "road proxy bake + street asphalt (placeholder in the template)"; return road; }
        return ResolveSlot(mesh, name, hash, out template);
    }

    // The materials one appearance puts on a mesh (slot, material, template, how many parts
    // wear it), in part order; every slot when the appearance is unknown.
    public Task<IReadOnlyList<MeshMaterialInfo>> ReadMeshMaterialsAsync(ulong hash, string? appearanceName, CancellationToken cancellation = default) => Task.Run(() =>
    {
        IReadOnlyList<MeshMaterialInfo> none = [];
        if (!_entries.TryGetValue(hash, out var entry)) return none;
        using var data = ExtractEntry(entry, cancellation);
        using var reader = new CR2WReader(data);
        reader.ParsingError += SectorReader.TolerateSectorParsingError;
        if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file is null) return none;
        using (file)
        {
            if (file.RootChunk is not CMesh mesh) return none;
            var appearances = mesh.Appearances.Select(a => a.Chunk).OfType<meshMeshAppearance>().ToArray();
            var appearance = appearances.FirstOrDefault(a => a.Name.ToString() == (appearanceName ?? "default")) ?? appearances.FirstOrDefault(a => a.Name.ToString() == "default") ?? appearances.FirstOrDefault();
            var names = appearance is not null ? appearance.ChunkMaterials.Select(c => c.ToString() ?? "").ToList() : mesh.MaterialEntries.Select(e => e.Name.ToString() ?? "").ToList();
            var result = new List<MeshMaterialInfo>();
            foreach (var group in names.GroupBy(n => n, StringComparer.Ordinal))
            {
                SurfaceMaterial? material = null; string template = "";
                try { material = ViewedSlot(mesh, group.Key, hash, entry.FileName, out template); } catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or InvalidCastException) { }
                result.Add(new(group.Key, material, template, group.Count()));
            }
            return (IReadOnlyList<MeshMaterialInfo>)result;
        }
    }, cancellation);

    // Diagnostics (probe): a multilayer setup's layers with their textures.
    public IEnumerable<string> DumpSetup(string path)
    {
        var layers = SurfaceSetup(WolvenKit.Common.FNV1A.FNV1A64HashAlgorithm.HashString(path));
        if (layers is null) { yield return "not a setup"; yield break; }
        for (int i = 0; i < layers.Length; i++)
        {
            var l = layers[i];
            yield return $"layer {i}: tile {l.Tile:0.##} opacity {l.Opacity:0.##} color {TexturePath(l.Color)} normal {TexturePath(l.Normal)} rough {TexturePath(l.Roughness)} scale {l.ColorScale} normal strength {l.NormalStrength:0.##} rough levels {l.RoughLevels} metal levels {l.MetalLevels}";
        }
    }

    // Diagnostics (probe): a texture decoded the right way up, by depot path.
    public CustomMeshes.RgbaImage? DecodeTexture(string path, int maximum = 2048)
        => WithResource(WolvenKit.Common.FNV1A.FNV1A64HashAlgorithm.HashString(path), file => file.RootChunk is CBitmapTexture t ? CustomMeshes.XbmReader.Decode(t, maximum) : null);

    // Diagnostics (probe): each part's vertex count, position box and UV channel ranges.
    public IEnumerable<string> DumpMeshUvs(ulong hash)
    {
        if (!_entries.TryGetValue(hash, out var entry)) { yield return "not found"; yield break; }
        using var data = ExtractEntry(entry, default);
        using var reader = new CR2WReader(data);
        if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file is null || file.RootChunk is not CMesh { RenderResourceBlob.Chunk: rendRenderMeshBlob blob } mesh) { yield return "unreadable"; yield break; }
        var info = MeshTools.GetMeshesinfo(blob, mesh);
        using var buffer = new MemoryStream(blob.RenderBuffer.Buffer.GetBytes());
        var raw = MeshTools.ContainRawMesh(buffer, info, false);
        static string Range(IEnumerable<(float X, float Y)>? values)
        {
            var list = values?.ToList();
            if (list is not { Count: > 0 }) return "none";
            return $"u {list.Min(v => v.X):0.###}..{list.Max(v => v.X):0.###} v {list.Min(v => v.Y):0.###}..{list.Max(v => v.Y):0.###}";
        }
        for (int r = 0; r < raw.Count; r++)
        {
            var p = raw[r];
            if (p.positions is not { Length: > 0 }) continue;
            yield return $"part {r} '{p.name}' lod {p.lod}: {p.positions.Length} vertices, x {p.positions.Min(q => q.X):0.##}..{p.positions.Max(q => q.X):0.##} y {p.positions.Min(q => q.Y):0.##}..{p.positions.Max(q => q.Y):0.##} z {p.positions.Min(q => q.Z):0.##}..{p.positions.Max(q => q.Z):0.##}";
            if (p.normals is { Length: > 0 } normals)
            {
                yield return $"   normal y {normals.Min(n => n.Y):0.##}..{normals.Max(n => n.Y):0.##} (mean {normals.Average(n => n.Y):0.##})";
                // Raw axes are glTF's (Y up, Z = -game Y): the facing of a wall module, in game axes.
                int Count(Func<System.Numerics.Vector3, bool> test) => normals.Count(n => test(new System.Numerics.Vector3(n.X, n.Y, n.Z)));
                yield return $"   facing (game axes): +X {Count(n => n.X > 0.5f)}, -X {Count(n => n.X < -0.5f)}, +Y {Count(n => n.Z < -0.5f)}, -Y {Count(n => n.Z > 0.5f)}, up {Count(n => n.Y > 0.5f)}, down {Count(n => n.Y < -0.5f)} of {normals.Length}";
            }
            yield return "   uv0 " + Range(p.texCoords0?.Select(t => (t.X, t.Y)));
            yield return "   uv1 " + Range(p.texCoords1?.Select(t => (t.X, t.Y)));
        }
    }

    // Diagnostics (probe): a material instance chain, flattened (child values over parent ones).
    public IEnumerable<string> DumpMaterialChain(ulong hash)
    {
        if (ExternalChain(hash, 0) is not { } chain) { yield return "not a material"; yield break; }
        yield return "template " + chain.Template;
        foreach (var (key, value) in chain.Values.OrderBy(p => p.Key, StringComparer.Ordinal))
            yield return $"  {key} = " + value switch
            {
                CFloat f => ((float)f).ToString(System.Globalization.CultureInfo.InvariantCulture),
                CColor c => $"color {c.Red},{c.Green},{c.Blue},{c.Alpha}",
                WolvenKit.RED4.Types.Vector4 q => $"vec4 {(float)q.X},{(float)q.Y},{(float)q.Z},{(float)q.W}",
                IRedRef r => "ref " + (r.DepotPath.GetResolvedText() ?? r.DepotPath.GetRedHash().ToString("x16")),
                _ => value.GetType().Name + " " + value
            };
    }

    // A texture's depot path, when the archives name it.
    public string? TexturePath(ulong hash) => hash != 0 && _entries.TryGetValue(hash, out var e) ? e.FileName : null;

    // The material slots of a mesh, by name, for the material viewer.
    public Task<IReadOnlyList<(string Slot, SurfaceMaterial? Material, string Template)>> ReadMaterialSlotsAsync(ulong hash, CancellationToken cancellation = default) => Task.Run(() =>
    {
        IReadOnlyList<(string, SurfaceMaterial?, string)> none = [];
        if (!_entries.TryGetValue(hash, out var entry)) return none;
        using var data = ExtractEntry(entry, cancellation);
        using var reader = new CR2WReader(data);
        reader.ParsingError += SectorReader.TolerateSectorParsingError;
        if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file is null) return none;
        using (file)
        {
            if (file.RootChunk is not CMesh mesh) return none;
            var result = new List<(string, SurfaceMaterial?, string)>();
            foreach (var e in mesh.MaterialEntries)
            {
                string name = e.Name.ToString() ?? "";
                SurfaceMaterial? material = null; string template = "";
                try { material = ViewedSlot(mesh, name, hash, entry.FileName, out template); } catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or InvalidCastException) { }
                result.Add((name, material, template));
            }
            return (IReadOnlyList<(string, SurfaceMaterial?, string)>)result;
        }
    }, cancellation);

    private SurfaceMaterial? ResolveSlot(CMesh mesh, string name, ulong meshHash) => ResolveSlot(mesh, name, meshHash, out _);

    private SurfaceMaterial? ResolveSlot(CMesh mesh, string name, ulong meshHash, out string template, bool cutUnmasked = false)
    {
        template = "";
        var entry = mesh.MaterialEntries.FirstOrDefault(e => e.Name.ToString() == name);
        if (entry is null) return null;
        int i = entry.Index;
        MaterialChain? chain; string key;
        if (entry.IsLocalInstance)
        {
            var instance = (i < mesh.LocalMaterialBuffer.Materials.Count ? mesh.LocalMaterialBuffer.Materials[i]
                : i < mesh.LocalMaterialInstances.Count ? mesh.LocalMaterialInstances[i].Chunk
                : i < mesh.PreloadLocalMaterialInstances.Count ? mesh.PreloadLocalMaterialInstances[i].Chunk : null) as CMaterialInstance;
            chain = Chain(instance, 0); key = $"{meshHash:x16}:{name}";
        }
        else
        {
            ulong external = i < mesh.ExternalMaterials.Count ? mesh.ExternalMaterials[i].DepotPath.GetRedHash() : 0;
            chain = ExternalChain(external, 0); key = $"mi:{external:x16}";
        }
        if (chain is null) return null;
        template = chain.Template;
        return Classify(chain, key, cutUnmasked);
    }

    private MaterialChain? Chain(CMaterialInstance? instance, int depth)
    {
        if (instance is null || depth > 8) return null;
        var path = instance.BaseMaterial.DepotPath;
        string text = path.GetResolvedText() ?? "";
        var parent = text.EndsWith(".mi", StringComparison.OrdinalIgnoreCase) || text.EndsWith(".w2mi", StringComparison.OrdinalIgnoreCase)
            ? ExternalChain(path.GetRedHash(), depth + 1) ?? new("", new())
            : new MaterialChain(text.ToLowerInvariant(), new());
        var values = new Dictionary<string, IRedType>(parent.Values, StringComparer.Ordinal);
        foreach (var pair in instance.Values) if (pair.Value is not null) values[pair.Key.ToString() ?? ""] = pair.Value;
        // Masked by itself or by the instance it derives from.
        return new(parent.Template, values) { Masked = instance.EnableMask || parent.Masked };
    }

    private MaterialChain? ExternalChain(ulong hash, int depth)
    {
        if (hash == 0 || depth > 8) return null;
        if (_materialChains.TryGetValue(hash, out var cached)) return cached;
        MaterialChain? chain = null;
        try
        {
            chain = WithResource(hash, file => file.RootChunk switch
            {
                CMaterialInstance instance => Chain(instance, depth),
                CMaterialTemplate => new MaterialChain((_entries.TryGetValue(hash, out var e) ? e.FileName ?? "" : "").ToLowerInvariant(), new()),
                _ => null
            });
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or InvalidCastException) { }
        return _materialChains[hash] = chain;
    }

    // --- Template families ------------------------------------------------------------------

    private static readonly string[] HiddenTemplates = ["invisible", "blackwall", "silverhand", "cloak", "hud_", "gi_visible", "particles_", "fillable_fluid", "vehicle_destr", "proxy_ad"];
    private static readonly string[] EmissiveTemplates = ["neon_tubes", "signages", "parallaxscreen", "television_ad", "hologram", "neon_parallax", "diode_pavements", "animated_ceiling"];

    // cutUnmasked: an instance that is not masked still cuts at the threshold it carries (road bakes).
    private SurfaceMaterial? Classify(MaterialChain chain, string key, bool cutUnmasked = false)
    {
        string t = chain.Template; var v = chain.Values;
        if (t.Length == 0) return null;
        if (t.Contains("multilayered"))
        {
            // Terrain cells: their UVs come from the position.
            var layered = Multilayer(v);
            // Their tint (BaseColor, a colour map over the cell) multiplies the layers.
            return layered is not null && t.Contains("terrain") && V4(v, "UVGenScaleOffset") is { } generated ? layered with { GeneratedUv = generated, BaseColor = Tex(v, "BaseColor") } : layered;
        }
        if (HiddenTemplates.Any(t.Contains)) return new(key, SurfaceKind.Hidden);
        if (t.Contains("water"))
        {
            // Bright scattering (pools) lets the bottom show; the dark ocean is nearly opaque.
            var tint = Color(v, "ScatteringColor") ?? new(.02f, .09f, .1f);
            float luminance = tint.X * .3f + tint.Y * .59f + tint.Z * .11f;
            return new(key, SurfaceKind.Water) { Tint = tint, Opacity = luminance > .1f ? .4f : .85f, Normal = Tex(v, "Normal", "NormalTexture") };
        }
        // Screens showing UI (ads, terminals) are drawn by the game's UI system: a dark, glossy panel here.
        if (t.Contains("metal_base_ui"))
            return new(key, SurfaceKind.Standard)
            {
                BaseColorScale = new(.03f, .035f, .045f, 1), RoughnessScale = 0, RoughnessBias = .12f,
                EmissiveColor = new(.25f, .45f, .8f), EmissiveIntensity = .012f
            };
        if (t.Contains("window_parallax") || t.Contains("window_interior"))
            return new(key, SurfaceKind.Window)
            {
                BaseColor = Tex(v, "WindowTexture"), Emissive = Tex(v, "RoomAtlas"), Normal = Tex(v, "Normal"), Roughness = Tex(v, "Roughness"),
                Tint = Color(v, "TintColorAtNight") ?? new(1f, .72f, .45f), EmissiveIntensity = Intensity(F(v, "EmissiveEV", 4)), NormalStrength = F(v, "NormalStrength", 1)
            };
        if (t.Contains("glass"))
            return new(key, SurfaceKind.Glass)
            {
                Tint = Color(v, "TintColor") ?? Vector3.One, Opacity = Math.Clamp(F(v, "Opacity", .12f), .02f, 1), Normal = Tex(v, "Normal"), Roughness = Tex(v, "Roughness"),
                BaseColor = Tex(v, "MaskTexture"), NormalStrength = F(v, "NormalStrength", 1),
                UvTransform = new(F(v, "UvTilingX", 1), F(v, "UvTilingY", 1), F(v, "UvOffsetX", 0), F(v, "UvOffsetY", 0))
            };
        if (t.Contains("mesh_decal"))
        {
            var color = Color(v, "DiffuseColor") ?? Vector3.One;
            bool emissive = t.Contains("emissive");
            return new(key, SurfaceKind.Decal)
            {
                BaseColor = Tex(v, "DiffuseTexture"), Normal = Tex(v, "NormalTexture"), Roughness = Tex(v, "RoughnessTexture"), Metalness = Tex(v, "MetalnessTexture"),
                BaseColorScale = new(color, 1), Opacity = Math.Clamp(F(v, "DiffuseAlpha", 1), 0, 1), NormalStrength = F(v, "NormalAlpha", 1),
                UvTransform = new(F(v, "UVScaleX", 1), F(v, "UVScaleY", 1), F(v, "UVOffsetX", 0), F(v, "UVOffsetY", 0)),
                EmissiveColor = emissive ? color : default, EmissiveIntensity = emissive ? Intensity(F(v, "EmissiveEV", 4)) : 0
            };
        }
        if (t.Contains("light_gradients"))
        {
            // Every parameter of the template; an instance sets some, the template's own value stands for the others.
            var defaults = TemplateDefaults(t);
            float P(string name, float fallback) => F(v, name, F(defaults, name, fallback));
            var bottom = Color(v, "BottomColor") ?? Color(defaults, "BottomColor") ?? new(.072f, .539f, 1);
            var top = Color(v, "TopColor") ?? Color(defaults, "TopColor") ?? new(0, 1, .374f);
            var mean = (bottom + top) * .5f;
            return new(key, SurfaceKind.LightGradient)
            {
                // What stands for it where it is shown as a surface (the material's sphere): a light of its mean color.
                EmissiveColor = mean, BaseColorScale = new(mean, 1), EmissiveIntensity = .6f,
                Gradient = new(bottom, top,
                    P("AdditiveAlphaBlend", 0), P("LerpGradient", 0), P("Multiplier", 1), P("MinProximityAlpha", .035f), P("MaxProximityAlpha", .05f), P("GroundPosition", 45), P("TopPosition", 200),
                    P("GradientDirection", .704641f), P("RoundGradientPosition", .150591f), P("RoundGradientScale", 1), P("DistanceToSurfaceModifier", 100))
            };
        }
        if (t.Contains("speedtree"))
            return new(key, SurfaceKind.Foliage) { BaseColor = Tex(v, "DiffuseMap"), Normal = Tex(v, "NormalMap"), AlphaThreshold = t.Contains("onesided") || t.Contains("seams") ? 0 : .45f };
        if (EmissiveTemplates.Any(t.Contains))
        {
            var given = Color(v, "color", "Color", "ColorOneStart", "EmissiveColor", "EmissiveColor1");
            float ev = F(v, "EmissiveEV", F(v, "Emissive", 4));
            ulong picture = Tex(v, "MainTexture", "ParalaxTexture", "AdTexture", "SignTexture", "tex1", "DiffuseTexture");
            // A screen whose content comes at runtime (no picture, no color): a muted glow, not white.
            bool blank = picture == 0 && given is null;
            var color = blank ? new Vector3(.15f, .35f, .6f) : given ?? Vector3.One;
            // Screen shaders sample their picture top-down (the opposite of mesh textures); ads
            // are flipbooks (TilesWidth x TilesHeight frames played at PlaySpeed).
            bool screen = t.Contains("parallaxscreen") || t.Contains("television_ad");
            float columns = Math.Max(1, F(v, "TilesWidth", 1)), rows = Math.Max(1, F(v, "TilesHeight", 1));
            return new(key, SurfaceKind.Emissive)
            {
                BaseColor = picture, EmissiveColor = color, EmissiveIntensity = blank ? .15f : Intensity(ev), BaseColorScale = new(color, 1),
                Screen = new(columns * rows > 1 ? columns : 0, columns * rows > 1 ? rows : 0, Math.Max(0, F(v, "PlaySpeed", 0)), screen ? 1 : 0)
            };
        }
        // metal_base family and the rest of the standard surfaces.
        ulong baseColor = Tex(v, "BaseColor", "DiffuseTexture", "Diffuse", "DiffuseMap", "MainTexture");
        // No base color at all: the template's default reads as a mid grey, not white.
        var scale = V4(v, "BaseColorScale") ?? (Color(v, "BaseColorScale") is { } c ? new Vector4(c, 1) : baseColor == 0 ? new Vector4(.45f, .45f, .45f, 1) : Vector4.One);

        var emissiveColor = Color(v, "EmissiveColor", "EmissiveColor1");
        float emissiveEv = F(v, "EmissiveEV", F(v, "Zone0EmissiveEV", 0));
        ulong emissiveMap = Tex(v, "Emissive", "EmissiveTexture");
        float tile = F(v, "LayerTile", 1);
        return new(key, SurfaceKind.Standard)
        {
            BaseColor = baseColor, Normal = Tex(v, "Normal", "NormalTexture", "NormalMap"),
            Roughness = Tex(v, "Roughness", "RoughnessTexture"), Metalness = Tex(v, "Metalness", "MetalnessTexture"), Emissive = emissiveMap,
            // The game's alpha test compares the base color texture's alpha alone with the threshold: the
            // scale's alpha takes no part in it (building proxies have a scale of (1, 1, 1, 0)).
            BaseColorScale = scale with { W = 1 }, UvTransform = new(tile, tile, 0, 0),
            RoughnessScale = F(v, "RoughnessScale", 1), RoughnessBias = F(v, "RoughnessBias", 0), MetalnessScale = F(v, "MetalnessScale", 1), MetalnessBias = F(v, "MetalnessBias", 0),
            // The game tests alpha in its masked pass only (enableMask on the instance; the flag gates the
            // test in the shader): at the instance's threshold, or its template's (0.38 for metal_base).
            // An unmasked instance is drawn whole whatever threshold it carries: building proxies carry
            // 0.38, and cut by it their walls vanished, leaving their windows in the air.
            NormalStrength = F(v, "NormalStrength", 1),
            AlphaThreshold = chain.Masked ? F(v, "AlphaThreshold", F(TemplateDefaults(t), "AlphaThreshold", .38f)) : cutUnmasked ? F(v, "AlphaThreshold", 0) : 0,
            EmissiveColor = emissiveColor ?? Vector3.One,
            // Proxies stand for whole buildings far away: their emissive masks light entire silhouettes.
            EmissiveIntensity = emissiveEv > 0 && !t.Contains("proxy") && (emissiveMap != 0 || emissiveColor is not null) ? Intensity(emissiveEv) : 0
        };
    }

    // EmissiveEV (exposure value) to a display intensity: faint at 0, a bright neon around 10
    // (the night preview raises it further).
    internal static float Intensity(float ev) => Math.Clamp(.25f + ev * .15f, .1f, 4f);

    private SurfaceMaterial? Multilayer(Dictionary<string, IRedType> v)
    {
        ulong setup = Tex(v, "MultilayerSetup"), mask = Tex(v, "MultilayerMask");
        if (setup == 0 || mask == 0 || SurfaceSetup(setup) is not { Length: > 0 } layers) return null;
        ulong normal = Tex(v, "GlobalNormal");
        var scale = V4(v, "GlobalNormalUVScale"); var bias = V4(v, "GlobalNormalUVBias");
        return new($"ml:{setup:x16}:{mask:x16}:{normal:x16}", SurfaceKind.Multilayer)
        {
            Mask = mask, Layers = [.. layers], GlobalNormal = normal, NormalStrength = F(v, "GlobalNormalIntensity", 1),
            GlobalNormalTransform = new(scale?.X is > 0 ? scale.Value.X : 1, scale?.Y is > 0 ? scale.Value.Y : 1, bias?.X ?? 0, bias?.Y ?? 0)
        };
    }

    private MaterialLayer[]? SurfaceSetup(ulong hash) => _surfaceSetups.GetOrAdd(hash, h =>
    {
        try
        {
            return WithResource(h, file =>
            {
                if (file.RootChunk is not Multilayer_Setup setup) return null;
                var layers = new List<MaterialLayer>(setup.Layers.Count);
                foreach (var layer in setup.Layers)
                {
                    var t = SurfaceTemplate(layer.Material.DepotPath.GetRedHash());
                    string Name(CName n) => n.ToString() ?? "null";
                    Vector2 Levels(Dictionary<string, Vector2>? table, CName name) => table is not null && (table.TryGetValue(Name(name), out var l) || table.TryGetValue("null", out l)) ? l : new(1, 0);
                    var color = t?.ColorScales.GetValueOrDefault(Name(layer.ColorScale), Vector3.One) ?? Vector3.One;
                    float strength = t is not null && (t.NormalStrengths.TryGetValue(Name(layer.NormalStrength), out var s) || t.NormalStrengths.TryGetValue("null", out s)) ? s : 1;
                    var roughIn = Levels(t?.RoughIn, layer.RoughLevelsIn); var roughOut = Levels(t?.RoughOut, layer.RoughLevelsOut);
                    var metalIn = Levels(t?.MetalIn, layer.MetalLevelsIn); var metalOut = Levels(t?.MetalOut, layer.MetalLevelsOut);
                    layers.Add(new(t?.Color ?? 0, t?.Normal ?? 0, t?.Roughness ?? 0, t?.Metalness ?? 0, color, (float)layer.MatTile * (t?.Tiling ?? 1),
                        new((float)layer.OffsetU, (float)layer.OffsetV), (float)layer.Opacity, strength,
                        new(roughIn.X, roughIn.Y, roughOut.X, roughOut.Y), new(metalIn.X, metalIn.Y, metalOut.X, metalOut.Y)));
                }
                return layers.ToArray();
            });
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or InvalidCastException) { return null; }
    });

    private SurfaceLayerTemplate? SurfaceTemplate(ulong hash) => hash == 0 ? null : _surfaceTemplates.GetOrAdd(hash, h =>
    {
        try
        {
            return WithResource(h, file =>
            {
                if (file.RootChunk is not Multilayer_LayerTemplate t) return null;
                var o = t.Overrides;
                var colors = new Dictionary<string, Vector3>(StringComparer.Ordinal);
                foreach (var e in o.ColorScale) if (e.V.Count >= 3) colors[e.N.ToString() ?? ""] = new(e.V[0], e.V[1], e.V[2]);
                var strengths = new Dictionary<string, float>(StringComparer.Ordinal);
                foreach (var e in o.NormalStrength) strengths[e.N.ToString() ?? ""] = e.V;
                static Dictionary<string, Vector2> Levels(CArray<Multilayer_LayerTemplateOverridesLevels> list)
                {
                    var d = new Dictionary<string, Vector2>(StringComparer.Ordinal);
                    foreach (var e in list) if (e.V.Count >= 2) d[e.N.ToString() ?? ""] = new(e.V[0], e.V[1]);
                    return d;
                }
                float tiling = (float)t.TilingMultiplier;
                return new SurfaceLayerTemplate(t.ColorTexture.DepotPath.GetRedHash(), t.NormalTexture.DepotPath.GetRedHash(), t.RoughnessTexture.DepotPath.GetRedHash(),
                    t.MetalnessTexture.DepotPath.GetRedHash(), tiling > 0 ? tiling : 1, colors, strengths, Levels(o.RoughLevelsIn), Levels(o.RoughLevelsOut), Levels(o.MetalLevelsIn), Levels(o.MetalLevelsOut));
            });
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or InvalidCastException) { return null; }
    });

    // --- Parameter values ---------------------------------------------------------------------

    private static ulong Tex(Dictionary<string, IRedType> v, params string[] keys)
    {
        foreach (var key in keys) if (v.TryGetValue(key, out var value) && value is IRedRef r && r.DepotPath.GetRedHash() is var h && h != 0) return h;
        return 0;
    }
    private static float F(Dictionary<string, IRedType> v, string key, float fallback) => v.TryGetValue(key, out var value) && value is CFloat f && float.IsFinite(f) ? f : fallback;
    private static Vector4? V4(Dictionary<string, IRedType> v, string key) => v.TryGetValue(key, out var value) && value is WolvenKit.RED4.Types.Vector4 q ? new((float)q.X, (float)q.Y, (float)q.Z, (float)q.W) : null;
    // Material colors are 8-bit sRGB: returned linear.
    private static Vector3? Color(Dictionary<string, IRedType> v, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!v.TryGetValue(key, out var value)) continue;
            if (value is CColor c) return new(Linear(c.Red), Linear(c.Green), Linear(c.Blue));
            if (value is WolvenKit.RED4.Types.Vector4 q) return new((float)q.X, (float)q.Y, (float)q.Z);
        }
        return null;
    }
    private static float Linear(byte b) => MathF.Pow(b / 255f, 2.2f);

    // --- Textures and masks ---------------------------------------------------------------------

    public Task<TextureData?> ReadTextureAsync(ulong hash, int maximumSize, CancellationToken cancellation = default) => Task.Run(() =>
    {
        // The user's own decal pictures first (their hashes are under ncwe\decals, never the game's).
        if (!_entries.ContainsKey(hash) && Decals?.Texture(hash, maximumSize) is { } own) return own;
        // A texture kept in a mesh's own file.
        if (!_entries.ContainsKey(hash) && _embeddedTextures.TryGetValue(hash, out ulong holder))
            try { return WithResource(holder, file => file.EmbeddedFiles.FirstOrDefault(e => e.FileName.GetRedHash() == hash)?.Content is CBitmapTexture kept ? TextureMips(kept, maximumSize) : null); }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or InvalidCastException or ArgumentException) { return null; }
        try { return WithResource(hash, file => file.RootChunk is CBitmapTexture texture ? TextureMips(texture, maximumSize) : null); }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or InvalidCastException or ArgumentException) { return null; }
    }, cancellation);

    public Task<MaskData?> ReadMaskAsync(ulong hash, int maximumSize, CancellationToken cancellation = default) => Task.Run(() =>
    {
        try
        {
            return WithResource(hash, file =>
            {
                if (file.RootChunk is not Multilayer_Mask mask) return null;
                var layers = ReadMasks(mask, maximumSize);
                if (layers.Length == 0) return null;
                int w = layers[0].Width, h = layers[0].Height;
                var pixels = new byte[w * h * layers.Length];
                for (int i = 0; i < layers.Length; i++) layers[i].Values.AsSpan(0, w * h).CopyTo(pixels.AsSpan(i * w * h));
                return new MaskData(w, h, layers.Length, pixels);
            });
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or InvalidCastException or ArgumentException) { return null; }
    }, cancellation);

    // The bytes from one row (of pixels, or of 4x4 blocks) to the next in a stored level. Some
    // textures declare garbage there (1024 px multilayer colour textures say 2,042,520,512): a
    // pitch the level's own data cannot hold is not believed, and the rows are packed.
    public static int RowPitch(uint declared, int rowBytes, int rows, uint levelBytes)
        => declared >= rowBytes && (long)(rows - 1) * declared + rowBytes <= levelBytes ? (int)declared : rowBytes;

    // The mip chain from the largest level no bigger than `maximum`, rows packed (first slice).
    internal static TextureData? TextureMips(CBitmapTexture texture, int maximum)
    {
        if (texture.RenderTextureResource.RenderResourceBlobPC.Chunk is not rendRenderTextureBlobPC blob) return null;
        var header = blob.Header;
        int width = header.SizeInfo.Width, height = header.SizeInfo.Height, mips = header.MipMapInfo.Count;
        if (width <= 0 || height <= 0 || mips == 0) return null;
        Enums.ETextureCompression compression = texture.Setup.Compression; Enums.ETextureRawFormat raw = texture.Setup.RawFormat;
        TextureEncoding? encoding = compression switch
        {
            Enums.ETextureCompression.TCM_None => raw switch { Enums.ETextureRawFormat.TRF_Grayscale => TextureEncoding.R8, Enums.ETextureRawFormat.TRF_TrueColor => TextureEncoding.Rgba8, _ => null },
            Enums.ETextureCompression.TCM_DXTNoAlpha => TextureEncoding.BC1,
            Enums.ETextureCompression.TCM_DXTAlpha or Enums.ETextureCompression.TCM_DXTAlphaLinear => TextureEncoding.BC3,
            Enums.ETextureCompression.TCM_QualityR => TextureEncoding.BC4,
            Enums.ETextureCompression.TCM_Normalmap or Enums.ETextureCompression.TCM_QualityRG or Enums.ETextureCompression.TCM_Normals => TextureEncoding.BC5,
            Enums.ETextureCompression.TCM_QualityColor => TextureEncoding.BC7,
            _ => null
        };
        if (encoding is not { } format) return null;
        bool block = format is not (TextureEncoding.R8 or TextureEncoding.Rgba8);
        int unit = format switch { TextureEncoding.BC1 or TextureEncoding.BC4 => 8, TextureEncoding.R8 => 1, TextureEncoding.Rgba8 => 4, _ => 16 };
        int level = 0;
        while (level + 1 < mips && Math.Max(width >> level, height >> level) > maximum) level++;
        // Block-compressed top levels must be whole blocks.
        while (block && level > 0 && ((width >> level) % 4 != 0 || (height >> level) % 4 != 0)) level--;
        int top = Math.Max(1, width >> level), topHeight = Math.Max(1, height >> level);
        if (block && (top % 4 != 0 || topHeight % 4 != 0)) return null;
        var all = blob.TextureData.Buffer.GetBytes();
        var levels = new List<byte[]>(); var pitches = new List<int>();
        for (int l = level; l < mips; l++)
        {
            int w = Math.Max(1, width >> l), h = Math.Max(1, height >> l);
            int rows = block ? (h + 3) / 4 : h, rowBytes = block ? (w + 3) / 4 * unit : w * unit;
            var mip = header.MipMapInfo[l];
            int offset = (int)(uint)mip.Placement.Offset, pitch = RowPitch((uint)mip.Layout.RowPitch, rowBytes, rows, (uint)mip.Placement.Size);
            if (offset < 0 || (long)offset + (long)(rows - 1) * pitch + rowBytes > all.Length) break;
            var data = new byte[rows * rowBytes];
            for (int r = 0; r < rows; r++) all.AsSpan(offset + r * pitch, rowBytes).CopyTo(data.AsSpan(r * rowBytes));
            levels.Add(data); pitches.Add(rowBytes);
        }
        if (levels.Count == 0) return null;
        bool srgb = texture.Setup.IsGamma && format is TextureEncoding.BC1 or TextureEncoding.BC2 or TextureEncoding.BC3 or TextureEncoding.BC7 or TextureEncoding.Rgba8;
        return new(top, topHeight, format, srgb, levels.ToArray(), pitches.ToArray());
    }
}

public sealed record MeshMaterialInfo(string Slot, SurfaceMaterial? Material, string Template, int Parts);

// The archive as the renderer's material source.
public sealed class ArchiveMaterialSource(GameArchiveIndex index) : IMaterialSource
{
    public Task<MeshSurface?> ReadSurfaceAsync(ulong mesh, CancellationToken cancellation) => index.ReadSurfaceAsync(mesh, cancellation);
    public Task<TextureData?> ReadTextureAsync(ulong texture, int maximumSize, CancellationToken cancellation) => index.ReadTextureAsync(texture, maximumSize, cancellation);
    public Task<MaskData?> ReadMaskAsync(ulong mask, int maximumSize, CancellationToken cancellation) => index.ReadMaskAsync(mask, maximumSize, cancellation);
    public Task<DecalMaterial?> ReadDecalAsync(ulong material, CancellationToken cancellation) => index.ReadDecalAsync(material, cancellation);
}
