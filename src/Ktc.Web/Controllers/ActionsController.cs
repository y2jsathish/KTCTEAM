using Ktc.Application.Abstractions;
using Ktc.Application.Models;
using Ktc.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Ktc.Web.Controllers;

/// <summary>
/// ATM Actions tab. Lists KTC's command types and sends one command to one ATM from the generic parameter form.
/// Sending only happens when KtcActions:SendEnabled is true; every attempt (and every check) is audited by the service.
/// Anti-forgery applies to the POSTs (global AutoValidateAntiforgeryToken filter).
/// </summary>
[Route("data/actions")]
[ApiController]
public class ActionsController : ControllerBase
{
    private readonly IKtcActionService _actions; private readonly IRebootService _reboots;
    public ActionsController(IKtcActionService actions, IRebootService reboots) { _actions = actions; _reboots = reboots; }

    [HttpGet("command-types")]
    [Audit("AtmActions", "KTC command types viewed")]
    public async Task<IActionResult> CommandTypes(bool refresh, CancellationToken ct) => Ok(await _actions.GetCatalogAsync(refresh, ct));

    /// <summary>Runs every check without sending (the confirm dialog calls this before showing the Send button).</summary>
    [HttpPost("check")]
    [EnableRateLimiting("atm-actions-check")]
    public async Task<IActionResult> Check([FromBody] AtmActionRequest? q, CancellationToken ct)
        => q == null ? BadRequest(new { error = "Invalid request body." }) : Ok(await _actions.SendAsync(q, checkOnly: true, ct));

    /// <summary>Sends the command when every check passes and sending is enabled.</summary>
    [HttpPost("send")]
    [EnableRateLimiting("atm-actions")]
    public async Task<IActionResult> Send([FromBody] AtmActionRequest? q, CancellationToken ct)
        => q == null ? BadRequest(new { error = "Invalid request body." }) : Ok(await _actions.SendAsync(q, checkOnly: false, ct));

    // ---------- what happened after sending ----------

    /// <summary>The ATM's KTC actions (last 1-31 days) and its guided reboots.</summary>
    [HttpGet("atm/{id}/history")]
    [Audit("AtmActions", "ATM action history viewed")]
    public async Task<IActionResult> History(string id, int days = 7, CancellationToken ct = default)
    {
        var h = await _actions.GetHistoryAsync(id, Math.Clamp(days, 1, 31), ct);
        return Ok(h with { Reboots = await _reboots.ListAsync(h.AtmId, false, ct) });
    }

    [HttpGet("action/{id}")]
    public async Task<IActionResult> Action(string id, CancellationToken ct) => await _actions.GetActionAsync(id, ct) is { } a ? Ok(a) : NotFound(new { error = $"KTC has no action {id}." });

    [HttpGet("action/{id}/files")]
    public async Task<IActionResult> Files(string id, CancellationToken ct)
    {
        var (files, err) = await _actions.GetFilesAsync(id, ct);
        return err != null ? BadRequest(new { error = err }) : Ok(files);
    }

    /// <summary>Preview (JSON) or download (?download=true) of one uploaded file: text only, PAN-masked, track data removed.</summary>
    [HttpGet("action/{id}/file")]
    public async Task<IActionResult> File(string id, string name, bool download, CancellationToken ct)
    {
        var (f, err) = await _actions.GetFileAsync(id, name, preview: !download, ct);
        if (err != null || f == null) return BadRequest(new { error = err ?? "File not available." });
        if (!download) return Ok(f);
        if (f.Text == null) return StatusCode(415, new { error = f.BlockedReason });
        var safe = string.Concat(Path.GetFileName(f.Name).Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_'));
        return File(System.Text.Encoding.UTF8.GetBytes(f.Text), "text/plain; charset=utf-8", $"masked-{safe}{(safe.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? "" : ".txt")}");
    }

    // ---------- guided reboot ----------

    [HttpPost("reboot")]
    [EnableRateLimiting("atm-actions")]
    public async Task<IActionResult> StartReboot([FromBody] RebootRequest? q, CancellationToken ct)
    {
        if (q == null) return BadRequest(new { error = "Invalid request body." });
        var (run, result) = await _reboots.StartAsync(q, ct);
        return Ok(new { run, result });
    }

    [HttpGet("reboot/{id}")]
    public async Task<IActionResult> Reboot(string id, CancellationToken ct) => await _reboots.GetAsync(id, ct) is { } r ? Ok(r) : NotFound(new { error = "No such guided reboot." });

    [HttpPost("reboot/{id}/cancel")]
    public async Task<IActionResult> CancelReboot(string id, CancellationToken ct) => await _reboots.CancelAsync(id, ct) is { } r ? Ok(r) : NotFound(new { error = "No such guided reboot." });
}
