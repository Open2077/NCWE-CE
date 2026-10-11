// SPDX-License-Identifier: GPL-3.0-only
using WolvenKit.RED4.Archive.Buffer;
using WolvenKit.RED4.Archive.IO;
using WolvenKit.RED4.Types;

namespace NCWE.Formats;

// Diagnostics (probe): what decides how the game draws a mesh chunk (its render mask: two-sided,
// shadows...) and a decal node (its box, flips and material).
public sealed partial class GameArchiveIndex
{
    public sealed record ChunkSide(int Index, uint Lod, string Mask, bool OneSided, string Material, string Template, string Alpha = "");

    // Per render chunk, in render-blob order: LOD mask, render mask, the material of the first
    // appearance and whether the game draws its front faces only (ChunkOneSided).
    public IReadOnlyList<ChunkSide> ChunkSides(ulong hash)
    {
        return WithResource(hash, file =>
        {
            if (file.RootChunk is not CMesh { RenderResourceBlob.Chunk: rendRenderMeshBlob blob } mesh) return null;
            var materials = mesh.Appearances.Select(a => a.Chunk).OfType<meshMeshAppearance>().FirstOrDefault()?.ChunkMaterials;
            var oneSided = ChunkOneSided(mesh, blob);
            var chunks = new List<ChunkSide>();
            for (int i = 0; i < blob.Header.RenderChunkInfos.Count; i++)
            {
                var chunk = blob.Header.RenderChunkInfos[i];
                string name = materials is not null && i < materials.Count ? materials[i].ToString() ?? "" : "";
                string template = SlotTemplate(mesh, name);
                chunks.Add(new(i, (uint)(byte)chunk.LodMask, ((Enums.EMeshChunkFlags)chunk.RenderMask).ToString(), oneSided[i], name, template, AlphaNote(mesh, name, template)));
            }
            return chunks;
        }) ?? [];
    }

    // Diagnostics: the slot's instance masks (EnableMask), the threshold it sets and its template's default.
    private string AlphaNote(CMesh mesh, string name, string template)
    {
        var entry = mesh.MaterialEntries.FirstOrDefault(e => e.Name.ToString() == name);
        if (entry is null) return "";
        int i = entry.Index;
        CMaterialInstance? instance = null; MaterialChain? chain;
        if (entry.IsLocalInstance)
        {
            instance = (i < mesh.LocalMaterialBuffer.Materials.Count ? mesh.LocalMaterialBuffer.Materials[i]
                : i < mesh.LocalMaterialInstances.Count ? mesh.LocalMaterialInstances[i].Chunk
                : i < mesh.PreloadLocalMaterialInstances.Count ? mesh.PreloadLocalMaterialInstances[i].Chunk : null) as CMaterialInstance;
            chain = Chain(instance, 0);
        }
        else chain = ExternalChain(i < mesh.ExternalMaterials.Count ? mesh.ExternalMaterials[i].DepotPath.GetRedHash() : 0, 0);
        string set = chain is not null && chain.Values.TryGetValue("AlphaThreshold", out var value) && value is CFloat f ? ((float)f).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "-";
        string fallback = TemplateDefaults(template).TryGetValue("AlphaThreshold", out var d) && d is CFloat df ? ((float)df).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "-";
        return $"mask {(instance is null ? "?" : instance.EnableMask ? "on" : "off")} chain-mask {(chain?.Masked == true ? "on" : "off")} threshold {set} default {fallback}";
    }

    // A texture as the renderer's loader sees it: path, size, format, and what it makes of it.
    public string DescribeTexture(ulong hash)
    {
        string path = TexturePath(hash) ?? "(not in the archives)";
        try
        {
            return WithResource(hash, file =>
            {
                if (file.RootChunk is not CBitmapTexture t) return $"{hash:x16} {path}: a {file.RootChunk.GetType().Name}, not a texture";
                string blob = t.RenderTextureResource.RenderResourceBlobPC.Chunk is rendRenderTextureBlobPC b
                    ? $"{b.Header.SizeInfo.Width}x{b.Header.SizeInfo.Height} mips {b.Header.MipMapInfo.Count} type {b.Header.TextureInfo.Type} slices {b.Header.TextureInfo.SliceCount} data {b.TextureData.Buffer.GetBytes().Length} pitch {(b.Header.MipMapInfo.Count > 0 ? (uint)b.Header.MipMapInfo[0].Layout.RowPitch : 0)}"
                    : "no PC blob (" + (t.RenderTextureResource.RenderResourceBlobPC.Chunk?.GetType().Name ?? "none") + ")";
                var data = TextureMips(t, 1024);
                return $"{hash:x16} {path}: {(uint)t.Width}x{(uint)t.Height} {t.Setup.Compression} {t.Setup.RawFormat} gamma {t.Setup.IsGamma} group {t.Setup.Group} streamable {t.Setup.IsStreamable} | {blob} | loader: {(data is null ? "REFUSED" : $"{data.Width}x{data.Height} {data.Encoding} {data.Mips.Length} mips")}";
            }) ?? $"{hash:x16} {path}: unreadable";
        }
        catch (Exception ex) { return $"{hash:x16} {path}: {ex.GetType().Name} {ex.Message}"; }
    }

