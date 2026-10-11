// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using System.Collections.Immutable;
using NCWE.Core;
using NCWE.Formats.CustomMeshes;
using WolvenKit.Modkit.RED4.Tools;
using WolvenKit.RED4.Archive.CR2W;
using WolvenKit.RED4.Archive.IO;
using WolvenKit.RED4.Types;

namespace NCWE.Formats;

// The material tool's view of the archives (see NCWE.Core.ProjectMaterials):
// - a mesh's material slots, and which slot each drawn part wears per appearance;
// - a slot's or a material file's material as a definition (import into the bank);
// - a definition as the editor draws it (the same instance chain and families as game materials);
// - a mesh with some slots wearing other materials (the export's variant meshes).
public sealed partial class GameArchiveIndex
{
    public sealed record SlotInfo(string Name, string Material, bool Local, int Parts);

    // The user's material store (set by the host): the textures of the project's materials.
    public MaterialStore? MaterialStore { get; set; }

    // A mesh's slots (its material entries) with what each wears: a .mi path, or the base material
    // of a local instance; Parts: how many parts of the default appearance wear it.
    public IReadOnlyList<SlotInfo> MeshSlots(ulong meshHash, string? appearance = null)
    {
        if (CustomInfo(meshHash) is { } custom)
            return [.. custom.Materials.Select((m, i) => new SlotInfo(CustomMeshCooker.SlotName(m, i), m.Kind == CustomMaterialKind.Game ? m.GameMaterial ?? "" : m.Kind.ToString(), true, 1))];
        var described = DescribeMaterials(meshHash);
        var look = described.Appearances.FirstOrDefault(a => a.Name == (appearance ?? "default")) ?? described.Appearances.FirstOrDefault(a => a.Name == "default") ?? described.Appearances.FirstOrDefault();
        return [.. described.Slots.Select(s => new SlotInfo(s.Name, s.Reference ?? s.BaseMaterial ?? "", s.Local, look?.ChunkMaterials.Count(c => c == s.Name) ?? 0))];
    }

    private CustomMeshInfo? CustomInfo(ulong meshHash) => !_entries.ContainsKey(meshHash) && Custom is CustomMeshLibrary library ? library.ByHash(meshHash) : null;

