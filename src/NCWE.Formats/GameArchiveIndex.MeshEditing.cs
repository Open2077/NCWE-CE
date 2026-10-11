// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using NCWE.Core.MeshEditing;
using WolvenKit.Modkit.RED4.Tools;
using WolvenKit.RED4.Archive.CR2W;
using WolvenKit.RED4.Archive.IO;
using WolvenKit.RED4.Types;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace NCWE.Formats;

// A game mesh as the mesh edit tool works on it: every vertex attribute of its render chunks.
// ChunkCount: render chunks in the file; Appearances: their names; SurfaceParts: per render chunk,
// its part in the mesh's material surface (the chunks with triangles, in order; -1 for the others).
public sealed record EditableSource(EditableMesh Mesh, string Path, string Fingerprint, int ChunkCount, IReadOnlyList<string> Appearances, int[] SurfaceParts);

public sealed partial class GameArchiveIndex
{
    public EditableSource ReadEditableMesh(ulong hash, bool allLods = false, CancellationToken cancellation = default)
    {
        if (!_entries.TryGetValue(hash, out var entry)) throw new FileNotFoundException("Only meshes of the game can be edited in place (custom meshes are edited in their own model).");
        using var data = ExtractEntry(entry, cancellation);
        using var reader = new CR2WReader(data);
        if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file is null) throw new InvalidDataException("The mesh could not be read.");
        using (file)
        {
            if (file.RootChunk is not CMesh mesh) throw new NotSupportedException("This resource is not a mesh.");
            var (editable, chunks, appearances, surfaceParts) = EditableMeshReader.Read(mesh, allLods, cancellation);
            if (mesh.RenderResourceBlob?.Chunk is rendRenderMeshBlob blob)
            {
                var oneSided = ChunkOneSided(mesh, blob);
                foreach (var part in editable.Parts) part.OneSided = part.Chunk < oneSided.Length && oneSided[part.Chunk];
            }
            editable.ObjectType = (byte)(int)(Enums.ERenderObjectType)mesh.ObjectType;
            return new(editable, entry.FileName ?? $"{hash}.mesh", Convert.ToHexString(entry.SHA1Hash), chunks, appearances, surfaceParts);
        }
    }
}

public static class EditableMeshReader
{
    // The chunks of the mesh's first LOD (or of every LOD), with positions, normals, tangents, both UV
    // sets and colours. UVs are turned to the editor's convention (V = 0 at the top of the image, as
    // glTF), the opposite of the render buffer's; the cooker's glTF import writes them back as they were.
    public static (EditableMesh Mesh, int Chunks, string[] Appearances, int[] SurfaceParts) Read(CMesh mesh, bool allLods = false, CancellationToken cancellation = default)
    {
        if (mesh.RenderResourceBlob?.Chunk is not rendRenderMeshBlob blob) throw new NotSupportedException("This mesh has no static geometry.");
        var info = MeshTools.GetMeshesinfo(blob, mesh);
        if (info.weightCounts.Any(w => w != 0)) throw new NotSupportedException("Skinned meshes (characters, clothes, animated props) cannot be edited in place.");
        using var buffer = new MemoryStream(blob.RenderBuffer.Buffer.GetBytes());
        var raw = MeshTools.ContainRawMesh(buffer, info, false);
        uint firstLod = raw.Where(r => r.positions is { Length: > 0 } && r.indices is { Length: > 0 }).Select(r => r.lod).DefaultIfEmpty(1u).Min();
        var editable = new EditableMesh();
        var surfaceParts = new int[raw.Count]; int surfaceCount = 0;
        for (int r = 0; r < raw.Count; r++)
        {
            cancellation.ThrowIfCancellationRequested();
            var part = raw[r];
            surfaceParts[r] = -1;
            if (part.positions is not { Length: > 0 } positions || part.indices is not { Length: > 0 } indices) continue;
            surfaceParts[r] = surfaceCount++;
            if (!allLods && part.lod != firstLod) continue;
            int n = positions.Length;
            editable.Parts.Add(new EditablePart
            {
                Name = part.name ?? $"submesh_{r:00}", Chunk = r, LodMask = allLods ? part.lod : 1,
                Positions = [.. positions],
                Normals = part.normals?.Length == n ? [.. part.normals.Select(v => EditablePart.Unit(v))] : Recalculate(positions, indices),
                Tangents = part.tangents?.Length == n ? [.. part.tangents] : null,
                Uv0 = part.texCoords0?.Length == n ? [.. part.texCoords0.Select(Editor)] : [.. Enumerable.Repeat(Vector2.Zero, n)],
                Uv1 = part.texCoords1?.Length == n ? [.. part.texCoords1.Select(Editor)] : null,
                Colors = part.colors0?.Length == n ? [.. part.colors0] : null,
                Indices = [.. indices.Select(i => (int)i)]
            });
        }
        if (editable.Parts.Count == 0) throw new NotSupportedException("The mesh has no triangles to edit.");
        var appearances = mesh.Appearances.Select(a => a.Chunk).OfType<meshMeshAppearance>().Select(a => a.Name.ToString() ?? "default").ToArray();
        return (editable, raw.Count, appearances, surfaceParts);
    }

    private static Vector2 Editor(Vector2 raw) => new(raw.X, 1 - raw.Y);

    private static List<Vector3> Recalculate(Vector3[] positions, uint[] indices)
    {
        var sums = new Vector3[positions.Length];
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            var a = positions[indices[i]]; var cross = Vector3.Cross(positions[indices[i + 1]] - a, positions[indices[i + 2]] - a);
            sums[indices[i]] += cross; sums[indices[i + 1]] += cross; sums[indices[i + 2]] += cross;
        }
        return [.. sums.Select(s => EditablePart.Unit(s))];
    }
}
