using System.Text.Json;
using System.Text.Json.Serialization;
using Ktc.Application.Abstractions;
using Ktc.Application.Logic;
using Ktc.Application.Models;
using Ktc.Application.Options;
using Ktc.Domain.Entities;
using Ktc.Domain.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ktc.Infrastructure.KtcApi;

/// <summary>Guided reboot runs, stored in DashboardCaches (key "reboot:{atm}:{id}") so a restart resumes them.</summary>
public class RebootService : IRebootService
{
    internal static readonly JsonSerializerOptions J = new() { Converters = { new JsonStringEnumConverter() } };
    private const string Prefix = "reboot:";
    private readonly IUnitOfWork _uow; private readonly IKtcActionService _actions; private readonly KtcActionsOptions _opt;
    private readonly IAuditService _audit; private readonly IHttpContextAccessor _http; private readonly ILogger<RebootService> _log;

    public RebootService(IUnitOfWork uow, IKtcActionService actions, IOptions<KtcActionsOptions> opt, IAuditService audit, IHttpContextAccessor http, ILogger<RebootService> log)
    { _uow = uow; _actions = actions; _opt = opt.Value; _audit = audit; _http = http; _log = log; }

    public async Task<(RebootRun? Run, AtmActionResult Result)> StartAsync(RebootRequest q, CancellationToken ct = default)
    {
        var corr = Guid.NewGuid().ToString("N")[..12];
        var checks = new List<ActionCheck>();
        var svc = (KtcActionService)_actions;
        var (atm, ticket, reason) = await svc.CommonChecksAsync(q.AtmId, q.ConfirmAtmId, q.TicketNumber, q.Reason, checks, ct);
        var cat = await _actions.GetCatalogAsync(false, ct);
        foreach (var key in new[] { "OutOfService", "Reboot", "InService" })
        {
            var p = cat.Planned.First(x => x.Key == key);
            var cmd = p.CommandTypeId is int id ? cat.Commands.FirstOrDefault(c => c.Id == id) : null;
            var why = cmd == null ? $"Not mapped (KtcActions:Commands:{key})." : cmd.RequiresDualAuth ? $"{cmd.Name} needs a second login (not supported yet)."
                : CommandParameterRules.Validate(cmd.Parameters, CommandParameterRules.GuidedValues(cmd.Parameters, "Ticket x: reason")).Errors is { Count: > 0 } e ? $"{cmd.Name} has required parameters without defaults: {string.Join(" ", e)}" : null;
            checks.Add(new ActionCheck($"{p.Label} command", why == null, why ?? $"{cmd!.Name} (#{cmd.Id})"));
        }
        if (atm != null)
        {
            var running = await ListAsync(atm.Id, true, ct);
            checks.Add(new ActionCheck("No reboot already running", running.Count == 0, running.Count == 0 ? "OK" : $"Guided reboot {running[0].Id} is already running on this ATM."));
        }
        var ok = checks.All(c => c.Passed);
        checks.Add(new ActionCheck("Sending enabled", _opt.SendEnabled, _opt.SendEnabled ? "OK" : "Sending is off (KtcActions:SendEnabled = false). Nothing was started."));
        RebootRun? run = null;
        if (ok && _opt.SendEnabled)
        {
            run = new RebootRun { Id = corr, AtmId = atm!.Id, AtmName = atm.Name, User = _http.HttpContext?.User.Identity?.Name ?? "?", Ticket = ticket, Reason = reason };
            run.Events.Add(new RebootEvent(DateTime.UtcNow, RebootStep.Starting, "Checks passed: " + string.Join(", ", checks.Select(c => c.Name))));
            await SaveAsync(run, ct);
        }
        var msg = !ok ? "Not started: " + string.Join(" ", checks.Where(c => !c.Passed && c.Name != "Sending enabled").Select(c => c.Detail))
            : !_opt.SendEnabled ? "All checks passed, but sending is off (KtcActions:SendEnabled = false). Nothing was started."
            : $"Guided reboot started on {atm!.Name}.";
        await _audit.LogAsync("AtmActions", run != null ? "Guided reboot started" : "Guided reboot not started", atm?.Id ?? q.AtmId,
            $"ref={corr}; ticket={ticket}; reason={reason}; checks=" + string.Join(", ", checks.Select(c => $"{c.Name}:{(c.Passed ? "ok" : "FAIL")}")), run != null);
        return (run, new AtmActionResult(run != null, msg, checks, "Guided reboot", null, null, null, corr));
    }

