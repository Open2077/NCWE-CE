// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Numerics;
using NCWE.Core;
using NCWE.Formats;
using NCWE.Render;

namespace NCWE.Studio.Session;

// Road editing: roads are documents' RoadDefinitions; each one is drawn as bended segments
// of its template (generated here on the UI thread, a few milliseconds per road) and edited
// through control-point handles. Rendered placements use ids "road:<road id>:<segment>".
internal sealed partial class StudioSession
{
    public const string RoadPrefix = "road:";
    public string? SelectedRoadId { get; private set; }
    public int SelectedRoadPoint { get; private set; } = -1;
    // Template for new roads (chosen in the Roads panel).
    public MeshReference? RoadTemplate { get; set; }
    // How new roads shape the terrain (null: they leave it alone).
    public RoadGround? NewRoadGround { get; set; } = new();
    // While a control point is dragged, the road is drawn from this preview; committed on release.
    private RoadDefinition? _roadPreview;
    // Drawing mode: clicks on the ground append points to the selected road (or start one).
    public bool RoadDrawing { get; private set; }
    private Vector3? _roadFirstPoint;
    public long RoadVersion { get; private set; }
    // While drawing, the road with the point under the cursor (a new road: GhostRoadId); its
    // meshes follow it a few times a second (_roadGhostShown), its outline every frame.
    public const string GhostRoadId = "r:~ghost";
    private RoadDefinition? _roadGhost, _roadGhostShown;
    private object? _roadsGhostBuilt;
    private double _roadGhostShownAt;

    private readonly Dictionary<ulong, MeshAsset> _roadTemplates = [];
    private readonly Dictionary<ulong, RoadRig?> _roadRigs = [];
    private readonly Dictionary<ulong, string> _roadTemplateErrors = [];
    private readonly HashSet<ulong> _roadTemplateLoading = [];
    private sealed record RoadMeshes(RoadDefinition Road, ImmutableArray<(float From, float To)> Gaps, (ulong Key, MeshAsset Mesh, Vector3 Origin, ImmutableArray<Matrix4x4> Frames)[] Segments);
    private readonly Dictionary<string, RoadMeshes> _roadMeshes = new(StringComparer.Ordinal);
    private object? _roadsBuiltFrom, _roadsPreviewBuilt;
    private bool _roadsDirty = true;
    private long _roadGeneration;

    public IReadOnlyDictionary<string, RoadDefinition> Roads => Document.Project.Changes.Roads;
    public RoadDefinition? SelectedRoad => SelectedRoadId is { } id ? RoadById(id) : null;
    public Vector3? FirstDrawnPoint => _roadFirstPoint;
    private RoadDefinition? RoadById(string id)
        => _roadPreview?.Id == id ? _roadPreview : Roads.TryGetValue(id, out var road) ? road : null;
    public static string? RoadIdOf(string? placementId)
    {
        if (placementId is null || !placementId.StartsWith(RoadPrefix, StringComparison.Ordinal)) return null;
        int end = placementId.LastIndexOf(':');
        return end > RoadPrefix.Length ? placementId[RoadPrefix.Length..end] : null;
    }

    // Template length (m) once loaded, for the panel.
    public float? RoadTemplateLength(MeshReference template)
    {
        if (_roadTemplates.TryGetValue(template.Hash, out var mesh)) return RoadBending.Length(mesh, RoadBending.AxisOf(mesh.ResourcePath));
        LoadRoadTemplate(template); return null;
    }
    public string? RoadTemplateError(MeshReference template) => _roadTemplateErrors.GetValueOrDefault(template.Hash);
    public float? RoadTemplateHalfWidth(MeshReference template)
        => _roadTemplates.TryGetValue(template.Hash, out var mesh) ? RoadBending.HalfWidth(mesh, RoadBending.AxisOf(mesh.ResourcePath)) : null;
    // Exported roads need a piece rigged for bending (one frame per bone); null while loading.
    public bool? RoadExportable(MeshReference template) => _roadTemplates.ContainsKey(template.Hash) ? _roadRigs.GetValueOrDefault(template.Hash) is not null : null;

