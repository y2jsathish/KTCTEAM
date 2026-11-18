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
    /// <summary>Who KTC calls run as right now ("user DOMAIN\\x" or "service account"), for logs and per-account caches.</summary>
    string Identity => "default";
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
    Task<TicketDetails?> GetTicketDetailsAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<TransactionDto>> SearchTransactionsAsync(TransactionQuery query, CancellationToken ct = default);
    Task<TransactionDto?> GetTransactionAsync(string id, CancellationToken ct = default);
    /// <summary>General details of one ATM from its KTC Client record, with branch, business and region names. Null when no ATM matches.</summary>
    Task<AtmGeneral?> GetAtmGeneralAsync(string atmId, CancellationToken ct = default);
    /// <summary>Replenishment tab of one ATM; null when no ATM matches.</summary>
    Task<AtmReplenishment?> GetReplenishmentAsync(string atmId, CancellationToken ct = default);
    /// <summary>Cash units of one ATM now (KTC CurrentCashUnitStatus); Units is null when KTC did not answer or no ATM matches.</summary>
    Task<(IReadOnlyList<CashUnitLevel>? Units, IReadOnlyList<string> Warnings)> GetCashUnitsAsync(string atmId, CancellationToken ct = default);
    /// <summary>Cash counter reports the ATM printed to its EJ (on restart) in the last <paramref name="days"/> days; null when no ATM matches.</summary>
    Task<AtmEjCounters?> GetEjCountersAsync(string atmId, int days, CancellationToken ct = default);
    /// <summary>Transaction details for the popup: the record, session, STX fields, EJ lines and dispense counters. Null when the transaction is not found.</summary>
    Task<TransactionDetails?> GetTransactionDetailsAsync(string id, string? atmId, DateTime? timeUtc, CancellationToken ct = default);
    Task<IReadOnlyList<EjEntryDto>> SearchEjAsync(EjQuery query, CancellationToken ct = default);
    Task<AtmAvailability> GetAvailabilityAsync(string atmId, DateTime from, DateTime to, CancellationToken ct = default);
    /// <summary>Current status of each hardware component (card reader, dispenser, printers, ...) of one ATM.</summary>
    Task<AtmHardware?> GetHardwareAsync(string atmId, CancellationToken ct = default);
    /// <summary>Application and CEN/XFS device counters of one ATM, read from the configurable Ktc:CountersEndpoint. Null when no ATM matches.</summary>
    Task<AtmCounters?> GetCountersAsync(string atmId, CancellationToken ct = default);
}

/// <summary>ATM Actions tab: KTC's command types, and sending one command to one ATM (only when KtcActions:SendEnabled is on).</summary>
public interface IKtcActionService
{
    Task<AtmActionCatalog> GetCatalogAsync(bool refresh, CancellationToken ct = default);
    /// <summary>Runs every check; sends to KTC only when all pass and sending is enabled. Always audited.</summary>
    Task<AtmActionResult> SendAsync(AtmActionRequest request, bool checkOnly, CancellationToken ct = default);
    /// <summary>Sends one mapped command (KtcActions:Commands key) with its default parameters. Used by the guided reboot only.</summary>
    Task<(bool Ok, string? ActionId, string? Error)> SendMappedAsync(string atmId, string commandKey, string comment, CancellationToken ct = default);
    /// <summary>The ATM's actions of the last <paramref name="days"/> days (GET Client/{id}/Actions?numdays=) and its guided reboots.</summary>
    Task<AtmActionHistory> GetHistoryAsync(string atmId, int days, CancellationToken ct = default);
    Task<ActionRecord?> GetActionAsync(string actionId, CancellationToken ct = default);
    /// <summary>Files an upload-type action brought back (GET Action/{id}/UploadedDirectoryListing).</summary>
    Task<(IReadOnlyList<ActionFile> Files, string? Error)> GetFilesAsync(string actionId, CancellationToken ct = default);
    /// <summary>One uploaded file after the PCI rules (text only, PAN-masked, track data removed). Audited.</summary>
    Task<(ActionFileContent? File, string? Error)> GetFileAsync(string actionId, string name, bool preview, CancellationToken ct = default);
}

