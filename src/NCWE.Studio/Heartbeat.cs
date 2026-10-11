// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;

namespace NCWE.Studio;

// A line in exceptions.log every 20 seconds (memory, GC, threads), and the overlay or capture
// hooks other programs inject into NCWE (RivaTuner, Afterburner, GeForce, Discord, Steam, OBS...).
// A fail-fast crash leaves no exception behind, but last-crash-windows.txt carries the crashed
// run's log tail: these lines then tell what the process looked like just before it died.
internal static class Heartbeat
{
    private static Timer? _timer;
    private static readonly Stopwatch Uptime = Stopwatch.StartNew();
    private static string _hooks = "";

    // Module names of known injectors (lower case, matched as substrings).
    private static readonly string[] HookModules =
    [
        "rtss", "nvspcap", "nvcamera", "nvoverlay", "discordhook", "gameoverlayrenderer", "graphics-hook", "overwolf", "ow-graphics",
        "xsplit", "reshade", "fraps", "bdcam", "action_x64", "medal", "nahimic", "sonicsuite", "eosoverlay", "uplay_r", "igo64", "razer",
    ];

    public static void Start()
    {
        _timer ??= new Timer(_ => Beat(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20));
    }

    private static void Beat()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            string hooks = Hooks(process);
            if (hooks != _hooks) { _hooks = hooks; App.TraceLine($"{DateTime.Now:HH:mm:ss.fff} Injected modules: {(hooks.Length == 0 ? "none known" : hooks)}"); }
            var gc = GC.GetGCMemoryInfo();
            App.TraceLine($"{DateTime.Now:HH:mm:ss.fff} Heartbeat: up {Uptime.Elapsed:hh\\:mm\\:ss}, private {process.PrivateMemorySize64 >> 20} MB, working set {process.WorkingSet64 >> 20} MB, " +
                $"managed {GC.GetTotalMemory(false) >> 20} MB, memory load {(gc.TotalAvailableMemoryBytes == 0 ? 0 : 100 * gc.MemoryLoadBytes / gc.TotalAvailableMemoryBytes)} %, " +
                $"GC {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}, threads {process.Threads.Count}, handles {process.HandleCount}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
    }

    private static string Hooks(Process process)
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ProcessModule module in process.Modules)
        {
            string name = module.ModuleName.ToLowerInvariant();
            string path = module.FileName;
            // Windows' own dxgi/d3d11 are expected; a copy outside Windows (ReShade-style proxy) is not.
            if ((name is "dxgi.dll" or "d3d11.dll") && path.StartsWith(system, StringComparison.OrdinalIgnoreCase)) continue;
            if (name is "dxgi.dll" or "d3d11.dll" || HookModules.Any(h => name.Contains(h, StringComparison.Ordinal))) found.Add(path);
        }
        return string.Join(", ", found);
    }
}
