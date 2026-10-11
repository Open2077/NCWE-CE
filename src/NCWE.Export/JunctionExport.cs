// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Numerics;
using NCWE.Core;
using NCWE.Formats;
using NCWE.Formats.CustomMeshes;
using WolvenKit.RED4.Types;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace NCWE.Export;

// The project's road junctions (RoadJunctions) as the game will have them: each junction a mesh
// cooked here, wearing its roads' street multilayers (JunctionStyle: its own multilayered
// material instances), placed by a static mesh node at its centre with box collision; the roads
// leave gaps there (RoadBuilder).
public sealed record JunctionExportResult(JunctionPlan Plan, IReadOnlyList<CookedFile> Files, IReadOnlyList<PreparedNode> Nodes, IReadOnlyList<AuthoredCollision> Collisions);

public static class JunctionExport
{
    public static async Task<JunctionExportResult> BuildAsync(ProjectData project, GameArchiveIndex index, ExportTracker tracker, CancellationToken cancellation)
    {
        var progress = tracker.Text;
        var inputs = new List<JunctionRoad>();
        var profiles = new Dictionary<ulong, RoadProfile>();
        async Task<RoadProfile?> ProfileOf(MeshReference type)
        {
            if (profiles.TryGetValue(type.Hash, out var known)) return known;
            if (index.Find(type.Hash) is not { } resource) return null;
            var template = await index.ReadMeshConcurrentAsync(resource.Hash, cancellation);
            return profiles[type.Hash] = RoadProfile.Of(template, RoadBending.AxisOf(resource.Path));
        }
        var joining = project.Changes.Roads.Values.Where(r => r.Joins).OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
        foreach (var road in joining)
            inputs.Add(new(road.Id, road.Points, await ProfileOf(road.Template) ?? throw new FileNotFoundException("Road type is not present in this archive profile: " + road.Template.Path))
            {
                // Its junctions wear its type's street materials.
                Style = await Task.Run(() => JunctionStyle.Of(index.StreetLayers(road.Template.Hash)), cancellation)
            });
        if (joining.Count > 0)
        {
            // The game's roads around them: road pieces of the sectors near the roads, unless the
            // project deleted, moved or replaced them; junctions are laid on top of those.
            progress?.Report("Finding the game's roads the project's roads meet...");
            var placements = await GamePlacementsNear(index, joining, tracker, cancellation);
            var excluded = project.Changes.Roads.Values.SelectMany(r => r.Replaces)
                .Concat(project.Changes.Modified.Where(e => e.Value.Deleted || e.Value.Transform is not null).Select(e => e.Key)).ToHashSet(StringComparer.Ordinal);
            foreach (var mesh in placements.Where(p => !p.Deformation.IsDefaultOrEmpty && p.Mesh is { } m && RoadBending.IsRoadPiece(m.Path)).Select(p => p.Mesh!).DistinctBy(m => m.Hash))
                await ProfileOf(mesh);
            inputs.AddRange(GameRoadPieces.Near(placements, joining, excluded, type => profiles.TryGetValue(type.Hash, out var p) ? p : null));
        }
        var plan = inputs.Count == 0 ? JunctionPlan.Empty : RoadJunctions.Find(inputs);
        var files = new List<CookedFile>(); var nodes = new List<PreparedNode>(); var collisions = new List<AuthoredCollision>();
        if (plan.Junctions.Length == 0) return new(plan, files, nodes, collisions);
        string work = Path.Combine(Path.GetTempPath(), "ncwe-junctions", Guid.NewGuid().ToString("N"));
        tracker.More(plan.Junctions.Length);
        try
        {
            int n = 0;
            foreach (var junction in plan.Junctions)
            {
                cancellation.ThrowIfCancellationRequested();
                n++;
                // Named by the project's roads that meet there.
                tracker.Item(string.Join(" × ", junction.Arms.Select(a => project.Changes.Roads.TryGetValue(a.RoadId, out var road) ? road.Name : null).OfType<string>().Distinct()),
                    $"Building junction {n} of {plan.Junctions.Length}...");
                var mesh = RoadJunctions.Mesh(junction);
                string id = Id(project.Id, junction.Key);
                string name = $"Junction {n}";
                var geometry = Geometry(mesh, junction.Center);
                var info = new CustomMeshInfo(id, name)
                {
                    Materials = [.. RoadJunctions.Materials.Select((m, part) => new CustomMaterial(m.Name)
                    {
                        Kind = CustomMaterialKind.Multilayer, MultilayerSetup = junction.Style[part].Setup, MultilayerMask = junction.Style[part].Mask
                    })],
                    Bounds = geometry.Bounds, Vertices = geometry.Submeshes.Sum(s => s.Positions.Length), Triangles = geometry.Submeshes.Sum(s => s.TriangleCount),
                    Submeshes = geometry.Submeshes.Length,
                };
                files.AddRange(await Task.Run(() => CustomMeshCooker.Cook(info, geometry, work, index, Path.Combine(work, id), null, cancellation), cancellation));
                var transform = PlacementTransform.Identity with { Position = junction.Center };
                var node = new worldStaticMeshNode { Mesh = new CResourceAsyncReference<CMesh>(info.MeshPath), MeshAppearance = "default", DebugName = name };
                nodes.Add(new PreparedNode("junction-" + id, node, transform, null, mesh.Bounds));
                int part = 0;
                foreach (var chunk in mesh.Collision.Select(s => s with { Position = s.Position - junction.Center }).Chunk(64))
                    collisions.Add(new AuthoredCollision($"c:junction-{id}-{part++:D2}", $"{name} collision", transform, [.. chunk]));
            }
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return new(plan, files, nodes, collisions);
    }

    // The placements of the city's detailed sectors (levels 0 and 1, where road pieces are) around
    // the roads.
    private static async Task<List<SourcePlacement>> GamePlacementsNear(GameArchiveIndex index, IReadOnlyList<RoadDefinition> roads, ExportTracker tracker, CancellationToken cancellation)
    {
        var world = index.Worlds.FirstOrDefault(w => w.Path.EndsWith(@"\03_night_city.streamingworld", StringComparison.OrdinalIgnoreCase));
        if (world is null) return [];
        var catalog = await index.LoadWorldCatalogAsync(world.Hash, null, cancellation);
        var spatial = new SectorSpatialIndex(catalog.Descriptors);
        var sectors = new HashSet<ulong>();
        foreach (var road in roads)
            foreach (var point in RoadSpline.Sample(road.Points, 30))
                foreach (var candidate in spatial.Query(point, 60, 4000).Candidates)
                    if (candidate.Level <= 1) sectors.Add(candidate.Sector.Hash);
        var placements = new List<SourcePlacement>();
        // Each sector read is an item of the junctions' stage.
        tracker.More(sectors.Count);
        foreach (var hash in sectors.Order())
        {
            tracker.Item(Path.GetFileNameWithoutExtension(index.Find(hash)?.Path ?? hash.ToString("x16")));
            placements.AddRange((await index.LoadSectorAsync(hash, cancellation)).Placements);
        }
        return placements;
    }

    // Stable across exports of the project (the junction's key names its roads and stations).
    internal static string Id(Guid project, string key)
    {
        ulong hash = CustomMeshInfo.Fnv1a64($"{project:N}:{key}");
        return $"j_{hash:x16}";
    }

    // The junction's parts (one per material that has triangles) around its centre, in view space
    // as the cooker takes them (glTF: Y up, counter-clockwise front faces), with tangents.
    internal static CustomGeometry Geometry(RoadJunctions.JunctionMesh mesh, Vector3 center)
    {
        var submeshes = ImmutableArray.CreateBuilder<CustomSubmesh>();
        List<uint>[] lists = [mesh.Asphalt, mesh.Sidewalk, mesh.Curb];
        for (int m = 0; m < lists.Length; m++)
        {
            if (lists[m].Count == 0) continue;
            var map = new Dictionary<uint, uint>();
            var positions = new List<Vector3>(); var normals = new List<Vector3>(); var uvs = new List<Vector2>();
            var indices = new uint[lists[m].Count];
            for (int i = 0; i < indices.Length; i++)
            {
                uint old = lists[m][i];
                if (!map.TryGetValue(old, out var index))
                {
                    map[old] = index = (uint)positions.Count;
                    positions.Add(Coordinates.GameToView(mesh.Positions[(int)old] - center));
                    normals.Add(Vector3.Normalize(Coordinates.GameToView(mesh.Normals[(int)old])));
                    uvs.Add(mesh.Uv[(int)old]);
                }
                indices[i] = index;
            }
            submeshes.Add(new CustomSubmesh(m, [.. positions], [.. normals], Tangents(positions, normals, uvs, indices), [.. uvs], indices));
        }
        return new(submeshes.ToImmutable());
    }

    // Per-vertex tangents along increasing U (accumulated over the triangles, made orthogonal to
    // the normal), W the bitangent's handedness.
    private static Vector4[] Tangents(List<Vector3> positions, List<Vector3> normals, List<Vector2> uvs, uint[] indices)
    {
        var tangent = new Vector3[positions.Count]; var bitangent = new Vector3[positions.Count];
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            int a = (int)indices[i], b = (int)indices[i + 1], c = (int)indices[i + 2];
            Vector3 e1 = positions[b] - positions[a], e2 = positions[c] - positions[a];
            Vector2 d1 = uvs[b] - uvs[a], d2 = uvs[c] - uvs[a];
            float det = d1.X * d2.Y - d2.X * d1.Y;
            if (MathF.Abs(det) < 1e-12f) continue;
            float r = 1 / det;
            var t = (e1 * d2.Y - e2 * d1.Y) * r; var s = (e2 * d1.X - e1 * d2.X) * r;
            foreach (int v in (ReadOnlySpan<int>)[a, b, c]) { tangent[v] += t; bitangent[v] += s; }
        }
        var result = new Vector4[positions.Count];
        for (int v = 0; v < result.Length; v++)
        {
            var n = normals[v];
            var t = tangent[v] - n * Vector3.Dot(n, tangent[v]);
            if (t.LengthSquared() < 1e-12f) t = Vector3.Cross(n, MathF.Abs(n.Y) < .9f ? Vector3.UnitY : Vector3.UnitX);
            t = Vector3.Normalize(t);
            float w = Vector3.Dot(Vector3.Cross(n, t), bitangent[v]) < 0 ? -1 : 1;
            result[v] = new(t, w);
        }
        return result;
    }
}
