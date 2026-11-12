using Ktc.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Ktc.Infrastructure.Data;

public class KtcDbContext : DbContext
{
    public KtcDbContext(DbContextOptions<KtcDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<DashboardCache> DashboardCaches => Set<DashboardCache>();
    public DbSet<SavedSearch> SavedSearches => Set<SavedSearch>();
    public DbSet<FavoriteAtm> FavoriteAtms => Set<FavoriteAtm>();
    public DbSet<AlertRule> AlertRules => Set<AlertRule>();
    public DbSet<AlertHistory> AlertHistories => Set<AlertHistory>();
    public DbSet<ReportSchedule> ReportSchedules => Set<ReportSchedule>();
    public DbSet<ApiModule> ApiModules => Set<ApiModule>();
    public DbSet<ApiEndpoint> ApiEndpoints => Set<ApiEndpoint>();
    public DbSet<ApiMapping> ApiMappings => Set<ApiMapping>();
    public DbSet<ApiFieldMapping> ApiFieldMappings => Set<ApiFieldMapping>();
    public DbSet<IncidentAction> IncidentActions => Set<IncidentAction>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Role>(e => { e.HasIndex(x => x.Name).IsUnique(); e.Property(x => x.Name).HasMaxLength(50); });
        b.Entity<User>(e =>
        {
            e.HasIndex(x => x.Username).IsUnique();
            e.Property(x => x.Username).HasMaxLength(200);
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.Theme).HasMaxLength(20);
            e.HasOne(x => x.Role).WithMany(r => r.Users).HasForeignKey(x => x.RoleId);
        });
        b.Entity<AuditLog>(e =>
        {
            e.HasIndex(x => x.TimestampUtc); e.HasIndex(x => new { x.Category, x.TimestampUtc }); e.HasIndex(x => x.UserName);
            e.Property(x => x.UserName).HasMaxLength(200); e.Property(x => x.Category).HasMaxLength(50);
            e.Property(x => x.Action).HasMaxLength(200); e.Property(x => x.Target).HasMaxLength(400); e.Property(x => x.IpAddress).HasMaxLength(64);
            e.Property(x => x.Details).HasMaxLength(4000);
        });
        b.Entity<DashboardCache>(e => { e.HasIndex(x => x.Key).IsUnique(); e.Property(x => x.Key).HasMaxLength(200); });
        b.Entity<SavedSearch>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200); e.Property(x => x.SearchType).HasMaxLength(50);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<FavoriteAtm>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.AtmId }).IsUnique(); e.Property(x => x.AtmId).HasMaxLength(100);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<AlertRule>(e => { e.Property(x => x.Name).HasMaxLength(200); e.Property(x => x.Recipients).HasMaxLength(1000); });
        b.Entity<AlertHistory>(e =>
        {
            e.HasIndex(x => x.RaisedUtc); e.HasIndex(x => new { x.RuleType, x.SourceKey, x.RaisedUtc });
            e.Property(x => x.Title).HasMaxLength(300); e.Property(x => x.SourceKey).HasMaxLength(200);
            e.HasOne(x => x.Rule).WithMany().HasForeignKey(x => x.RuleId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<ReportSchedule>(e => { e.Property(x => x.Name).HasMaxLength(200); e.Property(x => x.Recipients).HasMaxLength(1000); });
        b.Entity<ApiModule>(e => { e.HasIndex(x => x.ModuleName).IsUnique(); e.Property(x => x.ModuleName).HasMaxLength(100); e.Property(x => x.DisplayName).HasMaxLength(200); e.Property(x => x.Icon).HasMaxLength(60); e.Property(x => x.ScreenName).HasMaxLength(200); });
        b.Entity<ApiEndpoint>(e =>
        {
            e.HasIndex(x => new { x.HttpMethod, x.RelativeUrl }).IsUnique();
            e.Property(x => x.HttpMethod).HasMaxLength(10); e.Property(x => x.RelativeUrl).HasMaxLength(450); e.Property(x => x.EndpointName).HasMaxLength(500);
            e.HasOne(x => x.Module).WithMany(m => m.Endpoints).HasForeignKey(x => x.ModuleId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ApiMapping>(e =>
        {
            e.Property(x => x.ScreenName).HasMaxLength(200);
            e.HasOne(x => x.ApiEndpoint).WithMany(x => x.Mappings).HasForeignKey(x => x.ApiEndpointId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ApiFieldMapping>(e =>
        {
            e.Property(x => x.FieldPath).HasMaxLength(300); e.Property(x => x.Label).HasMaxLength(200); e.Property(x => x.Aggregation).HasMaxLength(20);
            e.HasOne(x => x.ApiMapping).WithMany(x => x.Fields).HasForeignKey(x => x.ApiMappingId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<IncidentAction>(e => { e.HasIndex(x => x.IncidentKey); e.Property(x => x.IncidentKey).HasMaxLength(100); e.Property(x => x.UserName).HasMaxLength(200); e.Property(x => x.Value).HasMaxLength(2000); });
    }
}
