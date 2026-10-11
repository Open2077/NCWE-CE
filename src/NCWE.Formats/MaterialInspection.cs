// SPDX-License-Identifier: GPL-3.0-only
using WolvenKit.RED4.Archive.CR2W;
using WolvenKit.RED4.Archive.IO;
using WolvenKit.RED4.Types;

namespace NCWE.Formats;

public sealed partial class GameArchiveIndex
{
    // Diagnostic: every indexed resource path (names resolved by the hash dictionary).
    public IEnumerable<string> AllPaths() => _entries.Values.Select(e => e.FileName);

    // Resources with a known path and one of the extensions (".particle", ".effect", ".mi"...).
    public IReadOnlyList<ArchiveResource> ResourcesWithExtension(params string[] extensions)
        => _entries.Values.Where(e => e.FileName.Contains('\\') && extensions.Any(x => e.FileName.EndsWith(x, StringComparison.OrdinalIgnoreCase)))
            .Select(e => new ArchiveResource(e.Key, e.FileName, Path.GetFileName(e.Archive.ArchiveAbsolutePath), e.Size)).OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ToArray();

    // Diagnostic: material slots of a mesh with their base material and texture/color parameters.
    public IEnumerable<string> InspectMaterials(ulong meshHash)
    {
        var entry = EntryOrNull(meshHash) ?? throw new FileNotFoundException();
        using var data = ExtractEntry(entry, default);
        using var reader = new CR2WReader(data);
        if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file is null) yield break;
        using (file)
        {
            var mesh = (CMesh)file.RootChunk;
            yield return $"buffers: localBuffer={mesh.LocalMaterialBuffer.Materials.Count} localInstances={mesh.LocalMaterialInstances.Count} preload={mesh.PreloadLocalMaterialInstances.Count} external={mesh.ExternalMaterials.Count} preloadExternal={mesh.PreloadExternalMaterials.Count}";
            yield return $"appearances: {string.Join(", ", mesh.Appearances.Select(a => a.Chunk).OfType<meshMeshAppearance>().Select(a => $"{a.Name}[{string.Join("|", a.ChunkMaterials.Select(m => m.ToString()))}]"))}";
            foreach (var e in mesh.MaterialEntries)
            {
                int i = e.Index;
                if (e.IsLocalInstance)
                {
                    var local = i < mesh.LocalMaterialBuffer.Materials.Count ? mesh.LocalMaterialBuffer.Materials[i] : i < mesh.LocalMaterialInstances.Count ? mesh.LocalMaterialInstances[i].Chunk
                        : i < mesh.PreloadLocalMaterialInstances.Count ? mesh.PreloadLocalMaterialInstances[i].Chunk : null;
                    if (local is CMaterialInstance mi)
                    {
                        yield return $"slot {e.Name} local base={mi.BaseMaterial.DepotPath.GetResolvedText()}";
                        foreach (var p in mi.Values) yield return $"    {p.Key} = {Describe(p.Value)}";
                    }
                    else yield return $"slot {e.Name} local {local?.GetType().Name}";
                }
                else yield return $"slot {e.Name} external {(i < mesh.ExternalMaterials.Count ? mesh.ExternalMaterials[i].DepotPath.GetResolvedText() : "?")}";
            }
        }
    }

    private static string Describe(IRedType? value) => value switch
    {
        IRedRef r => $"ref {r.DepotPath.GetResolvedText() ?? r.DepotPath.GetRedHash().ToString()}",
        CColor c => $"color {c.Red},{c.Green},{c.Blue},{c.Alpha}",
        Vector4 v => $"vec4 {v.X},{v.Y},{v.Z},{v.W}",
        CFloat f => $"float {(float)f}",
        null => "null",
        _ => value.GetType().Name + " " + value
    };
}
