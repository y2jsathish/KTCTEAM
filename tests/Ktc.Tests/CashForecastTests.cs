using Ktc.Application.Logic;
using Ktc.Application.Models;
using Ktc.Application.Options;
using Ktc.Infrastructure.Jobs;

namespace Ktc.Tests;

public class CashForecastTests
{
    private static readonly DateTime Now = new(2026, 11, 17, 8, 0, 0, DateTimeKind.Utc);   // Tuesday
    private static readonly CashForecastOptions O = new();
    private static CashUnitLevel U(string unit, decimal? value, long count, string type = "Recycling", DateTime? at = null) => new("Cash Dispenser", unit, type, "OK", value, count, at ?? Now);
    private static CashSnapshotPoint S(int dayOffset, string unit, decimal value, long notes) => new(Now.Date.AddDays(dayOffset), unit, value, notes);
    private static CashForecastInput In(IReadOnlyList<CashUnitLevel> units, IReadOnlyList<CashSnapshotPoint>? hist = null, IReadOnlyList<NotesByDenomination>? notes = null,
        DateTime? reset = null, decimal? perDay = null, IReadOnlyList<CashDailyPoint>? daily = null) =>
        new("1019", "ATM-1", "Bank", Now, Now.AddHours(4), units, null, hist ?? Array.Empty<CashSnapshotPoint>(), notes, reset, perDay, daily ?? Array.Empty<CashDailyPoint>());

    [Fact]
    public void Only_valued_non_reject_units_are_dispensing()
    {
        Assert.True(CashForecastRules.IsDispensing(U("1", 100, 10)));
        Assert.False(CashForecastRules.IsDispensing(U("5", null, 700, "Cash in")));
        Assert.False(CashForecastRules.IsDispensing(U("R", 100, 3, "Reject")));
        Assert.False(CashForecastRules.IsDispensing(U("6", 100, 3, "Cash in")));
    }

    [Fact]
    public void History_rate_uses_drops_and_skips_the_replenishment_interval()
    {
        // 1000 -> 900 -> 800 -> (refilled) 2000 -> 1900: three drops of 100 over three days; the refill interval is left out.
        var pts = new[] { S(-4, "1", 100, 1000), S(-3, "1", 100, 900), S(-2, "1", 100, 800), S(-1, "1", 100, 2000), S(0, "1", 100, 1900) };
        var (rate, days) = CashForecastRules.HistoryRate(pts, 3);
        Assert.Equal(100, rate);
        Assert.Equal(3, days);
        Assert.Null(CashForecastRules.HistoryRate(pts.Take(3), 3).Rate);   // only two intervals
    }

    [Fact]
    public void Counter_rate_divides_by_days_since_reset_and_nets_recycled_deposits()
    {
        Assert.Equal(50, CashForecastRules.CounterRate(100, 40, false, Now.AddDays(-2), Now).Rate);
        Assert.Equal(30, CashForecastRules.CounterRate(100, 40, true, Now.AddDays(-2), Now).Rate);
        Assert.Null(CashForecastRules.CounterRate(100, 0, false, null, Now).Rate);
        Assert.Equal(400, CashForecastRules.CounterRate(100, 0, false, Now.AddHours(-1), Now).Rate);   // at least a quarter day
    }

    [Fact]
    public void Days_left_and_status()
    {
        Assert.Equal(9.5, CashForecastRules.DaysLeft(1000, 50, 100, null, Now));
        Assert.Equal(0, CashForecastRules.DaysLeft(40, 50, 100, null, Now));
        Assert.Null(CashForecastRules.DaysLeft(1000, 50, null, null, Now));
        Assert.Equal(CashForecastRules.MaxDays, CashForecastRules.DaysLeft(100000, 50, 1, null, Now));
        Assert.Equal("Out", CashForecastRules.StatusFor(50, 50, 0));
        Assert.Equal("Critical", CashForecastRules.StatusFor(150, 50, 1));
        Assert.Equal("Warning", CashForecastRules.StatusFor(350, 50, 3));
        Assert.Equal("OK", CashForecastRules.StatusFor(1000, 50, 9.5));
        Assert.Equal("No usage data", CashForecastRules.StatusFor(1000, 50, null));
    }

