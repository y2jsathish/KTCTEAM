using Ktc.Domain.Enums;

namespace Ktc.Application.Models;

public record AtmDto
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string DeviceStatus { get; init; } = "Unknown";
    public HealthStatus Health { get; init; }
    public int HealthScore { get; init; }
    public DateTime? LastCommunicationUtc { get; init; }
    public string Location { get; init; } = "";
    public string City { get; init; } = "";
    public string Region { get; init; } = "";
    public string Client { get; init; } = "";
    public string Branch { get; init; } = "";
    public string AtmType { get; init; } = "";
    public double? AvailabilityPct { get; init; }
    public int OpenIncidents { get; init; }
    public int OpenTickets { get; init; }
    public bool HostDisconnected { get; init; }
    public bool Offline { get; init; }
    public string HealthReason { get; init; } = "";
}

public record IncidentDto
{
    public string Key { get; init; } = "";
    public string Number { get; init; } = "";
    public string AtmId { get; init; } = "";
    public string AtmName { get; init; } = "";
    public string Client { get; init; } = "";
    public Severity Severity { get; init; }
    public DateTime OpenedUtc { get; init; }
    public DateTime? ClosedUtc { get; init; }
    public DateTime SlaDueUtc { get; init; }
    public SlaState Sla { get; init; }
    public string Status { get; init; } = "Open";
    public string ErrorCode { get; init; } = "";
    public string Description { get; init; } = "";
    public string? AssignedTo { get; init; }
    public bool IsOpen => ClosedUtc == null;
    public bool HostRelated { get; init; }
}

public record TicketDto
{
    public string Id { get; init; } = "";
    public string AtmId { get; init; } = "";
    public string AtmName { get; init; } = "";
    public string Client { get; init; } = "";
    public TicketStatus Status { get; init; }
    public Severity Severity { get; init; }
    public DateTime CreatedUtc { get; init; }
    public DateTime? UpdatedUtc { get; init; }
    public DateTime? ClosedUtc { get; init; }
    public string? Owner { get; init; }
    public string Description { get; init; } = "";
    public DateTime SlaDueUtc { get; init; }
    public SlaState Sla { get; init; }
    public bool IsOpen => Status != TicketStatus.Closed && Status != TicketStatus.Resolved;
}

public record TransactionDto
{
    public string Id { get; init; } = "";
    public string AtmId { get; init; } = "";
    public string AtmName { get; init; } = "";
    public string Client { get; init; } = "";
    public string Region { get; init; } = "";
    public string Type { get; init; } = "";
    public bool Success { get; init; }
    public string Status => Success ? "Success" : "Failed";
    public decimal? Amount { get; init; }
    public string? Currency { get; init; }
    public DateTime TimeUtc { get; init; }
    public int ResponseMs { get; init; }
    public string ErrorCode { get; init; } = "";
    public string FailureCategory { get; init; } = "";     // Host | Communication | CashRetract | Dispense | Other
}

/// <summary>Text is always PAN-masked; raw journal text never leaves the data service.</summary>
public record EjEntryDto(string Sequence, DateTime TimeUtc, string Text, int TransactionNo);

public record TimelineStep(DateTime TimeUtc, string Label, string Detail, string Level);

public record AtmAvailability(double AvailabilityPct, IReadOnlyList<(string Reason, double Hours)> TopUnavailableReasons);

public record ClientSummary(string Name, int Atms, int Online, double AvailabilityPct, int OpenIncidents, int OpenTickets, double SlaPct, int Critical);

public record ChartSeries(string Label, IReadOnlyList<string> Labels, IReadOnlyList<double> Values);

