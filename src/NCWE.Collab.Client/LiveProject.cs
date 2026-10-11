// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Text.Json;
using System.Threading.Channels;
using NCWE.Core;

namespace NCWE.Collab.Client;

public enum LiveState { Connecting, Live, Reconnecting, Ended }

// What a participant shows of themselves: where their camera is and what they have selected.
public sealed record PresenceData(float[]? Camera, float Heading, float Pitch, string[]? Selection, string? Tool);

// An NCWE project bound to a live collaboration session.
// - Local edits (EditorDocument.Edited) leave as project deltas, split to the op size limit.
// - Everyone's edits come back in the server's order and are applied to the document, except the
//   objects this client changed in edits not yet confirmed (they come later in that order and win).
// - After a gap or a lost connection the client catches up from its last op, or reloads the newest
//   snapshot when the log no longer reaches back; snapshots are uploaded when the server asks.
// - Presence, locks (on the selection) and chat ride on the same connection.
// Everything that touches the document runs through `post` (Studio's UI thread).
public sealed class LiveProject : IAsyncDisposable
{
    private const int OpPieceBytes = 400 * 1024;
    private static readonly int[] Backoff = [500, 1000, 2000, 5000, 10000, 15000];

    private readonly CollabClient _client;
    private readonly IProjectPackager _packager;
    private readonly Action<Action> _post;
    private readonly Func<ProjectData, EditorDocument> _rebuild;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<ClientMessage> _outgoing = Channel.CreateUnbounded<ClientMessage>(new UnboundedChannelOptions { SingleReader = true });
    private readonly List<PendingOp> _pending = [];
    private readonly List<OpApplied> _held = [];
    private readonly Dictionary<string, ParticipantInfo> _participants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonElement> _presence = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LockInfo> _locks = new(StringComparer.Ordinal);
    private readonly List<ChatPosted> _chat = [];
    private HashSet<string> _wantedLocks = new(StringComparer.Ordinal);
    private EditorDocument _document;
    private long _seq;
    private bool _replaying = true, _resyncing, _snapshotWanted, _uploadingSnapshot, _replayDoneDuringResync;
    private string? _lastPresence;
    private long _lastPresenceAt;
    private Task? _receiver;
    private int _opCounter;

    private sealed class PendingOp(string id, ProjectDelta delta, string name)
    {
        public string Id { get; } = id;
        public ProjectDelta Delta { get; } = delta;
        public string Name { get; } = name;
        public bool Sent { get; set; }
    }

    private LiveProject(CollabClient client, SessionInfo session, IProjectPackager packager, Action<Action> post, Func<ProjectData, EditorDocument> rebuild, EditorDocument document)
    {
        _client = client; _packager = packager; _post = post; _rebuild = rebuild; _document = document;
        Session = session;
    }

    public CollabClient Client => _client;
    public SessionInfo Session { get; private set; }
    public string Code => Session.Id;
    public ParticipantInfo? You { get; private set; }
    public string Role => You?.Role ?? Session.YourRole;
    public bool IsOwner => Role == SessionRoles.Owner;
    public bool CanEdit => SessionRoles.CanEdit(Role);
    public LiveState State { get; private set; } = LiveState.Connecting;
    public string? EndReason { get; private set; }
    public long Seq => Interlocked.Read(ref _seq);
    public int PendingEdits => _pending.Count;
    // The shared world the session works on (its edits are saved into it), if any.
    public Guid? WorldId => Session.ProjectId;
    // This client keeps the session's snapshots (the owner, or the earliest editor without them).
    public bool IsSnapshotKeeper => ShouldSnapshot();
    public EditorDocument Document => _document;
    public IReadOnlyCollection<ParticipantInfo> Participants => _participants.Values;
    public IReadOnlyDictionary<string, JsonElement> Presence => _presence;
    public IReadOnlyDictionary<string, LockInfo> Locks => _locks;
    public IReadOnlyList<ChatPosted> Chat => _chat;

