using Microsoft.AspNetCore.Http;
using System.Text.Json;
using Ktc.Application.Logic;
using Ktc.Domain.Enums;
using Ktc.Infrastructure.KtcApi;
using Ktc.Infrastructure.Services;
using Xunit;

namespace Ktc.Tests;

public class HealthTests
{
    private static readonly DateTime Now = new(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);
    [Fact] public void Healthy_when_no_incidents_and_recent_contact() =>
        Assert.Equal(HealthStatus.Healthy, AtmHealthCalculator.Evaluate(new(Now.AddMinutes(-3), false, false, 0, 0, 0, 0), Now).Status);
    [Fact] public void Warning_when_contact_older_than_10_minutes() =>
        Assert.Equal(HealthStatus.Warning, AtmHealthCalculator.Evaluate(new(Now.AddMinutes(-11), false, false, 0, 0, 0, 0), Now).Status);
    [Fact] public void Warning_for_open_P3_P4() =>
        Assert.Equal(HealthStatus.Warning, AtmHealthCalculator.Evaluate(new(Now, false, false, 0, 0, 1, 0), Now).Status);
    [Theory, InlineData(1, 0), InlineData(0, 1)]
    public void Critical_for_open_P1_P2(int p1, int p2) =>
        Assert.Equal(HealthStatus.Critical, AtmHealthCalculator.Evaluate(new(Now, false, false, p1, p2, 0, 0), Now).Status);
    [Fact] public void Critical_when_offline_or_host_disconnect()
    {
        Assert.Equal(HealthStatus.Critical, AtmHealthCalculator.Evaluate(new(Now, true, false, 0, 0, 0, 0), Now).Status);
        Assert.Equal(HealthStatus.Critical, AtmHealthCalculator.Evaluate(new(Now, false, true, 0, 0, 0, 0), Now).Status);
    }
    [Fact] public void Unknown_last_contact_is_warning_not_healthy() =>
        Assert.Equal(HealthStatus.Warning, AtmHealthCalculator.Evaluate(new(null, false, false, 0, 0, 0, 0), Now).Status);
    [Fact] public void Score_is_bounded() => Assert.InRange(AtmHealthCalculator.Evaluate(new(null, true, true, 9, 9, 9, 9), Now).Score, 0, 100);
}

public class SlaTests
{
    private static readonly DateTime Now = new(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);
    [Fact] public void Breached_when_past_due_and_open() => Assert.Equal(SlaState.Breached, SlaCalculator.Evaluate(Now.AddMinutes(-1), null, Now));
    [Fact] public void Near_breach_within_one_hour() => Assert.Equal(SlaState.NearBreach, SlaCalculator.Evaluate(Now.AddMinutes(59), null, Now));
    [Fact] public void On_track_beyond_one_hour() => Assert.Equal(SlaState.OnTrack, SlaCalculator.Evaluate(Now.AddMinutes(61), null, Now));
    [Fact] public void Achieved_when_closed_before_due() => Assert.Equal(SlaState.Achieved, SlaCalculator.Evaluate(Now, Now.AddHours(-1), Now));
    [Fact] public void Closed_late_is_breached() => Assert.Equal(SlaState.Breached, SlaCalculator.Evaluate(Now.AddHours(-2), Now.AddHours(-1), Now));
    [Fact] public void Percent_counts_breached_only() => Assert.Equal(75, SlaCalculator.AchievedPercent(new[] { SlaState.Achieved, SlaState.Achieved, SlaState.OnTrack, SlaState.Breached }));
}

public class PanMaskerTests
{
    [Theory,
     InlineData("PAN: 4111111111111111 OK", "PAN: 411111******1111 OK"),
     InlineData("card 4111 1111 1111 1111", "card 411111******1111"),
     InlineData("5500-0055-5555-5559", "550000******5559"),
     InlineData("AMEX 378282246310005", "AMEX 378282*****0005")]
    public void Masks_pan(string input, string expected) => Assert.Equal(expected, PanMasker.Mask(input));
    [Fact] public void Leaves_amounts_and_short_numbers_alone() => Assert.Equal("AMOUNT 1500.00 SEQ 183443", PanMasker.Mask("AMOUNT 1500.00 SEQ 183443"));
    [Fact] public void Never_returns_raw_pan() => Assert.DoesNotContain("4111111111111111", PanMasker.Mask("x4111111111111111x 4111111111111111"));
    [Fact] public void Detects_pan_like_search_terms() { Assert.True(PanMasker.LooksLikePan("4111 1111 1111 1111")); Assert.False(PanMasker.LooksLikePan("ATM-001")); }
}

