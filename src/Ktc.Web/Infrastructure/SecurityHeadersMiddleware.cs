namespace Ktc.Web.Infrastructure;

public class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    public SecurityHeadersMiddleware(RequestDelegate next) => _next = next;
    public Task Invoke(HttpContext ctx)
    {
        var h = ctx.Response.Headers;
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "DENY";
        h["Referrer-Policy"] = "same-origin";
        h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        // Inline scripts are used by the page views; tighten with nonces if the CDN assets are vendored locally.
        h["Content-Security-Policy"] = "default-src 'self'; script-src 'self' 'unsafe-inline' https://cdnjs.cloudflare.com https://cdn.datatables.net; " +
            "style-src 'self' 'unsafe-inline' https://cdnjs.cloudflare.com https://cdn.datatables.net; font-src 'self' https://cdnjs.cloudflare.com; img-src 'self' data:; connect-src 'self' ws: wss:; frame-ancestors 'none'";
        return _next(ctx);
    }
}

/// <summary>Adds a correlation id to the response and the log scope for every request.</summary>
public class CorrelationMiddleware
{
    private readonly RequestDelegate _next; private readonly ILogger<CorrelationMiddleware> _log;
    public CorrelationMiddleware(RequestDelegate next, ILogger<CorrelationMiddleware> log) { _next = next; _log = log; }
    public async Task Invoke(HttpContext ctx)
    {
        var id = ctx.Request.Headers["X-Correlation-ID"].FirstOrDefault() ?? Guid.NewGuid().ToString("N")[..12];
        ctx.Response.Headers["X-Correlation-ID"] = id;
        using (_log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = id, ["User"] = ctx.User.Identity?.Name ?? "anonymous" })) await _next(ctx);
    }
}

/// <summary>Turns unhandled exceptions in /data/* calls into JSON problem details (pages use the Error view).</summary>
public class ApiExceptionMiddleware
{
    private readonly RequestDelegate _next; private readonly ILogger<ApiExceptionMiddleware> _log;
    public ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> log) { _next = next; _log = log; }
    public async Task Invoke(HttpContext ctx)
    {
        try { await _next(ctx); }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { }
        catch (Exception ex) when (ctx.Request.Path.StartsWithSegments("/data") && !ctx.Response.HasStarted)
        {
            _log.LogError(ex, "Unhandled error in {Path}", ctx.Request.Path);
            ctx.Response.StatusCode = 500; ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsJsonAsync(new { error = "An unexpected error occurred.", correlationId = ctx.Response.Headers["X-Correlation-ID"].ToString() });
        }
    }
}
