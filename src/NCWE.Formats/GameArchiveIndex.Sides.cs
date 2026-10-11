// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using WolvenKit.RED4.Types;

namespace NCWE.Formats;

// Which faces of a mesh chunk the game draws. Most surfaces show their front only: the back is
// culled, so a facade seen from inside its building is not there. A chunk shows both faces when
// its render mask says so (MCF_IsTwoSided: hair cards, cloth, some sheets) or when its material's
// template draws both windings (leaves and tarps with CULL_None, glass with a CULL_Front pass
// before its CULL_Back one). Read from the game's own data, nothing is guessed from names.
public sealed partial class GameArchiveIndex
{
    private readonly ConcurrentDictionary<string, bool> _templateTwoSided = new(StringComparer.OrdinalIgnoreCase);

    // The stages that put a surface's colour on screen (not shadows, depth, velocity, highlights or debug views).
    private static readonly HashSet<string> ColorStages = new(StringComparer.OrdinalIgnoreCase)
    {
        "renderstage_gbuffer_regular", "renderstage_late_gbuffer_regular", "renderstage_post_gbuffer", "renderstage_hair_gbuffer_solid",
        "renderstage_transparent", "renderstage_transparent_notxaa", "renderstage_transparent_background", "renderstage_transparent_notxaa_background",
        "renderstage_transparent_back_face", "renderstage_unlit", "renderstage_screenspace_vfx", "renderstage_composition_element"
    };

    // Whether a pass draws triangles wound clockwise (the game's front) and counter-clockwise ones.
    public static (bool Clockwise, bool Counter) Windings(string cull, string frontWinding)
    {
        if (cull == "CULL_None") return (true, true);
        bool frontClockwise = frontWinding != "FRONTFACE_CCW";
        // CULL_Back keeps the front winding, CULL_Front the other one.
        bool keepsFront = cull != "CULL_Front";
        return keepsFront == frontClockwise ? (true, false) : (false, true);
    }

    // A template shows both faces when its colour stages draw both windings; one that only draws
    // the reverse winding (inside-out shells) is left two-sided here.
    public static bool DrawsBothSides(IEnumerable<(string Stage, string Cull, string FrontWinding)> passes)
    {
        bool counter = false;
        foreach (var (stage, cull, front) in passes)
            if (ColorStages.Contains(stage)) counter |= Windings(cull, front).Counter;
        return counter;
    }

    private bool TemplateTwoSided(string template) => template.Length != 0 && _templateTwoSided.GetOrAdd(template, path =>
    {
        try
        {
            var passes = WithResource(WolvenKit.Common.FNV1A.FNV1A64HashAlgorithm.HashString(path), file => file.RootChunk is not CMaterialTemplate t ? null
                : t.Techniques.SelectMany(technique => technique.Passes)
                    .Select(p => (p.StagePassNameRegular.ToString() ?? "", p.RasterizerMode.CullMode.ToString(), p.RasterizerMode.FrontWinding.ToString())).ToList());
            return passes is not null && DrawsBothSides(passes);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or InvalidCastException) { return false; }
    });

    // The template (.mt, .remt) a material slot of the mesh resolves to, through its instances.
    private string SlotTemplate(CMesh mesh, string name)
    {
        var entry = mesh.MaterialEntries.FirstOrDefault(e => e.Name.ToString() == name);
        if (entry is null) return "";
        int i = entry.Index;
        if (!entry.IsLocalInstance)
            return ExternalChain(i < mesh.ExternalMaterials.Count ? mesh.ExternalMaterials[i].DepotPath.GetRedHash() : 0, 0)?.Template ?? "";
        var instance = (i < mesh.LocalMaterialBuffer.Materials.Count ? mesh.LocalMaterialBuffer.Materials[i]
            : i < mesh.LocalMaterialInstances.Count ? mesh.LocalMaterialInstances[i].Chunk
            : i < mesh.PreloadLocalMaterialInstances.Count ? mesh.PreloadLocalMaterialInstances[i].Chunk : null) as CMaterialInstance;
        return Chain(instance, 0)?.Template ?? "";
    }

    // A custom mesh's material: one-sided unless it is marked double-sided (its chunks are then
    // cooked with MCF_IsTwoSided) or the template it is written with draws both faces (glass).
    internal bool CustomOneSided(NCWE.Core.CustomMaterial material)
    {
        if (material.DoubleSided) return false;
        try
        {
            string template = material.Kind switch
            {
                NCWE.Core.CustomMaterialKind.Glass => @"base\materials\glass.mt",
                NCWE.Core.CustomMaterialKind.Game when material.GameMaterial is { Length: > 0 } path => GameMaterialChain(path)?.Template ?? "",
                NCWE.Core.CustomMaterialKind.Instance when material.BaseMaterial is { Length: > 0 } parent => GameMaterialChain(parent)?.Template ?? "",
                _ => @"engine\materials\metal_base.remt"
            };
            return !TemplateTwoSided(template);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or InvalidCastException or ArgumentException) { return true; }
    }

    // Per render chunk (render-blob order): true when the game draws its front faces only. The
    // material is the chunk's in the mesh's first appearance.
    internal bool[] ChunkOneSided(CMesh mesh, rendRenderMeshBlob blob)
    {
        var infos = blob.Header.RenderChunkInfos;
        var result = new bool[infos.Count];
        var materials = mesh.Appearances.Select(a => a.Chunk).OfType<meshMeshAppearance>().FirstOrDefault()?.ChunkMaterials;
        var slots = new Dictionary<string, bool>(StringComparer.Ordinal);
        for (int i = 0; i < result.Length; i++)
        {
            if (((Enums.EMeshChunkFlags)infos[i].RenderMask).HasFlag(Enums.EMeshChunkFlags.MCF_IsTwoSided)) continue;
            string name = materials is not null && i < materials.Count ? materials[i].ToString() ?? "" : "";
            if (!slots.TryGetValue(name, out bool twoSided))
            {
                try { twoSided = TemplateTwoSided(SlotTemplate(mesh, name)); }
                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or InvalidCastException) { twoSided = false; }
                slots[name] = twoSided;
            }
            result[i] = !twoSided;
        }
        return result;
    }
}
