using System.Text.Json;
using Ktc.Application.Abstractions;
using Ktc.Application.Logic;
using Ktc.Application.Models;
using Ktc.Application.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ktc.Infrastructure.KtcApi;

/// <summary>
/// ATM Actions tab. Reads KTC's command types with the signed-in user's token, maps the planned actions to them, and sends one
/// command to one ATM through the generic parameter form. Sending is off unless KtcActions:SendEnabled is true; reboot, resets
/// and in/out of service are refused here (they get their own guided flow), as are dual-authorization commands.
/// </summary>
public class KtcActionService : IKtcActionService
{
    private readonly IKtcApiClient _api; private readonly KtcActionsOptions _opt; private readonly KtcOptions _ktc; private readonly IKtcDataService _data;
    private readonly IAuditService _audit; private readonly IMemoryCache _cache; private readonly IHttpContextAccessor _http; private readonly ILogger<KtcActionService> _log;

    public KtcActionService(IKtcApiClient api, IOptions<KtcActionsOptions> opt, IOptions<KtcOptions> ktc, IKtcDataService data, IAuditService audit,
        IMemoryCache cache, IHttpContextAccessor http, ILogger<KtcActionService> log)
    { _api = api; _opt = opt.Value; _ktc = ktc.Value; _data = data; _audit = audit; _cache = cache; _http = http; _log = log; }

    private string User => _http.HttpContext?.User.Identity?.Name ?? "svc";
    // Per user: KTC may show each account only the commands it is allowed to run.
    private string Key => "ktc-actions:commandtypes:" + User;

    public async Task<AtmActionCatalog> GetCatalogAsync(bool refresh, CancellationToken ct = default)
    {
        if (!refresh && _cache.TryGetValue(Key, out AtmActionCatalog? hit) && hit != null) return hit;
        var route = string.IsNullOrWhiteSpace(_opt.CommandTypesEndpoint) ? "api/v1/CommandType" : _opt.CommandTypesEndpoint.Trim().TrimStart('/');
        var warnings = new List<string>();
        var r = await _api.SendAsync(HttpMethod.Get, route, null, new KtcCallOptions(), ct);
        List<KtcCommandTypeDto> commands = new(); List<string> fields = new();
        if (!r.Success) warnings.Add($"GET {route}: {r.Error}");
        else
        {
            commands = CommandTypeRules.Parse(r.Json);
            fields = CommandTypeRules.Fields(r.Json);
            if (commands.Count == 0) warnings.Add(r.Json == null ? $"GET {route} answered, but not with JSON." : $"GET {route} answered with no command types.");
        }
        var planned = CommandTypeRules.Map(commands, _opt.Commands, r.Success && commands.Count > 0);
        commands = CommandTypeRules.Classify(commands, planned, _opt.HighRiskPattern, _opt.BlockedPattern);
        // Shape only, never values: lets the log show what the real server returned.
        _log.LogInformation("KTC command types: {Route} -> HTTP {Status}, {Count} commands, fields [{Fields}], {Dual} need dual authorization, {Params} with parameters",
            route, r.StatusCode, commands.Count, string.Join(", ", fields), commands.Count(c => c.RequiresDualAuth), commands.Count(c => c.Parameters.Count > 0));
        var cat = new AtmActionCatalog(route, DateTime.UtcNow, _ktc.UseMock, r.Success, commands, planned, fields, warnings) { SendEnabled = _opt.SendEnabled };
        if (r.Success) _cache.Set(Key, cat, TimeSpan.FromMinutes(Math.Max(1, _opt.CacheMinutes)));
        return cat;
    }

