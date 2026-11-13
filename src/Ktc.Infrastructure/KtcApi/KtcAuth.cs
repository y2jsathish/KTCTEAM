using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Ktc.Application.Abstractions;
using Ktc.Application.Logic;
using Ktc.Application.Models;
using Ktc.Application.Options;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ktc.Infrastructure.KtcApi;

public static class SessionKeys
{
    public const string Token = "ktc.token";
    public const string Expiry = "ktc.exp";
    public const string Cred = "ktc.cred";
}

/// <summary>Forces background-style (service account) authentication inside the current async flow.</summary>
public static class KtcTokenScope
{
    private static readonly AsyncLocal<bool> Forced = new();
    public static bool IsServiceForced => Forced.Value;
    public static IDisposable ForceService() { Forced.Value = true; return new Reset(); }
    private sealed class Reset : IDisposable { public void Dispose() => Forced.Value = false; }
}

/// <summary>Talks to POST api/v1/Login | Relogin | Logoff. Uses the HttpClient factory directly to avoid a dependency cycle with the token provider.</summary>
public class KtcAuthService : IKtcAuthService
{
    private static readonly string[] TokenNames = { "access_token", "AccessToken", "Token", "BearerToken", "SessionToken", "AuthToken", "ktc_access_token", "KtcAccessToken" };
    private static readonly string[] Wrappers = { "Data", "Result", "Value", "Response", "Session", "Payload" };

    private readonly IHttpClientFactory _factory;
    private readonly KtcOptions _opt;
    private readonly ILogger<KtcAuthService> _log;
    public KtcAuthService(IHttpClientFactory f, IOptions<KtcOptions> o, ILogger<KtcAuthService> log) { _factory = f; _opt = o.Value; _log = log; }

    public Task<KtcLoginResult> LoginAsync(string u, string p, string? m, string? app = null, CancellationToken ct = default) => CallAsync("api/v1/Login", u, p, m, app, null, ct);
    public Task<KtcLoginResult> ReLoginAsync(string u, string p, string? m, string? app = null, CancellationToken ct = default) => ReLoginAsync(u, p, m, app, null, ct);
    /// <summary>Relogin refreshes an existing KTC session, so KTC expects the current token as Bearer (see the Postman collection).</summary>
    public Task<KtcLoginResult> ReLoginAsync(string u, string p, string? m, string? app, string? currentToken, CancellationToken ct = default) => CallAsync("api/v1/Relogin", u, p, m, app, currentToken, ct);

