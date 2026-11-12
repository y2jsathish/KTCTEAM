using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ktc.Infrastructure.Data;

/// <summary>Used only by `dotnet ef` so migrations are always generated for SQL Server.</summary>
public class DesignTimeFactory : IDesignTimeDbContextFactory<KtcDbContext>
{
    public KtcDbContext CreateDbContext(string[] args)
    {
        var o = new DbContextOptionsBuilder<KtcDbContext>()
            .UseSqlServer(Environment.GetEnvironmentVariable("KTC_DESIGN_CONN") ?? "Server=.;Database=KtcMonitoringPortal;Trusted_Connection=True;TrustServerCertificate=True").Options;
        return new KtcDbContext(o);
    }
}
