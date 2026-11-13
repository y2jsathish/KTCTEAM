using Ktc.Application.Abstractions;
using Ktc.Domain.Enums;
using Ktc.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Ktc.Web.Controllers;

public class AtmController : Controller
{
    private readonly IDashboardService _dash;
    public AtmController(IDashboardService dash) => _dash = dash;
    public IActionResult Index() => View();
    /// <summary>ATM 360 view.</summary>
    [Audit("AtmView", "ATM 360 view")]
    public IActionResult Detail(string id) { ViewBag.AtmId = id; return View(); }
}

public class IncidentsController : Controller { [Audit("IncidentView", "Incident command center")] public IActionResult Index(string? q) { ViewBag.Q = q; return View(); } }

public class TicketsController : Controller { [Audit("TicketView", "Ticket workbench")] public IActionResult Index(string? ticketId, string? atmId) { ViewBag.TicketId = ticketId; ViewBag.AtmId = atmId; return View(); } }

public class HardwareController : Controller { [Audit("HardwareView", "ATM hardware status")] public IActionResult Index(string? atmId) { ViewBag.AtmId = atmId; return View(); } }

public class TransactionsController : Controller { public IActionResult Index(string? id) { ViewBag.Id = id; return View(); } }