    [Fact]
    public void Weekday_pattern_needs_two_weeks_and_shifts_the_run_out()
    {
        var start = new DateTime(2026, 11, 2);   // Monday
        var daily = Enumerable.Range(0, 14).Select(i => new CashDailyPoint(start.AddDays(i), 10, start.AddDays(i).DayOfWeek == DayOfWeek.Friday ? 3000 : 1000, 0, 0, false)).ToList();
        Assert.Null(CashForecastRules.WeekdayFactors(daily.Take(10), 14));
        var f = CashForecastRules.WeekdayFactors(daily, 14)!;
        Assert.True(f[(int)DayOfWeek.Friday] > 2 && f[(int)DayOfWeek.Monday] < 1);
        // From Wednesday midnight at 100 notes/day on average: 450 usable notes run out sooner when Friday is three times as busy.
        var wed = new DateTime(2026, 11, 18);
        Assert.True(CashForecastRules.DaysLeft(500, 50, 100, f, wed) < CashForecastRules.DaysLeft(500, 50, 100, null, wed));
    }

    [Fact]
    public void Build_prefers_snapshots_then_counters_then_amounts()
    {
        var units = new[] { U("1", 100, 1000), U("2", 200, 1000), U("3", 500, 1000) };
        var hist = new[] { S(-3, "1", 100, 1300), S(-2, "1", 100, 1200), S(-1, "1", 100, 1100), S(0, "1", 100, 1000) };
        var notes = new[] { new NotesByDenomination("200", 200, "2", 200, 0) };
        var f = CashForecastRules.Build(In(units, hist, notes, Now.AddDays(-2), perDay: 50_000), O);
        Assert.Equal(CashForecastRules.SourceHistory, f.Units[0].RateSource);
        Assert.Equal(100, f.Units[0].NotesPerDay);
        Assert.Equal(CashForecastRules.SourceCounters, f.Units[1].RateSource);
        Assert.Equal(100, f.Units[1].NotesPerDay);
        Assert.Equal(CashForecastRules.SourceAmounts, f.Units[2].RateSource);
        Assert.Equal("Very low", f.Units[2].Confidence);
        Assert.Equal("Very low", f.Confidence);                     // the weakest estimate
        Assert.Equal(800m * 1000, f.CashAmount);
        Assert.Contains(f.Warnings, w => w.Contains("rough guess"));
    }

    [Fact]
    public void Build_without_usage_says_so_and_suggests_nothing()
    {
        var f = CashForecastRules.Build(In(new[] { U("1", 100, 1000), U("5", null, 700, "Cash in") }), O);
        Assert.Single(f.Units);
        Assert.Equal("No usage data", f.Status);
        Assert.Null(f.Suggestion);
        Assert.Contains(f.Warnings, w => w.Contains("No usage known"));
    }

    [Fact]
    public void Suggestion_visits_before_the_earliest_run_out_and_caps_the_load()
    {
        var units = new[] { U("1", 100, 350), U("2", 1000, 2000) };
        var notes = new[] { new NotesByDenomination("100", 100, "1", 200, 0), new NotesByDenomination("1000", 1000, "4", 1000, 0) };
        var f = CashForecastRules.Build(In(units, notes: notes, reset: Now.AddDays(-2)), O);
        // unit 1: 100/day, 300 usable -> 3 days -> Warning; visit 1 day earlier
        Assert.Equal("Warning", f.Units[0].Status);
        var s = f.Suggestion!;
        Assert.Equal(Now.AddDays(2), s.VisitByUtc);
        var l1 = s.Loads.Single(l => l.Unit == "1");
        Assert.Equal(150, l1.ExpectedAtVisit);                      // 350 - 2 days x 100
        Assert.Equal(900, l1.TargetNotes);                          // 100 x 7 x 1.2 = 840 -> 900
        Assert.Equal(750, l1.AddNotes);
        var l2 = s.Loads.Single(l => l.Unit == "2");
        Assert.Equal(3000, l2.TargetNotes);                         // 500 x 7 x 1.2 = 4200, capped at what the cassette held when loaded (2000 + 1000 dispensed), above the configured 2500
        Assert.Equal(900 * 100 + 3000 * 1000m, s.TargetAmount);
    }

