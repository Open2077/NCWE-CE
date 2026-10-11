// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using NCWE.Core;
using SharpGLTF.Schema2;
using WolvenKit.Common;
using WolvenKit.Common.Model.Arguments;
using WolvenKit.Common.Services;
using WolvenKit.Core.Interfaces;
using WolvenKit.Modkit.RED4;
using WolvenKit.Modkit.RED4.Tools;
using WolvenKit.RED4.CR2W;
using WolvenKit.RED4.Types;
using RedVector4 = WolvenKit.RED4.Types.Vector4;
using RedVector3 = WolvenKit.RED4.Types.Vector3;
using Vector3 = System.Numerics.Vector3;

namespace NCWE.Formats.CustomMeshes;

public sealed record CookedFile(string Path, byte[] Bytes);

// Turns a library mesh into game resources: its .mesh (geometry written by WolvenKit's glTF
// importer into a cleaned static mesh of the game, materials as local instances) and one .xbm
// per texture its materials use.
public static class CustomMeshCooker
{
    // Bumped whenever the cooked output changes for the same library version (2: textures stored
    // upside down like the game's; 3: cut-out materials are masked instances), so cached files are
    // cooked again.
    public const int Format = 3;
    public const string StandardTemplate = @"engine\materials\metal_base.remt";
    public const string GlassTemplate = @"base\materials\glass.mt";
    public const string MultilayerTemplate = @"engine\materials\multilayered.mt";
    public const string WhiteTexture = @"engine\textures\editor\white.xbm";
    // Static meshes of the game whose header and defaults the generated mesh starts from.
    private static readonly string[] MeshTemplates =
    [
        @"base\environment\decoration\lighting\residential\poor_desk_lamp\poor_desk_lamp_f.mesh",
        @"base\environment\decoration\misc\paper\magazine\magazine_a.mesh"
    ];

    public static IReadOnlyList<CookedFile> Cook(CustomMeshInfo info, CustomGeometry geometry, string textureFolder, GameArchiveIndex index,
        string workDirectory, IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        var files = new List<CookedFile>();
        // Textures first: their depot paths go into the materials.
        var textures = new Dictionary<(int Material, char Channel), string>();
        for (int i = 0; i < info.Materials.Length; i++)
        {
            var m = info.Materials[i];
            if (m.Kind is CustomMaterialKind.Game or CustomMaterialKind.Multilayer) continue;
            void Add(char channel, string? file, Func<RgbaImage, RgbaImage> prepare, Func<RgbaImage, TextureKind> kind)
            {
                if (file is null) return;
                cancellation.ThrowIfCancellationRequested();
                string full = Path.Combine(textureFolder, file);
                if (!File.Exists(full)) return;
                progress?.Report($"Texture {m.Name} ({channel})…");
                var image = prepare(TextureFiles.Fit(TextureFiles.Load(full), info.Import.MaxTextureSize));
                string path = CustomMeshPaths.Texture(info.Id, i, channel);
                files.Add(new(path, XbmWriter.Write(image, kind(image))));
                textures[(i, channel)] = path;
            }
            Add('d', m.BaseColorTexture, x => x, x => m.AlphaCutout && x.HasAlpha() ? TextureKind.ColorAlpha : TextureKind.Color);
            Add('n', m.NormalTexture, x => m.FlipNormalGreen ? TextureFiles.FlipGreen(x) : x, _ => TextureKind.Normal);
            if (m.Kind == CustomMaterialKind.Standard)
            {
                Add('r', m.RoughnessTexture, x => TextureFiles.Channel(x, m.RoughnessChannel, m.InvertRoughness), _ => TextureKind.Gray);
                Add('m', m.MetalnessTexture, x => TextureFiles.Channel(x, m.MetalnessChannel, false), _ => TextureKind.Gray);
                if (m.EmissiveEV > 0) Add('e', m.EmissiveTexture, x => x, _ => TextureKind.Color);
            }
            // An Instance material's own textures (parameters naming a file).
            if (m.Kind == CustomMaterialKind.Instance)
                foreach (var p in m.Parameters.Where(p => p.File is not null))
                    Add(ParameterChannel(m, p.Name), p.File, x => x, x => MaterialInstances.TextureKindFor(p.Name, x));
        }
        progress?.Report("Mesh…");
        files.Insert(0, new(info.MeshPath, BuildMesh(info, geometry, index, workDirectory, textures)));
        return files;
    }

