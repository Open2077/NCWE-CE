// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using ImGuiNET;
using NCWE.Core;

namespace NCWE.Studio.Viewport;

// One collaborator as the 3D view shows them: their camera (where they are, where they look) and
// what they have selected (those objects are locked for everyone else), in their colour.
internal sealed record CollabPeer(string Id, string Name, Vector4 Color, Vector3? Eye, Vector3 Forward, IReadOnlyList<Bounds3> Selection, string? Tool, bool Viewer);

// The other participants of a live session, drawn over the frame with the ImGui overlay (game
// space, metres, Z up). The window refreshes the peers a few times a second; the overlay draws
// them every frame (so they follow the camera smoothly) and glides their cameras to new places.
internal sealed class CollabPresence
{
    private const float Near = .05f;
    private static readonly Vector4[] Palette =
    [
        new(1f, .62f, .25f, 1), new(.45f, .9f, .45f, 1), new(.95f, .45f, .85f, 1), new(1f, .85f, .3f, 1),
        new(.45f, .75f, 1f, 1), new(1f, .45f, .45f, 1), new(.6f, 1f, .85f, 1), new(.75f, .6f, 1f, 1)
    ];

    private IReadOnlyList<CollabPeer> _peers = [];
    private readonly Dictionary<string, (Vector3 Eye, Vector3 Forward)> _shown = new(StringComparer.Ordinal);
    private ImDrawListPtr _draw;
    private CameraFrame _frame;
    private Vector2 _viewport;

    public IReadOnlyList<CollabPeer> Peers
    {
        get => _peers;
        set { _peers = value; foreach (var id in _shown.Keys.Where(k => value.All(p => p.Id != k)).ToArray()) _shown.Remove(id); }
    }

    public static Vector4 ColorOf(string key)
    {
        uint hash = 2166136261;
        foreach (char c in key) { hash ^= c; hash *= 16777619; }
        return Palette[hash % (uint)Palette.Length];
    }

    public void Draw(ImDrawListPtr draw, CameraFrame frame, Vector2 viewport, float dt)
    {
        if (_peers.Count == 0) return;
        _draw = draw; _frame = frame; _viewport = viewport;
        float glide = 1 - MathF.Exp(-dt * 10);
        foreach (var peer in _peers)
        {
            foreach (var box in peer.Selection) DrawBox(box, peer.Color, peer.Name);
            if (peer.Eye is not { } target) continue;
            var (eye, forward) = _shown.TryGetValue(peer.Id, out var shown) && Vector3.DistanceSquared(shown.Eye, target) < 250 * 250
                ? (Vector3.Lerp(shown.Eye, target, glide), Vector3.Normalize(Vector3.Lerp(shown.Forward, peer.Forward, glide)))
                : (target, peer.Forward);
            if (!float.IsFinite(forward.X)) forward = peer.Forward;
            _shown[peer.Id] = (eye, forward);
            DrawCamera(eye, forward, peer);
        }
    }

