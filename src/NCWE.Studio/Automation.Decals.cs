// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using NCWE.Core;
using NCWE.Studio.Session;

namespace NCWE.Studio;

// api.decals.*: the custom decal bank for agents and tests (import a picture, list, change its
// size or glow, rename, replace the picture, delete). Placing one is api.elements.add with kind
// "decal" and "decal": its id or name (or "resource": its material path).
internal sealed partial class Automation
{
    private static object DecalInfo(StudioSession s, CustomDecalInfo info) => new
    {
        id = info.Id, name = info.Name, material = info.MaterialPath, source = info.SourceFile, pixels = new[] { info.Width, info.Height }, has_alpha = info.HasAlpha,
        size = new[] { R(info.Size), R(info.Size * info.Proportions) }, emissive = info.Emissive, emissive_ev = info.Emissive ? info.EmissiveEV : (float?)null,
        version = info.Version, warnings = info.Warnings, uses_in_project = s.CustomDecalUses(info.Id)
    };

    private async Task<object> Decals(StudioSession s, string op, JsonElement c)
    {
        var library = s.CustomDecals;
        CustomDecalInfo Item() => library.Resolve(Str(c, "id") ?? Str(c, "name") ?? throw new ArgumentException("id is required."))
            ?? throw new KeyNotFoundException("No custom decal " + (Str(c, "id") ?? Str(c, "name")) + " (api.decals.list lists them).");
        const string hint = "Place it with add_elements: kind decal, decal = its id (scale defaults to its own size; rotation {pitch: 90, yaw} stands it on a wall).";
        switch (op)
        {
            case "api.decals.list":
                return new { ok = true, folder = library.Directory, decals = library.List().Select(i => DecalInfo(s, i)).ToArray() };
            case "api.decals.import":
            {
                string path = Str(c, "path") ?? throw new ArgumentException("path is required.");
                bool trim = !c.TryGetProperty("trim", out var t) || t.ValueKind != JsonValueKind.False;
                var info = await Task.Run(() => library.Import(path, Str(c, "name"), OptFloat(c, "size"), trim));
                if (c.TryGetProperty("emissive", out var glow) && glow.ValueKind == JsonValueKind.True)
                    info = library.Save(info with { Emissive = true, EmissiveEV = OptFloat(c, "emissive_ev") ?? info.EmissiveEV });
                _window.CustomDecalsChanged(info.Id);
                return new { ok = true, decal = DecalInfo(s, info), hint };
            }
            case "api.decals.update":
            {
                var info = Item();
                var next = info with
                {
                    Name = c.TryGetProperty("id", out _) && Str(c, "name") is { Length: > 0 } name ? name : info.Name,
                    Size = OptFloat(c, "size") ?? info.Size,
                    Emissive = c.TryGetProperty("emissive", out var glow) && glow.ValueKind is JsonValueKind.True or JsonValueKind.False ? glow.GetBoolean() : info.Emissive,
                    EmissiveEV = OptFloat(c, "emissive_ev") ?? info.EmissiveEV
                };
                if (next != info) next = library.Save(next);
                _window.CustomDecalsChanged(info.Id);
                return new { ok = true, decal = DecalInfo(s, next) };
            }
            case "api.decals.rename":
            {
                var info = library.Rename(Item().Id, Str(c, "new_name") ?? Str(c, "name") ?? throw new ArgumentException("name is required."));
                _window.CustomDecalsChanged(info.Id);
                return new { ok = true, decal = DecalInfo(s, info) };
            }
            case "api.decals.replace":
            {
                var info = Item();
                string path = Str(c, "path") ?? throw new ArgumentException("path is required.");
                bool trim = !c.TryGetProperty("trim", out var t) || t.ValueKind != JsonValueKind.False;
                var next = await Task.Run(() => library.ReplaceImage(info.Id, path, trim));
                _window.CustomDecalsChanged(info.Id);
                return new { ok = true, decal = DecalInfo(s, next) };
            }
            case "api.decals.delete":
            {
                var info = Item();
                library.Delete(info.Id);
                _window.CustomDecalsChanged(info.Id);
                return new { ok = true, deleted = info.Id };
            }
            case "api.decals.cook":
            {
                // What an export writes for a decal (paths and sizes), built now.
                var info = Item();
                var files = await Task.Run(() => library.Cook(info));
                return new { ok = true, files = files.Select(f => new { path = f.Path, bytes = f.Bytes.Length }).ToArray() };
            }
            case "api.decals.select":
            {
                // Shows the bank (Add ▸ Decals) with this decal chosen, ready to place.
                var info = Item();
                _window.ShowAddCategory("Decal");
                _window.SelectDecal(info.Id);
                return new { ok = true };
            }
            default: throw new ArgumentException("Unknown command " + op);
        }
    }
}
