// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.CompilerServices;
using NCWE.Core;
using NCWE.Formats;
using NCWE.Render;

namespace NCWE.Studio.Session;

// How collision and occluders read on screen: shaded translucent volumes (faces lit from above so
// boxes read as boxes) with only their outline edges (box edges, mesh creases and borders: no
// triangle diagonals), hidden by walls unless X-ray is on. A selection fades the rest; the shape
// under the cursor is highlighted and named. A shape around the camera keeps its outline only (its
// faces would tint the whole view).
internal sealed partial class StudioSession
{
    // Outlines stay faintly visible through walls: off for collision (the levels below the floor
    // would show through it), on for occluders (they sit inside the buildings they stand for).
    public bool CollisionXray { get; set; }
    public bool OccluderXray { get; set; } = true;
    public bool LayerXray
    {
        get => Layer == EditLayer.Occlusion ? OccluderXray : CollisionXray;
        set { if (Layer == EditLayer.Occlusion) OccluderXray = value; else CollisionXray = value; }
    }
    public bool FadeUnselected { get; set; } = true;
    // The world darkened in the Collision and Occlusion layers.
    public bool DimWorld { get; set; } = true;
    public bool ShowGameCollision { get; set; } = true;
    public bool ShowProjectCollision { get; set; } = true;
    // Filters (the colour legends' checkboxes): collision presets and occluder types not drawn
    // nor picked. Change LayerDisplayVersion after editing them.
    public HashSet<string> HiddenCollisionPresets { get; } = new(StringComparer.Ordinal);
    public HashSet<string> HiddenOccluderTypes { get; } = new(StringComparer.Ordinal);
    public bool CollisionCapped => _collisionCapped;
    public bool OccluderCapped => _occluderCapped;
    // Occluders whose largest side is bigger are not drawn (0: no limit).
    public float MaxOccluderSize { get; set; }
    public long LayerDisplayVersion { get; set; }
    // What the cursor is over in the Collision and Occlusion layers (shown next to it).
    public string? HoverLabel { get; private set; }

    private static readonly Vector3 LightDirection = Vector3.Normalize(new(.35f, .45f, .82f));
    private const float FillAlpha = .2f, ProjectFillAlpha = .28f, FadedAlpha = .05f, SelectedAlpha = .38f, HoverAlpha = .32f;
    private static readonly Vector4 HoverOutline = new(1f, 1f, 1f, 1);

    // Faces lit from above: the colour of a face by its normal (either side).
    private static Vector4 Shaded(Vector4 color, Vector3 a, Vector3 b, Vector3 c, float alpha)
    {
        var n = Vector3.Cross(b - a, c - a);
        float light = n.LengthSquared() < 1e-12f ? .75f : .5f + .5f * MathF.Abs(Vector3.Dot(Vector3.Normalize(n), LightDirection));
        return new(color.X * light, color.Y * light, color.Z * light, alpha);
    }

    // Lines fade by their alpha (they are blended), never darkened.
    private static Vector4 Dimmed(Vector4 color, float factor) => color with { W = color.W * factor };

    // --- Shape geometry (local space of the shape, game convention) --------------------------------------

    private sealed record ShapeMesh(Vector3[] Vertices, int[] Triangles, int[] Edges);

    private static readonly ConditionalWeakTable<CollisionMeshData, ShapeMesh> CookedShapes = new();
    private static readonly ConditionalWeakTable<MeshAsset, ShapeMesh> OccluderShapes = new();

