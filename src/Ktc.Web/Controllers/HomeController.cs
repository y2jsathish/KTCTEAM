using System.Diagnostics;
using Ktc.Application.Abstractions;
using Ktc.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ktc.Web.Controllers;

public class HomeController : Controller
{
    public IActionResult Index() => View();
    public IActionResult Management() => View();
    public IActionResult Clients() => View();
    [Audit("Search", "Root cause analytics")] public IActionResult Rca() => View();
    [Audit("Search", "Global search")] public IActionResult Search(string? q) { ViewBag.Q = q; return View(); }
    [AllowAnonymous, ResponseCache(Duration = 0, NoStore = true)] public IActionResult Error() => View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
}
public class ErrorViewModel { public string? RequestId { get; set; } public bool ShowRequestId => !string.IsNullOrEmpty(RequestId); }