public class EjTimelineTests
{
    [Fact] public void Groups_transactions_and_masks()
    {
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var rows = new[] { ("1", t, "TRANSACTION START"), ("2", t.AddSeconds(1), "CARD INSERTED PAN: 4111111111111111"), ("3", t.AddSeconds(2), "TRANSACTION END - SUCCESS"),
                           ("4", t.AddMinutes(5), "TRANSACTION START"), ("5", t.AddMinutes(5).AddSeconds(1), "HOST RESPONSE: TIMEOUT") };
        var r = EjTimeline.Number(rows);
        Assert.Equal(new[] { 1, 1, 1, 2, 2 }, r.Select(x => x.TransactionNo));
        Assert.DoesNotContain(r, x => x.Text.Contains("4111111111111111"));
        Assert.Contains(EjTimeline.Build(r, 2), s => s.Level == "danger");
    }
}

public class FailureClassifierTests
{
    [Theory, InlineData("Host timeout - no response", FailureClassifier.Host), InlineData("Cash retract - note not taken", FailureClassifier.Retract),
     InlineData("Dispense failure - cassette jam", FailureClassifier.Dispense), InlineData("Communication failure - connection lost", FailureClassifier.Comm), InlineData("Card reader error", FailureClassifier.Other)]
    public void Classifies(string text, string expected) => Assert.Equal(expected, FailureClassifier.Classify(text));
}

public class PostmanImporterTests
{
    private static List<Ktc.Application.Models.ImportedEndpoint> Real()
    {
        using var fs = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "KTC_REST_API.postman_collection.json"));
        return PostmanImporter.Parse(fs);
    }
    [Fact] public void Parses_endpoints_from_names_when_url_raw_is_empty()
    {
        var e = Real();
        Assert.True(e.Count > 780);
        Assert.Contains(e, x => x.Method == "POST" && x.RelativeUrl == "api/v1/Login" && x.Module == "Login");
        Assert.Contains(e, x => x.Method == "POST" && x.RelativeUrl == "api/v1/Ticket/Search" && x.RequestBody!.Contains("SLAFilterType"));
        Assert.DoesNotContain(e, x => x.RelativeUrl.Contains("&amp;"));
    }
    [Fact] public void Skips_non_api_probe_requests() => Assert.All(Real(), x => Assert.StartsWith("api/v", x.RelativeUrl));
    [Theory, InlineData("Login", "POST", "api/v1/Login", EndpointKind.Authentication), InlineData("Ticket", "POST", "api/v1/Ticket/Search", EndpointKind.Search),
     InlineData("Client", "POST", "api/v1/Client/{id}/EJRow", EndpointKind.Search), InlineData("Ticket", "PUT", "api/v1/Ticket/{id}", EndpointKind.Update),
     InlineData("Client", "GET", "api/v1/Client/{id}", EndpointKind.Monitoring), InlineData("Client", "DELETE", "api/v1/Client/{id}", EndpointKind.Update)]
    public void Classifies(string module, string method, string url, EndpointKind kind) => Assert.Equal(kind, ApiCatalogRules.Classify(module, method, url));
    [Fact] public void Credential_endpoints_are_sensitive_and_never_enabled()
    {
        foreach (var e in Real().Where(x => x.RelativeUrl.Contains("Password", StringComparison.OrdinalIgnoreCase) || x.RelativeUrl.Contains("Atm3DesKey", StringComparison.OrdinalIgnoreCase) || x.RelativeUrl.Contains("BitLocker")))
        {
            Assert.True(ApiCatalogRules.IsSensitive(e.Module, e.RelativeUrl), e.RelativeUrl);
            Assert.False(ApiCatalogRules.EnabledByDefault(ApiCatalogRules.Classify(e.Module, e.Method, e.RelativeUrl), true));
        }
    }
    [Fact] public void Mutating_endpoints_are_disabled_by_default() => Assert.False(ApiCatalogRules.EnabledByDefault(EndpointKind.Update, false));
    [Fact] public void Spec_modules_resolve_to_screens()
    {
        Assert.Equal("Ticket Workbench", ApiCatalogRules.Known["Ticket"].Screen);
        Assert.Equal("Transaction Dashboard", ApiCatalogRules.Known["TransactionData"].Screen);
        Assert.NotNull(ApiCatalogRules.FindCurated("POST", "api/v1/Client/{id}/EJRow"));
    }
}

