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
    private EditorDocument? _document;
    private readonly Dictionary<ulong, MeshAsset> _worldAssets = [];
    private readonly Dictionary<ulong, string> _assetErrors = [];
    private ArchiveResource[] _filteredSectors = [];
    private bool _worldMode, _showPlaceholders, _sceneDirty, _gizmoDragging;
    private int _browserMode;
    private string? _projectPath, _selectedObject;
    private PlacementTransform? _previewTransform;
    private long _publishedRevision = -1;
    private double _lastAutosave;
    private ScenePlacement? Selected => _selectedObject is null ? null : _document?.Objects(true).FirstOrDefault(p => p.Id == _selectedObject);
    private string RecoveryPath => Path.Combine(Program.StateDirectory, "Recovery", $"{_document!.Project.Id:N}.ncwe.json");

    private void ApplySectorSearch() => _filteredSectors = _index?.Sectors.Where(s => s.Path.Contains(_search, StringComparison.OrdinalIgnoreCase)).Take(1500).ToArray() ?? [];
    private void BuildBrowserMode()
    {
        ImGui.RadioButton("Meshes", ref _browserMode, 0); Remember("browser-meshes"); ImGui.SameLine();
        ImGui.RadioButton("Sectors", ref _browserMode, 1); Remember("browser-sectors"); ImGui.SameLine();
        ImGui.RadioButton("Scene", ref _browserMode, 2); Remember("browser-scene");
    }
    private string BrowserCount() => _browserMode switch {
        1 => $"{_filteredSectors.Length:N0} shown / {_index?.Sectors.Length ?? 0:N0} sectors",
        2 => $"{_document?.Objects().Count() ?? 0:N0} resident placements",
        _ => $"{_filtered.Length:N0} shown / {_index?.Meshes.Length ?? 0:N0} meshes"
    };

    private void BuildWorldBrowser()
    {
        if (_browserMode == 1)
        {
            foreach (var resource in _filteredSectors)
            {
                if (ImGui.Selectable(Path.GetFileName(resource.Path) + "##sector" + resource.Hash) && !_busy) _ = OpenSector(resource.Hash);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(resource.Path);
            }
        }
        else if (_document is not null)
        {
            var matches = _document.Objects().Where(o => o.Name.Contains(_search, StringComparison.OrdinalIgnoreCase)
                || o.Id.Contains(_search, StringComparison.OrdinalIgnoreCase) || (o.Mesh?.Path.Contains(_search, StringComparison.OrdinalIgnoreCase) ?? false)
                || (o.Original?.Source.NodeType.Contains(_search, StringComparison.OrdinalIgnoreCase) ?? false)
                || (o.Original?.Source.Sector.Path.Contains(_search, StringComparison.OrdinalIgnoreCase) ?? false));
            foreach (var sector in matches.GroupBy(o => o.Original?.Source.Sector))
            {
                string label = sector.Key is null ? "Authored objects" : Path.GetFileName(sector.Key.Path);
                if (_search.Length > 0) ImGui.SetNextItemOpen(true);
                bool open = ImGui.TreeNodeEx($"{label} ({sector.Count()})##sector-tree-{sector.Key?.Hash ?? 0}", ImGuiTreeNodeFlags.DefaultOpen);
                if (sector.Key is not null && ImGui.IsItemHovered()) ImGui.SetTooltip(sector.Key.Path);
                if (!open) continue;
                foreach (var node in sector.GroupBy(o => o.Original?.Source.NodeIndex))
                {
                    var items = node.ToArray();
                    if (node.Key is null || items.Length == 1) { foreach (var item in items) DrawPlacement(item); continue; }
                    if (_search.Length > 0) ImGui.SetNextItemOpen(true);
                    if (!ImGui.TreeNodeEx($"Node {node.Key}: {items[0].Original!.Source.NodeType} ({items.Length})##node-{node.Key}")) continue;
                    foreach (var item in items) DrawPlacement(item);
                    ImGui.TreePop();
                }
                ImGui.TreePop();
            }
        }
    }

    private void DrawPlacement(ScenePlacement item)
    {
        string marker = item.SourceMismatch ? "! " : !item.CanEdit ? "[read-only] " : item.Original is null ? "+ " : "";
        if (ImGui.Selectable(marker + item.Name + "##" + item.Id, item.Id == _selectedObject)) SelectObject(item.Id);
        if (ImGui.IsItemVisible()) Remember("object-" + item.Id);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{item.Id}\n{item.Original?.Source.NodeType ?? "Authored mesh"}\n{item.Mesh?.Path}\n{item.Diagnostic}");
    }

    private void BuildProjectMenu()
    {
        if (ImGui.BeginMenu("Project"))
        {
            if (ImGui.MenuItem("New world project", null, false, !_busy)) NewProject();
            if (ImGui.MenuItem("Open project...", null, false, !_busy))
            {
                using var picker = new OpenFileDialog { Filter = "NCWE projects|*.ncwe.json|JSON files|*.json" };
                if (picker.ShowDialog(this) == DialogResult.OK) _ = OpenProject(picker.FileName);
            }
            if (ImGui.MenuItem("Save project...", "Ctrl+S", false, _document is not null && !_busy)) SaveProjectDialog();
            if (ImGui.MenuItem("Export ArchiveXL mod...", null, false, _document is not null && _index is not null && !_busy)) ExportModDialog();
            if (ImGui.MenuItem("Open last export folder", null, false, _lastExportDirectory is not null))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_lastExportDirectory!) { UseShellExecute = true });
            if (ImGui.MenuItem("Install last export (game closed)", null, false, _lastExportDirectory is not null && !_busy)) _ = InstallExport(_lastExportDirectory!);
            if (ImGui.MenuItem("Remove an installed NCWE mod...", null, false, _index is not null && !_busy)) RemoveModDialog();
            if (ImGui.MenuItem("Recover autosave...", null, false, !_busy))
            {
                var folder = Path.Combine(Program.StateDirectory, "Recovery"); Directory.CreateDirectory(folder);
                using var picker = new OpenFileDialog { InitialDirectory = folder, Filter = "NCWE recovery|*.ncwe.json" };
                if (picker.ShowDialog(this) == DialogResult.OK) _ = OpenProject(picker.FileName);
            }
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Edit"))
        {
            if (ImGui.MenuItem("Undo " + _document?.UndoName, "Ctrl+Z", false, _document?.CanUndo == true)) _document!.Undo();
            if (ImGui.MenuItem("Redo " + _document?.RedoName, "Ctrl+Y", false, _document?.CanRedo == true)) _document!.Redo();
            ImGui.EndMenu();
        }
        ImGui.SameLine(); ImGui.TextDisabled(_document is null ? "MESH VIEWER" : (_document.IsDirty ? "* " : "") + _document.Project.Name);
        bool navigating = _flyLookActive || (_worldMode && _worldCamera.IsFlying && ImGui.IsMouseDown(ImGuiMouseButton.Right));
        if (_document is not null && _inputFocused && !navigating && !ImGui.GetIO().WantTextInput && !_busy)
        {
            var io = ImGui.GetIO();
            if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.Z)) _document.Undo();
            if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.Y)) _document.Redo();
            if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.S)) SaveProjectDialog();
            if (_worldMode && Selected is { CanEdit: true } selected)
            {
                if (ImGui.IsKeyPressed(ImGuiKey.Delete)) _document.Delete(selected.Id);
                if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.D)) SelectObject(_document.Duplicate(selected.Id));
            }
        }
    }

    private void BuildWorldActions()
    {
        ImGui.BeginDisabled(_busy);
        if (_document is not null)
        {
            if (Button("World view", "world-view")) ShowWorld();
            if (!_worldMode && _mesh is not null && _index is not null)
            {
                var asset = _index.Meshes.FirstOrDefault(m => m.Path == _mesh.ResourcePath);
                if (asset is not null) { ImGui.SameLine(); if (Button("Add mesh", "add-mesh")) _ = AddWorldMesh(asset); }
            }
        }
        ImGui.EndDisabled();
    }

    private void NewProject()
    {
        StopFlyLook();
        TryAutosave();
        _ = StopNeighborhood();
        _document = new(ProjectData.Create("Untitled world")); _projectPath = null; _selectedObject = null;
        _previewTransform = null; _worldAssets.Clear(); _assetErrors.Clear();
        _worldAssets[0] = SyntheticMesh.Cube(); _renderer!.SetWorld(_worldAssets);
        _worldMode = true; _sceneDirty = true; _browserMode = 1; _error = "";
        _status = "Choose a sector or add an existing mesh to begin.";
    }

    private async Task OpenSector(ulong hash)
    {
        if (_busy || _index is null) return;
        _busy = true; _error = ""; _status = "Reading sector placements...";
        try
        {
            await StopNeighborhood();
            var sector = await _index.LoadSectorAsync(hash, _shutdown.Token);
            _shutdown.Token.ThrowIfCancellationRequested();
            _document ??= new(ProjectData.Create("Untitled world"));
            _document.Attach(sector);
            await LoadWorldAssets();
            _browserMode = 2; _search = ""; ApplySearch();
            FrameWorld();
            _status = $"{sector.Placements.Length:N0} placements · {_assetErrors.Count} resource diagnostics";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _error = ex.Message; _status = "Sector could not be opened"; }
        finally { EndOperation(); }
    }

    private async Task LoadWorldAssets()
    {
        var references = _document!.Objects().Where(p => p.Mesh is not null).Select(p => p.Mesh!).DistinctBy(m => m.Hash).ToArray();
        var wanted = references.Select(m => m.Hash).ToHashSet(); wanted.Add(0);
        foreach (var stale in _worldAssets.Keys.Where(h => !wanted.Contains(h)).ToArray()) _worldAssets.Remove(stale);
        foreach (var stale in _assetErrors.Keys.Where(h => !wanted.Contains(h)).ToArray()) _assetErrors.Remove(stale);
        _worldAssets[0] = SyntheticMesh.Cube();
        for (int i = 0; i < references.Length; i++)
        {
            _shutdown.Token.ThrowIfCancellationRequested();
            var reference = references[i];
            if (_worldAssets.ContainsKey(reference.Hash) || _assetErrors.ContainsKey(reference.Hash)) continue;
            _status = $"Loading sector meshes {i + 1}/{references.Length}...";
            try { _worldAssets[reference.Hash] = await _index!.LoadMeshAsync(reference.Hash, _shutdown.Token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _assetErrors[reference.Hash] = ex.Message; }
        }
        _shutdown.Token.ThrowIfCancellationRequested();
        ShowWorld();
    }

    private void ShowWorld()
    {
        if (_document is null) return;
        if (_mesh is not null) _previousMesh = new(_mesh);
        _mesh = null; _visible = [];
        _renderer!.SetWorld(_worldAssets); _worldMode = true; _sceneDirty = true;
    }

    private async Task AddWorldMesh(ArchiveResource resource)
    {
        if (_busy || _index is null) return;
        _busy = true; _error = "";
        try
        {
            _document ??= new(ProjectData.Create("Untitled world"));
            if (_streamer is not null)
            {
                var authoredId = _document.Add(new(resource.Hash, resource.Path), PlacementTransform.Identity with { Position = Coordinates.ViewToGame(_worldCamera.Target) });
                ShowWorld(); SelectObject(authoredId); _browserMode = 2; _search = ""; _nextNeighborhoodPlan = 0;
                _status = "Added mesh reference; geometry will load within the neighborhood budget.";
                return;
            }
            var mesh = _worldAssets.TryGetValue(resource.Hash, out var cached) ? cached : await _index.LoadMeshAsync(resource.Hash, _shutdown.Token);
            _shutdown.Token.ThrowIfCancellationRequested(); _worldAssets[resource.Hash] = mesh; _worldAssets[0] = SyntheticMesh.Cube();
            var transform = PlacementTransform.Identity with { Position = Coordinates.ViewToGame(_worldCamera.Target) };
            var id = _document.Add(new(resource.Hash, resource.Path), transform);
            ShowWorld(); SelectObject(id); _browserMode = 2; _search = "";
            _status = "Added a mesh reference to the project.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _error = ex.Message; }
        finally { EndOperation(); }
    }

    private void SelectObject(string? id)
    {
        _selectedObject = id; _previewTransform = null; _gizmoDragging = false; _sceneDirty = true;
    }
    private void PublishWorldInstances()
    {
        if (_document is null || (!_sceneDirty && _publishedRevision == _document.Revision && _previewTransform is null)) return;
        var instances = new List<RenderPlacement>();
        foreach (var item in _document.Objects())
        {
            var transform = item.Id == _selectedObject ? _previewTransform ?? item.Transform : item.Transform;
            if (item.Mesh is not null && _worldAssets.ContainsKey(item.Mesh.Hash))
                instances.Add(new(item.Id, item.Mesh.Hash, transform.ViewMatrix, item.Id == _selectedObject));
            else if (_showPlaceholders)
                instances.Add(new(item.Id, 0, Matrix4x4.CreateScale(.25f) * Matrix4x4.CreateTranslation(Coordinates.GameToView(transform.Position)), item.Id == _selectedObject, true));
        }
        _renderer!.SetWorldInstances(instances); _sceneDirty = false; _publishedRevision = _document.Revision;
    }

    private void BuildWorldInspector()
    {
        if (_document is null) return;
        if (ImGui.Checkbox("Show unsupported placeholders", ref _showPlaceholders)) _sceneDirty = true;
        Remember("placeholders");
        var selected = Selected;
        if (selected is null) { ImGui.TextWrapped("Select a placement in the scene tree or viewport."); return; }
        ImGui.TextWrapped(selected.Name); ImGui.TextDisabled(selected.Original?.Source.NodeType ?? "Authored static mesh");
        ImGui.TextWrapped(selected.Mesh?.Path ?? "No mesh resource");
        if (selected.Original is not null)
        {
            var anchor = selected.Original.Source;
            ImGui.Text($"Placement {anchor.PlacementIndex} · definition {anchor.NodeIndex}");
            if (anchor.InstanceIndex >= 0) ImGui.Text($"Instance {anchor.InstanceIndex}/{anchor.ExpectedInstances}");
        }
        if (selected.Diagnostic is not null) ImGui.TextWrapped(selected.Diagnostic);
        if (selected.Mesh is not null && _assetErrors.TryGetValue(selected.Mesh.Hash, out var message)) ImGui.TextWrapped(message);
        ImGui.Separator(); ImGui.TextDisabled("WORLD TRANSFORM · METERS · Z UP");
        ImGui.BeginDisabled(!selected.CanEdit || _busy || selected.Deleted);
        var t = _previewTransform ?? selected.Transform;
        var position = t.Position; var scale = t.Scale;
        var rotation = new Vector4(t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.DragFloat3("##position", ref position, .05f)) PreviewTransform(t with { Position = position });
        Remember("position"); if (ImGui.IsItemDeactivatedAfterEdit()) CommitPreview();
        ImGui.TextDisabled("Position X / Y / Z");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.DragFloat4("##rotation", ref rotation, .005f)) PreviewTransform(t with { Rotation = new(rotation.X, rotation.Y, rotation.Z, rotation.W) });
        Remember("rotation"); if (ImGui.IsItemDeactivatedAfterEdit()) CommitPreview();
        ImGui.TextDisabled("Quaternion X / Y / Z / W");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.DragFloat3("##scale", ref scale, .01f)) PreviewTransform(t with { Scale = scale });
        Remember("scale"); if (ImGui.IsItemDeactivatedAfterEdit()) CommitPreview();
        ImGui.TextDisabled("Scale X / Y / Z");
        if (Button("Duplicate", "duplicate-object")) SelectObject(_document.Duplicate(selected.Id));
        ImGui.SameLine(); if (Button("Delete", "delete-object")) { _document.Delete(selected.Id); SelectObject(null); }
        ImGui.EndDisabled();
        ImGui.BeginDisabled(!_document.CanUndo); if (Button("Undo", "undo")) _document.Undo(); ImGui.EndDisabled();
        ImGui.SameLine(); ImGui.BeginDisabled(!_document.CanRedo); if (Button("Redo", "redo")) _document.Redo(); ImGui.EndDisabled();
        if (Button("Save project", "save-project")) SaveProjectDialog();
        ImGui.Separator();
    }

    private void PreviewTransform(PlacementTransform transform)
    {
        try { _previewTransform = transform.Validated(); _sceneDirty = true; }
        catch (InvalidDataException ex) { _error = ex.Message; }
    }
    private void CommitPreview()
    {
        if (_previewTransform is { } transform && _selectedObject is not null)
        {
            try { _document!.Transform(_selectedObject, transform); }
            catch (InvalidOperationException ex) { _error = ex.Message; }
        }
        _previewTransform = null; _sceneDirty = true;
    }
    private void WorldViewport(bool hovered)
    {
        var selected = Selected;
        if (_flyLookActive) return;
        if (_gizmo && selected is { CanEdit: true, Deleted: false } && !_busy)
        {
            var frame = _worldCamera.GetFrame(_viewportSize.X / _viewportSize.Y, _renderer!.CameraRelative);
            var matrix = frame.Relative((_previewTransform ?? selected.Transform).ViewMatrix);
            Gizmo.Manipulate(frame.View, frame.Projection, ref matrix,
                _viewportPosition.X, _viewportPosition.Y, _viewportSize.X, _viewportSize.Y, _operation);
            bool usingGizmo = Gizmo.IsUsing() != 0;
            if (usingGizmo)
            {
                try { PreviewTransform(PlacementTransform.FromViewMatrix(frame.Absolute(matrix))); }
                catch (InvalidDataException ex) { _error = ex.Message; }
            }
            if (_gizmoDragging && !usingGizmo) CommitPreview();
            _gizmoDragging = usingGizmo;
        }
        bool blocked = _gizmo && selected is { CanEdit: true } && (Gizmo.IsOver() != 0 || Gizmo.IsUsing() != 0);
        if (hovered && !blocked && ImGui.IsMouseReleased(ImGuiMouseButton.Left) && ImGui.GetMouseDragDelta(ImGuiMouseButton.Left).LengthSquared() < 16)
            PickWorld(ImGui.GetMousePos() - _viewportPosition);
    }

    private string? PickWorld(Vector2 pixel)
    {
        if (_renderer!.RenderedWorldCamera is not { } frame) return null;
        var ray = ScenePicking.ViewRay(pixel, _viewportSize, frame.View, frame.Projection);
        float distance = float.PositiveInfinity; string? nearest = null;
        var currentIds = _document!.Objects().Select(p => p.Id).ToHashSet();
        foreach (var item in _renderer.DrawnPlacements)
        {
            if (!currentIds.Contains(item.Id) || !_worldAssets.TryGetValue(item.MeshHash, out var mesh) || mesh.SourceHash != item.MeshFingerprint) continue;
            // Pick the geometry and LOD actually drawn in the last displayed frame.
            var hit = ScenePicking.Intersect(ray, mesh, item.RelativeModel, item.LodMask);
            if (hit.HasValue && hit.Value < distance) { distance = hit.Value; nearest = item.Id; }
        }
        SelectObject(nearest); return nearest;
    }
    private void FrameSelection()
    {
        var selected = Selected;
        if (selected?.Mesh is not null && _worldAssets.TryGetValue(selected.Mesh.Hash, out var mesh))
            _worldCamera.Frame(selected.Transform.TransformBounds(mesh.Bounds));
        else if (selected is not null) { var center = Coordinates.GameToView(selected.Transform.Position); _worldCamera.Frame(new(center - Vector3.One, center + Vector3.One)); }
        else FrameWorld();
    }
    private void FrameWorld()
    {
        var bounds = _document?.Objects().Where(p => p.Mesh is not null && _worldAssets.ContainsKey(p.Mesh.Hash))
            .Select(p => p.Transform.TransformBounds(_worldAssets[p.Mesh!.Hash].Bounds)).ToArray() ?? [];
        if (bounds.Length != 0) _worldCamera.Frame(Bounds3.From(bounds.SelectMany(b => new[] { b.Min, b.Max })));
    }
    private void ResetWorldTransform()
    {
        if (Selected is { CanEdit: true } item) _document!.Transform(item.Id, item.Original?.Transform
            ?? PlacementTransform.Identity with { Position = item.Transform.Position });
    }

    private void SaveProjectDialog()
    {
        if (_document is null) return;
        if (_projectPath is null)
        {
            using var picker = new SaveFileDialog { Filter = "NCWE project|*.ncwe.json", DefaultExt = "ncwe.json", FileName = _document.Project.Name + ".ncwe.json" };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            _projectPath = picker.FileName;
        }
        try { SaveProject(_projectPath); } catch (Exception ex) { _error = ex.Message; }
    }
    private void SaveProject(string path)
    {
        if (_document is null) throw new InvalidOperationException("No world project.");
        ProjectStorage.WriteAtomic(path, _document.Project); _document.MarkSaved(); _projectPath = Path.GetFullPath(path);
        _status = "Project saved: " + Path.GetFileName(path);
    }
    private async Task OpenProject(string path)
    {
        if (_busy) return;
        _busy = true; _error = "";
        try
        {
            await StopNeighborhood();
            var project = await Task.Run(() => ProjectStorage.Read(path));
            if (_index is null && (project.Sources.Count != 0 || project.Changes.Added.Count != 0)) throw new InvalidOperationException("Open the game's archives before reopening this world project.");
            TryAutosave(); _document = new(project); _projectPath = Path.GetFullPath(path); SelectObject(null);
            foreach (var source in project.Sources.Values)
            {
                _status = "Restoring sector " + source.Path;
                _document.Attach(await _index!.LoadSectorAsync(source.Hash, _shutdown.Token));
            }
            await LoadWorldAssets(); _document.MarkSaved(); _browserMode = 2; FrameWorld();
            _status = "Project reopened";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _error = ex.Message; }
        finally { EndOperation(); }
    }
    private void AutosaveIfNeeded()
    {
        if (_clock.Elapsed.TotalSeconds - _lastAutosave < 30 || _busy) return;
        _lastAutosave = _clock.Elapsed.TotalSeconds; TryAutosave();
    }
    private void TryAutosave()
    {
        if (_document?.IsDirty != true) return;
        try { ProjectStorage.WriteAtomic(RecoveryPath, _document.Project); }
        catch (Exception ex) { _error = "Autosave failed: " + ex.Message; }
    }

    private bool ExecuteWorldCommand(JsonElement command, AutomationReply result)
    {
        string Str(string key) => command.GetProperty(key).GetString()!;
        string Id() => command.TryGetProperty("id", out var id) ? id.GetString()! : _selectedObject ?? throw new InvalidOperationException("Nothing selected.");
        switch (Str("op"))
        {
            case "world-status":
                result.SetResult(new { ok = true, world = _worldMode, dirty = _document?.IsDirty, project = _document?.Project,
                    selected = _selectedObject, objects = _document?.Objects(true), assetErrors = _assetErrors,
                    assets = _worldAssets.Count, cpuBytes = _worldAssets.Values.Sum(m => m.CpuBytes), selectionScreen = SelectionScreen(),
                    canUndo = _document?.CanUndo, canRedo = _document?.CanRedo }); return true;
            case "validate-project": _ = ValidateProjectSources(result); return true;
            case "export-project": _ = ExportMod(Str("path"), result); return true;
            case "install-package": _ = InstallExport(Str("path"), result); return true;
            case "remove-mod": RemoveMod(Str("path")); break;
            case "search-sectors":
                result.SetResult(new { ok = true, sectors = _index?.Sectors.Where(s => s.Path.Contains(Str("text"), StringComparison.OrdinalIgnoreCase)).Take(100) }); return true;
            case "open-sector":
                var sector = _index?.Sectors.FirstOrDefault(s => s.Path.Equals(Str("path"), StringComparison.OrdinalIgnoreCase)) ?? throw new FileNotFoundException("Sector was not indexed.");
                _ = OpenSector(sector.Hash); break;
            case "new-project": NewProject(); break;
            case "open-project": _ = OpenProject(Str("path")); break;
            case "save-project": SaveProject(Str("path")); break;
            case "select-object": SelectObject(Id()); break;
            case "frame-selection": FrameSelection(); break;
            case "frame-world": FrameWorld(); break;
            case "show-world": ShowWorld(); break;
            case "transform-object": _document!.Transform(Id(), command.GetProperty("transform").Deserialize<PlacementTransform>(ProjectStorage.JsonOptions)); break;
            case "duplicate-object": SelectObject(_document!.Duplicate(Id())); break;
            case "delete-object": _document!.Delete(Id()); SelectObject(null); break;
            case "undo": _document!.Undo(); break;
            case "redo": _document!.Redo(); break;
            case "add-object":
                var resource = _index?.Meshes.FirstOrDefault(m => m.Path.Equals(Str("path"), StringComparison.OrdinalIgnoreCase)) ?? throw new FileNotFoundException("Mesh was not indexed.");
                _ = AddWorldMesh(resource); break;
            case "evict-sector":
                _document!.Evict(command.GetProperty("hash").GetUInt64());
                var needed = _document.Objects().Where(p => p.Mesh is not null).Select(p => p.Mesh!.Hash).ToHashSet(); needed.Add(0);
                foreach (var stale in _worldAssets.Keys.Where(h => !needed.Contains(h)).ToArray()) _worldAssets.Remove(stale);
                ShowWorld(); break;
            default: return false;
        }
        result.SetResult(new { ok = true, frame = _frame }); return true;
    }

    private float[]? SelectionScreen()
    {
        if (Selected is not { } selected) return null;
        var origin = Coordinates.GameToView((_previewTransform ?? selected.Transform).Position);
        var frame = _worldCamera.GetFrame(_viewportSize.X / _viewportSize.Y, _renderer!.CameraRelative);
        var clip = Vector4.Transform(new Vector4(origin - frame.Origin, 1), frame.ViewProjection);
        if (clip.W <= 0) return null;
        return [_viewportPosition.X + (clip.X / clip.W + 1) * .5f * _viewportSize.X,
            _viewportPosition.Y + (1 - clip.Y / clip.W) * .5f * _viewportSize.Y];
    }

    private async Task ValidateProjectSources(AutomationReply result)
    {
        if (_busy || _document is null || _index is null) { result.SetResult(new { ok = false, error = "Open an idle world project and installation first." }); return; }
        _busy = true;
        try
        {
            var fresh = new Dictionary<ulong, SectorSnapshot>();
            foreach (var source in _document.Project.Sources.Values) fresh[source.Hash] = await _index.LoadSectorAsync(source.Hash, _shutdown.Token);
            var errors = ExportPreparation.Validate(_document.Project, fresh);
            result.SetResult(new { ok = true, valid = errors.IsEmpty, errors });
        }
        catch (Exception ex) { result.SetResult(new { ok = false, error = ex.Message }); }
        finally { EndOperation(); }
    }
}
