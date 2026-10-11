// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using NCWE.Core;
using NCWE.Formats;

namespace NCWE.Studio;

// Headless preflight: around a center, converts every sector the editor could stream
// (any level whose streaming box lies within the radius) and every mesh their default
// variants use into the shared disk caches, nearest first. Nothing is kept in memory;
// the editor later reads the converted files (≈0.5 ms per mesh instead of decoding).
// Progress is written to stdout as one JSON object per line. The worker stops when its
// parent exits.
internal static class CacheWorker
{
    public static int Run(string[] args)
    {
        string? Value(string key) { int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            string game = Value("--game") ?? throw new ArgumentException("--game is required.");
            string state = Path.GetFullPath(Value("--state-dir") ?? throw new ArgumentException("--state-dir is required."));
            var center = (Value("--center") ?? "0,0,0").Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            float radius = float.Parse(Value("--radius") ?? "600", CultureInfo.InvariantCulture);
            int threads = int.Parse(Value("--threads") ?? "4", CultureInfo.InvariantCulture);
            long diskBudget = long.Parse(Value("--disk-budget") ?? (4L << 30).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            int parent = int.Parse(Value("--parent") ?? "0", CultureInfo.InvariantCulture);
            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            using var stop = new CancellationTokenSource();
            if (parent != 0) _ = WatchParent(parent, stop);
            return RunAsync(game, state, new(center[0], center[1], center[2]), radius, Math.Clamp(threads, 1, 32), diskBudget, stop.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { return 2; }
        catch (Exception ex) { Report(new { error = ex.Message }); return 1; }
    }

    private static async Task WatchParent(int pid, CancellationTokenSource stop)
    {
        try
        {
            using var parent = Process.GetProcessById(pid);
            await parent.WaitForExitAsync(stop.Token);
        }
        catch (ArgumentException) { }       // already gone
        catch (OperationCanceledException) { return; }
        stop.Cancel();
    }

    private static volatile string Phase = "convert";
    // Background work yields to the machine: above 90 % memory load, wait for 85 %.
    private static async Task WaitForMemory(CancellationToken cancellation)
    {
        if (SystemMemory.Load() <= .9) return;
        Phase = "waiting-memory";
        while (SystemMemory.Load() > .85) await Task.Delay(2000, cancellation);
        Phase = "convert";
    }

    private static readonly object Output = new();
    private static void Report(object value) { lock (Output) { Console.Out.WriteLine(JsonSerializer.Serialize(value)); Console.Out.Flush(); } }

    private static async Task<int> RunAsync(string game, string state, Vector3 center, float radius, int threads, long diskBudget, CancellationToken cancellation)
    {
        Report(new { phase = "index" });
        using var index = new GameArchiveIndex(game, includeExpansion: true);
        await index.BuildAsync(null, cancellation);
        var world = index.Worlds.Single(w => w.Path.EndsWith("\\03_night_city.streamingworld", StringComparison.OrdinalIgnoreCase));
        var cache = Path.Combine(state, "Cache");
        var catalog = await index.LoadWorldCatalogCachedAsync(world.Hash, new SnapshotCache(Path.Combine(cache, "Catalog-v1")), null, cancellation);
        var spatial = new SectorSpatialIndex(catalog.Descriptors);
        var source = new CachedStreamingSource(index, Path.Combine(cache, "Meshes-v1"), diskBudget, Path.Combine(cache, "Sectors-v1"), concurrent: true);
        var targets = spatial.Query(center, radius, 100_000, NeighborhoodPolicy.SupportsRuntime).Candidates
            .Select(d => (Descriptor: d, Distance: SectorSpatialIndex.DistanceSquared(center, d.GameBounds)))
            .Where(t => t.Distance <= (double)radius * radius)
            .OrderBy(t => t.Distance).ThenBy(t => t.Descriptor.Level)
            .Select(t => t.Descriptor).DistinctBy(d => d.Sector.Hash).ToArray();
        int sectorsDone = 0, meshesSeen = 0, meshesConverted = 0, failures = 0;
        var seen = new ConcurrentDictionary<ulong, byte>();
        var watch = Stopwatch.StartNew();
        using var ticker = new Timer(_ => Report(new { phase = Phase, sectorsDone = Volatile.Read(ref sectorsDone), sectorsTotal = targets.Length,
            meshesSeen = Volatile.Read(ref meshesSeen), meshesConverted = Volatile.Read(ref meshesConverted), failures = Volatile.Read(ref failures),
            seconds = Math.Round(watch.Elapsed.TotalSeconds, 1) }), null, 0, 500);
        await Parallel.ForEachAsync(targets, new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = cancellation }, async (descriptor, token) =>
        {
            await WaitForMemory(token);
            try
            {
                var sector = NeighborhoodPolicy.DefaultVariant(await source.ReadSectorAsync(descriptor.Sector.Hash, 96L << 20, token), descriptor);
                foreach (var mesh in sector.Placements.SelectMany(p => p.Meshes()))
                {
                    if (mesh.Hash == 0 || !seen.TryAdd(mesh.Hash, 0)) continue;
                    Interlocked.Increment(ref meshesSeen);
                    try { if (await source.WarmMeshAsync(mesh.Hash, 96L << 20, token)) Interlocked.Increment(ref meshesConverted); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { Interlocked.Increment(ref failures); }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Interlocked.Increment(ref failures); }
            Interlocked.Increment(ref sectorsDone);
        });
        Report(new { phase = "done", sectorsDone, sectorsTotal = targets.Length, meshesSeen, meshesConverted, failures, seconds = Math.Round(watch.Elapsed.TotalSeconds, 1) });
        return 0;
    }
}
