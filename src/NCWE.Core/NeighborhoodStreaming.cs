// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Numerics;

namespace NCWE.Core;

public sealed record StreamingBudgets(long CpuBytes = 1024L * 1024 * 1024, long GpuBytes = 512L * 1024 * 1024,
    long WorkerReservationBytes = 384L * 1024 * 1024, long MaximumMeshBytes = 32L * 1024 * 1024,
    long MaximumSourceBytes = 32L * 1024 * 1024, long MaximumSectorBytes = 64L * 1024 * 1024,
    int MaximumSectors = 24, int MaximumPendingMeshes = 128, int MaximumWorkers = 1)
{
    public void Validate()
    {
        if (CpuBytes <= WorkerReservationBytes || WorkerReservationBytes <= 0 || GpuBytes <= 0 || MaximumMeshBytes <= 0
            || MaximumMeshBytes > GpuBytes || MaximumMeshBytes > CpuBytes - WorkerReservationBytes
            || MaximumSourceBytes <= 0 || MaximumSectorBytes <= 0 || MaximumSectors is < 1 or > 4096 || MaximumPendingMeshes is < 1 or > 4096 || MaximumWorkers is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(CpuBytes), "Invalid streaming budgets.");
    }
}

public interface IStreamingSource
{
    Task<SectorSnapshot> ReadSectorAsync(ulong hash, long maximumSourceBytes, CancellationToken cancellation);
    Task<MeshAsset> ReadMeshAsync(ulong hash, long maximumMeshBytes, long maximumSourceBytes, CancellationToken cancellation);
}

public static class NeighborhoodPolicy
{
    // Near levels are additive detail (0) and structure (1). Distant proxy levels
    // 2+ are excluded until their replacement relationships are supported.
    public static bool Supports(StreamingDescriptor d) => d.Sector.Resolved && d.HasValidBounds && !d.IsQuestDependent
        && d.BlockIndex.Group == "Base" && d.Level <= 1 && d.Category is "Exterior" or "Interior";

    // keep: sectors currently resident. They stay eligible up to RetainFactor × radius
    // (hysteresis), so small camera moves do not evict and reload the same sectors.
    public const float RetainFactor = 1.3f;
    public static ImmutableArray<StreamingDescriptor> Choose(SectorSpatialIndex spatial, Vector3 position, float radius, int maximum,
        IReadOnlySet<ulong>? keep = null)
    {
        double squaredRadius = (double)radius * radius;
        var candidates = spatial.Query(position, keep is null ? radius : radius * RetainFactor, 2048, Supports).Candidates
            .Where(d => keep is not null && keep.Contains(d.Sector.Hash) || SectorSpatialIndex.DistanceSquared(position, d.GameBounds) <= squaredRadius)
            .ToImmutableArray();
        // Streaming boxes include margins, so many zero-distance boxes overlap.
        // Center distance breaks these ties instead of archive order selecting a
        // far corner of the neighborhood. Reserve space for both near levels.
        double Score(StreamingDescriptor d) => SectorSpatialIndex.DistanceSquared(position, d.GameBounds)
            + Vector3.DistanceSquared(position, d.GameBounds.Center) * .001;
        var ordered = candidates.OrderBy(Score).ThenBy(d => d.Sector.Hash).ToArray();
        var chosen = ordered.Where(d => d.Level == 0).Take((maximum + 1) / 2)
            .Concat(ordered.Where(d => d.Level == 1).Take(maximum / 2)).DistinctBy(d => d.Sector.Hash).ToList();
        foreach (var d in ordered)
        { if (chosen.Count == maximum) break; if (chosen.All(c => c.Sector.Hash != d.Sector.Hash)) chosen.Add(d); }
        return chosen.OrderBy(Score).ThenBy(d => d.Sector.Hash).Take(maximum).ToImmutableArray();
    }

    // Distant levels 2+ are coarser proxies; like the runtime, load those whose streaming
    // box contains the camera. Their proxy nodes hide near the camera (NearHideDistance).
    public static bool SupportsDistant(StreamingDescriptor d) => d.Sector.Resolved && d.HasValidBounds && !d.IsQuestDependent
        && d.BlockIndex.Group == "Base" && d.Level >= 2 && d.Category == "Exterior";

