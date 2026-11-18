namespace Ktc.Application.Models;

// ---- Disputes menu (KTC api/v1/DisputeManagement). Field names are read tolerantly: the KTC collection has no response samples. ----

/// <summary>A KTC dispute state from GET DisputeManagement/DisputeState: its number, its name, and whether KTC marks it final.</summary>
public record DisputeStateDto(int Id, string Name, bool? IsFinal);

/// <summary>One row of the Disputes list. Card numbers are masked; "Raw" values are shown when KTC gives no name.</summary>
public record DisputeDto
{
    public string Id { get; init; } = "";
    public string AtmId { get; init; } = "";
    public string AtmName { get; init; } = "";
    public string Client { get; init; } = "";
    /// <summary>KTC's state number (Item1 of ChangeState), when the record has one.</summary>
    public int? StateId { get; init; }
    /// <summary>KTC's state name from GET DisputeState, or "State 7" when the number is not in that list.</summary>
    public string State { get; init; } = "";
    public bool IsClosed { get; init; }
    public DateTime? CreatedUtc { get; init; }
    public DateTime? UpdatedUtc { get; init; }
    public DateTime? ClosedUtc { get; init; }
    public DateTime? TransactionUtc { get; init; }
    public decimal? Amount { get; init; }
    public string Currency { get; init; } = "";
    /// <summary>Masked card number (first 6 / last 4 at most).</summary>
    public string Card { get; init; } = "";
    public string TransactionId { get; init; } = "";
    public string TransactionType { get; init; } = "";
    public string Customer { get; init; } = "";
    public string Reason { get; init; } = "";
    public string CreatedBy { get; init; } = "";
    public string LastChangeBy { get; init; } = "";
    /// <summary>KTC's dispute rule that raised it ("Dispute Rules" column of KTC's Windows client): its id and its name from GET DisputeRule.</summary>
    public int? RuleId { get; init; }
    public string Rule { get; init; } = "";
    /// <summary>Text of the newest comment ("Last Comment" column), card numbers masked.</summary>
    public string LastComment { get; init; } = "";
}

/// <summary>
/// The Disputes list query. StateId is sent to KTC as statusfilter (0 or none = all states), Days as Numdays ("Number of days to show"),
/// as KTC's Windows client does. A numeric AtmId that is a known KTC client reads that ATM's ClientDisputes instead of the group list.
/// </summary>
public record DisputeQuery(int? Days = null, int? StateId = null, string? AtmId = null, decimal? MinAmount = null, decimal? MaxAmount = null,
    string? DisputeId = null, string? Card = null);

public record DisputeSearchResult(IReadOnlyList<DisputeDto> Rows, IReadOnlyList<DisputeStateDto> States, IReadOnlyList<string> Warnings, IReadOnlyList<string> Sources,
    bool AmountKnown);

/// <summary>Dispute Details popup (read-only): the record, its maps (customer, extra data, session, transaction), comments and notifications.</summary>
public record DisputeDetails(DisputeDto Dispute, IReadOnlyList<KtcField> Fields, IReadOnlyList<KtcField> Customer, IReadOnlyList<KtcField> ExtraData,
    IReadOnlyList<KtcField> Session, IReadOnlyList<KtcField> Transaction, IReadOnlyList<TicketComment> Comments, IReadOnlyList<IReadOnlyList<KtcField>> History,
    IReadOnlyList<IReadOnlyList<KtcField>> Notifications, IReadOnlyList<string> Sources, IReadOnlyList<string> Warnings);

/// <summary>KTC's dispute set-up (read-only): states and rules, every field as KTC returned it.</summary>
public record DisputeSetup(IReadOnlyList<DisputeStateDto> States, IReadOnlyList<IReadOnlyList<KtcField>> StateRecords, IReadOnlyList<IReadOnlyList<KtcField>> Rules,
    IReadOnlyList<IReadOnlyList<KtcField>> MessageTemplates, IReadOnlyList<string> Warnings);

/// <summary>Change a dispute's state, or close it, from the portal.</summary>
public record DisputeWriteRequest(int? StateId, string? Reason, string? ConfirmDisputeId, string? AtmId = null);
public record DisputeWriteState(bool Enabled, bool CanComment, bool StateChangeAllowed, bool CanChangeState, bool CanClose, string? BlockedReason, IReadOnlyList<DisputeStateDto> States, int MinReasonLength, int MaxReasonLength);
public record DisputeWriteResult(bool Done, string Message, IReadOnlyList<ActionCheck> Checks, string CorrelationId)
{
    /// <summary>What KTC answered at each step and the state read back.</summary>
    public IReadOnlyList<string> KtcSteps { get; init; } = Array.Empty<string>();
}
