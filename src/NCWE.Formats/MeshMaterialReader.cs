// SPDX-License-Identifier: GPL-3.0-only
using WolvenKit.RED4.Archive.IO;
using WolvenKit.RED4.Types;

namespace NCWE.Formats;

public sealed record MaterialValue(string Key, string Kind, string Text);
public sealed record MaterialSlot(int Index, string Name, bool Local, string? Reference, string? BaseMaterial, MaterialValue[] Values);
public sealed record MeshAppearanceInfo(string Name, string[] ChunkMaterials);
public sealed record MeshMaterials(string Path, MeshAppearanceInfo[] Appearances, MaterialSlot[] Slots);

// Read-only description of a mesh's material bindings, for investigating texture and
// material approximations. Nothing here is used for rendering yet.
public sealed partial class GameArchiveIndex
{
    public MeshMaterials DescribeMaterials(ulong hash)
    {
        if (!_entries.TryGetValue(hash, out var entry)) throw new FileNotFoundException($"Unknown resource hash: {hash}");
        using var data = ExtractEntry(entry, default);
        using var reader = new CR2WReader(data);
        if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file is null) throw new InvalidDataException("CR2W read failed.");
        using (file)
        {
            if (file.RootChunk is not CMesh mesh) throw new NotSupportedException("Not a mesh.");
            var appearances = mesh.Appearances.Select(a => a.Chunk).OfType<meshMeshAppearance>()
                .Select(a => new MeshAppearanceInfo(a.Name.ToString() ?? "", a.ChunkMaterials.Select(m => m.ToString() ?? "").ToArray())).ToArray();
            var slots = new List<MaterialSlot>();
            for (int i = 0; i < mesh.MaterialEntries.Count; i++)
            {
                var e = mesh.MaterialEntries[i];
                string name = e.Name.ToString() ?? $"material {i}";
                int index = e.Index;
                if (e.IsLocalInstance)
                {
                    IMaterial? material = index < mesh.LocalMaterialBuffer.Materials.Count ? mesh.LocalMaterialBuffer.Materials[index]
                        : index < mesh.LocalMaterialInstances.Count ? mesh.LocalMaterialInstances[index].Chunk
                        : index < mesh.PreloadLocalMaterialInstances.Count ? mesh.PreloadLocalMaterialInstances[index].Chunk : null;
                    slots.Add(Describe(i, name, true, null, material));
                }
                else
                {
                    string? path = index < mesh.ExternalMaterials.Count ? mesh.ExternalMaterials[index].DepotPath.GetResolvedText() ?? mesh.ExternalMaterials[index].DepotPath.GetRedHash().ToString() : null;
                    slots.Add(new(i, name, false, path, null, []));
                }
            }
            return new(entry.FileName, appearances, slots.ToArray());
        }
    }

    private static MaterialSlot Describe(int index, string name, bool local, string? reference, IMaterial? material)
    {
        if (material is not CMaterialInstance instance) return new(index, name, local, reference, material?.GetType().Name, []);
        string? baseMaterial = instance.BaseMaterial.DepotPath.GetResolvedText() ?? instance.BaseMaterial.DepotPath.GetRedHash().ToString();
        var values = instance.Values.Select(v => new MaterialValue(v.Key.ToString() ?? "", v.Value?.GetType().Name ?? "null", Format(v.Value))).ToArray();
        return new(index, name, local, reference, baseMaterial, values);
    }

    private static string Format(IRedType? value) => value switch
    {
        null => "",
        IRedRef reference => reference.DepotPath.GetResolvedText() ?? reference.DepotPath.GetRedHash().ToString(),
        CColor c => $"rgba({c.Red},{c.Green},{c.Blue},{c.Alpha})",
        Vector4 v => $"({(float)v.X:0.###},{(float)v.Y:0.###},{(float)v.Z:0.###},{(float)v.W:0.###})",
        CFloat f => ((float)f).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
        CName n => n.ToString() ?? "",
        _ => value.GetType().Name
    };
}
