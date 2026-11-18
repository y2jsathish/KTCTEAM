using System.Text.Json;
using Ktc.Application.Abstractions;
using Ktc.Application.Logic;
using Ktc.Application.Models;
using Ktc.Application.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ktc.Infrastructure.KtcApi;

/// <summary>
/// Dispute Details popup: change state (POST DisputeManagement/Dispute/{id}/ChangeState, {"Item1": state id, "Item2": reason}) and
/// close (POST Dispute/Close?disputeId=). Sent with the signed-in user's token, only when DisputeWrite:Enabled is on, never retried, the
/// dispute number typed to confirm, the state taken from KTC's own GET DisputeState list, and done only when GET Dispute/{id} reads
/// back the change. Every attempt (sent or not) is audited with the user, dispute, state and masked reason.
/// </summary>
public class DisputeWriteService : IDisputeWriteService
{
    private const string Root = "api/v1/DisputeManagement";
    private readonly IKtcApiClient _api; private readonly IKtcDisputeService _data; private readonly DisputeWriteOptions _opt;
    private readonly IAuditService _audit; private readonly IHttpContextAccessor _http; private readonly ILogger<DisputeWriteService> _log;

    public DisputeWriteService(IKtcApiClient api, IKtcDisputeService data, IOptions<DisputeWriteOptions> opt, IAuditService audit, IHttpContextAccessor http, ILogger<DisputeWriteService> log)
    { _api = api; _data = data; _opt = opt.Value; _audit = audit; _http = http; _log = log; }

    private string User => _http.HttpContext?.User.Identity?.Name ?? "unknown";

    public async Task<DisputeWriteState> StateAsync(string disputeId, CancellationToken ct = default)
    {
        var d = await _data.GetAsync(disputeId, ct);
        var states = await _data.GetStatesAsync(ct);
        var why = d == null ? "KTC has no such dispute." : d.IsClosed ? $"The dispute is already closed ({d.State})." : null;
        var stateWhy = why ?? (states.Count == 0 ? "KTC returned no dispute states (GET DisputeState), so there is nothing to pick." : null);
        return new DisputeWriteState(_opt.Enabled, stateWhy == null, why == null, why ?? stateWhy, states, _opt.MinReasonLength, _opt.MaxReasonLength);
    }

    private (string Reason, bool Ok, string Detail) CheckReason(string? raw)
    {
        var c = PanMasker.Mask((raw ?? "").Trim());
        if (c.Length < _opt.MinReasonLength) return (c, false, $"Write at least {_opt.MinReasonLength} characters.");
        if (c.Length > _opt.MaxReasonLength) return (c, false, $"At most {_opt.MaxReasonLength} characters.");
        if (c.Any(ch => char.IsControl(ch) && ch is not '\n' and not '\r' and not '\t')) return (c, false, "The reason contains control characters.");
        return (c, true, c == (raw ?? "").Trim() ? "OK" : "OK (card numbers masked)");
    }

    private List<ActionCheck> Common(string disputeId, DisputeDto? d, DisputeWriteRequest q, out string reason)
    {
        var checks = new List<ActionCheck>
        {
            new("Dispute", d != null, d != null ? $"{d.Id} · {d.AtmName} · {d.State}" : $"KTC has no dispute {disputeId}."),
            new("Still open", d is { IsClosed: false }, d == null ? "—" : d.IsClosed ? $"The dispute is already closed ({d.State})." : "Open in KTC.")
        };
        var typed = (q.ConfirmDisputeId ?? "").Trim();
        checks.Add(new("Dispute number typed", d != null && typed == d.Id, d != null && typed == d.Id ? "OK" : "Type the dispute number to confirm."));
        var (r, ok, detail) = CheckReason(q.Reason);
        reason = r;
        checks.Add(new("Reason", ok, detail));
        return checks;
    }

    private void Enabled(List<ActionCheck> checks) =>
        checks.Add(new("Writing enabled", _opt.Enabled, _opt.Enabled ? "OK" : "Writing to KTC disputes is off (DisputeWrite:Enabled = false). Nothing was sent."));

