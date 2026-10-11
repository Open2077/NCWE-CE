// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Numerics;
using NCWE.Core;
using NCWE.Core.Terrain;
using NCWE.Render;

namespace NCWE.Studio.Session;

// The Roads tool in the view:
// - drawing: the road to the cursor is shown before the click (its meshes a few times a second,
//   its outline every frame, with length, grade and turn radius); ends of project roads and of
//   the game's road pieces attract the cursor and the road meets them along their direction;
//   Shift turns in 15° steps, Ctrl keeps the height; a double-click finishes, Backspace takes
//   back the last point;
// - editing: handles are dragged on the ground, a click (or a drag) on the curve inserts a point;
// - earthworks: roads with RoadGround shape the terrain (their ghost and dragged preview too).
internal sealed partial class StudioSession
{
    // An end the cursor snaps to: where, the direction leading out of it (a road meeting it comes
    // in along it), its name and, for a project road, its id and which end.
    public sealed record RoadSnapTarget(Vector3 Point, Vector3 Outward, string Label, string? RoadId, bool Start);

    // Where a click would put the next point, and what the stretch to it would be like.
    public sealed record RoadCursor(Vector3 Point, RoadSnapTarget? Snap, bool Angled, bool Level, float Length, float? Grade, float? Radius, float HalfWidth);

    public RoadCursor? RoadHover { get; private set; }
    // The drawing ghost's points (automation).
    public int RoadGhostPoints => _roadGhost?.Points.Length ?? 0;
    public int RoadHoverHandle { get; private set; } = -1;
    public (int Index, Vector3 Point)? RoadInsertHover { get; private set; }
    public bool RoadDragging => _roadDrag is not null;

    private Vector3? _roadFirstOutward;
    private string? _roadCreatedNow;
    private (double Time, Vector2 Pixel) _lastDrawClick = (double.NegativeInfinity, default);
    // Point index range of the ghost the cursor's stretch covers (drawn as a ribbon).
    private (int From, int To) _ghostStretch;

    // --- Ends to meet -----------------------------------------------------------------------------

    private List<RoadSnapTarget>? _roadSnapTargets;
    private (long Revision, int Sectors, object Roads, double At) _roadSnapTargetsKey;

    private List<RoadSnapTarget> SnapTargets()
    {
        var key = (Document.Revision, Document.ResidentSectors.Count, (object)Roads, 0.0);
        if (_roadSnapTargets is not null && _roadSnapTargetsKey.Revision == key.Revision && _roadSnapTargetsKey.Sectors == key.Item2 && ReferenceEquals(_roadSnapTargetsKey.Roads, key.Item3)) return _roadSnapTargets;
        var targets = new List<RoadSnapTarget>();
        foreach (var road in Roads.Values)
        {
            if (road.Points.Length < 2) continue;
            targets.Add(new(road.Points[0], -RoadSpline.StartDirection(road.Points), road.Name, road.Id, true));
            targets.Add(new(road.Points[^1], RoadSpline.EndDirection(road.Points), road.Name, road.Id, false));
        }
        // The game's bended road pieces (streets near the camera), unless deleted or replaced.
        var replaced = Roads.Values.SelectMany(r => r.Replaces).ToHashSet(StringComparer.Ordinal);
        var edits = Document.Project.Changes.Modified;
        foreach (var sector in Document.ResidentSectors.Values)
            foreach (var p in sector.Placements)
            {
                if (p.Deformation.IsDefaultOrEmpty || p.Deformation.Length < 2 || p.Mesh is null || !RoadBending.IsRoadPiece(p.Mesh.Path) || replaced.Contains(p.Id)) continue;
                if (edits.TryGetValue(p.Id, out var edit) && (edit.Deleted || edit.Transform is not null)) continue;
                var m = p.Transform.GameMatrix; var rotation = m; rotation.Translation = Vector3.Zero;
                Vector3 At(Matrix4x4 f) => Vector3.Transform(f.Translation, m);
                Vector3 Along(Matrix4x4 f) { var t = Vector3.TransformNormal(new(f.M21, f.M22, f.M23), rotation); return t.LengthSquared() < 1e-8f ? Vector3.UnitY : Vector3.Normalize(t); }
                targets.Add(new(At(p.Deformation[0]), -Along(p.Deformation[0]), p.Name, null, true));
                targets.Add(new(At(p.Deformation[^1]), Along(p.Deformation[^1]), p.Name, null, false));
            }
        _roadSnapTargetsKey = key;
        return _roadSnapTargets = targets;
    }