public record DashboardSnapshot
{
    public DateTime GeneratedUtc { get; init; } = DateTime.UtcNow;
    public int TotalAtms { get; init; }
    public int OnlineAtms { get; init; }
    public int OfflineAtms { get; init; }
    public int WarningAtms { get; init; }
    public int CriticalAtms { get; init; }
    public int TotalTickets { get; init; }
    public int OpenTickets { get; init; }
    public int CriticalIncidents { get; init; }
    public int TransactionsToday { get; init; }
    public int FailedTransactions { get; init; }
    public int HighPriorityAlerts { get; init; }
    public int RecentTxCount { get; init; }
    public double RecentFailureRatePct { get; init; }
    public int RecurringFaultAtms { get; init; }
    public double AvailabilityPct { get; init; }
    public double SlaPct { get; init; }
    public Dictionary<string, int> TicketsByStatus { get; init; } = new();
    public Dictionary<string, int> TicketsBySeverity { get; init; } = new();
    public Dictionary<string, int> IncidentsBySeverity { get; init; } = new();
    public Dictionary<string, int> AtmHealth { get; init; } = new();
    public List<ChartPoint> IncidentTrend { get; init; } = new();
    public List<ChartPoint> TransactionVolume { get; init; } = new();
    public List<ChartPoint> TransactionFailures { get; init; } = new();
    public Dictionary<string, int> FailureByCategory { get; init; } = new();
    public List<ClientSummary> Clients { get; init; } = new();
    public List<AtmDto> Atms { get; init; } = new();
    public List<IncidentDto> Incidents { get; init; } = new();
    public List<TicketDto> Tickets { get; init; } = new();
    public List<string> Warnings { get; init; } = new();   // partial-failure messages from the KTC API
}

public record ChartPoint(string Label, int Value);


public record AtmFilter(string? Client = null, string? City = null, string? Region = null, string? Status = null);

public record TicketQuery(string? AtmId = null, string? TicketId = null, string? Client = null, DateTime? From = null, DateTime? To = null);

public record TransactionQuery(string? AtmId = null, string? Client = null, string? Region = null, DateTime? From = null, DateTime? To = null, bool? Success = null);

public record EjQuery(string AtmId, DateTime From, DateTime To, string? CardNumber = null, string? Sequence = null, string? Text = null);

public record AtmDetail(AtmDto Atm, IReadOnlyList<IncidentDto> Incidents, IReadOnlyList<TicketDto> Tickets,
    IReadOnlyList<TransactionDto> Transactions, IReadOnlyList<EjEntryDto> Ej, AtmAvailability Availability,
    IReadOnlyList<string> Warnings);

public record RecurringFailure(string AtmId, string AtmName, string Client, string ErrorCode, string Description, int Occurrences, DateTime LastSeenUtc, double SharePct);
public record RcaResult(IReadOnlyList<RecurringFailure> Recurring, IReadOnlyList<ChartPoint> TopErrorCodes, IReadOnlyList<ChartPoint> TopAtms, int Days);

public record GlobalSearchResult(string Type, string Id, string Title, string Subtitle, string Url);

public record ReportData(string Title, DateTime GeneratedUtc, string? Subtitle, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows,
    IReadOnlyList<(string Label, string Value)> Summary);

public record KtcLoginResult(bool Success, string? Token, DateTime? ExpiresUtc, string? Error);

public record KtcResponse(bool Success, int StatusCode, System.Text.Json.JsonElement? Json, string? Raw, string? Error, long ElapsedMs)
{
    public static KtcResponse Fail(string error, int status = 0, long ms = 0) => new(false, status, null, null, error, ms);
}

public record ApiHealthRow(string Endpoint, string Method, int Calls, int Failures, int Timeouts, double SuccessRatePct, double AvgMs, double P95Ms, long LastMs,
    int ConsecutiveFailures, DateTime? LastCallUtc, string? LastError, string Color);

public record ApiExecutionResult(bool Success, int StatusCode, string? Error, string? Body, long ElapsedMs, bool FromCache, DateTime ExecutedUtc);

public record ImportedEndpoint(string Module, string Name, string Method, string RelativeUrl, string? RequestBody, string? Description);
public record ImportSummary(int Modules, int Endpoints, int Added, int Updated, int Enabled, int DisabledMutating, int SensitiveBlocked, int Widgets);