    private async Task<KtcLoginResult> CallAsync(string url, string user, string pass, string? mode, string? app, string? bearer, CancellationToken ct)
    {
        try
        {
            var http = _factory.CreateClient("ktc");
            var body = JsonSerializer.Serialize(new { Username = user, Password = pass, AppName = string.IsNullOrWhiteSpace(app) ? _opt.AppName : app, AuthMode = string.IsNullOrWhiteSpace(mode) ? _opt.DefaultAuthMode : mode, PluginName = (string?)null });
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            req.Headers.Accept.ParseAdd("application/json");
            if (!string.IsNullOrEmpty(bearer)) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
            using var res = await http.SendAsync(req, ct);
            var raw = (await res.Content.ReadAsStringAsync(ct)).Trim();
            if (!res.IsSuccessStatusCode)
            {
                _log.LogWarning("KTC {Route} failed for {User}: HTTP {Status} {Reason}", url, user, (int)res.StatusCode, res.ReasonPhrase);
                return new(false, null, null, res.StatusCode == System.Net.HttpStatusCode.Unauthorized || res.StatusCode == System.Net.HttpStatusCode.BadRequest
                    ? "Invalid username or password." : $"KTC login failed (HTTP {(int)res.StatusCode}).");
            }
            var parsed = ParseToken(raw);
            if (!parsed.Success) _log.LogWarning("KTC {Route} returned HTTP {Status} but no token was found. Response shape: {Shape}", url, (int)res.StatusCode, Describe(raw));
            return parsed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "KTC login call failed");
            return new(false, null, null, "Unable to reach the KTC API: " + ex.Message);
        }
    }

    /// <summary>
    /// The response shape is not documented: accept a bare/quoted token string, or an object (optionally wrapped in Data/Result/...)
    /// with a token + optional expiry. The token is cleaned of quotes, whitespace and a leading "Bearer " so the header is never "Bearer Bearer x".
    /// </summary>
    public KtcLoginResult ParseToken(string raw)
    {
        string? token = null, field = null; DateTime? bodyExp = null; int secs = 0;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var r = doc.RootElement;
            if (r.ValueKind == JsonValueKind.String) { token = r.GetString(); field = "(string body)"; }
            else if (r.ValueKind == JsonValueKind.Object)
            {
                var o = FindTokenObject(r, out field);
                if (o is { } obj)
                {
                    token = KtcJson.Str(obj, field!);
                    bodyExp = KtcJson.Date(obj, "expires", "Expires", "ExpiresAt", "Expiry", "expiry", "TokenExpiry", "Expiration", ".expires", "ExpiresUtc", "expires_at");
                    secs = KtcJson.Int(obj, 0, "expires_in", "ExpiresIn", "ExpiresInSeconds");
                }
            }
        }
        catch (JsonException) { token = raw; field = "(plain text body)"; }

        token = CleanToken(token);
        if (string.IsNullOrWhiteSpace(token)) return new(false, null, null, "KTC returned no access token.");

        // Prefer the unambiguous sources: JWT exp, then expires_in, then a date in the body (which may be server-local time).
        var now = DateTime.UtcNow;
        DateTime? exp = JwtExpiry(token);
        if (exp == null && secs > 0) exp = now.AddSeconds(secs);
        if (exp == null && bodyExp is { } be && be > now.AddMinutes(1)) exp = be;
        exp ??= now.AddMinutes(_opt.DefaultTokenLifetimeMinutes);
        _log.LogInformation("KTC token read from {Field}: {Length} chars, {Kind}, expires {Exp:u}", field, token.Length, token.Count(c => c == '.') == 2 ? "JWT" : "opaque", exp);
        return new(true, token, exp, null);
    }

    public static string? CleanToken(string? t)
    {
        if (t == null) return null;
        t = t.Trim().Trim('"', '\'').Trim();
        if (t.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) t = t[7..].Trim();
        return t.Length == 0 ? null : t;
    }

    private static JsonElement? FindTokenObject(JsonElement o, out string? field)
    {
        foreach (var n in TokenNames)
            if (KtcJson.TryProp(o, out var v, n) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())) { field = n; return o; }
        foreach (var w in Wrappers)
            if (KtcJson.TryProp(o, out var inner, w))
            {
                if (inner.ValueKind == JsonValueKind.Object && FindTokenObject(inner, out field) is { } hit) return hit;
                if (inner.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(inner.GetString()) && inner.GetString()!.Length >= 16) { field = w; return o; }
            }
        field = null; return null;
    }

    /// <summary>Property names and value kinds only (never values), so a log can show the real Login response shape safely.</summary>
    public static string Describe(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            static string D(JsonElement e, int depth) => e.ValueKind switch
            {
                JsonValueKind.Object when depth < 3 => "{" + string.Join(", ", e.EnumerateObject().Select(p => p.Name + ":" + D(p.Value, depth + 1))) + "}",
                JsonValueKind.Array => "array",
                _ => e.ValueKind.ToString().ToLowerInvariant()
            };
            return D(doc.RootElement, 0);
        }
        catch (JsonException) { return $"non-JSON text, {raw.Length} chars"; }
    }

    private static DateTime? JwtExpiry(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) return null;
            var p = parts[1].Replace('-', '+').Replace('_', '/');
            p = p.PadRight(p.Length + (4 - p.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(p));
            return doc.RootElement.TryGetProperty("exp", out var e) ? DateTimeOffset.FromUnixTimeSeconds(e.GetInt64()).UtcDateTime : null;
        }
        catch { return null; }
    }

    public async Task LogoffAsync(string token, CancellationToken ct = default)
    {
        try
        {
            var http = _factory.CreateClient("ktc");
            using var req = new HttpRequestMessage(HttpMethod.Post, "api/v1/Logoff") { Content = new StringContent("true", Encoding.UTF8, "application/json") };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var _ = await http.SendAsync(req, ct);
        }
        catch (Exception ex) { _log.LogWarning(ex, "KTC logoff failed (ignored)"); }
    }
}

