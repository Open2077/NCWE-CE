// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Numerics;
using NCWE.Core;
using NCWE.Export;
using NCWE.Formats;
using NCWE.Render;

namespace NCWE.Studio.Session;

public enum ViewDistance { Near, Medium, Far }
public enum NavigationMode { Fly, Walk }

// UI-neutral editor state. Every member is used on the UI thread; asynchronous
// archive work only returns owned snapshots. The XAML shell polls this object.
internal sealed partial class StudioSession : IDisposable
{
    private readonly D3DRenderer _renderer;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<ulong, MeshAsset> _assets = [];
    private readonly Dictionary<ulong, string> _assetErrors = [];
    private readonly HashSet<ulong> _streamedSectors = [];
    private NeighborhoodStreamer? _streamer;
    private CachedStreamingSource? _source;
    private Task _streamStop = Task.CompletedTask;
    private long _streamRevision = -1, _publishedRevision = -1;
    private double _nextPlan, _nextPublish;
    private bool _sceneDirty = true;
    private ScenePlacement? _selected;
    private long _selectedRevision = -1;
    private string? _selectedFor;

    public StudioSettings Settings { get; }
    public SceneCamera Camera { get; } = new();
    public GameArchiveIndex? Index { get; private set; }
    public StreamingCatalog? Catalog { get; private set; }
    public SectorSpatialIndex? Spatial { get; private set; }
    public EditorDocument Document { get; private set; } = new(ProjectData.Create(Loc.T("Untitled world")));
    public string? ProjectPath { get; private set; }
    public string Status { get; set; } = Loc.T("Ready");
    public string Error { get; set; } = "";
    public bool Busy { get; private set; }
    public bool Indexing { get; private set; }
    public bool Streaming => _streamer is not null;
    public string? LastExportDirectory { get; private set; }
    public string? SelectedId { get; private set; }
    public IReadOnlyDictionary<ulong, MeshAsset> Assets => _assets;
    public IReadOnlyDictionary<ulong, string> AssetErrors => _assetErrors;
    public StreamingMetrics? StreamMetrics => _streamer?.Metrics;
    public object? StreamerInspection => _streamer?.Inspect();
    public StreamingBudgets? Budgets => _streamer?.Budgets;
    public long CacheHits => _source?.CacheHits ?? 0;
    public object? SourceTimings => _source?.Timings;
    public IReadOnlyDictionary<(bool Mesh, ulong Hash), string> StreamErrors => _streamer?.Errors ?? new Dictionary<(bool, ulong), string>();
    public bool ShowPlaceholders { get => _showPlaceholders; set { _showPlaceholders = value; _sceneDirty = true; } }
    private bool _showPlaceholders;
    public ViewDistance Distance { get; private set; }
    public const int DistantSectorLimit = 160;
    public bool DistantCity { get => _distantCity; set { if (_distantCity == value) return; _distantCity = value; _nextPlan = 0; _plannedAt = new(float.NaN); _renderer.ShowDistantProxies = value; } }
    private bool _distantCity = true;

    // Sector variants are the quest states of a place (the barriers of a lockdown, the props of a
    // story moment): the world is shown in the game's default state, plus the variants chosen here.
    public VariantSelection Variants { get; private set; } = VariantSelection.Default;
    public void SetVariants(VariantSelection selection)
    {
        if (Variants.SameAs(selection)) return;
        Variants = selection;
        Settings.AllVariants = selection.All; Settings.ShownVariants = [.. selection.Names.Order(StringComparer.OrdinalIgnoreCase)]; Settings.Save();
        if (_streamer is null || !_streamer.SetVariants(selection)) return;
        // The sectors read again leave the document now: one back before the next publication
        // would keep its previous content.
        foreach (var hash in _streamedSectors.Where(h => !_streamer.Sectors.ContainsKey(h)).ToArray())
        { Document.Evict(hash); _streamedSectors.Remove(hash); _bendedBySector.Remove(hash); }
        _sceneDirty = true; _streamRevision = -1; _nextPlan = 0;
    }

    // The variants the game keeps off as it starts that have objects around the camera, nearest first.
    public const float VariantRange = 150;
    public IReadOnlyList<NearbyVariant> NearbyVariants(float radius = VariantRange) =>
        _streamer is null ? [] : NeighborhoodPolicy.Nearby(_streamer.OffByDefault, Coordinates.ViewToGame(Camera.Eye), radius);
    public long SceneRevision { get; private set; }
    public event Action? SceneChanged;

    public StudioSession(D3DRenderer renderer, StudioSettings settings)
    {
        _renderer = renderer; Settings = settings; Distance = settings.ViewDistance;
        Variants = VariantSelection.Of(settings.AllVariants, settings.ShownVariants);
        if (App.Arguments.Contains("--no-preflight")) Settings.Preflight = false; // benchmarks and tests
        Camera.SetFlying(true);
        Camera.FieldOfView = settings.FieldOfView;
        var start = settings.Camera ?? new CameraBookmark(Loc.T("Start"), -1174, -1200, 40, .7f, .15f);
        GoTo(start);
        _assets[0] = SyntheticMesh.Cube();
        _renderer.SetWorld(_assets);
    }

    public ScenePlacement? Selected
    {
        get
        {
            if (SelectedId is null) return null;
            if (_selectedFor != SelectedId || _selectedRevision != Document.Revision)
            {
                _selected = Document.TryGetObject(SelectedId);   // every kind of id, collision actors included
                _selectedFor = SelectedId; _selectedRevision = Document.Revision;
            }
            return _selected;
        }
    }

    private void SetStatus(string status) => Status = status;

    // --- Installation -------------------------------------------------------------

    public async Task OpenGameAsync(string path)
    {
        if (Busy || _shutdown.IsCancellationRequested) return;
        Busy = Indexing = true; Error = "";
        GameArchiveIndex? candidate = null;
        try
        {
            await StopStreamingAsync();
            candidate = new(path, includeExpansion: true);
            await candidate.BuildAsync(new Progress<string>(SetStatus), _shutdown.Token);
            _shutdown.Token.ThrowIfCancellationRequested();
            var world = candidate.Worlds.SingleOrDefault(w => w.Path.EndsWith("\\03_night_city.streamingworld", StringComparison.OrdinalIgnoreCase))
                ?? throw new NotSupportedException("The Night City streaming world is absent from this archive profile.");
            var catalog = await candidate.LoadWorldCatalogCachedAsync(world.Hash, new SnapshotCache(Path.Combine(StudioSettings.StateDirectory, "Cache", "Catalog-v1")),
                new Progress<string>(SetStatus), _shutdown.Token);
            _renderer.MaterialSource = null; _renderer.ResetMaterials();
            Index?.Dispose(); Index = candidate; candidate = null;
            Index.Custom = CustomMeshes; Index.Decals = CustomDecals;
            // Surfaces of game materials, once read, come from the disk (a quarter of the mesh cache's room).
            var cacheFolder = Path.Combine(StudioSettings.StateDirectory, "Cache");
            Index.SurfaceCache = new SurfaceDiskCache(Path.Combine(cacheFolder, "Surfaces"), Math.Clamp(DiskCacheBudget(cacheFolder) / 4, 1L << 30, 6L << 30));
            _renderer.MaterialSource = DressedMaterials(EditedMaterials(TerrainMaterials(new ArchiveMaterialSource(Index))));
            // Meshes of mesh particles (debris), read as they come into view.
            var particleIndex = Index;
            _renderer.ParticleMeshSource = async hash => particleIndex.Find(hash) is null ? null : await particleIndex.ReadMeshConcurrentAsync(hash, default, 64L << 20);
            _effects.Clear(); _effectPreview = null;
            ResetTerrain();
            Catalog = catalog; Spatial = new(catalog.Descriptors);
            Hold = LoadingHold.Follow; _held = []; _lastDesired = []; HoldLoading = HoldOutside = false; HoldLeftOut = 0;
            Settings.Game = path; Settings.Save();
            Indexing = false;
            StartStreaming();
            Status = Loc.F("{0:N0} meshes · {1:N0} streaming sectors", Index.Meshes.Length, Spatial.Count);
            SceneChanged?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error = ex.Message; Status = Loc.T("The installation could not be opened."); }
        finally { candidate?.Dispose(); Busy = Indexing = false; }
    }

    // --- Streaming ----------------------------------------------------------------

    // Prefetch distance for levels 0-1 beyond the sectors whose boxes contain the camera.
    private static (float Radius, int Sectors) Preset(ViewDistance distance) => distance switch
    {
        ViewDistance.Near => (40, 2048),
        ViewDistance.Far => (250, 2048),
        _ => (120, 2048)
    };

    public void SetViewDistance(ViewDistance distance)
    {
        if (distance == Distance && _streamer is not null) return;
        Distance = distance; Settings.ViewDistance = distance; Settings.Save();
        if (Index is not null && !Busy) _ = RestartStreamingAsync();
    }

    private async Task RestartStreamingAsync() { await StopStreamingAsync(); StartStreaming(); }

    private void StartStreaming()
    {
        if (Index is null || Spatial is null || _streamer is not null) return;
        var (_, sectors) = Preset(Distance);
        var budgets = MachineBudgets(sectors);
        budgets = budgets with
        {
            GpuBytes = budgets.GpuBytes - _assets[0].CpuBytes - D3DRenderer.InstanceBufferReservationBytes,
            CpuBytes = budgets.CpuBytes - D3DRenderer.InstanceBufferReservationBytes
        };
        var cache = Path.Combine(StudioSettings.StateDirectory, "Cache");
        _source = new(Index, Path.Combine(cache, "Meshes-v1"), DiskCacheBudget(cache), Path.Combine(cache, "Sectors-v1"), concurrent: true,
            maximumDecodes: Math.Clamp(Environment.ProcessorCount / 3, 2, 8));
        _streamer = new(_source, budgets);
        _streamer.SetVariants(Variants);
        _streamRevision = -1; _nextPlan = 0;
    }

