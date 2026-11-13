using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Ktc.Application.Logic;
using Ktc.Application.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ktc.Infrastructure.KtcApi;

/// <summary>
/// Step-by-step check of the KTC login and token, for "401 Invalid token" problems. It logs in once with the given credentials,
/// calls a few read-only routes with that exact token (no retries, no redirects followed, no refresh), and logs off again.
/// The report contains statuses, header names, field names and token length/kind and JWT time claims, never the token,
/// the password or response values.
/// </summary>
public class KtcAuthDiagnostics
{
    public const string ClientName = "ktc-diag";
    private readonly IHttpClientFactory _factory; private readonly KtcOptions _opt; private readonly ILogger<KtcAuthDiagnostics> _log;
    public KtcAuthDiagnostics(IHttpClientFactory f, IOptions<KtcOptions> o, ILogger<KtcAuthDiagnostics> log) { _factory = f; _opt = o.Value; _log = log; }

    public static readonly string[] Routes =
    {
        "api/v1/Business?includeDefault=false", "api/v1/Region?includeDefault=false", "api/v1/LastClientContact", "api/v1/ActiveErrorExtra?abovelevel=0",
        "api/v1/HistoricalError", "api/v1/CurrentStatus/LastTimeInService", "api/v1/Login"
    };

    /// <summary>Runs the check for the signed-in user and, when configured, for Ktc:ServiceAccount (which the shared dashboard and background jobs use).</summary>
    public async Task<string> RunAllAsync(string user, string pass, string? mode, string? app, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"KTC token check  {DateTime.UtcNow:u}");
        sb.AppendLine($"Ktc:BaseUrl      {_factory.CreateClient(ClientName).BaseAddress}");
        sb.AppendLine($"Ktc:UseMock      {_opt.UseMock}");
        sb.AppendLine($"Service account  {(_opt.ServiceAccount.Configured ? "configured: the dashboard, alerts and scheduled widgets call KTC as the service account, not as you" : "not configured: every page calls KTC as the signed-in user")}");
        if (_opt.ServiceAccount.Configured && string.Equals(_opt.ServiceAccount.Username, user, StringComparison.OrdinalIgnoreCase))
            sb.AppendLine("                 WARNING: the service account is your own user, so its logins and yours can end each other's KTC session");
        sb.AppendLine();
        sb.AppendLine("=== As you (" + user + ") ===");
        sb.Append(await RunAsync(user, pass, mode, app, ct));
        if (_opt.ServiceAccount.Configured)
        {
            sb.AppendLine();
            sb.AppendLine("=== As the service account (" + _opt.ServiceAccount.Username + ") ===");
            sb.Append(await RunAsync(_opt.ServiceAccount.Username!, _opt.ServiceAccount.Password!, _opt.ServiceAccount.AuthMode, null, ct));
        }
        return sb.ToString();
    }

    public async Task<string> RunAsync(string user, string pass, string? mode, string? app, CancellationToken ct)
    {
        var sb = new StringBuilder();
        void L(string s) => sb.AppendLine(s);
        var http = _factory.CreateClient(ClientName);
        L($"AppName/AuthMode {(string.IsNullOrWhiteSpace(app) ? _opt.AppName : app)} / {(string.IsNullOrWhiteSpace(mode) ? _opt.DefaultAuthMode : mode)}");

        // 1. Login
        var body = JsonSerializer.Serialize(new { Username = user, Password = pass, AppName = string.IsNullOrWhiteSpace(app) ? _opt.AppName : app, AuthMode = string.IsNullOrWhiteSpace(mode) ? _opt.DefaultAuthMode : mode, PluginName = (string?)null });
        string raw; string? token = null;
        using (var req = new HttpRequestMessage(HttpMethod.Post, "api/v1/Login") { Content = new StringContent(body, Encoding.UTF8, "application/json") })
        {
            var (res, ms) = await Send(http, req, ct);
            if (res == null) { L("1. POST api/v1/Login  -> could not reach KTC: " + ms); return sb.ToString(); }
            using (res)
            {
                raw = (await res.Content.ReadAsStringAsync(ct)).Trim();
                L($"1. POST api/v1/Login  -> HTTP {(int)res.StatusCode} {res.ReasonPhrase} in {ms}");
                Headers(res, L);
                L($"   body shape: {KtcAuthService.Describe(raw)}");
                if (!res.IsSuccessStatusCode) { L("   Login failed, stopping here."); return sb.ToString(); }
            }
        }
        var parsed = new KtcAuthService(_factory, Options.Create(_opt), Microsoft.Extensions.Logging.Abstractions.NullLogger<KtcAuthService>.Instance).ParseToken(raw);
        if (!parsed.Success) { L("   No token found in the body. Send this report so the parser can be taught the field name."); return sb.ToString(); }
        token = parsed.Token!;
        L($"   token used: {token.Length} chars, {Kind(token)}, starts '{token[..Math.Min(4, token.Length)]}…', ends '…{token[^Math.Min(4, token.Length)..]}'");
        L($"   other token-like fields in the body: {OtherTokenFields(raw, token)}");
        Jwt(token, L);
        L($"   portal will treat it as expiring {parsed.ExpiresUtc:u}");
        L("");

        // 2. Use it straight away, exactly as the portal does
        var n = 2;
        foreach (var route in Routes)
            await Probe(http, n++, route, "Bearer", token, L, ct);
        // 3. Variants that show what the server actually expects
        await Probe(http, n++, "api/v1/LastClientContact", null, token, L, ct, "(token without the word Bearer)");
        await Probe(http, n++, "api/v1/LastClientContact", "none", token, L, ct, "(no Authorization header, for comparison)");

        // 4. Clean up
        using (var off = new HttpRequestMessage(HttpMethod.Post, "api/v1/Logoff") { Content = new StringContent("true", Encoding.UTF8, "application/json") })
        {
            off.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var (res, ms) = await Send(http, off, ct);
            L($"{n}. POST api/v1/Logoff -> {(res == null ? ms : $"HTTP {(int)res.StatusCode} {res.ReasonPhrase}")}");
            res?.Dispose();
        }
        _log.LogInformation("KTC token check run for {User}", user);
        return sb.ToString();
    }

    private static async Task Probe(HttpClient http, int n, string route, string? scheme, string token, Action<string> L, CancellationToken ct, string note = "")
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, route);
        if (scheme == "Bearer") req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        else if (scheme == null) req.Headers.TryAddWithoutValidation("Authorization", token);
        var (res, ms) = await Send(http, req, ct);
        if (res == null) { L($"{n}. GET {route} {note} -> failed: {ms}"); return; }
        using (res)
        {
            var text = await res.Content.ReadAsStringAsync(ct);
            L($"{n}. GET {route} {note} -> HTTP {(int)res.StatusCode} {res.ReasonPhrase} in {ms}");
            if (!res.IsSuccessStatusCode)
            {
                Headers(res, L);
                var snippet = CardMaskerSafe(text.Length > 300 ? text[..300] + "…" : text);
                L($"   body: {snippet.Replace('\n', ' ').Replace('\r', ' ')}");
            }
            else L($"   body shape: {Shorten(KtcAuthService.Describe(text))}");
        }
    }

    private static async Task<(HttpResponseMessage? Res, string Info)> Send(HttpClient http, HttpRequestMessage req, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try { var r = await http.SendAsync(req, ct); return (r, sw.ElapsedMilliseconds + "ms"); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { return (null, ex.GetType().Name + ": " + ex.Message); }
    }

    private static void Headers(HttpResponseMessage res, Action<string> L)
    {
        if (res.Headers.Location != null) L($"   REDIRECT to {res.Headers.Location.GetLeftPart(UriPartial.Path)}  <- set Ktc:BaseUrl to this address; .NET drops the Bearer header on redirects");
        if (res.Headers.WwwAuthenticate.Count > 0) L($"   WWW-Authenticate: {string.Join(", ", res.Headers.WwwAuthenticate)}");
        var names = res.Headers.Select(h => h.Key).Concat(res.Content.Headers.Select(h => h.Key)).ToList();
        L($"   headers: {string.Join(", ", names)}");
        var tokenish = res.Headers.Where(h => h.Key.Contains("token", StringComparison.OrdinalIgnoreCase) || h.Key.Contains("auth", StringComparison.OrdinalIgnoreCase)).Select(h => $"{h.Key} ({h.Value.FirstOrDefault()?.Length ?? 0} chars)").ToList();
        if (tokenish.Count > 0) L($"   token-like response headers: {string.Join(", ", tokenish)}");
    }

    private static string OtherTokenFields(string raw, string used)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var found = new List<string>();
            void Walk(JsonElement e, string path)
            {
                if (e.ValueKind == JsonValueKind.Object) foreach (var p in e.EnumerateObject()) Walk(p.Value, path + "." + p.Name);
                else if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: >= 16 } s)
                    found.Add($"{path.TrimStart('.')} ({s.Length} chars, {Kind(s)}{(KtcAuthService.CleanToken(s) == used ? ", USED" : "")})");
            }
            Walk(doc.RootElement, "");
            return found.Count == 0 ? "none" : string.Join("; ", found);
        }
        catch (JsonException) { return "body is not JSON"; }
    }

    private static string Kind(string t) => t.Count(c => c == '.') == 2 ? "JWT" : Guid.TryParse(t, out _) ? "GUID" : "opaque";

    private static void Jwt(string token, Action<string> L)
    {
        var parts = token.Split('.');
        if (parts.Length != 3) return;
        try
        {
            static JsonDocument Part(string s) { s = s.Replace('-', '+').Replace('_', '/'); return JsonDocument.Parse(Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='))); }
            using var h = Part(parts[0]); using var p = Part(parts[1]);
            L($"   JWT header: alg={KtcJson.Str(h.RootElement, "alg")} typ={KtcJson.Str(h.RootElement, "typ")}; claims: {string.Join(", ", p.RootElement.EnumerateObject().Select(x => x.Name))}");
            foreach (var c in new[] { "nbf", "iat", "exp" })
                if (p.RootElement.TryGetProperty(c, out var v) && v.TryGetInt64(out var secs))
                {
                    var t = DateTimeOffset.FromUnixTimeSeconds(secs).UtcDateTime;
                    L($"   {c} = {t:u} ({(t - DateTime.UtcNow).TotalMinutes:+0.0;-0.0} min from this server's clock)");
                }
        }
        catch { L("   looks like a JWT but could not be decoded"); }
    }

    private static string Shorten(string s) => s.Length > 200 ? s[..200] + "…" : s;
    private static string CardMaskerSafe(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"\b\d{13,19}\b", m => m.Value[..6] + new string('*', m.Value.Length - 10) + m.Value[^4..]);
}