    // --- Mesh --------------------------------------------------------------------------------------------

    private static byte[] BuildMesh(CustomMeshInfo info, CustomGeometry geometry, GameArchiveIndex index, string work, IReadOnlyDictionary<(int, char), string> textures)
    {
        Directory.CreateDirectory(work);
        string glb = Path.Combine(work, info.Id + ".glb");
        WriteGlb(geometry, glb);

        // A cleaned static mesh of the game: valid header and defaults, nothing of its own.
        var template = Template(index);
        var blob = (rendRenderMeshBlob)template.RenderResourceBlob.Chunk!;
        template.Parameters.Clear(); template.MaterialEntries.Clear(); template.ExternalMaterials.Clear();
        template.LocalMaterialInstances.Clear(); template.LocalMaterialBuffer = new meshMeshMaterialBuffer();
        template.PreloadExternalMaterials.Clear(); template.PreloadLocalMaterialInstances.Clear(); template.InplaceResources.Clear();
        template.Appearances.Clear(); template.FloatTrackNames.Clear(); template.BoneNames.Clear(); template.BoneRigMatrices.Clear();
        template.BoneVertexEpsilons.Clear(); template.LodBoneMask.Clear();
        template.LodLevelInfo = new CArray<CFloat> { 0 };
        template.ObjectType = Enums.ERenderObjectType.ROT_Static;
        blob.Header.BonePositions.Clear(); blob.Header.SpeedTreeWind.Clear(); blob.Header.OpacityMicromaps.Clear(); blob.Header.CustomData.Clear();
        blob.Header.TopologyData.Clear(); blob.Header.TopologyMetadata.Clear(); blob.Header.Topology.Clear();
        blob.Header.RenderLODs = new CArray<CFloat> { 0 };
        // WolvenKit's importer takes each chunk's render flags from these (in submesh order).
        blob.Header.RenderChunkInfos.Clear();
        foreach (var submesh in geometry.Submeshes)
        {
            var mask = Enums.EMeshChunkFlags.MCF_RenderInScene | Enums.EMeshChunkFlags.MCF_RenderInShadows;
            if (info.Materials.ElementAtOrDefault(submesh.Material)?.DoubleSided == true) mask |= Enums.EMeshChunkFlags.MCF_IsTwoSided;
            blob.Header.RenderChunkInfos.Add(new rendChunk { RenderMask = mask, LodMask = 1 });
        }
        using var stream = new MemoryStream();   // expandable: WolvenKit rewrites it in place
        stream.Write(CustomCr2w.Write(template)); stream.Position = 0;

        var logger = new CookLogger();
        var hashes = new HashService(false); var hooks = new HookService();
        var tools = new ModTools(logger, null!, hashes, new Red4ParserService(hashes, logger, hooks), null!, hooks);
        var args = new GltfImportArgs { ImportMaterials = false, OverrideMeshNameWithNodeName = true, FillEmpty = false, ImportGarmentSupport = false };
        if (!tools.ImportMesh(new FileInfo(glb), stream, args))
            throw new InvalidDataException("WolvenKit could not write the mesh geometry." + (logger.Errors.Count > 0 ? " " + string.Join(" ", logger.Errors) : ""));

        using var imported = CustomCr2w.Read(stream.ToArray());
        if (imported.RootChunk is not CMesh mesh || mesh.RenderResourceBlob.Chunk is not rendRenderMeshBlob written)
            throw new InvalidDataException("The generated mesh has no render data.");
        if (written.Header.RenderChunkInfos.Count != geometry.Submeshes.Length)
            throw new InvalidDataException($"The generated mesh has {written.Header.RenderChunkInfos.Count} chunks for {geometry.Submeshes.Length} pieces.");
        ApplyMaterials(mesh, info, geometry, textures);
        mesh.SurfaceAreaPerAxis = SurfaceArea(geometry);
        // Its own geometry identity (the template's would make it pass for that game mesh).
        mesh.GeometryHash = CustomMeshInfo.Fnv1a64($"{info.MeshPath}:{info.Version}:{geometry.Submeshes.Sum(s => s.Positions.Length)}");
        var bytes = CustomCr2w.Write(mesh);
        Verify(bytes, geometry);
        return bytes;
    }