    [Fact]
    public void No_suggestion_beyond_the_horizon()
    {
        var notes = new[] { new NotesByDenomination("100", 100, "1", 20, 0) };
        var f = CashForecastRules.Build(In(new[] { U("1", 100, 2000) }, notes: notes, reset: Now.AddDays(-2)), O);   // 10/day -> 195 days
        Assert.Equal("OK", f.Status);
        Assert.Null(f.Suggestion);
    }

    private static EjCounterReport Ej(params EjCassetteCounter[] types) => new(Now, "1", "ATM-1", null, "17/11/26 08:00", null, null, types);
    private static EjCassetteCounter T(decimal den, long cassette, long dispensed) => new("TYPE", den, cassette, 0, dispensed, 0, cassette + dispensed, new Dictionary<string, long>());

    [Fact]
    public void Reconcile_matches_and_flags_differences_when_times_line_up()
    {
        var units = new[] { U("1", 100, 900, at: Now), U("2", 200, 500, at: Now) };
        var app = new[] { new NotesByDenomination("100", 100, "1", 35, 0), new NotesByDenomination("200", 200, "2", 67, 0) };
        var r = CashForecastRules.Reconcile(units, app, Now.AddDays(-3), Ej(T(100, 900, 35), T(200, 498, 70)), Now.AddMinutes(-30), Now.AddDays(-3).AddMinutes(10), 2000, TimeSpan.FromHours(2));
        Assert.Equal("Match", r.Rows.Single(x => x.Denomination == 100).Status);
        var r200 = r.Rows.Single(x => x.Denomination == 200);
        Assert.Equal("Differs", r200.Status);
        Assert.Contains("XFS 500 vs EJ 498", r200.Detail);
        Assert.Contains("application 67 vs EJ 70", r200.Detail);
        Assert.Equal("Differs", r.Status);
        Assert.Equal(102, r.AppDispensedTotal);
        Assert.Equal(105, r.EjDispensedTotal);
    }

    [Fact]
    public void Reconcile_does_not_compare_figures_from_different_times()
    {
        var units = new[] { U("1", 100, 900, at: Now) };
        var app = new[] { new NotesByDenomination("100", 100, "1", 35, 0) };
        var r = CashForecastRules.Reconcile(units, app, Now.AddDays(-1), Ej(T(100, 950, 10)), Now.AddDays(-1), Now.AddDays(-4), null, TimeSpan.FromHours(2));
        Assert.Equal("Not comparable", r.Status);
        Assert.Equal(2, r.Notes.Count);
        var none = CashForecastRules.Reconcile(units, null, null, null, null, null, null, TimeSpan.FromHours(2));
        Assert.Equal("Not comparable", none.Status);
        Assert.Contains(none.Notes, n => n.Contains("No EJ counter report"));
    }

    [Fact]
    public void Daily_counts_successful_customer_withdrawals_per_local_day()
    {
        TransactionDto Tx(string type, int hour, decimal amt, bool ok = true) => new() { Id = hour.ToString(), Type = type, TimeUtc = Now.Date.AddHours(hour), Amount = amt, Success = ok };
        var tx = new[] { Tx("Cash Withdrawal", 1, 500), Tx("Cash Withdrawal", 2, 300), Tx("Cash Withdrawal", 3, 900, ok: false), Tx("Deposit", 4, 200), Tx("Cash Withdrawal", 22, 100) };
        var d = CashForecastRules.Daily(tx, t => t.AddHours(4), Now.Date.AddDays(1));   // portal = UTC+4: 22:00 UTC is the next local day
        Assert.Equal(2, d.Count);
        Assert.Equal(800, d[0].Withdrawn);
        Assert.Equal(2, d[0].Withdrawals);
        Assert.False(d[0].Partial);
        Assert.Equal(1, d[0].Deposits);
        Assert.True(d[1].Partial);
    }

    [Fact]
    public void Snapshot_job_runs_at_start_up_once_a_day_and_after_the_snapshot_hour()
    {
        var day = new DateTime(2026, 11, 17);
        Assert.True(CashSnapshotJob.Due(null, day.AddHours(9), 23, startup: true));
        Assert.True(CashSnapshotJob.Due(day.AddDays(-1).AddHours(23.5), day.AddHours(9), 23, startup: true));
        Assert.False(CashSnapshotJob.Due(day.AddHours(8), day.AddHours(9), 23, startup: true));
        Assert.False(CashSnapshotJob.Due(day.AddHours(8), day.AddHours(22), 23, startup: false));
        Assert.True(CashSnapshotJob.Due(day.AddHours(8), day.AddHours(23.2), 23, startup: false));
        Assert.False(CashSnapshotJob.Due(day.AddHours(23.1), day.AddHours(23.5), 23, startup: false));
    }