    public async Task<AtmActionResult> SendAsync(AtmActionRequest q, bool checkOnly, CancellationToken ct = default)
    {
        var corr = Guid.NewGuid().ToString("N")[..12];
        var checks = new List<ActionCheck>();
        void Check(string name, bool ok, string detail) => checks.Add(new ActionCheck(name, ok, PanMasker.Mask(detail)));

        var (atm, ticketNo, reason) = await CommonChecksAsync(q.AtmId, q.ConfirmAtmId, q.TicketNumber, q.Reason, checks, ct);

        // 3. Command and parameters
        var cat = await GetCatalogAsync(false, ct);
        var cmd = cat.Commands.FirstOrDefault(c => c.Id == q.CommandTypeId);
        Check("Command", cmd != null, cmd != null ? $"{cmd.Name} (#{cmd.Id})" : $"KTC has no command type #{q.CommandTypeId}.");
        Dictionary<string, string> values = new(); string? shown = null;
        if (cmd != null)
        {
            Check("Allowed from this form", cmd.SendBlockedReason == null, cmd.SendBlockedReason ?? "OK");
            var (vals, errs) = CommandParameterRules.Validate(cmd.Parameters, q.Parameters);
            values = vals; shown = CommandParameterRules.Describe(cmd.Parameters, vals);
            Check("Parameters", errs.Count == 0, errs.Count == 0 ? (shown.Length > 0 ? shown : "No parameters") : string.Join(" ", errs));
            if (cmd.HighRisk)
            {
                var riskOk = q.AcknowledgedRisk && (q.ConfirmCommandName ?? "").Trim().Equals(cmd.Name, StringComparison.OrdinalIgnoreCase);
                Check("High-risk confirmation", riskOk, riskOk ? "Command name retyped and risk acknowledged." : "High-risk command: retype the command name and tick \"I understand\".");
            }
        }

        // 4. Not already pending on this ATM (KTC's list of clients with a pending/in-progress action of this type)
        if (atm != null && cmd != null && checks.All(c => c.Passed))
        {
            var pip = await _api.SendAsync(HttpMethod.Get, $"api/v1/Action/ClientIDsPIP?commandtypeID={cmd.Id}", null, new KtcCallOptions(), ct);
            if (!pip.Success) Check("Not already pending", true, $"Could not check ({pip.Error}); KTC still applies its own limits.");
            else
            {
                var busy = KtcJson.Items(pip.Json).Select(e => e.ValueKind == JsonValueKind.Number ? e.GetRawText() : KtcJson.Str(e, "ClientID", "ID", "Id")).Any(x => KtcJson.NormId(x) == KtcJson.NormId(atm.Id));
                Check("Not already pending", !busy, busy ? $"{cmd.Name} is already pending or in progress on this ATM." : "OK");
            }
        }

        var allOk = checks.All(c => c.Passed);
        Check("Sending enabled", _opt.SendEnabled, _opt.SendEnabled ? "OK" : "Sending is off (KtcActions:SendEnabled = false). The form and checks work; nothing was sent.");

        AtmActionResult result;
        if (!allOk || !_opt.SendEnabled || checkOnly)
        {
            var msg = !allOk ? "Not sent: " + string.Join(" ", checks.Where(c => !c.Passed && c.Name != "Sending enabled").Select(c => c.Detail))
                : !_opt.SendEnabled ? "All checks passed, but sending is off (KtcActions:SendEnabled = false). Nothing was sent."
                : "All checks passed.";
            result = new AtmActionResult(false, msg, checks, cmd?.Name, shown, null, null, corr);
        }
        else
        {
            var xml = CommandParameterRules.BuildXml(cmd!.Parameters, values, _opt.ParametersRoot, _opt.ParameterElement, _opt.ParameterNameAttribute);
            var comment = PanMasker.Mask($"{(ticketNo.Length > 0 ? $"Ticket {ticketNo}: " : "")}{reason} (via KTC Monitoring Portal, ref {corr})");
            var body = new { Value1 = cmd.Id, Value2 = xml, Value3 = new[] { new { Timestamp = DateTime.UtcNow, User, Comment = comment, Confidential = false } } };
            // Not idempotent: never retried automatically, so one click sends at most one command.
            var r = await _api.SendAsync(HttpMethod.Post, $"api/v1/Client/{atm!.Id}/Actions", body, new KtcCallOptions { Idempotent = false }, ct);
            var actionId = r.Success ? ActionIdFrom(r.Json) : null;
            result = new AtmActionResult(r.Success, r.Success ? $"Sent {cmd.Name} to {atm.Name}{(actionId != null ? $" (KTC action {actionId})" : "")}." : $"KTC refused the command: {PanMasker.Mask(r.Error)}",
                checks, cmd.Name, shown, actionId, r.StatusCode, corr);
        }

        await _audit.LogAsync("AtmActions", checkOnly ? "Action check" : result.Sent ? "Action sent" : "Action not sent", atm?.Id ?? q.AtmId,
            Trunc($"ref={corr}; command={cmd?.Name ?? "#" + q.CommandTypeId}; params={shown ?? ""}; ticket={ticketNo}; reason={reason}; ktc={result.KtcStatus?.ToString() ?? "-"}; action={result.ActionId ?? "-"}; checks=" +
                string.Join(", ", checks.Select(c => $"{c.Name}:{(c.Passed ? "ok" : "FAIL")}")), 3900), result.Sent || (checkOnly && allOk));
        _log.LogInformation("ATM action {Ref} by {User}: {Command} on {Atm} -> {Outcome} (KTC {Status})", corr, User, cmd?.Name, atm?.Id, result.Sent ? "sent" : "not sent", result.KtcStatus);
        return result;
    }