public class ApiExecutionTests
{
    [Fact] public void Builds_path_and_query_and_omits_unset_optionals()
    {
        var u = ApiExecutionService.BuildUrl("api/v1/Client/{id}/Tickets?statusfilter={statusfilter}&Numdays={Numdays}", new Dictionary<string, string> { ["id"] = "12", ["Numdays"] = "3" }, out var miss);
        Assert.Null(miss); Assert.Equal("api/v1/Client/12/Tickets?Numdays=3", u);
    }
    [Fact] public void Reports_missing_path_parameter() { ApiExecutionService.BuildUrl("api/v1/Client/{id}", new Dictionary<string, string>(), out var miss); Assert.Equal("id", miss); }
    [Fact] public void Escapes_parameter_values() => Assert.Equal("api/v1/Client/a%2Fb", ApiExecutionService.BuildUrl("api/v1/Client/{id}", new Dictionary<string, string> { ["id"] = "a/b" }, out _));
    [Fact] public void Flattens_json_to_report()
    {
        var r = ApiExecutionService.ToReport("t", "[{\"a\":1,\"b\":\"x\"},{\"a\":2}]");
        Assert.Equal(new[] { "a", "b" }, r.Columns); Assert.Equal(2, r.Rows.Count); Assert.Equal("", r.Rows[1][1]);
    }
}

public class ApiHealthTests
{
    [Theory, InlineData("/api/v1/Client/1234/Tickets?x=1", "/api/v1/Client/{id}/Tickets"), InlineData("api/v1/TransactionData/Search", "/api/v1/TransactionData/Search")]
    public void Normalizes_ids(string url, string expected) => Assert.Equal(expected, ApiHealthTracker.Normalize(url));

    [Fact] public void Colours_follow_thresholds_and_consecutive_failures()
    {
        var t = new ApiHealthTracker();
        for (int i = 0; i < 5; i++) t.Record("GET", "/a", 200, 500, false, null);
        Assert.Equal("green", t.Snapshot().Single(r => r.Endpoint == "/a").Color);
        for (int i = 0; i < 5; i++) t.Record("GET", "/b", 200, 3000, false, null);
        Assert.Equal("yellow", t.Snapshot().Single(r => r.Endpoint == "/b").Color);
        for (int i = 0; i < 5; i++) t.Record("GET", "/c", 200, 6000, false, null);
        Assert.Equal("red", t.Snapshot().Single(r => r.Endpoint == "/c").Color);
        for (int i = 0; i < 4; i++) t.Record("GET", "/d", 0, 100, true, "Timeout");
        var d = t.Snapshot().Single(r => r.Endpoint == "/d"); Assert.Equal("red", d.Color); Assert.Equal(4, d.ConsecutiveFailures); Assert.Equal(4, d.Timeouts);
    }
    [Fact] public void Success_rate_and_reset_of_consecutive()
    {
        var t = new ApiHealthTracker();
        t.Record("GET", "/x", 500, 10, false, "e"); t.Record("GET", "/x", 200, 10, false, null);
        var r = t.Snapshot().Single(); Assert.Equal(50, r.SuccessRatePct); Assert.Equal(0, r.ConsecutiveFailures);
    }
}

