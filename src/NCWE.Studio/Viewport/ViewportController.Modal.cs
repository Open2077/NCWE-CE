// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using ImGuiNET;
using NCWE.Core;
using NCWE.Studio.Session;
using Windows.System;

namespace NCWE.Studio.Viewport;

// Blender shortcuts (Edit ▸ Preferences): G, R or S over the view moves, turns or scales the
// selection with the pointer alone (ModalTransform), without the gizmo. X, Y or Z keep it to an
// axis, digits type the amount, a click or Enter keeps the result, the right button or Esc drops
// it. In mesh edit mode the same keys act on the picked vertices or faces.
internal sealed partial class ViewportController
{
    public ModalTransform? Modal { get; private set; }
    private NativeInput.Point _modalCursor;
    private long _modalSelection;
    // On the vertices or faces picked in mesh edit mode (their own preview and undo step).
    private bool _modalMesh;
    private bool _modalSnapping;
    // Automation: the pointer is moved by the pipe (the real mouse is somebody else's).
    private bool _modalSimulated;
    private Vector2 _modalPending;
    private string _modalReadout = "";

    private bool ModalSelectionOk => _modalMesh
        ? Session.MeshEditing && Session.MeshTool == MeshEditTool.Select && Session.MeshSelectionCenter is not null
        : !Session.MeshEditing && Session.Selected is { CanEdit: true, Deleted: false } lead && !Session.IsLocked(lead.Id);

    // Something a transform key can act on, right now.
    public bool CanStartModal
    {
        get
        {
            if (Ai.Locked || IsLooking || _gizmoDragging || _leftDown || _vertexDrag) return false;
            if (Session.MeshEditing) return Session.MeshTool == MeshEditTool.Select && Session.MeshSelectionCenter is not null;
            // The tools that work on objects (Place too: the piece just placed is adjusted at once); the others have their own selections.
            if (Tool is not (EditorTool.Select or EditorTool.Move or EditorTool.Rotate or EditorTool.Scale or EditorTool.Place) && !(Tool == EditorTool.Element && Session.ArmedElement is null)) return false;
            return Session.Selected is { CanEdit: true, Deleted: false } lead && !Session.IsLocked(lead.Id);
        }
    }

    // Starts moving, turning or scaling the selection with the pointer; during one, changes its kind.
    public bool BeginModal(ModalKind kind, bool simulated = false)
    {
        if (Modal is { } running) { running.SetKind(kind); return true; }
        if (!CanStartModal) return false;
        _modalMesh = Session.MeshEditing;
        var pivot = _modalMesh ? Matrix4x4.CreateTranslation(Session.MeshSelectionCenter!.Value) : Session.GizmoPivot(true) ?? Session.Selected!.Transform.ViewMatrix;
        Modal = new(kind, pivot, PointerOverViewport ? _pointer : ViewportPixels / 2);
        _modalCursor = NativeInput.Cursor(); _modalSelection = Session.SelectionVersion;
        _modalSimulated = simulated; _modalPending = default; _modalSnapping = false; _modalReadout = "";
        App.Breadcrumb("modal " + kind);
        return true;
    }

    // Automation: the pointer moves by this many pixels at the next frame.
    internal void SimulateModalPointer(Vector2 delta) => _modalPending += delta;

    // A key pressed while a transform runs: every key is its own until it ends.
    public bool ModalKey(VirtualKey key, bool shift)
    {
        if (Modal is not { } modal) return false;
        switch (key)
        {
            case VirtualKey.Enter: ConfirmModal(); break;
            case VirtualKey.Escape: CancelModal(); break;
            case VirtualKey.X: modal.PressAxis(0, shift); break;
            case VirtualKey.Y: modal.PressAxis(1, shift); break;
            case VirtualKey.Z: modal.PressAxis(2, shift); break;
            case VirtualKey.G: modal.SetKind(ModalKind.Move); break;
            case VirtualKey.R: modal.SetKind(ModalKind.Rotate); break;
            case VirtualKey.S: modal.SetKind(ModalKind.Scale); break;
            case VirtualKey.Back: modal.Backspace(); break;
            default: if (TypedCharacter(key, shift) is { } c) modal.Type(c); break;
        }
        return true;
    }

    // The digit, sign or decimal separator a key types on this keyboard layout (digits under
    // Shift on AZERTY, the keypad everywhere).
    private static char? TypedCharacter(VirtualKey key, bool shift)
    {
        if (key is >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9) return (char)('0' + (key - VirtualKey.NumberPad0));
        if (key == VirtualKey.Decimal) return '.';
        if (key == VirtualKey.Subtract) return '-';
        char plain = NativeInput.VirtualKeyChar((int)key);
        if (!shift && plain is >= '0' and <= '9' or '-' or '.' or ',') return plain;
        if (key is >= VirtualKey.Number0 and <= VirtualKey.Number9) return (char)('0' + (key - VirtualKey.Number0));
        return null;
    }

