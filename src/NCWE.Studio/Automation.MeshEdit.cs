// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;
using NCWE.Core;
using NCWE.Core.MeshEditing;
using NCWE.Studio.Session;

namespace NCWE.Studio;

// api.meshedit.*: editing a mesh in place for agents and tests. Positions and directions are game
// space (metres, X east, Y north, Z up) in the world; the edit itself is stored in the mesh's own
// space, so it follows the object when it moves. Each operation is one undo step.
internal sealed partial class Automation
{
    private async Task<object> MeshEdit(StudioSession s, Viewport.ViewportController v, string op, JsonElement c)
    {
        if (op == "api.meshedit.begin")
        {
            string id = Str(c, "id") ?? s.SelectedId ?? throw new InvalidOperationException("id: the mesh to edit");
            if (!s.BeginMeshEditing(id)) return new { ok = false, error = s.Error };
            if (Str(c, "mode") is { } beginMode) s.MeshSelectMode = Mode(beginMode);
            await WaitEdited(s);
            return Info(s);
        }
        if (op == "api.meshedit.end") { s.EndMeshEditing(); return new { ok = true }; }
        // Every other command works on the mesh being edited (or the one named, edit mode started).
        if (Str(c, "id") is { } named && named != s.MeshEditId)
        {
            if (!s.BeginMeshEditing(named)) return new { ok = false, error = s.Error };
        }
        if (s.MeshEditId is null) throw new InvalidOperationException("Start with api.meshedit.begin {id}.");
        await WaitEdited(s);
        if (Str(c, "mode") is { } mode) s.MeshSelectMode = Mode(mode);
        if (c.TryGetProperty("select", out var select) && select.ValueKind == JsonValueKind.Object) Select(s, select);
        switch (op)
        {
            case "api.meshedit.info": return Info(s);
            case "api.meshedit.select": return new { ok = true, selected = SelectionJson(s) };
            case "api.meshedit.faces":
            {
                // The faces of the selection (or all), up to a limit: centre, normal, area, part.
                var mesh = s.MeshEditResult!; var model = Model(s);
                var faces = s.MeshSelectedFaces.IsDefaultOrEmpty ? MeshSelection.Group(MeshSelection.AllFaces(mesh)) : s.MeshSelectedFaces;
                int limit = Int(c, "limit", 60);
                return new
                {
                    ok = true, count = MeshSelection.Count(faces),
                    faces = MeshSelection.Flatten(faces).Take(limit).Select(x =>
                    {
                        var part = mesh.Parts[x.Part];
                        var cross = part.FaceCross(x.Index) * part.WindingSign();
                        return new { part = x.Part, face = x.Index, center = A(Game(Vector3.Transform(part.FaceCenter(x.Index), model))),
                            normal = A(GameDirection(Vector3.TransformNormal(EditablePart.Unit(cross), model))), area = R(cross.Length() / 2) };
                    }).ToArray()
                };
            }
            case "api.meshedit.cut_opening":
            {
                var center = OptV3(c, "center") ?? throw new ArgumentException("center: [x, y, z], a point of the surface to open (game space)");
                float width = Float(c, "width"), height = Float(c, "height"), depth = OptFloat(c, "depth") ?? s.CutDepth;
                bool jambs = !c.TryGetProperty("jambs", out var j) || j.GetBoolean();
                var mesh = s.MeshEditResult!; var model = Model(s);
                Matrix4x4.Invert(model, out var inverse);
                var local = Vector3.Transform(Coordinates.GameToView(center), inverse);
                // The surface's normal: given, or the nearest face's (turned towards the camera).
                Vector3 normal;
                if (OptV3(c, "normal") is { } given) normal = EditablePart.Unit(Vector3.TransformNormal(Coordinates.GameToView(given), inverse));
                else
                {
                    var (part, face) = NearestFace(mesh, local) ?? throw new InvalidOperationException("The mesh has no face.");
                    normal = EditablePart.Unit(mesh.Parts[part].FaceCross(face) * mesh.Parts[part].WindingSign());
                    var eye = Vector3.Transform(s.Camera.Eye, inverse);
                    if (Vector3.Dot(normal, eye - local) < 0) normal = -normal;
                }
                var up = EditablePart.Unit(Vector3.TransformNormal(Coordinates.GameToView(OptV3(c, "up") ?? Vector3.UnitZ), inverse));
                var x = Vector3.Cross(up, normal);
                x = x.LengthSquared() < 1e-6f ? EditablePart.Unit(Vector3.Cross(Vector3.UnitX, normal)) : Vector3.Normalize(x);
                var y = Vector3.Normalize(Vector3.Cross(normal, x));
                // Sizes are in world metres: the object's scale along each axis turns them local.
                float sx = Vector3.TransformNormal(x, model).Length(), sy = Vector3.TransformNormal(y, model).Length(), sz = Vector3.TransformNormal(normal, model).Length();
                var cut = StudioSession.CutOp(local, x, y, normal, width / sx, height / sy, depth / sz, jambs);
                int before = mesh.TriangleCount;
                if (!s.MeshEditDo(cut)) return new { ok = false, error = s.Error };
                await WaitEdited(s);
                return new { ok = true, faces_before = before, faces = s.MeshEditResult?.TriangleCount, normal = A(GameDirection(Vector3.TransformNormal(normal, model))), info = Info(s) };
            }
            case "api.meshedit.transform":
            {
                var vertices = s.MeshSelectedVertices;
                if (vertices.IsDefaultOrEmpty) throw new InvalidOperationException("Select vertices or faces first (select: {...}).");
                var pivot = OptV3(c, "pivot") is { } p ? Coordinates.GameToView(p) : s.MeshSelectionCenter ?? Vector3.Zero;
                var offset = OptV3(c, "offset") is { } o ? Coordinates.GameToView(o) : Vector3.Zero;
                var rotation = OptV3(c, "rotation") is { } r
                    ? Quaternion.CreateFromAxisAngle(Vector3.UnitY, r.Z * MathF.PI / 180) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, r.X * MathF.PI / 180) * Quaternion.CreateFromAxisAngle(-Vector3.UnitZ, r.Y * MathF.PI / 180)
                    : Quaternion.Identity;
                var scale = c.TryGetProperty("scale", out var sc) ? sc.ValueKind == JsonValueKind.Number ? new Vector3(sc.GetSingle()) : ViewScale(V3(sc)) : Vector3.One;
                var delta = Matrix4x4.CreateTranslation(-pivot) * Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(pivot + offset);
                if (!s.MeshEditTransformView(delta)) return new { ok = false, error = s.Error };
                return new { ok = true, moved = MeshSelection.Count(vertices), info = Info(s) };
            }
            case "api.meshedit.delete_faces": return await Do(s, new DeleteFacesOp(Faces(s)));
            case "api.meshedit.extrude":
            {
                var direction = OptV3(c, "direction") is { } d ? Vector3.TransformNormal(Coordinates.GameToView(d), Inverse(s)) : (Vector3?)null;
                return await Do(s, new ExtrudeFacesOp(Faces(s), Float(c, "distance"), direction));
            }
            case "api.meshedit.subdivide": return await Do(s, new SubdivideFacesOp(Faces(s)));
            case "api.meshedit.flip": return await Do(s, new FlipFacesOp(Faces(s)));
            // all: every face of the mesh (a wall or floor seen from both sides), else the selection.
            case "api.meshedit.two_sided":
                return await Do(s, c.TryGetProperty("all", out var everyFace) && everyFace.GetBoolean() ? new TwoSidedOp([], All: true) : new TwoSidedOp(Faces(s)));
            case "api.meshedit.normals": return await Do(s, new NormalsOp(Faces(s), !c.TryGetProperty("smooth", out var smooth) || smooth.GetBoolean()));
            case "api.meshedit.set_part": return await Do(s, new MoveFacesToPartOp(Faces(s), Int(c, "part", -1)));
            case "api.meshedit.uv_project":
            {
                var faces = Faces(s); var mesh = s.MeshEditResult!; var inverse = Inverse(s);
                // The plane: the faces' average normal, or a game direction.
                Vector3 normal;
                if (OptV3(c, "axis") is { } axis) normal = EditablePart.Unit(Vector3.TransformNormal(Coordinates.GameToView(axis), inverse));
                else
                {
                    var sum = Vector3.Zero;
                    foreach (var (p, f) in MeshSelection.Flatten(faces)) sum += mesh.Parts[p].FaceCross(f) * mesh.Parts[p].WindingSign();
                    normal = EditablePart.Unit(sum);
                }
                var up = EditablePart.Unit(Vector3.TransformNormal(Vector3.UnitY, inverse));
                var u = Vector3.Cross(up, normal);
                u = u.LengthSquared() < 1e-6f ? EditablePart.Unit(Vector3.Cross(Vector3.UnitZ, normal)) : Vector3.Normalize(u);
                var vAxis = -Vector3.Normalize(Vector3.Cross(normal, u));   // V grows downwards (image rows)
                float angle = (OptFloat(c, "rotation") ?? 0) * MathF.PI / 180;
                if (angle != 0) { var turned = Quaternion.CreateFromAxisAngle(normal, angle); u = Vector3.Transform(u, turned); vAxis = Vector3.Transform(vAxis, turned); }
                int part = MeshSelection.Flatten(faces).Select(x => x.Part).FirstOrDefault();
                float density = mesh.Parts.Count > part ? mesh.Parts[part].TexelDensity() : 1;
                var scale = c.TryGetProperty("scale", out var sc) ? sc.ValueKind == JsonValueKind.Number ? new Vector2(sc.GetSingle()) : new Vector2(sc[0].GetSingle(), sc[1].GetSingle()) : new Vector2(density);
                var offset = c.TryGetProperty("offset", out var of) ? new Vector2(of[0].GetSingle(), of[1].GetSingle()) : Vector2.Zero;
                return await Do(s, new UvProjectOp(faces, Vector3.Zero, u, vAxis, scale, offset, Int(c, "channel", 0)));
            }
            case "api.meshedit.uv_transform":
            {
                var faces = Faces(s); var mesh = s.MeshEditResult!;
                int channel = Int(c, "channel", 0);
                var pivot = c.TryGetProperty("pivot", out var pv) ? new Vector2(pv[0].GetSingle(), pv[1].GetSingle()) : UvCenter(mesh, faces, channel);
                var scale = c.TryGetProperty("scale", out var sc) ? sc.ValueKind == JsonValueKind.Number ? new Vector2(sc.GetSingle()) : new Vector2(sc[0].GetSingle(), sc[1].GetSingle()) : Vector2.One;
                var offset = c.TryGetProperty("offset", out var of) ? new Vector2(of[0].GetSingle(), of[1].GetSingle()) : Vector2.Zero;
                float angle = (OptFloat(c, "rotation") ?? 0) * MathF.PI / 180;
                var matrix = Matrix3x2.CreateTranslation(-pivot) * Matrix3x2.CreateScale(scale) * Matrix3x2.CreateRotation(angle) * Matrix3x2.CreateTranslation(pivot + offset);
                return await Do(s, new UvTransformOp(faces, matrix, channel));
            }
            case "api.meshedit.add_faces":
            {
                if (!c.TryGetProperty("corners", out var corners) || corners.ValueKind != JsonValueKind.Array) throw new ArgumentException("corners: [[x, y, z], ...], three per face (game space)");
                var inverse = Inverse(s);
                var local = corners.EnumerateArray().Select(e => Vector3.Transform(Coordinates.GameToView(V3(e)), inverse)).ToImmutableArray();
                var facing = OptV3(c, "facing") is { } f ? Vector3.TransformNormal(Coordinates.GameToView(f), inverse) : (Vector3?)null;
                return await Do(s, new AddTrianglesOp(Int(c, "part", 0), local, facing));
            }
            case "api.meshedit.revert":
            {
                string id = s.MeshEditId!;
                var edit = s.Document.Project.Changes.Added[id].MeshEdit!;
                if (!s.ApplyDocument(() => s.Document.SetMeshEdit(id, edit with { Ops = [] }, "Revert mesh edit"))) return new { ok = false, error = s.Error };
                await WaitEdited(s);
                return Info(s);
            }
            case "api.meshedit.click":
            {
                // The edit mode's mouse (tests): a click at a viewport pixel, or a drawn opening.
                var pixel = Pixel(c, "pixel") ?? throw new ArgumentException("pixel: [x, y]");
                var viewport = v.ViewportPixels;
                if (Pixel(c, "to") is { } to)
                {
                    if (!s.BeginMeshCut(pixel, viewport)) return new { ok = false, error = "No surface of the mesh under that pixel." };
                    s.UpdateMeshCut(to, viewport);
                    bool cut = s.EndMeshCut();
                    await WaitEdited(s);
                    return new { ok = cut, info = Info(s) };
                }
                s.MeshEditClick(pixel, viewport, c.TryGetProperty("additive", out var add) && add.GetBoolean(), c.TryGetProperty("subtract", out var sub) && sub.GetBoolean());
                return new { ok = true, selected = SelectionJson(s) };
            }
            default: throw new InvalidOperationException("Unknown mesh edit command: " + op);
        }
    }

    private static Vector2? Pixel(JsonElement c, string name) => c.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Array && p.GetArrayLength() >= 2 ? new Vector2(p[0].GetSingle(), p[1].GetSingle()) : null;

    private static MeshSelectMode Mode(string mode) => mode.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? MeshSelectMode.Vertex : MeshSelectMode.Face;

    private static async Task WaitEdited(StudioSession s)
    {
        for (int i = 0; i < 600 && s.MeshEditId is { } id && (s.MeshEditLoading(id) || s.MeshEditing && s.MeshEditError(id) is null && s.MeshEditStale); i++) await Task.Delay(30);
        if (s.MeshEditId is { } edited && s.MeshEditError(edited) is { } error) throw new InvalidOperationException(error);
        if (s.MeshEditId is { } loading && s.MeshEditLoading(loading)) throw new TimeoutException("The mesh is still loading: try again.");
    }

    private async Task<object> Do(StudioSession s, MeshEditOp op)
    {
        int before = s.MeshEditResult?.TriangleCount ?? 0;
        if (!s.MeshEditDo(op)) return new { ok = false, error = s.Error };
        await WaitEdited(s);
        return new { ok = true, done = op.Describe(), faces_before = before, faces = s.MeshEditResult?.TriangleCount, selected = SelectionJson(s) };
    }

    private static Vector3 ViewScale(Vector3 game) => new(game.X, game.Z, game.Y);

    private static ImmutableArray<PartIndices> Faces(StudioSession s)
    {
        var faces = s.MeshSelectedFaces;
        if (faces.IsDefaultOrEmpty) throw new InvalidOperationException("Select faces first (select: {...}).");
        return faces;
    }

    private static Matrix4x4 Model(StudioSession s) => s.Document.TryGetObject(s.MeshEditId!)!.Transform.ViewMatrix;
    private static Matrix4x4 Inverse(StudioSession s) { Matrix4x4.Invert(Model(s), out var inverse); return inverse; }
    private static Vector3 Game(Vector3 view) => Coordinates.ViewToGame(view);
    private static Vector3 GameDirection(Vector3 view) => EditablePart.Unit(Coordinates.ViewToGame(view));

    private static Vector2 UvCenter(EditableMesh mesh, ImmutableArray<PartIndices> faces, int channel)
    {
        Vector2 sum = default; int n = 0;
        foreach (var (p, v) in MeshSelection.Flatten(MeshSelection.VerticesOfFaces(mesh, faces)))
        {
            var uvs = channel == 1 && mesh.Parts[p].Uv1 is { } uv1 ? uv1 : mesh.Parts[p].Uv0;
            sum += uvs[v]; n++;
        }
        return n > 0 ? sum / n : Vector2.Zero;
    }

    private static (int Part, int Face)? NearestFace(EditableMesh mesh, Vector3 point)
    {
        (int, int)? best = null; float nearest = float.PositiveInfinity;
        for (int p = 0; p < mesh.Parts.Count; p++)
            for (int f = 0; f < mesh.Parts[p].TriangleCount; f++)
            {
                var (a, b, c) = mesh.Parts[p].Triangle(f);
                float d = Vector3.DistanceSquared(ClosestOnTriangle(point, mesh.Parts[p].Positions[a], mesh.Parts[p].Positions[b], mesh.Parts[p].Positions[c]), point);
                if (d < nearest) { nearest = d; best = (p, f); }
            }
        return best;
    }

    // The point of a triangle nearest a point (Ericson, Real-Time Collision Detection 5.1.5).
    private static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a; var ac = c - a; var ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;
        var bp = p - b; float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return b;
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
        var cp = p - c; float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return c;
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + (c - b) * ((d4 - d3) / (d4 - d3 + (d5 - d6)));
        float denominator = 1 / (va + vb + vc);
        return a + ab * (vb * denominator) + ac * (vc * denominator);
    }

    // select: {by: all|none|box|facing|ray|part|faces|vertices, ..., grow: linked|coplanar, additive, subtract}.
    private static void Select(StudioSession s, JsonElement select)
    {
        var mesh = s.MeshEditResult ?? throw new InvalidOperationException("The mesh is still loading.");
        var model = Model(s); Matrix4x4.Invert(model, out var inverse);
        bool additive = select.TryGetProperty("additive", out var add) && add.GetBoolean();
        bool subtract = select.TryGetProperty("subtract", out var sub) && sub.GetBoolean();
        if (Str(select, "mode") is { } mode) s.MeshSelectMode = Mode(mode);
        bool faces = s.MeshSelectMode == MeshSelectMode.Face;
        ImmutableArray<PartIndices> found;
        switch ((Str(select, "by") ?? "all").ToLowerInvariant())
        {
            case "none": s.MeshSelectNone(); return;
            case "all": found = faces ? MeshSelection.Group(MeshSelection.AllFaces(mesh)) : MeshSelection.Group(mesh.Parts.SelectMany((p, i) => MeshSelection.UsedVertices(p).Select(v => (i, v)))); break;
            case "box":
            {
                // A box in game space: centre, size [x, y, z] (or one number), yaw in degrees.
                var center = Coordinates.GameToView(OptV3(select, "center") ?? throw new ArgumentException("select.center: [x, y, z]"));
                var size = select.TryGetProperty("size", out var sz) ? sz.ValueKind == JsonValueKind.Number ? new Vector3(sz.GetSingle()) : V3(sz) : new Vector3(1);
                float yaw = (OptFloat(select, "yaw") ?? 0) * MathF.PI / 180;
                var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
                var axes = new[] { Vector3.Transform(Vector3.UnitX, turn), Vector3.UnitY, Vector3.Transform(Vector3.UnitZ, turn) };
                var halfView = new Vector3(size.X, size.Z, size.Y) / 2;
                var local = new Vector3[3]; var half = new float[3];
                for (int i = 0; i < 3; i++) { var a = Vector3.TransformNormal(axes[i], inverse); float length = a.Length(); local[i] = a / length; half[i] = halfView[i] * length; }
                var rotation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(new Matrix4x4(local[0].X, local[0].Y, local[0].Z, 0, local[1].X, local[1].Y, local[1].Z, 0, local[2].X, local[2].Y, local[2].Z, 0, 0, 0, 0, 1)));
                var c = Vector3.Transform(center, inverse);
                found = faces ? MeshSelection.FacesInBox(mesh, c, rotation, new(half[0], half[1], half[2])) : MeshSelection.VerticesInBox(mesh, c, rotation, new(half[0], half[1], half[2]));
                break;
            }
            case "facing":
            {
                var direction = EditablePart.Unit(Vector3.TransformNormal(Coordinates.GameToView(OptV3(select, "direction") ?? throw new ArgumentException("select.direction: [x, y, z]")), inverse));
                var facingFaces = MeshSelection.FacesFacing(mesh, direction, OptFloat(select, "angle") ?? 30, s.MeshSelectedFaces.IsDefaultOrEmpty || !additive ? default : s.MeshSelectedFaces);
                found = faces ? facingFaces : MeshSelection.VerticesOfFaces(mesh, facingFaces);
                break;
            }
            case "ray":
            {
                var origin = Vector3.Transform(Coordinates.GameToView(OptV3(select, "origin") ?? throw new ArgumentException("select.origin")), inverse);
                var direction = Vector3.TransformNormal(Coordinates.GameToView(OptV3(select, "direction") ?? throw new ArgumentException("select.direction")), inverse);
                if (MeshSelection.Raycast(mesh, origin, direction) is not { } hit) throw new InvalidOperationException("The ray misses the mesh.");
                found = [new PartIndices(hit.Part, [hit.Face])];
                if (!faces) found = MeshSelection.VerticesOfFaces(mesh, found);
                break;
            }
            case "part":
            {
                int part = Int(select, "part", 0);
                if (part < 0 || part >= mesh.Parts.Count) throw new ArgumentException("select.part: 0 to " + (mesh.Parts.Count - 1));
                found = faces ? MeshSelection.Group(Enumerable.Range(0, mesh.Parts[part].TriangleCount).Select(f => (part, f))) : MeshSelection.Group(MeshSelection.UsedVertices(mesh.Parts[part]).Select(v => (part, v)));
                break;
            }
            case "faces" or "vertices" or "indices":
            {
                if (!select.TryGetProperty("items", out var items)) throw new ArgumentException("select.items: [[part, index], ...]");
                found = MeshSelection.Group(items.EnumerateArray().Select(e => (e[0].GetInt32(), e[1].GetInt32())));
                break;
            }
            default: throw new ArgumentException("select.by: all, none, box, facing, ray, part or items");
        }
        s.MeshSelect(found, additive, subtract);
        switch (Str(select, "grow"))
        {
            case "linked": s.MeshSelectLinked(coplanar: false); break;
            case "coplanar": s.MeshSelectLinked(coplanar: true); break;
        }
    }

    private static object SelectionJson(StudioSession s) => new
    {
        mode = s.MeshSelectMode.ToString().ToLowerInvariant(), count = MeshSelection.Count(s.MeshSelected),
        faces = MeshSelection.Count(s.MeshSelectedFaces), vertices = MeshSelection.Count(s.MeshSelectedVertices),
        center = s.MeshSelectionCenter is { } center ? A(Game(center)) : null
    };

    private object Info(StudioSession s)
    {
        string id = s.MeshEditId!;
        var item = s.Document.TryGetObject(id)!;
        var mesh = s.MeshEditResult; var source = s.EditedSource(id);
        var edit = s.Document.Project.Changes.Added.TryGetValue(id, out var added) ? added.MeshEdit : null;
        var slots = item.Mesh is { } reference ? s.Index?.PartSlots(reference.Hash) : null;
        object? bounds = null;
        if (mesh is not null)
        {
            var model = item.Transform.ViewMatrix;
            var b = mesh.Bounds;
            var corners = Enumerable.Range(0, 8).Select(i => Game(Vector3.Transform(new Vector3((i & 1) == 0 ? b.Min.X : b.Max.X, (i & 2) == 0 ? b.Min.Y : b.Max.Y, (i & 4) == 0 ? b.Min.Z : b.Max.Z), model))).ToArray();
            bounds = new { min = A(corners.Aggregate(Vector3.Min)), max = A(corners.Aggregate(Vector3.Max)) };
        }
        return new
        {
            ok = true, id, name = item.Name, mesh = source?.Path ?? item.Mesh?.Path, error = s.MeshEditError(id), loading = s.MeshEditLoading(id),
            faces = mesh?.TriangleCount, vertices = mesh?.VertexCount, bounds,
            parts = mesh?.Parts.Select((p, i) => new
            {
                part = i, chunk = p.Chunk, faces = p.TriangleCount, vertices = p.VertexCount,
                slots = slots?.ToDictionary(a => a.Key, a => source is not null && p.Chunk < source.SurfaceParts.Length && source.SurfaceParts[p.Chunk] is int sp && sp >= 0 && sp < a.Value.Length ? a.Value[sp] : null)
            }).ToArray(),
            operations = edit is null ? [] : MeshEdits.Ops(edit).Select(o => o.Describe()).ToArray(),
            selected = SelectionJson(s)
        };
    }
}