    // Edges between faces bent by more than ~25°, and border edges: what outlines a mesh.
    private static int[] FeatureEdges(Vector3[] vertices, int[] triangles)
    {
        var faces = new Dictionary<long, (Vector3 Normal, int Count, bool Sharp)>();
        // Welded positions so split vertices still share their edges.
        var weld = new Dictionary<(int, int, int), int>();
        var id = new int[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            var v = vertices[i];
            var key = ((int)MathF.Round(v.X * 1000), (int)MathF.Round(v.Y * 1000), (int)MathF.Round(v.Z * 1000));
            if (!weld.TryGetValue(key, out int w)) weld[key] = w = i;
            id[i] = w;
        }
        for (int t = 0; t + 2 < triangles.Length; t += 3)
        {
            var a = vertices[triangles[t]]; var b = vertices[triangles[t + 1]]; var c = vertices[triangles[t + 2]];
            var n = Vector3.Cross(b - a, c - a);
            if (n.LengthSquared() < 1e-14f) continue;
            n = Vector3.Normalize(n);
            for (int k = 0; k < 3; k++)
            {
                int p = id[triangles[t + k]], q = id[triangles[t + (k + 1) % 3]];
                if (p == q) continue;
                long key = p < q ? ((long)p << 32) | (uint)q : ((long)q << 32) | (uint)p;
                if (faces.TryGetValue(key, out var seen)) faces[key] = (seen.Normal, seen.Count + 1, seen.Sharp || MathF.Abs(Vector3.Dot(seen.Normal, n)) < .9f);
                else faces[key] = (n, 1, false);
            }
        }
        var edges = new List<int>();
        foreach (var (key, face) in faces)
            if (face.Count == 1 || face.Sharp) { edges.Add((int)(key >> 32)); edges.Add((int)(key & 0xffffffff)); }
        return [.. edges];
    }

    private static ShapeMesh Cooked(CollisionMeshData mesh) => CookedShapes.GetValue(mesh, m => new(m.Vertices, m.Indices, FeatureEdges(m.Vertices, m.Indices)));

    private static ShapeMesh BoxMesh(Vector3 h)
    {
        var v = new Vector3[8];
        for (int i = 0; i < 8; i++) v[i] = new((i & 1) == 0 ? -h.X : h.X, (i & 2) == 0 ? -h.Y : h.Y, (i & 4) == 0 ? -h.Z : h.Z);
        int[] t = [0, 1, 3, 0, 3, 2, 4, 6, 7, 4, 7, 5, 0, 4, 5, 0, 5, 1, 2, 3, 7, 2, 7, 6, 0, 2, 6, 0, 6, 4, 1, 5, 7, 1, 7, 3];
        int[] e = [0, 1, 2, 3, 4, 5, 6, 7, 0, 2, 1, 3, 4, 6, 5, 7, 0, 4, 1, 5, 2, 6, 3, 7];
        return new(v, t, e);
    }

    // A capsule along X (half length h between the cap centres, radius r); h = 0 is a sphere.
    private static ShapeMesh RoundMesh(float r, float h)
    {
        const int around = 16, rings = 5;   // rings per hemisphere
        var v = new List<Vector3>(); var t = new List<int>(); var e = new List<int>();
        // Rings from the -X pole to the +X pole: hemisphere, (cylinder), hemisphere.
        var ringX = new List<(float X, float Radius)>();
        for (int i = 0; i <= rings; i++) { float a = -MathF.PI / 2 + i * MathF.PI / 2 / rings; ringX.Add((-h + r * MathF.Sin(a), r * MathF.Cos(a))); }
        for (int i = 0; i <= rings; i++) { float a = i * MathF.PI / 2 / rings; ringX.Add((h + r * MathF.Sin(a), r * MathF.Cos(a))); }
        foreach (var (x, radius) in ringX)
            for (int k = 0; k < around; k++) { float a = k * MathF.Tau / around; v.Add(new(x, radius * MathF.Cos(a), radius * MathF.Sin(a))); }
        for (int ring = 0; ring + 1 < ringX.Count; ring++)
            for (int k = 0; k < around; k++)
            {
                int a = ring * around + k, b = ring * around + (k + 1) % around, c = (ring + 1) * around + k, d = (ring + 1) * around + (k + 1) % around;
                t.AddRange([a, b, d, a, d, c]);
            }
        // Outline: the equator rings of both caps, four lines along it, and two meridians.
        foreach (int ring in new[] { rings, rings + 1 })
            for (int k = 0; k < around; k++) { e.Add(ring * around + k); e.Add(ring * around + (k + 1) % around); }
        foreach (int k in new[] { 0, around / 4, around / 2, 3 * around / 4 })
            for (int ring = 0; ring + 1 < ringX.Count; ring++) { e.Add(ring * around + k); e.Add((ring + 1) * around + k); }
        return new([.. v], [.. t], [.. e]);
    }

