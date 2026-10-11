// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NCWE.Core;
using NCWE.Formats;
using NCWE.Render;

namespace NCWE.Studio.Viewport;

// A small, independent mesh viewer for the asset browser. It owns its own device so
// it never competes with the world renderer's state, and only renders while visible.
internal sealed class AssetPreview : IDisposable
{
    private readonly SwapChainPanel _panel;
    private readonly D3DRenderer _renderer;
    private readonly SceneCamera _camera = new() { Pitch = .35f };
    private MeshAsset? _mesh;
    private CancellationTokenSource? _loading;
    private bool _dragging;
    private Windows.Foundation.Point _last;
    private double _lastTime;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    public bool Active { get; set; }
    // Road types: pieces cooked for bending are shown with their baked material surfaces.
    public bool Surfaces { get; set; }
    public string Info { get; private set; } = "";
    public event Action? InfoChanged;
    // The preview stopped for good (its device was lost, or a frame failed): its owner makes a new one.
    public bool Failed { get; private set; }

    public AssetPreview(SwapChainPanel panel)
    {
        _panel = panel;
        _renderer = D3DRenderer.ForComposition(Math.Max(1, (int)panel.ActualWidth), Math.Max(1, (int)panel.ActualHeight), 4);
        Attach();
        _panel.PointerPressed += (_, e) => { _dragging = _panel.CapturePointer(e.Pointer); _last = e.GetCurrentPoint(_panel).Position; };
        _panel.PointerMoved += (_, e) =>
        {
            if (!_dragging) return;
            var p = e.GetCurrentPoint(_panel).Position;
            _camera.Orbit(-(float)(p.X - _last.X) * .01f, (float)(p.Y - _last.Y) * .01f); _last = p;
        };
        _panel.PointerReleased += (_, e) => { _dragging = false; _panel.ReleasePointerCapture(e.Pointer); };
        _panel.PointerWheelChanged += (_, e) => _camera.Zoom(e.GetCurrentPoint(_panel).Properties.MouseWheelDelta / 120f);
        CompositionTarget.Rendering += OnRendering;
    }

    private void Attach() => SwapChainPanels.Set(_panel, _renderer.SwapChain.NativePointer);

