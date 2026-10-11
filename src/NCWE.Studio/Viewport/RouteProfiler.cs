// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Numerics;
using NCWE.Core;
using NCWE.Render;
using NCWE.Studio.Session;

namespace NCWE.Studio.Viewport;

public sealed record FrameStatistics(int Frames, double Seconds, double MeanMs, double P50Ms, double P95Ms, double P99Ms, double MaxMs,
    int Over33Ms, int Over50Ms, int Over100Ms);
public sealed record RouteReport(FrameStatistics FrameIntervals, FrameStatistics CpuFrame, double DistanceMeters, long PeakPrivateBytes,
    long PeakManagedBytes, long PeakMeshGpuBytes, long PeakStreamingCpuBytes, int PeakPendingMeshes, int PeakDeferredMeshes, int PeakResidentSectors,
    long CacheHits, double GcPauseMs, int Gen2Collections, int PeakVisible, string Adapter, int ViewportWidth, int ViewportHeight,
    GcBreakdown? Gc = null, double AllocatedMegabytes = 0, long PeakCommittedBytes = 0, double PeakMemoryLoadPercent = 0, object? Allocations = null);
// Collections seen during the route by kind: count, total and longest pause.
public sealed record GcBreakdown(int Ephemeral, double EphemeralMs, double EphemeralMaxMs, int Background, double BackgroundMs, double BackgroundMaxMs,
    int Blocking, double BlockingMs, double BlockingMaxMs, int Gen0, int Gen1);

// Flies the camera along fixed waypoints at constant speed, one step per rendered
// frame, and records frame intervals and resource peaks for milestone 4/5 evidence.
internal sealed class RouteProfiler
{
    private readonly Vector3[] _points; // view space
    private readonly float _speed;
    private int _leg;
    private float _along;
    private readonly List<double> _intervals = [], _cpu = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _last = -1, _distance;
    private long _peakPrivate, _peakManaged, _peakGpu, _peakStreamCpu, _peakCommitted;
    private double _peakLoad;
    private int _peakPending, _peakDeferred, _peakSectors, _peakVisible;
    private readonly TimeSpan _pauseAtStart = GC.GetTotalPauseDuration();
    private readonly int _gen2AtStart = GC.CollectionCount(2), _gen1AtStart = GC.CollectionCount(1), _gen0AtStart = GC.CollectionCount(0);
    private readonly long _allocatedAtStart = GC.GetTotalAllocatedBytes(false);
    private readonly (int Count, double Ms)[] _gcAtStart = [(GcPauses.Ephemeral.Count, GcPauses.Ephemeral.TotalMs), (GcPauses.Background.Count, GcPauses.Background.TotalMs), (GcPauses.Blocking.Count, GcPauses.Blocking.TotalMs)];
    private readonly double[] _gcMax = new double[3], _gcSeen = new double[3];
    private int _sampleCountdown;
    public bool Done { get; private set; }
    public double Progress => _points.Length < 2 ? 1 : (_leg + (double)_along / Math.Max(1e-3f, Vector3.Distance(_points[Math.Min(_leg, _points.Length - 2)], _points[Math.Min(_leg + 1, _points.Length - 1)]))) / (_points.Length - 1);

    public RouteProfiler(IEnumerable<Vector3> gamePoints, float speed)
    {
        _points = gamePoints.Select(Coordinates.GameToView).ToArray();
        _speed = speed;
        if (_points.Length < 2) throw new ArgumentException("A route needs at least two points.");
    }

    public void Step(SceneCamera camera, float dt)
    {
        if (Done) return;
        float move = _speed * dt; _distance += move;
        while (move > 0 && _leg < _points.Length - 1)
        {
            var a = _points[_leg]; var b = _points[_leg + 1]; float length = Vector3.Distance(a, b);
            float remaining = length - _along;
            if (move < remaining) { _along += move; move = 0; }
            else { move -= remaining; _leg++; _along = 0; }
        }
        if (_leg >= _points.Length - 1) { Done = true; camera.SetEye(_points[^1]); return; }
        var from = _points[_leg]; var to = _points[_leg + 1];
        var direction = Vector3.Normalize(to - from);
        camera.SetEye(from + direction * _along);
        // Face the travel direction, slightly downwards (Forward = -Offset).
        camera.Yaw = MathF.Atan2(-direction.X, -direction.Z); camera.Pitch = .25f;
    }

