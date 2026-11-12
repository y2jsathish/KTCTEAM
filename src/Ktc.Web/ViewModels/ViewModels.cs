using System.ComponentModel.DataAnnotations;
using Ktc.Domain.Entities;
using Ktc.Domain.Enums;

namespace Ktc.Web.ViewModels;

public class LoginViewModel
{
    [Required] public string Username { get; set; } = "";
    [Required, DataType(DataType.Password)] public string Password { get; set; } = "";
    [Required] public string AppName { get; set; } = "KTC_UI";
    [Required] public string AuthMode { get; set; } = "DOMAIN";
    public string[] AuthModes { get; set; } = Array.Empty<string>();
    public string? ReturnUrl { get; set; }
    public bool MockMode { get; set; }
}

public class ReportsViewModel
{
    public List<ReportSchedule> Schedules { get; set; } = new();
    public Dictionary<int, string> Types { get; set; } = new();
}

public class ScheduleInput
{
    [Required, StringLength(200)] public string Name { get; set; } = "";
    [Required] public ReportType ReportType { get; set; }
    [Required] public ReportFrequency Frequency { get; set; }
    [Required] public ExportFormat Format { get; set; }
    [Required, StringLength(1000)] public string Recipients { get; set; } = "";
    public DateTime? FirstRunLocal { get; set; }
}

public class AlertRuleInput
{
    public int Id { get; set; }
    public double Threshold { get; set; }
    [Range(1, 10080)] public int WindowMinutes { get; set; } = 60;
    [Range(1, 10080)] public int CooldownMinutes { get; set; } = 30;
    public Severity Severity { get; set; }
    public AlertChannels Channels { get; set; }
    [StringLength(1000)] public string? Recipients { get; set; }
    public bool Enabled { get; set; }
}

public class ApiExecuteInput
{
    public int EndpointId { get; set; }
    public Dictionary<string, string>? Parameters { get; set; }
    public string? Body { get; set; }
    public bool UseCache { get; set; } = true;
}

public class IncidentActionInput
{
    [Required] public string Key { get; set; } = "";
    [Required] public IncidentActionType Type { get; set; }
    [Required, StringLength(2000)] public string Value { get; set; } = "";
}

public class FieldMappingInput
{
    public int MappingId { get; set; }
    [Required, StringLength(300)] public string FieldPath { get; set; } = "";
    [Required, StringLength(200)] public string Label { get; set; } = "";
    public WidgetType WidgetType { get; set; }
    public string? Aggregation { get; set; }
}

public class MappingInput
{
    public int EndpointId { get; set; }
    [Required, StringLength(200)] public string ScreenName { get; set; } = "";
    public WidgetType WidgetType { get; set; }
    [Range(0, 86400)] public int RefreshInterval { get; set; } = 60;
    public ExecutionMode ExecutionMode { get; set; }
}
