// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Numerics;

namespace NCWE.Core;

// worldCableMeshNode reproduction (power lines, hanging wires). Verified on the 3,012 cables of
// 2.31 with Phantom Liberty (probe --cable-survey):
// - every cable draws one of the tube templates of ...\cables\bended_cables\ (along game -X from
//   x = 0, 20 to 260 m long, its radius in its name), skinned to 21 evenly spaced joints; a node
//   stores one DeformationData matrix per joint, in node space, and stretches or squeezes the tube
//   over its whole length;
// - a matrix's rows: X = against the direction of travel, Z = to the left of it (level), Y = Z
//   crossed with the travel (down, for a level cable), W = the point on the curve; the last
//   column is 0, 0, 0, 1. The basis is left-handed: so is the template's rig, and the tube comes
//   out the right way round;
// - a ring of the tube hangs on its joint alone: only the Y and Z rows and the point place its
//   vertices (ring y along Z, ring z along Y), the rig scaling both by a few percent (RoadRig).
public static class CableBending
{
    public const string NodeType = "worldCableMeshNode";

    public static bool IsCable(string meshPath) => meshPath.Contains(@"\cables\bended_cables\", StringComparison.OrdinalIgnoreCase);

    // The radius a template is named after ("..._r7_sub_20_triangle": 7 cm), in centimetres as
    // the node stores it; 0 when its name does not say.
    public static float RadiusOf(string meshPath)
    {
        string name = Path.GetFileNameWithoutExtension(meshPath);
        foreach (var part in name.Split('_'))
            if (part.Length > 1 && part[0] is 'r' or 'R' && int.TryParse(part.AsSpan(1), out int radius)) return radius;
        return 0;
    }

    // Frames along a curve as RoadBuilder makes them (rows: right, travel, up, point) turned into a
    // cable's (rows: against travel, down, left, point).
    public static ImmutableArray<Matrix4x4> Frames(IReadOnlyList<Matrix4x4> along)
    {
        var frames = ImmutableArray.CreateBuilder<Matrix4x4>(along.Count);
        foreach (var f in along)
            frames.Add(new Matrix4x4(-f.M21, -f.M22, -f.M23, 0, -f.M31, -f.M32, -f.M33, 0, -f.M11, -f.M12, -f.M13, 0, f.M41, f.M42, f.M43, 1));
        return frames.MoveToImmutable();
    }

    // The template drawn along a cable's frames as the game skins it; `thickness` is its rig's scale
    // of the section (1.02 to 1.06 in the game's templates: left at 1 where the rig is not at hand).
    // Template and result are in the editor's Y-up mesh space (x, up, -gameY); the result is
    // node-local (place it with the node's setup).
    public static MeshAsset Deform(MeshAsset template, IReadOnlyList<Matrix4x4> frames, string resourcePath, float thickness = 1)
    {
        if (frames.Count == 0) throw new ArgumentException("A cable needs deformation frames.", nameof(frames));
        // The tube runs from x = 0 (first joint) towards -x (last joint).
        float start = template.Bounds.Max.X, length = Math.Max(1e-4f, template.Bounds.Size.X);
        var parts = new MeshPart[template.Parts.Length];
        for (int p = 0; p < parts.Length; p++)
        {
            var source = template.Parts[p];
            var vertices = GeometryArrays.Allocate<MeshVertex>(source.Vertices.Length);
            for (int i = 0; i < vertices.Length; i++)
            {
                var v = source.Vertices[i];
                var frame = FrameAt(frames, (start - v.Position.X) / length);
                Vector3 against = Row(frame, 0), down = Row(frame, 1), left = Row(frame, 2);
                // Ring axes in game space: y = -view z, z = view y.
                var game = frame.Translation + (down * v.Position.Y - left * v.Position.Z) * thickness;
                var normal = down * v.Normal.Y - left * v.Normal.Z - against * v.Normal.X;
                vertices[i] = new(Coordinates.GameToView(game), SafeNormalize(Coordinates.GameToView(normal)));
            }
            parts[p] = source with { Vertices = vertices };
        }
        return new MeshAsset(resourcePath, template.SourceHash + "+cable", parts,
            Bounds3.From(parts.SelectMany(part => part.Vertices.Select(v => v.Position))), template.SourceArchive, 0)
            { AppearanceColors = template.AppearanceColors, PartTextures = template.PartTextures };
    }

    private static Matrix4x4 FrameAt(IReadOnlyList<Matrix4x4> frames, float t)
    {
        if (frames.Count == 1) return frames[0];
        float s = Math.Clamp(t, 0, 1) * (frames.Count - 1);
        int i = Math.Min((int)s, frames.Count - 2);
        return Matrix4x4.Lerp(frames[i], frames[i + 1], s - i);
    }

    private static Vector3 Row(Matrix4x4 m, int row) => row switch
    {
        0 => new(m.M11, m.M12, m.M13),
        1 => new(m.M21, m.M22, m.M23),
        _ => new(m.M31, m.M32, m.M33)
    };

    private static Vector3 SafeNormalize(Vector3 v) => v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : Vector3.UnitY;
}