/// <summary>
/// Token management. Interactive requests use the signed-in user's token, auto-refreshed through Relogin using the encrypted
/// credentials kept in the session. Background jobs use the configured service account.
///
/// The current token per user lives in a process-wide map, not only in the ASP.NET session. Session values are a per-request
/// snapshot that is written back when the request ends (last write wins), so with several dashboard calls running at once a
/// request could keep using, or even save back, a token that a parallel refresh had already replaced, and KTC answers
/// "401 Invalid token" for it. A refresh also only logs in again when the token that failed is still the current one, so a
/// burst of parallel 401s causes one Relogin instead of a chain of logins that invalidate each other.
/// </summary>
public class KtcTokenProvider : IKtcTokenProvider
{
    private sealed record Entry(string Token, DateTime ExpiresUtc);
    private static readonly ConcurrentDictionary<string, Entry> Users = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> UserGates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim ServiceGate = new(1, 1);
    private static Entry? _svc;
    private static readonly TimeSpan Skew = TimeSpan.FromSeconds(60);

    private readonly IHttpContextAccessor _ctx;
    private readonly IKtcAuthService _auth;
    private readonly KtcOptions _opt;
    private readonly IDataProtector _protector;
    private readonly ILogger<KtcTokenProvider>? _log;

    public KtcTokenProvider(IHttpContextAccessor ctx, IKtcAuthService auth, IOptions<KtcOptions> opt, IDataProtectionProvider dp, ILogger<KtcTokenProvider>? log = null)
    { _ctx = ctx; _auth = auth; _opt = opt.Value; _protector = dp.CreateProtector("Ktc.SessionCredentials"); _log = log; }

    public static string ProtectCredentials(IDataProtectionProvider dp, string user, string pass, string? mode, string? app)
        => dp.CreateProtector("Ktc.SessionCredentials").Protect($"{user}\n{mode}\n{app}\n{pass}");

    /// <summary>Records the token from an interactive login in the session and in the shared map.</summary>
    public static void Remember(HttpContext http, string user, string token, DateTime expiresUtc)
    {
        http.Session.SetString(SessionKeys.Token, token);
        http.Session.SetString(SessionKeys.Expiry, expiresUtc.Ticks.ToString());
        Users[user] = new Entry(token, expiresUtc);
    }

    public static void Forget(string? user) { if (user != null) Users.TryRemove(user, out _); }

    /// <summary>The newest token known for this user (shared map first, then the session copy). Used by logoff/status.</summary>
    public static (string? Token, DateTime? ExpiresUtc) Current(HttpContext http)
    {
        var name = http.User?.Identity?.Name;
        if (name != null && Users.TryGetValue(name, out var e)) return (e.Token, e.ExpiresUtc);
        var t = http.Session.GetString(SessionKeys.Token);
        return (t, long.TryParse(http.Session.GetString(SessionKeys.Expiry), out var x) ? new DateTime(x, DateTimeKind.Utc) : null);
    }

    private HttpContext? Http => KtcTokenScope.IsServiceForced ? null : _ctx.HttpContext;
    private bool Interactive => Http?.User?.Identity?.IsAuthenticated == true && Http.Session != null;

