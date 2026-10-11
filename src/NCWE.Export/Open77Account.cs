// SPDX-License-Identifier: GPL-3.0-only
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NCWE.Export;

// The Open//77 platform: its master (accounts API) and its website (where people sign in).
public sealed record Open77Endpoints(Uri Master, Uri Website)
{
    // Production unless overridden (our own development and tests).
    public static Open77Endpoints Default() => new(
        Resolve("NCWE_OPEN77_MASTER_URL", "https://master.open2077.net/"),
        Resolve("NCWE_OPEN77_WEBSITE_URL", "https://open2077.net/"));

    public Uri AccountPage => new(Website, "account");

    private static Uri Resolve(string variable, string fallback)
    {
        string value = Environment.GetEnvironmentVariable(variable) is { Length: > 0 } configured ? configured.Trim() : fallback;
        return new Uri(value.EndsWith('/') ? value : value + "/", UriKind.Absolute);
    }
}

// A signed-in Open//77 account: its opaque session token (a bearer token for the master) and what
// /accounts/me tells about it.
public sealed record Open77Session(Guid AccountId, string DisplayName, string Token, DateTimeOffset ExpiresAtUtc)
{
    public string? Email { get; init; }
    public bool EmailVerified { get; init; }
    public string? Role { get; init; }
}