    // Snap targets near a game position (for markers in the view).
    public IEnumerable<RoadSnapTarget> SnapTargetsNear(Vector3 game, float radius)
        => SnapTargets().Where(t => Vector2.DistanceSquared(new(t.Point.X, t.Point.Y), new(game.X, game.Y)) < radius * radius);

    // --- Drawing ------------------------------------------------------------------------------------

    // Where the next point attaches: the point the road goes on from, the direction it leaves that
    // point in (when known), and where the new point goes in the road (before the first point,
    // after the last, or after a point in the middle).
    private (Vector3 Point, Vector3? Direction, int At, bool Prepend, bool Append)? DrawAnchor()
    {
        if (!RoadDrawing) return null;
        if (SelectedRoad is { } road)
        {
            int n = road.Points.Length;
            if (SelectedRoadPoint == 0 && n > 1) return (road.Points[0], -RoadSpline.StartDirection(road.Points), 0, true, false);
            if (SelectedRoadPoint >= 0 && SelectedRoadPoint < n - 1)
            {
                int i = SelectedRoadPoint;
                var d = road.Points[i + 1] - road.Points[Math.Max(0, i - 1)];
                return (road.Points[i], d.LengthSquared() > 1e-8f ? Vector3.Normalize(d) : null, i + 1, false, false);
            }
            return (road.Points[^1], RoadSpline.EndDirection(road.Points), n, false, true);
        }
        if (_roadFirstPoint is { } first) return (first, _roadFirstOutward, 1, false, true);
        return null;
    }

    // The ground under a pixel, other roads left out (null: nothing there).
    private Vector3? RoadGroundHit(Vector2 pixel, Vector2 viewport)
    {
        if (_renderer.RenderedWorldCamera is not { } frame) return null;
        var ray = ScenePicking.ViewRay(pixel, viewport, frame.View, frame.Projection);
        var world = new Ray3(ray.Origin + frame.Origin, ray.Direction);
        if (Grid.Raycast(world, 2500, null, RoadPrefix) is { } hit) return Coordinates.ViewToGame(world.Origin + world.Direction * hit);
        if (Terrain?.Raycast(Coordinates.ViewToGame(world.Origin), Coordinates.ViewToGame(world.Direction), 2500) is { } ground) return ground;
        return null;
    }

    private float? GroundHeightAt(Vector3 game)
        => Grid.Ground(Coordinates.GameToView(game) with { Y = game.Z + 60 }, 0, 800, RoadPrefix) ?? Terrain?.HeightAt(new(game.X, game.Y));

    // The cursor's point for the Roads tool: a road end within 16 px (or 2.5 m), else the ground;
    // Shift turns the stretch in 15° steps from the road's direction, Ctrl keeps the height of
    // the point it goes on from. `excluded`: a road whose ends do not count (the one dragged).
    public RoadCursor? RoadCursorAt(Vector2 pixel, Vector2 viewport, bool shift, bool control, string? excluded = null)
    {
        if (_renderer.RenderedWorldCamera is not { } frame || RoadGroundHit(pixel, viewport) is not { } ground) return null;
        var anchor = DrawAnchor();
        string? extending = RoadDrawing ? SelectedRoadId : null;
        // The road drawn (or whose end is dragged) counts with its other end only: meeting it
        // closes a loop.
        bool Skip(RoadSnapTarget t)
        {
            if (t.RoadId is not { } id) return false;
            if (id == excluded)
                return !(_roadDrag is { Inserted: false } d && Roads.TryGetValue(id, out var dragged) && dragged.Points.Length > 2 && !IsLoop(dragged)
                    && (d.Index == dragged.Points.Length - 1 ? t.Start : d.Index == 0 && !t.Start));
            return id == extending && !(anchor is { } a && (a.Append ? t.Start : a.Prepend && !t.Start) && Vector3.Distance(t.Point, a.Point) > 1);
        }
        RoadSnapTarget? snap = null;
        float best = 16;
        foreach (var t in SnapTargets())
        {
            if (Skip(t)) continue;
            if (Project(t.Point, frame, viewport) is { } screen && Vector2.Distance(screen, pixel) < best) { best = Vector2.Distance(screen, pixel); snap = t; }
        }
        if (snap is null)
        {
            float near = 2.5f;
            foreach (var t in SnapTargets())
            {
                if (Skip(t)) continue;
                float d = Vector3.Distance(t.Point, ground);
                if (d < near) { near = d; snap = t; }
            }
        }
        var point = ground; bool angled = false, level = false;
        if (snap is not null) point = snap.Point;
        else
        {
            if (shift && anchor is { } a && Vector2.Distance(new(a.Point.X, a.Point.Y), new(ground.X, ground.Y)) > .5f)
            {
                point = RoadGeometry.SnapAngle(a.Point, a.Direction, ground);
                point = point with { Z = GroundHeightAt(point) ?? ground.Z };
                angled = true;
            }
            if (control && (anchor?.Point ?? (_roadDrag is { } d ? d.Start : (Vector3?)null)) is { } from) { point = point with { Z = from.Z }; level = true; }
        }
        float half = (SelectedRoad?.Template ?? RoadTemplate) is { } template ? RoadTemplateHalfWidth(template) ?? 0 : 0;
        float length = anchor is { } b ? Vector3.Distance(b.Point, point) : 0;
        float? grade = anchor is { } c ? RoadGeometry.Grade(c.Point, point) : null;
        var cursor = new RoadCursor(point, snap, angled, level, length, grade, null, half);
        if (anchor is not null && Extended(cursor) is { } extended)
        {
            // The stretch as it will be built: its length along the curve, its grade over that
            // run, and its tightest turn (with the points around it).
            var stretch = RoadSpline.Sample(extended.Points[extended.From..(extended.To + 1)], .5f);
            float run = 0;
            for (int i = 1; i < stretch.Length; i++) run += new Vector2(stretch[i].X - stretch[i - 1].X, stretch[i].Y - stretch[i - 1].Y).Length();
            var near = extended.Points[Math.Max(0, extended.From - 1)..Math.Min(extended.Points.Length, extended.To + 2)];
            float radius = RoadSpline.TightestRadius(near);
            cursor = cursor with
            {
                Length = RoadSpline.Length(stretch), Grade = run > .5f ? (stretch[^1].Z - stretch[0].Z) / run : grade,
                Radius = float.IsFinite(radius) ? radius : null,
            };
        }
        return cursor;
    }

