using Ktc.Application.Abstractions;
using Ktc.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ktc.Web;

/// <summary>Navigation entries auto-generated from the API registry (ApiModules.ShowInNav) - the "Explorer" menu.</summary>
public class ApiNavViewComponent : ViewComponent
{
    private readonly IUnitOfWork _uow;
    public ApiNavViewComponent(IUnitOfWork uow) => _uow = uow;
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var curated = Ktc.Application.Logic.ApiCatalogRules.Known.Values.Select(k => k.Screen).ToHashSet();
        var mods = await _uow.Repo<ApiModule>().Query().Where(m => m.ShowInNav && m.Endpoints.Any(e => e.Enabled && e.Mappings.Any()))
            .OrderBy(m => m.DisplayName).Select(m => new NavItem(m.DisplayName, m.Icon ?? "bi-puzzle", m.ScreenName ?? m.DisplayName)).ToListAsync();
        return View(mods.Where(m => !curated.Contains(m.Screen)).DistinctBy(m => m.Screen).ToList());
    }
}
public record NavItem(string Title, string Icon, string Screen);