    public static ImmutableArray<StreamingDescriptor> ChooseDistant(SectorSpatialIndex spatial, Vector3 position, int maximum)
        => spatial.Query(position, 0, Math.Max(1, maximum * 2), SupportsDistant).Candidates
            .OrderBy(d => d.Level).ThenBy(d => Vector3.DistanceSquared(position, d.GameBounds.Center)).ThenBy(d => d.Sector.Hash)
            .DistinctBy(d => d.Sector.Hash).Take(maximum).ToImmutableArray();

    // Runtime-faithful selection: RED4 streams a sector when the camera is inside its
    // streaming box (boxes already include each level's visibility margin). Every such
    // Base Exterior/Interior sector is chosen, at any level; "prefetch" adds levels 0-1
    // sectors within that extra distance so nearby content is ready before arrival.
    public static bool SupportsRuntime(StreamingDescriptor d) => d.Sector.Resolved && d.HasValidBounds && !d.IsQuestDependent
        && d.BlockIndex.Group is "Base" or "EP1" && d.Category is "Exterior" or "Interior";

    public static ImmutableArray<StreamingDescriptor> ChooseRuntime(SectorSpatialIndex spatial, Vector3 position, float prefetch, int maximum,
        IReadOnlySet<ulong>? keep = null)
    {
        float reach = Math.Max(0, prefetch) * (keep is null ? 1 : RetainFactor);
        double squaredPrefetch = (double)prefetch * prefetch;
        var chosen = new List<(StreamingDescriptor D, double Distance)>();
        foreach (var d in spatial.Query(position, reach, 100_000, SupportsRuntime).Candidates)
        {
            double distance = SectorSpatialIndex.DistanceSquared(position, d.GameBounds);
            bool contains = distance == 0;
            bool near = d.Level <= 1 && (distance <= squaredPrefetch || (keep is not null && keep.Contains(d.Sector.Hash)));
            if (contains || near) chosen.Add((d, distance));
        }
        // Containing sectors first (what the game shows here), then prefetch by distance.
        return chosen.OrderBy(c => c.Distance > 0).ThenBy(c => c.D.Level).ThenBy(c => c.Distance).ThenBy(c => c.D.Sector.Hash)
            .Select(c => c.D).DistinctBy(d => d.Sector.Hash).Take(maximum).ToImmutableArray();
    }

    // An area kept loaded whatever the camera does next: every sector the game would stream from
    // some position within radius of the centre (its streaming box reaches that sphere), at any
    // level. Nearest first, so a budget that cuts the list keeps what surrounds the centre.
    public static ImmutableArray<StreamingDescriptor> ChooseArea(SectorSpatialIndex spatial, Vector3 centre, float radius, int maximum)
    {
        radius = Math.Max(0, radius);
        double limit = (double)radius * radius;
        var chosen = new List<(StreamingDescriptor D, double Distance)>();
        foreach (var d in spatial.Query(centre, radius, 100_000, SupportsRuntime).Candidates)
        {
            double distance = SectorSpatialIndex.DistanceSquared(centre, d.GameBounds);
            if (distance <= limit) chosen.Add((d, distance));
        }
        return chosen.OrderBy(c => c.Distance).ThenBy(c => c.D.Level).ThenBy(c => c.D.Sector.Hash)
            .Select(c => c.D).DistinctBy(d => d.Sector.Hash).Take(Math.Max(0, maximum)).ToImmutableArray();
    }

    public static SectorSnapshot DefaultVariant(SectorSnapshot sector, StreamingDescriptor descriptor) => Variant(sector, descriptor, VariantSelection.Default);

    // The node ranges of a sector that are on: the game's defaults, plus the variants the selection adds.
    private static HashSet<int> EnabledRanges(SectorSnapshot sector, StreamingDescriptor descriptor, VariantSelection selection)
    {
        // Unnamed range zero is the base range. Do not force it on if a
        // descriptor explicitly assigns it to a disabled variant.
        var enabled = new HashSet<int>();
        if (!descriptor.Variants.Any(v => v.RangeIndex == 0)) enabled.Add(0);
        if (sector.PersistentRangeIndex >= 0) enabled.Add(sector.PersistentRangeIndex);
        uint ranges = (uint)Math.Max(1, sector.RangeStarts.Length);
        foreach (var v in descriptor.Variants)
        {
            if (v.RangeIndex >= ranges) continue;
            // A variant the user asked for is shown whatever its parents' state.
            if (selection.Shows(v.Name)) { enabled.Add((int)v.RangeIndex); continue; }
            if (!v.EnabledByDefault) continue;
            var path = new HashSet<uint> { v.Id }; uint parent = v.ParentId; bool active = true;
            while (parent != 0)
            {
                if (!path.Add(parent)) { active = false; break; }
                var ancestor = descriptor.Variants.FirstOrDefault(a => a.NodeRef == v.NodeRef && a.Id == parent);
                if (ancestor is null || !ancestor.EnabledByDefault) { active = false; break; }
                parent = ancestor.ParentId;
            }
            if (active) enabled.Add((int)v.RangeIndex);
        }
        return enabled;
    }

