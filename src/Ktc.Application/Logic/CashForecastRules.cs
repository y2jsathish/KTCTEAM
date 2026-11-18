using System.Text.RegularExpressions;
using Ktc.Application.Models;
using Ktc.Application.Options;

namespace Ktc.Application.Logic;

/// <summary>
/// Cash forecasting (phase 1): usage per cassette, run-out estimate, suggested replenishment and reconciliation between the
/// XFS cash units, the EJ counter report and the application (per-transaction) counters. Pure functions; every result except the
/// counts is an estimate.
/// </summary>
public static class CashForecastRules
{
    public const string SourceHistory = "Daily snapshots", SourceCounters = "Notes since last reset", SourceAmounts = "Withdrawal amounts", SourceNone = "No usage data";
    public const string StatusOut = "Out", StatusCritical = "Critical", StatusWarning = "Warning", StatusOk = "OK", StatusUnknown = "No usage data";
    public const string Match = "Match", Differs = "Differs", NotComparable = "Not comparable";
    /// <summary>Run-out estimates are capped here; beyond it the page says "more than N days".</summary>
    public const double MaxDays = 90;

    private static readonly Regex NotDispensing = new(@"reject|retract|cash\s*-?\s*in\b|deposit|escrow", RegexOptions.IgnoreCase);
    private static readonly Regex Recycling = new(@"recycl", RegexOptions.IgnoreCase);

    /// <summary>A cash unit customers are paid from: it has a note value and is not a reject / retract / cash-in unit.</summary>
    public static bool IsDispensing(CashUnitLevel u) => u.Value is > 0 && !NotDispensing.IsMatch(u.Type) && !NotDispensing.IsMatch(u.Unit);

    /// <summary>
    /// Notes per day from the daily snapshots of one unit: the drops between consecutive snapshots, over the days they span.
    /// Intervals where the count went up (replenishment or recycled deposits) are left out, usage and days alike.
    /// Null when fewer than <paramref name="minIntervals"/> usable intervals.
    /// </summary>
    public static (double? Rate, double Days) HistoryRate(IEnumerable<CashSnapshotPoint> points, int minIntervals)
    {
        var p = points.OrderBy(x => x.Day).ToList();
        double drop = 0, days = 0; var n = 0;
        for (int i = 1; i < p.Count; i++)
        {
            var gap = (p[i].Day - p[i - 1].Day).TotalDays;
            if (gap <= 0 || p[i].Notes > p[i - 1].Notes) continue;
            drop += p[i - 1].Notes - p[i].Notes; days += gap; n++;
        }
        return n >= Math.Max(1, minIntervals) && days > 0 ? (drop / days, days) : (null, days);
    }

    /// <summary>Notes per day since the reset; recycling units count dispensed minus accepted (deposits refill them). Null without a reset time.</summary>
    public static (double? Rate, double Days) CounterRate(long dispensed, long accepted, bool recycling, DateTime? resetUtc, DateTime nowUtc)
    {
        if (resetUtc is not { } r || r > nowUtc) return (null, 0);
        var days = Math.Max(0.25, (nowUtc - r).TotalDays);
        var net = recycling ? Math.Max(0, dispensed - accepted) : dispensed;
        return (net / days, days);
    }

    /// <summary>
    /// Weekday factors (index = DayOfWeek) from full days of actual withdrawals: average of the weekday / average of all days.
    /// Null below <paramref name="minDays"/> full days. Weekdays without a sample get 1.
    /// </summary>
    public static double[]? WeekdayFactors(IEnumerable<CashDailyPoint> daily, int minDays)
    {
        var full = daily.Where(d => !d.Partial).ToList();
        if (full.Count < Math.Max(7, minDays)) return null;
        var avg = (double)full.Average(d => d.Withdrawn);
        if (avg <= 0) return null;
        var f = new double[7];
        for (int i = 0; i < 7; i++)
        {
            var w = full.Where(d => (int)d.Day.DayOfWeek == i).ToList();
            f[i] = w.Count == 0 ? 1 : Math.Clamp((double)w.Average(d => d.Withdrawn) / avg, 0.3, 3);
        }
        return f;
    }

    /// <summary>
    /// Days until <paramref name="notes"/> drops to <paramref name="lowNotes"/> at <paramref name="ratePerDay"/> (scaled per weekday when
    /// factors are given), from <paramref name="nowLocal"/>. 0 when already at or below; null without a usage rate; capped at <see cref="MaxDays"/>.
    /// </summary>
    public static double? DaysLeft(long notes, long lowNotes, double? ratePerDay, double[]? factors, DateTime nowLocal)
    {
        var usable = (double)(notes - lowNotes);
        if (usable <= 0) return 0;
        if (ratePerDay is not { } rate || rate <= 0) return null;
        if (factors == null) return Math.Min(MaxDays, usable / rate);
        double t = 0; var day = nowLocal;
        var frac = 1 - day.TimeOfDay.TotalHours / 24;           // rest of today
        while (t < MaxDays)
        {
            var use = rate * factors[(int)day.DayOfWeek] * frac;
            if (use >= usable) return t + frac * (usable / use);
            usable -= use; t += frac; day = day.Date.AddDays(1); frac = 1;
        }
        return MaxDays;
    }

