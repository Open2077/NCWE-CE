// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using NCWE.Studio.Session;

namespace NCWE.Studio;

// Exits nothing in Studio could log (a native crash in a graphics driver, a fail-fast of the UI
// framework): a marker file stays while Studio runs; when the next start finds it, one report is
// written beside the other logs (last-crash-windows.txt) for a player to send:
// - Windows' own records about NCWE.Studio.exe since then (Application Error: the faulting module;
//   .NET Runtime: the managed stack of a fail-fast; Windows Error Reporting; Application Hang);
// - what NCWE itself recorded during that run (exceptions.log: the run's header and graphics
//   adapter, the user's last actions, the last exceptions);
// - the crash dumps Windows saved, when crash dumps are on (Help ▸ Record crash dumps).
internal static partial class CrashReport
{
    private const string LocalDumpsKey = @"SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\NCWE.Studio.exe";
    private static string Marker => Path.Combine(StudioSettings.StateDirectory, "session-running.txt");
    public static string ReportPath => Path.Combine(StudioSettings.StateDirectory, "last-crash-windows.txt");
    public static string DumpFolder => Path.Combine(StudioSettings.StateDirectory, "CrashDumps");
    public static bool PreviousExitUnclean { get; private set; }

    public static void Begin()
    {
        try
        {
            if (File.Exists(Marker) && !OwnedByLiveInstance(File.ReadAllText(Marker)))
            {
                PreviousExitUnclean = true;
                var since = File.GetLastWriteTimeUtc(Marker).AddMinutes(-1);
                // Read now, before this run adds its own lines to the log.
                string? previous = PreviousRun();
                _ = Task.Run(() => Collect(since, previous));
            }
            File.WriteAllText(Marker, $"{Environment.ProcessId} {DateTime.UtcNow:O}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // A clean exit: the marker goes (only ours: another instance may share the folder).
    public static void End()
    {
        try { if (File.Exists(Marker) && File.ReadAllText(Marker).StartsWith(Environment.ProcessId + " ", StringComparison.Ordinal)) File.Delete(Marker); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // Another Studio still running with this state folder wrote the marker: not a crash.
    private static bool OwnedByLiveInstance(string marker)
    {
        if (!int.TryParse(marker.Split(' ')[0], out int pid) || pid == Environment.ProcessId) return false;
        try { using var process = Process.GetProcessById(pid); return process.ProcessName.Equals("NCWE.Studio", StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    // The previous run's part of exceptions.log: its header and graphics line, the user's last
    // actions ("UI:" lines) and its last records.
    private static string? PreviousRun()
    {
        string path = Path.Combine(StudioSettings.StateDirectory, "exceptions.log");
        if (!File.Exists(path)) return null;
        string text;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            const int window = 512 * 1024;
            if (stream.Length > window) stream.Seek(-window, SeekOrigin.End);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            text = reader.ReadToEnd();
        }
        int run = text.LastIndexOf("=== NCWE Studio started", StringComparison.Ordinal);
        if (run >= 0) text = text[run..];
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var context = lines.Where(l => l.StartsWith("=== ", StringComparison.Ordinal) || l.StartsWith("Graphics:", StringComparison.Ordinal)).Take(2);
        var actions = lines.Where(l => l.StartsWith("UI: ", StringComparison.Ordinal) || l.Contains(" UI: ", StringComparison.Ordinal)).TakeLast(40).ToArray();
        const int tail = 24 * 1024;
        string last = text.Length > tail ? "…" + text[^tail..] : text;
        var report = new StringBuilder();
        foreach (var line in context) report.AppendLine(line);
        report.AppendLine();
        report.AppendLine(actions.Length == 0 ? "Last actions: (none recorded)" : "Last actions:");
        foreach (var action in actions) report.AppendLine("  " + action);
        report.AppendLine();
        report.AppendLine("Last records:");
        report.AppendLine(last);
        return report.ToString();
    }

    [GeneratedRegex(@"(?m)^Event\[\d+\]")]
    private static partial Regex EventStart();

    private static void Collect(DateTime sinceUtc, string? previousRun)
    {
        var report = new StringBuilder();
        report.AppendLine($"NCWE Studio closed unexpectedly before {DateTime.Now:yyyy-MM-dd HH:mm} (local time).");
        report.AppendLine($"Windows reports about NCWE.Studio.exe since {sinceUtc.ToLocalTime():yyyy-MM-dd HH:mm} (their \"Date\" is local time despite its Z):");
        report.AppendLine();
        try
        {
            string query = "*[System[(Provider[@Name='Application Error'] or Provider[@Name='.NET Runtime'] or Provider[@Name='Windows Error Reporting'] or Provider[@Name='Application Hang'])"
                + $" and TimeCreated[@SystemTime>='{sinceUtc:yyyy-MM-ddTHH:mm:ss.fffZ}']]]";
            var start = new ProcessStartInfo("wevtutil.exe", $"qe Application /q:\"{query}\" /f:text /rd:true /c:60 /uni:true")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.Unicode };   // /uni: localized text survives
            using var process = Process.Start(start) ?? throw new InvalidOperationException("wevtutil did not start.");
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(20_000)) { try { process.Kill(); } catch (InvalidOperationException) { } }
            var events = EventStart().Split(output.Result).Where(e => e.Contains("NCWE.Studio", StringComparison.OrdinalIgnoreCase)).Select(e => e.Trim()).ToArray();
            report.AppendLine(events.Length == 0 ? "(Windows recorded nothing about it.)" : string.Join(Environment.NewLine + Environment.NewLine, events));
        }
        catch (Exception ex) { report.AppendLine("Could not read the Windows event log: " + ex.Message); }

        report.AppendLine();
        try
        {
            var dumps = Directory.Exists(DumpFolder)
                ? new DirectoryInfo(DumpFolder).GetFiles("*.dmp").Where(f => f.LastWriteTimeUtc >= sinceUtc).OrderByDescending(f => f.LastWriteTimeUtc).ToArray() : [];
            report.AppendLine(dumps.Length > 0
                ? "Crash dumps (send the newest with this file):" + Environment.NewLine + string.Join(Environment.NewLine, dumps.Select(d => $"  {d.FullName} ({d.Length / 1024 / 1024} MB)"))
                : DumpsEnabled() ? "No crash dump was saved." : "Crash dumps are off (Help ▸ Record crash dumps turns them on for the next time).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        if (previousRun is not null)
        {
            report.AppendLine();
            report.AppendLine("What NCWE recorded during that run (exceptions.log):");
            report.AppendLine(previousRun);
        }
        try { File.WriteAllText(ReportPath, report.ToString()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // --- Crash dumps ----------------------------------------------------------------------------
    // A fail-fast (a stowed exception in the UI framework, a driver crash) leaves no managed trace:
    // only a dump tells where it came from. Windows Error Reporting writes one per crash when the
    // machine-wide LocalDumps key names the program (it needs administrator rights once).

    public static bool DumpsEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(LocalDumpsKey);
            return key?.GetValue("DumpFolder") is string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
    }

    // Asks Windows (an administrator prompt) to keep NCWE's crash dumps in DumpFolder: small dumps
    // with the memory a stowed exception needs (data segments, handles, thread info, memory the
    // stacks point to), the five newest. False when the prompt was declined.
    public static bool EnableDumps()
    {
        Directory.CreateDirectory(DumpFolder);
        const int flags = 0x1 | 0x4 | 0x20 | 0x40 | 0x100 | 0x800 | 0x1000;
        string key = @"HKLM\" + LocalDumpsKey;
        string commands = string.Join(" && ",
            $"reg add \"{key}\" /v DumpFolder /t REG_EXPAND_SZ /d \"{DumpFolder}\" /f",
            $"reg add \"{key}\" /v DumpType /t REG_DWORD /d 0 /f",
            $"reg add \"{key}\" /v CustomDumpFlags /t REG_DWORD /d {flags} /f",
            $"reg add \"{key}\" /v DumpCount /t REG_DWORD /d 5 /f");
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c " + commands) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            process?.WaitForExit(30_000);
            return DumpsEnabled();
        }
        catch (System.ComponentModel.Win32Exception) { return false; }   // the prompt was declined
    }
}
