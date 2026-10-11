// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Numerics;
using NCWE.Core;
using NCWE.Render;

namespace NCWE.Studio.Session;

// Direct manipulation of traffic lanes, everything with the mouse and visible as it happens:
// - hovering names what is under the cursor (lane, point, end, link) and highlights it;
// - a lane's end (cyan knob) dragged onto another lane links them (where it is released along
//   it), dragged onto the ground extends the lane with a new linked one; its start (magenta
//   knob) the same way round (the lane released on leads into it);
// - points drag flat, at their own height (Ctrl: along the ground under the cursor, Shift: up and
//   down; the gizmo moves the selected one along one axis), the selected lane drags as a whole;
// - double-click on the selected lane adds a point there, Alt+click deletes a point or a link.
// Each gesture is one undo step.
internal sealed partial class StudioSession
{
    public enum TrafficHandle { None, Lane, Point, End, Start, Link }
    public readonly record struct TrafficHover(TrafficHandle Kind, string? Lane, int Point = -1, string? Target = null);

    public TrafficHover TrafficHovered { get; private set; }
    // A link of the selected lane to show bold (the panel's link rows on hover).
    public (string From, string To)? HighlightedLink { get; set; }
    // Every lane's links drawn (curves at the junctions), or only the selected lane's.
    public bool TrafficShowLinks { get; set; } = true;
    public bool TrafficShowDeadEnds { get; set; }
    private Vector3? _trafficCursor;

    private enum DragKind { Point, Lane, End, Start }
    private sealed class TrafficDrag
    {
        public DragKind Kind; public string Lane = ""; public int Point; public Vector2 Press; public Vector3 Anchor; public float StartZ;
        public Vector3 Ground; public string? Target; public float TargetStation; public bool Valid; public string Message = "";
        public float PlaneZ, Reach; public Vector3 Last;
    }
    private TrafficDrag? _drag;
    public bool TrafficDragging => _drag is not null;

    // --- Hit testing --------------------------------------------------------------------------------

    private TrafficHover HitTraffic(Vector2 pixel, Vector2 viewport)
    {
        if (_renderer.RenderedWorldCamera is not { } frame) return default;
        // The selected lane's points, then its knobs, then its links.
        if (SelectedLaneState is { } lane && SelectedLane is { } key)
        {
            int point = TrafficPointAt(pixel, viewport);
            if (point > 0 && point < lane.Outline.Length - 1) return new(TrafficHandle.Point, key, point);
            if (KnobAt(lane, pixel, viewport, frame) is { } knob) return new(knob, key, knob == TrafficHandle.End ? lane.Outline.Length - 1 : 0);
            if (point >= 0) return new(TrafficHandle.Point, key, point);
            if (LinkAt(key, lane, pixel, viewport, frame) is { } link) return link;
        }
        var hit = TrafficLaneAt(pixel, viewport);
        if (hit is null || Lane(hit) is not { } hovered) return default;
        // Near a lane's end: its end knob (drag it to link or extend).
        if (Project(hovered.Outline[^1], frame, viewport) is { } end && Vector2.Distance(end, pixel) < 16) return new(TrafficHandle.End, hit, hovered.Outline.Length - 1);
        return new(TrafficHandle.Lane, hit);
    }

    private static float KnobPixels => 16;
    private TrafficHandle? KnobAt(TrafficLaneState lane, Vector2 pixel, Vector2 viewport, CameraFrame frame)
    {
        float end = Project(lane.Outline[^1], frame, viewport) is { } e ? Vector2.Distance(e, pixel) : float.MaxValue;
        float start = Project(lane.Outline[0], frame, viewport) is { } s ? Vector2.Distance(s, pixel) : float.MaxValue;
        if (Math.Min(end, start) > KnobPixels) return null;
        return end <= start ? TrafficHandle.End : TrafficHandle.Start;
    }