    // A small camera body with its view cone, the name above it; far away, just the name tag.
    private void DrawCamera(Vector3 eye, Vector3 forward, CollabPeer peer)
    {
        float distance = Clip(eye).W;   // depth from the viewer
        if (!Project(eye, out var at))
        {
            DrawOffscreen(eye, peer);
            return;
        }
        if (forward.LengthSquared() < 1e-6f) forward = Vector3.UnitY;
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitZ) is { } r && r.LengthSquared() > 1e-6f ? r : Vector3.UnitX);
        var up = Vector3.Normalize(Vector3.Cross(right, forward));
        float size = Math.Clamp(distance * .03f, .35f, 6f);
        var tip = eye + forward * size * 2.2f;
        var corners = new[] { tip + (right + up * .62f) * size, tip + (right - up * .62f) * size, tip + (-right - up * .62f) * size, tip + (-right + up * .62f) * size };
        uint line = U(peer.Color, .95f), fill = U(peer.Color, .16f);
        var screen = new Vector2[4];
        bool all = true;
        for (int i = 0; i < 4; i++) all &= Project(corners[i], out screen[i]);
        if (all)
        {
            _draw.AddQuadFilled(screen[0], screen[1], screen[2], screen[3], fill);
            for (int i = 0; i < 4; i++) { _draw.AddLine(at, screen[i], line, 1.4f); _draw.AddLine(screen[i], screen[(i + 1) % 4], line, 1.4f); }
        }
        _draw.AddCircleFilled(at, 9, U(peer.Color, .18f), 20);
        _draw.AddCircleFilled(at, 4.5f, line, 16);
        _draw.AddCircleFilled(at, 2, U(new(1, 1, 1, 1), .95f), 10);
        Tag(at - new Vector2(0, 14), peer.Name + (peer.Viewer ? Loc.T(" (viewing)") : ""), peer.Color);
    }

    // Behind the camera or out of the frame: an arrow on the edge pointing at them.
    private void DrawOffscreen(Vector3 eye, CollabPeer peer)
    {
        var c = Clip(eye);
        var dir = new Vector2(c.X, -c.Y) * MathF.Sign(c.W == 0 ? 1 : c.W);
        if (dir.LengthSquared() < 1e-8f) return;
        dir = Vector2.Normalize(dir);
        var center = _viewport / 2;
        var half = _viewport / 2 - new Vector2(28, 28);
        float scale = MathF.Min(MathF.Abs(half.X / (dir.X == 0 ? 1e-6f : dir.X)), MathF.Abs(half.Y / (dir.Y == 0 ? 1e-6f : dir.Y)));
        var at = center + dir * scale;
        var side = new Vector2(-dir.Y, dir.X);
        _draw.AddTriangleFilled(at + dir * 9, at - dir * 5 + side * 6, at - dir * 5 - side * 6, U(peer.Color, .9f));
        Tag(at - dir * 18, peer.Name, peer.Color);
    }

    private void DrawBox(Bounds3 box, Vector4 color, string name)
    {
        var a = box.Min; var b = box.Max;
        Vector3[] p =
        [
            new(a.X, a.Y, a.Z), new(b.X, a.Y, a.Z), new(b.X, b.Y, a.Z), new(a.X, b.Y, a.Z),
            new(a.X, a.Y, b.Z), new(b.X, a.Y, b.Z), new(b.X, b.Y, b.Z), new(a.X, b.Y, b.Z)
        ];
        uint glow = U(color, .22f), line = U(color, .95f);
        foreach (var (i, j) in Edges)
            if (Segment(p[i], p[j], out var sa, out var sb)) { _draw.AddLine(sa, sb, glow, 4.5f); _draw.AddLine(sa, sb, line, 1.5f); }
        if (Project(new((a.X + b.X) / 2, (a.Y + b.Y) / 2, b.Z), out var top)) Tag(top - new Vector2(0, 8), name, color);
    }

    private static readonly (int, int)[] Edges = [(0, 1), (1, 2), (2, 3), (3, 0), (4, 5), (5, 6), (6, 7), (7, 4), (0, 4), (1, 5), (2, 6), (3, 7)];

    private void Tag(Vector2 at, string text, Vector4 color)
    {
        var size = ImGui.CalcTextSize(text);
        var min = new Vector2(at.X - size.X / 2 - 6, at.Y - size.Y - 4);
        var max = new Vector2(at.X + size.X / 2 + 6, at.Y + 1);
        _draw.AddRectFilled(min, max, U(new(.035f, .045f, .065f, 1), .82f), 4);
        _draw.AddRect(min, max, U(color, .9f), 4, ImDrawFlags.None, 1);
        _draw.AddText(new(min.X + 6, min.Y + 2), U(color, 1), text);
    }

    // --- Projection (as AiPresence) ---------------------------------------------------------------

    private Vector4 Clip(Vector3 game) => Vector4.Transform(new Vector4(Coordinates.GameToView(game) - _frame.Origin, 1), _frame.ViewProjection);
    private Vector2 Screen(Vector4 c) => new((c.X / c.W * .5f + .5f) * _viewport.X, (.5f - c.Y / c.W * .5f) * _viewport.Y);

    private bool Project(Vector3 game, out Vector2 at)
    {
        var c = Clip(game);
        at = c.W > Near ? Screen(c) : default;
        return c.W > Near && at.X >= -40 && at.Y >= -40 && at.X <= _viewport.X + 40 && at.Y <= _viewport.Y + 40;
    }

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

    private static uint U(Vector4 c, float alpha) => ImGui.ColorConvertFloat4ToU32(c with { W = Math.Clamp(c.W * alpha, 0, 1) });
}
