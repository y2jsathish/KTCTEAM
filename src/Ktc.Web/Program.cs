using System.Threading.RateLimiting;
using Ktc.Application.Abstractions;
using Ktc.Infrastructure;
using Ktc.Infrastructure.Data;
using Ktc.Web.Hubs;
using Ktc.Web.Infrastructure;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog((ctx, lc) =>
{
    lc.ReadFrom.Configuration(ctx.Configuration).Enrich.FromLogContext().Enrich.WithProperty("App", "KtcMonitoringPortal");
    // Optional: persist logs to SQL Server (set Serilog:SqlServerSink=true)
    if (ctx.Configuration.GetValue<bool>("Serilog:SqlServerSink"))
        lc.WriteTo.MSSqlServer(ctx.Configuration.GetConnectionString("KtcPortal"), new Serilog.Sinks.MSSqlServer.MSSqlServerSinkOptions { TableName = "ApplicationLogs", AutoCreateSqlTable = true }, restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Warning);
});

var dp = builder.Services.AddDataProtection().SetApplicationName("KtcMonitoringPortal");
var keys = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(keys)) dp.PersistKeysToFileSystem(new DirectoryInfo(keys));     // required for multi-node / restarts

builder.Services.AddKtcInfrastructure(builder.Configuration);
builder.Services.AddSingleton<IRealtimeNotifier, SignalRNotifier>();          // overrides the null notifier

builder.Services.AddDistributedMemoryCache();                                 // swap for Redis/SQL when scaling out
builder.Services.AddSession(o => { o.Cookie.Name = ".ktc.sid"; o.Cookie.HttpOnly = true; o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; o.Cookie.SameSite = SameSiteMode.Lax; o.IdleTimeout = TimeSpan.FromHours(8); o.Cookie.IsEssential = true; });
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.Cookie.Name = ".ktc.auth"; o.Cookie.HttpOnly = true; o.Cookie.SameSite = SameSiteMode.Lax;
    o.LoginPath = "/Account/Login"; o.AccessDeniedPath = "/Account/Denied"; o.ExpireTimeSpan = TimeSpan.FromHours(8); o.SlidingExpiration = true;
    o.Events.OnValidatePrincipal = CookieEvents.ValidateAsync;
    o.Events.OnRedirectToLogin = CookieEvents.ApiAware401;
});
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy(Policies.Operator, p => p.RequireRole(Roles.Admin, Roles.Operator));
    o.AddPolicy(Policies.Admin, p => p.RequireRole(Roles.Admin));
    o.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
});
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "x", _ => new FixedWindowRateLimiterOptions { PermitLimit = 8, Window = TimeSpan.FromMinutes(1) }));
});
builder.Services.AddControllersWithViews(o => o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()))
    .AddJsonOptions(o => { o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase; o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()); });
builder.Services.AddSignalR();
builder.Services.AddResponseCompression();
builder.Services.AddHealthChecks().AddDbContextCheck<KtcDbContext>("database");

var app = builder.Build();

if (!app.Environment.IsDevelopment()) { app.UseExceptionHandler("/Home/Error"); app.UseHsts(); app.UseHttpsRedirection(); }
app.UseResponseCompression();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseSerilogRequestLogging(o => o.GetLevel = (c, _, ex) => ex != null || c.Response.StatusCode >= 500 ? Serilog.Events.LogEventLevel.Error : c.Request.Path.StartsWithSegments("/data") ? Serilog.Events.LogEventLevel.Debug : Serilog.Events.LogEventLevel.Information);
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseSession();
app.UseAuthentication();
app.UseMiddleware<CorrelationMiddleware>();
app.UseAuthorization();
app.UseMiddleware<ApiExceptionMiddleware>();

app.MapHealthChecks("/health").AllowAnonymous();
app.MapHub<PortalHub>("/hubs/portal");
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");

await DbSeeder.InitializeAsync(app.Services);
app.Run();

public partial class Program { }