    public Task<string?> GetTokenAsync(CancellationToken ct = default) => Interactive ? UserTokenAsync(false, null, ct) : ServiceTokenAsync(false, null, ct);
    public Task<string?> RefreshAsync(CancellationToken ct = default) => RefreshAsync(null, ct);
    public Task<string?> RefreshAsync(string? failedToken, CancellationToken ct = default) => Interactive ? UserTokenAsync(true, failedToken, ct) : ServiceTokenAsync(true, failedToken, ct);

    private Entry? Load(HttpContext http, string user)
    {
        if (Users.TryGetValue(user, out var e)) return e;
        var t = http.Session.GetString(SessionKeys.Token);
        if (t == null || !long.TryParse(http.Session.GetString(SessionKeys.Expiry), out var x)) return null;
        e = new Entry(t, new DateTime(x, DateTimeKind.Utc));
        return Users.GetOrAdd(user, e);       // e.g. after an app restart with a persistent session store
    }

    private static bool Fresh(Entry? e) => e != null && DateTime.UtcNow < e.ExpiresUtc - Skew;

    /// <summary>True when another caller already replaced the token that failed (or a forced refresh with no failed token).</summary>
    private static bool AlreadyRefreshed(Entry? e, string? failedToken) => failedToken != null && e != null && e.Token != failedToken && Fresh(e);

    private async Task<string?> UserTokenAsync(bool force, string? failedToken, CancellationToken ct)
    {
        var http = Http!;
        var user = http.User.Identity!.Name!;
        var cur = Load(http, user);
        if (!force && Fresh(cur)) return cur!.Token;
        if (force && AlreadyRefreshed(cur, failedToken)) return cur!.Token;

        var gate = UserGates.GetOrAdd(user, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            cur = Load(http, user);
            if (!force && Fresh(cur)) return cur!.Token;
            if (force && AlreadyRefreshed(cur, failedToken)) return cur!.Token;

            var cred = http.Session.GetString(SessionKeys.Cred);
            if (cred == null) return null;
            string[] parts;
            try { parts = _protector.Unprotect(cred).Split('\n', 4); } catch { return null; }
            var mode = parts[1].Length == 0 ? null : parts[1];
            var res = await _auth.ReLoginAsync(parts[0], parts[3], mode, parts[2], cur?.Token, ct);
            if (!res.Success) res = await _auth.LoginAsync(parts[0], parts[3], mode, parts[2], ct);
            if (!res.Success) { _log?.LogWarning("KTC token refresh failed for {User}: {Error}", user, res.Error); return null; }
            Remember(http, user, res.Token!, res.ExpiresUtc!.Value);
            _log?.LogInformation("KTC token refreshed for {User}", user);
            return res.Token;
        }
        finally { gate.Release(); }
    }

    private async Task<string?> ServiceTokenAsync(bool force, string? failedToken, CancellationToken ct)
    {
        if (_opt.UseMock) return "mock-token";
        if (!_opt.ServiceAccount.Configured) return null;
        var cur = _svc;
        if (!force && Fresh(cur)) return cur!.Token;
        if (force && AlreadyRefreshed(cur, failedToken)) return cur!.Token;
        await ServiceGate.WaitAsync(ct);
        try
        {
            cur = _svc;
            if (!force && Fresh(cur)) return cur!.Token;
            if (force && AlreadyRefreshed(cur, failedToken)) return cur!.Token;
            var sa = _opt.ServiceAccount;
            var res = cur != null ? await _auth.ReLoginAsync(sa.Username!, sa.Password!, sa.AuthMode, null, cur.Token, ct) : new KtcLoginResult(false, null, null, null);
            if (!res.Success) res = await _auth.LoginAsync(sa.Username!, sa.Password!, sa.AuthMode, null, ct);
            if (!res.Success) { _svc = null; _log?.LogWarning("KTC service account login failed: {Error}", res.Error); return null; }
            _svc = new Entry(res.Token!, res.ExpiresUtc!.Value);
            return res.Token;
        }
        finally { ServiceGate.Release(); }
    }
}
