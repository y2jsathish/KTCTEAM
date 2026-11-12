namespace Ktc.Domain.Enums;

public enum HealthStatus { Unknown = 0, Healthy = 1, Warning = 2, Critical = 3 }
public enum Severity { P1 = 1, P2 = 2, P3 = 3, P4 = 4 }
public enum TicketStatus { Open = 1, Assigned = 2, InProgress = 3, Resolved = 4, Closed = 5 }
public enum SlaState { OnTrack = 0, NearBreach = 1, Breached = 2, Achieved = 3 }

public enum AlertRuleType
{
    AtmOffline = 1,
    HighTicketVolume = 2,
    HighIncidentVolume = 3,
    TransactionFailureSpike = 4,
    HostDisconnect = 5,
    ApiSuccessRateLow = 6,
    ApiResponseSlow = 7,
    ApiConsecutiveFailures = 8,
    TicketSlaBreachImminent = 9,
    IncidentEscalation = 10
}

[Flags]
public enum AlertChannels { None = 0, Portal = 1, Email = 2, Teams = 4 }

public enum ReportType
{
    AtmAvailability = 1, Incident = 2, TicketSummary = 3, Transaction = 4, Sla = 5, ExecutiveSummary = 6
}
public enum ReportFrequency { Daily = 1, Weekly = 2, Monthly = 3 }
public enum ExportFormat { Pdf = 1, Excel = 2, Csv = 3 }

public enum ExecutionMode { Realtime = 1, Scheduled = 2, Manual = 3 }
public enum WidgetType { Table = 1, StatusCard = 2, KpiWidget = 3, Counter = 4, GaugeChart = 5, TrendChart = 6, PerformanceGraph = 7, BarChart = 8, PieChart = 9 }
public enum EndpointKind { Authentication = 1, Monitoring = 2, Search = 3, Update = 4, Other = 5 }

public enum IncidentActionType { Assign = 1, Comment = 2, Escalate = 3 }
