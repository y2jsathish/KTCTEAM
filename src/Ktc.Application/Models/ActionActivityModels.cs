namespace Ktc.Application.Models;

// ---- ATM Actions tab: what happened after a command was sent. ----

/// <summary>
/// One KTC action (GET Client/{id}/Actions or GET Action/{id}). Status is the name from KtcActions:StatusMap; Final = it will not
/// change any more. User, Ticket and Reason come from the action's comments (the portal writes "Ticket X: reason (via KTC
/// Monitoring Portal, ref R)"). Result and Parameters are PAN-masked. HasFiles = the command returns files (upload-type command).
/// </summary>
public record ActionRecord(string Id, int CommandId, string CommandName, string CommandLabel, string Category, int? StatusByte, string Status, bool Final, bool Failed,
    int? ProgressPercent, int? RetryCount, DateTime? AddedUtc, DateTime? StartUtc, DateTime? EndUtc, string Result, string Parameters,
    string? User, string? Ticket, string? Reason, string? PortalRef, bool HasFiles);

public record AtmActionHistory(string AtmId, int Days, IReadOnlyList<ActionRecord> Actions, IReadOnlyList<RebootRun> Reboots, IReadOnlyList<string> Warnings, bool Demo);

/// <summary>One file an upload-type action brought back from the ATM (GET Action/{id}/UploadedDirectoryListing).</summary>
public record ActionFile(string Name, long? Size, DateTime? ModifiedUtc, bool Viewable, string? BlockedReason);

/// <summary>
/// A file from GET Action/{id}/UploadedFile after the PCI rules: only text can be checked, so only text is shown or downloaded,
/// always PAN-masked with track data removed. Binary or oversized files are refused (Text null, BlockedReason set).
/// </summary>
public record ActionFileContent(string Name, long Size, string? Text, int MaskedCount, bool Truncated, string? BlockedReason, string Sha256);

/// <summary>Start of a guided reboot: out of service, wait for idle, reboot, wait for the ATM to come back, back in service.</summary>
public record RebootRequest(string AtmId, string? TicketNumber, string? Reason, string? ConfirmAtmId);

public enum RebootStep { Starting, WaitingForIdle, TakingOutOfService, ReCheckingIdle, Rebooting, WaitingForOffline, WaitingForOnline, PuttingInService, CheckingDevices, Done }
public enum RebootState { Running, Completed, Failed, Cancelled, NeedsAttention }

public record RebootEvent(DateTime Utc, RebootStep Step, string Message, bool Ok = true, string? ActionId = null);

/// <summary>A guided reboot of one ATM, stored so a portal restart resumes it instead of leaving an ATM out of service.</summary>
public class RebootRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string AtmId { get; set; } = "";
    public string AtmName { get; set; } = "";
    public string User { get; set; } = "";
    public string Ticket { get; set; } = "";
    public string Reason { get; set; } = "";
    public RebootState State { get; set; } = RebootState.Running;
    public RebootStep Step { get; set; } = RebootStep.Starting;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime StepStartedUtc { get; set; } = DateTime.UtcNow;
    public string? OosActionId { get; set; }
    public string? RebootActionId { get; set; }
    public string? InServiceActionId { get; set; }
    public DateTime? OutOfServiceUtc { get; set; }
    public DateTime? RebootSentUtc { get; set; }
    public DateTime? LastContactBeforeUtc { get; set; }
    public DateTime? OfflineUtc { get; set; }
    public DateTime? OnlineUtc { get; set; }
    public DateTime? InServiceUtc { get; set; }
    public int Retries { get; set; }
    public bool CancelRequested { get; set; }
    public List<RebootEvent> Events { get; set; } = new();
    public bool Finished => State != RebootState.Running;
}
