// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using NCWE.Export;

namespace NCWE.Studio;

// Checks of the AI setup, one result each: the bridge, the server program, a real round trip
// (the server started like an AI client would, asked for its tools and for Studio's status
// through this Studio's pipe), the clients' registrations, other Studios, the server log.
internal static class McpDiagnostics
{
    public enum Level { Ok, Warning, Error, Info }
    public sealed record Check(string Title, Level Level, string Detail);

    public static string LogPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NCWE", "mcp.log");

    public sealed record Context(BridgeActivity Bridge, bool BridgeRunning, string Pipe, bool GameLoaded, string ProjectName, McpServerFiles Server, IReadOnlyList<McpClientState> Clients);

    public static async Task<List<Check>> RunAsync(Context c, CancellationToken cancellation)
    {
        var checks = new List<Check>();
        // The bridge and the pipe.
        if (!c.BridgeRunning) checks.Add(new(Loc.T("AI bridge"), Level.Error, Loc.T("Off: agents cannot reach this Studio. Start it on the Overview page.")));
        else if (c.Bridge.State == BridgeActivity.BridgeState.Waiting) checks.Add(new(Loc.T("AI bridge"), Level.Warning, Loc.F("Waiting: another NCWE Studio holds the pipe \"{0}\". Agents talk to that one; close it to use this one.", c.Pipe)));
        else
        {
            var (ok, detail) = await PingAsync(c.Pipe, cancellation);
            checks.Add(new(Loc.T("AI bridge"), ok ? Level.Ok : Level.Error, ok ? Loc.F("Listening on pipe \"{0}\" and answering ({1}).", c.Pipe, detail) : Loc.F("Pipe \"{0}\" does not answer: {1}", c.Pipe, detail)));
        }
        checks.Add(c.GameLoaded ? new(Loc.T("Game"), Level.Ok, Loc.F("Loaded. Project: {0}.", c.ProjectName))
            : new(Loc.T("Game"), Level.Warning, Loc.T("Not loaded yet: agents' commands wait until it is (choose the installation if Studio asks).")));
        // The program the clients run.
        var s = c.Server;
        if (!s.StableExists) checks.Add(new(Loc.T("Server program"), Level.Error, Loc.F("Not found at {0}. {1}", s.Stable, (s.Bundled is null ? Loc.T("This build has no NCWE.Mcp.exe either.") : Loc.T("Use “Install / update” on the Overview page.")))));
        else checks.Add(new(Loc.T("Server program"), s.UpdateAvailable ? Level.Warning : Level.Ok,
            Loc.F("{0} ({1} MB, {2:yyyy-MM-dd HH:mm}){3}", s.Stable, s.StableBytes / (1 << 20), s.StableDate, (s.UpdateAvailable ? Loc.T(". This Studio build carries a newer one: update it.") : "."))));
        // A real round trip, as an AI client would do it.
        if (s.StableExists && c.BridgeRunning && c.Bridge.State == BridgeActivity.BridgeState.Listening)
            checks.AddRange(await SelfTestAsync(s.Stable, c.Pipe, cancellation));
        else if (s.StableExists) checks.Add(new(Loc.T("Round trip"), Level.Info, Loc.T("Skipped: the bridge is not listening.")));
        // Clients.
        var registered = c.Clients.Where(x => x.State == McpRegistration.Registered).Select(x => x.Name).ToArray();
        var outdated = c.Clients.Where(x => x.State == McpRegistration.Outdated).ToArray();
        if (outdated.Length > 0)
            checks.Add(new(Loc.T("AI clients"), Level.Warning, string.Join(" ", outdated.Select(o => Loc.F("{0} runs another program ({1}): register it again.", o.Name, o.RegisteredCommand)))));
        if (registered.Length > 0) checks.Add(new(Loc.T("AI clients"), Level.Ok, Loc.F("Connected: {0}.", string.Join(", ", registered))));
        else if (outdated.Length == 0) checks.Add(new(Loc.T("AI clients"), Level.Warning, Loc.T("No AI client is connected yet: see “Connect an AI”.")));
        // Other Studios competing for the pipe.
        int studios = Process.GetProcessesByName("NCWE.Studio").Length;
        if (studios > 1) checks.Add(new(Loc.T("Other Studios"), Level.Warning, Loc.F("{0} NCWE Studio windows are running: agents talk to the one holding \"{1}\" (the first opened).", studios, c.Pipe)));
        // The server log.
        var tail = ReadLog(400);
        int failures = tail.Count(l => l.Contains(" failed", StringComparison.Ordinal));
        checks.Add(tail.Count == 0 ? new(Loc.T("Server log"), Level.Info, Loc.T("Empty: no agent has used the server yet on this PC."))
            : new(Loc.T("Server log"), failures > 0 ? Level.Warning : Level.Ok, failures > 0 ? Loc.F("{0} failure(s) in the last {1} lines (see below).", failures, tail.Count) : Loc.F("No failure in the last {0} lines.", tail.Count)));
        return checks;
    }

