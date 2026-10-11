// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using System.Text.Json;
using ImGuiNET;

namespace NCWE.Editor;

internal sealed partial class EditorWindow
{
    private bool _flyLookActive, _flyPointerCaptured, _nativeRightDown, _inputFocused = true;
    private float _flySpeed = 20;
    private int _flyLayout;
    private Point _flyPointerOrigin, _flyPointerCenter;
    private Vector2 _nativeLookDelta;

    private void BuildCameraControls()
    {
        if (!_worldMode) return;
        bool flying = _worldCamera.IsFlying;
        if (ImGui.Checkbox("Free fly", ref flying)) { StopFlyLook(); _worldCamera.SetFlying(flying); }
        Remember("camera-fly");
        if (!flying) return;
        ImGui.SameLine(); ImGui.SetNextItemWidth(130);
        ImGui.DragFloat("##fly-speed", ref _flySpeed, 1, .1f, 500, "%.1f m/s");
        _flySpeed = float.IsFinite(_flySpeed) ? Math.Clamp(_flySpeed, .1f, 500) : 20;
        Remember("camera-speed");
        ImGui.SameLine(); ImGui.SetNextItemWidth(95);
        ImGui.Combo("##fly-layout", ref _flyLayout, "WASD\0ZQSD\0"); Remember("camera-layout");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Movement keys while holding the right mouse button. Wheel changes speed; Escape releases the mouse.");
    }

    private void NavigateFlyCamera(bool hovered)
    {
        var io = ImGui.GetIO();
        if (!_worldMode || !_worldCamera.IsFlying || !_inputFocused || io.WantTextInput || ImGui.IsKeyPressed(ImGuiKey.Escape))
        { StopFlyLook(); return; }
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Right)) { StopFlyLook(); return; }
        if (!_flyLookActive && hovered && !_gizmoDragging && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            _flyLookActive = true;
            // Only real native input captures/warps the OS pointer. Pipe-driven
            // acceptance tests must not move the user's cursor or take focus.
            if (_nativeRightDown && ContainsFocus)
            {
                _flyPointerOrigin = Cursor.Position;
                _flyPointerCenter = new((int)(_viewportPosition.X + _viewportSize.X / 2), (int)(_viewportPosition.Y + _viewportSize.Y / 2));
                _nativeLookDelta = Vector2.Zero; _flyPointerCaptured = true;
                Capture = true; Cursor.Hide(); Cursor.Position = PointToScreen(_flyPointerCenter);
                FeedMouse(_flyPointerCenter.X, _flyPointerCenter.Y);
            }
        }
        if (!_flyLookActive) return;
        var delta = _flyPointerCaptured ? _nativeLookDelta : io.MouseDelta;
        _nativeLookDelta = Vector2.Zero;
        // A newly pressed button may follow an unrelated pointer relocation.
        if (!ImGui.IsMouseClicked(ImGuiMouseButton.Right)) _worldCamera.Orbit(-delta.X * .004f, delta.Y * .004f);
        if (io.MouseWheel != 0) _flySpeed = Math.Clamp(_flySpeed * MathF.Exp(io.MouseWheel * .18f), .1f, 500);
        float Key(ImGuiKey key) => ImGui.IsKeyDown(key) ? 1 : 0;
        var input = new Vector3(Key(ImGuiKey.D) - Key(_flyLayout == 0 ? ImGuiKey.A : ImGuiKey.Q),
            Key(ImGuiKey.Space) - Key(ImGuiKey.C), Key(_flyLayout == 0 ? ImGuiKey.W : ImGuiKey.Z) - Key(ImGuiKey.S));
        // Do not interpret Ctrl+S/Z/D editing shortcuts as navigation.
        if (!io.KeyCtrl && !io.KeyAlt) _worldCamera.Move(input, io.DeltaTime, _flySpeed * (io.KeyShift ? 4 : 1));
    }

    private void FeedPointer(int x, int y)
    {
        if (!_flyPointerCaptured) { FeedMouse(x, y); return; }
        var delta = new Vector2(x - _flyPointerCenter.X, y - _flyPointerCenter.Y);
        if (delta == Vector2.Zero) return;
        _nativeLookDelta += delta;
        Cursor.Position = PointToScreen(_flyPointerCenter);
    }

    private void StopFlyLook(bool restorePointer = true)
    {
        _flyLookActive = false; _nativeLookDelta = Vector2.Zero;
        if (!_flyPointerCaptured) return;
        _flyPointerCaptured = false; Capture = false; Cursor.Show();
        if (restorePointer && ContainsFocus) Cursor.Position = _flyPointerOrigin;
    }

    private void SetInputFocus(bool focused)
    {
        _inputFocused = focused;
        if (!focused) { _nativeRightDown = false; StopFlyLook(false); }
        if (_ui is not null) ImGui.GetIO().AddFocusEvent(focused);
    }

    private bool ExecuteCameraCommand(JsonElement command, AutomationReply result)
    {
        switch (command.GetProperty("op").GetString())
        {
            case "camera-options":
                float speed = command.TryGetProperty("speed", out var s) ? s.GetSingle() : _flySpeed;
                int layout = command.TryGetProperty("layout", out var l) ? l.GetString() switch {
                    "WASD" => 0, "ZQSD" => 1, _ => throw new ArgumentException("Use WASD or ZQSD.") } : _flyLayout;
                if (!float.IsFinite(speed) || speed is < .1f or > 500) throw new ArgumentOutOfRangeException("speed");
                bool flying = command.TryGetProperty("fly", out var f) ? f.GetBoolean() : _worldCamera.IsFlying;
                StopFlyLook(); _worldCamera.SetFlying(flying); _flySpeed = speed; _flyLayout = layout; break;
            case "input-focus": SetInputFocus(command.GetProperty("focused").GetBoolean()); break;
            default: return false;
        }
        result.SetResult(new { ok = true, frame = _frame }); return true;
    }
}
