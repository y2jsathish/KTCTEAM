using System.Security.Claims;
using Ktc.Application.Abstractions;
using Ktc.Application.Logic;
using Ktc.Infrastructure.KtcApi;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Ktc.Web.Infrastructure;

public static class Roles { public const string Admin = "Admin", Operator = "Operator", Viewer = "Viewer"; }
public static class Policies { public const string Operator = "OperatorOrAdmin", Admin = "AdminOnly"; }

/// <summary>Drops the portal cookie when the server-side KTC session (token) no longer exists, e.g. after an app restart.</summary>
public static class CookieEvents
{
    public static async Task ValidateAsync(CookieValidatePrincipalContext ctx)
    {
        var s = ctx.HttpContext.Session;
        await s.LoadAsync();
        if (s.GetString(SessionKeys.Token) == null && s.GetString(SessionKeys.Cred) == null)
        { ctx.RejectPrincipal(); await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme); }
    }

    public static Task ApiAware401(RedirectContext<CookieAuthenticationOptions> ctx)
    {
        if (ctx.Request.Path.StartsWithSegments("/data") || ctx.Request.Path.StartsWithSegments("/hubs")) ctx.Response.StatusCode = 401;
        else ctx.Response.Redirect(ctx.RedirectUri);
        return Task.CompletedTask;
    }
}

/// <summary>Records page/search/view access in AuditLogs. Query values are PAN-masked before storage.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class AuditAttribute : ActionFilterAttribute
{
    private readonly string _category, _action;
    public AuditAttribute(string category, string action) { _category = category; _action = action; }

    public override async Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
    {
        var done = await next();
        var http = ctx.HttpContext;
        var q = http.Request.Query.Where(k => k.Value.Count > 0 && k.Key != "_" && k.Key != "draw")
            .Select(k => $"{k.Key}={Sanitize(k.Key, k.Value.ToString())}");
        var route = ctx.RouteData.Values.Where(v => v.Key is "id" or "atmId").Select(v => v.Value?.ToString());
        var target = string.Join(" ", route.Where(x => !string.IsNullOrEmpty(x)));
        var ok = done.Exception == null && (done.Result is not Microsoft.AspNetCore.Mvc.StatusCodeResult sc || sc.StatusCode < 400);
        await http.RequestServices.GetRequiredService<IAuditService>().LogAsync(_category, _action, target.Length > 0 ? target : null, string.Join("&", q), ok);
    }

    private static string Sanitize(string key, string v) =>
        key.Contains("card", StringComparison.OrdinalIgnoreCase) || PanMasker.LooksLikePan(v) ? PanMasker.MaskDigits(PanMasker.Digits(v)) : PanMasker.Mask(v);
}

public static class HttpExtensions
{
    public static string UserName(this ClaimsPrincipal p) => p.Identity?.Name ?? "unknown";
}
