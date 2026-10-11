// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using System.Diagnostics.Tracing;

namespace NCWE.Studio.Session;

// Diagnostic, on with NCWE_ALLOC_TRACE=1: what the process allocates, by type, from the
// runtime's allocation ticks (one event per ~100 KB allocated on a heap, naming the type
// that crossed the mark). The sums are a sample, proportional to the real volumes.
internal sealed class AllocationTrace : EventListener
{
    private static AllocationTrace? _instance;
    private readonly ConcurrentDictionary<(string Type, int Kind), long[]> _types = new();

    public static void StartIfRequested()
    {
        if (Environment.GetEnvironmentVariable("NCWE_ALLOC_TRACE") == "1") _instance ??= new AllocationTrace();
    }

    protected override void OnEventSourceCreated(EventSource source)
    {
        // GC keyword (0x1) at verbose level: the allocation ticks.
        if (source.Name == "Microsoft-Windows-DotNETRuntime") EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1);
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (e.EventName is null || !e.EventName.StartsWith("GCAllocationTick", StringComparison.Ordinal) || e.Payload is null || e.PayloadNames is null) return;
        string type = "?"; int kind = 0; long amount = 0;
        for (int i = 0; i < e.PayloadNames.Count; i++)
            switch (e.PayloadNames[i])
            {
                case "TypeName": type = e.Payload[i] as string ?? "?"; break;
                case "AllocationKind": kind = Convert.ToInt32(e.Payload[i]); break;
                case "AllocationAmount64": amount = Convert.ToInt64(e.Payload[i]); break;
                case "AllocationAmount" when amount == 0: amount = Convert.ToInt64(e.Payload[i]); break;
            }
        var totals = _types.GetOrAdd((type, kind), _ => new long[2]);
        Interlocked.Add(ref totals[0], amount); Interlocked.Increment(ref totals[1]);
    }

    // The largest types since the last reset: MB (sampled), ticks, kind (small, large, pinned).
    public static object? Snapshot(bool reset)
    {
        if (_instance is not { } trace) return null;
        var top = trace._types.Select(t => new { type = t.Key.Type, kind = t.Key.Kind switch { 1 => "large", 2 => "pinned", _ => "small" }, mb = Math.Round(t.Value[0] / 1048576.0, 1), ticks = t.Value[1] })
            .OrderByDescending(t => t.mb).Take(40).ToArray();
        if (reset) trace._types.Clear();
        return top;
    }
}
