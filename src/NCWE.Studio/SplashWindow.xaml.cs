// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using NCWE.Studio.Viewport;
using Windows.Graphics;

namespace NCWE.Studio;

// Startup splash: shown first, borderless and on top; the main window starts behind it once the
// splash has drawn a frame with its logos (building the main window keeps the interface thread
// busy for seconds: images decoded after that would never show). It closes once the main
// window has drawn its first frame (at least MinimumSeconds after it appeared, at most
// MaximumSeconds), or on a click.
public sealed partial class SplashWindow : Window
{
    private const double MinimumSeconds = 2.2, MaximumSeconds = 12;
    private readonly DispatcherQueueTimer _timer;
    private readonly long _shownAt = Environment.TickCount64;
    private MainWindow? _main;
    private bool _closed, _painted, _started;
    // Logos still decoding (each reports opened or failed).
    private int _logosPending;

    // The splash has been drawn once: the main window can start.
    public event Action? Painted;

    public SplashWindow()
    {
        InitializeComponent();
        Brand.SetIcon(this);
        Loc.Translate(this);
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false; presenter.IsMaximizable = false; presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
            presenter.SetBorderAndTitleBar(false, false);
        }
        AppWindow.IsShownInSwitchers = false;
        double scale = NativeInput.Dpi(hwnd);
        var area = DisplayArea.Primary.WorkArea;
        int width = (int)(640 * scale), height = (int)(360 * scale);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
        // Windows 11 rounds borderless windows only when asked (DWMWA_WINDOW_CORNER_PREFERENCE = round).
        int round = 2; _ = DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));

        BuildText.Text = Brand.BuildLabel;
        // A click anywhere but on the link closes it (the link handles its own click).
        Root.PointerReleased += (_, _) => Dismiss();
        foreach (var logo in (Microsoft.UI.Xaml.Controls.Image[])[NcweLogo, Logo])
        {
            _logosPending++;
            logo.ImageOpened += (_, _) => _logosPending--;
            logo.ImageFailed += (_, _) => _logosPending--;
        }
        CompositionTarget.Rendering += OnRendering;
        // Safety net: the main window starts even if no splash frame (or logo) is ever reported.
        var fallback = DispatcherQueue.CreateTimer();
        fallback.Interval = TimeSpan.FromSeconds(2); fallback.IsRepeating = false;
        fallback.OnTick(Start);
        fallback.Start();
        App.SplashState = "shown";
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(150);
        _timer.OnTick(Tick);
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    private void OnRendering(object? sender, object e)
    {
        // The first frame with the logos decoded.
        if (_painted || _logosPending > 0) return;
        _painted = true;
        CompositionTarget.Rendering -= OnRendering;
        // One more turn of the queue so the first frame is presented before the heavier start.
        DispatcherQueue.Post(DispatcherQueuePriority.Low, Start);
    }

    private void Start()
    {
        if (_started) return;
        _started = true;
        App.SplashState = "painted";
        App.SplashLogos = _logosPending == 0;
        Painted?.Invoke();
    }

    // Follows the main window's start (status line) until it is ready.
    public void Follow(MainWindow main)
    {
        _main = main;
        _timer.Start();
    }

    private void Tick()
    {
        if (_closed || _main is null) return;
        double age = (Environment.TickCount64 - _shownAt) / 1000.0;
        StatusText.Text = _main.StartupStatus;
        if ((age >= MinimumSeconds && _main.HasRendered) || age >= MaximumSeconds) Dismiss();
    }

    private void OnSite(object sender, RoutedEventArgs e) => Brand.OpenOpen77Site();

    // Automation checks: beyond every monitor, not on top, never closing by itself.
    internal void ShowOffscreen()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = false;
        App.ShowQuietly(this);
    }

    private void Dismiss()
    {
        if (_closed) return;
        Start();   // closed before its first frame: the main window still starts
        _closed = true;
        _timer.Stop();
        CompositionTarget.Rendering -= OnRendering;
        Close();
        App.SplashState = "closed";
        if (!App.Background) _main?.Activate();
    }
}