    // A material template's passes: render stage and cull mode, in order (two-sided templates draw
    // their main stage with CULL_None, or twice: CULL_Front then CULL_Back).
    public IReadOnlyList<(string Stage, string Cull)> TemplatePasses(ulong hash)
    {
        return WithResource(hash, file => file.RootChunk is not CMaterialTemplate template ? null
            : template.Techniques.SelectMany(t => t.Passes).Select(p => (p.StagePassNameRegular.ToString() ?? "",
                p.RasterizerMode.CullMode.ToString() + (p.RasterizerMode.FrontWinding.ToString() == "FRONTFACE_CW" ? "" : "/" + p.RasterizerMode.FrontWinding))).ToList()) ?? [];
    }

    public IEnumerable<(string Path, ulong Hash)> MaterialTemplates() => _entries.Values
        .Where(e => e.FileName is { Length: > 0 } && (e.Extension.Equals(".mt", StringComparison.OrdinalIgnoreCase) || e.Extension.Equals(".remt", StringComparison.OrdinalIgnoreCase)))
        .Select(e => (e.FileName!, e.Key)).OrderBy(e => e.Item1, StringComparer.OrdinalIgnoreCase);

    // The decal nodes of a sector: material, its template and parameter names, box size and flags.
    public IReadOnlyList<(string Material, string Template, string[] Keys, System.Numerics.Vector3 Scale, bool Flip, float Hide, System.Numerics.Vector3 Position)> DecalSurvey(ulong sectorHash)
    {
        var entry = EntryOrNull(sectorHash) ?? throw new FileNotFoundException();
        using var data = ExtractEntry(entry, default);
        using var reader = new CR2WReader(data);
        reader.ParsingError += SectorReader.TolerateSectorParsingError;
        if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file is null) throw new InvalidDataException();
        var found = new List<(string, string, string[], System.Numerics.Vector3, bool, float, System.Numerics.Vector3)>();
        using (file)
        {
            var sector = (worldStreamingSector)file.RootChunk;
            if (sector.NodeData.Buffer.Data is not worldNodeDataBuffer setups) return found;
            foreach (var s in setups)
            {
                if (sector.Nodes[s.NodeIndex].Chunk is not worldStaticDecalNode node) continue;
                var chain = ExternalChain(node.Material.DepotPath.GetRedHash(), 0);
                found.Add((node.Material.DepotPath.GetResolvedText() ?? "", chain?.Template ?? "", chain?.Values.Keys.ToArray() ?? [], new(s.Scale.X, s.Scale.Y, s.Scale.Z),
                    node.HorizontalFlip || node.VerticalFlip, node.AutoHideDistance, new(s.Position.X, s.Position.Y, s.Position.Z)));
            }
        }
        return found;
    }

    // The placements of a sector by node type.
    public IReadOnlyDictionary<string, int> NodeCensus(ulong sectorHash)
    {
        var entry = EntryOrNull(sectorHash) ?? throw new FileNotFoundException();
        using var data = ExtractEntry(entry, default);
        using var reader = new CR2WReader(data);
        reader.ParsingError += SectorReader.TolerateSectorParsingError;
        if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file is null) throw new InvalidDataException();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        using (file)
        {
            var sector = (worldStreamingSector)file.RootChunk;
            if (sector.NodeData.Buffer.Data is not worldNodeDataBuffer setups) return counts;
            foreach (var s in setups)
            {
                string type = sector.Nodes[s.NodeIndex].Chunk?.GetType().Name ?? "(empty)";
                counts[type] = counts.GetValueOrDefault(type) + 1;
            }
        }
        return counts;
    }

    // A material template's parameters with their defaults, as an instance would set them.
    public IEnumerable<string> DumpTemplateParameters(string template)
    {
        foreach (var (key, value) in TemplateDefaults(template.Trim().ToLowerInvariant()).OrderBy(p => p.Key, StringComparer.Ordinal))
            yield return $"{key} = " + value switch
            {
                CFloat f => ((float)f).ToString(System.Globalization.CultureInfo.InvariantCulture),
                CColor c => $"color {c.Red},{c.Green},{c.Blue},{c.Alpha}",
                WolvenKit.RED4.Types.Vector4 q => FormattableString.Invariant($"vec4 {(float)q.X},{(float)q.Y},{(float)q.Z},{(float)q.W}"),
                IRedRef r => "ref " + (r.DepotPath.GetResolvedText() ?? r.DepotPath.GetRedHash().ToString("x16")) + (r.DepotPath.GetRedHash() != 0 && EntryOrNull(r.DepotPath.GetRedHash()) is null ? "  (not in the archives)" : ""),
                _ => value.GetType().Name + " " + value
            };
    }

    // The kind of object a mesh is to the renderer (CMesh.ObjectType): what typed decals look at.
    public string MeshObjectType(ulong hash)
    {
        try { return WithResource(hash, file => file.RootChunk is CMesh mesh ? mesh.ObjectType.ToString() : "(not a mesh)") ?? "(unreadable)"; }
        catch (Exception ex) { return "(" + ex.GetType().Name + ")"; }
    }

    // The decal nodes of a sector: which surfaces each accepts (normal threshold, stretching) and how it blends.
    public IReadOnlyList<(bool Threshold, float Angle, bool Stretch, float Alpha, string Mode, string Surface, string Material)> DecalFacingSurvey(ulong sectorHash)
    {
        var entry = EntryOrNull(sectorHash) ?? throw new FileNotFoundException();
        using var data = ExtractEntry(entry, default);
        using var reader = new CR2WReader(data);
        reader.ParsingError += SectorReader.TolerateSectorParsingError;
        if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file is null) throw new InvalidDataException();
        var found = new List<(bool, float, bool, float, string, string, string)>();
        using (file)
        {
            var sector = (worldStreamingSector)file.RootChunk;
            if (sector.NodeData.Buffer.Data is not worldNodeDataBuffer setups) return found;
            foreach (var s in setups)
                if (sector.Nodes[s.NodeIndex].Chunk is worldStaticDecalNode node)
                    found.Add((node.EnableNormalTreshold, node.NormalThreshold, node.IsStretchingEnabled, node.Alpha, node.DecalRenderMode.ToString(), node.SurfaceType.ToString(),
                        System.IO.Path.GetFileName(node.Material.DepotPath.GetResolvedText() ?? "")));
        }
        return found;
    }

    // The decal nodes of a sector around a point: transform, every node property, and the material chain.
    public IEnumerable<string> DumpDecalsNear(ulong sectorHash, System.Numerics.Vector3 point, float radius, bool materials = true)
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
                if (sector.Nodes[setups[i].NodeIndex].Chunk is not worldStaticDecalNode node) continue;
                var s = setups[i];
                var position = new System.Numerics.Vector3(s.Position.X, s.Position.Y, s.Position.Z);
                if (System.Numerics.Vector3.Distance(position, point) > radius) continue;
                string path = node.Material.DepotPath.GetResolvedText() ?? node.Material.DepotPath.GetRedHash().ToString("x16");
                yield return $"placement {i} node {s.NodeIndex} {node.DebugName} pos ({position.X:F3},{position.Y:F3},{position.Z:F3}) rot ({s.Orientation.I:F4},{s.Orientation.J:F4},{s.Orientation.K:F4},{s.Orientation.R:F4}) scale ({s.Scale.X:F3},{s.Scale.Y:F3},{s.Scale.Z:F3})";
                yield return "    material " + path;
                var values = new List<string>();
                foreach (var property in typeof(worldStaticDecalNode).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
                {
                    if (property.Name is "Material" || property.GetIndexParameters().Length != 0) continue;
                    object? value; try { value = property.GetValue(node); } catch (Exception) { continue; }
                    values.Add($"{property.Name}={(value is HDRColor c ? $"({(float)c.Red:0.###},{(float)c.Green:0.###},{(float)c.Blue:0.###},{(float)c.Alpha:0.###})" : value)}");
                }
                yield return "    " + string.Join(" ", values);
                if (materials) foreach (var line in DumpMaterialChain(node.Material.DepotPath.GetRedHash())) yield return "      " + line;
            }
        }
    }
}