    // Whether a snap target is the drawn road's own other end (meeting it closes a loop).
    private bool ClosesLoop(RoadSnapTarget? snap) => snap?.RoadId is { } id && RoadDrawing && id == SelectedRoadId;

    // The road's points once the cursor's point is added, and where it lands. A stretch that
    // leaves or meets a road end is a drivable way along that end's direction, its turns no
    // tighter than the road allows (RoadGeometry.Connect); other stretches are left to the spline.
    private (ImmutableArray<Vector3> Points, int Index, int From, int To)? Extended(RoadCursor cursor)
    {
        if (DrawAnchor() is not { } anchor) return null;
        var p = cursor.Snap?.Point ?? cursor.Point;
        if (Vector2.Distance(new(anchor.Point.X, anchor.Point.Y), new(p.X, p.Y)) < .5f) return null;
        float radius = RoadGeometry.TurnRadius(cursor.HalfWidth);
        // Meeting an end: the road arrives along it (the drawn road's own start: along its start).
        Vector3? arrive = cursor.Snap is { } s ? -s.Outward : null;
        Vector3[] Stretch(Vector3? leave, Vector3? behind = null) => [.. RoadGeometry.Connect(anchor.Point, leave, p, arrive, radius, behind), p];
        if (SelectedRoad is { } road)
        {
            if (anchor.Prepend)
            {
                Vector3[] add = [.. Stretch(arrive is null ? null : anchor.Direction, road.Points.Length > 1 ? road.Points[1] : null).Reverse()];
                return (road.Points.InsertRange(0, add), 0, 0, add.Length);
            }
            if (anchor.Append)
            {
                var add = Stretch(arrive is null ? null : anchor.Direction, road.Points.Length > 1 ? road.Points[^2] : null);
                var points = road.Points.AddRange(add);
                return (points, points.Length - 1, road.Points.Length - 1, points.Length - 1);
            }
            return (road.Points.Insert(anchor.At, p), anchor.At, anchor.At - 1, Math.Min(anchor.At + 1, road.Points.Length));
        }
        if (_roadFirstPoint is { } first)
        {
            // Started on a road end: the road leaves it along it.
            Vector3[] points = [first, .. Stretch(_roadFirstOutward)];
            return ([.. points], points.Length - 1, 0, points.Length - 1);
        }
        return null;
    }

