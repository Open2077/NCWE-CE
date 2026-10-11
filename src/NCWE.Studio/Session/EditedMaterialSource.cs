// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using System.Numerics;
using NCWE.Core;

namespace NCWE.Studio.Session;

// The archives' materials, with the meshes edited in place answered here: an edited drawable wears
// the materials of the game mesh it was made from (per part, the slot of the chunk the part came
// from), on its own UVs and vertices.
internal sealed class EditedMaterialSource(IMaterialSource inner) : IMaterialSource
{
    // Source: the game mesh; SurfaceParts: per drawn part, its part in the source's surface.
    public sealed record Entry(ulong Source, int[] SurfaceParts, Vector2[]?[] Uvs, int[] VertexCounts);
    private readonly ConcurrentDictionary<ulong, Entry> _entries = new();

    public void Set(ulong key, Entry entry) => _entries[key] = entry;
    public void Remove(ulong key) => _entries.TryRemove(key, out _);

    // Per appearance, the slot each drawn part of an edited drawable wears (the source mesh's slots,
    // through the parts they came from); null when the key is not an edited drawable.
    public IReadOnlyDictionary<string, string[]>? PartSlots(ulong key, Func<ulong, IReadOnlyDictionary<string, string[]>?> sourceSlots)
    {
        if (!_entries.TryGetValue(key, out var entry) || sourceSlots(entry.Source) is not { } slots) return null;
        return slots.ToDictionary(s => s.Key, s => entry.SurfaceParts.Select(p => p >= 0 && p < s.Value.Length ? s.Value[p] : "").ToArray(), StringComparer.Ordinal);
    }

    public async Task<MeshSurface?> ReadSurfaceAsync(ulong mesh, CancellationToken cancellation)
    {
        if (!_entries.TryGetValue(mesh, out var entry)) return await inner.ReadSurfaceAsync(mesh, cancellation).ConfigureAwait(false);
        var source = await inner.ReadSurfaceAsync(entry.Source, cancellation).ConfigureAwait(false);
        if (source is null) return null;
        var appearances = source.Appearances.ToDictionary(a => a.Key,
            a => entry.SurfaceParts.Select(p => p >= 0 && p < a.Value.Length ? a.Value[p] : null).ToArray(), StringComparer.Ordinal);
        return new MeshSurface(mesh, entry.Uvs, entry.VertexCounts, appearances);
    }

    public Task<TextureData?> ReadTextureAsync(ulong texture, int maximumSize, CancellationToken cancellation) => inner.ReadTextureAsync(texture, maximumSize, cancellation);
    public Task<DecalMaterial?> ReadDecalAsync(ulong material, CancellationToken cancellation) => inner.ReadDecalAsync(material, cancellation);
    public Task<MaskData?> ReadMaskAsync(ulong mask, int maximumSize, CancellationToken cancellation) => inner.ReadMaskAsync(mask, maximumSize, cancellation);
}
