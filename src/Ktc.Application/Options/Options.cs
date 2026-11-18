namespace Ktc.Application.Options;

public class KtcOptions
{
    public const string Section = "Ktc";
    public string BaseUrl { get; set; } = "https://ktc.example.local/";
    public bool UseMock { get; set; }
    public string AppName { get; set; } = "KTC_UI";
    public string DefaultAuthMode { get; set; } = "DOMAIN";
     public string[] AuthModes { get; set; } = { "DOMAIN","INTERNAL" };
    public int DefaultTokenLifetimeMinutes { get; set; } = 20;
    public int TimeoutSeconds { get; set; } = 15;
    public int RetryCount { get; set; } = 3;
    public int BreakerFailures { get; set; } = 5;
    public int BreakerBreakSeconds { get; set; } = 30;
    public bool AcceptAnyServerCertificate { get; set; }
    /// <summary>Account used by background jobs (dashboard cache, alerts, scheduled reports).</summary>
    public ServiceAccountOptions ServiceAccount { get; set; } = new();
    public int AtmOnlineMinutes { get; set; } = 10;
    public int OfflineMinutes { get; set; } = 60;
    public int TicketSearchGroupId { get; set; } = 2;
    /// <summary>KTC group searched for transactions. 0 = the "All Clients" group, looked up by name (group 1 is "Unknown Clients").</summary>
    public int TransactionSearchGroupId { get; set; } = 0;
    public int TicketLookbackDays { get; set; } = 30;
    public int AvailabilityCacheMinutes { get; set; } = 15;
    public int AvailabilityWarmupPerCycle { get; set; } = 150;
    /// <summary>KTC error "level" -> P1..P4. Key = level, value = 1..4.</summary>
    public Dictionary<string, int> SeverityMap { get; set; } = new() { ["1"] = 1, ["2"] = 2, ["3"] = 3, ["4"] = 4 };
    /// <summary>
    /// KTC ticket StatusByte -> KTC's status name as its ticket list shows it (e.g. "4" = "AutoClosed"). Used when KTC sends only the
    /// number. The portal files each name under Open / Assigned / InProgress / Resolved / Closed by its wording. Unmapped numbers show
    /// as "Status N" (closed ones as Closed, or AutoClosed when KTC closed them itself).
    /// </summary>
    public Dictionary<string, string> TicketStatusMap { get; set; } = new();
    /// <summary>Closed tickets older than this many days are left out (KTC's "Number of days of Closed Tickets to show").</summary>
    public int TicketClosedDays { get; set; } = 14;
    /// <summary>Fallback SLA hours per severity when the Ticket SLA endpoint returns nothing.</summary>
    public Dictionary<string, double> DefaultSlaHours { get; set; } = new() { ["P1"] = 4, ["P2"] = 8, ["P3"] = 24, ["P4"] = 72 };
    public int NearBreachMinutes { get; set; } = 60;
    /// <summary>EJ application names to leave out of EJ search (KTC's EJRow Value3). Empty = all.</summary>
    public string[] EjExcludeApps { get; set; } = Array.Empty<string>();
    /// <summary>
    /// KTC route with the device counters of one ATM ({atmId} = KTC client id). KTC's own UI reads GET Client/{id}/Counters
    /// (List of KTCCounter: component DBName, counter name, value), so that is the default. When it gives no counters the XFS
    /// counters come from the counter-like properties in Client/{id}/CurrentStatus instead. Empty turns the route off.
    /// Application counters always come from TransactionData/Search unless this route also returns application counters.
    /// </summary>
    public string CountersEndpoint { get; set; } = "api/v1/Client/{atmId}/Counters";
    /// <summary>Days of transactions searched for the application counters ("Since reset" goes back at most this far).</summary>
    public int CounterLookbackDays { get; set; } = 7;
    /// <summary>
    /// KTC's per-transaction counters (what KTC's UI reads for one transaction), used for "Notes dispensed" and "Notes accepted".
    /// {atmId} and {transactionId} are filled in. Empty turns it off; the totals then come from the dispenser / acceptor counters.
    /// </summary>
    public string TransactionCountersEndpoint { get; set; } = "api/v1/HistoricalCounter/ClientTransactionCounters?clientid={atmId}&transactionid={transactionId}";
    /// <summary>At most this many cash transactions (the latest since the reset) are read per page load; answers are cached.</summary>
    public int TransactionNotesMax { get; set; } = 300;
    /// <summary>
    /// Note value per KTC DenominationID (e.g. "28": 100), used before KTC's Denomination list, for ids KTC has no value for.
    /// Set as Ktc:DenominationValues:28 = 100 (environment: Ktc__DenominationValues__28=100).
    /// </summary>
    public Dictionary<string, decimal> DenominationValues { get; set; } = new();
    /// <summary>EJ counter reports: read DATE-TIME / LAST CLEARED as dd/MM/yy instead of MM/dd/yy (a date in the future always falls back to the other).</summary>
    public bool EjDayFirst { get; set; }
    /// <summary>
    /// XFS counter thresholds: a counter whose name contains the key (case-insensitive) is flagged Warning at or above the value.
    /// The longest matching key wins. KTC sends no thresholds, so these are the portal's.
    /// </summary>
    public Dictionary<string, long> CounterThresholds { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fatal error count"] = 1, ["error count"] = 5, ["jam"] = 3, ["tamper"] = 1, ["fail"] = 50, ["retain"] = 20, ["captur"] = 20,
        ["retract"] = 10, ["reject"] = 200, ["refused"] = 150, ["suspect"] = 5, ["counterfeit"] = 5,
        ["bad reads"] = 50, ["bad chip reads"] = 50, ["print errors"] = 20, ["captured"] = 20
    };
    public int AvailabilityLookbackDays { get; set; } = 7;
    public int MaxParallelCalls { get; set; } = 8;
    /// <summary>Ticket and transaction searches run once per KTC business (KTC requires a BusinessID); at most this many businesses.</summary>
    public int MaxBusinessesPerSearch { get; set; } = 25;
    /// <summary>
    /// Format of dates sent in KTC query strings (start/end). Default: whole seconds with no fraction or offset, e.g.
    /// 2026-11-13T11:48:24. Use "o" for full ISO (2026-11-13T11:48:24.3662432Z) if your KTC server needs it.
    /// </summary>
    public string QueryDateFormat { get; set; } = "yyyyMMddHHmmssfff";
    /// <summary>Send query dates in this time zone instead of UTC (e.g. "Arabian Standard Time" or "Asia/Dubai") when KTC stores local times.</summary>
    public string? QueryTimeZone { get; set; }

