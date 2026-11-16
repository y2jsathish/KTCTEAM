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

public class TxDetailsTests
{
    private static JsonElement Json(string s) { using var d = JsonDocument.Parse(s); return d.RootElement.Clone(); }

    [Fact]
    public void Reads_stx_fields_keyed_by_field_id_with_lookups()
    {
        // Dictionary<short, KTCTransactionField>, as KTC's UI logged it: id;transactionId;time;...;STX field 17;value 22;LookupString.
        var json = Json("""{"17":{"ID":53202,"TransactionID":41703,"Timestamp":"2026-10-29T12:16:46","STXFieldID":17,"Value":"22","Type":"LookupString"},"30":{"STXFieldID":30,"Value":"4111111111111111","Type":"String"}}""");
        var f = TxDetailRules.ReadFields(json, id => id == "17" ? "TxCurrency" : null, v => v == "22" ? "AED" : null);
        Assert.Contains(f, x => x.Name == "TxCurrency" && x.Value == "AED" && x.Id == "17");
        Assert.Contains(f, x => x.Name == "Field 30" && x.Value == "411111******1111");   // PAN masked
    }

    [Fact]
    public void Reads_ej_lines_and_strips_ejdata_tags()
    {
        var lines = TxDetailRules.EjLines(Json("""["<EJData>_[020t 10:53:13 CARDLESS TRANSACTION STARTED</EJData>","<EJData>_[020t 10:54:28 CUSTOMER CANCELLED</EJData>"]"""));
        Assert.Equal(new[] { "_[020t 10:53:13 CARDLESS TRANSACTION STARTED", "_[020t 10:54:28 CUSTOMER CANCELLED" }, lines);
    }

    [Fact]
    public void Flattens_records_with_named_ids()
    {
        var f = TxDetailRules.Flatten(Json("""{"transactionID":42013,"typeID":4,"timestampUTC":"2026-11-03T06:53:13"}"""), (p, v) => p == "typeID" ? "CashWithdrawal" : null);
        Assert.Contains(f, x => x.Name == "Type ID" && x.Value == "CashWithdrawal (4)");
        Assert.Contains(f, x => x.Name == "Transaction ID" && x.Value == "42013");
        Assert.Equal("Value", Assert.Single(TxDetailRules.Flatten(Json("\"53471;03/11/2026 6:53:13 AM;80;19\""))).Name);
    }

    [Fact]
    public async Task Demo_transaction_has_details_ej_session_fields_and_dispense()
    {
        var svc = Service();
        var now = DateTime.UtcNow;
        var tx = (await svc.SearchTransactionsAsync(new TransactionQuery(null, null, null, now.AddDays(-3), now))).Where(t => t.Type == "Cash Withdrawal" && t.Success && t.Uuid.Length > 0).First();
        Assert.NotEmpty(tx.StartEjId);
        var d = await svc.GetTransactionDetailsAsync(tx.Id, tx.AtmId, tx.TimeUtc);
        Assert.NotNull(d);
        Assert.Equal(tx.Id, d!.Transaction.Id);
        Assert.NotEmpty(d.Record);
        Assert.Contains(d.Record, f => f.Name == "UUID" && f.Value == tx.Uuid);   // a UUID is not masked as a card number
        Assert.Contains(d.Ej, l => l.Contains("TRANSACTION STARTED"));
        Assert.DoesNotContain(d.Ej, l => System.Text.RegularExpressions.Regex.IsMatch(l, @"\d{16}"));        // PAN masked
        Assert.StartsWith("EJ rows ", d.EjSource);
        Assert.Contains(d.SessionTransactions, s => s.Id == tx.Id);
        Assert.Contains(d.SessionFields, f => f.Name == "Language" && f.Value == "en");
        Assert.Contains(d.Fields, f => f.Name == "Currency" && f.Value == "AED");
        Assert.Contains(d.Fields, f => f.Name == "Masked PAN" && f.Value.Contains('*'));
        Assert.Contains(d.Dispense, x => x.Kind == "Dispensed" && x.Device == "Cash Dispenser" && x.Denomination.StartsWith("AED") && x.Count > 0);
        Assert.Empty(d.Missing);
    }

    [Fact]
    public async Task Missing_parts_get_a_reason()
    {
        var svc = Service();
        var now = DateTime.UtcNow;
        var tx = (await svc.SearchTransactionsAsync(new TransactionQuery(null, null, null, now.AddDays(-3), now))).First(t => t.Type == "Balance Inquiry");
        var d = await svc.GetTransactionDetailsAsync(tx.Id, tx.AtmId, tx.TimeUtc);
        Assert.Contains("nothing dispensed", d!.Missing["dispense"]);
        Assert.Null(await svc.GetTransactionDetailsAsync("123", null, null));
    }

    private static KtcDataService Service() =>
        new(new MockClient(), Microsoft.Extensions.Options.Options.Create(new KtcOptions()), new MemoryCache(new MemoryCacheOptions()), new HttpContextAccessor(), NullLogger<KtcDataService>.Instance);

    private sealed class MockClient : IKtcApiClient
    {
        private readonly HttpClient _http = new(new MockKtcHttpHandler()) { BaseAddress = new Uri("https://ktc.test/") };
        public async Task<KtcResponse> SendAsync(HttpMethod method, string url, object? body = null, KtcCallOptions? options = null, CancellationToken ct = default)
        {
            using var req = new HttpRequestMessage(method, url);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer test");
            if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
            using var res = await _http.SendAsync(req, ct);
            var raw = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) return KtcResponse.Fail($"HTTP {(int)res.StatusCode}", (int)res.StatusCode);
            using var d = JsonDocument.Parse(raw);
            return new KtcResponse(true, (int)res.StatusCode, d.RootElement.Clone(), raw, null, 1);
        }
    }
}
