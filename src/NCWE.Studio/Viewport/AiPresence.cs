// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Numerics;
using ImGuiNET;
using NCWE.Core;

namespace NCWE.Studio.Viewport;

// The AI agent in the 3D view (the NCWE MCP tools reach Studio through the automation pipe):
// - who acts and what it is doing, in a pill at the top of the view;
// - the view taken for itself (lock_view): the edges glow and breathe, the user's mouse and keys no
//   longer steer the view, a "Take back control" button (or Esc) gives it back;
// - what its tools do, animated in the world: rays shot and landing, measures drawn, ground probes
//   dropping, searched areas pinged, objects placed, moved or deleted flashing, its annotations.
// Game space (metres, Z up); everything is drawn in 2D over the frame with the ImGui overlay, so
// screenshots taken for the agent (scene only) never show it.
// A note the user left in the view, or the one being drawn (Draft). Game coordinates.
internal sealed record NoteMark(int Number, NoteKind Kind, Vector3[] Points, string Text, bool Draft, bool Selected = false);

internal sealed class AiPresence
{
    // The notes on screen, set from the session each frame; drawn in DrawWorld.
    private IReadOnlyList<NoteMark> _noteMarks = [];
    public void SetNoteMarks(IReadOnlyList<NoteMark> marks) => _noteMarks = marks;

    // Numbered notes: a dot on each point, lines or a box for the shape, the number and text on the first point.
    // A draft is drawn in cyan until it is complete.
    private void DrawNotes()
    {
        foreach (var note in _noteMarks)
        {
            // A picked note is white and heavier, so it reads as selected.
            var color = note.Draft ? Cyan : note.Selected ? new Vector4(1f, 1f, 1f, 1f) : Amber;
            float alpha = note.Draft ? .85f : 1f;
            float width = note.Selected ? 3.5f : 2f;
            var points = note.Points;
            switch (note.Kind)
            {
                case NoteKind.Line when points.Length >= 2:
                    for (int i = 0; i + 1 < points.Length; i++) Line(points[i], points[i + 1], color, alpha, width);
                    break;
                case NoteKind.Box when points.Length == 2:
                    DrawBox(Vector3.Min(points[0], points[1]), Vector3.Max(points[0], points[1]), color, alpha, width, true);
                    break;
            }
            foreach (var point in points) Dot(point, 3.5f, color, alpha);
            if (points.Length > 0 && Project(points[0], out var at))
                Label(at, note.Draft ? note.Text : $"{note.Number}  {note.Text}", color, alpha, 14);
        }
    }

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    public static double Now => Clock.Elapsed.TotalSeconds;
    // A lock nobody uses any more (the agent stopped, crashed or forgot it) gives the view back.
    public const double IdleRelease = 300;
    private const float Near = .05f;
    private const double FadeOut = .8, PillLinger = 4;
    private const int MaximumVisuals = 3000;

    public static readonly Vector4 Cyan = new(.13f, .89f, .95f, 1), Violet = new(.62f, .45f, 1f, 1), Amber = new(1f, .72f, .3f, 1),
        Green = new(.36f, .95f, .56f, 1), Red = new(1f, .4f, .4f, 1), White = new(1, 1, 1, 1);
    private static readonly Vector4 Ink = new(.035f, .045f, .065f, 1), Muted = new(.68f, .73f, .8f, 1);

    // --- Control and activity -------------------------------------------------------------------

    public bool Locked { get; private set; }
    public string? Reason { get; private set; }
    public string Agent { get; private set; } = "AI";
    // When the user last took the view back (the agent should ask before taking it again).
    public DateTime? UserTookBack { get; private set; }
    private double _lockedAt, _releasedAt = double.NegativeInfinity, _lastCommand = double.NegativeInfinity;
    private string? _activity;
    private double _activityAt = double.NegativeInfinity;
    private int _running;
    private double _sweepAt = double.NegativeInfinity, _shutterAt = double.NegativeInfinity;
    private Vector2 _buttonMin, _buttonMax;
    private bool _buttonShown;

    public double LockedSeconds => Locked ? Now - _lockedAt : 0;

    public void Lock(string client, string? reason)
    {
        if (!Locked) _lockedAt = Now;
        Locked = true; Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        Agent = AgentName(client); _lastCommand = Now;
    }

    // by: "user" (button, Esc), "agent" (lock_view false) or "idle".
    public void Release(string by)
    {
        if (!Locked) return;
        Locked = false; _releasedAt = Now;
        if (by == "user") UserTookBack = DateTime.Now;
    }

    // Every command of the pipe (status polls excepted) shows in the pill for a few seconds.
    public void Begin(string client, string op)
    {
        if (BridgeActivity.IsPollOp(op)) return;
        _running++; _activity = ActivityLabel(op); _activityAt = _lastCommand = Now;
        if (!Locked) Agent = AgentName(client);
    }

    public void End(string op)
    {
        if (BridgeActivity.IsPollOp(op)) return;
        _running = Math.Max(0, _running - 1); _activityAt = _lastCommand = Now;
    }

    public void CameraMoved() => _sweepAt = Now;
    // What the pill says the agent just did, when the command's name does not tell it.
    public void Did(string activity) { _activity = activity; _activityAt = Now; }
    public void Shutter() => _shutterAt = Now;

    public bool TakeBackHit(Vector2 pixel) => Locked && _buttonShown && pixel.X >= _buttonMin.X && pixel.Y >= _buttonMin.Y && pixel.X <= _buttonMax.X && pixel.Y <= _buttonMax.Y;

    public object State() => new
    {
        locked = Locked, agent = Agent, reason = Reason, locked_seconds = Math.Round(LockedSeconds),
        auto_release_after_idle_seconds = IdleRelease,
        user_took_back = UserTookBack?.ToString("HH:mm:ss")
    };

    internal static string AgentName(string client)
    {
        if (string.IsNullOrWhiteSpace(client) || client == "script") return Loc.T("Automation");
        string name = client.Trim().Split(' ')[0];
        return name.ToLowerInvariant() switch
        {
            "claude-code" => "Claude Code",
            "claude-ai" or "claude" or "claude-desktop" => "Claude",
            "codex" or "codex-mcp-client" => "Codex",
            "cursor" or "cursor-vscode" => "Cursor",
            "visual-studio-code" or "vscode" => "VS Code",
            _ => string.Join(' ', name.Replace('-', ' ').Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(w => char.ToUpperInvariant(w[0]) + w[1..]))
        };
    }