    // Every few frames while the Roads tool is on: the drawing ghost, or what the cursor is over.
    public void UpdateRoadHover(Vector2? pixel, Vector2 viewport, bool shift, bool control)
    {
        RoadHoverHandle = -1; RoadInsertHover = null;
        if (RoadDrawing)
        {
            RoadHover = pixel is { } p ? RoadCursorAt(p, viewport, shift, control) : null;
            var extended = RoadHover is { } cursor ? Extended(cursor) : null;
            if (extended is { } e && (SelectedRoad?.Template ?? RoadTemplate) is { } template)
            {
                _ghostStretch = (e.From, e.To);
                _roadGhost = SelectedRoad is { } road ? road with { Points = e.Points }
                    : new RoadDefinition(GhostRoadId, Loc.T("New road"), template, e.Points) { Ground = NewRoadGround };
            }
            else _roadGhost = null;
            return;
        }
        RoadHover = null; _roadGhost = null;
        if (pixel is not { } at || _roadDrag is not null || SelectedRoad is null) return;
        RoadHoverHandle = RoadPointAt(at, viewport);
        if (RoadHoverHandle < 0) RoadInsertHover = RoadCurveAt(at, viewport);
    }

    // What the Roads tool highlights under the cursor: roads and the game's road pieces only.
    public void UpdateRoadPickHover(Vector2? pixel, Vector2 viewport)
    {
        UpdateHover(pixel, viewport);
        if (HoverId is { } id && RoadIdOf(id) is null && !(Document.PlacementOf(id) is { Deformation.IsDefaultOrEmpty: false, Mesh: { } mesh } && RoadBending.IsRoadPiece(mesh.Path))) { HoverId = null; _hover = null; }
    }

    // Road tool click. Drawing: the cursor's point is added (a double-click finishes; meeting a
    // road end connects to it and finishes). Otherwise: a handle selects its point, the curve of
    // the selected road gets a point there, a road is selected, or a game piece (to rebuild).
    public void RoadClick(Vector2 pixel, Vector2 viewport, bool shift = false, bool control = false)
    {
        if (!RoadDrawing)
        {
            int handle = RoadPointAt(pixel, viewport);
            if (handle >= 0) { SelectRoadPoint(handle); return; }
            if (SelectedRoad is { } selected && RoadCurveAt(pixel, viewport) is { } on)
            {
                CommitRoad(selected with { Points = selected.Points.Insert(on.Index, on.Point) }, Loc.T("Insert road point"));
                SelectedRoadPoint = on.Index;
                return;
            }
            var hit = HitTest(pixel, viewport);
            if (RoadIdOf(hit) is { } roadId && roadId != GhostRoadId) { SelectRoad(roadId); return; }
            SelectRoad(null);
            Select(hit); // a vanilla road piece can then be rebuilt as an editable road
            return;
        }
        double now = Environment.TickCount64 / 1000.0;
        bool twice = now - _lastDrawClick.Time < .45 && Vector2.Distance(pixel, _lastDrawClick.Pixel) < 8;
        _lastDrawClick = twice ? (double.NegativeInfinity, default) : (now, pixel);
        if (twice) { FinishRoadDrawing(); return; }
        if (RoadCursorAt(pixel, viewport, shift, control) is not { } cursor) { Status = Loc.T("Point at the ground to place the road."); return; }
        if (SelectedRoad is { } road)
        {
            if (Extended(cursor) is not { } e) return;
            bool loop = ClosesLoop(cursor.Snap);
            CommitRoad(road with { Points = e.Points }, loop ? Loc.T("Close the road's loop") : Loc.T("Add road point"));
            SelectedRoadPoint = e.Index;
            _roadGhost = null;
            if (cursor.Snap is { } s) { StopRoadDrawing(); Status = loop ? Loc.F("Loop closed: {0} ends where it starts.", road.Name) : Loc.F("Connected to {0}.", s.Label); }
            return;
        }
        if (_roadFirstPoint is not null)
        {
            if (Extended(cursor) is not { } e || RoadTemplate is not { } template) return;
            var created = new RoadDefinition(EditorDocument.NewRoadId(), Loc.F("Road {0}", Roads.Count + 1), template, e.Points) { Ground = NewRoadGround };
            if (!Apply(() => Document.AddRoad(created))) return;
            if (SelectedId is not null) Select(null);
            SelectedRoadId = created.Id; SelectedRoadPoint = e.Points.Length - 1; _roadFirstPoint = null; _roadFirstOutward = null; _roadGhost = null;
            _roadCreatedNow = created.Id; _roadsDirty = true; _sceneDirty = true; RoadVersion++;
            if (cursor.Snap is { } s) { StopRoadDrawing(); Status = Loc.F("Connected to {0}.", s.Label); }
            else Status = Loc.T("Road started: keep clicking to extend it. Double-click or Enter finishes.");
            return;
        }
        // The first point. On the end of a project road, that road goes on from there; on a game
        // road's end, the new road leaves it in line with it.
        if (cursor.Snap is { RoadId: { } id } own && Roads.TryGetValue(id, out var other))
        {
            if (SelectedId is not null) Select(null);
            SelectedRoadId = id; SelectedRoadPoint = own.Start ? 0 : other.Points.Length - 1; _roadPreview = null;
            _sceneDirty = true; RoadVersion++;
            Status = Loc.F("Continuing {0}: click to add points.", other.Name);
            return;
        }
        _roadFirstPoint = cursor.Point; _roadFirstOutward = cursor.Snap?.Outward; RoadVersion++;
        Status = cursor.Snap is { } start ? Loc.F("Starting from {0}: click the next point.", start.Label) : Loc.T("Click the next point: the stretch follows the cursor.");
    }

