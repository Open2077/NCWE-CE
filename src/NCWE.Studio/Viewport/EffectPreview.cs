// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NCWE.Core;
using NCWE.Core.Fx;
using NCWE.Formats;
using NCWE.Render;

namespace NCWE.Studio.Viewport;

// A small viewer that plays one particle system or effect of the game by itself (the library's
// effects page): its own device, like the mesh viewer, and the same simulation and particle pass
// as the 3D view. The camera turns around what the effect covers; dragging orbits, the wheel zooms.
internal sealed class EffectPreview : IDisposable
{
    private readonly SwapChainPanel _panel;
    private readonly D3DRenderer _renderer;
    private readonly GameArchiveIndex _index;
    private readonly SceneCamera _camera = new() { Pitch = .12f, Yaw = .6f };
    private readonly Dictionary<(FxEmitterInstance Emitter, int Mesh), ParticleBatch> _cache = [];
    private readonly List<ParticleBatch> _batches = [];
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private FxPlayback? _playback;
    private CancellationTokenSource? _loading;
    private bool _dragging;
    private Windows.Foundation.Point _last;
    private double _lastTime;

    public bool Active { get; set; }
    public bool Paused { get; set; }
    public float Spin { get; set; } = .15f;
    // How close the camera frames what the effect covers (1: the page's viewer; more for a small card).
    public float Closeness { get; set; } = 1;
    public string Path { get; private set; } = "";
    public string Info { get; private set; } = "";
    public int Particles { get; private set; }
    public bool Failed { get; private set; }
    public event Action? InfoChanged;
    // A frame of what Show asked for has reached the panel (a card shows the panel from then on).
    public event Action? Presented;
    // How many of its emitters are drawn, of how many (0 of 0 while loading or unread).
    public (int Shown, int Total) Emitters { get; private set; }
    // Called with each sound event the effect plays.
    public Action<string>? Sound { get; set; }
    // The effect or system shown (for the page's details).
    public FxEffect? Effect { get; private set; }
    public FxSystem? SystemShown { get; private set; }

