// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace NCWE.Mcp;

// One Studio command: {"op": ..., args...} -> the reply object ({"ok": true|false, ...}).
internal interface IStudio
{
    Task<JsonObject> CallAsync(string op, JsonObject? args, TimeSpan timeout, CancellationToken cancellation);
}

// Talks to NCWE Studio's automation pipe (one JSON line per connection, current user only).
// When nothing answers, launches Studio (visible by default, so the user watches the AI work)
// and waits until it has opened the game.
internal sealed class StudioLink(ServerOptions options) : IStudio, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _launched;
    private bool _ready;

    public async Task<JsonObject> CallAsync(string op, JsonObject? args, TimeSpan timeout, CancellationToken cancellation)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            var command = args is null ? new JsonObject() : (JsonObject)args.DeepClone();
            command["op"] = op;
            command["client"] = McpServer.Client;   // who acts, for Studio's activity log (ignored otherwise)
            string line = command.ToJsonString();
            try { var reply = await SendAsync(line, timeout, cancellation); _ready = true; return reply; }
            catch (StudioUnavailableException) when (!options.AttachOnly)
            {
                // A Studio that is running but silent is busy (or too old to have the pipe):
                // never start a second editor over the user's.
                if (!options.Headless && (_launched is null || _launched.HasExited) && Process.GetProcessesByName("NCWE.Studio").Length > 0)
                {
                    for (int attempt = 0; attempt < 6; attempt++)
                    {
                        await Task.Delay(2500, cancellation);
                        try { var reply = await SendAsync(line, timeout, cancellation); _ready = true; return reply; }
                        catch (StudioUnavailableException) { }
                    }
                    throw new InvalidOperationException($"NCWE Studio is running but does not answer on pipe \"{options.Pipe}\". Its AI bridge may be off (in Studio: Help ▸ AI agents ▸ Start the bridge), or it is an older build without it (close it and start the current one, launch-ncwe.cmd).");
                }
                await LaunchAsync(cancellation);
                return await SendAsync(line, timeout, cancellation);
            }
        }
        finally { _gate.Release(); }
    }

    private async Task<JsonObject> SendAsync(string line, TimeSpan timeout, CancellationToken cancellation)
    {
        using var pipe = new NamedPipeClientStream(".", options.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(_ready ? 8000 : 2500, cancellation); }
        catch (TimeoutException) { _ready = false; throw new StudioUnavailableException(options.Pipe); }
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
        await writer.WriteLineAsync(line.AsMemory(), cancellation);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(timeout);
        string? reply;
        try { reply = await reader.ReadLineAsync(limit.Token); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new TimeoutException($"Studio did not answer within {timeout.TotalSeconds:0} s (it may still be working)."); }
        if (reply is null) { _ready = false; throw new IOException("Studio closed the connection (was it closed?)."); }
        return JsonNode.Parse(reply) as JsonObject ?? throw new InvalidDataException("Studio replied with something that is not an object.");
    }

    // Starts Studio and waits for it to answer with the game indexed.
    private async Task LaunchAsync(CancellationToken cancellation)
    {
        string exe = FindStudio() ?? throw new InvalidOperationException(
            "NCWE Studio is not running and its executable was not found. Start NCWE Studio (launch-ncwe.cmd), or pass --studio <NCWE.Studio.exe>.");
        if (_launched is null || _launched.HasExited)
        {
            var arguments = new List<string>();
            if (options.Headless) arguments.AddRange(["--background", "--pipe", options.Pipe]);
            else if (options.Pipe != "ncwe-studio") arguments.AddRange(["--pipe", options.Pipe]);
            if (options.Game is { } game) arguments.AddRange(["--game", game]);
            if (options.StateDirectory is { } state) arguments.AddRange(["--state-dir", state]);
            // Shell execution: Studio must not inherit this server's stdio pipes.
            var start = new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)!, Arguments = string.Join(' ', arguments.Select(Quote)) };
            Log.Write($"Launching Studio: {exe} {start.Arguments}");
            _launched = Process.Start(start) ?? throw new InvalidOperationException("Could not start " + exe);
        }
        var timer = Stopwatch.StartNew(); string last = "";
        while (timer.Elapsed < options.LaunchTimeout)
        {
            cancellation.ThrowIfCancellationRequested();
            if (_launched.HasExited) throw new InvalidOperationException($"NCWE Studio exited during startup (code {_launched.ExitCode}).");
            try
            {
                var status = await SendAsync("""{"op":"api.status"}""", TimeSpan.FromSeconds(20), cancellation);
                if (status["ok"]?.GetValue<bool>() == true && status["indexed"]?.GetValue<bool>() == true && status["busy"]?.GetValue<bool>() != true) { _ready = true; return; }
                last = status["status"]?.ToString() ?? status["error"]?.ToString() ?? "";
            }
            catch (Exception ex) when (ex is StudioUnavailableException or IOException or TimeoutException) { }
            await Task.Delay(1500, cancellation);
        }
        throw new TimeoutException($"NCWE Studio did not finish opening the game within {options.LaunchTimeout.TotalMinutes:0} min. Last status: {last}. If no game is configured, open it once in Studio or pass --game.");
    }

    private static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;

    private string? FindStudio()
    {
        if (options.StudioExe is { } given && File.Exists(given)) return Path.GetFullPath(given);
        string here = AppContext.BaseDirectory;
        string beside = Path.Combine(here, "NCWE.Studio.exe");
        if (File.Exists(beside)) return beside;
        // Repository layout: <repo>\artifacts\studio-previews\<stamp>\NCWE.Studio.exe, newest first.
        for (var dir = new DirectoryInfo(here); dir is not null; dir = dir.Parent)
        {
            var previews = Path.Combine(dir.FullName, "artifacts", "studio-previews");
            if (!Directory.Exists(previews)) continue;
            var newest = Directory.EnumerateDirectories(previews).OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                .Select(d => Path.Combine(d, "NCWE.Studio.exe")).FirstOrDefault(File.Exists);
            if (newest is not null) return newest;
        }
        return null;
    }

    // A headless Studio started for this session goes with it; a visible one stays for the user.
    public void Dispose()
    {
        if (options.Headless && _launched is { HasExited: false } process)
        {
            try { _ = SendAsync("""{"op":"quit"}""", TimeSpan.FromSeconds(5), CancellationToken.None).Wait(6000); } catch (Exception) { }
            try { if (!process.WaitForExit(8000)) process.Kill(); } catch (Exception) { }
        }
        _gate.Dispose();
    }
}

internal sealed class StudioUnavailableException(string pipe) : Exception($"NCWE Studio is not answering on pipe \"{pipe}\".");
