using System.Text.Json;
using Ktc.Application.Abstractions;
using Ktc.Application.Logic;
using Ktc.Domain.Entities;
using Ktc.Domain.Enums;
using Ktc.Infrastructure.Services;
using Ktc.Web.Infrastructure;
using Ktc.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ktc.Web.Controllers;

public class AlertsController : Controller
{
    private readonly IUnitOfWork _uow; private readonly IAuditService _audit; private readonly INotificationSender _notify;
    public AlertsController(IUnitOfWork u, IAuditService a, INotificationSender n) { _uow = u; _audit = a; _notify = n; }

    public IActionResult Index() => View();

    [Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Rules() => View(await _uow.Repo<AlertRule>().Query().OrderBy(r => r.RuleType).ToListAsync());

    [HttpPost, Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> SaveRule([FromBody] AlertRuleInput? m)
    {
        if (m == null || !ModelState.IsValid) return BadRequest(ModelState);
        var r = await _uow.Repo<AlertRule>().GetAsync(m.Id);
        if (r == null) return NotFound();
        r.Threshold = m.Threshold; r.WindowMinutes = m.WindowMinutes; r.CooldownMinutes = m.CooldownMinutes; r.Severity = m.Severity; r.Channels = m.Channels | AlertChannels.Portal; r.Enabled = m.Enabled;
        r.Recipients = string.IsNullOrWhiteSpace(m.Recipients) ? null : m.Recipients.Trim();
        await _uow.SaveChangesAsync();
        await _audit.LogAsync("Admin", "Alert rule updated", r.Name, JsonSerializer.Serialize(m));
        return Ok();
    }

    [HttpPost, Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> TestNotification()
    {
        var status = await _notify.SendAsync(AlertChannels.Email | AlertChannels.Teams, "[TEST] KTC Portal notification", $"Test notification triggered by {User.Identity!.Name} at {DateTime.UtcNow:u}.", null);
        await _audit.LogAsync("Admin", "Notification test", null, status);
        return Json(new { status });
    }
}

public class ReportsController : Controller
{
    private readonly IReportService _reports; private readonly IUnitOfWork _uow; private readonly IAuditService _audit; private readonly PortalClock _clock;
    public ReportsController(IReportService r, IUnitOfWork u, IAuditService a, PortalClock c) { _reports = r; _uow = u; _audit = a; _clock = c; }

    public async Task<IActionResult> Index() => View(new ReportsViewModel
    {
        Schedules = await _uow.Repo<ReportSchedule>().Query().OrderBy(s => s.Name).ToListAsync(),
        Types = Enum.GetValues<ReportType>().ToDictionary(t => (int)t, t => System.Text.RegularExpressions.Regex.Replace(t.ToString(), "(?<=[a-z])(?=[A-Z])", " "))
    });

    [HttpGet]
    public async Task<IActionResult> Run(ReportType type, DateTime? from, DateTime? to, ExportFormat format = ExportFormat.Pdf, bool preview = false, CancellationToken ct = default)
    {
        var t = (to ?? DateTime.UtcNow).ToUniversalTime(); var f = (from ?? t.AddDays(-1)).ToUniversalTime();
        if (f >= t || t - f > TimeSpan.FromDays(92)) return BadRequest("Invalid report period (max 92 days).");
        var data = await _reports.BuildAsync(type, f, t, ct);
        await _audit.LogAsync("Report", preview ? "Preview" : "Export " + format, type.ToString(), $"{f:u} - {t:u}");
        if (preview) return Json(new { data.Title, data.Subtitle, data.Columns, rows = data.Rows.Take(500), summary = data.Summary.Select(x => new { label = x.Label, value = x.Value }), total = data.Rows.Count });
        var file = await _reports.ExportAsync(data, format);
        return File(file.Data, file.MimeType, file.FileName);
    }

    [HttpPost, Authorize(Policy = Policies.Operator)]
    public async Task<IActionResult> Schedule([FromForm] ScheduleInput m)
    {
        if (!ModelState.IsValid) { TempData["Error"] = "Invalid schedule: " + string.Join("; ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage)); return RedirectToAction(nameof(Index)); }
        var first = m.FirstRunLocal.HasValue ? _clock.LocalDateToUtc(m.FirstRunLocal.Value) : DateTime.UtcNow.AddMinutes(5);
        await _uow.Repo<ReportSchedule>().AddAsync(new ReportSchedule { Name = m.Name.Trim(), ReportType = m.ReportType, Frequency = m.Frequency, Format = m.Format, Recipients = m.Recipients.Trim(), NextRunUtc = first, CreatedBy = User.Identity!.Name });
        await _uow.SaveChangesAsync();
        await _audit.LogAsync("Report", "Schedule created", m.Name);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, Authorize(Policy = Policies.Operator)]
    public async Task<IActionResult> ToggleSchedule(int id) { var s = await _uow.Repo<ReportSchedule>().GetAsync(id); if (s != null) { s.Enabled = !s.Enabled; await _uow.SaveChangesAsync(); } return RedirectToAction(nameof(Index)); }

    [HttpPost, Authorize(Policy = Policies.Operator)]
    public async Task<IActionResult> DeleteSchedule(int id)
    {
        var s = await _uow.Repo<ReportSchedule>().GetAsync(id);
        if (s != null) { _uow.Repo<ReportSchedule>().Remove(s); await _uow.SaveChangesAsync(); await _audit.LogAsync("Report", "Schedule deleted", s.Name); }
        return RedirectToAction(nameof(Index));
    }
}

/// <summary>API registry: catalog, import (auto-discovery), field/widget mapping, generated screens, API health.</summary>
public class ApiAdminController : Controller
{
    private readonly IUnitOfWork _uow; private readonly IApiRegistryService _registry; private readonly IApiExecutionService _exec; private readonly IAuditService _audit;
    public ApiAdminController(IUnitOfWork u, IApiRegistryService r, IApiExecutionService e, IAuditService a) { _uow = u; _registry = r; _exec = e; _audit = a; }

    public IActionResult Catalog() => View();
    public IActionResult Health() => View();
    [Authorize(Policy = Policies.Admin)] public IActionResult Import() => View();

    [HttpGet("/data/api/modules")]
    public async Task<IActionResult> Modules() => Ok(await _uow.Repo<ApiModule>().Query().OrderBy(m => m.SortOrder).ThenBy(m => m.DisplayName)
        .Select(m => new { m.Id, m.ModuleName, m.DisplayName, m.Icon, m.ScreenName, m.ShowInNav, m.Description, endpoints = m.Endpoints.Count, enabled = m.Endpoints.Count(e => e.Enabled) }).ToListAsync());

    [HttpGet("/data/api/endpoints")]
    public async Task<IActionResult> Endpoints(int? moduleId)
    {
        var q = _uow.Repo<ApiEndpoint>().Query().Include(e => e.Module).Include(e => e.Mappings).AsQueryable();
        if (moduleId.HasValue) q = q.Where(e => e.ModuleId == moduleId);
        var rows = await q.OrderBy(e => e.Module!.SortOrder).ThenBy(e => e.RelativeUrl).Take(2000).ToListAsync();
        return Ok(new { data = rows.Select(e => new { e.Id, module = e.Module!.DisplayName, e.EndpointName, e.HttpMethod, e.RelativeUrl, e.Description, kind = e.Kind.ToString(), e.Enabled, e.IsSensitive, hasSchema = e.ResponseSchema != null, e.RequestSchema,
            mappings = e.Mappings.Select(m => new { m.Id, m.ScreenName, widget = m.WidgetType.ToString(), m.RefreshInterval, mode = m.ExecutionMode.ToString() }) }) });
    }

    [HttpPost("/data/api/import"), Authorize(Policy = Policies.Admin), RequestSizeLimit(30_000_000)]
    public async Task<IActionResult> ImportFile(IFormFile file)
    {
        if (file == null || file.Length == 0) return BadRequest(new { error = "Choose a Postman collection (.json) file." });
        try
        {
            await using var s = file.OpenReadStream();
            var summary = await _registry.ImportPostmanAsync(s);
            await _audit.LogAsync("Admin", "Postman import", file.FileName, JsonSerializer.Serialize(summary));
            return Ok(summary);
        }
        catch (JsonException) { return BadRequest(new { error = "The file is not valid JSON / a Postman collection." }); }
    }

    [HttpPost("/data/api/toggle/{id:int}"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Toggle(int id)
    {
        var e = await _uow.Repo<ApiEndpoint>().GetAsync(id);
        if (e == null) return NotFound();
        if (e.IsSensitive) return BadRequest(new { error = "Sensitive endpoints (credentials/keys) cannot be enabled." });
        e.Enabled = !e.Enabled; await _uow.SaveChangesAsync();
        await _audit.LogAsync("Admin", e.Enabled ? "Endpoint enabled" : "Endpoint disabled", e.EndpointName);
        return Ok(new { e.Enabled });
    }

    [HttpPost("/data/api/execute")]
    [Authorize(Policy = Policies.Operator)]
    public async Task<IActionResult> Execute([FromBody] ApiExecuteInput? m, CancellationToken ct)
    {
        if (m == null) return BadRequest(new { error = "Invalid request body." });
        var r = await _exec.ExecuteAsync(m.EndpointId, m.Parameters, m.Body, m.UseCache, User.IsInRole(Roles.Admin), ct);
        await _audit.LogAsync("Admin", "API execute", m.EndpointId.ToString(), $"ok={r.Success} status={r.StatusCode} ms={r.ElapsedMs}", r.Success);
        return Ok(r);
    }

    [HttpGet("/data/api/export/{id:int}"), Authorize(Policy = Policies.Operator)]
    public async Task<IActionResult> Export(int id, ExportFormat format, [FromQuery] Dictionary<string, string>? p, CancellationToken ct)
    {
        try { var f = await _exec.ExportAsync(id, p, format, ct); await _audit.LogAsync("Report", "API export", id.ToString()); return File(f.Data, f.MimeType, f.FileName); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    // ----- mappings -----
    [HttpPost("/data/api/mapping"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> SaveMapping([FromBody] MappingInput? m)
    {
        if (m == null || !ModelState.IsValid) return BadRequest(ModelState);
        var ep = await _uow.Repo<ApiEndpoint>().GetAsync(m.EndpointId);
        if (ep == null) return NotFound();
        await _uow.Repo<ApiMapping>().AddAsync(new ApiMapping { ApiEndpointId = ep.Id, ScreenName = m.ScreenName.Trim(), WidgetType = m.WidgetType, RefreshInterval = m.RefreshInterval, ExecutionMode = m.ExecutionMode });
        await _uow.SaveChangesAsync(); await _audit.LogAsync("Admin", "Mapping created", ep.EndpointName, m.ScreenName);
        return Ok();
    }

    [HttpDelete("/data/api/mapping/{id:int}"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> DeleteMapping(int id)
    {
        var m = await _uow.Repo<ApiMapping>().GetAsync(id); if (m == null) return NotFound();
        _uow.Repo<ApiMapping>().Remove(m); await _uow.SaveChangesAsync(); return Ok();
    }

    [HttpGet("/data/api/mapping/{id:int}/fields")]
    public async Task<IActionResult> Fields(int id) => Ok(await _uow.Repo<ApiFieldMapping>().Query().Where(f => f.ApiMappingId == id).Select(f => new { f.Id, f.FieldPath, f.Label, widget = f.WidgetType.ToString(), f.Aggregation }).ToListAsync());

    [HttpPost("/data/api/fieldmapping"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> SaveField([FromBody] FieldMappingInput? m)
    {
        if (m == null || !ModelState.IsValid) return BadRequest(ModelState);
        await _uow.Repo<ApiFieldMapping>().AddAsync(new ApiFieldMapping { ApiMappingId = m.MappingId, FieldPath = m.FieldPath.Trim(), Label = m.Label.Trim(), WidgetType = m.WidgetType, Aggregation = m.Aggregation });
        await _uow.SaveChangesAsync(); return Ok();
    }

    [HttpDelete("/data/api/fieldmapping/{id:int}"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> DeleteField(int id) { var f = await _uow.Repo<ApiFieldMapping>().GetAsync(id); if (f == null) return NotFound(); _uow.Repo<ApiFieldMapping>().Remove(f); await _uow.SaveChangesAsync(); return Ok(); }

    /// <summary>Auto-generated screen: every ApiMapping with this ScreenName becomes a widget.</summary>
    [HttpGet("ApiAdmin/Screen/{name}")]
    public async Task<IActionResult> Screen(string name)
    {
        var maps = await _uow.Repo<ApiMapping>().Query().Include(m => m.ApiEndpoint).Include(m => m.Fields)
            .Where(m => m.ScreenName == name && m.ApiEndpoint!.Enabled).OrderBy(m => m.Id).ToListAsync();
        ViewBag.Name = name;
        return View(maps);
    }

    [HttpGet("/data/api/screen/{name}")]
    public async Task<IActionResult> ScreenData(string name, int mappingId, CancellationToken ct)
    {
        var m = await _uow.Repo<ApiMapping>().Query().Include(x => x.ApiEndpoint).FirstOrDefaultAsync(x => x.Id == mappingId && x.ScreenName == name, ct);
        if (m == null) return NotFound();
        if (m.ApiEndpoint!.RelativeUrl.Contains('{') || m.ApiEndpoint.HttpMethod != "GET") return Ok(new { success = false, error = "Needs parameters - use the API Catalog to execute." });
        var r = await _exec.ExecuteAsync(m.ApiEndpointId, null, null, true, false, ct);
        return Ok(r);
    }
}

[Authorize(Policy = Policies.Admin)]
public class AuditController : Controller { public IActionResult Index() => View(); }