    // Every slot's own material as the editor draws it, the slots no appearance draws included
    // (Parts: how many parts of the given appearance wear it).
    public Task<IReadOnlyList<MeshMaterialInfo>> ReadSlotMaterialsAsync(ulong meshHash, string? appearance, CancellationToken cancellation = default) => Task.Run(() =>
    {
        IReadOnlyList<MeshMaterialInfo> none = [];
        if (!_entries.TryGetValue(meshHash, out var entry)) return none;
        using var data = ExtractEntry(entry, cancellation);
        using var reader = new CR2WReader(data);
        reader.ParsingError += SectorReader.TolerateSectorParsingError;
        if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file is null) return none;
        using (file)
        {
            if (file.RootChunk is not CMesh mesh) return none;
            var looks = mesh.Appearances.Select(a => a.Chunk).OfType<meshMeshAppearance>().ToArray();
            var look = looks.FirstOrDefault(a => a.Name.ToString() == (appearance ?? "default")) ?? looks.FirstOrDefault(a => a.Name.ToString() == "default") ?? looks.FirstOrDefault();
            var drawn = look?.ChunkMaterials.Select(c => c.ToString() ?? "").ToList() ?? [];
            var result = new List<MeshMaterialInfo>();
            foreach (var name in mesh.MaterialEntries.Select(e => e.Name.ToString() ?? "").Where(n => n.Length > 0).Distinct(StringComparer.Ordinal))
            {
                cancellation.ThrowIfCancellationRequested();
                SurfaceMaterial? material = null; string template = "";
                try { material = ViewedSlot(mesh, name, meshHash, entry.FileName, out template); }
                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or InvalidCastException) { }
                result.Add(new(name, material, template, drawn.Count(c => c == name)));
            }
            return (IReadOnlyList<MeshMaterialInfo>)result;
        }
    }, cancellation);

    // --- Parts and slots --------------------------------------------------------------------------

    private readonly ConcurrentDictionary<ulong, IReadOnlyDictionary<string, string[]>?> _partSlots = new();

    // Per appearance, the slot each part the editor draws wears (the parts the surface describes,
    // in order); custom meshes: their submeshes' slots.
    public IReadOnlyDictionary<string, string[]>? PartSlots(ulong meshHash) => _partSlots.GetOrAdd(meshHash, hash =>
    {
        if (CustomInfo(hash) is { } custom && Custom is CustomMeshLibrary library)
        {
            var geometry = library.Geometry(custom);
            return new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["default"] = [.. geometry.Submeshes.Select(s => CustomMeshCooker.SlotName(s.Material < custom.Materials.Length ? custom.Materials[s.Material] : new CustomMaterial("Default"), Math.Clamp(s.Material, 0, Math.Max(0, custom.Materials.Length - 1))))]
            };
        }
        if (!_entries.TryGetValue(hash, out var entry)) return null;
        try
        {
            using var data = ExtractEntry(entry, default);
            using var reader = new CR2WReader(data);
            reader.ParsingError += SectorReader.TolerateSectorParsingError;
            if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file is null) return null;
            using (file)
            {
                if (file.RootChunk is not CMesh { RenderResourceBlob.Chunk: rendRenderMeshBlob blob } mesh) return null;
                var info = MeshTools.GetMeshesinfo(blob, mesh);
                using var buffer = new MemoryStream(blob.RenderBuffer.Buffer.GetBytes());
                var raw = MeshTools.ContainRawMesh(buffer, info, false);
                // The parts ReadSurface keeps, in the same order.
                var kept = Enumerable.Range(0, raw.Count).Where(r => raw[r].positions is { Length: > 0 } && raw[r].indices is { Length: > 0 }).ToArray();
                var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
                foreach (var appearance in mesh.Appearances.Select(a => a.Chunk).OfType<meshMeshAppearance>())
                    if (appearance.ChunkMaterials.Count >= raw.Count)
                        result.TryAdd(appearance.Name.ToString() ?? "default", [.. kept.Select(k => appearance.ChunkMaterials[k].ToString() ?? "")]);
                return result;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or InvalidCastException or ArgumentException) { return null; }
    });

    // --- Import ----------------------------------------------------------------------------------

    public sealed record ImportedMaterial(CustomMaterial Material, string Source, int Skipped);

    // A mesh slot's material as a definition. An external material is referenced as it is
    // (`editable`: copied one level instead, its values editable over its parent); a local
    // instance is copied with its values.
    public ImportedMaterial ImportSlotMaterial(ulong meshHash, string slot, bool editable = true)
    {
        string meshPath = Find(meshHash)?.Path ?? meshHash.ToString("x16");
        if (CustomInfo(meshHash) is { } custom)
        {
            var own = custom.Materials.Select((m, i) => (m, i)).FirstOrDefault(x => CustomMeshCooker.SlotName(x.m, x.i) == slot);
            if (own.m is null) throw new InvalidDataException($"No slot {slot} on {meshPath}.");
            if (own.m.TextureFiles().Any()) throw new InvalidDataException("This custom mesh's textures are in its own folder: edit its materials in Custom ▸ Materials, or remake the material in the bank.");
            return new(own.m with { Name = slot }, $"{meshPath} · {slot}", 0);
        }
        if (!_entries.TryGetValue(meshHash, out var entry)) throw new FileNotFoundException("Unknown mesh: " + meshPath);
        using var data = ExtractEntry(entry, default);
        using var reader = new CR2WReader(data);
        reader.ParsingError += SectorReader.TolerateSectorParsingError;
        if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file is null) throw new InvalidDataException("Unreadable mesh: " + meshPath);
        using (file)
        {
            if (file.RootChunk is not CMesh mesh) throw new InvalidDataException("Not a mesh: " + meshPath);
            var e = mesh.MaterialEntries.FirstOrDefault(x => x.Name.ToString() == slot) ?? throw new InvalidDataException($"No slot {slot} on {meshPath}.");
            string source = $"{meshPath} · {slot}";
            int i = e.Index;
            if (!e.IsLocalInstance)
            {
                var path = i < mesh.ExternalMaterials.Count ? mesh.ExternalMaterials[i].DepotPath : default;
                string text = path.GetResolvedText() is { Length: > 0 } t ? t : throw new InvalidDataException($"Slot {slot} refers to a material the archives do not name.");
                var imported = ImportMaterialFile(text, editable);
                return imported with { Material = imported.Material with { Name = slot }, Source = source };
            }
            var instance = (i < mesh.LocalMaterialBuffer.Materials.Count ? mesh.LocalMaterialBuffer.Materials[i]
                : i < mesh.LocalMaterialInstances.Count ? mesh.LocalMaterialInstances[i].Chunk
                : i < mesh.PreloadLocalMaterialInstances.Count ? mesh.PreloadLocalMaterialInstances[i].Chunk : null) as CMaterialInstance
                ?? throw new InvalidDataException($"Slot {slot} has no material instance.");
            return new(Instance(slot, instance, out int skipped), source, skipped);
        }
    }

    // A material file (.mi, .mt, .remt) as a definition: referenced (a .mt or `editable` false), or
    // its own values copied over its parent (a .mi with `editable`).
    public ImportedMaterial ImportMaterialFile(string path, bool editable = true)
    {
        string clean = path.Trim().Replace('/', '\\');
        ulong hash = WolvenKit.Common.FNV1A.FNV1A64HashAlgorithm.HashString(clean);
        if (!_entries.ContainsKey(hash)) throw new FileNotFoundException("Not in the game's archives: " + clean);
        string name = Path.GetFileNameWithoutExtension(clean);
        if (!editable || !clean.EndsWith(".mi", StringComparison.OrdinalIgnoreCase))
            return new(new CustomMaterial(name) { Kind = CustomMaterialKind.Game, GameMaterial = clean }, clean, 0);
        int skipped = 0;
        var material = WithResource(hash, f => f.RootChunk is CMaterialInstance mi ? Instance(name, mi, out skipped) : null)
            ?? throw new InvalidDataException("Not a material instance: " + clean);
        return new(material, clean, skipped);
    }

    private static CustomMaterial Instance(string name, CMaterialInstance instance, out int skipped)
    {
        string? baseMaterial = instance.BaseMaterial.DepotPath.GetResolvedText();
        if (string.IsNullOrEmpty(baseMaterial)) throw new InvalidDataException("This material's base material is not named in the archives.");
        return new CustomMaterial(name) { Kind = CustomMaterialKind.Instance, BaseMaterial = baseMaterial, Parameters = MaterialInstances.Parameters(instance, out skipped) };
    }

    // The game's material files whose path contains a text (search for references and bases).
    public IEnumerable<string> MaterialFiles(string text, int limit = 60) => _entries.Values
        .Select(e => e.FileName).OfType<string>()
        .Where(p => (p.EndsWith(".mi", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".mt", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".remt", StringComparison.OrdinalIgnoreCase))
            && p.Contains(text, StringComparison.OrdinalIgnoreCase))
        .OrderBy(p => p.Length).Take(limit);

    // The parameters a base material (.mt / .remt template, or a .mi chain) offers, with their
    // current values: what an Instance material can set.
    public IReadOnlyList<MaterialParameter> BaseParameters(string path)
    {
        ulong hash = WolvenKit.Common.FNV1A.FNV1A64HashAlgorithm.HashString(path.Trim());
        var result = new List<MaterialParameter>();
        try
        {
            WithResource<object>(hash, file =>
            {
                switch (file.RootChunk)
                {
                    case CMaterialInstance mi when ExternalChain(hash, 0) is { } chain:
                        foreach (var (key, value) in chain.Values.OrderBy(p => p.Key, StringComparer.Ordinal))
                            if (MaterialInstances.Parameter(key, value) is { } p) result.Add(p);
                        if (chain.Template.Length > 0) result.AddRange(BaseParameters(chain.Template).Where(p => result.All(q => q.Name != p.Name)));
                        break;
                    case CMaterialTemplate template:
                        foreach (var handle in template.Parameters.SelectMany(group => group))
                            if (handle.Chunk is CMaterialParameter parameter && TemplateDefault(parameter) is { } p) result.Add(p);
                        break;
                }
                return file;
            });
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or InvalidCastException) { }
        return result;
    }

    // A template parameter's name and default value (the values a material sets have these types).
    private static MaterialParameter? TemplateDefault(CMaterialParameter parameter)
    {
        string name = parameter.ParameterName.ToString() ?? "";
        if (name.Length == 0) return null;
        return parameter switch
        {
            CMaterialParameterScalar s => new(name, MaterialParameterType.Float) { Numbers = [(float)s.Scalar] },
            CMaterialParameterColor c => new(name, MaterialParameterType.Color) { Numbers = [c.Color.Red, c.Color.Green, c.Color.Blue, c.Color.Alpha] },
            CMaterialParameterVector v => new(name, MaterialParameterType.Vector) { Numbers = [(float)v.Vector.X, (float)v.Vector.Y, (float)v.Vector.Z, (float)v.Vector.W] },
            CMaterialParameterTexture t => new(name, MaterialParameterType.Resource) { Text = t.Texture.DepotPath.GetResolvedText() ?? "", ResourceType = "ITexture" },
            CMaterialParameterMultilayerSetup ms => new(name, MaterialParameterType.Resource) { Text = ms.Setup.DepotPath.GetResolvedText() ?? "", ResourceType = "Multilayer_Setup" },
            CMaterialParameterMultilayerMask mm => new(name, MaterialParameterType.Resource) { Text = mm.Mask.DepotPath.GetResolvedText() ?? "", ResourceType = "Multilayer_Mask" },
            CMaterialParameterCube cube => new(name, MaterialParameterType.Resource) { Text = cube.Texture.DepotPath.GetResolvedText() ?? "", ResourceType = "ITexture" },
            _ => null
        };
    }

    // --- Preview -----------------------------------------------------------------------------------

    // A definition as the editor draws it: written as its material instance (its own textures at
    // the depot paths `texture` gives) and read through the same chain and families as the game's.
    public SurfaceMaterial? MaterialSurface(CustomMaterial material, string key, Func<string, string, string> texture)
    {
        try
        {
            MaterialChain? chain = material.Kind == CustomMaterialKind.Game && material.GameMaterial is { } path
                ? GameMaterialChain(path)
                : Chain(MaterialInstances.Build(material, texture), 0);
            return chain is null ? null : Classify(chain, key);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or InvalidCastException or ArgumentException) { return null; }
    }

    private MaterialChain? GameMaterialChain(string path)
    {
        string clean = path.Trim().Replace('/', '\\');
        ulong hash = WolvenKit.Common.FNV1A.FNV1A64HashAlgorithm.HashString(clean);
        // A template is its own chain (no values); an instance is read through its parents.
        return clean.EndsWith(".mi", StringComparison.OrdinalIgnoreCase) ? ExternalChain(hash, 0) : new MaterialChain(clean.ToLowerInvariant(), new());
    }

    // --- Variants ---------------------------------------------------------------------------------

    // A game mesh with some slots wearing other materials (slot → .mi depot path).
    public byte[] MeshVariant(ulong meshHash, IReadOnlyDictionary<string, string> slots)
    {
        if (!_entries.TryGetValue(meshHash, out var entry)) throw new FileNotFoundException("Not in the game's archives: " + meshHash.ToString("x16"));
        using var data = ExtractEntry(entry, default);
        return MaterialVariants.Write(data.ToArray(), slots);
    }
}