    // A link curve of the selected lane under the cursor (out and in).
    private TrafficHover? LinkAt(string key, TrafficLaneState lane, Vector2 pixel, Vector2 viewport, CameraFrame frame)
    {
        TrafficHover? best = null; float bestDistance = 9;
        void Test(string from, string to, Vector3[] curve)
        {
            for (int i = 1; i < curve.Length; i++)
            {
                if (Project(curve[i - 1], frame, viewport) is not { } a || Project(curve[i], frame, viewport) is not { } b) continue;
                var ab = b - a; float t = ab.LengthSquared() < 1e-6f ? 0 : Math.Clamp(Vector2.Dot(pixel - a, ab) / ab.LengthSquared(), 0, 1);
                float d = Vector2.Distance(a + ab * t, pixel);
                if (d < bestDistance) { bestDistance = d; best = new(TrafficHandle.Link, from, -1, to); }
            }
        }
        foreach (var link in lane.Out) if (Lane(link.Lane) is { } to) Test(key, link.Lane, LinkCurve(lane, to, link, .45f));
        foreach (var source in lane.In) if (Lane(source) is { } from && from.Out.FirstOrDefault(l => l.Lane == key) is { } back) Test(source, key, LinkCurve(from, lane, back, .45f));
        return best;
    }

    // A link drawn as a curve from where traffic leaves a lane to where it enters the next,
    // leaving along the first and arriving along the second (raised above the ribbons).
    internal static Vector3[] LinkCurve(TrafficLaneState from, TrafficLaneState to, TrafficLink link, float lift)
    {
        var a = Along([.. from.Outline], link.ExitPosition >= from.Length - .05f ? Math.Max(0, from.Length - 1.2f) : link.ExitPosition);
        var b = Along([.. to.Outline], Math.Min(to.Length, link.EntryPosition + 1.2f));
        var p0 = a.Point; var p3 = b.Point;
        float reach = Math.Clamp(Vector3.Distance(p0, p3) * .45f, .4f, 12);
        var p1 = p0 + new Vector3(a.Direction, 0) * reach; var p2 = p3 - new Vector3(b.Direction, 0) * reach;
        var curve = new Vector3[10];
        for (int i = 0; i < curve.Length; i++)
        {
            float t = i / (curve.Length - 1f), u = 1 - t;
            curve[i] = u * u * u * p0 + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t * p3 + new Vector3(0, 0, lift);
        }
        return curve;
    }

    // --- Hover ------------------------------------------------------------------------------------

    public void UpdateTrafficHover(Vector2? pixel, Vector2 viewport)
    {
        if (Traffic is null || pixel is not { } p) { TrafficHovered = default; if (_drag is null) HoverLabel = null; _trafficCursor = null; return; }
        if (TrafficDrawing) { _trafficCursor = RoadGroundPoint(p, viewport); HoverLabel = DrawLabel(); TrafficHovered = default; return; }
        if (_drag is not null) { HoverLabel = _drag.Message; return; }
        if (TrafficLinking is not null) { TrafficHovered = TrafficLaneAt(p, viewport) is { } t ? new(TrafficHandle.Lane, t) : default; HoverLabel = TrafficHovered.Lane is null ? Loc.T("Click a lane (Esc cancels)") : Loc.T("Click to link"); return; }
        var hover = HitTraffic(p, viewport);
        if (hover != TrafficHovered) { TrafficHovered = hover; }
        HoverLabel = hover.Kind switch
        {
            TrafficHandle.Point => Loc.F("Point {0}: drag to move it flat (Ctrl: on the ground, Shift: height) · the gizmo moves it along one axis · Alt+click deletes it", hover.Point + 1),
            TrafficHandle.End => Loc.F("{0}\nEnd: drag onto a lane to link, onto the ground to extend", Describe(hover.Lane!)),
            TrafficHandle.Start => Loc.T("Start: drag onto the lane that should lead here, or onto the ground to extend backwards"),
            TrafficHandle.Link => Loc.F("Link -> {0} · Alt+click removes it", (Lane(hover.Target!) is { } t ? Loc.T(LaneKind(t.Flags)).ToLowerInvariant() : Loc.T("lane"))),
            TrafficHandle.Lane => Describe(hover.Lane!) + (hover.Lane == SelectedLane ? Loc.T("\ndrag to move it · double-click adds a point") : ""),
            _ => null
        };
    }

    private string DrawLabel() => _drawPoints.Count == 0 ? Loc.T("Click the lane's first point (on a lane's end: it continues it)")
        : Loc.F("Point {0} · {1:0} m · Enter finishes · Backspace undoes a point", _drawPoints.Count + 1, TrafficGeometry.Length([.. _drawPoints, _trafficCursor ?? _drawPoints[^1]]));