    // Anything changed (participants, presence, locks, state...): the UI refreshes.
    public event Action? Changed;
    public event Action<ChatPosted>? ChatReceived;
    // Something to tell the user ("Alice joined", "You were removed from the session").
    public event Action<string>? Notice;
    // The document was rebuilt from a snapshot (resynchronisation): the UI shows the new one.
    public event Action<EditorDocument>? DocumentReplaced;
    // Someone else's edit, as applied to the document (what it refers to may need loading: sectors,
    // custom meshes or sounds this client does not have yet).
    public event Action<ProjectDelta>? RemoteEdit;
    // An edit of this client, on its way to the session.
    public event Action<ProjectDelta>? LocalEdit;
    // A newer snapshot of the session is ready (files it carries can be fetched from it).
    public event Action<SnapshotInfo>? SnapshotPublished;

    // --- Opening ------------------------------------------------------------------------------------

    // Opens a session with the current project (uploaded as its first snapshot) and goes live.
    public static async Task<LiveProject> HostAsync(CollabClient client, CreateSessionRequest request, EditorDocument document, IProjectPackager packager,
        Action<Action> post, Func<ProjectData, EditorDocument> rebuild, CancellationToken cancellation = default)
    {
        var session = await client.CreateSessionAsync(request, cancellation).ConfigureAwait(false);
        if (session.Snapshot is null)
        {
            var snapshot = await client.UploadSnapshotAsync(session.Id, packager.Pack(document.Project), 0, packager.ContentType, cancellation).ConfigureAwait(false);
            session = session with { Snapshot = snapshot };
        }
        var live = new LiveProject(client, session, packager, post, rebuild, document);
        // Started where the document lives (its events and edits are the UI thread's).
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        post(() => { try { live.Start(0); started.SetResult(); } catch (Exception ex) { started.SetException(ex); } });
        await started.Task.ConfigureAwait(false);
        return live;
    }

    // Enters a session: the project to open (its newest snapshot) and the live project to start
    // with the document built from it (Start).
    public static async Task<(LiveProject Live, ProjectData Project, long BaseSeq)> JoinAsync(CollabClient client, string code, string password, IProjectPackager packager,
        Action<Action> post, Func<ProjectData, EditorDocument> rebuild, CancellationToken cancellation = default)
    {
        var session = await client.JoinSessionAsync(code, password, cancellation).ConfigureAwait(false);
        return await EnterAsync(client, session, packager, post, rebuild, cancellation).ConfigureAwait(false);
    }

    // Enters a session the account already belongs to (a shared world's, opened through the world):
    // the project to open (its newest snapshot) and the live project to start with it (Start).
    public static async Task<(LiveProject Live, ProjectData Project, long BaseSeq)> EnterAsync(CollabClient client, SessionInfo session, IProjectPackager packager,
        Action<Action> post, Func<ProjectData, EditorDocument> rebuild, CancellationToken cancellation = default)
    {
        ProjectData project;
        long baseSeq = 0;
        if (session.Snapshot is not null)
        {
            var (snapshot, content) = await client.DownloadSnapshotAsync(session.Id, cancellation).ConfigureAwait(false);
            project = packager.Unpack(content);
            baseSeq = snapshot.Seq;
        }
        else project = ProjectData.Create(session.Name);
        // The document is attached by Start (the caller builds it from the project first).
        return (new LiveProject(client, session, packager, post, rebuild, null!), project, baseSeq);
    }

    // Goes live with this document (JoinAsync: the one built from the returned project).
    public void Start(EditorDocument document, long baseSeq)
    {
        _document = document;
        Start(baseSeq);
    }

    private void Start(long baseSeq)
    {
        Interlocked.Exchange(ref _seq, baseSeq);
        Attach(_document);
        _receiver = Task.Run(() => RunAsync(_stop.Token));
    }

