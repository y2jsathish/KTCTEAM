using Ktc.Application.Abstractions;
using Ktc.Application.Logic;
using Ktc.Application.Models;
using Ktc.Application.Options;
using Ktc.Domain.Entities;
using Ktc.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ktc.Infrastructure.Services;

/// <summary>Imports a Postman collection and auto-discovers modules, navigation, widgets and the API catalog.</summary>
public class ApiRegistryService : IApiRegistryService
{
    private readonly IUnitOfWork _uow; private readonly PortalOptions _opt; private readonly IHostEnvironment _env; private readonly ILogger<ApiRegistryService> _log;
    public ApiRegistryService(IUnitOfWork uow, IOptions<PortalOptions> opt, IHostEnvironment env, ILogger<ApiRegistryService> log) { _uow = uow; _opt = opt.Value; _env = env; _log = log; }

    public async Task EnsureSeededAsync(CancellationToken ct = default)
    {
        if (!_opt.SeedPostmanOnStartup || await _uow.Repo<ApiModule>().Query().AnyAsync(ct)) return;
        var path = Path.Combine(_env.ContentRootPath, _opt.PostmanSeedFile);
        if (!File.Exists(path)) { _log.LogWarning("Postman seed file {Path} not found - API catalog left empty", path); return; }
        await using var fs = File.OpenRead(path);
        var s = await ImportPostmanAsync(fs, ct);
        _log.LogInformation("Seeded API catalog: {Modules} modules, {Endpoints} endpoints, {Enabled} enabled, {Widgets} widgets", s.Modules, s.Endpoints, s.Enabled, s.Widgets);
    }

    public async Task<ImportSummary> ImportPostmanAsync(Stream json, CancellationToken ct = default)
    {
        var parsed = PostmanImporter.Parse(json);
        var modules = await _uow.Repo<ApiModule>().Query().ToDictionaryAsync(m => m.ModuleName, StringComparer.OrdinalIgnoreCase, ct);
        var existing = await _uow.Repo<ApiEndpoint>().Query().Include(e => e.Mappings).ToDictionaryAsync(e => e.HttpMethod + " " + e.RelativeUrl, StringComparer.OrdinalIgnoreCase, ct);
        int added = 0, updated = 0, enabled = 0, mutatingOff = 0, sensitive = 0, widgets = 0, newModules = 0;

        foreach (var grp in parsed.GroupBy(p => p.Module, StringComparer.OrdinalIgnoreCase))
        {
            if (!modules.TryGetValue(grp.Key, out var mod))
            {
                ApiCatalogRules.Known.TryGetValue(grp.Key, out var k);
                mod = new ApiModule
                {
                    ModuleName = grp.Key, DisplayName = ApiCatalogRules.DisplayName(grp.Key), Icon = ApiCatalogRules.ModuleIcon(grp.Key),
                    ScreenName = k?.Screen ?? ApiCatalogRules.DisplayName(grp.Key) + " Explorer", Description = k?.Description ?? $"KTC {grp.Key} API ({grp.Count()} endpoints)",
                    SortOrder = k?.Sort ?? 100, ShowInNav = true
                };
                await _uow.Repo<ApiModule>().AddAsync(mod, ct); modules[grp.Key] = mod; newModules++;
            }
            foreach (var p in grp)
            {
                var key = p.Method + " " + p.RelativeUrl;
                var kind = ApiCatalogRules.Classify(p.Module, p.Method, p.RelativeUrl);
                var sens = ApiCatalogRules.IsSensitive(p.Module, p.RelativeUrl);
                if (existing.TryGetValue(key, out var ep)) { ep.Description ??= p.Description; ep.RequestSchema ??= p.RequestBody; ep.Kind = kind; ep.IsSensitive = sens; if (sens) ep.Enabled = false; updated++; continue; }

                ep = new ApiEndpoint
                {
                    Module = mod, EndpointName = p.Name, HttpMethod = p.Method, RelativeUrl = p.RelativeUrl, Description = p.Description, RequestSchema = p.RequestBody,
                    Kind = kind, IsSensitive = sens, Enabled = ApiCatalogRules.EnabledByDefault(kind, sens)
                };
                if (sens) sensitive++;
                else if (!ep.Enabled && kind is EndpointKind.Update) mutatingOff++;
                if (ep.Enabled) enabled++;

                var cur = ApiCatalogRules.FindCurated(p.Method, p.RelativeUrl);
                if (cur != null && ep.Enabled)
                { ep.Mappings.Add(new ApiMapping { ScreenName = cur.Screen, WidgetType = cur.Widget, RefreshInterval = cur.RefreshSeconds, ExecutionMode = cur.Mode }); widgets++; }
                else if (ep.Enabled && kind == EndpointKind.Monitoring && !p.RelativeUrl.Contains('{') && !p.RelativeUrl.Contains('?'))
                { ep.Mappings.Add(new ApiMapping { ScreenName = mod.ScreenName ?? mod.DisplayName, WidgetType = WidgetType.Table, RefreshInterval = 60, ExecutionMode = ExecutionMode.Manual }); widgets++; }

                await _uow.Repo<ApiEndpoint>().AddAsync(ep, ct); existing[key] = ep; added++;
            }
        }
        await _uow.SaveChangesAsync(ct);
        return new ImportSummary(modules.Count, parsed.Count, added, updated, enabled, mutatingOff, sensitive, widgets);
    }
}
