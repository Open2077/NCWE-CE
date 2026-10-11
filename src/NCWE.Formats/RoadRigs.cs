// SPDX-License-Identifier: GPL-3.0-only
using WolvenKit.RED4.Archive.IO;
using WolvenKit.RED4.Types;

namespace NCWE.Formats;

// Meshes cooked for bending are skinned to evenly spaced bones along their length; a node bending
// one stores one DeformationData matrix per bone:
// - road pieces (road_meshes\bended\br_*): bones "RoadBone0..N" along +Y (verified on 2.31: 24 m
//   pieces have 9 bones every 3 m and 9 matrices);
// - cables (...\cables\bended_cables\*): bones "joint1..N" along -X (21 joints, 1 to 13 m apart),
//   their rig scaling the tube's section by Thickness (CableBending).
public sealed record RoadRig(int Bones, float Spacing)
{
    public bool Cable { get; init; }
    public float Thickness { get; init; } = 1;
    public float Length => Spacing * (Bones - 1);

    // The rig of a mesh cooked for bending; null when it is not rigged as a road piece or a cable.
    public static RoadRig? Of(CMesh mesh)
    {
        int count = mesh.BoneNames.Count;
        if (count < 2 || mesh.BoneRigMatrices.Count != count) return null;
        return RoadPieceRig(mesh, count) ?? CableRig(mesh, count);
    }

    private static RoadRig? RoadPieceRig(CMesh mesh, int count)
    {
        float spacing = -(float)mesh.BoneRigMatrices[1].W.Y;
        if (!(spacing > .1f)) return null;
        for (int i = 0; i < count; i++)
        {
            if (mesh.BoneNames[i].ToString() != "RoadBone" + i) return null;
            var w = mesh.BoneRigMatrices[i].W;
            if (Math.Abs(-(float)w.Y - spacing * i) > .02f || Math.Abs((float)w.X) > .02f || Math.Abs((float)w.Z) > .02f) return null;
        }
        return new(count, spacing);
    }

    // A cable template: its joints stand at x = 0, -spacing, -2 spacing... The rig matrices are
    // the inverse bind poses: x mirrored, the section's axes swapped, all three scaled alike (so
    // joint k's reads W.x = k spacing X.x).
    private static RoadRig? CableRig(CMesh mesh, int count)
    {
        float along = (float)mesh.BoneRigMatrices[0].X.X;
        if (!(along < -.1f)) return null;
        float spacing = (float)mesh.BoneRigMatrices[1].W.X / along;
        if (!(spacing > .1f)) return null;
        for (int i = 0; i < count; i++)
        {
            if (mesh.BoneNames[i].ToString() != "joint" + (i + 1)) return null;
            var m = mesh.BoneRigMatrices[i];
            if (Math.Abs((float)m.X.X - along) > 1e-3f || Math.Abs((float)m.W.X / along - spacing * i) > .02f * spacing || Math.Abs((float)m.W.Y) > .02f || Math.Abs((float)m.W.Z) > .02f) return null;
            // The section as CableBending draws it: ring z on the Y row, ring y on the Z row.
            if (Math.Abs((float)m.Y.Z + along) > 1e-3f || Math.Abs((float)m.Z.Y + along) > 1e-3f || Math.Abs((float)m.Y.Y) > 1e-3f || Math.Abs((float)m.Z.Z) > 1e-3f) return null;
        }
        return new(count, spacing) { Cable = true, Thickness = -along };
    }
}

public sealed partial class GameArchiveIndex
{
    // Null when the mesh is missing or is not rigged as a bendable road piece or cable.
    public RoadRig? ReadRoadRig(ulong meshHash)
    {
        if (EntryOrNull(meshHash) is not { } entry) return null;
        using var data = ExtractEntry(entry, default);
        using var reader = new CR2WReader(data);
        if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file is null) return null;
        using (file) return file.RootChunk is CMesh mesh ? RoadRig.Of(mesh) : null;
    }
}
