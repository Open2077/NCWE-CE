// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Numerics;
using NCWE.Core;

namespace NCWE.Formats.CustomMeshes;

// What the importer found in a source file, before anything is written to the library.
public sealed record ModelAnalysis(string ModelPath, CustomImportSettings Detected, ImmutableArray<CustomMaterial> Materials,
    ImmutableArray<string> Warnings, int Vertices, int Triangles, int Meshes, bool Skinned, Bounds3 SourceBounds)
{
    // Textures referenced by the materials and where they were found (null: missing).
    public ImmutableDictionary<string, string?> Textures { get; init; } = ImmutableDictionary<string, string?>.Empty;
}

public sealed record ImportedModel(ModelAnalysis Analysis, CustomGeometry Geometry, ImmutableArray<CustomMaterial> Materials, ImmutableArray<string> Warnings)
{
    // Textures stored inside the model file (GLB, FBX): reference -> encoded image (PNG/JPG).
    public ImmutableDictionary<string, byte[]> Embedded { get; init; } = ImmutableDictionary<string, byte[]>.Empty;
}

// Reads a 3D model with Assimp (FBX, glTF/GLB, OBJ, DAE, 3DS, PLY, STL, BLEND...) into custom
// mesh geometry: skinned meshes are posed as in the file, nodes are flattened, everything is
// converted to metres with Y up (view space), turned, pivoted and split so no piece exceeds what
// one game mesh chunk can hold.
public static class ModelImporter
{
    public static readonly string[] Extensions = [".fbx", ".gltf", ".glb", ".obj", ".dae", ".3ds", ".ply", ".stl", ".blend", ".x", ".lwo", ".ms3d", ".off"];
    public const int MaxChunkVertices = 65000;

    private const Assimp.PostProcessSteps Steps = Assimp.PostProcessSteps.Triangulate | Assimp.PostProcessSteps.GenerateSmoothNormals
        | Assimp.PostProcessSteps.CalculateTangentSpace | Assimp.PostProcessSteps.JoinIdenticalVertices | Assimp.PostProcessSteps.SortByPrimitiveType
        | Assimp.PostProcessSteps.FlipUVs | Assimp.PostProcessSteps.LimitBoneWeights | Assimp.PostProcessSteps.FindInvalidData
        | Assimp.PostProcessSteps.ImproveCacheLocality | Assimp.PostProcessSteps.FindDegenerates;

    private static Assimp.Scene Load(string path)
    {
        using var context = new Assimp.AssimpContext();
        // No $AssimpFbx$ helper nodes: one node per FBX model.
        context.SetConfig(new Assimp.Configs.FBXPreservePivotsConfig(false));
        // Points and lines are not drawable.
        context.SetConfig(new Assimp.Configs.SortByPrimitiveTypeConfig(Assimp.PrimitiveType.Point | Assimp.PrimitiveType.Line));
        try { return context.ImportFile(path, Steps); }
        catch (Assimp.AssimpException ex) { throw new InvalidDataException("The model could not be read: " + ex.Message, ex); }
    }

    // Reads the file and reports what an import would produce with the detected settings.
    public static ModelAnalysis Analyze(string path, IReadOnlyList<string> textureRoots)
    {
        var scene = Load(path);
        var warnings = new List<string>();
        var detected = Detect(path, scene);
        var materials = ReadMaterials(scene, path, textureRoots, warnings, out var textures);
        var (posed, skinned) = Pose(scene, warnings);
        int vertices = posed.Sum(p => p.Positions.Length), triangles = posed.Sum(p => p.Indices.Length / 3);
        var bounds = posed.Count == 0 ? default : Bounds3.From(posed.SelectMany(p => p.Positions));
        if (posed.Count == 0) warnings.Add("The file contains no triangle mesh.");
        // Only the materials the meshes use (importers add a default one).
        var usedMaterials = posed.Select(p => p.Material).Where(m => m >= 0 && m < materials.Length).Distinct().Order().Select(m => materials[m]).ToImmutableArray();
        if (usedMaterials.Length > 0) materials = usedMaterials;
        return new(path, detected, materials, warnings.ToImmutableArray(), vertices, triangles, posed.Count, skinned, bounds) { Textures = textures };
    }