    public async Task<DisputeWriteResult> ChangeStateAsync(string disputeId, DisputeWriteRequest q, CancellationToken ct = default)
    {
        var corr = Guid.NewGuid().ToString("N")[..12];
        var d = await _data.GetAsync(disputeId, ct);
        var checks = Common(disputeId, d, q, out var reason);
        var states = await _data.GetStatesAsync(ct);
        var target = q.StateId is int sid ? states.FirstOrDefault(s => s.Id == sid) : null;
        checks.Add(new("New state", target != null && d?.StateId != target.Id,
            target == null ? (states.Count == 0 ? "KTC returned no dispute states." : "Pick a state from KTC's list.")
            : d?.StateId == target.Id ? $"The dispute is already in state {target.Id} ({target.Name})." : $"{target.Id} ({target.Name})"));
        Enabled(checks);

        DisputeWriteResult res;
        if (checks.Any(c => !c.Passed)) res = new(false, "Not sent: " + string.Join(" ", checks.Where(c => !c.Passed).Select(c => c.Detail)), checks, corr);
        else
        {
            var steps = new List<string> { "Before: " + DisputeRules.StateText(d) };
            var text = $"{reason} (via KTC Monitoring Portal, ref {corr})";
            var r = await _api.SendAsync(HttpMethod.Post, $"{Root}/Dispute/{Uri.EscapeDataString(d!.Id)}/ChangeState", DisputeRules.ChangeStateBody(target!.Id, text),
                new KtcCallOptions { Idempotent = false }, ct);
            steps.Add($"POST {Root}/Dispute/{d.Id}/ChangeState (Item1 {target.Id}): HTTP {r.StatusCode} {Answer(r)}");
            _data.Invalidate();
            var after = await _data.GetAsync(d.Id, ct);
            steps.Add("After: " + DisputeRules.StateText(after));
            res = after?.StateId == target.Id
                ? new(true, $"Dispute {d.Id} is now in state {target.Id} ({target.Name}).", checks, corr) { KtcSteps = steps }
                : new(false, !Accepted(r) ? "KTC refused the change: " + Answer(r)
                    : $"KTC answered OK but dispute {d.Id} reads back as {DisputeRules.StateText(after)}, so the change was not saved.", checks, corr) { KtcSteps = steps };
        }
        _log.LogInformation("Dispute state change {Dispute} -> {State} by {User}: {Result} (ref {Ref}) {Steps}", disputeId, q.StateId, User, res.Done, corr, string.Join(" | ", res.KtcSteps));
        await _audit.LogAsync("Disputes", res.Done ? "Dispute state changed" : "Dispute state not changed", disputeId,
            $"ref {corr}; to state {q.StateId}; reason: {reason}; {res.Message}; {string.Join(" | ", res.KtcSteps)}", res.Done);
        return res;
    }

    public async Task<DisputeWriteResult> CloseAsync(string disputeId, DisputeWriteRequest q, CancellationToken ct = default)
    {
        var corr = Guid.NewGuid().ToString("N")[..12];
        var d = await _data.GetAsync(disputeId, ct);
        var checks = Common(disputeId, d, q, out var reason);
        Enabled(checks);

        DisputeWriteResult res;
        if (checks.Any(c => !c.Passed)) res = new(false, "Not closed: " + string.Join(" ", checks.Where(c => !c.Passed).Select(c => c.Detail)), checks, corr);
        else
        {
            // KTC's Close call takes only the id (its body is empty in the collection); the reason is kept in the portal's audit.
            var steps = new List<string> { "Before: " + DisputeRules.StateText(d) };
            var r = await _api.SendAsync(HttpMethod.Post, $"{Root}/Dispute/Close?disputeId={Uri.EscapeDataString(d!.Id)}", new { }, new KtcCallOptions { Idempotent = false }, ct);
            steps.Add($"POST {Root}/Dispute/Close?disputeId={d.Id}: HTTP {r.StatusCode} {Answer(r)}");
            _data.Invalidate();
            var after = await _data.GetAsync(d.Id, ct);
            steps.Add("After: " + DisputeRules.StateText(after));
            res = after is { IsClosed: true }
                ? new(true, $"Dispute {d.Id} closed. KTC now shows {DisputeRules.StateText(after)}.", checks, corr) { KtcSteps = steps }
                : new(false, !Accepted(r) ? "KTC refused to close it: " + Answer(r)
                    : $"KTC answered OK but dispute {d.Id} still reads back open ({DisputeRules.StateText(after)}).", checks, corr) { KtcSteps = steps };
        }
        _log.LogInformation("Dispute close {Dispute} by {User}: {Result} (ref {Ref}) {Steps}", disputeId, User, res.Done, corr, string.Join(" | ", res.KtcSteps));
        await _audit.LogAsync("Disputes", res.Done ? "Dispute closed" : "Dispute not closed", disputeId, $"ref {corr}; reason: {reason}; {res.Message}; {string.Join(" | ", res.KtcSteps)}", res.Done);
        return res;
    }

    private static bool Accepted(KtcResponse r) => r.Success && !(r.Json is { ValueKind: JsonValueKind.False });

    private static string Answer(KtcResponse r)
    {
        var t = ((r.Success ? r.Raw : r.Error) ?? "").Replace('\n', ' ').Trim();
        return PanMasker.Mask(t.Length > 200 ? t[..200] + "…" : t.Length == 0 ? "(empty)" : t);
    }
}
