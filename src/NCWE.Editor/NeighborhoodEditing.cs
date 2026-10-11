// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using System.Text.Json;
using ImGuiNET;
using NCWE.Core;
using NCWE.Formats;
using NCWE.Render;

namespace NCWE.Editor;

internal sealed partial class EditorWindow
{
    private NeighborhoodStreamer? _streamer;
    private CachedStreamingSource? _streamSource;
    private StreamingCatalog? _catalog;
    private SectorSpatialIndex? _spatial;
    private Task _streamStopTask = Task.CompletedTask;
    private readonly HashSet<ulong> _streamedSectors = [];
    private long _streamPublishedRevision = -1, _streamDocumentRevision = -1;
    private double _nextNeighborhoodPlan;
    private Vector3 _neighborhoodPosition = new(-1174, -1200, 32);
    private Vector3? _plannedNeighborhoodPosition;
    private float _neighborhoodRadius = 128;
    private int _neighborhoodSectorLimit = 24;
    private bool _followNeighborhood = true;

    private void BuildNeighborhoodControls()
    {
        if (!ImGui.CollapsingHeader("Neighborhood", ImGuiTreeNodeFlags.DefaultOpen)) return;
        ImGui.TextDisabled("Game coordinates (meters, Z up)");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputFloat3("##neighborhood-position", ref _neighborhoodPosition); Remember("neighborhood-position");
        ImGui.SetNextItemWidth(-1);
        ImGui.SliderFloat("##neighborhood-radius", ref _neighborhoodRadius, 32, 512, "Radius: %.0f m");
        ImGui.BeginDisabled(_streamer is not null || _busy);
        ImGui.SetNextItemWidth(-1); ImGui.SliderInt("##neighborhood-sector-limit", ref _neighborhoodSectorLimit, 2, 64, "Up to %d sectors");
        ImGui.EndDisabled();
        ImGui.BeginDisabled(_busy || _index is null);
        if (Button(_streamer is null ? "Load neighborhood" : "Go to position", "neighborhood-start"))
        {
            if (_streamer is null) _ = StartNeighborhood();
            else MoveNeighborhoodCamera(_neighborhoodPosition);
        }
        if (_streamer is not null)
        {
            ImGui.SameLine(); if (Button("Unload", "neighborhood-stop")) _ = StopNeighborhood();
        }
        ImGui.EndDisabled();
        if (_streamer is not null)
        {
            ImGui.Checkbox(_worldCamera.IsFlying ? "Follow camera position" : "Follow camera focus", ref _followNeighborhood);
            var m = _streamer.Metrics;
            ImGui.Text($"Sectors {m.ResidentSectors}/{m.DesiredSectors} · meshes {m.CachedMeshes}");
            ImGui.Text($"Queue {m.PendingMeshes} (+{m.DeferredMeshes}) · GPU {_renderer!.PendingWorldUploads}");
            ImGui.Text($"CPU {(m.SectorBytes + m.MeshBytes) / 1048576f:F0} / {_streamer.Budgets.CpuBytes / 1048576} MiB");
            ImGui.Text($"GPU {m.GpuReservedBytes / 1048576f:F0} / {_streamer.Budgets.GpuBytes / 1048576} MiB");
            ImGui.Text($"Cache {_streamSource!.CacheHits} hits · {m.Diagnostics} diagnostics");
            if (!_worldMode) ImGui.TextWrapped("Streaming paused while previewing a mesh. Return to World view to continue.");
            if (m.Diagnostics > 0 && ImGui.TreeNode("Loading diagnostics"))
            {
                foreach (var (key, message) in _streamer.Errors.Take(20)) ImGui.TextWrapped($"{(key.Mesh ? "Mesh" : "Sector")} {key.Hash}: {message}");
                ImGui.TreePop();
            }
        }
        ImGui.TextWrapped("Near static geometry · default variants. Distant geometry and quest state are not yet reproduced.");
        ImGui.Separator();
    }

