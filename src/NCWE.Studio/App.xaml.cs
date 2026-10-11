// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using Microsoft.UI.Xaml;
using NCWE.Studio.Session;

namespace NCWE.Studio;

public partial class App : Application
{
    private MainWindow? _window;
    internal static string[] Arguments { get; private set; } = [];

    public App()
    {
        // A wait on this (UI) thread must never pump messages into XAML (NonPumpingSynchronizationContext).
        NCWE.Core.NonPumpingSynchronizationContext.InstallOnCurrentThread();
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        Arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (Value("--state-dir") is { } state) StudioSettings.StateDirectory = Path.GetFullPath(state);
        Directory.CreateDirectory(StudioSettings.StateDirectory);
        // The interface language (Help ▸ Language; --lang or NCWE_LANGUAGE override it, as tests do).
        Loc.Initialize(Value("--lang") ?? Environment.GetEnvironmentVariable("NCWE_LANGUAGE") ?? StudioSettings.Load().Language);
        // Dialogs and popups follow it too (font fallback), and the 3D overlay loads a Japanese font when needed.
        // Harmless when Windows refuses it (E_INVALIDARG, E_FAIL: an unpackaged app often cannot set
        // it): NCWE's own texts are translated anyway; only built-in control texts keep the Windows
        // language. English needs no override.
        try { if (Loc.Code != "en") Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = Loc.Languages.First(l => l.Code == Loc.Code).Culture; }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or TypeLoadException) { TraceLine($"Note: Windows kept its own language for built-in controls (0x{ex.HResult:x8}); NCWE's interface is in '{Loc.Code}' all the same."); }
        NCWE.Render.ImGuiRenderer.Script = Loc.Code;
        CrashReport.Begin();
        TraceLine($"=== NCWE Studio started {DateTime.Now:yyyy-MM-dd HH:mm:ss} (build {BuildStamp()}, PID {Environment.ProcessId}), {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1L << 30)} GB RAM{(CrashReport.PreviousExitUnclean ? ", the previous run ended unexpectedly" : "")} ===");
        // Streaming churns large arrays; prefer background gen2 collections to blocking ones.
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        NCWE.Studio.Session.AllocationTrace.StartIfRequested();
        Heartbeat.Start();
        UnhandledException += (_, e) => WriteCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrash(e.ExceptionObject as Exception);
        // Fail-fast paths (stowed XAML exceptions) skip both handlers above: keep a trail of
        // unexpected first-chance exceptions so a crash can still be explained afterwards.
        AppDomain.CurrentDomain.FirstChanceException += (_, e) => TraceException(e.Exception);
        InitializeComponent();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    internal static string? Value(string key)
    {
        int i = Array.IndexOf(Arguments, key);
        return i >= 0 && i + 1 < Arguments.Length ? Arguments[i + 1] : null;
    }

    [ThreadStatic] private static bool _tracing;
    private static readonly object TraceGate = new();
    // The same exception (type and message) is written once a minute with a count: streaming can
    // throw the same handled one thousands of times, and each trace walks the stack with line numbers.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> TraceSeen = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> TraceRepeats = new();
    private static void TraceException(Exception e)
    {
        // Expected, handled conditions (cache misses, cancellations, parse probes) are not traced.
        if (_tracing || e is IOException or OperationCanceledException or UnauthorizedAccessException or System.Text.Json.JsonException
            or FormatException or System.Threading.ThreadInterruptedException
            || e.GetType().Name == "InvalidRTTIException") return; // WolvenKit RTTI mismatches are tolerated by the sector reader
        string key = e.GetType().FullName + ": " + e.Message;
        long now = Environment.TickCount64;
        if (TraceSeen.TryGetValue(key, out long last) && now - last < 60_000) { TraceRepeats.AddOrUpdate(key, 1, (_, n) => n + 1); return; }
        if (TraceSeen.Count > 4096) { TraceSeen.Clear(); TraceRepeats.Clear(); }
        TraceSeen[key] = now;
        int repeats = TraceRepeats.TryRemove(key, out int skipped) ? skipped : 0;
        _tracing = true;
        try
        {
            Append(Loc.F("{0:yyyy-MM-dd HH:mm:ss.fff} thread {1}{2}\n{3}: {4}\n{5}\n", DateTime.Now, Environment.CurrentManagedThreadId, (Environment.CurrentManagedThreadId == 1 ? Loc.T(" (UI)") : ""), e.GetType().FullName, e.Message, new System.Diagnostics.StackTrace(1, true))
                + (repeats > 0 ? $"(the same exception {repeats} more times since it was last written)\n" : ""));
        }
        catch (Exception) { }
        finally { _tracing = false; }
    }