    public void ConfirmModal()
    {
        if (Modal is null) return;
        Modal = null;
        if (_modalMesh) Session.MeshEditCommitTransform(); else Session.CommitPreview();
        if (_modalSnapping) Session.EndObjectSnap();
        _modalSnapping = false;
        if (_modalReadout.Length > 0 && Session.Error.Length == 0) Session.Status = _modalReadout;
    }

    public void CancelModal()
    {
        if (Modal is null) return;
        Modal = null;
        if (_modalMesh) Session.MeshEditCancelPreview(); else Session.CancelPreview();
        if (_modalSnapping) Session.EndObjectSnap();
        _modalSnapping = false;
        Session.Status = Loc.T("Transform cancelled: nothing changed.");
    }

    // Each frame of a transform: the pointer's move becomes the preview, drawn with its axis and amount.
    private void UpdateModal(ImDrawListPtr draw)
    {
        if (Modal is not { } modal) return;
        // Whatever takes the selection from under it (the outliner, an undo, the agent) drops it.
        if (Ai.Locked || Session.SelectionVersion != _modalSelection || !ModalSelectionOk) { CancelModal(); return; }
        var frame = Session.Camera.GetFrame((float)_pixelWidth / _pixelHeight, Renderer.CameraRelative);
        var delta = _modalPending; _modalPending = default;
        if (!_modalSimulated)
        {
            var cursor = NativeInput.Cursor();
            if (NativeInput.IsForeground(_window)) delta += new Vector2(cursor.X - _modalCursor.X, cursor.Y - _modalCursor.Y);
            _modalCursor = cursor;
        }
        bool shift = !_modalSimulated && NativeInput.IsVirtualKeyDown(NativeInput.VkShift), control = !_modalSimulated && NativeInput.IsVirtualKeyDown(NativeInput.VkControl);
        bool alt = !_modalSimulated && NativeInput.IsVirtualKeyDown(NativeInput.VkMenu);
        // Ctrl held turns the snap on, or off when it is on (the toolbar's Snap).
        float? step = Snap == control ? null : modal.Kind switch { ModalKind.Rotate => RotateSnap, ModalKind.Scale => .1f, _ => _modalMesh ? Math.Min(TranslateSnap, .05f) : TranslateSnap };
        var next = modal.Update(frame, ViewportPixels, delta, shift, step);
        // A move lines up with the faces of nearby objects, as the gizmo's does (Alt: freely).
        if (modal.Kind == ModalKind.Move && !_modalMesh && SnapObjects && !alt && !modal.Local && modal.TypedValue is null)
        {
            if (!_modalSnapping) { Session.BeginObjectSnap(); _modalSnapping = true; }
            var move = next.Translation - modal.Start.Translation;
            float threshold = Math.Clamp(Vector3.Distance(Session.Camera.Eye, modal.Start.Translation) * .012f, .03f, 1f);
            next.Translation = modal.Start.Translation + Session.SnapMove(move, threshold);
        }
        if (_modalMesh) { if (Matrix4x4.Invert(modal.Start, out var inverse)) Session.MeshEditPreviewTransform(inverse * next); }
        else Session.PreviewPivot(modal.Start, next);
        _modalReadout = ModalReadout(modal, Coordinates.ViewToGame(next.Translation - modal.Start.Translation));
        DrawModal(draw, modal, frame, next);
    }

    private static readonly string[] AxisNames = ["X", "Y", "Z"];
    // An axis is drawn both ways from the pivot, as far as it stays in front of the camera.
    private static readonly float[] AxisSides = [-1, 1], AxisReaches = [4000, 800, 160, 32, 6];
    private static readonly Vector4[] AxisColors = [new(1f, .36f, .36f, 1), new(.5f, .92f, .42f, 1), new(.42f, .64f, 1f, 1)];

