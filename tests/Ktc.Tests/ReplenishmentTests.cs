using System.Text.Json;
using Ktc.Application.Abstractions;
using Ktc.Application.Logic;
using Ktc.Application.Models;
using Ktc.Application.Options;
using Ktc.Infrastructure.KtcApi;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ktc.Tests;

public class ReplenishmentTests
{
    [Fact]
    public void History_groups_replenishment_sessions_and_counts_the_cycle_until_the_next()
    {
        var t0 = new DateTime(2026, 11, 10, 6, 0, 0, DateTimeKind.Utc);
        TransactionDto T(string id, string type, DateTime at, string sid, bool ok = true, decimal? amount = null) =>
            new() { Id = id, Type = type, TimeUtc = at, SessionId = sid, Success = ok, Amount = amount };
        var tx = new List<TransactionDto>
        {
            T("1", "REPLENISH", t0, "S1"), T("2", "ADD_CASH", t0.AddMinutes(2), "S1"), T("3", "PRNT_CNTRS", t0.AddMinutes(3), "S1"),
            T("4", "Cash Withdrawal", t0.AddHours(1), "C1", amount: 500), T("5", "Cash Withdrawal", t0.AddHours(2), "C2", ok: false, amount: 900),
            T("6", "Deposit", t0.AddHours(3), "C3", amount: 1200), T("7", "Balance Inquiry", t0.AddHours(4), "C4"),
            T("8", "CLR_CASH", t0.AddDays(2), "S2", ok: false),
            T("9", "Cash Withdrawal", t0.AddDays(2).AddHours(1), "C5", amount: 300),
        };
        var h = ReplenishmentRules.History(tx, t0.AddDays(3));
        Assert.Equal(2, h.Count);
        Assert.Equal(t0.AddDays(2), h[0].StartUtc);   // newest first
        Assert.False(h[0].Success);
        Assert.Null(h[0].NextUtc);
        Assert.Equal((1, 1, 300m), (h[0].CustomerTransactions, h[0].Withdrawals, h[0].AmountWithdrawn));
        var first = h[1];
        Assert.Equal(new[] { "REPLENISH", "ADD_CASH", "PRNT_CNTRS" }, first.Operations);
        Assert.Equal(t0.AddDays(2), first.NextUtc);
        Assert.Equal((4, 1, 500m, 1, 1200m), (first.CustomerTransactions, first.Withdrawals, first.AmountWithdrawn, first.Deposits, first.AmountDeposited));
    }

    [Fact]
    public async Task Replenishment_tab_reads_cash_units_and_ktc_replenishment_routes()
    {
        var api = new Fake(new()
        {
            ["api/v1/Client"] = """[{"ID":19,"Name":"ATM-NINETEEN","BusinessID":1}]""",
            ["api/v1/Client/19/CurrentCashUnitStatus"] = """[{"ClientID":19,"DBComponentID":7,"TypeID":21,"StatusID":2,"CashUnitNumber":2,"Value":500.0,"Count":40,"Timestamp":"2026-11-16T05:37:39"},{"ClientID":19,"DBComponentID":7,"TypeID":21,"StatusID":1,"CashUnitNumber":1,"Value":100.0,"Count":812,"Timestamp":"2026-11-16T05:37:39"},{"ClientID":19,"DBComponentID":8,"TypeID":23,"StatusID":1,"CashUnitNumber":5,"Value":0.0,"Count":120}]""",
            ["api/v1/ComponentList"] = """{"7":{"ID":7,"DBName":"KXDevice.KXCashDispenser","DisplayName":"Cash Dispenser"},"8":{"ID":8,"DisplayName":"Cash Acceptor"}}""",
            ["api/v1/CashUnitType"] = """[{"ID":21,"Name":"Recycling"},{"ID":23,"Name":"Cash in"}]""",
            ["api/v1/CashUnitStatus"] = """[{"ID":1,"Name":"OK"},{"ID":2,"Name":"Low"}]""",
            ["api/v1/Client/19/ReplenishmentOrders"] = """[{"ID":4,"ClientID":19,"State":"Delivered","Amount":250000,"CardNumber":"4111111111111111"}]""",
            ["api/v1/Client/19/ReplenishmentsByTimeRange"] = "[]",
        });
        var r = await new KtcDataService(api, Microsoft.Extensions.Options.Options.Create(new KtcOptions()), new MemoryCache(new MemoryCacheOptions()), new HttpContextAccessor(), NullLogger<KtcDataService>.Instance)
            .GetReplenishmentAsync("19");
        Assert.NotNull(r);
        Assert.Equal(new[] { "1", "2", "5" }, r!.CashUnits.Select(u => u.Unit));
        var u2 = r.CashUnits[1];
        Assert.Equal(("Cash Dispenser", "Recycling", "Low", 500m, 40L, 20000m), (u2.Device, u2.Type, u2.Status, u2.Value!.Value, u2.Count, u2.Amount!.Value));
        Assert.Null(r.CashUnits[2].Amount);   // the acceptor's cash-in unit has no note value
        Assert.Contains("POST /ReplenishmentsByTimeRange", api.Calls.Select(c => c.Replace("api/v1/Client/19", "")));
        var orders = r.KtcRecords.Single(s => s.Title == "KTC replenishment orders");
        Assert.Null(orders.Error);
        Assert.Contains(orders.Records[0], f => f.Name == "State" && f.Value == "Delivered");
        Assert.DoesNotContain(orders.Records[0], f => f.Value.Contains("4111111111111111"));   // card numbers masked
        Assert.NotNull(r.KtcRecords.Single(s => s.Title == "Physical cassettes").Error);
        Assert.Empty(r.KtcRecords.Single(s => s.Title == "KTC replenishments").Records);
        Assert.Contains(r.Warnings, w => w.StartsWith("Replenishment: no replenishment"));
        Assert.Null(await new KtcDataService(api, Microsoft.Extensions.Options.Options.Create(new KtcOptions()), new MemoryCache(new MemoryCacheOptions()), new HttpContextAccessor(), NullLogger<KtcDataService>.Instance).GetReplenishmentAsync("NO-SUCH-ATM"));
    }