    [Fact]
    public void Threshold_percent_converts_to_notes_and_defaults_to_today()
    {
        Assert.Equal(2, O.DefaultLowPct);                          // 50 of 2500
        Assert.Equal(50, O.LowNotesFor(2));
        Assert.Equal(250, O.LowNotesFor(10));
        Assert.Equal(13, O.LowNotesFor(0.5));                       // 12.5 rounded up
        Assert.Equal(2500, O.LowNotesFor(150));                     // clamped
        var c = O.WithLowNotes(250);
        Assert.Equal(250, c.LowNotes);
        Assert.Equal(50, O.LowNotes);                               // the original is unchanged
    }

    [Fact]
    public void Rethreshold_with_the_same_threshold_changes_nothing_and_a_higher_one_brings_run_out_forward()
    {
        var units = new[] { U("1", 100, 650), U("2", 200, 2000) };
        var notes = new[] { new NotesByDenomination("100", 100, "1", 200, 0), new NotesByDenomination("200", 200, "2", 100, 0) };
        var f = CashForecastRules.Build(In(units, notes: notes, reset: Now.AddDays(-2)), O);   // unit 1: 100/day, 600 usable -> 6 days
        var same = CashForecastRules.Rethreshold(f, O, Now.AddHours(4));
        Assert.Equal(f.Units.Select(u => (u.DaysLeft, u.Status)), same.Units.Select(u => (u.DaysLeft, u.Status)));
        Assert.Equal(f.Status, same.Status);
        Assert.Equal("OK", f.Status);

        var high = CashForecastRules.Rethreshold(f, O.WithLowNotes(O.LowNotesFor(16)), Now.AddHours(4));   // 400 notes
        Assert.Equal(2.5, high.Units[0].DaysLeft);
        Assert.Equal("Warning", high.Units[0].Status);
        Assert.Equal("Warning", high.Status);
        Assert.Equal(2.5, high.DaysLeft);
        Assert.Equal(Now.AddDays(1.5), high.Suggestion!.VisitByUtc);

        var out26 = CashForecastRules.Rethreshold(f, O.WithLowNotes(O.LowNotesFor(26)), Now.AddHours(4));  // 650 notes
        Assert.Equal("Out", out26.Units[0].Status);
        Assert.Equal("Out", out26.Status);
    }

    [Theory]
    [InlineData("Cheque Deposit", true)]
    [InlineData("Check Deposit", true)]
    [InlineData("DEPOSIT_CHEQUE", true)]
    [InlineData("Cheque In", true)]
    [InlineData("Deposit", false)]
    [InlineData("Cash Deposit", false)]
    [InlineData("Cheque Book Request", false)]
    [InlineData("Check Password", false)]
    public void Cheque_deposits_are_told_apart_from_cash_deposits(string type, bool cheque)
    {
        Assert.Equal(cheque, CounterRules.IsChequeType(type));
        var names = CounterRules.CountersFor(new TransactionDto { Type = type, Success = true }).ToList();
        if (cheque) { Assert.Contains("Cheque deposits", names); Assert.DoesNotContain("Cash deposits", names); }
        else Assert.DoesNotContain("Cheque deposits", names);
    }

    [Fact]
    public void Daily_counts_cheque_deposits_separately_and_they_are_not_cash_transactions()
    {
        TransactionDto Tx(string type, int hour, decimal amt) => new() { Id = hour.ToString(), Type = type, TimeUtc = Now.Date.AddHours(hour), Amount = amt, Success = true };
        var tx = new[] { Tx("Deposit", 1, 200), Tx("Cheque Deposit", 2, 5000), Tx("Cheque Deposit", 3, 1500), Tx("Cash Withdrawal", 4, 100) };
        var d = Assert.Single(CashForecastRules.Daily(tx, t => t, Now.Date.AddDays(1)));
        Assert.Equal((1, 200m), (d.Deposits, d.Deposited));
        Assert.Equal((2, 6500m), (d.ChequeDeposits, d.ChequeDeposited));
        Assert.Equal((1, 100m), (d.Withdrawals, d.Withdrawn));
        Assert.False(CounterRules.IsCashTransaction(tx[1]));
    }