    public void FinishRoadDrawing()
    {
        var road = SelectedRoad;
        StopRoadDrawing();
        if (road is not null) Status = Loc.F("{0}: {1:F0} m, {2} points.", road.Name, RoadSpline.Length(RoadSpline.Sample(road.Points)), road.Points.Length);
    }

    // Backspace while drawing: the last point placed goes (a road started now goes back to its
    // first point).
    public void RoadBackspace()
    {
        if (!RoadDrawing) return;
        if (SelectedRoad is { } road)
        {
            int i = SelectedRoadPoint < 0 ? road.Points.Length - 1 : SelectedRoadPoint;
            if (road.Points.Length > 2)
            {
                CommitRoad(road with { Points = road.Points.RemoveAt(i) }, Loc.T("Remove road point"));
                SelectedRoadPoint = i == 0 ? 0 : i - 1;
            }
            else if (_roadCreatedNow == road.Id)
            {
                var keep = i == 0 ? road.Points[1] : road.Points[0];
                if (Apply(() => Document.DeleteRoad(road.Id))) { SelectedRoadId = null; SelectedRoadPoint = -1; _roadFirstPoint = keep; _roadFirstOutward = null; RoadVersion++; }
            }
            else Status = Loc.T("A road keeps two points: delete the road instead.");
            _roadGhost = null;
            return;
        }
        if (_roadFirstPoint is not null) { _roadFirstPoint = null; _roadFirstOutward = null; _roadGhost = null; RoadVersion++; }
    }

    // --- Editing in the view ---------------------------------------------------------------------

    private (int Index, bool Inserted, Vector3 Start)? _roadDrag;
    private (RoadDefinition Road, List<(int Segment, Vector3 Point)> Samples)? _curveSamples;

    // The point of the selected road's curve under a pixel (within 10 px, away from its handles)
    // and where it would go in its points.
    public (int Index, Vector3 Point)? RoadCurveAt(Vector2 pixel, Vector2 viewport)
    {
        if (SelectedRoad is not { } road || _renderer.RenderedWorldCamera is not { } frame) return null;
        if (_curveSamples?.Road != road) _curveSamples = (road, RoadSpline.SampleSegments(road.Points, 1f));
        foreach (var p in road.Points) if (Project(p, frame, viewport) is { } handle && Vector2.Distance(handle, pixel) < 16) return null;
        (int, Vector3)? best = null; float nearest = 10;
        foreach (var (segment, point) in _curveSamples.Value.Samples)
            if (Project(point, frame, viewport) is { } screen && Vector2.Distance(screen, pixel) < nearest) { nearest = Vector2.Distance(screen, pixel); best = (segment + 1, point); }
        return best;
    }

    // A press with the Roads tool (not drawing): on a handle, it drags that point over the ground;
    // on the selected road's curve, a new point there is dragged. False: nothing to drag there.
    public bool BeginRoadDrag(Vector2 pixel, Vector2 viewport)
    {
        if (RoadDrawing || SelectedRoad is not { } road) return false;
        int handle = RoadPointAt(pixel, viewport);
        if (handle >= 0) { SelectRoadPoint(handle); _roadDrag = (handle, false, road.Points[handle]); return true; }
        if (RoadCurveAt(pixel, viewport) is not { } on) return false;
        _roadPreview = road with { Points = road.Points.Insert(on.Index, on.Point) };
        SelectedRoadPoint = on.Index; _roadDrag = (on.Index, true, on.Point);
        _sceneDirty = true; RoadVersion++;
        return true;
    }