    // Budgets follow the machine: half the video memory (≤ 8 GiB) for drawable meshes, a
    // fifth of the RAM (3–6 GiB) for decoded data, and every core but four for loading.
    // Workers mix disk reads with CPU decoding, so they may exceed the decode-bound count.
    private StreamingBudgets MachineBudgets(int sectors)
    {
        long ram = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        long gpu = Math.Clamp((long)(_renderer.DedicatedVideoMemory / 2), 1L << 30, 8L << 30);
        long cpu = Math.Clamp(ram / 5, 3L << 30, 6L << 30);
        if (Distance == ViewDistance.Far) cpu = Math.Min(cpu + (1L << 30), Math.Max(cpu, ram / 4));
        int workers = Math.Clamp(Environment.ProcessorCount - 4, 4, 24);
        return new StreamingBudgets(CpuBytes: cpu, GpuBytes: gpu, MaximumSectors: sectors, MaximumPendingMeshes: 2048,
            MaximumMeshBytes: 96L << 20, MaximumSourceBytes: 96L << 20, WorkerReservationBytes: 512L << 20, MaximumWorkers: workers);
    }

    // The converted-mesh cache may use 15 % of the free space on its drive (4–24 GiB).
    private static long DiskCacheBudget(string folder)
    {
        try
        {
            var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace;
            return Math.Clamp(free / 100 * 15, 4L << 30, 24L << 30);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return 4L << 30; }
    }

    private Task StopStreamingAsync()
    {
        if (_streamer is null) return _streamStop;
        var previous = _streamer; _streamer = null; _source = null;
        _streamStop = Task.WhenAll(_streamStop, previous.StopAsync());
        foreach (var hash in _streamedSectors) Document.Evict(hash);
        _streamedSectors.Clear(); _assets.Clear(); _assetErrors.Clear(); _assets[0] = SyntheticMesh.Cube();
        _bendedInAssets.Clear(); _bending.Clear(); _meshRevision = -1;
        _roadMeshes.Clear(); _roadsDirty = true;
        // Edited terrain cells are drawn again once streaming restarts (their previews are rebuilt).
        foreach (var preview in _terrainPreviews.Values) { preview.Generation = -1; preview.Asset = null; }
        _terrainPreviews.Clear(); TerrainSurfaces.Clear();
        SetWorldAssets(); _sceneDirty = true;
        return _streamStop;
    }

    // Called once per rendered frame.
    public double PlanMilliseconds { get; private set; }
    public double AdvanceMilliseconds { get; private set; }
    public double AttachMilliseconds { get; private set; }
    public double PublishMilliseconds { get; private set; }

    public void Tick(double now)
    {
        _now = now;
        TrackVelocity(now);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        if (_streamer is not null && !_shutdown.IsCancellationRequested)
        {
            var position = Coordinates.ViewToGame(Camera.Eye);
            bool moved = !(Vector3.DistanceSquared(position, _plannedAt) < 36);
            bool authoredChanged = !ReferenceEquals(Document.Project.Changes.Added, _plannedAdded) || !ReferenceEquals(Document.Project.Changes.Elements, _plannedElements) || _doorVisualsVersion != _plannedDoorVisuals
                || !ReferenceEquals(Document.Project.Changes.Elevators, _plannedElevators);
            // A hold (World ▸ Loading) keeps its sectors wherever the camera goes: only what the
            // project adds is planned again.
            bool held = Hold != LoadingHold.Follow;
            if (held ? authoredChanged || _holdChanged || now >= _nextPlan + 2 : (moved && now >= _nextPlan) || authoredChanged || now >= _nextPlan + 2)
            {
                var (radius, sectors) = Preset(Distance);
                var choose = System.Diagnostics.Stopwatch.StartNew();
                var desired = _held;
                if (!held)
                {
                    desired = NeighborhoodPolicy.ChooseRuntime(Spatial!, position, radius, sectors, _streamer.Sectors.Keys.ToHashSet());
                    // Look-ahead: while moving, also request what surrounds the position ~1.5 s ahead
                    // (after the current neighborhood), so flying forward finds it already loaded.
                    var ahead = _velocity * 1.5f;
                    if (ahead.Length() > 300) ahead = Vector3.Normalize(ahead) * 300;
                    if (_velocity.Length() > 4 && desired.Length < sectors)
                        desired = desired.Concat(NeighborhoodPolicy.ChooseRuntime(Spatial!, position + ahead, radius, sectors))
                            .DistinctBy(d => d.Sector.Hash).Take(sectors).ToImmutableArray();
                }
                _lastDesired = desired; _holdChanged = false;
                FrameProfile.Record("plan.choose", choose.Elapsed.TotalMilliseconds);
                var added = Document.Project.Changes.Added;
                // Authored objects remain available even if their source sector is evicted.
                // Held: meshes keep the order they had from the hold's centre, so none is swapped out as the camera moves.
                _streamer.SetDesired(desired, held ? HoldCentre : position, added.Values.Select(a => new ScenePlacement(a.Id, a.Name, a.Mesh, a.Transform, null, false, false, null)).Concat(DoorElementMeshes()).Concat(ElevatorMeshes()).ToArray());
                _plannedAt = position; _plannedAdded = added; _plannedElements = Document.Project.Changes.Elements; _plannedDoorVisuals = _doorVisualsVersion; _plannedElevators = Document.Project.Changes.Elevators;
                _nextPlan = now + .25;
                PlanMilliseconds = watch.Elapsed.TotalMilliseconds; FrameProfile.Record("plan", PlanMilliseconds);
            }
            watch.Restart();
            _streamer.Advance();
            AdvanceMilliseconds = watch.Elapsed.TotalMilliseconds; FrameProfile.Record("advance", AdvanceMilliseconds);
            RequestDoorSurfaces(now);
            if ((_streamer.Revision != _streamRevision || _bendArrived) && now >= _nextPublish)
            {
                watch.Restart();
                _bendArrived = false;
                foreach (var hash in _streamedSectors.Where(h => !_streamer.Sectors.ContainsKey(h)).ToArray())
                { Document.Evict(hash); _streamedSectors.Remove(hash); }
                // A streamed sector missing from the document (replaced by an opened project while it
                // arrived) is attached again rather than staying invisible until evicted.
                foreach (var (hash, sector) in _streamer.Sectors)
                    if (_streamedSectors.Add(hash) || !Document.ResidentSectors.ContainsKey(hash)) Document.Attach(sector, remember: false);
                var part = System.Diagnostics.Stopwatch.StartNew();
                // The drawable mesh set only changes with mesh arrivals/evictions, not sectors.
                bool meshesChanged = _streamer.MeshRevision != _meshRevision;
                if (meshesChanged)
                {
                    _assets.Clear(); _assets[0] = SyntheticMesh.Cube(); _bendedInAssets.Clear();
                    foreach (var (hash, mesh) in _streamer.DrawableMeshes) _assets.Add(hash, mesh);
                    _meshRevision = _streamer.MeshRevision;
                }
                FrameProfile.Record("att.assets", part.Elapsed.TotalMilliseconds); part.Restart();
                bool bendedChanged = AddBendedAssets() | AddRoadAssets() | AddDoorSurfaceAssets() | AddTerrainAssets() | AddEditedAssets() | AddDressedAssets();
                FrameProfile.Record("att.bend", part.Elapsed.TotalMilliseconds); part.Restart();
                if (meshesChanged || bendedChanged) SetWorldAssets();
                _sceneDirty = true;
                FrameProfile.Record("att.setworld", part.Elapsed.TotalMilliseconds);
                _streamRevision = _streamer.Revision;
                // Coalesce bursts of arrivals into a few publications per second.
                _nextPublish = now + .2;
                AttachMilliseconds = watch.Elapsed.TotalMilliseconds; FrameProfile.Record("attach", AttachMilliseconds);
            }
            if (_streamer.Errors.Count != _assetErrorCount)
            {
                _assetErrors.Clear();
                foreach (var (key, message) in _streamer.Errors) if (key.Mesh) _assetErrors[key.Hash] = message;
                _assetErrorCount = _streamer.Errors.Count;
            }
        }
        LineUpPending();
        UpdateHold(now);
        AutoPreflight(now);
        UpdateRoadAssets(now);
        UpdateTerrain(now);
        UpdateElevators();
        UpdateMeshEdits();
        UpdateMaterialDressing();
        watch.Restart();
        if (PublishInstances()) { PublishMilliseconds = watch.Elapsed.TotalMilliseconds; FrameProfile.Record("publish", PublishMilliseconds); }
        BuildOverlays();
        AutosaveIfNeeded(now);
    }
    private double _now;
    private int _assetErrorCount = -1;
    private Vector3 _plannedAt = new(float.NaN);
    private Vector3 _velocityPosition, _velocity;
    private double _velocityTime = double.NaN;

    // Smoothed camera velocity (game space, m/s); jumps (go to, bookmarks) are not movement.
    private void TrackVelocity(double now)
    {
        var position = Coordinates.ViewToGame(Camera.Eye);
        if (!double.IsNaN(_velocityTime) && now > _velocityTime)
        {
            float dt = (float)(now - _velocityTime);
            var step = position - _velocityPosition;
            if (step.Length() > 60 || dt > .5f) _velocity = Vector3.Zero;
            else _velocity = Vector3.Lerp(_velocity, step / dt, 1 - MathF.Exp(-dt / .3f));
        }
        _velocityPosition = position; _velocityTime = now;
    }
    private object? _plannedAdded, _plannedElements;
    private long _plannedDoorVisuals = -1;