    public static ImportedModel Import(string path, CustomImportSettings settings, IReadOnlyList<string> textureRoots)
    {
        var scene = Load(path);
        var warnings = new List<string>();
        var detected = Detect(path, scene);
        var materials = ReadMaterials(scene, path, textureRoots, warnings, out var textures);
        var (posed, skinned) = Pose(scene, warnings);
        if (posed.Count == 0) throw new InvalidDataException("The file contains no triangle mesh.");
        var analysis = new ModelAnalysis(path, detected, materials, warnings.ToImmutableArray(), posed.Sum(p => p.Positions.Length),
            posed.Sum(p => p.Indices.Length / 3), posed.Count, skinned, Bounds3.From(posed.SelectMany(p => p.Positions))) { Textures = textures };

        // Source space -> view space (Y up), metres, turned about the up axis.
        var toView = settings.UpAxis == CustomUpAxis.Z ? new Matrix4x4(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1) : Matrix4x4.Identity;
        toView *= Matrix4x4.CreateScale(settings.Scale) * Matrix4x4.CreateRotationY(settings.Yaw * MathF.PI / 180);
        var converted = posed.Select(p => Transform(p, toView)).ToList();
        var bounds = Bounds3.From(converted.SelectMany(p => p.Positions));
        var offset = settings.Pivot switch
        {
            CustomPivot.BottomCenter => new Vector3(-bounds.Center.X, -bounds.Min.Y, -bounds.Center.Z),
            CustomPivot.Center => -bounds.Center,
            _ => Vector3.Zero
        };
        var submeshes = ImmutableArray.CreateBuilder<CustomSubmesh>();
        foreach (var part in converted)
        {
            var moved = part with { Positions = part.Positions.Select(p => p + offset).ToArray() };
            foreach (var chunk in Split(moved)) submeshes.Add(chunk);
        }
        if (submeshes.Any(s => s.Material < 0 || s.Material >= materials.Length)) throw new InvalidDataException("A mesh refers to a missing material.");
        // Only the materials the pieces use, in their order.
        var usedMaterials = submeshes.Select(s => s.Material).Distinct().Order().ToArray();
        var remap = usedMaterials.Select((m, i) => (m, i)).ToDictionary(x => x.m, x => x.i);
        for (int i = 0; i < submeshes.Count; i++) submeshes[i] = submeshes[i] with { Material = remap[submeshes[i].Material] };
        materials = usedMaterials.Select(m => materials[m]).ToImmutableArray();
        var embedded = ImmutableDictionary.CreateBuilder<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (reference, location) in textures)
            if (location?.StartsWith("embedded:", StringComparison.Ordinal) == true && EmbeddedBytes(scene, reference) is { } bytes) embedded[reference] = bytes;
        return new(analysis, new CustomGeometry(submeshes.ToImmutable()), materials, warnings.ToImmutableArray()) { Embedded = embedded.ToImmutable() };
    }

    // An embedded texture as an encoded image file (raw texels are re-encoded as PNG).
    private static byte[]? EmbeddedBytes(Assimp.Scene scene, string reference)
    {
        var texture = scene.GetEmbeddedTexture(reference);
        if (texture is null && reference.StartsWith('*') && int.TryParse(reference.AsSpan(1), out int i) && i >= 0 && i < scene.TextureCount) texture = scene.Textures[i];
        if (texture is null) return null;
        if (texture.HasCompressedData) return texture.CompressedData;
        if (!texture.HasNonCompressedData) return null;
        var pixels = new byte[texture.Width * texture.Height * 4];
        for (int p = 0; p < texture.NonCompressedData.Length && p * 4 + 3 < pixels.Length; p++)
        {
            var t = texture.NonCompressedData[p];
            pixels[p * 4] = t.R; pixels[p * 4 + 1] = t.G; pixels[p * 4 + 2] = t.B; pixels[p * 4 + 3] = t.A;
        }
        using var image = SixLabors.ImageSharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgba32>(pixels, texture.Width, texture.Height);
        using var stream = new MemoryStream();
        SixLabors.ImageSharp.ImageExtensions.SaveAsPng(image, stream);
        return stream.ToArray();
    }

    // --- Conventions -----------------------------------------------------------------------------------

    private static CustomImportSettings Detect(string path, Assimp.Scene scene)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        float scale = 1; var up = CustomUpAxis.Y;
        if (extension == ".fbx")
        {
            // UnitScaleFactor: centimetres per file unit (1 = cm, 100 = m).
            if (scene.Metadata.TryGetValue("UnitScaleFactor", out var unit) && ToFloat(unit.Data) is float cm and > 0) scale = cm / 100f;
            else scale = .01f;
            if (scene.Metadata.TryGetValue("UpAxis", out var axis) && ToFloat(axis.Data) == 2) up = CustomUpAxis.Z;
        }
        else if (extension is ".3ds" or ".blend" or ".stl") up = CustomUpAxis.Z;
        return new CustomImportSettings { Scale = scale, UpAxis = up };
    }

    private static float? ToFloat(object? value) => value switch
    {
        float f => f, double d => (float)d, int i => i, long l => l, uint u => u, _ => null
    };

    // --- Geometry ---------------------------------------------------------------------------------------

    private sealed record Posed(string Name, int Material, Vector3[] Positions, Vector3[] Normals, Vector4[] Tangents, Vector2[] Uv, uint[] Indices);

    private static Matrix4x4 ToNumerics(Assimp.Matrix4x4 m) => new(m.A1, m.B1, m.C1, m.D1, m.A2, m.B2, m.C2, m.D2, m.A3, m.B3, m.C3, m.D3, m.A4, m.B4, m.C4, m.D4);

    // Every mesh instance in world space: node transforms applied; skinned meshes in the pose
    // the file stores (bones' current transforms times their inverse bind matrices).
    private static (List<Posed> Parts, bool Skinned) Pose(Assimp.Scene scene, List<string> warnings)
    {
        var global = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
        var instances = new List<(int Mesh, Matrix4x4 World)>();
        void Walk(Assimp.Node node, Matrix4x4 parent)
        {
            var world = ToNumerics(node.Transform) * parent;
            global.TryAdd(node.Name ?? "", world);
            foreach (int mesh in node.MeshIndices) instances.Add((mesh, world));
            foreach (var child in node.Children) Walk(child, world);
        }
        Walk(scene.RootNode, Matrix4x4.Identity);
        var parts = new List<Posed>(); bool skinned = false; int unposed = 0;
        foreach (var (index, world) in instances)
        {
            var mesh = scene.Meshes[index];
            if (!mesh.HasFaces || mesh.VertexCount == 0 || mesh.PrimitiveType != Assimp.PrimitiveType.Triangle) continue;
            int n = mesh.VertexCount;
            var source = mesh.Vertices; var normalsIn = mesh.HasNormals ? mesh.Normals : null;
            var tangentsIn = mesh.HasTangentBasis ? mesh.Tangents : null; var bitangentsIn = mesh.HasTangentBasis ? mesh.BiTangents : null;
            var positions = new Vector3[n]; var normals = new Vector3[n]; var tangents = new Vector4[n];
            // Per vertex: the matrix taking it from mesh space to world space.
            Matrix4x4[]? skin = null;
            if (mesh.HasBones)
            {
                skinned = true;
                var accumulated = new Matrix4x4[n]; var weightSum = new float[n];
                foreach (var bone in mesh.Bones)
                {
                    if (!global.TryGetValue(bone.Name ?? "", out var boneWorld)) { unposed++; continue; }
                    var m = ToNumerics(bone.OffsetMatrix) * boneWorld;
                    foreach (var weight in bone.VertexWeights)
                    {
                        if (weight.VertexID < 0 || weight.VertexID >= n || weight.Weight <= 0) continue;
                        accumulated[weight.VertexID] += m * weight.Weight; weightSum[weight.VertexID] += weight.Weight;
                    }
                }
                skin = new Matrix4x4[n];
                for (int v = 0; v < n; v++) skin[v] = weightSum[v] > 1e-4f ? accumulated[v] * (1 / weightSum[v]) : world;
            }
            for (int v = 0; v < n; v++)
            {
                var m = skin?[v] ?? world;
                var p = source[v];
                positions[v] = Vector3.Transform(new Vector3(p.X, p.Y, p.Z), m);
                var normal = normalsIn is null ? Vector3.UnitY : Vector3.TransformNormal(new(normalsIn[v].X, normalsIn[v].Y, normalsIn[v].Z), m);
                normals[v] = SafeNormalize(normal, Vector3.UnitY);
                if (tangentsIn is not null && bitangentsIn is not null)
                {
                    var t = SafeNormalize(Vector3.TransformNormal(new(tangentsIn[v].X, tangentsIn[v].Y, tangentsIn[v].Z), m), Vector3.UnitX);
                    var b = Vector3.TransformNormal(new(bitangentsIn[v].X, bitangentsIn[v].Y, bitangentsIn[v].Z), m);
                    tangents[v] = new(t, Vector3.Dot(Vector3.Cross(normals[v], t), b) < 0 ? -1 : 1);
                }
            }
            var uv = new Vector2[n];
            if (mesh.HasTextureCoords(0)) { var channel = mesh.TextureCoordinateChannels[0]; for (int v = 0; v < n; v++) uv[v] = new(channel[v].X, channel[v].Y); }
            var indices = new List<uint>(mesh.FaceCount * 3);
            foreach (var face in mesh.Faces)
                if (face.IndexCount == 3) { indices.Add((uint)face.Indices[0]); indices.Add((uint)face.Indices[1]); indices.Add((uint)face.Indices[2]); }
            if (tangentsIn is null) ComputeTangents(positions, normals, uv, indices, tangents);
            // A mirrored node (negative scale) turns the faces inside out: flip them back.
            if ((skin is null ? world : skin[0]).GetDeterminant() < 0)
                for (int i = 0; i + 2 < indices.Count; i += 3) (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
            parts.Add(new(mesh.Name ?? $"mesh {index}", mesh.MaterialIndex, positions, normals, tangents, uv, indices.ToArray()));
        }
        if (unposed > 0) warnings.Add($"{unposed} bone reference(s) not found in the scene: those vertices keep their bind pose.");
        if (!parts.Any(p => p.Uv.Any(u => u != Vector2.Zero))) warnings.Add("The model has no texture coordinates: textures cannot be applied.");
        return (parts, skinned);
    }

    private static Posed Transform(Posed part, Matrix4x4 m)
    {
        var normalMatrix = m; normalMatrix.Translation = Vector3.Zero;
        bool mirrored = m.GetDeterminant() < 0;
        var indices = part.Indices;
        if (mirrored) { indices = (uint[])indices.Clone(); for (int i = 0; i + 2 < indices.Length; i += 3) (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]); }
        return part with
        {
            Positions = part.Positions.Select(p => Vector3.Transform(p, m)).ToArray(),
            Normals = part.Normals.Select(n => SafeNormalize(Vector3.TransformNormal(n, normalMatrix), Vector3.UnitY)).ToArray(),
            Tangents = part.Tangents.Select(t => new Vector4(SafeNormalize(Vector3.TransformNormal(new(t.X, t.Y, t.Z), normalMatrix), Vector3.UnitX), mirrored ? -t.W : t.W)).ToArray(),
            Indices = indices
        };
    }

    // Pieces of at most MaxChunkVertices vertices (16-bit indices in the game's mesh chunks).
    private static IEnumerable<CustomSubmesh> Split(Posed part)
    {
        if (part.Positions.Length <= MaxChunkVertices) { yield return new(part.Material, part.Positions, part.Normals, part.Tangents, part.Uv, part.Indices); yield break; }
        var remap = new Dictionary<uint, uint>();
        var p = new List<Vector3>(); var n = new List<Vector3>(); var t = new List<Vector4>(); var uv = new List<Vector2>(); var ix = new List<uint>();
        for (int i = 0; i + 2 < part.Indices.Length; i += 3)
        {
            if (p.Count + 3 > MaxChunkVertices)
            {
                yield return new(part.Material, p.ToArray(), n.ToArray(), t.ToArray(), uv.ToArray(), ix.ToArray());
                remap.Clear(); p.Clear(); n.Clear(); t.Clear(); uv.Clear(); ix.Clear();
            }
            for (int k = 0; k < 3; k++)
            {
                uint source = part.Indices[i + k];
                if (!remap.TryGetValue(source, out uint mapped))
                {
                    mapped = (uint)p.Count; remap[source] = mapped;
                    p.Add(part.Positions[source]); n.Add(part.Normals[source]); t.Add(part.Tangents[source]); uv.Add(part.Uv[source]);
                }
                ix.Add(mapped);
            }
        }
        if (ix.Count > 0) yield return new(part.Material, p.ToArray(), n.ToArray(), t.ToArray(), uv.ToArray(), ix.ToArray());
    }

    private static Vector3 SafeNormalize(Vector3 v, Vector3 fallback) => v.LengthSquared() is > 1e-20f and var l && float.IsFinite(l) ? Vector3.Normalize(v) : fallback;

    // Per-triangle UV tangents accumulated per vertex (Lengyel), orthogonalised against the normal.
    internal static void ComputeTangents(Vector3[] positions, Vector3[] normals, Vector2[] uv, IReadOnlyList<uint> indices, Vector4[] tangents)
    {
        var tan = new Vector3[positions.Length]; var bit = new Vector3[positions.Length];
        for (int i = 0; i + 2 < indices.Count; i += 3)
        {
            uint a = indices[i], b = indices[i + 1], c = indices[i + 2];
            var e1 = positions[b] - positions[a]; var e2 = positions[c] - positions[a];
            var d1 = uv[b] - uv[a]; var d2 = uv[c] - uv[a];
            float r = d1.X * d2.Y - d2.X * d1.Y;
            if (MathF.Abs(r) < 1e-12f) continue;
            r = 1 / r;
            var sdir = (e1 * d2.Y - e2 * d1.Y) * r; var tdir = (e2 * d1.X - e1 * d2.X) * r;
            tan[a] += sdir; tan[b] += sdir; tan[c] += sdir; bit[a] += tdir; bit[b] += tdir; bit[c] += tdir;
        }
        for (int v = 0; v < positions.Length; v++)
        {
            var n = normals[v];
            var t = tan[v] - n * Vector3.Dot(n, tan[v]);
            if (t.LengthSquared() < 1e-12f) t = MathF.Abs(n.X) < .9f ? Vector3.Cross(n, Vector3.UnitX) : Vector3.Cross(n, Vector3.UnitY);
            t = Vector3.Normalize(t);
            tangents[v] = new(t, Vector3.Dot(Vector3.Cross(n, t), bit[v]) < 0 ? -1 : 1);
        }
    }

    // --- Materials --------------------------------------------------------------------------------------

    private static ImmutableArray<CustomMaterial> ReadMaterials(Assimp.Scene scene, string modelPath, IReadOnlyList<string> roots, List<string> warnings,
        out ImmutableDictionary<string, string?> textures)
    {
        var found = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        bool gltf = Path.GetExtension(modelPath).ToLowerInvariant() is ".gltf" or ".glb";
        var doubleSided = gltf ? GltfDoubleSided(modelPath) : new HashSet<string>();
        var result = ImmutableArray.CreateBuilder<CustomMaterial>(scene.MaterialCount);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < scene.MaterialCount; i++)
        {
            var m = scene.Materials[i];
            string name = string.IsNullOrWhiteSpace(m.Name) ? $"Material {i + 1}" : m.Name.Trim();
            for (int k = 2; !names.Add(name); k++) name = $"{m.Name} {k}";
            string? Texture(Assimp.TextureType type)
            {
                if (!m.GetMaterialTexture(type, 0, out var slot) || string.IsNullOrWhiteSpace(slot.FilePath)) return null;
                string reference = slot.FilePath;
                if (!found.ContainsKey(reference)) found[reference] = Locate(reference, scene, modelPath, roots);
                return reference;
            }
            var properties = m.GetAllProperties();
            float[]? Floats(string key) => properties.FirstOrDefault(p => p.Name == key && p.PropertyType == Assimp.PropertyType.Float)?.GetFloatArrayValue();
            string? Text(string key) => properties.FirstOrDefault(p => p.Name == key && p.PropertyType == Assimp.PropertyType.String)?.GetStringValue();

            string? color = Texture(Assimp.TextureType.Diffuse) ?? Texture(Assimp.TextureType.BaseColor);
            string? normal = Texture(Assimp.TextureType.Normals) ?? Texture(Assimp.TextureType.NormalCamera) ?? (gltf ? null : Texture(Assimp.TextureType.Height));
            string? emissive = Texture(Assimp.TextureType.Emissive) ?? Texture(Assimp.TextureType.EmissionColor);
            string? opacity = Texture(Assimp.TextureType.Opacity);
            // glTF metallic-roughness texture (Assimp 5.0 reports it as "unknown"): G roughness, B metalness.
            string? metalRough = gltf ? Texture(Assimp.TextureType.Unknown) : null;
            string? roughness = Texture(Assimp.TextureType.Roughness) ?? metalRough;
            string? metalness = Texture(Assimp.TextureType.Metalness) ?? metalRough;
            string? gloss = roughness is null ? Texture(Assimp.TextureType.Shininess) : null;

            var baseColor = Floats("$mat.gltf.pbrMetallicRoughness.baseColorFactor") is { Length: >= 4 } factor ? new Vector4(factor[0], factor[1], factor[2], factor[3])
                : m.HasColorDiffuse ? new Vector4(m.ColorDiffuse.R, m.ColorDiffuse.G, m.ColorDiffuse.B, m.HasOpacity ? m.Opacity : 1) : Vector4.One;
            // FBX/OBJ: the diffuse colour of a textured material is usually a viewport tint; keep the texture as it is.
            if (!gltf && color is not null) baseColor = new(1, 1, 1, baseColor.W);
            float metallic = Floats("$mat.gltf.pbrMetallicRoughness.metallicFactor") is { Length: > 0 } mf ? mf[0] : 0;
            float rough = Floats("$mat.gltf.pbrMetallicRoughness.roughnessFactor") is { Length: > 0 } rf ? rf[0]
                : m.HasShininess && m.Shininess > 0 ? Math.Clamp(1 - MathF.Sqrt(m.Shininess / 1000f), .05f, 1) : .6f;
            var emissiveColor = m.HasColorEmissive ? new Vector3(m.ColorEmissive.R, m.ColorEmissive.G, m.ColorEmissive.B) : Vector3.Zero;
            string alphaMode = Text("$mat.gltf.alphaMode") ?? (opacity is not null || baseColor.W < .99f ? "MASK" : "OPAQUE");
            float cutoff = Floats("$mat.gltf.alphaCutoff") is { Length: > 0 } cf ? cf[0] : .5f;
            bool glass = name.Contains("glass", StringComparison.OrdinalIgnoreCase) && color is null;

            result.Add(new CustomMaterial(name)
            {
                Kind = glass ? CustomMaterialKind.Glass : CustomMaterialKind.Standard,
                BaseColor = baseColor, BaseColorTexture = color,
                Roughness = rough, RoughnessTexture = roughness ?? gloss, RoughnessChannel = metalRough is not null && roughness == metalRough ? 1 : 0, InvertRoughness = gloss is not null,
                Metalness = metallic, MetalnessTexture = metalness, MetalnessChannel = metalRough is not null && metalness == metalRough ? 2 : 0,
                NormalTexture = normal,
                EmissiveColor = emissiveColor, EmissiveEV = emissiveColor.LengthSquared() > 1e-4f || emissive is not null ? 4 : 0, EmissiveTexture = emissive,
                AlphaCutout = alphaMode != "OPAQUE", AlphaThreshold = cutoff,
                DoubleSided = m.HasTwoSided && m.IsTwoSided || doubleSided.Contains(m.Name ?? "")
            });
        }
        if (result.Count == 0) result.Add(new CustomMaterial("Default"));
        foreach (var (reference, location) in found.Where(f => f.Value is null)) warnings.Add($"Texture not found: {Path.GetFileName(reference.Replace('\\', '/'))}");
        textures = found.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
        return result.ToImmutable();
    }

    // Names of the glTF materials marked "doubleSided" (Assimp 5.0 drops the flag). Assimp only
    // keeps the materials meshes use, in the order they are met, so they are matched by name
    // (an unnamed one is "materials_<index>" there). Empty when the JSON cannot be read.
    private static HashSet<string> GltfDoubleSided(string path)
    {
        try
        {
            byte[] json;
            if (Path.GetExtension(path).Equals(".glb", StringComparison.OrdinalIgnoreCase))
            {
                using var reader = new BinaryReader(File.OpenRead(path));
                if (reader.ReadUInt32() != 0x46546C67) return [];    // "glTF"
                reader.ReadUInt64();                                 // version, total length
                int length = reader.ReadInt32();
                if (reader.ReadUInt32() != 0x4E4F534A || length is <= 0 or > 64 << 20) return [];   // "JSON" chunk
                json = reader.ReadBytes(length);
            }
            else
            {
                if (new FileInfo(path).Length > 256L << 20) return [];
                json = File.ReadAllBytes(path);
            }
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (!document.RootElement.TryGetProperty("materials", out var materials) || materials.ValueKind != System.Text.Json.JsonValueKind.Array) return names;
            int index = 0;
            foreach (var m in materials.EnumerateArray())
            {
                if (m.ValueKind == System.Text.Json.JsonValueKind.Object && m.TryGetProperty("doubleSided", out var flag) && flag.ValueKind == System.Text.Json.JsonValueKind.True)
                    names.Add(m.TryGetProperty("name", out var name) && name.ValueKind == System.Text.Json.JsonValueKind.String ? name.GetString()! : $"materials_{index}");
                index++;
            }
            return names;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return []; }
    }

    // A texture reference made usable: embedded ("*0") or a file found as given, beside the
    // model, or by its file name anywhere under the given folders (authors' absolute paths).
    private static string? Locate(string reference, Assimp.Scene scene, string modelPath, IReadOnlyList<string> roots)
    {
        if (reference.StartsWith('*') || scene.GetEmbeddedTexture(reference) is not null) return "embedded:" + reference;
        string normalized = reference.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        string directory = Path.GetDirectoryName(Path.GetFullPath(modelPath))!;
        try
        {
            if (Path.IsPathRooted(normalized) && File.Exists(normalized)) return normalized;
            var beside = Path.GetFullPath(Path.Combine(directory, normalized));
            if (File.Exists(beside)) return beside;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        string name = Path.GetFileName(normalized);
        if (name.Length == 0) return null;
        string stem = Path.GetFileNameWithoutExtension(name);
        foreach (var root in roots.Append(directory).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Take(20000).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (files.FirstOrDefault(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase)) is { } exact) return exact;
            // Same name, other image format (a .tga reference shipped as .png).
            if (files.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(stem, StringComparison.OrdinalIgnoreCase) && TextureFiles.IsImage(f)) is { } other) return other;
        }
        return null;
    }
}