    /// <summary>Formats a UTC time for a KTC query string. Set by the infrastructure layer to the format KTC accepted.</summary>
    public Func<DateTime, string>? QueryDateFormatter { get; set; }
    public string QueryDate(DateTime utc) => QueryDateFormatter?.Invoke(utc)
        ?? Uri.EscapeDataString(DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString(string.IsNullOrWhiteSpace(QueryDateFormat) ? "yyyyMMddHHmmssfff" : QueryDateFormat, System.Globalization.CultureInfo.InvariantCulture));
}

public class ServiceAccountOptions
{
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? AuthMode { get; set; }
    public bool Configured => !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password);
}

public class PortalOptions
{
    public const string Section = "Portal";
    public string[] AdminUsers { get; set; } = Array.Empty<string>();
    public string[] OperatorUsers { get; set; } = Array.Empty<string>();
    public string TimeZoneId { get; set; } = "Asia/Dubai";
    public int DashboardRefreshSeconds { get; set; } = 60;
    public bool EnableBackgroundJobs { get; set; } = true;
    public bool SeedPostmanOnStartup { get; set; } = true;
    public string PostmanSeedFile { get; set; } = "Seed/KTC_REST_API.postman_collection.json";
}

public class NotificationOptions
{
    public const string Section = "Notifications";
    public SmtpOptions Smtp { get; set; } = new();
    public string? TeamsWebhookUrl { get; set; }
    public string[] DefaultRecipients { get; set; } = Array.Empty<string>();
    public string PortalBaseUrl { get; set; } = "";
}

public class SmtpOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public bool UseStartTls { get; set; } = true;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string From { get; set; } = "ktc-portal@localhost";
}

/// <summary>Cash Forecasting menu: run-out estimates, replenishment suggestions and the daily cash snapshot. All results are estimates.</summary>
public class CashForecastOptions
{
    public const string Section = "CashForecast";
    /// <summary>Portal-local hour (0-23) after which the daily snapshot is taken (once a day). It also runs at start-up when today has none.</summary>
    public int SnapshotHour { get; set; } = 23;
    /// <summary>Turns the daily snapshot job off (the page can still be recalculated by hand).</summary>
    public bool EnableSnapshotJob { get; set; } = true;
    /// <summary>A cassette counts as out of cash at or below this many notes.</summary>
    public long LowNotes { get; set; } = 50;
    /// <summary>Suggested visit = earliest run-out minus this many days.</summary>
    public double BufferDays { get; set; } = 1;
    /// <summary>A replenishment is suggested only when a cassette runs out within this many days.</summary>
    public int HorizonDays { get; set; } = 14;
    /// <summary>Suggested load covers this many days of use after the visit.</summary>
    public int CoverDays { get; set; } = 7;
    /// <summary>Extra notes on top of the expected use, in percent.</summary>
    public double SafetyStockPct { get; set; } = 20;
    /// <summary>Notes a cassette holds (KTC PhysicalCassettes capacity; 2500 in the KTC data seen so far).</summary>
    public long CassetteCapacity { get; set; } = 2500;
    /// <summary>Day-to-day drops needed before the daily snapshots are used instead of the counters since the last reset.</summary>
    public int MinHistoryDays { get; set; } = 3;
    /// <summary>Days of withdrawals needed before weekday patterns (e.g. busier Fridays) are used.</summary>
    public int MinDaysForWeekdayPattern { get; set; } = 14;
    /// <summary>Reconciliation: two sources are compared only when their reset / report times are this close.</summary>
    public int ReconcileToleranceMinutes { get; set; } = 120;
    /// <summary>Days of EJ read for the latest counter report on the ATM forecast page.</summary>
    public int EjDays { get; set; } = 2;
    /// <summary>ATMs read at the same time when the whole estate is recalculated.</summary>
    public int MaxParallel { get; set; } = 3;
    /// <summary>Daily snapshots older than this are deleted.</summary>
    public int RetentionDays { get; set; } = 400;

