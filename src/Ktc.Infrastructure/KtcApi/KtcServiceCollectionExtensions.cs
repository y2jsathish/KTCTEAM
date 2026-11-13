using Ktc.Application.Abstractions;
using Ktc.Application.Options;
using Ktc.Infrastructure.Mock;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Extensions.Http;
using Polly.Timeout;

namespace Ktc.Infrastructure.KtcApi;

public static class KtcHttpRegistration
{
    public static IServiceCollection AddKtcHttp(this IServiceCollection services, KtcOptions opt)
    {
        services.AddSingleton<IApiHealthTracker, ApiHealthTracker>();
        services.AddTransient<ApiHealthHandler>();
        services.AddScoped<IKtcAuthService, KtcAuthService>();
        services.AddScoped<IKtcTokenProvider, KtcTokenProvider>();
        services.AddTransient<KtcRedirectHandler>();
        services.AddScoped<KtcAuthDiagnostics>();

        void Base(IHttpClientBuilder b) => b.ConfigureHttpClient(c =>
        {
            c.BaseAddress = new Uri(opt.BaseUrl.EndsWith('/') ? opt.BaseUrl : opt.BaseUrl + "/");
            c.Timeout = TimeSpan.FromSeconds(Math.Max(60, opt.TimeoutSeconds * (opt.RetryCount + 2)));
            c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        });

        // Auth client (Login / Relogin / Logoff): no retry and no circuit breaker, so a breaker opened by data calls can never block sign-in.
        var auth = services.AddHttpClient("ktc"); Base(auth);
        auth.AddHttpMessageHandler<ApiHealthHandler>();
        auth.AddPolicyHandler(Policy.TimeoutAsync<HttpResponseMessage>(TimeSpan.FromSeconds(Math.Max(opt.TimeoutSeconds, 30)), TimeoutStrategy.Optimistic));
        auth.AddHttpMessageHandler<KtcRedirectHandler>();
        auth.ConfigurePrimaryHttpMessageHandler(() => Primary(opt));

        // Data client: retry + breaker for transport failures only.
        var data = services.AddHttpClient<IKtcApiClient, KtcApiClient>(); Base(data);
        data.AddHttpMessageHandler<ApiHealthHandler>();
        data.AddPolicyHandler((sp, req) => RetryPolicy(opt, sp.GetRequiredService<ILoggerFactory>().CreateLogger("Ktc.Retry"), req));
        data.AddPolicyHandler((sp, _) => BreakerPolicy(opt, sp.GetRequiredService<ILoggerFactory>().CreateLogger("Ktc.Breaker")));
        data.AddPolicyHandler(Policy.TimeoutAsync<HttpResponseMessage>(TimeSpan.FromSeconds(opt.TimeoutSeconds), TimeoutStrategy.Optimistic));
        data.AddHttpMessageHandler<KtcRedirectHandler>();     // innermost: follows same-host redirects without losing the Bearer header
        data.ConfigurePrimaryHttpMessageHandler(() => Primary(opt));

        // Token check page: no retries, breaker or redirect following, so it shows exactly what KTC answers.
        services.AddHttpClient(KtcAuthDiagnostics.ClientName).ConfigureHttpClient(c =>
        {
            c.BaseAddress = new Uri(opt.BaseUrl.EndsWith('/') ? opt.BaseUrl : opt.BaseUrl + "/");
            c.Timeout = TimeSpan.FromSeconds(Math.Max(15, opt.TimeoutSeconds));
            c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        }).ConfigurePrimaryHttpMessageHandler(() => Primary(opt));
        return services;
    }

    private static HttpMessageHandler Primary(KtcOptions opt) => opt.UseMock
        ? new MockKtcHttpHandler()
        : new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AllowAutoRedirect = false,     // see KtcRedirectHandler
            SslOptions = opt.AcceptAnyServerCertificate
                ? new System.Net.Security.SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true }
                : new System.Net.Security.SslClientAuthenticationOptions()
        };

    private static IAsyncPolicy<HttpResponseMessage> RetryPolicy(KtcOptions opt, ILogger log, HttpRequestMessage req)
    {
        // Never retry non-idempotent calls (PUT/POST that are not searches) or logins.
        if (!req.Options.TryGetValue(KtcRequestKeys.Idempotent, out var idem) || !idem) return Policy.NoOpAsync<HttpResponseMessage>();
        // Retry only what a retry can fix: network errors, timeouts, 502/503/504 and 429. A 500 is KTC rejecting the request
        // (e.g. HistoricalError without dates) and answers the same way every time, so it is not retried.
        return Policy<HttpResponseMessage>.Handle<HttpRequestException>()
            .Or<TimeoutRejectedException>()
            .OrResult(r => (int)r.StatusCode is 429 or 502 or 503 or 504)
            .WaitAndRetryAsync(opt.RetryCount,
                attempt => TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt)) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 200)),
                (outcome, delay, attempt, _) => log.LogWarning("KTC retry {Attempt} in {Delay}ms: {Reason}", attempt, (int)delay.TotalMilliseconds,
                    outcome.Exception?.Message ?? outcome.Result.StatusCode.ToString()));
    }

    private static readonly object BreakerLock = new();
    private static IAsyncPolicy<HttpResponseMessage>? _breaker;
    /// <summary>When the breaker opened last, so callers can say how long until KTC calls resume.</summary>
    public static DateTime? BreakerOpenUntilUtc { get; private set; }

    /// <summary>
    /// One breaker for the KTC host. It counts only transport failures (cannot connect, timeouts, 502/503/504): HTTP answers
    /// such as 401/403/404/500 prove the server is up and must not stop every other call, as they did before.
    /// </summary>
    private static IAsyncPolicy<HttpResponseMessage> BreakerPolicy(KtcOptions opt, ILogger log)
    {
        lock (BreakerLock)
            return _breaker ??= Policy<HttpResponseMessage>.Handle<HttpRequestException>()
                .Or<TimeoutRejectedException>()
                .OrResult(r => (int)r.StatusCode is 502 or 503 or 504)
                .CircuitBreakerAsync(opt.BreakerFailures, TimeSpan.FromSeconds(opt.BreakerBreakSeconds),
                    (outcome, wait) =>
                    {
                        BreakerOpenUntilUtc = DateTime.UtcNow + wait;
                        log.LogWarning("KTC circuit opened for {Seconds}s after {Count} failures in a row: {Reason}", (int)wait.TotalSeconds, opt.BreakerFailures,
                            outcome.Exception?.Message ?? outcome.Result?.StatusCode.ToString());
                    },
                    () => { BreakerOpenUntilUtc = null; log.LogInformation("KTC circuit closed, calls resumed"); });
    }
}