    // The sector with what its enabled ranges hold. A range holds everything of its variant,
    // collision included (VariantCollisionNode), so the collision actors follow the same ranges as
    // the placements: a variant that is off shows neither its meshes nor its collision.
    public static SectorSnapshot Variant(SectorSnapshot sector, StreamingDescriptor descriptor, VariantSelection selection)
    {
        var enabled = EnabledRanges(sector, descriptor, selection);
        // Quest-prefab placements (street doors, devices, shop props) are streamed like the rest:
        // the game shows them by default; quests only address them. They stay read-only (doors
        // excepted) at conversion.
        var starts = sector.RangeStarts;
        int Range(int placement) { int range = 0; for (int i = 0; i < starts.Length && starts[i] <= placement; i++) range = i; return range; }
        return sector with
        {
            Placements = sector.Placements.Where(p => enabled.Contains(p.RangeIndex)).ToImmutableArray(),
            CollisionActors = sector.CollisionActors.IsDefaultOrEmpty ? sector.CollisionActors
                : sector.CollisionActors.Where(a => enabled.Contains(Range(a.PlacementIndex))).ToImmutableArray()
        };
    }

    // What this sector keeps off as the game starts: each variant with the box and the count of
    // its objects (the full sector is given, before any filtering).
    public static ImmutableArray<VariantContent> OffByDefault(SectorSnapshot sector, StreamingDescriptor descriptor)
    {
        if (descriptor.Variants.IsDefaultOrEmpty) return [];
        var enabled = EnabledRanges(sector, descriptor, VariantSelection.Default);
        var boxes = new Dictionary<int, (Vector3 Min, Vector3 Max, int Count)>();
        foreach (var p in sector.Placements)
        {
            if (enabled.Contains(p.RangeIndex)) continue;
            var at = p.Transform.Position;
            boxes[p.RangeIndex] = boxes.TryGetValue(p.RangeIndex, out var box) ? (Vector3.Min(box.Min, at), Vector3.Max(box.Max, at), box.Count + 1) : (at, at, 1);
        }
        if (boxes.Count == 0) return [];
        return [.. descriptor.Variants.Where(v => boxes.ContainsKey((int)v.RangeIndex)).DistinctBy(v => (v.Name.ToUpperInvariant(), v.RangeIndex))
            .Select(v => { var box = boxes[(int)v.RangeIndex]; return new VariantContent(v.Name, (int)v.RangeIndex, new(box.Min, box.Max), box.Count); })];
    }