/// <summary>Guided reboot runs: start (with the same checks as the form), list, cancel. A background job moves them on.</summary>
public interface IRebootService
{
    Task<(RebootRun? Run, AtmActionResult Result)> StartAsync(RebootRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<RebootRun>> ListAsync(string? atmId, bool runningOnly, CancellationToken ct = default);
    Task<RebootRun?> GetAsync(string id, CancellationToken ct = default);
    Task<RebootRun?> CancelAsync(string id, CancellationToken ct = default);
    Task SaveAsync(RebootRun run, CancellationToken ct = default);
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

/// <summary>Cash Forecasting menu (phase 1). Results are estimates, except the counts and the daily cash history.</summary>
public interface ICashForecastService
{
    /// <summary>
    /// The latest stored estate forecast (computed when none exists yet). <paramref name="lowPct"/> = low-cash threshold as a percent of
    /// cassette capacity; null keeps CashForecast:LowNotes. Status, run-out and suggestions are recalculated for it.
    /// </summary>
    Task<EstateCashForecast> GetEstateAsync(double? lowPct = null, CancellationToken ct = default);
    /// <summary>Reads every ATM's cash units and the transactions, stores the daily snapshot and daily cash history, and stores the estate forecast.</summary>
    Task<EstateCashForecast> RunAsync(string trigger, CancellationToken ct = default);
    /// <summary>One ATM, live: forecast (with notes per denomination since the reset), reconciliation XFS / EJ / application counters, history.</summary>
    Task<AtmCashForecastDetail?> GetAtmAsync(string atmId, double? lowPct = null, CancellationToken ct = default);
    /// <summary>Actual cash withdrawn / deposited per day across the estate from the stored history (last <paramref name="days"/> days).</summary>
    Task<IReadOnlyList<CashDailyPoint>> GetDailyAsync(int days, CancellationToken ct = default);
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

/// <summary>Disputes menu (read-only): KTC DisputeManagement search, details, states and rules.</summary>
public interface IKtcDisputeService
{
    Task<DisputeSearchResult> SearchAsync(DisputeQuery query, CancellationToken ct = default);
    /// <param name="atmId">The dispute's ATM (KTC client id) when known: the dispute is then read from that ATM's ClientDisputes list.</param>
    Task<DisputeDto?> GetAsync(string id, string? atmId = null, CancellationToken ct = default);
    Task<DisputeDetails?> GetDetailsAsync(string id, string? atmId = null, CancellationToken ct = default);
    Task<IReadOnlyList<DisputeStateDto>> GetStatesAsync(CancellationToken ct = default);
    /// <summary>The dispute and its comments straight from KTC (not cached), for read-back after a write.</summary>
    Task<(DisputeDto? Dispute, IReadOnlyList<TicketComment> Comments)> ReadCommentsAsync(string id, string? atmId, CancellationToken ct = default);
    Task<DisputeSetup> GetSetupAsync(CancellationToken ct = default);
    /// <summary>Drops cached dispute lists so a change shows at once.</summary>
    Task<DisputeRawSample> GetRawSampleAsync(DisputeQuery q, int max = 3, CancellationToken ct = default);
    void Invalidate();
}

/// <summary>Change state / close on KTC disputes (behind DisputeWrite:Enabled), audited, verified by reading the dispute back.</summary>
public interface IDisputeWriteService
{
    Task<DisputeWriteState> StateAsync(string disputeId, string? atmId = null, CancellationToken ct = default);
    Task<DisputeWriteResult> AddCommentAsync(string disputeId, DisputeWriteRequest q, CancellationToken ct = default);
    Task<DisputeWriteResult> ChangeStateAsync(string disputeId, DisputeWriteRequest q, CancellationToken ct = default);
    Task<DisputeWriteResult> CloseAsync(string disputeId, DisputeWriteRequest q, CancellationToken ct = default);
}

/// <summary>Add comment / close on KTC tickets (behind TicketWrite:Enabled), audited.</summary>
public interface ITicketWriteService
{
    Task<TicketWriteState> StateAsync(string ticketId, CancellationToken ct = default);
    Task<TicketWriteResult> AddCommentAsync(string ticketId, TicketWriteRequest q, CancellationToken ct = default);
    Task<TicketWriteResult> CloseAsync(string ticketId, TicketWriteRequest q, CancellationToken ct = default);
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