    private string Describe(string key)
    {
        if (Lane(key) is not { } lane) return key;
        bool dead = lane.Out.Length == 0 && (lane.Flags & TrafficLaneFlags.NeverDeadEnd) == 0;
        return Loc.F("{0} · {1:0} m · {2:0} km/h · {3} out, {4} in{5}{6}", Loc.T(LaneKind(lane.Flags)), lane.Length, lane.MaxSpeed * 3.6, lane.Out.Length, lane.In.Length, (dead ? Loc.T(" · dead end") : ""), (IsLaneDeleted(key) ? Loc.T(" · deleted") : TrafficKeys.IsCreated(key) ? Loc.T(" · drawn") : TrafficEdits.ContainsKey(key) ? Loc.T(" · edited") : ""));
    }

    // --- Dragging ------------------------------------------------------------------------------------

    // The cursor on the horizontal plane at height z, at most `reach` metres (flat) from the camera:
    // near the horizon (or above it) the plane runs away, so the point stops at the reach in the
    // cursor's direction instead of flying off.
    private Vector3? PlanePoint(Vector2 pixel, Vector2 viewport, float z, float reach = 5000)
    {
        if (GameRay(pixel, viewport) is not var (origin, direction)) return null;
        var heading = new Vector2(direction.X, direction.Y);
        float t = MathF.Abs(direction.Z) < 1e-4f ? -1 : (z - origin.Z) / direction.Z;
        if (t > 0 && heading.Length() * t <= reach) return origin + direction * t;
        if (heading.LengthSquared() < 1e-8f) return null;
        heading = Vector2.Normalize(heading) * reach;
        return new Vector3(origin.X + heading.X, origin.Y + heading.Y, z);
    }

    // A drag follows the plane at its start height, or the ground under the cursor with Ctrl.
    private Vector3 DragPoint(TrafficDrag d, Vector2 pixel, Vector2 viewport, bool ground)
    {
        var point = ground ? RoadGroundPoint(pixel, viewport) : PlanePoint(pixel, viewport, d.PlaneZ, d.Reach) ?? d.Last;
        d.Last = point;
        return point;
    }

    public bool BeginTrafficDrag(Vector2 pixel, Vector2 viewport)
    {
        if (Traffic is null || TrafficDrawing || TrafficLinking is not null) return false;
        var hit = HitTraffic(pixel, viewport);
        DragKind kind;
        switch (hit.Kind)
        {
            case TrafficHandle.Point: kind = DragKind.Point; break;
            case TrafficHandle.End: kind = DragKind.End; break;
            case TrafficHandle.Start: kind = DragKind.Start; break;
            case TrafficHandle.Lane when hit.Lane == SelectedLane && !IsLaneDeleted(hit.Lane!): kind = DragKind.Lane; break;
            default: return false;
        }
        if (Lane(hit.Lane!) is not { } lane) return false;
        if (IsLocked(LaneEntryId(hit.Lane!))) { Status = Loc.T("This lane is locked: L or the outliner's lock unlocks it."); return false; }
        if (hit.Lane != SelectedLane) SelectLane(hit.Lane);
        if (kind == DragKind.Point) SelectLanePoint(hit.Point);
        // The plane the drag happens on: the point's (or end's) height, the lane's mean height when moved whole.
        float planeZ = hit.Point >= 0 && hit.Point < lane.Outline.Length ? lane.Outline[hit.Point].Z : lane.Outline.Average(p => p.Z);
        // How far it can go: four times its distance from the camera (60 m at least).
        var grabbed = lane.Outline[Math.Clamp(hit.Point, 0, lane.Outline.Length - 1)];
        var eye = Coordinates.ViewToGame(Camera.Eye);
        float reach = Math.Max(60, 4 * Vector2.Distance(new(eye.X, eye.Y), new(grabbed.X, grabbed.Y)));
        var anchor = PlanePoint(pixel, viewport, planeZ, reach) ?? grabbed;
        _drag = new TrafficDrag { Kind = kind, Lane = hit.Lane!, Point = hit.Point, Press = pixel, Anchor = anchor, Ground = anchor, Last = anchor, PlaneZ = planeZ, Reach = reach,
            StartZ = hit.Point >= 0 && hit.Point < lane.Outline.Length ? lane.Outline[hit.Point].Z : 0 };
        _drag.Message = kind switch { DragKind.End => Loc.T("Release on a lane to link · on the ground to extend"), DragKind.Start => Loc.T("Release on the lane that leads here · on the ground to extend"), _ => "" };
        return true;
    }

