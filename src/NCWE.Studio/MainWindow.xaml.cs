// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NCWE.Core;
using NCWE.Render;
using NCWE.Studio.Session;
using NCWE.Studio.Viewport;
using Windows.Graphics;
using Windows.System;

namespace NCWE.Studio;

public sealed partial class MainWindow : Window
{
    private readonly StudioSettings _settings = StudioSettings.Load();
    private readonly nint _hwnd;
    private ViewportController? _viewport;
    private StudioSession? Session => _viewport?.Session;
    private double _nextUiUpdate;
    private bool _closing, _closeReady, _updatingToolbar;
    private string? _detectedGame;
    private Automation? _automation;

    public MainWindow()
    {
        InitializeComponent();
        Brand.SetIcon(this);
        Loc.Translate(this);
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Colors.Transparent; titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = ColorHelper.FromArgb(255, 0xEC, 0xEF, 0xF4);
        titleBar.ButtonHoverBackgroundColor = ColorHelper.FromArgb(255, 0x22, 0x27, 0x33);
        titleBar.ButtonPressedBackgroundColor = ColorHelper.FromArgb(255, 0x2A, 0x2F, 0x3A);
        double scale = NativeInput.Dpi(_hwnd);
        CaptionColumn.Width = new GridLength(Math.Max(140, titleBar.RightInset / scale));
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int width = Math.Min((int)(1680 * scale), area.Width - 60), height = Math.Min((int)(1000 * scale), area.Height - 60);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
        if (AppWindow.Presenter is OverlappedPresenter presenter) { presenter.PreferredMinimumWidth = (int)(1000 * scale); presenter.PreferredMinimumHeight = (int)(640 * scale); }
        AppWindow.Closing += OnClosing;

        SidebarColumn.Width = new GridLength(Math.Clamp(_settings.SidebarWidth, 300, 620));
        SidebarSizer.Dragged += delta =>
        {
            double next = Math.Clamp(SidebarColumn.ActualWidth - delta, 300, Math.Min(620, Root.ActualWidth - 500));
            SidebarColumn.Width = new GridLength(next); _settings.SidebarWidth = next;
        };
        InitializeToolbarDrag();
        InitializeEffectHover();
        ApplyToolbarOrientation();
        Viewport.Loaded += (_, _) => StartViewport();
        InitializeHome();
        InitializeLanguage();
        InitializeAccount();
        HomeCrashNotice.Visibility = CrashReport.PreviousExitUnclean ? Visibility.Visible : Visibility.Collapsed;
        Root.PreviewKeyDown += OnPreviewKeyDown;
        Activated += (_, e) => { if (e.WindowActivationState == WindowActivationState.Deactivated) { _viewport?.EndInteractions(); HideEffectHover(); } };
        // The saved place is applied again whenever the viewport or the toolbox gets its size (the
        // first layout passes are narrower: clamping the current place would keep the squeezed one).
        ToolbarCanvas.SizeChanged += (_, _) => { if (!_toolbarDragging) PositionToolbar(); };
        ToolBox.SizeChanged += (_, _) => { if (!_toolbarDragging) PositionToolbar(); };
    }

    private void StartViewport()
    {
        if (_viewport is not null) return;
        try
        {
            _viewport = new ViewportController(Viewport, _hwnd, _settings);
            _viewport.Session.NoteReady += OnNoteReady;
        }
        catch (Exception ex)
        {
            App.WriteCrash(ex);
            StatusText.Text = Loc.F("Direct3D 11 could not start: {0}", ex.Message); StatusIcon.Glyph = ""; StatusIcon.Foreground = (Brush)Application.Current.Resources["DangerBrush"];
            return;
        }
        _viewport.Rendered += OnFrame;
        _viewport.FieldOfViewChanged += ShowFieldOfView;
        _viewport.DeviceLostDetected += () => DispatcherQueue.Post(() => _ = ShowDeviceLostAsync());
        Viewport.PointerPressed += (_, _) => ViewportFocus.Focus(FocusState.Pointer);
        ViewportFocus.Focus(FocusState.Programmatic);
        _viewport.PlaceRequested = PlaceAt;
        InitializeLibraryDrop();
        InitializeMaterialDrop();
        InitializeRadial();
        _viewport.Session.SceneChanged += () => _entitiesDirty = true;
        ApplySettingsToChrome();
        InitializeCustom(_viewport.Session);
        InitializeDecals(_viewport.Session);
        // The automation pipe (NCWE MCP server, scripts): current user only; --no-pipe turns it off.
        // The MCP window (Help ▸ AI agents) turns it off and on; an explicit --pipe always opens it.
        string? pipe = App.Value("--pipe") ?? (App.Arguments.Contains("--no-pipe") || !_settings.AiBridge ? null : Automation.DefaultPipe);
        _bridgePipe = App.Value("--pipe") ?? Automation.DefaultPipe;
        if (pipe is not null) _automation = new Automation(pipe, this, Bridge);
        string? game = App.Value("--game") ?? _settings.Game;
        if (App.Arguments.Contains("--no-auto-open")) game = null;
        if (GameLocator.IsInstallation(game)) _ = OpenGame(game!);
        else
        {
            _detectedGame = App.Arguments.Contains("--no-auto-open") ? null : GameLocator.Detect();
            if (_detectedGame is not null)
            {
                DetectedGameButton.Content = Loc.F("Use {0}", _detectedGame); DetectedGameButton.Visibility = Visibility.Visible;
                WelcomeSubtitle.Text = Loc.T("Found an installation on this PC.");
            }
        }
    }

    internal ViewportController? Controller => _viewport;

    // For the splash screen: the first frame was drawn, and what the start is doing.
    internal bool HasRendered { get; private set; }
    internal string StartupStatus => _viewport is null ? Loc.T("Starting the 3D view…") : Session is { } s && s.Status.Length > 0 ? s.Status : Loc.T("Opening the game archives…");

