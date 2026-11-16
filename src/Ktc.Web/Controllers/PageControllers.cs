using Ktc.Application.Abstractions;
using Ktc.Application.Logic;
using Ktc.Domain.Enums;
using Ktc.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Ktc.Web.Controllers;

public class AtmController : Controller
{
    private readonly IDashboardService _dash; private readonly IKtcDataService _data; private readonly IReportService _reports;
    public AtmController(IDashboardService dash, IKtcDataService data, IReportService reports) { _dash = dash; _data = data; _reports = reports; }
    public IActionResult Index() => View();
    /// <summary>ATM 360 view (Overview, Application Counters and XFS Counters tabs).</summary>
    [Audit("AtmView", "ATM 360 view")]
    public IActionResult Detail(string id) { ViewBag.AtmId = id; return View(); }

    /// <summary>Export the application and XFS counters of one ATM as CSV, Excel or PDF.</summary>
    [HttpGet, Audit("Report", "ATM counters export")]
    /// <param name="devices">XFS service classes to include (comma separated, e.g. "CDM,CIM,IPM", the devices ticked on the XFS tab); empty includes all.</param>
    public async Task<IActionResult> CountersExport(string id, string format = "csv", string? devices = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return BadRequest("id is required");
        var c = await _data.GetCountersAsync(id.Trim(), ct);
        if (c == null) return NotFound($"No ATM matches \"{id.Trim()}\".");
        if (!string.IsNullOrWhiteSpace(devices)) c = c with { Xfs = CounterRules.FilterClasses(c.Xfs, devices) };
        var f = format.ToLowerInvariant() switch { "xlsx" or "excel" => ExportFormat.Excel, "pdf" => ExportFormat.Pdf, _ => ExportFormat.Csv };
        var file = await _reports.ExportAsync(CounterRules.ToReport(c, DateTime.UtcNow), f);
        return File(file.Data, file.MimeType, file.FileName);
    }
}

public class IncidentsController : Controller { [Audit("IncidentView", "Incident command center")] public IActionResult Index(string? q) { ViewBag.Q = q; return View(); } }

public class TicketsController : Controller { [Audit("TicketView", "Ticket workbench")] public IActionResult Index(string? ticketId, string? atmId) { ViewBag.TicketId = ticketId; ViewBag.AtmId = atmId; return View(); } }

public class HardwareController : Controller { [Audit("HardwareView", "ATM hardware status")] public IActionResult Index(string? atmId, string? atm, string? component) { ViewBag.AtmId = string.IsNullOrWhiteSpace(atmId) ? atm : atmId; ViewBag.Component = component; return View(); } }

public class TransactionsController : Controller { public IActionResult Index(string? id) { ViewBag.Id = id; return View(); } }