    private async void LoadRoadTemplate(MeshReference template)
    {
        if (_roadTemplates.ContainsKey(template.Hash) || _roadTemplateErrors.ContainsKey(template.Hash) || Index is null || !_roadTemplateLoading.Add(template.Hash)) return;
        try
        {
            // Pieces cooked for bending carry the game's materials: load them with UVs and baked surfaces.
            var mesh = RoadBending.AxisOf(template.Path) == RoadBending.Axis.Y ? await Index.ReadSurfacedMeshAsync(template.Hash, 1024, _shutdown.Token)
                : _source is { } source ? await source.ReadMeshAsync(template.Hash, 96L << 20, 96L << 20, _shutdown.Token)
                : await Index.ReadMeshConcurrentAsync(template.Hash, _shutdown.Token, 96L << 20, 96L << 20);
            var index = Index!;
            bool cable = CableBending.IsCable(mesh.ResourcePath);
            _roadRigs[template.Hash] = cable || RoadBending.AxisOf(mesh.ResourcePath) == RoadBending.Axis.Y ? await Task.Run(() => index.ReadRoadRig(template.Hash)) : null;
            // Its street materials, which its junctions wear (a cable meets nothing).
            if (!cable) _roadStyles[template.Hash] = await Task.Run(() => JunctionStyle.Of(index.StreetLayers(template.Hash)));
            _roadTemplates[template.Hash] = mesh; _roadsDirty = true; RoadVersion++;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _roadTemplateErrors[template.Hash] = ex.Message; RoadVersion++; }
        finally { _roadTemplateLoading.Remove(template.Hash); }
    }

    private static ulong RoadKey(string id, int segment)
    {
        ulong hash = 14695981039346656037UL;
        foreach (char ch in RoadPrefix + id + ":" + segment) { hash ^= ch; hash *= 1099511628211UL; }
        return hash | 1UL << 62 | 1UL << 63;
    }

    // The roads as drawn: each stored road, or its drawing ghost or dragged preview; a new road
    // being drawn is its ghost alone.
    private IEnumerable<RoadDefinition> DrawnRoads(RoadDefinition? ghost)
    {
        foreach (var stored in Roads.Values)
            yield return ghost?.Id == stored.Id ? ghost : _roadPreview?.Id == stored.Id ? _roadPreview : stored;
        if (ghost is { Id: GhostRoadId }) yield return ghost;
    }

