// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Serialization;

namespace NCWE.Core;

// Materials the project puts on meshes. A material is a definition (CustomMaterial: a PBR
// material, glass, a game multilayer, a reference to a game material, or any material instance
// with its parameters) kept in the project (ChangeSet.Materials) and assigned to the material
// slots of project meshes (AuthoredPlacement.Materials; a game mesh is first replaced by a project
// copy of its node). The user's material bank (MaterialAsset) is where materials are made, kept
// and imported from the game; applying one copies it into the project. The export writes each
// project material as a .mi and each mesh wearing one as a variant of its .mesh (MaterialPaths).

public enum MaterialParameterType { Float, Color, Vector, Resource, Name, Bool }

// One value of an Instance material, as the game's material instances store them.
public sealed record MaterialParameter(string Name, MaterialParameterType Type)
{
    // Float: one number; Color: red, green, blue, alpha (0-255, sRGB); Vector: x, y, z, w; Bool: 0 or 1.
    public ImmutableArray<float> Numbers { get; init; } = [];
    // Resource: a depot path, or "file:<name>" for a texture of the material store; Name: the name.
    public string Text { get; init; } = "";
    // Resource: the type it refers to (ITexture, Multilayer_Setup, Multilayer_Mask, CGradient...).
    public string ResourceType { get; init; } = "ITexture";

    public const string FilePrefix = "file:";
    [JsonIgnore] public string? File => Type == MaterialParameterType.Resource && Text.StartsWith(FilePrefix, StringComparison.Ordinal) ? Text[FilePrefix.Length..] : null;
    [JsonIgnore] public bool IsTexture => Type == MaterialParameterType.Resource && ResourceType is "ITexture" or "CBitmapTexture";

    public static int NumberCount(MaterialParameterType type) => type switch
    {
        MaterialParameterType.Float or MaterialParameterType.Bool => 1,
        MaterialParameterType.Color or MaterialParameterType.Vector => 4,
        _ => 0
    };

    public MaterialParameter Validated()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 128) throw new InvalidDataException("A material parameter needs a name (128 characters at most).");
        if (!Enum.IsDefined(Type)) throw new InvalidDataException($"Material parameter {Name}: unknown type.");
        var numbers = Numbers.IsDefault ? [] : Numbers;
        if (numbers.Length != NumberCount(Type)) throw new InvalidDataException($"Material parameter {Name}: {NumberCount(Type)} number(s) expected.");
        if (numbers.Any(n => !float.IsFinite(n))) throw new InvalidDataException($"Material parameter {Name}: numbers must be finite.");
        if (Type == MaterialParameterType.Color && numbers.Any(n => n is < 0 or > 255)) throw new InvalidDataException($"Material parameter {Name}: colour channels are 0 to 255.");
        if (Text.Length > 512 || ResourceType.Length is 0 or > 64 || !ResourceType.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            throw new InvalidDataException($"Material parameter {Name}: invalid value or resource type.");
        return this with { Numbers = numbers };
    }
}

// A mesh slot (its material entry's name) wearing a project material.
public sealed record SlotMaterial(string Slot, string Material);

public sealed record ProjectMaterial(string Id, string Name, CustomMaterial Material)
{
    // The bank material it was taken from (null: made in the project).
    public string? BankId { get; init; }

    public const string Prefix = "m:";
    public static string NewId() => Prefix + Guid.NewGuid().ToString("N");
    // The id in depot paths (its hexadecimal part).
    [JsonIgnore] public string Key => Id.StartsWith(Prefix, StringComparison.Ordinal) ? Id[Prefix.Length..] : Id;

    public ProjectMaterial Validated()
    {
        if (!Id.StartsWith(Prefix, StringComparison.Ordinal) || Key.Length is 0 or > 64 || !Key.All(char.IsAsciiHexDigitLower))
            throw new InvalidDataException("A project material needs an m: identity.");
        if (string.IsNullOrWhiteSpace(Name)) throw new InvalidDataException("A material needs a name.");
        return this with { Name = Name.Length > 200 ? Name[..200] : Name, Material = MaterialDefinitions.Validated(Material) };
    }
}

// A material of the user's bank (<state>\Materials\bank): made there, imported from a game mesh or
// material, or saved from a project; reused across projects.
public sealed record MaterialAsset(string Id, string Name, CustomMaterial Material)
{
    public string Category { get; init; } = "";
    public ImmutableArray<string> Tags { get; init; } = [];
    // Where it came from (a game mesh and slot, a .mi), for the user.
    public string Source { get; init; } = "";
    public DateTimeOffset Created { get; init; }
    public DateTimeOffset Modified { get; init; }
    public int Version { get; init; } = 1;

