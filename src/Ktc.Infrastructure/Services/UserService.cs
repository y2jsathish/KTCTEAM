using Ktc.Application.Abstractions;
using Ktc.Application.Options;
using Ktc.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Ktc.Infrastructure.Services;

public class UserService : IUserService
{
    private readonly IUnitOfWork _uow; private readonly PortalOptions _portal; private readonly KtcOptions _ktc;
    public UserService(IUnitOfWork uow, IOptions<PortalOptions> p, IOptions<KtcOptions> k) { _uow = uow; _portal = p.Value; _ktc = k.Value; }

    public async Task<PortalUser> EnsureUserAsync(string username, CancellationToken ct = default)
    {
        var users = _uow.Repo<User>();
        var u = await users.Query().Include(x => x.Role).FirstOrDefaultAsync(x => x.Username == username, ct);
        var roles = await _uow.Repo<Role>().Query().ToListAsync(ct);
        string want = Has(_portal.AdminUsers, username) ? "Admin" : Has(_portal.OperatorUsers, username) ? "Operator"
            : (_ktc.UseMock && _portal.AdminUsers.Length == 0) ? "Admin" : "Viewer";     // demo mode only: first-run convenience
        if (u == null)
        {
            u = new User { Username = username, DisplayName = username, RoleId = roles.First(r => r.Name == want).Id, LastLoginUtc = DateTime.UtcNow };
            await users.AddAsync(u, ct);
        }
        else
        {
            u.LastLoginUtc = DateTime.UtcNow;
            // Configuration is authoritative for elevated roles; manual DB promotions are kept.
            if (want != "Viewer" && u.Role?.Name != want && (u.Role?.Name == "Viewer" || want == "Admin")) u.RoleId = roles.First(r => r.Name == want).Id;
            users.Update(u);
        }
        await _uow.SaveChangesAsync(ct);
        var roleName = roles.First(r => r.Id == u.RoleId).Name;
        return new PortalUser(u.Id, u.Username, u.DisplayName, u.IsActive ? roleName : "Disabled", u.Theme);
    }

    public async Task SetThemeAsync(string username, string theme, CancellationToken ct = default)
    {
        var u = await _uow.Repo<User>().Query().FirstOrDefaultAsync(x => x.Username == username, ct);
        if (u == null) return;
        u.Theme = theme == "light" ? "light" : "dark"; await _uow.SaveChangesAsync(ct);
    }
    private static bool Has(string[] list, string u) => list.Any(x => x.Equals(u, StringComparison.OrdinalIgnoreCase));
}
