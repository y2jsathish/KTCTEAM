using Ktc.Application.Abstractions;
using Ktc.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Ktc.Infrastructure.Services;

public class AuditService : IAuditService
{
    private readonly IUnitOfWork _uow; private readonly IHttpContextAccessor _http; private readonly ILogger<AuditService> _log;
    public AuditService(IUnitOfWork uow, IHttpContextAccessor http, ILogger<AuditService> log) { _uow = uow; _http = http; _log = log; }

    public async Task LogAsync(string category, string action, string? target = null, string? details = null, bool success = true, string? userOverride = null)
    {
        try
        {
            var ctx = _http.HttpContext;
            var entry = new AuditLog
            {
                UserName = userOverride ?? ctx?.User.Identity?.Name ?? "system", Category = category, Action = action,
                Target = Trunc(target, 400), Details = Trunc(details, 4000), Success = success, IpAddress = ctx?.Connection.RemoteIpAddress?.ToString()
            };
            await _uow.Repo<AuditLog>().AddAsync(entry);
            await _uow.SaveChangesAsync();
        }
        catch (Exception ex) { _log.LogError(ex, "Failed to write audit log ({Category}/{Action})", category, action); }   // auditing must never break the request
    }
    private static string? Trunc(string? s, int n) => s is { Length: > 0 } && s.Length > n ? s[..n] : s;
}
