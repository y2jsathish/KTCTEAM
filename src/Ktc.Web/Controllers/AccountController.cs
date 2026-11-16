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
    { AppName = _opt.AppName, AuthMode = _opt.DefaultAuthMode, AuthModes = new[] { _opt.DefaultAuthMode }.Concat(_opt.AuthModes).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim().ToUpperInvariant()).Distinct().ToArray(), ReturnUrl = returnUrl, MockMode = _opt.UseMock };

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
        KtcTokenProvider.Remember(HttpContext, user.Username, res.Token!, res.ExpiresUtc!.Value);
        HttpContext.Session.SetString(SessionKeys.Cred, KtcTokenProvider.ProtectCredentials(_dp, m.Username.Trim(), m.Password, m.AuthMode, m.AppName));

        var claims = new List<Claim> { new(ClaimTypes.Name, user.Username), new(ClaimTypes.NameIdentifier, user.Id.ToString()), new(ClaimTypes.Role, user.Role), new("theme", user.Theme), new("authmode", m.AuthMode) };
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)),
            new AuthenticationProperties { IsPersistent = false });
        await _audit.LogAsync("Login", "Login success", user.Username, $"role={user.Role}", true, user.Username);
        return LocalRedirect(Url.IsLocalUrl(m.ReturnUrl) ? m.ReturnUrl! : "/");
    }

    /// <summary>A typed or bookmarked /Account/Logout used to answer 405. Sign-out stays POST-only (CSRF); this sends people home, where the sign-out button is.</summary>
    [HttpGet("/Account/Logout")]
    public IActionResult LogoutGet() => User.Identity?.IsAuthenticated == true ? Redirect("/") : RedirectToAction(nameof(Login));

    [HttpPost]
    public async Task<IActionResult> Logout()
    {
        var token = KtcTokenProvider.Current(HttpContext).Token;
        if (token != null) await _auth.LogoffAsync(token);
        KtcTokenProvider.Forget(User.Identity?.Name);
        await _audit.LogAsync("Login", "Logout");
        HttpContext.Session.Clear();
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login));
    }

    /// <summary>Step-by-step KTC login/token check for "401 Invalid token". Uses the signed-in user's own credentials.</summary>
    [HttpGet("/diag/ktc-token")]
    public IActionResult TokenCheck() => View("TokenCheck", (string?)null);

    [HttpPost("/diag/ktc-token"), EnableRateLimiting("login")]
    public async Task<IActionResult> TokenCheck([FromServices] KtcAuthDiagnostics diag, CancellationToken ct)
    {
        var cred = HttpContext.Session.GetString(SessionKeys.Cred);
        if (cred == null) return View("TokenCheck", "Your saved KTC credentials are missing. Sign out and sign in again, then rerun the check.");
        string[] p;
        try { p = _dp.CreateProtector("Ktc.SessionCredentials").Unprotect(cred).Split('\n', 4); }
        catch { return View("TokenCheck", "Your saved KTC credentials could not be read. Sign out and sign in again, then rerun the check."); }
        var report = await diag.RunAllAsync(p[0], p[3], p[1].Length == 0 ? null : p[1], p[2], ct);
        await _audit.LogAsync("Diagnostics", "KTC token check");
        return View("TokenCheck", report);
    }

    [HttpGet("/data/session")]
    public IActionResult SessionStatus()
    {
        var exp = KtcTokenProvider.Current(HttpContext).ExpiresUtc;
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