    public static string StatusFor(long notes, long lowNotes, double? daysLeft) =>
        notes <= lowNotes ? StatusOut : daysLeft is not { } d ? StatusUnknown : d <= 1 ? StatusCritical : d <= 3 ? StatusWarning : StatusOk;

    private static int Rank(string status) => status switch { StatusOut => 0, StatusCritical => 1, StatusWarning => 2, StatusOk => 3, _ => 4 };
    private static int ConfidenceRank(string c) => c switch { "Medium" => 0, "Low" => 1, "Very low" => 2, _ => 3 };

    /// <summary>Forecast of one ATM: usage per dispensing cassette (best source first), run-out, status and replenishment suggestion.</summary>
    public static AtmCashForecast Build(CashForecastInput i, CashForecastOptions o)
    {
        var warnings = new List<string>();
        var factors = WeekdayFactors(i.Daily, o.MinDaysForWeekdayPattern);
        var disp = i.Units.Where(IsDispensing).ToList();
        if (i.Units.Count == 0) warnings.Add("KTC returned no cash units for this ATM, so there is nothing to forecast.");
        else if (disp.Count == 0) warnings.Add("None of the cash units has a note value, so there is nothing to forecast.");

        var units = new List<CashUnitForecast>();
        foreach (var u in disp)
        {
            double? rate = null; double rateDays = 0; var source = SourceNone; var conf = "None";
            // 1. own daily snapshots
            var (hr, hd) = HistoryRate(i.History.Where(h => h.Unit == u.Unit && h.Denomination == u.Value), o.MinHistoryDays);
            if (hr.HasValue) { rate = hr; rateDays = hd; source = SourceHistory; conf = hd >= 28 ? "Medium" : "Low"; }
            // 2. notes per denomination since the reset, split over the units holding that note value
            if (rate == null && i.NotesSinceReset != null)
            {
                var nb = i.NotesSinceReset.Where(n => n.Value == u.Value).ToList();
                if (nb.Count > 0)
                {
                    var same = disp.Where(x => x.Value == u.Value).ToList();
                    var share = same.Count == 1 ? 1.0 : same.Sum(x => x.Count) is var tot and > 0 ? (double)u.Count / tot : 1.0 / same.Count;
                    var (cr, cd) = CounterRate(nb.Sum(n => n.NotesDispensed), nb.Sum(n => n.NotesAccepted), Recycling.IsMatch(u.Type), i.ResetUtc, i.NowUtc);
                    if (cr.HasValue) { rate = cr * share; rateDays = cd; source = SourceCounters; conf = "Low"; }
                }
            }
            // 3. the ATM's average amount withdrawn per day, split by the cash each unit holds (rough)
            if (rate == null && i.WithdrawnPerDay is > 0 && u.Value is > 0)
            {
                var total = disp.Sum(x => x.Amount ?? 0);
                if (total > 0) { rate = (double)(i.WithdrawnPerDay.Value * ((u.Amount ?? 0) / total) / u.Value.Value); source = SourceAmounts; conf = "Very low"; }
            }
            var left = DaysLeft(u.Count, o.LowNotes, rate, factors, i.NowLocal);
            units.Add(new CashUnitForecast(u.Device, u.Unit, u.Type, u.Value, u.Count, u.Amount, rate is { } r0 ? Math.Round(r0, 1) : null, source,
                Math.Round(rateDays, 1), conf, left is { } l0 ? Math.Round(l0, 1) : null, left is { } l1 ? i.NowUtc.AddDays(l1) : null, StatusFor(u.Count, o.LowNotes, left)));
        }
        if (units.Count > 0 && units.All(u => u.NotesPerDay == null))
            warnings.Add("No usage known yet (no snapshots, counters or withdrawals), so no run-out date can be estimated.");
        if (units.Any(u => u.RateSource == SourceAmounts))
            warnings.Add("Some cassettes use the ATM's withdrawal amounts split by the cash each holds: a rough guess until notes per denomination or daily snapshots are available.");

        var known = units.Where(u => u.DaysLeft.HasValue).ToList();
        var first = known.OrderBy(u => u.DaysLeft).FirstOrDefault();
        var status = units.Count == 0 ? StatusUnknown : units.OrderBy(u => Rank(u.Status)).First().Status;
        var rated = units.Where(u => u.NotesPerDay.HasValue).ToList();
        var confidence = rated.Count == 0 ? "None" : rated.OrderByDescending(u => ConfidenceRank(u.Confidence)).First().Confidence;
        return new AtmCashForecast(i.AtmId, i.AtmName, i.Client, i.NowUtc, i.LastReplenishmentUtc,
            disp.Sum(u => u.Amount ?? 0), disp.Sum(u => u.Count), units, first?.RunOutUtc, first?.DaysLeft, status, confidence, factors != null,
            Suggest(units, i.NowUtc, o), warnings);
    }

