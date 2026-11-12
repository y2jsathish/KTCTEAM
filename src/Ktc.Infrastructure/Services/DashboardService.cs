using System.Text.Json;
using Ktc.Application.Abstractions;
using Ktc.Application.Logic;
using Ktc.Application.Models;
using Ktc.Application.Options;
using Ktc.Domain.Entities;
using Ktc.Domain.Enums;
using Ktc.Infrastructure.KtcApi;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ktc.Infrastructure.Services;

public class DashboardService : IDashboardService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DashboardSnapshot? _latestShared;

    private readonly IKtcDataService _data; private readonly IUnitOfWork _uow; private readonly IMemoryCache _cache; private readonly IHttpContextAccessor _http;
    private readonly KtcOptions _ktc; private readonly PortalOptions _portal; private readonly PortalClock _clock; private readonly ILogger<DashboardService> _log;

    public DashboardService(IKtcDataService data, IUnitOfWork uow, IMemoryCache cache, IHttpContextAccessor http, IOptions<KtcOptions> ktc, IOptions<PortalOptions> portal, PortalClock clock, ILogger<DashboardService> log)
    { _data = data; _uow = uow; _cache = cache; _http = http; _ktc = ktc.Value; _portal = portal.Value; _clock = clock; _log = log; }

    private bool Shared => _ktc.UseMock || _ktc.ServiceAccount.Configured;
    private string Key => Shared ? "snap:global" : "snap:u:" + (_http.HttpContext?.User.Identity?.Name ?? "anon");
    public DashboardSnapshot? Latest => Shared ? _latestShared : _cache.Get<DashboardSnapshot>(Key);

    public async Task<DashboardSnapshot> GetSnapshotAsync(bool force = false, CancellationToken ct = default)
    {
        var key = Key;
        if (!force && _cache.TryGetValue(key, out DashboardSnapshot? s) && s != null && (DateTime.UtcNow - s.GeneratedUtc).TotalSeconds < _portal.DashboardRefreshSeconds) return s;
        await Gate.WaitAsync(ct);
        try
        {
            if (!force && _cache.TryGetValue(key, out s) && s != null && (DateTime.UtcNow - s.GeneratedUtc).TotalSeconds < _portal.DashboardRefreshSeconds) return s;
            using var scope = Shared ? KtcTokenScope.ForceService() : null;
            var snap = await BuildAsync(ct);
            _cache.Set(key, snap, TimeSpan.FromHours(1));
            if (Shared) _latestShared = snap;
            await PersistAsync(key, snap, ct);
            return snap;
        }
        finally { Gate.Release(); }
    }

    private async Task PersistAsync(string key, DashboardSnapshot s, CancellationToken ct)
    {
        try
        {
            var lite = s with { Atms = new(), Incidents = new(), Tickets = new() };
            var repo = _uow.Repo<DashboardCache>();
            var row = await repo.Query().FirstOrDefaultAsync(x => x.Key == key, ct);
            var json = JsonSerializer.Serialize(lite);
            if (row == null) await repo.AddAsync(new DashboardCache { Key = key, PayloadJson = json, ExpiresUtc = DateTime.UtcNow.AddHours(24) }, ct);
            else { row.PayloadJson = json; row.UpdatedUtc = DateTime.UtcNow; repo.Update(row); }
            await _uow.SaveChangesAsync(ct);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not persist dashboard cache"); }
    }

    private async Task<DashboardSnapshot> BuildAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow; var today = _clock.TodayStartUtc();
        var tAtm = _data.GetAtmsAsync(ct);
        var tInc = _data.GetIncidentsAsync(true, ct);
        var tTk = _data.SearchTicketsAsync(new TicketQuery(), ct);
        var tTx = _data.SearchTransactionsAsync(new TransactionQuery(From: today, To: now), ct);
        var tHist = HistoricalLast24hAsync(ct);
        await Task.WhenAll(tAtm, tInc, tTk, tTx, tHist);

        var (atms, warnings) = tAtm.Result; var incidents = tInc.Result.ToList(); var tickets = tTk.Result.ToList(); var tx = tTx.Result;
        var atmList = atms.ToList();
        var openTickets = tickets.Where(t => t.IsOpen).ToList();
        var known = atmList.Where(a => a.AvailabilityPct.HasValue).ToList();
        double avail = known.Count >= Math.Max(1, atmList.Count / 2) ? Math.Round(known.Average(a => a.AvailabilityPct!.Value), 2)
                       : atmList.Count == 0 ? 0 : Math.Round(100.0 * atmList.Count(a => !a.Offline) / atmList.Count, 2);

        // hourly buckets in portal-local time
        string H(DateTime utc) => _clock.ToLocal(utc).ToString("HH:00");
        var hours = Enumerable.Range(0, 24).Select(i => _clock.ToLocal(now).Date.AddHours(i)).Where(h => h <= _clock.ToLocal(now)).Select(h => h.ToString("HH:00")).ToList();
        var vol = tx.GroupBy(t => H(t.TimeUtc)).ToDictionary(g => g.Key, g => g.Count());
        var fail = tx.Where(t => !t.Success).GroupBy(t => H(t.TimeUtc)).ToDictionary(g => g.Key, g => g.Count());

        var last24 = Enumerable.Range(0, 24).Select(i => _clock.ToLocal(now).AddHours(-23 + i)).Select(h => h.ToString("HH:00")).ToList();
        var incAll = tHist.Result.Concat(incidents).DistinctBy(i => i.Key).ToList();
        var incTrend = incAll.Where(i => i.OpenedUtc >= now.AddHours(-24)).GroupBy(i => H(i.OpenedUtc)).ToDictionary(g => g.Key, g => g.Count());

        var recent = tx.Where(t => t.TimeUtc >= now.AddMinutes(-30)).ToList();
        var clients = atmList.GroupBy(a => string.IsNullOrEmpty(a.Client) ? "Unassigned" : a.Client).Select(g =>
        {
            var tks = tickets.Where(t => (string.IsNullOrEmpty(t.Client) ? "Unassigned" : t.Client) == g.Key).ToList();
            var kn = g.Where(a => a.AvailabilityPct.HasValue).ToList();
            return new ClientSummary(g.Key, g.Count(), g.Count(a => !a.Offline),
                kn.Count > 0 ? Math.Round(kn.Average(a => a.AvailabilityPct!.Value), 2) : Math.Round(100.0 * g.Count(a => !a.Offline) / g.Count(), 2),
                g.Sum(a => a.OpenIncidents), g.Sum(a => a.OpenTickets), SlaCalculator.AchievedPercent(tks.Select(t => t.Sla)), g.Count(a => a.Health == HealthStatus.Critical));
        }).OrderBy(c => c.Name).ToList();

        int highAlerts = 0;
        try { var since = now.AddHours(-24); highAlerts = await _uow.Repo<AlertHistory>().Query().CountAsync(a => a.RaisedUtc >= since && a.AcknowledgedUtc == null && a.Severity <= Severity.P2, ct); }
        catch (Exception ex) { _log.LogWarning(ex, "Alert count failed"); }

        return new DashboardSnapshot
        {
            GeneratedUtc = now, TotalAtms = atmList.Count, OfflineAtms = atmList.Count(a => a.Offline), OnlineAtms = atmList.Count(a => !a.Offline),
            WarningAtms = atmList.Count(a => a.Health == HealthStatus.Warning), CriticalAtms = atmList.Count(a => a.Health == HealthStatus.Critical),
            TotalTickets = tickets.Count, OpenTickets = openTickets.Count, CriticalIncidents = incidents.Count(i => i.Severity is Severity.P1 or Severity.P2),
            TransactionsToday = tx.Count, FailedTransactions = tx.Count(t => !t.Success), HighPriorityAlerts = highAlerts,
            RecentTxCount = recent.Count, RecentFailureRatePct = recent.Count == 0 ? 0 : Math.Round(100.0 * recent.Count(t => !t.Success) / recent.Count, 1),
            AvailabilityPct = avail, SlaPct = SlaCalculator.AchievedPercent(tickets.Select(t => t.Sla)),
            TicketsByStatus = Enum.GetValues<TicketStatus>().ToDictionary(s => s.ToString(), s => tickets.Count(t => t.Status == s)),
            TicketsBySeverity = Enum.GetValues<Severity>().ToDictionary(s => s.ToString(), s => openTickets.Count(t => t.Severity == s)),
            IncidentsBySeverity = Enum.GetValues<Severity>().ToDictionary(s => s.ToString(), s => incidents.Count(i => i.Severity == s)),
            AtmHealth = new() { ["Healthy"] = atmList.Count(a => a.Health == HealthStatus.Healthy), ["Warning"] = atmList.Count(a => a.Health == HealthStatus.Warning), ["Critical"] = atmList.Count(a => a.Health == HealthStatus.Critical) },
            IncidentTrend = last24.Select(h => new ChartPoint(h, incTrend.GetValueOrDefault(h))).ToList(),
            TransactionVolume = hours.Select(h => new ChartPoint(h, vol.GetValueOrDefault(h))).ToList(),
            TransactionFailures = hours.Select(h => new ChartPoint(h, fail.GetValueOrDefault(h))).ToList(),
            FailureByCategory = tx.Where(t => !t.Success).GroupBy(t => t.FailureCategory.Length == 0 ? FailureClassifier.Other : t.FailureCategory).ToDictionary(g => g.Key, g => g.Count()),
            Clients = clients, Atms = atmList, Incidents = incidents, Tickets = tickets, Warnings = warnings
        };
    }

    private Task<IReadOnlyList<IncidentDto>> HistoricalLast24hAsync(CancellationToken ct) =>
        _cache.GetOrCreateAsync("hist24:" + (Shared ? "g" : _http.HttpContext?.User.Identity?.Name), e =>
        { e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5); return _data.GetHistoricalIncidentsAsync(DateTime.UtcNow.AddHours(-24), DateTime.UtcNow, null, ct); })!;

    // ---------------- grids ----------------
    public async Task<IReadOnlyList<AtmDto>> GetAtmsAsync(AtmFilter f, CancellationToken ct = default)
    {
        var s = await GetSnapshotAsync(false, ct);
        IEnumerable<AtmDto> r = s.Atms;
        if (!string.IsNullOrWhiteSpace(f.Client)) r = r.Where(a => a.Client.Equals(f.Client, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(f.City)) r = r.Where(a => a.City.Equals(f.City, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(f.Region)) r = r.Where(a => a.Region.Equals(f.Region, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(f.Status) && Enum.TryParse<HealthStatus>(f.Status, true, out var hs)) r = r.Where(a => a.Health == hs);
        return r.ToList();
    }

    // ---------------- ATM 360 ----------------
    public async Task<AtmDetail?> GetAtmDetailAsync(string atmId, CancellationToken ct = default)
    {
        var s = await GetSnapshotAsync(false, ct);
        var atm = s.Atms.FirstOrDefault(a => a.Id == atmId) ?? s.Atms.FirstOrDefault(a => a.Name.Equals(atmId, StringComparison.OrdinalIgnoreCase));
        if (atm == null) return null;
        using var scope = Shared ? KtcTokenScope.ForceService() : null;
        var now = DateTime.UtcNow;
        var tHist = _data.GetHistoricalIncidentsAsync(now.AddDays(-7), now, atm.Id, ct);
        var tTk = _data.SearchTicketsAsync(new TicketQuery(AtmId: atm.Id), ct);
        var tTx = _data.SearchTransactionsAsync(new TransactionQuery(AtmId: atm.Id, From: now.AddHours(-24), To: now), ct);
        var tEj = _data.SearchEjAsync(new EjQuery(atm.Id, now.AddHours(-6), now), ct);
        var tAv = _data.GetAvailabilityAsync(atm.Id, now.AddDays(-30), now, ct);
        var warnings = new List<string>();
        async Task<T> Safe<T>(Task<T> t, T fallback, string what) { try { return await t; } catch (Exception ex) { warnings.Add($"{what}: {ex.Message}"); return fallback; } }
        var hist = await Safe(tHist, Array.Empty<IncidentDto>(), "incident history");
        var tk = await Safe(tTk, Array.Empty<TicketDto>(), "tickets");
        var tx = await Safe(tTx, Array.Empty<TransactionDto>(), "transactions");
        var ej = await Safe(tEj, Array.Empty<EjEntryDto>(), "EJ");
        var av = await Safe(tAv, new AtmAvailability(double.NaN, Array.Empty<(string, double)>()), "availability");
        var open = s.Incidents.Where(i => i.AtmId == atm.Id).ToList();
        var inc = open.Concat(hist.Where(h => open.All(o => o.Key != h.Key))).OrderByDescending(i => i.OpenedUtc).ToList();
        return new AtmDetail(atm, inc, tk.Take(50).ToList(), tx.Take(100).ToList(), ej.TakeLast(300).ToList(), av, warnings);
    }

    // ---------------- Root cause ----------------
    public async Task<RcaResult> GetRootCauseAsync(int days, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 90);
        return await _cache.GetOrCreateAsync($"rca:{(Shared ? "g" : _http.HttpContext?.User.Identity?.Name)}:{days}", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
            using var scope = Shared ? KtcTokenScope.ForceService() : null;
            var inc = await _data.GetHistoricalIncidentsAsync(DateTime.UtcNow.AddDays(-days), DateTime.UtcNow, null, ct);
            var total = Math.Max(1, inc.Count);
            var key = (IncidentDto i) => (i.AtmId, Code: i.Description.Length > 0 ? i.Description : i.ErrorCode);
            var rec = inc.GroupBy(key).Where(g => g.Count() >= 3).OrderByDescending(g => g.Count()).Take(50)
                .Select(g => new RecurringFailure(g.Key.AtmId, g.First().AtmName, g.First().Client, g.First().ErrorCode, g.Key.Code, g.Count(), g.Max(x => x.OpenedUtc), Math.Round(100.0 * g.Count() / total, 1))).ToList();
            var codes = inc.GroupBy(i => i.Description.Length > 0 ? i.Description : i.ErrorCode).OrderByDescending(g => g.Count()).Take(10).Select(g => new ChartPoint(g.Key, g.Count())).ToList();
            var topAtm = inc.GroupBy(i => string.IsNullOrEmpty(i.AtmName) ? i.AtmId : i.AtmName).OrderByDescending(g => g.Count()).Take(10).Select(g => new ChartPoint(g.Key, g.Count())).ToList();
            return new RcaResult(rec, codes, topAtm, days);
        })!;
    }

    // ---------------- Global search ----------------
    public async Task<IReadOnlyList<GlobalSearchResult>> GlobalSearchAsync(string term, CancellationToken ct = default)
    {
        term = (term ?? "").Trim();
        if (term.Length < 2) return Array.Empty<GlobalSearchResult>();
        var s = await GetSnapshotAsync(false, ct);
        bool M(string? v) => v != null && v.Contains(term, StringComparison.OrdinalIgnoreCase);
        var res = new List<GlobalSearchResult>();
        res.AddRange(s.Atms.Where(a => M(a.Id) || M(a.Name) || M(a.Location) || M(a.City)).Take(8)
            .Select(a => new GlobalSearchResult("ATM", a.Id, a.Name, $"{a.Client} · {a.City} · {a.DeviceStatus}", $"/Atm/Detail/{Uri.EscapeDataString(a.Id)}")));
        res.AddRange(s.Tickets.Where(t => M(t.Id) || M(t.AtmName) || M(t.Description)).Take(8)
            .Select(t => new GlobalSearchResult("Ticket", t.Id, $"Ticket {t.Id}", $"{t.AtmName} · {t.Status} · {t.Severity}", $"/Tickets?ticketId={Uri.EscapeDataString(t.Id)}")));
        res.AddRange(s.Incidents.Where(i => M(i.Number) || M(i.Key) || M(i.AtmName) || M(i.Description)).Take(8)
            .Select(i => new GlobalSearchResult("Incident", i.Key, i.Number, $"{i.AtmName} · {i.Severity} · {i.Description}", $"/Incidents?q={Uri.EscapeDataString(i.Number)}")));
        try
        {
            var tx = await _data.SearchTransactionsAsync(new TransactionQuery(From: _clock.TodayStartUtc(), To: DateTime.UtcNow), ct);
            res.AddRange(tx.Where(t => M(t.Id)).Take(8)
                .Select(t => new GlobalSearchResult("Transaction", t.Id, $"Txn {t.Id}", $"{t.AtmName} · {t.Type} · {t.Status}", $"/Transactions?id={Uri.EscapeDataString(t.Id)}")));
        }
        catch (Exception ex) { _log.LogWarning(ex, "Transaction search failed in global search"); }
        return res;
    }
}