    public EffectPreview(SwapChainPanel panel, GameArchiveIndex index)
    {
        _panel = panel; _index = index;
        _renderer = D3DRenderer.ForComposition(Math.Max(1, (int)panel.ActualWidth), Math.Max(1, (int)panel.ActualHeight), 4);
        _renderer.MaterialSource = new ArchiveMaterialSource(index);
        _renderer.ParticleMeshSource = async hash => index.Find(hash) is null ? null : await index.ReadMeshConcurrentAsync(hash, default, 64L << 20);
        SwapChainPanels.Set(_panel, _renderer.SwapChain.NativePointer);
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

    public async void Show(string path)
    {
        _loading?.Cancel(); var cancel = _loading = new();
        Path = path; _playback = null; Effect = null; SystemShown = null; Emitters = default; _cache.Clear(); _batches.Clear(); _renderer.Particles = [];
        Info = Loc.F("Loading {0}…", System.IO.Path.GetFileName(path)); InfoChanged?.Invoke();
        try
        {
            var index = _index;
            var (effect, system, reach) = await Task.Run(() =>
            {
                FxEffect? e = null; FxSystem? s = null;
                if (path.EndsWith(".effect", StringComparison.OrdinalIgnoreCase))
                {
                    e = index.ReadEffect(path);
                    if (e is not null) foreach (var item in e.Particles) index.ReadParticleSystem(item.System);
                }
                else s = index.ReadParticleSystem(path);
                // What it covers, from a first run that nobody sees.
                var trial = e is not null ? new FxPlayback(e, p => index.ReadParticleSystem(p), 7) { Replay = false } : s is not null ? new FxPlayback(s, 7) { Replay = false } : null;
                return (e, s, trial is null ? default : Reach(trial));
            }, cancel.Token);
            if (cancel.IsCancellationRequested) return;
            Effect = effect; SystemShown = system;
            var systems = effect is not null ? effect.Particles.Select(p => index.ReadParticleSystem(p.System)).OfType<FxSystem>().ToList() : system is not null ? [system] : [];
            Emitters = (systems.Sum(s => s.Emitters.Count(e => e.Drawable)), systems.Sum(s => s.Emitters.Length));
            if (effect is null && system is null) { Info = Loc.T("No preview: it could not be read."); InfoChanged?.Invoke(); return; }
            _playback = effect is not null ? new FxPlayback(effect, p => index.ReadParticleSystem(p), 3) : new FxPlayback(system!, 3);
            var (min, max) = reach;
            if (max.X < min.X) { min = new(-1, 0, -1); max = new(1, 2, 1); }
            _camera.Frame(new Bounds3(min, max));
            _camera.Distance = Math.Clamp((max.X - min.X) * 1.9f / Math.Max(.25f, Closeness), .6f, 400);
            Info = Describe(effect, system, index); InfoChanged?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Info = Loc.F("No preview: {0}", ex.Message); InfoChanged?.Invoke(); }
    }

    // The box (view space) that holds what is seen of it: where its particles are over its first
    // seconds, weighed by how much each shows (area and opacity), so that a few sparks flying far
    // do not shrink the fire they come from.
    private static (Vector3 Min, Vector3 Max) Reach(FxPlayback playback)
    {
        var samples = new List<(Vector3 At, float Weight, float Size)>();
        var camera = new Vector3(0, -30, 2);
        for (int step = 0; step < 150; step++)
        {
            playback.Step(1f / 30, Matrix4x4.Identity, camera);
            if (step % 3 != 0) continue;
            foreach (var system in playback.Systems)
                foreach (var emitter in system.Emitters)
                {
                    if (!emitter.Emitter.Drawable) continue;
                    for (int i = 0; i < emitter.Count; i++)
                    {
                        ref var p = ref emitter.Particles[i];
                        var at = Coordinates.GameToView(emitter.WorldPosition(p, Matrix4x4.Identity));
                        float size = MathF.Min(MathF.Max(MathF.Abs(p.Size.X), MathF.Abs(p.Size.Y)), 40);
                        if (!float.IsFinite(at.X + at.Y + at.Z) || at.Length() > 500 || size <= 0) continue;
                        samples.Add((at, size * size * Math.Clamp(p.Alpha, .05f, 1), size));
                    }
                }
        }
        if (samples.Count == 0) return (new(float.MaxValue), new(float.MinValue));
        float total = samples.Sum(x => x.Weight);
        var centre = Vector3.Zero; float mean = 0;
        foreach (var (at, weight, size) in samples) { centre += at * (weight / total); mean += size * (weight / total); }
        float spread = 0;
        foreach (var (at, weight, _) in samples) spread += Vector3.DistanceSquared(at, centre) * (weight / total);
        float radius = MathF.Max(MathF.Sqrt(spread) * 1.6f + mean, .25f);
        return (centre - new Vector3(radius), centre + new Vector3(radius));
    }

    private static string Describe(FxEffect? effect, FxSystem? system, GameArchiveIndex index)
    {
        if (system is not null) return Loc.F("Particle system · {0}{1}", FxBatches.Coverage(system), system.OneShot ? Loc.T(" · plays once (shown again and again)") : Loc.T(" · loops"));
        var systems = effect!.Particles.Select(p => index.ReadParticleSystem(p.System)).OfType<FxSystem>().ToList();
        int emitters = systems.Sum(s => s.Emitters.Length), drawn = systems.Sum(s => s.Emitters.Count(e => e.Drawable));
        return Loc.F("Effect · {0:0.#} s{1} · {2} particle systems, {3} of {4} emitters shown{5}{6}{7}", effect.Length, effect.Loop is not null ? Loc.T(", loops") : "", effect.Particles.Length, drawn, emitters,
            effect.Sounds.Length > 0 ? Loc.F(" · sounds: {0}", string.Join(", ", effect.Sounds.Select(s => s.Event).Distinct().Take(4))) : "",
            effect.Lights.Length > 0 ? Loc.F(" · {0} lights", effect.Lights.Length) : "", effect.Skipped.Length > 0 ? Loc.F(" · not shown: {0}", string.Join(", ", effect.Skipped)) : "");
    }

    public void Restart() { _playback?.Restart(); _cache.Clear(); }
    public void Clear() { _loading?.Cancel(); _playback = null; Effect = null; SystemShown = null; Emitters = default; Path = ""; _cache.Clear(); _batches.Clear(); _renderer.Particles = []; Info = ""; InfoChanged?.Invoke(); }

    // An exception escaping a CompositionTarget.Rendering handler ends the process: a frame that fails stops the preview.
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
            if (!_dragging && !Paused) _camera.Orbit(dt * Spin, 0);
            _batches.Clear(); int particles = 0;
            if (_playback is { } playback)
            {
                if (!Paused) playback.Step(dt, Matrix4x4.Identity, Coordinates.ViewToGame(_camera.Eye), Sound);
                FxBatches.Collect(playback, _cache, Matrix4x4.Identity, _batches, ref particles, 40000);
            }
            Particles = particles;
            _renderer.Particles = _batches;
            _renderer.ResizeScene(w, h);
            _renderer.RenderScene(_camera, 1, []);
            _renderer.PresentSceneAsBackground();
            _renderer.Present();
            Presented?.Invoke();
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
        App.TraceLine($"Effect preview stopped: {_renderer.RemovedReason() ?? ex.GetType().Name + ": " + ex.Message}");
        App.WriteCrash(ex);
        Info = Loc.T("No preview: the 3D preview stopped (see exceptions.log).");
        try { InfoChanged?.Invoke(); } catch (Exception handler) when (handler is not OutOfMemoryException) { App.WriteCrash(handler); }
    }

    // The frame as pixels (automation: checking what the page shows).
    public (byte[] Pixels, int Width, int Height)? Capture() { try { return _renderer.Capture(sceneOnly: true); } catch (Exception) { return null; } }
    public object Diagnostic() => new { path = Path, particles = Particles, info = Info, failed = Failed, renderer = _renderer.ParticleDiagnostic() };

    public void Dispose()
    {
        CompositionTarget.Rendering -= OnRendering;
        _loading?.Cancel();
        SwapChainPanels.Detach(_panel);
        _renderer.Dispose();
    }
}