    /// <summary>
    /// Suggested replenishment when a cassette runs out within HorizonDays: visit BufferDays before the earliest run-out (not before now),
    /// and load each cassette for CoverDays of use plus SafetyStockPct, rounded up to 100 notes and capped at CassetteCapacity.
    /// Cassettes without a usage estimate are filled to capacity.
    /// </summary>
    public static CashReplenishmentSuggestion? Suggest(IReadOnlyList<CashUnitForecast> units, DateTime nowUtc, CashForecastOptions o)
    {
        var first = units.Where(u => u.DaysLeft.HasValue).OrderBy(u => u.DaysLeft).FirstOrDefault();
        if (first == null || first.DaysLeft > o.HorizonDays) return null;
        var visit = Math.Max(0, first.DaysLeft!.Value - o.BufferDays);
        var loads = units.Select(u =>
        {
            var known = u.NotesPerDay.HasValue;
            var expected = known ? Math.Max(0, (long)Math.Round(u.Notes - u.NotesPerDay!.Value * visit)) : u.Notes;
            var need = known ? (long)Math.Ceiling(u.NotesPerDay!.Value * o.CoverDays * (1 + o.SafetyStockPct / 100)) : o.CassetteCapacity;
            var target = Math.Min(o.CassetteCapacity, Math.Max(o.LowNotes * 2, (long)Math.Ceiling(need / 100.0) * 100));
            return new CashLoadLine(u.Unit, u.Denomination, u.Notes, expected, target, Math.Max(0, target - expected), known);
        }).ToList();
        var reason = first.Notes <= o.LowNotes
            ? $"Cassette {first.Unit}{Den(first.Denomination)} is at or below {o.LowNotes} notes now."
            : $"Cassette {first.Unit}{Den(first.Denomination)} is estimated to run out in about {first.DaysLeft:0.#} day(s).";
        return new CashReplenishmentSuggestion(nowUtc.AddDays(visit), reason, loads,
            loads.Sum(l => l.TargetNotes * (l.Denomination ?? 0)), loads.Sum(l => l.AddNotes * (l.Denomination ?? 0)));
    }

    private static string Den(decimal? d) => d is { } v ? $" ({v:0.##})" : "";

