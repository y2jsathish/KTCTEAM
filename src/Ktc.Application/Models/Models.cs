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
    // As KTC's own ticket list shows them.
    /// <summary>KTC's status name (e.g. AutoClosed); Status above is the portal's category of it.</summary>
    public string KtcStatus { get; init; } = "";
    public int? StatusByte { get; init; }
    public string TypeId { get; init; } = "";
    public string TypeName { get; init; } = "";
    public DateTime? OpenedUtc { get; init; }
    public string ErrorId { get; init; } = "";
    public string ErrorCodeTypeId { get; init; } = "";
    public string ErrorCode { get; init; } = "";
    public string ErrorText { get; init; } = "";
    public string? LastChangeBy { get; init; }
    public DateTime? LastChangeUtc { get; init; }
    /// <summary>Last comment, card numbers masked.</summary>
    public string LastComment { get; init; } = "";
    /// <summary>"No SLA" when KTC gives the ticket no SLA time, else due / met / breached.</summary>
    public string SlaSummary { get; init; } = "No SLA";
    public bool HasKtcSla { get; init; }
}

public record TicketComment(string? User, DateTime? Utc, string Text);
public record KtcField(string Name, string Value);

/// <summary>Ticket Details popup: the ticket and the KTC records around it, each also as all the fields KTC returned.</summary>
public record TicketDetails(TicketDto Ticket, IReadOnlyList<KtcField> TicketFields, IReadOnlyList<TicketComment> Comments,
    IReadOnlyList<KtcField>? TicketType, IReadOnlyList<KtcField>? ErrorCodeType, IReadOnlyList<KtcField>? Error,
    IReadOnlyList<IReadOnlyList<KtcField>> Sla, IReadOnlyList<IReadOnlyList<KtcField>> Notifications, IReadOnlyList<TicketDto> Children,
    IReadOnlyList<string> Sources, IReadOnlyList<string> Warnings);

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
    /// <summary>KTC sessionID (one customer or supervisor session can hold several transactions).</summary>
    public string SessionId { get; init; } = "";
    /// <summary>KTC reason name (reasonID looked up), also for successful transactions, e.g. Normal, HostDeclined, HostTimeout.</summary>
    public string Reason { get; init; } = "";
    /// <summary>KTC transaction UUID (for TransactionData/{uuid}/FieldsFromTransaction).</summary>
    public string Uuid { get; init; } = "";
    /// <summary>First and last EJ row ids of the transaction (startEJID / endEJID), for EJData/EJDataForClientBetweenIDs.</summary>
    public string StartEjId { get; init; } = "";
    public string EndEjId { get; init; } = "";
    /// <summary>The ATM's local start time when KTC sends one.</summary>
    public DateTime? LocalTime { get; init; }
}

/// <summary>
/// An ATM's general details from its KTC Client record (name, IP, location, branch, business, regions, ...), ids named through the
/// Business / Branch / Region lists. Values KTC does not hold are listed as not set (Value empty) so the page can say so.
/// </summary>
public record AtmGeneral(string AtmId, string Name, IReadOnlyList<DetailGroup> Groups, double? Latitude, double? Longitude, IReadOnlyList<string> NotSet, IReadOnlyList<string> Warnings);
public record DetailGroup(string Title, IReadOnlyList<DetailField> Fields);

/// <summary>One named value shown in the transaction details popup (PAN-masked).</summary>
public record DetailField(string Name, string Value, string? Id = null);

/// <summary>Notes or items one transaction moved on one device counter (HistoricalCounter/ClientTransactionCounters).</summary>
public record DispenseLine(string Device, string Counter, string Denomination, long Count, long? RunningValue, string Kind);

/// <summary>Everything the transaction details popup shows. Each part has a note when KTC had nothing for it.</summary>
public record TransactionDetails(
    TransactionDto Transaction,
    IReadOnlyList<DetailField> Record,
    IReadOnlyList<DetailField> Session, IReadOnlyList<TransactionDto> SessionTransactions, IReadOnlyList<DetailField> SessionFields,
    IReadOnlyList<DetailField> Fields,
    IReadOnlyList<string> Ej, string? EjSource,
    IReadOnlyList<DispenseLine> Dispense,
    IReadOnlyDictionary<string, string> Missing,
    IReadOnlyList<string> Diagnostics);

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