    /// <summary>LowNotes as a percent of CassetteCapacity: the default of the low-cash threshold control (50 of 2500 = 2%).</summary>
    public double DefaultLowPct => CassetteCapacity <= 0 ? 0 : Math.Round(LowNotes * 100.0 / CassetteCapacity, 2);
    /// <summary>Notes for a low-cash threshold of <paramref name="pct"/> percent of CassetteCapacity (0-100), rounded up.</summary>
    public long LowNotesFor(double pct) => (long)Math.Ceiling(Math.Clamp(pct, 0, 100) * CassetteCapacity / 100.0);
    /// <summary>A copy with another low-cash threshold (in notes).</summary>
    public CashForecastOptions WithLowNotes(long lowNotes) { var c = (CashForecastOptions)MemberwiseClone(); c.LowNotes = Math.Max(0, lowNotes); return c; }
}

/// <summary>
/// ATM Actions tab. Step 1 is read-only: the portal lists KTC's command types (GET api/v1/CommandType) and shows which KTC command
/// each planned action would use. No command is sent to an ATM yet.
/// </summary>
public class KtcActionsOptions
{
    public const string Section = "KtcActions";
    /// <summary>KTC route with the command types.</summary>
    public string CommandTypesEndpoint { get; set; } = "api/v1/CommandType";
    /// <summary>Minutes the command list is cached (the Refresh button reads it again).</summary>
    public int CacheMinutes { get; set; } = 10;
    /// <summary>
    /// Planned action -> KTC CommandName (case-insensitive), e.g. KtcActions:Commands:Reboot = "REBOOT". Empty = not configured yet;
    /// the tab then only suggests commands whose names look right. Keys: Reboot, XfsReset, DeviceReset, OutOfService, InService.
    /// Defaults are the names on the KTC server (KTC_REBOOT #7, KTC_RESET_DEVICE #9, KTC_SUPERVISOR_ON #12 / OFF #11); XFS reset has
    /// no KTC command there and stays unmapped. Any key can be overridden, or cleared with an empty value.
    /// </summary>
    public Dictionary<string, string?> Commands { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Reboot"] = "KTC_REBOOT", ["DeviceReset"] = "KTC_RESET_DEVICE", ["OutOfService"] = "KTC_SUPERVISOR_ON", ["InService"] = "KTC_SUPERVISOR_OFF",
    };

    /// <summary>
    /// Master switch for sending commands from the generic form (POST Client/{id}/Actions). OFF by default: the form can be filled
    /// and every check runs, but nothing reaches KTC. Turn on with KtcActions:SendEnabled = true (env: KtcActions__SendEnabled=true)
    /// only after the real command list has been reviewed.
    /// </summary>
    public bool SendEnabled { get; set; }
    /// <summary>The ticket number must exist in KTC (GET Ticket/{id}) and, when KTC says which ATM it is for, be for this ATM.</summary>
    public bool VerifyTicketInKtc { get; set; } = true;
    /// <summary>Minimum length of the reason.</summary>
    public int MinReasonLength { get; set; } = 10;
    /// <summary>Commands sent per user per minute.</summary>
    public int MaxSendsPerMinute { get; set; } = 5;
    /// <summary>
    /// Commands (name or description, regex, case-insensitive) that need the extra confirmation: retype the command name and tick
    /// "I understand". Default: Emergency Reboot, Shutdown, Rename File or Folder, Run ATM Script, Trigger Rescue Image.
    /// </summary>
    public string HighRiskPattern { get; set; } = @"emergency.?reboot|shut.?down|rename|run.*script|script.*run|rescue.?image";
    /// <summary>Commands never sent from the generic form (regex on name or description). Empty = none beyond the special flows.</summary>
    public string BlockedPattern { get; set; } = "";
    /// <summary>Shape of the parameter XML sent as Value2: &lt;Parameters&gt;&lt;Parameter Name="x"&gt;value&lt;/Parameter&gt;&lt;/Parameters&gt;.</summary>
    public string ParametersRoot { get; set; } = "Parameters";
    public string ParameterElement { get; set; } = "Parameter";
    public string ParameterNameAttribute { get; set; } = "Name";