    // ---- loaded vs remaining, per denomination ----
    private static NotesByDenomination N(decimal v, long disp, long acc = 0) => new(v.ToString("0"), v, null, disp, acc);

    [Fact]
    public void Loaded_is_remaining_plus_notes_dispensed_since_the_reset_and_split_by_denomination()
    {
        var units = new[] { U("1", 100, 1500, "Dispense"), U("2", 500, 900, "Dispense") };
        var f = CashForecastRules.Build(In(units, notes: new[] { N(100, 500), N(500, 100) }, reset: Now.AddDays(-2)), O);
        var u1 = f.Units.Single(u => u.Unit == "1"); var u2 = f.Units.Single(u => u.Unit == "2");
        Assert.Equal(2000, u1.Loaded); Assert.Equal(1000, u2.Loaded);
        Assert.Equal(CashForecastRules.LoadedFromCounters, u1.LoadedSource);
        var d100 = f.Denominations!.Single(d => d.Denomination == 100); var d500 = f.Denominations!.Single(d => d.Denomination == 500);
        Assert.Equal(75, d100.RemainingPct); Assert.Equal(200000, d100.LoadedAmount); Assert.Equal(150000, d100.Amount);
        Assert.Equal(90, d500.RemainingPct);
        Assert.Equal(250, d100.NotesPerDay);   // 500 notes over 2 days
    }

    [Fact]
    public void Recycling_units_net_deposits_out_of_the_loaded_count()
    {
        var f = CashForecastRules.Build(In(new[] { U("1", 100, 1000) }, notes: new[] { N(100, 600, 200) }, reset: Now.AddDays(-2)), O);
        Assert.Equal(1400, f.Units[0].Loaded);   // 1000 + (600 - 200)
    }

    [Fact]
    public void Loaded_falls_back_to_the_highest_snapshot_since_the_replenishment_and_gives_a_rate()
    {
        var input = In(new[] { U("1", 100, 1200, "Dispense") }, new[] { S(-3, "1", 100, 500), S(-2, "1", 100, 2000), S(-1, "1", 100, 1600) }) with { LastReplenishmentUtc = Now.AddDays(-2.5) };
        var f = CashForecastRules.Build(input, O);
        var u = f.Units[0];
        Assert.Equal(2000, u.Loaded); Assert.Equal(CashForecastRules.LoadedFromSnapshots, u.LoadedSource);
        Assert.Equal(CashForecastRules.SourceLoaded, u.RateSource); Assert.True(u.NotesPerDay > 0);
    }

    [Fact]
    public void Loaded_is_unknown_without_a_replenishment_or_counters_and_the_denomination_total_then_has_none()
    {
        var f = CashForecastRules.Build(In(new[] { U("1", 100, 1200), U("2", 100, 800) }), O);
        Assert.All(f.Units, u => Assert.Null(u.Loaded));
        var d = Assert.Single(f.Denominations!);
        Assert.Equal(2, d.Cassettes); Assert.Equal(2000, d.Notes); Assert.Null(d.Loaded); Assert.Null(d.RemainingPct);
    }

    [Fact]
    public void Unknown_usage_repeats_the_last_load_instead_of_filling_to_capacity()
    {
        var u = new CashUnitForecast("d", "1", "Dispense", 100, 30, 3000, null, "No usage data", 0, "None", 0, Now, "Out", Loaded: 1200);
        var s = CashForecastRules.Suggest(new[] { u }, Now, O)!;
        Assert.Equal(1200, s.Loads[0].TargetNotes);
    }

    [Fact]
    public void Rethreshold_recalculates_the_denomination_rows()
    {
        var f = CashForecastRules.Build(In(new[] { U("1", 100, 1000) }, notes: new[] { N(100, 500) }, reset: Now.AddDays(-2)), O);
        var f2 = CashForecastRules.Rethreshold(f, O.WithLowNotes(900), Now.AddHours(4));
        Assert.True(f2.Denominations![0].DaysLeft < f.Denominations![0].DaysLeft);
    }

