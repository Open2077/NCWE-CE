// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NCWE.Core;
using NCWE.Render;
using NCWE.Studio.Session;

namespace NCWE.Studio.Viewport;

public enum EditorTool { Select, Move, Rotate, Scale, Place, Road, Element, Traffic, Elevator, Terrain, Note }

// Owns the D3D11 device, the composition swap chain shown by the SwapChainPanel,
// game-like navigation and the transform gizmo. Runs on the XAML UI thread.
internal sealed partial class ViewportController : IDisposable
{
    private const float EyeHeight = 1.75f, Gravity = 18f, WalkSpeed = 4.2f, JumpSpeed = 5.5f;
    private readonly SwapChainPanel _panel;
    private readonly nint _window;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private ImGuiRenderer? _overlay;
    private double _last, _previousFrame;
    private float _scaleX = 1, _scaleY = 1;
    private int _pixelWidth, _pixelHeight;
    private bool _looking, _leftDown, _leftDragging, _middleDown, _gizmoDragging, _gizmoCaptured, _roadDragging, _elevatorDragging;
    // Shift + left drag: rectangle selection.
    private bool _marquee, _marqueeDragging;
    private NativeInput.Point _lookAnchor;
    private Vector2 _pointer, _pressPosition, _lastPointer;
    private float _verticalSpeed;
    private int _fieldOfViewWheel;
    private double _nextHover;
    private double _settleUntil;
    private bool _settleWalk;
    private readonly Queue<double> _frameTimes = new();

    public D3DRenderer Renderer { get; }
    public StudioSession Session { get; }
    // The AI agent in the view: its lock, its activity and its tools' visuals.
    public AiPresence Ai { get; } = new();
    // The other participants of a live collaboration session (their cameras and selections).
    public CollabPresence Collab { get; } = new();
    public EditorTool Tool { get; set; } = EditorTool.Select;
    public NavigationMode Navigation { get; set; }
    public float FlySpeed { get; set; }
    public bool Snap { get; set; }
    // A move lines the selection's box up with nearby objects' faces (hold Alt to move freely).
    public bool SnapObjects { get; set; } = true;
    // Gizmo drag: the pivot at its start and now (view space).
    private Matrix4x4 _pivotStart;
    private Matrix4x4? _pivotNow;
    // Hold V and drag: a vertex of the selection goes onto a vertex (or the surface) under the cursor.
    private bool _vertexDrag;
    private Vector3? _vertexSource;
    private (Vector3 Point, bool Vertex)? _vertexTarget;
    private const int VkV = 0x56;
    public bool VertexSnapHeld => NativeInput.IsVirtualKeyDown(VkV) && KeysActive;
    public bool LocalSpace { get; set; } = true;
    public float TranslateSnap { get; set; } = .5f;
    public float RotateSnap { get; set; } = 15;
    public bool KeyboardEnabled { get; set; } = true;
    public Func<Vector3, bool>? PlaceRequested { get; set; }
    public double FramesPerSecond { get; private set; }
    public double FrameMilliseconds { get; private set; }
    private readonly Queue<(double Time, double Ms)> _maxFrames = new();
    public double MaxFrameMilliseconds => _maxFrames.Count == 0 ? 0 : _maxFrames.Max(f => f.Ms);
    public double NavigateMilliseconds { get; private set; }
    public double TickMilliseconds { get; private set; }
    public double UploadMilliseconds { get; private set; }
    public double SceneMilliseconds { get; private set; }
    public double OverlayMilliseconds { get; private set; }
    public double PresentMilliseconds { get; private set; }
    public bool PointerOverViewport { get; private set; }
    // Automation: the cursor at a viewport pixel (null: away from the viewport), with the
    // modifier keys it holds (until the real mouse moves).
    internal void SimulatePointer(Vector2? pixel, bool shift = false, bool control = false)
    {
        if (pixel is { } p) { _pointer = p; PointerOverViewport = true; } else PointerOverViewport = false;
        _simulatedKeys = (shift, control);
        _nextHover = 0;
    }
    public Vector2 PointerPixels => _pointer;
    public Vector2 ViewportPixels => new(Math.Max(1, _pixelWidth), Math.Max(1, _pixelHeight));
    public bool IsLooking => _looking || _leftDragging;
    public event Action? Rendered;
    public event Action? FieldOfViewChanged;
    public RouteProfiler? Route { get; set; }