    public void Record(double cpuMs, D3DRenderer renderer, StudioSession session)
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        if (_last >= 0) _intervals.Add(now - _last);
        _last = now; _cpu.Add(cpuMs);
        _peakGpu = Math.Max(_peakGpu, renderer.MeshGpuBytes); _peakVisible = Math.Max(_peakVisible, renderer.VisiblePlacements);
        if (session.StreamMetrics is { } m)
        {
            _peakPending = Math.Max(_peakPending, m.PendingMeshes); _peakDeferred = Math.Max(_peakDeferred, m.DeferredMeshes);
            _peakSectors = Math.Max(_peakSectors, m.ResidentSectors); _peakStreamCpu = Math.Max(_peakStreamCpu, m.SectorBytes + m.MeshBytes + m.WorkerReservedBytes);
        }
        // The longest pause of each kind during the route (per-frame growth of the totals).
        var kinds = GcPauses.All;
        for (int i = 0; i < 3; i++)
        {
            double total = kinds[i].TotalMs - _gcAtStart[i].Ms;
            _gcMax[i] = Math.Max(_gcMax[i], total - _gcSeen[i]); _gcSeen[i] = total;
        }
        if (--_sampleCountdown <= 0)
        {
            _sampleCountdown = 30;
            _peakPrivate = Math.Max(_peakPrivate, SystemMemory.PrivateBytes()); _peakManaged = Math.Max(_peakManaged, GC.GetTotalMemory(false));
            // What the GC holds from the system, and the machine's memory load as the GC sees it
            // (past its high threshold, collections turn into blocking compacting ones).
            var gc = GC.GetGCMemoryInfo();
            _peakCommitted = Math.Max(_peakCommitted, gc.TotalCommittedBytes);
            if (gc.TotalAvailableMemoryBytes > 0) _peakLoad = Math.Max(_peakLoad, 100.0 * gc.MemoryLoadBytes / gc.TotalAvailableMemoryBytes);
        }
    }

    public RouteReport Report(D3DRenderer renderer, StudioSession session, int width, int height) => new(
        Statistics(_intervals), Statistics(_cpu), _distance, _peakPrivate, _peakManaged, _peakGpu, _peakStreamCpu, _peakPending, _peakDeferred, _peakSectors,
        session.CacheHits, (GC.GetTotalPauseDuration() - _pauseAtStart).TotalMilliseconds, GC.CollectionCount(2) - _gen2AtStart, _peakVisible,
        renderer.AdapterName, width, height,
        new(GcPauses.Ephemeral.Count - _gcAtStart[0].Count, GcPauses.Ephemeral.TotalMs - _gcAtStart[0].Ms, _gcMax[0],
            GcPauses.Background.Count - _gcAtStart[1].Count, GcPauses.Background.TotalMs - _gcAtStart[1].Ms, _gcMax[1],
            GcPauses.Blocking.Count - _gcAtStart[2].Count, GcPauses.Blocking.TotalMs - _gcAtStart[2].Ms, _gcMax[2],
            GC.CollectionCount(0) - _gen0AtStart, GC.CollectionCount(1) - _gen1AtStart),
        (GC.GetTotalAllocatedBytes(false) - _allocatedAtStart) / 1048576.0, _peakCommitted, Math.Round(_peakLoad, 1), AllocationTrace.Snapshot(false));

    private static FrameStatistics Statistics(List<double> values)
    {
        if (values.Count == 0) return new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var sorted = values.Order().ToArray();
        double P(double p) => sorted[(int)Math.Min(sorted.Length - 1, Math.Ceiling(p * sorted.Length) - 1)];
        return new(sorted.Length, values.Sum() / 1000, values.Average(), P(.5), P(.95), P(.99), sorted[^1],
            values.Count(v => v > 33.4), values.Count(v => v > 50), values.Count(v => v > 100));
    }
}
