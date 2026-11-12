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

        // Same pipeline for the auth client ("ktc") and the data client (typed IKtcApiClient).
        void Configure(IHttpClientBuilder b)
        {
            b.ConfigureHttpClient(c =>
            {
                c.BaseAddress = new Uri(opt.BaseUrl.EndsWith('/') ? opt.BaseUrl : opt.BaseUrl + "/");
                c.Timeout = TimeSpan.FromSeconds(Math.Max(60, opt.TimeoutSeconds * (opt.RetryCount + 2)));
                c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            });
            b.AddHttpMessageHandler<ApiHealthHandler>();
            b.AddPolicyHandler((sp, req) => RetryPolicy(opt, sp.GetRequiredService<ILoggerFactory>().CreateLogger("Ktc.Retry"), req));
            b.AddPolicyHandler(BreakerPolicy(opt));
            b.AddPolicyHandler(Policy.TimeoutAsync<HttpResponseMessage>(TimeSpan.FromSeconds(opt.TimeoutSeconds), TimeoutStrategy.Optimistic));
            b.ConfigurePrimaryHttpMessageHandler(() => opt.UseMock
                ? new MockKtcHttpHandler()
                : new SocketsHttpHandler
                {
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                    SslOptions = opt.AcceptAnyServerCertificate
                        ? new System.Net.Security.SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true }
                        : new System.Net.Security.SslClientAuthenticationOptions()
                });
        }
        Configure(services.AddHttpClient("ktc"));
        Configure(services.AddHttpClient<IKtcApiClient, KtcApiClient>());
        return services;
    }

    private static IAsyncPolicy<HttpResponseMessage> RetryPolicy(KtcOptions opt, ILogger log, HttpRequestMessage req)
    {
        // Never retry non-idempotent calls (PUT/POST that are not searches) or logins.
        if (!req.Options.TryGetValue(KtcRequestKeys.Idempotent, out var idem) || !idem) return Policy.NoOpAsync<HttpResponseMessage>();
        return HttpPolicyExtensions.HandleTransientHttpError()
            .Or<TimeoutRejectedException>()
            .OrResult(r => (int)r.StatusCode == 429)
            .WaitAndRetryAsync(opt.RetryCount,
                attempt => TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt)) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 200)),
                (outcome, delay, attempt, _) => log.LogWarning("KTC retry {Attempt} in {Delay}ms: {Reason}", attempt, (int)delay.TotalMilliseconds,
                    outcome.Exception?.Message ?? outcome.Result.StatusCode.ToString()));
    }

    private static readonly object BreakerLock = new();
    private static IAsyncPolicy<HttpResponseMessage>? _breaker;
    private static IAsyncPolicy<HttpResponseMessage> BreakerPolicy(KtcOptions opt)
    {
        lock (BreakerLock)    // one breaker shared by both clients: it guards the KTC host, not an individual route
            return _breaker ??= HttpPolicyExtensions.HandleTransientHttpError().Or<TimeoutRejectedException>()
                .CircuitBreakerAsync(opt.BreakerFailures, TimeSpan.FromSeconds(opt.BreakerBreakSeconds));
    }
}
