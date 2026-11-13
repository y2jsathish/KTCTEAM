using System.Linq.Expressions;
using Ktc.Application.Models;
using Ktc.Domain.Entities;
using Ktc.Domain.Enums;

namespace Ktc.Application.Abstractions;

// ---- persistence (Repository + Unit of Work) ----
public interface IRepository<T> where T : Entity
{
    IQueryable<T> Query();
    Task<T?> GetAsync(int id, CancellationToken ct = default);
    Task<List<T>> ListAsync(Expression<Func<T, bool>>? predicate = null, CancellationToken ct = default);
    Task AddAsync(T entity, CancellationToken ct = default);
    void Update(T entity);
    void Remove(T entity);
}
public interface IUnitOfWork
{
    IRepository<T> Repo<T>() where T : Entity;
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}

// ---- KTC API ----
public class KtcCallOptions
{
    public bool Idempotent { get; set; }        // allows retry for POST searches
    public bool BypassAuth { get; set; }        // login calls
    public string? Token { get; set; }          // explicit token override
    public TimeSpan? CacheFor { get; set; }
}

public interface IKtcApiClient
{
    Task<KtcResponse> SendAsync(HttpMethod method, string relativeUrl, object? body = null, KtcCallOptions? options = null, CancellationToken ct = default);
    Task<KtcResponse> GetAsync(string relativeUrl, CancellationToken ct = default) => SendAsync(HttpMethod.Get, relativeUrl, null, null, ct);
}

public interface IKtcTokenProvider
{
    /// <summary>Token for the current user (interactive) or service account (background). Null if unauthenticated.</summary>
    Task<string?> GetTokenAsync(CancellationToken ct = default);
    /// <summary>Force a refresh (Relogin).</summary>
    Task<string?> RefreshAsync(CancellationToken ct = default);
    /// <summary>Refresh after KTC rejected <paramref name="failedToken"/> with 401. Returns the current token without logging in again when another call already replaced it.</summary>
    Task<string?> RefreshAsync(string? failedToken, CancellationToken ct = default) => RefreshAsync(ct);
}

public interface IKtcAuthService
{
    Task<KtcLoginResult> LoginAsync(string username, string password, string? authMode, string? appName = null, CancellationToken ct = default);
    Task<KtcLoginResult> ReLoginAsync(string username, string password, string? authMode, string? appName = null, CancellationToken ct = default);
    /// <summary>Relogin with the current token sent as Bearer, as KTC expects for refreshing an existing session.</summary>
    Task<KtcLoginResult> ReLoginAsync(string username, string password, string? authMode, string? appName, string? currentToken, CancellationToken ct = default) => ReLoginAsync(username, password, authMode, appName, ct);
    Task LogoffAsync(string token, CancellationToken ct = default);
}

public interface IKtcDataService
{
    Task<(IReadOnlyList<AtmDto> Atms, List<string> Warnings)> GetAtmsAsync(CancellationToken ct = default);
    Task<AtmDto?> GetAtmAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<IncidentDto>> GetIncidentsAsync(bool openOnly, CancellationToken ct = default);
    Task<IReadOnlyList<IncidentDto>> GetHistoricalIncidentsAsync(DateTime from, DateTime to, string? atmId = null, CancellationToken ct = default);
    Task<IReadOnlyList<TicketDto>> SearchTicketsAsync(TicketQuery query, CancellationToken ct = default);
    Task<TicketDto?> GetTicketAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<TransactionDto>> SearchTransactionsAsync(TransactionQuery query, CancellationToken ct = default);
    Task<TransactionDto?> GetTransactionAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<EjEntryDto>> SearchEjAsync(EjQuery query, CancellationToken ct = default);
    Task<AtmAvailability> GetAvailabilityAsync(string atmId, DateTime from, DateTime to, CancellationToken ct = default);
}

// ---- Services ----
public interface IDashboardService
{
    Task<DashboardSnapshot> GetSnapshotAsync(bool forceRefresh = false, CancellationToken ct = default);
    DashboardSnapshot? Latest { get; }
    Task<IReadOnlyList<AtmDto>> GetAtmsAsync(AtmFilter filter, CancellationToken ct = default);
    Task<AtmDetail?> GetAtmDetailAsync(string atmId, CancellationToken ct = default);
    Task<RcaResult> GetRootCauseAsync(int days, CancellationToken ct = default);
    Task<IReadOnlyList<GlobalSearchResult>> GlobalSearchAsync(string term, CancellationToken ct = default);
}

public interface IAlertEngine
{
    Task<int> EvaluateAsync(DashboardSnapshot snapshot, CancellationToken ct = default);
    Task RaiseManualAsync(AlertRuleType type, Severity severity, string title, string message, string? sourceKey, CancellationToken ct = default);
}

public interface INotificationSender
{
    Task<string> SendAsync(AlertChannels channels, string subject, string body, IEnumerable<string>? recipients, CancellationToken ct = default);
    Task SendEmailAsync(IEnumerable<string> to, string subject, string htmlBody, (string Name, byte[] Data, string MimeType)? attachment = null, CancellationToken ct = default);
}

public interface IRealtimeNotifier
{
    Task DashboardUpdatedAsync(DashboardSnapshot snapshot);
    Task AlertRaisedAsync(AlertHistory alert);
    Task ApiHealthChangedAsync(IReadOnlyList<ApiHealthRow> rows);
}

public interface IReportService
{
    Task<ReportData> BuildAsync(ReportType type, DateTime from, DateTime to, CancellationToken ct = default);
    Task<(byte[] Data, string FileName, string MimeType)> ExportAsync(ReportData data, ExportFormat format);
    Task<int> RunDueSchedulesAsync(CancellationToken ct = default);
}

public interface IExportService
{
    byte[] ToExcel(ReportData data);
    byte[] ToPdf(ReportData data);
    byte[] ToCsv(ReportData data);
}

public interface IAuditService
{
    Task LogAsync(string category, string action, string? target = null, string? details = null, bool success = true, string? userOverride = null);
}

public interface IApiHealthTracker
{
    void Record(string method, string url, int statusCode, long elapsedMs, bool timeout, string? error);
    IReadOnlyList<ApiHealthRow> Snapshot();
}

public interface IApiRegistryService
{
    Task<ImportSummary> ImportPostmanAsync(Stream postmanJson, CancellationToken ct = default);
    Task EnsureSeededAsync(CancellationToken ct = default);
}

public interface IApiExecutionService
{
    Task<ApiExecutionResult> ExecuteAsync(int endpointId, IDictionary<string, string>? parameters, string? body, bool useCache, bool allowMutating, CancellationToken ct = default);
    Task<(byte[] Data, string FileName, string MimeType)> ExportAsync(int endpointId, IDictionary<string, string>? parameters, ExportFormat format, CancellationToken ct = default);
    Task<int> RunScheduledMappingsAsync(CancellationToken ct = default);
}

public record PortalUser(int Id, string Username, string? DisplayName, string Role, string Theme);
public interface IUserService
{
    Task<PortalUser> EnsureUserAsync(string username, CancellationToken ct = default);
    Task SetThemeAsync(string username, string theme, CancellationToken ct = default);
}
