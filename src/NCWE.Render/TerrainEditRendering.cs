// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using NCWE.Core;
using Vortice.Direct3D11;

namespace NCWE.Render;

// Live terrain sculpting: an edited terrain cell's mesh changes several times a second. Replacing it
// through SetWorld would drop it (and its game materials) until the new upload lands, so the ground
// would flicker under the brush. Instead its buffers are swapped in place (same parts, same vertex
// counts: only heights and the triangles cut out change), and its paint mask and normal texture
// are read again behind the old ones, which stay drawn until the new ones arrive.
public sealed partial class D3DRenderer
{
    private readonly ConcurrentQueue<((ulong, bool) Key, ID3D11ShaderResourceView? View, long Bytes)> _reloadArrivals = new();
    private readonly Dictionary<(ulong, bool), bool> _reloading = [];

    // UI thread. False when the mesh is not resident yet or its layout changed (SetWorld then).
    public bool UpdateWorldMeshInPlace(ulong hash, MeshAsset asset)
    {
        if (!_worldMeshes.TryGetValue(hash, out var parts) || parts.Count != asset.Parts.Length) return false;
        if (!_worldVertexCounts.TryGetValue(hash, out var counts) || counts.Length != asset.Parts.Length) return false;
        for (int i = 0; i < counts.Length; i++)
            if (counts[i] != asset.Parts[i].Vertices.Length || parts[i].LodMask != asset.Parts[i].LodMask) return false;
        var fresh = new GpuPart?[parts.Count];
        try
        {
            for (int i = 0; i < parts.Count; i++)
            {
                var vertices = Device.CreateBuffer(asset.Parts[i].Vertices, BindFlags.VertexBuffer);
                ID3D11Buffer indices;
                try { indices = Device.CreateBuffer(asset.Parts[i].Indices, BindFlags.IndexBuffer); }
                catch { vertices.Dispose(); throw; }
                fresh[i] = parts[i] with { Vertices = vertices, Indices = indices, Count = (uint)asset.Parts[i].Indices.Length };
            }
        }
        catch
        {
            foreach (var part in fresh) { part?.Vertices.Dispose(); part?.Indices.Dispose(); }
            throw;
        }
        for (int i = 0; i < parts.Count; i++)
        {
            // The UV stream and baked surface are shared with the new part: only the geometry goes.
            parts[i].Vertices.Dispose(); parts[i].Indices.Dispose();
            parts[i] = fresh[i]!;
        }
        foreach (var group in parts.GroupBy(p => p.LodMask)) _lodParts[(hash, group.Key)] = group.ToArray();
        _worldIdentities[hash] = (asset.SourceHash, _worldIdentities.TryGetValue(hash, out var identity) ? identity.Bytes : asset.CpuBytes);
        _worldBounds[hash] = asset.Bounds;
        // A still camera reuses the last cull, whose batches hold the parts just disposed: the cell
        // would not be drawn until the stroke ends (the ground under the brush went flat grey).
        _cullValid = false;
        return true;
    }

    // UI thread: reads a game texture (mask: a multilayer mask) from the material source again. The
    // current one stays in use until the new one is ready (PumpReloadedTextures); one reload runs
    // at a time per texture, a request during it starts another when it ends. A texture never
    // loaded needs nothing: its first load reads the current data.
    public void ReloadTexture(ulong hash, bool mask)
    {
        var key = (hash, mask);
        if (MaterialSource is not { } source || !_textures.TryGetValue(key, out var entry) || entry.View is null) return;
        if (_reloading.ContainsKey(key)) { _reloading[key] = true; return; }
        _reloading[key] = false;
        int limit = TextureLimit; var token = _materialCancel.Token;
        _ = Task.Run(async () =>
        {
            ID3D11ShaderResourceView? view = null; long bytes = 0;
            try
            {
                if (mask) { if (await source.ReadMaskAsync(hash, Math.Min(limit, 512), token).ConfigureAwait(false) is { } m) (view, bytes) = CreateMaskTexture(m); }
                else if (await source.ReadTextureAsync(hash, limit, token).ConfigureAwait(false) is { } data) (view, bytes) = CreateGameTexture(data);
            }
            catch (Exception) { view?.Dispose(); view = null; }
            _reloadArrivals.Enqueue((key, view, bytes));
        });
    }

    // UI thread, once per frame: the reloaded textures replace the old ones.
    public void PumpReloadedTextures()
    {
        while (_reloadArrivals.TryDequeue(out var arrival))
        {
            bool again = _reloading.Remove(arrival.Key, out var requested) && requested;
            if (arrival.View is { } view)
            {
                if (_textures.TryGetValue(arrival.Key, out var entry) && entry.View is not null)
                {
                    entry.View.Dispose();
                    _textureBytes += arrival.Bytes - entry.Bytes;
                    entry.View = view; entry.Bytes = arrival.Bytes; entry.LastUsed = _frameNumber;
                    // Materials read their views again before their next draw.
                    _evictionGeneration++;
                }
                else view.Dispose();
            }
            if (again) ReloadTexture(arrival.Key.Item1, arrival.Key.Item2);
        }
    }
}