    // Render data is cached per resident sector and rebuilt only when that sector, the
    // drawable meshes it references, or its edits change. Edited, added and selected
    // objects are composed on top, so a selection or gizmo preview is cheap.
    private sealed class SectorRender
    {
        public required SectorSnapshot Sector;
        // Placements bucketed by mesh; a bucket's group is created once its mesh is drawable.
        public required Dictionary<ulong, RenderPlacement[]> ByMesh;
        public required RenderPlacement[] Placeholders;
        public required MarkerEntry[] Markers;
        // The sector's decals (StudioSession.Decals), or none.
        public RenderDecalGroup? Decals;
        public readonly Dictionary<ulong, RenderGroup> Groups = [];
        public RenderGroup? PlaceholderGroup;
        // The groups of the drawable buckets, for the drawable set they were listed for.
        public RenderGroup[] Drawn = [];
        public long DrawnVersion = -1;
        // What the renderer is given for this sector: Drawn and its placeholders, while they last.
        public RenderGroupSet? Set;
        public bool SetPlaceholders;
        // The terrain cells its plants stand in (they follow the ground's edits there).
        public HashSet<Core.Terrain.TerrainCell>? FoliageCells;
    }
    // A marker node (light, sound, effect, decal, collision) at its current view-space position.
    internal readonly record struct MarkerEntry(string Id, Vector3 Position, DebugMarker Marker, bool Editable, ElementProperties? Properties = null, Quaternion Rotation = default);
    private readonly Dictionary<ulong, SectorRender> _sectorRender = [];
    private HashSet<ulong> _publishedDrawable = [];
    // Bumped when the drawable set changed; _assetsVersion when the renderer's mesh table was
    // given a changed _assets (SetWorldAssets).
    private long _drawableVersion, _assetsVersion, _publishedAssetsVersion = -1;
    private void SetWorldAssets() { _renderer.SetWorld(_assets); _assetsVersion++; }
    private List<RenderGroupSet> _publishSpare = [];
    private RenderGroupSet? _restSet;
    private object? _publishedEdits;
    private bool _publishedPlaceholders;
    private List<RenderGroupSet> _published = [];
    private HashSet<string> _renderedSelection = [];
    private bool _gridDirty = true;
    private double _gridBuilt = double.NegativeInfinity;

    // Streamed placement IDs encode their sector: "s:<hash x16>:...".
    private static ulong? SectorOf(string? id)
        => id is not null && id.Length > 19 && id[0] == 's' && id[1] == ':' && ulong.TryParse(id.AsSpan(2, 16), System.Globalization.NumberStyles.HexNumber, null, out var hash) ? hash : null;

    private bool PublishInstances()
    {
        if (!_sceneDirty && _publishedRevision == Document.Revision && _previews is null && !_previewWasActive) return false;
        _previewWasActive = _previews is not null;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        bool structural = _sceneDirty || _publishedRevision != Document.Revision;
        var edits = Document.Project.Changes.Modified;
        if (structural)
        {
            // The drawable set is read again only when the renderer's mesh table changed (an
            // edit or a selection does not change it). Sectors list their drawable groups again
            // only when one of their meshes came or went.
            if (_assetsVersion != _publishedAssetsVersion)
            {
                var drawable = _assets.Keys.ToHashSet();
                // A mesh that left the drawable set may come back with different bounds.
                var lost = _publishedDrawable.Where(h => !drawable.Contains(h)).ToHashSet();
                if (lost.Count != 0) foreach (var entry in _sectorRender.Values) foreach (var hash in lost) entry.Groups.Remove(hash);
                var changed = lost; changed.UnionWith(drawable.Where(h => !_publishedDrawable.Contains(h)));
                long previous = _drawableVersion;
                _publishedDrawable = drawable; _publishedAssetsVersion = _assetsVersion;
                if (changed.Count != 0)
                {
                    _drawableVersion++;
                    foreach (var entry in _sectorRender.Values)
                    {
                        if (entry.DrawnVersion != previous) continue;
                        bool touched = false;
                        foreach (var mesh in entry.ByMesh.Keys) if (changed.Contains(mesh)) { touched = true; break; }
                        if (!touched) entry.DrawnVersion = _drawableVersion;
                    }
                }
            }
            var dirtySectors = new HashSet<ulong>();
            // Sectors whose plants stand where the terrain changed.
            dirtySectors.UnionWith(_terrainDirtySectors); _terrainDirtySectors.Clear();
            if (!ReferenceEquals(edits, _publishedEdits))
            {
                // Only the sectors whose edits changed (an object edited or restored). An object
                // edited again stays out of its sector's drawing either way: only a marker, drawn
                // where its edit puts it, needs its sector rebuilt.
                var old = _publishedEdits as IReadOnlyDictionary<string, PlacementEdit>;
                foreach (var (id, e) in edits)
                {
                    if (old is not null && old.TryGetValue(id, out var before)
                        && (ReferenceEquals(before, e) || before.Deleted == e.Deleted && Document.PlacementOf(id) is { Marker: null })) continue;
                    dirtySectors.Add(e.Source.Sector.Hash);
                }
                if (old is not null) foreach (var (id, e) in old) if (!edits.ContainsKey(id)) dirtySectors.Add(e.Source.Sector.Hash);
            }
            // Selected placements are drawn highlighted in the dynamic group, so the
            // sectors they leave or join are rebuilt without/with them.
            if (!_renderedSelection.SetEquals(_selectionSet))
            {
                foreach (var id in _renderedSelection.Concat(_selectionSet)) if (SectorOf(id) is { } hash) dirtySectors.Add(hash);
                _renderedSelection = _selectionSet.ToHashSet();
            }
            // Objects hidden or shown since the last build: their sectors are rebuilt.
            var hiddenNow = HiddenIds;
            if (!ReferenceEquals(hiddenNow, _publishedHidden))
            {
                foreach (var id in hiddenNow) if (!_publishedHidden.Contains(id) && SectorOf(id) is { } hash) dirtySectors.Add(hash);
                foreach (var id in _publishedHidden) if (!hiddenNow.Contains(id) && SectorOf(id) is { } hash) dirtySectors.Add(hash);
                _publishedHidden = hiddenNow;
            }
            if (_publishedPlaceholders != _showPlaceholders) _sectorRender.Clear();
            if (_doorSurfacesBuilt != _doorSurfacesVersion)
            {
                foreach (var (hash, entry) in _sectorRender.Where(e => e.Value.Markers.Any(m => m.Marker.Kind == "Door")).ToArray()) _sectorRender.Remove(hash);
                _doorSurfacesBuilt = _doorSurfacesVersion;
            }
            foreach (var hash in _sectorRender.Keys.Where(h => !Document.ResidentSectors.ContainsKey(h)).ToArray()) _sectorRender.Remove(hash);
            var build = System.Diagnostics.Stopwatch.StartNew();
            var toBuild = new List<(ulong Hash, SectorSnapshot Sector)>();
            foreach (var (hash, sector) in Document.ResidentSectors)
            {
                if (_sectorRender.TryGetValue(hash, out var cached) && ReferenceEquals(cached.Sector, sector) && !dirtySectors.Contains(hash)) continue;
                toBuild.Add((hash, sector));
            }
            // Sector render data is a pure function of the sector, the edits, the selection and
            // the drawable set (all only read here): build new sectors on every core.
            var built = new SectorRender[toBuild.Count];
            UiParallel.For(toBuild.Count, i => built[i] = BuildSector(toBuild[i].Sector, edits, hiddenNow, _sectorRender.GetValueOrDefault(toBuild[i].Hash)));
            for (int i = 0; i < built.Length; i++) _sectorRender[toBuild[i].Hash] = built[i];
            FrameProfile.Record("pub.build", build.Elapsed.TotalMilliseconds);
            _publishedEdits = edits; _publishedPlaceholders = _showPlaceholders;
        }
        // One set of groups per sector, kept while the sector's drawing is unchanged: the renderer
        // skips a set it already has, so a publish costs the sectors that changed. The list of the
        // previous publish but one is reused.
        var sets = _publishSpare; sets.Clear();
        var isolated = _isolated;
        // Isolation: the sectors' own drawing is left out; isolated game objects join the dynamic group.
        if (isolated is null)
            foreach (var entry in _sectorRender.Values)
            {
                if (entry.DrawnVersion != _drawableVersion)
                {
                    var drawn = new List<RenderGroup>(entry.ByMesh.Count);
                    foreach (var (mesh, bucket) in entry.ByMesh)
                    {
                        if (!_assets.ContainsKey(mesh)) continue;
                        if (!entry.Groups.TryGetValue(mesh, out var group)) entry.Groups[mesh] = group = new(bucket);
                        drawn.Add(group);
                    }
                    entry.Drawn = drawn.ToArray(); entry.DrawnVersion = _drawableVersion; entry.Set = null;
                }
                bool placeholders = _showPlaceholders && entry.Placeholders.Length != 0;
                if (entry.Set is null || entry.SetPlaceholders != placeholders)
                {
                    entry.Set = new(placeholders ? [.. entry.Drawn, entry.PlaceholderGroup ??= new(entry.Placeholders)] : entry.Drawn);
                    entry.SetPlaceholders = placeholders;
                }
                sets.Add(entry.Set);
            }
        // What is drawn outside the sectors and does not move while the selection is dragged
        // (edited and added objects, project doors, isolated ones) is one group, rebuilt only on
        // structural changes: a drag republishes the selection's group alone.
        if (structural || _restGroup is null)
        {
            var rest = new List<RenderPlacement>();
            if (isolated is not null)
                foreach (var id in isolated)
                {
                    if (edits.ContainsKey(id) || _selectionSet.Contains(id) || !IsShown(id) || Document.TryGetObject(id)?.Original is not { } game || game.OccluderNode) continue;
                    if (game.Marker is null || !game.EntityParts.IsDefaultOrEmpty) AddInstance(rest, id, game.Mesh, game.Transform, false, game.InstanceOffsets, game.EntityParts);
                }
            foreach (var (id, edit) in edits)
            {
                if (edit.Deleted || _selectionSet.Contains(id) || !IsShown(id) || !Document.ResidentSectors.ContainsKey(edit.Source.Sector.Hash)) continue;
                var original = Document.PlacementOf(id);
                if (original is not null && !original.OccluderNode && (original.Marker is null || !original.EntityParts.IsDefaultOrEmpty)) AddInstance(rest, id, original.Mesh, edit.Transform ?? original.Transform, false, original.InstanceOffsets, original.EntityParts);
            }
            foreach (var added in Document.Project.Changes.Added.Values)
                if (!_selectionSet.Contains(added.Id) && IsShown(added.Id)) AddInstance(rest, added.Id, added.Mesh, added.Transform, false);
            // Project doors: the meshes of their template appearance.
            foreach (var element in Document.Project.Changes.Elements.Values)
                if (element.Properties.Kind is ElementKind.Door or ElementKind.Entity && !_selectionSet.Contains(element.Id) && IsShown(element.Id) && ElementParts(element) is { IsDefaultOrEmpty: false } doorParts)
                    AddInstance(rest, element.Id, null, element.Transform, false, parts: doorParts);
            _restGroup = new RenderGroup(rest.ToArray());
            _restSet = new([_restGroup]);
        }
        sets.Add(_restSet!);
        var dynamic = new List<RenderPlacement>();
        foreach (var selected in CachedSelection())
            if (!selected.Deleted && IsShown(selected.Id) && selected.Original?.OccluderNode != true && (selected.Original is null ? selected.Element is null || ElementParts(selected.Element) is { IsDefaultOrEmpty: false } : selected.Original.Marker is null || !selected.Original.EntityParts.IsDefaultOrEmpty))
                AddInstance(dynamic, selected.Id, selected.Mesh, _previews is not null && _previews.TryGetValue(selected.Id, out var preview) ? preview : selected.Transform, selected.Id != MeshEditId,
                    selected.Original?.InstanceOffsets ?? default, selected.Original?.EntityParts ?? (selected.Element is { } chosen ? ElementParts(chosen) : default));
        AddRoadInstances(dynamic);
        AddElevatorInstances(dynamic);
        sets.Add(new([new RenderGroup(dynamic.ToArray())]));
        var prepare = System.Diagnostics.Stopwatch.StartNew();
        _renderer.SetWorldSets(sets);
        PublishDecals(structural);
        FrameProfile.Record("pub.groups", prepare.Elapsed.TotalMilliseconds);
        FrameProfile.Record("pub.mb", (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / 1048576.0);
        _publishSpare = _published; _published = sets;
        if (structural) _gridDirty = true;
        _sceneDirty = false; _publishedRevision = Document.Revision;
        if (structural) { SceneRevision++; SceneChanged?.Invoke(); }
        return true;
    }
    private bool _previewWasActive;
    private RenderGroup? _restGroup;
    private IReadOnlySet<string> _publishedHidden = new HashSet<string>();

    // Bended (road) placements get a per-placement mesh bent from their template.
    private readonly Dictionary<ulong, SourcePlacement[]> _bendedBySector = [];
    private readonly Dictionary<string, (string TemplateFingerprint, MeshAsset Asset)> _bended = new(StringComparer.Ordinal);

    private static ulong BendedKey(string id)
    {
        ulong hash = 14695981039346656037UL;
        foreach (char ch in id) { hash ^= ch; hash *= 1099511628211UL; }
        return hash | (1UL << 63);
    }

    // Bending runs on the thread pool; finished meshes join the drawable set on the next
    // publish (_bendArrived requests one). Returns whether _assets changed.
    private bool AddBendedAssets()
    {
        bool changed = false;
        foreach (var key in _bendedBySector.Keys.Where(k => !_streamer!.Sectors.ContainsKey(k)).ToArray()) _bendedBySector.Remove(key);
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (hash, sector) in _streamer!.Sectors)
        {
            if (!_bendedBySector.TryGetValue(hash, out var bended))
                _bendedBySector[hash] = bended = sector.Placements.Where(p => !p.Deformation.IsDefaultOrEmpty && p.Mesh is not null).ToArray();
            foreach (var p in bended)
            {
                if (!_assets.TryGetValue(p.Mesh!.Hash, out var template)) continue;
                live.Add(p.Id);
                ulong key = BendedKey(p.Id);
                // A bent mesh keeps its template's parts, vertex order and UVs: it wears the template's materials.
                _renderer.AliasSurface(key, p.Mesh.Hash);
                if (_bended.TryGetValue(p.Id, out var cached) && cached.TemplateFingerprint == template.SourceHash)
                {
                    if (_bendedInAssets.Add(p.Id)) { _assets[key] = cached.Asset; changed = true; }
                    continue;
                }
                if (_bending.TryGetValue(p.Id, out var pending) && pending.TemplateFingerprint == template.SourceHash)
                {
                    if (!pending.Task.IsCompleted) continue;
                    _bending.Remove(p.Id);
                    if (pending.Task.Result is not { } asset) continue;
                    _bended[p.Id] = (template.SourceHash, asset);
                    _assets[key] = asset; _bendedInAssets.Add(p.Id); changed = true;
                    continue;
                }
                var frames = p.Deformation; string name = p.Name; var source = template;
                _bending[p.Id] = (template.SourceHash, Task.Run(() =>
                {
                    // Cables hang their tube on their frames as the game skins it; road pieces bend along theirs.
                    try { return CableBending.IsCable(source.ResourcePath) ? CableBending.Deform(source, frames, name) : RoadBending.Deform(source, frames, name, RoadBending.AxisOf(source.ResourcePath)); }
                    catch (Exception ex) when (ex is ArgumentException or InvalidDataException) { return null; }
                    finally { _bendArrived = true; }
                }));
            }
        }
        foreach (var id in _bended.Keys.Where(k => !live.Contains(k)).ToArray()) _bended.Remove(id);
        foreach (var id in _bending.Keys.Where(k => !live.Contains(k)).ToArray()) _bending.Remove(id);
        foreach (var id in _bendedInAssets.Where(k => !live.Contains(k)).ToArray())
        { _bendedInAssets.Remove(id); _assets.Remove(BendedKey(id)); changed = true; }
        return changed;
    }
    private readonly Dictionary<string, (string TemplateFingerprint, Task<MeshAsset?> Task)> _bending = new(StringComparer.Ordinal);
    private readonly HashSet<string> _bendedInAssets = new(StringComparer.Ordinal);
    private volatile bool _bendArrived;
    private long _meshRevision = -1;