    // The variants with objects within a distance of a position, nearest first.
    public static IReadOnlyList<NearbyVariant> Nearby(IEnumerable<VariantContent> contents, Vector3 position, float radius)
    {
        double limit = (double)radius * radius;
        return contents.Select(c => (Content: c, Distance: SectorSpatialIndex.DistanceSquared(position, c.Box))).Where(c => c.Distance <= limit)
            .GroupBy(c => c.Content.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new NearbyVariant(g.First().Content.Name, g.Sum(c => c.Content.Objects), (float)Math.Sqrt(g.Min(c => c.Distance))))
            .OrderBy(v => v.Distance).ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

// Sector variants are node ranges that quests switch on and off (barricades of a lockdown, the props
// of a story moment, loot, GPS paths). The world is shown in the game's default state; a selection
// adds every variant (All) or the named ones.
public sealed record VariantSelection(bool All, ImmutableHashSet<string> Names)
{
    public static readonly VariantSelection Default = new(false, ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase));
    public static VariantSelection Of(bool all, IEnumerable<string>? names) =>
        new(all, ImmutableHashSet.CreateRange(StringComparer.OrdinalIgnoreCase, (names ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim())));
    public bool Shows(string name) => All || Names.Contains(name);
    public bool SameAs(VariantSelection other) => All == other.All && Names.SetEquals(other.Names);
}
// The objects one variant range of a sector holds, and a variant as found around a position.
public sealed record VariantContent(string Name, int Range, Bounds3 Box, int Objects);
public sealed record NearbyVariant(string Name, int Objects, float Distance);

public sealed record StreamingMetrics(long Revision, long Generation, int DesiredSectors, int ResidentSectors, int PendingSectors,
    int PendingMeshes, int DeferredMeshes, int ActiveWorkers, long SectorBytes, long MeshBytes, long WorkerReservedBytes, long GpuReservedBytes,
    int CachedMeshes, long CompletedJobs, long CancelledJobs, long RejectedStaleResults, long EvictedMeshes, int Diagnostics);

// All state is owned by the caller/UI thread. At most MaximumWorkers asynchronous
// jobs exist; a cancelled job keeps its slot until it terminates. WorkerReservation
// is the shared transient allowance for all of them. Request lists contain
// identifiers, never decompressed resources. Advance never waits on a live task.
public sealed class NeighborhoodStreamer
{
    // Used is refreshed in place on every request pass (a record copy per wanted mesh was thousands of allocations).
    private sealed class CachedMesh(MeshAsset asset, long used) { public MeshAsset Asset { get; } = asset; public long Used = used; }
    private sealed record Work(bool Mesh, ulong Hash, long Allowance, CancellationTokenSource Cancellation, Task<Outcome> Task);
    private sealed record Outcome(SectorSnapshot? Sector = null, MeshAsset? Mesh = null, string? Error = null, bool Cancelled = false,
        (ulong Mesh, Bounds3 Box)[]? Summary = null, long DecodedBytes = 0, ImmutableArray<VariantContent> Off = default);
    private readonly IStreamingSource _source;
    private readonly Dictionary<ulong, SectorSnapshot> _sectors = [];
    // Per resident sector: each referenced mesh and the box of its placement positions.
    private readonly Dictionary<ulong, (ulong Mesh, Bounds3 Box)[]> _sectorMeshes = [];
    // Per resident sector that has some: the variants the game keeps off as it starts.
    private readonly Dictionary<ulong, ImmutableArray<VariantContent>> _offByDefault = [];
    private readonly Dictionary<ulong, CachedMesh> _cache = [];
    private readonly Dictionary<(bool Mesh, ulong Hash), string> _errors = [];
    private ImmutableArray<StreamingDescriptor> _desired = [];
    private readonly List<ulong> _wantedMeshes = [];
    private readonly HashSet<ulong> _wantedSet = [];
    private readonly List<ulong> _pendingMeshes = [];
    private readonly Dictionary<(bool Mesh, ulong Hash), long> _budgetErrors = [];
    private Task? _stopTask;
    private readonly List<Work> _work = [];
    private bool _stopped, _preferMesh;
    private long _tick, _generation, _revision, _completed, _cancelled, _stale, _evicted, _sectorBytes;
    private Vector3 _position;
    private IReadOnlyCollection<ScenePlacement> _authored = [];
    public StreamingBudgets Budgets { get; }
    public IReadOnlyDictionary<ulong, SectorSnapshot> Sectors => _sectors;
    public IEnumerable<VariantContent> OffByDefault => _offByDefault.Values.SelectMany(v => v);
    public IReadOnlyDictionary<(bool Mesh, ulong Hash), string> Errors => _errors;
    public long Revision => _revision;
    // Changes only when the drawable mesh set may have changed (not on sector arrivals).
    public long MeshRevision => _meshRevision;
    private long _meshRevision;
    public bool Stopped => _stopped;
    public bool HasActiveWork => _work.Count != 0;
    public IEnumerable<KeyValuePair<ulong, MeshAsset>> DrawableMeshes
    {
        get
        {
            long bytes = 0;
            foreach (var h in _wantedMeshes)
                if (_cache.TryGetValue(h, out var entry) && bytes + entry.Asset.CpuBytes <= Budgets.GpuBytes)
                { bytes += entry.Asset.CpuBytes; yield return new(h, entry.Asset); }
        }
    }
    private long SectorBytes => _sectorBytes;
    // Maintained on cache insert/evict; recomputing these sums per completion was O(n²) per frame.
    private long _meshBytes;
    private long MeshBytes => _meshBytes;
    private long _gpuRevision = -1, _gpuReserved;
    private long GpuReserved
    {
        get
        {
            if (_gpuRevision != _revision) { _gpuReserved = DrawableMeshes.Sum(m => m.Value.CpuBytes); _gpuRevision = _revision; }
            return _gpuReserved;
        }
    }
    private int MissingMeshes => _wantedMeshes.Count(h => !_cache.ContainsKey(h) && !_errors.ContainsKey((true, h)));
    public StreamingMetrics Metrics => new(_revision, _generation, _desired.Length, _sectors.Count,
        _desired.Count(d => !_sectors.ContainsKey(d.Sector.Hash) && !_errors.ContainsKey((false, d.Sector.Hash))),
        _pendingMeshes.Count, Math.Max(0, MissingMeshes - _pendingMeshes.Count), _work.Count,
        SectorBytes, MeshBytes, _work.Count == 0 ? 0 : Budgets.WorkerReservationBytes, GpuReserved,
        _cache.Count, _completed, _cancelled, _stale, _evicted, _errors.Count);