// Sign-in the launcher's way (FiveM/Cfx.re style): the browser opens open2077.net/launcher, the
// person signs in there (no password ever typed into Studio) and the site hands a one-time code
// to a loopback callback; PKCE binds it to this Studio, which trades it for a long session at the
// master (POST api/v1/accounts/launcher/token). The editor never needs an account: cloud saves and
// live collaboration do.
public sealed class Open77Account(Open77Endpoints endpoints, HttpClient? http = null) : IDisposable
{
    public static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);
    private readonly HttpClient _http = http ?? CreateClient();
    private readonly bool _ownsHttp = http is null;

    public Open77Endpoints Endpoints => endpoints;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NCWE-Studio", typeof(Open77Account).Assembly.GetName().Version?.ToString() ?? "1"));
        return client;
    }

    // PKCE (RFC 7636, S256) and the anti-forgery state.
    public static (string Verifier, string Challenge, string State) NewPkce()
    {
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        return (verifier, Challenge(verifier), Base64Url(RandomNumberGenerator.GetBytes(16)));
    }

    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    // The website's launcher sign-in page, with this Studio's loopback callback.
    public Uri AuthorizeUrl(string redirect, string state, string challenge) => new UriBuilder(new Uri(endpoints.Website, "launcher"))
    {
        Query = string.Join('&', $"redirect_uri={Uri.EscapeDataString(redirect)}", $"state={state}", $"code_challenge={challenge}", "code_challenge_method=S256", "client=ncwe-studio")
    }.Uri;

    // Opens the browser on the sign-in page and waits for the site to come back (or the timeout,
    // or cancellation, which free the loopback port).
    public async Task<Open77Session> SignInAsync(Action<Uri> openBrowser, TimeSpan timeout, CancellationToken cancellation = default)
    {
        var (verifier, challenge, state) = NewPkce();
        using var listener = new HttpListener();
        string redirect = $"http://127.0.0.1:{FreeLoopbackPort()}/callback/";
        listener.Prefixes.Add(redirect);
        listener.Start();
        openBrowser(AuthorizeUrl(redirect, state, challenge));

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        if (timeout > TimeSpan.Zero) linked.CancelAfter(timeout);
        using var stop = linked.Token.Register(() => { try { listener.Stop(); listener.Close(); } catch (ObjectDisposedException) { } });
        HttpListenerContext context;
        try { context = await listener.GetContextAsync().ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
        {
            if (cancellation.IsCancellationRequested) throw new OperationCanceledException("Sign-in was cancelled.", cancellation);
            if (linked.IsCancellationRequested) throw new TimeoutException("The browser sign-in did not come back in time.");
            throw new OperationCanceledException("Sign-in stopped before the browser came back.");
        }
        var query = context.Request.QueryString;
        string? code = query["code"], returned = query["state"], error = query["error"];
        bool ok = error is null && code is not null && returned == state;
        await RespondAsync(context, ok).ConfigureAwait(false);
        if (error is not null) throw new InvalidOperationException(error == "access_denied" ? "Sign-in was declined on open2077.net." : "Sign-in failed: " + error);
        if (!ok) throw new InvalidOperationException("Sign-in returned an invalid or mismatched answer.");
        return await ExchangeAsync(code!, verifier, cancellation).ConfigureAwait(false);
    }

    private sealed record TokenResponse(string SessionToken, DateTimeOffset ExpiresAtUtc, Guid AccountId, string DisplayName);
    private sealed record MeResponse(Guid AccountId, string Email, bool EmailVerified, string DisplayName, string Role);

    // The one-time code and its verifier for a session.
    public async Task<Open77Session> ExchangeAsync(string code, string verifier, CancellationToken cancellation = default)
    {
        using var response = await _http.PostAsJsonAsync(new Uri(endpoints.Master, "api/v1/accounts/launcher/token"), new { code, codeVerifier = verifier }, cancellation).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"The Open//77 platform refused the sign-in ({(int)response.StatusCode}{await ErrorCodeAsync(response, cancellation).ConfigureAwait(false)}).");
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellation).ConfigureAwait(false) ?? throw new InvalidDataException("The Open//77 platform sent an empty session.");
        var session = new Open77Session(token.AccountId, token.DisplayName, token.SessionToken, token.ExpiresAtUtc);
        // The account's details (e-mail, role); the sign-in stands even if they cannot be read now.
        try { return await RefreshAsync(session, cancellation).ConfigureAwait(false) ?? session; }
        catch (HttpRequestException) { return session; }
    }

    // The account behind a session, fresh from the master: null when the session is no longer
    // valid (expired or revoked); throws when the platform cannot be reached (keep the session).
    public async Task<Open77Session?> RefreshAsync(Open77Session session, CancellationToken cancellation = default)
    {
        if (session.ExpiresAtUtc <= DateTimeOffset.UtcNow) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoints.Master, "api/v1/accounts/me"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        using var response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return null;
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"The Open//77 platform answered {(int)response.StatusCode}.");
        var me = await response.Content.ReadFromJsonAsync<MeResponse>(cancellation).ConfigureAwait(false) ?? throw new HttpRequestException("The Open//77 platform sent an empty account.");
        return session with { AccountId = me.AccountId, DisplayName = me.DisplayName, Email = me.Email, EmailVerified = me.EmailVerified, Role = me.Role };
    }

    // Revokes the session at the master (best effort: signing out locally always works).
    public async Task SignOutAsync(Open77Session session, CancellationToken cancellation = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoints.Master, "api/v1/accounts/logout"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
            using var response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
    }

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response, CancellationToken cancellation)
    {
        try { return (await response.Content.ReadFromJsonAsync<JsonElement>(cancellation).ConfigureAwait(false)).TryGetProperty("code", out var c) ? ", " + c.GetString() : ""; }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or HttpRequestException) { return ""; }
    }

    private static async Task RespondAsync(HttpListenerContext context, bool ok)
    {
        byte[] body = Encoding.UTF8.GetBytes(CallbackPage(ok));
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.StatusCode = ok ? 200 : 400;
        context.Response.ContentLength64 = body.Length;
        await context.Response.OutputStream.WriteAsync(body).ConfigureAwait(false);
        context.Response.Close();
    }

    // The page the browser lands on: self-contained (served by a throwaway loopback listener).
    private static string CallbackPage(bool ok)
    {
        string kicker = ok ? "Signed in" : "Sign-in failed", title = ok ? "You're signed in to NCWE Studio" : "Something went wrong";
        string message = ok ? "You can close this tab and return to NCWE Studio." : "Return to NCWE Studio and try again.";
        string accent = ok ? "#22d8e2" : "#ff5964";
        return $$"""
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>NCWE Studio · OPEN//77</title>
<style>
  :root { color-scheme: dark; } html,body { height:100%; margin:0; }
  body { font-family:'Segoe UI',system-ui,sans-serif; background:#050b14; color:#e5e9ec; display:flex; align-items:center; justify-content:center; text-align:center; }
  .card { max-width:440px; padding:40px 44px; margin:20px; background:#0a1626; border:1px solid #1c334a; border-radius:14px; box-shadow:0 24px 60px rgba(0,0,0,.5); }
  .brand { font-weight:800; letter-spacing:.28em; font-size:14px; color:#8fa3b8; } .brand b { color:#e5e9ec; }
  .kicker { margin-top:26px; font-size:12px; letter-spacing:.22em; text-transform:uppercase; color:{{accent}}; }
  h1 { margin:.35em 0 .2em; font-size:24px; } p { margin:.4em 0 0; color:#9fb0c2; line-height:1.5; }
  .dot { display:inline-block; width:9px; height:9px; border-radius:50%; background:{{accent}}; margin-right:8px; vertical-align:middle; }
</style></head>
<body><div class="card"><div class="brand">NCWE STUDIO · OPEN<b>//</b>77</div><div class="kicker"><span class="dot"></span>{{kicker}}</div><h1>{{title}}</h1><p>{{message}}</p></div>
<script>setTimeout(function () { try { window.close(); } catch (e) {} }, 800);</script></body></html>
""";
    }

    private static int FreeLoopbackPort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
}

