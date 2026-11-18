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
/// Ticket Details popup: add a comment (POST api/v1/Comment) and close a ticket (closing comment, then GET Ticket/{id} and PUT it
/// back as closed; KTC has no dedicated close call). Sent with the signed-in user's token, only when TicketWrite:Enabled is on,
/// never retried automatically, and every attempt (sent or not) is audited with the user, ticket and masked comment.
/// </summary>
public class TicketWriteService : ITicketWriteService
{
    private readonly IKtcApiClient _api; private readonly IKtcDataService _data; private readonly TicketWriteOptions _opt;
    private readonly IAuditService _audit; private readonly IHttpContextAccessor _http; private readonly ILogger<TicketWriteService> _log;
    private readonly Microsoft.Extensions.Caching.Memory.IMemoryCache _cache;

    public TicketWriteService(IKtcApiClient api, IKtcDataService data, IOptions<TicketWriteOptions> opt, IAuditService audit, IHttpContextAccessor http, ILogger<TicketWriteService> log,
        Microsoft.Extensions.Caching.Memory.IMemoryCache cache)
    { _api = api; _data = data; _opt = opt.Value; _audit = audit; _http = http; _log = log; _cache = cache; }

    /// <summary>Drops the cached ticket list (KtcDataService keys it "{scope}|tickets") so the change shows at once.</summary>
    private void Refresh() { _cache.Remove("svc|tickets"); _cache.Remove("u:" + User + "|tickets"); }

    private string User => _http.HttpContext?.User.Identity?.Name ?? "unknown";

    private string? CloseBlocked(TicketDto? t) =>
        t == null ? "KTC has no such ticket."
        : !t.IsOpen ? $"The ticket is already {t.KtcStatus}."
        : _opt.ClosedStatusByte == null ? "Closing is not set up: TicketWrite:ClosedStatusByte (KTC's status number for a closed ticket) is not configured."
        : null;

    public async Task<TicketWriteState> StateAsync(string ticketId, CancellationToken ct = default)
    {
        var t = await _data.GetTicketAsync(ticketId, ct);
        var why = CloseBlocked(t);
        return new TicketWriteState(_opt.Enabled, t != null, why == null, why, _opt.MinCommentLength, _opt.MaxCommentLength);
    }

    private (string Comment, bool Ok, string Detail) CheckComment(string? raw)
    {
        var c = PanMasker.Mask((raw ?? "").Trim());
        if (c.Length < _opt.MinCommentLength) return (c, false, $"Write at least {_opt.MinCommentLength} characters.");
        if (c.Length > _opt.MaxCommentLength) return (c, false, $"At most {_opt.MaxCommentLength} characters.");
        if (c.Any(ch => char.IsControl(ch) && ch is not '\n' and not '\r' and not '\t')) return (c, false, "The comment contains control characters.");
        return (c, true, c == (raw ?? "").Trim() ? "OK" : "OK (card numbers masked)");
    }

    public async Task<TicketWriteResult> AddCommentAsync(string ticketId, TicketWriteRequest q, CancellationToken ct = default)
    {
        var corr = Guid.NewGuid().ToString("N")[..12]; var checks = new List<ActionCheck>();
        var t = await _data.GetTicketAsync(ticketId, ct);
        checks.Add(new("Ticket", t != null, t != null ? $"{t.Id} · {t.AtmName} · {t.KtcStatus}" : $"KTC has no ticket {ticketId}."));
        var (comment, cok, cdetail) = CheckComment(q.Comment);
        checks.Add(new("Comment", cok, cdetail));
        checks.Add(new("Writing enabled", _opt.Enabled, _opt.Enabled ? "OK" : "Writing to KTC tickets is off (TicketWrite:Enabled = false). Nothing was sent."));
        TicketWriteResult res;
        if (checks.Any(c => !c.Passed)) res = new(false, "Not sent: " + string.Join(" ", checks.Where(c => !c.Passed).Select(c => c.Detail)), checks, corr);
        else
        {
            var r = await PostCommentAsync(t!.Id, comment, corr, ct);
            res = r == null ? new(true, $"Comment added to ticket {t.Id}.", checks, corr) : new(false, "KTC refused the comment: " + r, checks, corr);
        }
        if (res.Done) Refresh();
        await _audit.LogAsync("Tickets", res.Done ? "Ticket comment added" : "Ticket comment not added", ticketId,
            $"ref {corr}; comment: {comment}; {res.Message}", res.Done);
        return res;
    }

