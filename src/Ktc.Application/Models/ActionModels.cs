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
}

/// <summary>
/// A planned Actions tab button and the KTC command it would use. CommandTypeId is set only when the configured CommandName
/// matches a command KTC returned. Suggestions are names that look right, for filling in the config. Enabled is always false in step 1.
/// </summary>
public record PlannedAtmAction(string Key, string Label, string Description, string? ConfiguredName, int? CommandTypeId, string? MatchedName,
    bool? RequiresDualAuth, IReadOnlyList<string> Suggestions, bool Enabled, string Status);

/// <summary>What the Actions tab shows: KTC's command types, the planned actions mapped to them, and the field names KTC used.</summary>
public record AtmActionCatalog(string Route, DateTime ReadUtc, bool Demo, bool Ok, IReadOnlyList<KtcCommandTypeDto> Commands,
    IReadOnlyList<PlannedAtmAction> Planned, IReadOnlyList<string> Fields, IReadOnlyList<string> Warnings);
