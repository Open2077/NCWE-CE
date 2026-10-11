// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using Vector3 = System.Numerics.Vector3;
using Quaternion = System.Numerics.Quaternion;
using WolvenKit.RED4.Archive.Buffer;
using WolvenKit.RED4.Archive.CR2W;
using WolvenKit.RED4.Archive.IO;
using WolvenKit.RED4.Types;

namespace NCWE.Formats;

public readonly record struct FoliageStance(Vector3 Position, float Scale, Quaternion Rotation);

// A compiled foliage resource (.cfoliage) as the editor uses it: the plants' stances and the
// buckets grouping them. The city's are files of the archives, shared by several sectors; outside
// it (the Badlands) each sector carries the ones its plant nodes name inside itself, as embedded
// resources that no archive lists: both are read here.
public sealed record FoliagePopulation(FoliageStance[] Stances, (int Sub, int Count)[] Buckets)
{
    public static FoliagePopulation? Of(RedBaseClass? resource) => resource is worldFoliageCompiledResource { DataBuffer.Buffer.Data: FoliageBuffer buffer }
        ? new(buffer.Populations.Select(p => new FoliageStance(new(p.Position.X, p.Position.Y, p.Position.Z), p.Scale,
                Quaternion.Normalize(new(p.Rotation.X, p.Rotation.Y, p.Rotation.Z, p.Rotation.W)))).ToArray(),
            buffer.Buckets.Select(b => ((int)(uint)b.PopulationSubIndex, (int)(uint)b.PopulationCount)).ToArray())
        : null;

    // The resource a file (a streaming sector) carries inside itself under this path hash.
    public static FoliagePopulation? Carried(CR2WFile holder, ulong hash)
    {
        foreach (var embedded in holder.EmbeddedFiles)
            if (embedded.FileName.GetRedHash() == hash) return Of(embedded.Content);
        return null;
    }
}

public sealed partial class GameArchiveIndex
{
    private readonly ConcurrentDictionary<ulong, FoliageStance[]?> _foliage = new();

    // Compiled foliage populations of the archives, shared by several sectors; converted once to
    // owned arrays so no WolvenKit objects are retained.
    internal FoliageStance[]? FoliageStances(ulong hash) => hash == 0 ? null : _foliage.GetOrAdd(hash, h =>
    {
        try { return ReadFoliagePopulation(h)?.Stances; }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or InvalidCastException) { return null; }
    });

    // The populations a sector's plant nodes refer to: the archives' file, else the resource the
    // sector carries itself (read once per sector, not kept: the plants become placements).
    internal Func<ulong, FoliageStance[]?> FoliageOf(CR2WFile sector)
    {
        Dictionary<ulong, FoliageStance[]?>? carried = null;
        return hash =>
        {
            if (FoliageStances(hash) is { } shared) return shared;
            if (hash == 0) return null;
            carried ??= [];
            if (!carried.TryGetValue(hash, out var stances)) carried[hash] = stances = FoliagePopulation.Carried(sector, hash)?.Stances;
            return stances;
        };
    }

    // A compiled foliage resource of the archives (null when it is not a file of theirs or cannot be read).
    internal FoliagePopulation? ReadFoliagePopulation(ulong hash)
    {
        var entry = EntryOrNull(hash);
        if (entry is null) return null;
        using var data = ExtractEntry(entry, default);
        using var reader = new CR2WReader(data);
        if (reader.ReadFile(out var file) != EFileReadErrorCodes.NoError || file is null) return null;
        using (file) return FoliagePopulation.Of(file.RootChunk);
    }

    // A compiled foliage file of the archives to write another one from (its version and buffer
    // layout): `preferred` when the archives hold it, else the first by path.
    internal ulong FoliageTemplate(ulong preferred)
    {
        if (preferred != 0 && EntryOrNull(preferred) is not null) return preferred;
        var first = _entries.Values.Where(e => e.Extension.Equals(".cfoliage", StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.FileName, StringComparer.Ordinal).FirstOrDefault();
        return first?.Key ?? 0;
    }
}