    public void UpdateTrafficDrag(Vector2 pixel, Vector2 viewport, bool vertical, bool followGround = false)
    {
        if (_drag is not { } d || Lane(d.Lane) is not { } lane) return;
        var ground = vertical ? d.Last : DragPoint(d, pixel, viewport, followGround);
        d.Ground = ground;
        switch (d.Kind)
        {
            case DragKind.Point:
            {
                Vector3 to;
                if (vertical)
                {
                    float metres = Vector3.Distance(Coordinates.ViewToGame(Camera.Eye), lane.Outline[d.Point]) * .0025f;
                    to = (_lanePreview ?? lane).Outline[d.Point] with { Z = d.StartZ + (d.Press.Y - pixel.Y) * metres };
                    d.Message = Loc.F("Height {0:+0.0;-0.0} m", to.Z - d.StartZ);
                }
                else
                {
                    // Lane ends snap onto other lanes' ends, so chains join cleanly.
                    to = ground; string snap = "";
                    if (d.Point == 0 || d.Point == lane.Outline.Length - 1)
                        foreach (var other in LanesNear(ground, 3))
                            if (other != d.Lane && other != TwinOf(d.Lane) && Lane(other) is { } o && !IsLaneDeleted(other))
                                foreach (var end in new[] { o.Outline[0], o.Outline[^1] })
                                    if (Vector3.Distance(end, ground) < 1.2f) { to = end; snap = Loc.T(" · snapped to a lane end"); }
                    d.Message = Loc.F("Point {0}{1}{2}", d.Point + 1, snap, (followGround ? Loc.T(" · on the ground") : Loc.F(" · height kept ({0:0.0} m), Ctrl: follow the ground, Shift: height", to.Z)));
                }
                PreviewLanePoint(to);
                break;
            }
            case DragKind.Lane:
            {
                var delta = ground - d.Anchor; delta.Z = 0;
                _lanePreview = lane with { Outline = [.. lane.Outline.Select(p => p + delta)] };
                d.Message = Loc.F("Moved {0:0.0} m", new Vector2(delta.X, delta.Y).Length());
                TrafficVersion++;
                break;
            }
            default:
            {
                // Link (onto a lane) or extension (onto the ground).
                d.Target = null; d.Valid = false;
                var target = TrafficLaneAt(pixel, viewport, exclude: d.Lane);
                if (target is not null && Lane(target) is { } to && !IsLaneDeleted(target))
                {
                    d.Target = target;
                    float station = TrafficGeometry.Station(to.Outline, ground);
                    bool outgoing = d.Kind == DragKind.End;
                    if (outgoing) station = station < 2.5f ? 0 : station;
                    else station = station > to.Length - 2.5f ? to.Length : station;
                    d.TargetStation = station;
                    bool exists = outgoing ? lane.Out.Any(l => l.Lane == target) : to.Out.Any(l => l.Lane == d.Lane);
                    bool twin = TwinOf(d.Lane) == target;
                    d.Valid = !exists && !twin;
                    d.Message = exists ? Loc.T("Already linked") : twin ? Loc.T("That is this lane's other direction")
                        : outgoing ? Loc.F("Link -> {0}{1}", Loc.T(LaneKind(to.Flags)).ToLowerInvariant(), (station > 0 ? Loc.F(", joining it at {0:0} m", station) : Loc.T(", at its start")))
                        : Loc.F("Link <- {0}{1}", Loc.T(LaneKind(to.Flags)).ToLowerInvariant(), (station < to.Length ? Loc.F(", leaving it at {0:0} m", station) : Loc.T(", from its end")));
                }
                else
                {
                    var from = d.Kind == DragKind.End ? lane.Outline[^1] : lane.Outline[0];
                    float length = Vector3.Distance(from, ground);
                    d.Valid = length >= 1.5f;
                    d.Message = d.Valid ? Loc.F("Extend: new {0} lane, {1:0} m, linked", Loc.T(LaneKind(lane.Flags)).ToLowerInvariant(), length) : Loc.T("Release on a lane to link · on the ground to extend");
                }
                HoverLabel = d.Message;
                TrafficVersion++;
                break;
            }
        }
        HoverLabel = d.Message.Length > 0 ? d.Message : HoverLabel;
    }