public record TicketQuery(string? AtmId = null, string? TicketId = null, string? Client = null, DateTime? From = null, DateTime? To = null, int? ClosedDays = null);

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

// ---- Hardware status (per ATM component) ----
public record HardwareProperty(string Name, string Value, string Status, DateTime? UpdatedUtc);

public record HardwareModule(string Id, string Name, string Status, DateTime? UpdatedUtc, IReadOnlyList<HardwareProperty> Properties, IReadOnlyList<string> Errors);

public record AtmHardware(string AtmId, string AtmName, string Status, DateTime? UpdatedUtc, IReadOnlyList<HardwareModule> Modules, IReadOnlyList<string> Warnings);

// ---- Counters (per ATM): KTC application counters and CEN/XFS device counters ----
/// <summary>One application (KTC) counter, e.g. Transactions / Cash withdrawals, for today and since the last counter reset.</summary>
public record ApplicationCounter(string Category, string Name, long Today, long SinceReset);

/// <summary>One CEN/XFS device counter reported by a service provider (IDC, CDM, CIM, PTR, PIN, SIU).</summary>
public record XfsCounter(string Device, string ServiceClass, string? LogicalName, string Counter, long Value, long? Threshold)
{
    /// <summary>"Warning" when the value is at or above its threshold, otherwise "OK".</summary>
    public string Status => Ktc.Application.Logic.CounterRules.Status(Value, Threshold);
}

/// <param name="ResetSource">What "Since reset" counts from, e.g. "last replenishment (REPLENISH) 2026-11-14 06:02 UTC" or "start of the 7-day lookback".</param>
/// <param name="Diagnostics">What KTC returned (rows, components, transactions), so an empty tab says why.</param>
public record AtmCounters(string AtmId, string AtmName, DateTime? CollectedUtc, DateTime? LastResetUtc,
    IReadOnlyList<ApplicationCounter> Application, IReadOnlyList<XfsCounter> Xfs, IReadOnlyList<string> Warnings,
    string? ResetSource = null, IReadOnlyList<string>? Diagnostics = null,
    IReadOnlyList<NotesByType>? NotesByType = null, string? NotesSource = null,
    IReadOnlyList<NotesByDenomination>? NotesByDenomination = null);

/// <summary>Notes moved by one transaction (KTC per-transaction counters).</summary>
/// <remarks>The per-denomination split is keyed by KTC DenominationID ("" = notes KTC did not split by denomination).</remarks>
public record TxNotes(long Dispensed, long Accepted)
{
    public IReadOnlyDictionary<string, long> DispensedByDenomination { get; init; } = new Dictionary<string, long>();
    public IReadOnlyDictionary<string, long> AcceptedByDenomination { get; init; } = new Dictionary<string, long>();
    public virtual bool Equals(TxNotes? o) => o is not null && o.Dispensed == Dispensed && o.Accepted == Accepted;
    public override int GetHashCode() => HashCode.Combine(Dispensed, Accepted);
}

/// <summary>Notes dispensed and accepted since the reset by one transaction type.</summary>
public record NotesByType(string Type, long Transactions, long NotesDispensed, long NotesAccepted)
{
    /// <summary>The same notes split by note value (only the denominations this type moved).</summary>
    public IReadOnlyList<NotesByDenomination>? Denominations { get; init; }
}

/// <summary>
/// Notes dispensed and accepted since the reset for one note value. Denomination is the note value ("100"),
/// "unknown (id 7)" when KTC's Denomination list has no value for the id, or "Not split by denomination".
/// </summary>
public record NotesByDenomination(string Denomination, decimal? Value, string? DenominationId, long NotesDispensed, long NotesAccepted)
{
    public decimal? AmountDispensed => Value * NotesDispensed;
    public decimal? AmountAccepted => Value * NotesAccepted;
}

