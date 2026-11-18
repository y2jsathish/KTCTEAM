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
        Assert.Equal(2500, l2.TargetNotes);                         // 500 x 7 x 1.2 = 4200, capped at capacity
        Assert.Equal(900 * 100 + 2500 * 1000m, s.TargetAmount);
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
}
