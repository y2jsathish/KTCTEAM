using System.Security.Claims;
using Ktc.Application.Abstractions;
using Ktc.Application.Models;
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
    private readonly IAuditService _audit; private readonly IDataProtectionProvider _dp; private readonly KtcOptions _opt; private readonly EntraIdOptions _entra;

    public const string EntraScheme = "EntraId";
    public const string ExternalScheme = "EntraExternal";

    public AccountController(IKtcAuthService auth, IKtcTokenProvider tokens, IUserService users, IAuditService audit, IDataProtectionProvider dp, IOptions<KtcOptions> opt, IOptions<EntraIdOptions> entra)
    { _auth = auth; _tokens = tokens; _users = users; _audit = audit; _dp = dp; _opt = opt.Value; _entra = entra.Value; }

    private LoginViewModel Blank(string? returnUrl) => new()
    { AppName = _opt.AppName, AuthMode = _opt.DefaultAuthMode, AuthModes = WithMsal(_opt.AuthModes), ReturnUrl = returnUrl, MockMode = _opt.UseMock,
      EntraEnabled = _entra.IsConfigured, EntraButtonText = _entra.ButtonText };

    /// <summary>MSAL (Single Sign-On) is always offered in the Auth mode list, so it is visible before Entra ID is set up.</summary>
    private static string[] WithMsal(string[] modes) => modes.Any(a => a.Equals(MsalMode, StringComparison.OrdinalIgnoreCase)) ? modes : modes.Append(MsalMode).ToArray();
    private const string MsalMode = "MSAL";

    /// <summary>What is still missing for Microsoft sign-in, in words the login page can show.</summary>
    private string EntraMissing()
    {
        var miss = new List<string>();
        if (!_entra.Enabled) miss.Add("EntraId:Enabled = true");
        if (string.IsNullOrWhiteSpace(_entra.TenantId)) miss.Add("EntraId:TenantId");
        if (string.IsNullOrWhiteSpace(_entra.ClientId)) miss.Add("EntraId:ClientId");
        if (string.IsNullOrWhiteSpace(_entra.ClientSecret)) miss.Add("EntraId:ClientSecret");
        return "Microsoft (MSAL) sign-in is not set up on this portal. An administrator needs to set " + string.Join(", ", miss) + " (see docs/DEPLOYMENT.md, \"Sign in with Microsoft\"), then restart the portal.";
    }

    [AllowAnonymous, HttpGet]
    public IActionResult Login(string? returnUrl = null, string? entraError = null)
    {
        if (User.Identity?.IsAuthenticated == true) return RedirectToAction("Index", "Home");
        var m = Blank(returnUrl);
        if (entraError != null) m.EntraError = "Microsoft sign-in did not complete. Try again, or sign in with your KTC credentials.";
        return View(m);
    }

    [AllowAnonymous, HttpPost, EnableRateLimiting("login")]
    public async Task<IActionResult> Login(LoginViewModel m)
    {
        var shell = Blank(m.ReturnUrl); m.AuthModes = shell.AuthModes; m.MockMode = shell.MockMode; m.EntraEnabled = shell.EntraEnabled; m.EntraButtonText = shell.EntraButtonText;
        if (string.Equals(m.AuthMode, MsalMode, StringComparison.OrdinalIgnoreCase))
        {
            // MSAL has no password: hand over to the Microsoft sign-in, or say what is missing.
            if (_entra.IsConfigured) return RedirectToAction(nameof(EntraLogin), new { returnUrl = m.ReturnUrl });
            ModelState.Clear();
            ModelState.AddModelError("", EntraMissing());
            m.Password = "";
            return View(m);
        }
        if (!ModelState.IsValid) return View(m);

        var res = await _auth.LoginAsync(m.Username.Trim(), m.Password, m.AuthMode, m.AppName);
        if (!res.Success)
        {
            await _audit.LogAsync("Login", "Login failed", m.Username, res.Error, false, m.Username.Trim());
            var err = res.Error ?? "Login failed.";
            // KTC answers 401 for a wrong password and also when the account is not allowed to use the chosen auth mode (KTC lists INTERNAL, DOMAIN, MSAL and
            // INTEGRATED via api/v1/LoginMethods). Accounts that sign in to the KTC app with Single Sign-On (MSAL) have no password KTC can check.
            if (err.StartsWith("Invalid username or password", StringComparison.OrdinalIgnoreCase))
                err += _entra.IsConfigured
                    ? " If your KTC account uses Single Sign-On (MSAL), use \"" + _entra.ButtonText + "\" below instead."
                    : " If your KTC account uses Single Sign-On (MSAL), it has no password KTC can check here: try another Auth mode, or ask an administrator to enable Microsoft sign-in (EntraId settings).";
            ModelState.AddModelError("", err);
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

    /// <summary>Starts Microsoft (Entra ID) sign-in. Only available when EntraId is enabled and fully configured.</summary>
    [AllowAnonymous, HttpGet, EnableRateLimiting("login")]
    public IActionResult EntraLogin(string? returnUrl = null)
    {
        if (!_entra.IsConfigured) return NotFound();
        var done = Url.Action(nameof(EntraDone), "Account", new { returnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : null })!;
        return Challenge(new AuthenticationProperties { RedirectUri = done }, EntraScheme);
    }

    /// <summary>Back from Entra: map the Microsoft identity to a portal user (role from Portal:AdminUsers / OperatorUsers by sign-in name), sign in to KTC, issue the normal portal cookie.</summary>
    [AllowAnonymous, HttpGet]
    public async Task<IActionResult> EntraDone(string? returnUrl = null)
    {
        if (!_entra.IsConfigured) return NotFound();
        var ext = await HttpContext.AuthenticateAsync(ExternalScheme);
        await HttpContext.SignOutAsync(ExternalScheme);
        if (!ext.Succeeded || ext.Principal == null) return RedirectToAction(nameof(Login), new { returnUrl, entraError = 1 });

        string? Claim(string t) { var v = ext.Principal.FindFirst(t)?.Value; return string.IsNullOrWhiteSpace(v) ? null : v.Trim(); }
        var name = Claim(_entra.UsernameClaim) ?? Claim("preferred_username") ?? Claim("upn") ?? Claim("email");
        if (name == null)
        {
            await _audit.LogAsync("Login", "Login failed", null, "Entra token has no user name claim", false, null);
            return View("Login", Blank(returnUrl).With(m => m.EntraError = "Your Microsoft account did not return a user name (preferred_username/upn/email)."));
        }
        var idToken = ext.Properties?.GetTokenValue("id_token");

        var user = await _users.EnsureUserAsync(name);
        if (user.Role == "Disabled")
        {
            await _audit.LogAsync("Login", "Login failed", name, "portal account disabled", false, name);
            return View("Login", Blank(returnUrl).With(m => m.EntraError = "Your portal account is disabled. Contact an administrator."));
        }

        HttpContext.Session.Clear();
        var claims = new List<Claim> { new(ClaimTypes.Name, user.Username), new(ClaimTypes.NameIdentifier, user.Id.ToString()), new(ClaimTypes.Role, user.Role), new("theme", user.Theme), new("authmode", "MSAL") };
        string via = "KTC session";
        KtcLoginResult? ktc = null;
        if (_entra.KtcLogin && !string.IsNullOrEmpty(idToken))
            ktc = await _auth.LoginAsync(name, idToken, "MSAL", _opt.AppName);
        if (ktc is { Success: true })
        {
            KtcTokenProvider.Remember(HttpContext, user.Username, ktc.Token!, ktc.ExpiresUtc!.Value);
            HttpContext.Session.SetString(SessionKeys.Cred, KtcTokenProvider.ProtectCredentials(_dp, name, idToken!, "MSAL", _opt.AppName));
        }
        else if (_entra.FallbackToServiceAccount && (_opt.UseMock || _opt.ServiceAccount.Configured))
        {
            claims.Add(new("ktcsvc", "1")); via = "service account (KTC did not accept the Microsoft token)";
        }
        else
        {
            await _audit.LogAsync("Login", "Login failed", name, "KTC rejected MSAL login: " + (ktc?.Error ?? "KtcLogin disabled"), false, name);
            return View("Login", Blank(returnUrl).With(m => m.EntraError = "Microsoft sign-in worked, but KTC did not accept it. " + (ktc?.Error ?? "")));
        }

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)),
            new AuthenticationProperties { IsPersistent = false });
        await _audit.LogAsync("Login", "Login success (Microsoft)", user.Username, $"role={user.Role}; KTC via {via}", true, user.Username);
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/");
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

internal static class LoginViewModelExt
{
    public static LoginViewModel With(this LoginViewModel m, Action<LoginViewModel> f) { f(m); return m; }
}
