// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using NCWE.Core;
using NCWE.Core.MeshEditing;
using NCWE.Formats.CustomMeshes;
using SharpGLTF.Schema2;
using WolvenKit.Common.Model.Arguments;
using WolvenKit.Common.Services;
using WolvenKit.Core.Interfaces;
using WolvenKit.Modkit.RED4;
using WolvenKit.Modkit.RED4.Tools;
using WolvenKit.RED4.CR2W;
using WolvenKit.RED4.Types;
using WolvenKit.Common;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace NCWE.Formats.MeshEditing;

// Writes an edited game mesh: the game's own .mesh (materials, appearances, parameters and flags
// kept) with its geometry replaced by the edited parts, through WolvenKit's glTF importer, the way
// modders round-trip meshes through Blender. One LOD: the edited parts draw at every distance.
// Each part becomes a render chunk wearing the material slot of the chunk it came from.
public static class EditedMeshCooker
{
    // Bumped whenever the cooked output changes for the same edit.
    public const int Format = 2;   // 2: jambs continue the wall's UVs

    public static byte[] Cook(GameArchiveIndex index, string sourcePath, EditableMesh edited, string workDirectory, string identity)
    {
        var parts = edited.Parts.Where(p => p.TriangleCount > 0).Select(Compact).ToList();
        if (parts.Count == 0) throw new InvalidDataException("Every face of the mesh was deleted: delete the object instead.");
        using var file = index.ReadFile(sourcePath) ?? throw new FileNotFoundException("The edited mesh's original is not in the game's archives.", sourcePath);
        if (file.RootChunk is not CMesh { RenderResourceBlob.Chunk: rendRenderMeshBlob blob } mesh) throw new InvalidDataException("The edited mesh's original has no render data.");
        int chunks = blob.Header.RenderChunkInfos.Count;

        // The original, made ready for new geometry: one LOD, one render chunk per edited part (each
        // with the render flags of its source chunk), the appearances' slots in that order.
        var sourceInfos = blob.Header.RenderChunkInfos.ToArray();
        blob.Header.RenderChunkInfos.Clear();
        foreach (var part in parts)
        {
            var source = part.Chunk >= 0 && part.Chunk < sourceInfos.Length ? sourceInfos[part.Chunk] : null;
            blob.Header.RenderChunkInfos.Add(new rendChunk
            {
                RenderMask = source?.RenderMask ?? (Enums.EMeshChunkFlags.MCF_RenderInScene | Enums.EMeshChunkFlags.MCF_RenderInShadows),
                LodMask = 1
            });
        }
        foreach (var appearance in mesh.Appearances.Select(a => a.Chunk).OfType<meshMeshAppearance>())
        {
            var before = appearance.ChunkMaterials.ToArray();
            var slots = new CArray<CName>();
            foreach (var part in parts) slots.Add(before.Length == 0 ? (CName)"default" : before[Math.Clamp(part.Chunk, 0, before.Length - 1)]);
            appearance.ChunkMaterials = slots;
        }
        mesh.LodLevelInfo = new CArray<CFloat> { 0 };
        blob.Header.RenderLODs = new CArray<CFloat> { 0 };
        // Data computed from the old triangles.
        blob.Header.Topology.Clear(); blob.Header.TopologyData.Clear(); blob.Header.TopologyMetadata.Clear(); blob.Header.OpacityMicromaps.Clear();
        foreach (var parameter in mesh.Parameters.ToArray())
            if (parameter.Chunk is meshMeshParamGarmentSupport or meshMeshParamCloth or meshMeshParamCloth_Graphical or meshMeshParamTerrain)
                mesh.Parameters.Remove(parameter);

        Directory.CreateDirectory(workDirectory);
        string glb = Path.Combine(workDirectory, $"edit-{Guid.NewGuid():N}.glb");
        try
        {
            WriteGlb(parts, glb);
            using var stream = new MemoryStream();   // expandable: WolvenKit rewrites it in place
            stream.Write(CustomCr2w.Write(mesh)); stream.Position = 0;
            var logger = new CookLogger();
            var hashes = new HashService(false); var hooks = new HookService();
            var tools = new ModTools(logger, null!, hashes, new Red4ParserService(hashes, logger, hooks), null!, hooks);
            var args = new GltfImportArgs { ImportMaterials = false, OverrideMeshNameWithNodeName = true, FillEmpty = false, ImportGarmentSupport = false };
            if (!tools.ImportMesh(new FileInfo(glb), stream, args))
                throw new InvalidDataException("WolvenKit could not write the edited geometry." + (logger.Errors.Count > 0 ? " " + string.Join(" ", logger.Errors) : ""));
            using var imported = CustomCr2w.Read(stream.ToArray());
            if (imported.RootChunk is not CMesh written || written.RenderResourceBlob.Chunk is not rendRenderMeshBlob writtenBlob)
                throw new InvalidDataException("The edited mesh has no render data.");
            if (writtenBlob.Header.RenderChunkInfos.Count != parts.Count)
                throw new InvalidDataException($"The edited mesh has {writtenBlob.Header.RenderChunkInfos.Count} chunks for {parts.Count} parts.");
            written.SurfaceAreaPerAxis = SurfaceArea(parts);
            // Its own geometry identity (the original's would make the game take it for that mesh).
            written.GeometryHash = CustomMeshInfo.Fnv1a64($"{sourcePath}|{identity}|{Format}");
            var bytes = CustomCr2w.Write(written);
            Verify(bytes, parts);
            return bytes;
        }
        finally { try { File.Delete(glb); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    // A part without the vertices no triangle uses any more (the cut and delete leave some).
    private static EditablePart Compact(EditablePart part)
    {
        var remap = new int[part.VertexCount]; Array.Fill(remap, -1);
        var used = new List<int>();
        foreach (int v in part.Indices) if (remap[v] < 0) { remap[v] = used.Count; used.Add(v); }
        return new EditablePart
        {
            Name = part.Name, Chunk = part.Chunk, LodMask = 1,
            Positions = [.. used.Select(v => part.Positions[v])], Normals = [.. used.Select(v => part.Normals[v])],
            Tangents = part.Tangents is null ? null : [.. used.Select(v => part.Tangents[v])],
            Uv0 = [.. used.Select(v => part.Uv0[v])], Uv1 = part.Uv1 is null ? null : [.. used.Select(v => part.Uv1[v])],
            Colors = part.Colors is null ? null : [.. used.Select(v => part.Colors[v])],
            Indices = [.. part.Indices.Select(v => remap[v])]
        };
    }

    // glTF (Y up, the parts' space): one node and one mesh per part, named in part order.
    internal static void WriteGlb(IReadOnlyList<EditablePart> parts, string path)
    {
        var model = ModelRoot.CreateModel();
        var scene = model.UseScene("scene");
        for (int i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            string name = $"piece_{i:0000}";
            var primitive = model.CreateMesh(name).CreatePrimitive()
                .WithVertexAccessor("POSITION", part.Positions.ToArray())
                .WithVertexAccessor("NORMAL", part.Normals.ToArray())
                .WithVertexAccessor("TANGENT", (part.Tangents ?? Tangents(part)).ToArray())
                .WithVertexAccessor("TEXCOORD_0", part.Uv0.ToArray());
            // The importer turns UV0 over (V = 1 - v) but writes UV1 as it is (read back on the probe).
            if (part.Uv1 is { } uv1) primitive.WithVertexAccessor("TEXCOORD_1", uv1.Select(u => new Vector2(u.X, 1 - u.Y)).ToArray());
            if (part.Colors is { } colors) primitive.WithVertexAccessor("COLOR_0", colors.Select(c => Vector4.Clamp(c, Vector4.Zero, Vector4.One)).ToArray());
            // WolvenKit's importer swaps the first two corners of each glTF triangle (its exporter
            // does the same): written swapped, the game gets the original winding back.
            var indices = part.Indices.ToArray();
            for (int t = 0; t + 2 < indices.Length; t += 3) (indices[t], indices[t + 1]) = (indices[t + 1], indices[t]);
            primitive.WithIndicesAccessor(PrimitiveType.TRIANGLES, indices);
            scene.CreateNode(name).Mesh = model.LogicalMeshes[i];
        }
        model.SaveGLB(path);
    }

    // Tangents along U for a part read without them.
    private static List<Vector4> Tangents(EditablePart part)
    {
        var sums = new Vector3[part.VertexCount];
        for (int f = 0; f < part.TriangleCount; f++)
        {
            var (a, b, c) = part.Triangle(f);
            var e1 = part.Positions[b] - part.Positions[a]; var e2 = part.Positions[c] - part.Positions[a];
            var d1 = part.Uv0[b] - part.Uv0[a]; var d2 = part.Uv0[c] - part.Uv0[a];
            float det = d1.X * d2.Y - d2.X * d1.Y;
            var t = MathF.Abs(det) > 1e-12f ? (e1 * d2.Y - e2 * d1.Y) / det : e1;
            sums[a] += t; sums[b] += t; sums[c] += t;
        }
        var result = new List<Vector4>(part.VertexCount);
        for (int v = 0; v < part.VertexCount; v++)
        {
            var n = part.Normals[v];
            var t = sums[v] - n * Vector3.Dot(n, sums[v]);
            if (t.LengthSquared() < 1e-12f) t = Vector3.Cross(MathF.Abs(n.Y) < .9f ? Vector3.UnitY : Vector3.UnitX, n);
            result.Add(new(Vector3.Normalize(t), 1));
        }
        return result;
    }

    private static WolvenKit.RED4.Types.Vector3 SurfaceArea(IEnumerable<EditablePart> parts)
    {
        double x = 0, y = 0, z = 0;
        foreach (var part in parts)
            for (int f = 0; f < part.TriangleCount; f++)
            {
                var cross = part.FaceCross(f) * .5f;
                x += MathF.Abs(cross.X); y += MathF.Abs(cross.Z); z += MathF.Abs(cross.Y);   // view (x, y up, -north) -> game
            }
        return new WolvenKit.RED4.Types.Vector3 { X = (float)x, Y = (float)y, Z = (float)z };
    }

    // Read back: one chunk per part, every vertex, whole triangles, the appearances covering them.
    private static void Verify(byte[] bytes, IReadOnlyList<EditablePart> parts)
    {
        using var file = CustomCr2w.Read(bytes);
        if (file.RootChunk is not CMesh { RenderResourceBlob.Chunk: rendRenderMeshBlob blob } mesh) throw new InvalidDataException("The edited mesh cannot be read back.");
        var info = MeshTools.GetMeshesinfo(blob, mesh);
        using var buffer = new MemoryStream(blob.RenderBuffer.Buffer.GetBytes());
        var raw = MeshTools.ContainRawMesh(buffer, info, false);
        if (raw.Count != parts.Count) throw new InvalidDataException($"The edited mesh holds {raw.Count} chunks instead of {parts.Count}.");
        for (int i = 0; i < raw.Count; i++)
        {
            int vertices = raw[i].positions?.Length ?? 0, indices = raw[i].indices?.Length ?? 0;
            if (!PartIntact(vertices, indices, parts[i].VertexCount, parts[i].Indices.Count))
                throw new InvalidDataException($"Part {i} of the edited mesh lost geometry ({vertices} vertices and {indices} indices written, {parts[i].VertexCount} and {parts[i].Indices.Count} expected).");
        }
        foreach (var appearance in mesh.Appearances.Select(a => a.Chunk).OfType<meshMeshAppearance>())
            if (appearance.ChunkMaterials.Count != raw.Count) throw new InvalidDataException($"The appearance {appearance.Name} does not cover every chunk of the edited mesh.");
    }

    // Every vertex and whole triangles. WolvenKit's importer drops a handful of triangles from some
    // dense meshes (every vertex kept): half a percent of a part is accepted, or a few triangles of
    // a small one while it keeps 98% of them; more is real geometry loss.
    internal static bool PartIntact(int vertices, int indices, int expectedVertices, int expectedIndices)
    {
        if (vertices != expectedVertices || indices % 3 != 0 || indices > expectedIndices) return false;
        double dropped = (expectedIndices - indices) / 3.0, triangles = expectedIndices / 3.0;
        return dropped <= Math.Max(triangles * .005, Math.Min(4, triangles * .02));
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