    [Fact]
    public void Ktc_replenishment_record_wins_over_the_counters_for_the_loaded_count()
    {
        var units = new[] { U("1", 100, 1500, "Dispense"), U("2", 500, 900, "Dispense") };
        var loads = new[] { new KtcLoad("1", 100, 2400, Now.AddDays(-2)), new KtcLoad("2", 500, 800, Now.AddDays(-2)) };   // 800 < 900 now: ignored
        var f = CashForecastRules.Build(In(units, notes: new[] { N(100, 500), N(500, 100) }, reset: Now.AddDays(-2)) with { KtcLoads = loads }, O);
        var u1 = f.Units.Single(u => u.Unit == "1"); var u2 = f.Units.Single(u => u.Unit == "2");
        Assert.Equal(2400, u1.Loaded); Assert.StartsWith(CashForecastRules.LoadedFromKtc, u1.LoadedSource);
        Assert.Equal(1000, u2.Loaded); Assert.Equal(CashForecastRules.LoadedFromCounters, u2.LoadedSource);
    }

    [Fact]
    public void Ktc_record_without_counters_gives_loaded_and_a_rate_from_its_time()
    {
        var f = CashForecastRules.Build(In(new[] { U("1", 100, 1200, "Dispense") }) with { KtcLoads = new[] { new KtcLoad(null, 100, 2000, Now.AddDays(-4)) } }, O);
        Assert.Equal(2000, f.Units[0].Loaded);
        Assert.Equal(CashForecastRules.SourceLoaded, f.Units[0].RateSource); Assert.Equal(200, f.Units[0].NotesPerDay);
    }

    [Fact]
    public void Replenishment_record_parser_reads_nested_cassette_lines_and_picks_the_newest_record()
    {
        using var d = System.Text.Json.JsonDocument.Parse("""
            [ {"ID":1,"ReplenishmentTime":"2026-11-10T06:00:00Z","Cassettes":[{"CassetteNumber":1,"Denomination":100,"Count":1000}]},
              {"ID":2,"ReplenishmentTime":"2026-11-14T06:00:00Z","Cassettes":[{"CassetteNumber":1,"Denomination":100,"Count":2000},{"CassetteNumber":2,"Denomination":200,"Count":1500}]} ]
            """);
        var loads = ReplenishmentLoadRules.Parse(new System.Text.Json.JsonElement?[] { d.RootElement.Clone() });
        Assert.Equal(2, loads.Count); Assert.Equal(2000, loads.Single(l => l.Unit == "1").Notes); Assert.Equal(200, loads.Single(l => l.Unit == "2").Denomination);
        Assert.Empty(ReplenishmentLoadRules.Parse(new System.Text.Json.JsonElement?[] { null }));
    }

    // The Replenishments tab of the KTC client (screenshot): replenishment 404790 / 404796 loaded 3000 / 1800 / 2000 (AED 100 / 500 / 1000); 407370 later set Loaded
    // to 0 (cash removed). The JSON below is rebuilt from that screen: the field names are a GUESS (the traces only log decoded rows), the values are real.
    private const string ReplenishmentsScreen = """
        [ {"ID":404790,"ClientID":18,"ComponentID":7,"Time":"2026-10-22T03:08:31Z","TransactionID":18711012,"Details":[
             {"Property":"Loaded","Denomination":"AED 100","Before":3000,"After":3000},{"Property":"Loaded","Denomination":"AED 500","Before":1800,"After":1800},{"Property":"Loaded","Denomination":"AED 1000","Before":2000,"After":2000},
             {"Property":"Remaining","Denomination":"AED 100","Before":1849,"After":1849},{"Property":"Dispensed","Denomination":"AED 100","Before":1150,"After":1150}]},
          {"ID":404796,"ClientID":18,"ComponentID":7,"Time":"2026-10-22T03:09:12Z","TransactionID":18711017,"Details":[
             {"Property":"Loaded","Denomination":"AED 100","Before":3000,"After":3000},{"Property":"Loaded","Denomination":"AED 500","Before":1800,"After":1800},{"Property":"Loaded","Denomination":"AED 1000","Before":2000,"After":2000}]},
          {"ID":404791,"ClientID":18,"ComponentID":16,"Time":"2026-10-22T03:09:12Z","TransactionID":18711017,"Details":[]},
          {"ID":407370,"ClientID":18,"ComponentID":7,"Time":"2026-10-22T13:19:22Z","TransactionID":18739639,"Details":[
             {"Property":"Loaded","Denomination":"AED 100","Before":3000,"After":0},{"Property":"Loaded","Denomination":"AED 500","Before":1800,"After":0},{"Property":"Loaded","Denomination":"AED 1000","Before":2000,"After":0}]} ]
        """;