    // previous: the sector's render data before this rebuild. Its buckets that come out the same
    // keep their group (and the renderer its preparation and GPU slots): an edit or a selection
    // in a dense sector re-prepares the meshes it touched, not the whole sector.
    private SectorRender BuildSector(SectorSnapshot sector, IReadOnlyDictionary<string, PlacementEdit> edits, IReadOnlySet<string> hidden, SectorRender? previous = null)
    {
        var render = BuildSector(sector, edits, hidden);
        if (previous is null) return render;
        foreach (var (mesh, bucket) in render.ByMesh.ToArray())
            if (previous.ByMesh.TryGetValue(mesh, out var before) && previous.Groups.TryGetValue(mesh, out var group) && ReferenceEquals(group.Items, before)
                && bucket.AsSpan().SequenceEqual(before))
            { render.ByMesh[mesh] = before; render.Groups[mesh] = group; }
        if (previous.PlaceholderGroup is { } placeholders && render.Placeholders.AsSpan().SequenceEqual(previous.Placeholders))
        { render.Placeholders = previous.Placeholders; render.PlaceholderGroup = placeholders; }
        if (previous.Decals is { } decalsBefore && render.Decals is { } decalsNow && decalsNow.Items.AsSpan().SequenceEqual(decalsBefore.Items)) render.Decals = decalsBefore;
        return render;
    }

    private SectorRender BuildSector(SectorSnapshot sector, IReadOnlyDictionary<string, PlacementEdit> edits, IReadOnlySet<string> hidden)
    {
        var byMesh = new Dictionary<ulong, List<RenderPlacement>>();
        var placeholders = new List<RenderPlacement>();
        var markers = new List<MarkerEntry>();
        List<RenderDecal>? decals = null;
        HashSet<Core.Terrain.TerrainCell>? foliageCells = null;
        var terrain = Terrain;
        foreach (var p in sector.Placements)
        {
            if (hidden.Count != 0 && hidden.Contains(p.Id)) continue;
            if (p.Marker is { } marker)
            {
                // Moved markers show where they were moved to; deleted ones disappear.
                var at = p.Transform;
                if (edits.TryGetValue(p.Id, out var edit)) { if (edit.Deleted) continue; at = edit.Transform ?? at; }
                markers.Add(new(p.Id, Coordinates.GameToView(at.Position), marker, p.CanEdit, p.Element, at.Rotation));
                // A decal is drawn too, where its edit puts it (a selected one with the selection).
                if (p.Decal is { } decal && !_selectionSet.Contains(p.Id)) (decals ??= []).Add(GameDecal(p, decal, at, false));
                // Doors are drawn too (their icon is in addition to their meshes).
                if (p.EntityParts.IsDefaultOrEmpty) continue;
            }
            if (edits.ContainsKey(p.Id) || _selectionSet.Contains(p.Id)) continue;
            if (p.OccluderNode) continue;   // occluders: wireframes in the occlusion view only
            if (!p.EntityParts.IsDefaultOrEmpty)
            {
                // Entities (doors, devices): every mesh of their appearance, picking the node.
                ulong entityKey = TypeKey(p.Source.NodeType);
                bool door = p.Entity?.IsDoor == true;
                foreach (var part in p.EntityParts)
                {
                    ulong key = door ? DoorPartKey(part.Mesh) : part.Mesh.Hash;
                    if (!byMesh.TryGetValue(key, out var parts)) byMesh[key] = parts = [];
                    parts.Add(new(p.Id, key, p.Transform.Apply(part.Local).ViewMatrix, false, Appearance: part.Mesh.Appearance, TypeKey: entityKey, HideFar: p.MaxStreamingDistance));
                }
                continue;
            }
            var transform = p.Transform;
            // Plants on sculpted ground go up and down with it (and disappear in its holes).
            if (p.Source.NodeType == "worldFoliageNode")
            {
                var cell = Core.Terrain.TerrainCell.At(new(transform.Position.X, transform.Position.Y));
                (foliageCells ??= []).Add(cell);
                if (terrain?.Effective(cell) is not null
                    && Core.Terrain.TerrainFollow.OnEditedGround(terrain, transform.Position, Core.Terrain.TerrainFollow.FoliageBelow, Core.Terrain.TerrainFollow.FoliageAbove) is { } follow)
                {
                    if (follow.Gone) continue;
                    transform = transform with { Position = transform.Position + new Vector3(0, 0, follow.Delta) };
                }
            }
            if (p.Mesh is not null)
            {
                ulong meshKey = p.Deformation.IsDefaultOrEmpty ? p.Mesh.Hash : BendedKey(p.Id);
                if (!byMesh.TryGetValue(meshKey, out var list)) byMesh[meshKey] = list = [];
                ulong typeKey = TypeKey(p.Source.NodeType);
                // Road proxies stay drawn near the camera, pushed below like the terrain: the bent road
                // pieces drawn there win, and where a piece has no mesh in the archives (whole
                // shorelines, some highways) the proxy is the road.
                bool roadProxy = p.Source.NodeType == "worldRoadProxyMeshNode";
                // Destructible nodes: one drawn copy per instance, all picking the same node.
                if (p.InstanceOffsets.IsDefaultOrEmpty)
                    list.Add(new(p.Id, meshKey, transform.ViewMatrix, false, HideNear: roadProxy ? 0 : p.NearHideDistance, Appearance: p.Mesh.Appearance, TypeKey: typeKey, HideFar: p.MaxStreamingDistance,
                        Ground: roadProxy || p.Source.NodeType == "worldTerrainMeshNode"));
                else foreach (var offset in p.InstanceOffsets)
                    list.Add(new(p.Id, meshKey, p.Transform.Apply(offset).ViewMatrix, false, Appearance: p.Mesh.Appearance, TypeKey: typeKey, HideFar: p.MaxStreamingDistance));
            }
            if (p.Mesh is null || !_assets.ContainsKey(p.Mesh.Hash)) placeholders.Add(Placeholder(p.Id, p.Transform, false));
        }
        return new() { Sector = sector, ByMesh = byMesh.ToDictionary(e => e.Key, e => e.Value.ToArray()), Placeholders = placeholders.ToArray(), Markers = markers.ToArray(), FoliageCells = foliageCells,
            Decals = decals is null ? null : new(decals.ToArray()) };
    }