    /// <summary>Checks shared by the command form and the guided reboot: ATM, typed ATM id, ticket in KTC for this ATM, reason.</summary>
    internal async Task<(AtmDto? Atm, string Ticket, string Reason)> CommonChecksAsync(string? atmId, string? confirmAtmId, string? ticket, string? reasonIn, List<ActionCheck> checks, CancellationToken ct)
    {
        void Check(string name, bool ok, string detail) => checks.Add(new ActionCheck(name, ok, PanMasker.Mask(detail)));
        var q = new { AtmId = atmId, ConfirmAtmId = confirmAtmId, TicketNumber = ticket, Reason = reasonIn };
        // ATM
        var atm = string.IsNullOrWhiteSpace(q.AtmId) ? null : await _data.GetAtmAsync(q.AtmId.Trim(), ct);
        Check("ATM found", atm != null, atm != null ? $"{atm.Name} (KTC client {atm.Id})" : $"No ATM matches \"{q.AtmId}\".");
        var typed = (q.ConfirmAtmId ?? "").Trim();
        var typedOk = atm != null && (typed.Equals(atm.Id, StringComparison.OrdinalIgnoreCase) || typed.Equals(atm.Name, StringComparison.OrdinalIgnoreCase));
        Check("ATM id typed", typedOk, typedOk ? "OK" : typed.Length == 0 ? "Type the ATM id to confirm." : "The typed ATM id does not match this ATM.");

        // 2. Ticket and reason
        var ticketNo = (q.TicketNumber ?? "").Trim();
        // The ticket is optional (the bank's choice): empty is fine, any reference is recorded in the comment and the audit,
        // and a KTC ticket number is checked against KTC only when RequireTicket is on.
        if (ticketNo.Length == 0) Check("Ticket", !_opt.RequireTicket, _opt.RequireTicket ? "A ticket number is required." : "None given (optional).");
        else if (ticketNo.Length > 40 || ticketNo.Any(c => char.IsControl(c))) Check("Ticket", false, "The ticket or reference is not valid (40 characters at most, no control characters).");
        else if (!_opt.RequireTicket || !_opt.VerifyTicketInKtc) Check("Ticket", true, $"{ticketNo} (recorded as a reference)");
        else
        {
            var t = await _data.GetTicketAsync(ticketNo, ct);
            if (t == null) Check("Ticket", false, $"Ticket {ticketNo} was not found in KTC. Enter the KTC ticket number (digits, as in the Tickets list) for this ATM.{await OpenTicketsHintAsync(atm, ct)}");
            else if (atm != null && t.AtmId.Length > 0 && !t.AtmId.Equals(atm.Id, StringComparison.OrdinalIgnoreCase))
                Check("Ticket", false, $"Ticket {ticketNo} is for another ATM ({t.AtmName}).{await OpenTicketsHintAsync(atm, ct)}");
            else Check("Ticket", true, $"Ticket {ticketNo} found in KTC{(t.AtmId.Length > 0 ? " for this ATM" : "")}.");
        }
        var reason = PanMasker.Mask((q.Reason ?? "").Trim());
        Check("Reason", reason.Length >= _opt.MinReasonLength && reason.Length <= 500,
            reason.Length < _opt.MinReasonLength ? $"Give a reason of at least {_opt.MinReasonLength} characters." : reason.Length > 500 ? "The reason is too long (500 characters at most)." : "OK");

        return (atm, ticketNo, reason);
    }


