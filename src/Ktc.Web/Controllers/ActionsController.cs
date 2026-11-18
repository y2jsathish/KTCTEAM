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
    private readonly IKtcActionService _actions;
    public ActionsController(IKtcActionService actions) => _actions = actions;

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
}