    // Regenerates the meshes of roads that changed and updates the drawable set.
    private void UpdateRoadAssets(double now)
    {
        // The drawing ghost's meshes follow the cursor a few times a second: bending a long road
        // takes a while, its outline (BuildRoadOverlay) is drawn every frame.
        if (!ReferenceEquals(_roadGhost, _roadGhostShown) && now >= _roadGhostShownAt)
        {
            _roadGhostShown = _roadGhost;
            _roadGhostShownAt = now + (_roadGhost is { } g && RoadSegmentCount(g.Id) > 20 ? .35 : .12);
        }
        var roads = Roads;
        if (!_roadsDirty && ReferenceEquals(roads, _roadsBuiltFrom) && ReferenceEquals(_roadPreview, _roadsPreviewBuilt) && ReferenceEquals(_roadGhostShown, _roadsGhostBuilt)) return;
        _roadsDirty = false; _roadsBuiltFrom = roads; _roadsPreviewBuilt = _roadPreview; _roadsGhostBuilt = _roadGhostShown;
        bool changed = false;
        var live = new HashSet<string>(StringComparer.Ordinal);
        var drawn = DrawnRoads(_roadGhostShown).ToList();
        // Junctions first: the roads stop at their edges.
        var plan = Junctions = PlanJunctions(drawn);
        foreach (var road in drawn)
        {
            live.Add(road.Id);
            var gaps = plan.GapsOf(road.Id);
            if (_roadMeshes.TryGetValue(road.Id, out var cached) && cached.Road == road && cached.Gaps.SequenceEqual(gaps)) continue;
            if (!_roadTemplates.TryGetValue(road.Template.Hash, out var template)) { LoadRoadTemplate(road.Template); continue; }
            changed |= RemoveRoadMeshes(road.Id);
            try
            {
                var axis = RoadBending.AxisOf(template.ResourcePath);
                // Rigged pieces bend with one frame per bone, exactly as the game will.
                var rig = _roadRigs.GetValueOrDefault(road.Template.Hash);
                var segments = rig is not null ? RoadBuilder.Build(road, rig.Length, rig.Bones, gaps) : RoadBuilder.Build(road, RoadBending.Length(template, axis), 0, gaps);
                long generation = ++_roadGeneration;
                // A cable hangs its tube on its frames (one per joint), as the game's cables do.
                bool cable = road.IsCable;
                var built = segments.Select(s => (RoadKey(road.Id, s.Index),
                    (cable ? CableBending.Deform(template, CableBending.Frames(s.Frames), $"{road.Name} · {s.Index + 1}", rig?.Thickness ?? 1)
                        : RoadBending.Deform(template, s.Frames, $"{road.Name} · {s.Index + 1}", axis)) with { SourceHash = $"{template.SourceHash}+road{generation}" }, s.Origin, s.Frames)).ToArray();
                _roadMeshes[road.Id] = new(road, gaps, built);
                foreach (var (key, mesh, _, _) in built) _assets[key] = mesh;
                changed = true;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException) { Error = Loc.F("Road: {0}", ex.Message); }
        }
        foreach (var id in _roadMeshes.Keys.Where(k => !live.Contains(k)).ToArray()) changed |= RemoveRoadMeshes(id);
        changed |= UpdateJunctionMeshes(plan, ++_roadGeneration);
        if (changed) { SetWorldAssets(); _sceneDirty = true; RoadVersion++; }
    }

    private bool RemoveRoadMeshes(string id)
    {
        if (!_roadMeshes.Remove(id, out var old)) return false;
        foreach (var (key, _, _, _) in old.Segments) _assets.Remove(key);
        return true;
    }

    // After the drawable table is rebuilt from the streamer, put the road meshes back.
    private bool AddRoadAssets()
    {
        bool changed = AddJunctionAssets();
        foreach (var road in _roadMeshes.Values)
            foreach (var (key, mesh, _, _) in road.Segments)
                if (!_assets.ContainsKey(key)) { _assets[key] = mesh; changed = true; }
        return changed;
    }

    private void AddRoadInstances(List<RenderPlacement> instances)
    {
        AddJunctionInstances(instances);
        foreach (var (id, road) in _roadMeshes)
        {
            if (!IsShown(id)) continue;
            // Not tinted when selected: the road keeps its look, its outline and handles show it.
            foreach (var (key, _, origin, _) in road.Segments)
                instances.Add(new(RoadPrefix + id + ":" + key, key, Matrix4x4.CreateTranslation(Coordinates.GameToView(origin)), false,
                    TypeKey: (1UL << 32) | 0x4A5A6AUL));
        }
    }

    // --- Selection ---------------------------------------------------------------------------

    public void SelectRoad(string? id, int point = -1)
    {
        if (id is not null && !Roads.ContainsKey(id)) id = null;
        if (id != SelectedRoadId) { RoadDrawing = false; _roadFirstPoint = null; _roadFirstOutward = null; _roadGhost = null; }
        SelectedRoadId = id; SelectedRoadPoint = id is null ? -1 : point; _roadPreview = null;
        if (id is not null && SelectedId is not null) Select(null);
        if (id is not null && SelectedElevatorId is not null) SelectElevator(null);
        _sceneDirty = true; RoadVersion++;
    }