    internal static string ActivityLabel(string op) => op switch
    {
        "api.camera" or "goto" => Loc.T("Moving the camera"),
        "api.screenshot" or "screenshot" => Loc.T("Looking at the view"),
        "api.raycast" => Loc.T("Casting rays"),
        "api.ground" => Loc.T("Probing the ground"),
        "api.measure" => Loc.T("Measuring"),
        "api.measure.space" => Loc.T("Measuring the space"),
        "api.annotate" => Loc.T("Pointing things out"),
        "api.say" => Loc.T("Writing to you"),
        "api.pick" => Loc.T("Pointing at the view"),
        "api.query" => Loc.T("Searching the area"),
        "api.object" or "api.selection" => Loc.T("Inspecting objects"),
        "api.select" => Loc.T("Selecting"),
        "api.place" or "api.prefab.place" => Loc.T("Placing objects"),
        "api.elements.add" => Loc.T("Adding elements"),
        "api.elements.edit" => Loc.T("Editing an element"),
        "api.transform" => Loc.T("Moving objects"),
        "api.duplicate" => Loc.T("Duplicating"),
        "api.delete" or "api.clear_area" => Loc.T("Removing objects"),
        "api.undo" => Loc.T("Undoing"),
        "api.redo" => Loc.T("Redoing"),
        "api.control" => Loc.T("Taking the view"),
        "api.project.export" => Loc.T("Exporting the mod"),
        "api.view" => Loc.T("Changing the view"),
        _ when op.StartsWith("api.collisions.", StringComparison.Ordinal) => Loc.T("Editing collision"),
        _ when op.StartsWith("api.occlusion.", StringComparison.Ordinal) => Loc.T("Editing occlusion"),
        _ when op.StartsWith("api.roads.", StringComparison.Ordinal) => Loc.T("Working on roads"),
        _ when op.StartsWith("api.elevators.", StringComparison.Ordinal) => Loc.T("Working on elevators"),
        _ when op.StartsWith("api.traffic.", StringComparison.Ordinal) => Loc.T("Working on traffic"),
        _ when op.StartsWith("api.assets.", StringComparison.Ordinal) => Loc.T("Browsing assets"),
        _ when op.StartsWith("api.custom.", StringComparison.Ordinal) || op.StartsWith("api.sounds.", StringComparison.Ordinal) => Loc.T("Using the library"),
        _ when op.StartsWith("api.project", StringComparison.Ordinal) => Loc.T("Managing the project"),
        _ => Loc.T("Working")
    };

    // --- World visuals ----------------------------------------------------------------------------

    private enum Kind { Ray, Measure, Probe, Sphere, Region, Brackets, Flash, Pin, Tag, Arrow, Path, Box, Reticle, Footprint }

    private sealed class Visual
    {
        public Kind Kind;
        public Vector3[] Points = [];
        public string?[]? Texts;
        public string? Text;
        public Vector4 Color = Cyan;
        public float Radius;
        public Vector3 Normal;
        public bool Hit = true, Legs, Closed, Removal, Annotation;
        public double Born, Delay, Duration;   // Duration 0: until cleared
    }

    private readonly List<Visual> _visuals = [];

    private void Add(Visual v)
    {
        v.Born = Now;
        if (_visuals.Count >= MaximumVisuals) _visuals.RemoveRange(0, _visuals.Count - MaximumVisuals + 1);
        _visuals.Add(v);
    }

    // A ray: shot from the origin, it travels to its end and lands (an impact on the surface).
    public void Ray(Vector3 origin, Vector3 end, bool hit, Vector3 normal, string? label, double delay = 0, double duration = 8)
        => Add(new() { Kind = Kind.Ray, Points = [origin, end], Hit = hit, Normal = normal, Text = label, Delay = delay, Duration = duration, Color = Cyan });

    // A measure: the line draws itself, ticks every step, then the lengths. legs: a 2-point measure
    // also shows its horizontal and vertical parts (labels[1], labels[2]).
    public void Measure(Vector3[] points, string?[] labels, string? total, bool legs, bool closed, Vector4 color, double duration = 20)
        => Add(new() { Kind = Kind.Measure, Points = points, Texts = labels, Text = total, Legs = legs, Closed = closed, Color = color, Duration = duration });

    // A ground probe: a drop falls from the top onto the surface and ripples.
    public void Probe(Vector3 top, Vector3 bottom, bool hit, string? label, double delay = 0, double duration = 8)
        => Add(new() { Kind = Kind.Probe, Points = [top, bottom], Hit = hit, Text = label, Delay = delay, Duration = duration, Color = Green });

    // A searched sphere or box (sonar ping).
    public void Sphere(Vector3 center, float radius, Vector4 color, double duration = 3.5)
        => Add(new() { Kind = Kind.Sphere, Points = [center], Radius = radius, Color = color, Duration = duration });
    public void Region(Vector3 min, Vector3 max, Vector4 color, double duration = 3.5)
        => Add(new() { Kind = Kind.Region, Points = [min, max], Color = color, Duration = duration });

    // Corner brackets around something found.
    public void Brackets(Bounds3 box, Vector4 color, double delay = 0, double duration = 3.5)
        => Add(new() { Kind = Kind.Brackets, Points = [box.Min, box.Max], Color = color, Delay = delay, Duration = duration });

    // An object placed, moved or edited (a scan rises through its box), or deleted (it sinks).
    public void Flash(Bounds3 box, Vector4 color, bool removal = false, double delay = 0, double duration = 2.4)
        => Add(new() { Kind = Kind.Flash, Points = [box.Min, box.Max], Color = color, Removal = removal, Delay = delay, Duration = duration });

    // The point a pixel of a screenshot pointed at.
    public void Reticle(Vector3 point, string? label, double duration = 5)
        => Add(new() { Kind = Kind.Reticle, Points = [point], Text = label, Duration = duration, Color = Amber });

    // The free space around a point (measure_space): the ring of wall hits, filled.
    public void Footprint(Vector3 center, Vector3[] ring, string? label, double delay, double duration = 20)
        => Add(new() { Kind = Kind.Footprint, Points = [center, .. ring], Text = label, Delay = delay, Duration = duration, Color = Cyan });

