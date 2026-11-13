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
