using System.Security.Claims;
using Ktc.Application.Abstractions;
using Ktc.Application.Options;
using Ktc.Infrastructure.KtcApi;
using Ktc.Web.ViewModels;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Ktc.Web.Controllers;

public class AccountController : Controller
{
    private readonly IKtcAuthService _auth; private readonly IKtcTokenProvider _tokens; private readonly IUserService _users;
    private readonly IAuditService _audit; private readonly IDataProtectionProvider _dp; private readonly KtcOptions _opt;

    public AccountController(IKtcAuthService auth, IKtcTokenProvider tokens, IUserService users, IAuditService audit, IDataProtectionProvider dp, IOptions<KtcOptions> opt)
    { _auth = auth; _tokens = tokens; _users = users; _audit = audit; _dp = dp; _opt = opt.Value; }

    private LoginViewModel Blank(string? returnUrl) => new()
    { AppName = _opt.AppName, AuthMode = _opt.DefaultAuthMode, AuthModes = _opt.AuthModes, ReturnUrl = returnUrl, MockMode = _opt.UseMock };

    [AllowAnonymous, HttpGet]
    public IActionResult Login(string? returnUrl = null) => User.Identity?.IsAuthenticated == true ? RedirectToAction("Index", "Home") : View(Blank(returnUrl));

    [AllowAnonymous, HttpPost, EnableRateLimiting("login")]
    public async Task<IActionResult> Login(LoginViewModel m)
    {
        var shell = Blank(m.ReturnUrl); m.AuthModes = shell.AuthModes; m.MockMode = shell.MockMode;
        if (!ModelState.IsValid) return View(m);

        var res = await _auth.LoginAsync(m.Username.Trim(), m.Password, m.AuthMode, m.AppName);
        if (!res.Success)
        {
            await _audit.LogAsync("Login", "Login failed", m.Username, res.Error, false, m.Username.Trim());
            ModelState.AddModelError("", res.Error ?? "Login failed.");
            m.Password = "";
            return View(m);
        }
        var user = await _users.EnsureUserAsync(m.Username.Trim());
        if (user.Role == "Disabled")
        {
            await _auth.LogoffAsync(res.Token!);
            ModelState.AddModelError("", "Your portal account is disabled. Contact an administrator.");
            return View(m);
        }

        HttpContext.Session.Clear();
        HttpContext.Session.SetString(SessionKeys.Token, res.Token!);
        HttpContext.Session.SetString(SessionKeys.Expiry, res.ExpiresUtc!.Value.Ticks.ToString());
        HttpContext.Session.SetString(SessionKeys.Cred, KtcTokenProvider.ProtectCredentials(_dp, m.Username.Trim(), m.Password, m.AuthMode, m.AppName));

        var claims = new List<Claim> { new(ClaimTypes.Name, user.Username), new(ClaimTypes.NameIdentifier, user.Id.ToString()), new(ClaimTypes.Role, user.Role), new("theme", user.Theme), new("authmode", m.AuthMode) };
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)),
            new AuthenticationProperties { IsPersistent = false });
        await _audit.LogAsync("Login", "Login success", user.Username, $"role={user.Role}", true, user.Username);
        return LocalRedirect(Url.IsLocalUrl(m.ReturnUrl) ? m.ReturnUrl! : "/");
    }

    [HttpPost]
    public async Task<IActionResult> Logout()
    {
        var token = HttpContext.Session.GetString(SessionKeys.Token);
        if (token != null) await _auth.LogoffAsync(token);
        await _audit.LogAsync("Login", "Logout");
        HttpContext.Session.Clear();
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login));
    }

    [HttpGet("/data/session")]
    public IActionResult SessionStatus()
    {
        var exp = long.TryParse(HttpContext.Session.GetString(SessionKeys.Expiry), out var t) ? new DateTime(t, DateTimeKind.Utc) : (DateTime?)null;
        var left = exp.HasValue ? (int)Math.Max(0, (exp.Value - DateTime.UtcNow).TotalSeconds) : 0;
        return Json(new { user = User.Identity!.Name, role = User.FindFirstValue(ClaimTypes.Role), status = left > 0 ? "Active" : "Refreshing", expiresUtc = exp, secondsLeft = left, mock = _opt.UseMock });
    }

    [HttpPost("/data/session/refresh")]
    public async Task<IActionResult> Refresh()
    {
        var token = await _tokens.RefreshAsync(HttpContext.RequestAborted);
        if (token == null) return Unauthorized(new { error = "Session could not be refreshed. Please sign in again." });
        return SessionStatus();
    }

    [HttpPost("/data/theme")]
    public async Task<IActionResult> Theme([FromForm] string theme) { await _users.SetThemeAsync(User.Identity!.Name!, theme); return NoContent(); }

    [AllowAnonymous] public IActionResult Denied() => View();
}