    internal async Task OpenGame(string path)
    {
        if (Session is not { } session) return;
        WelcomeCard.Visibility = Visibility.Visible; ChooseGamePanel.Visibility = Visibility.Collapsed; LoadingPanel.Visibility = Visibility.Visible;
        _gameError = null;
        await session.OpenGameAsync(path);
        if (session.Index is null)
        {
            LoadingPanel.Visibility = Visibility.Collapsed; ChooseGamePanel.Visibility = Visibility.Visible;
            WelcomeSubtitle.Text = _gameError = session.Error.Length > 0 ? session.Error : Loc.T("The installation could not be opened.");
            return;
        }
        WelcomeCard.Visibility = Visibility.Collapsed;
        BuildMap();
        InitializeAdd(session.Index);
        if (_settings.SidebarTab == "Add") ShowAddCategory(_settings.AddCategory);
        UpdateProjectPanel();
        await OpenPendingProject();
    }

    // --- Settings <-> chrome ----------------------------------------------------------

    private void ApplySettingsToChrome()
    {
        _updatingToolbar = true;
        var v = _viewport!;
        SpeedSlider.Value = SpeedToSlider(v.FlySpeed);
        DistanceBox.SelectedIndex = (int)_settings.ViewDistance;
        AtmosphereButton.IsChecked = AtmosphereMenu.IsChecked = v.Renderer.Atmosphere;
        PreflightMenu.IsChecked = v.Session.Settings.Preflight;
        SyncIconMenus();
        SyncDrawDistanceMenu();
        SyncPreferences();
        ShadeMaterial.IsChecked = v.Renderer.Shading == SceneShading.Material; ShadeTinted.IsChecked = v.Renderer.Shading == SceneShading.Tinted; ShadeClay.IsChecked = v.Renderer.Shading == SceneShading.Clay; ShadeType.IsChecked = v.Renderer.Shading == SceneShading.NodeType;
        SnapButton.IsChecked = v.Snap;
        v.Session.Pivot = _settings.Pivot; SnapObjectsButton.IsChecked = v.SnapObjects; ShowPivot();
        LocalSpaceButton.IsChecked = !v.LocalSpace; SpaceLabel.Text = v.LocalSpace ? Loc.T("Local") : Loc.T("World");
        SyncMaterialsChrome();
        _updatingToolbar = false;
        SetTool(EditorTool.Select);
        SetNavigation(_settings.Navigation);
        ApplyFog();
        RefreshBookmarks();
        PositionToolbar();
        InitializeOutliner();
        PanelSection.Collapsed = _settings.CollapsedSections;
        PanelSection.CollapsedChanged = _settings.Save;
        if (_settings.SidebarTab is "World" or "Project") ShowTab(_settings.SidebarTab);
    }

    private static double SpeedToSlider(float speed) => Math.Log(Math.Clamp(speed, .5f, 800) / .5) / Math.Log(1600) * 100;
    private static float SliderToSpeed(double value) => (float)(.5 * Math.Pow(1600, value / 100));

    private void SaveSettings()
    {
        if (_viewport is not { } v) return;
        _settings.SnapObjects = v.SnapObjects; _settings.Pivot = v.Session.Pivot;
        _settings.FlySpeed = v.FlySpeed; _settings.FieldOfView = v.Session.Camera.FieldOfView; _settings.Navigation = v.Navigation; _settings.Snap = v.Snap; _settings.LocalSpace = v.LocalSpace;
        _settings.Atmosphere = v.Renderer.Atmosphere; _settings.Shading = v.Renderer.Shading; _settings.GameFaces = v.Renderer.GameFaces; _settings.ShowDecals = v.Renderer.ShowDecals;
        _settings.MaterialRange = v.Renderer.MaterialRange; _settings.TextureLimit = v.Renderer.TextureLimit; _settings.DrawDistance = v.Renderer.DrawDistance;
        _settings.Save();
    }

    // --- Per-frame chrome refresh ----------------------------------------------------------