    private IQueryable<DashboardCache> Rows => _uow.Repo<DashboardCache>().Query().Where(c => c.Key.StartsWith(Prefix));
    private static RebootRun? Read(DashboardCache c) { try { return JsonSerializer.Deserialize<RebootRun>(c.PayloadJson, J); } catch (JsonException) { return null; } }

    public async Task<IReadOnlyList<RebootRun>> ListAsync(string? atmId, bool runningOnly, CancellationToken ct = default)
    {
        var q = Rows;
        if (!string.IsNullOrWhiteSpace(atmId)) { var p = Prefix + atmId.Trim() + ":"; q = q.Where(c => c.Key.StartsWith(p)); }
        var rows = await q.OrderByDescending(c => c.UpdatedUtc).Take(200).ToListAsync(ct);
        return rows.Select(Read).OfType<RebootRun>().Where(r => !runningOnly || !r.Finished).OrderByDescending(r => r.CreatedUtc).ToList();
    }

    public async Task<RebootRun?> GetAsync(string id, CancellationToken ct = default)
    {
        var suffix = ":" + id;
        var row = await Rows.FirstOrDefaultAsync(c => c.Key.EndsWith(suffix), ct);
        return row == null ? null : Read(row);
    }

    public async Task<RebootRun?> CancelAsync(string id, CancellationToken ct = default)
    {
        var r = await GetAsync(id, ct);
        if (r == null || r.Finished) return r;
        if (r.Step is not (RebootStep.Starting or RebootStep.WaitingForIdle or RebootStep.TakingOutOfService or RebootStep.ReCheckingIdle)) return r;
        r.CancelRequested = true;
        r.Events.Add(new RebootEvent(DateTime.UtcNow, r.Step, $"Cancel requested by {_http.HttpContext?.User.Identity?.Name ?? "?"}."));
        await SaveAsync(r, ct);
        await _audit.LogAsync("AtmActions", "Guided reboot cancel requested", r.AtmId, $"ref={r.Id}; step={r.Step}");
        return r;
    }

    public async Task SaveAsync(RebootRun run, CancellationToken ct = default)
    {
        var key = $"{Prefix}{run.AtmId}:{run.Id}";
        var repo = _uow.Repo<DashboardCache>();
        var row = await repo.Query().FirstOrDefaultAsync(c => c.Key == key, ct);
        var json = JsonSerializer.Serialize(run, J);
        if (row == null) await repo.AddAsync(new DashboardCache { Key = key, PayloadJson = json, UpdatedUtc = run.UpdatedUtc, ExpiresUtc = null }, ct);
        else { row.PayloadJson = json; row.UpdatedUtc = run.UpdatedUtc; repo.Update(row); }
        await _uow.SaveChangesAsync(ct);
    }
}

/// <summary>Live <see cref="IRebootProbe"/>: KTC commands, actions, LastClientContact, transactions and hardware status.</summary>
public class KtcRebootProbe : IRebootProbe
{
    private readonly IKtcActionService _actions; private readonly IKtcApiClient _api; private readonly IKtcDataService _data; private readonly ILogger<KtcRebootProbe> _log;
    public KtcRebootProbe(IKtcActionService actions, IKtcApiClient api, IKtcDataService data, ILogger<KtcRebootProbe> log) { _actions = actions; _api = api; _data = data; _log = log; }

    public Task<(bool Ok, string? ActionId, string? Error)> SendAsync(string atmId, string commandKey, string comment, CancellationToken ct) => _actions.SendMappedAsync(atmId, commandKey, comment, ct);
    public Task<ActionRecord?> GetActionAsync(string actionId, CancellationToken ct) => _actions.GetActionAsync(actionId, ct);