    public NeighborhoodStreamer(IStreamingSource source, StreamingBudgets budgets)
    { budgets.Validate(); _source = source; Budgets = budgets; }

    public void SetDesired(IEnumerable<StreamingDescriptor> descriptors, Vector3 position, IReadOnlyCollection<ScenePlacement>? authored = null)
    {
        if (_stopped) return;
        var next = descriptors.DistinctBy(d => d.Sector.Hash).Take(Budgets.MaximumSectors).ToImmutableArray();
        bool changed = !_desired.Select(d => d.Sector.Hash).SequenceEqual(next.Select(d => d.Sector.Hash));
        _position = position; _authored = authored ?? [];
        if (changed) { _generation++; _desired = next; }
        var wanted = _desired.Select(d => d.Sector.Hash).ToHashSet();
        foreach (var hash in _sectors.Keys.Where(h => !wanted.Contains(h)).ToArray()) { _sectorBytes -= _sectors[hash].EstimatedBytes; _sectors.Remove(hash); _sectorMeshes.Remove(hash); _revision++; }
        // Kept through a variant change (it does not depend on what is shown), dropped with the sector.
        foreach (var hash in _offByDefault.Keys.Where(h => !wanted.Contains(h)).ToArray()) _offByDefault.Remove(hash);
        RefreshMeshRequests();
        foreach (var key in _errors.Keys.Where(k => k.Mesh ? !_wantedSet.Contains(k.Hash) : !wanted.Contains(k.Hash)).ToArray()) _errors.Remove(key);
        foreach (var key in _budgetErrors.Keys.Where(k => !_errors.ContainsKey(k)).ToArray()) _budgetErrors.Remove(key);
        RefreshAdmission();
        foreach (var work in _work) if (!Wanted(work)) work.Cancellation.Cancel();
    }

    // A mesh whose source changed (a custom mesh edited in the library): dropped from the cache
    // and its error forgotten, so it is read again while it is wanted.
    public void Invalidate(ulong hash)
    {
        if (_cache.Remove(hash, out var cached)) _meshBytes -= cached.Asset.CpuBytes;
        _errors.Remove((true, hash)); _budgetErrors.Remove((true, hash));
        foreach (var work in _work) if (work.Mesh && work.Hash == hash) work.Cancellation.Cancel();
        _revision++; _meshRevision++;
        RefreshAdmission();
    }

    // Which sector variants are shown beside the game's defaults. The sectors that keep something
    // off are read again under the new selection; the others stay resident.
    public VariantSelection Variants { get; private set; } = VariantSelection.Default;
    public bool SetVariants(VariantSelection selection)
    {
        if (_stopped || Variants.SameAs(selection)) return false;
        Variants = selection;
        // A resident sector that keeps nothing off looks the same under any selection.
        var affected = _desired.Where(d => d.Variants.Length != 0 && (!_sectors.ContainsKey(d.Sector.Hash) || _offByDefault.ContainsKey(d.Sector.Hash)))
            .Select(d => d.Sector.Hash).ToHashSet();
        foreach (var work in _work) if (!work.Mesh && affected.Contains(work.Hash)) work.Cancellation.Cancel();
        foreach (var hash in _sectors.Keys.Where(affected.Contains).ToArray()) { _sectorBytes -= _sectors[hash].EstimatedBytes; _sectors.Remove(hash); _sectorMeshes.Remove(hash); }
        _generation++; _revision++;
        RefreshMeshRequests();
        RefreshAdmission();
        return true;
    }

