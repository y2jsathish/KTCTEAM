using System.Text.Json;
using System.Text.RegularExpressions;
using Ktc.Application.Abstractions;
using Ktc.Application.Logic;
using Ktc.Application.Models;
using Ktc.Application.Options;
using Ktc.Domain.Entities;
using Ktc.Infrastructure.KtcApi;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ktc.Infrastructure.Services;

/// <summary>
/// Cash Forecasting (phase 1). The estate run reads every ATM's cash units and the transactions once, stores the daily snapshot
/// (notes per cash unit) and the daily cash history (actual withdrawals / deposits per ATM and day), then estimates run-out dates
/// and replenishments and stores the result. The ATM page is computed live and adds notes per denomination since the reset and
/// the reconciliation XFS / EJ / application counters.
/// </summary>
public class CashForecastService : ICashForecastService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Regex DispensedCounter = new(@"^(notes\s+)?dispensed$", RegexOptions.IgnoreCase);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IKtcDataService _data; private readonly IDashboardService _dash; private readonly IUnitOfWork _uow; private readonly IHttpContextAccessor _http;
    private readonly KtcOptions _ktc; private readonly CashForecastOptions _o; private readonly PortalClock _clock; private readonly ILogger<CashForecastService> _log;

    public CashForecastService(IKtcDataService data, IDashboardService dash, IUnitOfWork uow, IHttpContextAccessor http, IOptions<KtcOptions> ktc,
        IOptions<CashForecastOptions> o, PortalClock clock, ILogger<CashForecastService> log)
    { _data = data; _dash = dash; _uow = uow; _http = http; _ktc = ktc.Value; _o = o.Value; _clock = clock; _log = log; }

    private bool Shared => _ktc.UseMock || _ktc.ServiceAccount.Configured;
    private string Key => Shared ? "cash:forecast" : "cash:forecast:u:" + (_http.HttpContext?.User.Identity?.Name ?? "anon");

    public async Task<EstateCashForecast> GetEstateAsync(double? lowPct = null, CancellationToken ct = default)
    {
        var key = Key; EstateCashForecast? f = null;
        var row = await _uow.Repo<DashboardCache>().Query().AsNoTracking().FirstOrDefaultAsync(x => x.Key == key, ct);
        if (row != null)
        {
            try { f = JsonSerializer.Deserialize<EstateCashForecast>(row.PayloadJson, Json); }
            catch (Exception ex) { _log.LogWarning(ex, "Stored cash forecast could not be read; recalculating"); }
        }
        return WithThreshold(f ?? await RunAsync("first view", ct), lowPct);
    }

    /// <summary>The estate forecast for a low-cash threshold of <paramref name="lowPct"/> percent of cassette capacity (null = CashForecast:LowNotes).</summary>
    private EstateCashForecast WithThreshold(EstateCashForecast f, double? lowPct)
    {
        var (o, pct) = Options(lowPct);
        if (lowPct == null || o.LowNotes == (f.LowNotes ?? _o.LowNotes)) return f with { LowPct = pct, LowNotes = o.LowNotes };
        var local = _clock.ToLocal(f.GeneratedUtc);
        var atms = f.Atms.Select(a => CashForecastRules.Rethreshold(a, o, local)).OrderBy(a => a.DaysLeft ?? double.MaxValue).ThenBy(a => a.AtmName).ToList();
        return f with { Atms = atms, LowPct = pct, LowNotes = o.LowNotes };
    }

    /// <summary>Options with the low-cash threshold for <paramref name="lowPct"/> (null = configured LowNotes) and that threshold as a percent.</summary>
    private (CashForecastOptions Options, double Pct) Options(double? lowPct) =>
        lowPct is { } p && double.IsFinite(p) ? (_o.WithLowNotes(_o.LowNotesFor(p)), Math.Round(Math.Clamp(p, 0, 100), 2)) : (_o, _o.DefaultLowPct);

    public async Task<EstateCashForecast> RunAsync(string trigger, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            using var scope = Shared ? KtcTokenScope.ForceService() : null;
            var nowUtc = DateTime.UtcNow; var nowLocal = _clock.ToLocal(nowUtc); var today = nowLocal.Date;
            var warnings = new List<string>();
            var atms = (await _dash.GetAtmsAsync(new AtmFilter(), ct)).ToList();
            if (atms.Count == 0) warnings.Add("KTC returned no ATMs.");

            // ---- actual cash per ATM and day (transactions of the lookback, from local midnight) -> CashDailyUsage ----
            var lookback = Math.Clamp(_ktc.CounterLookbackDays, 1, 31);
            var fromDay = today.AddDays(-lookback);
            IReadOnlyList<TransactionDto> tx = Array.Empty<TransactionDto>();
            try { tx = await _data.SearchTransactionsAsync(new TransactionQuery(From: _clock.LocalDateToUtc(fromDay), To: nowUtc), ct); }
            catch (Exception ex) { warnings.Add("Transactions could not be read: " + ex.Message); }
            await StoreDailyAsync(atms, tx, fromDay, today, nowUtc, ct);

            // ---- cash units now -> CashSnapshot (one row per ATM, unit and day; the latest run of the day wins) ----
            var gate = new SemaphoreSlim(Math.Clamp(_o.MaxParallel, 1, 10));
            var reads = await Task.WhenAll(atms.Select(async a =>
            {
                await gate.WaitAsync(ct);
                try { var (u, w) = await _data.GetCashUnitsAsync(a.Id, ct); return (Atm: a, Units: u, Warnings: w); }
                catch (Exception ex) { return (Atm: a, Units: (IReadOnlyList<CashUnitLevel>?)null, Warnings: (IReadOnlyList<string>)new[] { ex.Message }); }
                finally { gate.Release(); }
            }));
            await StoreSnapshotsAsync(reads.Where(r => r.Units != null).Select(r => (r.Atm.Id, r.Atm.Name, r.Units!)), today, nowUtc, ct);
            var noUnits = reads.Count(r => r.Units == null);
            if (noUnits > 0) warnings.Add($"KTC did not return the cash units of {noUnits} ATM(s); they show no forecast.");

            // ---- history and forecast ----
            var since = today.AddDays(-Math.Max(60, _o.MinDaysForWeekdayPattern * 2));
            var snaps = await _uow.Repo<CashSnapshot>().Query().AsNoTracking().Where(s => s.Day >= since).ToListAsync(ct);
            var usage = await _uow.Repo<CashDailyUsage>().Query().AsNoTracking().Where(s => s.Day >= since).ToListAsync(ct);
            var snapBy = snaps.GroupBy(s => s.AtmId).ToDictionary(g => g.Key, g => g.Select(s => new CashSnapshotPoint(s.Day, s.Unit, s.Denomination, s.Notes)).ToList());
            var useBy = usage.GroupBy(u => u.AtmId).ToDictionary(g => g.Key, g => g.Select(ToPoint).OrderBy(p => p.Day).ToList());
            var lastRep = tx.Where(t => CounterRules.IsResetType(t.Type)).GroupBy(t => t.AtmId).ToDictionary(g => g.Key, g => g.Max(t => t.TimeUtc));

            var items = reads.Select(r =>
            {
                var daily = useBy.GetValueOrDefault(r.Atm.Id) ?? new();
                var f = CashForecastRules.Build(new CashForecastInput(r.Atm.Id, r.Atm.Name, r.Atm.Client, nowUtc, nowLocal, r.Units ?? Array.Empty<CashUnitLevel>(),
                    lastRep.TryGetValue(r.Atm.Id, out var lr) ? lr : null, snapBy.GetValueOrDefault(r.Atm.Id) ?? new(), null, null, WithdrawnPerDay(daily), daily), _o);
                return r.Units == null ? f with { Warnings = r.Warnings.Concat(f.Warnings.Skip(1)).ToList() } : f;
            }).OrderBy(f => f.DaysLeft ?? double.MaxValue).ThenBy(f => f.AtmName).ToList();

            var historyDays = snaps.Select(s => s.Day).Distinct().Count();
            if (historyDays < _o.MinHistoryDays + 1)
                warnings.Add($"Daily snapshots: {historyDays} day(s) stored so far. Until there are {_o.MinHistoryDays + 1}, usage comes from the withdrawal amounts (a rough guess); estimates improve as history builds up.");
            var result = new EstateCashForecast(nowUtc, trigger, historyDays, items, EstateDaily(usage, today.AddDays(-30)), warnings, _o.DefaultLowPct, _o.LowNotes);
            await StoreAsync(result, ct);
            await CleanupAsync(today, ct);
            _log.LogInformation("Cash forecast ({Trigger}): {Atms} ATMs, {Critical} critical, {Warning} warning, {Days} snapshot day(s)", trigger, items.Count,
                items.Count(i => i.Status is CashForecastRules.StatusOut or CashForecastRules.StatusCritical), items.Count(i => i.Status == CashForecastRules.StatusWarning), historyDays);
            return result;
        }
        finally { Gate.Release(); }
    }

    public async Task<AtmCashForecastDetail?> GetAtmAsync(string atmId, double? lowPct = null, CancellationToken ct = default)
    {
        var (opt, pct) = Options(lowPct);
        using var scope = Shared ? KtcTokenScope.ForceService() : null;
        var tRep = _data.GetReplenishmentAsync(atmId, ct);
        var tCnt = _data.GetCountersAsync(atmId, ct);
        var tEj = _data.GetEjCountersAsync(atmId, Math.Clamp(_o.EjDays, 1, 31), ct);
        var diag = new List<string>(); var warnings = new List<string>();
        async Task<T?> Safe<T>(Task<T?> t, string what) where T : class { try { return await t; } catch (Exception ex) { warnings.Add($"{what}: {ex.Message}"); return null; } }
        var rep = await Safe(tRep, "Cash units"); var cnt = await Safe(tCnt, "Counters"); var ej = await Safe(tEj, "EJ counters");
        if (rep == null && cnt == null) return null;

        var id = rep?.AtmId ?? cnt!.AtmId; var name = rep?.AtmName ?? cnt!.AtmName;
        var nowUtc = DateTime.UtcNow; var nowLocal = _clock.ToLocal(nowUtc); var today = nowLocal.Date;
        var units = rep?.CashUnits ?? Array.Empty<CashUnitLevel>();
        string client = "";
        try { client = (await _dash.GetAtmsAsync(new AtmFilter(), ct)).FirstOrDefault(a => a.Id == id)?.Client ?? ""; } catch { /* name only */ }
        if (rep != null && units.Count > 0) await StoreSnapshotsAsync(new[] { (id, name, (IReadOnlyList<CashUnitLevel>)units) }, today, nowUtc, ct);

        var since = today.AddDays(-90);
        var snaps = await _uow.Repo<CashSnapshot>().Query().AsNoTracking().Where(s => s.AtmId == id && s.Day >= since).OrderBy(s => s.Day).ToListAsync(ct);
        var history = snaps.Select(s => new CashSnapshotPoint(s.Day, s.Unit, s.Denomination, s.Notes)).ToList();
        var daily = (await _uow.Repo<CashDailyUsage>().Query().AsNoTracking().Where(s => s.AtmId == id && s.Day >= since).OrderBy(s => s.Day).ToListAsync(ct)).Select(ToPoint).ToList();
        if (daily.Count == 0)
        {
            try
            {
                var lookback = Math.Clamp(_ktc.CounterLookbackDays, 1, 31);
                var tx = await _data.SearchTransactionsAsync(new TransactionQuery(AtmId: id, From: _clock.LocalDateToUtc(today.AddDays(-lookback)), To: nowUtc), ct);
                daily = CashForecastRules.Daily(tx.Where(t => t.AtmId == id), _clock.ToLocal, today);
                diag.Add($"Daily cash: no stored history for this ATM yet, so the last {lookback} days of transactions were used ({daily.Count} day(s) with cash).");
            }
            catch (Exception ex) { warnings.Add("Transactions: " + ex.Message); }
        }
        else diag.Add($"Daily cash: {daily.Count} stored day(s) since {daily[0].Day:yyyy-MM-dd}.");

        var f = CashForecastRules.Build(new CashForecastInput(id, name, client, nowUtc, nowLocal, units, rep?.LastReplenishmentUtc, history,
            cnt?.NotesByDenomination, cnt?.LastResetUtc, WithdrawnPerDay(daily), daily, rep?.Loads), opt);
        f = f with { Warnings = warnings.Concat(rep?.Warnings ?? Array.Empty<string>()).Concat(f.Warnings).Distinct().ToList() };

        // ---- reconciliation ----
        var report = ej?.Reports.OrderByDescending(r => r.ReportTime ?? r.EjTimeUtc).FirstOrDefault();
        DateTime? reportUtc = report == null ? null : report.ReportTime is { } rt ? _clock.LocalDateToUtc(rt) : report.EjTimeUtc;
        DateTime? clearedUtc = report?.LastCleared is { } lc ? _clock.LocalDateToUtc(lc) : null;
        var xfsDispensed = cnt?.Xfs.Where(x => x.ServiceClass == "CDM" && DispensedCounter.IsMatch(x.Counter)).Select(x => (long?)x.Value).FirstOrDefault();
        var recon = CashForecastRules.Reconcile(units, cnt?.NotesByDenomination, cnt?.LastResetUtc, report, reportUtc, clearedUtc, xfsDispensed,
            TimeSpan.FromMinutes(Math.Max(1, _o.ReconcileToleranceMinutes)));

        diag.Add($"Cash units (CurrentCashUnitStatus): {units.Count}, {units.Count(CashForecastRules.IsDispensing)} dispensing.");
        diag.Add(cnt?.NotesByDenomination is { Count: > 0 } nb ? $"Notes by denomination since the reset ({cnt.ResetSource ?? "reset time unknown"}): {nb.Count} value(s)." : "Notes by denomination: none read (application counters).");
        diag.Add(ej == null ? "EJ counter reports: not read." : $"EJ counter reports: {ej.Reports.Count} in the last {_o.EjDays} day(s){(report != null ? $", latest {report.ReportTimeText ?? report.EjTimeUtc.ToString("u")}" : "")}.");
        diag.Add($"Daily snapshots: {history.Select(h => h.Day).Distinct().Count()} day(s) stored for this ATM.");
        return new AtmCashForecastDetail(f, recon, daily, history, diag, pct, opt.LowNotes);
    }

    public async Task<IReadOnlyList<CashDailyPoint>> GetDailyAsync(int days, CancellationToken ct = default)
    {
        var from = _clock.ToLocal(DateTime.UtcNow).Date.AddDays(-Math.Clamp(days, 1, 400));
        var rows = await _uow.Repo<CashDailyUsage>().Query().AsNoTracking().Where(u => u.Day >= from).ToListAsync(ct);
        return EstateDaily(rows, from);
    }

    // ---------------- helpers ----------------
    private static CashDailyPoint ToPoint(CashDailyUsage u) => new(u.Day, u.Withdrawals, u.AmountWithdrawn, u.Deposits, u.AmountDeposited, u.Partial, u.ChequeDeposits, u.AmountChequeDeposited);

    private static List<CashDailyPoint> EstateDaily(IEnumerable<CashDailyUsage> rows, DateTime from) =>
        rows.Where(u => u.Day >= from).GroupBy(u => u.Day).OrderBy(g => g.Key)
            .Select(g => new CashDailyPoint(g.Key, g.Sum(x => x.Withdrawals), g.Sum(x => x.AmountWithdrawn), g.Sum(x => x.Deposits), g.Sum(x => x.AmountDeposited), g.Any(x => x.Partial),
                g.Sum(x => x.ChequeDeposits), g.Sum(x => x.AmountChequeDeposited))).ToList();

    /// <summary>Average amount withdrawn per full day over the last 28 stored days (null when none).</summary>
    private static decimal? WithdrawnPerDay(IReadOnlyList<CashDailyPoint> daily)
    {
        var full = daily.Where(d => !d.Partial).OrderByDescending(d => d.Day).Take(28).ToList();
        return full.Count == 0 ? null : full.Average(d => d.Withdrawn);
    }

    private async Task StoreDailyAsync(IReadOnlyList<AtmDto> atms, IReadOnlyList<TransactionDto> tx, DateTime fromDay, DateTime today, DateTime nowUtc, CancellationToken ct)
    {
        // Local DB writes are short and must finish even if the caller's request is aborted; a cancelled token would drop the history mid-way.
        ct = CancellationToken.None;
        try
        {
            var byAtm = atms.ToDictionary(a => a.Id);
            var repo = _uow.Repo<CashDailyUsage>();
            var existing = (await repo.Query().Where(u => u.Day >= fromDay).ToListAsync(ct)).ToDictionary(u => (u.AtmId, u.Day));
            foreach (var g in tx.Where(t => t.AtmId.Length > 0).GroupBy(t => t.AtmId))
            {
                var atm = byAtm.GetValueOrDefault(g.Key);
                foreach (var d in CashForecastRules.Daily(g, _clock.ToLocal, today).Where(d => d.Day >= fromDay))
                {
                    if (!existing.TryGetValue((g.Key, d.Day), out var row))
                    {
                        row = new CashDailyUsage { AtmId = g.Key, Day = d.Day };
                        await repo.AddAsync(row, ct); existing[(g.Key, d.Day)] = row;
                    }
                    else repo.Update(row);
                    row.AtmName = atm?.Name ?? g.First().AtmName; row.Client = atm?.Client ?? g.First().Client; row.CapturedUtc = nowUtc;
                    row.Withdrawals = d.Withdrawals; row.AmountWithdrawn = d.Withdrawn; row.Deposits = d.Deposits; row.AmountDeposited = d.Deposited; row.Partial = d.Partial;
                    row.ChequeDeposits = d.ChequeDeposits; row.AmountChequeDeposited = d.ChequeDeposited;
                }
            }
            await _uow.SaveChangesAsync(ct);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not store the daily cash history"); }
    }

    private async Task StoreSnapshotsAsync(IEnumerable<(string AtmId, string AtmName, IReadOnlyList<CashUnitLevel> Units)> atms, DateTime today, DateTime nowUtc, CancellationToken ct)
    {
        // Local DB writes are short and must finish even if the caller's request is aborted; a cancelled token would drop the history mid-way.
        ct = CancellationToken.None;
        try
        {
            var list = atms.ToList(); var ids = list.Select(a => a.AtmId).ToList();
            var repo = _uow.Repo<CashSnapshot>();
            var existing = (await repo.Query().Where(s => s.Day == today && ids.Contains(s.AtmId)).ToListAsync(ct)).ToDictionary(s => (s.AtmId, s.Unit));
            foreach (var (atmId, atmName, units) in list)
                foreach (var u in units.Where(CashForecastRules.IsDispensing).GroupBy(u => u.Unit).Select(g => g.First()))
                {
                    if (!existing.TryGetValue((atmId, u.Unit), out var row)) { row = new CashSnapshot { AtmId = atmId, Day = today, Unit = u.Unit }; await repo.AddAsync(row, ct); existing[(atmId, u.Unit)] = row; }
                    else repo.Update(row);
                    row.AtmName = atmName; row.UnitType = u.Type; row.Denomination = u.Value; row.Notes = u.Count; row.CapturedUtc = nowUtc;
                }
            await _uow.SaveChangesAsync(ct);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not store the cash snapshot"); }
    }

    private async Task StoreAsync(EstateCashForecast f, CancellationToken ct)
    {
        // Local DB writes are short and must finish even if the caller's request is aborted; a cancelled token would drop the history mid-way.
        ct = CancellationToken.None;
        try
        {
            var key = Key; var repo = _uow.Repo<DashboardCache>();
            var row = await repo.Query().FirstOrDefaultAsync(x => x.Key == key, ct);
            var json = JsonSerializer.Serialize(f, Json);
            if (row == null) await repo.AddAsync(new DashboardCache { Key = key, PayloadJson = json, UpdatedUtc = f.GeneratedUtc }, ct);
            else { row.PayloadJson = json; row.UpdatedUtc = f.GeneratedUtc; repo.Update(row); }
            await _uow.SaveChangesAsync(ct);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not store the cash forecast"); }
    }

    private async Task CleanupAsync(DateTime today, CancellationToken ct)
    {
        // Local DB writes are short and must finish even if the caller's request is aborted; a cancelled token would drop the history mid-way.
        ct = CancellationToken.None;
        try
        {
            var cut = today.AddDays(-Math.Max(30, _o.RetentionDays));
            var s = _uow.Repo<CashSnapshot>(); var u = _uow.Repo<CashDailyUsage>();
            var old = await s.Query().Where(x => x.Day < cut).Take(5000).ToListAsync(ct);
            var oldU = await u.Query().Where(x => x.Day < cut).Take(5000).ToListAsync(ct);
            if (old.Count + oldU.Count == 0) return;
            old.ForEach(s.Remove); oldU.ForEach(u.Remove);
            await _uow.SaveChangesAsync(ct);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not delete old cash history"); }
    }
}
