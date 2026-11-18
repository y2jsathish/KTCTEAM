using Ktc.Application.Abstractions;
using Ktc.Application.Options;
using Ktc.Infrastructure.KtcApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ktc.Infrastructure.Jobs;

/// <summary>Refreshes the shared dashboard snapshot, evaluates alerts, broadcasts over SignalR. Runs only when a service account (or mock mode) provides credentials.</summary>
public class DashboardRefreshJob : BackgroundService
{
    private readonly IServiceScopeFactory _sf; private readonly PortalOptions _portal; private readonly KtcOptions _ktc; private readonly ILogger<DashboardRefreshJob> _log;
    public DashboardRefreshJob(IServiceScopeFactory sf, IOptions<PortalOptions> p, IOptions<KtcOptions> k, ILogger<DashboardRefreshJob> log) { _sf = sf; _portal = p.Value; _ktc = k.Value; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!_portal.EnableBackgroundJobs) return;
        if (!_ktc.UseMock && !_ktc.ServiceAccount.Configured) { _log.LogWarning("Ktc:ServiceAccount not configured - background dashboard refresh and alerting are disabled (dashboards refresh per user)."); return; }
        await Task.Delay(TimeSpan.FromSeconds(3), stop);
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(15, _portal.DashboardRefreshSeconds)));
        do
        {
            try
            {
                using var scope = _sf.CreateScope();
                var dash = scope.ServiceProvider.GetRequiredService<IDashboardService>();
                var snap = await dash.GetSnapshotAsync(true, stop);
                var rt = scope.ServiceProvider.GetRequiredService<IRealtimeNotifier>();
                await rt.DashboardUpdatedAsync(snap with { Atms = new(), Incidents = new(), Tickets = new() });
                await scope.ServiceProvider.GetRequiredService<IAlertEngine>().EvaluateAsync(snap, stop);
                await rt.ApiHealthChangedAsync(scope.ServiceProvider.GetRequiredService<IApiHealthTracker>().Snapshot());
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.LogError(ex, "Dashboard refresh cycle failed"); }
        } while (await timer.WaitForNextTickAsync(stop));
    }
}

public class ReportSchedulerJob : BackgroundService
{
    private readonly IServiceScopeFactory _sf; private readonly PortalOptions _portal; private readonly ILogger<ReportSchedulerJob> _log;
    public ReportSchedulerJob(IServiceScopeFactory sf, IOptions<PortalOptions> p, ILogger<ReportSchedulerJob> log) { _sf = sf; _portal = p.Value; _log = log; }
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!_portal.EnableBackgroundJobs) return;
        await Task.Delay(TimeSpan.FromSeconds(20), stop);
        var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try { using var scope = _sf.CreateScope(); var n = await scope.ServiceProvider.GetRequiredService<IReportService>().RunDueSchedulesAsync(stop); if (n > 0) _log.LogInformation("Ran {Count} scheduled report(s)", n); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.LogError(ex, "Report scheduler cycle failed"); }
        } while (await timer.WaitForNextTickAsync(stop));
    }
}

public class ApiMappingSchedulerJob : BackgroundService
{
    private readonly IServiceScopeFactory _sf; private readonly PortalOptions _portal; private readonly KtcOptions _ktc; private readonly ILogger<ApiMappingSchedulerJob> _log;
    public ApiMappingSchedulerJob(IServiceScopeFactory sf, IOptions<PortalOptions> p, IOptions<KtcOptions> k, ILogger<ApiMappingSchedulerJob> log) { _sf = sf; _portal = p.Value; _ktc = k.Value; _log = log; }
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!_portal.EnableBackgroundJobs || (!_ktc.UseMock && !_ktc.ServiceAccount.Configured)) return;
        await Task.Delay(TimeSpan.FromSeconds(30), stop);
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try { using var scope = _sf.CreateScope(); await scope.ServiceProvider.GetRequiredService<IApiExecutionService>().RunScheduledMappingsAsync(stop); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.LogError(ex, "API mapping scheduler cycle failed"); }
        } while (await timer.WaitForNextTickAsync(stop));
    }
}

/// <summary>
/// Daily cash snapshot for the Cash Forecasting menu: once a day after CashForecast:SnapshotHour (portal time), and at start-up when
/// today has no run yet, stores notes per cash unit and actual cash per day, and recalculates the estate forecast.
/// </summary>
public class CashSnapshotJob : BackgroundService
{
    private readonly IServiceScopeFactory _sf; private readonly PortalOptions _portal; private readonly KtcOptions _ktc; private readonly CashForecastOptions _o;
    private readonly Services.PortalClock _clock; private readonly ILogger<CashSnapshotJob> _log;
    public CashSnapshotJob(IServiceScopeFactory sf, IOptions<PortalOptions> p, IOptions<KtcOptions> k, IOptions<CashForecastOptions> o, Services.PortalClock clock, ILogger<CashSnapshotJob> log)
    { _sf = sf; _portal = p.Value; _ktc = k.Value; _o = o.Value; _clock = clock; _log = log; }

    /// <summary>
    /// True when a run is due: at start-up when today has no run yet; otherwise once the snapshot hour has passed and the last run was before it,
    /// so the end-of-day counts are the ones kept.
    /// </summary>
    public static bool Due(DateTime? lastRunLocal, DateTime nowLocal, int snapshotHour, bool startup)
    {
        if (startup && lastRunLocal?.Date != nowLocal.Date) return true;
        var mark = nowLocal.Date.AddHours(Math.Clamp(snapshotHour, 0, 23));
        return nowLocal >= mark && (lastRunLocal == null || lastRunLocal < mark);
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!_portal.EnableBackgroundJobs || !_o.EnableSnapshotJob) return;
        if (!_ktc.UseMock && !_ktc.ServiceAccount.Configured) { _log.LogWarning("Ktc:ServiceAccount not configured - the daily cash snapshot is disabled (Cash Forecasting can still be recalculated by hand)."); return; }
        await Task.Delay(TimeSpan.FromSeconds(40), stop);
        var startup = true;
        var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        do
        {
            try
            {
                using var scope = _sf.CreateScope();
                var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var last = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(
                    uow.Repo<Ktc.Domain.Entities.DashboardCache>().Query(), x => x.Key == "cash:forecast", stop);
                var nowLocal = _clock.ToLocal(DateTime.UtcNow);
                if (Due(last == null ? null : _clock.ToLocal(last.UpdatedUtc), nowLocal, _o.SnapshotHour, startup))
                    await scope.ServiceProvider.GetRequiredService<ICashForecastService>().RunAsync(startup ? "start-up" : "daily snapshot", stop);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.LogError(ex, "Cash snapshot cycle failed"); }
            startup = false;
        } while (await timer.WaitForNextTickAsync(stop));
    }
}