    private bool Wanted(Work work) => work.Mesh ? _wantedSet.Contains(work.Hash) : _desired.Any(d => d.Sector.Hash == work.Hash);
    private bool _deferRefresh;
    private readonly Dictionary<ulong, double> _nearest = [];
    private (double Distance, ulong Mesh)[] _order = [];
    private void RefreshMeshRequests()
    {
        if (_deferRefresh) return;
        // Nearest placement distance per mesh, from per-sector summaries (a lower bound
        // through the box of that mesh's placement positions; exact for single uses).
        var nearest = _nearest; nearest.Clear();
        foreach (var (sector, summary) in _sectorMeshes)
        {
            if (!_sectors.ContainsKey(sector)) continue;
            foreach (var (mesh, box) in summary)
            {
                if (mesh == 0) continue; // 0 is "no resource"; it must never block the queue.
                double distance = SectorSpatialIndex.DistanceSquared(_position, box);
                ref var slot = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(nearest, mesh, out bool exists);
                if (!exists || distance < slot) slot = distance;
            }
        }
        foreach (var p in _authored) if (p.Mesh is { Hash: not 0 }) nearest[p.Mesh.Hash] = -1;
        bool sameSet = nearest.Count == _wantedSet.Count;
        if (sameSet) foreach (var mesh in nearest.Keys) if (!_wantedSet.Contains(mesh)) { sameSet = false; break; }
        // The order only matters for what is still to load and when the GPU budget truncates
        // the drawable set; a camera move over a loaded neighborhood skips the sort and keeps
        // the drawable set (and its revision) as it is.
        bool pending = false;
        if (sameSet) foreach (var mesh in nearest.Keys) if (!_cache.ContainsKey(mesh) && !_errors.ContainsKey((true, mesh))) { pending = true; break; }
        bool truncates = _meshBytes > Budgets.GpuBytes;
        if (!sameSet || pending || truncates)
        {
            int count = nearest.Count;
            if (_order.Length < count) _order = new (double, ulong)[Math.Max(count, _order.Length * 3 / 2)];
            int k = 0;
            foreach (var (mesh, distance) in nearest) _order[k++] = (distance, mesh);
            Array.Sort(_order, 0, count);
            bool changed = _wantedMeshes.Count != count;
            for (int i = 0; i < count && !changed; i++) changed = _wantedMeshes[i] != _order[i].Mesh;
            if (changed)
            {
                _wantedMeshes.Clear();
                for (int i = 0; i < count; i++) _wantedMeshes.Add(_order[i].Mesh);
                if (!sameSet) { _wantedSet.Clear(); _wantedSet.UnionWith(_wantedMeshes); }
                if (!sameSet || truncates) { _revision++; _meshRevision++; }
            }
        }
        foreach (var hash in _wantedMeshes) if (_cache.TryGetValue(hash, out var m)) m.Used = _tick;
        RefreshAdmission();
    }

    private void RefreshAdmission()
    {
        _pendingMeshes.Clear();
        foreach (var hash in _wantedMeshes)
        {
            if (!_cache.ContainsKey(hash) && !_errors.ContainsKey((true, hash)) && !InFlight(true, hash)) _pendingMeshes.Add(hash);
            if (_pendingMeshes.Count == Budgets.MaximumPendingMeshes) break;
        }
    }
    private bool InFlight(bool mesh, ulong hash) { foreach (var w in _work) if (w.Mesh == mesh && w.Hash == hash) return true; return false; }
    private long InFlightMeshAllowance { get { long sum = 0; foreach (var w in _work) if (w.Mesh) sum += w.Allowance; return sum; } }
    private long MeshAllowance => Math.Min(Budgets.MaximumMeshBytes, Math.Min(Budgets.CpuBytes - Budgets.WorkerReservationBytes - SectorBytes - MeshBytes - InFlightMeshAllowance,
        Budgets.GpuBytes - GpuReserved - InFlightMeshAllowance));