    public async void Show(GameArchiveIndex index, ArchiveResource resource)
    {
        _loading?.Cancel(); var cancel = _loading = new();
        Info = Loc.F("Loading {0}…", Path.GetFileName(resource.Path)); InfoChanged?.Invoke();
        try
        {
            var mesh = Surfaces && RoadBending.AxisOf(resource.Path) == RoadBending.Axis.Y ? await index.ReadSurfacedMeshAsync(resource.Hash, 1024, cancel.Token)
                : await index.ReadMeshConcurrentAsync(resource.Hash, cancel.Token, 96L << 20, 128L << 20);
            if (cancel.IsCancellationRequested) return;
            _mesh = mesh; _renderer.SetMesh(mesh); _camera.Frame(mesh.Bounds); _camera.Distance = _camera.Radius * 2.1f;
            Info = Loc.F("{0:N0} triangles · {1} submeshes · LODs {2} · {3:F1} × {4:F1} × {5:F1} m{6}", mesh.TriangleCount, mesh.Parts.Length, string.Join(",", mesh.LodMasks), mesh.Bounds.Size.X, mesh.Bounds.Size.Z, mesh.Bounds.Size.Y, (mesh.PartTextures is { } surfaces ? Loc.F(" · {0} textured parts", surfaces.Count(s => s is not null)) : ""));
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { _mesh = null; _renderer.SetMesh(null); Info = Loc.F("No preview: {0}", ex.Message); }
        InfoChanged?.Invoke();
    }

    public MeshAsset? Mesh => _mesh;
    // Rotation speed while idle (radians per second); 0 stops it.
    public float Spin { get; set; } = .35f;

    // Library: a prop or an entity appearance, shown at once untextured, then with its baked
    // material surfaces when ready.
    // quick: an untextured first look while the materials bake; yaw/pitch: the starting view.
    public async void ShowItem(GameArchiveIndex index, ArchiveResource resource, bool entity, string appearance, int surfaceSize = 1024, bool quick = true, float? yaw = null, float? pitch = null)
    {
        if (yaw is { } y) _camera.Yaw = y;
        if (pitch is { } p) _camera.Pitch = p;
        _nextPitch = pitch ?? .35f;
        _loading?.Cancel(); var cancel = _loading = new();
        Info = Loc.F("Loading {0}…", Path.GetFileNameWithoutExtension(resource.Path)); InfoChanged?.Invoke();
        bool shown = false;
        try
        {
            if (quick)
            {
                var first = entity ? await index.ReadEntityMeshAsync(resource.Hash, appearance, false, 0, cancel.Token)
                    : await index.ReadMeshConcurrentAsync(resource.Hash, cancel.Token, 96L << 20, 128L << 20);
                if (cancel.IsCancellationRequested) return;
                Present(first, keepCamera: false, textured: false); shown = true;
            }
            var detailed = entity ? await index.ReadEntityMeshAsync(resource.Hash, appearance, true, surfaceSize, cancel.Token)
                : await index.ReadSurfacedMeshAsync(resource.Hash, surfaceSize, cancel.Token);
            if (cancel.IsCancellationRequested) return;
            if (!shown || detailed.PartTextures is not null) Present(detailed, keepCamera: shown, textured: detailed.PartTextures is not null);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (!shown && !cancel.IsCancellationRequested) { _mesh = null; _renderer.SetMesh(null); Info = Loc.F("No preview: {0}", ex.Message); InfoChanged?.Invoke(); }
        catch (Exception) { }
    }

    public void Clear() { _loading?.Cancel(); _mesh = null; _renderer.SetMesh(null); Info = ""; InfoChanged?.Invoke(); }

    private void Present(MeshAsset mesh, bool keepCamera, bool textured)
    {
        _mesh = mesh; _renderer.SetMesh(mesh);
        if (!keepCamera) { _camera.Pitch = _nextPitch; _camera.Fit(mesh.Bounds, (float)Math.Max(.2, _panel.ActualWidth / Math.Max(1, _panel.ActualHeight)), anyYaw: true, margin: 1.06f); }
        Info = Loc.F("{0:N0} triangles · {1} parts · {2:F2} × {3:F2} × {4:F2} m{5}", mesh.TriangleCount, mesh.Parts.Length, mesh.Bounds.Size.X, mesh.Bounds.Size.Z, mesh.Bounds.Size.Y, (textured ? Loc.T(" · textured") : ""));
        InfoChanged?.Invoke(); _announce = true;
    }

    private float _nextPitch = .35f;
    private bool _announce;
    // A new mesh has been drawn and presented (hover previews reveal themselves then).
    public event Action? Presented;

    // An exception escaping a CompositionTarget.Rendering handler ends the process (a stowed
    // exception in Microsoft.UI.Xaml.dll, no managed trace): a frame that fails stops the preview.
    private void OnRendering(object? sender, object e)
    {
        try { Frame(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Fail(ex); }
    }

    private void Frame()
    {
        if (Failed) return;
        double now = _clock.Elapsed.TotalSeconds; float dt = (float)Math.Min(.1, now - _lastTime); _lastTime = now;
        if (!Active || _panel.ActualWidth < 2 || _panel.ActualHeight < 2) return;
        float sx = _panel.CompositionScaleX, sy = _panel.CompositionScaleY;
        int w = (int)Math.Ceiling(_panel.ActualWidth * sx), h = (int)Math.Ceiling(_panel.ActualHeight * sy);
        try
        {
            if (w != _renderer.Width || h != _renderer.Height)
            {
                _renderer.Resize(w, h);
                using var swap2 = _renderer.SwapChain.QueryInterfaceOrNull<Vortice.DXGI.IDXGISwapChain2>();
                if (swap2 is not null) swap2.MatrixTransform = new Matrix3x2(1 / sx, 0, 0, 1 / sy, 0, 0);
            }
            if (!_dragging && _mesh is not null) _camera.Orbit(dt * Spin, 0);
            _renderer.ResizeScene(w, h);
            uint lod = _mesh?.LodMasks.FirstOrDefault() ?? 1;
            _renderer.RenderScene(_camera, lod, []);
            _renderer.PresentSceneAsBackground();
            _renderer.Present();
            if (_announce && _mesh is not null) { _announce = false; Presented?.Invoke(); }
        }
        // A lost device never comes back: stop; any other Direct3D hiccup skips the frame.
        catch (SharpGen.Runtime.SharpGenException ex) when (_renderer.RemovedReason() is null) { _ = ex; }
    }

    private void Fail(Exception ex)
    {
        if (Failed) return;
        Failed = true; Active = false;
        CompositionTarget.Rendering -= OnRendering;
        SwapChainPanels.Detach(_panel);
        App.TraceLine($"Asset preview stopped: {_renderer.RemovedReason() ?? ex.GetType().Name + ": " + ex.Message}");
        App.WriteCrash(ex);
        Info = Loc.T("No preview: the 3D preview stopped (see exceptions.log).");
        try { InfoChanged?.Invoke(); } catch (Exception handler) when (handler is not OutOfMemoryException) { App.WriteCrash(handler); }
    }

    public void Dispose()
    {
        CompositionTarget.Rendering -= OnRendering;
        _loading?.Cancel();
        // The panel lets go of the swap chain before its device goes.
        SwapChainPanels.Detach(_panel);
        _renderer.Dispose();
    }
}