    /// <summary>KTC action StatusByte -> name. Placeholder values until confirmed on the real server.</summary>
    public Dictionary<string, string> StatusMap { get; set; } = new() { ["0"] = "Pending", ["1"] = "In progress", ["2"] = "Completed", ["3"] = "Failed", ["4"] = "Cancelled" };
    /// <summary>Status names that are final, and those of them that mean failure (case-insensitive, matched as words).</summary>
    public string FinalStatusPattern { get; set; } = @"complete|success|succeeded|done|fail|error|cancel|abort|expired|timeout|rejected";
    public string FailedStatusPattern { get; set; } = @"fail|error|cancel|abort|expired|timeout|rejected";
    /// <summary>Commands that bring files back from the ATM (regex on name, description or category).</summary>
    public string FileCommandPattern { get; set; } = @"upload|listing|get.?file|journal|ej|log";
    /// <summary>Largest file the portal will check and show (bytes). Bigger files are refused.</summary>
    public long FileMaxBytes { get; set; } = 5 * 1024 * 1024;
    /// <summary>Lines shown in the preview (the download has the whole masked file).</summary>
    public int FilePreviewLines { get; set; } = 400;
    /// <summary>KTC route of one uploaded file; {actionId} and {file} are filled in.</summary>
    public string UploadedFileEndpoint { get; set; } = "api/v1/Action/{actionId}/UploadedFile?filename={file}";
    public RebootWorkflowOptions Reboot { get; set; } = new();
}

/// <summary>Guided reboot: out of service, wait for idle, reboot, wait for the ATM to come back, back in service.</summary>
public class RebootWorkflowOptions
{
    /// <summary>Seconds without a customer transaction before the ATM counts as idle.</summary>
    public int IdleSeconds { get; set; } = 90;
    public int IdleTimeoutMinutes { get; set; } = 15;
    public int OutOfServiceTimeoutMinutes { get; set; } = 5;
    public int RebootTimeoutMinutes { get; set; } = 10;
    /// <summary>The ATM must stop reporting for this long after the reboot was sent to count as offline (or go straight to online when it reports again later).</summary>
    public int OfflineAfterSeconds { get; set; } = 60;
    public int ComeBackTimeoutMinutes { get; set; } = 20;
    public int InServiceTimeoutMinutes { get; set; } = 5;
    /// <summary>Put the ATM back in service after a good reboot (false = leave it out of service for an engineer).</summary>
    public bool AutoInService { get; set; } = true;
    public int TickSeconds { get; set; } = 5;
}

/// <summary>
/// Writing to KTC tickets from the Ticket Details popup (add comment, close). OFF by default: the buttons show, every check runs,
/// nothing is sent. KTC has no "close ticket" call: closing is GET Ticket/{id}, set StatusByte / TimeClosed / ClosureMode, then
/// PUT Ticket/{id}; comments go through POST api/v1/Comment. The numbers below are KTC's and must be confirmed for your server.
/// </summary>
public class TicketWriteOptions
{
    public const string Section = "TicketWrite";
    /// <summary>Master switch (env TicketWrite__Enabled=true).</summary>
    public bool Enabled { get; set; }
    /// <summary>
    /// How comments are written: "CommentApi" (default) uses POST api/v1/Comment/User, the call KTC's Windows client makes. "Ticket" puts
    /// them in the ticket's CommentsString with PUT Ticket/{id}; the bank's server answers that with the comment but does not keep it.
    /// </summary>
    public string CommentVia { get; set; } = "CommentApi";
    /// <summary>POST api/v1/Comment/User "table" for tickets: 6, from KTC's client trace (4 is the Action table).</summary>
    public int CommentTable { get; set; } = 6;
    /// <summary>KTC StatusByte of a closed ticket. Empty = Close stays disabled (we do not guess it).</summary>
    public int? ClosedStatusByte { get; set; }
    /// <summary>KTC ClosureMode / ClosureModeByte for a manual close. Empty = left as KTC has it.</summary>
    public int? ManualClosureMode { get; set; }
    /// <summary>PUT Ticket/{id}?forceupdate= value.</summary>
    public bool ForceUpdate { get; set; }
    /// <summary>When KTC's record still shows the ticket open after the PUT, also call PUT Ticket/{id}/ClosedDate, then check again.</summary>
    public bool UseClosedDateCall { get; set; } = true;
    public int MinCommentLength { get; set; } = 10;
    public int MaxCommentLength { get; set; } = 1000;
}

