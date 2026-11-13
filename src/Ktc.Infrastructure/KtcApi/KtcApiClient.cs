using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Ktc.Application.Abstractions;
using Ktc.Application.Models;
using Ktc.Application.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Ktc.Infrastructure.KtcApi;

public static class KtcRequestKeys
{
    public static readonly HttpRequestOptionsKey<bool> Idempotent = new("ktc.idempotent");
}

/// <summary>Reusable KTC service layer: token injection, 401 re-auth, retry/breaker/timeout (via the HttpClient pipeline), logging, caching.</summary>
public class KtcApiClient : IKtcApiClient
{
    private static readonly TimeSpan DeniedFor = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ForbiddenFor = TimeSpan.FromMinutes(15);

    /// <summary>KTC errors come back as {"Message":"..."}; returns that text, trimmed.</summary>
    public static string? KtcMessage(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var d = JsonDocument.Parse(raw);
            if (d.RootElement.ValueKind == JsonValueKind.String) return d.RootElement.GetString();
            if (d.RootElement.ValueKind == JsonValueKind.Object && d.RootElement.TryGetProperty("Message", out var m) && m.ValueKind == JsonValueKind.String) return m.GetString();
        }
        catch (JsonException) { }
        return raw.Length > 200 ? raw[..200] : raw;
    }
    private readonly HttpClient _http;
    private readonly IKtcTokenProvider _tokens;
    private readonly IMemoryCache _cache;
    private readonly ILogger<KtcApiClient> _log;

    public KtcApiClient(HttpClient http, IKtcTokenProvider tokens, IMemoryCache cache, ILogger<KtcApiClient> log)
    { _http = http; _tokens = tokens; _cache = cache; _log = log; }

    public async Task<KtcResponse> SendAsync(HttpMethod method, string relativeUrl, object? body = null, KtcCallOptions? options = null, CancellationToken ct = default)
    {
        options ??= new KtcCallOptions();
        var cacheKey = options.CacheFor.HasValue && method == HttpMethod.Get ? "ktc:" + relativeUrl : null;
        if (cacheKey != null && _cache.TryGetValue(cacheKey, out KtcResponse? cached) && cached != null) return cached;

        // A route that KTC rejected for this account even with a freshly issued token is not retried on every refresh cycle.
        var deniedKey = "ktc-denied:" + _tokens.Identity + ":" + method + " " + ApiHealthTracker.Normalize(relativeUrl);
        if (!options.BypassAuth && options.Token == null && _cache.TryGetValue(deniedKey, out KtcResponse? denied) && denied != null) return denied;

        var token = options.Token;
        if (token == null && !options.BypassAuth)
        {
            token = await _tokens.GetTokenAsync(ct);
            if (token == null) return KtcResponse.Fail("Not authenticated with KTC.", 401);
        }

        var resp = await ExecuteOnce(method, relativeUrl, body, options, token, ct);
        if (resp.StatusCode == 403 && !options.BypassAuth && options.Token == null)
        {
            // A real permission refusal: the token is fine, the KTC user lacks a role. Do not re-login or retry; pause the route.
            var why = KtcMessage(resp.Raw) ?? "KTC refused this request (HTTP 403).";
            _log.LogWarning("KTC {Method} {Url} -> 403 for {Identity}: {Why}. Paused for {Minutes} min.", method, relativeUrl, _tokens.Identity, why, ForbiddenFor.TotalMinutes);
            var msg = KtcResponse.Fail("Permission required: " + why, 403, resp.ElapsedMs);
            _cache.Set(deniedKey, msg, ForbiddenFor);
            return msg;
        }
        if (resp.StatusCode == 401 && !options.BypassAuth && options.Token == null)
        {
            var fresh = await _tokens.RefreshAsync(token, ct);
            if (fresh != null) resp = await ExecuteOnce(method, relativeUrl, body, options, fresh, ct);
            if (resp.StatusCode == 401 && fresh != null)
            {
                _log.LogWarning("KTC {Method} {Url} still 401 with a fresh token for {Identity}: KTC is refusing this route for that account. Pausing it for {Minutes} min. Open /diag/ktc-token to compare accounts.",
                    method, relativeUrl, _tokens.Identity, DeniedFor.TotalMinutes);
                var msg = KtcResponse.Fail($"KTC refused {method} {relativeUrl.Split('?')[0]} for {_tokens.Identity} (HTTP 401 even after a fresh login). Paused for {DeniedFor.TotalMinutes:0} min.", 401, resp.ElapsedMs);
                _cache.Set(deniedKey, msg, DeniedFor);
                return msg;
            }
        }
        if (cacheKey != null && resp.Success) _cache.Set(cacheKey, resp, options.CacheFor!.Value);
        return resp;
    }

    private async Task<KtcResponse> ExecuteOnce(HttpMethod method, string url, object? body, KtcCallOptions options, string? token, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(method, url.TrimStart('/'));
            if (token != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Options.Set(KtcRequestKeys.Idempotent, options.Idempotent || method == HttpMethod.Get);
            if (body != null)
            {
                var json = body is string s ? s : JsonSerializer.Serialize(body);
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }
            using var res = await _http.SendAsync(req, ct);
            var raw = await res.Content.ReadAsStringAsync(ct);
            JsonElement? el = null;
            if (!string.IsNullOrWhiteSpace(raw))
            {
                try { using var d = JsonDocument.Parse(raw); el = d.RootElement.Clone(); } catch (JsonException) { /* plain text body */ }
            }
            var ok = res.IsSuccessStatusCode;
            if (!ok) _log.LogWarning("KTC {Method} {Url} -> {Status} {Reason} in {Ms}ms as {Identity}", method, url, (int)res.StatusCode, res.ReasonPhrase, sw.ElapsedMilliseconds, _tokens.Identity);
            else _log.LogDebug("KTC {Method} {Url} -> {Status} in {Ms}ms", method, url, (int)res.StatusCode, sw.ElapsedMilliseconds);
            return new KtcResponse(ok, (int)res.StatusCode, el, raw, ok ? null : $"HTTP {(int)res.StatusCode} {res.ReasonPhrase}", sw.ElapsedMilliseconds);
        }
        catch (BrokenCircuitException)
        {
            var left = KtcHttpRegistration.BreakerOpenUntilUtc is { } u ? Math.Max(1, (int)(u - DateTime.UtcNow).TotalSeconds) : 0;
            return KtcResponse.Fail($"KTC server not reachable: calls paused after repeated connection failures{(left > 0 ? $", retrying in {left}s" : "")}.", 503, sw.ElapsedMilliseconds);
        }
        catch (TimeoutRejectedException) { return KtcResponse.Fail("KTC API call timed out.", 504, sw.ElapsedMilliseconds); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogError(ex, "KTC {Method} {Url} failed", method, url);
            return KtcResponse.Fail(ex.Message, 0, sw.ElapsedMilliseconds);
        }
    }
}
