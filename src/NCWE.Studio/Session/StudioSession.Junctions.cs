// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using NCWE.Core;
using NCWE.Render;

namespace NCWE.Studio.Session;

// Junctions (RoadJunctions) between the drawn roads: found again whenever a road changes (its
// drawing ghost and dragged preview included). Roads are built with gaps there (UpdateRoadAssets)
// and each junction is a generated mesh wearing its roads' street materials (JunctionStyle),
// baked here.
internal sealed partial class StudioSession
{
    public JunctionPlan Junctions { get; private set; } = JunctionPlan.Empty;
    private readonly Dictionary<ulong, NCWE.Core.RoadProfile> _roadProfiles = [];
    // Road types' street materials, read with their template (null: none).
    private readonly Dictionary<ulong, JunctionStyle?> _roadStyles = [];
    private readonly Dictionary<string, (ulong Key, MeshAsset Mesh, Vector3 Origin)> _junctionMeshes = new(StringComparer.Ordinal);
    // Per style, its parts' surfaces (null while they bake).
    private readonly Dictionary<JunctionStyle, SurfaceTexture?[]?> _junctionTextures = [];
    // Automation: how many street surfaces are baked (-1: not asked for yet).
    public int JunctionTextureCount => _junctionTextures.Count == 0 ? -1 : _junctionTextures.Values.Sum(t => t?.Count(x => x is not null) ?? 0);
    public const string JunctionPrefix = "junction:";

