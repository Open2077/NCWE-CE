// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NCWE.Core.MeshEditing;

// Rules and names shared by the editor and the export: what a stored edit may hold, where its
// cooked mesh goes in a mod, and the content signature that names it.
public static class MeshEdits
{
    public const int MaxOps = 4096;
    public const int MaxIndices = 4_000_000;
    public const string ExportRoot = @"ncwe\meshedit\";

    public static void Validate(MeshEditDefinition edit, MeshReference mesh)
    {
        if (edit.Source.Hash != mesh.Hash) throw new InvalidDataException("A mesh edit belongs to another mesh.");
        var ops = edit.Ops.IsDefault ? [] : edit.Ops;
        if (ops.Length > MaxOps) throw new InvalidDataException($"A mesh edit holds at most {MaxOps} operations.");
        long indices = 0;
        foreach (var op in ops)
        {
            switch (op)
            {
                case null: throw new InvalidDataException("A mesh edit has an empty operation.");
                case CutBoxOp cut:
                    if (!Finite(cut.Center) || !Finite(cut.HalfSize) || !float.IsFinite(cut.Rotation.X + cut.Rotation.Y + cut.Rotation.Z + cut.Rotation.W)
                        || cut.HalfSize.X <= 0 || cut.HalfSize.Y <= 0 || cut.HalfSize.Z <= 0 || cut.HalfSize.Length() > 10_000)
                        throw new InvalidDataException("An opening needs a finite box with a size.");
                    break;
                case TransformVerticesOp t:
                    indices += Count(t.Vertices);
                    if (!Finite(t.Matrix)) throw new InvalidDataException("A vertex transform must be finite.");
                    break;
                case DeleteFacesOp d: indices += Count(d.Faces); break;
                case ExtrudeFacesOp e:
                    indices += Count(e.Faces);
                    if (!float.IsFinite(e.Distance) || Math.Abs(e.Distance) > 1000 || e.Direction is { } dir && !Finite(dir)) throw new InvalidDataException("An extrusion needs a finite distance.");
                    break;
                case SubdivideFacesOp s: indices += Count(s.Faces); break;
                case FlipFacesOp f: indices += Count(f.Faces); break;
                case NormalsOp n: indices += Count(n.Faces); break;
                case UvProjectOp p:
                    indices += Count(p.Faces);
                    if (!Finite(p.Origin) || !Finite(p.AxisU) || !Finite(p.AxisV) || !float.IsFinite(p.Scale.X + p.Scale.Y + p.Offset.X + p.Offset.Y) || p.Channel is < 0 or > 1)
                        throw new InvalidDataException("A UV projection must be finite.");
                    break;
                case UvTransformOp u:
                    indices += Count(u.Faces);
                    if (!float.IsFinite(u.Matrix.M11 + u.Matrix.M12 + u.Matrix.M21 + u.Matrix.M22 + u.Matrix.M31 + u.Matrix.M32) || u.Channel is < 0 or > 1)
                        throw new InvalidDataException("A UV transform must be finite.");
                    break;
                case UvMoveOp m:
                    indices += Count(m.Vertices);
                    if (!float.IsFinite(m.Delta.X + m.Delta.Y) || m.Channel is < 0 or > 1) throw new InvalidDataException("A UV move must be finite.");
                    break;
                case MoveFacesToPartOp m: indices += Count(m.Faces); break;
                case TwoSidedOp t: indices += Count(t.Faces); break;
                case AddTrianglesOp a:
                    indices += a.Corners.IsDefault ? 0 : a.Corners.Length;
                    if (!a.Corners.IsDefault && a.Corners.Any(c => !Finite(c))) throw new InvalidDataException("Added faces need finite corners.");
                    break;
            }
        }
        if (indices > MaxIndices) throw new InvalidDataException("A mesh edit names too many vertices and faces.");
    }

    private static long Count(System.Collections.Immutable.ImmutableArray<PartIndices> selection) => MeshSelection.Count(selection);
    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    private static bool Finite(Matrix4x4 m) => float.IsFinite(m.M11 + m.M12 + m.M13 + m.M14 + m.M21 + m.M22 + m.M23 + m.M24 + m.M31 + m.M32 + m.M33 + m.M34 + m.M41 + m.M42 + m.M43 + m.M44);

    // A short digest of the operations (names the cooked mesh; the same edit cooks to the same file).
    public static string Signature(MeshEditDefinition edit)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(edit.Ops.IsDefault ? [] : edit.Ops, ProjectStorage.JsonOptions);
        return Convert.ToHexString(SHA256.HashData(json))[..16].ToLowerInvariant();
    }

    // Where an edited mesh goes in a mod: ncwe\meshedit\<project>\<mesh name>_<signature>.mesh.
    public static string ExportPath(Guid project, MeshEditDefinition edit, string sourcePath)
    {
        string stem = Path.GetFileNameWithoutExtension(sourcePath.Replace('\\', '/'));
        var chars = stem.ToLowerInvariant().Select(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' ? c : '_').ToArray();
        stem = new string(chars).Trim('_');
        if (stem.Length > 48) stem = stem[..48];
        return $@"{ExportRoot}{project:N}\{(stem.Length > 0 ? stem : "mesh")}_{Signature(edit)}.mesh";
    }

    public static bool IsEditedPath(string? path) => path is not null && path.StartsWith(ExportRoot, StringComparison.OrdinalIgnoreCase);

    // Ops of a stored edit (an older project may have none).
    public static IReadOnlyList<MeshEditOp> Ops(MeshEditDefinition edit) => edit.Ops.IsDefault ? [] : edit.Ops;
}