    // Annotations the agent draws for the user (cleared with ClearAnnotations or after their duration).
    public void Pin(Vector3 point, string? text, Vector4 color, double duration) => Add(new() { Kind = Kind.Pin, Points = [point], Text = text, Color = color, Duration = duration, Annotation = true });
    public void Tag(Vector3 point, string text, Vector4 color, double duration) => Add(new() { Kind = Kind.Tag, Points = [point], Text = text, Color = color, Duration = duration, Annotation = true });
    public void Arrow(Vector3 from, Vector3 to, string? text, Vector4 color, double duration) => Add(new() { Kind = Kind.Arrow, Points = [from, to], Text = text, Color = color, Duration = duration, Annotation = true });
    public void Path(Vector3[] points, bool closed, string? text, Vector4 color, double duration) => Add(new() { Kind = Kind.Path, Points = points, Closed = closed, Text = text, Color = color, Duration = duration, Annotation = true });
    public void Box(Bounds3 box, string? text, Vector4 color, double duration) => Add(new() { Kind = Kind.Box, Points = [box.Min, box.Max], Text = text, Color = color, Duration = duration, Annotation = true });
    public void Globe(Vector3 center, float radius, string? text, Vector4 color, double duration) => Add(new() { Kind = Kind.Sphere, Points = [center], Radius = radius, Text = text, Color = color, Duration = duration, Annotation = true });
    public void Measured(Vector3[] points, string?[] labels, string? total, bool closed, Vector4 color, double duration) => Add(new() { Kind = Kind.Measure, Points = points, Texts = labels, Text = total, Closed = closed, Color = color, Duration = duration, Annotation = true });

    public int ClearAnnotations() => _visuals.RemoveAll(v => v.Annotation);
    public int ClearAll() { int n = _visuals.Count; _visuals.Clear(); return n; }
    public int Count => _visuals.Count;

    // --- Drawing -----------------------------------------------------------------------------------

    private ImDrawListPtr _draw;
    private CameraFrame _frame;
    private Vector2 _viewport;
    private double _now;

    // The world visuals, under the gizmo.
    public void DrawWorld(ImDrawListPtr draw, CameraFrame frame, Vector2 viewport)
    {
        _draw = draw; _frame = frame; _viewport = viewport; _now = Now;
        if (Locked && _now - _lastCommand > IdleRelease) Release("idle");
        _visuals.RemoveAll(v => v.Duration > 0 && _now - v.Born - v.Delay > v.Duration + FadeOut);
        _labels.Clear();
        DrawNotes();
        // Newest first: when tags would overlap, the latest ones are read.
        for (int index = _visuals.Count - 1; index >= 0; index--)
        {
            var v = _visuals[index];
            double t = _now - v.Born - v.Delay;
            if (t < 0) continue;
            float alpha = v.Duration <= 0 || t <= v.Duration ? 1 : (float)Math.Max(0, 1 - (t - v.Duration) / FadeOut);
            if (alpha <= 0) continue;
            switch (v.Kind)
            {
                case Kind.Ray: DrawRay(v, t, alpha); break;
                case Kind.Measure: DrawMeasure(v, t, alpha); break;
                case Kind.Probe: DrawProbe(v, t, alpha); break;
                case Kind.Sphere: DrawSphere(v, t, alpha); break;
                case Kind.Region: DrawRegion(v, t, alpha); break;
                case Kind.Brackets: DrawBrackets(v.Points[0], v.Points[1], v.Color, alpha * Ease(t / .18) * (t < .5 ? 1 + .6f * (float)(1 - t / .5) : 1)); break;
                case Kind.Flash: DrawFlash(v, t, alpha); break;
                case Kind.Pin: DrawPin(v, t, alpha); break;
                case Kind.Tag: if (Project(v.Points[0], out var at)) { Dot2(at, 3, v.Color, alpha); Label(at, v.Text!, v.Color, alpha * Ease(t / .2)); } break;
                case Kind.Arrow: DrawArrow(v, t, alpha); break;
                case Kind.Path: DrawPath(v, t, alpha); break;
                case Kind.Box: DrawBox(v.Points[0], v.Points[1], v.Color, alpha * Ease(t / .25), 1.6f, true); if (v.Text is { } boxText && Project(new((v.Points[0].X + v.Points[1].X) / 2, (v.Points[0].Y + v.Points[1].Y) / 2, v.Points[1].Z), out var top)) Label(top, boxText, v.Color, alpha); break;
                case Kind.Reticle: DrawReticle(v, t, alpha); break;
                case Kind.Footprint: DrawFootprint(v, t, alpha); break;
            }
        }
    }

    // Screen effects and the pill, over everything.
    public void DrawScreen(ImDrawListPtr draw, Vector2 viewport, Vector2 pointer, bool pointerOver)
    {
        _draw = draw; _viewport = viewport; _now = Now;
        DrawSweep(); DrawShutter(); DrawEdges(); DrawPill(pointer, pointerOver);
    }

    // --- Projection -------------------------------------------------------------------------------

    private Vector4 Clip(Vector3 game) => Vector4.Transform(new Vector4(Coordinates.GameToView(game) - _frame.Origin, 1), _frame.ViewProjection);
    private Vector2 Screen(Vector4 c) => new((c.X / c.W * .5f + .5f) * _viewport.X, (.5f - c.Y / c.W * .5f) * _viewport.Y);

    private bool Project(Vector3 game, out Vector2 at)
    {
        var c = Clip(game);
        at = c.W > Near ? Screen(c) : default;
        return c.W > Near && MathF.Abs(at.X) < 1e5f && MathF.Abs(at.Y) < 1e5f;
    }

    // A segment cut at the near plane (a ray starting behind the camera is still drawn).
    private bool Segment(Vector3 a, Vector3 b, out Vector2 sa, out Vector2 sb)
    {
        var ca = Clip(a); var cb = Clip(b);
        sa = sb = default;
        if (ca.W <= Near && cb.W <= Near) return false;
        if (ca.W <= Near) ca = Vector4.Lerp(ca, cb, (Near - ca.W) / (cb.W - ca.W));
        else if (cb.W <= Near) cb = Vector4.Lerp(cb, ca, (Near - cb.W) / (ca.W - cb.W));
        sa = Screen(ca); sb = Screen(cb);
        return MathF.Abs(sa.X) < 1e5f && MathF.Abs(sa.Y) < 1e5f && MathF.Abs(sb.X) < 1e5f && MathF.Abs(sb.Y) < 1e5f;
    }

    // --- Primitives -------------------------------------------------------------------------------

    private static uint U(Vector4 c, float alpha) => ImGui.ColorConvertFloat4ToU32(c with { W = Math.Clamp(c.W * alpha, 0, 1) });
    private static float Ease(double t) { float x = (float)Math.Clamp(t, 0, 1); return 1 - (1 - x) * (1 - x) * (1 - x); }
    private static Vector4 Bright(Vector4 c) => Vector4.Lerp(c, White, .35f) with { W = c.W };