    /// <summary>" Open tickets for ADB05401: 5992, 6001." (or that it has none), to help when the typed ticket is wrong.</summary>
    private async Task<string> OpenTicketsHintAsync(AtmDto? atm, CancellationToken ct)
    {
        if (atm == null) return "";
        try
        {
            var open = (await _data.SearchTicketsAsync(new TicketQuery(AtmId: atm.Id, ClosedDays: 0), ct))
                .Where(x => x.IsOpen && (x.AtmId.Length == 0 || x.AtmId.Equals(atm.Id, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(x => x.CreatedUtc).Select(x => x.Id).Take(5).ToList();
            return open.Count == 0 ? $" {atm.Name} has no open ticket in KTC." : $" Open tickets for {atm.Name}: {string.Join(", ", open)}.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return ""; }
    }

    public async Task<(bool Ok, string? ActionId, string? Error)> SendMappedAsync(string atmId, string commandKey, string comment, CancellationToken ct = default)
    {
        if (!_opt.SendEnabled) return (false, null, "sending is off (KtcActions:SendEnabled = false)");
        var cat = await GetCatalogAsync(false, ct);
        var planned = cat.Planned.FirstOrDefault(p => p.Key.Equals(commandKey, StringComparison.OrdinalIgnoreCase));
        if (planned?.CommandTypeId is not int id) return (false, null, $"{commandKey} is not mapped to a KTC command (KtcActions:Commands:{commandKey})");
        var cmd = cat.Commands.First(c => c.Id == id);
        if (cmd.RequiresDualAuth) return (false, null, $"{cmd.Name} needs a second login, which is not supported yet");
        var note = PanMasker.Mask(System.Text.RegularExpressions.Regex.Replace(comment, @"\s*\(via KTC Monitoring Portal.*$", ""));
        var (vals, errs) = CommandParameterRules.Validate(cmd.Parameters, CommandParameterRules.GuidedValues(cmd.Parameters, note));
        if (errs.Count > 0) return (false, null, $"{cmd.Name} needs parameters without defaults: {string.Join(" ", errs)}");
        var xml = CommandParameterRules.BuildXml(cmd.Parameters, vals, _opt.ParametersRoot, _opt.ParameterElement, _opt.ParameterNameAttribute);
        var body = new { Value1 = cmd.Id, Value2 = xml, Value3 = new[] { new { Timestamp = DateTime.UtcNow, User = "KTC Monitoring Portal", Comment = PanMasker.Mask(comment), Confidential = false } } };
        var r = await _api.SendAsync(HttpMethod.Post, $"api/v1/Client/{atmId}/Actions", body, new KtcCallOptions { Idempotent = false }, ct);
        _log.LogInformation("Guided action {Key} ({Command}) on {Atm} -> HTTP {Status}", commandKey, cmd.Name, atmId, r.StatusCode);
        return r.Success ? (true, ActionIdFrom(r.Json), null) : (false, null, PanMasker.Mask(r.Error));
    }

    private async Task<Dictionary<int, KtcCommandTypeDto>> CommandsByIdAsync(CancellationToken ct) =>
        (await GetCatalogAsync(false, ct)).Commands.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());

    public async Task<AtmActionHistory> GetHistoryAsync(string atmId, int days, CancellationToken ct = default)
    {
        var w = new List<string>();
        var atm = await _data.GetAtmAsync(atmId.Trim(), ct);
        var id = atm?.Id ?? atmId.Trim();
        var cmds = await CommandsByIdAsync(ct);
        var r = await _api.SendAsync(HttpMethod.Get, $"api/v1/Client/{id}/Actions?numdays={days}", null, new KtcCallOptions(), ct);
        if (!r.Success) w.Add($"GET Client/{id}/Actions: {r.Error}");
        var list = r.Success ? ActionActivityRules.ParseActions(r.Json, cmds, _opt) : new List<ActionRecord>();
        return new AtmActionHistory(id, days, list, Array.Empty<RebootRun>(), w, _ktc.UseMock);
    }

    public async Task<ActionRecord?> GetActionAsync(string actionId, CancellationToken ct = default)
    {
        if (!long.TryParse(actionId, out _)) return null;
        var r = await _api.SendAsync(HttpMethod.Get, $"api/v1/Action/{actionId}", null, new KtcCallOptions(), ct);
        if (!r.Success || r.Json is not { } j) return null;
        var e = KtcJson.Items(j).FirstOrDefault(x => x.ValueKind == JsonValueKind.Object);
        return e.ValueKind == JsonValueKind.Object ? ActionActivityRules.ParseAction(e, actionId, await CommandsByIdAsync(ct), _opt) : null;
    }

    public async Task<(IReadOnlyList<ActionFile> Files, string? Error)> GetFilesAsync(string actionId, CancellationToken ct = default)
    {
        if (!long.TryParse(actionId, out _)) return (Array.Empty<ActionFile>(), "Not a KTC action id.");
        var r = await _api.SendAsync(HttpMethod.Get, $"api/v1/Action/{actionId}/UploadedDirectoryListing", null, new KtcCallOptions(), ct);
        if (!r.Success) return (Array.Empty<ActionFile>(), r.Error);
        return (ActionActivityRules.ParseListing(r.Json, r.Raw, _opt), null);
    }

    public async Task<(ActionFileContent? File, string? Error)> GetFileAsync(string actionId, string name, bool preview, CancellationToken ct = default)
    {
        if (!long.TryParse(actionId, out _)) return (null, "Not a KTC action id.");
        name = (name ?? "").Trim();
        if (name.Length == 0 || name.Length > 260 || name.Any(char.IsControl)) return (null, "Choose a file from the list.");
        // Only names KTC listed for this action can be fetched (no free paths).
        var (files, err) = await GetFilesAsync(actionId, ct);
        if (err != null) return (null, err);
        var f = files.FirstOrDefault(x => x.Name == name);
        if (f == null) return (null, "That file is not in this action's list.");
        if (!f.Viewable) return (new ActionFileContent(f.Name, f.Size ?? 0, null, 0, false, f.BlockedReason, ""), null);
        var route = (_opt.UploadedFileEndpoint ?? "api/v1/Action/{actionId}/UploadedFile").Replace("{actionId}", actionId).Replace("{file}", Uri.EscapeDataString(name)).TrimStart('/');
        var r = await _api.SendAsync(HttpMethod.Get, route, null, new KtcCallOptions(), ct);
        if (!r.Success) return (null, r.Error);
        var (bytes, _) = ActionActivityRules.DecodeFile(r.Json, r.Raw);
        var res = ActionActivityRules.Check(f.Name, bytes, _opt, preview);
        if (!preview || res.BlockedReason != null)
            await _audit.LogAsync("AtmActions", res.BlockedReason != null ? "Uploaded file refused" : "Uploaded file downloaded", actionId,
                $"file={PanMasker.Mask(f.Name)}; size={res.Size}; sha256={res.Sha256}; masked={res.MaskedCount}; {res.BlockedReason ?? "masked copy only"}", res.BlockedReason == null);
        return (res, null);
    }

    private static string Trunc(string s, int n) => s.Length > n ? s[..n] : s;

    /// <summary>The new action id from KTC's answer: a bare number, or an object with ID/ActionID.</summary>
    public static string? ActionIdFrom(JsonElement? j)
    {
        if (j is not { } e) return null;
        if (e.ValueKind == JsonValueKind.Number) return e.GetRawText();
        if (e.ValueKind == JsonValueKind.String && long.TryParse(e.GetString(), out var n)) return n.ToString();
        if (e.ValueKind == JsonValueKind.Object) { var s = KtcJson.Str(e, "ActionID", "ID", "Id"); return s.Length > 0 ? s : null; }
        return null;
    }
}