    // Live ATM 19: ReplenishmentsByTimeRange answered records of ids only; each is named through KTC's lists, the id kept for the tooltip.
    [Fact]
    public async Task Ktc_replenishment_records_show_names_for_their_ids()
    {
        var api = new Fake(new()
        {
            ["api/v1/Client"] = """[{"ID":19,"Name":"ATM-NINETEEN","BusinessID":1}]""",
            ["api/v1/Client/19/ReplenishmentsByTimeRange"] = """[{"ID":7,"ClientID":19,"BranchID":3,"DenominationID":28,"CurrencyID":1,"ReplenishmentProviderID":2,"SupervisorUserID":5,"DBComponentID":7,"ReasonTypeID":4,"GroupID":0,"Count":200,"Timestamp":"2026-11-15T08:00:00"}]""",
            ["api/v1/Branch"] = """[{"ID":3,"Name":"Corniche Branch"}]""",
            ["api/v1/Denomination"] = """[{"ID":28,"CurrencyID":1,"CurrencyValue":100.0}]""",
            ["api/v1/Currency"] = """[{"ID":1,"Code":"AED","Description":"UAE Dirham"}]""",
            ["api/v1/ReplenishmentProvider"] = """[{"ID":2,"Name":"G4S"}]""",
            ["api/v1/SupervisorUser"] = """[{"ID":5,"UserName":"cit.operator"}]""",
            ["api/v1/ComponentList"] = """{"7":{"ID":7,"DisplayName":"Cash Dispenser"}}""",
        });
        var r = await new KtcDataService(api, Microsoft.Extensions.Options.Options.Create(new KtcOptions()), new MemoryCache(new MemoryCacheOptions()), new HttpContextAccessor(), NullLogger<KtcDataService>.Instance)
            .GetReplenishmentAsync("19");
        var set = r!.KtcRecords.Single(s => s.Title == "KTC replenishments");
        var f = set.Records[0].ToDictionary(x => x.Name);
        Assert.Equal(("ATM-NINETEEN", "19"), (f["Client"].Value, f["Client"].Id));
        Assert.Equal(("Corniche Branch", "3"), (f["Branch"].Value, f["Branch"].Id));
        Assert.Equal(("AED 100", "28"), (f["Denomination"].Value, f["Denomination"].Id));
        Assert.Equal("AED", f["Currency"].Value);
        Assert.Equal("G4S", f["Replenishment Provider"].Value);
        Assert.Equal("cit.operator", f["Supervisor User"].Value);
        Assert.Equal("Cash Dispenser", f["Component"].Value);
        Assert.Equal("none", f["Group"].Value);
        Assert.Equal("7", f["ID"].Value);   // the record's own id stays
        Assert.Equal("4", f["Reason Type ID"].Value);
        Assert.Equal(new[] { "ReasonTypeID" }, set.UnnamedIds);
        Assert.Contains(r.Diagnostics, d => d.StartsWith("KTC replenishments: no KTC list names these id fields") && d.Contains("ReasonTypeID"));
    }

