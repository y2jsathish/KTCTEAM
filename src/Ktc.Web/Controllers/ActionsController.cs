using Ktc.Application.Abstractions;
using Ktc.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Ktc.Web.Controllers;

/// <summary>
/// ATM Actions tab, step 1 (read-only). Lists KTC's command types so the real command ids can be confirmed.
/// There is deliberately no endpoint here that sends a command to an ATM.
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
}