    private void Line2(Vector2 a, Vector2 b, Vector4 color, float alpha, float width, bool glow)
    {
        if (glow) { _draw.AddLine(a, b, U(color, alpha * .08f), width * 6.5f); _draw.AddLine(a, b, U(color, alpha * .2f), width * 3f); }
        _draw.AddLine(a, b, U(Bright(color), alpha), width);
    }

    private void Line(Vector3 a, Vector3 b, Vector4 color, float alpha, float width = 1.6f, bool glow = true)
    {
        if (alpha > .003f && Segment(a, b, out var sa, out var sb)) Line2(sa, sb, color, alpha, width, glow);
    }

    // A line fading from `from` alpha to `to` alpha (trails, misses).
    private void Fading(Vector3 a, Vector3 b, Vector4 color, float from, float to, float width, int steps = 10)
    {
        for (int i = 0; i < steps; i++)
            Line(Vector3.Lerp(a, b, i / (float)steps), Vector3.Lerp(a, b, (i + 1) / (float)steps), color, from + (to - from) * (i + .5f) / steps, width, i == steps - 1);
    }

    private void Dashed(Vector3 a, Vector3 b, Vector4 color, float alpha, float dash = .2f, float offset = 0, float width = 1.3f)
    {
        float length = Vector3.Distance(a, b);
        if (length < 1e-4f) return;
        dash = MathF.Max(dash, length / 160);
        float period = dash * 2, start = -(offset % period);
        for (float s = start; s < length; s += period)
        {
            float s0 = MathF.Max(0, s), s1 = MathF.Min(length, s + dash);
            if (s1 > s0) Line(Vector3.Lerp(a, b, s0 / length), Vector3.Lerp(a, b, s1 / length), color, alpha, width, false);
        }
    }

    private void Dot2(Vector2 at, float radius, Vector4 color, float alpha)
    {
        _draw.AddCircleFilled(at, radius * 2.6f, U(color, alpha * .14f), 20);
        _draw.AddCircleFilled(at, radius, U(color, alpha), 16);
        _draw.AddCircleFilled(at, radius * .45f, U(White, alpha * .9f), 10);
    }

    private void Dot(Vector3 p, float radius, Vector4 color, float alpha) { if (Project(p, out var at)) Dot2(at, radius, color, alpha); }

    // A circle lying in the plane of `normal` (impacts, ripples, spheres).
    private void Ring(Vector3 center, Vector3 normal, float radius, Vector4 color, float alpha, int segments = 40, float width = 1.5f, bool glow = true)
    {
        if (radius <= 0 || alpha <= .003f) return;
        normal = Vector3.Normalize(normal);
        var u = Vector3.Normalize(Vector3.Cross(normal, MathF.Abs(normal.Z) < .9f ? Vector3.UnitZ : Vector3.UnitX));
        var w = Vector3.Cross(normal, u);
        var previous = center + u * radius;
        for (int i = 1; i <= segments; i++)
        {
            float a = i * MathF.Tau / segments;
            var next = center + (u * MathF.Cos(a) + w * MathF.Sin(a)) * radius;
            Line(previous, next, color, alpha, width, glow);
            previous = next;
        }
    }

    // Tags drawn this frame: one that would cover another is left out.
    private readonly List<(Vector2 Min, Vector2 Max)> _labels = [];

    // A tag over a screen point (kept inside the view).
    private void Label(Vector2 at, string text, Vector4 accent, float alpha, float lift = 12)
    {
        if (alpha <= .01f) return;
        var size = ImGui.CalcTextSize(text); var pad = new Vector2(7, 3);
        var min = new Vector2(at.X - size.X / 2 - pad.X, at.Y - lift - size.Y - pad.Y * 2);
        var max = min + size + pad * 2;
        if (max.X < -40 || min.X > _viewport.X + 40 || max.Y < -40 || min.Y > _viewport.Y + 40) return;
        var shift = new Vector2(Math.Clamp(min.X, 4, Math.Max(4, _viewport.X - (max.X - min.X) - 4)) - min.X, Math.Clamp(min.Y, 4, Math.Max(4, _viewport.Y - (max.Y - min.Y) - 4)) - min.Y);
        min += shift; max += shift;
        foreach (var (a, b) in _labels) if (min.X < b.X + 2 && max.X > a.X - 2 && min.Y < b.Y + 1 && max.Y > a.Y - 1) return;
        _labels.Add((min, max));
        _draw.AddRectFilled(min, max, U(Ink, .86f * alpha), 5);
        _draw.AddRect(min, max, U(accent, .8f * alpha), 5, ImDrawFlags.None, 1.2f);
        _draw.AddText(min + pad, U(White, alpha), text);
    }