// The session at rest: %LOCALAPPDATA%\NCWE\open77-session-v1.dat, protected with DPAPI for the
// current Windows user (as the launcher keeps its own), never the token in plain text.
public sealed class Open77SessionStore(string path)
{
    private static readonly byte[] Entropy = "NCWEOpen77SessionV1"u8.ToArray();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string Path => path;

    public static string DefaultPath() => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NCWE", "open77-session-v1.dat");

    public void Save(Open77Session session)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(session, Json);
        byte[] sealedBytes;
        try { sealedBytes = Dpapi.Protect(plain, Entropy); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllBytes(temporary, sealedBytes); File.Move(temporary, path, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    // Null when there is none, it cannot be read (another user, damaged) or it has expired.
    public Open77Session? Load()
    {
        if (!File.Exists(path)) return null;
        try
        {
            byte[] plain = Dpapi.Unprotect(File.ReadAllBytes(path), Entropy);
            try
            {
                var session = JsonSerializer.Deserialize<Open77Session>(plain, Json);
                return session is { Token.Length: > 0 } && session.ExpiresAtUtc > DateTimeOffset.UtcNow ? session : null;
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    public void Clear() { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } }
}

// Windows DPAPI (CryptProtectData, current user), without a package dependency.
internal static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob data, string? description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob result);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob data, IntPtr description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob result);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private const int UiForbidden = 0x1;

    public static byte[] Protect(byte[] data, byte[] entropy) => Run(data, entropy, protect: true);
    public static byte[] Unprotect(byte[] data, byte[] entropy) => Run(data, entropy, protect: false);

    private static byte[] Run(byte[] data, byte[] entropy, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The Open//77 session is kept with Windows data protection.");
        var dataHandle = GCHandle.Alloc(data, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(entropy, GCHandleType.Pinned);
        try
        {
            var input = new Blob { Size = data.Length, Data = dataHandle.AddrOfPinnedObject() };
            var salt = new Blob { Size = entropy.Length, Data = entropyHandle.AddrOfPinnedObject() };
            bool ok = protect ? CryptProtectData(ref input, "NCWE Open77 session", ref salt, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output)
                              : CryptUnprotectData(ref input, IntPtr.Zero, ref salt, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally { LocalFree(output.Data); }
        }
        finally { dataHandle.Free(); entropyHandle.Free(); }
    }
}