    private static CMesh Template(GameArchiveIndex index)
    {
        foreach (var path in MeshTemplates.Concat(index.PathsWithExtension(".mesh").Where(p => p.Contains(@"\decoration\", StringComparison.OrdinalIgnoreCase)).Take(200)))
        {
            var file = index.ReadFile(path);
            if (file?.RootChunk is CMesh { RenderResourceBlob.Chunk: rendRenderMeshBlob } mesh && mesh.BoneNames.Count == 0
                && mesh.ObjectType == Enums.ERenderObjectType.ROT_Static) return mesh;
            file?.Dispose();
        }
        throw new InvalidOperationException("No static game mesh found to start the custom mesh from (open the game first).");
    }

    // glTF (Y up) as WolvenKit's importer reads it: one node and one mesh per piece, named so
    // that its alphabetical order is the pieces' order.
    internal static void WriteGlb(CustomGeometry geometry, string path)
    {
        var model = ModelRoot.CreateModel();
        var scene = model.UseScene("scene");
        for (int i = 0; i < geometry.Submeshes.Length; i++)
        {
            var s = geometry.Submeshes[i];
            string name = $"piece_{i:0000}";
            var mesh = model.CreateMesh(name);
            mesh.CreatePrimitive()
                .WithVertexAccessor("POSITION", s.Positions)
                .WithVertexAccessor("NORMAL", s.Normals)
                .WithVertexAccessor("TANGENT", s.Tangents)
                .WithVertexAccessor("TEXCOORD_0", s.Uv)
                .WithIndicesAccessor(PrimitiveType.TRIANGLES, s.Indices.Select(x => (int)x).ToArray());
            scene.CreateNode(name).Mesh = mesh;
        }
        model.SaveGLB(path);
    }

    // --- Materials -------------------------------------------------------------------------------------

    internal static string SlotName(CustomMaterial m, int index)
    {
        var chars = m.Name.ToLowerInvariant().Select(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '_').ToArray();
        string stem = new string(chars).Trim('_');
        return $"m{index:00}" + (stem.Length > 0 ? "_" + (stem.Length > 32 ? stem[..32] : stem) : "");
    }

    private static void ApplyMaterials(CMesh mesh, CustomMeshInfo info, CustomGeometry geometry, IReadOnlyDictionary<(int, char), string> textures)
    {
        mesh.MaterialEntries.Clear(); mesh.ExternalMaterials.Clear(); mesh.LocalMaterialInstances.Clear();
        mesh.LocalMaterialBuffer = new meshMeshMaterialBuffer(); mesh.Appearances.Clear();
        var names = new string[Math.Max(1, info.Materials.Length)];
        for (int i = 0; i < names.Length; i++)
        {
            var m = i < info.Materials.Length ? info.Materials[i] : new CustomMaterial("Default");
            names[i] = SlotName(m, i);
            if (m.Kind == CustomMaterialKind.Game && !string.IsNullOrWhiteSpace(m.GameMaterial))
            {
                mesh.MaterialEntries.Add(new CMeshMaterialEntry { Name = names[i], Index = (ushort)mesh.ExternalMaterials.Count, IsLocalInstance = false });
                mesh.ExternalMaterials.Add(new CResourceAsyncReference<IMaterial>(m.GameMaterial));
                continue;
            }
            mesh.MaterialEntries.Add(new CMeshMaterialEntry { Name = names[i], Index = (ushort)mesh.LocalMaterialBuffer.Materials.Count, IsLocalInstance = true });
            mesh.LocalMaterialBuffer.Materials.Add(m.Kind switch
            {
                CustomMaterialKind.Glass => Glass(m, i, textures),
                CustomMaterialKind.Multilayer when !string.IsNullOrWhiteSpace(m.MultilayerSetup) && !string.IsNullOrWhiteSpace(m.MultilayerMask) => Multilayer(m),
                CustomMaterialKind.Instance when !string.IsNullOrWhiteSpace(m.BaseMaterial) => MaterialInstances.FromParameters(m, (parameter, file) => textures.GetValueOrDefault((i, ParameterChannel(m, parameter))) ?? WhiteTexture),
                _ => Standard(m, i, textures)
            });
        }
        var appearance = new meshMeshAppearance { Name = "default" };
        foreach (var submesh in geometry.Submeshes) appearance.ChunkMaterials.Add(names[Math.Clamp(submesh.Material, 0, names.Length - 1)]);
        mesh.Appearances.Add(new CHandle<meshMeshAppearance>(appearance));
    }

    // The texture channel of an Instance material's parameter (its index among the material's file textures).
    internal static char ParameterChannel(CustomMaterial m, string parameter) => (char)('A' + Math.Max(0, m.Parameters.Where(p => p.File is not null).Select(p => p.Name).ToList().IndexOf(parameter)));

    internal static CMaterialInstance Instance(string template) => new()
    {
        BaseMaterial = new CResourceReference<IMaterial>(template), Values = new CArray<CKeyValuePair>(), ResourceVersion = 4
    };

    private static void Set(CMaterialInstance instance, string key, IRedType value) => instance.Values.Add(new CKeyValuePair(key, value));
    private static CResourceReference<ITexture> Texture(string path) => new(path);
    private static float Linear(float srgb) => srgb <= .04045f ? srgb / 12.92f : MathF.Pow((srgb + .055f) / 1.055f, 2.4f);
    private static RedVector4 V4(float x, float y, float z, float w) => new() { X = x, Y = y, Z = z, W = w };
    private static CColor Color(Vector3 c, float a = 1) => new()
    {
        Red = (byte)Math.Clamp(c.X * 255, 0, 255), Green = (byte)Math.Clamp(c.Y * 255, 0, 255), Blue = (byte)Math.Clamp(c.Z * 255, 0, 255), Alpha = (byte)Math.Clamp(a * 255, 0, 255)
    };

    // engine\materials\metal_base.remt: colour × scale, roughness and metalness = texture × scale + bias.
    internal static CMaterialInstance Standard(CustomMaterial m, int i, IReadOnlyDictionary<(int, char), string> textures)
    {
        var instance = Instance(StandardTemplate);
        Set(instance, "BaseColor", Texture(textures.TryGetValue((i, 'd'), out var color) ? color : WhiteTexture));
        Set(instance, "BaseColorScale", V4(Linear(m.BaseColor.X), Linear(m.BaseColor.Y), Linear(m.BaseColor.Z), m.BaseColor.W));
        if (textures.TryGetValue((i, 'r'), out var roughness)) { Set(instance, "Roughness", Texture(roughness)); Set(instance, "RoughnessScale", (CFloat)1f); Set(instance, "RoughnessBias", (CFloat)0f); }
        else { Set(instance, "RoughnessScale", (CFloat)0f); Set(instance, "RoughnessBias", (CFloat)Math.Clamp(m.Roughness, 0, 1)); }
        if (textures.TryGetValue((i, 'm'), out var metalness)) { Set(instance, "Metalness", Texture(metalness)); Set(instance, "MetalnessScale", (CFloat)1f); Set(instance, "MetalnessBias", (CFloat)0f); }
        else { Set(instance, "MetalnessScale", (CFloat)0f); Set(instance, "MetalnessBias", (CFloat)Math.Clamp(m.Metalness, 0, 1)); }
        if (textures.TryGetValue((i, 'n'), out var normal)) { Set(instance, "Normal", Texture(normal)); Set(instance, "NormalStrength", (CFloat)m.NormalStrength); }
        // The game tests alpha on masked instances only: without the flag the threshold is ignored.
        if (m.AlphaCutout) { instance.EnableMask = true; Set(instance, "AlphaThreshold", (CFloat)Math.Clamp(m.AlphaThreshold, 0, 1)); }
        if (m.EmissiveEV > 0)
        {
            Set(instance, "Emissive", Texture(textures.TryGetValue((i, 'e'), out var emissive) ? emissive : WhiteTexture));
            Set(instance, "EmissiveColor", Color(m.EmissiveColor.LengthSquared() > 0 ? m.EmissiveColor : Vector3.One));
            Set(instance, "EmissiveEV", (CFloat)m.EmissiveEV);
        }
        return instance;
    }

    // engine\materials\multilayered.mt with a game setup and mask, as the game's road pieces wear them.
    internal static CMaterialInstance Multilayer(CustomMaterial m)
    {
        var instance = Instance(MultilayerTemplate);
        Set(instance, "MultilayerSetup", new CResourceReference<Multilayer_Setup>(m.MultilayerSetup!));
        Set(instance, "MultilayerMask", new CResourceReference<Multilayer_Mask>(m.MultilayerMask!));
        return instance;
    }

    internal static CMaterialInstance Glass(CustomMaterial m, int i, IReadOnlyDictionary<(int, char), string> textures)
    {
        var instance = Instance(GlassTemplate);
        Set(instance, "TintColor", Color(new(m.BaseColor.X, m.BaseColor.Y, m.BaseColor.Z)));
        Set(instance, "Opacity", (CFloat)Math.Clamp(m.GlassOpacity, 0, 1));
        if (textures.TryGetValue((i, 'n'), out var normal)) { Set(instance, "Normal", Texture(normal)); Set(instance, "NormalStrength", (CFloat)m.NormalStrength); }
        return instance;
    }

    // Area facing each game axis (Z up), as the game's meshes record it.
    private static RedVector3 SurfaceArea(CustomGeometry geometry)
    {
        double x = 0, y = 0, z = 0;
        foreach (var s in geometry.Submeshes)
            for (int i = 0; i + 2 < s.Indices.Length; i += 3)
            {
                var a = s.Positions[s.Indices[i]]; var cross = Vector3.Cross(s.Positions[s.Indices[i + 1]] - a, s.Positions[s.Indices[i + 2]] - a) * .5f;
                x += MathF.Abs(cross.X); y += MathF.Abs(cross.Z); z += MathF.Abs(cross.Y);   // view (x, y up, -north) -> game
            }
        return new RedVector3 { X = (float)x, Y = (float)y, Z = (float)z };
    }

    // Read back: same pieces and vertices as the library geometry, and its triangles. WolvenKit's
    // importer drops a handful of triangles from some dense scanned or decal meshes (seen: 9 of
    // 21,000 in a piece, every vertex kept, none degenerate in float or on the 16-bit grid); up to
    // MaxDroppedTriangles of a piece is accepted, more means real geometry loss.
    public const double MaxDroppedTriangles = 0.005;

    private static void Verify(byte[] bytes, CustomGeometry geometry)
    {
        using var file = CustomCr2w.Read(bytes);
        if (file.RootChunk is not CMesh { RenderResourceBlob.Chunk: rendRenderMeshBlob blob } mesh) throw new InvalidDataException("The generated mesh cannot be read back.");
        var info = MeshTools.GetMeshesinfo(blob, mesh);
        using var buffer = new MemoryStream(blob.RenderBuffer.Buffer.GetBytes());
        var raw = MeshTools.ContainRawMesh(buffer, info, false);
        if (raw.Count != geometry.Submeshes.Length) throw new InvalidDataException($"The generated mesh holds {raw.Count} pieces instead of {geometry.Submeshes.Length}.");
        for (int i = 0; i < raw.Count; i++)
        {
            int vertices = raw[i].positions?.Length ?? 0, indices = raw[i].indices?.Length ?? 0;
            int expectedVertices = geometry.Submeshes[i].Positions.Length, expectedIndices = geometry.Submeshes[i].Indices.Length;
            if (!PieceIntact(vertices, indices, expectedVertices, expectedIndices))
                throw new InvalidDataException($"Piece {i} of the generated mesh lost geometry ({vertices} vertices and {indices} indices written, {expectedVertices} and {expectedIndices} expected).");
        }
        var appearance = mesh.Appearances.FirstOrDefault()?.Chunk as meshMeshAppearance;
        if (appearance is null || appearance.ChunkMaterials.Count != raw.Count) throw new InvalidDataException("The generated mesh's appearance does not cover every piece.");
    }

    // Every vertex, whole triangles, and at most MaxDroppedTriangles of the triangles gone (or, on
    // a small piece, a few triangles while it keeps 98% of them).
    public static bool PieceIntact(int vertices, int indices, int expectedVertices, int expectedIndices)
    {
        if (vertices != expectedVertices || indices % 3 != 0 || indices > expectedIndices) return false;
        double dropped = (expectedIndices - indices) / 3.0, triangles = expectedIndices / 3.0;
        return dropped <= Math.Max(triangles * MaxDroppedTriangles, Math.Min(4, triangles * .02));
    }

    private sealed class CookLogger : ILoggerService
    {
        public List<string> Errors { get; } = [];
        public LoggerVerbosity LoggerVerbosity { get; private set; }
        public void SetLoggerVerbosity(LoggerVerbosity verbosity) => LoggerVerbosity = verbosity;
        public void Info(string s) { }
        public void Warning(string s) { }
        public void Error(string s) => Errors.Add(s);
        public void Error(Exception exception) => Errors.Add(exception.Message);
        public void Success(string s) { }
        public void Debug(string s) { }
    }
}
