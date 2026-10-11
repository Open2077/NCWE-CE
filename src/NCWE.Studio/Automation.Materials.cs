// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;
using NCWE.Core;
using NCWE.Studio.Session;

namespace NCWE.Studio;

// api.materials.*: the material tool for agents and tests (the bank, the project's materials,
// mesh slots, applying materials, importing the game's).
internal sealed partial class Automation
{
    private static object MaterialJson(CustomMaterial m) => new
    {
        kind = m.Kind.ToString(), base_color = new[] { m.BaseColor.X, m.BaseColor.Y, m.BaseColor.Z, m.BaseColor.W }, base_color_texture = m.BaseColorTexture,
        roughness = m.Roughness, roughness_texture = m.RoughnessTexture, roughness_channel = m.RoughnessChannel, invert_roughness = m.InvertRoughness,
        metalness = m.Metalness, metalness_texture = m.MetalnessTexture, metalness_channel = m.MetalnessChannel,
        normal_texture = m.NormalTexture, normal_strength = m.NormalStrength, flip_normal_green = m.FlipNormalGreen,
        emissive_color = new[] { m.EmissiveColor.X, m.EmissiveColor.Y, m.EmissiveColor.Z }, emissive_ev = m.EmissiveEV, emissive_texture = m.EmissiveTexture,
        alpha_cutout = m.AlphaCutout, alpha_threshold = m.AlphaThreshold, double_sided = m.DoubleSided, glass_opacity = m.GlassOpacity,
        game_material = m.GameMaterial, multilayer_setup = m.MultilayerSetup, multilayer_mask = m.MultilayerMask, base_material = m.BaseMaterial,
        parameters = (m.Parameters.IsDefault ? [] : m.Parameters).Select(p => new { name = p.Name, type = p.Type.ToString(), value = ParameterValue(p), resource_type = p.Type == MaterialParameterType.Resource ? p.ResourceType : null }).ToArray()
    };

    private static object? ParameterValue(MaterialParameter p) => p.Type switch
    {
        MaterialParameterType.Float or MaterialParameterType.Bool => p.Numbers.Length > 0 ? p.Numbers[0] : 0,
        MaterialParameterType.Color or MaterialParameterType.Vector => p.Numbers.ToArray(),
        _ => p.Text
    };

