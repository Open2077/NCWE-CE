// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;

namespace NCWE.Core;

// Several meshes placed relative to one origin, as one mesh (entity previews and thumbnails):
// each source keeps its best LOD only, vertices are transformed, surfaces and part colors kept.
public static class MeshMerge
{
    public static MeshAsset Merge(string resourcePath, IEnumerable<(MeshAsset Mesh, PlacementTransform Local, string Appearance)> sources)
    {
        var parts = new List<MeshPart>(); var textures = new List<SurfaceTexture?>(); var colors = new List<Vector4>();
        bool textured = false;
        foreach (var (mesh, local, appearance) in sources)
        {
            var matrix = local.ViewMatrix;
            Matrix4x4.Invert(matrix, out var inverse); var normalMatrix = Matrix4x4.Transpose(inverse);
            var appearanceColors = mesh.AppearanceColors is { } table ? table.GetValueOrDefault(appearance) ?? table.GetValueOrDefault("default") : null;
            uint best = mesh.Parts.Length == 0 ? 1 : mesh.Parts.Min(p => p.LodMask);
            for (int i = 0; i < mesh.Parts.Length; i++)
            {
                var part = mesh.Parts[i];
                if (part.LodMask != best) continue;
                var vertices = new MeshVertex[part.Vertices.Length];
                for (int v = 0; v < vertices.Length; v++)
                {
                    var n = Vector3.TransformNormal(part.Vertices[v].Normal, normalMatrix);
                    vertices[v] = new(Vector3.Transform(part.Vertices[v].Position, matrix), n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitY);
                }
                parts.Add(part with { Vertices = vertices, LodMask = 1 });
                var texture = mesh.PartTextures is { } surfaces && i < surfaces.Count ? surfaces[i] : null;
                textures.Add(texture); textured |= texture is not null;
                colors.Add(appearanceColors is not null && i < appearanceColors.Length ? appearanceColors[i] : default);
            }
        }
        if (parts.Count == 0) throw new InvalidDataException("Nothing to merge: the entity has no displayable mesh.");
        return new MeshAsset(resourcePath, "merged:" + resourcePath + ":" + parts.Count, parts.ToArray(),
            Bounds3.From(parts.SelectMany(p => p.Vertices.Select(v => v.Position))), "", 0)
        {
            PartTextures = textured ? textures : null,
            AppearanceColors = new Dictionary<string, Vector4[]> { ["default"] = colors.ToArray() }
        };
    }
}
