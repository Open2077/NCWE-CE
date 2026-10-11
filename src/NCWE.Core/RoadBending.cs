// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;

namespace NCWE.Core;

// worldBendedMeshNode reproduction. Shipping data references generated meshes
// (road_meshes\bended\br_<id>_<template>.mesh) that are not in the archives; the
// template lies along its game X axis (length), Y (width) and Z (up). The node's
// DeformationData are row-vector frames along the curve in node space: X = width
// direction, Y = tangent, Z = up, translation = curve point. Verified on 2.31 data:
// mapping width to -X keeps handedness and reproduces the stored deformed boxes.
public static class RoadBending
{
    // Frames are game space (Z up). Template and result use the editor's Y-up mesh
    // space; the result is in node-local mesh space (place it with the node setup).
    // Road-tool proxy templates (street\tool\...\base_proxy) lie along game X; meshes cooked for
    // bending (road_meshes\bended\br_*) and the engine's own convention lie along game Y.
    public enum Axis { X, Y }
    public static Axis AxisOf(string path) => Path.GetFileName(path).StartsWith("br_", StringComparison.OrdinalIgnoreCase) ? Axis.Y : Axis.X;
    // A bended mesh that is a road piece: the street tool's road types (street\tool\rt_*, lone
    // sidewalks aside) or a piece cooked for bending (br_*). Bended nodes also carry railtracks,
    // sidewalks, passages and cables, whose ends a road does not meet.
    public static bool IsRoadPiece(string meshPath)
    {
        string file = Path.GetFileName(meshPath);
        if (file.Contains("rt_sidewalk", StringComparison.OrdinalIgnoreCase) || file.Contains("railtrack", StringComparison.OrdinalIgnoreCase)) return false;
        return file.StartsWith("br_", StringComparison.OrdinalIgnoreCase) || meshPath.Contains(@"street\tool\rt_", StringComparison.OrdinalIgnoreCase);
    }
    // Length along the travel axis, in metres (game space).
    public static float Length(MeshAsset template, Axis axis) => axis == Axis.X ? template.Bounds.Size.X : template.Bounds.Size.Z;
    // How far the piece reaches on either side of its curve (game width axis): a turn tighter than
    // this folds its inner side over itself, in the game too.
    public static float HalfWidth(MeshAsset template, Axis axis) => axis == Axis.X
        ? MathF.Max(MathF.Abs(template.Bounds.Min.Z), MathF.Abs(template.Bounds.Max.Z))
        : MathF.Max(MathF.Abs(template.Bounds.Min.X), MathF.Abs(template.Bounds.Max.X));

    public static MeshAsset Deform(MeshAsset template, IReadOnlyList<Matrix4x4> frames, string resourcePath, Axis axis)
    {
        if (axis == Axis.X) return Deform(template, frames, resourcePath);
        if (frames.Count == 0) throw new ArgumentException("A bended node needs deformation frames.", nameof(frames));
        // View space (x, up, -gameY): gameY = -z runs from -Max.Z to -Min.Z.
        float min = -template.Bounds.Max.Z, length = Math.Max(1e-4f, template.Bounds.Size.Z);
        var parts = new MeshPart[template.Parts.Length];
        for (int p = 0; p < parts.Length; p++)
        {
            var source = template.Parts[p];
            var vertices = GeometryArrays.Allocate<MeshVertex>(source.Vertices.Length);
            for (int i = 0; i < vertices.Length; i++)
            {
                var v = source.Vertices[i];
                var g = Coordinates.ViewToGame(v.Position); var n = Coordinates.ViewToGame(v.Normal);
                var frame = FrameAt(frames, (g.Y - min) / length);
                var game = Row(frame, 0) * g.X + Row(frame, 2) * g.Z + frame.Translation;
                var normalGame = Row(frame, 0) * n.X + Row(frame, 1) * n.Y + Row(frame, 2) * n.Z;
                vertices[i] = new(Coordinates.GameToView(game), SafeNormalize(Coordinates.GameToView(normalGame)));
            }
            parts[p] = source with { Vertices = vertices };
        }
        return new MeshAsset(resourcePath, template.SourceHash + "+bended", parts,
            Bounds3.From(parts.SelectMany(part => part.Vertices.Select(v => v.Position))), template.SourceArchive, 0)
            { AppearanceColors = Asphalt(template), PartTextures = template.PartTextures };
    }

    public static MeshAsset Deform(MeshAsset template, IReadOnlyList<Matrix4x4> frames, string resourcePath)
    {
        if (frames.Count == 0) throw new ArgumentException("A bended node needs deformation frames.", nameof(frames));
        float min = template.Bounds.Min.X, length = Math.Max(1e-4f, template.Bounds.Max.X - min);
        var parts = new MeshPart[template.Parts.Length];
        for (int p = 0; p < parts.Length; p++)
        {
            var source = template.Parts[p];
            var vertices = GeometryArrays.Allocate<MeshVertex>(source.Vertices.Length);
            for (int i = 0; i < vertices.Length; i++)
            {
                var v = source.Vertices[i];
                var frame = FrameAt(frames, (v.Position.X - min) / length);
                // Mesh Y-up (x, up, -gameY) -> template game axes: length x, width gameY = -z, up y.
                float width = -v.Position.Z, up = v.Position.Y;
                var game = Row(frame, 0) * -width + Row(frame, 2) * up + frame.Translation;
                var normalGame = Row(frame, 1) * v.Normal.X + Row(frame, 0) * v.Normal.Z + Row(frame, 2) * v.Normal.Y;
                vertices[i] = new(Coordinates.GameToView(game), SafeNormalize(Coordinates.GameToView(normalGame)));
            }
            parts[p] = source with { Vertices = vertices };
        }
        return new MeshAsset(resourcePath, template.SourceHash + "+bended", parts,
            Bounds3.From(parts.SelectMany(part => part.Vertices.Select(v => v.Position))), template.SourceArchive, 0)
            { AppearanceColors = Asphalt(template), PartTextures = template.PartTextures };
    }

    // Road templates carry a placeholder "default" material; the engine applies the road
    // tool's materials. Unknown parts are shown as asphalt.
    private static readonly Vector4 AsphaltColor = new(.11f, .11f, .12f, 1);
    private static IReadOnlyDictionary<string, Vector4[]> Asphalt(MeshAsset template)
    {
        var source = template.AppearanceColors;
        if (source is null || source.Count == 0) return new Dictionary<string, Vector4[]> { ["default"] = Enumerable.Repeat(AsphaltColor, template.Parts.Length).ToArray() };
        return source.ToDictionary(e => e.Key, e => e.Value.Select(c => c.W > 0 ? c : AsphaltColor).ToArray());
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

    // "br_<8 hex>_<template>.mesh" -> "<template>.mesh".
    public static string? TemplateFileName(string generatedPath)
    {
        var file = Path.GetFileName(generatedPath);
        return file.Length > 12 && file.StartsWith("br_", StringComparison.OrdinalIgnoreCase) && file[11] == '_' ? file[12..] : null;
    }
}
