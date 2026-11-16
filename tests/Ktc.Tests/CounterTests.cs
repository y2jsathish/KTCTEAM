using System.Text.Json;
using Ktc.Application.Abstractions;
using Ktc.Application.Logic;
using Ktc.Application.Models;
using Ktc.Application.Options;
using Ktc.Infrastructure.KtcApi;
using Ktc.Infrastructure.Mock;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ktc.Tests;

public class CounterTests
{
    [Theory]
    [InlineData(2, null, "OK")]
    [InlineData(2, 3L, "OK")]
    [InlineData(3, 3L, "Warning")]
    [InlineData(9, 3L, "Warning")]
    public void Xfs_counter_is_flagged_at_or_above_threshold(long value, long? threshold, string expected)
    {
        Assert.Equal(expected, CounterRules.Status(value, threshold));
        Assert.Equal(expected, new XfsCounter("Card Reader", "IDC", null, "Media jams", value, threshold).Status);
    }

    [Fact]
    public void Parses_the_counters_payload()
    {
        var c = CounterRules.Parse(Json("""
            {"CollectedUtc":"2026-11-16T06:00:00Z","LastResetUtc":"2026-11-14T06:00:00Z",
             "Application":[{"Category":"Transactions","Name":"Cash withdrawals","Today":"12","SinceReset":340},{"Category":"Operations","Name":"Application restarts","Today":0,"SinceReset":1}],
             "Xfs":[{"Device":"Cash Dispenser","ServiceClass":"cdm","LogicalName":"CurrencyDispenser1","Counter":"Pick failures","Value":41,"Threshold":40},{"ServiceClass":"IDC","Counter":"Cards inserted","Value":900}]}
            """));
        Assert.Equal(new DateTime(2026, 11, 14, 6, 0, 0), c.LastResetUtc);
        Assert.Equal(2, c.Application.Count);
        Assert.Contains(c.Application, a => a.Name == "Cash withdrawals" && a.Today == 12 && a.SinceReset == 340);
        var pick = c.Xfs.Single(x => x.Counter == "Pick failures");
        Assert.Equal(("CDM", 41L, 40L, "Warning"), (pick.ServiceClass, pick.Value, pick.Threshold!.Value, pick.Status));
        var idc = c.Xfs.Single(x => x.ServiceClass == "IDC");
        Assert.Equal("Card Reader", idc.Device);   // device name filled in from the XFS service class
        Assert.Null(idc.Threshold);
        Assert.Equal("OK", idc.Status);
    }

    [Fact]
    public void Parses_a_flat_list_of_counter_records()
    {
        var c = CounterRules.Parse(Json("""
            {"data":[{"Category":"Exceptions","Name":"Host timeouts","Today":2,"SinceReset":9},{"Device":"Sensors","ServiceClass":"SIU","Counter":"Tamper alarms","Value":1,"Threshold":1}]}
            """));
        Assert.Single(c.Application, a => a.Category == "Exceptions" && a.Name == "Host timeouts");
        Assert.Single(c.Xfs, x => x.ServiceClass == "SIU" && x.Status == "Warning");
        Assert.Empty(CounterRules.Parse(null).Xfs);
    }

    [Fact]
    public void Export_lists_application_then_xfs_counters()
    {
        var c = new AtmCounters("1019", "ADC-ATM-020", null, new DateTime(2026, 11, 14, 6, 0, 0),
            new[] { new ApplicationCounter("Operations", "Supervisor entries", 0, 2), new ApplicationCounter("Transactions", "Cash withdrawals", 12, 1340) },
            new[] { new XfsCounter("Card Reader", "IDC", "CardReader1", "Media jams", 3, 3) }, Array.Empty<string>());
        var r = CounterRules.ToReport(c, DateTime.UtcNow);
        Assert.Equal(3, r.Rows.Count);
        Assert.Equal(new[] { "Application", "Transactions", "Cash withdrawals", "12", "1,340", "", "" }, r.Rows[0]);
        Assert.Equal("Operations", r.Rows[1][1]);
        Assert.Equal(new[] { "XFS", "Card Reader (IDC / CardReader1)", "Media jams", "", "3", "3", "Warning" }, r.Rows[2]);
        Assert.Contains(r.Summary, s => s.Label == "XFS at or over threshold" && s.Value == "1");
    }

