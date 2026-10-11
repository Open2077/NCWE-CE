// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Globalization;
using NCWE.Core;
using WolvenKit.RED4.Types;
using RedVector4 = WolvenKit.RED4.Types.Vector4;

namespace NCWE.Formats.CustomMeshes;

// Material definitions (CustomMaterial) as the game's material instances, and back:
// - Build: the CMaterialInstance a definition becomes (written as a .mi by the export, read by
//   the editor's material chain for its preview); `texture` gives the depot path each of its own
//   textures gets (channel d/n/r/m/e, or the parameter's name, and the file);
// - Parameters: a game instance's values as typed parameters (import into the bank).
public static class MaterialInstances
{
    public static CMaterialInstance Build(CustomMaterial m, Func<string, string, string> texture)
    {
        var channels = new Dictionary<(int, char), string>();
        void Channel(char c, string? file) { if (file is not null) channels[(0, c)] = texture(c.ToString(), file); }
        Channel('d', m.BaseColorTexture); Channel('n', m.NormalTexture); Channel('r', m.RoughnessTexture); Channel('m', m.MetalnessTexture);
        if (m.EmissiveEV > 0) Channel('e', m.EmissiveTexture);
        return m.Kind switch
        {
            // A child of the game's material, nothing changed: meshes wear it through the project's .mi.
            CustomMaterialKind.Game => CustomMeshCooker.Instance(m.GameMaterial ?? CustomMeshCooker.StandardTemplate),
            CustomMaterialKind.Glass => CustomMeshCooker.Glass(m, 0, channels),
            CustomMaterialKind.Multilayer => CustomMeshCooker.Multilayer(m),
            CustomMaterialKind.Instance => FromParameters(m, texture),
            _ => CustomMeshCooker.Standard(m, 0, channels)
        };
    }

    public static CMaterialInstance FromParameters(CustomMaterial m, Func<string, string, string> texture)
    {
        var instance = CustomMeshCooker.Instance(string.IsNullOrWhiteSpace(m.BaseMaterial) ? CustomMeshCooker.StandardTemplate : m.BaseMaterial);
        foreach (var p in m.Parameters.IsDefault ? [] : m.Parameters)
            if (Value(p, texture) is { } value) instance.Values.Add(new CKeyValuePair(p.Name, value));
        return instance;
    }

    public static IRedType? Value(MaterialParameter p, Func<string, string, string> texture)
    {
        float N(int i) => i < p.Numbers.Length ? p.Numbers[i] : 0;
        byte B(int i) => (byte)Math.Clamp(MathF.Round(N(i)), 0, 255);
        return p.Type switch
        {
            MaterialParameterType.Float => (CFloat)N(0),
            MaterialParameterType.Bool => (CBool)(N(0) != 0),
            MaterialParameterType.Color => new CColor { Red = B(0), Green = B(1), Blue = B(2), Alpha = B(3) },
            MaterialParameterType.Vector => new RedVector4 { X = N(0), Y = N(1), Z = N(2), W = N(3) },
            MaterialParameterType.Name => (CName)p.Text,
            MaterialParameterType.Resource => Reference(p.ResourceType, p.File is { } file ? texture(p.Name, file) : p.Text),
            _ => null
        };
    }

    // A reference to a resource of the named type (rRef:<type>); a path "#<hex>" is a bare hash.
    public static IRedType? Reference(string type, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var target = typeof(CMaterialInstance).Assembly.GetType("WolvenKit.RED4.Types." + type);
        if (target is null) return null;
        ResourcePath resource = path.StartsWith('#') && ulong.TryParse(path.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash) ? (ResourcePath)hash : (ResourcePath)path;
        try { return System.Activator.CreateInstance(typeof(CResourceReference<>).MakeGenericType(target), resource) as IRedType; }
        catch (ArgumentException) { return null; }
    }

    // A material instance's own values as parameters: those of a kind the export writes again
    // (floats, booleans, colours, vectors, names, resource references); `skipped`: the others.
    public static ImmutableArray<MaterialParameter> Parameters(CMaterialInstance instance, out int skipped)
    {
        var result = ImmutableArray.CreateBuilder<MaterialParameter>(); skipped = 0;
        foreach (var pair in instance.Values)
        {
            string name = pair.Key.ToString() ?? "";
            if (name.Length == 0 || pair.Value is null) continue;
            if (Parameter(name, pair.Value) is { } p && result.All(q => q.Name != name)) result.Add(p); else skipped++;
        }
        return result.ToImmutable();
    }

