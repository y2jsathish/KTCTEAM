using System.Text.Json;
using Ktc.Application.Abstractions;
using Ktc.Application.Models;
using Ktc.Application.Options;
using Ktc.Infrastructure.KtcApi;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ktc.Tests;

/// <summary>Runs the transaction and EJ searches against the exact response shapes the live KTC server returned.</summary>
public class RealShapeTests
{
    private sealed class FakeKtc : IKtcApiClient
    {
        public readonly List<(string Method, string Url, string? Body)> Calls = new();
        public Task<KtcResponse> SendAsync(HttpMethod method, string url, object? body = null, KtcCallOptions? options = null, CancellationToken ct = default)
        {
            var b = body == null ? null : body as string ?? JsonSerializer.Serialize(body);
            lock (Calls) Calls.Add((method.Method, url, b));
            var path = url.Split('?')[0];
            string json = path switch
            {
                "api/v1/Client" => """[{"ID":19,"Name":"ATM-NINETEEN","BusinessID":0,"BranchID":0,"Level1RegionID":0,"IsActive":false,"DeleteLater":false},{"ID":5,"Name":"ATM-FIVE","BusinessID":0}]""",
                "api/v1/Business" or "api/v1/Region" or "api/v1/Branch" => "[]",
                "api/v1/Group" => """[{"ID":1,"Name":"Unknown Clients"},{"ID":2,"Name":"All Clients"}]""",
                "api/v1/TransactionType" => """[{"ID":1,"Name":"CashWithdrawal","STXFieldLookupIDs":[52,53]},{"ID":2,"Name":"CashDeposit","STXFieldLookupIDs":[60]}]""",
                "api/v1/STXFieldLookup" => """{"52":{"ID":52,"Name":"Fast Cash"},"6":{"ID":6,"Name":"Successful"},"7":{"ID":7,"Name":"Failed"},"3":{"ID":3,"Name":"None"}}""",
                "api/v1/TransactionData/Search" => """{"42328":{"transactionID":42328,"transaction_UUID":"f0b7","sessionID":53735,"timestampUTC":"2026-11-13T04:53:51","timestampLocal":"2026-11-13T08:53:51","amount":0.0,"typeID":52,"completionID":6,"reasonID":3,"ClientID":19}, "42329":{"transactionID":42329,"transaction_UUID":"a1","sessionID":53736,"timestampUTC":"2026-11-13T05:10:00","timestampLocal":"2026-11-13T09:10:00","amount":500.0,"typeID":60,"completionID":7,"reasonID":3,"ClientID":5}}""",
                "api/v1/Client/19/EJRow" => """[{"rid":9012,"aid":1,"tsp":"2026-11-13T09:11:50","ssid":5,"oid":3,"ejd":"TRANSACTION START","atsp":"2026-11-13T05:13:29.303"}, {"rid":9013,"aid":1,"tsp":"2026-11-13T09:11:55","ssid":5,"oid":3,"ejd":"CARD INSERTED 4111111111111111","atsp":"2026-11-13T05:13:29.303"}, {"rid":9014,"aid":1,"tsp":"2026-11-13T09:12:30","ssid":5,"oid":3,"ejd":"TRANSACTION END","atsp":"2026-11-13T05:13:29.303"}]""",
                _ => "[]"
            };
            using var d = JsonDocument.Parse(json);
            return Task.FromResult(new KtcResponse(true, 200, d.RootElement.Clone(), json, null, 1));
        }
    }

    private static (KtcDataService Svc, FakeKtc Api) Make()
    {
        var api = new FakeKtc();
        var opt = Microsoft.Extensions.Options.Options.Create(new KtcOptions());
        return (new KtcDataService(api, opt, new MemoryCache(new MemoryCacheOptions()), new HttpContextAccessor(), NullLogger<KtcDataService>.Instance), api);
    }

    [Fact]
    public async Task Transactions_from_id_keyed_map_are_mapped()
    {
        var (svc, api) = Make();
        var tx = await svc.SearchTransactionsAsync(new TransactionQuery(From: new DateTime(2026, 11, 13, 0, 0, 0, DateTimeKind.Utc), To: new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(2, tx.Count);
        var t = tx.Single(x => x.Id == "42328");
        Assert.Equal("19", t.AtmId);
        Assert.Equal("ATM-NINETEEN", t.AtmName);
        Assert.Equal("CashWithdrawal (Fast Cash)", t.Type);
        Assert.True(t.Success);
        Assert.Equal(new DateTime(2026, 11, 13, 4, 53, 51, DateTimeKind.Utc), t.TimeUtc);
        var f = tx.Single(x => x.Id == "42329");
        Assert.Equal("CashDeposit", f.Type);
        Assert.False(f.Success);
        Assert.Contains(api.Calls, c => c.Url == "api/v1/TransactionData/Search" && c.Body!.Contains("\"GroupID\":2"));

        var byAtm = await svc.SearchTransactionsAsync(new TransactionQuery(AtmId: "ATM-NINETEEN", From: new DateTime(2026, 11, 13, 0, 0, 0, DateTimeKind.Utc), To: new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc)));
        Assert.Single(byAtm);
        var byId = await svc.SearchTransactionsAsync(new TransactionQuery(AtmId: "19", From: new DateTime(2026, 11, 13, 0, 0, 0, DateTimeKind.Utc), To: new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc)));
        Assert.Single(byId);
        var partial = await svc.SearchTransactionsAsync(new TransactionQuery(AtmId: " nineteen ", From: new DateTime(2026, 11, 13, 0, 0, 0, DateTimeKind.Utc), To: new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc)));
        Assert.Single(partial);
        var none = await svc.SearchTransactionsAsync(new TransactionQuery(AtmId: "ADB05414", From: new DateTime(2026, 11, 13, 0, 0, 0, DateTimeKind.Utc), To: new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc)));
        Assert.Empty(none);
    }

    [Fact]
    public async Task Ej_rows_are_read_with_text_and_utc_time()
    {
        var (svc, api) = Make();
        foreach (var atm in new[] { "19", "ATM-NINETEEN", "nineteen" })
        {
            var ej = await svc.SearchEjAsync(new EjQuery(atm, new DateTime(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc)));
            Assert.Equal(3, ej.Count);
            Assert.Equal("9012", ej[0].Sequence);
            Assert.Equal(new DateTime(2026, 11, 13, 5, 11, 50, DateTimeKind.Utc), ej[0].TimeUtc);
            Assert.DoesNotContain(ej, e => e.Text.Contains("4111111111111111"));
            Assert.Contains(ej, e => e.Text.Contains("TRANSACTION START"));
        }
        Assert.Contains(api.Calls, c => c.Url == "api/v1/Client/19/EJRow" && c.Body!.Contains("\"Value3\":[]"));
        var card = await svc.SearchEjAsync(new EjQuery("19", new DateTime(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc), CardNumber: "4111111111111111"));
        Assert.NotEmpty(card);
    }
}
