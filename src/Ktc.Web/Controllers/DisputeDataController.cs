using Ktc.Application.Abstractions;
using Ktc.Application.Models;
using Ktc.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ktc.Web.Controllers;

/// <summary>JSON for the Disputes menu (KTC api/v1/DisputeManagement). Reads are audited; writes need DisputeWrite:Enabled and an operator (change state / close also DisputeWrite:AllowStateChange).</summary>
[Route("data/disputes")]
[ApiController]
public class DisputeDataController : ControllerBase
{
    private readonly IKtcDisputeService _d;
    public DisputeDataController(IKtcDisputeService d) { _d = d; }

    /// <param name="state">KTC dispute state id (from GET DisputeState), sent as statusfilter; 0 or none = all.</param>
    /// <param name="days">"Number of days to show" (KTC's Numdays).</param>
    /// <param name="card">Last 4 digits of the card; only those are compared.</param>
    [HttpGet("")]
    [Audit("DisputeView", "Dispute search")]
    public async Task<IActionResult> Search(int? days, int? state, string? atmId, decimal? minAmount, decimal? maxAmount, string? disputeId, string? card, CancellationToken ct)
    {
        if (days is < 1 or > 3650) return BadRequest(new { error = "Number of days to show must be 1 to 3650." });
        if (minAmount > maxAmount) return BadRequest(new { error = "The minimum amount is above the maximum." });
        var r = await _d.SearchAsync(new DisputeQuery(days, state, atmId, minAmount, maxAmount, disputeId, card), ct);
        var counts = r.Rows.GroupBy(x => x.State).Select(g => new { name = g.Key, stateId = g.First().StateId, count = g.Count() }).OrderByDescending(x => x.count).ToList();
        return Ok(new { data = r.Rows, states = r.States, counts, open = r.Rows.Count(x => !x.IsClosed), closed = r.Rows.Count(x => x.IsClosed),
            totalAmount = r.Rows.Where(x => x.Amount != null).GroupBy(x => x.Currency).Select(g => new { currency = g.Key, amount = g.Sum(x => x.Amount) }),
            r.Warnings, r.Sources, r.AmountKnown });
    }

    /// <summary>KTC's raw dispute records (card data masked) and their field names: the one-click way to see what KTC really returns.</summary>
    [HttpGet("raw")]
    [Audit("DisputeView", "Dispute raw records")]
    public async Task<IActionResult> Raw(int? days, int? state, string? atmId, CancellationToken ct) =>
        days is < 1 or > 3650 ? BadRequest(new { error = "Number of days to show must be 1 to 3650." }) : Ok(await _d.GetRawSampleAsync(new DisputeQuery(days, state, atmId), 3, ct));

    [HttpGet("{id}/details")]
    [Audit("DisputeView", "Dispute details")]
    public async Task<IActionResult> Details(string id, string? atmId, CancellationToken ct)
    {
        var d = await _d.GetDetailsAsync(id, atmId, ct);
        return d == null ? NotFound(new { error = $"KTC has no dispute {id}." }) : Ok(d);
    }

    [HttpGet("setup")]
    [Audit("DisputeView", "Dispute set-up")]
    public async Task<IActionResult> Setup(CancellationToken ct) => Ok(await _d.GetSetupAsync(ct));

    // ---------- change state / close (DisputeWrite:Enabled) ----------
    [HttpGet("{id}/write")]
    public async Task<IActionResult> WriteState(string id, string? atmId, [FromServices] IDisputeWriteService w, CancellationToken ct) => Ok((await w.StateAsync(id, atmId, ct)) with { UserAllowed = User.IsInRole(Roles.Admin) || User.IsInRole(Roles.Operator) });

    [HttpPost("{id}/comment")]
    [Authorize(Policy = Policies.Operator)]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("atm-actions")]
    public async Task<IActionResult> Comment(string id, [FromBody] DisputeWriteRequest? q, [FromServices] IDisputeWriteService w, CancellationToken ct) =>
        q == null ? BadRequest(new { error = "Invalid request body." }) : Ok(await w.AddCommentAsync(id, q, ct));

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
