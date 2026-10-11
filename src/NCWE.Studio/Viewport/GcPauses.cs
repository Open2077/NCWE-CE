// SPDX-License-Identifier: GPL-3.0-only
using NCWE.Studio.Session;

namespace NCWE.Studio.Viewport;

// Garbage collection pauses by kind, polled once per frame: ephemeral (generations 0/1),
// background generation 2 (short suspensions around a concurrent mark) and blocking
// generation 2. The runtime reports the newest collection of each kind, so several of one
// kind between two polls count as one.
internal static class GcPauses
{
    internal sealed class Kind(GCKind kind, string name)
    {
        public GCKind Value { get; } = kind;
        public string Name { get; } = name;
        public long LastIndex = -1;
        public int Count;
        public double TotalMs, MaxMs;
    }
    public static readonly Kind Ephemeral = new(GCKind.Ephemeral, "gc.eph"), Background = new(GCKind.Background, "gc.bg"), Blocking = new(GCKind.FullBlocking, "gc.full");
    public static readonly Kind[] All = [Ephemeral, Background, Blocking];
    private static int _collections = -1;

    public static void Poll()
    {
        int collections = GC.CollectionCount(0); // every collection counts generation 0
        if (collections == _collections) return;
        bool first = _collections < 0; _collections = collections;
        Check(Ephemeral, first); Check(Background, first); Check(Blocking, first);
    }

    private static void Check(Kind kind, bool first)
    {
        var info = GC.GetGCMemoryInfo(kind.Value);
        if (info.Index == kind.LastIndex) return;
        kind.LastIndex = info.Index;
        if (first || info.Index == 0) return;
        double pause = 0;
        foreach (var p in info.PauseDurations) pause += p.TotalMilliseconds;
        kind.Count++; kind.TotalMs += pause; kind.MaxMs = Math.Max(kind.MaxMs, pause);
        FrameProfile.Record(kind.Name, pause);
        // Why a blocking collection happened is not reported; the machine's memory load at that
        // moment (past the GC's high threshold it compacts in blocking collections) tells most.
        if (kind == Blocking && info.TotalAvailableMemoryBytes > 0)
        {
            FrameProfile.Record("gc.full.load%", 100.0 * info.MemoryLoadBytes / info.TotalAvailableMemoryBytes);
            FrameProfile.Record("gc.full.high%", 100.0 * info.HighMemoryLoadThresholdBytes / info.TotalAvailableMemoryBytes);
            FrameProfile.Record("gc.full.heapMB", info.HeapSizeBytes / 1048576.0);
            FrameProfile.Record("gc.full.compacted", info.Compacted ? 1 : 0);
        }
    }
}