    public void SelectRoadPoint(int index)
    {
        if (SelectedRoad is not { } road) return;
        SelectedRoadPoint = index >= 0 && index < road.Points.Length ? index : -1; RoadVersion++;
    }

    // Screen-space handle under the cursor (within 14 px), for the selected road.
    public int RoadPointAt(Vector2 pixel, Vector2 viewport)
    {
        if (SelectedRoad is not { } road || _renderer.RenderedWorldCamera is not { } frame) return -1;
        int best = -1; float bestDistance = 14;
        for (int i = 0; i < road.Points.Length; i++)
            if (Project(road.Points[i], frame, viewport) is { } screen && Vector2.Distance(screen, pixel) < bestDistance) { best = i; bestDistance = Vector2.Distance(screen, pixel); }
        return best;
    }

    private static Vector2? Project(Vector3 game, CameraFrame frame, Vector2 viewport)
    {
        var clip = Vector4.Transform(new Vector4(Coordinates.GameToView(game) - frame.Origin, 1), frame.ViewProjection);
        if (clip.W <= 1e-4f) return null;
        return new((clip.X / clip.W * .5f + .5f) * viewport.X, (.5f - clip.Y / clip.W * .5f) * viewport.Y);
    }

    // Ground under the cursor, ignoring editor roads (so new points land on the terrain).
    public Vector3 RoadGroundPoint(Vector2 pixel, Vector2 viewport)
    {
        if (_renderer.RenderedWorldCamera is { } frame)
        {
            var ray = ScenePicking.ViewRay(pixel, viewport, frame.View, frame.Projection);
            var world = new Ray3(ray.Origin + frame.Origin, ray.Direction);
            if (Grid.Raycast(world, 1500, null, RoadPrefix) is { } hit) return Coordinates.ViewToGame(world.Origin + world.Direction * hit);
        }
        return Coordinates.ViewToGame(Camera.Eye + Camera.Forward * 20);
    }

    // --- Editing -------------------------------------------------------------------------------

    public void StartRoadDrawing()
    {
        if (RoadTemplate is null) { Status = Loc.T("Choose a road type in the Roads panel first."); return; }
        SelectRoad(null); RoadDrawing = true; _roadFirstPoint = null; _roadFirstOutward = null; _roadGhost = null; RoadVersion++;
        Status = Loc.T("Click on the ground to place the road's points: the next stretch follows the cursor. Double-click or Enter finishes.");
    }

    public void StopRoadDrawing()
    {
        RoadDrawing = false; _roadFirstPoint = null; _roadFirstOutward = null; _roadGhost = null; RoadHover = null; RoadVersion++;
    }

    // A road through game-space points with the chosen type (automation and tests).
    public string? CreateRoad(IReadOnlyList<Vector3> points, string? name = null, RoadGround? ground = null, bool defaultGround = true)
    {
        if (RoadTemplate is not { } template) { Error = Loc.T("Choose a road type first."); return null; }
        var road = new RoadDefinition(EditorDocument.NewRoadId(), name ?? Loc.F("Road {0}", (Roads.Count + 1)), template, [.. points]) { Ground = ground ?? (defaultGround ? NewRoadGround : null) };
        if (!Apply(() => Document.AddRoad(road))) return null;
        SelectRoad(road.Id, points.Count - 1);
        return road.Id;
    }

    public int RoadSegmentCount(string id) => _roadMeshes.TryGetValue(id, out var built) ? built.Segments.Length : 0;

    // Extends the selected road with clicks: from its first point when that one is selected
    // (points are prepended), otherwise after the selected point (or at the end).
    public void ContinueRoadDrawing()
    {
        if (SelectedRoad is not { } road) return;
        if (SelectedRoadPoint < 0) SelectedRoadPoint = road.Points.Length - 1;
        RoadDrawing = true; _roadFirstPoint = null; _roadFirstOutward = null; _roadGhost = null; RoadVersion++;
        Status = Loc.T("Click on the ground to add points: the next stretch follows the cursor. Double-click or Enter finishes.");
    }

