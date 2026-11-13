using Ktc.Application.Abstractions;
using Ktc.Application.Models;
using Ktc.Application.Options;
using Ktc.Domain.Entities;
using Ktc.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ktc.Infrastructure.Services;

public class AlertEngine : IAlertEngine
{
    private readonly IUnitOfWork _uow; private readonly INotificationSender _notify; private readonly IRealtimeNotifier _rt;
    private readonly IApiHealthTracker _api; private readonly ILogger<AlertEngine> _log; private readonly NotificationOptions _nopt;

    public AlertEngine(IUnitOfWork uow, INotificationSender n, IRealtimeNotifier rt, IApiHealthTracker api, IOptions<NotificationOptions> nopt, ILogger<AlertEngine> log)
    { _uow = uow; _notify = n; _rt = rt; _api = api; _log = log; _nopt = nopt.Value; }

    private record Candidate(string Source, string Title, string Message);

    public async Task<int> EvaluateAsync(DashboardSnapshot s, CancellationToken ct = default)
    {
        var rules = await _uow.Repo<AlertRule>().Query().Where(r => r.Enabled).ToListAsync(ct);
        var now = DateTime.UtcNow; int raised = 0;
        foreach (var rule in rules)
        {
            foreach (var c in Candidates(rule, s, now))
            {
                var since = now.AddMinutes(-rule.CooldownMinutes);
                var dup = await _uow.Repo<AlertHistory>().Query().AnyAsync(h => h.RuleType == rule.RuleType && h.SourceKey == c.Source && h.RaisedUtc > since, ct);
                if (dup) continue;
                await RaiseAsync(rule, c, ct); raised++;
            }
        }
        return raised;
    }

    private IEnumerable<Candidate> Candidates(AlertRule r, DashboardSnapshot s, DateTime now)
    {
        var window = now.AddMinutes(-r.WindowMinutes);
        switch (r.RuleType)
        {
            case AlertRuleType.AtmOffline:
                // Only alert on a known, old last-contact time. No contact time means the data source failed or did not match,
                // not that the ATM is down (that produced a flood of false "ATM offline" alerts).
                foreach (var a in s.Atms.Where(a => a.Offline && a.LastCommunicationUtc.HasValue && (now - a.LastCommunicationUtc.Value).TotalMinutes >= r.Threshold))
                    yield return new(a.Id, $"ATM offline: {a.Name}", $"{a.Name} ({a.Client}, {a.City}) has been offline since {(a.LastCommunicationUtc?.ToString("u") ?? "unknown")}. Open incidents: {a.OpenIncidents}.");
                break;
            case AlertRuleType.HostDisconnect:
                var hd = s.Atms.Where(a => a.HostDisconnected).ToList();
                if (hd.Count >= Math.Max(1, r.Threshold))
                    yield return new("fleet", $"Host disconnect on {hd.Count} ATM(s)", "Affected: " + string.Join(", ", hd.Take(15).Select(a => a.Name)) + (hd.Count > 15 ? $" and {hd.Count - 15} more" : ""));
                break;
            case AlertRuleType.HighTicketVolume:
                var nt = s.Tickets.Count(t => t.CreatedUtc >= window);
                if (nt >= r.Threshold) yield return new("fleet", $"High ticket volume: {nt} tickets in {r.WindowMinutes} min", $"Threshold {r.Threshold}. Open tickets: {s.OpenTickets}.");
                break;
            case AlertRuleType.HighIncidentVolume:
                var ni = s.Incidents.Count(i => i.OpenedUtc >= window);
                if (ni >= r.Threshold) yield return new("fleet", $"High incident volume: {ni} new incidents in {r.WindowMinutes} min", $"Threshold {r.Threshold}. Critical open: {s.CriticalIncidents}.");
                break;
            case AlertRuleType.TransactionFailureSpike:
                if (s.RecentTxCount >= 20 && s.RecentFailureRatePct >= r.Threshold)
                    yield return new("fleet", $"Transaction failure spike: {s.RecentFailureRatePct:0.#}%", $"{s.RecentFailureRatePct:0.#}% of the last {s.RecentTxCount} transactions failed (threshold {r.Threshold}%).");
                break;
            case AlertRuleType.TicketSlaBreachImminent:
                foreach (var t in s.Tickets.Where(t => t.Sla == SlaState.NearBreach && t.IsOpen))
                    yield return new("tk" + t.Id, $"SLA breach within {Math.Max(1, (int)(t.SlaDueUtc - now).TotalMinutes)} min: ticket {t.Id}", $"{t.AtmName} ({t.Client}) - {t.Description}. Due {t.SlaDueUtc:u}.");
                break;
            case AlertRuleType.ApiSuccessRateLow:
                foreach (var a in _api.Snapshot().Where(a => a.Calls >= 5 && a.SuccessRatePct < r.Threshold))
                    yield return new($"{a.Method} {a.Endpoint}", $"KTC API success rate {a.SuccessRatePct}%: {a.Method} {a.Endpoint}", $"{a.Failures} failures in last {a.Calls} calls. Last error: {a.LastError}");
                break;
            case AlertRuleType.ApiResponseSlow:
                foreach (var a in _api.Snapshot().Where(a => a.Calls >= 3 && a.AvgMs > r.Threshold * 1000))
                    yield return new($"{a.Method} {a.Endpoint}", $"KTC API slow ({a.AvgMs / 1000:0.0}s): {a.Method} {a.Endpoint}", $"Average of last calls {a.AvgMs} ms, p95 {a.P95Ms} ms (threshold {r.Threshold}s).");
                break;
            case AlertRuleType.ApiConsecutiveFailures:
                // One grouped alert for the fleet instead of one P1 per route.
                var failing = _api.Snapshot().Where(a => a.ConsecutiveFailures > r.Threshold).OrderByDescending(a => a.ConsecutiveFailures).ToList();
                if (failing.Count > 0)
                    yield return new("api-failing", $"KTC API failing on {failing.Count} route(s)",
                        string.Join("\n", failing.Take(10).Select(a => $"{a.Method} {a.Endpoint}: {a.ConsecutiveFailures} in a row. Last error: {a.LastError}")) + (failing.Count > 10 ? $"\n…and {failing.Count - 10} more" : ""));
                break;
        }
    }

