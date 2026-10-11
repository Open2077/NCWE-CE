// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Pipes;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using NCWE.Core;
using NCWE.Render;
using NCWE.Studio.Session;
using NCWE.Studio.Viewport;

namespace NCWE.Studio;

// Automation pipe (default "ncwe-studio", --pipe <name>, --no-pipe), restricted to the current
// user: the NCWE MCP server and test scripts drive the editor through it. One JSON command per
// connection; replies are produced on the UI thread.
internal sealed partial class Automation : IDisposable
{
    public const string DefaultPipe = "ncwe-studio";
    private static readonly JsonSerializerOptions Options = new() { IncludeFields = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    private readonly CancellationTokenSource _stop = new();
    private readonly MainWindow _window;
    private readonly BridgeActivity _activity;
    private readonly string _name;
    private SplashWindow? _splash;

    public Automation(string name, MainWindow window, BridgeActivity activity)
    {
        _window = window; _activity = activity; _name = name;
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                NamedPipeServerStream server;
                // Another Studio may own the name: wait for it to close instead of spinning.
                try { server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _activity.SetState(BridgeActivity.BridgeState.Waiting, name);
                    try { await Task.Delay(3000, _stop.Token); } catch (OperationCanceledException) { break; }
                    continue;
                }
                _activity.SetState(BridgeActivity.BridgeState.Listening, name);
                try
                {
                    using var pipe = server;
                    await pipe.WaitForConnectionAsync(_stop.Token);
                    using var reader = new StreamReader(pipe, leaveOpen: true); using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                    var line = await reader.ReadLineAsync(_stop.Token);
                    if (line is null) continue;
                    var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _window.DispatcherQueue.TryEnqueue(async () =>
                    {
                        var watch = System.Diagnostics.Stopwatch.StartNew();
                        string op = "?", client = "script";
                        try
                        {
                            var command = JsonDocument.Parse(line).RootElement;
                            if (command.TryGetProperty("op", out var o) && o.ValueKind == JsonValueKind.String) op = o.GetString()!;
                            if (command.TryGetProperty("client", out var who) && who.ValueKind == JsonValueKind.String) client = who.GetString()!;
                            _client = client;
                            _window.Controller?.Ai.Begin(client, op);
                            object result;
                            try { result = await Execute(command); }
                            finally { _window.Controller?.Ai.End(op); }
                            string json = JsonSerializer.Serialize(result, Options);
                            // Replies that report a failure ({"ok": false, "error": ...}) count as errors.
                            string? failure = null;
                            if (json.StartsWith("{\"ok\":false", StringComparison.Ordinal))
                                try { failure = JsonDocument.Parse(json).RootElement.TryGetProperty("error", out var e) ? e.ToString() : "failed"; } catch (JsonException) { failure = "failed"; }
                            _activity.Record(client, op, watch.Elapsed.TotalMilliseconds, failure is null, failure);
                            // What the edit touched flashes in the view (never fails the command).
                            if (failure is null) try { ShowEdit(op, command, json); } catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { }
                            reply.SetResult(json);
                        }
                        catch (Exception ex)
                        {
                            _activity.Record(client, op, watch.Elapsed.TotalMilliseconds, false, ex.Message);
                            reply.SetResult(JsonSerializer.Serialize(new { ok = false, error = ex.Message }));
                        }
                    });
                    await writer.WriteLineAsync(await reply.Task);
                }
                catch (OperationCanceledException) { break; }
                catch (IOException) { }
            }
            _activity.SetState(BridgeActivity.BridgeState.Stopped, name);
        });
    }

    private async Task<object> Execute(JsonElement c)
    {
        var v = _window.Controller ?? throw new InvalidOperationException("Viewport not ready.");
        var s = v.Session;
        string op = c.GetProperty("op").GetString()!;
        if (op.StartsWith("api.", StringComparison.Ordinal)) return await ExecuteApi(op, c, v);
        float F(string key) => c.GetProperty(key).GetSingle();
        Vector3[] Points(string key) => c.GetProperty(key).EnumerateArray().Select(p => new Vector3(p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle())).ToArray();
        switch (op)
        {
            case "status":
                var eye = Coordinates.ViewToGame(s.Camera.Eye);
                return new { ok = true, status = s.Status, error = s.Error, busy = s.Busy, indexed = s.Index is not null, fps = v.FramesPerSecond, frameMs = v.FrameMilliseconds,
                    eye = new[] { eye.X, eye.Y, eye.Z }, yaw = s.Camera.Yaw, pitch = s.Camera.Pitch, fov = s.Camera.FieldOfView, splash = App.SplashState, splashLogos = App.SplashLogos, selected = s.SelectedId, tool = v.Tool.ToString(), navigation = v.Navigation.ToString(),
                    visible = v.Renderer.VisiblePlacements, draws = v.Renderer.DrawCalls, lightGradients = v.Renderer.LightGradientDraws, uploads = v.Renderer.PendingWorldUploads, metrics = s.StreamMetrics,
                    objects = s.Document.Objects().Count(), gizmo = new { v.GizmoActive, v.OverlayVertices, v.GizmoHot }, gc = new { pauseMs = GC.GetTotalPauseDuration().TotalMilliseconds, gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2), heapMb = GC.GetTotalMemory(false) >> 20, maxFrameMs = v.MaxFrameMilliseconds }, dirty = s.Document.IsDirty, cacheHits = s.CacheHits,
                    timings = new { plan = s.PlanMilliseconds, advance = s.AdvanceMilliseconds, attach = s.AttachMilliseconds, publish = s.PublishMilliseconds, v.NavigateMilliseconds, v.TickMilliseconds, v.UploadMilliseconds, v.SceneMilliseconds, v.OverlayMilliseconds, v.PresentMilliseconds } };
            case "open-game": await _window.OpenGame(c.GetProperty("path").GetString()!); break;
            case "goto":
                if (c.TryGetProperty("fov", out var fov)) s.Camera.FieldOfView = fov.GetSingle();
                s.GoTo(new("Automation", F("x"), F("y"), F("z"), c.TryGetProperty("yaw", out var yaw) ? yaw.GetSingle() : s.Camera.Yaw,
                    c.TryGetProperty("pitch", out var pitch) ? pitch.GetSingle() : s.Camera.Pitch)); break;
            case "wheel": v.Wheel(c.GetProperty("delta").GetInt32(), c.TryGetProperty("ctrl", out var wheelCtrl) && wheelCtrl.GetBoolean()); return new { ok = true, fov = s.Camera.FieldOfView, speed = v.FlySpeed };
            case "tab": _window.ShowTab(c.GetProperty("name").GetString()!); break;
            // The menu bar as it stands; "open": a menu's title (with "sub": a sub-menu's text) opens it, "close": true closes it.
            case "menus":
            {
                bool opened = true;
                if (c.TryGetProperty("close", out var shut) && shut.GetBoolean()) await _window.OpenMenuAsync(null, null);
                else if (Str(c, "open") is { } title) opened = await _window.OpenMenuAsync(title, Str(c, "sub"));
                return new { ok = opened, menus = _window.MenuTree(), windows = c.TryGetProperty("windows", out var list) && list.GetBoolean() ? ProcessWindows() : null };
            }
            case "add-category": _window.ShowAddCategory(c.GetProperty("name").GetString()!); break;
            case "shortcuts": _ = _window.ShowShortcutsForAutomation(Str(c, "section"), Str(c, "search")); break;
            case "radial":
                return _window.RadialAutomation(Str(c, "action") ?? "state", c.TryGetProperty("at", out var radialAt) ? [radialAt[0].GetDouble(), radialAt[1].GetDouble()] : null,
                    Str(c, "category"), Str(c, "item"));
            case "material-picker":
            {
                int spheres = _window.OpenMaterialPicker(c.GetProperty("index").GetInt32(), Str(c, "query"), Str(c, "filter"));
                // choose: the n-th sphere shown, clicked.
                string? chosen = spheres >= 0 && c.TryGetProperty("choose", out var choose) ? _window.ChooseInMaterialPicker(choose.GetInt32()) : null;
                return new { ok = spheres >= 0, spheres, chosen };
            }
            case "inspector-scroll": _window.ScrollInspector(c.TryGetProperty("offset", out var offset) ? offset.GetDouble() : 0); break;
            case "account": return await _window.AccountUi(Str(c, "action") ?? "state");
            case "collab": return await _window.CollabUi(c);
            case "cloud":
                return await _window.CloudUi(Str(c, "action") ?? "state", Str(c, "note"), Str(c, "project") is { } cloudProject ? Guid.Parse(cloudProject) : null,
                    c.TryGetProperty("version", out var cloudVersion) ? cloudVersion.GetInt32() : null);
            case "toolbar": _window.SetToolbarVertical(c.GetProperty("vertical").GetBoolean()); break;
            case "tool": _window.SetTool(Enum.Parse<EditorTool>(c.GetProperty("name").GetString()!, true)); break;
            case "navigation": _window.SetNavigation(Enum.Parse<NavigationMode>(c.GetProperty("name").GetString()!, true)); break;
            case "preflight":
                if (c.TryGetProperty("city", out _)) s.PrepareWholeCity();
                if (c.TryGetProperty("enabled", out var enabled)) { s.Settings.Preflight = enabled.GetBoolean(); if (!s.Settings.Preflight) s.Preflight.Stop(); }
                return new { ok = true, running = s.Preflight.Running, completed = s.Preflight.Completed, phase = s.Preflight.Phase, sectorsDone = s.Preflight.SectorsDone,
                    sectorsTotal = s.Preflight.SectorsTotal, meshesSeen = s.Preflight.MeshesSeen, meshesConverted = s.Preflight.MeshesConverted, error = s.Preflight.Error,
                    center = new[] { s.Preflight.Center.X, s.Preflight.Center.Y, s.Preflight.Center.Z }, radius = s.Preflight.Radius };
            case "profile": return new { ok = true, fps = v.FramesPerSecond, visible = v.Renderer.VisiblePlacements, draws = v.Renderer.DrawCalls, uploads = v.Renderer.PendingWorldUploads, metrics = s.StreamMetrics,
                allocations = AllocationTrace.Snapshot(!c.TryGetProperty("keep", out _)), stages = FrameProfile.Snapshot(!c.TryGetProperty("keep", out _)), source = s.SourceTimings, extract = s.Index is { } ix ? new { totalMs = ix.ExtractTime.TotalMilliseconds, waitMs = ix.ExtractWaitTime.TotalMilliseconds } : null, gc = new { gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2),
                    last = GcInfo(GCKind.Any), fullBlocking = GcInfo(GCKind.FullBlocking), background = GcInfo(GCKind.Background) } };
            case "hits": return new { ok = true, hits = s.HitAll(new(F("x"), F("y")), v.ViewportPixels).Take(30).Select(h => new { h.Id, h.Mesh, h.Distance }).ToArray() };
            case "pick": return new { ok = true, id = s.Pick(new(F("x"), F("y")), v.ViewportPixels, c.TryGetProperty("additive", out var add) && add.GetBoolean(), c.TryGetProperty("decals", out var pickDecals) && pickDecals.GetBoolean()), selection = s.SelectedIds };
            // Traffic tool clicks at viewport pixels (select, Ctrl insert, draw, link), Enter, and the lane under a pixel.
            case "traffic-click":
                s.TrafficClick(new(F("x"), F("y")), v.ViewportPixels, c.TryGetProperty("control", out var ctl) && ctl.GetBoolean(), c.TryGetProperty("alt", out var altKey) && altKey.GetBoolean());
                return new { ok = true, lane = s.SelectedLane, point = s.SelectedLanePoint, drawing = s.TrafficDrawing, points = s.TrafficDrawPoints.Count, status = s.Status, error = s.Error };
            case "traffic-draw": s.StartTrafficDrawing(); return new { ok = true };
            // The gizmo drag of the selected point: previews along the way, one command on release.
            case "traffic-drag":
                foreach (var step in c.GetProperty("path").EnumerateArray()) s.PreviewLanePoint(new(step[0].GetSingle(), step[1].GetSingle(), step[2].GetSingle()));
                s.CommitLanePreview();
                return new { ok = s.Error.Length == 0, error = s.Error, undo = s.Document.UndoName };
            case "traffic-finish": return new { ok = true, lane = s.FinishTrafficDrawing(), status = s.Status };
            // Mouse gestures of the Traffic tool at viewport pixels: begin (on a point, end, start or the selected lane), move, end.
            case "traffic-drag-begin":
                v.SimulatePointer(new(F("x"), F("y")));
                return new { ok = s.BeginTrafficDrag(new(F("x"), F("y")), v.ViewportPixels), lane = s.SelectedLane, point = s.SelectedLanePoint };
            case "traffic-drag-move":
                v.SimulatePointer(new(F("x"), F("y")));
                s.UpdateTrafficDrag(new(F("x"), F("y")), v.ViewportPixels, c.TryGetProperty("shift", out var dragShift) && dragShift.GetBoolean());
                return new { ok = true, message = s.HoverLabel };
            case "traffic-drag-end":
                s.EndTrafficDrag();
                return new { ok = s.Error.Length == 0, status = s.Status, error = s.Error, undo = s.Document.UndoName, lane = s.SelectedLane, links = s.SelectedLaneState is { } dl ? new { @out = dl.Out.Length, @in = dl.In.Length } : null };
            // Where a lane's points are on screen (viewport pixels), and its links.
            case "traffic-screen":
            {
                string laneKey = c.TryGetProperty("lane", out var lk) ? lk.GetString()! : s.SelectedLane ?? "";
                var state = s.Lane(laneKey);
                return new { ok = state is not null, lane = laneKey, points = state?.Outline.Select(p => s.ScreenOf(Coordinates.GameToView(p), v.ViewportPixels) is { } q ? new[] { q.X, q.Y } : null).ToArray(),
                    game = state?.Outline.Select(p => new[] { p.X, p.Y, p.Z }).ToArray(), @out = state?.Out.Select(l => l.Lane).ToArray(), @in = state?.In.ToArray() };
            }
            case "traffic-hover":
                v.SimulatePointer(c.TryGetProperty("x", out _) ? new(F("x"), F("y")) : null);
                await Task.Delay(150);
                return new { ok = true, kind = s.TrafficHovered.Kind.ToString(), lane = s.TrafficHovered.Lane, point = s.TrafficHovered.Point, target = s.TrafficHovered.Target, label = s.HoverLabel };
            case "traffic-at": return new { ok = true, lane = s.TrafficLaneAt(new(F("x"), F("y")), v.ViewportPixels), point = s.TrafficPointAt(new(F("x"), F("y")), v.ViewportPixels) };
            case "select-many": s.SelectMany(c.GetProperty("ids").EnumerateArray().Select(e => e.GetString()!)); return new { ok = true, selection = s.SelectedIds };
            case "drop": s.DropSelectedToGround(); return new { ok = s.Error.Length == 0, status = s.Status, error = s.Error };
            case "duplicate-selection": s.DuplicateSelected(); return new { ok = s.Error.Length == 0, selection = s.SelectedIds, error = s.Error };
            case "delete-selection": s.DeleteSelected(); return new { ok = s.Error.Length == 0, error = s.Error };
            case "move-selection":
            {
                var lead = s.Selected ?? throw new InvalidOperationException("Nothing selected.");
                s.SetTransform(lead.Transform with { Position = lead.Transform.Position + new Vector3(F("dx"), F("dy"), F("dz")) });
                return new { ok = s.Error.Length == 0, error = s.Error };
            }
            case "select": s.Select(c.GetProperty("id").GetString()); break;
            case "frame-selection": s.FrameSelection(); break;
            case "move-selected":
                if (s.Selected is { } selected) s.SetTransform(selected.Transform with { Position = selected.Transform.Position + new Vector3(F("dx"), F("dy"), F("dz")) });
                break;
            case "objects":
            {
                // Editable sample near the camera, nearest first.
                var eyeGame = Coordinates.ViewToGame(s.Camera.Eye);
                int take = c.TryGetProperty("take", out var t) ? t.GetInt32() : 40;
                var items = s.Document.Objects(true).Where(o => o.Mesh is not null && s.Assets.ContainsKey(o.Mesh.Hash))
                    .OrderBy(o => Vector3.DistanceSquared(o.Transform.Position, eyeGame)).Take(4000)
                    .Select(Describe).ToArray();
                return new { ok = true, count = s.Document.Objects().Count(), objects = items.Where(o => o.canEdit).Take(take).ToArray(),
                    instanced = items.Where(o => o.canEdit && o.instance >= 0 && o.instances > 1).Take(take).ToArray() };
            }
            case "object":
            {
                var id = c.GetProperty("id").GetString()!;
                var item = s.Document.Objects(true).FirstOrDefault(o => o.Id == id);
                return new { ok = true, found = item is not null, item = item is null ? null : Describe(item) };
            }
            case "transform":
            {
                var id = c.GetProperty("id").GetString()!;
                var item = s.Document.GetObject(id);
                var t = item.Transform;
                if (c.TryGetProperty("position", out var p)) t = t with { Position = new(p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle()) };
                if (c.TryGetProperty("yaw", out var yawDeg)) t = t with { Rotation = new EulerAngles(yawDeg.GetSingle(), 0, 0).ToQuaternion() };
                s.Select(id); s.SetTransform(t);
                if (s.Error.Length > 0) throw new InvalidOperationException(s.Error);
                break;
            }
            case "duplicate": s.Select(c.GetProperty("id").GetString()); s.DuplicateSelected(); return new { ok = s.Error.Length == 0, id = s.SelectedId, error = s.Error };
            case "delete": s.Select(c.GetProperty("id").GetString()); s.DeleteSelected(); if (s.Error.Length > 0) throw new InvalidOperationException(s.Error); break;
            case "undo": s.Undo(); break;
            case "redo": s.Redo(); break;
            case "add-mesh":
            {
                var path = c.GetProperty("path").GetString()!;
                var resource = s.Index?.Meshes.FirstOrDefault(m => m.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) ?? throw new FileNotFoundException(path);
                var p = c.GetProperty("position");
                // lineUp: as a click of the Place tool does it (Edit ▸ Preferences), beside what is drawn there.
                s.AddMesh(resource, new(p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle()), c.TryGetProperty("lineUp", out var lineUp) && lineUp.GetBoolean());
                return new { ok = s.Error.Length == 0, id = s.SelectedId, error = s.Error, status = s.Status };
            }
            case "preferences":
                if (c.TryGetProperty("blenderKeys", out var blenderKeys)) _window.SetBlenderKeys(blenderKeys.GetBoolean());
                if (c.TryGetProperty("alignPlaced", out var alignPlaced)) _window.SetAlignPlaced(alignPlaced.GetBoolean());
                // forget: as on a first run, the first G over a selected object asks.
                if (c.TryGetProperty("forget", out var forget) && forget.GetBoolean()) { s.Settings.BlenderKeys = null; s.Settings.Save(); }
                return new { ok = true, blenderKeys = s.Settings.BlenderKeys, alignPlaced = s.Settings.AlignPlaced, status = s.Status };
            case "modal":
            {
                // A transform driven by keys and the pointer alone (Blender shortcuts), from the pipe:
                // press (G, R or S as the keyboard sends it), start (move / rotate / scale), axis (x, y, z; plane),
                // key (a key name while it runs), type (digits), drag ([dx, dy] pixels), confirm, cancel, answer (the question asked on the first G).
                if (c.TryGetProperty("answer", out var answer)) return new { ok = _window.BlenderQuestion(answer.ValueKind == JsonValueKind.Null ? null : answer.GetBoolean()), blenderKeys = s.Settings.BlenderKeys };
                bool pressed = false;
                if (c.TryGetProperty("press", out var press)) pressed = _window.OnBlenderKey(Enum.Parse<Windows.System.VirtualKey>(press.GetString()!, true), false);
                if (c.TryGetProperty("start", out var modalKind) && !v.BeginModal(Enum.Parse<ModalKind>(modalKind.GetString()!, true), simulated: true))
                    return new { ok = false, error = "Nothing a transform key can act on: select an editable object (Select, Move, Rotate or Scale tool)." };
                if (c.TryGetProperty("axis", out var modalAxis)) v.Modal?.PressAxis("xyz".IndexOf(char.ToLowerInvariant(modalAxis.GetString()![0])), c.TryGetProperty("plane", out var modalPlane) && modalPlane.GetBoolean());
                if (c.TryGetProperty("key", out var modalKey)) v.ModalKey(Enum.Parse<Windows.System.VirtualKey>(modalKey.GetString()!, true), c.TryGetProperty("shift", out var modalShift) && modalShift.GetBoolean());
                if (c.TryGetProperty("type", out var modalTyped)) foreach (char digit in modalTyped.GetString()!) v.Modal?.Type(digit);
                if (c.TryGetProperty("drag", out var modalDrag)) v.SimulateModalPointer(new(modalDrag[0].GetSingle(), modalDrag[1].GetSingle()));
                if (c.TryGetProperty("confirm", out var modalConfirm) && modalConfirm.GetBoolean()) v.ConfirmModal();
                if (c.TryGetProperty("cancel", out var modalCancel) && modalCancel.GetBoolean()) v.CancelModal();
                var modal = v.Modal;
                return new { ok = true, pressed, asking = _window.BlenderQuestion(null), active = modal is not null, kind = modal?.Kind.ToString(), axis = modal?.Axis ?? -1, plane = modal?.Plane ?? false, local = modal?.Local ?? false, typed = modal?.Typed ?? "",
                    delta = modal is null ? null : new[] { modal.GameDelta.X, modal.GameDelta.Y, modal.GameDelta.Z }, degrees = modal?.Degrees ?? 0, factor = modal?.Factor ?? 1, status = s.Status, error = s.Error };
            }
            case "loading":
            {
                // World ▸ Loading: follow the camera, keep what is loaded, or load an area and keep it.
                if (c.TryGetProperty("mode", out var hold))
                    switch (hold.GetString())
                    {
                        case "follow": s.FollowCamera(); break;
                        case "keep": if (!s.KeepLoaded()) return new { ok = false, error = s.Status }; break;
                        case "area": if (!s.HoldArea(c.TryGetProperty("radius", out var around) ? around.GetSingle() : s.Settings.HoldRadius)) return new { ok = false, error = s.Status }; break;
                        default: return new { ok = false, error = "mode: follow, keep or area (with radius)" };
                    }
                return new { ok = true, hold = s.Hold.ToString().ToLowerInvariant(), radius = s.HoldRadius, centre = new[] { s.HoldCentre.X, s.HoldCentre.Y, s.HoldCentre.Z }, sectors = s.HeldSectors,
                    leftOut = s.HoldLeftOut, loading = s.HoldLoading, outside = s.HoldOutside, resident = s.StreamMetrics?.ResidentSectors ?? 0, desired = s.StreamMetrics?.DesiredSectors ?? 0, status = s.Status };
            }
            case "project":
                return new { ok = true, project = s.Document.Project, dirty = s.Document.IsDirty, path = s.ProjectPath, canUndo = s.Document.CanUndo, canRedo = s.Document.CanRedo };
            case "new-project": s.NewProject(); break;
            case "save-project": s.SaveProject(c.GetProperty("path").GetString()!); break;
            case "open-project":
                await s.OpenProjectAsync(c.GetProperty("path").GetString()!);
                if (s.Error.Length > 0) throw new InvalidOperationException(s.Error);
                break;
            case "validate": { var errors = await s.ValidateAsync(); return new { ok = true, valid = errors.Count == 0, errors }; }
            case "export":
            {
                // window: in the export window as File ▸ Export runs it (returns at once; the
                // window stays until dialog-close, export-cancel stops the export).
                if (c.TryGetProperty("window", out var inWindow) && inWindow.GetBoolean()) { _ = _window.ExportWithWindow(c.GetProperty("path").GetString()!); return new { ok = true }; }
                var result = await s.ExportAsync(c.GetProperty("path").GetString()!);
                if (result is null) throw new InvalidOperationException(s.Error.Length > 0 ? s.Error : "Export failed.");
                return new { ok = true, result.Directory, result.ManifestPath, result.Manifest };
            }
            case "asset-search": _window.SearchAssets(c.GetProperty("text").GetString()!, c.TryGetProperty("select", out var pick) ? pick.GetInt32() : -1); break;
            case "asset-select": _window.SelectAsset(c.GetProperty("index").GetInt32()); break;
            case "overlays":
                if (c.TryGetProperty("sectors", out var sec)) s.ShowSectorBounds = sec.GetBoolean();
                if (c.TryGetProperty("selection", out var sel)) s.ShowSelectionBounds = sel.GetBoolean();
                if (c.TryGetProperty("distant", out var far)) s.DistantCity = far.GetBoolean();
                if (c.TryGetProperty("markers", out var marks)) s.ShowMarkers = marks.GetBoolean();
                if (c.TryGetProperty("doorIcons", out var doorIcons)) s.ShowDoorIcons = doorIcons.GetBoolean();
                // World ▸ Draw distance (0: no limit).
                if (c.TryGetProperty("drawDistance", out var drawDistance)) _window.SetDrawDistance(drawDistance.GetSingle());
                // The icons of the project's own elements: shown or not, and within which distance.
                if (c.TryGetProperty("projectIcons", out var projectIcons) || c.TryGetProperty("projectIconRange", out _))
                    _window.SetProjectIcons(projectIcons.ValueKind == JsonValueKind.Undefined ? null : projectIcons.GetBoolean(), c.TryGetProperty("projectIconRange", out var projectIconRange) ? projectIconRange.GetSingle() : null);
                if (c.TryGetProperty("iconRange", out var iconRange)) s.Settings.IconRange = iconRange.GetSingle();
                if (c.TryGetProperty("collision", out var coll)) s.ShowCollision = coll.GetBoolean();
                if (c.TryGetProperty("shading", out var shade)) v.Renderer.Shading = Enum.Parse<SceneShading>(shade.GetString()!, true);
                if (c.TryGetProperty("night", out var night)) v.Renderer.NightPreview = night.GetBoolean();
                return new { ok = true, scene = v.Renderer.SceneLines.Count, overlay = v.Renderer.OverlayLines.Count, drawDistance = v.Renderer.DrawDistance };
            case "material-view": return new { ok = true, count = _window.OpenMaterialTile(c.TryGetProperty("index", out var tile) ? tile.GetInt32() : 0) };
            case "materials":
            {
                var r = v.Renderer;
                if (c.TryGetProperty("on", out var materialsOn) || c.TryGetProperty("range", out _) || c.TryGetProperty("textures", out _))
                    _window.SetMaterials(materialsOn.ValueKind == JsonValueKind.Undefined ? r.MaterialRange > 0 : materialsOn.GetBoolean(),
                        c.TryGetProperty("range", out var materialRange) ? materialRange.GetSingle() : null, c.TryGetProperty("textures", out var textureLimit) ? textureLimit.GetInt32() : null);
                return new { ok = true, shading = r.Shading.ToString(), range = r.MaterialRange, textureLimit = r.TextureLimit, surfaces = r.SurfaceCount, textures = r.TextureCount,
                    textureMb = r.TextureBytes >> 20, loading = r.MaterialLoads, drawn = r.MaterialDraws, fps = v.FramesPerSecond };
            }
            case "surface":
            {
                // Where an object's game materials stand (tests, diagnostics).
                var o = s.Document.TryGetObject(c.GetProperty("id").GetString()!) ?? throw new KeyNotFoundException("No such object.");
                if (o.Original?.EntityParts is { IsDefaultOrEmpty: false } entityParts)
                    return new { ok = true, parts = entityParts.Select(p => new { mesh = p.Mesh.Path, appearance = p.Mesh.Appearance, surface = v.Renderer.SurfaceDiagnostic(p.Mesh.Hash, p.Mesh.Appearance) }).ToArray() };
                if (o.Mesh is not { } mesh) return new { ok = false, error = "The object has no mesh." };
                return new { ok = true, mesh = mesh.Path, appearance = mesh.Appearance, surface = v.Renderer.SurfaceDiagnostic(s.MeshKeyFor(o.Id, mesh), mesh.Appearance) };
            }
            case "rename-title": return _window.RenameFromTitle(c.TryGetProperty("name", out var titleName) ? titleName.GetString() : null);
            case "home": return _window.HomeUi(c.TryGetProperty("action", out var homeAction) ? homeAction.GetString()! : "state");
            case "layer":
                _window.SetLayer(Enum.Parse<StudioSession.EditLayer>(c.GetProperty("name").GetString()!, true));
                if (c.TryGetProperty("range", out var layerRange)) _window.SetCollisionRange(layerRange.GetSingle());
                if (c.TryGetProperty("occluderRange", out var occluderRange)) s.OccluderRange = occluderRange.GetSingle();
                if (c.TryGetProperty("maxOccluder", out var maxOccluder)) s.MaxOccluderSize = maxOccluder.GetSingle();
                if (c.TryGetProperty("xray", out var layerXray)) s.LayerXray = layerXray.GetBoolean();
                if (c.TryGetProperty("fade", out var layerFade)) s.FadeUnselected = layerFade.GetBoolean();
                if (c.TryGetProperty("dim", out var layerDim)) s.DimWorld = layerDim.GetBoolean();
                if (c.TryGetProperty("game", out var layerGame)) s.ShowGameCollision = layerGame.GetBoolean();
                if (c.TryGetProperty("project", out var layerProject)) s.ShowProjectCollision = layerProject.GetBoolean();
                if (c.TryGetProperty("hidden", out var layerHidden))
                {
                    var set = s.Layer == StudioSession.EditLayer.Occlusion ? s.HiddenOccluderTypes : s.HiddenCollisionPresets;
                    set.Clear(); foreach (var name in layerHidden.EnumerateArray()) set.Add(name.GetString()!);
                }
                s.LayerDisplayVersion++; _window.RefreshLayerPanel();
                return new { ok = true, layer = s.Layer.ToString(), s.CollisionRange, s.OccluderRange, s.CollisionCapped, s.OccluderCapped };
            case "collision":
            {
                // Collision and occlusion state around the camera and of the selection.
                var eyeGame = Coordinates.ViewToGame(s.Camera.Eye);
                var near = s.Document.ResidentSectors.Values.SelectMany(sector => sector.CollisionActors.Select(a => (sector, a)))
                    .OrderBy(e => Vector3.DistanceSquared(e.a.Transform.Position, eyeGame)).Take(c.TryGetProperty("take", out var ct) ? ct.GetInt32() : 5)
                    .Select(e => new { id = StudioSession.ActorId(e.sector, e.a), node = e.a.Node, distance = Vector3.Distance(e.a.Transform.Position, eyeGame), shapes = e.a.Shapes.Select(x => x.Kind + (x.IsMesh ? "" : $" {x.Size.X:0.##}")).ToArray() }).ToArray();
                var nearOccluders = s.Document.ResidentSectors.Values.SelectMany(sector => sector.Placements.Where(p => p.OccluderNode))
                    .Select(p => (p, at: p.InstanceOffsets.IsDefaultOrEmpty ? p.Transform.Position : p.Transform.Apply(p.InstanceOffsets[0]).Position))
                    .OrderBy(e => (e.p.InstanceOffsets.IsDefaultOrEmpty ? 1 : 0) + Vector3.DistanceSquared(e.at, eyeGame) / 1e6f).Take(c.TryGetProperty("occluderTake", out var ot) ? ot.GetInt32() : 0)
                    .Select(e => new { id = e.p.Id, e.p.Name, type = e.p.Source.NodeType, instances = e.p.InstanceOffsets.Length, distance = Vector3.Distance(e.at, eyeGame) }).ToArray();
                var picked = s.Selected;
                return new
                {
                    ok = true, layer = s.Layer.ToString(), status = s.Status, error = s.Error, actors = s.Document.ResidentSectors.Values.Sum(x => x.CollisionActors.Length),
                    projectCollisions = s.Document.Project.Changes.Collisions.Count, near, nearOccluders, xray = v.Renderer.XrayLines.Count + v.Renderer.StaticXrayCount, overlay = v.Renderer.OverlayLines.Count,
                    occluders = s.Document.ResidentSectors.Values.Sum(x => x.Placements.Count(p => p.OccluderNode)),
                    selected = picked is null ? null : new { picked.Id, picked.Name, collision = picked.IsCollision, shapes = picked.CollisionShapes.Select(x => new { kind = x.Kind.ToString(), x.Preset, x.Material, size = new[] { x.Size.X, x.Size.Y, x.Size.Z } }).ToArray(),
                        occluder = picked.Occluder, element = picked.ElementProperties?.Kind.ToString(), position = new[] { picked.Transform.Position.X, picked.Transform.Position.Y, picked.Transform.Position.Z },
                        collisionOf = s.Document.CollisionOf(picked.Id), occludersOf = s.Document.OccludersOf(picked.Id), disabled = s.Document.Project.Changes.Modified.GetValueOrDefault(picked.Id)?.DisabledShapes }
                };
            }
            case "hover":
                v.SimulatePointer(c.TryGetProperty("x", out _) ? new(F("x"), F("y")) : null);
                await Task.Delay(300);
                return new { ok = true, label = s.HoverLabel };
            case "collision-scroll": _window.ScrollCollisionPanel(c.GetProperty("offset").GetDouble()); break;
            case "collision-do":
            {
                string action = c.GetProperty("action").GetString()!;
                var at = c.TryGetProperty("position", out var cp) ? new Vector3(cp[0].GetSingle(), cp[1].GetSingle(), cp[2].GetSingle()) : Coordinates.ViewToGame(s.Camera.Eye + s.Camera.Forward * 6);
                switch (action)
                {
                    case "pick": s.Pick(new(F("x"), F("y")), v.ViewportPixels); break;
                    case "add": s.AddCollisionAt(Enum.Parse<CollisionShapeKind>(c.GetProperty("kind").GetString()!, true), at); break;
                    case "fit": s.FitCollisionToSelection(); break;
                    case "editable": if (s.SelectedId is { } editable) s.MakeCollisionEditable(editable); break;
                    case "shape": if (s.SelectedId is { } shapeOwner) s.SetCollisionShapeEnabled(shapeOwner, c.GetProperty("index").GetInt32(), c.GetProperty("enabled").GetBoolean()); break;
                    case "resize":
                        if (s.Selected?.AuthoredCollision is { } project)
                            s.UpdateCollision(project with { Shapes = project.Shapes.SetItem(c.GetProperty("index").GetInt32(), project.Shapes[c.GetProperty("index").GetInt32()] with { Size = new(F("sx"), F("sy"), F("sz")) }) }, "Resize collision shape");
                        break;
                    case "preset":
                        if (s.Selected?.AuthoredCollision is { } styled)
                            s.UpdateCollision(styled with { Shapes = styled.Shapes.Select(x => x with { Preset = c.GetProperty("value").GetString()! }).ToImmutableArray() }, "Change collision preset");
                        break;
                    case "occluder": s.AddOccluderAt(c.TryGetProperty("plane", out var plane) && plane.GetBoolean(), at); break;
                    case "mesh-occluder": s.SetMeshOccluder(new MeshOccluder(c.GetProperty("value").GetString()!)); break;
                    case "expand": if (s.SelectedId is { } expand) s.ExpandInstancedOccluder(expand); break;
                    default: throw new ArgumentException("Unknown collision action " + action);
                }
                return new { ok = s.Error.Length == 0, status = s.Status, error = s.Error, selected = s.SelectedIds.Take(20).ToArray(), projectCollisions = s.Document.Project.Changes.Collisions.Count, undo = s.Document.UndoName };
            }
            case "export-open77":
            {
                // window: in the export window, as File ▸ Export for Open77 runs it (returns at once).
                if (c.TryGetProperty("window", out var inWindow) && inWindow.GetBoolean()) { _ = _window.ExportOpen77WithWindow(c.GetProperty("path").GetString()!); return new { ok = true }; }
                var report = await s.ExportOpen77Async(c.GetProperty("path").GetString()!) ?? throw new InvalidOperationException(s.Error.Length > 0 ? s.Error : "Open77 export failed.");
                return new { ok = true, report };
            }
            case "export-worldbuilder":
            {
                var report = s.ExportWorldBuilder(c.GetProperty("path").GetString()!);
                if (report is null) throw new InvalidOperationException(s.Error);
                return new { ok = true, report };
            }
            case "route-start":
            {
                var points = c.GetProperty("points").EnumerateArray().Select(p => new Vector3(p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle())).ToArray();
                v.Route = new Viewport.RouteProfiler(points, F("speed"));
                return new { ok = true };
            }
            case "route-status":
                return new { ok = true, done = v.Route?.Done ?? true, progress = v.Route?.Progress ?? 1,
                    report = v.Route is { Done: true } finished ? finished.Report(v.Renderer, s, (int)v.ViewportPixels.X, (int)v.ViewportPixels.Y) : null };
            case "streamer": return new { ok = true, state = s.StreamerInspection, timings = new { s.PlanMilliseconds, s.AdvanceMilliseconds } };
            case "streaming-idle":
                var m = s.StreamMetrics;
                return new { ok = true, idle = m is not null && m.PendingSectors == 0 && m.PendingMeshes == 0 && m.ActiveWorkers == 0 && v.Renderer.PendingWorldUploads == 0, metrics = m };
            case "road-template": return new { ok = true, result = await _window.ChooseRoadTemplate(c.GetProperty("text").GetString()!, c.TryGetProperty("select", out var rs) ? rs.GetInt32() : 0) };
            case "road-create": { string? id = s.CreateRoad(Points("points")); return RoadState(s, id is not null); }
            case "road-draw": s.StartRoadDrawing(); return RoadState(s);
            case "road-continue": s.ContinueRoadDrawing(); return RoadState(s);
            case "road-finish": s.FinishRoadDrawing(); return RoadState(s);
            case "road-click":
                s.RoadClick(new(F("x"), F("y")), v.ViewportPixels, c.TryGetProperty("shift", out var rcShift) && rcShift.GetBoolean(), c.TryGetProperty("ctrl", out var rcCtrl) && rcCtrl.GetBoolean());
                return RoadState(s);
            case "road-hover":
            {
                // The Roads tool's cursor at a viewport pixel (the drawing ghost, handles, curve).
                var pixel = new Vector2(F("x"), F("y"));
                bool shift = c.TryGetProperty("shift", out var rhShift) && rhShift.GetBoolean(), control = c.TryGetProperty("ctrl", out var rhCtrl) && rhCtrl.GetBoolean();
                v.SimulatePointer(pixel, shift, control);
                s.UpdateRoadHover(pixel, v.ViewportPixels, shift, control);
                await Task.Delay(Int(c, "wait_ms", 400));
                var cursor = s.RoadHover;
                return new
                {
                    ok = true, handle = s.RoadHoverHandle, insert = s.RoadInsertHover is { } ins ? new { index = ins.Index, point = new[] { ins.Point.X, ins.Point.Y, ins.Point.Z } } : null,
                    cursor = cursor is null ? null : new
                    {
                        point = new[] { cursor.Point.X, cursor.Point.Y, cursor.Point.Z }, length = cursor.Length, grade = cursor.Grade, radius = cursor.Radius, halfWidth = cursor.HalfWidth,
                        snap = cursor.Snap?.Label, snapRoad = cursor.Snap?.RoadId, cursor.Angled, cursor.Level
                    },
                    ghost = s.RoadGhostPoints, handles = s.RoadHandles(v.ViewportPixels).Select(h => new { h.Index, x = h.Screen.X, y = h.Screen.Y }).ToArray(),
                    ends = c.TryGetProperty("ends", out var ends) && ends.GetBoolean() && s.CameraGame() is var camera
                        ? s.SnapTargetsNear(camera.Eye, 400).Select(t => (t, screen: s.RoadScreen(t.Point, v.ViewportPixels))).Where(e => e.screen is { } p && p.X > 0 && p.Y > 0 && p.X < v.ViewportPixels.X && p.Y < v.ViewportPixels.Y)
                            .Take(12).Select(e => new { e.t.Label, road = e.t.RoadId, x = e.screen!.Value.X, y = e.screen.Value.Y, point = new[] { e.t.Point.X, e.t.Point.Y, e.t.Point.Z } }).ToArray()
                        : null,
                    state = RoadState(s)
                };
            }
            case "road-backspace": s.RoadBackspace(); return RoadState(s);
            case "road-drag":
            {
                var from = c.GetProperty("from"); var to = c.GetProperty("to");
                Vector2 a = new(from[0].GetSingle(), from[1].GetSingle()), b = new(to[0].GetSingle(), to[1].GetSingle());
                if (!s.BeginRoadDrag(a, v.ViewportPixels)) return new { ok = false, error = "Nothing to drag under that pixel." };
                bool moved = Vector2.Distance(a, b) > 3;
                if (moved) for (int i = 1; i <= 8; i++) { s.UpdateRoadDrag(Vector2.Lerp(a, b, i / 8f), v.ViewportPixels, c.TryGetProperty("ctrl", out var dc) && dc.GetBoolean()); await Task.Delay(20); }
                s.EndRoadDrag(moved);
                return RoadState(s);
            }
            case "road-select": s.SelectRoad(c.GetProperty("id").GetString(), c.TryGetProperty("point", out var rp) ? rp.GetInt32() : -1); return RoadState(s);
            case "road-point":
            {
                var p = c.GetProperty("position");
                var position = new Vector3(p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle());
                s.SelectRoadPoint(c.GetProperty("index").GetInt32());
                if (c.TryGetProperty("preview", out _)) s.PreviewRoadPoint(position); else s.SetRoadPoint(s.SelectedRoadPoint, position);
                return RoadState(s);
            }
            case "road-edit":
                switch (c.GetProperty("action").GetString())
                {
                    case "insert": s.InsertRoadPoint(); break;
                    case "delete-point": s.DeleteRoadPoint(); break;
                    case "reverse": s.ReverseRoad(); break;
                    case "snap": s.SnapRoadToGround(); break;
                    case "banking": s.SetRoadBanking(F("value")); break;
                    case "rename": s.RenameRoad(c.GetProperty("value").GetString()!); break;
                    case "delete": s.DeleteSelectedRoad(); break;
                    case "rebuild": s.RebuildSelectedAsRoad(); break;
                    case "commit": s.CommitRoadPreview(); break;
                    case "apply-type": if (s.RoadTemplate is { } type) s.SetRoadTemplate(type); break;
                    case "frame": if (s.SelectedRoad is { } framed) s.FrameRoad(framed); break;
                    case "smooth": s.SmoothRoadProfile(); break;
                    case "even-grade": s.EvenRoadGrade(); break;
                    case "overpass": s.SetRoadOverpass(!c.TryGetProperty("value", out var ov) || ov.GetBoolean()); break;
                    case "ground":
                        s.SetRoadGround(c.TryGetProperty("on", out var on) && !on.GetBoolean() ? null
                            : new RoadGround(c.TryGetProperty("slope", out var sl) ? sl.GetSingle() : 33, c.TryGetProperty("margin", out var mg) ? mg.GetSingle() : 1.5f,
                                !c.TryGetProperty("clear_plants", out var cp) || cp.GetBoolean()));
                        break;
                    default: throw new ArgumentException("Unknown road action");
                }
                return RoadState(s);
            case "roads": return RoadState(s);
            case "road-game-roads": return new { ok = true, roads = s.GameRoadsAround().ToArray(), pieces = c.TryGetProperty("x", out _) ? s.GamePiecesNear(new(F("x"), F("y")), Float(c, "radius", 30)).ToArray() : null };
            case "road-join-game": { if (Str(c, "id") is { } joinId) s.SelectRoad(joinId); int rebuilt = await _window.RebuildGameRoadsAtJunctions(); return new { ok = true, rebuilt, state = RoadState(s) }; }
            case "elevator-type": return new { ok = true, result = _window.ChooseElevatorType(c.GetProperty("type").GetString()!, Str(c, "appearance")) };
            case "elevator":
            {
                int Index() => c.TryGetProperty("floor", out var fl) ? fl.GetInt32() : s.SelectedElevatorFloor;
                Vector3 At() { var p = c.GetProperty("position"); return new(p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle()); }
                if (Str(c, "id") is { } chosen) s.SelectElevator(chosen, c.TryGetProperty("floor", out var sf) ? sf.GetInt32() : -1);
                switch (Str(c, "action") ?? "state")
                {
                    case "create":
                    {
                        var type = ElevatorTypes.Find(Str(c, "type") ?? "common") ?? throw new ArgumentException("Unknown elevator type.");
                        IReadOnlyList<ElevatorFloor>? floors = c.TryGetProperty("heights", out var hs) ? hs.EnumerateArray().Select((h, i) => new ElevatorFloor(h.GetSingle(), ElevatorDefinition.DefaultFloorName(i))).ToList() : null;
                        s.CreateElevator(At(), type, Str(c, "appearance") ?? "", c.TryGetProperty("yaw", out var y) ? y.GetSingle() : null, floors, Str(c, "name"));
                        break;
                    }
                    case "convert": s.ConvertToElevator(Str(c, "element")!); break;
                    case "arm": s.ArmElevator(new(ElevatorTypes.Find(Str(c, "type") ?? "common") ?? ElevatorTypes.Default, Str(c, "appearance") ?? "")); break;
                    case "click": s.ElevatorClick(new(F("x"), F("y")), v.ViewportPixels); break;
                    case "select-floor": s.SelectElevatorFloor(Index()); break;
                    case "add-floor-at": s.AddElevatorFloorAt(F("z"), Str(c, "name")); break;
                    case "add-above": s.AddElevatorFloor(above: true); break;
                    case "add-below": s.AddElevatorFloor(above: false); break;
                    case "floor":
                        s.SetElevatorFloor(Index(), c.TryGetProperty("height", out var fh) ? fh.GetSingle() : null, Str(c, "name"), c.TryGetProperty("door", out var fd) ? fd.GetBoolean() : null);
                        break;
                    case "remove-floor": s.RemoveElevatorFloor(Index()); break;
                    case "floors": s.SetElevatorFloors(c.GetProperty("count").GetInt32(), F("spacing")); break;
                    case "type": s.SetElevatorType(Str(c, "type")!); break;
                    case "look": s.SetElevatorAppearance(Str(c, "appearance")!); break;
                    case "speed": s.SetElevatorSpeed(F("value")); break;
                    case "start": s.SetElevatorStartFloor(c.GetProperty("value").GetInt32()); break;
                    case "panel-left": s.SetElevatorPanelSide(c.GetProperty("value").GetBoolean()); break;
                    case "shaft": s.SetElevatorShaft(c.GetProperty("value").GetBoolean()); break;
                    case "yaw": s.SetElevatorYaw(F("value")); break;
                    case "move": s.SetElevatorPosition(At()); break;
                    case "rename": s.RenameElevator(Str(c, "name")!); break;
                    case "duplicate": s.DuplicateSelectedElevator(); break;
                    case "ground": s.SnapElevatorToGround(); break;
                    case "remove": s.DeleteSelectedElevator(); break;
                    case "frame": if (s.SelectedElevator is { } framed) s.FrameElevator(framed); break;
                    case "goto-floor": s.GoToElevatorFloor(Index()); break;
                    case "deselect": s.SelectElevator(null); break;
                    case "state": break;
                    default: throw new ArgumentException("Unknown elevator action");
                }
                return new
                {
                    ok = s.Error.Length == 0, error = s.Error, status = s.Status, selected = s.SelectedElevatorId, floor = s.SelectedElevatorFloor, armed = s.ArmedElevator?.Type.Key, undo = s.Document.UndoName,
                    elevators = s.Elevators.Values.Select(e => new
                    {
                        e.Id, e.Name, e.Type, appearance = e.CabinAppearance, position = new[] { MathF.Round(e.Position.X, 2), MathF.Round(e.Position.Y, 2), MathF.Round(e.Position.Z, 2) }, e.Yaw, e.Speed, e.StartFloor, e.PanelOnLeft, e.Shaft,
                        floors = e.Floors.Select(f => new { height = MathF.Round(f.Height, 2), f.Name, f.Door }).ToArray()
                    }).ToArray()
                };
            }
            case "doors":
            {
                var eyeAt = Coordinates.ViewToGame(s.Camera.Eye);
                var doors = s.Document.Objects().Where(o => o.Original?.Entity is { IsDoor: true }).OrderBy(o => Vector3.DistanceSquared(o.Transform.Position, eyeAt)).Take(c.TryGetProperty("take", out var dt) ? dt.GetInt32() : 15)
                    .Select(o => new { o.Id, o.Name, o.CanEdit, template = o.Original!.Entity!.Template, appearance = o.Original.Entity.Appearance, door = o.Original.Entity.Door, parts = o.Original.EntityParts.Length,
                        position = new[] { o.Transform.Position.X, o.Transform.Position.Y, o.Transform.Position.Z }, quest = o.Original.QuestPrefabRef != 0 }).ToArray();
                int entities = s.Document.Objects().Count(o => o.Original?.Entity is not null);
                var sample = s.Document.Objects().Where(o => o.Original?.Source.NodeType == "worldEntityNode").Take(3)
                    .Select(o => new { o.Id, o.Diagnostic, entity = o.Original!.Entity is not null, parts = o.Original.EntityParts.Length }).ToArray();
                return new { ok = true, entities, texturedDoorMeshes = s.TexturedDoorMeshes, entityNodes = s.Document.Objects().Count(o => o.Original?.Source.NodeType == "worldEntityNode"), sample, doors };
            }
            case "proxies":
            {
                var eyeAt = Coordinates.ViewToGame(s.Camera.Eye);
                var near = s.Document.Objects(true).Where(o => o.Original is { ProxyOwner: not 0 }).OrderBy(o => Vector3.DistanceSquared(o.Transform.Position, eyeAt)).Take(c.TryGetProperty("take", out var pt) ? pt.GetInt32() : 10)
                    .Select(o => new { o.Id, o.Name, mesh = o.Mesh?.Path, o.CanEdit, o.Deleted, quest = o.Original!.QuestPrefabRef != 0, hideNear = o.Original.NearHideDistance, linked = s.Document.LinkedTo(o.Id),
                        position = new[] { o.Transform.Position.X, o.Transform.Position.Y, o.Transform.Position.Z } }).ToArray();
                var entities = s.Document.Objects().Where(o => o.Original is { Source.NodeType: "worldEntityNode" or "worldDeviceNode" }).OrderBy(o => Vector3.DistanceSquared(o.Transform.Position, eyeAt)).Take(12)
                    .Select(o => new { o.Id, o.Name, template = o.Original!.Entity?.Template, globalId = o.Original.Source.PlacementId.ToString("x16"), quest = o.Original.QuestPrefabRef.ToString("x16"), appearance = o.Original.Entity?.Appearance, position = new[] { o.Transform.Position.X, o.Transform.Position.Y, o.Transform.Position.Z } }).ToArray();
                var owners = s.Document.Objects().Where(o => o.Original is { ProxyOwner: not 0 }).OrderBy(o => Vector3.DistanceSquared(o.Transform.Position, eyeAt)).Take(6).Select(o => o.Original!.ProxyOwner.ToString("x16") + " quest " + o.Original.QuestPrefabRef.ToString("x16") + " " + Path.GetFileName(o.Mesh?.Path)).ToArray();
                return new { ok = true, owners, entities, total = s.Document.Objects().Count(o => o.Original is { ProxyOwner: not 0 }), questEditable = s.Document.Objects().Count(o => o.Original is { QuestPrefabRef: not 0 } && o.CanEdit), near };
            }
            case "element-arm":
            {
                var kind = Enum.Parse<ElementKind>(c.GetProperty("kind").GetString()!, true);
                var props = ElementProperties.Preset(kind, c.TryGetProperty("light", out var lt) ? Enum.Parse<LightKind>(lt.GetString()!, true) : LightKind.Point);
                if (c.TryGetProperty("resource", out var res)) props = props with { Resource = res.GetString()! };
                SourceAnchor? template = null;
                if (kind == ElementKind.Sound) { var sound = s.SoundTemplates().FirstOrDefault(); if (sound.Placement is null) throw new InvalidOperationException("No sound emitter loaded."); props = sound.Placement.Element!; template = sound.Placement.Source; }
                s.ArmElement(new(c.TryGetProperty("name", out var nm) ? nm.GetString()! : kind.ToString(), props, template));
                return new { ok = true, status = s.Status };
            }
            case "element-place":
            {
                var id = s.PlaceElement(new(F("x"), F("y")), v.ViewportPixels);
                return new { ok = id is not null, id, error = s.Error, status = s.Status };
            }
            case "element-edit":
            {
                var p = s.SelectedElementProperties ?? throw new InvalidOperationException("No element selected.");
                if (c.TryGetProperty("intensity", out var i)) p = p with { Intensity = i.GetSingle() };
                if (c.TryGetProperty("radius", out var r)) p = p with { Radius = r.GetSingle() };
                if (c.TryGetProperty("temperature", out var k)) p = p with { Temperature = k.GetSingle() };
                if (c.TryGetProperty("color", out var col)) p = p with { Color = new(col[0].GetSingle(), col[1].GetSingle(), col[2].GetSingle()), Temperature = -1 };
                if (c.TryGetProperty("type", out var ty)) p = p with { LightType = Enum.Parse<LightKind>(ty.GetString()!, true) };
                if (c.TryGetProperty("resource", out var path)) p = p with { Resource = path.GetString()! };
                if (c.TryGetProperty("alpha", out var al)) p = p with { Alpha = al.GetSingle() };
                if (c.TryGetProperty("occluderType", out var ot)) p = p with { OccluderType = ot.GetString()! };
                if (c.TryGetProperty("autohide", out var ah)) p = p with { AutohideScale = ah.GetInt32() };
                if (c.TryGetProperty("appearance", out var look)) p = p with { Appearance = look.GetString()! };
                if (c.TryGetProperty("door", out var dj))
                {
                    var d = p.Door ?? new DoorSettings();
                    if (dj.TryGetProperty("type", out var v1)) d = d with { DoorType = v1.GetString()! };
                    if (dj.TryGetProperty("state", out var v2)) d = d with { InitialState = v2.GetString()! };
                    if (dj.TryGetProperty("opening", out var v3)) d = d with { Opening = v3.GetString()! };
                    if (dj.TryGetProperty("delay", out var v4)) d = d with { CloseDelay = v4.GetSingle() };
                    if (dj.TryGetProperty("autoClose", out var v5)) d = d with { ClosesAutomatically = v5.GetBoolean() };
                    p = p with { Door = d };
                }
                bool ok = s.UpdateSelectedElement(p);
                return new { ok, error = s.Error, selected = s.SelectedId, properties = s.SelectedElementProperties };
            }
            case "elements":
                return new { ok = true, selected = s.SelectedId, properties = s.SelectedElementProperties, elements = s.Elements.Values.Select(e => new { e.Id, e.Name, e.Properties, position = new[] { e.Transform.Position.X, e.Transform.Position.Y, e.Transform.Position.Z }, template = e.Template?.ObjectId, replaces = e.Replaces.Length }).ToArray(),
                    sounds = s.SoundTemplates().Take(10).Select(t => t.Name).ToArray() };
            case "marquee":
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                int count = s.SelectInRectangle(new(F("x0"), F("y0")), new(F("x1"), F("y1")), v.ViewportPixels, c.TryGetProperty("additive", out var more) && more.GetBoolean());
                long marqueeMs = timer.ElapsedMilliseconds;
                return new { ok = true, count, ms = marqueeMs, status = s.Status, selection = s.SelectedIds.Take(50).ToArray(), total = s.SelectedIds.Count };
            }
            case "icons":
                return new { ok = true, count = s.Icons.Count, project = s.Icons.Count(i => i.Authored), projectIcons = s.ShowProjectIcons, projectIconRange = s.ProjectIconRange, kinds = s.Icons.GroupBy(i => i.Kind).ToDictionary(g => g.Key, g => g.Count()),
                    nearest = s.Icons.OrderBy(i => i.Depth).Take(10).Select(i => new { i.Id, i.Kind, x = i.Screen.X, y = i.Screen.Y, i.Depth, i.Editable }).ToArray() };
            case "screenshot":
            {
                string? which = c.TryGetProperty("window", out var w) ? w.GetString() : null;
                if (which == "popups") return new { ok = true, path = CapturePopups(_window, c.GetProperty("path").GetString()!) };
                Microsoft.UI.Xaml.Window target = which switch
                {
                    "library" => _window.Library ?? throw new InvalidOperationException("The library is not open."),
                    "mcp" => _window.McpWindow ?? throw new InvalidOperationException("The AI agents window is not open."),
                    "splash" => _splash ?? throw new InvalidOperationException("No splash preview: send {\"op\":\"splash\"} first."),
                    _ => _window
                };
                return new { ok = true, path = Capture(target, c.GetProperty("path").GetString()!) };
            }
            // The startup splash, opened off-screen for checks (it never closes on its own here).
            case "splash":
                if (c.TryGetProperty("close", out var closeSplash) && closeSplash.GetBoolean()) { _splash?.Close(); _splash = null; break; }
                _splash ??= new SplashWindow();
                _splash.ShowOffscreen();
                break;
            case "library":
            {
                var library = _window.OpenLibrary() ?? throw new InvalidOperationException(s.Status);
                if (c.TryGetProperty("action", out var action))
                    library.Automate(action.GetString()!, c.TryGetProperty("value", out var lv) ? lv.GetString() : null, c.TryGetProperty("index", out var li) ? li.GetInt32() : -1);
                if (c.TryGetProperty("wait", out var lw)) await Task.Delay(lw.GetInt32());
                return library.State();
            }
            case "library-close": _window.Library?.Close(); break;
            case "agent-feed": return _window.FeedState();
            // Tests the lost-device path (the view stops, autosave, the restart dialog) without a driver reset.
            case "simulate-device-lost":
                v.LoseDevice("simulated", null);
                await Task.Delay(500);
                return new { ok = true, lost = v.DeviceLost, error = s.Error };
            // The AI agents window (Help ▸ AI agents): open it on a page, run its diagnostics, read it.
            case "mcp-window":
            {
                var window = _window.OpenMcpWindow();
                return await window.Ui(c.TryGetProperty("action", out var mw) ? mw.GetString()! : "state", c.TryGetProperty("page", out var mp) ? mp.GetString() : null);
            }
            case "mcp-window-close": _window.McpWindow?.Close(); break;
            // Help ▸ Credits and licenses: open it on a page, filter or pick a component, read it.
            case "credits":
            {
                var credits = _window.OpenCredits(c.TryGetProperty("page", out var cp) ? cp.GetString()! : _window.Credits?.Page ?? "about");
                if (c.TryGetProperty("filter", out var cf)) credits.Filter(cf.GetString() ?? "");
                if (c.TryGetProperty("select", out var cs) && !credits.Select(cs.GetString() ?? "")) throw new InvalidOperationException($"No component named \"{cs.GetString()}\".");
                await Task.Delay(c.TryGetProperty("wait", out var cw) ? cw.GetInt32() : 400);
                return credits.State();
            }
            case "credits-close": _window.CloseCredits(); break;
            case "bridge-cycle": return _window.CycleBridge(c.TryGetProperty("ms", out var bms) ? bms.GetInt32() : 2000);
            case "entity-mesh":
            {
                var index = s.Index ?? throw new InvalidOperationException("No game.");
                string path = c.GetProperty("path").GetString()!;
                var resource = index.Find(WolvenKit.Common.FNV1A.FNV1A64HashAlgorithm.HashString(path)) ?? throw new InvalidOperationException("Not found: " + path);
                int size = c.TryGetProperty("size", out var es) ? es.GetInt32() : 256;
                var mesh = path.EndsWith(".ent", StringComparison.OrdinalIgnoreCase)
                    ? await index.ReadEntityMeshAsync(resource.Hash, c.TryGetProperty("appearance", out var ea) ? ea.GetString()! : "", size > 0, size)
                    : await index.ReadSurfacedMeshAsync(resource.Hash, size);
                return new { ok = true, parts = mesh.Parts.Length, textured = mesh.PartTextures?.Count(t => t is not null) ?? 0, lods = mesh.Parts.Select(p => p.LodMask).Distinct().ToArray(),
                    colors = mesh.AppearanceColors?.Keys.ToArray(), errors = index.SurfaceErrors.TakeLast(6).ToArray() };
            }
            case "outliner-ui":
                return _window.OutlinerUi(c.GetProperty("action").GetString()!, c.TryGetProperty("key", out var olKey) ? olKey.GetString() : null, c.TryGetProperty("text", out var olText) ? olText.GetString() : null,
                    c.TryGetProperty("ctrl", out var olCtrl) && olCtrl.GetBoolean(), c.TryGetProperty("shift", out var olShift) && olShift.GetBoolean(),
                    c.TryGetProperty("keys", out var olKeys) ? olKeys.EnumerateArray().Select(k => k.GetString()!).ToArray() : null);
            case "rename-selection": _window.RenameSelection(); break;
            // Custom mesh dialogs for checks: opened without waiting, closed by "dialog-close".
            case "custom-ui":
                switch (c.GetProperty("action").GetString())
                {
                    case "import-dialog": _ = _window.ImportModelsAsync([c.GetProperty("path").GetString()!], null); break;
                    case "materials": if (s.CustomMeshes.Get(c.GetProperty("id").GetString()!) is { } custom) _window.OpenCustomMaterials(custom); break;
                    case "show": _window.ShowAddCategory("Custom"); if (c.TryGetProperty("id", out var cid)) _window.SelectCustom(cid.GetString()!); break;
                    case "sounds": _window.ShowAddCategory("Sound"); if (c.TryGetProperty("id", out var sid)) _window.SelectSound(sid.GetString()!, arm: false); break;
                }
                break;
            case "dialog-close": _window.CloseDialogs(); break;
            case "export-cancel": s.CancelExport(); break;
            // Editing helpers (tests): copy/paste through text (the Windows clipboard stays untouched),
            // hide/isolate/lock, align, series, pivot, prefabs.
            case "edit":
            {
                string action = c.GetProperty("action").GetString()!;
                switch (action)
                {
                    case "copy": return new { ok = true, text = s.CopySelection(), status = s.Status };
                    case "paste":
                    {
                        var content = StudioSession.ParseClipboard(c.GetProperty("text").GetString()) ?? throw new InvalidDataException("Not NCWE clipboard text.");
                        var created = s.Paste(content, c.TryGetProperty("at", out var pasteAt) ? new Vector3(pasteAt[0].GetSingle(), pasteAt[1].GetSingle(), pasteAt[2].GetSingle()) : null);
                        return new { ok = created is not null, created, error = s.Error };
                    }
                    case "hide": s.HideSelected(); break;
                    case "isolate": s.ToggleIsolation(); break;
                    case "reveal": s.RevealAll(); break;
                    case "lock": s.ToggleLockSelected(); break;
                    case "unlock": s.UnlockAll(); break;
                    case "align": s.AlignSelection(c.GetProperty("axis").GetInt32(), Enum.Parse<StudioSession.AlignEdge>(c.GetProperty("edge").GetString()!, true)); break;
                    case "distribute": s.DistributeSelection(c.GetProperty("axis").GetInt32()); break;
                    case "series":
                    {
                        var o = c.GetProperty("offset");
                        var created = s.DuplicateSeries(c.GetProperty("count").GetInt32(), new(o[0].GetSingle(), o[1].GetSingle(), o[2].GetSingle()), c.TryGetProperty("turn", out var turn) ? turn.GetSingle() : 0);
                        return new { ok = created is not null, created, status = s.Status, error = s.Error };
                    }
                    case "pivot": s.Pivot = Enum.Parse<PivotMode>(c.GetProperty("mode").GetString()!, true); break;
                    case "pivot-preview":
                    {
                        // Turns the selection by yaw degrees about the gizmo pivot, as a gizmo drag would (committed).
                        var start = s.GizmoPivot(c.TryGetProperty("local", out var local) && local.GetBoolean()) ?? throw new InvalidOperationException("Nothing selected.");
                        var now = start; now.Translation = Vector3.Zero;
                        now *= Matrix4x4.CreateRotationY(c.GetProperty("yaw").GetSingle() * MathF.PI / 180);   // view space: Y is up
                        now.Translation = start.Translation;
                        s.PreviewPivot(start, now); s.CommitPreview();
                        break;
                    }
                    case "save-prefab": await _window.SavePrefab(c.GetProperty("name").GetString()!); break;
                    case "prefabs": return new { ok = true, folder = PrefabLibrary.Directory, prefabs = PrefabLibrary.List() };
                    case "snap-point":
                    {
                        // What V + drag would snap to under a viewport pixel.
                        var hit = s.SnapPoint(new(c.GetProperty("x").GetSingle(), c.GetProperty("y").GetSingle()), v.ViewportPixels, c.TryGetProperty("selected", out var snapSelected) && snapSelected.GetBoolean(), 30);
                        return new { ok = true, found = hit is not null, vertex = hit?.Vertex, point = hit is { } h ? new[] { Coordinates.ViewToGame(h.Point).X, Coordinates.ViewToGame(h.Point).Y, Coordinates.ViewToGame(h.Point).Z } : null };
                    }
                    case "snap-move":
                    {
                        s.BeginObjectSnap();
                        var snapMove = c.GetProperty("move");
                        var snapped = s.SnapMove(new(snapMove[0].GetSingle(), snapMove[1].GetSingle(), snapMove[2].GetSingle()), c.TryGetProperty("threshold", out var th) ? th.GetSingle() : .3f);
                        s.EndObjectSnap();
                        return new { ok = true, snapped = new[] { snapped.X, snapped.Y, snapped.Z } };
                    }
                    default: throw new ArgumentException("Unknown edit action " + action);
                }
                return new { ok = s.Error.Length == 0, status = s.Status, error = s.Error, selection = s.SelectedIds, hidden = s.HiddenIds.Count, locked = s.LockedIds.Count, isolated = s.Isolated?.Count };
            }
            case "recent":
                if (c.TryGetProperty("open", out var openRecent)) _window.OpenRecent(openRecent.GetString()!);
                return new { ok = true, recent = s.Settings.RecentProjects, path = s.ProjectPath, menu = _window.RecentMenuEntries() };
            // The decals of the view: how many there are, are drawn, and where their materials stand.
            case "decals":
                if (c.TryGetProperty("show", out var showDecals)) v.Renderer.ShowDecals = showDecals.GetBoolean();
                if (c.TryGetProperty("range", out var decalRange)) v.Renderer.DecalRange = decalRange.GetSingle();
                if (c.TryGetProperty("debug", out var decalDebug)) v.Renderer.DecalDebug = decalDebug.GetInt32();
                return new { ok = true, decals = v.Renderer.DecalDiagnostic(), viewport = new[] { v.ViewportPixels.X, v.ViewportPixels.Y },
                    materials = c.TryGetProperty("materials", out var listMaterials) ? v.Renderer.DecalMaterialStates(listMaterials.ValueKind != JsonValueKind.String || listMaterials.GetString() != "all") : null };
            // World > Quest variants as shown (refreshed with the camera, about once a second).
            case "variants": return new { ok = true, all = s.Variants.All, shown = s.Variants.Names.Order(StringComparer.OrdinalIgnoreCase).ToArray(), menu = _window.VariantMenuEntries() };
            case "quit": _window.RequestClose(); break;
            default: throw new ArgumentException("Unknown command " + op);
        }
        return new { ok = true };
    }

    private static object RoadState(StudioSession s, bool ok = true) => new
    {
        ok = ok && s.Error.Length == 0, error = s.Error, status = s.Status, selected = s.SelectedRoadId, point = s.SelectedRoadPoint, drawing = s.RoadDrawing,
        first = s.FirstDrawnPoint is { } f ? new[] { f.X, f.Y, f.Z } : null, selectedObject = s.SelectedId, canRebuild = s.CanRebuildSelectedAsRoad,
        roads = s.Roads.Values.Select(r => new { r.Id, r.Name, template = r.Template.Path, r.Banking, replaces = r.Replaces.Length, segments = s.RoadSegmentCount(r.Id), ground = r.Ground, r.Overpass,
            points = r.Points.Select(p => new[] { MathF.Round(p.X, 2), MathF.Round(p.Y, 2), MathF.Round(p.Z, 2) }).ToArray() }).ToArray(),
        junctions = s.Junctions.Junctions.Select(j => new { key = j.Key, center = new[] { MathF.Round(j.Center.X, 2), MathF.Round(j.Center.Y, 2), MathF.Round(j.Center.Z, 2) },
            radius = j.Radius, arms = j.Arms.Select(a => new { road = a.RoadId, a.Forward, cut = MathF.Round(a.Cut, 2) }).ToArray(),
            style = new[] { j.Style.Asphalt.Setup, j.Style.Sidewalk.Setup, j.Style.Curb.Setup } }).ToArray(),
        junctionTextures = s.JunctionTextureCount,
        gaps = s.Junctions.Gaps.ToDictionary(g => g.Key, g => g.Value.Select(x => new[] { MathF.Round(x.From, 2), MathF.Round(x.To, 2) }).ToArray()),
    };

    private static object GcInfo(GCKind kind)
    {
        var i = GC.GetGCMemoryInfo(kind);
        return new { i.Index, i.Generation, i.Compacted, i.Concurrent, pauseMs = i.PauseDurations.ToArray().Select(p => Math.Round(p.TotalMilliseconds, 1)).ToArray(),
            loadPct = i.TotalAvailableMemoryBytes == 0 ? 0 : Math.Round(100.0 * i.MemoryLoadBytes / i.TotalAvailableMemoryBytes, 1),
            highLoadPct = i.TotalAvailableMemoryBytes == 0 ? 0 : Math.Round(100.0 * i.HighMemoryLoadThresholdBytes / i.TotalAvailableMemoryBytes, 1),
            heapMb = i.HeapSizeBytes >> 20, promotedMb = i.PromotedBytes >> 20, pinned = i.PinnedObjectsCount, fragmentedMb = i.FragmentedBytes >> 20 };
    }

    private static ObjectInfo Describe(ScenePlacement o) => new(o.Id, o.Name, o.CanEdit, o.Deleted, o.SourceMismatch,
        o.Original?.Source.InstanceIndex ?? -1, o.Original?.Source.ExpectedInstances ?? 0, o.Original?.Source.PlacementIndex ?? -1,
        o.Original?.Source.Sector.Hash ?? 0, o.Mesh?.Path, [o.Transform.Position.X, o.Transform.Position.Y, o.Transform.Position.Z]);
    private sealed record ObjectInfo(string id, string name, bool canEdit, bool deleted, bool mismatch, int instance, int instances, int placement, ulong sector, string? mesh, float[] position);

    // PW_RENDERFULLCONTENT includes DirectComposition content (XAML and the swap chain).
    // A frame read back from a renderer (BGRA rows), as a PNG.
    internal static void WritePng(string path, byte[] pixels, int width, int height)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try { for (int y = 0; y < height; y++) System.Runtime.InteropServices.Marshal.Copy(pixels, y * width * 4, data.Scan0 + y * data.Stride, width * 4); }
        finally { bitmap.UnlockBits(data); }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        bitmap.Save(path, ImageFormat.Png);
    }

    private static string Capture(Microsoft.UI.Xaml.Window window, string path)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        GetWindowRect(hwnd, out var rect);
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            var hdc = g.GetHdc();
            try { PrintWindow(hwnd, hdc, 2); } finally { g.ReleaseHdc(hdc); }
            // Open menus and flyouts are windows of their own: drawn where they stand over this one.
            foreach (nint popup in Popups(hwnd)) DrawPopup(g, popup, rect.Left, rect.Top);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        bitmap.Save(path, ImageFormat.Png);
        return Path.GetFullPath(path);
    }

    // The top-level windows of this process (class, visible, place): where a popup went.
    private static object[] ProcessWindows()
    {
        var found = new List<object>();
        var name = new char[96];
        uint mine = (uint)Environment.ProcessId;
        for (nint other = FindWindowExW(0, 0, null, null); other != 0 && found.Count < 64; other = FindWindowExW(0, other, null, null))
        {
            uint thread = GetWindowThreadProcessId(other, out uint process);
            if (process != mine) continue;
            int length = GetClassNameW(other, name, name.Length);
            GetWindowRect(other, out var at);
            found.Add(new { @class = new string(name, 0, Math.Max(0, length)), visible = IsWindowVisible(other), thread, rect = new[] { at.Left, at.Top, at.Right - at.Left, at.Bottom - at.Top } });
        }
        return [.. found];
    }

    // The open menus and flyouts of a window's thread, the lowest first.
    private static List<nint> Popups(nint hwnd)
    {
        var popups = new List<nint>();
        uint thread = GetWindowThreadProcessId(hwnd, out _);
        var name = new char[96];
        for (nint other = FindWindowExW(0, 0, null, null); other != 0 && popups.Count < 16; other = FindWindowExW(0, other, null, null))
        {
            if (other == hwnd || !IsWindowVisible(other) || GetWindowThreadProcessId(other, out _) != thread) continue;
            int length = GetClassNameW(other, name, name.Length);
            if (new string(name, 0, Math.Max(0, length)).Contains("PopupWindowSiteBridge", StringComparison.Ordinal)) popups.Add(other);
        }
        popups.Reverse();
        return popups;
    }

    private static void DrawPopup(Graphics g, nint popup, int left, int top)
    {
        GetWindowRect(popup, out var at);
        int w = at.Right - at.Left, h = at.Bottom - at.Top;
        if (w <= 0 || h <= 0 || w > 8192 || h > 8192) return;
        using var layer = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        using (var lg = Graphics.FromImage(layer))
        {
            var dc = lg.GetHdc();
            try { PrintWindow(popup, dc, 2); } finally { lg.ReleaseHdc(dc); }
        }
        g.DrawImage(layer, at.Left - left, at.Top - top, w, h);
    }

    // The open menus and flyouts alone, as they stand to one another (a background window keeps
    // off the screen, its menus do not: they are not over it).
    private static string CapturePopups(Microsoft.UI.Xaml.Window window, string path)
    {
        var popups = Popups(WinRT.Interop.WindowNative.GetWindowHandle(window));
        if (popups.Count == 0) throw new InvalidOperationException("No menu or flyout is open.");
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        foreach (nint popup in popups)
        {
            GetWindowRect(popup, out var at);
            left = Math.Min(left, at.Left); top = Math.Min(top, at.Top); right = Math.Max(right, at.Right); bottom = Math.Max(bottom, at.Bottom);
        }
        using var bitmap = new Bitmap(Math.Clamp(right - left, 1, 8192), Math.Clamp(bottom - top, 1, 8192), PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.FromArgb(255, 16, 17, 19));
            foreach (nint popup in popups) DrawPopup(g, popup, left, top);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        bitmap.Save(path, ImageFormat.Png);
        return Path.GetFullPath(path);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetWindowRect(nint window, out Rect rect);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool PrintWindow(nint window, nint hdc, uint flags);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsWindowVisible(nint window);
    [LibraryImport("user32.dll")] private static partial uint GetWindowThreadProcessId(nint window, out uint process);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial nint FindWindowExW(nint parent, nint after, string? className, string? title);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int GetClassNameW(nint window, [Out] char[] name, int count);

    public void Dispose() { _stop.Cancel(); _activity.SetState(BridgeActivity.BridgeState.Stopped, _name); }
}
