// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NCWE.Collab;

// The live connection: GET /v1/sessions/{id}/live upgraded to a WebSocket (subprotocol
// "ncwe-collab.v1", the Open//77 bearer token in the Authorization header, optional ?since=<seq>).
// Text frames, one JSON object per message, discriminated by "type".
//
// The server orders every edit: each "op" a client sends comes back to everyone (the sender
// included, with its clientOpId) stamped with the session's next sequence number. Apply ops in seq
// order on top of the latest snapshot; ops are opaque to the server (the editor defines them).

// --- Client to server -------------------------------------------------------------------------------

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(SendOp), "op")]
[JsonDerivedType(typeof(UpdatePresence), "presence")]
[JsonDerivedType(typeof(AcquireLocks), "lock")]
[JsonDerivedType(typeof(ReleaseLocks), "unlock")]
[JsonDerivedType(typeof(SendChat), "chat")]
[JsonDerivedType(typeof(Ping), "ping")]
[JsonDerivedType(typeof(RequestResync), "resync")]
public abstract record ClientMessage;

// An edit (or a batch of edits applied together). clientOpId: the sender's own id, echoed back.
public sealed record SendOp(string ClientOpId, JsonElement Ops) : ClientMessage;

// Where the user is and what they look at (camera, selection, tool...): opaque, the latest one is
// kept and shown to newcomers. Sent at most ~20 times a second; extra updates are dropped.
public sealed record UpdatePresence(JsonElement Data) : ClientMessage;

// Exclusive edit locks on object ids (released on unlock, disconnect, kick or demotion to viewer).
public sealed record AcquireLocks(string RequestId, string[] Ids) : ClientMessage;

public sealed record ReleaseLocks(string[] Ids) : ClientMessage;

public sealed record SendChat(string Text) : ClientMessage;

public sealed record Ping(long T) : ClientMessage;

// Replays the ops after `since` (after a gap, or when the client fell behind).
public sealed record RequestResync(long Since) : ClientMessage;

// --- Server to client -------------------------------------------------------------------------------

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(Welcome), "welcome")]
[JsonDerivedType(typeof(ParticipantJoined), "joined")]
[JsonDerivedType(typeof(ParticipantLeft), "left")]
[JsonDerivedType(typeof(OpApplied), "op")]
[JsonDerivedType(typeof(OpRejected), "op_rejected")]
[JsonDerivedType(typeof(PresenceChanged), "presence")]
[JsonDerivedType(typeof(LocksChanged), "locks")]
[JsonDerivedType(typeof(LockResult), "lock_result")]
[JsonDerivedType(typeof(ChatPosted), "chat")]
[JsonDerivedType(typeof(RoleChanged), "role_changed")]
[JsonDerivedType(typeof(SessionUpdated), "session_updated")]
[JsonDerivedType(typeof(SnapshotAvailable), "snapshot")]
[JsonDerivedType(typeof(SnapshotRequested), "snapshot_requested")]
[JsonDerivedType(typeof(ResyncRequired), "resync_required")]
[JsonDerivedType(typeof(ReplayDone), "replay_done")]
[JsonDerivedType(typeof(Kicked), "kicked")]
[JsonDerivedType(typeof(SessionClosed), "session_closed")]
[JsonDerivedType(typeof(LiveError), "error")]
[JsonDerivedType(typeof(Pong), "pong")]
public abstract record ServerMessage;

public sealed record ParticipantInfo(string ParticipantId, Guid AccountId, string DisplayName, string Role, DateTimeOffset ConnectedAt, string? Client);

public sealed record PresenceState(string ParticipantId, Guid AccountId, JsonElement Data);

public sealed record LockInfo(string Id, Guid AccountId, string ParticipantId);

// First message of a connection. Base: the snapshot to load first (null: the session started
// empty); then the server sends the ops from ReplayFrom + 1 to Seq ("op" messages, then
// "replay_done"), then live traffic.
public sealed record Welcome(
    int Protocol,
    SessionInfo Session,
    ParticipantInfo You,
    IReadOnlyList<ParticipantInfo> Participants,
    long Seq,
    SnapshotInfo? Base,
    long ReplayFrom,
    IReadOnlyList<PresenceState> Presence,
    IReadOnlyList<LockInfo> Locks) : ServerMessage;

public sealed record ParticipantJoined(ParticipantInfo Participant) : ServerMessage;

// reason: "left", "kicked", "banned", "timeout", "replaced", "closed".
public sealed record ParticipantLeft(string ParticipantId, Guid AccountId, string Reason) : ServerMessage;

public sealed record OpApplied(long Seq, Guid AccountId, string ParticipantId, string ClientOpId, JsonElement Ops, DateTimeOffset At) : ServerMessage;

public sealed record OpRejected(string ClientOpId, string Code, string Message) : ServerMessage;

public sealed record PresenceChanged(string ParticipantId, Guid AccountId, JsonElement Data) : ServerMessage;

public sealed record LocksChanged(IReadOnlyList<LockInfo> Acquired, IReadOnlyList<string> Released) : ServerMessage;

public sealed record LockResult(string RequestId, IReadOnlyList<string> Granted, IReadOnlyList<LockInfo> Denied) : ServerMessage;

public sealed record ChatPosted(string ParticipantId, Guid AccountId, string DisplayName, string Text, DateTimeOffset At) : ServerMessage;

public sealed record RoleChanged(Guid AccountId, string Role) : ServerMessage;

// The session's settings or owner changed (name, lock, maximum, owner...).
public sealed record SessionUpdated(SessionInfo Session) : ServerMessage;

// A newer snapshot exists: clients that fall behind can restart from it.
public sealed record SnapshotAvailable(SnapshotInfo Snapshot) : ServerMessage;

// The op log is long: an editor should upload a snapshot (POST .../snapshots) at its current seq.
public sealed record SnapshotRequested(long Seq, int OpsSinceSnapshot, bool Required) : ServerMessage;

// The ops the client asked for are no longer kept: load Base (GET .../snapshot), then the server
// replays from Base.Seq.
public sealed record ResyncRequired(SnapshotInfo? Base, long ReplayFrom, string Reason) : ServerMessage;

public sealed record ReplayDone(long Seq) : ServerMessage;

public sealed record Kicked(string? Reason, bool Banned) : ServerMessage;

public sealed record SessionClosed(string Reason) : ServerMessage;

public sealed record LiveError(string Code, string Message) : ServerMessage;

public sealed record Pong(long T, DateTimeOffset ServerTime) : ServerMessage;