    public async Task<bool?> IsIdleAsync(string atmId, int idleSeconds, CancellationToken ct)
    {
        try
        {
            var now = DateTime.UtcNow;
            var tx = await _data.SearchTransactionsAsync(new TransactionQuery(atmId, null, null, now.AddSeconds(-Math.Max(30, idleSeconds) - 60), now), ct);
            return !tx.Any(t => KtcJson.NormId(t.AtmId) == KtcJson.NormId(atmId) && t.TimeUtc >= now.AddSeconds(-idleSeconds));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Idle check failed for {Atm}", atmId); return null; }
    }

    public async Task<DateTime?> LastContactAsync(string atmId, CancellationToken ct)
    {
        var r = await _api.SendAsync(HttpMethod.Get, "api/v1/LastClientContact", null, new KtcCallOptions(), ct);
        if (!r.Success) return null;
        foreach (var (id, v) in KtcJson.ById(r.Json, "ClientID", "ClientId", "ID", "Id"))
            if (KtcJson.NormId(id) == KtcJson.NormId(atmId))
                return KtcJson.DateValue(v, "LastContact", "LastClientContact", "LastContactTime", "Timestamp", "TimeStamp", "Time", "DateTime");
        return null;
    }

    public async Task<(bool Ok, string Detail)?> DevicesOkAsync(string atmId, CancellationToken ct)
    {
        var hw = await _data.GetHardwareAsync(atmId, ct);
        if (hw == null || hw.Modules.Count == 0) return null;
        var bad = hw.Modules.Where(m => m.Status == "Fatal").Select(m => m.Name).ToList();
        return bad.Count == 0 ? (true, "") : (false, string.Join(", ", bad));
    }
}

/// <summary>Moves running guided reboots on every few seconds (service account), saves them, audits each new step and raises alerts.</summary>
public class RebootWorkflowJob : BackgroundService
{
    private readonly IServiceScopeFactory _sf; private readonly KtcActionsOptions _opt; private readonly KtcOptions _ktc; private readonly ILogger<RebootWorkflowJob> _log;
    public RebootWorkflowJob(IServiceScopeFactory sf, IOptions<KtcActionsOptions> opt, IOptions<KtcOptions> ktc, ILogger<RebootWorkflowJob> log) { _sf = sf; _opt = opt.Value; _ktc = ktc.Value; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(2, _opt.Reboot.TickSeconds)));
        while (await timer.WaitForNextTickAsync(stop))
        {
            try { await TickAsync(stop); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.LogError(ex, "Guided reboot tick failed"); }
        }
    }

    public async Task TickAsync(CancellationToken ct)
    {
        using var scope = _sf.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IRebootService>();
        var runs = await svc.ListAsync(null, true, ct);
        if (runs.Count == 0) return;
        if (!_ktc.UseMock && !_ktc.ServiceAccount.Configured) { _log.LogWarning("{Count} guided reboot(s) waiting: Ktc:ServiceAccount is not configured, so they cannot run.", runs.Count); return; }
        using var _ = KtcTokenScope.ForceService();
        var probe = scope.ServiceProvider.GetRequiredService<IRebootProbe>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();
        foreach (var run in runs)
        {
            var before = run.Events.Count; var wasFinished = run.Finished;
            try { await RebootWorkflow.StepAsync(run, probe, _opt.Reboot, DateTime.UtcNow, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogError(ex, "Guided reboot {Id} step {Step} failed", run.Id, run.Step); continue; }
            if (run.Events.Count == before && run.Finished == wasFinished) continue;
            await svc.SaveAsync(run, ct);
            foreach (var e in run.Events.Skip(before))
                await audit.LogAsync("AtmActions", "Guided reboot: " + e.Step, run.AtmId, $"ref={run.Id}; {e.Message}{(e.ActionId != null ? "; action=" + e.ActionId : "")}", e.Ok, "system (guided reboot for " + run.User + ")");
            if (!wasFinished && run.State is RebootState.Failed or RebootState.NeedsAttention)
            {
                var msg = $"Guided reboot {run.Id} of {run.AtmName} ({run.AtmId}) {(run.State == RebootState.Failed ? "failed" : "needs attention")}: {run.Events.Last().Message}{(run.Ticket.Length > 0 ? $" Ticket {run.Ticket}," : "")} Requested by {run.User}.";
                _log.LogWarning("{Message}", msg);
                try { await scope.ServiceProvider.GetRequiredService<IAlertEngine>().RaiseManualAsync(AlertRuleType.AtmOffline, run.State == RebootState.NeedsAttention ? Severity.P1 : Severity.P2, $"Guided reboot {(run.State == RebootState.Failed ? "failed" : "needs attention")}: {run.AtmName}", msg, "reboot:" + run.Id, ct); }
                catch (Exception ex) { _log.LogError(ex, "Could not raise the alert for guided reboot {Id}", run.Id); }
            }
        }
    }
}