    // A definition from JSON, over `basis` (fields left out keep its values); texture fields take a
    // store name or an image path on disk (copied into the store).
    private static CustomMaterial MaterialFrom(StudioSession s, JsonElement c, CustomMaterial basis)
    {
        var m = basis;
        if (Str(c, "kind") is { } kind)
            m = m with
            {
                Kind = kind.Trim().ToLowerInvariant() switch
                {
                    "standard" or "pbr" => CustomMaterialKind.Standard,
                    "glass" => CustomMaterialKind.Glass,
                    "multilayer" or "multilayered" => CustomMaterialKind.Multilayer,
                    "game" => CustomMaterialKind.Game,
                    "instance" or "advanced" => CustomMaterialKind.Instance,
                    _ => throw new ArgumentException("kind: Standard, Glass, Multilayer, Game or Instance.")
                }
            };
        float[]? Numbers(string key) => c.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(x => x.GetSingle()).ToArray() : null;
        string? Texture(string key)
        {
            if (!c.TryGetProperty(key, out var v)) return null;
            if (v.ValueKind == JsonValueKind.Null) return "";
            string value = v.GetString() ?? "";
            if (value.Length == 0 || Formats.MaterialStore.IsTextureName(value)) return value;
            return s.AddMaterialTexture(value) ?? throw new InvalidDataException(s.Error);
        }
        static string? Keep(string? value, string? current) => value is null ? current : value.Length == 0 ? null : value;
        if (Numbers("base_color") is { Length: >= 3 } color) m = m with { BaseColor = new(color[0], color[1], color[2], color.Length > 3 ? color[3] : 1) };
        if (Numbers("emissive_color") is { Length: >= 3 } glow) m = m with { EmissiveColor = new(glow[0], glow[1], glow[2]) };
        m = m with
        {
            Name = Str(c, "name") ?? m.Name,
            BaseColorTexture = Keep(Texture("base_color_texture"), m.BaseColorTexture),
            Roughness = OptFloat(c, "roughness") ?? m.Roughness, RoughnessTexture = Keep(Texture("roughness_texture"), m.RoughnessTexture),
            RoughnessChannel = Int(c, "roughness_channel", m.RoughnessChannel), InvertRoughness = c.TryGetProperty("invert_roughness", out var inv) ? inv.GetBoolean() : m.InvertRoughness,
            Metalness = OptFloat(c, "metalness") ?? m.Metalness, MetalnessTexture = Keep(Texture("metalness_texture"), m.MetalnessTexture),
            MetalnessChannel = Int(c, "metalness_channel", m.MetalnessChannel),
            NormalTexture = Keep(Texture("normal_texture"), m.NormalTexture), NormalStrength = OptFloat(c, "normal_strength") ?? m.NormalStrength,
            FlipNormalGreen = c.TryGetProperty("flip_normal_green", out var flip) ? flip.GetBoolean() : m.FlipNormalGreen,
            EmissiveEV = OptFloat(c, "emissive_ev") ?? m.EmissiveEV, EmissiveTexture = Keep(Texture("emissive_texture"), m.EmissiveTexture),
            AlphaCutout = c.TryGetProperty("alpha_cutout", out var cut) ? cut.GetBoolean() : m.AlphaCutout, AlphaThreshold = OptFloat(c, "alpha_threshold") ?? m.AlphaThreshold,
            DoubleSided = c.TryGetProperty("double_sided", out var two) ? two.GetBoolean() : m.DoubleSided, GlassOpacity = OptFloat(c, "glass_opacity") ?? m.GlassOpacity,
            GameMaterial = Str(c, "game_material") ?? m.GameMaterial, MultilayerSetup = Str(c, "multilayer_setup") ?? m.MultilayerSetup,
            MultilayerMask = Str(c, "multilayer_mask") ?? m.MultilayerMask, BaseMaterial = Str(c, "base_material") ?? m.BaseMaterial
        };
        if (c.TryGetProperty("parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Array)
        {
            var list = (m.Parameters.IsDefault ? [] : m.Parameters).ToList();
            foreach (var p in parameters.EnumerateArray())
            {
                string name = Str(p, "name") ?? throw new ArgumentException("A parameter needs a name.");
                list.RemoveAll(q => q.Name == name);
                if (p.TryGetProperty("remove", out var remove) && remove.GetBoolean()) continue;
                var type = Enum.TryParse<MaterialParameterType>(Str(p, "type") ?? "Float", true, out var t) ? t : throw new ArgumentException("type: Float, Color, Vector, Resource, Name or Bool.");
                p.TryGetProperty("value", out var value);
                var parameter = new MaterialParameter(name, type) { ResourceType = Str(p, "resource_type") ?? "ITexture" };
                parameter = value.ValueKind switch
                {
                    JsonValueKind.Array => parameter with { Numbers = [.. value.EnumerateArray().Select(x => x.GetSingle())] },
                    JsonValueKind.Number => parameter with { Numbers = [value.GetSingle()] },
                    JsonValueKind.True or JsonValueKind.False => parameter with { Numbers = [value.GetBoolean() ? 1 : 0] },
                    JsonValueKind.String when type == MaterialParameterType.Resource && value.GetString() is { } text && File.Exists(text)
                        => parameter with { Text = MaterialParameter.FilePrefix + (s.AddMaterialTexture(text) ?? throw new InvalidDataException(s.Error)) },
                    JsonValueKind.String => parameter with { Text = value.GetString() ?? "" },
                    _ => parameter with { Numbers = [.. Enumerable.Repeat(0f, MaterialParameter.NumberCount(type))] }
                };
                list.Add(parameter.Validated());
            }
            m = m with { Parameters = [.. list] };
        }
        return m;
    }

    private Task<object> Materials(StudioSession s, string op, JsonElement c)
    {
        object result;
        switch (op)
        {
            case "api.materials.bank":
                result = new
                {
                    ok = true, folder = s.MaterialBank.Root,
                    materials = s.BankMaterials().Select(a => new { id = a.Id, name = a.Name, category = a.Category, source = a.Source, tags = a.Tags, version = a.Version, material = MaterialJson(a.Material) }).ToArray()
                };
                break;
            case "api.materials.project":
                result = new
                {
                    ok = true,
                    materials = s.Document.Materials.Values.OrderBy(m => m.Name, StringComparer.Ordinal).Select(m => new
                    { id = m.Id, name = m.Name, bank_id = m.BankId, worn_by = s.Document.ObjectsWearing(m.Id).ToArray(), material = MaterialJson(m.Material) }).ToArray(),
                    dressed = s.DressedObjects.ToDictionary(p => p.Key, p => p.Value.ToString("x16"))
                };
                break;
            case "api.materials.slots":
            {
                string id = Str(c, "id") ?? throw new ArgumentException("id is required.");
                bool can = s.CanWearMaterials(id, out var reason);
                result = new
                {
                    ok = true, id, can_change = can, reason = can ? null : reason,
                    slots = s.SlotsOf(id).Select(x => new { name = x.Slot.Name, wears = x.Slot.Material, local = x.Slot.Local, parts = x.Slot.Parts, project_material = x.Worn?.Id, project_material_name = x.Worn?.Name }).ToArray()
                };
                break;
            }
            case "api.materials.texture":
                result = new { ok = true, texture = s.AddMaterialTexture(Str(c, "path") ?? throw new ArgumentException("path is required.")) ?? throw new InvalidDataException(s.Error) };
                break;
            case "api.materials.create":
            {
                string name = Str(c, "name") ?? "Material";
                var material = MaterialDefinitions.Validated(MaterialFrom(s, c, new CustomMaterial(name) { BaseColor = new Vector4(.75f, .75f, .75f, 1), Roughness = .5f }));
                if (Str(c, "target") is "project")
                {
                    var created = new ProjectMaterial(ProjectMaterial.NewId(), name, material);
                    s.AddProjectMaterial(created);
                    result = new { ok = s.Error.Length == 0, error = s.Error, id = created.Id };
                }
                else
                {
                    var saved = s.SaveBankMaterial(new MaterialAsset(MaterialAsset.NewId(name), name, material) { Category = Str(c, "category") ?? "" }) ?? throw new InvalidDataException(s.Error);
                    result = new { ok = true, id = saved.Id };
                }
                break;
            }
            case "api.materials.update":
            {
                string id = Str(c, "id") ?? throw new ArgumentException("id is required.");
                if (s.Document.Materials.TryGetValue(id, out var project))
                {
                    s.UpdateProjectMaterial(project with { Name = Str(c, "name") ?? project.Name, Material = MaterialDefinitions.Validated(MaterialFrom(s, c, project.Material)) });
                    result = new { ok = s.Error.Length == 0, error = s.Error, id };
                }
                else
                {
                    var asset = s.MaterialBank.Get(id) ?? throw new KeyNotFoundException("No material " + id + " in the bank or the project.");
                    var saved = s.SaveBankMaterial(asset with { Name = Str(c, "name") ?? asset.Name, Category = Str(c, "category") ?? asset.Category, Material = MaterialFrom(s, c, asset.Material) })
                        ?? throw new InvalidDataException(s.Error);
                    result = new { ok = true, id = saved.Id, version = saved.Version };
                }
                break;
            }
            case "api.materials.delete":
            {
                string id = Str(c, "id") ?? throw new ArgumentException("id is required.");
                if (s.Document.Materials.ContainsKey(id)) s.RemoveProjectMaterial(id); else s.DeleteBankMaterial(id);
                result = new { ok = s.Error.Length == 0, error = s.Error };
                break;
            }
            case "api.materials.import":
            {
                bool editable = !c.TryGetProperty("editable", out var e) || e.GetBoolean();
                MaterialAsset? asset;
                if (Str(c, "path") is { } path) asset = s.ImportMaterialFileToBank(path, editable, Str(c, "category") ?? "");
                else
                {
                    string id = Str(c, "id") ?? throw new ArgumentException("path (a .mi/.mt) or id and slot are required.");
                    var mesh = s.Document.TryGetObject(id)?.Mesh ?? throw new KeyNotFoundException("No mesh object " + id);
                    asset = s.ImportSlotToBank(mesh.Hash, Str(c, "slot") ?? throw new ArgumentException("slot is required."), editable, Str(c, "name"), Str(c, "category") ?? "");
                }
                result = asset is null ? new { ok = false, error = s.Error } : new { ok = true, id = asset.Id, name = asset.Name, material = MaterialJson(asset.Material) };
                break;
            }
            case "api.materials.apply":
            {
                var ids = Ids(c);
                string? slot = Str(c, "slot") is { } named && named != "*" ? named : null;
                bool restore = c.TryGetProperty("restore", out var r) && r.GetBoolean();
                string? material = restore ? null : Str(c, "material") ?? throw new ArgumentException("material (a bank id or a project m: id) or restore:true is required.");
                IReadOnlyList<string> done = material is null ? s.ApplyMaterial(ids, slot, null)
                    : material.StartsWith(ProjectMaterial.Prefix, StringComparison.Ordinal) ? s.ApplyMaterial(ids, slot, material) : s.ApplyBankMaterial(ids, slot, material);
                result = new { ok = done.Count > 0, error = s.Error, status = s.Status, ids = done };
                break;
            }
            case "api.materials.try":
            {
                // A material tried on objects without changing the project (clear: back to the project's).
                if (c.TryGetProperty("clear", out var clear) && clear.GetBoolean()) { s.TryMaterial(null); result = new { ok = true }; break; }
                var ids = Ids(c);
                string? slot = Str(c, "slot") is { } named && named != "*" ? named : null;
                string material = Str(c, "material") ?? throw new ArgumentException("material (a bank id, a project m: id or a game .mi/.mt path) is required.");
                var definition = s.Document.Materials.TryGetValue(material, out var project) ? project.Material
                    : s.MaterialBank.Get(material)?.Material
                    ?? (s.Index?.ImportMaterialFile(material, editable: false).Material ?? throw new KeyNotFoundException("No material " + material));
                var trial = s.TrialOf(ids, slot is null ? null : [slot], definition) ?? throw new InvalidDataException("This material cannot be drawn.");
                s.TryMaterial(trial, _materialTrialOwner);
                result = new { ok = true, signature = trial.Signature };
                break;
            }
            case "api.materials.slot_at":
            {
                // The object and slot under a pixel of the 3D view (as a drop would find them).
                var size = _window.ViewportPixels();
                // pixel: in pixels; fraction: of the view's size; default: the middle.
                var at = c.TryGetProperty("pixel", out var pixel) ? new Vector2(pixel[0].GetSingle(), pixel[1].GetSingle())
                    : c.TryGetProperty("fraction", out var fraction) ? size * new Vector2(fraction[0].GetSingle(), fraction[1].GetSingle()) : size / 2;
                var hit = s.SlotAt(at, size);
                result = new { ok = hit is not null, id = hit?.Id, slot = hit?.Slot };
                break;
            }
            default: throw new ArgumentException("Unknown command " + op);
        }
        return Task.FromResult(result);
    }

    private readonly object _materialTrialOwner = new();

    private static string[] Ids(JsonElement c) => c.TryGetProperty("ids", out var ids) && ids.ValueKind == JsonValueKind.Array ? [.. ids.EnumerateArray().Select(i => i.GetString() ?? "")]
        : Str(c, "id") is { } one ? [one] : throw new ArgumentException("ids (or id) is required.");
}