    private async Task RaiseAsync(AlertRule rule, Candidate c, CancellationToken ct)
    {
        var h = new AlertHistory { RuleId = rule.Id, RuleType = rule.RuleType, Severity = rule.Severity, Title = Trunc(c.Title, 300), Message = c.Message, SourceKey = Trunc(c.Source, 200) };
        var channels = rule.Channels & ~AlertChannels.Portal;
        if (channels != AlertChannels.None)
        {
            var rec = rule.Recipients?.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            h.NotificationStatus = await _notify.SendAsync(channels, $"[{rule.Severity}] {c.Title}", c.Message, rec, ct);
        }
        await _uow.Repo<AlertHistory>().AddAsync(h, ct);
        await _uow.SaveChangesAsync(ct);
        _log.LogWarning("ALERT [{Sev}] {Title}", rule.Severity, c.Title);
        await _rt.AlertRaisedAsync(h);
    }

    public async Task RaiseManualAsync(AlertRuleType type, Severity sev, string title, string message, string? key, CancellationToken ct = default)
    {
        var rule = await _uow.Repo<AlertRule>().Query().FirstOrDefaultAsync(r => r.RuleType == type, ct)
                   ?? new AlertRule { Name = type.ToString(), RuleType = type, Severity = sev, Channels = AlertChannels.Portal | AlertChannels.Email | AlertChannels.Teams };
        var channels = rule.Channels & ~AlertChannels.Portal;
        var h = new AlertHistory { RuleId = rule.Id == 0 ? null : rule.Id, RuleType = type, Severity = sev, Title = Trunc(title, 300), Message = message, SourceKey = key };
        if (channels != AlertChannels.None) h.NotificationStatus = await _notify.SendAsync(channels, $"[{sev}] {title}", message, null, ct);
        await _uow.Repo<AlertHistory>().AddAsync(h, ct);
        await _uow.SaveChangesAsync(ct);
        await _rt.AlertRaisedAsync(h);
    }
    private static string Trunc(string s, int n) => s.Length > n ? s[..n] : s;
}
