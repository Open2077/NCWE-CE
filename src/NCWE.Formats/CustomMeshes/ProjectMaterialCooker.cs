// SPDX-License-Identifier: GPL-3.0-only
using NCWE.Core;

namespace NCWE.Formats.CustomMeshes;

// The project's materials as game resources (MaterialPaths): a .mi per material, its own
// textures as .xbm beside it (read from the material store, prepared as the material uses them).
public static class ProjectMaterialCooker
{
    public const int MaxTextureSize = 2048;

    public static IReadOnlyList<CookedFile> Cook(Guid project, ProjectMaterial material, MaterialStore? store, CancellationToken cancellation = default)
    {
        var files = new List<CookedFile>();
        foreach (var texture in MaterialInstances.OwnTextures(material.Material))
        {
            cancellation.ThrowIfCancellationRequested();
            string source = store?.TexturePath(texture.File)
                ?? throw new FileNotFoundException($"Material \"{material.Name}\": its texture {texture.File} is not in this computer's material store (Materials\\textures).");
            var image = texture.Prepare(TextureFiles.Fit(TextureFiles.Load(source), MaxTextureSize));
            files.Add(new(MaterialPaths.Texture(project, material, texture.Channel), XbmWriter.Write(image, texture.Kind(image))));
        }
        var instance = MaterialInstances.Build(material.Material, (channel, _) => MaterialPaths.Texture(project, material, channel));
        files.Insert(0, new(MaterialPaths.Instance(project, material), CustomCr2w.Write(instance)));
        return files;
    }
}