    private void OnFrame()
    {
        if (_viewport is not { } v) return;
        HasRendered = true;
        v.KeyboardEnabled = !IsTextInputFocused() && !HomeOpen;
        if (_tab == "Add") _thumbnails?.Pump();
        double now = Environment.TickCount64 / 1000.0;
        if (now < _nextUiUpdate) return;
        _nextUiUpdate = now + .1;
        var s = v.Session;
        // The shell's own cost, by block (the profile pipe op shows them as ui.*).
        long mark = System.Diagnostics.Stopwatch.GetTimestamp();
        void Mark(string block) { long t = System.Diagnostics.Stopwatch.GetTimestamp(); FrameProfile.Record("ui." + block, System.Diagnostics.Stopwatch.GetElapsedTime(mark, t).TotalMilliseconds); mark = t; }
        // Status and errors.
        bool error = s.Error.Length > 0;
        StatusText.Text = error ? s.Error : s.Status;
        StatusIcon.Glyph = error ? "" : s.Busy ? "" : "";
        StatusIcon.Foreground = (Brush)Application.Current.Resources[error ? "DangerBrush" : s.Busy ? "CyanBrush" : "SuccessBrush"];
        DismissError.Visibility = error ? Visibility.Visible : Visibility.Collapsed;
        if (s.Indexing) LoadingText.Text = s.Status;
        var eye = Coordinates.ViewToGame(s.Camera.Eye);
        CameraText.Text = $"X {eye.X,8:F1}   Y {eye.Y,8:F1}   Z {eye.Z,6:F1}";
        SceneText.Text = Loc.F("{0:N0} visible · {1:N0} draws", v.Renderer.VisiblePlacements, v.Renderer.DrawCalls);
        if (now >= _nextMemorySample) { _nextMemorySample = now + 1; _memoryText = $"{SystemMemory.PrivateBytes() >> 20:N0} MB"; }
        FpsText.Text = Loc.F("{0:F0} fps · {1}", v.FramesPerSecond, _memoryText);
        Mark("status");
        if (IssuesExpander.IsExpanded && now >= _nextIssues) { _nextIssues = now + 2; RefreshIssues(); }
        if (Math.Abs(SliderToSpeed(SpeedSlider.Value) - v.FlySpeed) > .01f * v.FlySpeed) { _updatingToolbar = true; SpeedSlider.Value = SpeedToSlider(v.FlySpeed); _updatingToolbar = false; }
        SpeedLabel.Text = v.FlySpeed >= 100 ? $"{v.FlySpeed:F0} m/s" : $"{v.FlySpeed:F1} m/s";
        FieldOfViewLabel.Text = $"{s.Camera.FieldOfView:F0}°";
        UpdateRecentProjects();
        UpdateVariantsMenu(s, now);
        UpdateHome();
        Mark("home");
        // The welcome card (game choice, loading) gives way to the splash and the home screen.
        bool welcomeHidden = HomeOpen || SplashUp;
        WelcomeCard.Opacity = welcomeHidden ? 0 : 1; WelcomeCard.IsHitTestVisible = !welcomeHidden;
        if (FieldOfViewToast.Visibility == Visibility.Visible && now >= _fieldOfViewToastUntil) FieldOfViewToast.Visibility = Visibility.Collapsed;
        Crosshair.Visibility = v.IsLooking ? Visibility.Visible : Visibility.Collapsed;
        string hint = v.Ai.Locked ? Loc.F("{0} has the view · press Esc or Take back control to get it back", v.Ai.Agent)
            : (v.Navigation == NavigationMode.Walk
                ? Loc.T("Hold right mouse to look · move with your game keys · Shift run · Space jump · Tab fly")
                : Loc.T("Hold right mouse to look · move with your game keys · A/E or Space/C down/up · Shift fast · wheel speed · Ctrl+wheel field of view"))
              + Loc.F(" · hold {0}: shortcut menu", KeyLabel("Radial")) + (_settings.BlenderKeys == true ? Loc.T(" · G / R / S: move, turn, scale the selection") : "");
        if (HintText.Text != hint) HintText.Text = hint;
        // Title and project state.
        DirtyDot.Visibility = s.Document.IsDirty ? Visibility.Visible : Visibility.Collapsed;
        ProjectTitle.Text = s.Document.Project.Name + (s.ProjectPath is not null ? " — " + Path.GetFileName(s.ProjectPath) : _live is not null ? Loc.T(" — live") : CloudLinked ? Loc.T(" — cloud") : "");
        UndoMenu.Text = s.Document.CanUndo ? Loc.F("Undo {0}", s.Document.UndoName.ToLowerInvariant()) : Loc.T("Undo");
        RedoMenu.Text = s.Document.CanRedo ? Loc.F("Redo {0}", s.Document.RedoName.ToLowerInvariant()) : Loc.T("Redo");
        UndoMenu.IsEnabled = s.Document.CanUndo; RedoMenu.IsEnabled = s.Document.CanRedo;
        UpdateStreaming(s);
        UpdateLoadingHold(s);
        UpdateAgentFeed();
        TickLive(s, v);
        UpdateMapMarker();
        Mark("chrome");
        UpdateOutliner();
        Mark("outliner");
        if (_worldScope && _entitiesDirty && now >= _nextEntityRefresh) RefreshEntities();
        SyncEntitySelection();
        FollowSelection(s);
        Mark("entities");
        switch (_tab)
        {
            case "Inspector": UpdateInspector(); break;
            case "Add": UpdateAddPanel(s); break;
            case "World": UpdateCollisionStats(); break;
            case "Project": UpdateProjectPanel(); break;
        }
        Mark("tab." + _tab);
        if (NightSwitch.IsOn != v.Renderer.NightPreview || NightMenu.IsChecked != v.Renderer.NightPreview) SetNight(v.Renderer.NightPreview);
        if (GameFacesMenu.IsChecked != v.Renderer.GameFaces) GameFacesMenu.IsChecked = v.Renderer.GameFaces;
        if (DecalsMenu.IsChecked != v.Renderer.ShowDecals) DecalsMenu.IsChecked = v.Renderer.ShowDecals;
        UpdateSounds(s);
        UpdateTrafficPanel(s);
        UpdateTerrainPanel(s);
        UpdateMeshEditPanel(s);
        UpdateLayerPanel(s);
        Mark("panels");
    }

    private void UpdateStreaming(StudioSession s)
    {
        if (s.StreamMetrics is not { } m) { StreamingCard.Visibility = Visibility.Collapsed; StreamDetails.Text = Loc.T("Streaming starts once the game is indexed."); return; }
        int pending = m.PendingSectors + m.PendingMeshes + m.DeferredMeshes + v_Uploads();
        bool loading = pending > 0 || m.ActiveWorkers > 0;
        StreamingCard.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        StreamingText.Text = m.PendingSectors > 0 ? Loc.F("Loading sectors · {0}/{1}", m.ResidentSectors, m.DesiredSectors) : Loc.F("Loading meshes · {0:N0} left", pending);
        double total = m.DesiredSectors + m.CachedMeshes + m.PendingMeshes + m.DeferredMeshes;
        StreamProgress.Value = total <= 0 ? 0 : (m.ResidentSectors + (double)m.CachedMeshes) / total;
        long gpu = m.GpuReservedBytes >> 20, gpuBudget = (s.Budgets?.GpuBytes ?? 0) >> 20;
        var p = s.Preflight;
        string preflight = p.Running ? (p.SectorsTotal > 0 ? Loc.F("\nPreflight {0:N0} / {1:N0} sectors · {2:N0} meshes converted", p.SectorsDone, p.SectorsTotal, p.MeshesConverted) : Loc.T("\nPreflight starting…"))
            : p.Completed ? Loc.F("\nPreflight done · {0:N0} sectors, {1:N0} meshes converted", p.SectorsTotal, p.MeshesConverted) : p.Error is { } failed ? Loc.F("\nPreflight {0}", failed) : "";
        StreamDetails.Text = Loc.F("Sectors   {0,4} / {1}\nMeshes    {2,4} loaded · {3} queued\nWorkers   {4} busy · cache hits {5:N0}\nGPU       {6} / {7} MiB\nIssues    {8}{9}", m.ResidentSectors, m.DesiredSectors, m.CachedMeshes, m.PendingMeshes + m.DeferredMeshes, m.ActiveWorkers, s.CacheHits, gpu, gpuBudget, m.Diagnostics, preflight);
        int v_Uploads() => _viewport!.Renderer.PendingWorldUploads;
    }

    // --- Keyboard ------------------------------------------------------------------------

