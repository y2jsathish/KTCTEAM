namespace Ktc.Application.Models;

// ---- ATM Actions tab, step 1 (read-only). ----

/// <summary>
/// One KTC command type (GET api/v1/CommandType). Text fields are PAN-masked and secret-looking values are hidden.
/// DualAuthMode is KTC's DualAuthModeByte: non-zero means KTC wants a second login (ActionsWithSecondLogin).
/// </summary>
public record KtcCommandTypeDto(int Id, string Name, string Description, string Category, string ParameterTemplate, string SubCommands,
    bool? AllowClientInitiation, int? MaxRetries, int? RetryWaitTime, int? MaxSimultaneous, int? TargetType, bool? IsUserDefined, int? DualAuthMode)
{
    public bool RequiresDualAuth => DualAuthMode is > 0;
    /// <summary>Form fields read from ParameterListTemplateString (see CommandParameterRules).</summary>
    public IReadOnlyList<CommandParameter> Parameters { get; init; } = Array.Empty<CommandParameter>();
    /// <summary>Set when the template could not be read.</summary>
    public string? ParametersNote { get; init; }
    /// <summary>Why this command cannot be sent from the generic form (special flow, dual authorization, blocked); null = it can.</summary>
    public string? SendBlockedReason { get; init; }
    /// <summary>Needs the extra confirmation (KtcActions:HighRiskPattern).</summary>
    public bool HighRisk { get; init; }
}

/// <summary>
/// One input of a command's form. Type: choice, string, bool, integer, number, secret or datetime. Secrets are never logged or
/// shown back. RawType is KTC's own type name; Note explains a fallback (e.g. unknown type entered as text).
/// </summary>
public record CommandParameter(string Name, string Label, string Type, string? RawType, IReadOnlyList<string> Choices, string? Default, bool Required,
    double? Min, double? Max, int? MaxLength, string? TrueText, string? FalseText, string? Help, string? Note);

/// <summary>A request from the Actions tab to send one command to one ATM.</summary>
public record AtmActionRequest(string AtmId, int CommandTypeId, Dictionary<string, string?>? Parameters, string? TicketNumber, string? Reason, string? ConfirmAtmId,
    string? ConfirmCommandName = null, bool AcknowledgedRisk = false);

/// <summary>One validation check and its result, kept in the audit.</summary>
public record ActionCheck(string Name, bool Passed, string Detail);

/// <summary>Outcome of a send. Sent = KTC accepted the request; ActionId = the KTC action id when KTC returned one.</summary>
public record AtmActionResult(bool Sent, string Message, IReadOnlyList<ActionCheck> Checks, string? CommandName, string? ParametersShown,
    string? ActionId, int? KtcStatus, string CorrelationId);

/// <summary>
/// A planned Actions tab button and the KTC command it would use. CommandTypeId is set only when the configured CommandName
/// matches a command KTC returned. Suggestions are names that look right, for filling in the config. Enabled is always false in step 1.
/// </summary>
public record PlannedAtmAction(string Key, string Label, string Description, string? ConfiguredName, int? CommandTypeId, string? MatchedName,
    bool? RequiresDualAuth, IReadOnlyList<string> Suggestions, bool Enabled, string Status);

/// <summary>What the Actions tab shows: KTC's command types, the planned actions mapped to them, and the field names KTC used.</summary>
public record AtmActionCatalog(string Route, DateTime ReadUtc, bool Demo, bool Ok, IReadOnlyList<KtcCommandTypeDto> Commands,
    IReadOnlyList<PlannedAtmAction> Planned, IReadOnlyList<string> Fields, IReadOnlyList<string> Warnings)
{
    /// <summary>KtcActions:SendEnabled. Off by default: the form can be filled and checked, but nothing is sent.</summary>
    public bool SendEnabled { get; init; }
}