    // Context lines in the same log (each run, the graphics adapter): a crash report then says
    // which run and which machine an exception belongs to.
    internal static void TraceLine(string line)
    {
        try { Append(line + "\n"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // What the user just did, timestamped (a crash report lists the last ones).
    internal static void Breadcrumb(string what) => TraceLine($"{DateTime.Now:HH:mm:ss.fff} UI: {what}");

    private static void Append(string text)
    {
        var path = Path.Combine(StudioSettings.StateDirectory, "exceptions.log");
        // TryEnter: a contended lock would be a blocking (message-pumping) wait on the UI thread.
        if (!Monitor.TryEnter(TraceGate)) return;
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length > 2 * 1024 * 1024) File.Move(path, path + ".old", true);
            File.AppendAllText(path, text);
        }
        finally { Monitor.Exit(TraceGate); }
    }

    private static string BuildStamp()
    {
        try { return Environment.ProcessPath is { } exe ? File.GetLastWriteTime(exe).ToString("yyyy-MM-dd HH:mm") : "unknown"; }
        catch (IOException) { return "unknown"; }
    }

    private static string? _lastCrash;
    private static long _lastCrashAt;

    // Never throws (it runs in render callbacks, where an escaping exception ends the process), and
    // a failure repeating every frame is written once every ten seconds.
    internal static void WriteCrash(Exception? e)
    {
        try
        {
            string text = e?.ToString() ?? "Unknown failure";
            long now = Environment.TickCount64;
            if (text == _lastCrash && now - _lastCrashAt < 10_000) return;
            _lastCrash = text; _lastCrashAt = now;
            File.WriteAllText(Path.Combine(StudioSettings.StateDirectory, "last-crash-studio.txt"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{text}");
        }
        catch (Exception) { }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // --background --splash runs the splash sequence off-screen (checks).
        bool splashCheck = Background && Arguments.Contains("--splash");
        if (Background && !splashCheck) { _window = new MainWindow(); ShowQuietly(_window); return; }
        if (Arguments.Contains("--no-splash")) { _window = new MainWindow(); _window.Activate(); return; }
        // The splash is drawn first; the main window then starts behind it.
        var splash = new SplashWindow();
        splash.Painted += () =>
        {
            _window = new MainWindow();
            if (Background) ShowQuietly(_window); else _window.Activate();
            splash.Follow(_window);
        };
        if (Background) splash.ShowOffscreen(); else splash.Activate();
    }

    // Startup splash progress (none, shown, painted, closed), for checks.
    internal static string SplashState { get; set; } = "none";
    // Whether the splash's logos were drawn before the main window started (checks).
    internal static bool? SplashLogos { get; set; }

    // Development runs (--background) must not take focus or cover the user's screen.
    internal static bool Background => Arguments.Contains("--background");

    // Beyond every monitor, so nobody can see or click it by accident.
    internal static void ShowQuietly(Window window)
    {
        int right = GetSystemMetrics(76) + GetSystemMetrics(78); // virtual screen left + width
        window.AppWindow.Move(new(right + 400, 40));
        window.AppWindow.Show(false);
    }
}
