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

    [Theory]
    [InlineData("Card Reader", "IDC")]
    [InlineData("KXDevice.KXCashDispenser", "CDM")]
    [InlineData("Cash Acceptor", "CIM")]
    [InlineData("Journal Printer", "PTR")]
    [InlineData("Encrypting PinPad", "PIN")]
    [InlineData("Doors", "SIU")]
    [InlineData("Sensors", "SIU")]
    [InlineData("Application", null)]
    [InlineData("Camera", null)]
    [InlineData("Audio", null)]
    public void Maps_ktc_components_to_xfs_classes(string component, string? expected) => Assert.Equal(expected, CounterRules.XfsClass(component));

    [Theory]
    [InlineData("Cards retained", false, "3", true)]
    [InlineData("Media jams", false, "0", true)]
    [InlineData("Fatal error count", true, "2", true)]
    [InlineData("Some numeric", true, "17", true)]
    [InlineData("Firmware version", true, "4", false)]
    [InlineData("ShutdownDuration", true, "81", false)]
    [InlineData("Device status", false, "Online", false)]
    [InlineData("Cards retained", false, "n/a", false)]
    public void Picks_counter_properties(string name, bool numeric, string value, bool expected) => Assert.Equal(expected, CounterRules.IsCounter(name, numeric, value, out _));

    [Fact]
    public void Thresholds_match_by_the_longest_name_part()
    {
        var t = new KtcOptions().CounterThresholds;
        Assert.Equal(1, CounterRules.ThresholdFor("Fatal error count", t));
        Assert.Equal(3, CounterRules.ThresholdFor("Media jams", t));
        Assert.Equal(50, CounterRules.ThresholdFor("Read failures", t));
        Assert.Null(CounterRules.ThresholdFor("Cards inserted", t));
    }

    [Fact]
    public void Application_counters_count_from_the_last_replenishment()
    {
        var day = new DateTime(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc);
        TransactionDto T(string id, string session, string type, DateTime at, bool ok = true, string reason = "", string category = "") =>
            new() { Id = id, SessionId = session, Type = type, TimeUtc = at, Success = ok, Reason = reason, ErrorCode = ok ? "" : reason, FailureCategory = category };
        var tx = new[]
        {
            T("1", "10", "CashWithdrawal", day.AddDays(-3)),                              // before the reset: not counted
            T("2", "20", "REPLENISH", day.AddDays(-2)), T("3", "20", "ADD_CASH", day.AddDays(-2).AddMinutes(1)),
            T("4", "30", "CashWithdrawal", day.AddDays(-1)), T("5", "30", "BalanceInquiry", day.AddDays(-1)),
            T("6", "40", "CashWithdrawal", day.AddHours(5), false, "HostDeclined"),
            T("7", "41", "CashWithdrawal", day.AddHours(6), false, "HostTimeout"),
            T("8", "42", "PinChange", day.AddHours(7)), T("9", "43", "CashDepositToAccount", day.AddHours(8)),
            T("10", "44", "CashWithdrawal", day.AddHours(9), false, "Cash retract - note not taken", "CashRetract"),
            T("11", "45", "CashWithdrawal", day.AddHours(10), false, "RESTART"),
        };
        var (app, reset, type) = CounterRules.ApplicationFromTransactions(tx, day);
        Assert.Equal(day.AddDays(-2), reset);   // start of the latest replenishment session
        Assert.Equal("REPLENISH", type);
        long Today(string n) => app.Single(a => a.Name == n).Today;
        long Since(string n) => app.Single(a => a.Name == n).SinceReset;
        Assert.Equal((4, 5), (Today("Cash withdrawals"), Since("Cash withdrawals")));
        Assert.Equal((6, 7), (Today("Customer sessions"), Since("Customer sessions")));
        Assert.Equal((0, 1), (Today("Balance inquiries"), Since("Balance inquiries")));
        Assert.Equal(1, Today("Declined by host")); Assert.Equal(1, Today("Host timeouts")); Assert.Equal(1, Today("Cash retracts"));
        Assert.Equal(1, Today("PIN changes")); Assert.Equal(1, Today("Cash deposits")); Assert.Equal(1, Today("Application restarts"));
        Assert.Equal((0, 1), (Today("Supervisor entries"), Since("Supervisor entries")));  // the replenishment session itself
        Assert.Equal(CounterRules.ApplicationCounters.Length, app.Count);
    }

    [Fact]
    public async Task Builds_counters_from_real_ktc_shapes()
    {
        var c = await Service(new RealShapes()).GetCountersAsync("19");
        Assert.NotNull(c);
        // XFS: numeric / counter-named properties of the XFS components, plus the cash units.
        Assert.Contains(c!.Xfs, x => x.Device == "Card Reader" && x.ServiceClass == "IDC" && x.Counter == "Cards retained" && x.Value == 4 && x.Threshold == 20);
        Assert.Contains(c.Xfs, x => x.Device == "Card Reader" && x.Counter == "Fatal error count" && x.Status == "Warning");
        Assert.DoesNotContain(c.Xfs, x => x.Counter == "Device status" || x.Counter == "Firmware version" || x.Device == "Application");
        Assert.Contains(c.Xfs, x => x.Device == "Cash Dispenser" && x.Counter == "Cash unit 1 notes" && x.Value == 812);
        // Application: from TransactionData/Search rows (typeID / completionID / reasonID looked up in STXFieldLookup).
        Assert.Equal(2, c.Application.Single(a => a.Name == "Cash withdrawals").SinceReset);
        Assert.Equal(1, c.Application.Single(a => a.Name == "Declined by host").SinceReset);
        Assert.Equal(1, c.Application.Single(a => a.Name == "Balance inquiries").SinceReset);
        Assert.Contains(c.Diagnostics!, d => d.StartsWith("CurrentStatus: 6 rows"));
        Assert.Contains(c.Diagnostics!, d => d.Contains("4 for this ATM"));
    }

    // Records as KTC's own UI logged them for client 19: client;component;counter;value;delta. The JSON field names are not
    // documented, so the same record is read with several plausible spellings.
    [Theory]
    [InlineData("""{"ClientID":19,"ComponentName":"KXDevice.KXCardReader","CounterName":"BadReads","Value":233,"Delta":0}""")]
    [InlineData("""{"CID":19,"Component":"KXDevice.KXCardReader","Name":"BadReads","Count":233,"Difference":0}""")]
    [InlineData("""{"clientId":19,"dbComponentName":"KXDevice.KXCardReader","counter":"BadReads","currentValue":233,"previousValue":0}""")]
    [InlineData("""{"Client":19,"Device":"KXDevice.KXCardReader","Key":"BadReads","Total":"233"}""")]
    public void Reads_ktc_counter_records_by_content(string json)
    {
        var c = CounterRules.ReadKtcCounter(Json(json));
        Assert.NotNull(c);
        Assert.Equal(("Card Reader", "IDC", "Bad reads", 233L), (c!.Device, c.ServiceClass, c.Counter, c.Value));
    }

    [Theory]
    [InlineData("KXDevice.KXCardReader.ContactlessICCardReader", "Contactless IC Card Reader")]
    [InlineData("KXDevice.KXCashDispenser", "Cash Dispenser")]
    [InlineData("KXDevice.KXBundleCheckScanner", "Bundle Check Scanner")]
    public void Names_ktc_devices(string db, string expected) => Assert.Equal(expected, CounterRules.DeviceName(db));

    [Fact]
    public async Task Uses_ktc_counters_route_when_it_answers()
    {
        var api = new RealShapes { Counters = """
            [{"ClientID":19,"ComponentName":"KXDevice.KXCardReader","CounterName":"GoodReads","Value":185,"Delta":0},
             {"ClientID":19,"ComponentName":"KXDevice.KXCardReader","CounterName":"BadReads","Value":233,"Delta":0},
             {"ClientID":19,"ComponentName":"KXDevice.KXCardReader.ContactlessICCardReader","CounterName":"GoodChipReads","Value":230,"Delta":0},
             {"ClientID":19,"ComponentName":"KXDevice.KXCashAcceptor","CounterName":"RetainedND","Value":1,"Delta":0},
             {"ClientID":19,"ComponentName":"KXDevice.KXCashDispenser","CounterName":"Dispensed","Value":44,"Delta":0},
             {"ClientID":19,"ComponentName":"KXDevice.KXBundleCheckScanner","CounterName":"ItemsReturned","Value":3,"Delta":0}]
            """ };
        var c = await Service(api).GetCountersAsync("19");
        Assert.Contains("api/v1/Client/19/Counters", api.Urls);
        Assert.Contains(c!.Xfs, x => x.Device == "Card Reader" && x.ServiceClass == "IDC" && x.Counter == "Bad reads" && x.Value == 233 && x.Status == "Warning");   // over the 50 threshold
        Assert.Contains(c.Xfs, x => x.Device == "Contactless IC Card Reader" && x.ServiceClass == "IDC");
        Assert.Contains(c.Xfs, x => x.ServiceClass == "CDM" && x.Counter == "Dispensed" && x.Value == 44);
        Assert.Contains(c.Xfs, x => x.ServiceClass == "CIM" && x.Counter == "Retained ND");
        Assert.DoesNotContain(c.Xfs, x => x.Counter == "Cards retained");          // CurrentStatus not used for a device the route covers
        Assert.Contains(c.Xfs, x => x.Counter == "Cash unit 1 notes");               // cash units still added
        Assert.Contains(c.Diagnostics!, d => d.StartsWith("api/v1/Client/19/Counters: 6 device counters"));
        Assert.Equal(2, c.Application.Single(a => a.Name == "Cash withdrawals").SinceReset);   // application still from transactions
    }

    [Fact]
    public async Task Empty_ktc_data_gives_reasons_instead_of_null()
    {
        var c = await Service(new RealShapes { Empty = true }).GetCountersAsync("19");
        Assert.NotNull(c);
        Assert.Empty(c!.Xfs); Assert.Empty(c.Application);
        Assert.Contains(c.Warnings, w => w.StartsWith("XFS counters: KTC returned no counters and no CurrentStatus rows"));
        Assert.Contains(c.Warnings, w => w.StartsWith("Application counters: KTC returned no transactions"));
        Assert.Null(await Service(new RealShapes()).GetCountersAsync("NO-SUCH-ATM"));
    }

    [Fact]
    public async Task Counters_endpoint_is_an_optional_override()
    {
        var api = new RealShapes { Override = """{"Xfs":[{"ServiceClass":"CDM","Counter":"Pick failures","Value":41,"Threshold":40}]}""" };
        var c = await Service(api, new KtcOptions { CountersEndpoint = "/api/v2/Atm/{atmId}/Counters" }).GetCountersAsync("19");
        Assert.Contains("api/v2/Atm/19/Counters", api.Urls);
        Assert.Single(c!.Xfs, x => x.Counter == "Pick failures" && x.Status == "Warning");

        var failing = await Service(new RealShapes(), new KtcOptions { CountersEndpoint = "api/v2/Atm/{atmId}/Counters" }).GetCountersAsync("19");
        Assert.Contains(failing!.Xfs, x => x.Counter == "Cards retained");          // falls back to CurrentStatus
        Assert.Contains(failing.Diagnostics!, d => d.StartsWith("api/v2/Atm/19/Counters: "));
    }

    [Fact]
    public async Task Demo_data_has_application_and_xfs_counters_for_every_device()
    {
        var c = await Service(new MockClient()).GetCountersAsync("1019");
        Assert.NotNull(c);
        Assert.Equal("1019", c!.AtmId);
        Assert.Empty(c.Warnings);
        Assert.NotNull(c.LastResetUtc);
        Assert.True(c.ResetSource!.StartsWith("last "), c.ResetSource + " | " + string.Join(" | ", c.Diagnostics!));
        Assert.Equal(CounterRules.ApplicationCounters.Select(a => a.Name).Concat(new[] { "Notes dispensed", "Notes accepted" }), c.Application.Select(a => a.Name));
        Assert.True(c.Application.Single(a => a.Name == "Cash withdrawals").SinceReset > 0);
        Assert.True(c.Application.Single(a => a.Name == "Notes dispensed").SinceReset > 0, string.Join(" | ", c.Diagnostics!));
        Assert.True(c.Application.Single(a => a.Name == "Notes accepted").SinceReset > 0);
        Assert.Contains(c.NotesByType!, t => t.Type == "Cash Withdrawal" && t.NotesDispensed > 0 && t.NotesAccepted == 0);
        Assert.Contains(c.NotesByType!, t => t.Type == "Deposit" && t.NotesAccepted > 0);
        Assert.All(c.Application, a => Assert.True(a.SinceReset >= a.Today));
        Assert.All(c.Warnings, w => Assert.StartsWith("Application counters: notes are counted for", w));   // only the Ktc:TransactionNotesMax note
        Assert.Equal(new[] { "IDC", "CDM", "CIM", "IPM", "PTR", "PIN", "SIU" }, c.Xfs.Select(x => x.ServiceClass).Distinct());
        Assert.Contains(c.Xfs, x => x.Threshold != null);
    }

    // ---------------- notes dispensed / accepted ----------------

    [Theory]
    // Per denomination, as KTC's UI looks the denominations up: 2 + 3 notes.
    [InlineData("""[{"ClientID":19,"TransactionID":7,"ComponentName":"KXDevice.KXCashDispenser","CounterName":"Dispensed","DenominationID":1,"Value":2},{"ClientID":19,"TransactionID":7,"ComponentName":"KXDevice.KXCashDispenser","CounterName":"Dispensed","DenominationID":2,"Value":3}]""", 5, 0)]
    // A total row (denomination 0) next to the per-denomination rows is not added twice; Rejected / Retracted are not dispensed notes.
    [InlineData("""[{"Name":"Dispensed","DenominationID":0,"Count":5},{"Name":"Dispensed","DenominationID":1,"Count":2},{"Name":"Dispensed","DenominationID":2,"Count":3},{"Name":"Rejected","DenominationID":0,"Count":1},{"Name":"Retracted","Count":4}]""", 5, 0)]
    // Deposited wins over its Deposited_L2 / _L4 breakdown; Returned / Retained do not count.
    [InlineData("""[{"Counter":"Deposited","Value":12},{"Counter":"Deposited_L2","Value":2},{"Counter":"Deposited_L4","Value":10},{"Counter":"Returned","Value":3}]""", 0, 12)]
    [InlineData("""[]""", 0, 0)]
    public void Reads_notes_of_one_transaction(string json, long dispensed, long accepted)
    {
        var n = CounterRules.ReadTransactionNotes(Json(json));
        Assert.Equal(new TxNotes(dispensed, accepted), n);
    }

    [Fact]
    public void Transaction_without_note_counters_is_unreadable()
    {
        Assert.Null(CounterRules.ReadTransactionNotes(Json("""[{"CounterName":"GoodReads","Value":3}]""")));
        Assert.Null(CounterRules.ReadTransactionNotes(null));
    }

    [Fact]
    public void Notes_counters_count_today_since_reset_and_per_type()
    {
        var day = new DateTime(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc);
        (TransactionDto, TxNotes) R(string type, DateTime at, long d, long a) => (new TransactionDto { Id = at.Ticks.ToString(), Type = type, TimeUtc = at, Success = true }, new TxNotes(d, a));
        var (c, byType, running) = CounterRules.NotesFromTransactions(new[]
        {
            R("CashWithdrawal", day.AddHours(-5), 10, 0), R("CashWithdrawal", day.AddHours(2), 4, 0),
            R("CashDepositToAccount", day.AddHours(3), 0, 25), R("FastCash", day.AddHours(4), 2, 0),
        }, day);
        Assert.False(running);
        Assert.Equal(new ApplicationCounter("Cash", "Notes dispensed", 6, 16), c[0]);
        Assert.Equal(new ApplicationCounter("Cash", "Notes accepted", 25, 25), c[1]);
        Assert.Equal(new NotesByType("CashDepositToAccount", 1, 0, 25), byType[0]);
        Assert.Equal(new NotesByType("CashWithdrawal", 2, 14, 0), byType[1]);
    }

    [Fact]
    public void Running_note_totals_are_counted_as_the_change_per_transaction()
    {
        var day = new DateTime(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc);
        var rows = new[] { 1000L, 1010, 1014, 1030 }.Select((v, i) => (new TransactionDto { Id = i.ToString(), Type = "CashWithdrawal", TimeUtc = day.AddHours(i + 1) }, new TxNotes(v, 0))).ToList();
        var (c, _, running) = CounterRules.NotesFromTransactions(rows, day);
        Assert.True(running);
        Assert.Equal(30, c[0].SinceReset);   // 10 + 4 + 16, the first one has no previous value
    }

    [Fact]
    public async Task Notes_come_from_ktc_per_transaction_counters()
    {
        var api = new RealShapes
        {
            TxCounters = new()
            {
                ["1"] = """[{"ClientID":19,"ComponentName":"KXDevice.KXCashDispenser","CounterName":"Dispensed","DenominationID":1,"Value":3}]""",
                ["2"] = "[]",
            }
        };
        var c = await Service(api).GetCountersAsync("19");
        Assert.Equal(3, c!.Application.Single(a => a.Name == "Notes dispensed").SinceReset);
        Assert.Equal(0, c.Application.Single(a => a.Name == "Notes accepted").SinceReset);
        Assert.Contains(c.NotesByType!, t => t.Type == "CashWithdrawal" && t.Transactions == 2 && t.NotesDispensed == 3);
        Assert.StartsWith("KTC per-transaction counters of 2", c.NotesSource);
        Assert.Contains(c.Diagnostics!, d => d.StartsWith("api/v1/HistoricalCounter/ClientTransactionCounters: 2 cash transactions since the reset, 2 asked, 2 answered"));
    }

    [Fact]
    public async Task Notes_are_split_by_denomination_with_the_standard_values_always_listed()
    {
        var api = new RealShapes
        {
            // KTC's Denomination list: id 1 = 100, id 2 = 500 (value field), id 4 = 2000 (value only in the name); id 9 is not in it.
            Denominations = """[{"ID":1,"CurrencyCode":"AED","Value":100.0},{"ID":2,"CurrencyCode":"AED","Value":500.0},{"ID":4,"Name":"AED 2000"}]""",
            TxCounters = new()
            {
                ["1"] = """[{"ClientID":19,"ComponentID":7,"PropertyID":319,"DenominationID":1,"Value":900,"Delta":3,"TransactionID":1},{"ClientID":19,"ComponentID":7,"PropertyID":319,"DenominationID":2,"Value":400,"Delta":2,"TransactionID":1},{"ClientID":19,"ComponentID":7,"PropertyID":319,"DenominationID":9,"Value":50,"Delta":1,"TransactionID":1}]""",
                ["2"] = """[{"ClientID":19,"ComponentID":8,"PropertyID":320,"DenominationID":4,"Value":10,"Delta":4,"TransactionID":2},{"ClientID":19,"ComponentID":8,"PropertyID":320,"DenominationID":0,"Value":16,"Delta":6,"TransactionID":2}]""",
            }
        };
        var c = await Service(api).GetCountersAsync("19");
        Assert.Equal(6, c!.Application.Single(a => a.Name == "Notes dispensed").SinceReset);
        Assert.Equal(6, c.Application.Single(a => a.Name == "Notes accepted").SinceReset);
        var d = c.NotesByDenomination!;
        Assert.Equal(new[] { "50", "100", "200", "500", "1000", "2000", "unknown (id 9)", "Not split by denomination" }, d.Select(x => x.Denomination));
        NotesByDenomination R(string n) => d.Single(x => x.Denomination == n);
        Assert.Equal((0L, 0L), (R("50").NotesDispensed, R("50").NotesAccepted));
        Assert.Equal((3L, 300m), (R("100").NotesDispensed, R("100").AmountDispensed!.Value));
        Assert.Equal((2L, 1000m), (R("500").NotesDispensed, R("500").AmountDispensed!.Value));
        Assert.Equal((4L, 8000m), (R("2000").NotesAccepted, R("2000").AmountAccepted!.Value));
        Assert.Equal((1L, (decimal?)null), (R("unknown (id 9)").NotesDispensed, R("unknown (id 9)").AmountDispensed));
        Assert.Equal(2, R("Not split by denomination").NotesAccepted);   // the total record (6) counts 2 more than the per-denomination one
        Assert.Equal(c.Application.Single(a => a.Name == "Notes dispensed").SinceReset, d.Sum(x => x.NotesDispensed));
        Assert.Contains(c.Warnings, w => w.Contains("denomination id(s) 9 have no note value"));
        Assert.Contains(c.Diagnostics!, x => x.StartsWith("Denominations: KTC Denomination list id 1 = 100, id 2 = 500, id 4 = 2000; ids in these transactions: 1, 2, 4, 9"));
        var w = c.NotesByType!.Single(t => t.Type == "CashWithdrawal").Denominations!;
        Assert.Equal(new[] { "100", "500", "2000", "unknown (id 9)", "Not split by denomination" }, w.Select(x => x.Denomination));   // both test transactions are withdrawals
        Assert.Contains(CounterRules.ToReport(c, DateTime.UtcNow).Rows, r => r[0] == "Notes by denomination" && r[1] == "2000" && r[4] == "0 dispensed / 4 accepted");
    }

    // Live ATM 19: Denomination records are {ID, CurrencyID, CurrencyValue}; ids missing from the list are read from Denomination/{id},
    // then from Ktc:DenominationValues.
    [Fact]
    public async Task Denomination_ids_are_valued_from_currency_value_the_id_route_and_settings()
    {
        var api = new RealShapes
        {
            Denominations = """[{"ID":28,"CurrencyID":1,"CurrencyValue":100.0}]""",
            DenominationById = new() { ["29"] = """{"ID":29,"CurrencyID":1,"CurrencyValue":500.0}""" },
            TxCounters = new()
            {
                ["1"] = """[{"ClientID":19,"ComponentID":7,"PropertyID":319,"DenominationID":28,"Value":900,"Delta":3,"TransactionID":1},{"ClientID":19,"ComponentID":7,"PropertyID":319,"DenominationID":29,"Value":400,"Delta":2,"TransactionID":1},{"ClientID":19,"ComponentID":7,"PropertyID":319,"DenominationID":30,"Value":60,"Delta":1,"TransactionID":1},{"ClientID":19,"ComponentID":7,"PropertyID":319,"DenominationID":31,"Value":60,"Delta":1,"TransactionID":1}]""",
                // The declined withdrawal: KTC reports only the Remaining counter, so no notes moved.
                ["2"] = """[{"ClientID":19,"ComponentID":7,"PropertyID":321,"DenominationID":0,"Value":2100,"Delta":0,"TransactionID":2}]""",
            }
        };
        var c = await Service(api, new KtcOptions { DenominationValues = new() { ["30"] = 1000 } }).GetCountersAsync("19");
        NotesByDenomination R(string n) => c!.NotesByDenomination!.Single(x => x.Denomination == n);
        Assert.Equal((3L, "28"), (R("100").NotesDispensed, R("100").DenominationId));
        Assert.Equal((2L, "29"), (R("500").NotesDispensed, R("500").DenominationId));
        Assert.Equal((1L, "30"), (R("1000").NotesDispensed, R("1000").DenominationId));
        Assert.Equal(1, R("unknown (id 31)").NotesDispensed);
        Assert.Contains(c!.Warnings, w => w.Contains("id(s) 31 have no note value") && w.Contains("Ktc__DenominationValues__31=100"));
        Assert.DoesNotContain(c.Warnings, w => w.Contains("notes are counted for"));   // the declined withdrawal counts as 0 notes
        Assert.Equal(7, c.Application.Single(a => a.Name == "Notes dispensed").SinceReset);
    }

    [Fact]
    public async Task Transactions_without_note_counts_are_listed_with_the_reason()
    {
        var api = new RealShapes { TxCounters = new() { ["2"] = "null" } };   // 1: HTTP 404; 2: an empty answer
        var c = await Service(api).GetCountersAsync("19");
        Assert.Contains(c!.Warnings, w => w.StartsWith("Application counters: notes are counted for 1 of the 2 cash transactions since the reset. KTC's per-transaction counters route gave no note counts for the others: 1 HTTP 404"));
        Assert.Contains(c.Diagnostics!, d => d.Contains("(1 with an empty answer, counted as no notes)") && d.Contains("1 not answered (HTTP 404 x1)"));
        Assert.Contains(c.Diagnostics!, d => d.StartsWith("Cash transactions without note counts: 1 ") && d.Contains("success: HTTP 404"));
    }

    [Fact]
    public async Task Without_per_transaction_counters_notes_come_from_the_device_counters()
    {
        var api = new RealShapes { Counters = """
            [{"ClientID":19,"ComponentName":"KXDevice.KXCashDispenser","CounterName":"Dispensed","Value":440,"Delta":0},
             {"ClientID":19,"ComponentName":"KXDevice.KXCashAcceptor","CounterName":"Deposited","Value":120,"Delta":0}]
            """ };
        var c = await Service(api).GetCountersAsync("19");
        Assert.Equal(new ApplicationCounter("Cash", "Notes dispensed", CounterRules.Unknown, 440), c!.Application.Single(a => a.Name == "Notes dispensed"));
        Assert.Equal(120, c.Application.Single(a => a.Name == "Notes accepted").SinceReset);
        Assert.Null(c.NotesByType);
        Assert.Contains(c.Warnings, w => w.StartsWith("Application counters: KTC returned no per-transaction note counts"));
        var csv = CounterRules.ToReport(c, DateTime.UtcNow).Rows.Single(r => r[2] == "Notes dispensed");
        Assert.Equal("n/a", csv[3]);
    }

    // The shapes the live KTC sent for ATM 19 (Client/19/Counters and HistoricalCounter/ClientTransactionCounters): ids only, no names.
    [Fact]
    public async Task Reads_live_ktc_id_records_for_device_counters_and_notes()
    {
        var api = new RealShapes
        {
            Counters = """
                [{"ClientID":19,"DBComponentID":7,"DBPropertyID":319,"DenominationID":0,"CounterValue":440,"Timestamp":"2026-11-16T07:22:34","LastResetTimestamp":"2026-11-16T06:50:00","LastMismatchSize":0},
                 {"ClientID":19,"DBComponentID":7,"DBPropertyID":321,"DenominationID":0,"CounterValue":2100,"Timestamp":"2026-11-16T07:22:34","LastResetTimestamp":"2026-11-16T06:50:00","LastMismatchSize":0},
                 {"ClientID":19,"DBComponentID":8,"DBPropertyID":320,"DenominationID":0,"CounterValue":120,"Timestamp":"2026-11-16T07:22:34","LastResetTimestamp":"2026-11-16T06:50:00","LastMismatchSize":0},
                 {"ClientID":19,"DBComponentID":5,"DBPropertyID":322,"DenominationID":0,"CounterValue":233,"Timestamp":"2026-11-16T07:22:34","LastResetTimestamp":"2026-11-16T06:50:00","LastMismatchSize":0}]
                """,
            TxCounters = new()
            {
                // Dispenser and its sub-device both report the transaction: counted once. Remaining goes down: not notes.
                ["1"] = """[{"ClientID":19,"ComponentID":16,"PropertyID":319,"DenominationID":0,"Timestamp":"2026-11-16T07:22:34","Value":839,"Delta":2,"Reset":false,"UntilTimestamp":"0001-01-01T00:00:00","TransactionID":1},{"ClientID":19,"ComponentID":7,"PropertyID":319,"DenominationID":0,"Value":440,"Delta":2,"TransactionID":1},{"ClientID":19,"ComponentID":7,"PropertyID":321,"DenominationID":0,"Value":2100,"Delta":-2,"TransactionID":1}]""",
                ["2"] = """[{"ClientID":19,"ComponentID":8,"PropertyID":320,"DenominationID":0,"Value":120,"Delta":5,"TransactionID":2}]""",
            }
        };
        var c = await Service(api).GetCountersAsync("19");
        Assert.Contains(c!.Xfs, x => x.Device == "Cash Dispenser" && x.ServiceClass == "CDM" && x.Counter == "Dispensed" && x.Value == 440 && x.LogicalName == "component 7");
        Assert.Contains(c.Xfs, x => x.Device == "Cash Acceptor" && x.ServiceClass == "CIM" && x.Counter == "Deposited" && x.Value == 120);
        Assert.Contains(c.Xfs, x => x.Device == "Card Reader" && x.Counter == "Bad reads" && x.Status == "Warning");
        Assert.Contains(c.Diagnostics!, d => d.StartsWith("api/v1/Client/19/Counters: 4 counters named") && d.Contains("[property 319]"));
        Assert.Equal(2, c.Application.Single(a => a.Name == "Notes dispensed").SinceReset);
        Assert.Equal(5, c.Application.Single(a => a.Name == "Notes accepted").SinceReset);
        Assert.Contains(c.Diagnostics!, d => d.StartsWith("Per-transaction counters reported: ") && d.Contains("Cash Dispenser [component 7] / Dispensed [property 319]: delta 2 in 1 tx = NOTES DISPENSED"));
    }

    [Fact]
    public void Device_selection_filters_the_xfs_counters()
    {
        var x = new List<XfsCounter> { new("Cash Dispenser", "CDM", null, "Dispensed", 1, null), new("Card Reader", "IDC", null, "Bad reads", 1, null), new("Bundle Check Scanner", "IPM", null, "Items accepted", 1, null) };
        Assert.Equal(new[] { "CDM", "IPM" }, CounterRules.FilterClasses(x, "cdm, IPM").Select(c => c.ServiceClass));
        Assert.Equal(3, CounterRules.FilterClasses(x, "").Count);
        Assert.Empty(CounterRules.FilterClasses(x, "none"));
        Assert.Equal("IPM", CounterRules.XfsClass("KXDevice.KXBundleCheckScanner"));
        Assert.Equal("CIM", CounterRules.XfsClass("KXDevice.KXCashAcceptor"));
    }

    /// <summary>A KTC server answering in the shapes the real one sends (see the /diag/ktc-token report).</summary>
    private sealed class RealShapes : IKtcApiClient
    {
        public bool Empty { get; init; }
        public string? Counters { get; init; }
        public string? Override { get; init; }
        /// <summary>HistoricalCounter/ClientTransactionCounters answers by transaction id; null answers 404.</summary>
        public Dictionary<string, string>? TxCounters { get; init; }
        public string? Denominations { get; init; }
        /// <summary>GET Denomination/{id} answers by id; others answer 404.</summary>
        public Dictionary<string, string>? DenominationById { get; init; }
        public List<string> Urls { get; } = new();
        public Task<KtcResponse> SendAsync(HttpMethod method, string url, object? body = null, KtcCallOptions? options = null, CancellationToken ct = default)
        {
            var path = url.Split('?')[0];
            lock (Urls) Urls.Add(path);
            var now = DateTime.UtcNow; string D(DateTime d) => d.ToString("yyyy-MM-ddTHH:mm:ss");
            var txId = System.Text.RegularExpressions.Regex.Match(url, @"transactionid=(\d+)").Groups[1].Value;
            string? json = path switch
            {
                "api/v1/HistoricalCounter/ClientTransactionCounters" => TxCounters?.GetValueOrDefault(txId),
                "api/v1/Denomination" => Denominations,
                "api/v1/Client" => """[{"ID":19,"Name":"ATM-NINETEEN","BusinessID":0},{"ID":5,"Name":"ATM-FIVE","BusinessID":0}]""",
                "api/v2/Atm/19/Counters" => Override,
                "api/v1/Client/19/Counters" => Counters,
                "api/v1/Client/19/CurrentStatus" => Empty ? "[]" : """
                    [{"CID":19,"CompId":1,"PropId":70,"VID":0,"NumericValue":81.0,"UseNumeric":true,"Timestamp":"2026-11-10T11:58:33.903"},
                     {"CID":19,"CompId":5,"PropId":71,"VID":6,"UseNumeric":false,"Timestamp":"2026-11-16T06:55:51.27"},
                     {"CID":19,"CompId":5,"PropId":300,"VID":-1,"NumericValue":4.0,"UseNumeric":true,"Timestamp":"2026-11-16T06:55:51.27"},
                     {"CID":19,"CompId":5,"PropId":301,"VID":-1,"NumericValue":2.0,"UseNumeric":true,"Timestamp":"2026-11-16T06:55:51.27"},
                     {"CID":19,"CompId":5,"PropId":302,"VID":-1,"ManualValue":"v4.07","UseNumeric":false,"Timestamp":"2026-11-16T06:55:51.27"},
                     {"CID":19,"CompId":4,"PropId":303,"VID":-1,"NumericValue":9.0,"UseNumeric":true,"Timestamp":"2026-11-16T06:55:51.27"}]
                    """,
                "api/v1/Client/19/CurrentCashUnitStatus" => Empty ? "[]" : """[{"ClientID":19,"DBComponentID":7,"TypeID":21,"StatusID":1,"CashUnitNumber":1,"Value":100.0,"Count":812,"Total":0,"Timestamp":"2026-11-16T05:37:39.823"}]""",
                "api/v1/ComponentList" => """{"1":{"ID":1,"DBName":"Application","DisplayName":"Application"},"4":{"ID":4,"DBName":"KXDevice.KXCamera","DisplayName":"Camera"},"5":{"ID":5,"DBName":"KXDevice.KXCardReader","DisplayName":"Card Reader"},"7":{"ID":7,"DBName":"KXDevice.KXCashDispenser","DisplayName":"Cash Dispenser"},"8":{"ID":8,"DBName":"KXDevice.KXCashAcceptor","DisplayName":"Cash Acceptor"},"16":{"ID":16,"DBName":"KXDevice.KXCashDispenser.DN_CdmRm4V","DisplayName":"Cash Dispenser (DN_CdmRm4V)"}}""",
                "api/v1/PropertyList" => """{"70":{"ID":70,"DisplayName":"ShutdownDuration"},"71":{"ID":71,"DisplayName":"Device status"},"300":{"ID":300,"DisplayName":"Cards retained"},"301":{"ID":301,"DisplayName":"Fatal error count"},"302":{"ID":302,"DisplayName":"Firmware version"},"303":{"ID":303,"DisplayName":"Pictures taken"},"319":{"ID":319,"DisplayName":"Dispensed"},"320":{"ID":320,"DisplayName":"Deposited"},"321":{"ID":321,"DisplayName":"Remaining"},"322":{"ID":322,"DisplayName":"BadReads"}}""",
                "api/v1/ValueList" => """{"6":"IDLE"}""",
                "api/v1/Group" => """[{"ID":1,"Name":"Unknown Clients"},{"ID":2,"Name":"All Clients"}]""",
                "api/v1/TransactionType" => """{"1":{"ID":1,"Name":"CashWithdrawal","STXFieldLookupIDs":[4]}}""",
                "api/v1/STXFieldLookup" => """{"4":{"ID":4,"Description":"CashWithdrawal"},"52":{"ID":52,"Description":"BalanceInquiry"},"6":{"ID":6,"Description":"SUCCESS"},"10":{"ID":10,"Description":"FAIL"},"3":{"ID":3,"Description":"Normal"},"28":{"ID":28,"Description":"HostDeclined"}}""",
                "api/v1/TransactionData/Search" => Empty ? "{}" : $$$"""
                    {"1":{"transactionID":1,"sessionID":100,"timestampUTC":"{{{D(now.AddHours(-3))}}}","typeID":4,"completionID":6,"reasonID":3,"ClientID":19},
                     "2":{"transactionID":2,"sessionID":101,"timestampUTC":"{{{D(now.AddHours(-2))}}}","typeID":4,"completionID":10,"reasonID":28,"ClientID":19},
                     "3":{"transactionID":3,"sessionID":101,"timestampUTC":"{{{D(now.AddHours(-2))}}}","typeID":52,"completionID":6,"reasonID":3,"ClientID":19},
                     "4":{"transactionID":4,"sessionID":102,"timestampUTC":"{{{D(now.AddHours(-1))}}}","typeID":17,"completionID":18,"reasonID":0,"ClientID":19},
                     "5":{"transactionID":5,"sessionID":200,"timestampUTC":"{{{D(now.AddHours(-1))}}}","typeID":4,"completionID":6,"reasonID":3,"ClientID":5}}
                    """,
                _ when path.StartsWith("api/v1/Denomination/") => DenominationById?.GetValueOrDefault(path["api/v1/Denomination/".Length..]),
                _ => null
            };
            if (json == null) return Task.FromResult(KtcResponse.Fail("HTTP 404 Not Found", 404));
            using var d = JsonDocument.Parse(json);
            return Task.FromResult(new KtcResponse(true, 200, d.RootElement.Clone(), json, null, 1));
        }
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
            if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
            using var res = await _http.SendAsync(req, ct);
            var raw = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) return KtcResponse.Fail($"HTTP {(int)res.StatusCode}", (int)res.StatusCode);
            using var d = JsonDocument.Parse(raw);
            return new KtcResponse(true, (int)res.StatusCode, d.RootElement.Clone(), raw, null, 1);
        }
    }
}