    // One status command through the pipe, like the server sends them.
    private static async Task<(bool, string)> PingAsync(string pipe, CancellationToken cancellation)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            // One command at a time: an agent's long command (a screenshot) can hold the pipe a while.
            await client.ConnectAsync(15000, cancellation);
            using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, new UTF8Encoding(false), leaveOpen: true);
            await writer.WriteLineAsync("""{"op":"api.status","client":"diagnostics"}""");
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation); limit.CancelAfter(8000);
            var line = await reader.ReadLineAsync(limit.Token);
            return line is not null && JsonNode.Parse(line)?["ok"]?.GetValue<bool>() == true ? (true, Loc.F("{0} ms", watch.ElapsedMilliseconds)) : (false, line ?? Loc.T("no reply"));
        }
        catch (TimeoutException) { return (false, Loc.T("no connection within 15 s (Studio busy with a long command, or frozen)")); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { return (false, ex.Message); }
    }

    // Starts the server program (never another Studio: --attach-only), initializes it, lists its
    // tools and calls studio_status, which goes through this Studio's pipe.
    private static async Task<List<Check>> SelfTestAsync(string exe, string pipe, CancellationToken cancellation)
    {
        var checks = new List<Check>();
        string log = Path.Combine(Path.GetTempPath(), "ncwe-mcp-selftest.log");
        var start = new ProcessStartInfo(exe) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "--attach-only", "--pipe", pipe, "--log", log }) start.ArgumentList.Add(a);
        Process? process = null;
        var watch = Stopwatch.StartNew();
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the server.");
            var errors = process.StandardError.ReadToEndAsync(cancellation);
            async Task<JsonObject?> Ask(int id, string method, JsonObject parameters, int timeoutMs)
            {
                await process.StandardInput.WriteLineAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters }.ToJsonString());
                await process.StandardInput.FlushAsync(cancellation);
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation); limit.CancelAfter(timeoutMs);
                while (await process.StandardOutput.ReadLineAsync(limit.Token) is { } line)
                    if (JsonNode.Parse(line) is JsonObject reply && reply["id"]?.GetValue<int>() == id) return reply;
                return null;
            }
            var init = await Ask(1, "initialize", new JsonObject
            {
                ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "ncwe-diagnostics", ["version"] = "1" }
            }, 15000);
            if (init?["result"]?["serverInfo"] is not JsonObject info) { checks.Add(new(Loc.T("Server starts"), Level.Error, Loc.F("It did not answer the MCP handshake. {0}", await Tail(errors)))); return checks; }
            await process.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            var tools = await Ask(2, "tools/list", new JsonObject(), 10000);
            int count = (tools?["result"]?["tools"] as JsonArray)?.Count ?? 0;
            checks.Add(new(Loc.T("Server starts"), count > 0 ? Level.Ok : Level.Error,
                Loc.F("{0} {1} answered in {2} ms with {3} tools (protocol {4}).", info["name"], info["version"], watch.ElapsedMilliseconds, count, init["result"]?["protocolVersion"])));
            watch.Restart();
            var status = await Ask(3, "tools/call", new JsonObject { ["name"] = "studio_status", ["arguments"] = new JsonObject() }, 30000);
            var result = status?["result"];
            string text = (result?["content"] as JsonArray)?.OfType<JsonObject>().Select(x => x["text"]?.ToString()).FirstOrDefault(t => t is not null) ?? "";
            bool failed = result is null || result["isError"]?.GetValue<bool>() == true;
            checks.Add(new(Loc.T("Round trip"), failed ? Level.Error : Level.Ok, failed
                ? Loc.F("studio_status failed: {0}", (text.Length > 0 ? text : status?["error"]?["message"]?.ToString() ?? Loc.T("no reply")))
                : Loc.F("studio_status reached this Studio through the pipe in {0} ms.", watch.ElapsedMilliseconds)));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException or System.Text.Json.JsonException)
        {
            checks.Add(new(Loc.T("Server starts"), Level.Error, ex.Message));
        }
        finally
        {
            if (process is not null)
            {
                try { process.StandardInput.Close(); } catch (Exception) { }
                try { if (!process.WaitForExit(3000)) process.Kill(true); } catch (Exception) { }
                process.Dispose();
            }
        }
        return checks;

        static async Task<string> Tail(Task<string> errors)
        {
            try { var text = await errors.WaitAsync(TimeSpan.FromSeconds(2)); return text.Length > 300 ? "…" + text[^300..] : text; }
            catch (Exception) { return ""; }
        }
    }

    public static List<string> ReadLog(int lines)
    {
        try
        {
            if (!File.Exists(LogPath)) return [];
            using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long from = Math.Max(0, stream.Length - lines * 220L);
            stream.Seek(from, SeekOrigin.Begin);
            var all = new StreamReader(stream).ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
            if (from > 0 && all.Count > 0) all.RemoveAt(0);   // a partial first line
            return all.Count > lines ? all[^lines..] : all;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    // --- Running servers ------------------------------------------------------------------------

    public sealed record ServerProcess(int Id, DateTime? Started, string Parent, string Path);

    // The NCWE MCP servers running now, with the program that started them (the AI client).
    public static List<ServerProcess> RunningServers()
    {
        var parents = Snapshot();
        var result = new List<ServerProcess>();
        foreach (var p in Process.GetProcessesByName("NCWE.Mcp"))
        {
            using (p)
            {
                DateTime? started = null; string path = "";
                try { started = p.StartTime; path = p.MainModule?.FileName ?? ""; } catch (Exception) { }
                string parent = parents.TryGetValue(p.Id, out var info) && parents.TryGetValue(info.Parent, out var owner) ? Loc.F("{0} (pid {1})", owner.Name, info.Parent) : Loc.T("unknown");
                result.Add(new(p.Id, started, parent, path));
            }
        }
        return result;
    }

    public static void Stop(int id)
    {
        using var p = Process.GetProcessById(id);
        if (!p.ProcessName.Equals("NCWE.Mcp", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Not an NCWE MCP server.");
        p.Kill(true);
    }

    private static Dictionary<int, (int Parent, string Name)> Snapshot()
    {
        var map = new Dictionary<int, (int, string)>();
        nint snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == -1) return map;
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            for (bool more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
                map[(int)entry.ProcessId] = ((int)entry.ParentProcessId, entry.ExeFile);
        }
        finally { CloseHandle(snapshot); }
        return map;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId; public nint DefaultHeapId; public uint ModuleId, Threads, ParentProcessId; public int PriorityBase; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
