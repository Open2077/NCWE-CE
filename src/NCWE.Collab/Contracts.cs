// SPDX-License-Identifier: GPL-3.0-only
namespace NCWE.Collab;

// REST payloads of the collaboration service (JSON, camelCase). Every route but /v1/health takes
// the editor's Open//77 session token: "Authorization: Bearer <token>".

// --- Accounts -------------------------------------------------------------------------------------

public sealed record AccountInfo(Guid AccountId, string DisplayName);

// SharedProjects: the shared worlds the account takes part in (its own once shared, and those it joined).
public sealed record CloudUsage(long UsedBytes, long QuotaBytes, int Projects, int MaxProjects, int SharedProjects = 0, int MaxSharedProjects = 0);

// What an account may use. The service's defaults, unless the account has its own (a plan): every
// limit can differ per account (subscriptions).
public sealed record AccountLimits(long QuotaBytes, int MaxProjects, int VersionsKept, int MaxSharedProjects, int MaxCollaborators, int MaxOpenSessions);

public sealed record MeResponse(AccountInfo Account, string? PlatformRole, CloudUsage Cloud, int OpenSessions, int MaxOpenSessions, string? Plan = null, AccountLimits? Limits = null);

// Development servers only (the dev authentication): an account's plan name and the limits that
// differ from the defaults. Production sets them in the database (scripts/collab-limits.ps1).
public sealed record AccountLimitsOverride(long? QuotaBytes = null, int? MaxProjects = null, int? VersionsKept = null, int? MaxSharedProjects = null,
    int? MaxCollaborators = null, int? MaxOpenSessions = null);

public sealed record SetAccountLimitsRequest(string? Plan = null, AccountLimitsOverride? Limits = null);

public sealed record ApiError(string Code, string Message);

public sealed record ApiErrorResponse(ApiError Error);

// --- Sessions -------------------------------------------------------------------------------------

// A session is found by its code ("ABCD-EFGH", sent with or without the dash) and entered once
// with its password: the account then stays a member (it reconnects without the password) until
// it leaves, is kicked or banned, or the session closes.
public sealed record CreateSessionRequest(
    string Name,
    string Password,
    int? MaxParticipants = null,
    string? DefaultRole = null,
    // Optional: start from a version of one of the owner's cloud projects (it becomes snapshot 0).
    Guid? ProjectId = null,
    int? ProjectVersion = null);

public sealed record UpdateSessionRequest(string? Name = null, string? Password = null, int? MaxParticipants = null, string? DefaultRole = null, bool? Locked = null);

public sealed record JoinSessionRequest(string Password);

public sealed record KickRequest(Guid AccountId, bool Ban = false, string? Reason = null);

public sealed record RoleRequest(Guid AccountId, string Role);

public sealed record TransferRequest(Guid AccountId);

public sealed record SessionInfo(
    string Id,
    string Name,
    AccountInfo Owner,
    DateTimeOffset CreatedAt,
    bool Locked,
    int MaxParticipants,
    string DefaultRole,
    int Online,
    int Members,
    long Seq,
    SnapshotInfo? Snapshot,
    // The caller's role ("owner", "editor", "viewer").
    string YourRole,
    // WebSocket path of the live connection (relative to the service).
    string LivePath,
    // The shared world the session works on (its members enter without the password).
    Guid? ProjectId = null);

// What a non-member sees of a session before entering its password.
public sealed record SessionPreview(string Id, string Name, string OwnerName, int Online, int MaxParticipants, bool Locked);

public sealed record SessionListResponse(IReadOnlyList<SessionInfo> Sessions);

public sealed record CreateSessionResponse(SessionInfo Session);

public sealed record JoinSessionResponse(SessionInfo Session);

public sealed record MemberInfo(Guid AccountId, string DisplayName, string Role, bool Online, DateTimeOffset JoinedAt, DateTimeOffset LastSeenAt);

public sealed record MemberListResponse(IReadOnlyList<MemberInfo> Members);

public sealed record BanInfo(Guid AccountId, string DisplayName, Guid BannedBy, string? Reason, DateTimeOffset CreatedAt);

public sealed record BanListResponse(IReadOnlyList<BanInfo> Bans);

// --- Snapshots (a session's full project state at an op sequence number) ---------------------------