public class LoginParsingTests
{
    private static KtcAuthService Svc() => new(null!, Microsoft.Extensions.Options.Options.Create(new Ktc.Application.Options.KtcOptions { DefaultTokenLifetimeMinutes = 20 }), Microsoft.Extensions.Logging.Abstractions.NullLogger<KtcAuthService>.Instance);
    [Fact] public void Parses_quoted_token_string() { var r = Svc().ParseToken("\"abc123\""); Assert.True(r.Success); Assert.Equal("abc123", r.Token); Assert.True(r.ExpiresUtc > DateTime.UtcNow.AddMinutes(19)); }
    [Fact] public void Parses_object_with_expires_in() { var r = Svc().ParseToken("{\"access_token\":\"t\",\"expires_in\":60}"); Assert.Equal("t", r.Token); Assert.InRange((r.ExpiresUtc!.Value - DateTime.UtcNow).TotalSeconds, 50, 61); }
    [Fact] public void Reads_jwt_exp()
    {
        var exp = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();
        string B(string s) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var jwt = $"{B("{\"alg\":\"none\"}")}.{B($"{{\"exp\":{exp}}}")}.sig";
        Assert.InRange((Svc().ParseToken(jwt).ExpiresUtc!.Value - DateTime.UtcNow).TotalSeconds, 290, 301);
    }
    [Fact] public void Empty_token_fails() => Assert.False(Svc().ParseToken("{}").Success);
}

public class KtcJsonTests
{
    [Fact] public void Case_insensitive_aliases_and_dates()
    {
        using var d = JsonDocument.Parse("{\"clientid\":42,\"TimeAdded\":\"2026-11-12T10:00:00Z\",\"TimeClosed\":\"0001-01-01T00:00:00\"}");
        Assert.Equal("42", KtcJson.Str(d.RootElement, "ClientID"));
        Assert.NotNull(KtcJson.Date(d.RootElement, "TimeAdded"));
        Assert.Null(KtcJson.Date(d.RootElement, "TimeClosed"));       // SQL/.NET default date = not set
    }
    [Fact] public void Items_unwraps_envelopes() { using var d = JsonDocument.Parse("{\"items\":[{\"a\":1},{\"a\":2}]}"); Assert.Equal(2, KtcJson.Items(d.RootElement).Count()); }
}

public class LoginTokenCleanupTests
{
    private static KtcAuthService Svc() => new(null!, Microsoft.Extensions.Options.Options.Create(new Ktc.Application.Options.KtcOptions { DefaultTokenLifetimeMinutes = 20 }), Microsoft.Extensions.Logging.Abstractions.NullLogger<KtcAuthService>.Instance);
    [Fact] public void Strips_bearer_prefix() => Assert.Equal("abc", Svc().ParseToken("{\"Token\":\"Bearer abc\"}").Token);
    [Fact] public void Strips_whitespace_and_quotes_from_plain_text() => Assert.Equal("abc", Svc().ParseToken("  'abc'\r\n").Token);
    [Fact] public void Finds_token_inside_wrapper_object() => Assert.Equal("tok", Svc().ParseToken("{\"Success\":true,\"Data\":{\"access_token\":\"tok\"}}").Token);
    [Fact] public void Prefers_access_token_over_ktc_access_token() => Assert.Equal("a", Svc().ParseToken("{\"ktc_access_token\":\"k\",\"access_token\":\"a\"}").Token);
    [Fact] public void Falls_back_to_ktc_access_token() => Assert.Equal("k", Svc().ParseToken("{\"ktc_access_token\":\"k\"}").Token);
    [Fact] public void Past_body_expiry_falls_back_to_default_lifetime()
    {
        var r = Svc().ParseToken("{\"Token\":\"t\",\"Expires\":\"" + DateTime.UtcNow.AddHours(-3).ToString("s") + "\"}");
        Assert.True(r.ExpiresUtc > DateTime.UtcNow.AddMinutes(19));
    }
    [Fact] public void Describe_never_includes_values() => Assert.DoesNotContain("secret", KtcAuthService.Describe("{\"Token\":\"secret\",\"Data\":{\"x\":1}}"));
}