    [Fact]
    public async Task Demo_data_has_application_and_xfs_counters_for_every_device()
    {
        var svc = Service(new MockClient());
        var c = await svc.GetCountersAsync("1019");
        Assert.NotNull(c);
        Assert.Equal("1019", c!.AtmId);
        Assert.Empty(c.Warnings);
        Assert.NotNull(c.LastResetUtc);
        foreach (var name in new[] { "Customer sessions", "Cash withdrawals", "Balance inquiries", "Mini statements", "Fund transfers", "PIN changes", "Cash deposits",
                                     "Declined by host", "Host timeouts", "Reversals", "Cards captured", "Cash retracts", "Supervisor entries", "Application restarts" })
            Assert.Contains(c.Application, a => a.Name == name);
        Assert.Equal(new[] { "Transactions", "Exceptions", "Operations" }, c.Application.Select(a => a.Category).Distinct());
        Assert.All(c.Application, a => Assert.True(a.SinceReset >= a.Today));
        Assert.Equal(new[] { "IDC", "CDM", "CIM", "PTR", "PIN", "SIU" }, c.Xfs.Select(x => x.ServiceClass).Distinct());
        Assert.Contains(c.Xfs, x => x.Threshold != null);
        Assert.Null(await svc.GetCountersAsync("NO-SUCH-ATM"));
    }

    [Fact]
    public async Task Unconfigured_or_failing_endpoint_gives_empty_counters_with_a_warning()
    {
        var off = await Service(new MockClient(), new KtcOptions { CountersEndpoint = "" }).GetCountersAsync("1019");
        Assert.Empty(off!.Xfs);
        Assert.Contains(off.Warnings, w => w.Contains("Ktc:CountersEndpoint"));

        var client = new MockClient();
        var custom = await Service(client, new KtcOptions { CountersEndpoint = "/api/v2/Atm/{atmId}/DeviceCounters" }).GetCountersAsync("1019");
        Assert.Contains("api/v2/Atm/1019/DeviceCounters", client.Urls);
        Assert.Empty(custom!.Application);
        Assert.Contains(custom.Warnings, w => w.Contains("DeviceCounters"));
    }

    private static JsonElement Json(string s) { using var d = JsonDocument.Parse(s); return d.RootElement.Clone(); }

    private static KtcDataService Service(IKtcApiClient api, KtcOptions? o = null) =>
        new(api, Microsoft.Extensions.Options.Options.Create(o ?? new KtcOptions()), new MemoryCache(new MemoryCacheOptions()), new HttpContextAccessor(), NullLogger<KtcDataService>.Instance);

    /// <summary>Sends calls to the built-in mock KTC server; an unknown route answers 404.</summary>
    private sealed class MockClient : IKtcApiClient
    {
        private readonly HttpClient _http = new(new MockKtcHttpHandler()) { BaseAddress = new Uri("https://ktc.test/") };
        public List<string> Urls { get; } = new();
        public async Task<KtcResponse> SendAsync(HttpMethod method, string url, object? body = null, KtcCallOptions? options = null, CancellationToken ct = default)
        {
            lock (Urls) Urls.Add(url.Split('?')[0]);
            if (url.StartsWith("api/v2/")) return KtcResponse.Fail("HTTP 404 Not Found", 404);
            using var req = new HttpRequestMessage(method, url);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer test");
            using var res = await _http.SendAsync(req, ct);
            var raw = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) return KtcResponse.Fail($"HTTP {(int)res.StatusCode}", (int)res.StatusCode);
            using var d = JsonDocument.Parse(raw);
            return new KtcResponse(true, (int)res.StatusCode, d.RootElement.Clone(), raw, null, 1);
        }
    }
}