    // Bended placements draw their rebuilt mesh, never the straight template they reference.
    // A mesh wearing project materials draws its dressed drawable (StudioSession.Materials), an
    // edited mesh its edited one (StudioSession.MeshEdit; dressing an edited mesh dresses that).
    internal ulong MeshKeyFor(string id, MeshReference mesh) => _dressedKeys.TryGetValue(id, out var dressed) && _assets.ContainsKey(dressed) ? dressed
        : _editedKeys.TryGetValue(id, out var edited) && _assets.ContainsKey(edited) ? edited
        : _bended.ContainsKey(id) || _bending.ContainsKey(id) ? BendedKey(id) : mesh.Hash;

    private void AddInstance(List<RenderPlacement> instances, string id, MeshReference? mesh, PlacementTransform transform, bool selected, ImmutableArray<PlacementTransform> offsets = default,
        ImmutableArray<EntityPart> parts = default)
    {
        // A material being tried on shows as it is: no selection tint (the selection box stays).
        if (selected && Untinted(id)) selected = false;
        if (!parts.IsDefaultOrEmpty)
        {
            foreach (var part in parts)
            {
                ulong key = DoorPartKey(part.Mesh);
                if (_assets.ContainsKey(key)) instances.Add(new(id, key, transform.Apply(part.Local).ViewMatrix, selected, Appearance: part.Mesh.Appearance));
            }
            return;
        }
        if (mesh is not null && _assets.ContainsKey(MeshKeyFor(id, mesh)))
        {
            if (offsets.IsDefaultOrEmpty) instances.Add(new(id, MeshKeyFor(id, mesh), transform.ViewMatrix, selected, Appearance: mesh.Appearance));
            else foreach (var offset in offsets) instances.Add(new(id, MeshKeyFor(id, mesh), transform.Apply(offset).ViewMatrix, selected, Appearance: mesh.Appearance));
        }
        // Markers are drawn as icons, not placeholders.
        else if (_showPlaceholders || (selected && mesh is not null)) instances.Add(Placeholder(id, transform, selected));
    }

    // TypeKey carries a packed 0xRRGGBB color (+ bit 32 set) for the "by node type" view.
    // Computed, not cached: BuildSector runs on several threads (UI included), and a
    // contended cache lock would be a message-pumping wait on the UI thread.
    private const ulong FoliageTypeKey = (1UL << 32) | 0x46B43CUL;
    private static ulong TypeKey(string t) => (1UL << 32) | t switch
    {
        "worldStaticMeshNode" => 0x5A8CF0UL,
        "worldInstancedMeshNode" => 0x33C8C8UL,
        "worldInstancedDestructibleMeshNode" or "worldPhysicalDestructionNode" => 0xF08C33UL,
        "worldFoliageNode" => FoliageTypeKey & 0xFFFFFFUL,
        "worldAdvertisementNode" => 0xF0D23CUL,
        "worldWaterPatchNode" => 0x6EB4FFUL,
        _ when t.Contains("Proxy") => 0x8C8C8CUL,
        _ => 0xC878D2UL
    };

    private static RenderPlacement Placeholder(string id, PlacementTransform transform, bool selected)
        => new(id, 0, Matrix4x4.CreateScale(.25f) * Matrix4x4.CreateTranslation(Coordinates.GameToView(transform.Position)), selected, true);
    // Built on demand (walk mode, placement) and at most once a second while streaming.
    public PlacementGrid Grid
    {
        get
        {
            if (_gridDirty && (_now - _gridBuilt > 1 || _grid.Count == 0))
            {
                var grid = new PlacementGrid();
                foreach (var instance in _published.SelectMany(s => s.Groups).SelectMany(g => g.Items))
                    // Light gradients are light, not something to stand or to place on. Nor are the
                    // game's plants (hundreds of thousands around some places): a click, a road, a
                    // dropped object or a walker meets the ground they grow on, as in the game.
                    if (!instance.Placeholder && instance.TypeKey != FoliageTypeKey && !LightGradient.IsMesh(instance.MeshHash) && _assets.TryGetValue(instance.MeshHash, out var mesh)) grid.Add(instance.Id, mesh, instance.Transform);
                _grid = grid; _gridDirty = false; _gridBuilt = _now;
            }
            return _grid;
        }
    }
    private PlacementGrid _grid = new();
    // --- Camera -------------------------------------------------------------------

    public void GoTo(CameraBookmark bookmark)
    {
        Camera.Yaw = bookmark.Yaw; Camera.Pitch = bookmark.Pitch;
        Camera.SetEye(Coordinates.GameToView(new(bookmark.X, bookmark.Y, bookmark.Z)));
        _nextPlan = 0;
    }

    public CameraBookmark CurrentView(string name)
    {
        var eye = Coordinates.ViewToGame(Camera.Eye);
        return new(name, eye.X, eye.Y, eye.Z, Camera.Yaw, Camera.Pitch);
    }

    public void FrameSelection()
    {
        if (SelectedElevator is { } elevator)
        {
            if (SelectedElevatorFloor >= 0) GoToElevatorFloor(SelectedElevatorFloor); else FrameElevator(elevator);
            return;
        }
        var selected = Selected;
        if (selected is not null && EntityBounds(selected, Preview ?? selected.Transform) is { } entity) Camera.Frame(entity);
        else if (selected?.Mesh is not null && _assets.TryGetValue(MeshKeyFor(selected.Id, selected.Mesh), out var mesh))
            Camera.Frame((Preview ?? selected.Transform).TransformBounds(mesh.Bounds));
        else if (selected is not null)
        {
            var center = Coordinates.GameToView(selected.Transform.Position);
            Camera.Frame(new(center - new Vector3(2), center + new Vector3(2)));
        }
        _nextPlan = 0;
    }

    // View-space bounds of an entity's loaded parts (doors, devices), or null.
    internal Bounds3? EntityBounds(ScenePlacement item, PlacementTransform transform)
    {
        var parts = item.Original?.EntityParts ?? default;
        if (parts.IsDefaultOrEmpty) return null;
        Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
        foreach (var part in parts)
        {
            if (!_assets.TryGetValue(part.Mesh.Hash, out var mesh)) continue;
            var b = transform.Apply(part.Local).TransformBounds(mesh.Bounds);
            min = Vector3.Min(min, b.Min); max = Vector3.Max(max, b.Max);
        }
        return float.IsFinite(min.X) ? new Bounds3(min, max) : null;
    }

    // --- Selection and editing ----------------------------------------------------
    // The last selected object is the primary: the gizmo and inspector edit it, and
    // the same rigid change is applied to the rest of the selection about its pivot.

    private readonly List<string> _selection = [];
    private readonly HashSet<string> _selectionSet = [];
    private Dictionary<string, PlacementTransform>? _previews;
    public IReadOnlyList<string> SelectedIds => _selection;
    public bool IsSelected(string id) => _selectionSet.Contains(id);
    public IReadOnlyDictionary<string, PlacementTransform>? Previews => _previews;
    public PlacementTransform? Preview => SelectedId is not null && _previews is not null && _previews.TryGetValue(SelectedId, out var t) ? t : null;
    public long SelectionVersion { get; private set; }

    public void Select(string? id)
    {
        if (id is null ? _selection.Count == 0 : _selection.Count == 1 && SelectedId == id) return;
        _selection.Clear(); _selectionSet.Clear();
        if (id is not null) { _selection.Add(id); _selectionSet.Add(id); }
        SelectionChanged(id);
    }

    public void SelectMany(IEnumerable<string> ids)
    {
        _selection.Clear(); _selectionSet.Clear();
        foreach (var id in ids) if (!string.IsNullOrEmpty(id) && _selectionSet.Add(id)) _selection.Add(id);   // scripts may send nulls
        SelectionChanged(_selection.LastOrDefault());
    }

    public void ToggleSelect(string id)
    {
        if (_selectionSet.Remove(id)) _selection.Remove(id);
        else { _selectionSet.Add(id); _selection.Add(id); }
        SelectionChanged(_selection.LastOrDefault());
    }

    private void SelectionChanged(string? primary)
    {
        SelectedId = primary; _previews = null; _sceneDirty = true; SelectionVersion++;
        // An object selected means the elevator is not (and the other way round, SelectElevator).
        if (primary is not null && SelectedElevatorId is not null) { SelectedElevatorId = null; SelectedElevatorFloor = -1; _elevatorPreview = null; ElevatorVersion++; }
    }

    // One pass over the document; selections are small, the document is large.
    public IReadOnlyList<ScenePlacement> SelectedObjects()
    {
        if (_selection.Count == 0) return [];
        if (_selection.Count == 1) return Selected is { } single ? [single] : [];
        return _selection.Select(Document.TryGetObject).OfType<ScenePlacement>().ToArray();
    }