    // The dragged point follows the ground under the cursor (road ends attract it; Ctrl keeps its
    // height). An end dropped on a road end meets it along its direction (on the road's own other
    // end: a loop); a loop's seam moves both its ends.
    public void UpdateRoadDrag(Vector2 pixel, Vector2 viewport, bool control)
    {
        if (_roadDrag is not { } drag || SelectedRoadId is not { } id || !Roads.TryGetValue(id, out var stored)) return;
        if (RoadCursorAt(pixel, viewport, false, false, excluded: id) is not { } cursor) return;
        var point = cursor.Snap?.Point ?? cursor.Point;
        if (control && cursor.Snap is null) point = point with { Z = drag.Start.Z };
        int n = stored.Points.Length;
        bool end = !drag.Inserted && n >= 2 && (drag.Index == 0 || drag.Index == n - 1);
        if (drag.Inserted) PreviewRoadPoint(point);
        else if (end && IsLoop(stored))
            _roadPreview = stored with { Points = stored.Points.SetItem(0, point).SetItem(n - 1, point) };
        else if (end && cursor.Snap is { } s)
        {
            float radius = RoadGeometry.TurnRadius(cursor.HalfWidth);
            if (drag.Index == n - 1)
            {
                var rest = stored.Points[..^1];
                Vector3? leave = rest.Length >= 2 ? RoadSpline.EndDirection(rest) : null;
                _roadPreview = stored with { Points = [.. rest, .. RoadGeometry.Connect(rest[^1], leave, s.Point, -s.Outward, radius, rest.Length >= 2 ? rest[^2] : null), s.Point] };
                SelectedRoadPoint = _roadPreview.Points.Length - 1;
            }
            else
            {
                var rest = stored.Points[1..];
                Vector3? leave = rest.Length >= 2 ? -RoadSpline.StartDirection(rest) : null;
                _roadPreview = stored with { Points = [s.Point, .. RoadGeometry.Connect(rest[0], leave, s.Point, -s.Outward, radius, rest.Length >= 2 ? rest[1] : null).Reverse(), .. rest] };
                SelectedRoadPoint = 0;
            }
        }
        else { _roadPreview = stored with { Points = stored.Points.SetItem(drag.Index, point) }; SelectedRoadPoint = drag.Index; }
        _sceneDirty = true;
        RoadHover = cursor with { Point = point };
    }

    // A road whose last point is its first: a loop (its seam's handles move together).
    private static bool IsLoop(RoadDefinition road) => road.Points.Length > 2 && Vector3.Distance(road.Points[0], road.Points[^1]) < .05f;

    // Released: moved (or inserted), one undo step; a press without moving only selected.
    public void EndRoadDrag(bool moved)
    {
        if (_roadDrag is not { } drag) return;
        _roadDrag = null; RoadHover = null;
        if (moved || drag.Inserted) CommitRoad(_roadPreview ?? SelectedRoad!, drag.Inserted ? Loc.T("Insert road point") : Loc.T("Move road point"));
        else CancelRoadPreview();
    }

    public void CancelRoadDrag() { if (_roadDrag is null) return; _roadDrag = null; RoadHover = null; CancelRoadPreview(); }

    // Screen handles of the selected road: position, index, selected, under the cursor, an end.
    public IEnumerable<(Vector2 Screen, int Index, bool Selected, bool Hovered, bool End)> RoadHandles(Vector2 viewport)
    {
        if (SelectedRoad is not { } road || _renderer.RenderedWorldCamera is not { } frame) yield break;
        for (int i = 0; i < road.Points.Length; i++)
            if (Project(road.Points[i], frame, viewport) is { } screen)
                yield return (screen, i, i == SelectedRoadPoint, i == RoadHoverHandle, i == 0 || i == road.Points.Length - 1);
    }

    public Vector2? RoadScreen(Vector3 game, Vector2 viewport) => _renderer.RenderedWorldCamera is { } frame ? Project(game, frame, viewport) : null;

