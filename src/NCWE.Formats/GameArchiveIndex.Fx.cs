// SPDX-License-Identifier: GPL-3.0-only
using NCWE.Core.Fx;
using NCWE.Formats.Fx;
using WolvenKit.RED4.Types;

namespace NCWE.Formats;

// The game's particle systems and effects, read once each.
public sealed partial class GameArchiveIndex
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, object?> _fx = new(StringComparer.OrdinalIgnoreCase);

    // A particle system of the game (cached), or null when it is not there or cannot be read.
    public FxSystem? ReadParticleSystem(string depotPath) => _fx.GetOrAdd("p|" + depotPath, _ =>
    {
        try
        {
            using var file = ReadFile(depotPath);
            return file?.RootChunk is CParticleSystem system ? ParticleReader.ReadSystem(system, depotPath, ReadMaterialInstance) : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidCastException or ArgumentException or NullReferenceException) { return null; }
    }) as FxSystem;

    public FxEffect? ReadEffect(string depotPath) => _fx.GetOrAdd("e|" + depotPath, _ =>
    {
        try
        {
            using var file = ReadFile(depotPath);
            return file?.RootChunk is worldEffect effect ? ParticleReader.ReadEffect(effect, depotPath) : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidCastException or ArgumentException or NullReferenceException) { return null; }
    }) as FxEffect;

    private CMaterialInstance? ReadMaterialInstance(string depotPath)
    {
        try { using var file = ReadFile(depotPath); return file?.RootChunk as CMaterialInstance; }
        catch (Exception ex) when (ex is InvalidDataException or IOException) { return null; }
    }
}
