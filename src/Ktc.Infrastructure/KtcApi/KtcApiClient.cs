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

        var token = options.Token;
        if (token == null && !options.BypassAuth)
        {
            token = await _tokens.GetTokenAsync(ct);
            if (token == null) return KtcResponse.Fail("Not authenticated with KTC.", 401);
        }

        var resp = await ExecuteOnce(method, relativeUrl, body, options, token, ct);
        if (resp.StatusCode == 401 && !options.BypassAuth && options.Token == null)
        {
            var fresh = await _tokens.RefreshAsync(ct);
            if (fresh != null) resp = await ExecuteOnce(method, relativeUrl, body, options, fresh, ct);
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
            if (!ok) _log.LogWarning("KTC {Method} {Url} -> {Status} in {Ms}ms", method, url, (int)res.StatusCode, sw.ElapsedMilliseconds);
            else _log.LogDebug("KTC {Method} {Url} -> {Status} in {Ms}ms", method, url, (int)res.StatusCode, sw.ElapsedMilliseconds);
            return new KtcResponse(ok, (int)res.StatusCode, el, raw, ok ? null : $"HTTP {(int)res.StatusCode} {res.ReasonPhrase}", sw.ElapsedMilliseconds);
        }
        catch (BrokenCircuitException) { return KtcResponse.Fail("KTC API circuit breaker is open - calls are temporarily suspended.", 503, sw.ElapsedMilliseconds); }
        catch (TimeoutRejectedException) { return KtcResponse.Fail("KTC API call timed out.", 504, sw.ElapsedMilliseconds); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogError(ex, "KTC {Method} {Url} failed", method, url);
            return KtcResponse.Fail(ex.Message, 0, sw.ElapsedMilliseconds);
        }
    }
}
