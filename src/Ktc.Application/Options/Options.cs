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
    /// <summary>Ticket StatusByte -> TicketStatus name (Open/Assigned/InProgress/Resolved/Closed).</summary>
    public Dictionary<string, string> TicketStatusMap { get; set; } = new();
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
}