public class TokenRefreshTests
{
    private sealed class MemSession : Microsoft.AspNetCore.Http.ISession
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> _d = new();
        public bool IsAvailable => true; public string Id => "s"; public IEnumerable<string> Keys => _d.Keys;
        public void Clear() => _d.Clear(); public Task CommitAsync(CancellationToken c = default) => Task.CompletedTask; public Task LoadAsync(CancellationToken c = default) => Task.CompletedTask;
        public void Remove(string k) => _d.TryRemove(k, out _); public void Set(string k, byte[] v) => _d[k] = v;
        public bool TryGetValue(string k, out byte[] v) => _d.TryGetValue(k, out v!);
    }

    private sealed class FakeAuth : Ktc.Application.Abstractions.IKtcAuthService
    {
        public int Logins; public string? LastReloginBearer;
        private Ktc.Application.Models.KtcLoginResult Next() { var n = Interlocked.Increment(ref Logins); return new(true, "tok" + n, DateTime.UtcNow.AddMinutes(20), null); }
        public Task<Ktc.Application.Models.KtcLoginResult> LoginAsync(string u, string p, string? m, string? a = null, CancellationToken ct = default) => Task.FromResult(Next());
        public Task<Ktc.Application.Models.KtcLoginResult> ReLoginAsync(string u, string p, string? m, string? a = null, CancellationToken ct = default) => Task.FromResult(Next());
        public async Task<Ktc.Application.Models.KtcLoginResult> ReLoginAsync(string u, string p, string? m, string? a, string? bearer, CancellationToken ct = default) { LastReloginBearer = bearer; await Task.Delay(20, ct); return Next(); }
        public Task LogoffAsync(string token, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task Parallel_401s_trigger_one_relogin_and_share_the_new_token()
    {
        var user = "u" + Guid.NewGuid().ToString("N");
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, user) }, "test"));
        http.Features.Set<Microsoft.AspNetCore.Http.Features.ISessionFeature>(new Microsoft.AspNetCore.Http.Features.DefaultSessionFeature { Session = new MemSession() });
        var dp = new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider();
        http.Session.SetString(SessionKeys.Cred, KtcTokenProvider.ProtectCredentials(dp, user, "pw", "DOMAIN", "KTC_UI"));
        KtcTokenProvider.Remember(http, user, "tok0", DateTime.UtcNow.AddMinutes(20));

        var auth = new FakeAuth();
        var p = new KtcTokenProvider(new Microsoft.AspNetCore.Http.HttpContextAccessor { HttpContext = http }, auth,
            Microsoft.Extensions.Options.Options.Create(new Ktc.Application.Options.KtcOptions()), dp);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => p.RefreshAsync("tok0")));
        Assert.Equal(1, auth.Logins);
        Assert.All(results, t => Assert.Equal("tok1", t));
        Assert.Equal("tok0", auth.LastReloginBearer);
        Assert.Equal("tok1", await p.GetTokenAsync());
        Assert.Equal("tok1", http.Session.GetString(SessionKeys.Token));
    }
}

public class DeniedRouteTests
{
    private sealed class Always401 : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        { Interlocked.Increment(ref Calls); return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized) { ReasonPhrase = "Invalid token" }); }
    }
    private sealed class Tokens : Ktc.Application.Abstractions.IKtcTokenProvider
    {
        private int _n;
        public Task<string?> GetTokenAsync(CancellationToken ct = default) => Task.FromResult<string?>("t" + _n);
        public Task<string?> RefreshAsync(CancellationToken ct = default) => Task.FromResult<string?>("t" + Interlocked.Increment(ref _n));
        public string Identity => "service account test";
    }

    [Fact]
    public async Task Route_refused_with_fresh_token_is_paused_instead_of_hammered()
    {
        var h = new Always401();
        Ktc.Application.Abstractions.IKtcApiClient api = new KtcApiClient(new HttpClient(h) { BaseAddress = new Uri("https://ktc.test/") }, new Tokens(),
            new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<KtcApiClient>.Instance);
        var first = await api.GetAsync("api/v1/LastClientContact");
        Assert.Equal(2, h.Calls);                     // original + one retry with a fresh token
        Assert.Contains("service account test", first.Error);
        var second = await api.GetAsync("api/v1/LastClientContact");
        Assert.Equal(2, h.Calls);                     // paused: no further KTC calls
        Assert.False(second.Success);
    }
}

