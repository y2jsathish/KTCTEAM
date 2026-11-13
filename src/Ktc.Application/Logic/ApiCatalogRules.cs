using System.Text.RegularExpressions;
using Ktc.Domain.Enums;

namespace Ktc.Application.Logic;

public record KnownModule(string DisplayName, string Icon, string Screen, string Description, int Sort);
public record CuratedMapping(string Method, string UrlStartsWith, string Screen, WidgetType Widget, int RefreshSeconds, ExecutionMode Mode);

/// <summary>Auto-discovery rules: classifies Postman requests and decides defaults. Pure and unit-tested.</summary>
public static class ApiCatalogRules
{
    public static readonly string[] AuthModules =
        { "Login", "Relogin", "Logoff", "AuthMode", "AuthModes", "LoginMethods", "ExternalLogin", "ForceLogOff", "ChangePassword",
          "CheckPassword", "RequestPasswordResetEmail", "ChangeTokenGenerationMethodAndLogOffUsers" };

    /// <summary>Business-facing module catalog. Keys are KTC controller names. Anything else is auto-generated.</summary>
    public static readonly Dictionary<string, KnownModule> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Login"] = new("Authentication", "bi-shield-lock", "Login", "User authentication & token management", 1),
        ["Relogin"] = new("Authentication", "bi-shield-lock", "Login", "Token refresh", 1),
        ["Logoff"] = new("Authentication", "bi-shield-lock", "Login", "Session termination", 1),
        ["Client"] = new("Device Monitoring", "bi-hdd-network", "ATM Monitoring Dashboard", "ATMs / devices: status, health, availability, EJ", 2),
        ["LastClientContact"] = new("Device Monitoring", "bi-hdd-network", "ATM Monitoring Dashboard", "Last communication per ATM", 2),
        ["CurrentStatus"] = new("Device Monitoring", "bi-hdd-network", "ATM Monitoring Dashboard", "Current component status", 2),
        ["ActiveError"] = new("Incident Management", "bi-exclamation-triangle", "Incident Command Center", "Active faults (incidents)", 3),
        ["ActiveErrorExtra"] = new("Incident Management", "bi-exclamation-triangle", "Incident Command Center", "Active faults with extra detail", 3),
        ["HistoricalError"] = new("Incident Management", "bi-exclamation-triangle", "Incident Command Center", "Historical faults / recurrence analysis", 3),
        ["ErrorCodeType"] = new("Incident Management", "bi-exclamation-triangle", "Incident Command Center", "Fault code catalogue", 3),
        ["Ticket"] = new("Ticket Management", "bi-ticket-detailed", "Ticket Workbench", "Service-desk tickets and SLAs", 4),
        ["SLAType"] = new("Ticket Management", "bi-ticket-detailed", "Ticket Workbench", "SLA definitions", 4),
        ["TransactionData"] = new("Transaction Monitoring", "bi-credit-card-2-front", "Transaction Dashboard", "Withdrawals, deposits, reversals, failures", 5),
        ["TransactionVolume"] = new("Transaction Monitoring", "bi-credit-card-2-front", "Transaction Dashboard", "Transaction volumes", 5),
        ["EJData"] = new("EJ Monitoring", "bi-journal-text", "EJ Search Portal", "Electronic journal", 6),
        ["Business"] = new("Client Management", "bi-building", "Client Dashboard", "Customer organisations (banks) & ATM ownership", 7),
        ["Branch"] = new("Client Management", "bi-building", "Client Dashboard", "Branches / sites", 7),
        ["Region"] = new("Client Management", "bi-building", "Client Dashboard", "Regions", 7),
    };

    /// <summary>
    /// Query string to use when a route is called without one but KTC needs it (KTC answers 500 otherwise), e.g. a scheduled widget.
    /// HistoricalError without start/end fails on the server; default to the last 24 hours.
    /// </summary>
    public static string? DefaultQuery(string relativeUrl, DateTime nowUtc)
    {
        if (relativeUrl.Contains('?')) return null;
        var path = relativeUrl.TrimStart('/');
        if (path.Equals("api/v1/HistoricalError", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(path, @"^api/v1/Client/[^/]+/HistoricalError$", RegexOptions.IgnoreCase))
            return $"start={Uri.EscapeDataString(nowUtc.AddHours(-24).ToString("o"))}&end={Uri.EscapeDataString(nowUtc.ToString("o"))}";
        return null;
    }

    public static readonly CuratedMapping[] Curated =
    {
        new("GET", "api/v1/Client", "ATM Monitoring Dashboard", WidgetType.Table, 60, ExecutionMode.Realtime),
        new("GET", "api/v1/LastClientContact", "ATM Monitoring Dashboard", WidgetType.StatusCard, 60, ExecutionMode.Realtime),
        new("GET", "api/v1/CurrentStatus/LastTimeInService", "ATM Monitoring Dashboard", WidgetType.StatusCard, 60, ExecutionMode.Scheduled),
        new("GET", "api/v1/Client/{id}/OverallAvailability", "ATM 360 View", WidgetType.GaugeChart, 60, ExecutionMode.Manual),
        new("GET", "api/v1/Client/{id}/Top5UnavailableReasons", "ATM 360 View", WidgetType.BarChart, 60, ExecutionMode.Manual),
        new("GET", "api/v1/ActiveErrorExtra", "Incident Command Center", WidgetType.KpiWidget, 30, ExecutionMode.Realtime),
        new("GET", "api/v1/HistoricalError", "Incident Command Center", WidgetType.TrendChart, 30, ExecutionMode.Scheduled),
        new("POST", "api/v1/Ticket/Search", "Ticket Workbench", WidgetType.Table, 60, ExecutionMode.Realtime),
        new("GET", "api/v1/Ticket/{id}/SLA", "Ticket Workbench", WidgetType.StatusCard, 60, ExecutionMode.Manual),
        new("POST", "api/v1/TransactionData/Search", "Transaction Dashboard", WidgetType.TrendChart, 30, ExecutionMode.Realtime),
        new("GET", "api/v1/TransactionVolume/TransactionVolumes", "Transaction Dashboard", WidgetType.PerformanceGraph, 30, ExecutionMode.Scheduled),
        new("POST", "api/v1/Client/{id}/EJRow", "EJ Search Portal", WidgetType.Table, 0, ExecutionMode.Manual),
        new("POST", "api/v1/Client/{id}/EJPlusTime", "EJ Search Portal", WidgetType.Table, 0, ExecutionMode.Manual),
        new("GET", "api/v1/Business", "Client Dashboard", WidgetType.Table, 300, ExecutionMode.Scheduled),
    };

    private static readonly Regex Sensitive = new(@"password|3deskey|bitlocker|luks|publickey|certificate|secret|credential|/key", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReadPost = new(@"EJRow|EJPlusTime|TransactionIDs|TransactionUUIDs|ExtraTransactionFields|Within|VJMetaData|QueuedNotification$|/Multiple|GetDisregard|Counters$|Compare|Lookup", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Split = new(@"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", RegexOptions.Compiled);

    public static string DisplayName(string module) => Known.TryGetValue(module, out var k) ? k.DisplayName : Split.Replace(module, " ");

    public static bool IsSensitive(string module, string url) => Sensitive.IsMatch(url) || module.Equals("ChangePassword", StringComparison.OrdinalIgnoreCase);

    public static EndpointKind Classify(string module, string method, string url)
    {
        if (AuthModules.Contains(module, StringComparer.OrdinalIgnoreCase)) return EndpointKind.Authentication;
        method = method.ToUpperInvariant();
        if (url.Contains("/Search", StringComparison.OrdinalIgnoreCase)) return EndpointKind.Search;
        if (method == "POST" && ReadPost.IsMatch(url)) return EndpointKind.Search;
        if (method is "PUT" or "DELETE" or "PATCH" or "POST") return EndpointKind.Update;
        if (method == "GET") return EndpointKind.Monitoring;
        return EndpointKind.Other;
    }

    /// <summary>Read-only monitoring/search endpoints are enabled automatically; mutating, auth and sensitive ones stay disabled.</summary>
    public static bool EnabledByDefault(EndpointKind kind, bool sensitive) =>
        !sensitive && kind is EndpointKind.Monitoring or EndpointKind.Search;

    public static string ModuleIcon(string module) => Known.TryGetValue(module, out var k) ? k.Icon : "bi-puzzle";

    public static CuratedMapping? FindCurated(string method, string url)
    {
        var path = url.Split('?')[0];
        return Curated.FirstOrDefault(c => c.Method == method.ToUpperInvariant() && path.Equals(c.UrlStartsWith, StringComparison.OrdinalIgnoreCase));
    }

    public static string EndpointDisplayName(string method, string url) => $"{method.ToUpperInvariant()} {url}";
}