    // The road's profile: along it (metres), its height and the ground's under it (the game's with
    // the project's edits, earthworks aside; null where nothing is read), at `samples` stations.
    public (float Distance, float Road, float? Ground)[] RoadProfile(RoadDefinition road, int samples = 120)
    {
        var line = RoadSpline.Sample(road.Points, .5f);
        if (line.Length < 2) return [];
        var arc = new float[line.Length];
        for (int i = 1; i < line.Length; i++) arc[i] = arc[i - 1] + Vector3.Distance(line[i - 1], line[i]);
        var result = new (float, float, float?)[samples];
        int j = 0;
        for (int k = 0; k < samples; k++)
        {
            float s = arc[^1] * k / (samples - 1);
            while (j + 1 < arc.Length - 1 && arc[j + 1] < s) j++;
            float span = arc[j + 1] - arc[j];
            var p = span < 1e-5f ? line[j] : Vector3.Lerp(line[j], line[j + 1], Math.Clamp((s - arc[j]) / span, 0, 1));
            float? ground = Terrain?.EditedGroundAt(new(p.X, p.Y));
            ground ??= Grid.Ground(Coordinates.GameToView(p) with { Y = p.Z + 60 }, 0, 800, RoadPrefix);
            result[k] = (s, p.Z, ground);
        }
        return result;
    }

    // --- Earthworks ----------------------------------------------------------------------------------

    private readonly Dictionary<string, (RoadDefinition Road, float HalfWidth, bool Complete, TerrainShape Shape)> _roadShapes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (Junction Junction, RoadGround Ground, bool Complete, TerrainShape[] Shapes)> _junctionShapes = new(StringComparer.Ordinal);
    private double _nextRoadShapes;
    private int _roadShapesBases = -1;