    private ShapeMesh? ShapeGeometry(CollisionShape shape, ulong cookedSector) => shape.Kind switch
    {
        CollisionShapeKind.Box => BoxMesh(shape.Size),
        CollisionShapeKind.Sphere => RoundMesh(shape.Size.X, 0),
        CollisionShapeKind.Capsule => RoundMesh(shape.Size.X, shape.Size.Y),
        _ => Cooked(cookedSector, shape.MeshHash) is { } mesh ? Cooked(mesh) : null
    };

    // Lines and faces of one shape mesh under a model matrix (game space), into view space.
    private static void AddShape(List<DebugLine> lines, List<DebugTriangle>? fills, ShapeMesh mesh, Matrix4x4 model, Vector4 lineColor, Vector4 fillColor, float alpha, Vector3 eye)
    {
        var world = new Vector3[mesh.Vertices.Length];
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        for (int i = 0; i < world.Length; i++) { world[i] = Vector3.Transform(mesh.Vertices[i], model); min = Vector3.Min(min, world[i]); max = Vector3.Max(max, world[i]); }
        for (int i = 0; i + 1 < mesh.Edges.Length; i += 2)
            lines.Add(new(Coordinates.GameToView(world[mesh.Edges[i]]), Coordinates.GameToView(world[mesh.Edges[i + 1]]), lineColor));
        if (fills is null || alpha <= 0) return;
        // From inside, the faces would cover the whole view: outline only.
        if (eye.X > min.X - .3f && eye.Y > min.Y - .3f && eye.Z > min.Z - .3f && eye.X < max.X + .3f && eye.Y < max.Y + .3f && eye.Z < max.Z + .3f) return;
        for (int t = 0; t + 2 < mesh.Triangles.Length; t += 3)
        {
            var a = world[mesh.Triangles[t]]; var b = world[mesh.Triangles[t + 1]]; var c = world[mesh.Triangles[t + 2]];
            fills.Add(new(Coordinates.GameToView(a), Coordinates.GameToView(b), Coordinates.GameToView(c), Shaded(fillColor, a, b, c, alpha)));
        }
    }

    internal static string PresetOf(CollisionShape shape) => shape.Preset.Length > 0 ? shape.Preset : CollisionPresets.Default;