    // Camera above the whole road, looking down at it.
    public void FrameRoad(RoadDefinition road) => FrameFromAbove(road.Points, road.Name, 15);

    // Camera above game points (a road, a lane), looking down at all of them.
    private void FrameFromAbove(IEnumerable<Vector3> points, string name, float minimum)
    {
        var bounds = Bounds3.From(points.Select(Coordinates.GameToView));
        float radius = Math.Max(minimum, bounds.Size.Length() * .6f);
        var center = Coordinates.ViewToGame(bounds.Center);
        var forward = Coordinates.ViewToGame(Camera.Forward) with { Z = 0 };
        forward = forward.LengthSquared() < 1e-6f ? Vector3.UnitY : Vector3.Normalize(forward);
        var eye = center - forward * radius * .6f + Vector3.UnitZ * radius * .8f;
        GoTo(CurrentView(name) with { X = eye.X, Y = eye.Y, Z = eye.Z, Pitch = .9f });
    }

    private void CommitRoad(RoadDefinition road, string name)
    {
        _roadPreview = null;
        Apply(() => Document.UpdateRoad(road, name));
        _roadsDirty = true; _sceneDirty = true; RoadVersion++;
    }

    // Gizmo drag of the selected point: preview, then one undoable command on release.
    public void PreviewRoadPoint(Vector3 game)
    {
        if (SelectedRoadId is not { } id || !Roads.TryGetValue(id, out var stored) || SelectedRoadPoint < 0 || SelectedRoadPoint >= stored.Points.Length) return;
        var points = (_roadPreview?.Id == id ? _roadPreview : stored).Points.SetItem(SelectedRoadPoint, game);
        _roadPreview = stored with { Points = points };
        _sceneDirty = true;
    }

    public void CommitRoadPreview() { if (_roadPreview is { } preview) CommitRoad(preview, Loc.T("Move road point")); }
    public void CancelRoadPreview() { _roadPreview = null; _roadsDirty = true; _sceneDirty = true; }

    public void SetRoadPoint(int index, Vector3 game)
    {
        if (SelectedRoad is not { } road || index < 0 || index >= road.Points.Length) return;
        CommitRoad(road with { Points = road.Points.SetItem(index, game) }, Loc.T("Move road point"));
    }

    public void InsertRoadPoint()
    {
        if (SelectedRoad is not { } road) return;
        int i = Math.Clamp(SelectedRoadPoint, 0, road.Points.Length - 2);
        var line = RoadSpline.Sample([.. road.Points.Skip(Math.Max(0, i - 1)).Take(i == 0 ? 3 : 4)]);
        // Midpoint of the curve between points i and i+1 (closest sample to the chord middle).
        var middle = (road.Points[i] + road.Points[i + 1]) * .5f;
        var point = line.OrderBy(p => Vector3.DistanceSquared(p, middle)).First();
        CommitRoad(road with { Points = road.Points.Insert(i + 1, point) }, Loc.T("Insert road point"));
        SelectedRoadPoint = i + 1;
    }

    public void DeleteRoadPoint()
    {
        if (SelectedRoad is not { } road || SelectedRoadPoint < 0) return;
        if (road.Points.Length <= 2) { Status = Loc.T("A road needs two points: delete the road instead."); return; }
        int index = SelectedRoadPoint;
        CommitRoad(road with { Points = road.Points.RemoveAt(index) }, Loc.T("Delete road point"));
        SelectedRoadPoint = Math.Min(index, road.Points.Length - 2);
    }