    public async Task<TicketWriteResult> CloseAsync(string ticketId, TicketWriteRequest q, CancellationToken ct = default)
    {
        var corr = Guid.NewGuid().ToString("N")[..12]; var checks = new List<ActionCheck>();
        var t = await _data.GetTicketAsync(ticketId, ct);
        checks.Add(new("Ticket", t != null, t != null ? $"{t.Id} · {t.AtmName} · {t.KtcStatus}" : $"KTC has no ticket {ticketId}."));
        var why = CloseBlocked(t);
        if (t != null) checks.Add(new("Can be closed", why == null, why ?? "Open in KTC."));
        var typed = (q.ConfirmTicketId ?? "").Trim();
        checks.Add(new("Ticket id typed", t != null && typed == t.Id, t != null && typed == t.Id ? "OK" : "Type the ticket number to confirm."));
        var (comment, cok, cdetail) = CheckComment(q.Comment);
        checks.Add(new("Closing comment", cok, cdetail));
        checks.Add(new("Writing enabled", _opt.Enabled, _opt.Enabled ? "OK" : "Writing to KTC tickets is off (TicketWrite:Enabled = false). Nothing was sent."));

        TicketWriteResult res;
        if (checks.Any(c => !c.Passed)) res = new(false, "Not closed: " + string.Join(" ", checks.Where(c => !c.Passed).Select(c => c.Detail)), checks, corr);
        else
        {
            // 1. the closing comment, 2. the ticket record read back from KTC and PUT as closed.
            var cerr = await PostCommentAsync(t!.Id, "Closing: " + comment, corr, ct);
            if (cerr != null) res = new(false, "Not closed: KTC refused the closing comment: " + cerr, checks, corr);
            else
            {
                var esc = Uri.EscapeDataString(t.Id);
                var g = await _api.SendAsync(HttpMethod.Get, $"api/v1/Ticket/{esc}", null, new KtcCallOptions(), ct);
                var rec = KtcJson.Items(g.Json).FirstOrDefault(e => e.ValueKind == JsonValueKind.Object);
                if (!g.Success || rec.ValueKind != JsonValueKind.Object)
                    res = new(false, $"The closing comment was added, but the ticket could not be read back to close it ({g.Error}). It is still open.", checks, corr);
                else
                {
                    var body = TicketRules.CloseBody(rec, _opt.ClosedStatusByte!.Value, _opt.ManualClosureMode, DateTime.UtcNow);
                    var p = await _api.SendAsync(HttpMethod.Put, $"api/v1/Ticket/{esc}?forceupdate={(_opt.ForceUpdate ? "true" : "false")}", body, new KtcCallOptions { Idempotent = false }, ct);
                    res = p.Success && !(p.Json is { ValueKind: JsonValueKind.False })
                        ? new(true, $"Ticket {t.Id} closed.", checks, corr)
                        : new(false, $"The closing comment was added, but KTC did not close the ticket ({PanMasker.Mask(p.Error ?? "it answered false")}). It is still open.", checks, corr);
                }
            }
        }
        if (res.Done) Refresh();
        _log.LogInformation("Ticket close {Ticket} by {User}: {Result} (ref {Ref})", ticketId, User, res.Done, corr);
        await _audit.LogAsync("Tickets", res.Done ? "Ticket closed" : "Ticket not closed", ticketId, $"ref {corr}; comment: {comment}; {res.Message}", res.Done);
        return res;
    }

    /// <summary>POST api/v1/Comment for the ticket. Null when KTC accepted it, else the error.</summary>
    private async Task<string?> PostCommentAsync(string ticketId, string comment, string corr, CancellationToken ct)
    {
        var body = TicketRules.CommentBody(ticketId, User, $"{comment} (via KTC Monitoring Portal, ref {corr})", _opt.CommentTable, _opt.SendStatusParameter, DateTime.UtcNow);
        var r = await _api.SendAsync(HttpMethod.Post, "api/v1/Comment", body, new KtcCallOptions { Idempotent = false }, ct);
        return r.Success && !(r.Json is { ValueKind: JsonValueKind.False }) ? null : PanMasker.Mask(r.Error ?? "KTC answered false");
    }
}
