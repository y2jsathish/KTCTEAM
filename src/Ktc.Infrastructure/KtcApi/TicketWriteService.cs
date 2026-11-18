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
            var steps = new List<string>();
            var text = $"{comment} (via KTC Monitoring Portal, ref {corr})";
            var marker = "ref " + corr;
            bool seen = false; string? err = null; JsonElement? after = null;
            if (!ViaCommentApi)
            {
                (_, after, err) = await UpdateTicketAsync(t!.Id, o => TicketRules.WithComment(o, User, DateTime.UtcNow, text), steps, ct, marker);
                seen = Has(after, marker);
            }
            // Comment API: the only way in CommentApi mode, and the fallback when the ticket update did not keep the comment.
            if (!seen && (ViaCommentApi || _opt.CommentTable != null))
            {
                if (_opt.CommentTable == null) { err = "CommentVia is CommentApi but TicketWrite:CommentTable (KTC's table number for tickets) is not set. Nothing was sent."; steps.Add(err); }
                else
                {
                    var r = await PostCommentAsync(t!.Id, text, ct);
                    steps.Add($"POST api/v1/Comment (table {_opt.CommentTable}): " + (r ?? "accepted"));
                    after = await ReadAsync(Uri.EscapeDataString(t.Id), ct);
                    if (after != null) steps.Add("Comments after: " + TicketRules.CommentsText(after.Value));
                    seen = Has(after, marker);
                    if (r != null) err = "KTC refused the comment: " + r;
                }
            }
            var closedNote = t!.IsOpen ? "" : $" The ticket is {t.KtcStatus} in KTC, and KTC may not keep changes to a closed ticket; please try an open ticket.";
            res = seen ? new(true, $"Comment added to ticket {t.Id}. KTC now shows {TicketRules.CommentsText(after!.Value)}.", checks, corr) { KtcSteps = steps }
                : new(false, (err ?? $"KTC answered OK but the comment is not on ticket {t.Id} when read back, so it was not saved.") + closedNote, checks, corr) { KtcSteps = steps };
        }
        Refresh();
        _log.LogInformation("Ticket comment {Ticket} by {User}: {Result} (ref {Ref}) {Steps}", ticketId, User, res.Done, corr, string.Join(" | ", res.KtcSteps));
        await _audit.LogAsync("Tickets", res.Done ? "Ticket comment added" : "Ticket comment not added", ticketId,
            $"ref {corr}; comment: {comment}; {res.Message}; {string.Join(" | ", res.KtcSteps)}", res.Done);
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
            // 1. the closing comment; 2. the record read back and PUT as closed; 3. read again to see what KTC actually stored;
            // 4. if still open, KTC's PUT Ticket/{id}/ClosedDate and read again. Only a record that reads back closed counts.
            var steps = new List<string>();
            var text = $"Closing: {comment} (via KTC Monitoring Portal, ref {corr})";
            string? cerr = null;
            if (ViaCommentApi)
            {
                cerr = _opt.CommentTable == null ? "TicketWrite:CommentTable is not set." : await PostCommentAsync(t!.Id, text, ct);
                steps.Add($"POST api/v1/Comment (table {_opt.CommentTable}): " + (cerr ?? "accepted"));
            }
            if (cerr != null) res = new(false, "Not closed: KTC refused the closing comment: " + cerr, checks, corr) { KtcSteps = steps };
            else
            {
                // One update: closed status and time (and the closing comment, unless it went through the Comment API).
                var (ok, after, err) = await UpdateTicketAsync(t!.Id, o =>
                {
                    var c = TicketRules.CloseBody(System.Text.Json.JsonDocument.Parse(o.ToJsonString()).RootElement, _opt.ClosedStatusByte!.Value, _opt.ManualClosureMode, DateTime.UtcNow);
                    return ViaCommentApi ? c : TicketRules.WithComment(c, User, DateTime.UtcNow, text);
                }, steps, ct, "ref " + corr, b => TicketRules.LooksClosed(b, _opt.ClosedStatusByte)
                    ? $"Ticket {t.Id} is already closed in KTC ({TicketRules.StateText(b)}). Nothing was changed." : null);
                var closed = after != null && TicketRules.LooksClosed(after.Value, _opt.ClosedStatusByte);
                if (err != null && steps.Any(x => x.StartsWith("Not sent: "))) { res = new(false, err, checks, corr) { KtcSteps = steps }; goto done; }
                if (!closed && after != null && _opt.UseClosedDateCall)
                {
                    var esc = Uri.EscapeDataString(t.Id);
                    var cd = await _api.SendAsync(HttpMethod.Put, $"api/v1/Ticket/{esc}/ClosedDate",
                        new { Value1 = long.TryParse(t.Id, out var n) ? n : 0, Value2 = DateTimeOffset.UtcNow }, new KtcCallOptions { Idempotent = false }, ct);
                    steps.Add($"PUT api/v1/Ticket/{t.Id}/ClosedDate: HTTP {cd.StatusCode} {Answer(cd)}");
                    after = await ReadAsync(esc, ct);
                    if (after != null) steps.Add("After ClosedDate: " + TicketRules.StateText(after.Value));
                    closed = after != null && TicketRules.LooksClosed(after.Value, _opt.ClosedStatusByte);
                }
                res = closed
                    ? new(true, $"Ticket {t.Id} closed. KTC now shows: {TicketRules.StateText(after!.Value)}.", checks, corr) { KtcSteps = steps }
                    : new(false, err ?? $"KTC did not close ticket {t.Id}: the ticket still reads back open" + (after != null ? $" ({TicketRules.StateText(after.Value)})" : "") + ". See KTC's answers below.", checks, corr) { KtcSteps = steps };
            }
        }
        done:
        // A comment may have been added either way, so the list is refreshed either way.
        Refresh();
        _log.LogInformation("Ticket close {Ticket} by {User}: {Result} (ref {Ref}) {Steps}", ticketId, User, res.Done, corr, string.Join(" | ", res.KtcSteps));
        await _audit.LogAsync("Tickets", res.Done ? "Ticket closed" : "Ticket not closed", ticketId, $"ref {corr}; comment: {comment}; {res.Message}; {string.Join(" | ", res.KtcSteps)}", res.Done);
        return res;
    }

    private async Task<JsonElement?> ReadAsync(string escId, CancellationToken ct)
    {
        var g = await _api.SendAsync(HttpMethod.Get, $"api/v1/Ticket/{escId}", null, new KtcCallOptions(), ct);
        var rec = KtcJson.Items(g.Json).FirstOrDefault(e => e.ValueKind == JsonValueKind.Object);
        return g.Success && rec.ValueKind == JsonValueKind.Object ? rec.Clone() : null;
    }

    private static string Answer(KtcResponse r)
    {
        var t = (r.Success ? r.Raw : r.Error) ?? "";
        t = t.Replace('\n', ' ').Trim();
        return PanMasker.Mask(t.Length > 200 ? t[..200] + "…" : t.Length == 0 ? "(empty)" : t);
    }

    private static bool Has(JsonElement? ticket, string marker) => ticket != null && TicketRules.Comments(ticket.Value).Any(c => c.Text.Contains(marker));

    private bool ViaCommentApi => _opt.CommentVia.Equals("CommentApi", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the ticket, changes it, PUTs the whole record back (TimeUpdated kept as read, for KTC's conflict check), and reads it
    /// again. Returns whether KTC accepted the PUT, the record as KTC now has it, and an error to show when it went wrong.
    /// </summary>
    private async Task<(bool Ok, JsonElement? After, string? Error)> UpdateTicketAsync(string id, Func<System.Text.Json.Nodes.JsonObject, System.Text.Json.Nodes.JsonObject> change, List<string> steps, CancellationToken ct,
        string? marker = null, Func<JsonElement, string?>? stopIf = null)
    {
        var esc = Uri.EscapeDataString(id);
        var before = await ReadAsync(esc, ct);
        if (before == null) { steps.Add($"GET api/v1/Ticket/{id}: could not read the ticket"); return (false, null, "The ticket could not be read from KTC, so nothing was changed."); }
        steps.Add("Before: " + TicketRules.StateText(before.Value));
        steps.Add("Comments before: " + TicketRules.CommentsText(before.Value));
        if (stopIf?.Invoke(before.Value) is { } stop) { steps.Add("Not sent: " + stop); return (false, before, stop); }
        var body = change(System.Text.Json.Nodes.JsonNode.Parse(before.Value.GetRawText())!.AsObject());
        var fu = _opt.ForceUpdate ? "true" : "false";
        var p = await _api.SendAsync(HttpMethod.Put, $"api/v1/Ticket/{esc}?forceupdate={fu}", body, new KtcCallOptions { Idempotent = false }, ct);
        // KTC answers the PUT with the record it holds: say whether the new comment is in that answer.
        var echo = KtcJson.Items(p.Json).FirstOrDefault(e => e.ValueKind == JsonValueKind.Object);
        steps.Add($"PUT api/v1/Ticket/{id}?forceupdate={fu}: HTTP {p.StatusCode} " + (p.Success && echo.ValueKind == JsonValueKind.Object ? "answered with the ticket record (" + TicketRules.StateText(echo) + ")" : Answer(p)));
        if (marker != null && echo.ValueKind == JsonValueKind.Object)
            steps.Add($"KTC's PUT answer: {TicketRules.CommentsText(echo)}; new comment {(Has(echo, marker) ? "included" : "NOT included")}");
        var after = await ReadAsync(esc, ct);
        if (after != null) { steps.Add("After: " + TicketRules.StateText(after.Value)); steps.Add("Comments after: " + TicketRules.CommentsText(after.Value)); }
        var ok = p.Success && !(p.Json is { ValueKind: JsonValueKind.False });
        return (ok, after, ok ? null : $"KTC refused the update: {Answer(p)}");
    }

    /// <summary>POST api/v1/Comment for the ticket (CommentVia = CommentApi). Null when KTC accepted it, else KTC's error.</summary>
    private async Task<string?> PostCommentAsync(string ticketId, string text, CancellationToken ct)
    {
        var body = TicketRules.CommentBody(ticketId, User, text, _opt.CommentTable!.Value, _opt.SendStatusParameter, DateTime.UtcNow);
        var r = await _api.SendAsync(HttpMethod.Post, "api/v1/Comment", body, new KtcCallOptions { Idempotent = false }, ct);
        return r.Success && !(r.Json is { ValueKind: JsonValueKind.False }) ? null : Answer(r);
    }
}
