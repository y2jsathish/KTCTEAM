using Ktc.Application.Abstractions;
using Ktc.Application.Models;
using Ktc.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Ktc.Web.Hubs;

[Authorize]
public class PortalHub : Hub
{
    public Task WatchAtm(string atmId) => Groups.AddToGroupAsync(Context.ConnectionId, "atm-" + atmId);
    public Task UnwatchAtm(string atmId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, "atm-" + atmId);
}

public class SignalRNotifier : IRealtimeNotifier
{
    private readonly IHubContext<PortalHub> _hub;
    public SignalRNotifier(IHubContext<PortalHub> hub) => _hub = hub;
    public Task DashboardUpdatedAsync(DashboardSnapshot s) => _hub.Clients.All.SendAsync("dashboardUpdated", s);
    public Task AlertRaisedAsync(AlertHistory a) => _hub.Clients.All.SendAsync("alertRaised", new { a.Id, Severity = a.Severity.ToString(), a.Title, a.Message, a.RaisedUtc, Type = a.RuleType.ToString() });
    public Task ApiHealthChangedAsync(IReadOnlyList<ApiHealthRow> rows) => _hub.Clients.All.SendAsync("apiHealth", rows);
}