    // One collision actor: each shape (disabled ones grey, outline only; hidden presets skipped).
    private void AddCollisionActor(List<DebugLine> lines, List<DebugTriangle>? fills, PlacementTransform actor, ImmutableArray<CollisionShape> shapes, ulong cookedSector,
        Vector4? color, ImmutableArray<int> disabled, float alpha, float lineDim, Vector3 eye, IReadOnlySet<string>? hidden = null)
    {
        var actorMatrix = actor.GameMatrix;
        for (int s = 0; s < shapes.Length; s++)
        {
            var shape = shapes[s];
            if (hidden is { Count: > 0 } && hidden.Contains(PresetOf(shape))) continue;
            bool off = !disabled.IsDefaultOrEmpty && disabled.Contains(s);
            var c = off ? DisabledCollision : color ?? CollisionPresets.Color(shape.Preset);
            var model = Matrix4x4.CreateFromQuaternion(shape.Rotation) * Matrix4x4.CreateTranslation(shape.Position) * actorMatrix;
            if (ShapeGeometry(shape, cookedSector) is not { } mesh)
            {
                // Not decoded yet (or missing): a small cross marks the shape.
                var p = Vector3.Transform(Vector3.Zero, model);
                foreach (var d in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
                    lines.Add(new(Coordinates.GameToView(p - d * .25f), Coordinates.GameToView(p + d * .25f), c));
                continue;
            }
            AddShape(lines, fills, mesh, model, Dimmed(c, lineDim), c, off ? 0 : alpha, eye);
        }
    }

    // --- Occluders ---------------------------------------------------------------------------------------

    private static ShapeMesh OccluderShape(MeshAsset mesh) => OccluderShapes.GetValue(mesh, m =>
    {
        uint lod = m.LodMasks.FirstOrDefault(1u);
        var vertices = new List<Vector3>(); var triangles = new List<int>();
        foreach (var part in m.Parts.Where(p => p.LodMask == lod))
        {
            int offset = vertices.Count;
            foreach (var v in part.Vertices) vertices.Add(Coordinates.ViewToGame(v.Position));
            foreach (var i in part.Indices) triangles.Add((int)i + offset);
        }
        var v2 = vertices.ToArray(); var t2 = triangles.ToArray();
        return new(v2, t2, FeatureEdges(v2, t2));
    });

    // An occluder instance's size (its largest side, metres).
    private static float OccluderSize(ShapeMesh mesh, PlacementTransform instance)
    {
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        var model = instance.GameMatrix;
        foreach (var v in mesh.Vertices) { var w = Vector3.Transform(v, model); min = Vector3.Min(min, w); max = Vector3.Max(max, w); }
        var size = max - min;
        return MathF.Max(size.X, MathF.Max(size.Y, size.Z));
    }

    // --- Hover --------------------------------------------------------------------------------------------

    private string? _hoverShape;

    // The collision or occluder under the cursor and what it is (Collision and Occlusion layers).
    private void UpdateLayerHover(Vector2? pixel, Vector2 viewport)
    {
        string? id = pixel is { } p ? Layer == EditLayer.Collision ? PickCollision(p, viewport) : PickOccluder(p, viewport) : null;
        _hoverShape = id;
        HoverLabel = id is null ? null : Document.TryGetObject(id) is { } item ? Describe(item) : null;
    }

    private string Describe(ScenePlacement item)
    {
        if (item.IsCollision)
        {
            var shapes = item.CollisionShapes;
            string first = shapes.IsDefaultOrEmpty ? "" : ShapeText(shapes[0]) + (shapes.Length > 1 ? Loc.F(" + {0} shape{1}", shapes.Length - 1, (shapes.Length == 2 ? "" : "s")) : "");
            string preset = shapes.IsDefaultOrEmpty ? "" : shapes[0].Preset.Length > 0 ? shapes[0].Preset : Loc.T("default preset");
            string material = shapes.IsDefaultOrEmpty ? "" : shapes[0].Material.Length > 0 ? shapes[0].Material.Replace(".physmat", "") : Loc.T("default material");
            return $"{(item.AuthoredCollision is not null ? Loc.T("Project collision") : Loc.T("Game collision"))} · {item.Name}\n{first}\n{preset} · {material}";
        }
        string type = OccluderType(item);
        string size = OccluderMesh(OccluderMeshHash(item)) is { } mesh ? $" · {OccluderInstances(item).Select(i => OccluderSize(OccluderShape(mesh), i)).DefaultIfEmpty().Max():0.#} m" : "";
        int count = item.Original?.InstanceOffsets.IsDefaultOrEmpty == false ? item.Original.InstanceOffsets.Length : 1;
        return $"{(item.Element is not null ? Loc.T("Project occluder") : Loc.T("Game occluder"))} · {item.Name}\n{type}{size}" + (count > 1 ? Loc.F(" · {0} boxes in one node", count) : "");
    }

    private static string ShapeText(CollisionShape shape) => shape.Kind switch
    {
        CollisionShapeKind.Box => Loc.F("Box {0:0.##} × {1:0.##} × {2:0.##} m", shape.Size.X * 2, shape.Size.Y * 2, shape.Size.Z * 2),
        CollisionShapeKind.Sphere => Loc.F("Sphere Ø {0:0.##} m", shape.Size.X * 2),
        CollisionShapeKind.Capsule => Loc.F("Capsule Ø {0:0.##} m × {1:0.##} m", shape.Size.X * 2, (shape.Size.Y + shape.Size.X) * 2),
        CollisionShapeKind.ConvexMesh => Loc.T("Convex mesh"),
        _ => Loc.T("Triangle mesh")
    };
}