public sealed record SnapshotInfo(string Id, long Seq, long Size, string Sha256, string ContentType, DateTimeOffset CreatedAt, Guid CreatedBy);

// size in bytes, sha256 in lowercase hex; seq: the last op applied to the uploaded state.
public sealed record BeginSnapshotRequest(long Size, string Sha256, long Seq, string? ContentType = null);

// Send the body with this method, to this URL (relative URLs are relative to the service), with
// exactly these headers. Object storage checks the SHA-256.
public sealed record UploadInstructions(string Method, string Url, IReadOnlyDictionary<string, string> Headers, DateTimeOffset ExpiresAt);

public sealed record DownloadInstructions(string Url, DateTimeOffset ExpiresAt, long Size, string Sha256);

public sealed record BeginSnapshotResponse(string SnapshotId, UploadInstructions Upload);

public sealed record SnapshotResponse(SnapshotInfo Snapshot);

public sealed record SnapshotDownloadResponse(SnapshotInfo Snapshot, DownloadInstructions Download);

// The owner keeps the session's latest snapshot as a version of one of their cloud projects (a new
// project named Name when ProjectId is absent).
public sealed record SaveSnapshotRequest(Guid? ProjectId = null, string? Name = null, string? Note = null);

// --- Cloud projects ---------------------------------------------------------------------------------

// Shared: the owner invited others (an invite code or members). YourRole: owner, editor or viewer.
// Live: the session its members are working in right now, if any.
public sealed record CloudProjectInfo(Guid Id, string Name, string? Description, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int LatestVersion, int Versions, long Bytes,
    AccountInfo? Owner = null, string? YourRole = null, bool Shared = false, int Members = 0, ProjectLiveInfo? Live = null);

public sealed record ProjectLiveInfo(string Code, AccountInfo StartedBy, DateTimeOffset StartedAt, int Online);

public sealed record CloudVersionInfo(int Version, long Size, string Sha256, string ContentType, string? Note, Guid AuthorId, DateTimeOffset CreatedAt, string Status,
    string? AuthorName = null);

public sealed record CreateProjectRequest(string Name, string? Description = null);

public sealed record UpdateProjectRequest(string? Name = null, string? Description = null);

// BaseVersion: the version the uploader started from; refused (version_conflict) when someone saved
// a newer one meanwhile. Null: saved as the newest whatever came before.
public sealed record BeginVersionRequest(long Size, string Sha256, string? Note = null, string? ContentType = null, int? BaseVersion = null);

public sealed record BeginVersionResponse(CloudVersionInfo Version, UploadInstructions Upload);

public sealed record ProjectResponse(CloudProjectInfo Project);

// Invite: the project's invite code (its owner only sees it).
public sealed record ProjectDetailsResponse(CloudProjectInfo Project, IReadOnlyList<CloudVersionInfo> Versions, ProjectInviteInfo? Invite = null);

// --- Shared worlds ----------------------------------------------------------------------------------

// A code the owner gives to the people who should work on the world with them. Entering it makes
// an account a member with this role, until the code expires or is replaced.
public sealed record ProjectInviteInfo(string Code, string Role, DateTimeOffset ExpiresAt);

public sealed record CreateInviteRequest(string? Role = null, int? ExpiresInHours = null);

public sealed record InviteResponse(ProjectInviteInfo Invite);

public sealed record JoinSharedRequest(string Code);

public sealed record ProjectMemberInfo(Guid AccountId, string DisplayName, string Role, DateTimeOffset AddedAt);

public sealed record ProjectMembersResponse(IReadOnlyList<ProjectMemberInfo> Members);

public sealed record ProjectRoleRequest(string Role);

// Opening a shared world: the live session its members work in (joined, or Started from the latest version).
public sealed record ProjectLiveResponse(SessionInfo Session, bool Started);

public sealed record ProjectListResponse(IReadOnlyList<CloudProjectInfo> Projects, CloudUsage Usage);

public sealed record VersionResponse(CloudVersionInfo Version);

public sealed record VersionDownloadResponse(CloudVersionInfo Version, DownloadInstructions Download);

public static class VersionStatus
{
    public const string Pending = "pending", Ready = "ready";
}

// --- Service ----------------------------------------------------------------------------------------

public sealed record HealthResponse(string Status, string Version, int Protocol, string Storage, DateTimeOffset Time);
