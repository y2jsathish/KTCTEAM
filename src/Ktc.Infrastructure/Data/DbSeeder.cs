using Ktc.Application.Abstractions;
using Ktc.Domain.Entities;
using Ktc.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ktc.Infrastructure.Data;

public static class DbSeeder
{
    public static async Task InitializeAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KtcDbContext>();
        var log = scope.ServiceProvider.GetRequiredService<ILogger<KtcDbContext>>();
        if (db.Database.IsRelational()) await db.Database.MigrateAsync(ct); else await db.Database.EnsureCreatedAsync(ct);

        if (!await db.Roles.AnyAsync(ct))
        {
            db.Roles.AddRange(new Role { Name = "Admin", Description = "Full access incl. API catalog, alert rules, schedules, user roles" },
                              new Role { Name = "Operator", Description = "Monitoring + incident actions + reports" },
                              new Role { Name = "Viewer", Description = "Read-only dashboards" });
            await db.SaveChangesAsync(ct);
        }
        if (!await db.AlertRules.AnyAsync(ct))
        {
            var all = AlertChannels.Portal | AlertChannels.Email | AlertChannels.Teams;
            db.AlertRules.AddRange(
                new AlertRule { Name = "ATM offline > 30 min", RuleType = AlertRuleType.AtmOffline, Threshold = 30, Severity = Severity.P2, Channels = all, CooldownMinutes = 120 },
                new AlertRule { Name = "Host disconnect (3+ ATMs)", RuleType = AlertRuleType.HostDisconnect, Threshold = 3, Severity = Severity.P1, Channels = all, CooldownMinutes = 30 },
                new AlertRule { Name = "High ticket volume (20 / hour)", RuleType = AlertRuleType.HighTicketVolume, Threshold = 20, WindowMinutes = 60, Severity = Severity.P2, Channels = AlertChannels.Portal | AlertChannels.Teams, CooldownMinutes = 60 },
                new AlertRule { Name = "High incident volume (15 / hour)", RuleType = AlertRuleType.HighIncidentVolume, Threshold = 15, WindowMinutes = 60, Severity = Severity.P2, Channels = AlertChannels.Portal | AlertChannels.Teams, CooldownMinutes = 60 },
                new AlertRule { Name = "Transaction failure spike (>= 15%)", RuleType = AlertRuleType.TransactionFailureSpike, Threshold = 15, WindowMinutes = 30, Severity = Severity.P1, Channels = all, CooldownMinutes = 30 },
                new AlertRule { Name = "Ticket SLA breach within 1 h", RuleType = AlertRuleType.TicketSlaBreachImminent, Threshold = 60, Severity = Severity.P2, Channels = AlertChannels.Portal | AlertChannels.Email, CooldownMinutes = 120 },
                new AlertRule { Name = "KTC API success rate < 95%", RuleType = AlertRuleType.ApiSuccessRateLow, Threshold = 95, Severity = Severity.P2, Channels = AlertChannels.Portal | AlertChannels.Teams, CooldownMinutes = 30 },
                new AlertRule { Name = "KTC API response > 5 s", RuleType = AlertRuleType.ApiResponseSlow, Threshold = 5, Severity = Severity.P3, Channels = AlertChannels.Portal, CooldownMinutes = 30 },
                new AlertRule { Name = "KTC API > 3 consecutive failures", RuleType = AlertRuleType.ApiConsecutiveFailures, Threshold = 3, Severity = Severity.P1, Channels = all, CooldownMinutes = 15 });
            await db.SaveChangesAsync(ct);
        }
        await scope.ServiceProvider.GetRequiredService<IApiRegistryService>().EnsureSeededAsync(ct);
        log.LogInformation("Database initialised");
    }
}
