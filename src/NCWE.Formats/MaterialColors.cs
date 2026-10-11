// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using WolvenKit.RED4.Archive.CR2W;
using WolvenKit.RED4.Archive.IO;
using WolvenKit.RED4.Types;
using Vector3 = System.Numerics.Vector3;

namespace NCWE.Formats;

// Documented approximation of RED4 materials as one flat color per material slot:
// - multilayered.mt: the colorScale of the setup's first (base) layer, looked up in that
//   layer template's colorScale overrides; masks, microblends and upper layers are ignored;
// - other instances: BaseColorScale / color parameters when present;
// - material-instance chains (.mi) are followed up to four levels.
// Textures are not sampled. A null result means "no reliable color": callers fall back.
public sealed class MaterialColors(GameArchiveIndex index)
{
    private readonly ConcurrentDictionary<ulong, Vector3?> _setups = new(), _instances = new();
    private readonly ConcurrentDictionary<ulong, IReadOnlyDictionary<string, Vector3>> _templates = new();

    public Vector3? ForInstance(CMaterialInstance? instance, int depth = 0)
    {
        if (instance is null || depth > 4) return null;
        foreach (var pair in instance.Values)
        {
            string key = pair.Key.ToString() ?? "";
            if (key == "MultilayerSetup" && pair.Value is IRedRef setup) return Setup(setup.DepotPath.GetRedHash());
        }
        foreach (var pair in instance.Values)
        {
            string key = pair.Key.ToString() ?? "";
            if (key is "BaseColorScale" && pair.Value is Vector4 v) return Clamp(new((float)v.X, (float)v.Y, (float)v.Z));
            if (key is "color" or "Color" or "BaseColor" && pair.Value is CColor c) return new(c.Red / 255f, c.Green / 255f, c.Blue / 255f);
        }
        var parent = instance.BaseMaterial.DepotPath;
        string? path = parent.GetResolvedText();
        if (path is not null && path.EndsWith(".mi", StringComparison.OrdinalIgnoreCase)) return External(parent.GetRedHash(), depth + 1);
        return null;
    }

    public Vector3? External(ulong hash, int depth = 0)
    {
        if (hash == 0 || depth > 4) return null;
        if (_instances.TryGetValue(hash, out var cached)) return cached;
        Vector3? color = null;
        try { color = Read(hash, file => ForInstance(file.RootChunk as CMaterialInstance, depth)); }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or FileNotFoundException) { }
        return _instances[hash] = color;
    }

    private Vector3? Setup(ulong hash)
    {
        if (_setups.TryGetValue(hash, out var cached)) return cached;
        Vector3? color = null;
        try
        {
            color = Read(hash, file =>
            {
                if (file.RootChunk is not Multilayer_Setup setup || setup.Layers.Count == 0) return (Vector3?)null;
                var layer = setup.Layers[0];
                var palette = Template(layer.Material.DepotPath.GetRedHash());
                string scale = layer.ColorScale.ToString() ?? "";
                return palette.TryGetValue(scale, out var rgb) ? Clamp(rgb) : null;
            });
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or FileNotFoundException) { }
        return _setups[hash] = color;
    }

    private IReadOnlyDictionary<string, Vector3> Template(ulong hash)
    {
        if (_templates.TryGetValue(hash, out var cached)) return cached;
        IReadOnlyDictionary<string, Vector3> palette = new Dictionary<string, Vector3>();
        try
        {
            palette = Read(hash, file =>
            {
                var result = new Dictionary<string, Vector3>();
                if (file.RootChunk is Multilayer_LayerTemplate template && template.Overrides is { } overrides)
                    foreach (var entry in overrides.ColorScale)
                        if (entry.V.Count >= 3) result[entry.N.ToString() ?? ""] = new(entry.V[0], entry.V[1], entry.V[2]);
                return (IReadOnlyDictionary<string, Vector3>)result;
            }) ?? palette;
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or FileNotFoundException) { }
        return _templates[hash] = palette;
    }

    private T? Read<T>(ulong hash, Func<CR2WFile, T?> convert)
    {
        var entry = index.EntryOrNull(hash) ?? throw new FileNotFoundException($"Resource {hash} is not in the archive profile.");
        using var data = index.ExtractEntry(entry, default);
        using var reader = new CR2WReader(data);
        reader.ParsingError += SectorReader.TolerateSectorParsingError;
        if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file is null) throw new InvalidDataException("CR2W read failed.");
        using (file) return convert(file);
    }

    // Last-resort guess for vegetation, whose SpeedTree materials only carry textures:
    // bark/trunk/wood brown, leaves/fronds/grass green. Never used when a color resolved.
    public static Vector3? GuessFromNames(string slotName, string? baseMaterial, string? meshPath = null)
    {
        string s = slotName.ToLowerInvariant(), b = (baseMaterial ?? "").ToLowerInvariant(), m = (meshPath ?? "").ToLowerInvariant();
        if (s.Contains("bark") || s.Contains("trunk") || s.Contains("stem") || s.Contains("wood") || s.Contains("branch")) return new(.24f, .16f, .10f);
        if (s.Contains("leaf") || s.Contains("leaves") || s.Contains("frond") || s.Contains("grass") || s.Contains("bush") || s.Contains("shrub")
            || s.Contains("needle") || s.Contains("foliage") || s.Contains("atlas") || s.Contains("billboard") || b.Contains("speedtree") || b.Contains("vegetation")) return new(.13f, .20f, .07f);
        // Unnamed vegetation slots: dry/fall shrubs straw, the rest green.
        if (m.Contains(@"\vegetation\")) return m.Contains("dry") || m.Contains("fall") || m.Contains("dead") ? new(.30f, .25f, .15f) : new(.13f, .20f, .07f);
        return null;
    }

    private static Vector3 Clamp(Vector3 v) => Vector3.Clamp(v, Vector3.Zero, new(1.5f));
}
