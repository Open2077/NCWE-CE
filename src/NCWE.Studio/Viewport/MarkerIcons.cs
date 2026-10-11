// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using ImGuiNET;
using NCWE.Studio.Session;

namespace NCWE.Studio.Viewport;

// Gizmo icons drawn with the ImGui overlay: a dark disc with a glyph per marker kind.
// Lights use their own color (brightened so dark lights stay readable).
internal static class MarkerIcons
{
    private static readonly Vector4 Backdrop = new(.07f, .08f, .1f, .8f), Rim = new(1, 1, 1, .35f), Selection = new(1f, .78f, .18f, 1), Grey = new(.62f, .64f, .68f, 1), Authored = new(.3f, .85f, 1f, .95f);

    public static void Draw(ImDrawListPtr draw, StudioSession.MarkerIcon icon)
    {
        float r = icon.Radius, alpha = icon.Alpha;
        var c = icon.Screen;
        uint U(Vector4 v) => ImGui.ColorConvertFloat4ToU32(v with { W = v.W * alpha });
        draw.AddCircleFilled(c, r + 2, U(Backdrop), 24);
        draw.AddCircle(c, r + 2, U(icon.Selected ? Selection : icon.Authored ? Authored : Rim), 24, icon.Selected || icon.Authored ? 2f : 1f);
        var color = icon.Color;
        switch (icon.Kind)
        {
            case "Light":
            {
                float peak = MathF.Max(color.X, MathF.Max(color.Y, color.Z));
                var bulb = peak > .01f ? new Vector4(color.X / peak, color.Y / peak, color.Z / peak, 1) : new Vector4(1, .95f, .8f, 1);
                var center = c + new Vector2(0, -r * .16f);
                for (int i = 0; i < 5; i++)
                {
                    float a = MathF.PI * (1.1f + i * .2f);
                    var dir = new Vector2(MathF.Cos(a), MathF.Sin(a));
                    draw.AddLine(center + dir * r * .62f, center + dir * r * .86f, U(bulb), 1.4f);
                }
                draw.AddCircleFilled(center, r * .44f, U(bulb), 16);
                draw.AddRectFilled(c + new Vector2(-r * .2f, r * .26f), c + new Vector2(r * .2f, r * .52f), U(Grey), 1.5f);
                break;
            }
            case "Sound":
                draw.AddRectFilled(c + new Vector2(-r * .58f, -r * .2f), c + new Vector2(-r * .3f, r * .2f), U(color));
                draw.AddQuadFilled(c + new Vector2(-r * .3f, -r * .2f), c + new Vector2(r * .02f, -r * .5f), c + new Vector2(r * .02f, r * .5f), c + new Vector2(-r * .3f, r * .2f), U(color));
                draw.PathArcTo(c + new Vector2(r * .02f, 0), r * .32f, -.85f, .85f, 8); draw.PathStroke(U(color), ImDrawFlags.None, 1.4f);
                draw.PathArcTo(c + new Vector2(r * .02f, 0), r * .6f, -.85f, .85f, 10); draw.PathStroke(U(color), ImDrawFlags.None, 1.4f);
                break;
            case "Effect":
                draw.AddQuadFilled(c + new Vector2(0, -r * .78f), c + new Vector2(r * .17f, 0), c + new Vector2(0, r * .78f), c + new Vector2(-r * .17f, 0), U(color));
                draw.AddQuadFilled(c + new Vector2(-r * .78f, 0), c + new Vector2(0, -r * .17f), c + new Vector2(r * .78f, 0), c + new Vector2(0, r * .17f), U(color));
                draw.AddCircleFilled(c + new Vector2(r * .45f, -r * .45f), r * .13f, U(color), 8);
                break;
            case "Door":
                draw.AddRect(c + new Vector2(-r * .38f, -r * .62f), c + new Vector2(r * .38f, r * .62f), U(color), 1.5f, ImDrawFlags.None, 1.6f);
                draw.AddCircleFilled(c + new Vector2(r * .18f, r * .05f), r * .1f, U(color), 8);
                break;
            case "Entity":
                // A small cube.
                draw.AddQuad(c + new Vector2(-r * .45f, -r * .2f), c + new Vector2(0, -r * .48f), c + new Vector2(r * .45f, -r * .2f), c + new Vector2(0, r * .08f), U(color), 1.4f);
                draw.AddLine(c + new Vector2(-r * .45f, -r * .2f), c + new Vector2(-r * .45f, r * .32f), U(color), 1.4f);
                draw.AddLine(c + new Vector2(r * .45f, -r * .2f), c + new Vector2(r * .45f, r * .32f), U(color), 1.4f);
                draw.AddLine(c + new Vector2(0, r * .08f), c + new Vector2(0, r * .6f), U(color), 1.4f);
                draw.AddLine(c + new Vector2(-r * .45f, r * .32f), c + new Vector2(0, r * .6f), U(color), 1.4f);
                draw.AddLine(c + new Vector2(r * .45f, r * .32f), c + new Vector2(0, r * .6f), U(color), 1.4f);
                break;
            case "Decal":
                draw.AddRect(c - new Vector2(r * .5f), c + new Vector2(r * .5f), U(color), 1.5f, ImDrawFlags.None, 1.4f);
                draw.AddTriangleFilled(c + new Vector2(r * .5f, -r * .5f), c + new Vector2(r * .5f, r * .1f), c + new Vector2(-r * .1f, -r * .5f), U(color));
                break;
            default:
                draw.AddCircleFilled(c, r * .35f, U(color), 12);
                break;
        }
        // Read-only markers (quest content) get a small lock-like bar.
        if (!icon.Editable) draw.AddLine(c + new Vector2(-r * .7f, r * .75f), c + new Vector2(r * .7f, r * .75f), U(Grey), 2f);
    }
}
