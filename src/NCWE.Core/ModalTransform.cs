// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Numerics;

namespace NCWE.Core;

public enum ModalKind { Move, Rotate, Scale }

// A transform driven by the pointer alone, the way Blender does it: a key starts moving, turning
// or scaling the selection about its pivot and the pointer sets the amount, without a gizmo.
// X, Y and Z keep it to a game axis (east, north, up): once for the world's axis, again for the
// object's own, a third time for none; with Shift, to the plane without that axis. Digits type the
// amount (metres, degrees, a factor). Everything here is in view space (Coordinates: Y up).
public sealed class ModalTransform
{
    public ModalKind Kind { get; private set; }
    // 0 X (east), 1 Y (north), 2 Z (up); -1: free.
    public int Axis { get; private set; } = -1;
    // The two other axes (Shift + the key).
    public bool Plane { get; private set; }
    // The object's own axes rather than the world's. A scale is always along the object's axes.
    public bool Local { get; private set; }
    public string Typed { get; private set; } = "";
    // The pivot as the transform started.
    public Matrix4x4 Start { get; }
    // What the last update came to: the move in game metres, the turn in degrees (counter-clockwise
    // seen from the tip of its axis), the scale factor.
    public Vector3 GameDelta { get; private set; }
    public float Degrees { get; private set; }
    public float Factor { get; private set; } = 1;
    // Where the pivot is on screen, when it is in front of the camera.
    public Vector2? PivotOnScreen { get; private set; }
    public Vector2 Pointer => _pointer;

    private Vector2 _pointerStart, _pointer, _anglePointer;
    private float _angle;
    private Vector3 _lastMove;

    public ModalTransform(ModalKind kind, Matrix4x4 pivot, Vector2 pointer)
    {
        Kind = kind; Start = pivot; _pointerStart = _pointer = _anglePointer = pointer;
    }

    // Another kind from the same start (G, R or S pressed again): the amount starts over.
    public void SetKind(ModalKind kind)
    {
        if (kind == Kind) return;
        Kind = kind; Typed = ""; _pointerStart = _anglePointer = _pointer; _angle = 0; _lastMove = default;
        Axis = -1; Plane = Local = false;
    }

    // X, Y or Z: the world's axis, then the object's own, then free again.
    public void PressAxis(int axis, bool plane)
    {
        if (axis is < 0 or > 2) return;
        bool same = Axis == axis && Plane == plane;
        // A scale has no world axes: one press for the object's axis, another for none.
        if (Kind == ModalKind.Scale) { if (same) { Axis = -1; Plane = false; } else { Axis = axis; Plane = plane; } Local = Axis >= 0; }
        else if (!same) { Axis = axis; Plane = plane; Local = false; }
        else if (!Local) Local = true;
        else { Axis = -1; Plane = Local = false; }
        _lastMove = default;
    }

    // A digit, the decimal separator or the minus sign (which flips the sign wherever it is typed).
    public bool Type(char c)
    {
        if (c == ',') c = '.';
        if (c == '-') { Typed = Typed.StartsWith('-') ? Typed[1..] : "-" + Typed; return true; }
        if (c == '.' ? Typed.Contains('.') : c is < '0' or > '9') return false;
        if (Typed.TrimStart('-').Length >= 9) return false;
        Typed += c;
        return true;
    }

    public bool Backspace()
    {
        if (Typed.Length == 0) return false;
        Typed = Typed[..^1];
        return true;
    }

