// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NCWE.Collab;

// Wire constants of the collaboration service: protocol version, roles, WebSocket close codes and
// the limits both sides enforce.
public static class CollabProtocol
{
    // Bumped on any incompatible change of the live messages; the server refuses other versions.
    public const int Version = 1;
    public const string Subprotocol = "ncwe-collab.v1";
    // The production service (the Cloudflare Worker in workers/collab).
    public const string DefaultService = "https://collab.open77.dev/";

    public const int MaxMessageBytes = 1 << 20;      // one WebSocket message, any type
    public const int MaxOpBytes = 512 * 1024;         // the "ops" payload of one op message
    public const int MaxPresenceBytes = 8 * 1024;
    public const int MaxChatLength = 2000;
    public const int MaxLockIds = 256;
    public const int MaxClientOpIdLength = 64;

    // The JSON options of every payload (camelCase, as the Open//77 platform).
    public static readonly JsonSerializerOptions Json = CreateJson();

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // Live messages may carry their "type" anywhere in the object.
            AllowOutOfOrderMetadataProperties = true
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

// What a member may do in a session. The owner manages it (kick, ban, roles, settings, close);
// editors send edits, take locks and upload snapshots; viewers watch, talk and show presence.
public static class SessionRoles
{
    public const string Owner = "owner", Editor = "editor", Viewer = "viewer";

    public static bool IsValidAssignable(string? role) => role is Editor or Viewer;
    public static bool CanEdit(string role) => role is Owner or Editor;
}

// WebSocket close codes (4000-4999 are the application's).
public static class CollabCloseCodes
{
    public const int ProtocolError = 4000;     // a malformed or unexpected message
    public const int Unauthorized = 4001;      // no valid Open//77 session
    public const int NotMember = 4002;         // join the session (password) first
    public const int Kicked = 4003;            // removed by the owner (banned or not)
    public const int SessionClosed = 4004;     // the owner closed it, or it expired
    public const int Replaced = 4005;          // the same account connected again elsewhere
    public const int Full = 4006;              // the session has its maximum of participants online
    public const int TooSlow = 4008;           // the client did not read fast enough: reconnect and resync
    public const int AccountRevoked = 4010;    // the Open//77 session ended while connected
    public const int RateLimited = 4029;
    public const int ServerShutdown = 4050;
}

// Error codes of REST replies ({"error":{"code","message"}}) and live "error"/"op_rejected" messages.
public static class CollabErrors
{
    public const string Unauthorized = "unauthorized", AccountServiceUnavailable = "account_service_unavailable",
        NotFound = "not_found", Forbidden = "forbidden", InvalidRequest = "invalid_request",
        InvalidPassword = "invalid_password", Banned = "banned", SessionFull = "session_full", SessionLocked = "session_locked",
        TooManyAttempts = "too_many_attempts", TooManySessions = "too_many_sessions", NotMember = "not_member",
        OwnerOnly = "owner_only", ReadOnly = "read_only", QuotaExceeded = "quota_exceeded", TooLarge = "too_large",
        TooManyProjects = "too_many_projects", UploadMissing = "upload_missing", UploadMismatch = "upload_mismatch",
        Conflict = "conflict", SnapshotRequired = "snapshot_required", RateLimited = "rate_limited",
        StorageUnavailable = "storage_unavailable", UnsupportedProtocol = "unsupported_protocol",
        VersionConflict = "version_conflict", ProjectFull = "project_full", TooManyShared = "too_many_shared", NotLive = "not_live";
}