    public static MaterialParameter? Parameter(string name, IRedType value) => value switch
    {
        CFloat f => new(name, MaterialParameterType.Float) { Numbers = [(float)f] },
        CBool b => new(name, MaterialParameterType.Bool) { Numbers = [(bool)b ? 1 : 0] },
        CColor c => new(name, MaterialParameterType.Color) { Numbers = [c.Red, c.Green, c.Blue, c.Alpha] },
        RedVector4 v => new(name, MaterialParameterType.Vector) { Numbers = [(float)v.X, (float)v.Y, (float)v.Z, (float)v.W] },
        CName n => new(name, MaterialParameterType.Name) { Text = n.ToString() ?? "" },
        IRedRef r when ReferenceType(value) is { } type => new(name, MaterialParameterType.Resource)
        {
            Text = r.DepotPath.GetResolvedText() is { Length: > 0 } text ? text : r.DepotPath.GetRedHash() is var h && h != 0 ? "#" + h.ToString("x16") : "",
            ResourceType = type
        },
        _ => null
    };

    private static string? ReferenceType(IRedType value)
    {
        var type = value.GetType();
        return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(CResourceReference<>) ? type.GenericTypeArguments[0].Name : null;
    }

    // A definition's own textures (files of its store or folder) as they are written: the channel or
    // parameter they serve, the file, how the image is prepared and how it is stored.
    public sealed record OwnTexture(string Channel, string File, Func<RgbaImage, RgbaImage> Prepare, Func<RgbaImage, TextureKind> Kind);

    public static IEnumerable<OwnTexture> OwnTextures(CustomMaterial m)
    {
        if (m.Kind is CustomMaterialKind.Game or CustomMaterialKind.Multilayer) yield break;
        if (m.Kind == CustomMaterialKind.Instance)
        {
            foreach (var p in m.Parameters.IsDefault ? [] : m.Parameters)
                if (p.File is { } file) yield return new(p.Name, file, x => x, x => TextureKindFor(p.Name, x));
            yield break;
        }
        if (m.Kind == CustomMaterialKind.Standard && m.BaseColorTexture is { } color)
            yield return new("d", color, x => x, x => m.AlphaCutout && x.HasAlpha() ? TextureKind.ColorAlpha : TextureKind.Color);
        if (m.NormalTexture is { } normal) yield return new("n", normal, x => m.FlipNormalGreen ? TextureFiles.FlipGreen(x) : x, _ => TextureKind.Normal);
        if (m.Kind != CustomMaterialKind.Standard) yield break;
        if (m.RoughnessTexture is { } rough) yield return new("r", rough, x => TextureFiles.Channel(x, m.RoughnessChannel, m.InvertRoughness), _ => TextureKind.Gray);
        if (m.MetalnessTexture is { } metal) yield return new("m", metal, x => TextureFiles.Channel(x, m.MetalnessChannel, false), _ => TextureKind.Gray);
        if (m.EmissiveEV > 0 && m.EmissiveTexture is { } emissive) yield return new("e", emissive, x => x, _ => TextureKind.Color);
    }

    // The slot name a custom mesh's material gets in its cooked mesh (m00_name...).
    public static string CustomSlot(CustomMaterial m, int index) => CustomMeshCooker.SlotName(m, index);

    // How an own texture is stored, from the parameter's name (normal maps, single channels, colour).
    public static TextureKind TextureKindFor(string parameter, RgbaImage image)
    {
        string p = parameter.ToLowerInvariant();
        if (p.Contains("normal")) return TextureKind.Normal;
        if (p.Contains("rough") || p.Contains("metal") || p.Contains("mask") || p.Contains("height") || p.Contains("occlusion") || p == "ao") return TextureKind.Gray;
        return image.HasAlpha() ? TextureKind.ColorAlpha : TextureKind.Color;
    }

    // Whether an own texture holds colour (sampled as sRGB).
    public static bool IsColorTexture(string parameterOrChannel) => parameterOrChannel is "d" or "e"
        || (parameterOrChannel.Length > 1 && !parameterOrChannel.Contains("normal", StringComparison.OrdinalIgnoreCase)
            && !new[] { "rough", "metal", "mask", "height", "occlusion" }.Any(k => parameterOrChannel.Contains(k, StringComparison.OrdinalIgnoreCase)));
}