    // Live ATM 19: ReplenishmentsByTimeRange answered 500 SqlDateTime overflow (its date fields were not filled) and ReplenishmentOrders
    // 500 FormatException (ISO dates in the query). This KTC takes yyyyMMddHHmmssfff query dates and, here, a {StartDate, EndDate} body.
    [Fact]
    public async Task Replenishment_routes_retry_date_body_names_and_query_formats()
    {
        var api = new DateStrict();
        var r = await new KtcDataService(api, Microsoft.Extensions.Options.Options.Create(new KtcOptions()), new MemoryCache(new MemoryCacheOptions()), new HttpContextAccessor(), NullLogger<KtcDataService>.Instance)
            .GetReplenishmentAsync("19");
        var rep = r!.KtcRecords.Single(s => s.Title == "KTC replenishments");
        Assert.Null(rep.Error);
        Assert.Contains(rep.Records[0], f => f.Name == "Amount" && f.Value == "300000");
        Assert.Contains("\"StartDate\"", rep.Sent);
        var orders = r.KtcRecords.Single(s => s.Title == "KTC replenishment orders");
        Assert.Null(orders.Error);
        Assert.Matches(@"start=\d{17}&end=\d{17}", orders.Sent);
        Assert.Contains("POST api/v1/Client/19/ReplenishmentsByTimeRange {\"dt1\"", string.Join("|", api.Calls));   // tried first, then the others
    }

    [Fact]
    public void Failing_routes_get_a_hint()
    {
        var m = typeof(KtcDataService).GetMethod("ReplenishmentHint", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        string? H(string e) => (string?)m.Invoke(null, new object?[] { e, "{\"dt1\":\"x\"}" });
        Assert.StartsWith("KTC did not find the start / end dates", H("HTTP 500 Internal Server Error: SqlTypeException: SqlDateTime overflow."));
        Assert.StartsWith("KTC could not read the dates", H("HTTP 500: FormatException: String was not recognized as a valid DateTime."));
        Assert.StartsWith("This KTC server does not have this route", H("HTTP 404 Not Found"));
    }

    private sealed class DateStrict : IKtcApiClient
    {
        public List<string> Calls { get; } = new();
        public Task<KtcResponse> SendAsync(HttpMethod method, string url, object? body = null, KtcCallOptions? options = null, CancellationToken ct = default)
        {
            var path = url.Split('?')[0]; var b = body as string ?? "";
            lock (Calls) Calls.Add($"{method.Method} {path} {b}");
            KtcResponse Json(string j) { using var d = JsonDocument.Parse(j); return new KtcResponse(true, 200, d.RootElement.Clone(), j, null, 1); }
            KtcResponse Err(string m) => new(false, 500, null, m, "HTTP 500 Internal Server Error: " + m, 1);
            return Task.FromResult(path switch
            {
                "api/v1/Client" => Json("""[{"ID":19,"Name":"ATM-NINETEEN"}]"""),
                "api/v1/Client/19/ReplenishmentsByTimeRange" => b.Contains("\"StartDate\"") ? Json("""[{"ID":1,"Amount":300000}]""") : Err("SqlTypeException: SqlDateTime overflow. Must be between 1/1/1753 12:00:00 AM and 12/31/9999 11:59:59 PM."),
                "api/v1/Client/19/ReplenishmentOrders" => System.Text.RegularExpressions.Regex.IsMatch(url, @"start=\d{17}&end=\d{17}") ? Json("""[{"ID":4,"State":"Open"}]""") : Err("FormatException: String was not recognized as a valid DateTime."),
                _ => KtcResponse.Fail("HTTP 404 Not Found", 404),
            });
        }
    }

    private sealed class Fake(Dictionary<string, string> routes) : IKtcApiClient
    {
        public List<string> Calls { get; } = new();
        public Task<KtcResponse> SendAsync(HttpMethod method, string url, object? body = null, KtcCallOptions? options = null, CancellationToken ct = default)
        {
            var path = url.Split('?')[0];
            lock (Calls) Calls.Add($"{method.Method} {path}");
            if (!routes.TryGetValue(path, out var json)) return Task.FromResult(KtcResponse.Fail("HTTP 404 Not Found", 404));
            using var d = JsonDocument.Parse(json);
            return Task.FromResult(new KtcResponse(true, 200, d.RootElement.Clone(), json, null, 1));
        }
    }
}