    public void EndTrafficDrag()
    {
        if (_drag is not { } d) return;
        _drag = null; HoverLabel = null;
        if (Lane(d.Lane) is not { } lane) { CancelLanePreview(); return; }
        switch (d.Kind)
        {
            case DragKind.Point: CommitLanePreview(); Status = Loc.T("Point moved."); break;
            case DragKind.Lane:
            {
                if (_lanePreview is not { } moved) return;
                var changes = new Dictionary<string, TrafficLaneState>();
                WithTwin(changes, d.Lane, moved);
                CommitTraffic(changes, Loc.T("Move lane"));
                Status = Loc.T("Lane moved (its links follow).");
                break;
            }
            default:
            {
                TrafficVersion++;
                if (!d.Valid) { Status = d.Message == Loc.T("Release on a lane to link · on the ground to extend") || d.Message == Loc.T("Release on the lane that leads here · on the ground to extend") ? Loc.T("Nothing done: drag farther, onto a lane or the ground.") : d.Message + "."; return; }
                bool outgoing = d.Kind == DragKind.End;
                if (d.Target is { } target)
                {
                    bool ok = outgoing ? LinkLanes(d.Lane, target, entry: d.TargetStation) : LinkLanes(target, d.Lane, exit: d.TargetStation);
                    if (ok) Status = outgoing ? Loc.T("Linked: traffic continues into that lane.") : Loc.T("Linked: that lane now leads into this one.");
                }
                else ExtendLane(d.Lane, lane, d.Ground, outgoing);
                break;
            }
        }
    }

    public void CancelTrafficDrag()
    {
        if (_drag is null) return;
        _drag = null; HoverLabel = null; _lanePreview = null; TrafficVersion++;
    }

    // A new lane from the lane's end to `to` (or from `to` into its start), same kind, width and
    // speed, linked to it and to the lanes its other end meets. One undo step.
    private void ExtendLane(string key, TrafficLaneState lane, Vector3 to, bool forward)
    {
        var from = forward ? lane.Outline[^1] : lane.Outline[0];
        var points = forward ? new[] { from, to } : new[] { to, from };
        string created = TrafficKeys.NewLane();
        var state = new TrafficLaneState([.. points], lane.Width, lane.MaxSpeed, lane.Flags & ~(TrafficLaneFlags.Bidirectional | TrafficLaneFlags.TrafficDisabled));
        var changes = new Dictionary<string, TrafficLaneState> { [created] = state };
        if (forward) LinkLanes(key, created, changes, entry: 0); else LinkLanes(created, key, changes);
        int links = AutoConnect(created, changes) ;
        if (!CommitTraffic(changes, Loc.T("Extend lane"))) return;
        SelectLane(created, forward ? 1 : 0);
        Status = Loc.F("New lane ({0:0} m) linked to the one it continues{1} Drag its end again to keep going.", Vector3.Distance(from, to), (links > 1 ? Loc.F(" and to {0} other{1} its end meets.", links - 1, (links == 2 ? "" : "s")) : "."));
    }

    // --- Alt and double clicks -------------------------------------------------------------------------

    private (string Lane, double Time, Vector2 Pixel)? _lastLaneClick;

    // Alt+click: a point or a link of the selected lane goes. Returns true when handled.
    private bool TrafficAltClick(Vector2 pixel, Vector2 viewport)
    {
        var hit = HitTraffic(pixel, viewport);
        if (hit.Kind == TrafficHandle.Point && hit.Lane == SelectedLane) { SelectLanePoint(hit.Point); DeleteLanePoint(); return true; }
        if (hit.Kind == TrafficHandle.Link && hit.Target is { } to) { if (UnlinkLanes(hit.Lane!, to)) Status = Loc.T("Link removed."); return true; }
        return false;
    }