    public static string NewId(string name) => CustomMeshPaths.NewId(name, "material");
    public static bool IsValidId(string id) => CustomMeshPaths.IsValidId(id);
}

public static class MaterialDefinitions
{
    // Defaults and ranges kept, the paths a kind needs present.
    public static CustomMaterial Validated(CustomMaterial m)
    {
        if (!Enum.IsDefined(m.Kind)) throw new InvalidDataException("Unknown material kind.");
        static float Clamp(float v, float lo, float hi, float fallback) => float.IsFinite(v) ? Math.Clamp(v, lo, hi) : fallback;
        bool Path(string? p) => !string.IsNullOrWhiteSpace(p) && p.Length <= 512;
        switch (m.Kind)
        {
            case CustomMaterialKind.Game when !Path(m.GameMaterial):
                throw new InvalidDataException($"Material {m.Name}: a game material needs its .mi or .mt path.");
            case CustomMaterialKind.Multilayer when !Path(m.MultilayerSetup) || !Path(m.MultilayerMask):
                throw new InvalidDataException($"Material {m.Name}: a multilayered material needs its setup (.mlsetup) and mask (.mlmask).");
            case CustomMaterialKind.Instance when !Path(m.BaseMaterial):
                throw new InvalidDataException($"Material {m.Name}: a material instance needs its base material (.mt, .remt or .mi).");
        }
        var parameters = m.Parameters.IsDefault ? [] : m.Parameters.Select(p => p.Validated()).ToImmutableArray();
        if (parameters.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != parameters.Length)
            throw new InvalidDataException($"Material {m.Name}: a parameter is set twice.");
        if (parameters.Length > 256) throw new InvalidDataException($"Material {m.Name}: 256 parameters at most.");
        return m with
        {
            Name = string.IsNullOrWhiteSpace(m.Name) ? "material" : m.Name.Length > 200 ? m.Name[..200] : m.Name,
            Roughness = Clamp(m.Roughness, 0, 1, .6f), Metalness = Clamp(m.Metalness, 0, 1, 0), NormalStrength = Clamp(m.NormalStrength, 0, 10, 1),
            EmissiveEV = Clamp(m.EmissiveEV, 0, 30, 0), AlphaThreshold = Clamp(m.AlphaThreshold, 0, 1, .5f), GlassOpacity = Clamp(m.GlassOpacity, 0, 1, .25f),
            RoughnessChannel = Math.Clamp(m.RoughnessChannel, 0, 3), MetalnessChannel = Math.Clamp(m.MetalnessChannel, 0, 3),
            Parameters = parameters
        };
    }

    // A stable fingerprint of a definition (its JSON): what a mesh variant or a preview depends on.
    public static string Fingerprint(CustomMaterial m)
    {
        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(m, ProjectStorage.JsonOptions);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(json))[..16].ToLowerInvariant();
    }
}

// Where the export puts the project's materials and the meshes wearing them, inside the mod's
// archive (its own folder per project: two mods never write the same path).
public static class MaterialPaths
{
    public const string Root = @"ncwe\materials";
    public static string Folder(Guid project) => $@"{Root}\{project:N}";
    public static string Instance(Guid project, ProjectMaterial material) => $@"{Folder(project)}\{material.Key}\{material.Key}.mi";
    // A texture of a material: by channel (d, n, r, m, e) or parameter name.
    public static string Texture(Guid project, ProjectMaterial material, string channel) => $@"{Folder(project)}\{material.Key}\{Clean(channel)}.xbm";
    // A mesh with some of its slots changed: one per mesh and set of assignments.
    public static string Variant(Guid project, MeshReference mesh, IEnumerable<SlotMaterial> slots) => $@"{Folder(project)}\variants\{VariantKey(mesh, slots)}.mesh";
    public static bool IsMaterialPath(string? path) => path?.StartsWith(Root + @"\", StringComparison.OrdinalIgnoreCase) == true;

    public static string VariantKey(MeshReference mesh, IEnumerable<SlotMaterial> slots)
    {
        var text = new StringBuilder(mesh.Path.ToLowerInvariant());
        foreach (var s in slots.OrderBy(s => s.Slot, StringComparer.Ordinal)) text.Append('|').Append(s.Slot).Append('=').Append(s.Material);
        string stem = new([.. Path.GetFileNameWithoutExtension(mesh.Path).ToLowerInvariant().Where(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_').Take(40)]);
        return (stem.Length == 0 ? "mesh" : stem) + "_" + CustomMeshInfo.Fnv1a64(text.ToString()).ToString("x16");
    }

    private static string Clean(string name)
    {
        string clean = new([.. name.ToLowerInvariant().Select(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' ? c : '_')]);
        return clean.Length == 0 ? "texture" : clean.Length > 48 ? clean[..48] : clean;
    }
}