    private static readonly (int A, int B)[] Edges = [(0, 1), (1, 3), (3, 2), (2, 0), (4, 5), (5, 7), (7, 6), (6, 4), (0, 4), (1, 5), (2, 6), (3, 7)];
    private static Vector3 Corner(Vector3 min, Vector3 max, int i) => new((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);

    private void DrawBox(Vector3 min, Vector3 max, Vector4 color, float alpha, float width, bool glow)
    {
        foreach (var (a, b) in Edges) Line(Corner(min, max, a), Corner(min, max, b), color, alpha, width, glow);
    }

    private void DrawBrackets(Vector3 min, Vector3 max, Vector4 color, float alpha)
    {
        if (alpha <= .003f) return;
        var size = max - min;
        float arm = MathF.Max(.05f, MathF.Min(1.2f, MathF.Min(size.X, MathF.Min(size.Y, size.Z)) * .3f + .05f));
        for (int i = 0; i < 8; i++)
        {
            var c = Corner(min, max, i);
            var sx = (i & 1) == 0 ? 1 : -1; var sy = (i & 2) == 0 ? 1 : -1; var sz = (i & 4) == 0 ? 1 : -1;
            Line(c, c + new Vector3(sx * MathF.Min(arm, size.X), 0, 0), color, alpha, 2f, true);
            Line(c, c + new Vector3(0, sy * MathF.Min(arm, size.Y), 0), color, alpha, 2f, true);
            Line(c, c + new Vector3(0, 0, sz * MathF.Min(arm, size.Z)), color, alpha, 2f, true);
        }
    }

    // --- Visuals -----------------------------------------------------------------------------------

    private void DrawRay(Visual v, double t, float alpha)
    {
        Vector3 o = v.Points[0], e = v.Points[1];
        float length = Vector3.Distance(o, e);
        double travel = Math.Clamp(length / 45, .2, .65);
        var color = v.Hit ? v.Color : Violet;
        Dot(o, 2.6f, color, alpha * .9f);
        if (t < travel)
        {
            // In flight: a faint wake and a bright bolt at the head.
            float k = Ease(t / travel);
            var head = Vector3.Lerp(o, e, k);
            var tail = Vector3.Lerp(o, e, MathF.Max(0, k - MathF.Min(.3f, 4 / MathF.Max(length, .01f))));
            Line(o, tail, color, alpha * .3f, 1.1f, false);
            Fading(tail, head, color, alpha * .2f, alpha, 2.4f, 6);
            Dot(head, 3.6f, color, alpha);
            return;
        }
        double since = t - travel;
        if (!v.Hit)
        {
            // A miss fades away into the distance.
            Fading(o, e, color, alpha * .55f, 0, 1.2f);
            if (v.Text is { } miss && Project(Vector3.Lerp(o, e, MathF.Min(1, 6 / MathF.Max(length, .01f))), out var where)) Label(where, miss, color, alpha * Ease(since / .25));
            return;
        }
        Line(o, e, color, alpha * .55f, 1.3f, true);
        // A pulse keeps travelling along the ray while it is shown.
        double period = 1.7, phase = since % period / period;
        if (since < v.Duration) Dot(Vector3.Lerp(o, e, (float)phase), 2.2f, color, alpha * .7f * (float)Math.Sin(phase * Math.PI));
        // Impact: a flash and two ripples on the surface (screen circles when its normal is unknown).
        bool surface = v.Normal.LengthSquared() > .5f;
        for (int i = 0; i < 2; i++)
        {
            double r = since - i * .16;
            if (r is <= 0 or >= .75) continue;
            float p = (float)(r / .75);
            if (surface) Ring(e + v.Normal * .01f, v.Normal, .06f + p * .85f, Amber, alpha * (1 - p), 36, 1.6f);
            else if (Project(e, out var c)) _draw.AddCircle(c, 5 + p * 26, U(Amber, alpha * (1 - p)), 32, 1.8f);
        }
        if (since < .3 && Project(e, out var flash)) _draw.AddCircleFilled(flash, 4 + 10 * (float)(1 - since / .3), U(White, alpha * (float)(1 - since / .3) * .8f), 24);
        if (surface)
        {
            Ring(e + v.Normal * .01f, v.Normal, .12f, Amber, alpha * .9f, 24, 1.4f, false);
            Line(e, e + v.Normal * .5f, Amber, alpha * Ease(since / .3), 1.4f, false);
        }
        Dot(e, 3.2f, Amber, alpha);
        if (v.Text is { } text && Project(e, out var at)) Label(at, text, Amber, alpha * Ease((since - .1) / .25));
    }

    private static readonly float[] Steps = [.1f, .25f, .5f, 1, 2, 5, 10, 25, 50, 100, 250, 500];
    private static float NiceStep(float length)
    {
        foreach (var step in Steps) if (length / step <= 24) return step;
        return 1000;
    }

    private void DrawMeasure(Visual v, double t, float alpha)
    {
        var p = v.Points;
        if (p.Length < 2) return;
        int segments = v.Closed ? p.Length : p.Length - 1;
        var lengths = new float[segments];
        float total = 0;
        for (int i = 0; i < segments; i++) { lengths[i] = Vector3.Distance(p[i], p[(i + 1) % p.Length]); total += lengths[i]; }
        double grow = Math.Clamp(.3 + total / 70, .35, .95);
        float drawn = Ease(t / grow) * total, walked = 0;
        var color = v.Color;
        Dot(p[0], 3.2f, color, alpha);
        for (int i = 0; i < segments && drawn > walked; i++)
        {
            Vector3 a = p[i], b = p[(i + 1) % p.Length];
            float len = lengths[i], part = MathF.Min(1, (drawn - walked) / MathF.Max(len, 1e-4f));
            var end = Vector3.Lerp(a, b, part);
            Line(a, end, color, alpha, 1.9f, true);
            Ticks(a, b, len, part, color, alpha);
            if (part >= 1) Dot(b, 3.2f * (1 + .5f * MathF.Max(0, 1 - (float)(t - grow) * 4)), color, alpha);
            else Dot(end, 3.4f, White, alpha);
            walked += len;
        }
        if (t < grow) return;
        float labels = alpha * Ease((t - grow) / .3);
        if (v.Legs && p.Length == 2)
        {
            var corner = new Vector3(p[1].X, p[1].Y, p[0].Z);
            Dashed(p[0], corner, color, alpha * .55f, .15f, (float)t * .4f);
            Dashed(corner, p[1], color, alpha * .55f, .15f, (float)t * .4f);
            if (v.Texts is { Length: > 2 })
            {
                if (v.Texts[1] is { } across && Project(Vector3.Lerp(p[0], corner, .5f), out var h)) Label(h, across, color, labels * .9f, 6);
                if (v.Texts[2] is { } up && Project(Vector3.Lerp(corner, p[1], .5f), out var u)) Label(u, up, color, labels * .9f, 6);
            }
        }
        if (v.Texts is { Length: > 0 })
            for (int i = 0; i < segments; i++)
                if ((v.Legs ? i == 0 : i < v.Texts.Length) && v.Texts[i] is { } text && Project(Vector3.Lerp(p[i], p[(i + 1) % p.Length], .5f), out var mid)) Label(mid, text, color, labels);
        if (v.Text is { } sum)
        {
            var anchor = v.Closed ? p.Aggregate(Vector3.Zero, (s, q) => s + q) / p.Length : p[^1];
            if (Project(anchor, out var at)) Label(at, sum, White, labels, v.Closed ? 0 : 26);
        }
    }

    // Graduations along a measure (in metres), only where the segment is drawn and readable.
    private void Ticks(Vector3 a, Vector3 b, float length, float part, Vector4 color, float alpha)
    {
        if (length < .2f || !Segment(a, b, out var sa, out var sb)) return;
        float step = NiceStep(length);
        int count = (int)(length * part / step);
        if (count < 1 || Vector2.Distance(sa, sb) / (length / step) < 7) return;
        for (int i = 1; i <= count; i++)
        {
            float s = i * step;
            if (s >= length - step * .2f) break;
            if (!Segment(Vector3.Lerp(a, b, s / length), Vector3.Lerp(a, b, s / length + .001f), out var at, out var ahead)) continue;
            var direction = ahead - at;
            if (direction.LengthSquared() < 1e-6f) direction = sb - sa;
            if (direction.LengthSquared() < 1e-6f) continue;
            var across = Vector2.Normalize(new Vector2(-direction.Y, direction.X));
            float size = i % 5 == 0 ? 7 : 3.5f;
            _draw.AddLine(at - across * size, at + across * size, U(Bright(color), alpha * .85f), 1.2f);
        }
    }

    private void DrawProbe(Visual v, double t, float alpha)
    {
        Vector3 top = v.Points[0], bottom = v.Points[1];
        double fall = Math.Clamp((top.Z - bottom.Z) / 22, .14, .42);
        var color = v.Hit ? v.Color : Violet;
        if (t < fall)
        {
            // Falling (gravity: faster near the ground).
            float k = (float)(t / fall); k *= k;
            var head = Vector3.Lerp(top, bottom, k);
            Fading(top, head, color, alpha * .1f, alpha * .7f, 1.4f, 5);
            Dot(head, 3, color, alpha);
            return;
        }
        double since = t - fall;
        Line(top, bottom, color, alpha * .22f, 1f, false);
        if (!v.Hit)
        {
            if (Project(bottom, out var end)) { _draw.AddLine(end - new Vector2(4), end + new Vector2(4), U(color, alpha), 1.5f); _draw.AddLine(end + new Vector2(-4, 4), end + new Vector2(4, -4), U(color, alpha), 1.5f); }
            if (v.Text is { } none && Project(bottom, out var at)) Label(at, none, color, alpha * Ease(since / .25));
            return;
        }
        for (int i = 0; i < 2; i++)
        {
            double r = since - i * .18;
            if (r is <= 0 or >= .7) continue;
            float p = (float)(r / .7);
            Ring(bottom + new Vector3(0, 0, .02f), Vector3.UnitZ, .05f + p * .7f, color, alpha * (1 - p), 32, 1.4f, false);
        }
        Dot(bottom, 2.8f, color, alpha);
        if (v.Text is { } text && Project(bottom, out var label)) Label(label, text, color, alpha * Ease((since - .1) / .25));
    }

    private void DrawSphere(Visual v, double t, float alpha)
    {
        var c = v.Points[0];
        float r = v.Radius * Ease(t / .55);
        Ring(c, Vector3.UnitZ, r, v.Color, alpha * .9f, 72, 1.6f);
        Ring(c, Vector3.UnitX, r, v.Color, alpha * .3f, 48, 1.1f, false);
        Ring(c, Vector3.UnitY, r, v.Color, alpha * .3f, 48, 1.1f, false);
        // Sonar: rings leaving the sphere for the first seconds.
        if (!v.Annotation && t < 2.2)
        {
            double phase = t % 1.1 / 1.1;
            Ring(c, Vector3.UnitZ, v.Radius * (float)(.2 + phase * .9), v.Color, alpha * (float)(1 - phase) * .5f, 64, 1.2f, false);
        }
        Dot(c, 2.4f, v.Color, alpha);
        if (v.Text is { } text && Project(c + new Vector3(0, 0, v.Radius), out var top)) Label(top, text, v.Color, alpha);
    }

    private void DrawRegion(Visual v, double t, float alpha)
    {
        Vector3 min = v.Points[0], max = v.Points[1];
        float k = Ease(t / .5);
        foreach (var (a, b) in Edges)
        {
            var from = Corner(min, max, a);
            Line(from, Vector3.Lerp(from, Corner(min, max, b), k), v.Color, alpha * .85f, 1.5f, true);
        }
        // A scan plane sweeps the box once.
        if (t < 1.2)
        {
            float z = min.Z + (max.Z - min.Z) * Ease(t / 1.2);
            var s = (float)(1 - t / 1.2);
            Line(new(min.X, min.Y, z), new(max.X, min.Y, z), v.Color, alpha * s, 1.2f); Line(new(max.X, min.Y, z), new(max.X, max.Y, z), v.Color, alpha * s, 1.2f);
            Line(new(max.X, max.Y, z), new(min.X, max.Y, z), v.Color, alpha * s, 1.2f); Line(new(min.X, max.Y, z), new(min.X, min.Y, z), v.Color, alpha * s, 1.2f);
        }
    }

    private void DrawFlash(Visual v, double t, float alpha)
    {
        Vector3 min = v.Points[0], max = v.Points[1];
        const double scan = .5;
        if (t < scan)
        {
            // Placed: a scan rises through the box and builds it; removed: it sinks away.
            float k = Ease(t / scan);
            float z = v.Removal ? max.Z - (max.Z - min.Z) * k : min.Z + (max.Z - min.Z) * k;
            DrawBox(min, max with { Z = z }, v.Color, alpha * .9f, 1.5f, true);
            Line(new(min.X, min.Y, z), new(max.X, max.Y, z), v.Color, alpha * .35f, 1f, false);
            Line(new(max.X, min.Y, z), new(min.X, max.Y, z), v.Color, alpha * .35f, 1f, false);
            return;
        }
        if (v.Removal) { DrawBrackets(min, max with { Z = min.Z + .02f }, v.Color, alpha * .6f * (float)Math.Max(0, 1 - (t - scan) / 1.2)); return; }
        float fade = (float)Math.Max(.25, 1 - (t - scan) / 1.4);
        DrawBox(min, max, v.Color, alpha * fade * .6f, 1.2f, false);
        DrawBrackets(min, max, v.Color, alpha * fade);
    }

    private void DrawPin(Visual v, double t, float alpha)
    {
        var p = v.Points[0];
        float pulse = .5f + .5f * MathF.Sin((float)t * 4);
        var top = p + new Vector3(0, 0, .9f * Ease(t / .3));
        Line(p, top, v.Color, alpha * .8f, 1.4f, true);
        Ring(p + new Vector3(0, 0, .02f), Vector3.UnitZ, .12f + .1f * pulse, v.Color, alpha * (.4f + .4f * pulse), 28, 1.3f, false);
        Dot(p, 3.4f, v.Color, alpha);
        if (v.Text is { } text && Project(top, out var at)) Label(at, text, v.Color, alpha * Ease((t - .15) / .25), 4);
    }

    private void DrawArrow(Visual v, double t, float alpha)
    {
        Vector3 a = v.Points[0], b = Vector3.Lerp(v.Points[0], v.Points[1], Ease(t / .45));
        Line(a, b, v.Color, alpha, 2.2f, true);
        if (Segment(a, b, out var sa, out var sb) && Vector2.Distance(sa, sb) > 4)
        {
            var d = Vector2.Normalize(sb - sa);
            var left = new Vector2(d.X * MathF.Cos(2.6f) - d.Y * MathF.Sin(2.6f), d.X * MathF.Sin(2.6f) + d.Y * MathF.Cos(2.6f));
            var right = new Vector2(d.X * MathF.Cos(-2.6f) - d.Y * MathF.Sin(-2.6f), d.X * MathF.Sin(-2.6f) + d.Y * MathF.Cos(-2.6f));
            _draw.AddTriangleFilled(sb, sb + left * 13, sb + right * 13, U(Bright(v.Color), alpha));
        }
        if (v.Text is { } text && Project(v.Points[0], out var at)) Label(at, text, v.Color, alpha * Ease((t - .3) / .25), 6);
    }

    private void DrawPath(Visual v, double t, float alpha)
    {
        var p = v.Points;
        int segments = v.Closed ? p.Length : p.Length - 1;
        float k = Ease(t / .6) * segments;
        for (int i = 0; i < segments && k > i; i++)
        {
            Vector3 a = p[i], b = Vector3.Lerp(p[i], p[(i + 1) % p.Length], MathF.Min(1, k - i));
            Line(a, b, v.Color, alpha * .25f, 3f, false);
            Dashed(a, b, v.Color, alpha, .35f, -(float)t * .8f, 1.8f);   // marching ants
        }
        foreach (var q in p) Dot(q, 2.6f, v.Color, alpha);
        if (v.Text is { } text && Project(p[0], out var at)) Label(at, text, v.Color, alpha * Ease((t - .4) / .25));
    }

    private void DrawReticle(Visual v, double t, float alpha)
    {
        if (!Project(v.Points[0], out var c)) return;
        float size = 14 + 26 * (1 - Ease(t / .35)), turn = (float)t * .8f + (1 - Ease(t / .35)) * 1.6f;
        for (int i = 0; i < 4; i++)
        {
            float a = turn + i * MathF.PI / 2;
            var corner = c + new Vector2(MathF.Cos(a), MathF.Sin(a)) * size;
            var along = new Vector2(MathF.Cos(a + 2.36f), MathF.Sin(a + 2.36f)) * 7;
            var other = new Vector2(MathF.Cos(a - 2.36f), MathF.Sin(a - 2.36f)) * 7;
            _draw.AddLine(corner, corner + along, U(Amber, alpha), 2f);
            _draw.AddLine(corner, corner + other, U(Amber, alpha), 2f);
        }
        Dot2(c, 2.6f, Amber, alpha);
        if (v.Text is { } text) Label(c, text, Amber, alpha * Ease((t - .2) / .25), size + 6);
    }

    private void DrawFootprint(Visual v, double t, float alpha)
    {
        var center = v.Points[0];
        var ring = v.Points.AsSpan(1);
        if (ring.Length < 3) return;
        float k = Ease(t / .5);
        if (Project(center, out var c))
            for (int i = 0; i < ring.Length; i++)
            {
                if (!Project(Vector3.Lerp(center, ring[i], k), out var a) || !Project(Vector3.Lerp(center, ring[(i + 1) % ring.Length], k), out var b)) continue;
                _draw.AddTriangleFilled(c, a, b, U(v.Color, alpha * .07f));
            }
        for (int i = 0; i < ring.Length; i++) Line(Vector3.Lerp(center, ring[i], k), Vector3.Lerp(center, ring[(i + 1) % ring.Length], k), v.Color, alpha * .75f, 1.5f, true);
        if (v.Text is { } text && Project(center, out var at)) Label(at, text, White, alpha * Ease((t - .4) / .3), 30);
    }

    // --- Screen effects ----------------------------------------------------------------------------

    // The camera jumped: a scan line sweeps the view.
    private void DrawSweep()
    {
        double t = _now - _sweepAt;
        if (t is < 0 or > .5) return;
        float y = Ease(t / .5) * _viewport.Y, fade = (float)(1 - t / .5);
        _draw.AddRectFilledMultiColor(new(0, MathF.Max(0, y - 90)), new(_viewport.X, y), U(Cyan, 0), U(Cyan, 0), U(Cyan, .16f * fade), U(Cyan, .16f * fade));
        _draw.AddLine(new(0, y), new(_viewport.X, y), U(Bright(Cyan), .7f * fade), 1.6f);
    }

    // A screenshot: a viewfinder flash.
    private void DrawShutter()
    {
        double t = _now - _shutterAt;
        if (t is < 0 or > .5) return;
        float fade = (float)(1 - t / .5), inset = 18 + 10 * Ease(t / .5), arm = 34;
        _draw.AddRect(Vector2.Zero, _viewport, U(White, .55f * fade * fade), 0, ImDrawFlags.None, 6);
        Vector2[] corners = [new(inset, inset), new(_viewport.X - inset, inset), new(_viewport.X - inset, _viewport.Y - inset), new(inset, _viewport.Y - inset)];
        Vector2[] xs = [new(1, 0), new(-1, 0), new(-1, 0), new(1, 0)], ys = [new(0, 1), new(0, 1), new(0, -1), new(0, -1)];
        for (int i = 0; i < 4; i++)
        {
            _draw.AddLine(corners[i], corners[i] + xs[i] * arm, U(White, .9f * fade), 2.5f);
            _draw.AddLine(corners[i], corners[i] + ys[i] * arm, U(White, .9f * fade), 2.5f);
        }
    }

    private Vector4 Hue(double phase) => Vector4.Lerp(Cyan, Violet, .5f + .5f * (float)Math.Sin(phase));

    // The view is the agent's: a glow breathing on the edges, its colours drifting around the frame;
    // while a command runs, two sparks race along the border.
    private void DrawEdges()
    {
        float presence = Locked ? Ease((_now - _lockedAt) / .6) : (float)Math.Max(0, 1 - (_now - _releasedAt) / .6);
        bool busy = _running > 0 || _now - _activityAt < .7;
        float w = _viewport.X, h = _viewport.Y;
        if (presence > 0)
        {
            float breath = .5f + .5f * MathF.Sin((float)(_now * MathF.Tau / 3.8));
            float band = (70 + 50 * breath) * (.6f + .4f * presence), a = (.3f + .3f * breath) * presence;
            double drift = _now * .55;
            Vector4 tl = Hue(drift), tr = Hue(drift + 1.6), br = Hue(drift + 3.1), bl = Hue(drift + 4.7);
            _draw.AddRectFilledMultiColor(new(0, 0), new(w, band), U(tl, a), U(tr, a), U(tr, 0), U(tl, 0));
            _draw.AddRectFilledMultiColor(new(0, h - band), new(w, h), U(bl, 0), U(br, 0), U(br, a), U(bl, a));
            _draw.AddRectFilledMultiColor(new(0, 0), new(band, h), U(tl, a), U(tl, 0), U(bl, 0), U(bl, a));
            _draw.AddRectFilledMultiColor(new(w - band, 0), new(w, h), U(tr, 0), U(tr, a), U(br, a), U(br, 0));
            // A crisp rim, brighter on the breath.
            float rim = (.55f + .35f * breath) * presence, t = 2.5f;
            _draw.AddRectFilledMultiColor(new(0, 0), new(w, t), U(tl, rim), U(tr, rim), U(tr, rim), U(tl, rim));
            _draw.AddRectFilledMultiColor(new(0, h - t), new(w, h), U(bl, rim), U(br, rim), U(br, rim), U(bl, rim));
            _draw.AddRectFilledMultiColor(new(0, 0), new(t, h), U(tl, rim), U(tl, rim), U(bl, rim), U(bl, rim));
            _draw.AddRectFilledMultiColor(new(w - t, 0), new(w, h), U(tr, rim), U(tr, rim), U(br, rim), U(br, rim));
        }
        if (!busy) return;
        float perimeter = 2 * (w + h), strength = Locked ? 1 : .7f;
        Vector2 Along(float s)
        {
            s = ((s % perimeter) + perimeter) % perimeter;
            if (s < w) return new(s, 1.5f);
            if (s < w + h) return new(w - 1.5f, s - w);
            if (s < 2 * w + h) return new(w - (s - w - h), h - 1.5f);
            return new(1.5f, h - (s - 2 * w - h));
        }
        float head = (float)(_now * .6 % 1) * perimeter;
        for (int spark = 0; spark < 2; spark++)
            for (int i = 0; i < 26; i++)
            {
                float f = 1 - i / 26f;
                var at = Along(head + spark * perimeter / 2 - i * 11);
                var color = Vector4.Lerp(Violet, White, f * f);
                _draw.AddCircleFilled(at, 1 + 3.2f * f, U(color, strength * f * .9f), 12);
                if (i == 0) _draw.AddCircleFilled(at, 11, U(Cyan, strength * .22f), 20);
            }
    }

    // Who acts and what it does; while the view is locked, the button that gives it back.
    private void DrawPill(Vector2 pointer, bool pointerOver)
    {
        _buttonShown = false;
        double sinceActivity = _now - _activityAt;
        float alpha = Locked ? 1 : (float)Math.Clamp((PillLinger - sinceActivity) / .6, 0, 1);
        if (alpha <= 0) return;
        bool busy = _running > 0 || sinceActivity < .9;
        string title = Locked ? Loc.F("{0} has control", Agent) : Agent;
        // (The overlay font has no ellipsis glyph.)
        string detail = busy || !Locked ? (_activity ?? Loc.T("Working")) + (busy ? "..." : "") : Reason ?? Loc.T("Working in the view");
        if (Locked && busy && Reason is { } reason) detail = $"{detail}  ·  {reason}";
        string button = Loc.T("Take back control");
        var titleSize = ImGui.CalcTextSize(title); var detailSize = ImGui.CalcTextSize(detail); var buttonSize = ImGui.CalcTextSize(button);
        float height = titleSize.Y + 16, orb = 36, gap = 12;
        float width = orb + titleSize.X + gap + detailSize.X + 18 + (Locked ? gap + buttonSize.X + 22 : 0);
        width = MathF.Min(width, _viewport.X - 24);
        // At the bottom of the view, above the navigation hint (the toolbar floats at the top).
        var min = new Vector2(MathF.Round((_viewport.X - width) / 2), _viewport.Y - 46 - height + 8 * (1 - alpha));
        var max = min + new Vector2(width, height);
        _draw.AddRectFilled(min - new Vector2(4), max + new Vector2(4), U(Cyan, .06f * alpha), height / 2 + 4);
        _draw.AddRectFilled(min, max, U(Ink, .9f * alpha), height / 2);
        _draw.AddRect(min, max, U(Hue(_now * .55), .75f * alpha), height / 2, ImDrawFlags.None, 1.3f);
        // Busy: a shimmer runs along the pill.
        if (busy)
        {
            float x = min.X + (float)(_now * .9 % 1) * (width + 80) - 40;
            float x0 = MathF.Max(min.X + height / 2, x - 40), x1 = MathF.Min(max.X - height / 2, x + 40);
            if (x1 > x0)
            {
                _draw.AddRectFilledMultiColor(new(x0, min.Y + 1), new(MathF.Max(x0, x), min.Y + 2.5f), U(White, 0), U(White, .7f * alpha), U(White, .7f * alpha), U(White, 0));
                _draw.AddRectFilledMultiColor(new(MathF.Min(x1, x), min.Y + 1), new(x1, min.Y + 2.5f), U(White, .7f * alpha), U(White, 0), U(White, 0), U(White, .7f * alpha));
            }
        }
        // The orb: breathing, with a spinner while a command runs.
        var center = new Vector2(min.X + 18, (min.Y + max.Y) / 2);
        float breath = .5f + .5f * MathF.Sin((float)(_now * MathF.Tau / 1.9));
        _draw.AddCircleFilled(center, 9 + 2 * breath, U(Hue(_now * .8), .22f * alpha), 24);
        _draw.AddCircleFilled(center, 5.2f, U(Hue(_now * .8 + 1), alpha), 20);
        _draw.AddCircleFilled(center - new Vector2(1.4f), 2, U(White, .85f * alpha), 10);
        if (busy)
        {
            float spin = (float)(_now * 5.5);
            _draw.PathArcTo(center, 9.5f, spin, spin + 3.8f, 24);
            _draw.PathStroke(U(White, .85f * alpha), ImDrawFlags.None, 1.8f);
        }
        var text = new Vector2(min.X + orb, min.Y + 8);
        _draw.PushClipRect(min, max, true);
        _draw.AddText(text, U(White, alpha), title);
        _draw.AddText(text + new Vector2(titleSize.X + gap, 0), U(Muted, alpha), detail);
        _draw.PopClipRect();
        if (!Locked) return;
        _buttonMax = new(max.X - 5, max.Y - 5);
        _buttonMin = new(_buttonMax.X - buttonSize.X - 20, min.Y + 5);
        bool hover = pointerOver && pointer.X >= _buttonMin.X && pointer.Y >= _buttonMin.Y && pointer.X <= _buttonMax.X && pointer.Y <= _buttonMax.Y;
        _draw.AddRectFilled(_buttonMin, _buttonMax, U(Cyan, (hover ? .95f : .16f) * alpha), (_buttonMax.Y - _buttonMin.Y) / 2);
        _draw.AddText(new(_buttonMin.X + 10, _buttonMin.Y + (_buttonMax.Y - _buttonMin.Y - buttonSize.Y) / 2), U(hover ? Ink : Bright(Cyan), alpha), button);
        _buttonShown = true;
    }
}