    [Fact]
    public void Replenishments_tab_shape_gives_loaded_after_per_denomination_and_skips_the_removal()
    {
        using var d = System.Text.Json.JsonDocument.Parse(ReplenishmentsScreen);
        var loads = ReplenishmentLoadRules.Parse(new System.Text.Json.JsonElement?[] { d.RootElement.Clone() });
        Assert.Equal(3, loads.Count);
        Assert.Equal(3000, loads.Single(l => l.Denomination == 100).Notes);
        Assert.Equal(1800, loads.Single(l => l.Denomination == 500).Notes);
        Assert.Equal(2000, loads.Single(l => l.Denomination == 1000).Notes);
        Assert.All(loads, l => Assert.Equal(new DateTime(2026, 10, 22, 3, 9, 12, DateTimeKind.Utc), l.TimeUtc));   // 404796, the newest load; 407370 only removed cash
    }

    [Fact]
    public void A_load_per_note_value_is_split_over_the_cassettes_holding_that_value()
    {
        var units = new[] { U("1", 100, 1500, "Dispense"), U("2", 100, 500, "Dispense") };
        var f = CashForecastRules.Build(In(units) with { KtcLoads = new[] { new KtcLoad(null, 100, 3000, Now.AddDays(-4)) } }, O);
        Assert.Equal(2250, f.Units.Single(u => u.Unit == "1").Loaded); Assert.Equal(750, f.Units.Single(u => u.Unit == "2").Loaded);
        Assert.Equal(3000, f.Denominations!.Single().Loaded);
    }

    [Fact]
    public void Empty_cassettes_everywhere_get_a_warning_and_loaded_gives_a_usage()
    {
        var units = new[] { U("1", 100, 0, "Dispense"), U("2", 500, 0, "Dispense") };
        var loads = new[] { new KtcLoad(null, 100, 3000, Now.AddDays(-5)), new KtcLoad(null, 500, 1800, Now.AddDays(-5)) };
        var f = CashForecastRules.Build(In(units) with { KtcLoads = loads }, O);
        Assert.Contains(f.Warnings, w => w.Contains("0 notes"));
        Assert.All(f.Units, u => Assert.Equal(CashForecastRules.SourceLoaded, u.RateSource));   // no history, but loaded minus remaining gives a usage
        Assert.Equal(600, f.Units[0].NotesPerDay);   // 3000 notes over 5 days
        var s = f.Suggestion!;
        Assert.All(s.Loads, l => Assert.True(l.UsageKnown));   // sized from the usage (capped at the capacity), not blindly filled
    }

    [Fact]
    public void Same_name_different_ids_gives_one_warning_naming_the_ids()
    {
        var w = CashForecastRules.DuplicateNameWarning(new[] { ("7", "ADB04098"), ("19", "adb04098"), ("8", "ADB05101") });
        Assert.Contains("ADB04098", w); Assert.Contains("19, 7", w); Assert.DoesNotContain("ADB05101", w);
        Assert.Null(CashForecastRules.DuplicateNameWarning(new[] { ("1", "A"), ("2", "B") }));
    }

    [Fact]
    public void An_empty_cassette_that_the_replenishment_record_does_not_list_is_not_loaded_and_not_out()
    {
        var units = new[] { U("1", 100, 2000, "Dispense"), U("2", 200, 0, "Dispense"), U("3", 500, 1800, "Dispense") };
        var loads = new[] { new KtcLoad(null, 100, 3000, Now.AddDays(-5)), new KtcLoad(null, 500, 2000, Now.AddDays(-5)) };   // no 200 line
        var f = CashForecastRules.Build(In(units) with { KtcLoads = loads }, O);
        Assert.Equal(CashForecastRules.StatusNotLoaded, f.Units.Single(u => u.Unit == "2").Status);
        Assert.NotEqual(CashForecastRules.StatusOut, f.Status);
        Assert.DoesNotContain(f.Denominations!, d => d.Denomination == 200);
        Assert.DoesNotContain(f.Suggestion?.Loads ?? Array.Empty<CashLoadLine>(), l => l.Unit == "2");
        Assert.Contains(f.Warnings, w => w.Contains("not loaded"));
    }