    private async Task StartNeighborhood()
    {
        if (_busy || _index is null || _shutdown.IsCancellationRequested) return;
        _busy = true; _error = "";
        try
        {
            await StopNeighborhood();
            _shutdown.Token.ThrowIfCancellationRequested();
            if (_catalog is null)
            {
                var world = _index.Worlds.SingleOrDefault(w => w.Path.EndsWith("\\03_night_city.streamingworld", StringComparison.OrdinalIgnoreCase))
                    ?? throw new NotSupportedException("The Night City streaming world is absent from this archive profile.");
                _status = "Reading Night City streaming descriptors...";
                _catalog = await _index.LoadWorldCatalogAsync(world.Hash, new Progress<string>(s => _status = s), _shutdown.Token);
                _spatial = new(_catalog.Descriptors);
            }
            _shutdown.Token.ThrowIfCancellationRequested();
            _document ??= new(ProjectData.Create("Untitled world"));
            foreach (var hash in _document.ResidentSectors.Keys.ToArray()) _document.Evict(hash);
            _worldAssets.Clear(); _assetErrors.Clear(); _worldAssets[0] = SyntheticMesh.Cube();
            // Reserve the placeholder and the renderer's fixed instance stream.
            var budgets = new StreamingBudgets(MaximumSectors: _neighborhoodSectorLimit);
            budgets = budgets with { GpuBytes = budgets.GpuBytes - _worldAssets[0].CpuBytes - D3DRenderer.InstanceBufferReservationBytes,
                CpuBytes = budgets.CpuBytes - D3DRenderer.InstanceBufferReservationBytes };
            _streamSource = new(_index, Path.Combine(Program.StateDirectory, "Cache", "Meshes-v1"));
            _streamer = new(_streamSource, budgets);
            _streamPublishedRevision = -1; _streamDocumentRevision = -1; _nextNeighborhoodPlan = 0;
            MoveNeighborhoodCamera(_neighborhoodPosition);
            _browserMode = 2; _search = ""; ShowWorld();
            _status = "Neighborhood loading progressively. Navigate in orbit or free fly.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _error = ex.Message; }
        finally { EndOperation(); }
    }

    private void MoveNeighborhoodCamera(Vector3 position)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)) throw new ArgumentOutOfRangeException(nameof(position));
        _neighborhoodPosition = position;
        var center = Coordinates.GameToView(position);
        StopFlyLook();
        if (_worldCamera.IsFlying) _worldCamera.SetEye(center);
        else _worldCamera.Frame(new(center - new Vector3(15), center + new Vector3(15)));
        _nextNeighborhoodPlan = 0;
    }

    private void AdvanceNeighborhood()
    {
        if (_streamer is null || _busy || !_worldMode || _shutdown.IsCancellationRequested) return;
        if (_clock.Elapsed.TotalSeconds >= _nextNeighborhoodPlan || _document!.Revision != _streamDocumentRevision)
        {
            var position = _followNeighborhood ? Coordinates.ViewToGame(_worldCamera.StreamingCenter) : _neighborhoodPosition;
            var desired = NeighborhoodPolicy.Choose(_spatial!, position, _neighborhoodRadius, _streamer.Budgets.MaximumSectors);
            // Authored objects remain available even if their source sector was evicted.
            _streamer.SetDesired(desired, position, _document!.Objects().Where(p => p.Original is null).ToArray());
            _plannedNeighborhoodPosition = position;
            _nextNeighborhoodPlan = _clock.Elapsed.TotalSeconds + .5;
        }
        _streamer.Advance();
        if (_streamer.Revision != _streamPublishedRevision)
        {
            foreach (var hash in _streamedSectors.Where(h => !_streamer.Sectors.ContainsKey(h)).ToArray())
            { _document!.Evict(hash); _streamedSectors.Remove(hash); }
            foreach (var (hash, sector) in _streamer.Sectors)
            {
                if (_streamedSectors.Add(hash)) _document!.Attach(sector, remember: false);
            }
            _worldAssets.Clear(); _worldAssets[0] = SyntheticMesh.Cube();
            foreach (var (hash, mesh) in _streamer.DrawableMeshes) _worldAssets.Add(hash, mesh);
            _renderer!.SetWorld(_worldAssets); _sceneDirty = true;
            _streamPublishedRevision = _streamer.Revision;
        }
        _assetErrors.Clear();
        foreach (var (key, message) in _streamer.Errors) if (key.Mesh) _assetErrors[key.Hash] = message;
        _streamDocumentRevision = _document!.Revision;
    }

    private Task StopNeighborhood()
    {
        if (_streamer is null) return _streamStopTask;
        var previous = _streamer; _streamer = null; _streamSource = null; _plannedNeighborhoodPosition = null;
        _streamStopTask = Task.WhenAll(_streamStopTask, previous.StopAsync());
        foreach (var hash in _streamedSectors) _document?.Evict(hash);
        _streamedSectors.Clear(); _worldAssets.Clear(); _assetErrors.Clear();
        _worldAssets[0] = SyntheticMesh.Cube();
        if (_worldMode) { _renderer!.SetWorld(_worldAssets); _sceneDirty = true; }
        return _streamStopTask;
    }

    private async Task CloseAfterNeighborhood(Task stop)
    { await stop; if (!IsDisposed) BeginInvoke(Close); }

    private bool ExecuteNeighborhoodCommand(JsonElement command, AutomationReply result)
    {
        switch (command.GetProperty("op").GetString())
        {
            case "streaming-start":
                if (_busy || _index is null) throw new InvalidOperationException("Open an idle installation first.");
                if (command.TryGetProperty("maximumSectors", out var maximum))
                {
                    int value = maximum.GetInt32();
                    if (value is < 2 or > 64) throw new ArgumentOutOfRangeException("maximumSectors");
                    _neighborhoodSectorLimit = value;
                }
                _ = StartNeighborhood(); break;
            case "streaming-stop": _ = StopNeighborhood(); break;
            case "camera-position":
                var p = command.GetProperty("position"); MoveNeighborhoodCamera(new(p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle())); break;
            case "streaming-status":
                result.SetResult(new { ok = true, active = _streamer is not null, paused = !_worldMode, metrics = _streamer?.Metrics,
                    budgets = _streamer?.Budgets, cacheHits = _streamSource?.CacheHits, cacheMisses = _streamSource?.CacheMisses,
                    corruptEntries = _streamSource?.CorruptEntries, uploads = _renderer!.PendingWorldUploads,
                    uploadBytes = _renderer.WorldUploadBytesLastFrame, uploadMilliseconds = _renderer.WorldUploadMillisecondsLastFrame,
                    uploadParts = _renderer.WorldUploadParts, diagnostics = _streamer?.Errors.Select(e => new { e.Key.Mesh, e.Key.Hash, e.Value }),
                    sectors = _streamer?.Sectors.Keys, descriptors = _catalog?.Descriptors.Count(), stopping = !_streamStopTask.IsCompleted,
                    cameraCenter = Coordinates.ViewToGame(_worldCamera.StreamingCenter), plannedCenter = _plannedNeighborhoodPosition }); return true;
            default: return false;
        }
        result.SetResult(new { ok = true, frame = _frame }); return true;
    }
}