// Mesh files with some of their slots wearing other materials, everything else as it was.
public static class MaterialVariants
{
    public static byte[] Write(byte[] mesh, IReadOnlyDictionary<string, string> slots)
    {
        using var file = CustomCr2w.Read(mesh);
        if (file.RootChunk is not CMesh root) throw new InvalidDataException("Not a mesh.");
        Apply(root, slots);
        using var stream = new MemoryStream();
        using (var writer = new CR2WWriter(stream, System.Text.Encoding.UTF8, true)) writer.WriteFile(file);
        var bytes = stream.ToArray();
        // Read back: every changed slot refers to its material.
        using var check = CustomCr2w.Read(bytes);
        if (check.RootChunk is not CMesh written) throw new InvalidDataException("A mesh variant cannot be read back.");
        foreach (var (slot, path) in slots)
        {
            var e = written.MaterialEntries.FirstOrDefault(x => x.Name.ToString() == slot);
            if (e is null || e.IsLocalInstance || e.Index >= written.ExternalMaterials.Count
                || !string.Equals(written.ExternalMaterials[e.Index].DepotPath.GetResolvedText(), path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The mesh variant's slot {slot} does not refer to {path}.");
        }
        return bytes;
    }

    // Each named slot's entry refers to its material (external, like the game's .mi references);
    // a slot the mesh does not have is an error.
    public static void Apply(CMesh mesh, IReadOnlyDictionary<string, string> slots)
    {
        foreach (var (slot, path) in slots)
        {
            var entry = mesh.MaterialEntries.FirstOrDefault(e => e.Name.ToString() == slot) ?? throw new InvalidDataException("The mesh has no slot " + slot + ".");
            int existing = mesh.ExternalMaterials.Select((m, i) => (m, i)).FirstOrDefault(x => string.Equals(x.m.DepotPath.GetResolvedText(), path, StringComparison.OrdinalIgnoreCase)).i;
            bool found = existing < mesh.ExternalMaterials.Count && string.Equals(mesh.ExternalMaterials[existing].DepotPath.GetResolvedText(), path, StringComparison.OrdinalIgnoreCase);
            if (!found) { existing = mesh.ExternalMaterials.Count; mesh.ExternalMaterials.Add(new CResourceAsyncReference<IMaterial>(path)); }
            entry.IsLocalInstance = false;
            entry.Index = (ushort)existing;
        }
    }
}