    // "Moving · along X · 2.50 m": the kind, what keeps it and the amount.
    private static string ModalReadout(ModalTransform modal, Vector3 moved)
    {
        string kind = modal.Kind switch { ModalKind.Rotate => Loc.T("Turning"), ModalKind.Scale => Loc.T("Scaling"), _ => Loc.T("Moving") };
        string plane = modal.Axis switch { 0 => "YZ", 1 => "XZ", 2 => "XY", _ => "" };
        string kept = modal.Axis < 0 ? modal.Kind == ModalKind.Rotate ? Loc.T("about the line of sight") : ""
            : modal.Kind == ModalKind.Rotate ? modal.Local ? Loc.F("about its own {0} axis", AxisNames[modal.Axis]) : Loc.F("about {0}", AxisNames[modal.Axis])
            : modal.Plane ? modal.Local ? Loc.F("in its own {0} plane", plane) : Loc.F("in the {0} plane", plane)
            : modal.Local ? Loc.F("along its own {0} axis", AxisNames[modal.Axis]) : Loc.F("along {0}", AxisNames[modal.Axis]);
        string amount = modal.Kind switch
        {
            ModalKind.Rotate => string.Format(Loc.Culture, "{0:0.0}°", modal.Degrees),
            ModalKind.Scale => string.Format(Loc.Culture, "× {0:0.000}", modal.Factor),
            _ when modal.Axis >= 0 && !modal.Plane && !modal.Local => string.Format(Loc.Culture, "{0:0.00} m", modal.Axis switch { 0 => moved.X, 1 => moved.Y, _ => moved.Z }),
            _ when modal.Axis >= 0 && !modal.Plane => string.Format(Loc.Culture, "{0:0.00} m", Vector3.Dot(Coordinates.GameToView(moved), modal.Direction(modal.Axis))),
            _ => string.Format(Loc.Culture, "X {0:0.00}   Y {1:0.00}   Z {2:0.00} m", moved.X, moved.Y, moved.Z)
        };
        return kind + (kept.Length > 0 ? " · " + kept : "") + " · " + amount;
    }

    private void DrawModal(ImDrawListPtr draw, ModalTransform modal, CameraFrame frame, Matrix4x4 now)
    {
        var viewport = ViewportPixels; var viewProjection = frame.ViewProjection;
        // A move's axes follow the pivot; a turn and a scale keep theirs where the pivot is.
        var pivot = now.Translation - frame.Origin;
        uint shadow = U32(0, 0, 0, .55f);
        if (modal.Axis >= 0)
            for (int axis = 0; axis < 3; axis++)
            {
                if ((axis == modal.Axis) == (modal.Plane && modal.Kind != ModalKind.Rotate)) continue;
                var direction = modal.Direction(axis);
                uint color = ImGui.ColorConvertFloat4ToU32(AxisColors[axis]);
                foreach (float side in AxisSides)
                    foreach (float reach in AxisReaches)
                        if (ModalTransform.Project(pivot, viewProjection, viewport) is { } a && ModalTransform.Project(pivot + direction * (reach * side), viewProjection, viewport) is { } b)
                        { draw.AddLine(a, b, shadow, 3.5f); draw.AddLine(a, b, color, 1.6f); break; }
            }
        var tone = modal.Axis >= 0 && !(modal.Plane && modal.Kind != ModalKind.Rotate) ? AxisColors[modal.Axis] : new Vector4(1f, .78f, .18f, 1);
        uint accent = ImGui.ColorConvertFloat4ToU32(tone);
        if (ModalTransform.Project(pivot, viewProjection, viewport) is { } centre)
        {
            // A turn and a scale are read from the pointer's place around the pivot.
            if (modal.Kind != ModalKind.Move && modal.TypedValue is null) { draw.AddLine(centre, modal.Pointer, shadow, 3); draw.AddLine(centre, modal.Pointer, U32(1, 1, 1, .75f), 1); }
            draw.AddCircleFilled(centre, 5.5f, shadow, 16); draw.AddCircleFilled(centre, 4, accent, 16);
        }
        string typed = modal.Typed.Length > 0 ? "   [ " + modal.Typed + " ]" : "";
        string title = _modalReadout + typed;
        string keys = Loc.T("X Y Z: one axis (again: the object's own) · Shift + X Y Z: the plane without it · digits: exact amount · click or Enter: keep · right click or Esc: cancel");
        var titleSize = ImGui.CalcTextSize(title); var keysSize = ImGui.CalcTextSize(keys);
        float width = MathF.Max(titleSize.X, keysSize.X);
        // Above the bottom edge's own messages (the agent's pill, the hints of the tools).
        var at = new Vector2((_pixelWidth - width) / 2, _pixelHeight - titleSize.Y - keysSize.Y - 112);
        draw.AddRectFilled(at - new Vector2(12, 8), at + new Vector2(width + 12, titleSize.Y + keysSize.Y + 14), U32(.05f, .06f, .08f, .86f), 6);
        draw.AddRect(at - new Vector2(12, 8), at + new Vector2(width + 12, titleSize.Y + keysSize.Y + 14), accent, 6, ImDrawFlags.None, 1);
        draw.AddText(at + new Vector2((width - titleSize.X) / 2, 0), accent, title);
        draw.AddText(at + new Vector2((width - keysSize.X) / 2, titleSize.Y + 6), U32(.82f, .85f, .9f), keys);
    }
}