    /// <summary>
    /// Compares, per note value: notes in the cash units now (XFS) with the notes in the cassettes on the latest EJ counter report,
    /// when the report is within <paramref name="tolerance"/> of the cash unit time; and notes dispensed since the reset by the
    /// transactions (application counters) with the EJ report's dispensed notes, when the reset and the report's LAST CLEARED are
    /// within the tolerance. Anything else is "Not comparable" with the reason.
    /// </summary>
    public static CashReconciliation Reconcile(IReadOnlyList<CashUnitLevel> units, IReadOnlyList<NotesByDenomination>? app, DateTime? appResetUtc,
        EjCounterReport? ej, DateTime? ejReportUtc, DateTime? ejLastClearedUtc, long? xfsDispensedTotal, TimeSpan tolerance)
    {
        var disp = units.Where(IsDispensing).ToList();
        var xfsTime = disp.Select(u => u.UpdatedUtc).Where(t => t.HasValue).DefaultIfEmpty().Max();
        var appVals = app?.Where(a => a.Value is > 0).ToList() ?? new();
        var ejTypes = ej?.Types.Where(t => t.Denomination is > 0).ToList() ?? new();
        static bool Close(DateTime? a, DateTime? b, TimeSpan tol) => a.HasValue && b.HasValue && (a.Value - b.Value).Duration() <= tol;
        var notesComparable = ej != null && Close(ejReportUtc, xfsTime, tolerance);
        var dispComparable = ej != null && app != null && Close(appResetUtc, ejLastClearedUtc, tolerance);

        var notes = new List<string>();
        if (ej == null) notes.Add("No EJ counter report in the EJ read (the ATM prints one when it restarts), so only the XFS and application figures are shown.");
        else
        {
            if (!notesComparable) notes.Add(ejReportUtc == null || xfsTime == null
                ? "Notes in cassettes: the EJ report or the cash unit time is missing, so they are not compared."
                : $"Notes in cassettes: the EJ report ({ejReportUtc:yyyy-MM-dd HH:mm} UTC) and the cash units ({xfsTime:yyyy-MM-dd HH:mm} UTC) are more than {tolerance.TotalMinutes:0} minutes apart, so they are not compared.");
            if (app == null) notes.Add("Notes dispensed: the application counters (notes per transaction) were not read, so they are not compared.");
            else if (!dispComparable) notes.Add(appResetUtc == null || ejLastClearedUtc == null
                ? "Notes dispensed: the reset time or the EJ LAST CLEARED time is missing, so they are not compared."
                : $"Notes dispensed: the application counters count from {appResetUtc:yyyy-MM-dd HH:mm} UTC and the EJ report from {ejLastClearedUtc:yyyy-MM-dd HH:mm} UTC (LAST CLEARED), more than {tolerance.TotalMinutes:0} minutes apart, so they are not compared.");
        }

        var denoms = disp.Select(u => u.Value!.Value).Concat(appVals.Select(a => a.Value!.Value)).Concat(ejTypes.Select(t => t.Denomination!.Value)).Distinct().OrderBy(d => d).ToList();
        var rows = new List<CashReconRow>();
        foreach (var d in denoms)
        {
            var xu = disp.Where(u => u.Value == d).ToList(); var et = ejTypes.Where(t => t.Denomination == d).ToList(); var ap = appVals.Where(a => a.Value == d).ToList();
            long? xfs = xu.Count > 0 ? xu.Sum(u => u.Count) : null;
            long? ejN = et.Count > 0 && et.Any(t => t.Cassette.HasValue) ? et.Sum(t => t.Cassette ?? 0) : null;
            long? apD = ap.Count > 0 ? ap.Sum(a => a.NotesDispensed) : null;
            long? ejD = et.Count > 0 && et.Any(t => t.Dispensed.HasValue) ? et.Sum(t => t.Dispensed ?? 0) : null;
            var checks = new List<(bool Same, string Text)>();
            if (notesComparable && xfs.HasValue && ejN.HasValue) checks.Add((xfs == ejN, xfs == ejN ? $"notes in cassette {xfs:N0} on both" : $"notes in cassette XFS {xfs:N0} vs EJ {ejN:N0} ({Math.Abs(xfs.Value - ejN.Value):N0} apart)"));
            if (dispComparable && apD.HasValue && ejD.HasValue) checks.Add((apD == ejD, apD == ejD ? $"dispensed {apD:N0} on both" : $"dispensed application {apD:N0} vs EJ {ejD:N0} ({Math.Abs(apD.Value - ejD.Value):N0} apart)"));
            var status = checks.Count == 0 ? NotComparable : checks.All(c => c.Same) ? Match : Differs;
            var detail = checks.Count == 0
                ? (xfs.HasValue ? "" : "no XFS cash unit with this value; ") + (ejN.HasValue || ejD.HasValue ? "" : "not on the EJ report; ") + (apD.HasValue ? "" : "no application count; ") + "nothing to compare at the same time"
                : string.Join("; ", checks.Select(c => c.Text));
            rows.Add(new CashReconRow(d, xfs, ejN, apD, ejD, status, detail.TrimEnd(' ', ';')));
        }
        var overall = rows.Any(r => r.Status == Differs) ? Differs : rows.Any(r => r.Status == Match) ? Match : NotComparable;
        return new CashReconciliation(appResetUtc, ejReportUtc, ejLastClearedUtc, xfsTime, xfsDispensedTotal,
            app?.Sum(a => a.NotesDispensed), ej?.NotesDispensed, rows, overall, notes);
    }

    /// <summary>Actual cash per portal-local day from successful customer withdrawals / deposits (CounterRules decides what counts).</summary>
    public static List<CashDailyPoint> Daily(IEnumerable<TransactionDto> tx, Func<DateTime, DateTime> toLocal, DateTime todayLocal)
    {
        return tx.Where(t => t.Success && !CounterRules.IsSupervisorType(t.Type))
            .Select(t => (Day: toLocal(t.TimeUtc).Date, Names: CounterRules.CountersFor(t).ToList(), t.Amount))
            .Where(x => x.Names.Contains("Cash withdrawals") || x.Names.Contains("Cash deposits"))
            .GroupBy(x => x.Day).OrderBy(g => g.Key)
            .Select(g => new CashDailyPoint(g.Key,
                g.Count(x => x.Names.Contains("Cash withdrawals")), g.Where(x => x.Names.Contains("Cash withdrawals")).Sum(x => x.Amount ?? 0),
                g.Count(x => x.Names.Contains("Cash deposits")), g.Where(x => x.Names.Contains("Cash deposits")).Sum(x => x.Amount ?? 0),
                g.Key >= todayLocal)).ToList();
    }
}
