// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using System.Text.Json;
using NCWE.Core;
using NCWE.Formats.CustomMeshes;
using NCWE.Studio.Session;

namespace NCWE.Studio;

// api.custom.*: the custom mesh library for agents and tests (import a model without the
// dialog, list, edit materials, re-import with another scale or axis, delete).
internal sealed partial class Automation
{
    private object CustomInfo(StudioSession s, CustomMeshInfo info) => new
    {
        id = info.Id, name = info.Name, asset = info.MeshPath, version = info.Version, source = info.SourceFile,
        size = A(Coordinates.ViewToGame(info.Bounds.Size) is var g ? new Vector3(MathF.Abs(g.X), MathF.Abs(g.Y), MathF.Abs(g.Z)) : default),
        pieces = info.Submeshes, triangles = info.Triangles, uses_in_project = s.CustomMeshUses(info.Hash), warnings = info.Warnings,
        import = new { scale = info.Import.Scale, up_axis = info.Import.UpAxis.ToString(), pivot = info.Import.Pivot.ToString(), yaw = info.Import.Yaw },
        materials = info.Materials.Select((m, i) => new
        {
            index = i, name = m.Name, kind = m.Kind.ToString(), game_material = m.GameMaterial, base_color = new[] { m.BaseColor.X, m.BaseColor.Y, m.BaseColor.Z, m.BaseColor.W },
            base_color_texture = m.BaseColorTexture, roughness = m.Roughness, roughness_texture = m.RoughnessTexture, metalness = m.Metalness, metalness_texture = m.MetalnessTexture,
            normal_texture = m.NormalTexture, normal_strength = m.NormalStrength, emissive_color = new[] { m.EmissiveColor.X, m.EmissiveColor.Y, m.EmissiveColor.Z }, emissive_ev = m.EmissiveEV,
            emissive_texture = m.EmissiveTexture, alpha_cutout = m.AlphaCutout, alpha_threshold = m.AlphaThreshold, double_sided = m.DoubleSided, glass_opacity = m.GlassOpacity
        }).ToArray()
    };

    private static CustomImportSettings ImportSettings(JsonElement c, CustomImportSettings basis, Bounds3 source)
    {
        var settings = basis;
        if (OptFloat(c, "scale") is { } scale && scale > 0) settings = settings with { Scale = scale };
        if (Str(c, "up_axis") is { } up) settings = settings with { UpAxis = up.Trim().ToUpperInvariant() == "Z" ? CustomUpAxis.Z : CustomUpAxis.Y };
        if (Str(c, "pivot") is { } pivot) settings = settings with { Pivot = pivot.ToLowerInvariant() switch { "center" or "centre" => CustomPivot.Center, "original" or "file" => CustomPivot.Original, _ => CustomPivot.BottomCenter } };
        if (OptFloat(c, "yaw") is { } yaw) settings = settings with { Yaw = yaw };
        if (c.TryGetProperty("import_materials", out var materials) && materials.ValueKind is JsonValueKind.True or JsonValueKind.False) settings = settings with { ImportMaterials = materials.GetBoolean() };
        if (Int(c, "max_texture_size", 0) is int size and >= 256) settings = settings with { MaxTextureSize = Math.Min(size, 4096) };
        // A wanted height (metres) sets the scale.
        if (OptFloat(c, "height") is { } height && height > 0)
        {
            float sourceHeight = settings.UpAxis == CustomUpAxis.Z ? source.Size.Z : source.Size.Y;
            if (sourceHeight > 1e-6f) settings = settings with { Scale = height / sourceHeight };
        }
        return settings;
    }

