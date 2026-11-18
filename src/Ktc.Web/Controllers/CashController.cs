using Ktc.Application.Abstractions;
using Ktc.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ktc.Web.Controllers;

/// <summary>Cash Forecasting menu (phase 1): cash position, run-out estimates, suggested replenishment and reconciliation. Results are estimates.</summary>
public class CashController : Controller
{
    private readonly ICashForecastService _cash; private readonly IAuditService _audit; private readonly ILogger<CashController> _log;
    public CashController(ICashForecastService cash, IAuditService audit, ILogger<CashController> log) { _cash = cash; _audit = audit; _log = log; }

    [Audit("CashView", "Cash forecasting")]
    public IActionResult Index() => View();

    [Audit("CashView", "Cash forecast of one ATM")]
    public IActionResult Atm(string id) { ViewBag.AtmId = id; return View(); }

    /// <summary>The latest stored estate forecast (computed on first use). lowPct = low-cash threshold, % of cassette capacity (empty = configured).</summary>
    [HttpGet("/data/cash/forecast")]
    public async Task<IActionResult> Forecast(double? lowPct, CancellationToken ct)
        => Bad(lowPct) is { } e ? BadRequest(new { error = e }) : Ok(await _cash.GetEstateAsync(lowPct, ct));

    private static string? Bad(double? lowPct) => lowPct is { } p && (!double.IsFinite(p) || p < 0 || p > 100) ? "The low-cash threshold must be a percent from 0 to 100." : null;

    /// <summary>Recalculates the estate now (reads every ATM's cash units; takes a while on a large estate).</summary>
    [HttpPost("/data/cash/recalculate"), Authorize(Policy = Policies.Operator)]
    public async Task<IActionResult> Recalculate(double? lowPct, CancellationToken ct)
    {
        if (Bad(lowPct) is { } e) return BadRequest(new { error = e });
        var f = await _cash.RunAsync("by " + (User.Identity?.Name ?? "user"), ct);
        await _audit.LogAsync("CashForecast", "Recalculate", null, $"{f.Atms.Count} ATMs");
        return Ok(await _cash.GetEstateAsync(lowPct, ct));
    }

    /// <summary>One ATM, live: forecast, reconciliation XFS / EJ / application counters, daily cash and snapshots.</summary>
    [HttpGet("/data/cash/atm/{id}")]
    public async Task<IActionResult> AtmData(string id, double? lowPct, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id)) return BadRequest(new { error = "Select an ATM." });
        if (Bad(lowPct) is { } e) return BadRequest(new { error = e });
        var d = await _cash.GetAtmAsync(id.Trim(), lowPct, ct);
        return d == null ? NotFound(new { error = $"No ATM matches \"{id.Trim()}\"." }) : Ok(d);
    }

    /// <summary>Actual cash withdrawn / deposited per day across the estate (stored history).</summary>
    [HttpGet("/data/cash/daily")]
    public async Task<IActionResult> Daily(int days = 14, CancellationToken ct = default) => Ok(await _cash.GetDailyAsync(days, ct));
}