    private void Attach(EditorDocument document)
    {
        document.Edited += OnEdited;
        document.ReadOnly = !CanEdit;
    }

    private void Detach(EditorDocument document)
    {
        document.Edited -= OnEdited;
        document.ReadOnly = false;
    }

    // --- Connection (background) ------------------------------------------------------------------------

    private static readonly HashSet<int> Final = [CollabCloseCodes.Unauthorized, CollabCloseCodes.NotMember, CollabCloseCodes.Kicked,
        CollabCloseCodes.SessionClosed, CollabCloseCodes.Replaced, CollabCloseCodes.AccountRevoked];

    private async Task RunAsync(CancellationToken stop)
    {
        int attempt = 0;
        while (!stop.IsCancellationRequested)
        {
            CollabLiveConnection? live = null;
            try
            {
                live = await _client.ConnectAsync(Code, Seq, "NCWE Studio", stop).ConfigureAwait(false);
                attempt = 0;
                using var sending = CancellationTokenSource.CreateLinkedTokenSource(stop);
                var sender = SendLoopAsync(live, sending.Token);
                while (await live.ReceiveAsync(stop).ConfigureAwait(false) is { } message)
                    _post(() => Handle(message));
                sending.Cancel();
                try { await sender.ConfigureAwait(false); } catch (OperationCanceledException) { }
                int code = (int?)live.CloseStatus ?? 0;
                if (Final.Contains(code))
                {
                    string reason = live.CloseDescription ?? "The connection ended.";
                    _post(() => End(code switch
                    {
                        CollabCloseCodes.Kicked => reason,
                        CollabCloseCodes.SessionClosed => "The session was closed.",
                        CollabCloseCodes.Replaced => "This session was opened again elsewhere with your account.",
                        CollabCloseCodes.AccountRevoked => "Your Open//77 session ended: sign in again.",
                        CollabCloseCodes.NotMember => "You are no longer a member of this session.",
                        _ => reason
                    }));
                    return;
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or HttpRequestException or IOException or CollabApiException or InvalidOperationException)
            {
                if (ex is CollabApiException { Status: 401 or 403 or 404 } refused)
                {
                    _post(() => End(refused.Status == 404 ? "The session no longer exists." : refused.Message));
                    return;
                }
            }
            finally { if (live is not null) await live.DisposeAsync().ConfigureAwait(false); }
            if (stop.IsCancellationRequested) return;
            _post(() => { if (State != LiveState.Ended) { State = LiveState.Reconnecting; _replaying = true; foreach (var p in _pending) p.Sent = false; Changed?.Invoke(); } });
            try { await Task.Delay(Backoff[Math.Min(attempt++, Backoff.Length - 1)], stop).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task SendLoopAsync(CollabLiveConnection live, CancellationToken cancellation)
    {
        try
        {
            await foreach (var message in _outgoing.Reader.ReadAllAsync(cancellation).ConfigureAwait(false))
                await live.SendAsync(message, cancellation).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or IOException or ObjectDisposedException) { }
    }

    private void Send(ClientMessage message)
    {
        if (State is LiveState.Live or LiveState.Connecting or LiveState.Reconnecting) _outgoing.Writer.TryWrite(message);
    }

    // --- Messages (UI thread) ------------------------------------------------------------------------

    private void Handle(ServerMessage message)
    {
        if (State == LiveState.Ended) return;
        switch (message)
        {
            case Welcome welcome: OnWelcome(welcome); break;
            case ReplayDone: OnReplayDone(); break;
            case OpApplied op:
                if (_resyncing) _held.Add(op); else Apply(op);
                break;
            case OpRejected rejected: OnRejected(rejected); break;
            case ParticipantJoined joined:
                _participants[joined.Participant.ParticipantId] = joined.Participant;
                Notice?.Invoke($"{joined.Participant.DisplayName} joined.");
                break;
            case ParticipantLeft left:
                if (_participants.Remove(left.ParticipantId, out var gone) && left.Reason != "replaced")
                    Notice?.Invoke(left.Reason switch { "kicked" => $"{gone.DisplayName} was removed.", "banned" => $"{gone.DisplayName} was banned.", _ => $"{gone.DisplayName} left." });
                _presence.Remove(left.ParticipantId);
                break;
            case PresenceChanged presence: _presence[presence.ParticipantId] = presence.Data; break;
            case LocksChanged locks:
                foreach (var id in locks.Released) _locks.Remove(id);
                foreach (var info in locks.Acquired) _locks[info.Id] = info;
                break;
            case LockResult result:
                if (result.Denied.Count > 0 && result.Denied.FirstOrDefault(d => d.AccountId != You?.AccountId) is { } denied)
                    Notice?.Invoke($"{NameOf(denied.AccountId)} is editing {(result.Denied.Count == 1 ? "this object" : $"{result.Denied.Count} of the selected objects")}.");
                break;
            case ChatPosted chat:
                _chat.Add(chat);
                if (_chat.Count > 500) _chat.RemoveRange(0, _chat.Count - 500);
                ChatReceived?.Invoke(chat);
                break;
            case RoleChanged role: OnRoleChanged(role); break;
            case SessionUpdated updated: Session = updated.Session; break;
            case SnapshotAvailable snapshot: Session = Session with { Snapshot = snapshot.Snapshot }; SnapshotPublished?.Invoke(snapshot.Snapshot); break;
            case SnapshotRequested request: if (ShouldSnapshot()) { _snapshotWanted = true; TryUploadSnapshot(); } break;
            case ResyncRequired resync: _ = ResyncAsync(resync.Base); break;
            case Kicked kicked: End(kicked.Banned ? $"You were banned from the session{(kicked.Reason is { } r ? ": " + r : ".")}" : $"You were removed from the session{(kicked.Reason is { } why ? ": " + why : ".")}"); break;
            case SessionClosed closed: End(closed.Reason == "idle" ? "The session closed after a long idle time." : "The owner closed the session."); break;
            case LiveError error: if (error.Code != CollabErrors.RateLimited) Notice?.Invoke(error.Message); break;
        }
        Changed?.Invoke();
    }

    private string NameOf(Guid account) => _participants.Values.FirstOrDefault(p => p.AccountId == account)?.DisplayName ?? "Someone";

    private void OnWelcome(Welcome welcome)
    {
        Session = welcome.Session;
        You = welcome.You;
        _participants.Clear();
        foreach (var p in welcome.Participants) _participants[p.ParticipantId] = p;
        _presence.Clear();
        foreach (var p in welcome.Presence) _presence[p.ParticipantId] = p.Data;
        _locks.Clear();
        foreach (var l in welcome.Locks) _locks[l.Id] = l;
        _document.ReadOnly = !CanEdit;
        _replaying = true;
        State = LiveState.Live;
        // The server replays from where this client is, or from its newest snapshot when the log
        // no longer reaches back (the project is then reloaded from that snapshot first).
        if (welcome.ReplayFrom != Seq && welcome.Base is { } snapshot) _ = ResyncAsync(snapshot, replayFollows: true);
    }

    private void OnReplayDone()
    {
        if (_resyncing) { _replayDoneDuringResync = true; return; }
        _replaying = false;
        foreach (var op in _pending.Where(p => !p.Sent)) SendOp(op);
        if (_wantedLocks.Count > 0) Send(new AcquireLocks("relock", [.. _wantedLocks]));
        if (_lastPresence is not null) Send(new UpdatePresence(JsonDocument.Parse(_lastPresence).RootElement.Clone()));
        TryUploadSnapshot();
    }

    private void Apply(OpApplied op)
    {
        long seq = Seq;
        if (op.Seq <= seq) return;
        if (op.Seq != seq + 1)
        {
            // A gap (a message lost on the way): ask again from the last op applied.
            Send(new RequestResync(seq));
            return;
        }
        int own = You is { } you && op.AccountId == you.AccountId ? _pending.FindIndex(p => p.Id == op.ClientOpId) : -1;
        if (own >= 0) _pending.RemoveAt(own);   // ours, already applied locally
        else if (ReadDelta(op.Ops) is { } delta)
        {
            // Someone else's: the objects of our edits still on their way stay ours (they come
            // after this one in the server's order).
            var remote = _pending.Count == 0 ? delta : delta.Except(_pending.Select(p => p.Delta));
            try { _document.ApplyExternal(remote); RemoteEdit?.Invoke(remote); }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException)
            {
                Notice?.Invoke("An edit from the session could not be applied: reloading the session's project.");
                _ = ResyncAsync(Session.Snapshot);
            }
        }
        Interlocked.Exchange(ref _seq, op.Seq);
        if (_pending.Count == 0) TryUploadSnapshot();
    }

    private static ProjectDelta? ReadDelta(JsonElement ops)
    {
        try
        {
            if (ops.ValueKind != JsonValueKind.Object || !ops.TryGetProperty("delta", out var delta)) return null;
            return ProjectDelta.FromJson(delta);
        }
        catch (JsonException) { return null; }
    }

    private void OnRejected(OpRejected rejected)
    {
        int index = _pending.FindIndex(p => p.Id == rejected.ClientOpId);
        if (index < 0) return;
        var op = _pending[index];
        if (rejected.Code == CollabErrors.RateLimited)
        {
            op.Sent = false;
            _ = Task.Delay(1000).ContinueWith(_ => _post(() => { if (!op.Sent && _pending.Contains(op) && !_replaying) SendOp(op); }), TaskScheduler.Default);
            return;
        }
        // The session refused the edit (viewer, too large, log full...): back to the session's state.
        Notice?.Invoke("Your last edit was refused by the session: " + rejected.Message);
        _ = ResyncAsync(Session.Snapshot);
    }

    private void OnRoleChanged(RoleChanged role)
    {
        foreach (var (id, p) in _participants.ToArray())
            if (p.AccountId == role.AccountId) _participants[id] = p with { Role = role.Role };
        if (You is { } you && you.AccountId == role.AccountId)
        {
            You = you with { Role = role.Role };
            _document.ReadOnly = !CanEdit;
            Notice?.Invoke(role.Role switch { SessionRoles.Owner => "You now own the session.", SessionRoles.Editor => "You can edit the session now.", _ => "You are a viewer now: you can watch and chat." });
        }
    }

    // Reloads the project from a snapshot (or the newest one), then replays the ops after it.
    private async Task ResyncAsync(SnapshotInfo? snapshot, bool replayFollows = false)
    {
        if (_resyncing) return;
        _resyncing = true;
        try
        {
            ProjectData project;
            long baseSeq;
            if (snapshot is null)
            {
                project = ProjectData.Create(Session.Name);
                baseSeq = 0;
            }
            else
            {
                var (info, content) = await _client.DownloadSnapshotAsync(Code, _stop.Token).ConfigureAwait(false);
                project = _packager.Unpack(content);
                baseSeq = info.Seq;
            }
            var done = new TaskCompletionSource();
            _post(() =>
            {
                try
                {
                    Detach(_document);
                    _document = _rebuild(project);
                    Attach(_document);
                    _pending.Clear();
                    Interlocked.Exchange(ref _seq, baseSeq);
                    DocumentReplaced?.Invoke(_document);
                    // Ops after the base that arrived meanwhile, in order; ask for the rest.
                    var held = _held.Where(o => o.Seq > baseSeq).OrderBy(o => o.Seq).ToList();
                    _held.Clear();
                    _resyncing = false;
                    foreach (var op in held) Apply(op);
                    if (!replayFollows) Send(new RequestResync(Seq));
                    if (_replayDoneDuringResync) { _replayDoneDuringResync = false; OnReplayDone(); }
                    Changed?.Invoke();
                }
                finally { done.TrySetResult(); }
            });
            await done.Task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CollabApiException or HttpRequestException or InvalidDataException or JsonException or IOException)
        {
            _post(() => { _resyncing = false; End("The session's project could not be loaded: " + ex.Message); });
        }
        catch (OperationCanceledException) { _post(() => _resyncing = false); }
    }

    // --- Local edits (UI thread) ------------------------------------------------------------------------

    private void OnEdited(ProjectData before, ProjectData after, string name)
    {
        if (State == LiveState.Ended) return;
        var delta = ProjectDelta.Between(before, after);
        if (delta.IsEmpty) return;
        foreach (var piece in delta.Split(OpPieceBytes))
        {
            var op = new PendingOp($"{Environment.ProcessId:x}-{++_opCounter}-{Random.Shared.Next():x}", piece, name);
            _pending.Add(op);
            if (State == LiveState.Live && !_replaying && !_resyncing) SendOp(op);
        }
        LocalEdit?.Invoke(delta);   // after the pending ops: a snapshot asked for now waits for them
        Changed?.Invoke();
    }

    private void SendOp(PendingOp op)
    {
        string json = $$"""{"v":1,"name":{{JsonSerializer.Serialize(op.Name)}},"delta":{{op.Delta.ToJson()}}}""";
        using var document = JsonDocument.Parse(json);
        op.Sent = true;
        Send(new SendOp(op.Id, document.RootElement.Clone()));
    }

    // --- Presence, locks, chat ------------------------------------------------------------------------

    // Throttled to ten updates a second; unchanged presence is not sent again.
    public void UpdatePresence(PresenceData presence)
    {
        string json = JsonSerializer.Serialize(presence, CollabProtocol.Json);
        long now = Environment.TickCount64;
        if (json == _lastPresence || now - _lastPresenceAt < 100) { _lastPresence = json; return; }
        _lastPresence = json; _lastPresenceAt = now;
        if (State == LiveState.Live && !_replaying) Send(new UpdatePresence(JsonDocument.Parse(json).RootElement.Clone()));
    }

    // Locks follow the selection: what is selected is locked for this account (when free).
    public void SetSelection(IEnumerable<string> ids)
    {
        if (!CanEdit) return;
        var wanted = ids.Where(id => id.Length is > 0 and <= 256).Take(CollabProtocol.MaxLockIds).ToHashSet(StringComparer.Ordinal);
        var mine = _locks.Values.Where(l => l.AccountId == You?.AccountId).Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        var release = mine.Where(id => !wanted.Contains(id)).ToArray();
        var acquire = wanted.Where(id => !mine.Contains(id)).ToArray();
        _wantedLocks = wanted;
        if (State != LiveState.Live || _replaying) return;
        if (release.Length > 0) Send(new ReleaseLocks(release));
        if (acquire.Length > 0) Send(new AcquireLocks("selection", acquire));
    }

    // The participant who holds this object's lock, when it is someone else.
    public ParticipantInfo? LockedByOther(string id)
    {
        if (!_locks.TryGetValue(id, out var info) || info.AccountId == You?.AccountId) return null;
        return _participants.GetValueOrDefault(info.ParticipantId) ?? new ParticipantInfo(info.ParticipantId, info.AccountId, NameOf(info.AccountId), SessionRoles.Editor, DateTimeOffset.UtcNow, null);
    }

    public void SendChat(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        if (text.Length > CollabProtocol.MaxChatLength) text = text[..CollabProtocol.MaxChatLength];
        Send(new SendChat(text));
    }

    // --- Snapshots ------------------------------------------------------------------------------------

    // The owner answers snapshot requests; without the owner online, the earliest connected editor.
    private bool ShouldSnapshot()
    {
        if (!CanEdit || You is not { } you) return false;
        if (IsOwner) return true;
        if (_participants.Values.Any(p => p.Role == SessionRoles.Owner)) return false;
        return _participants.Values.Where(p => SessionRoles.CanEdit(p.Role)).OrderBy(p => p.ConnectedAt).ThenBy(p => p.ParticipantId).FirstOrDefault()?.ParticipantId == you.ParticipantId;
    }

    // A snapshot is the project exactly at the last applied op: only taken when none of our edits
    // is still on its way.
    private void TryUploadSnapshot()
    {
        if (!_snapshotWanted || _uploadingSnapshot || _pending.Count > 0 || _replaying || _resyncing || State != LiveState.Live) return;
        _snapshotWanted = false;
        _ = UploadSnapshotAsync();
    }

    // A snapshot as soon as our edits are confirmed (files only a snapshot carries, such as a custom
    // mesh just placed, reach the others that way).
    public void RequestSnapshot()
    {
        if (!CanEdit || State == LiveState.Ended) return;
        _snapshotWanted = true;
        TryUploadSnapshot();
    }

    public async Task<SnapshotInfo?> UploadSnapshotAsync()
    {
        if (_uploadingSnapshot) return null;
        _uploadingSnapshot = true;
        try
        {
            long seq = Seq;
            var project = _document.Project;   // immutable: packed off the UI thread
            byte[] content = await Task.Run(() => _packager.Pack(project)).ConfigureAwait(false);
            return await _client.UploadSnapshotAsync(Code, content, seq, _packager.ContentType, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CollabApiException or HttpRequestException or InvalidDataException or IOException) { _post(() => Notice?.Invoke("The session snapshot could not be saved: " + ex.Message)); return null; }
        catch (OperationCanceledException) { return null; }
        finally { _uploadingSnapshot = false; }
    }

    // The project as it is now as the session's newest snapshot, once this client's edits are
    // confirmed (saving a shared world: the service copies that snapshot into the world). Call it
    // from the thread of `post`. Null: it could not be made in time.
    public async Task<SnapshotInfo?> SnapshotNowAsync(TimeSpan wait)
    {
        var until = DateTime.UtcNow + wait;
        while ((_pending.Count > 0 || _uploadingSnapshot || _replaying || _resyncing) && State == LiveState.Live && DateTime.UtcNow < until) await Task.Delay(100);
        if (_pending.Count > 0 || _resyncing || State != LiveState.Live) return null;
        if (Session.Snapshot is { } current && current.Seq == Seq) return current;
        return await UploadSnapshotAsync();
    }

    // --- Ending ---------------------------------------------------------------------------------------

    private void End(string reason)
    {
        if (State == LiveState.Ended) return;
        State = LiveState.Ended;
        EndReason = reason;
        if (_document is not null) Detach(_document);
        _stop.Cancel();
        _outgoing.Writer.TryComplete();
        Notice?.Invoke(reason);
        Changed?.Invoke();
    }

    // Leaves the session (the owner closes it instead: CloseAsync). The document stays as it is.
    public async Task LeaveAsync()
    {
        try { if (!IsOwner) await _client.LeaveSessionAsync(Code).ConfigureAwait(false); }
        catch (CollabApiException) { }
        _post(() => End("You left the session."));
    }

    public async Task CloseAsync()
    {
        await _client.CloseSessionAsync(Code).ConfigureAwait(false);
        _post(() => End("You closed the session."));
    }

    // Disconnects (without leaving: the account stays a member). The document is released as it is.
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _outgoing.Writer.TryComplete();
        if (_receiver is not null) try { await _receiver.ConfigureAwait(false); } catch (OperationCanceledException) { }
        if (State != LiveState.Ended)
        {
            State = LiveState.Ended;
            EndReason ??= "Disconnected from the session.";
            if (_document is not null) Detach(_document);
        }
        // _stop is not disposed: callbacks still queued on the UI thread may cancel it again.
    }
}