    private async Task<object> Custom(StudioSession s, string op, JsonElement c)
    {
        var library = s.CustomMeshes;
        CustomMeshInfo Item() => library.Get(Str(c, "id") ?? throw new ArgumentException("id is required.")) ?? throw new KeyNotFoundException("No custom mesh " + Str(c, "id") + " (custom_meshes lists them).");
        switch (op)
        {
            case "api.custom.list":
                return new { ok = true, folder = library.Directory, meshes = library.List().Select(i => CustomInfo(s, i)).ToArray() };
            case "api.custom.analyze":
            {
                var source = await Task.Run(() => library.Prepare(Str(c, "path") ?? throw new ArgumentException("path is required.")));
                try
                {
                    var analysis = await Task.Run(() => library.Analyze(source));
                    var size = analysis.SourceBounds.Size;
                    return new
                    {
                        ok = true, model = Path.GetFileName(analysis.ModelPath), name = source.Name, vertices = analysis.Vertices, triangles = analysis.Triangles, meshes = analysis.Meshes,
                        skinned = analysis.Skinned, detected = new { scale = analysis.Detected.Scale, up_axis = analysis.Detected.UpAxis.ToString() },
                        source_size = A(size), height_m = (analysis.Detected.UpAxis == CustomUpAxis.Z ? size.Z : size.Y) * analysis.Detected.Scale,
                        materials = analysis.Materials.Select(m => m.Name).ToArray(), textures = analysis.Textures.ToDictionary(t => Path.GetFileName(t.Key.Replace('\\', '/')), t => t.Value is not null),
                        warnings = analysis.Warnings, licence = source.License
                    };
                }
                finally { if (source.Unpacked) try { Directory.Delete(source.Folder, true); } catch (IOException) { } }
            }
            case "api.custom.import":
            {
                if (s.Index is null) throw new InvalidOperationException("Open the game first.");
                var source = await Task.Run(() => library.Prepare(Str(c, "path") ?? throw new ArgumentException("path is required.")));
                try
                {
                    var analysis = await Task.Run(() => library.Analyze(source));
                    var settings = ImportSettings(c, analysis.Detected, analysis.SourceBounds);
                    var info = await Task.Run(() => library.Import(source, Str(c, "name") ?? source.Name, settings));
                    return new { ok = true, mesh = CustomInfo(s, info), hint = "Place it with place_objects (asset = mesh.asset), like a game prop." };
                }
                catch { if (source.Unpacked) try { Directory.Delete(source.Folder, true); } catch (IOException) { } throw; }
            }
            case "api.custom.reimport":
            {
                var info = Item();
                var analysis = await Task.Run(() => library.AnalyzeSource(info.Id));
                var saved = await Task.Run(() => library.Reimport(info.Id, ImportSettings(c, info.Import, analysis.SourceBounds)));
                s.CustomMeshChanged(saved.Hash);
                return new { ok = true, mesh = CustomInfo(s, saved) };
            }
            case "api.custom.material":
            {
                var info = Item();
                int index = Int(c, "index", -1);
                if (index < 0 && Str(c, "material") is { } named) index = info.Materials.ToList().FindIndex(m => m.Name.Equals(named, StringComparison.OrdinalIgnoreCase));
                if (index < 0 || index >= info.Materials.Length) throw new ArgumentException($"No material {index}: this mesh has {info.Materials.Length}.");
                var m = info.Materials[index];
                Vector4? V4(string key) => c.TryGetProperty(key, out var e) && e.ValueKind == JsonValueKind.Array && e.GetArrayLength() >= 3
                    ? new Vector4(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle(), e.GetArrayLength() > 3 ? e[3].GetSingle() : 1) : null;
                string? Texture(string key, string? current)
                {
                    if (!c.TryGetProperty(key, out var e)) return current;
                    if (e.ValueKind == JsonValueKind.Null || e.GetString() is not { Length: > 0 } path) return null;
                    return File.Exists(path) ? library.AddTexture(info.Id, path) : File.Exists(Path.Combine(library.TexturesFolder(info.Id), path)) ? path : throw new FileNotFoundException("Texture not found: " + path);
                }
                if (Str(c, "kind") is { } kind) m = m with { Kind = Enum.TryParse<CustomMaterialKind>(kind, true, out var k) ? k : throw new ArgumentException("kind: Standard, Glass or Game.") };
                if (Str(c, "game_material") is { } game) m = m with { GameMaterial = game, Kind = CustomMaterialKind.Game };
                if (V4("base_color") is { } color) m = m with { BaseColor = color };
                if (V4("emissive_color") is { } glow) m = m with { EmissiveColor = new(glow.X, glow.Y, glow.Z) };
                if (OptFloat(c, "roughness") is { } roughness) m = m with { Roughness = Math.Clamp(roughness, 0, 1) };
                if (OptFloat(c, "metalness") is { } metalness) m = m with { Metalness = Math.Clamp(metalness, 0, 1) };
                if (OptFloat(c, "normal_strength") is { } strength) m = m with { NormalStrength = strength };
                if (OptFloat(c, "emissive_ev") is { } ev) m = m with { EmissiveEV = Math.Max(0, ev) };
                if (OptFloat(c, "alpha_threshold") is { } threshold) m = m with { AlphaThreshold = Math.Clamp(threshold, 0, 1) };
                if (OptFloat(c, "glass_opacity") is { } opacity) m = m with { GlassOpacity = Math.Clamp(opacity, 0, 1) };
                if (c.TryGetProperty("alpha_cutout", out var cut)) m = m with { AlphaCutout = cut.GetBoolean() };
                if (c.TryGetProperty("double_sided", out var sides)) m = m with { DoubleSided = sides.GetBoolean() };
                if (c.TryGetProperty("flip_normal_green", out var flip)) m = m with { FlipNormalGreen = flip.GetBoolean() };
                m = m with
                {
                    BaseColorTexture = Texture("base_color_texture", m.BaseColorTexture), RoughnessTexture = Texture("roughness_texture", m.RoughnessTexture),
                    MetalnessTexture = Texture("metalness_texture", m.MetalnessTexture), NormalTexture = Texture("normal_texture", m.NormalTexture), EmissiveTexture = Texture("emissive_texture", m.EmissiveTexture)
                };
                var saved = library.Save(info with { Materials = info.Materials.SetItem(index, m) });
                _window.CustomMeshEdited(saved);
                return new { ok = true, mesh = CustomInfo(s, saved) };
            }
            case "api.custom.rename":
            {
                var saved = library.Save(Item() with { Name = Str(c, "name") is { Length: > 0 } n ? n : throw new ArgumentException("name is required.") });
                _window.CustomMeshEdited(saved);
                return new { ok = true, mesh = CustomInfo(s, saved) };
            }
            case "api.custom.delete":
            {
                var info = Item();
                library.Delete(info.Id); s.CustomMeshChanged(info.Hash);
                return new { ok = true, deleted = info.Id };
            }
            case "api.custom.cook":
            {
                // Builds its game resources now (what an export packs): sizes, for checks.
                var info = Item();
                var files = await Task.Run(() => library.Cook(info, s.Index ?? throw new InvalidOperationException("Open the game first.")));
                return new { ok = true, files = files.Select(f => new { path = f.Path, kb = f.Bytes.Length / 1024 }).ToArray() };
            }
            default: throw new ArgumentException("Unknown command " + op);
        }
    }
}