/// <summary>
/// ATM 360 Replenishment tab: current cash unit levels (KTC CurrentCashUnitStatus), the replenishment sessions found in the
/// transactions (REPLENISH, ADD_CASH, CLR_CASH, ...) with what happened until the next one, and KTC's own replenishment records.
/// </summary>
public record AtmReplenishment(string AtmId, string AtmName, DateTime? LastReplenishmentUtc, int LookbackDays,
    IReadOnlyList<CashUnitLevel> CashUnits, IReadOnlyList<ReplenishmentEvent> History, IReadOnlyList<KtcRecordSet> KtcRecords,
    IReadOnlyList<string> Warnings, IReadOnlyList<string> Diagnostics);

/// <summary>One cash unit (cassette) as KTC reports it now. Value is the note value; Amount = Value x Count.</summary>
public record CashUnitLevel(string Device, string Unit, string Type, string Status, decimal? Value, long Count, DateTime? UpdatedUtc)
{
    public decimal? Amount => Value is > 0 ? Value * Count : null;
}

/// <summary>
/// One replenishment session (the supervisor operations sharing a KTC session id) and the customer activity from it until the next
/// replenishment (or now): transactions, successful withdrawals / deposits and their amounts.
/// </summary>
public record ReplenishmentEvent(DateTime StartUtc, DateTime? NextUtc, string SessionId, IReadOnlyList<string> Operations, bool Success,
    int CustomerTransactions, int Withdrawals, decimal AmountWithdrawn, int Deposits, decimal AmountDeposited);

/// <summary>Records of one KTC route shown as they come (fields flattened, card numbers masked); Error when the route did not answer.</summary>
public record KtcRecordSet(string Title, string Route, string? Error, IReadOnlyList<IReadOnlyList<DetailField>> Records)
{
    /// <summary>What the error likely means and what to do, when it fails.</summary>
    public string? Hint { get; init; }
    /// <summary>The dates sent (JSON body or query), so a failure shows what KTC was given.</summary>
    public string? Sent { get; init; }
    /// <summary>Id fields no KTC list names, so they show the id.</summary>
    public IReadOnlyList<string> UnnamedIds { get; init; } = Array.Empty<string>();
}

/// <summary>
/// One cash counter report the ATM printed to its EJ (on restart): machine number, the report's own date and time (ATM local),
/// the last-cleared time, and one entry per cassette type. EjTimeUtc / EjSequence are the EJ row the report starts in.
/// </summary>
public record EjCounterReport(DateTime EjTimeUtc, string EjSequence, string? MachineNo, DateTime? ReportTime, string? ReportTimeText,
    DateTime? LastCleared, string? LastClearedText, IReadOnlyList<EjCassetteCounter> Types)
{
    public long NotesDispensed => Types.Sum(t => t.Dispensed ?? 0);
    public long NotesInCassettes => Types.Sum(t => t.Cassette ?? 0);
    public decimal AmountInCassettes => Types.Sum(t => (t.Denomination ?? 0) * (t.Cassette ?? 0));
}

/// <summary>
/// One cassette type of an EJ counter report (TYPE1-1000: type 1, note value 1000). Balanced: cassette + rejected + dispensed - deposited = total.
/// Other holds rows the report prints besides the five standard ones (e.g. RETRACTED).
/// </summary>
public record EjCassetteCounter(string Type, decimal? Denomination, long? Cassette, long? Rejected, long? Dispensed, long? Deposited, long? Total,
    IReadOnlyDictionary<string, long> Other)
{
    public bool? Balanced => Total is { } t && Cassette is { } c ? c + (Rejected ?? 0) + (Dispensed ?? 0) - (Deposited ?? 0) == t : null;
}

/// <summary>EJ Counters tab: the counter reports found in the EJ of the range, newest first, and what was read.</summary>
public record AtmEjCounters(string AtmId, string AtmName, DateTime FromUtc, DateTime ToUtc, int EjRows, IReadOnlyList<EjCounterReport> Reports,
    IReadOnlyList<string> Warnings, IReadOnlyList<string> Diagnostics);