    // Double-click on the selected lane: a point there.
    private bool TrafficDoubleClick(string? hit, Vector2 pixel, Vector2 viewport)
    {
        double now = Environment.TickCount64 / 1000.0;
        bool twice = hit is not null && _lastLaneClick is { } last && last.Lane == hit && now - last.Time < .4 && Vector2.Distance(last.Pixel, pixel) < 8;
        _lastLaneClick = hit is null ? null : (hit, now, pixel);
        if (!twice || hit != SelectedLane) return false;
        _lastLaneClick = null;
        InsertLanePoint(RoadGroundPoint(pixel, viewport));
        Status = Loc.T("Point added: drag it to shape the lane.");
        return true;
    }

    // Links its ends to the lanes they meet (the panel's Auto-link button).
    public void AutoLinkSelected()
    {
        if (SelectedLane is not { } key || Lane(key) is not { } lane) return;
        var changes = new Dictionary<string, TrafficLaneState> { [key] = lane };
        int links = AutoConnect(key, changes);
        if (links == 0) { Status = Loc.T("No lane of its kind starts or ends within 3 m of its ends."); return; }
        if (CommitTraffic(changes, Loc.T("Auto-link lane"))) Status = Loc.F("{0} link{1} added to the lanes its ends meet.", links, (links == 1 ? "" : "s"));
    }

    // A drawn lane the other way round (its links, which no longer fit, are dropped).
    public void ReverseSelectedLane()
    {
        if (SelectedLane is not { } key || Lane(key) is not { } lane) return;
        if (!TrafficKeys.IsCreated(key)) { Status = Loc.T("Only lanes drawn in this project can be reversed (game lanes keep their direction; disable one and draw it again)."); return; }
        var changes = new Dictionary<string, TrafficLaneState>();
        foreach (var link in lane.Out) if (Lane(link.Lane) is { } next) changes[link.Lane] = next with { In = next.In.Remove(key) };
        foreach (var source in lane.In) if (Lane(source) is { } previous) changes[source] = previous with { Out = previous.Out.RemoveAll(l => l.Lane == key) };
        changes[key] = lane with { Outline = [.. lane.Outline.Reverse()], Out = [], In = [] };
        int links = AutoConnect(key, changes);
        if (CommitTraffic(changes, Loc.T("Reverse lane"))) Status = Loc.F("Lane reversed ({0} link{1} to the lanes its ends meet).", links, (links == 1 ? "" : "s"));
    }

    // --- Feedback drawn every frame -------------------------------------------------------------------

    private static readonly Vector4 OutColor = new(.2f, .95f, 1f, 1), InColor = new(1f, .3f, .85f, 1), HoverColor = new(1, 1, 1, 1), BadColor = new(1f, .3f, .25f, 1), GoodColor = new(.35f, 1f, .45f, 1);