    private Dictionary<string, PlacementTransform> Propagate(PlacementTransform primary)
    {
        var result = new Dictionary<string, PlacementTransform>();
        if (Selected is not { } lead) return result;
        result[lead.Id] = primary;
        if (IsLocked(lead.Id)) { result.Clear(); Status = LockedMessage(lead.Id); return result; }
        if (_selection.Count == 1) return result;
        Matrix4x4.Invert(lead.Transform.ViewMatrix, out var inverse);
        var delta = inverse * primary.ViewMatrix;
        var scale = new Vector3(primary.Scale.X / lead.Transform.Scale.X, primary.Scale.Y / lead.Transform.Scale.Y, primary.Scale.Z / lead.Transform.Scale.Z);
        foreach (var other in SelectedObjects())
        {
            if (other.Id == lead.Id || !other.CanEdit || other.Deleted || IsLocked(other.Id)) continue;
            try { result[other.Id] = PlacementTransform.FromViewMatrix(other.Transform.ViewMatrix * delta); }
            // Non-uniform scaling of differently rotated objects would shear them:
            // scale each one in place instead.
            catch (InvalidDataException) { result[other.Id] = other.Transform with { Scale = other.Transform.Scale * scale }; }
        }
        return result;
    }

    public void PreviewTransform(PlacementTransform transform)
    {
        try { _previews = Propagate(transform.Validated()); }
        catch (InvalidDataException ex) { Error = ex.Message; }
    }

    public void CommitPreview()
    {
        if (_previews is { Count: > 0 } previews) Apply(() => Document.TransformMany(previews));
        _previews = null; _sceneDirty = true;
    }

    public void CancelPreview() { _previews = null; _sceneDirty = true; }

    public void SetTransform(PlacementTransform transform)
    {
        if (SelectedId is null) return;
        _previews = null;
        try { var all = Propagate(transform.Validated()); Apply(() => Document.TransformMany(all)); }
        catch (InvalidDataException ex) { Error = ex.Message; }
    }

    public void DeleteSelected()
    {
        if (SelectedElevator is not null) { DeleteSelectedElevator(); return; }
        var targets = EditableSelection(Loc.T("removed")).Select(o => o.Id).ToArray();
        if (targets.Length != 0 && Apply(() => Document.DeleteMany(targets))) Select(null);
    }

    public void DuplicateSelected()
    {
        if (SelectedElevator is not null) { DuplicateSelectedElevator(); return; }
        var targets = SelectedObjects().Where(o => o.CanEdit && !o.Deleted).Select(o => o.Id).ToArray();
        IReadOnlyList<string> created = [];
        if (targets.Length != 0 && Apply(() => created = Document.DuplicateMany(targets))) SelectMany(created);
    }

    public void ResetSelected()
    {
        var targets = EditableSelection(Loc.T("reset"))
            .ToDictionary(o => o.Id, o => o.Original?.Transform ?? PlacementTransform.Identity with { Position = o.Transform.Position });
        if (targets.Count != 0) Apply(() => Document.TransformMany(targets));
    }

    // Lowers each selected object until its bounds rest on the first surface below it.
    public void DropSelectedToGround()
    {
        if (SelectedElevator is not null) { SnapElevatorToGround(); return; }
        var moves = new Dictionary<string, PlacementTransform>();
        foreach (var item in EditableSelection(Loc.T("dropped")).Where(o => o.Mesh is not null))
        {
            if (!_assets.TryGetValue(item.Mesh!.Hash, out var mesh)) continue;
            var bounds = item.Transform.TransformBounds(mesh.Bounds);
            var probe = new Vector3(bounds.Center.X, bounds.Min.Y + .05f, bounds.Center.Z);
            var hit = Grid.Raycast(new(probe, -Vector3.UnitY), 500, item.Id);
            if (hit is not { } distance) continue;
            float drop = distance - .05f;
            moves[item.Id] = item.Transform with { Position = item.Transform.Position - new Vector3(0, 0, drop) };
        }
        if (moves.Count == 0) { Status = Loc.T("No surface found below the selection."); return; }
        Apply(() => Document.TransformMany(moves));
        Status = moves.Count == 1 ? Loc.T("Dropped to the ground.") : Loc.F("Dropped {0} objects to the ground.", moves.Count);
    }

    public void Undo() { Document.Undo(); _previews = null; _elevatorPreview = null; _sceneDirty = true; ElevatorVersion++; }
    public void Redo() { Document.Redo(); _previews = null; _elevatorPreview = null; _sceneDirty = true; ElevatorVersion++; }

    // lineUp: beside another object, it is turned and slid so their edges meet (StudioSession.Placing).
    public void AddMesh(ArchiveResource resource, Vector3 gamePosition, bool lineUp = false)
    {
        var transform = PlacementTransform.Identity with { Position = gamePosition };
        string? beside = null; bool later = false;
        if (lineUp && Settings.AlignPlaced)
        {
            // Its size is known once its geometry has been shown: otherwise it lines up as it arrives.
            if (!_assets.ContainsKey(resource.Hash)) later = true;
            else if (LinedUp(resource.Hash, transform) is { } lined) { transform = lined.Transform; beside = lined.With; }
        }
        string? id = null;
        // Custom meshes are named after their library entry, game ones after their file.
        if (Apply(() => id = Document.Add(new(resource.Hash, resource.Path), transform, CustomMeshes.ByHash(resource.Hash)?.Name)))
        {
            Select(id); _nextPlan = 0;
            Status = beside is { Length: > 0 } ? Loc.F("Added {0}, lined up with {1} (hold Alt to place freely).", Path.GetFileName(resource.Path), beside)
                : Loc.F("Added {0}; its geometry streams in like the rest of the world.", Path.GetFileName(resource.Path));
            _lineUpPending = later ? (id!, resource.Hash, _now + 15) : null;
        }
    }

