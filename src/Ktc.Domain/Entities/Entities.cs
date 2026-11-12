using Ktc.Domain.Enums;

namespace Ktc.Domain.Entities;

public abstract class Entity { public int Id { get; set; } }

public class Role : Entity
{
    public string Name { get; set; } = "";          // Admin | Operator | Viewer
    public string? Description { get; set; }
    public ICollection<User> Users { get; set; } = new List<User>();
}

public class User : Entity
{
    public string Username { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? Email { get; set; }
    public int RoleId { get; set; }
    public Role? Role { get; set; }
    public bool IsActive { get; set; } = true;
    public string Theme { get; set; } = "dark";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginUtc { get; set; }
}

public class AuditLog : Entity
{
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string UserName { get; set; } = "";
    public string Category { get; set; } = "";      // Login | Search | TicketView | IncidentView | Report | Admin ...
    public string Action { get; set; } = "";
    public string? Target { get; set; }
    public string? Details { get; set; }
    public string? IpAddress { get; set; }
    public bool Success { get; set; } = true;
}

public class DashboardCache : Entity
{
    public string Key { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresUtc { get; set; }
}

public class SavedSearch : Entity
{
    public int UserId { get; set; }
    public User? User { get; set; }
    public string Name { get; set; } = "";
    public string SearchType { get; set; } = "";    // Atm | Ticket | Incident | Transaction | EJ
    public string QueryJson { get; set; } = "{}";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public class FavoriteAtm : Entity
{
    public int UserId { get; set; }
    public User? User { get; set; }
    public string AtmId { get; set; } = "";
    public string? AtmName { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public class AlertRule : Entity
{
    public string Name { get; set; } = "";
    public AlertRuleType RuleType { get; set; }
    public double Threshold { get; set; }           // meaning depends on rule type (count, %, minutes, seconds)
    public int WindowMinutes { get; set; } = 60;
    public Severity Severity { get; set; } = Severity.P2;
    public AlertChannels Channels { get; set; } = AlertChannels.Portal;
    public string? Recipients { get; set; }         // ; separated e-mails (overrides default)
    public int CooldownMinutes { get; set; } = 30;
    public bool Enabled { get; set; } = true;
}

public class AlertHistory : Entity
{
    public int? RuleId { get; set; }
    public AlertRule? Rule { get; set; }
    public AlertRuleType RuleType { get; set; }
    public Severity Severity { get; set; }
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public string? SourceKey { get; set; }          // e.g. ATM id; used for de-duplication
    public DateTime RaisedUtc { get; set; } = DateTime.UtcNow;
    public string? AcknowledgedBy { get; set; }
    public DateTime? AcknowledgedUtc { get; set; }
    public string? NotificationStatus { get; set; }
}

public class ReportSchedule : Entity
{
    public string Name { get; set; } = "";
    public ReportType ReportType { get; set; }
    public ReportFrequency Frequency { get; set; }
    public ExportFormat Format { get; set; } = ExportFormat.Pdf;
    public string Recipients { get; set; } = "";
    public DateTime NextRunUtc { get; set; }
    public DateTime? LastRunUtc { get; set; }
    public string? LastStatus { get; set; }
    public bool Enabled { get; set; } = true;
    public string? CreatedBy { get; set; }
}

// ---------------- API registry ----------------
public class ApiModule : Entity
{
    public string ModuleName { get; set; } = "";    // KTC controller name, e.g. "Ticket"
    public string DisplayName { get; set; } = "";
    public string? Icon { get; set; }
    public string? Description { get; set; }
    public string? ScreenName { get; set; }         // business screen the module feeds
    public bool ShowInNav { get; set; }
    public int SortOrder { get; set; }
    public ICollection<ApiEndpoint> Endpoints { get; set; } = new List<ApiEndpoint>();
}

public class ApiEndpoint : Entity
{
    public int ModuleId { get; set; }
    public ApiModule? Module { get; set; }
    public string EndpointName { get; set; } = "";
    public string HttpMethod { get; set; } = "GET";
    public string RelativeUrl { get; set; } = "";
    public string? Description { get; set; }
    public string? RequestSchema { get; set; }
    public string? ResponseSchema { get; set; }     // learned from first successful execution
    public EndpointKind Kind { get; set; }
    public bool Enabled { get; set; }
    public bool IsSensitive { get; set; }           // secrets/passwords/keys - never executable
    public ICollection<ApiMapping> Mappings { get; set; } = new List<ApiMapping>();
}

public class ApiMapping : Entity
{
    public int ApiEndpointId { get; set; }
    public ApiEndpoint? ApiEndpoint { get; set; }
    public string ScreenName { get; set; } = "";
    public WidgetType WidgetType { get; set; }
    public int RefreshInterval { get; set; } = 60;  // seconds
    public ExecutionMode ExecutionMode { get; set; } = ExecutionMode.Manual;
    public ICollection<ApiFieldMapping> Fields { get; set; } = new List<ApiFieldMapping>();
}

/// <summary>Admin-defined "API field -> UI widget" mapping (e.g. DeviceStatus -> Status Card).</summary>
public class ApiFieldMapping : Entity
{
    public int ApiMappingId { get; set; }
    public ApiMapping? ApiMapping { get; set; }
    public string FieldPath { get; set; } = "";     // JSON path, e.g. "Status" or "Items[].Count"
    public string Label { get; set; } = "";
    public WidgetType WidgetType { get; set; }
    public string? Aggregation { get; set; }        // count | sum | avg | last
}

public class IncidentAction : Entity
{
    public string IncidentKey { get; set; } = "";
    public IncidentActionType ActionType { get; set; }
    public string UserName { get; set; } = "";
    public string? Value { get; set; }              // assignee / comment text / escalation level
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