    private void DrawTrafficFeedback()
    {
        var lines = _renderer.OverlayLines; var fills = _renderer.OverlayFills;
        var eye = Coordinates.ViewToGame(Camera.Eye);
        float Scale(Vector3 at) => Math.Clamp(Vector3.Distance(at, eye) * .02f, .5f, 6f);
        // Hovered lane: a white veil over its ribbon.
        if (TrafficHovered.Lane is { } hovered && TrafficHovered.Kind is TrafficHandle.Lane or TrafficHandle.End && hovered != SelectedLane && Lane(hovered) is { } h)
            Ribbon(fills, h, new Vector4(1, 1, 1, .22f), .1f);
        // Selected lane: its links as bold curves with arrowheads, its knobs.
        if (SelectedLane is { } key && SelectedLaneState is { } lane)
        {
            foreach (var link in lane.Out)
                if (Lane(link.Lane) is { } to)
                {
                    bool bold = HighlightedLink == (key, link.Lane) || TrafficHovered is { Kind: TrafficHandle.Link } hl && hl.Lane == key && hl.Target == link.Lane;
                    Ribbon(fills, to, OutColor with { W = bold ? .3f : .14f }, .12f);
                    Arrow(lines, LinkCurve(lane, to, link, .45f), bold ? HoverColor : OutColor, Scale(to.Outline[0]));
                }
            foreach (var source in lane.In)
                if (Lane(source) is { } from && from.Out.FirstOrDefault(l => l.Lane == key) is { } back)
                {
                    bool bold = HighlightedLink == (source, key) || TrafficHovered is { Kind: TrafficHandle.Link } hl && hl.Lane == source && hl.Target == key;
                    Ribbon(fills, from, InColor with { W = bold ? .3f : .14f }, .12f);
                    Arrow(lines, LinkCurve(from, lane, back, .45f), bold ? HoverColor : InColor, Scale(lane.Outline[0]));
                }
            Knob(lines, fills, lane.Outline[^1], TrafficGeometry.EndDirection(lane.Outline), OutColor, Scale(lane.Outline[^1]), TrafficHovered.Kind == TrafficHandle.End && TrafficHovered.Lane == key, true);
            Knob(lines, fills, lane.Outline[0], TrafficGeometry.StartDirection(lane.Outline), InColor, Scale(lane.Outline[0]), TrafficHovered.Kind == TrafficHandle.Start && TrafficHovered.Lane == key, false);
            if (lane.Out.Length == 0 && (lane.Flags & TrafficLaneFlags.NeverDeadEnd) == 0) DeadEnd(lines, lane, Scale(lane.Outline[^1]));
            if (TrafficHovered is { Kind: TrafficHandle.Point } hp && hp.Lane == key && hp.Point < lane.Outline.Length)
                Ring(lines, lane.Outline[hp.Point], Scale(lane.Outline[hp.Point]) * .9f, HoverColor);
        }
        // A hovered end of another lane: its knob, ready to drag.
        if (TrafficHovered is { Kind: TrafficHandle.End } he && he.Lane != SelectedLane && Lane(he.Lane!) is { } endLane)
            Knob(lines, fills, endLane.Outline[^1], TrafficGeometry.EndDirection(endLane.Outline), OutColor, Scale(endLane.Outline[^1]), true, true);
        // Dragging a knob: the link or the extension being made.
        if (_drag is { Kind: DragKind.End or DragKind.Start } d && Lane(d.Lane) is { } dragged)
        {
            bool outgoing = d.Kind == DragKind.End;
            var from = outgoing ? dragged.Outline[^1] : dragged.Outline[0];
            var color = d.Valid ? (d.Target is null ? new Vector4(1f, .85f, .2f, 1) : GoodColor) : BadColor;
            Vector3 to = d.Ground;
            if (d.Target is { } target && Lane(target) is { } t)
            {
                to = Along([.. t.Outline], d.TargetStation).Point;
                Ribbon(fills, t, color with { W = .3f }, .14f);
            }
            var a = from + new Vector3(0, 0, .5f); var b = to + new Vector3(0, 0, .5f);
            var dir = outgoing ? TrafficGeometry.EndDirection(dragged.Outline) : -TrafficGeometry.StartDirection(dragged.Outline);
            float reach = Math.Clamp(Vector3.Distance(a, b) * .4f, .5f, 15);
            var c1 = a + new Vector3(dir, 0) * reach;
            var curve = Enumerable.Range(0, 12).Select(i => { float s = i / 11f, u = 1 - s; return u * u * a + 2 * u * s * c1 + s * s * b; }).ToArray();
            Arrow(lines, outgoing ? curve : [.. curve.Reverse()], color, Scale(to));
            Ring(lines, to, Scale(to) * .8f, color);
        }
        // Drawing: the segment to the cursor.
        if (TrafficDrawing && _drawPoints.Count > 0 && _trafficCursor is { } cursor)
        {
            var color = new Vector4(1f, .85f, .2f, .8f);
            lines.Add(new(Coordinates.GameToView(_drawPoints[^1] + new Vector3(0, 0, .2f)), Coordinates.GameToView(cursor + new Vector3(0, 0, .2f)), color));
            Ring(lines, cursor, Scale(cursor) * .6f, color);
        }
    }

    private static void Ribbon(List<DebugTriangle> fills, TrafficLaneState lane, Vector4 color, float lift)
    {
        var outline = lane.Outline; int n = outline.Length;
        if (n < 2) return;
        var polygon = TrafficGeometry.Polygon(outline, Math.Max(lane.Width, 1));
        Vector3 V(Vector2 p, float z) => Coordinates.GameToView(new Vector3(p.X, p.Y, z + lift));
        for (int i = 0; i + 1 < n; i++)
        {
            var l0 = V(polygon[i], outline[i].Z); var l1 = V(polygon[i + 1], outline[i + 1].Z);
            var r0 = V(polygon[2 * n - 1 - i], outline[i].Z); var r1 = V(polygon[2 * n - 2 - i], outline[i + 1].Z);
            fills.Add(new(l0, r0, l1, color)); fills.Add(new(l1, r0, r1, color));
        }
    }