    private bool Apply(Action action)
    {
        try { action(); Error = ""; _sceneDirty = true; return true; }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException) { Error = ex.Message; return false; }
    }

    // Prefer the drawn LOD so the pick matches what the user sees.
    // decals: a decal drawn under the cursor is picked before the surface it lies on (the Elements
    // tool, or Alt held: a click otherwise takes the wall or the ground, not the stain on it).
    // The same goes for the light gradients in front of facades: a click goes through them.
    public string? Pick(Vector2 pixel, Vector2 viewport, bool additive = false, bool decals = false)
    {
        if (Layer != EditLayer.Scene)
        {
            // Collision layer: collision only. Occlusion layer: occluders first, then meshes (their occlusion settings).
            var target = Layer == EditLayer.Collision ? PickCollision(pixel, viewport) : PickOccluder(pixel, viewport) ?? HitTest(pixel, viewport);
            if (SelectedRoadId is not null) SelectRoad(null);
            if (additive) { if (target is not null) ToggleSelect(target); }
            else Select(target);
            return target;
        }
        // Icons (lights, sounds, effects, decals) sit in front of the geometry.
        var nearest = IconAt(pixel)?.Id ?? (decals ? DecalAt(pixel, viewport) : null) ?? HitTest(pixel, viewport, lights: decals);
        if (RoadIdOf(nearest) is { } road) { SelectRoad(road); return nearest; }
        if (SelectedRoadId is not null) SelectRoad(null);
        // Elevators: a floor's label or handle, or a piece (its panel and door pick their floor).
        if (!additive && (ElevatorFloorAt(pixel, viewport) is { } floor ? (floor.Id, floor.Floor) : ElevatorIdOf(nearest) is { } lift ? (lift, ElevatorFloorOf(nearest)) : default((string, int)?)) is { } elevator)
        { SelectElevator(elevator.Item1, elevator.Item2); return nearest; }
        if (SelectedElevatorId is not null && !additive) SelectElevator(null);
        if (additive) { if (nearest is not null) ToggleSelect(nearest); }
        else Select(nearest);
        return nearest;
    }

    // Rays from the cursor follow the view: with faces as the game draws them, the culled back of a
    // one-sided part is neither picked nor a surface to place on.
    internal bool PickFronts => _renderer.GameFaces && !_renderer.Wireframe;

    // Diagnostic: every placement along the pixel ray, nearest first.
    public IEnumerable<(string Id, string Mesh, float Distance)> HitAll(Vector2 pixel, Vector2 viewport)
    {
        if (_renderer.RenderedWorldCamera is not { } frame) yield break;
        var ray = ScenePicking.ViewRay(pixel, viewport, frame.View, frame.Projection);
        var hits = new List<(string, string, float)>();
        foreach (var item in _renderer.DrawnPlacements)
        {
            if (!_assets.TryGetValue(item.MeshHash, out var mesh) || mesh.SourceHash != item.MeshFingerprint) continue;
            if (ScenePicking.Intersect(ray, mesh, item.RelativeModel, item.LodMask, PickFronts) is { } d) hits.Add((item.Id, mesh.ResourcePath, d));
        }
        foreach (var h in hits.OrderBy(h => h.Item3)) yield return h;
    }

    public string? HitTest(Vector2 pixel, Vector2 viewport, bool lights = false)
    {
        if (_renderer.RenderedWorldCamera is not { } frame) return null;
        var ray = ScenePicking.ViewRay(pixel, viewport, frame.View, frame.Projection);
        var locked = LockedIds;
        return NearestDrawn(ray, frame, item => locked.Count == 0 || !locked.Contains(item.Id), lights) is { } hit ? hit.Item.Id : null;   // locked: clicks go through
    }

    // The nearest drawn placement under a camera-relative ray: boxes first, exact triangles only
    // for the boxes that could still be nearer than the best hit.
    // lights: the game's light gradients too. Drawn as the light they are (Material shading), their
    // planes are not under the pointer: what they light is.
    internal (DrawnPlacement Item, float Distance)? NearestDrawn(Ray3 ray, CameraFrame frame, Func<DrawnPlacement, bool>? accept = null, bool lights = false)
    {
        bool through = !lights && _renderer.Shading == SceneShading.Material;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        // Boxes first, straight from the renderer's visible instances: a placement (and its
        // camera-relative matrix) is only built for the boxes the ray enters.
        int count = _renderer.VisibleCount;
        _pickBoxes.Clear();
        for (int i = 0; i < count; i++)
        {
            var bounds = _renderer.VisibleBounds(i);
            if (bounds.Extents == Vector3.Zero && bounds.Center == Vector3.Zero) { _pickBoxes.Add((i, 0)); continue; }   // no box known: tested exactly
            if (ScenePicking.Enter(ray, bounds.Center - frame.Origin, bounds.Extents) is { } enter) _pickBoxes.Add((i, enter));
        }
        FrameProfile.Record("pick.boxes", watch.Elapsed.TotalMilliseconds); watch.Restart();
        int exact = 0; bool fronts = PickFronts;
        var nearest = ScenePicking.Nearest(_pickBoxes, i =>
        {
            if (_renderer.VisibleAt(i) is not { } item || (accept is not null && !accept(item))) return null;
            if (through && LightGradient.IsMesh(item.MeshHash)) return null;
            if (!_assets.TryGetValue(item.MeshHash, out var mesh) || mesh.SourceHash != item.MeshFingerprint) return null;
            exact++;
            return ScenePicking.Intersect(ray, mesh, item.RelativeModel, item.LodMask, fronts);
        });
        FrameProfile.Record("pick.exact", watch.Elapsed.TotalMilliseconds); FrameProfile.Record("pick.tests", exact);
        // What the draw distance cuts away is not under the pointer either.
        if (nearest is { } cut && _renderer.DrawDistance > 0 && cut.Distance > _renderer.DrawDistance) return null;
        return nearest is { } n && _renderer.VisibleAt(n.Index) is { } hit ? (hit, n.Distance) : null;
    }
    private readonly List<(int Index, float Enter)> _pickBoxes = [];
    // World-space point under the cursor, or a point in front of the camera.
    public Vector3 PlacementPoint(Vector2 pixel, Vector2 viewport)
    {
        if (_renderer.RenderedWorldCamera is { } frame)
        {
            var ray = ScenePicking.ViewRay(pixel, viewport, frame.View, frame.Projection);
            var world = new Ray3(ray.Origin + frame.Origin, ray.Direction);
            // Not onto what the draw distance hides.
            float reach = _renderer.DrawDistance > 0 ? MathF.Min(400, _renderer.DrawDistance) : 400;
            if (Grid.Raycast(world, reach, frontOnly: PickFronts) is { } hit) return Coordinates.ViewToGame(world.Origin + world.Direction * hit);
        }
        return Coordinates.ViewToGame(Camera.Eye + Camera.Forward * 8);
    }

    // --- Projects -----------------------------------------------------------------

    public void NewProject()
    {
        TryAutosave();
        var resident = Document.ResidentSectors.Values.ToArray();
        Document = new(ProjectData.Create(Loc.T("Untitled world")));
        foreach (var sector in resident) Document.Attach(sector, remember: false);
        ProjectPath = null; Select(null); _sceneDirty = true; Error = "";
        Status = Loc.T("New world project. Edits are stored separately from the game files.");
    }

    public void SaveProject(string path)
    {
        ProjectStorage.WriteAtomic(path, Document.Project); Document.MarkSaved(); ProjectPath = Path.GetFullPath(path);
        Settings.RememberProject(ProjectPath, Document.Project.Name);
        Status = Loc.F("Project saved: {0}", Path.GetFileName(path));
    }

    public async Task OpenProjectAsync(string path)
    {
        if (Busy) return;
        Busy = true; Error = "";
        try
        {
            var project = await Task.Run(() => ProjectStorage.Read(path));
            await ShowProjectAsync(project, Path.GetFullPath(path));
            Settings.RememberProject(ProjectPath!, project.Name);
            Status = Loc.F("Project opened: {0}", Path.GetFileName(path));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error = ex.Message; }
        finally { Busy = false; }
    }

    // A project from elsewhere (the cloud, a collaboration session): no file yet, nothing unsaved.
    public async Task<bool> OpenProjectDataAsync(ProjectData project, string status)
    {
        if (Busy) return false;
        Busy = true; Error = "";
        try
        {
            await ShowProjectAsync(project, null);
            Status = status;
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) { Error = ex.Message; return false; }
        finally { Busy = false; }
    }

    private async Task ShowProjectAsync(ProjectData project, string? path)
    {
        if (Index is null && (project.Sources.Count != 0 || project.Changes.Added.Count != 0))
            throw new InvalidOperationException("Open the game's archives before reopening this world project.");
        TryAutosave();
        // Edited sources must be resident to show and validate their edits.
        var restored = new List<SectorSnapshot>();
        foreach (var source in project.Sources.Values.Where(s => !Document.ResidentSectors.ContainsKey(s.Hash)))
        {
            Status = Loc.F("Restoring sector {0}", source.Path);
            restored.Add(await Index!.LoadSectorAsync(source.Hash, _shutdown.Token));
        }
        // The streamed sectors as they are now, after the awaits: those that arrived meanwhile
        // went to the old document only, and a snapshot taken before would lose them for good.
        var document = BuildDocument(project);
        foreach (var sector in restored) document.Attach(sector);
        document.MarkSaved();
        ReplaceDocument(document, path);
        var focus = project.Changes.Added.Values.Select(a => a.Transform.Position)
            .Concat(project.Changes.Modified.Values.Select(m => m.Transform?.Position).OfType<Vector3>()).ToArray();
        if (focus.Length != 0)
        {
            var center = focus.Aggregate(Vector3.Zero, (a, b) => a + b) / focus.Length;
            Camera.Frame(new(Coordinates.GameToView(center) - new Vector3(20), Coordinates.GameToView(center) + new Vector3(20)));
        }
    }

    // A document for this project over the sectors already streamed (a collaboration session
    // rebuilds its project this way); its other sources follow (RestoreSourcesAsync).
    public EditorDocument BuildDocument(ProjectData project)
    {
        var document = new EditorDocument(project);
        foreach (var sector in Document.ResidentSectors.Values) document.Attach(sector, remember: false);
        return document;
    }

    public void ReplaceDocument(EditorDocument document, string? path)
    {
        Document = document; ProjectPath = path; Select(null); _sceneDirty = true; _nextPlan = 0;
        _previews = null; _elevatorPreview = null; ElevatorVersion++;
        _streamRevision = -1;   // next frame: attach pass (repairs anything still missing)
    }

    // Loads the edited sources the document lacks (an edit from a collaborator, a rebuilt
    // document), attached to it unless another document replaced it meanwhile.
    public async Task RestoreSourcesAsync(IEnumerable<ulong> hashes)
    {
        if (Index is not { } index) return;
        var document = Document;
        foreach (var hash in hashes.Distinct().Where(h => !document.ResidentSectors.ContainsKey(h)).ToArray())
        {
            try
            {
                var sector = await index.LoadSectorAsync(hash, _shutdown.Token);
                if (ReferenceEquals(document, Document) && !document.ResidentSectors.ContainsKey(hash)) { document.Attach(sector); _sceneDirty = true; }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or KeyNotFoundException or InvalidOperationException) { Error = Loc.F("A sector of the project could not be loaded: {0}", ex.Message); }
        }
    }

    public string RecoveryDirectory => Path.Combine(StudioSettings.StateDirectory, "Recovery");
    private double _lastAutosave;
    private void AutosaveIfNeeded(double now)
    {
        if (now - _lastAutosave < 30 || Busy) return;
        _lastAutosave = now; TryAutosave();
    }
    public void TryAutosave()
    {
        if (!Document.IsDirty) return;
        try { ProjectStorage.WriteAtomic(Path.Combine(RecoveryDirectory, $"{Document.Project.Id:N}.ncwe.json"), Document.Project); }
        catch (Exception ex) { Error = Loc.F("Autosave failed: {0}", ex.Message); }
    }

    // The export under way, which Cancel stops (it removes what it wrote).
    private CancellationTokenSource? _exportCancel;
    public void CancelExport() => _exportCancel?.Cancel();
    // What the last export added on its own or left out (the export window lists them).
    public IReadOnlyList<string> LastExportNotes { get; private set; } = [];
    // The last export was stopped (Cancel), not failed.
    public bool LastExportCancelled { get; private set; }
    // The export's steps on the status line while it runs (a report arriving once it is over
    // would cover its result).
    private IProgress<string> ExportStatus() => new Progress<string>(message => { if (Busy) SetStatus(message); });

    // `stages`: the export's stages as they go (the export window); `tracker`: a caller's plan
    // with stages of its own after the export's.
    public async Task<ExportResult?> ExportAsync(string folder, IProgress<ExportProgress>? stages = null, ExportTracker? tracker = null)
    {
        if (Busy || Index is null) { Error = Loc.T("Open the game and wait for the current operation to finish."); return null; }
        if (!HasWorldChanges && CustomSoundCount > 0)
        { Error = Loc.T("This project only has custom sounds: Open77 plays them, so they are exported with File ▸ Export for Open77 (an ArchiveXL mod cannot carry new sounds)."); return null; }
        // Collision left behind by meshes deleted earlier (loaded sectors only) joins the export.
        int collision = 0;
        try { collision = Document.DeleteOrphanCollision(); if (collision > 0) _sceneDirty = true; }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException) { Error = ex.Message; }
        Busy = true; Error = ""; LastExportCancelled = false;
        using var cancel = _exportCancel = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        try
        {
            tracker ??= ModExporter.Plan(Document.Project, Index, stages, ExportStatus());
            var result = await ModExporter.ExportAsync(Document.Project, Index, folder, null, cancel.Token, tracker);
            LastExportDirectory = result.Directory;
            int sounds = CustomSoundCount;
            string collisionNote = collision > 0 ? Loc.F(" · the collision of deleted meshes was added ({0} actor{1}, Ctrl+Z to undo)", collision, (collision == 1 ? "" : "s")) : "";
            string soundNote = sounds > 0 ? Loc.F(" · {0} custom sound{1} Open77-only (not in this mod)", sounds, (sounds == 1 ? Loc.T(" is") : Loc.T("s are"))) : "";
            LastExportNotes = [.. new[] { collisionNote, soundNote }.Where(n => n.Length > 0).Select(n => n.TrimStart(' ', '·'))];
            Status = Loc.F("Exported {0} moves, {1} deletions and {2} additions to {3}{4}{5}", result.Manifest.Transformed, result.Manifest.Deleted, result.Manifest.Added, result.Directory, collisionNote, soundNote);
            return result;
        }
        catch (OperationCanceledException) { LastExportCancelled = true; if (!_shutdown.IsCancellationRequested) Status = Loc.T("Export cancelled"); return null; }
        catch (Exception ex) { Error = ex.Message; Status = Loc.T("Export failed"); return null; }
        finally { _exportCancel = null; Busy = false; }
    }

    // Open77: the export is packaged into a world resource (open77.lua + dist/*.zip) that the
    // dedicated server preloads as a required mod. The plain export is staged in the state folder.
    public async Task<Open77ResourceReport?> ExportOpen77Async(string resourceFolder, IProgress<ExportProgress>? stages = null)
    {
        if (!Open77ResourceExporter.IsResourceName(Path.GetFileName(Path.GetFullPath(resourceFolder).TrimEnd(Path.DirectorySeparatorChar))))
        { Error = Loc.F("Open77 resource folders are named in lowercase letters, digits, _ or - (the folder name is the resource name): \"{0}\".", Path.GetFileName(resourceFolder)); return null; }
        // Custom sounds are checked first: a missing file or too many synced loops stop the export early.
        Open77SoundSet sounds;
        try { sounds = SoundsForOpen77(); }
        catch (InvalidDataException ex) { Error = ex.Message; Status = Loc.T("Open77 export failed"); return null; }
        string staging = Path.Combine(StudioSettings.StateDirectory, "Open77Exports");
        // Only sounds: no world package to build.
        ExportResult? export = null;
        bool world = HasWorldChanges || sounds.IsEmpty;
        // The export's stages, then the resource's packaging.
        var tracker = world && Index is not null ? ModExporter.Plan(Document.Project, Index, stages, ExportStatus()) : new ExportTracker(stages, ExportStatus());
        tracker.Append("open77", "Packaging the Open77 resource", 1.5);
        if (world) { if (await ExportAsync(staging, stages, tracker) is not { } built) return null; export = built; }
        try
        {
            tracker.Begin("open77", 0, Path.GetFileName(resourceFolder));
            var report = await Task.Run(() => export is null ? Open77ResourceExporter.WriteSoundsOnly(resourceFolder, sounds, Document.Project.Name, Document.Project.Id)
                : Open77ResourceExporter.Write(export, resourceFolder, sounds));
            tracker.Next();
            Settings.Open77Resources[Document.Project.Id.ToString("N")] = report.ResourceDirectory; Settings.Save();
            Status = export is null
                ? Loc.F("Open77 resource {0} {1}: {2} custom sound{3}, nothing to preload. Restart the server (new scripts need it).", report.ResourceName, report.Version, report.Sounds, (report.Sounds == 1 ? "" : "s"))
                : Loc.F("Open77 resource {0} {1}: {2} ({3:N0} bytes){4}. Restart the server, then launch the game from the launcher.", report.ResourceName, report.Version, report.PackagePath, report.PackageBytes, (report.Sounds > 0 ? Loc.F(", {0} custom sound{1}", report.Sounds, (report.Sounds == 1 ? "" : "s")) : ""));
            Error = "";
            if (export is not null) PruneStaging(staging, export.Directory);
            return report;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { Error = ex.Message; Status = Loc.T("Open77 export failed"); return null; }
    }

    // Keeps the last staged exports of each project only.
    private static void PruneStaging(string staging, string keep)
    {
        try
        {
            foreach (var old in new DirectoryInfo(staging).EnumerateDirectories("ncwe-*").OrderByDescending(d => d.CreationTimeUtc).Skip(5))
                if (!string.Equals(old.FullName, keep, StringComparison.OrdinalIgnoreCase)) old.Delete(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public string? Open77ResourceFolder => Settings.Open77Resources.GetValueOrDefault(Document.Project.Id.ToString("N"));

    public WorldBuilderReport? ExportWorldBuilder(string folder)
    {
        if (Index is null) { Error = Loc.T("Open the game first."); return null; }
        // entSpawner places game resources only: custom meshes need the archive of an ArchiveXL or Open77 export.
        if (Document.Project.Changes.Added.Values.FirstOrDefault(a => CustomMeshes.Has(a.Mesh.Hash)) is { } custom)
        { Error = Loc.F("\"{0}\" is a custom mesh: World Builder cannot carry it. Export an ArchiveXL mod or an Open77 resource instead.", custom.Name); return null; }
        int sounds = CustomSoundCount;
        try
        {
            var index = Index;
            var report = WorldBuilderExporter.Write(Document.Project, folder, index.GameDirectory, m => index.Find(m.Hash) is not null);
            Status = Loc.F("World Builder file written: {0} ({1} objects{2}{3}", report.JsonPath, report.ExportedNodes, (report.SkippedTransforms + report.SkippedDeletions > 0 ? Loc.F(", {0} vanilla edits not representable)", report.SkippedTransforms + report.SkippedDeletions) : ")"), (sounds > 0 ? Loc.F(" · {0} custom sound{1} Open77-only (not included)", sounds, (sounds == 1 ? Loc.T(" is") : Loc.T("s are"))) : ""));
            Error = "";
            return report;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or InvalidDataException) { Error = ex.Message; return null; }
    }

    // Re-reads every edited source sector and checks fingerprints and placement guards.
    public async Task<IReadOnlyList<string>> ValidateAsync()
    {
        if (Index is null) return ["Open the game first."];
        var fresh = new Dictionary<ulong, SectorSnapshot>();
        foreach (var source in Document.Project.Sources.Values) fresh[source.Hash] = await Index.LoadSectorAsync(source.Hash, _shutdown.Token);
        return ExportPreparation.Validate(Document.Project, fresh);
    }

    public async Task InstallLastExportAsync()
    {
        if (Busy || Index is null || LastExportDirectory is null) return;
        Busy = true; Error = ""; Status = Loc.T("Revalidating the export before installation...");
        try
        {
            string receipt = await ModInstallation.InstallAsync(LastExportDirectory, Index, Path.Combine(StudioSettings.StateDirectory, "InstalledMods"), _shutdown.Token);
            Status = Loc.F("Mod installed. Start the game to see your changes. Receipt: {0}", receipt);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error = ex.Message; }
        finally { Busy = false; }
    }

    public void RemoveMod(string receipt)
    {
        if (Index is null || Busy) { Error = Loc.T("Wait for the game archives to be indexed."); return; }
        try { ModInstallation.Uninstall(receipt, Index.GameDirectory); Status = Loc.T("Removed the verified files of this NCWE mod."); Error = ""; }
        catch (Exception ex) { Error = ex.Message; }
    }

    // --- Lifetime -----------------------------------------------------------------

    public bool ShutdownRequested => _shutdown.IsCancellationRequested;
    public async Task ShutdownAsync()
    {
        TryAutosave();
        Settings.Camera = CurrentView(Loc.T("Last position")); Settings.Save();
        _shutdown.Cancel();
        await StopStreamingAsync();
        while (Busy) await Task.Delay(20);
    }

    // --- Preflight ------------------------------------------------------------------------
    // Once the visible neighborhood is loaded, a separate worker process converts a wider
    // area (sectors and the meshes they use) into the disk cache, nearest first, so that
    // travelling there later reads converted files instead of decoding game archives.
    public PreflightWorker Preflight { get; } = new();
    private readonly List<(Vector3 Center, float Radius)> _preflightDone = [];
    private double _streamBusyAt, _preflightStarted = double.NegativeInfinity;
    private bool _preflightWholeCity;
    private static float PreflightRadius(ViewDistance distance) => distance switch { ViewDistance.Near => 400, ViewDistance.Far => 1200, _ => 700 };

    private void AutoPreflight(double now)
    {
        if (Index is null || _streamer is null || Settings.Game is null) return;
        if (Preflight.Completed && Preflight.Center is var finished && !_preflightDone.Contains((finished, Preflight.Radius)))
        { _preflightDone.Add((finished, Preflight.Radius)); _preflightWholeCity = false; }
        if (!Settings.Preflight) return;
        // A hold is for working in one place without anything loading: the surroundings wait too.
        if (Hold != LoadingHold.Follow) { if (Preflight.Running && !_preflightWholeCity) { Preflight.Stop(); _preflightStarted = now; } return; }
        // Memory first: near 90 % system load the GC turns to blocking collections for every
        // process; a background worker must not push the machine (or a running game) there.
        double load = MemoryLoad();
        if (Preflight.Running && load > .93) { Preflight.Stop(); _preflightStarted = now; return; }
        if (_preflightWholeCity) return;
        if (_streamer.HasActiveWork || _renderer.PendingWorldUploads > 0 || load > .82) { _streamBusyAt = now; return; }
        if (now - _streamBusyAt < 2) return; // the visible neighborhood first
        var position = Coordinates.ViewToGame(Camera.Eye);
        float radius = PreflightRadius(Distance);
        if (Preflight.Running)
        {
            // Far from the area being prepared: restart around the camera (at most every 10 s).
            if (Vector3.Distance(position, Preflight.Center) > radius * .6f && now - _preflightStarted > 10) StartPreflight(position, radius, now);
            return;
        }
        if (_preflightDone.Any(d => Vector3.Distance(position, d.Center) + radius * .5f <= d.Radius)) return;
        if (now - _preflightStarted < 5) return;
        StartPreflight(position, radius, now);
    }

    private double _memoryLoad, _memorySampled = double.NegativeInfinity;
    private double MemoryLoad()
    {
        if (_now - _memorySampled < 1) return _memoryLoad;
        _memorySampled = _now;
        return _memoryLoad = SystemMemory.Load();
    }

    private void StartPreflight(Vector3 center, float radius, double now)
    {
        var cache = Path.Combine(StudioSettings.StateDirectory, "Cache");
        try { Preflight.Start(Settings.Game!, StudioSettings.StateDirectory, center, radius, Math.Clamp(Environment.ProcessorCount / 3, 2, 8), DiskCacheBudget(cache)); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException) { Error = Loc.F("Preflight: {0}", ex.Message); }
        _preflightStarted = now;
    }

    // Explicit: prepare the whole city (all streamable sectors) in the background.
    public void PrepareWholeCity()
    {
        if (Index is null || Settings.Game is null) return;
        _preflightWholeCity = true;
        StartPreflight(Coordinates.ViewToGame(Camera.Eye), 20000, _now);
        Status = Loc.T("Preparing the cache for the whole city in the background…");
    }

    public void Dispose() { Preflight.Dispose(); Index?.Dispose(); _shutdown.Dispose(); if (GameSoundsReady is { } sounds) sounds.Dispose(); }
}
