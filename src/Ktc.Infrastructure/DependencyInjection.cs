using Microsoft.Extensions.Options;
using Ktc.Application.Abstractions;
using Ktc.Application.Models;
using Ktc.Application.Options;
using Ktc.Domain.Entities;
using Ktc.Infrastructure.Data;
using Ktc.Infrastructure.Jobs;
using Ktc.Infrastructure.KtcApi;
using Ktc.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ktc.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddKtcInfrastructure(this IServiceCollection s, IConfiguration cfg)
    {
        s.Configure<PortalOptions>(cfg.GetSection(PortalOptions.Section));
        s.Configure<NotificationOptions>(cfg.GetSection(NotificationOptions.Section));
        // One KtcOptions instance for the whole app. The HTTP pipeline (mock or live handler) is built from it at startup, so the
        // token provider, dashboard and jobs must read the very same values: before, they could disagree (pipeline live while
        // IOptions said UseMock=true), and the background jobs then sent the literal "mock-token" to the real KTC server.
        var ktc = cfg.GetSection(KtcOptions.Section).Get<KtcOptions>() ?? new KtcOptions();
        s.AddSingleton<IOptions<KtcOptions>>(Options.Create(ktc));

        var provider = cfg["Database:Provider"] ?? "SqlServer";
        s.AddDbContext<KtcDbContext>(o =>
        {
            if (provider.Equals("InMemory", StringComparison.OrdinalIgnoreCase)) o.UseInMemoryDatabase("ktc-portal-dev");
            else o.UseSqlServer(cfg.GetConnectionString("KtcPortal"), sql => sql.EnableRetryOnFailure(5));
        });
        s.AddScoped<IUnitOfWork, UnitOfWork>();
        s.AddScoped(typeof(IRepository<>), typeof(EfRepository<>));

        s.AddHttpContextAccessor();
        s.AddMemoryCache();
        s.AddHttpClient("notify").ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(15));
        s.AddKtcHttp(ktc);

        s.AddSingleton<PortalClock>();
        s.AddSingleton<IExportService, ExportService>();
        s.AddScoped<IKtcDataService, KtcDataService>();
        s.AddScoped<IDashboardService, DashboardService>();
        s.AddScoped<IAlertEngine, AlertEngine>();
        s.AddScoped<INotificationSender, NotificationSender>();
        s.AddScoped<IReportService, ReportService>();
        s.AddScoped<IAuditService, AuditService>();
        s.AddScoped<IUserService, UserService>();
        s.AddScoped<IApiRegistryService, ApiRegistryService>();
        s.AddScoped<IApiExecutionService, ApiExecutionService>();
        s.AddSingleton<IRealtimeNotifier, NullRealtimeNotifier>();       // Web layer replaces this with the SignalR implementation

        s.AddHostedService<DashboardRefreshJob>();
        s.AddHostedService<ReportSchedulerJob>();
        s.AddHostedService<ApiMappingSchedulerJob>();
        return s;
    }
}

public class NullRealtimeNotifier : IRealtimeNotifier
{
    public Task DashboardUpdatedAsync(DashboardSnapshot snapshot) => Task.CompletedTask;
    public Task AlertRaisedAsync(AlertHistory alert) => Task.CompletedTask;
    public Task ApiHealthChangedAsync(IReadOnlyList<ApiHealthRow> rows) => Task.CompletedTask;
}