    public void Advance()
    {
        if (_stopped) return;
        _tick++;
        // Sector arrivals change the mesh request order; recompute it once per advance,
        // not per arrival (a burst of hundreds of sectors was quadratic).
        _deferRefresh = true;
        bool sectorArrived = false;
        try
        {
            for (int i = 0; i < _work.Count; i++)
            {
                var work = _work[i];
                if (!work.Task.IsCompleted) continue;
                _work.RemoveAt(i--);
                sectorArrived |= !work.Mesh;
                Complete(work, work.Task.GetAwaiter().GetResult()); // completed, never blocks
            }
        }
        finally { _deferRefresh = false; }
        if (sectorArrived) RefreshMeshRequests();
        if (_work.Count >= Budgets.MaximumWorkers) return;
        EvictUnused(Budgets.MaximumMeshBytes + Budgets.MaximumSectorBytes);
        foreach (var (key, previous) in _budgetErrors.ToArray())
        {
            long available = key.Mesh ? MeshAllowance : Budgets.CpuBytes - Budgets.WorkerReservationBytes - SectorBytes - MeshBytes;
            if (available > previous) { _budgetErrors.Remove(key); _errors.Remove(key); }
        }
        RefreshAdmission();
        // Sectors gate mesh discovery, so they may use every free slot; meshes
        // alternate with them when both are waiting.
        int pendingMeshIndex = 0;
        while (_work.Count < Budgets.MaximumWorkers)
        {
            var sectorRequest = _desired.FirstOrDefault(d => !_sectors.ContainsKey(d.Sector.Hash) && !_errors.ContainsKey((false, d.Sector.Hash)) && !InFlight(false, d.Sector.Hash));
            ulong meshRequest = pendingMeshIndex < _pendingMeshes.Count ? _pendingMeshes[pendingMeshIndex] : 0;
            bool loadMesh = meshRequest != 0 && (_preferMesh || sectorRequest is null);
            if (!loadMesh && sectorRequest is null) return;
            long allowance = MeshAllowance;
            if (loadMesh && allowance <= 0)
            {
                if (_work.Any(w => w.Mesh)) { if (sectorRequest is null) return; loadMesh = false; }
                else { _errors[(true, meshRequest)] = "CPU/GPU residency budget exhausted."; _budgetErrors[(true, meshRequest)] = allowance; RefreshAdmission(); return; }
            }
            ulong hash = loadMesh ? meshRequest : sectorRequest!.Sector.Hash;
            if (loadMesh) pendingMeshIndex++;
            if (!loadMesh && sectorRequest!.Sector.SourceBytes > Budgets.MaximumSourceBytes)
            { _errors[(false, hash)] = "Sector source exceeds the configured per-resource limit."; continue; }
            var cancellation = new CancellationTokenSource();
            _work.Add(new(loadMesh, hash, allowance, cancellation, Read(loadMesh, hash, allowance, loadMesh ? null : sectorRequest, Variants, cancellation.Token)));
            _preferMesh = !loadMesh;
        }
    }

    private void Complete(Work work, Outcome result)
    {
        bool cancelled = result.Cancelled || work.Cancellation.IsCancellationRequested;
        work.Cancellation.Dispose();
        if (cancelled) _cancelled++;
        else if (!Wanted(work)) _stale++;
        else if (result.Error is not null)
        {
            _errors[(work.Mesh, work.Hash)] = result.Error;
            if (work.Mesh && work.Allowance < Budgets.MaximumMeshBytes) _budgetErrors[(true, work.Hash)] = work.Allowance;
        }
        else if (result.Sector is { } sector)
        {
            // Account full decoded metadata even if default variants hide placements.
            if (result.DecodedBytes > Budgets.MaximumSectorBytes || SectorBytes + MeshBytes + result.DecodedBytes > Budgets.CpuBytes - Budgets.WorkerReservationBytes)
            {
                _errors[(false, work.Hash)] = "Sector metadata exceeds the resident CPU budget.";
                if (result.DecodedBytes <= Budgets.MaximumSectorBytes) _budgetErrors[(false, work.Hash)] = Budgets.CpuBytes - Budgets.WorkerReservationBytes - SectorBytes - MeshBytes;
            }
            else
            {
                _sectors[work.Hash] = sector; _completed++; _revision++;
                _sectorMeshes[work.Hash] = result.Summary ?? Summarize(sector);
                if (!result.Off.IsDefaultOrEmpty) _offByDefault[work.Hash] = result.Off;
                _sectorBytes += sector.EstimatedBytes;
                RefreshMeshRequests();
            }
        }
        else if (result.Mesh is { } mesh)
        {
            if (mesh.CpuBytes > Budgets.MaximumMeshBytes || SectorBytes + MeshBytes + mesh.CpuBytes > Budgets.CpuBytes - Budgets.WorkerReservationBytes || GpuReserved + mesh.CpuBytes > Budgets.GpuBytes)
            {
                _errors[(true, work.Hash)] = "Mesh exceeds remaining CPU/GPU residency budget.";
                if (mesh.CpuBytes <= Budgets.MaximumMeshBytes) _budgetErrors[(true, work.Hash)] = MeshAllowance;
            }
            else
            {
                if (_cache.TryGetValue(work.Hash, out var replaced)) _meshBytes -= replaced.Asset.CpuBytes;
                long gpu = GpuReserved;
                _cache[work.Hash] = new(mesh, _tick); _meshBytes += mesh.CpuBytes; _completed++; _revision++; _meshRevision++;
                // A wanted mesh admitted under the GPU check extends the drawable set; keep the
                // cached sum current instead of rescanning the wanted list per completion.
                if (replaced is null) { _gpuReserved = gpu + mesh.CpuBytes; _gpuRevision = _revision; }
            }
        }
    }
    private async Task<Outcome> Read(bool mesh, ulong hash, long allowance, StreamingDescriptor? descriptor, VariantSelection variants, CancellationToken cancellation)
    {
        try
        {
            if (mesh) return new(Mesh: await _source.ReadMeshAsync(hash, allowance, Budgets.MaximumSourceBytes, cancellation).ConfigureAwait(false));
            var sector = await _source.ReadSectorAsync(hash, Budgets.MaximumSourceBytes, cancellation).ConfigureAwait(false);
            // Variant filtering and the per-mesh summary run here, on the worker, not in Advance.
            var variant = NeighborhoodPolicy.Variant(sector, descriptor!, variants);
            var summary = Summarize(variant);
            return new(Sector: variant, Summary: summary, DecodedBytes: sector.EstimatedBytes, Off: NeighborhoodPolicy.OffByDefault(sector, descriptor!));
        }
        catch (OperationCanceledException) { return new(Cancelled: true); }
        catch (Exception ex) { return new(Error: ex.Message); }
    }