    public float? TypedValue => Typed.Length > 0 && float.TryParse(Typed is "-" or "." or "-." ? "0" : Typed, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && float.IsFinite(value) ? value : null;

    public static Vector3 GameAxis(int axis) => axis switch { 0 => Vector3.UnitX, 1 => -Vector3.UnitZ, _ => Vector3.UnitY };

    // The direction of a game axis (view space): the world's, or the pivot's own.
    public Vector3 Direction(int axis)
    {
        if (!Local && Kind != ModalKind.Scale) return GameAxis(axis);
        var own = Vector3.TransformNormal(GameAxis(axis), Start);
        return own.LengthSquared() > 1e-12f ? Vector3.Normalize(own) : GameAxis(axis);
    }

    // The pointer moved by delta pixels (precise: a tenth of it, Shift held); returns the pivot it
    // comes to. step: the snap (metres, degrees, a tenth for a scale) when snapping is on.
    public Matrix4x4 Update(CameraFrame frame, Vector2 viewport, Vector2 delta, bool precise = false, float? step = null)
    {
        _pointer += delta * (precise ? .1f : 1);
        var pivot = Start.Translation - frame.Origin;
        var viewProjection = frame.ViewProjection;
        var centre = Project(pivot, viewProjection, viewport);
        PivotOnScreen = centre;
        var anchor = centre ?? viewport / 2;
        // The turn follows the pointer around the pivot, added up so that it goes past half a turn.
        Vector2 before = _anglePointer - anchor, after = _pointer - anchor;
        if (before.LengthSquared() > 4 && after.LengthSquared() > 4) _angle += ScreenAngle(before, after);
        _anglePointer = _pointer;
        return Kind switch
        {
            ModalKind.Rotate => Rotated(frame, viewport, pivot, viewProjection, anchor, step),
            ModalKind.Scale => Scaled(anchor, step),
            _ => Moved(frame, viewport, pivot, step)
        };
    }

    private Matrix4x4 Moved(CameraFrame frame, Vector2 viewport, Vector3 pivot, float? step)
    {
        Vector3 move;
        if (TypedValue is { } typed)
        {
            // A typed distance goes along the axis; without one, along the first axis left free.
            int along = Axis < 0 ? 0 : !Plane ? Axis : Axis == 0 ? 1 : 0;
            move = Direction(along) * typed;
        }
        else
        {
            Ray3 from = ScenePicking.ViewRay(_pointerStart, viewport, frame.View, frame.Projection), to = ScenePicking.ViewRay(_pointer, viewport, frame.View, frame.Projection);
            Vector3? found;
            if (Axis >= 0 && !Plane)
            {
                var axis = Direction(Axis);
                found = Along(to, pivot, axis) is { } b && Along(from, pivot, axis) is { } a ? axis * Snapped(b - a, step) : null;
            }
            else
            {
                // Free: the plane facing the camera through the pivot. Constrained: the plane across the axis.
                var normal = Axis >= 0 ? Direction(Axis) : ScenePicking.ViewRay(viewport / 2, viewport, frame.View, frame.Projection).Direction;
                found = OnPlane(to, pivot, normal) is { } b && OnPlane(from, pivot, normal) is { } a ? b - a : null;
                if (found is { } free && step is { } s && s > 0 && !Local) found = new Vector3(MathF.Round(free.X / s) * s, MathF.Round(free.Y / s) * s, MathF.Round(free.Z / s) * s);
            }
            // Seen edge-on, the axis or the plane has no point under the pointer: the move stays where it was.
            move = found ?? _lastMove;
        }
        _lastMove = move;
        GameDelta = Coordinates.ViewToGame(move);
        var next = Start; next.Translation += move;
        return next;
    }

    private Matrix4x4 Rotated(CameraFrame frame, Vector2 viewport, Vector3 pivot, Matrix4x4 viewProjection, Vector2 anchor, float? step)
    {
        var sight = ScenePicking.ViewRay(viewport / 2, viewport, frame.View, frame.Projection);
        var forward = sight.Direction;
        // Free: about the line of sight, as seen. The axis points at the viewer so that the turn's
        // sign reads the same as for a game axis seen from its tip.
        var axis = Axis >= 0 ? Direction(Axis) : -forward;
        float radians;
        if (TypedValue is { } typed) radians = typed * MathF.PI / 180;
        else
        {
            radians = _angle * ScreenSense(axis, forward, pivot, MathF.Max(.05f, (pivot - sight.Origin).Length() * .05f), viewProjection, viewport, anchor);
            if (step is { } s && s > 0) { float size = s * MathF.PI / 180; radians = MathF.Round(radians / size) * size; }
        }
        Degrees = radians * 180 / MathF.PI;
        var at = Start.Translation;
        return Start * Matrix4x4.CreateTranslation(-at) * Matrix4x4.CreateFromAxisAngle(axis, radians) * Matrix4x4.CreateTranslation(at);
    }

    private Matrix4x4 Scaled(Vector2 anchor, float? step)
    {
        float factor;
        if (TypedValue is { } typed) factor = MathF.Abs(typed);
        else
        {
            // The pointer's distance to the pivot against the one it started at (never from too close: a small move would be a huge factor).
            float reference = MathF.Max((_pointerStart - anchor).Length(), 30);
            factor = 1 + ((_pointer - anchor).Length() - (_pointerStart - anchor).Length()) / reference;
            if (step is { } s && s > 0) factor = MathF.Round(factor / s) * s;
        }
        Factor = factor = Math.Clamp(float.IsFinite(factor) ? factor : 1, .01f, 1000);
        // The pivot's own axes, in the order of the mesh buffers (X east, Y up, Z south).
        var scale = Vector3.One;
        for (int axis = 0; axis < 3; axis++)
        {
            if (Axis >= 0 && (Axis == axis) == Plane) continue;
            scale = axis switch { 0 => scale with { X = factor }, 1 => scale with { Z = factor }, _ => scale with { Y = factor } };
        }
        return Matrix4x4.CreateScale(scale) * Start;
    }

    private static float Snapped(float value, float? step) => step is { } s && s > 0 ? MathF.Round(value / s) * s : value;

    // Signed angle on screen from one vector to another (pixels, Y down).
    private static float ScreenAngle(Vector2 from, Vector2 to) => MathF.Atan2(from.X * to.Y - from.Y * to.X, Vector2.Dot(from, to));

    // +1 when a positive turn about the axis goes the way a positive screen angle does, else -1:
    // measured by turning a point beside the pivot a little and looking where it goes on screen.
    private static float ScreenSense(Vector3 axis, Vector3 forward, Vector3 pivot, float reach, Matrix4x4 viewProjection, Vector2 viewport, Vector2 anchor)
    {
        var side = Vector3.Cross(axis, forward);
        if (side.LengthSquared() < 1e-6f) side = Vector3.Cross(axis, MathF.Abs(axis.Y) < .9f ? Vector3.UnitY : Vector3.UnitX);
        side = Vector3.Normalize(side) * reach;
        var turned = Vector3.Transform(side, Matrix4x4.CreateFromAxisAngle(axis, .05f));
        if (Project(pivot + side, viewProjection, viewport) is { } a && Project(pivot + turned, viewProjection, viewport) is { } b)
        {
            float angle = ScreenAngle(a - anchor, b - anchor);
            if (MathF.Abs(angle) > 1e-5f) return MathF.Sign(angle);
        }
        // The axis lies across the view: the side it leans to decides.
        return Vector3.Dot(axis, forward) > 0 ? 1 : -1;
    }

    public static Vector2? Project(Vector3 relative, Matrix4x4 viewProjection, Vector2 viewport)
    {
        var clip = Vector4.Transform(new Vector4(relative, 1), viewProjection);
        if (clip.W <= 1e-4f) return null;
        return new((clip.X / clip.W * .5f + .5f) * viewport.X, (.5f - clip.Y / clip.W * .5f) * viewport.Y);
    }

    // Where a ray meets the plane through a point (null: parallel to it, behind the camera or too far to mean anything).
    private static Vector3? OnPlane(Ray3 ray, Vector3 point, Vector3 normal)
    {
        float facing = Vector3.Dot(ray.Direction, normal);
        if (MathF.Abs(facing) < .02f) return null;
        float distance = Vector3.Dot(point - ray.Origin, normal) / facing;
        return distance is > 0 and < 20_000 ? ray.Origin + ray.Direction * distance : null;
    }

    // How far along the line through a point the ray passes closest (null: the ray runs along it).
    private static float? Along(Ray3 ray, Vector3 point, Vector3 axis)
    {
        float b = Vector3.Dot(axis, ray.Direction), spread = 1 - b * b;
        if (spread < 1e-3f) return null;
        var w = point - ray.Origin;
        float along = (b * Vector3.Dot(ray.Direction, w) - Vector3.Dot(axis, w)) / spread;
        return MathF.Abs(along) < 20_000 ? along : null;
    }
}
