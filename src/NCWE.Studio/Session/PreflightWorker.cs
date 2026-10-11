// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace NCWE.Studio.Session;

// Runs this executable as a headless --cache-worker child (see CacheWorker) and exposes
// its progress. The child exits by itself when done or when this process exits.
internal sealed class PreflightWorker : IDisposable
{
    private Process? _process;
    private volatile string _phase = "";
    private int _sectorsDone, _sectorsTotal, _meshesSeen, _meshesConverted;
    public Vector3 Center { get; private set; }
    public float Radius { get; private set; }
    private Task _launch = Task.CompletedTask;
    private volatile bool _launched, _exited;
    // Read every frame on the UI thread: never Process.HasExited there. It waits on the process
    // handle, and a wait on the UI (STA) thread pumps window messages: a mouse move dispatched
    // inside the rendering callback made XAML fail fast (0xC000027B, CXcpDispatcher::CheckReentrancy,
    // a tester's crash dump of 0.1.0-beta.1). The Exited event sets the flag instead.
    public bool Running
    {
        get
        {
            if (_process is null) return false;
            if (!_launched) return !_launch.IsCompleted || Error is null;
            return !_exited;
        }
    }
    public bool Completed { get; private set; }
    public string Phase => _phase;
    public int SectorsDone => Volatile.Read(ref _sectorsDone);
    public int SectorsTotal => Volatile.Read(ref _sectorsTotal);
    public int MeshesSeen => Volatile.Read(ref _meshesSeen);
    public int MeshesConverted => Volatile.Read(ref _meshesConverted);
    public string? Error { get; private set; }

    public void Start(string game, string stateDirectory, Vector3 center, float radius, int threads, long diskBudget)
    {
        Stop();
        Center = center; Radius = radius; Completed = false; Error = null; _phase = "starting";
        _sectorsDone = _sectorsTotal = _meshesSeen = _meshesConverted = 0;
        var info = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        foreach (var argument in new[] { "--cache-worker", "--game", game, "--state-dir", stateDirectory,
            "--center", string.Join(',', new[] { center.X, center.Y, center.Z }.Select(v => v.ToString("F1", CultureInfo.InvariantCulture))),
            "--radius", radius.ToString("F0", CultureInfo.InvariantCulture), "--threads", threads.ToString(CultureInfo.InvariantCulture),
            "--disk-budget", diskBudget.ToString(CultureInfo.InvariantCulture), "--parent", Environment.ProcessId.ToString(CultureInfo.InvariantCulture) })
            info.ArgumentList.Add(argument);
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is { Length: > 0 } line) Parse(line); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } line) Error = line; };
        process.Exited += (_, _) =>
        {
            if (ReferenceEquals(_process, process)) _exited = true;
            // Runs on the thread pool, possibly after Stop() disposed the Process: never throw here.
            try { int code = process.ExitCode; if (_phase != "done" && Error is null && code is not 0 and not 2) Error = Loc.F("Preflight worker exited ({0}).", code); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        };
        _process = process; _launched = false; _exited = false;
        // Never start (or stop) a process on the UI thread inside a rendering callback: with the
        // window active, XAML fails fast (0xC000027B). The launch runs on the thread pool.
        _launch = Task.Run(() =>
        {
            try { process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine(); _launched = true; }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException) { Error = Loc.F("Preflight: {0}", ex.Message); }
        });
    }

    private void Parse(string line)
    {
        try
        {
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            if (root.TryGetProperty("error", out var error)) { Error = error.GetString(); return; }
            if (root.TryGetProperty("phase", out var phase)) _phase = phase.GetString() ?? "";
            if (root.TryGetProperty("sectorsDone", out var v)) Volatile.Write(ref _sectorsDone, v.GetInt32());
            if (root.TryGetProperty("sectorsTotal", out v)) Volatile.Write(ref _sectorsTotal, v.GetInt32());
            if (root.TryGetProperty("meshesSeen", out v)) Volatile.Write(ref _meshesSeen, v.GetInt32());
            if (root.TryGetProperty("meshesConverted", out v)) Volatile.Write(ref _meshesConverted, v.GetInt32());
            if (_phase == "done") Completed = true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { }
    }

    public void Stop()
    {
        var process = _process; _process = null;
        if (process is null) return;
        var launch = _launch;
        _ = Task.Run(async () =>
        {
            try { await launch.ConfigureAwait(false); if (!process.HasExited) process.Kill(); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            process.Dispose();
        });
    }

    public void Dispose() => Stop();
}