    private bool IsTextInputFocused()
    {
        var focused = FocusManager.GetFocusedElement(Root.XamlRoot);
        return focused is TextBox or NumberBox or PasswordBox or AutoSuggestBox or RichEditBox
            || (focused is FrameworkElement { Parent: NumberBox });
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // The home screen: Esc closes it; the editor's shortcuts wait (menu accelerators still work).
        // A text field (the title being renamed) keeps its own keys.
        if (HomeOpen && !IsTextInputFocused())
        {
            if (e.Key == VirtualKey.Escape) { CloseHome(); e.Handled = true; }
            return;
        }
        if (_viewport is not { } v || IsTextInputFocused()) return;
        // The AI has the view: the editor's keys wait; Esc takes the view back.
        if (v.Ai.Locked)
        {
            if (e.Key == VirtualKey.Escape) { v.TakeBackControl(); e.Handled = true; }
            return;
        }
        // A transform started with a key (Blender shortcuts) owns every key until it ends.
        if (v.Modal is not null)
        {
            if (e.Key is not (VirtualKey.Shift or VirtualKey.Control or VirtualKey.Menu or VirtualKey.LeftShift or VirtualKey.RightShift or VirtualKey.LeftControl
                or VirtualKey.RightControl or VirtualKey.LeftMenu or VirtualKey.RightMenu)) v.ModalKey(e.Key, NativeInput.IsVirtualKeyDown(NativeInput.VkShift));
            e.Handled = true;
            return;
        }
        // The radial menu (MainWindow.Radial): Esc closes it; its key opens it (repeats while held are swallowed).
        if (e.Key == VirtualKey.Escape && CloseRadial()) { e.Handled = true; return; }
        // The Note tool: Esc drops the note in progress, Backspace removes its last point.
        if (v.Tool == EditorTool.Note && e.Key == VirtualKey.Escape && v.Session.CancelNote()) { e.Handled = true; return; }
        if (e.Key == VirtualKey.Escape && v.Session.SelectedNoteId is not null) { v.Session.SelectNote(null); e.Handled = true; return; }
        if (v.Tool == EditorTool.Note && e.Key == VirtualKey.Back && v.Session.NoteDraftPoints.Count > 0) { v.Session.NoteBackspace(); e.Handled = true; return; }
        // Enter completes a line or objects note directly (not through the shortcut table, which may leave it unbound).
        if (v.Tool == EditorTool.Note && e.Key == VirtualKey.Enter && v.Session.FinishNote()) { e.Handled = true; return; }
        bool control = NativeInput.IsVirtualKeyDown(NativeInput.VkControl);
        bool shift = NativeInput.IsVirtualKeyDown(NativeInput.VkShift), alt = NativeInput.IsVirtualKeyDown(NativeInput.VkMenu);
        if (control)
        {
            // Copy and paste of objects (text fields keep their own Ctrl+C / Ctrl+V); the rest: menu accelerators.
            if (e.Key == VirtualKey.C) { CopySelection(); e.Handled = true; }
            else if (e.Key == VirtualKey.V) { _ = PasteClipboard(shift); e.Handled = true; }
            return;
        }
        if (e.Key == VirtualKey.F2) { RenameSelection(); e.Handled = true; return; }
        if (e.Key == VirtualKey.Back && v.Tool == EditorTool.Traffic && v.Session.TrafficDrawing) { v.Session.TrafficBackspace(); e.Handled = true; return; }
        if (e.Key == VirtualKey.Back && v.Tool == EditorTool.Road && v.Session.RoadDrawing) { v.Session.RoadBackspace(); e.Handled = true; return; }
        // Terrain: [ and ] size the brush (keys left of Enter on most layouts).
        if (v.Tool == EditorTool.Terrain && (int)e.Key is 219 or 221 && (v.PointerOverViewport || v.IsLooking))
        { v.Session.ResizeTerrainBrush((int)e.Key == 221 ? 1.15f : 1 / 1.15f); e.Handled = true; return; }
        // Mesh edit mode (MainWindow.MeshEdit): M starts or ends it; V, B, C, Delete and Esc are its own.
        if (OnMeshEditKey(e.Key)) { e.Handled = true; return; }
        // Blender shortcuts (MainWindow.Preferences): G, R and S on the selection, before the keys' usual actions.
        if (!alt && OnBlenderKey(e.Key, e.KeyStatus.WasKeyDown)) { e.Handled = true; return; }
        if (ActionFor(e.Key) == "Radial") { if (e.KeyStatus.WasKeyDown || OnRadialKey(e.Key)) e.Handled = true; return; }
        switch (ActionFor(e.Key))
        {
            case "Select": SetTool(EditorTool.Select); break;
            case "Move": SetTool(EditorTool.Move); break;
            case "Rotate": SetTool(EditorTool.Rotate); break;
            case "Scale": SetTool(EditorTool.Scale); break;
            case "Place": SetTool(EditorTool.Place); break;
            case "Road": SetTool(EditorTool.Road); break;
            case "Element": SetTool(EditorTool.Element); break;
            case "Traffic": SetTool(EditorTool.Traffic); break;
            case "Note": SetTool(EditorTool.Note); break;
            case "Elevator": SetTool(EditorTool.Elevator); break;
            case "Terrain": SetTool(EditorTool.Terrain); break;
            case "Finish":
                if (v.Tool == EditorTool.Road && v.Session.RoadDrawing) v.Session.FinishRoadDrawing();
                else if (v.Tool == EditorTool.Traffic && v.Session.TrafficDrawing) v.Session.FinishTrafficDrawing();
                else if (v.Tool == EditorTool.Note && v.Session.FinishNote()) { }
                else return;
                break;
            case "Focus": if (v.PointerOverViewport || v.IsLooking) v.Session.FrameSelection(); else return; break;
            case "Delete":
                // A note picked in the outliner or the view goes first (when no object is selected).
                if (v.Session.DeleteSelectedNote()) break;
                if (v.Tool == EditorTool.Traffic && v.Session.SelectedLane is { } lane)
                { if (v.Session.SelectedLanePoint >= 0) v.Session.DeleteLanePoint(); else v.Session.DeleteLane(lane, bothWays: false); }
                else if (v.Session.SelectedRoad is not null) { if (v.Session.SelectedRoadPoint >= 0 && v.Session.SelectedRoad.Points.Length > 2) v.Session.DeleteRoadPoint(); else v.Session.DeleteSelectedRoad(); }
                // A selected floor goes (an elevator keeps two); otherwise the elevator.
                else if (v.Session.SelectedElevator is { } lift) { if (v.Session.SelectedElevatorFloor >= 0 && lift.Floors.Length > 2) v.Session.RemoveElevatorFloor(v.Session.SelectedElevatorFloor); else v.Session.DeleteSelectedElevator(); }
                else v.Session.DeleteSelected();
                break;
            case "Drop": if (v.Session.SelectedRoad is not null) v.Session.SnapRoadToGround(); else v.Session.DropSelectedToGround(); break;
            case "Deselect":
                v.EndInteractions();
                if (v.Tool == EditorTool.Place) SetTool(EditorTool.Select);
                else if (v.Session.ArmedElement is not null) v.Session.ArmElement(null);
                else if (v.Session.ArmedElevator is not null) v.Session.ArmElevator(null);
                else if (v.Tool == EditorTool.Traffic && v.Session.TrafficDragging) v.Session.CancelTrafficDrag();
                else if (v.Tool == EditorTool.Traffic && v.Session.TrafficLinking is not null) v.Session.CancelLinking();
                else if (v.Tool == EditorTool.Traffic && v.Session.TrafficDrawing) v.Session.CancelTrafficDrawing();
                else if (v.Tool == EditorTool.Traffic && v.Session.SelectedLane is not null) v.Session.SelectLane(null);
                else if (v.Session.RoadDrawing) v.Session.FinishRoadDrawing();
                else if (v.Session.SelectedRoad is not null) v.Session.SelectRoad(null);
                else if (v.Session.SelectedElevator is not null) { if (v.Session.SelectedElevatorFloor >= 0) v.Session.SelectElevatorFloor(-1); else v.Session.SelectElevator(null); }
                else v.Session.Select(null);
                break;
            case "Hide": if (alt) v.Session.RevealAll(); else if (shift) v.Session.ToggleIsolation(); else v.Session.HideSelected(); break;
            case "Lock": if (alt) v.Session.UnlockAll(); else v.Session.ToggleLockSelected(); break;
            case "ToggleWalk": SetNavigation(v.Navigation == NavigationMode.Fly ? NavigationMode.Walk : NavigationMode.Fly); break;
            // Movement keys are polled by the viewport; keep them from moving focus or scrolling.
            default: if (e.Key == VirtualKey.Space && (v.PointerOverViewport || v.IsLooking)) break; return;
        }
        e.Handled = true;
    }

