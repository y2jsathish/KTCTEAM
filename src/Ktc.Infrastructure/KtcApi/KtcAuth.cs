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

/// <summary>Talks to POST api/v1/Login | ReLogin | Logoff. Uses the HttpClient factory directly to avoid a dependency cycle with the token provider.</summary>
public class KtcAuthService : IKtcAuthService
{
    private readonly IHttpClientFactory _factory;
    private readonly KtcOptions _opt;
    private readonly ILogger<KtcAuthService> _log;
    public KtcAuthService(IHttpClientFactory f, IOptions<KtcOptions> o, ILogger<KtcAuthService> log) { _factory = f; _opt = o.Value; _log = log; }

    public Task<KtcLoginResult> LoginAsync(string u, string p, string? m, string? app = null, CancellationToken ct = default) => CallAsync("api/v1/Login", u, p, m, app, ct);
    public Task<KtcLoginResult> ReLoginAsync(string u, string p, string? m, string? app = null, CancellationToken ct = default) => CallAsync("api/v1/ReLogin", u, p, m, app, ct);

    private async Task<KtcLoginResult> CallAsync(string url, string user, string pass, string? mode, string? app, CancellationToken ct)
    {
        try
        {
            var http = _factory.CreateClient("ktc");
            var body = JsonSerializer.Serialize(new { Username = user, Password = pass, AppName = string.IsNullOrWhiteSpace(app) ? _opt.AppName : app, AuthMode = mode ?? _opt.DefaultAuthMode, PluginName = (string?)null });
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            using var res = await http.SendAsync(req, ct);
            var raw = (await res.Content.ReadAsStringAsync(ct)).Trim();
            if (!res.IsSuccessStatusCode)
            {
                _log.LogWarning("KTC login failed for {User}: HTTP {Status}", user, (int)res.StatusCode);
                return new(false, null, null, res.StatusCode == System.Net.HttpStatusCode.Unauthorized || res.StatusCode == System.Net.HttpStatusCode.BadRequest
                    ? "Invalid username or password." : $"KTC login failed (HTTP {(int)res.StatusCode}).");
            }
            return ParseToken(raw);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "KTC login call failed");
            return new(false, null, null, "Unable to reach the KTC API: " + ex.Message);
        }
    }

    /// <summary>The response shape is not documented: accept a bare/quoted token string or an object with a token + optional expiry.</summary>
    public KtcLoginResult ParseToken(string raw)
    {
        string? token = null; DateTime? exp = null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var r = doc.RootElement;
            if (r.ValueKind == JsonValueKind.String) token = r.GetString();
            else if (r.ValueKind == JsonValueKind.Object)
            {
                token = KtcJson.Str(r, "access_token", "AccessToken", "Token", "token", "BearerToken", "SessionToken");
                exp = KtcJson.Date(r, "expires", "Expires", "ExpiresAt", "Expiry", "expiry", "TokenExpiry", "Expiration", ".expires");
                var secs = KtcJson.Int(r, 0, "expires_in", "ExpiresIn");
                if (exp == null && secs > 0) exp = DateTime.UtcNow.AddSeconds(secs);
            }
        }
        catch (JsonException) { token = raw.Trim('"'); }

        if (string.IsNullOrWhiteSpace(token)) return new(false, null, null, "KTC returned no access token.");
        exp ??= JwtExpiry(token) ?? DateTime.UtcNow.AddMinutes(_opt.DefaultTokenLifetimeMinutes);
        return new(true, token, exp, null);
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
/// Token management. Interactive requests use the token stored in the server-side session (auto-refreshed through ReLogin using
/// the encrypted credentials kept in the session). Background jobs use the configured service account.
/// </summary>
public class KtcTokenProvider : IKtcTokenProvider
{
    private static readonly SemaphoreSlim UserGate = new(1, 1);
    private static readonly SemaphoreSlim ServiceGate = new(1, 1);
    private static string? _svcToken; private static DateTime _svcExp;
    private static readonly TimeSpan Skew = TimeSpan.FromSeconds(60);

    private readonly IHttpContextAccessor _ctx;
    private readonly IKtcAuthService _auth;
    private readonly KtcOptions _opt;
    private readonly IDataProtector _protector;

    public KtcTokenProvider(IHttpContextAccessor ctx, IKtcAuthService auth, IOptions<KtcOptions> opt, IDataProtectionProvider dp)
    { _ctx = ctx; _auth = auth; _opt = opt.Value; _protector = dp.CreateProtector("Ktc.SessionCredentials"); }

    public static string ProtectCredentials(IDataProtectionProvider dp, string user, string pass, string? mode, string? app)
        => dp.CreateProtector("Ktc.SessionCredentials").Protect($"{user}\n{mode}\n{app}\n{pass}");

    private HttpContext? Http => KtcTokenScope.IsServiceForced ? null : _ctx.HttpContext;
    private bool Interactive => Http?.User?.Identity?.IsAuthenticated == true && Http.Session != null;

    public Task<string?> GetTokenAsync(CancellationToken ct = default) => Interactive ? UserTokenAsync(false, ct) : ServiceTokenAsync(false, ct);
    public Task<string?> RefreshAsync(CancellationToken ct = default) => Interactive ? UserTokenAsync(true, ct) : ServiceTokenAsync(true, ct);

    private async Task<string?> UserTokenAsync(bool force, CancellationToken ct)
    {
        var s = Http!.Session;
        if (!force && Valid(s.GetString(SessionKeys.Token), s.GetString(SessionKeys.Expiry), out var t)) return t;
        await UserGate.WaitAsync(ct);
        try
        {
            if (!force && Valid(s.GetString(SessionKeys.Token), s.GetString(SessionKeys.Expiry), out t)) return t;
            var cred = s.GetString(SessionKeys.Cred);
            if (cred == null) return null;
            string[] parts;
            try { parts = _protector.Unprotect(cred).Split('\n', 4); } catch { return null; }
            var res = await _auth.ReLoginAsync(parts[0], parts[3], parts[1].Length == 0 ? null : parts[1], parts[2], ct);
            if (!res.Success) res = await _auth.LoginAsync(parts[0], parts[3], parts[1].Length == 0 ? null : parts[1], parts[2], ct);
            if (!res.Success) return null;
            s.SetString(SessionKeys.Token, res.Token!);
            s.SetString(SessionKeys.Expiry, res.ExpiresUtc!.Value.Ticks.ToString());
            return res.Token;
        }
        finally { UserGate.Release(); }
    }

    private async Task<string?> ServiceTokenAsync(bool force, CancellationToken ct)
    {
        if (_opt.UseMock) return "mock-token";
        if (!_opt.ServiceAccount.Configured) return null;
        if (!force && _svcToken != null && DateTime.UtcNow < _svcExp - Skew) return _svcToken;
        await ServiceGate.WaitAsync(ct);
        try
        {
            if (!force && _svcToken != null && DateTime.UtcNow < _svcExp - Skew) return _svcToken;
            var sa = _opt.ServiceAccount;
            var res = await _auth.LoginAsync(sa.Username!, sa.Password!, sa.AuthMode, null, ct);
            if (!res.Success) { _svcToken = null; return null; }
            _svcToken = res.Token; _svcExp = res.ExpiresUtc!.Value;
            return _svcToken;
        }
        finally { ServiceGate.Release(); }
    }

    private static bool Valid(string? token, string? ticks, out string? t)
    {
        t = token;
        return token != null && long.TryParse(ticks, out var x) && DateTime.UtcNow < new DateTime(x, DateTimeKind.Utc) - Skew;
    }
}
