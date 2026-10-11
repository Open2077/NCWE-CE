// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using NCWE.Core.Fx;
using NCWE.Studio.Session;

namespace NCWE.Studio;

// api.fx.*: the visual effects played in the 3D view, for agents and tests: what plays and
// what is drawn, switching them on and off, showing one effect in front of the camera, and what
// a particle system or an effect is made of.
internal sealed partial class Automation
{
    private Task<object> FxApi(StudioSession s, string op, JsonElement c)
    {
        switch (op)
        {
            case "api.fx.state": return Task.FromResult(s.EffectsState());
            case "api.fx.play":
            {
                if (c.TryGetProperty("on", out var on)) _window.SetPlayEffects(on.GetBoolean());
                if (c.TryGetProperty("range", out var range)) s.EffectRange = Math.Clamp(range.GetSingle(), 5, 400);
                if (c.TryGetProperty("exposure", out var exposure)) _window.SetEffectExposure(exposure.GetSingle());
                if (c.TryGetProperty("gradient_exposure", out var gradients)) _window.SetLightGradientExposure(gradients.GetSingle());
                return Task.FromResult<object>(new { ok = true, on = s.PlayEffects, range = s.EffectRange });
            }
            case "api.fx.preview":
            {
                // Shows an effect or a particle system in front of the camera (or at "position"); "stop": true ends it.
                if (c.TryGetProperty("stop", out var stop) && stop.GetBoolean()) { s.PreviewEffect(null); return Task.FromResult<object>(new { ok = true }); }
                string path = Str(c, "path") ?? throw new ArgumentException("path is required (a .particle or .effect of the game).");
                if (s.Index is null || !s.Index.Contains(path)) throw new FileNotFoundException("The game has no " + path + " (search_assets finds effects and particle systems).");
                System.Numerics.Vector3? at = c.TryGetProperty("position", out var p) && p.ValueKind == JsonValueKind.Array && p.GetArrayLength() == 3 ? new(p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle()) : null;
                s.PreviewEffect(path, at);
                return Task.FromResult<object>(new { ok = true, preview = path, hint = "It plays in front of the camera until api.fx.preview stop:true; screenshot shows it." });
            }
            case "api.fx.choose":
            {
                // The interface's own way: Add ▸ Effects, the effect chosen, its preview on (checks of the panel).
                string path = Str(c, "path") ?? throw new ArgumentException("path is required.");
                bool chosen = _window.ChooseVfx(path, !c.TryGetProperty("preview", out var pv) || pv.GetBoolean());
                return Task.FromResult<object>(new { ok = chosen, preview = s.EffectPreviewPath });
            }
            case "api.fx.hover":
            {
                // The card a resting pointer opens on a row of Add ▸ Effects (checks of the panel); "stop": true closes it.
                return Hover();
                async Task<object> Hover()
                {
                    if (c.TryGetProperty("stop", out var end) && end.GetBoolean()) { await _window.HoverVfxAsync(null); return new { ok = true }; }
                    // "pointer": true goes through what a pointer move does (the card opens after the rest delay).
                    bool opened = Str(c, "path") is not { } path || await _window.HoverVfxAsync(path, c.TryGetProperty("pointer", out var pointer) && pointer.GetBoolean());
                    return new { ok = opened, hover = _window.EffectHoverState() };
                }
            }
            case "api.fx.info":
            {
                string path = Str(c, "path") ?? throw new ArgumentException("path is required.");
                var index = s.Index ?? throw new InvalidOperationException("Open a game installation first.");
                if (path.EndsWith(".effect", StringComparison.OrdinalIgnoreCase))
                {
                    var effect = index.ReadEffect(path) ?? throw new FileNotFoundException("No effect " + path);
                    return Task.FromResult<object>(new
                    {
                        ok = true, path, seconds = effect.Length, loop = effect.Loop is { } l ? new[] { l.Start, l.End } : null,
                        particles = effect.Particles.Select(i => new { system = i.System, begins = i.Begin, lasts = i.Duration, drawable = Drawable(index.ReadParticleSystem(i.System)) }).ToArray(),
                        sounds = effect.Sounds.Select(i => new { @event = i.Event, at = i.Begin }).ToArray(), lights = effect.Lights.Length, not_shown = effect.Skipped
                    });
                }
                var system = index.ReadParticleSystem(path) ?? throw new FileNotFoundException("No particle system " + path);
                return Task.FromResult<object>(new
                {
                    ok = true, path, one_shot = system.OneShot, hidden_beyond = system.AutoHideDistance, drawable = Drawable(system),
                    emitters = system.Emitters.Select(e => new
                    {
                        name = e.Name, drawer = e.Drawer.ToString(), shader = e.Material.Shader.ToString(), template = e.Material.Template, max_particles = e.MaxParticles, loops = e.Loops,
                        modules = e.Modules.Select(m => m.Kind.ToString()).ToArray(), textures = e.Material.Textures.Values.ToArray(), meshes = e.Meshes
                    }).ToArray()
                });
            }
            default: throw new ArgumentException("Unknown command " + op);
        }
    }

    // How many of a system's emitters the view draws.
    private static string Drawable(FxSystem? system)
    {
        if (system is null) return "unreadable";
        int drawn = system.Emitters.Count(e => e.Drawable);
        return $"{drawn} of {system.Emitters.Length} emitters";
    }
}