public class RedirectHandlerTests
{
    private sealed class RedirectOnce : HttpMessageHandler
    {
        public string? AuthOnSecond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            if (r.RequestUri!.AbsolutePath.StartsWith("/old"))
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.TemporaryRedirect) { Headers = { Location = new Uri("https://ktc.test/new/api/v1/Region") } });
            AuthOnSecond = r.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    [Fact]
    public async Task Same_host_redirect_keeps_bearer_header()
    {
        var inner = new RedirectOnce();
        var h = new KtcRedirectHandler(Microsoft.Extensions.Logging.Abstractions.NullLogger<KtcRedirectHandler>.Instance) { InnerHandler = inner };
        using var http = new HttpClient(h);
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://ktc.test/old/api/v1/Region");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "abc");
        var res = await http.SendAsync(req);
        Assert.Equal(System.Net.HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("Bearer abc", inner.AuthOnSecond);
    }
}

public class PermissionAndDefaultsTests
{
    private sealed class Always403 : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden)
            { Content = new StringContent("{\"Message\":\"User is not member of any of the required roles: Administrator,MergeClients\"}") });
        }
    }
    private sealed class Tokens : Ktc.Application.Abstractions.IKtcTokenProvider
    {
        public int Refreshes;
        public Task<string?> GetTokenAsync(CancellationToken ct = default) => Task.FromResult<string?>("t");
        public Task<string?> RefreshAsync(CancellationToken ct = default) { Refreshes++; return Task.FromResult<string?>("t2"); }
    }

    [Fact]
    public async Task Forbidden_is_reported_as_permission_and_not_retried()
    {
        var h = new Always403(); var tk = new Tokens();
        Ktc.Application.Abstractions.IKtcApiClient api = new KtcApiClient(new HttpClient(h) { BaseAddress = new Uri("https://ktc.test/") }, tk,
            new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<KtcApiClient>.Instance);
        var r = await api.GetAsync("api/v1/LastClientContact");
        Assert.Equal(403, r.StatusCode);
        Assert.StartsWith("Permission required: User is not member", r.Error);
        await api.GetAsync("api/v1/LastClientContact");
        Assert.Equal(1, h.Calls);
        Assert.Equal(0, tk.Refreshes);
    }

    [Fact]
    public void HistoricalError_without_query_gets_last_24h()
    {
        var q = ApiCatalogRules.DefaultQuery("api/v1/HistoricalError", new DateTime(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc));
        Assert.StartsWith("start=2026-11-12T10", Uri.UnescapeDataString(q!));
        Assert.Null(ApiCatalogRules.DefaultQuery("api/v1/HistoricalError?start=x", DateTime.UtcNow));
        Assert.Null(ApiCatalogRules.DefaultQuery("api/v1/Region", DateTime.UtcNow));
    }
}

public class BreakerTests
{
    private sealed class Always500 : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        { Interlocked.Increment(ref Calls); return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError) { Content = new StringContent("{\"Message\":\"boom\"}") }); }
    }

    [Fact]
    public async Task Http_500s_do_not_open_the_breaker_or_get_retried()
    {
        var h = new Always500();
        var opt = new Ktc.Application.Options.KtcOptions { UseMock = true, BaseUrl = "https://ktc.test/", BreakerFailures = 2 };
        var s = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.LoggingServiceCollectionExtensions.AddLogging(s);
        Microsoft.Extensions.DependencyInjection.MemoryCacheServiceCollectionExtensions.AddMemoryCache(s);
        Microsoft.Extensions.DependencyInjection.HttpServiceCollectionExtensions.AddHttpContextAccessor(s);
        Microsoft.Extensions.DependencyInjection.DataProtectionServiceCollectionExtensions.AddDataProtection(s);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(s, Microsoft.Extensions.Options.Options.Create(opt));
        s.AddKtcHttp(opt);
        Microsoft.Extensions.DependencyInjection.HttpClientBuilderExtensions.ConfigurePrimaryHttpMessageHandler(
            Microsoft.Extensions.DependencyInjection.HttpClientFactoryServiceCollectionExtensions.AddHttpClient<Ktc.Application.Abstractions.IKtcApiClient, KtcApiClient>(s), () => h);
        using var sp = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(s);
        var api = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Ktc.Application.Abstractions.IKtcApiClient>(sp);
        for (var i = 0; i < 6; i++)
        {
            var r = await api.GetAsync("api/v1/HistoricalError");
            Assert.Equal(500, r.StatusCode);
        }
        Assert.Equal(6, h.Calls);   // no retries, and the breaker never opened
    }
}