    public ViewportController(SwapChainPanel panel, nint window, StudioSettings settings)
    {
        _panel = panel; _window = window;
        UpdateSize();
        Renderer = D3DRenderer.ForComposition(Math.Max(1, _pixelWidth), Math.Max(1, _pixelHeight), 4);
        App.TraceLine($"Graphics: {Renderer.AdapterName}, {Renderer.DedicatedVideoMemory >> 20} MB dedicated");
        Renderer.Atmosphere = settings.Atmosphere;
        Renderer.GameFaces = settings.GameFaces;
        Renderer.ShowDecals = settings.ShowDecals;
        Renderer.Shading = settings.Shading;
        Renderer.MaterialRange = Math.Clamp(settings.MaterialRange, 0, 1000);
        Renderer.DrawDistance = settings.DrawDistance >= MainWindow.MinDrawDistance ? Math.Min(settings.DrawDistance, MainWindow.MaxDrawDistance) : 0;
        // The menu offers 512/1024/2048; a lower limit set in the settings file (weaker graphics
        // cards) is kept, as a power of two from 128, instead of going back to 1024.
        Renderer.TextureLimit = settings.TextureLimit is >= 128 and <= 2048 ? (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)settings.TextureLimit + 1) / 2 : 1024;
        AttachSwapChain();
        _overlay = new ImGuiRenderer(Renderer.Device, Renderer.Context);
        if (Gizmo.Version != ImGui.GetVersion()) throw new InvalidOperationException($"ImGuizmo ABI mismatch: {Gizmo.Version} / {ImGui.GetVersion()}");
        var io = ImGui.GetIO(); io.ConfigFlags |= ImGuiConfigFlags.NoMouseCursorChange;
        Session = new(Renderer, settings);
        Navigation = settings.Navigation; FlySpeed = settings.FlySpeed; Snap = settings.Snap; LocalSpace = settings.LocalSpace; SnapObjects = settings.SnapObjects;
        TranslateSnap = settings.TranslateSnap; RotateSnap = settings.RotateSnap;
        _panel.SizeChanged += (_, _) => UpdateSize();
        _panel.CompositionScaleChanged += (_, _) => UpdateSize();
        _panel.PointerPressed += OnPointerPressed;
        _panel.PointerMoved += OnPointerMoved;
        _panel.PointerReleased += OnPointerReleased;
        _panel.PointerCaptureLost += (_, _) => EndInteractions();
        _panel.PointerWheelChanged += OnPointerWheel;
        _panel.PointerEntered += (_, _) => PointerOverViewport = true;
        _panel.PointerExited += (_, _) => PointerOverViewport = false;
        CompositionTarget.Rendering += OnRendering;
    }

    private void AttachSwapChain()
    {
        SwapChainPanels.Set(_panel, Renderer.SwapChain.NativePointer);
        ApplyScaleTransform();
    }

    private void UpdateSize()
    {
        _scaleX = Math.Max(.5f, _panel.CompositionScaleX); _scaleY = Math.Max(.5f, _panel.CompositionScaleY);
        _pixelWidth = (int)Math.Ceiling(_panel.ActualWidth * _scaleX); _pixelHeight = (int)Math.Ceiling(_panel.ActualHeight * _scaleY);
        if (Renderer is not null) ApplyScaleTransform();
    }

    private void ApplyScaleTransform()
    {
        using var swap2 = Renderer.SwapChain.QueryInterfaceOrNull<Vortice.DXGI.IDXGISwapChain2>();
        if (swap2 is not null) swap2.MatrixTransform = new Matrix3x2(1 / _scaleX, 0, 0, 1 / _scaleY, 0, 0);
    }

    // --- Frame ---------------------------------------------------------------------

    private void OnRendering(object? sender, object e)
    {
        if (_overlay is null || DeviceLost is not null || _pixelWidth < 1 || _pixelHeight < 1) return;
        double now = _clock.Elapsed.TotalSeconds;
        float dt = (float)Math.Clamp(now - _last, 0, .1); _last = now;
        var started = Stopwatch.GetTimestamp();
        var pauseBefore = GC.GetTotalPauseDuration();
        try
        {
            long stage = Stopwatch.GetTimestamp();
            double Stage() { long end = Stopwatch.GetTimestamp(); double ms = Stopwatch.GetElapsedTime(stage, end).TotalMilliseconds; stage = end; return ms; }
            Renderer.Resize(_pixelWidth, _pixelHeight);
            if (Route is { Done: false } route) route.Step(Session.Camera, dt); else Navigate(dt, now);
            if (now >= _nextHover)
            {
                long hoverStart = Stopwatch.GetTimestamp();
                var hoverAt = PointerOverViewport && !IsLooking && !_gizmoDragging && !GizmoHot && !_marqueeDragging && !Ai.Locked && Modal is null ? _pointer : (Vector2?)null;
                if (Session.MeshEditing) { _nextHover = now + .04; Session.UpdateMeshEditHover(_meshCutting || _meshGizmoDragging ? null : hoverAt, ViewportPixels); }
                else if (Tool == EditorTool.Traffic) { _nextHover = now + .05; Session.UpdateTrafficHover(hoverAt, ViewportPixels); }
                else if (Tool == EditorTool.Terrain) { _nextHover = now; Session.UpdateTerrainHover(hoverAt ?? (_terrainStroking ? _pointer : null), ViewportPixels); }
                else if (Tool == EditorTool.Note) { _nextHover = now + .03; Session.UpdateNoteHover(PointerOverViewport && !IsLooking ? _pointer : null, ViewportPixels); }
                else if (Tool == EditorTool.Road)
                {
                    // Roads: the drawing ghost and the handles under the cursor, often; the picking hover less so.
                    _nextHover = now + .03;
                    if (!_roadPress) Session.UpdateRoadHover(hoverAt, ViewportPixels, KeysDown(NativeInput.VkShift), KeysDown(NativeInput.VkControl));
                    if (!Session.RoadDrawing && now >= _nextRoadPick) { _nextRoadPick = now + .1; Session.UpdateRoadPickHover(hoverAt, ViewportPixels); }
                }
                else { _nextHover = now + .1; Session.UpdateHover(hoverAt, ViewportPixels); }
                if (Tool == EditorTool.Elevator) Session.UpdateElevatorGhost(hoverAt, ViewportPixels);
                FrameProfile.Record("hover", Stopwatch.GetElapsedTime(hoverStart).TotalMilliseconds);
            }
            // Terrain: the brush works every frame the button is held.
            if (_terrainStroking) Session.TerrainStrokeTick(dt);
            NavigateMilliseconds = Stage(); FrameProfile.Record("navigate", NavigateMilliseconds);
            Session.Tick(now); TickMilliseconds = Stage(); FrameProfile.Record("tick", TickMilliseconds);
            Renderer.UploadWorldStep(12L * 1024 * 1024, 4); UploadMilliseconds = Stage(); FrameProfile.Record("upload", UploadMilliseconds);
            Renderer.ResizeScene(_pixelWidth, _pixelHeight);
            Session.UpdatePreviewLights();
            Session.UpdateEffects(dt, now); FrameProfile.Record("effects", Stage());
            Renderer.BeginGpuFrame();
            Renderer.RenderScene(Session.Camera, 1, []);
            Renderer.PresentSceneAsBackground(); SceneMilliseconds = Stage(); FrameProfile.Record("scene", SceneMilliseconds);
            Renderer.MarkGpuSceneDone();
            FrameProfile.Record("cull", Renderer.WorldCullMilliseconds); FrameProfile.Record("cullpar", Renderer.WorldParallelCullMilliseconds); FrameProfile.Record("submit", Renderer.WorldSubmitMilliseconds);
            DrawGizmo(dt); OverlayMilliseconds = Stage(); FrameProfile.Record("overlay", OverlayMilliseconds);
            Renderer.EndGpuFrame();
            if (Renderer.GpuTimingFresh) { FrameProfile.Record("gpu.scene", Renderer.GpuSceneMilliseconds); FrameProfile.Record("gpu.frame", Renderer.GpuFrameMilliseconds); }
            Renderer.Present(); PresentMilliseconds = Stage(); FrameProfile.Record("present", PresentMilliseconds);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (Renderer.RemovedReason() is { } reason) { LoseDevice(reason, ex); return; }
            // Keep the editor alive; the project and autosave stay usable.
            Session.Error = Loc.F("Viewport: {0}", ex.Message);
            App.WriteCrash(ex);
        }
        FrameMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        FrameProfile.Record("frame", FrameMilliseconds); FrameProfile.Record("gcPause", (GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds);
        GcPauses.Poll();
        // Allocation rates (MB per frame): the whole process (streaming workers included) and this thread.
        long allocated = GC.GetTotalAllocatedBytes(false), mine = GC.GetAllocatedBytesForCurrentThread();
        if (_allocatedBefore > 0) { FrameProfile.Record("alloc.all", (allocated - _allocatedBefore) / 1048576.0); FrameProfile.Record("alloc.ui", (mine - _uiAllocatedBefore) / 1048576.0); }
        _allocatedBefore = allocated; _uiAllocatedBefore = mine;
        FrameProfile.Record("interval", (now - _previousFrame) * 1000); _previousFrame = now;
        // Exceptions escaping a CompositionTarget.Rendering handler fail the process fast (stowed
        // exception, no UnhandledException): the shell update must never let one through.
        long shell = Stopwatch.GetTimestamp();
        try { Rendered?.Invoke(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Session.Error = Loc.F("Interface: {0}", ex.Message); App.WriteCrash(ex); }
        FrameProfile.Record("shell", Stopwatch.GetElapsedTime(shell).TotalMilliseconds);
        if (Route is { Done: false } active) active.Record(FrameMilliseconds, Renderer, Session);
        RecordFrameStats(now);
    }

    private long _allocatedBefore, _uiAllocatedBefore;

    private void RecordFrameStats(double now)
    {
        _maxFrames.Enqueue((now, FrameMilliseconds)); while (_maxFrames.Count > 0 && now - _maxFrames.Peek().Time > 2) _maxFrames.Dequeue();
        _frameTimes.Enqueue(now); while (_frameTimes.Count > 0 && now - _frameTimes.Peek() > 1) _frameTimes.Dequeue();
        FramesPerSecond = _frameTimes.Count;
    }

    private void DrawGizmo(float dt)
    {
        _overlay!.NewFrame(_pixelWidth, _pixelHeight, dt);
        Gizmo.Begin(_overlay.ImGuiContext);
        ImGui.SetNextWindowPos(Vector2.Zero); ImGui.SetNextWindowSize(new(_pixelWidth, _pixelHeight));
        ImGui.Begin("##viewport-overlay", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing);
        var draw = ImGui.GetWindowDrawList();
        Session.UpdateIcons(Session.Camera.GetFrame((float)_pixelWidth / _pixelHeight, Renderer.CameraRelative), ViewportPixels);
        foreach (var icon in Session.Icons) MarkerIcons.Draw(draw, icon);
        var overlayFrame = Session.Camera.GetFrame((float)_pixelWidth / _pixelHeight, Renderer.CameraRelative);
        Collab.Draw(draw, overlayFrame, ViewportPixels, dt);
        Ai.SetNoteMarks(Session.NoteMarks());
        Ai.DrawWorld(draw, overlayFrame, ViewportPixels);
        if (_marqueeDragging)
        {
            var min = Vector2.Min(_pressPosition, _pointer); var max = Vector2.Max(_pressPosition, _pointer);
            draw.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(new(.35f, .6f, 1f, .12f)));
            draw.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(new(.45f, .7f, 1f, .9f)), 0, ImDrawFlags.None, 1.2f);
        }
        // A transform started with a key (Blender shortcuts) takes the gizmos' place while it lasts.
        UpdateModal(draw);
        var selected = Session.Selected;
        // Elements tool: a translate gizmo on the selection while nothing is armed for placing.
        bool active = (Tool is EditorTool.Move or EditorTool.Rotate or EditorTool.Scale || (Tool == EditorTool.Element && Session.ArmedElement is null))
            && selected is { CanEdit: true, Deleted: false } && !Session.IsLocked(selected.Id) && !IsLooking && !Session.MeshEditing && Modal is null;
        DrawMeshEditGizmo(draw);
        // Roads tool: the selected control point gets a world-space move gizmo.
        RoadGizmoActive = Tool == EditorTool.Road && !IsLooking && !Session.RoadDrawing && !Session.RoadDragging && Session.SelectedRoad is { } road
            && Session.SelectedRoadPoint >= 0 && Session.SelectedRoadPoint < road.Points.Length;
        if (RoadGizmoActive)
        {
            var frame = Session.Camera.GetFrame((float)_pixelWidth / _pixelHeight, Renderer.CameraRelative);
            var point = Session.SelectedRoad!.Points[Session.SelectedRoadPoint];
            var matrix = frame.Relative(Matrix4x4.CreateTranslation(Coordinates.GameToView(point)));
            Gizmo.Manipulate(frame.View, frame.Projection, ref matrix, 0, 0, _pixelWidth, _pixelHeight, 0, true, Snap ? TranslateSnap : null, .14f);
            bool usingGizmo = Gizmo.IsUsing() != 0;
            if (usingGizmo) Session.PreviewRoadPoint(Coordinates.ViewToGame(frame.Absolute(matrix).Translation));
            if (_roadDragging && !usingGizmo) Session.CommitRoadPreview();
            _roadDragging = usingGizmo;
        }
        else if (_roadDragging) { Session.CommitRoadPreview(); _roadDragging = false; }
        // Traffic tool: the selected lane point gets the move gizmo (one axis at a time, height
        // included); dragging a point itself moves it flat (Session.BeginTrafficDrag).
        TrafficGizmoActive = Tool == EditorTool.Traffic && !IsLooking && !Session.TrafficDrawing && Session.TrafficLinking is null && !Session.TrafficDragging
            && Session.SelectedLaneState is { } lane && Session.SelectedLanePoint >= 0 && Session.SelectedLanePoint < lane.Outline.Length;
        if (TrafficGizmoActive)
        {
            var frame = Session.Camera.GetFrame((float)_pixelWidth / _pixelHeight, Renderer.CameraRelative);
            var point = Session.SelectedLaneState!.Outline[Session.SelectedLanePoint];
            var matrix = frame.Relative(Matrix4x4.CreateTranslation(Coordinates.GameToView(point)));
            Gizmo.Manipulate(frame.View, frame.Projection, ref matrix, 0, 0, _pixelWidth, _pixelHeight, 0, true, Snap ? TranslateSnap : null, .14f);
            bool usingGizmo = Gizmo.IsUsing() != 0;
            if (usingGizmo) Session.PreviewLanePoint(Coordinates.ViewToGame(frame.Absolute(matrix).Translation));
            if (_trafficDragging && !usingGizmo) Session.CommitLanePreview();
            _trafficDragging = usingGizmo;
        }
        else if (_trafficDragging) { Session.CommitLanePreview(); _trafficDragging = false; }
        // Elevators: the selected floor moves up and down (its gizmo's vertical arrow), without a
        // floor the whole elevator moves (Elevators and Move tools) or turns (Rotate tool).
        bool floorGizmo = Session.SelectedElevatorFloor >= 0 && Tool != EditorTool.Rotate;
        ElevatorGizmoActive = Tool is EditorTool.Elevator or EditorTool.Move or EditorTool.Rotate && !IsLooking && Session.ArmedElevator is null
            && Session.ElevatorGizmo(floorGizmo) is not null;
        if (ElevatorGizmoActive)
        {
            var frame = Session.Camera.GetFrame((float)_pixelWidth / _pixelHeight, Renderer.CameraRelative);
            var matrix = frame.Relative(Session.ElevatorGizmo(floorGizmo)!.Value);
            int operation = Tool == EditorTool.Rotate ? 1 : 0;
            float? snap = !Snap ? null : Tool == EditorTool.Rotate ? RotateSnap : TranslateSnap;
            Gizmo.Manipulate(frame.View, frame.Projection, ref matrix, 0, 0, _pixelWidth, _pixelHeight, operation, Tool != EditorTool.Rotate || !LocalSpace, snap, .14f);
            bool usingGizmo = Gizmo.IsUsing() != 0;
            if (usingGizmo) Session.PreviewElevatorGizmo(frame.Absolute(matrix), floorGizmo);
            if (_elevatorDragging && !usingGizmo) Session.CommitElevatorPreview();
            _elevatorDragging = usingGizmo;
        }
        else if (_elevatorDragging) { Session.CommitElevatorPreview(); _elevatorDragging = false; }
        DrawElevatorLabels(draw);
        if (active && !_vertexDrag)
        {
            var frame = Session.Camera.GetFrame((float)_pixelWidth / _pixelHeight, Renderer.CameraRelative);
            // The pivot: the active object, the selection's centre or each origin (Session.Pivot).
            var pivot = _gizmoDragging && _pivotNow is { } now ? now : Session.GizmoPivot(LocalSpace) ?? selected!.Transform.ViewMatrix;
            var matrix = frame.Relative(pivot);
            int operation = Tool switch { EditorTool.Rotate => 1, EditorTool.Scale => 2, _ => 0 };
            float? snap = !Snap ? null : Tool switch { EditorTool.Rotate => RotateSnap, EditorTool.Scale => .1f, _ => TranslateSnap };
            Gizmo.Manipulate(frame.View, frame.Projection, ref matrix, 0, 0, _pixelWidth, _pixelHeight, operation, !LocalSpace && Tool != EditorTool.Scale, snap, .14f);
            bool usingGizmo = Gizmo.IsUsing() != 0;
            if (usingGizmo)
            {
                bool snapping = Tool == EditorTool.Move && SnapObjects;
                if (!_gizmoDragging) { _pivotStart = pivot; if (snapping) Session.BeginObjectSnap(); }
                var next = frame.Absolute(matrix);
                if (snapping && !NativeInput.IsVirtualKeyDown(NativeInput.VkMenu))
                {
                    var move = next.Translation - _pivotStart.Translation;
                    float threshold = Math.Clamp(Vector3.Distance(Session.Camera.Eye, _pivotStart.Translation) * .012f, .03f, 1f);
                    next.Translation = _pivotStart.Translation + Session.SnapMove(move, threshold);
                }
                _pivotNow = next;
                Session.PreviewPivot(_pivotStart, next);
            }
            if (_gizmoDragging && !usingGizmo) EndGizmoDrag();
            _gizmoDragging = usingGizmo;
        }
        else if (_gizmoDragging) { EndGizmoDrag(); _gizmoDragging = false; }
        DrawVertexSnap(draw);
        DrawTerrainBrush(draw);
        DrawRoadOverlay(draw);
        DrawHoverLabel(draw);
        Ai.DrawScreen(draw, ViewportPixels, _pointer, PointerOverViewport);
        ImGui.End();
        ImGui.Render();
        GizmoActive = active; OverlayVertices = ImGui.GetDrawData().TotalVtxCount;
        _overlay.Render(ImGui.GetDrawData(), null);
    }

    private void EndGizmoDrag()
    {
        Session.CommitPreview(); Session.EndObjectSnap(); _pivotNow = null;
    }

    // Mesh edit mode: the gizmo on the selected vertices or faces (the transform tools choose move,
    // turn or scale), and the size of an opening being drawn.
    private bool _meshGizmoDragging, _meshCutting, _meshMarquee;
    private Matrix4x4 _meshPivotStart;
    public bool MeshGizmoActive { get; private set; }

    private void DrawMeshEditGizmo(ImDrawListPtr draw)
    {
        MeshGizmoActive = Session.MeshEditing && Session.MeshTool == MeshEditTool.Select && !IsLooking && !_meshMarquee && Session.MeshSelectionCenter is not null && Modal is null;
        if (MeshGizmoActive)
        {
            var frame = Session.Camera.GetFrame((float)_pixelWidth / _pixelHeight, Renderer.CameraRelative);
            var pivot = _meshGizmoDragging && _pivotNow is { } dragged ? dragged : Matrix4x4.CreateTranslation(Session.MeshSelectionCenter!.Value);
            var matrix = frame.Relative(pivot);
            int operation = Tool switch { EditorTool.Rotate => 1, EditorTool.Scale => 2, _ => 0 };
            float? snap = !Snap ? null : Tool switch { EditorTool.Rotate => RotateSnap, EditorTool.Scale => .1f, _ => Math.Min(TranslateSnap, .05f) };
            Gizmo.Manipulate(frame.View, frame.Projection, ref matrix, 0, 0, _pixelWidth, _pixelHeight, operation, Tool != EditorTool.Scale, snap, .14f);
            bool usingGizmo = Gizmo.IsUsing() != 0;
            if (usingGizmo)
            {
                if (!_meshGizmoDragging) _meshPivotStart = pivot;
                var next = frame.Absolute(matrix);
                _pivotNow = next;
                Matrix4x4.Invert(_meshPivotStart, out var inverse);
                Session.MeshEditPreviewTransform(inverse * next);
            }
            if (_meshGizmoDragging && !usingGizmo) { Session.MeshEditCommitTransform(); _pivotNow = null; }
            _meshGizmoDragging = usingGizmo;
        }
        else if (_meshGizmoDragging) { Session.MeshEditCommitTransform(); _meshGizmoDragging = false; _pivotNow = null; }
        if (Session.MeshCutSize is { } size)
        {
            string text = $"{size.Width:0.00} × {size.Height:0.00} m";
            var at = _pointer + new Vector2(16, -26);
            draw.AddRectFilled(at - new Vector2(6, 3), at + ImGui.CalcTextSize(text) + new Vector2(6, 3), ImGui.ColorConvertFloat4ToU32(new(0, 0, 0, .7f)), 4);
            draw.AddText(at, ImGui.ColorConvertFloat4ToU32(new(1f, .55f, .55f, 1)), text);
        }
    }

    // V held: the selection's vertex nearest to the cursor (orange); while dragging, where it goes
    // (green: a vertex, blue: a surface point).
    private void DrawVertexSnap(ImDrawListPtr draw)
    {
        if (!_vertexDrag && !(VertexSnapHeld && PointerOverViewport && Session.Selected is { CanEdit: true } && !IsLooking)) return;
        var source = _vertexDrag ? _vertexSource : Session.SnapPoint(_pointer, ViewportPixels, selected: true, 40)?.Point;
        uint orange = ImGui.ColorConvertFloat4ToU32(new(1f, .6f, .2f, 1)), green = ImGui.ColorConvertFloat4ToU32(new(.45f, 1f, .5f, 1)), blue = ImGui.ColorConvertFloat4ToU32(new(.45f, .7f, 1f, 1));
        Vector2? from = source is { } s && Session.ScreenOf(s, ViewportPixels) is { } a ? a : null;
        if (from is { } f) draw.AddRect(f - new Vector2(5), f + new Vector2(5), orange, 0, ImDrawFlags.None, 2);
        if (_vertexDrag && _vertexTarget is { } target && Session.ScreenOf(target.Point, ViewportPixels) is { } b)
        {
            if (target.Vertex) draw.AddRect(b - new Vector2(6), b + new Vector2(6), green, 0, ImDrawFlags.None, 2.5f);
            else draw.AddCircle(b, 6, blue, 16, 2);
        }
        if (!_vertexDrag && from is null) draw.AddText(_pointer + new Vector2(14, 10), orange, Loc.T("V: point at a corner of the selection, then drag"));
    }

    // Collision and Occlusion layers: what the cursor is over, in a tag next to it (kept inside
    // the viewport).
    private void DrawHoverLabel(ImDrawListPtr draw)
    {
        if (Session.HoverLabel is not { } text || !PointerOverViewport || IsLooking || _gizmoDragging || _marqueeDragging) return;
        var size = ImGui.CalcTextSize(text); var pad = new Vector2(7, 5);
        var at = _pointer + new Vector2(16, 14);
        at.X = MathF.Min(at.X, _pixelWidth - size.X - pad.X * 2 - 4); at.Y = MathF.Min(at.Y, _pixelHeight - size.Y - pad.Y * 2 - 4);
        if (at.Y < _pointer.Y + 14 && at.X < _pointer.X + 16) at.Y = _pointer.Y - size.Y - pad.Y * 2 - 8;
        draw.AddRectFilled(at, at + size + pad * 2, ImGui.ColorConvertFloat4ToU32(new(.06f, .07f, .09f, .88f)), 4);
        draw.AddRect(at, at + size + pad * 2, ImGui.ColorConvertFloat4ToU32(new(1f, 1f, 1f, .18f)), 4);
        draw.AddText(at + pad, ImGui.ColorConvertFloat4ToU32(new(.92f, .94f, .97f, 1)), text);
    }

    // The graphics device is gone (the driver reset after a GPU hang, a driver update, the GPU
    // removed): nothing can be drawn with it again. The view stops, the panel lets go of the swap
    // chain (WinUI must not keep composing it), the project is autosaved and the window offers a
    // restart.
    public string? DeviceLost { get; private set; }
    public event Action? DeviceLostDetected;

    internal void LoseDevice(string reason, Exception? cause)
    {
        if (DeviceLost is not null) return;
        DeviceLost = reason;
        EndInteractions();
        SwapChainPanels.Detach(_panel);
        App.TraceLine($"Graphics device lost: {reason}");
        App.WriteCrash(new InvalidOperationException($"Graphics device lost ({reason}).", cause));
        try { Session.TryAutosave(); } catch (Exception ex) when (ex is not OutOfMemoryException) { App.WriteCrash(ex); }
        Session.Error = Loc.F("The graphics driver stopped responding ({0}). Your work was autosaved: restart NCWE.", reason);
        try { DeviceLostDetected?.Invoke(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { App.WriteCrash(ex); }
    }

    public bool GizmoActive { get; private set; }
    public bool RoadGizmoActive { get; private set; }
    public bool TrafficGizmoActive { get; private set; }
    public bool ElevatorGizmoActive { get; private set; }

    // The selected elevator's floors (name and height at their plate) and, with the Elevators tool,
    // the other elevators' names; clicking one selects it (Session.ElevatorFloorAt).
    private void DrawElevatorLabels(ImDrawListPtr draw)
    {
        Session.ElevatorLabelRects.Clear();
        var labels = Session.ElevatorLabels(ViewportPixels);
        if (labels.Count == 0) return;
        var pad = new Vector2(6, 3);
        uint text = ImGui.ColorConvertFloat4ToU32(new(.93f, .97f, .96f, 1)), muted = ImGui.ColorConvertFloat4ToU32(new(.62f, .78f, .74f, 1));
        uint back = ImGui.ColorConvertFloat4ToU32(new(.05f, .09f, .09f, .82f)), chosen = ImGui.ColorConvertFloat4ToU32(new(.42f, .3f, .04f, .92f));
        uint border = ImGui.ColorConvertFloat4ToU32(new(.42f, .86f, .76f, .7f)), chosenBorder = ImGui.ColorConvertFloat4ToU32(new(1f, .78f, .18f, 1));
        float lastY = float.NegativeInfinity;
        foreach (var label in labels.OrderBy(l => l.Screen.Y))
        {
            string detail = "  " + label.Detail;
            var size = ImGui.CalcTextSize(label.Text) + new Vector2(ImGui.CalcTextSize(detail).X, 0);
            var at = label.Title ? label.Screen - new Vector2(size.X / 2 + pad.X, size.Y + pad.Y * 2) : label.Screen - new Vector2(0, size.Y / 2 + pad.Y);
            // Floors too close on screen (seen from afar): the selected one wins, the others wait.
            if (!label.Title && !label.Selected && at.Y < lastY + 4) continue;
            if (at.X > _pixelWidth || at.Y > _pixelHeight || at.X + size.X < 0 || at.Y + size.Y < 0) continue;
            var max = at + size + pad * 2;
            draw.AddRectFilled(at, max, label.Selected ? chosen : back, 4);
            draw.AddRect(at, max, label.Selected ? chosenBorder : border, 4);
            draw.AddText(at + pad, text, label.Text);
            draw.AddText(at + pad + new Vector2(ImGui.CalcTextSize(label.Text).X, 0), muted, detail);
            if (!label.Title) lastY = max.Y;
            Session.ElevatorLabelRects.Add((label.ElevatorId, label.Floor, at, max));
        }
    }
    private bool _trafficDrag, _trafficDragMoved;
    private bool _terrainStroking;

    // Automation: the left button on the ground with the Terrain tool (down starts a stroke where
    // the simulated pointer is, up ends it), as the mouse does.
    internal bool SimulateTerrainButton(bool down, bool alternate = false)
    {
        if (!down) { if (_terrainStroking) { Session.EndTerrainStroke(); _terrainStroking = false; } return true; }
        if (_terrainStroking) return true;
        if (Tool != EditorTool.Terrain) return false;
        Session.UpdateTerrainHover(_pointer, ViewportPixels);
        return _terrainStroking = Session.BeginTerrainStroke(alternate);
    }

    // Terrain tool: the brush on the ground (its edge and hard core) and, near it, size and strength.
    private void DrawTerrainBrush(ImDrawListPtr draw)
    {
        if (Tool != EditorTool.Terrain || !PointerOverViewport && !_terrainStroking || IsLooking) return;
        var brush = Session.TerrainBrush;
        var color = brush.Kind switch
        {
            Core.Terrain.TerrainBrushKind.Raise or Core.Terrain.TerrainBrushKind.Mountain => new Vector4(.45f, 1f, .55f, 1),
            Core.Terrain.TerrainBrushKind.Lower => new Vector4(1f, .55f, .4f, 1),
            Core.Terrain.TerrainBrushKind.Hole => new Vector4(1f, .3f, .3f, 1),
            Core.Terrain.TerrainBrushKind.Paint => new Vector4(.95f, .8f, .35f, 1),
            Core.Terrain.TerrainBrushKind.Restore => new Vector4(.8f, .8f, .85f, 1),
            _ => new Vector4(.45f, .75f, 1f, 1)
        };
        uint edge = ImGui.ColorConvertFloat4ToU32(color with { W = _terrainStroking ? 1 : .9f });
        uint core = ImGui.ColorConvertFloat4ToU32(color with { W = .45f });
        uint shadow = ImGui.ColorConvertFloat4ToU32(new(0, 0, 0, .55f));
        var outlines = Session.TerrainBrushOutline(ViewportPixels);
        for (int i = 0; i < outlines.Count; i++)
        {
            var points = outlines[i];
            draw.AddPolyline(ref points[0], points.Length, shadow, ImDrawFlags.None, i == 0 ? 4f : 2.5f);
            draw.AddPolyline(ref points[0], points.Length, i == 0 ? edge : core, ImDrawFlags.None, i == 0 ? 2f : 1.2f);
        }
        if (Session.TerrainCursor is { } at && Session.ScreenOf(Coordinates.GameToView(at), ViewportPixels) is { } centre)
        {
            draw.AddCircleFilled(centre, 3, edge);
            string label = Loc.F("{0:F1} m · {1}", brush.Radius, brush.Kind is Core.Terrain.TerrainBrushKind.Raise or Core.Terrain.TerrainBrushKind.Lower or Core.Terrain.TerrainBrushKind.Mountain or Core.Terrain.TerrainBrushKind.Roughen
                ? Loc.F("{0:0.#} m/s", brush.Strength) : $"{brush.Strength * 100:F0} %");
            draw.AddText(centre + new Vector2(12, 8), shadow, label);
            draw.AddText(centre + new Vector2(11, 7), edge, label);
        }
        else if (!_terrainStroking && PointerOverViewport)
            draw.AddText(_pointer + new Vector2(14, 10), ImGui.ColorConvertFloat4ToU32(new(1f, .8f, .4f, 1)), Loc.T("Point at the ground (terrain loads around the camera)"));
    }
    private bool _trafficDragging;
    private bool _roadPress, _roadPressMoved;
    private double _nextRoadPick;
    private (bool Shift, bool Control)? _simulatedKeys;
    private bool KeysDown(int key) => _simulatedKeys is { } keys ? key == NativeInput.VkShift ? keys.Shift : key == NativeInput.VkControl && keys.Control : NativeInput.IsVirtualKeyDown(key);
    private static uint U32(float r, float g, float b, float a = 1) => ImGui.ColorConvertFloat4ToU32(new(r, g, b, a));

    // Roads tool: the selected road's handles (ends square), where a click would add a point, and
    // while drawing (or dragging a point) the cursor's point with the stretch's length, grade and
    // turn, the road ends it can meet, and the keys.
    private void DrawRoadOverlay(ImDrawListPtr draw)
    {
        if (Tool != EditorTool.Road || IsLooking) return;
        var s = Session; var viewport = ViewportPixels;
        uint shadow = U32(0, 0, 0, .6f), white = U32(1, 1, 1), amber = U32(1, .78f, .18f), cyan = U32(.3f, .88f, 1), green = U32(.45f, 1, .55f);
        foreach (var h in s.RoadHandles(viewport))
        {
            float r = h.Selected ? 7.5f : h.Hovered ? 7 : h.End ? 6 : 5;
            uint fill = h.Selected ? amber : h.Hovered ? cyan : white;
            if (h.End)
            {
                draw.AddRectFilled(h.Screen - new Vector2(r + 1.5f), h.Screen + new Vector2(r + 1.5f), shadow, 2);
                draw.AddRectFilled(h.Screen - new Vector2(r), h.Screen + new Vector2(r), fill, 2);
            }
            else { draw.AddCircleFilled(h.Screen, r + 1.5f, shadow, 20); draw.AddCircleFilled(h.Screen, r, fill, 20); }
            if (h.Hovered && !s.RoadDrawing && !s.RoadDragging) RoadLabel(draw, h.Screen + new Vector2(14, -10), Loc.T("Drag over the ground (Ctrl: keep its height) · Del removes it"), cyan);
        }
        if (!s.RoadDrawing && !s.RoadDragging && s.RoadInsertHover is { } insert && s.RoadScreen(insert.Point, viewport) is { } at)
        {
            draw.AddCircleFilled(at, 9, shadow, 20); draw.AddCircle(at, 8, green, 20, 2);
            draw.AddLine(at - new Vector2(4, 0), at + new Vector2(4, 0), green, 2); draw.AddLine(at - new Vector2(0, 4), at + new Vector2(0, 4), green, 2);
            RoadLabel(draw, at + new Vector2(14, -10), Loc.T("Click: a point here · drag: bend the road from here"), green);
        }
        if (!s.RoadDrawing && !s.RoadDragging) return;
        if (s.FirstDrawnPoint is { } first && s.RoadScreen(first, viewport) is { } start)
        { draw.AddRectFilled(start - new Vector2(8), start + new Vector2(8), shadow, 2); draw.AddRectFilled(start - new Vector2(6.5f), start + new Vector2(6.5f), amber, 2); }
        if (s.RoadHover is { } cursor)
        {
            foreach (var t in s.SnapTargetsNear(cursor.Point, 150))
                if (s.RoadScreen(t.Point, viewport) is { } end)
                {
                    bool active = ReferenceEquals(cursor.Snap, t);
                    draw.AddCircle(end, active ? 12 : 7, active ? green : U32(.45f, 1, .55f, .55f), 24, active ? 3 : 1.5f);
                }
            if (s.RoadScreen(cursor.Point, viewport) is { } point)
            {
                bool tight = cursor.Radius is { } radius && radius < cursor.HalfWidth;
                var tone = tight ? StudioSession.GradeColor(1) : cursor.Grade is { } g ? StudioSession.GradeColor(g) : new Vector4(.3f, .88f, 1, 1);
                uint color = ImGui.ColorConvertFloat4ToU32(tone);
                draw.AddCircleFilled(point, 7, shadow, 20); draw.AddCircle(point, 6, color, 20, 2.5f);
                var text = new System.Text.StringBuilder();
                if (cursor.Length > 0)
                {
                    text.Append(Loc.F("{0:0.0} m", cursor.Length));
                    if (cursor.Grade is { } grade) text.Append(Loc.F(" · {0:+0.0;-0.0;0} %", grade * 100));
                    if (cursor.Radius is { } turn && turn < 500) text.Append(Loc.F(" · turn {0:0} m", turn));
                }
                else if (s.RoadDrawing) text.Append(Loc.T("Click to start the road here"));
                if (cursor.Snap is { } snap) text.Append('\n').Append(snap.RoadId is { } loop && loop == s.SelectedRoadId ? Loc.T("Closes the loop") : Loc.F("Meets {0}", snap.Label));
                if (cursor.Angled) text.Append('\n').Append(Loc.T("15° steps"));
                if (cursor.Level) text.Append('\n').Append(Loc.T("Same height"));
                if (tight) text.Append('\n').Append(Loc.F("Turn too tight for this road ({0:0.#} m < {1:0.#} m): its inner side folds", cursor.Radius!.Value, cursor.HalfWidth));
                else if (cursor.Grade is { } steep && MathF.Abs(steep) >= .12f) text.Append('\n').Append(Loc.T("Steep: over 12 %"));
                if (text.Length > 0) RoadLabel(draw, point + new Vector2(16, 10), text.ToString(), color);
            }
        }
        if (s.RoadDrawing)
        {
            string keys = Loc.T("Click: add a point · Double-click or Enter: finish · Backspace: remove the last point · Shift: 15° steps · Ctrl: same height · road ends attract the cursor");
            var size = ImGui.CalcTextSize(keys);
            var at2 = new Vector2((_pixelWidth - size.X) / 2, _pixelHeight - size.Y - 34);
            draw.AddRectFilled(at2 - new Vector2(10, 6), at2 + size + new Vector2(10, 6), U32(.05f, .06f, .08f, .82f), 6);
            draw.AddText(at2, U32(.9f, .93f, .97f), keys);
        }
    }

    // A tag with a dark backing (kept inside the view).
    private void RoadLabel(ImDrawListPtr draw, Vector2 at, string text, uint color)
    {
        var size = ImGui.CalcTextSize(text); var pad = new Vector2(7, 5);
        at.X = MathF.Min(at.X, _pixelWidth - size.X - pad.X * 2 - 4); at.Y = MathF.Min(at.Y, _pixelHeight - size.Y - pad.Y * 2 - 4);
        draw.AddRectFilled(at, at + size + pad * 2, U32(.05f, .06f, .08f, .86f), 4);
        draw.AddRect(at, at + size + pad * 2, color, 4, ImDrawFlags.None, 1);
        draw.AddText(at + pad, U32(.93f, .95f, .98f), text);
    }

    public int OverlayVertices { get; private set; }
    public bool GizmoHot => (Tool is EditorTool.Move or EditorTool.Rotate or EditorTool.Scale || (Tool == EditorTool.Road && RoadGizmoActive) || (Tool == EditorTool.Traffic && TrafficGizmoActive) || (Tool == EditorTool.Element && GizmoActive) || ElevatorGizmoActive)
        && (Gizmo.IsOver() != 0 || Gizmo.IsUsing() != 0);

    // --- Navigation ------------------------------------------------------------------

    private bool KeysActive => KeyboardEnabled && !Ai.Locked && NativeInput.IsForeground(_window) && (PointerOverViewport || IsLooking);

    private void Navigate(float dt, double now)
    {
        var camera = Session.Camera;
        if (_settleUntil > now && Session.Grid.Ground(camera.Eye with { Y = camera.Eye.Y + 400 }, 0, 1200) is { } ground)
        {
            camera.SetEye(camera.Eye with { Y = ground + (_settleWalk ? EyeHeight : 35) });
            _settleUntil = 0;
        }
        bool keys = KeysActive && !NativeInput.IsVirtualKeyDown(NativeInput.VkControl) && Modal is null;
        float Key(uint scan) => keys && NativeInput.IsScanDown(scan) ? 1 : 0;
        bool sprint = keys && NativeInput.IsVirtualKeyDown(NativeInput.VkShift);
        bool slow = keys && NativeInput.IsVirtualKeyDown(NativeInput.VkMenu);
        float right = Key(NativeInput.ScanRight) - Key(NativeInput.ScanLeft), forward = Key(NativeInput.ScanForward) - Key(NativeInput.ScanBack);
        if (Navigation == NavigationMode.Fly)
        {
            float up = Math.Max(Key(NativeInput.ScanUp), Key(NativeInput.ScanSpace)) - Math.Max(Key(NativeInput.ScanDown), Key(NativeInput.ScanC));
            float speed = FlySpeed * (sprint ? 4 : 1) * (slow ? .2f : 1);
            if (right != 0 || forward != 0 || up != 0) camera.Move(new(right, up, forward), dt, speed);
            return;
        }
        // Walk: on-foot movement with gravity over loaded geometry; hover until it streams in.
        var flat = camera.Forward with { Y = 0 };
        flat = flat.LengthSquared() < 1e-6f ? -Vector3.UnitZ : Vector3.Normalize(flat);
        var side = Vector3.Normalize(Vector3.Cross(flat, Vector3.UnitY));
        var move = flat * forward + side * right;
        if (move.LengthSquared() > 1) move = Vector3.Normalize(move);
        var eye = camera.Eye + move * (WalkSpeed * (sprint ? 2.4f : 1) * dt);
        var floor = Session.Grid.Ground(eye with { Y = eye.Y - EyeHeight + .6f }, 0, 200);
        if (floor is { } height)
        {
            float feet = eye.Y - EyeHeight;
            bool grounded = feet <= height + .05f && _verticalSpeed <= 0;
            if (grounded)
            {
                eye.Y = height + EyeHeight; _verticalSpeed = 0;
                if (Key(NativeInput.ScanSpace) > 0) _verticalSpeed = JumpSpeed;
            }
            else
            {
                _verticalSpeed -= Gravity * dt;
                eye.Y = Math.Max(height + EyeHeight, eye.Y + _verticalSpeed * dt);
                if (eye.Y <= height + EyeHeight) _verticalSpeed = 0;
            }
        }
        else _verticalSpeed = 0;
        camera.SetEye(eye);
    }

    public void SettleOnGround(bool walk) { _settleUntil = _clock.Elapsed.TotalSeconds + 12; _settleWalk = walk; }

    // --- Pointer input ---------------------------------------------------------------

    public Vector2 ToPixels(Windows.Foundation.Point p) => new((float)p.X * _scaleX, (float)p.Y * _scaleY);

    private Vector2 Pixels(PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(_panel).Position;
        return new((float)p.X * _scaleX, (float)p.Y * _scaleY);
    }

    private void FeedImGui(Vector2 position, int button = -1, bool down = false)
    {
        if (_overlay is null) return;
        ImGui.SetCurrentContext(_overlay.ImGuiContext);
        var io = ImGui.GetIO();
        io.AddMousePosEvent(position.X, position.Y);
        if (button >= 0) io.AddMouseButtonEvent(button, down);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_panel); var position = Pixels(e);
        _pointer = _lastPointer = position; _simulatedKeys = null;
        // The AI has the view: only its "Take back control" button answers.
        if (Ai.Locked)
        {
            if (point.Properties.IsLeftButtonPressed && Ai.TakeBackHit(position)) TakeBackControl();
            e.Handled = true;
            return;
        }
        // A transform started with a key: the left button keeps it, the right one drops it.
        if (Modal is not null)
        {
            if (point.Properties.IsLeftButtonPressed) ConfirmModal(); else if (point.Properties.IsRightButtonPressed) CancelModal();
            e.Handled = true;
            return;
        }
        _panel.CapturePointer(e.Pointer);
        if (point.Properties.IsRightButtonPressed && !_looking)
        {
            _looking = true; _lookAnchor = NativeInput.Cursor(); NativeInput.SetCursorVisible(false);
        }
        else if (point.Properties.IsLeftButtonPressed && VertexSnapHeld && Tool is not (EditorTool.Place or EditorTool.Road or EditorTool.Traffic or EditorTool.Elevator or EditorTool.Terrain)
                 && Session.Selected is { CanEdit: true, Deleted: false } lead && !Session.IsLocked(lead.Id))
        {
            // V + drag: from the selection's vertex nearest to the cursor (or its pivot) onto a vertex or surface.
            _leftDown = true; _leftDragging = false; _pressPosition = position; _gizmoCaptured = true;
            _vertexDrag = true; _vertexTarget = null;
            _vertexSource = Session.SnapPoint(position, ViewportPixels, selected: true, 40)?.Point ?? (Session.Preview ?? lead.Transform).ViewMatrix.Translation;
        }
        else if (point.Properties.IsLeftButtonPressed)
        {
            _leftDown = true; _leftDragging = false; _pressPosition = position;
            _gizmoCaptured = GizmoHot;
            // Mesh edit mode: an opening is drawn on the surface, or faces and vertices picked (a
            // drag draws a selection rectangle; the right button turns the view).
            if (Session.MeshEditing && !_gizmoCaptured)
            {
                if (Session.MeshTool == MeshEditTool.Cut) { _meshCutting = Session.BeginMeshCut(position, ViewportPixels); _gizmoCaptured = true; }
                else { _meshMarquee = true; _gizmoCaptured = true; _marqueeDragging = false; }
                FeedImGui(position, 0, true);
                e.Handled = true;
                return;
            }
            // Traffic: a lane's point, end or the selected lane itself under the cursor starts a drag.
            _trafficDrag = Tool == EditorTool.Traffic && !_gizmoCaptured && !NativeInput.IsVirtualKeyDown(NativeInput.VkMenu) && Session.BeginTrafficDrag(position, ViewportPixels);
            _trafficDragMoved = false;
            if (_trafficDrag) _gizmoCaptured = true;
            // Terrain: the button held on the ground is a brush stroke (Alt: the opposite brush).
            _terrainStroking = Tool == EditorTool.Terrain && !_gizmoCaptured && Session.BeginTerrainStroke(NativeInput.IsVirtualKeyDown(NativeInput.VkMenu));
            if (_terrainStroking) _gizmoCaptured = true;
            // Roads: a handle (or the selected road's curve) under the cursor is dragged over the ground.
            _roadPress = Tool == EditorTool.Road && !_gizmoCaptured && Session.BeginRoadDrag(position, ViewportPixels);
            _roadPressMoved = false;
            if (_roadPress) _gizmoCaptured = true;
            _marquee = !_gizmoCaptured && Tool is not (EditorTool.Place or EditorTool.Road or EditorTool.Traffic or EditorTool.Elevator or EditorTool.Terrain or EditorTool.Note) && NativeInput.IsVirtualKeyDown(NativeInput.VkShift);
            _marqueeDragging = false;
            FeedImGui(position, 0, true);
        }
        else if (point.Properties.IsMiddleButtonPressed) _middleDown = true;
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var position = Pixels(e); _pointer = position; _simulatedKeys = null;
        if (Ai.Locked || Modal is not null) { _lastPointer = position; e.Handled = true; return; }
        var camera = Session.Camera;
        if (_looking)
        {
            var cursor = NativeInput.Cursor();
            float dx = cursor.X - _lookAnchor.X, dy = cursor.Y - _lookAnchor.Y;
            if (dx != 0 || dy != 0) { camera.Orbit(-dx * .0035f, dy * .0035f); NativeInput.MoveCursor(_lookAnchor); }
        }
        else if (_vertexDrag && _vertexSource is { } source)
        {
            _vertexTarget = Session.SnapPoint(position, ViewportPixels, selected: false);
            if (_vertexTarget is { } target) Session.PreviewMove(target.Point - source);
        }
        else if (_leftDown && _meshCutting) Session.UpdateMeshCut(position, ViewportPixels);
        else if (_leftDown && _meshMarquee)
        {
            if (!_marqueeDragging && Vector2.Distance(position, _pressPosition) > 4) _marqueeDragging = true;
        }
        else if (_leftDown && _trafficDrag)
        {
            if (!_trafficDragMoved && Vector2.Distance(position, _pressPosition) > 4) _trafficDragMoved = true;
            if (_trafficDragMoved) Session.UpdateTrafficDrag(position, ViewportPixels, NativeInput.IsVirtualKeyDown(NativeInput.VkShift), NativeInput.IsVirtualKeyDown(NativeInput.VkControl));
        }
        else if (_leftDown && _roadPress)
        {
            if (!_roadPressMoved && Vector2.Distance(position, _pressPosition) > 3) _roadPressMoved = true;
            if (_roadPressMoved) Session.UpdateRoadDrag(position, ViewportPixels, NativeInput.IsVirtualKeyDown(NativeInput.VkControl));
        }
        else if (_leftDown && _marquee)
        {
            if (!_marqueeDragging && Vector2.Distance(position, _pressPosition) > 4) _marqueeDragging = true;
        }
        else if (_leftDown && !_gizmoCaptured)
        {
            if (!_leftDragging && Vector2.Distance(position, _pressPosition) > 5) _leftDragging = true;
            if (_leftDragging) { var d = position - _lastPointer; camera.Orbit(-d.X * .0035f, d.Y * .0035f); }
        }
        else if (_middleDown)
        {
            var d = position - _lastPointer;
            var forward = camera.Forward; var side = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY)); var up = Vector3.Cross(side, forward);
            float scale = Math.Max(.02f, FlySpeed * .006f);
            camera.SetEye(camera.Eye + (-side * d.X + up * d.Y) * scale);
        }
        _lastPointer = position;
        FeedImGui(position);
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_panel); var position = Pixels(e);
        if (Ai.Locked) { e.Handled = true; return; }
        if (_looking && !point.Properties.IsRightButtonPressed) StopLook();
        if (_leftDown && !point.Properties.IsLeftButtonPressed && _vertexDrag)
        {
            if (_vertexTarget is not null) Session.CommitPreview(); else Session.CancelPreview();
            Session.Status = _vertexTarget is { Vertex: true } ? Loc.T("Snapped onto the vertex.") : _vertexTarget is not null ? Loc.T("Placed on the surface.") : Session.Status;
            _vertexDrag = false; _vertexSource = null; _vertexTarget = null;
            _leftDown = _leftDragging = _gizmoCaptured = false;
        }
        if (_terrainStroking && !point.Properties.IsLeftButtonPressed) { Session.EndTerrainStroke(); _terrainStroking = false; }
        if (_leftDown && !point.Properties.IsLeftButtonPressed && (_meshCutting || _meshMarquee))
        {
            FeedImGui(position, 0, false);
            bool shift = NativeInput.IsVirtualKeyDown(NativeInput.VkShift), control = NativeInput.IsVirtualKeyDown(NativeInput.VkControl);
            if (_meshCutting) Session.EndMeshCut();
            else if (_marqueeDragging) Session.MeshEditRectangle(_pressPosition, position, ViewportPixels, shift, control);
            else Session.MeshEditClick(position, ViewportPixels, shift, control);
            _meshCutting = _meshMarquee = false;
            _leftDown = _leftDragging = _gizmoCaptured = _marquee = _marqueeDragging = false;
        }
        if (_leftDown && !point.Properties.IsLeftButtonPressed && _roadPress)
        {
            FeedImGui(position, 0, false);
            Session.EndRoadDrag(_roadPressMoved);
            _roadPress = _roadPressMoved = false;
            _leftDown = _leftDragging = _gizmoCaptured = _marquee = _marqueeDragging = false;
        }
        if (_leftDown && !point.Properties.IsLeftButtonPressed && _trafficDrag)
        {
            FeedImGui(position, 0, false);
            // Without moving, it was a click (select, double-click, Alt+click).
            if (_trafficDragMoved) Session.EndTrafficDrag();
            else { Session.CancelTrafficDrag(); Session.TrafficClick(position, ViewportPixels, NativeInput.IsVirtualKeyDown(NativeInput.VkControl), NativeInput.IsVirtualKeyDown(NativeInput.VkMenu)); }
            _trafficDrag = _trafficDragMoved = false;
            _leftDown = _leftDragging = _gizmoCaptured = _marquee = _marqueeDragging = false;
        }
        if (_leftDown && !point.Properties.IsLeftButtonPressed)
        {
            FeedImGui(position, 0, false);
            if (_marquee)
            {
                bool control = NativeInput.IsVirtualKeyDown(NativeInput.VkControl);
                // A Shift-click without dragging adds or removes one object.
                if (_marqueeDragging) Session.SelectInRectangle(_pressPosition, position, ViewportPixels, control);
                else Session.Pick(position, ViewportPixels, true, PickDecals);
            }
            else if (Tool == EditorTool.Note && _leftDragging && !_gizmoCaptured) Session.NoteDrag(_pressPosition, position, ViewportPixels);
            else if (!_leftDragging && !_gizmoCaptured)
            {
                if (Tool == EditorTool.Place) PlaceRequested?.Invoke(Session.PlacementPoint(position, ViewportPixels));
                else if (Tool == EditorTool.Road) Session.RoadClick(position, ViewportPixels, NativeInput.IsVirtualKeyDown(NativeInput.VkShift), NativeInput.IsVirtualKeyDown(NativeInput.VkControl));
                else if (Tool == EditorTool.Elevator) Session.ElevatorClick(position, ViewportPixels);
                else if (Tool == EditorTool.Traffic) Session.TrafficClick(position, ViewportPixels, NativeInput.IsVirtualKeyDown(NativeInput.VkControl), NativeInput.IsVirtualKeyDown(NativeInput.VkMenu));
                else if (Tool == EditorTool.Element && Session.ArmedElement is not null) Session.PlaceElement(position, ViewportPixels);
                else if (Tool == EditorTool.Terrain) { }
                else if (Tool == EditorTool.Note) Session.NoteClick(position, ViewportPixels, NativeInput.IsVirtualKeyDown(NativeInput.VkControl));
                else Session.Pick(position, ViewportPixels, NativeInput.IsVirtualKeyDown(NativeInput.VkControl), PickDecals);
            }
            _leftDown = _leftDragging = _gizmoCaptured = _marquee = _marqueeDragging = false;
        }
        if (!point.Properties.IsMiddleButtonPressed) _middleDown = false;
        if (!_looking && !_leftDown && !_middleDown) _panel.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    // A click takes the decal under the cursor rather than its surface: Elements tool, or Alt held.
    private bool PickDecals => Tool == EditorTool.Element || NativeInput.IsVirtualKeyDown(NativeInput.VkMenu);

    private void OnPointerWheel(object sender, PointerRoutedEventArgs e)
    {
        if (Ai.Locked || Modal is not null) { e.Handled = true; return; }
        int wheel = e.GetCurrentPoint(_panel).Properties.MouseWheelDelta;
        // Terrain: Shift+wheel sizes the brush, Ctrl+Shift+wheel sets its strength.
        if (Tool == EditorTool.Terrain && NativeInput.IsVirtualKeyDown(NativeInput.VkShift))
        {
            if (NativeInput.IsVirtualKeyDown(NativeInput.VkControl)) Session.ScaleTerrainStrength(MathF.Exp(wheel / 120f * .15f));
            else Session.ResizeTerrainBrush(MathF.Exp(wheel / 120f * .12f));
            e.Handled = true; return;
        }
        Wheel(wheel,
            e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control) || NativeInput.IsVirtualKeyDown(NativeInput.VkControl));
        e.Handled = true;
    }

    // The wheel sets the fly speed; with Ctrl, the field of view in 5 degree steps (wheel up
    // narrows, like a zoom). Smooth wheels send fractions of a notch: they add up.
    public void Wheel(int delta, bool control)
    {
        if (control)
        {
            _fieldOfViewWheel += delta;
            int notches = _fieldOfViewWheel / 120;
            if (notches != 0)
            {
                _fieldOfViewWheel -= notches * 120;
                Session.Camera.FieldOfView = MathF.Round(Session.Camera.FieldOfView / 5) * 5 - notches * 5;
                FieldOfViewChanged?.Invoke();
            }
        }
        else FlySpeed = Math.Clamp(FlySpeed * MathF.Exp(delta / 120f * .15f), .5f, 800);
    }

    // The agent locks the view: whatever the user was doing in it stops.
    public void LockForAgent(string client, string? reason)
    {
        EndInteractions();
        Ai.Lock(client, reason);
    }

    // The user takes the view back (button, Esc).
    public void TakeBackControl()
    {
        if (!Ai.Locked) return;
        Ai.Release("user");
        Session.Status = Loc.T("You have the view back.");
    }

    private void StopLook()
    {
        if (!_looking) return;
        _looking = false; NativeInput.SetCursorVisible(true);
    }

    public void EndInteractions()
    {
        CancelModal();
        StopLook(); _leftDown = _leftDragging = _middleDown = _gizmoCaptured = _marquee = _marqueeDragging = false;
        if (_terrainStroking) { Session.EndTerrainStroke(); _terrainStroking = false; }
        if (_roadPress) { Session.CancelRoadDrag(); _roadPress = _roadPressMoved = false; }
        if (_vertexDrag) { Session.CancelPreview(); _vertexDrag = false; _vertexSource = null; _vertexTarget = null; }
        if (_meshCutting) { Session.CancelMeshCut(); _meshCutting = false; }
        _meshMarquee = false;
        if (_meshGizmoDragging) { Session.MeshEditCancelPreview(); _meshGizmoDragging = false; _pivotNow = null; }
        if (_overlay is not null) FeedImGui(_pointer, 0, false);
    }

    public void Dispose()
    {
        CompositionTarget.Rendering -= OnRendering;
        StopLook();
        SwapChainPanels.Detach(_panel);
        _overlay?.Dispose(); _overlay = null;
        Session.Dispose();
        Renderer.Dispose();
    }
}