    // Configurable shortcuts (Help > Keyboard shortcuts); keypad digits mirror the number row.
    private string? ActionFor(VirtualKey key)
    {
        string name = key is >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9 ? "Number" + (key - VirtualKey.NumberPad0) : key.ToString();
        foreach (var (action, _, _) in StudioSettings.DefaultShortcuts)
            if (string.Equals(_settings.KeyFor(action), name, StringComparison.OrdinalIgnoreCase)) return action;
        return null;
    }

    // --- Tools and navigation -------------------------------------------------------------

    internal void SetTool(EditorTool tool)
    {
        if (_viewport is null) return;
        if (_viewport.Tool != tool) App.Breadcrumb("tool " + tool);
        if (tool != EditorTool.Place) _armedPrefab = null;
        if (tool == EditorTool.Place && _selectedAsset is null && _armedPrefab is null)
        {
            ShowAddCategory("Props");
            tool = EditorTool.Select;
            Session!.Status = Loc.T("Choose a prop in Add, then click in the world to place it.");
        }
        if (_viewport.Tool == EditorTool.Road && tool != EditorTool.Road && Session is { } s) { s.StopRoadDrawing(); s.SelectRoad(null); }
        if (_viewport.Tool == EditorTool.Element && tool != EditorTool.Element) Session?.ArmElement(null);
        if (_viewport.Tool == EditorTool.Elevator && tool != EditorTool.Elevator) Session?.ArmElevator(null);
        if (Session is { } lifts) lifts.ElevatorMode = tool == EditorTool.Elevator;
        if (_viewport.Tool == EditorTool.Traffic && tool != EditorTool.Traffic && Session is { } traffic) { traffic.CancelTrafficDrawing(); traffic.CancelLinking(); traffic.SelectLane(null); }
        if (Session is { } trafficSession) { trafficSession.TrafficMode = tool == EditorTool.Traffic; if (tool == EditorTool.Traffic) { trafficSession.EnsureTraffic(); ShowTab("Inspector"); } }
        if (Session is { } session) session.ElementsMode = tool == EditorTool.Element;
        if (Session is { } ground)
        {
            if (tool != EditorTool.Terrain) ground.CancelTerrainStroke();
            ground.TerrainMode = tool == EditorTool.Terrain;
            if (tool == EditorTool.Terrain && _viewport.Tool != EditorTool.Terrain) ShowTab("Inspector");
        }
        _viewport.Tool = tool;
        SelectTool.IsChecked = tool == EditorTool.Select; MoveTool.IsChecked = tool == EditorTool.Move;
        RotateTool.IsChecked = tool == EditorTool.Rotate; ScaleTool.IsChecked = tool == EditorTool.Scale; PlaceTool.IsChecked = tool == EditorTool.Place;
        RoadTool.IsChecked = tool == EditorTool.Road; ElementTool.IsChecked = tool == EditorTool.Element; TrafficTool.IsChecked = tool == EditorTool.Traffic;
        ElevatorTool.IsChecked = tool == EditorTool.Elevator; TerrainTool.IsChecked = tool == EditorTool.Terrain;
        if (tool == EditorTool.Road && Session?.SelectedRoad is null && _tab != "Add") ShowAddCategory("Roads");
        if (tool == EditorTool.Elevator && Session?.SelectedElevator is null && Session?.ArmedElevator is null && _tab != "Add") ShowAddCategory("Elevators");
    }

