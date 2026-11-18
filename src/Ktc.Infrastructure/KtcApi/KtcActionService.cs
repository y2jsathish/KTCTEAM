using Ktc.Application.Abstractions;
using Ktc.Application.Logic;
using Ktc.Application.Models;
using Ktc.Application.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ktc.Infrastructure.KtcApi;

/// <summary>
/// ATM Actions tab, step 1. Reads KTC's command types with the signed-in user's token and maps the planned actions to them.
/// Read-only on purpose: there is no method here that sends a command to an ATM.
/// </summary>
public class KtcActionService : IKtcActionService
{
    private readonly IKtcApiClient _api; private readonly KtcActionsOptions _opt; private readonly KtcOptions _ktc;
    private readonly IMemoryCache _cache; private readonly IHttpContextAccessor _http; private readonly ILogger<KtcActionService> _log;

    public KtcActionService(IKtcApiClient api, IOptions<KtcActionsOptions> opt, IOptions<KtcOptions> ktc, IMemoryCache cache, IHttpContextAccessor http, ILogger<KtcActionService> log)
    { _api = api; _opt = opt.Value; _ktc = ktc.Value; _cache = cache; _http = http; _log = log; }

    // Per user: KTC may show each account only the commands it is allowed to run.
    private string Key => "ktc-actions:commandtypes:" + (_http.HttpContext?.User.Identity?.Name ?? "svc");

    public async Task<AtmActionCatalog> GetCatalogAsync(bool refresh, CancellationToken ct = default)
    {
        if (!refresh && _cache.TryGetValue(Key, out AtmActionCatalog? hit) && hit != null) return hit;
        var route = string.IsNullOrWhiteSpace(_opt.CommandTypesEndpoint) ? "api/v1/CommandType" : _opt.CommandTypesEndpoint.Trim().TrimStart('/');
        var warnings = new List<string>();
        var r = await _api.SendAsync(HttpMethod.Get, route, null, new KtcCallOptions(), ct);
        List<KtcCommandTypeDto> commands = new(); List<string> fields = new();
        if (!r.Success) warnings.Add($"GET {route}: {r.Error}");
        else
        {
            commands = CommandTypeRules.Parse(r.Json);
            fields = CommandTypeRules.Fields(r.Json);
            if (commands.Count == 0) warnings.Add(r.Json == null ? $"GET {route} answered, but not with JSON." : $"GET {route} answered with no command types.");
        }
        var planned = CommandTypeRules.Map(commands, _opt.Commands, r.Success && commands.Count > 0);
        // Shape only, never values: lets the log show what the real server returned.
        _log.LogInformation("KTC command types: {Route} -> HTTP {Status}, {Count} commands, fields [{Fields}], {Dual} need dual authorization",
            route, r.StatusCode, commands.Count, string.Join(", ", fields), commands.Count(c => c.RequiresDualAuth));
        var cat = new AtmActionCatalog(route, DateTime.UtcNow, _ktc.UseMock, r.Success, commands, planned, fields, warnings);
        if (r.Success) _cache.Set(Key, cat, TimeSpan.FromMinutes(Math.Max(1, _opt.CacheMinutes)));
        return cat;
    }
}
