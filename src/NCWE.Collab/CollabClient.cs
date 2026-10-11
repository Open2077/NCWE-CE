// SPDX-License-Identifier: GPL-3.0-only
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace NCWE.Collab;

// A refusal of the collaboration service (its HTTP status and error code).
public sealed class CollabApiException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

// Opens the live WebSocket (tests plug their in-memory server in here).
public delegate Task<WebSocket> LiveConnector(Uri uri, IReadOnlyDictionary<string, string> headers, string subprotocol, CancellationToken cancellation);

// Reference client of the collaboration service, for NCWE Studio and tests: every REST call, the
// upload/download dance with object storage (SHA-256 checked both ways) and the live connection.
// token: the account's Open//77 session token (Open77Session.Token in Studio).
public sealed class CollabClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly HttpClient? _storage;
    private readonly bool _ownsHttp;
    private readonly LiveConnector _connect;

    // storage: the client for object storage URLs on another host (R2); http by default.
    public CollabClient(Uri service, string token, HttpClient? http = null, LiveConnector? connect = null, HttpClient? storage = null)
    {
        _storage = storage;
        Service = service.AbsoluteUri.EndsWith('/') ? service : new Uri(service.AbsoluteUri + "/");
        Token = token;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _ownsHttp = http is null;
        _connect = connect ?? ConnectDefault;
    }

    public Uri Service { get; }
    public string Token { get; set; }

    // --- Account and sessions ---------------------------------------------------------------------

    public Task<MeResponse> MeAsync(CancellationToken cancellation = default) => Send<MeResponse>(HttpMethod.Get, "v1/me", null, cancellation);

    public async Task<SessionInfo> CreateSessionAsync(CreateSessionRequest request, CancellationToken cancellation = default)
        => (await Send<CreateSessionResponse>(HttpMethod.Post, "v1/sessions", request, cancellation)).Session;

    public async Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(CancellationToken cancellation = default)
        => (await Send<SessionListResponse>(HttpMethod.Get, "v1/sessions", null, cancellation)).Sessions;

    public Task<SessionInfo> GetSessionAsync(string code, CancellationToken cancellation = default) => Send<SessionInfo>(HttpMethod.Get, $"v1/sessions/{Code(code)}", null, cancellation);

    public Task<SessionPreview> PreviewSessionAsync(string code, CancellationToken cancellation = default) => Send<SessionPreview>(HttpMethod.Get, $"v1/sessions/{Code(code)}/preview", null, cancellation);

    public async Task<SessionInfo> JoinSessionAsync(string code, string password, CancellationToken cancellation = default)
        => (await Send<JoinSessionResponse>(HttpMethod.Post, $"v1/sessions/{Code(code)}/join", new JoinSessionRequest(password), cancellation)).Session;

    public Task LeaveSessionAsync(string code, CancellationToken cancellation = default) => Send(HttpMethod.Post, $"v1/sessions/{Code(code)}/leave", null, cancellation);

    public Task<SessionInfo> UpdateSessionAsync(string code, UpdateSessionRequest request, CancellationToken cancellation = default)
        => Send<SessionInfo>(HttpMethod.Patch, $"v1/sessions/{Code(code)}", request, cancellation);

    public Task CloseSessionAsync(string code, CancellationToken cancellation = default) => Send(HttpMethod.Delete, $"v1/sessions/{Code(code)}", null, cancellation);

    public async Task<IReadOnlyList<MemberInfo>> MembersAsync(string code, CancellationToken cancellation = default)
        => (await Send<MemberListResponse>(HttpMethod.Get, $"v1/sessions/{Code(code)}/members", null, cancellation)).Members;

    public Task KickAsync(string code, Guid account, bool ban = false, string? reason = null, CancellationToken cancellation = default)
        => Send(HttpMethod.Post, $"v1/sessions/{Code(code)}/kick", new KickRequest(account, ban, reason), cancellation);

    public async Task<IReadOnlyList<BanInfo>> BansAsync(string code, CancellationToken cancellation = default)
        => (await Send<BanListResponse>(HttpMethod.Get, $"v1/sessions/{Code(code)}/bans", null, cancellation)).Bans;

    public Task UnbanAsync(string code, Guid account, CancellationToken cancellation = default) => Send(HttpMethod.Delete, $"v1/sessions/{Code(code)}/bans/{account}", null, cancellation);

    public Task SetRoleAsync(string code, Guid account, string role, CancellationToken cancellation = default)
        => Send(HttpMethod.Post, $"v1/sessions/{Code(code)}/role", new RoleRequest(account, role), cancellation);

    public Task<SessionInfo> TransferAsync(string code, Guid account, CancellationToken cancellation = default)
        => Send<SessionInfo>(HttpMethod.Post, $"v1/sessions/{Code(code)}/transfer", new TransferRequest(account), cancellation);

    // --- Snapshots ----------------------------------------------------------------------------------

    // Uploads the project state as it is after op `seq` and makes it the session's snapshot.
    public async Task<SnapshotInfo> UploadSnapshotAsync(string code, byte[] content, long seq, string? contentType = null, CancellationToken cancellation = default)
    {
        var begin = await Send<BeginSnapshotResponse>(HttpMethod.Post, $"v1/sessions/{Code(code)}/snapshots",
            new BeginSnapshotRequest(content.LongLength, Sha256(content), seq, contentType), cancellation);
        await UploadAsync(begin.Upload, content, cancellation);
        return (await Send<SnapshotResponse>(HttpMethod.Post, $"v1/sessions/{Code(code)}/snapshots/{begin.SnapshotId}/commit", null, cancellation)).Snapshot;
    }

    public async Task<(SnapshotInfo Snapshot, byte[] Content)> DownloadSnapshotAsync(string code, CancellationToken cancellation = default)
    {
        var latest = await Send<SnapshotDownloadResponse>(HttpMethod.Get, $"v1/sessions/{Code(code)}/snapshot", null, cancellation);
        return (latest.Snapshot, await DownloadAsync(latest.Download, cancellation));
    }

    public Task<VersionDownloadResponse> SaveSnapshotToCloudAsync(string code, SaveSnapshotRequest request, CancellationToken cancellation = default)
        => Send<VersionDownloadResponse>(HttpMethod.Post, $"v1/sessions/{Code(code)}/snapshot/save", request, cancellation);

    // --- Cloud projects -----------------------------------------------------------------------------

    public Task<ProjectListResponse> ListProjectsAsync(CancellationToken cancellation = default) => Send<ProjectListResponse>(HttpMethod.Get, "v1/projects", null, cancellation);

    public async Task<CloudProjectInfo> CreateProjectAsync(string name, string? description = null, CancellationToken cancellation = default)
        => (await Send<ProjectResponse>(HttpMethod.Post, "v1/projects", new CreateProjectRequest(name, description), cancellation)).Project;

    public Task<ProjectDetailsResponse> GetProjectAsync(Guid project, CancellationToken cancellation = default) => Send<ProjectDetailsResponse>(HttpMethod.Get, $"v1/projects/{project}", null, cancellation);

    public async Task<CloudProjectInfo> UpdateProjectAsync(Guid project, UpdateProjectRequest request, CancellationToken cancellation = default)
        => (await Send<ProjectResponse>(HttpMethod.Patch, $"v1/projects/{project}", request, cancellation)).Project;

    public Task DeleteProjectAsync(Guid project, CancellationToken cancellation = default) => Send(HttpMethod.Delete, $"v1/projects/{project}", null, cancellation);

    // Uploads a new version of a project (the file as NCWE saves it) and commits it. baseVersion: the
    // version the content started from (refused with version_conflict when a newer one exists).
    public async Task<CloudVersionInfo> UploadVersionAsync(Guid project, byte[] content, string? note = null, string? contentType = null, CancellationToken cancellation = default,
        int? baseVersion = null)
    {
        var begin = await Send<BeginVersionResponse>(HttpMethod.Post, $"v1/projects/{project}/versions", new BeginVersionRequest(content.LongLength, Sha256(content), note, contentType, baseVersion), cancellation);
        await UploadAsync(begin.Upload, content, cancellation);
        return (await Send<VersionResponse>(HttpMethod.Post, $"v1/projects/{project}/versions/{begin.Version.Version}/commit", null, cancellation)).Version;
    }

    // version: a number, or null for the latest.
    public async Task<(CloudVersionInfo Version, byte[] Content)> DownloadVersionAsync(Guid project, int? version = null, CancellationToken cancellation = default)
    {
        var download = await Send<VersionDownloadResponse>(HttpMethod.Get, $"v1/projects/{project}/versions/{(version is { } v ? v.ToString(System.Globalization.CultureInfo.InvariantCulture) : "latest")}/download", null, cancellation);
        return (download.Version, await DownloadAsync(download.Download, cancellation));
    }

    public Task DeleteVersionAsync(Guid project, int version, CancellationToken cancellation = default) => Send(HttpMethod.Delete, $"v1/projects/{project}/versions/{version}", null, cancellation);

    // --- Shared worlds -------------------------------------------------------------------------------

    // A new invite code for the project (the previous one stops working).
    public async Task<ProjectInviteInfo> CreateInviteAsync(Guid project, string? role = null, int? expiresInHours = null, CancellationToken cancellation = default)
        => (await Send<InviteResponse>(HttpMethod.Post, $"v1/projects/{project}/invite", new CreateInviteRequest(role, expiresInHours), cancellation)).Invite;

    public Task RevokeInviteAsync(Guid project, CancellationToken cancellation = default) => Send(HttpMethod.Delete, $"v1/projects/{project}/invite", null, cancellation);

    // Enters a shared world with its invite code (the account becomes a member).
    public async Task<CloudProjectInfo> JoinSharedAsync(string code, CancellationToken cancellation = default)
        => (await Send<ProjectResponse>(HttpMethod.Post, "v1/shared/join", new JoinSharedRequest(code), cancellation)).Project;

    public async Task<IReadOnlyList<ProjectMemberInfo>> ProjectMembersAsync(Guid project, CancellationToken cancellation = default)
        => (await Send<ProjectMembersResponse>(HttpMethod.Get, $"v1/projects/{project}/members", null, cancellation)).Members;

    public Task SetProjectRoleAsync(Guid project, Guid account, string role, CancellationToken cancellation = default)
        => Send(HttpMethod.Patch, $"v1/projects/{project}/members/{account}", new ProjectRoleRequest(role), cancellation);

    // The owner removes a member; a member removes themselves (leaves the shared world).
    public Task RemoveProjectMemberAsync(Guid project, Guid account, CancellationToken cancellation = default)
        => Send(HttpMethod.Delete, $"v1/projects/{project}/members/{account}", null, cancellation);

    // The live session of a shared world: joined when someone is working in it, else started from
    // its latest version (editors and the owner).
    public Task<ProjectLiveResponse> OpenProjectLiveAsync(Guid project, CancellationToken cancellation = default)
        => Send<ProjectLiveResponse>(HttpMethod.Post, $"v1/projects/{project}/live", null, cancellation);

    // Development servers only: an account's plan and limits.
    public Task<MeResponse> SetAccountLimitsAsync(Guid account, SetAccountLimitsRequest request, CancellationToken cancellation = default)
        => Send<MeResponse>(HttpMethod.Put, $"v1/dev/accounts/{account}/limits", request, cancellation);

    // --- Live ---------------------------------------------------------------------------------------

    // since: the last op seq this client applied (null: start from the snapshot).
    public async Task<CollabLiveConnection> ConnectAsync(string code, long? since = null, string? clientName = null, CancellationToken cancellation = default)
    {
        string query = string.Join('&', new[] { since is { } s ? $"since={s}" : null, clientName is { } c ? "client=" + Uri.EscapeDataString(c) : null }.OfType<string>());
        var http = new Uri(Service, $"v1/sessions/{Code(code)}/live" + (query.Length > 0 ? "?" + query : ""));
        var ws = new UriBuilder(http) { Scheme = http.Scheme == Uri.UriSchemeHttps ? "wss" : "ws" }.Uri;
        var socket = await _connect(ws, new Dictionary<string, string> { ["Authorization"] = "Bearer " + Token }, CollabProtocol.Subprotocol, cancellation).ConfigureAwait(false);
        return new CollabLiveConnection(socket);
    }

    private static async Task<WebSocket> ConnectDefault(Uri uri, IReadOnlyDictionary<string, string> headers, string subprotocol, CancellationToken cancellation)
    {
        var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol(subprotocol);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        socket.Options.CollectHttpResponseDetails = true;
        foreach (var (name, value) in headers) socket.Options.SetRequestHeader(name, value);
        await socket.ConnectAsync(uri, cancellation).ConfigureAwait(false);
        return socket;
    }

    // --- Plumbing -----------------------------------------------------------------------------------

    public static string Sha256(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    private static string Code(string code) => Uri.EscapeDataString(code);

    private async Task UploadAsync(UploadInstructions upload, byte[] content, CancellationToken cancellation)
    {
        var target = new Uri(Service, upload.Url);
        using var request = new HttpRequestMessage(new HttpMethod(upload.Method), target) { Content = new ByteArrayContent(content) };
        foreach (var (name, value) in upload.Headers)
            if (!request.Headers.TryAddWithoutValidation(name, value)) request.Content.Headers.TryAddWithoutValidation(name, value);
        using var response = await ClientFor(target).SendAsync(request, cancellation).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new CollabApiException((int)response.StatusCode, "upload_failed", $"Object storage refused the upload ({(int)response.StatusCode}).");
    }

    private async Task<byte[]> DownloadAsync(DownloadInstructions download, CancellationToken cancellation)
    {
        var target = new Uri(Service, download.Url);
        using var response = await ClientFor(target).GetAsync(target, cancellation).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new CollabApiException((int)response.StatusCode, "download_failed", $"Object storage refused the download ({(int)response.StatusCode}).");
        byte[] content = await response.Content.ReadAsByteArrayAsync(cancellation).ConfigureAwait(false);
        if (!string.Equals(Sha256(content), download.Sha256, StringComparison.OrdinalIgnoreCase)) throw new CollabApiException(500, "corrupt_download", "The downloaded file does not match its SHA-256.");
        return content;
    }

    private HttpClient ClientFor(Uri target) => _storage is not null && !string.Equals(target.Authority, Service.Authority, StringComparison.OrdinalIgnoreCase) ? _storage : _http;

    private async Task<HttpResponseMessage> Raw(HttpMethod method, string path, object? body, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(method, new Uri(Service, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: CollabProtocol.Json);
        var response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return response;
        using (response)
        {
            ApiError? error = null;
            try { error = (await response.Content.ReadFromJsonAsync<ApiErrorResponse>(CollabProtocol.Json, cancellation).ConfigureAwait(false))?.Error; }
            catch (Exception ex) when (ex is JsonException or NotSupportedException) { }
            throw new CollabApiException((int)response.StatusCode, error?.Code ?? "http_" + (int)response.StatusCode, error?.Message ?? response.ReasonPhrase ?? "Request failed.");
        }
    }

    private async Task<T> Send<T>(HttpMethod method, string path, object? body, CancellationToken cancellation)
    {
        using var response = await Raw(method, path, body, cancellation).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<T>(CollabProtocol.Json, cancellation).ConfigureAwait(false)
            ?? throw new CollabApiException((int)response.StatusCode, "empty_response", "The service sent an empty answer.");
    }

    private async Task Send(HttpMethod method, string path, object? body, CancellationToken cancellation)
    {
        using var response = await Raw(method, path, body, cancellation).ConfigureAwait(false);
    }

    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
}

// A live connection: send client messages, receive server messages in order.
public sealed class CollabLiveConnection(WebSocket socket) : IAsyncDisposable
{
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly byte[] _buffer = new byte[64 * 1024];

    public WebSocketState State => socket.State;
    public WebSocketCloseStatus? CloseStatus => socket.CloseStatus;
    public string? CloseDescription => socket.CloseStatusDescription;

    public async Task SendAsync(ClientMessage message, CancellationToken cancellation = default)
    {
        byte[] frame = JsonSerializer.SerializeToUtf8Bytes(message, CollabProtocol.Json);
        await _send.WaitAsync(cancellation).ConfigureAwait(false);
        try { await socket.SendAsync(frame, WebSocketMessageType.Text, true, cancellation).ConfigureAwait(false); }
        finally { _send.Release(); }
    }

    // The next message, or null once the server closed the connection (see CloseStatus).
    public async Task<ServerMessage?> ReceiveAsync(CancellationToken cancellation = default)
    {
        using var message = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult result;
            try { result = await socket.ReceiveAsync(_buffer, cancellation).ConfigureAwait(false); }
            catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException) { return null; }
            if (result.MessageType == WebSocketMessageType.Close)
            {
                // Answer the server's close (it may already be gone).
                if (socket.State == WebSocketState.CloseReceived)
                    try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException) { }
                return null;
            }
            message.Write(_buffer, 0, result.Count);
            if (result.EndOfMessage) return JsonSerializer.Deserialize<ServerMessage>(message.ToArray(), CollabProtocol.Json);
        }
    }

    public async Task CloseAsync(CancellationToken cancellation = default)
    {
        if (socket.State == WebSocketState.Open)
            try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Bye.", cancellation).ConfigureAwait(false); }
            catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await CloseAsync(timeout.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
        socket.Dispose();
        _send.Dispose();
    }
}