    // The terrain shapes of the roads that shape the ground (their ghost and dragged preview
    // too, a few times a second while they move). A road whose ground is not read yet is laid
    // again as it arrives (its banks' reach depends on it).
    private void UpdateRoadShapes(TerrainSculptor sculptor, double now)
    {
        bool moving = _roadGhost is not null || _roadPreview is not null;
        if (moving && now < _nextRoadShapes) return;
        _nextRoadShapes = now + .25;
        bool arrived = _roadShapesBases != _terrainBasesArrived;
        _roadShapesBases = _terrainBasesArrived;
        var shapes = new List<TerrainShape>();
        var live = new HashSet<string>(StringComparer.Ordinal);
        bool changed = false;
        // The cells a shape lies on, read with their neighbours (its banks reach into them):
        // whether all are known already.
        bool Ensure(IEnumerable<Vector3> line)
        {
            var cells = line.Select(p => TerrainCell.At(p.X, p.Y)).Distinct().ToArray();
            sculptor.Ensure(cells.SelectMany(c => Enumerable.Range(-1, 3).SelectMany(dy => Enumerable.Range(-1, 3).Select(dx => new TerrainCell(c.X + dx, c.Y + dy)))).Distinct());
            return cells.All(sculptor.IsKnown);
        }
        var drawn = new Dictionary<string, RoadDefinition>(StringComparer.Ordinal);
        foreach (var road in DrawnRoads(_roadGhost))
        {
            drawn[road.Id] = road;
            if (road.Ground is null || RoadTemplateHalfWidth(road.Template) is not { } half) continue;
            live.Add(road.Id);
            if (_roadShapes.TryGetValue(road.Id, out var known) && ReferenceEquals(known.Road, road) && known.HalfWidth == half && (known.Complete || !arrived))
            { shapes.Add(known.Shape); continue; }
            if (RoadGround.Of(road, half) is not { } shape) continue;
            _roadShapes[road.Id] = (road, half, Ensure(shape.Line), shape);
            shapes.Add(shape); changed = true;
        }
        foreach (var key in _roadShapes.Keys.Where(k => !live.Contains(k)).ToArray()) { _roadShapes.Remove(key); changed = true; }
        // Under the junctions (applied after the roads': the ground follows their surface where
        // the roads meet, curb returns and sidewalk corners included).
        var junctions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var junction in Junctions.Junctions)
        {
            if (RoadJunctions.GroundOf(junction, drawn) is not { } ground) continue;
            junctions.Add(junction.Key);
            if (_junctionShapes.TryGetValue(junction.Key, out var known) && ReferenceEquals(known.Junction, junction) && known.Ground == ground && (known.Complete || !arrived))
            { shapes.AddRange(known.Shapes); continue; }
            TerrainShape[] made = [.. RoadJunctions.Ground(junction, ground)];
            _junctionShapes[junction.Key] = (junction, ground, Ensure(made.SelectMany(s => s.Line)), made);
            shapes.AddRange(made); changed = true;
        }
        foreach (var key in _junctionShapes.Keys.Where(k => !junctions.Contains(k)).ToArray()) { _junctionShapes.Remove(key); changed = true; }
        if (changed) sculptor.SetShapes(shapes);
    }

    // --- Overlay ---------------------------------------------------------------------------------------

    private static readonly Vector4 RoadGentle = new(.3f, .88f, 1f, 1), RoadSteep = new(1f, .72f, .25f, 1), RoadTooSteep = new(1f, .36f, .32f, 1);
    private static readonly Vector4 RoadEdge = new(1f, 1f, 1f, .42f), RoadJoint = new(1f, 1f, 1f, .75f);
    public static Vector4 GradeColor(float grade) => MathF.Abs(grade) < .06f ? RoadGentle : MathF.Abs(grade) < .12f ? RoadSteep : RoadTooSteep;
    private (RoadDefinition Road, Vector3[] Line)? _overlayLine;

    private void BuildRoadOverlay()
    {
        var lines = _renderer.OverlayLines;
        // The stretch the next click adds: a ribbon as wide as the road, coloured by its grade.
        if (RoadDrawing && _roadGhost is { } ghost && RoadHover is { } cursor)
        {
            float half = RoadTemplateHalfWidth(ghost.Template) ?? 3;
            var (from, to) = _ghostStretch;
            int lo = Math.Max(0, from - 1), hi = Math.Min(ghost.Points.Length, to + 2);
            var stretch = RoadSpline.SampleSegments(ghost.Points[lo..hi], .5f).Where(s => s.Segment + lo >= from && s.Segment + lo < Math.Max(to, from + 1)).Select(s => s.Point).ToArray();
            AddRibbon(stretch, half, cursor.Radius is { } r && r < half ? RoadTooSteep : null);
        }
        var road = RoadDrawing && _roadGhost is { } g && g.Id == SelectedRoadId ? g : SelectedRoad;
        if (road is null) return;
        float width = RoadTemplateHalfWidth(road.Template) ?? 3;
        if (_overlayLine?.Road != road) _overlayLine = (road, RoadSpline.Sample(road.Points, 1f));
        var line = _overlayLine.Value.Line;
        for (int i = 1; i < line.Length; i++)
        {
            var a = line[i - 1]; var b = line[i];
            var color = GradeColor(RoadGeometry.Grade(a, b));
            lines.Add(new(Coordinates.GameToView(a) + Vector3.UnitY * .3f, Coordinates.GameToView(b) + Vector3.UnitY * .3f, color));
            var right = Right(b - a) * width;
            lines.Add(new(Coordinates.GameToView(a + right) + Vector3.UnitY * .15f, Coordinates.GameToView(b + right) + Vector3.UnitY * .15f, RoadEdge));
            lines.Add(new(Coordinates.GameToView(a - right) + Vector3.UnitY * .15f, Coordinates.GameToView(b - right) + Vector3.UnitY * .15f, RoadEdge));
        }
        // Where the pieces meet (one bended node each).
        if (_roadMeshes.TryGetValue(road.Id, out var built))
            foreach (var (_, _, origin, frames) in built.Segments)
            {
                if (frames.IsDefaultOrEmpty) continue;
                var f = frames[0];
                var across = new Vector3(f.M11, f.M12, f.M13) * width;
                var at = origin + f.Translation;
                lines.Add(new(Coordinates.GameToView(at - across) + Vector3.UnitY * .3f, Coordinates.GameToView(at + across) + Vector3.UnitY * .3f, RoadJoint));
            }
    }

    private static Vector3 Right(Vector3 along)
    {
        var flat = new Vector3(along.X, along.Y, 0);
        if (flat.LengthSquared() < 1e-8f) return Vector3.UnitX;
        flat = Vector3.Normalize(flat);
        return new(flat.Y, -flat.X, 0);
    }

    // A translucent strip `half` metres each side of a polyline, coloured by its grade.
    private void AddRibbon(Vector3[] line, float half, Vector4? color)
    {
        var fills = _renderer.OverlayFills; var lines = _renderer.OverlayLines;
        var lift = Vector3.UnitY * .12f;
        for (int i = 1; i < line.Length; i++)
        {
            var a = line[i - 1]; var b = line[i];
            var c = color ?? GradeColor(RoadGeometry.Grade(a, b));
            var right = Right(b - a) * half;
            Vector3 al = Coordinates.GameToView(a - right) + lift, ar = Coordinates.GameToView(a + right) + lift, bl = Coordinates.GameToView(b - right) + lift, br = Coordinates.GameToView(b + right) + lift;
            fills.Add(new(al, ar, br, c with { W = .26f })); fills.Add(new(al, br, bl, c with { W = .26f }));
            lines.Add(new(al, bl, c)); lines.Add(new(ar, br, c));
            lines.Add(new(Coordinates.GameToView(a) + lift * 2, Coordinates.GameToView(b) + lift * 2, c with { W = .9f }));
        }
    }
}