    public void ReverseRoad() { if (SelectedRoad is { } road) CommitRoad(road with { Points = [.. road.Points.Reverse()] }, Loc.T("Reverse road")); }
    public void SetRoadBanking(float degrees) { if (SelectedRoad is { } road && road.Banking != degrees) CommitRoad(road with { Banking = Math.Clamp(degrees, -45, 45) }, Loc.T("Bank road")); }
    public void RenameRoad(string name) { if (SelectedRoad is { } road && name.Trim().Length > 0 && road.Name != name.Trim()) CommitRoad(road with { Name = name.Trim() }, Loc.T("Rename road")); }
    public void SetRoadTemplate(MeshReference template) { if (SelectedRoad is { } road && road.Template != template) CommitRoad(road with { Template = template }, Loc.T("Change road type")); }

    // Every point onto the first surface below it (other roads excluded).
    public void SnapRoadToGround()
    {
        if (SelectedRoad is not { } road) return;
        var points = road.Points.Select(p =>
        {
            var view = Coordinates.GameToView(p);
            return Grid.Ground(view, 30, 600, RoadPrefix) is { } height ? p with { Z = height } : p;
        }).ToImmutableArray();
        CommitRoad(road with { Points = points }, Loc.T("Snap road to ground"));
    }

    public void DeleteSelectedRoad()
    {
        if (SelectedRoadId is not { } id) return;
        if (Apply(() => Document.DeleteRoad(id))) SelectRoad(null);
    }

    // --- Vanilla roads ----------------------------------------------------------------------------

    public bool CanRebuildSelectedAsRoad => Selected is { Original: { } original, Deleted: false, SourceMismatch: false } && !original.Deformation.IsDefaultOrEmpty;

    // The selected vanilla piece and the pieces continuing it (same template, ends within
    // 30 cm) become one editable road that replaces them.
    public void RebuildSelectedAsRoad()
    {
        if (!CanRebuildSelectedAsRoad || Selected?.Original is not { } piece) return;
        RebuildGameRoad(piece);
    }

    // A game road piece and the pieces continuing it (same template, ends within 30 cm, whichever
    // way each runs) become one editable road of `type` (null: their own) that replaces them.
    // Its id, or null.
    public string? RebuildGameRoad(SourcePlacement piece, MeshReference? type = null)
    {
        if (piece.Mesh is null || piece.Deformation.IsDefaultOrEmpty) return null;
        var candidates = Document.Objects().Where(o => o.Original is { } p && !p.Deformation.IsDefaultOrEmpty && p.Mesh?.Hash == piece.Mesh.Hash && !o.Deleted
            && !Roads.Values.Any(r => r.Replaces.Contains(o.Id))).ToDictionary(o => o.Id, o => o.Original!);
        Vector3 Start(SourcePlacement p) => Vector3.Transform(p.Deformation[0].Translation, p.Transform.GameMatrix);
        Vector3 End(SourcePlacement p) => Vector3.Transform(p.Deformation[^1].Translation, p.Transform.GameMatrix);
        // The chain in travel order, each piece with whether it runs the other way.
        var chain = new LinkedList<(SourcePlacement Piece, bool Reversed)>(); chain.AddFirst((piece, false));
        var used = new HashSet<string> { piece.Id };
        Vector3 Tail() => chain.Last!.Value.Reversed ? Start(chain.Last.Value.Piece) : End(chain.Last.Value.Piece);
        Vector3 Head() => chain.First!.Value.Reversed ? End(chain.First.Value.Piece) : Start(chain.First.Value.Piece);
        for (int guard = 0; guard < 64; guard++)
        {
            var tail = Tail();
            var next = candidates.Values.FirstOrDefault(c => !used.Contains(c.Id) && (Vector3.Distance(Start(c), tail) < .3f || Vector3.Distance(End(c), tail) < .3f));
            if (next is null) break;
            chain.AddLast((next, Vector3.Distance(Start(next), tail) >= .3f)); used.Add(next.Id);
        }
        for (int guard = 0; guard < 64; guard++)
        {
            var head = Head();
            var previous = candidates.Values.FirstOrDefault(c => !used.Contains(c.Id) && (Vector3.Distance(End(c), head) < .3f || Vector3.Distance(Start(c), head) < .3f));
            if (previous is null) break;
            chain.AddFirst((previous, Vector3.Distance(End(previous), head) >= .3f)); used.Add(previous.Id);
        }
        var points = new List<Vector3>();
        foreach (var (p, reversed) in chain)
        {
            var part = RoadBuilder.ControlPoints(p.Transform, p.Deformation);
            IEnumerable<Vector3> ordered = reversed ? part.Reverse() : part;
            points.AddRange(points.Count == 0 ? ordered : ordered.Skip(1));
        }
        // A cable keeps its own tube and has no side to bank (its frames are not a road's).
        bool cable = type is null && CableBending.IsCable(piece.Mesh.Path);
        float banking = cable ? 0 : chain.Average(c => RoadBuilder.Banking(c.Piece.Transform, c.Piece.Deformation) * (c.Reversed ? -1 : 1));
        var road = new RoadDefinition(EditorDocument.NewRoadId(), piece.Name, type ?? piece.Mesh with { Appearance = "default" }, [.. points]) { Banking = MathF.Round(banking, 1) };
        string? id = null;
        if (!Apply(() => id = Document.ReplaceWithRoad(chain.Select(c => c.Piece.Id).ToArray(), road))) return null;
        SelectRoad(id);
        Status = cable ? (chain.Count == 1 ? Loc.T("Cable ready to edit.") : Loc.F("{0} connected cables rebuilt as one editable cable.", chain.Count))
            : chain.Count == 1 ? Loc.T("Road piece ready to edit.") : Loc.F("{0} connected road pieces rebuilt as one editable road.", chain.Count);
        return id;
    }

