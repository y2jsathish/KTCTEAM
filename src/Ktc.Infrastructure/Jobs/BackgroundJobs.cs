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