    private static (ulong Mesh, Bounds3 Box)[] Summarize(SectorSnapshot sector)
    {
        var boxes = new Dictionary<ulong, (Vector3 Min, Vector3 Max)>();
        foreach (var p in sector.Placements)
        {
            var position = p.Transform.Position;
            foreach (var mesh in p.Meshes())
                boxes[mesh.Hash] = boxes.TryGetValue(mesh.Hash, out var box) ? (Vector3.Min(box.Min, position), Vector3.Max(box.Max, position)) : (position, position);
        }
        return boxes.Select(b => (b.Key, new Bounds3(b.Value.Min, b.Value.Max))).ToArray();
    }

    private void EvictUnused(long reserve)
    {
        long meshBytes = MeshBytes;
        if (meshBytes + SectorBytes + reserve <= Budgets.CpuBytes - Budgets.WorkerReservationBytes) return;
        var wanted = _wantedMeshes.ToHashSet();
        foreach (var entry in _cache.Where(e => !wanted.Contains(e.Key)).OrderBy(e => e.Value.Used).ToArray())
        {
            if (meshBytes + SectorBytes + reserve <= Budgets.CpuBytes - Budgets.WorkerReservationBytes) break;
            _cache.Remove(entry.Key); meshBytes -= entry.Value.Asset.CpuBytes; _meshBytes -= entry.Value.Asset.CpuBytes; _evicted++; _revision++; _meshRevision++;
        }
    }

    // Diagnostic snapshot of the admission state (automation and tests).
    public object Inspect()
    {
        ulong first = _pendingMeshes.Count > 0 ? _pendingMeshes[0] : 0;
        return new { stopped = _stopped, work = _work.Count, pending = _pendingMeshes.Count, wanted = _wantedMeshes.Count, first,
            firstCached = _cache.ContainsKey(first), firstError = _errors.ContainsKey((true, first)), allowance = MeshAllowance,
            gpuReserved = GpuReserved, meshBytes = MeshBytes, sectorBytes = SectorBytes, preferMesh = _preferMesh,
            errors = _errors.Select(e => $"{(e.Key.Mesh ? "mesh" : "sector")} {e.Key.Hash}: {e.Value}").ToArray() };
    }

    public Task StopAsync()
    {
        if (_stopTask is not null) return _stopTask;
        _stopped = true;
        var work = _work.ToArray(); _work.Clear();
        _sectors.Clear(); _sectorMeshes.Clear(); _offByDefault.Clear(); _cache.Clear(); _meshBytes = 0; _wantedMeshes.Clear(); _wantedSet.Clear(); _pendingMeshes.Clear(); _errors.Clear(); _budgetErrors.Clear();
        _sectorBytes = 0;
        return _stopTask = FinishStop(work);
    }
    private static async Task FinishStop(Work[] work)
    {
        foreach (var w in work) w.Cancellation.Cancel();
        foreach (var w in work) { await w.Task.ConfigureAwait(false); w.Cancellation.Dispose(); }
    }
}
