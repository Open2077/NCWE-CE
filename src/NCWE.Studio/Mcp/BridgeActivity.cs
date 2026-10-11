// SPDX-License-Identifier: GPL-3.0-only
namespace NCWE.Studio;

// The automation pipe as the MCP window shows it: listening or not (another Studio may hold the
// name), and every command received, by whom (the AI client the MCP server reports), how long it
// took and how it ended. Written from the pipe loop and the UI thread.
internal sealed class BridgeActivity
{
    public enum BridgeState { Stopped, Listening, Waiting }
    public sealed record Entry(DateTime Time, string Client, string Op, double Milliseconds, bool Ok, string? Error)
    {
        // Status polls (scripts and agents waiting for streaming) are hidden by default.
        public bool IsPoll => IsPollOp(Op);
    }

    public static bool IsPollOp(string op) => op is "status" or "api.status" or "streaming-idle" or "api.wait" or "materials" or "home" or "outliner-ui";

    private const int Capacity = 500;
    private readonly object _gate = new();
    private readonly LinkedList<Entry> _entries = new();
    private readonly Dictionary<string, DateTime> _clients = new(StringComparer.Ordinal);

    public string Pipe { get; private set; } = Automation.DefaultPipe;
    public BridgeState State { get; private set; } = BridgeState.Stopped;
    public int Requests { get; private set; }
    public int Errors { get; private set; }
    public DateTime Started { get; } = DateTime.Now;
    // Bumped on every change: the window refreshes only then.
    public int Version { get; private set; }

    public void SetState(BridgeState state, string pipe)
    {
        lock (_gate) { if (State == state && Pipe == pipe) return; State = state; Pipe = pipe; Version++; }
    }

    public void Record(string client, string op, double milliseconds, bool ok, string? error)
    {
        lock (_gate)
        {
            _entries.AddFirst(new Entry(DateTime.Now, client, op, milliseconds, ok, error));
            if (_entries.Count > Capacity) _entries.RemoveLast();
            Requests++; if (!ok) Errors++;
            _clients[client] = DateTime.Now;
            Version++;
        }
    }

    public void Clear() { lock (_gate) { _entries.Clear(); Requests = Errors = 0; _clients.Clear(); Version++; } }

    public Entry[] Entries() { lock (_gate) return [.. _entries]; }
    public (string Client, DateTime Seen)[] Clients() { lock (_gate) return [.. _clients.Select(c => (c.Key, c.Value)).OrderByDescending(c => c.Value)]; }
    public Entry? Last(bool includePolls = false) { lock (_gate) return _entries.FirstOrDefault(e => includePolls || !e.IsPoll); }
}
