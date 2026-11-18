using Ktc.Application.Abstractions;
using Ktc.Application.Models;
using Ktc.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ktc.Web.Controllers;

/// <summary>JSON for the Disputes menu (KTC api/v1/DisputeManagement). Reads are audited; writes need DisputeWrite:Enabled and an operator.</summary>
[Route("data/disputes")]
[ApiController]
public class DisputeDataController : ControllerBase
{
    private readonly IKtcDisputeService _d;
    public DisputeDataController(IKtcDisputeService d) { _d = d; }

    /// <param name="state">KTC dispute state id (from GET DisputeState).</param>
    /// <param name="card">Last 4 digits of the card; only those are compared.</param>
    [HttpGet("")]
    [Audit("DisputeView", "Dispute search")]
    public async Task<IActionResult> Search(DateTime? from, DateTime? to, int? state, string? atmId, decimal? minAmount, decimal? maxAmount, string? disputeId, string? card, CancellationToken ct)
    {
        if (from > to) return BadRequest(new { error = "The start date is after the end date." });
        if (minAmount > maxAmount) return BadRequest(new { error = "The minimum amount is above the maximum." });
        var r = await _d.SearchAsync(new DisputeQuery(from?.ToUniversalTime(), to?.ToUniversalTime(), state, atmId, minAmount, maxAmount, disputeId, card), ct);
        var counts = r.Rows.GroupBy(x => x.State).Select(g => new { name = g.Key, stateId = g.First().StateId, count = g.Count() }).OrderByDescending(x => x.count).ToList();
        return Ok(new { data = r.Rows, states = r.States, counts, open = r.Rows.Count(x => !x.IsClosed), closed = r.Rows.Count(x => x.IsClosed),
            totalAmount = r.Rows.Where(x => x.Amount != null).GroupBy(x => x.Currency).Select(g => new { currency = g.Key, amount = g.Sum(x => x.Amount) }),
            r.Warnings, r.Sources, r.AmountKnown });
    }

    [HttpGet("{id}/details")]
    [Audit("DisputeView", "Dispute details")]
    public async Task<IActionResult> Details(string id, CancellationToken ct)
    {
        var d = await _d.GetDetailsAsync(id, ct);
        return d == null ? NotFound(new { error = $"KTC has no dispute {id}." }) : Ok(d);
    }

    [HttpGet("setup")]
    [Audit("DisputeView", "Dispute set-up")]
    public async Task<IActionResult> Setup(CancellationToken ct) => Ok(await _d.GetSetupAsync(ct));

    // ---------- change state / close (DisputeWrite:Enabled) ----------
    [HttpGet("{id}/write")]
    public async Task<IActionResult> WriteState(string id, [FromServices] IDisputeWriteService w, CancellationToken ct) => Ok(await w.StateAsync(id, ct));

    [HttpPost("{id}/state")]
    [Authorize(Policy = Policies.Operator)]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("atm-actions")]
    public async Task<IActionResult> ChangeState(string id, [FromBody] DisputeWriteRequest? q, [FromServices] IDisputeWriteService w, CancellationToken ct) =>
        q == null ? BadRequest(new { error = "Invalid request body." }) : Ok(await w.ChangeStateAsync(id, q, ct));

    [HttpPost("{id}/close")]
    [Authorize(Policy = Policies.Operator)]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("atm-actions")]
    public async Task<IActionResult> Close(string id, [FromBody] DisputeWriteRequest? q, [FromServices] IDisputeWriteService w, CancellationToken ct) =>
        q == null ? BadRequest(new { error = "Invalid request body." }) : Ok(await w.CloseAsync(id, q, ct));
}