    [Fact]
    public void Status_reason_names_the_cassette_behind_the_status()
    {
        var f = CashForecastRules.Build(In(new[] { U("1", 100, 3000, "Dispense"), U("3", 500, 300, "Dispense") }) with { KtcLoads = new[] { new KtcLoad(null, 100, 3000, Now.AddDays(-3)), new KtcLoad(null, 500, 3000, Now.AddDays(-3)) } }, O.WithLowNotes(500));
        Assert.Equal(CashForecastRules.StatusOut, f.Status);
        Assert.Contains("Cassette 3", f.StatusReason); Assert.Contains("300", f.StatusReason); Assert.Contains("500", f.StatusReason);
        Assert.Null(CashForecastRules.Build(In(new[] { U("1", 100, 3000, "Dispense") }), O).StatusReason);
    }

    [Fact]
    public void A_replenishment_record_older_than_the_last_replenishment_is_ignored()
    {
        var units = new[] { U("1", 100, 2000, "Dispense") };
        var input = In(units) with { LastReplenishmentUtc = Now.AddHours(-3), KtcLoads = new[] { new KtcLoad(null, 100, 9000, Now.AddDays(-30)) } };
        var f = CashForecastRules.Build(input, O);
        Assert.Null(f.Units[0].Loaded);
        Assert.Contains(f.Warnings, w => w.Contains("older than the last replenishment"));
        var fresh = CashForecastRules.Build(input with { KtcLoads = new[] { new KtcLoad(null, 100, 3000, Now.AddHours(-3)) } }, O);
        Assert.Equal(3000, fresh.Units[0].Loaded);
    }

    [Fact]
    public void Usage_from_a_few_hours_of_counters_or_loaded_minus_remaining_is_not_extrapolated()
    {
        var units = new[] { U("1", 100, 2900, "Dispense") };
        var counters = CashForecastRules.Build(In(units, notes: new[] { N(100, 100) }, reset: Now.AddHours(-5)), O);   // 5 hours: too short
        Assert.NotEqual(CashForecastRules.SourceCounters, counters.Units[0].RateSource);
        Assert.Equal(3000, counters.Units[0].Loaded);   // the loaded count itself is still known
        var rec = CashForecastRules.Build(In(units) with { KtcLoads = new[] { new KtcLoad(null, 100, 3000, Now.AddHours(-5)) } }, O);
        Assert.NotEqual(CashForecastRules.SourceLoaded, rec.Units[0].RateSource);
        var day = CashForecastRules.Build(In(units, notes: new[] { N(100, 100) }, reset: Now.AddDays(-1)), O);
        Assert.Equal(CashForecastRules.SourceCounters, day.Units[0].RateSource);
    }

    [Fact]
    public void Same_unit_numbers_on_two_devices_and_identical_readings_are_flagged()
    {
        CashUnitLevel D(string dev, string unit, long n) => new(dev, unit, "Dispense", "OK", 100, n, Now);
        var f = CashForecastRules.Build(In(new[] { D("A", "8", 1000), D("B", "8", 1000), D("B", "9", 500) }), O);
        Assert.Contains(f.Warnings, w => w.Contains("more than one device"));
        Assert.Contains(f.Warnings, w => w.Contains("same note value and count"));
    }

    [Fact]
    public void A_cassette_loaded_above_the_configured_capacity_is_not_told_to_load_less_than_it_holds()
    {
        var u = new CashUnitForecast("d", "1", "Dispense", 100, 400, 40000, 600, "Loaded minus remaining", 4, "Low", 0.5, Now, "Critical", Loaded: 3000, LoadedSource: "x");
        var s = CashForecastRules.Suggest(new[] { u }, Now, O)!;
        Assert.True(s.Loads[0].TargetNotes >= 400);
        Assert.True(s.Loads[0].TargetNotes <= 3000);
    }
}