    private static void Arrow(List<DebugLine> lines, Vector3[] curve, Vector4 color, float size)
    {
        for (int i = 1; i < curve.Length; i++) lines.Add(new(Coordinates.GameToView(curve[i - 1]), Coordinates.GameToView(curve[i]), color));
        var tip = curve[^1]; var dir = tip - curve[^2];
        var flat = new Vector2(dir.X, dir.Y);
        if (flat.LengthSquared() < 1e-8f) return;
        flat = Vector2.Normalize(flat);
        var back = new Vector3(flat, 0) * size * .7f; var side = new Vector3(-flat.Y, flat.X, 0) * size * .45f;
        lines.Add(new(Coordinates.GameToView(tip), Coordinates.GameToView(tip - back + side), color));
        lines.Add(new(Coordinates.GameToView(tip), Coordinates.GameToView(tip - back - side), color));
    }

    // A disc on the ground with an arrow in the lane's direction (out: pointing away; in: into it).
    private static void Knob(List<DebugLine> lines, List<DebugTriangle> fills, Vector3 at, Vector2 direction, Vector4 color, float size, bool hot, bool end)
    {
        float r = size * (hot ? 1.15f : .85f);
        var c = Coordinates.GameToView(at + new Vector3(0, 0, .3f));
        const int segments = 20;
        for (int i = 0; i < segments; i++)
        {
            float a0 = i * MathF.Tau / segments, a1 = (i + 1) * MathF.Tau / segments;
            var p0 = Coordinates.GameToView(at + new Vector3(MathF.Cos(a0) * r, MathF.Sin(a0) * r, .3f));
            var p1 = Coordinates.GameToView(at + new Vector3(MathF.Cos(a1) * r, MathF.Sin(a1) * r, .3f));
            fills.Add(new(c, p0, p1, color with { W = hot ? .55f : .3f }));
            lines.Add(new(p0, p1, color));
        }
        if (direction.LengthSquared() < 1e-8f) return;
        var d = new Vector3(Vector2.Normalize(direction), 0) * r * .7f; var s = new Vector3(-d.Y, d.X, 0) * .6f;
        var tip = end ? d : d * .2f; var tail = end ? -d * .2f : -d;
        lines.Add(new(Coordinates.GameToView(at + tail + new Vector3(0, 0, .32f)), Coordinates.GameToView(at + tip + new Vector3(0, 0, .32f)), HoverColor));
        lines.Add(new(Coordinates.GameToView(at + tip + new Vector3(0, 0, .32f)), Coordinates.GameToView(at + tip - d * .5f + s + new Vector3(0, 0, .32f)), HoverColor));
        lines.Add(new(Coordinates.GameToView(at + tip + new Vector3(0, 0, .32f)), Coordinates.GameToView(at + tip - d * .5f - s + new Vector3(0, 0, .32f)), HoverColor));
    }

    private static void Ring(List<DebugLine> lines, Vector3 at, float r, Vector4 color)
    {
        const int segments = 16;
        for (int i = 0; i < segments; i++)
        {
            float a0 = i * MathF.Tau / segments, a1 = (i + 1) * MathF.Tau / segments;
            lines.Add(new(Coordinates.GameToView(at + new Vector3(MathF.Cos(a0) * r, MathF.Sin(a0) * r, .35f)), Coordinates.GameToView(at + new Vector3(MathF.Cos(a1) * r, MathF.Sin(a1) * r, .35f)), color));
        }
    }

    // A dead end: a red bar across the lane's end.
    private static void DeadEnd(List<DebugLine> lines, TrafficLaneState lane, float size)
    {
        var end = lane.Outline[^1]; var d = TrafficGeometry.EndDirection(lane.Outline);
        var side = new Vector3(-d.Y, d.X, 0) * Math.Max(lane.Width * .6f, size);
        var forward = new Vector3(d, 0) * size * .5f;
        for (int k = 0; k < 2; k++)
        {
            var lift = new Vector3(0, 0, .3f + k * .05f);
            lines.Add(new(Coordinates.GameToView(end + forward * k + side + lift), Coordinates.GameToView(end + forward * k - side + lift), BadColor));
        }
    }
}