    private void OnToolClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<EditorTool>(tag, out var tool)) SetTool(tool);
    }

    internal void SetNavigation(NavigationMode mode)
    {
        if (_viewport is null) return;
        _viewport.Navigation = mode;
        FlyModeButton.IsChecked = mode == NavigationMode.Fly; WalkModeButton.IsChecked = mode == NavigationMode.Walk;
        SpeedSlider.IsEnabled = mode == NavigationMode.Fly;
        if (mode == NavigationMode.Walk) _viewport.SettleOnGround(true);
        SaveSettings();
    }

    private void OnFlyMode(object sender, RoutedEventArgs e) => SetNavigation(NavigationMode.Fly);
    private void OnWalkMode(object sender, RoutedEventArgs e) => SetNavigation(NavigationMode.Walk);

    // --- Field of view (Ctrl + wheel, toolbar, View menu) ------------------------------------

    private double _fieldOfViewToastUntil;

    private void SetFieldOfView(float degrees)
    {
        if (_viewport is not { } v) return;
        v.Session.Camera.FieldOfView = degrees;
        ShowFieldOfView();
    }

    private void ShowFieldOfView()
    {
        if (_viewport is not { } v) return;
        float fov = v.Session.Camera.FieldOfView;
        FieldOfViewLabel.Text = FieldOfViewToastText.Text = $"{fov:F0}°";
        FieldOfViewToast.Visibility = Visibility.Visible;
        _fieldOfViewToastUntil = Environment.TickCount64 / 1000.0 + 1.2;
        _settings.FieldOfView = fov;
    }

    private void OnFieldOfViewReset(object sender, RoutedEventArgs e) => SetFieldOfView(SceneCamera.DefaultFieldOfView);

    private void OnFieldOfViewMenu(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && float.TryParse(tag, System.Globalization.CultureInfo.InvariantCulture, out var degrees)) SetFieldOfView(degrees);
    }

    private void OnSpeedChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingToolbar || _viewport is null) return;
        _viewport.FlySpeed = SliderToSpeed(e.NewValue);
    }

    private void OnSpaceToggle(object sender, RoutedEventArgs e)
    {
        if (_viewport is null) return;
        _viewport.LocalSpace = LocalSpaceButton.IsChecked != true; SpaceLabel.Text = _viewport.LocalSpace ? Loc.T("Local") : Loc.T("World"); SaveSettings();
    }

    private void OnSnapToggle(object sender, RoutedEventArgs e) { if (_viewport is null) return; _viewport.Snap = SnapButton.IsChecked == true; SaveSettings(); }

    private void OnDistanceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingToolbar || Session is null || DistanceBox.SelectedIndex < 0) return;
        Session.SetViewDistance((ViewDistance)DistanceBox.SelectedIndex);
        ApplyFog();
    }

    private void OnWireframeToggle(object sender, RoutedEventArgs e) { if (_viewport is null) return; _viewport.Renderer.Wireframe = WireframeButton.IsChecked == true; WireframeMenu.IsChecked = _viewport.Renderer.Wireframe; }
    private void OnWireframeMenu(object sender, RoutedEventArgs e) { if (_viewport is null) return; _viewport.Renderer.Wireframe = WireframeMenu.IsChecked; WireframeButton.IsChecked = WireframeMenu.IsChecked; }
    private void OnAtmosphereToggle(object sender, RoutedEventArgs e) { if (_viewport is null) return; _viewport.Renderer.Atmosphere = AtmosphereButton.IsChecked == true; AtmosphereMenu.IsChecked = _viewport.Renderer.Atmosphere; SaveSettings(); }
    private void OnAtmosphereMenu(object sender, RoutedEventArgs e) { if (_viewport is null) return; _viewport.Renderer.Atmosphere = AtmosphereMenu.IsChecked; AtmosphereButton.IsChecked = AtmosphereMenu.IsChecked; SaveSettings(); }
    private void OnShadingMenu(object sender, RoutedEventArgs e)
    {
        if (_viewport is null || sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse<SceneShading>(tag, out var shading)) return;
        _viewport.Renderer.Shading = shading; SaveSettings(); SyncMaterialsChrome();
    }
    private void OnNightMenu(object sender, RoutedEventArgs e) => SetNight(NightMenu.IsChecked);
    private void OnGameFacesMenu(object sender, RoutedEventArgs e) { if (_viewport is null) return; _viewport.Renderer.GameFaces = GameFacesMenu.IsChecked; SaveSettings(); }
    private void OnNightSwitch(object sender, RoutedEventArgs e) => SetNight(NightSwitch.IsOn);
    private void SetNight(bool on)
    {
        if (_viewport is null) return;
        _viewport.Renderer.NightPreview = on;
        if (NightMenu.IsChecked != on) NightMenu.IsChecked = on;
        if (NightSwitch.IsOn != on) NightSwitch.IsOn = on;
    }
    private void OnSurfacesMenu(object sender, RoutedEventArgs e) { if (_viewport is not null) _viewport.Renderer.ShowSurfaces = SurfacesMenu.IsChecked; }
    private void OnDecalsMenu(object sender, RoutedEventArgs e) { if (_viewport is null) return; _viewport.Renderer.ShowDecals = DecalsMenu.IsChecked; SaveSettings(); }
    private void OnPlaceholderMenu(object sender, RoutedEventArgs e) { if (Session is not null) Session.ShowPlaceholders = PlaceholderMenu.IsChecked; }
    private void OnSelectionBoundsMenu(object sender, RoutedEventArgs e) { if (Session is not null) Session.ShowSelectionBounds = SelectionBoundsMenu.IsChecked; }
    private void OnPreflightMenu(object sender, RoutedEventArgs e)
    {
        if (Session is not { } s) return;
        s.Settings.Preflight = PreflightMenu.IsChecked; s.Settings.Save();
        if (!PreflightMenu.IsChecked) s.Preflight.Stop();
    }
    private void OnPrepareCity(object sender, RoutedEventArgs e) => Session?.PrepareWholeCity();
    private void OnDistantMenu(object sender, RoutedEventArgs e) { if (Session is not null) { Session.DistantCity = DistantMenu.IsChecked; ApplyFog(); } }
    // With distant proxies the fog only softens the horizon; without them it hides the streaming edge.
    private void ApplyFog()
    {
        if (Session is not { } s || _viewport is null) return;
        float near = s.Distance switch { ViewDistance.Near => 450, ViewDistance.Far => 1500, _ => 900 };
        _viewport.Renderer.FogDistance = s.DistantCity ? near * 4 : near;
    }
    private double _nextMemorySample, _nextIssues;
    private string _memoryText = "";

    private void OnIssuesExpanding(Expander sender, ExpanderExpandingEventArgs args) => RefreshIssues();
    private void RefreshIssues()
    {
        if (Session is not { } s) return;
        var lines = s.StreamErrors.Select(e => $"{(e.Key.Mesh ? "mesh  " : "sector")} {(e.Key.Mesh ? s.Index?.Find(e.Key.Hash)?.Path ?? e.Key.Hash.ToString() : e.Key.Hash.ToString())}\n       {e.Value}").Take(200).ToArray();
        IssuesExpander.Header = Loc.F("Loading issues ({0})", s.StreamErrors.Count);
        IssuesText.Text = lines.Length == 0 ? Loc.T("No issues.") : string.Join("\n", lines);
    }

    private void OnCollisionMenu(object sender, RoutedEventArgs e) { if (Session is not null) Session.ShowCollision = CollisionMenu.IsChecked; }
    private void OnMarkersMenu(object sender, RoutedEventArgs e) { if (Session is not null) { Session.ShowMarkers = MarkersMenu.IsChecked; _settings.Save(); } }
    private void OnDoorIconsMenu(object sender, RoutedEventArgs e) { if (Session is not null) { Session.ShowDoorIcons = DoorIconsMenu.IsChecked; _settings.Save(); } }

    private void OnIconRangeMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || !float.TryParse(tag, System.Globalization.CultureInfo.InvariantCulture, out var range)) return;
        _settings.IconRange = range; _settings.Save();
        if (Session is { } s) s.Status = Loc.F("The game's icons (lights, effects, sounds, doors) show within {0:0} m.", range);
    }

    // The icons of the project's own elements: shown or not, and how far.
    private void OnProjectIconsMenu(object sender, RoutedEventArgs e) => SetProjectIcons(ProjectIconsMenu.IsChecked, null);
    private void OnProjectIconRangeMenu(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && float.TryParse(tag, System.Globalization.CultureInfo.InvariantCulture, out var range)) SetProjectIcons(null, range);
    }

    internal void SetProjectIcons(bool? shown, float? range)
    {
        if (Session is not { } s) return;
        if (shown is { } on) s.ShowProjectIcons = on;
        if (range is { } metres) s.ProjectIconRange = metres;
        _settings.Save(); SyncIconMenus();
        s.Status = s.ShowProjectIcons ? Loc.F("Icons of your project's elements show within {0:0} m.", s.ProjectIconRange)
            : Loc.T("Icons of your project's elements hidden: the selected ones still show, and the Elements tool shows them all.");
    }

    private void SyncIconMenus()
    {
        MarkersMenu.IsChecked = _settings.ShowMarkerIcons; DoorIconsMenu.IsChecked = _settings.ShowDoorIcons;
        ProjectIconsMenu.IsChecked = _settings.ShowProjectIcons;
        foreach (var item in new[] { ProjectIconRange10, ProjectIconRange25, ProjectIconRange50, ProjectIconRange75, ProjectIconRange150 })
            item.IsChecked = float.TryParse(item.Tag as string, System.Globalization.CultureInfo.InvariantCulture, out var p) && MathF.Abs(p - _settings.ProjectIconRange) < .5f;
        PlayEffectsMenu.IsChecked = _settings.PlayEffects;
        foreach (var item in new[] { IconRange25, IconRange50, IconRange100, IconRange200 })
            item.IsChecked = float.TryParse(item.Tag as string, System.Globalization.CultureInfo.InvariantCulture, out var r) && MathF.Abs(r - _settings.IconRange) < .5f;
    }
    private void OnSectorBoundsMenu(object sender, RoutedEventArgs e) { if (Session is not null) Session.ShowSectorBounds = SectorBoundsMenu.IsChecked; }

    // --- Floating toolbar --------------------------------------------------------------------

    private bool _toolbarDragging;
    private Windows.Foundation.Point _toolbarGrab;

    private void InitializeToolbarDrag()
    {
        ToolbarGrip.PointerPressed += (_, e) =>
        {
            _toolbarDragging = ToolbarGrip.CapturePointer(e.Pointer);
            var p = e.GetCurrentPoint(ToolbarCanvas).Position;
            _toolbarGrab = new(p.X - Canvas.GetLeft(FloatingToolbar), p.Y - Canvas.GetTop(FloatingToolbar));
            e.Handled = true;
        };
        ToolbarGrip.PointerMoved += (_, e) =>
        {
            if (!_toolbarDragging) return;
            var p = e.GetCurrentPoint(ToolbarCanvas).Position;
            MoveToolbar(p.X - _toolbarGrab.X, p.Y - _toolbarGrab.Y); e.Handled = true;
        };
        ToolbarGrip.PointerReleased += (_, e) =>
        {
            _toolbarDragging = false; ToolbarGrip.ReleasePointerCapture(e.Pointer);
            _settings.ToolbarX = Canvas.GetLeft(FloatingToolbar); _settings.ToolbarY = Canvas.GetTop(FloatingToolbar); _settings.Save();
        };
    }

    private void PositionToolbar()
    {
        FloatingToolbar.UpdateLayout();
        double x = double.IsNaN(_settings.ToolbarX) ? (ToolbarCanvas.ActualWidth - FloatingToolbar.ActualWidth) / 2 : _settings.ToolbarX;
        MoveToolbar(x, double.IsNaN(_settings.ToolbarY) ? 14 : _settings.ToolbarY);
    }

    private void MoveToolbar(double x, double y)
    {
        double maxX = Math.Max(0, ToolbarCanvas.ActualWidth - FloatingToolbar.ActualWidth - 6), maxY = Math.Max(0, ToolbarCanvas.ActualHeight - FloatingToolbar.ActualHeight - 6);
        y = Math.Clamp(y, 6, Math.Max(6, maxY));
        double minX = ToolbarMinimumX(y);
        Canvas.SetLeft(FloatingToolbar, Math.Clamp(x, minX, Math.Max(minX, maxX)));
        Canvas.SetTop(FloatingToolbar, y);
    }

    // Beside the toolbox (top left) the toolbar starts right of it; below it, anywhere.
    private double ToolbarMinimumX(double y)
    {
        if (ToolBox.ActualWidth <= 0) return 6;
        var origin = ToolBox.TransformToVisual(ToolbarCanvas).TransformPoint(new(0, 0));
        bool beside = y < origin.Y + ToolBox.ActualHeight && y + FloatingToolbar.ActualHeight > origin.Y;
        return beside ? origin.X + ToolBox.ActualWidth + 6 : 6;
    }

    private void OnResetToolbar(object sender, RoutedEventArgs e) { _settings.ToolbarX = double.NaN; _settings.ToolbarY = 14; PositionToolbar(); _settings.Save(); }

    // Horizontal or vertical: the grip's menu, a double-click on the grip, or Window ▸ Vertical floating toolbar.
    private void OnToolbarOrientation(object sender, RoutedEventArgs e) => SetToolbarVertical(sender is FrameworkElement { Tag: "Vertical" });
    private void OnToolbarGripDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) { SetToolbarVertical(!_settings.ToolbarVertical); e.Handled = true; }
    private void OnToolbarVerticalMenu(object sender, RoutedEventArgs e) => SetToolbarVertical(ToolbarVerticalMenu.IsChecked);

    internal void SetToolbarVertical(bool vertical)
    {
        _settings.ToolbarVertical = vertical; _settings.Save();
        ApplyToolbarOrientation();
    }

    // Vertical: one item per row, buttons as wide as the toolbar, separators across, the grip on top.
    private void ApplyToolbarOrientation()
    {
        bool vertical = _settings.ToolbarVertical;
        ToolbarItems.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        ToolbarItems.Spacing = vertical ? 2 : 4;
        ToolbarGrip.Width = vertical ? double.NaN : 18; ToolbarGrip.Height = vertical ? 16 : double.NaN;
        ToolbarGripIcon.Glyph = vertical ? "\uE76F" : "\uE784";
        foreach (var child in ToolbarItems.Children)
        {
            if (child is Microsoft.UI.Xaml.Shapes.Rectangle line)
            {
                line.Width = vertical ? double.NaN : 1; line.Height = vertical ? 1 : 20;
                line.Margin = vertical ? new Thickness(4, 3, 4, 3) : new Thickness(4, 0, 4, 0);
                line.HorizontalAlignment = vertical ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
            }
            else if (child is Control control && child != ToolbarGrip)
            {
                control.HorizontalAlignment = vertical ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
                control.HorizontalContentAlignment = vertical ? HorizontalAlignment.Left : HorizontalAlignment.Center;
            }
        }
        DistanceBox.Width = vertical ? double.NaN : 136;
        ToolbarHorizontalItem.IsChecked = !vertical; ToolbarVerticalItem.IsChecked = vertical; ToolbarVerticalMenu.IsChecked = vertical;
        PositionToolbar();
    }

    // --- Lifetime -------------------------------------------------------------------------------

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeReady) return;
        args.Cancel = true;
        RequestClose();
    }

    internal async void RequestClose()
    {
        if (_closing) return;
        _closing = true;
        SaveSettings();
        DisposeAudio();
        DisposeEffectHover();
        _automation?.Dispose();
        _library?.Close(); _library = null;
        await DisposeLiveAsync();
        if (Session is { } session) await session.ShutdownAsync();
        AddGrid.Release();
        _thumbnails?.Dispose(); _thumbnails = null;
        _viewport?.Dispose(); _viewport = null;
        _closeReady = true;
        CrashReport.End();
        Close();
    }

    private void OnExit(object sender, RoutedEventArgs e) => RequestClose();

    // The logs a bug report needs (exceptions.log, last-crash-studio.txt, last-crash-windows.txt).
    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        string folder = NCWE.Studio.Session.StudioSettings.StateDirectory;
        string? select = File.Exists(CrashReport.ReportPath) ? CrashReport.ReportPath : File.Exists(Path.Combine(folder, "exceptions.log")) ? Path.Combine(folder, "exceptions.log") : null;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", select is null ? $"\"{folder}\"" : $"/select,\"{select}\"") { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private void OnCrashNoticeDismiss(object sender, RoutedEventArgs e) => HomeCrashNotice.Visibility = Visibility.Collapsed;

    private async void OnRecordDumps(object sender, RoutedEventArgs e)
    {
        if (Session is not { } s) return;
        if (CrashReport.DumpsEnabled()) { s.Status = Loc.F("Crash dumps are on: they go to {0}", CrashReport.DumpFolder); return; }
        s.Status = Loc.T("Waiting for the administrator prompt…");
        bool on = await Task.Run(CrashReport.EnableDumps);
        s.Status = on ? Loc.F("Crash dumps are on: if NCWE crashes, Windows keeps a dump in {0} (listed in last-crash-windows.txt).", CrashReport.DumpFolder)
            : Loc.T("Crash dumps were not turned on (the administrator prompt was declined).");
    }

    // The graphics driver reset (or the GPU went away): the view cannot come back in this run.
    private async Task ShowDeviceLostAsync()
    {
        if (_closing || _viewport?.DeviceLost is not { } reason) return;
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = Loc.T("The graphics driver stopped responding"),
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, MaxWidth = 460,
                Text = Loc.F("Windows reset the graphics driver ({0}), so the 3D view cannot continue. Your project was autosaved: restart NCWE, then use File ▸ Recover autosave if your last changes were not saved.\n\nIf it happens again, update the graphics driver and send the files of Help ▸ Open the logs folder.", reason)
            },
            PrimaryButtonText = Loc.T("Restart NCWE"), CloseButtonText = Loc.T("Not now"), DefaultButton = ContentDialogButton.Primary
        };
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            foreach (var argument in App.Arguments) start.ArgumentList.Add(argument);
            System.Diagnostics.Process.Start(start);
            RequestClose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { App.WriteCrash(ex); }
    }
}


