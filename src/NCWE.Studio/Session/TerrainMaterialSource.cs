// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using System.Numerics;
using NCWE.Core;
using NCWE.Core.Terrain;

namespace NCWE.Studio.Session;

// The archives' materials, with the terrain cells answered here:
// - a cell keeps its mask, normal map and tint inside its mesh file, with no archive entry of their
//   own: once a cell's surface is read, its texture hashes lead back to the cell and are read from it;
// - a cell the project edits wears its game material on the editor's mesh (its parts, UVs from
//   the positions as the game derives them) with the project's paint; it is lit by its mesh's own
//   normals (a 1 m grid, always in step with the brush), not the game's normal map.
internal sealed class TerrainMaterialSource(IMaterialSource inner, StudioSession session, Func<ulong, TerrainCell?> cellOfMesh,
    Func<TerrainCell, (MaskData? Mask, TextureData? Normal, TextureData? Tint)> readCell) : IMaterialSource
{
    private enum Embedded { Mask, Normal, Tint }
    private readonly ConcurrentDictionary<ulong, (TerrainCell Cell, Embedded Kind)> _embedded = new();
    private readonly ConcurrentDictionary<TerrainCell, Lazy<(MaskData? Mask, TextureData? Normal, TextureData? Tint)>> _cells = new();

    public async Task<MeshSurface?> ReadSurfaceAsync(ulong mesh, CancellationToken cancellation)
    {
        var original = await inner.ReadSurfaceAsync(mesh, cancellation).ConfigureAwait(false);
        var material = original?.Appearances.Values.SelectMany(m => m).FirstOrDefault(m => m is { Kind: SurfaceKind.Multilayer, GeneratedUv: var g } && g != default);
        if (material is null || cellOfMesh(mesh) is not { } cell) return original;
        _embedded[material.Mask] = (cell, Embedded.Mask);
        if (material.GlobalNormal != 0) _embedded[material.GlobalNormal] = (cell, Embedded.Normal);
        if (material.BaseColor != 0) _embedded[material.BaseColor] = (cell, Embedded.Tint);
        if (!session.TerrainSurfaces.TryGetValue(mesh, out var state)) return original;
        var edited = material with { Key = $"{material.Key}:ncwe-terrain:{state.Cell}", Mask = state.Mask, GlobalNormal = 0 };
        var gen = material.GeneratedUv;
        // Positions are relative to the cell centre (the node's origin), as the game's UVs are.
        var uvs = state.Positions.Select(part => part.Select(l => new Vector2(l.X * gen.X + gen.Z, 1 - (l.Y * gen.Y + gen.W))).ToArray()).ToArray();
        var parts = Enumerable.Repeat(edited, state.Counts.Length).Select(m => (SurfaceMaterial?)m).ToArray();
        return new MeshSurface(mesh, uvs.Select(u => (Vector2[]?)u).ToArray(), state.Counts, new Dictionary<string, SurfaceMaterial?[]> { ["default"] = parts });
    }

    private (MaskData? Mask, TextureData? Normal, TextureData? Tint) Cell(TerrainCell cell)
    {
        var value = _cells.GetOrAdd(cell, c => new(() => readCell(c))).Value;
        // A few dozen cells are around the camera; older ones are read again if needed.
        if (_cells.Count > 96) foreach (var key in _cells.Keys.Take(_cells.Count - 64).ToArray()) _cells.TryRemove(key, out _);
        return value;
    }

    public Task<TextureData?> ReadTextureAsync(ulong texture, int maximumSize, CancellationToken cancellation)
    {
        if (_embedded.TryGetValue(texture, out var e) && e.Kind != Embedded.Mask)
            return Task.Run(() => e.Kind == Embedded.Normal ? Cell(e.Cell).Normal : Cell(e.Cell).Tint, cancellation);
        return inner.ReadTextureAsync(texture, maximumSize, cancellation);
    }
    public Task<DecalMaterial?> ReadDecalAsync(ulong material, CancellationToken cancellation) => inner.ReadDecalAsync(material, cancellation);

    public Task<MaskData?> ReadMaskAsync(ulong mask, int maximumSize, CancellationToken cancellation)
    {
        if (session.TerrainMasks.TryGetValue(mask, out var data)) return Task.FromResult<MaskData?>(data);
        if (_embedded.TryGetValue(mask, out var e) && e.Kind == Embedded.Mask) return Task.Run(() => Cell(e.Cell).Mask, cancellation);
        return inner.ReadMaskAsync(mask, maximumSize, cancellation);
    }
}