    // A road type's cross-section, once its template is loaded.
    public NCWE.Core.RoadProfile? RoadProfileOf(MeshReference template)
    {
        if (_roadProfiles.TryGetValue(template.Hash, out var known)) return known;
        if (!_roadTemplates.TryGetValue(template.Hash, out var mesh)) return null;
        var axis = RoadBending.AxisOf(mesh.ResourcePath);
        NCWE.Core.RoadProfile profile;
        try { profile = NCWE.Core.RoadProfile.Of(mesh, axis); }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException) { profile = NCWE.Core.RoadProfile.Flat(MathF.Max(.5f, RoadBending.HalfWidth(mesh, axis))); }
        return _roadProfiles[template.Hash] = profile;
    }

    // The junctions of these roads (those whose type is loaded; overpasses take no part), with
    // each other and with the game's roads around them (laid on top of those).
    private JunctionPlan PlanJunctions(IEnumerable<RoadDefinition> roads)
    {
        var joining = roads.Where(r => r.Joins).ToList();
        var inputs = new List<JunctionRoad>();
        foreach (var road in joining)
            if (RoadProfileOf(road.Template) is { } profile) inputs.Add(new(road.Id, road.Points, profile) { Style = _roadStyles.GetValueOrDefault(road.Template.Hash) });
        if (inputs.Count == 0) return JunctionPlan.Empty;
        var excluded = Roads.Values.SelectMany(r => r.Replaces).Concat(Document.Project.Changes.Modified.Where(e => e.Value.Deleted || e.Value.Transform is not null).Select(e => e.Key))
            .ToHashSet(StringComparer.Ordinal);
        inputs.AddRange(GameRoadPieces.Near(GameRoadPlacements(), joining, excluded, template =>
        {
            if (RoadProfileOf(template) is { } profile) return profile;
            LoadRoadTemplate(template); return null;
        }));
        return RoadJunctions.Find(inputs);
    }

    // Automation: the game roads the junctions consider around the project's roads (chained).
    public IEnumerable<object> GameRoadsAround() =>
        GameRoadPieces.Near(GameRoadPlacements(), [.. Roads.Values.Where(r => r.Joins)], new HashSet<string>(StringComparer.Ordinal), t => RoadProfileOf(t))
            .Select(r => new { r.Id, points = r.Points.Length, length = MathF.Round(RoadSpline.Length(r.Points), 1), outer = r.Profile.Outer, carriage = r.Profile.Carriage,
                start = new[] { r.Points[0].X, r.Points[0].Y }, end = new[] { r.Points[^1].X, r.Points[^1].Y } });

    // Automation: the road pieces near a point, with their meshes (chaining checks).
    public IEnumerable<object> GamePiecesNear(Vector2 at, float radius) => Document.ResidentSectors.Values.SelectMany(s => s.Placements)
        .Where(p => !p.Deformation.IsDefaultOrEmpty && p.Mesh is not null)
        .Select(p => (p, line: GameRoadPieces.Line(p)))
        .Where(x => x.line.Any(q => Vector2.Distance(new(q.X, q.Y), at) < radius))
        .Select(x => new { x.p.Id, mesh = x.p.Mesh!.Path, road = RoadBending.IsRoadPiece(x.p.Mesh.Path), profile = RoadProfileOf(x.p.Mesh) is { } pr ? pr.Outer : -1,
            start = new[] { x.line[0].X, x.line[0].Y, x.line[0].Z }, end = new[] { x.line[^1].X, x.line[^1].Y, x.line[^1].Z } });

    // The game's road pieces among the streamed sectors (bended nodes of road types).
    private List<SourcePlacement> _gameRoadPlacements = [];
    private (long Revision, int Sectors) _gameRoadPlacementsKey = (-1, -1);
    private List<SourcePlacement> GameRoadPlacements()
    {
        var key = (Document.Revision, Document.ResidentSectors.Count);
        if (key == _gameRoadPlacementsKey) return _gameRoadPlacements;
        _gameRoadPlacementsKey = key;
        return _gameRoadPlacements = [.. Document.ResidentSectors.Values.SelectMany(s => s.Placements)
            .Where(p => !p.Deformation.IsDefaultOrEmpty && p.Mesh is { } mesh && RoadBending.IsRoadPiece(mesh.Path))];
    }

    private static ulong JunctionKey(string key)
    {
        ulong hash = 14695981039346656037UL;
        foreach (char ch in JunctionPrefix + key) { hash ^= ch; hash *= 1099511628211UL; }
        return hash | 1UL << 62 | 1UL << 63;
    }

    // Replaces the junction meshes with the plan's. True: the drawable set changed.
    private bool UpdateJunctionMeshes(JunctionPlan plan, long generation)
    {
        bool changed = false;
        foreach (var (key, _, _) in _junctionMeshes.Values) { _assets.Remove(key); changed = true; }
        _junctionMeshes.Clear();
        foreach (var style in plan.Junctions.Select(j => j.Style).Distinct()) EnsureJunctionTextures(style);
        foreach (var junction in plan.Junctions)
        {
            try
            {
                var mesh = JunctionAsset(junction, RoadJunctions.Mesh(junction), generation);
                ulong key = JunctionKey(junction.Key);
                _junctionMeshes[junction.Key] = (key, mesh, junction.Center);
                _assets[key] = mesh;
                changed = true;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException) { Error = Loc.F("Junction: {0}", ex.Message); }
        }
        return changed;
    }

    // The junction's parts (asphalt, sidewalk, curb: RoadJunctions.Materials) around its centre,
    // in the editor's space, with its style's surfaces baked onto them.
    private MeshAsset JunctionAsset(Junction junction, RoadJunctions.JunctionMesh mesh, long generation)
    {
        var parts = new List<MeshPart>(); var textures = new List<SurfaceTexture?>();
        var baked = _junctionTextures.GetValueOrDefault(junction.Style);
        List<uint>[] lists = [mesh.Asphalt, mesh.Sidewalk, mesh.Curb];
        for (int m = 0; m < lists.Length; m++)
        {
            if (lists[m].Count == 0) continue;
            var map = new Dictionary<uint, uint>(); var vertices = new List<MeshVertex>(); var uv = new List<Vector2>();
            var indices = new uint[lists[m].Count];
            for (int i = 0; i < indices.Length; i++)
            {
                uint old = lists[m][i];
                if (!map.TryGetValue(old, out var index))
                {
                    map[old] = index = (uint)vertices.Count;
                    vertices.Add(new(Coordinates.GameToView(mesh.Positions[(int)old] - junction.Center), Coordinates.GameToView(mesh.Normals[(int)old])));
                    uv.Add(mesh.Uv[(int)old]);
                }
                indices[i] = index;
            }
            // The viewport draws the game's winding (glTF's reversed, as custom meshes are shown).
            for (int k = 0; k + 2 < indices.Length; k += 3) (indices[k], indices[k + 1]) = (indices[k + 1], indices[k]);
            parts.Add(new MeshPart(RoadJunctions.Materials[m].Name, 1, [.. vertices], indices) { Uv = [.. uv] });
            textures.Add(baked?[m]);
        }
        var bounds = Bounds3.From(parts.SelectMany(p => p.Vertices).Select(v => v.Position));
        return new MeshAsset($"Junction · {junction.Arms.Length} roads", $"junction-{generation}", [.. parts], bounds, "Generated in code", 0)
        {
            PartTextures = textures.Any(t => t is not null) ? textures : null,
            AppearanceColors = new Dictionary<string, Vector4[]> { ["default"] = [.. parts.Select(p => p.Name == "asphalt" ? new Vector4(.24f, .24f, .25f, 1) : new Vector4(.52f, .51f, .5f, 1))] },
        };
    }

    // A style's street surfaces, baked once; the junctions are built again with them when they arrive.
    private async void EnsureJunctionTextures(JunctionStyle style)
    {
        if (_junctionTextures.ContainsKey(style) || Index is not { } index) return;
        _junctionTextures[style] = null;
        try
        {
            static ulong Hash(string path) => WolvenKit.Common.FNV1A.FNV1A64HashAlgorithm.HashString(path);
            var baked = await Task.Run(() => Enumerable.Range(0, RoadJunctions.Materials.Length).Select(part =>
            {
                var layer = style[part];
                try { return index.BakeMultilayer($"junction-{RoadJunctions.Materials[part].Name}-{Hash(layer.Setup):x16}-{Hash(layer.Mask):x16}", Hash(layer.Setup), Hash(layer.Mask), 1024, _shutdown.Token); }
                catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException) { return null; }
            }).ToArray());
            _junctionTextures[style] = baked;
            _roadsDirty = true;
        }
        catch (OperationCanceledException) { }
    }

    private bool AddJunctionAssets()
    {
        bool changed = false;
        foreach (var (key, mesh, _) in _junctionMeshes.Values)
            if (!_assets.ContainsKey(key)) { _assets[key] = mesh; changed = true; }
        return changed;
    }

    private void AddJunctionInstances(List<RenderPlacement> instances)
    {
        foreach (var (name, (key, _, origin)) in _junctionMeshes)
            instances.Add(new(JunctionPrefix + name, key, Matrix4x4.CreateTranslation(Coordinates.GameToView(origin)), false, TypeKey: (1UL << 32) | 0x4A5A6AUL));
    }
}