    // The game road pieces a project road's junctions are laid on (one per game road: the first
    // piece of its chain).
    public IReadOnlyList<SourcePlacement> GameRoadsMetBy(string roadId)
    {
        var found = new List<SourcePlacement>();
        foreach (var junction in Junctions.Junctions.Where(j => j.Arms.Any(a => a.RoadId == roadId)))
            foreach (var arm in junction.Arms.Where(a => !a.Trimmed && a.RoadId.StartsWith("game:", StringComparison.Ordinal)))
                if (Document.TryGetObject(arm.RoadId["game:".Length..])?.Original is { } piece && !found.Any(f => f.Id == piece.Id)) found.Add(piece);
        return found;
    }

    // --- Profile -----------------------------------------------------------------------------------

    // The road's heights evened out (its ends stay), or one even grade between its ends.
    public void SmoothRoadProfile() { if (SelectedRoad is { } road) CommitRoad(road with { Points = RoadGeometry.SmoothProfile(road.Points) }, Loc.T("Smooth road profile")); }
    public void EvenRoadGrade() { if (SelectedRoad is { } road) CommitRoad(road with { Points = RoadGeometry.EvenGrade(road.Points) }, Loc.T("Even road grade")); }

    // A bridge or overpass crosses other roads without junctions.
    public void SetRoadOverpass(bool overpass)
    {
        if (SelectedRoad is not { } road || road.Overpass == overpass) return;
        CommitRoad(road with { Overpass = overpass }, overpass ? Loc.T("Make the road an overpass") : Loc.T("Let the road meet others at junctions"));
    }

    // How the selected road shapes the terrain (null: it leaves it alone).
    public void SetRoadGround(RoadGround? ground)
    {
        if (SelectedRoad is not { } road || road.Ground == ground) return;
        CommitRoad(road with { Ground = ground?.Validated() }, ground is null ? Loc.T("Leave the terrain under the road") : road.Ground is null ? Loc.T("Shape the terrain under the road") : Loc.T("Change the road's earthworks"));
    }
}
