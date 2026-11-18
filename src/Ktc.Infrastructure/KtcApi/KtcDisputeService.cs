using System.Text.Json;
using System.Text.RegularExpressions;
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
/// Disputes menu, read-only, using the calls KTC's Windows client makes (bank trace and screenshots): list GET {group}/GroupDisputes, one
/// ATM's GET {client}/ClientDisputes (statusfilter = a DisputeState id, 0 = all), states GET DisputeState, rules GET DisputeRule, templates GET DisputeMessageTemplate, and the
/// dispute's queued customer messages POST SMSQueue/Disputes [id]. GET Dispute/{id} is only a fallback (not seen in the trace).
/// ATM names come from GET Client. No dispute write call is made here; see <see cref="DisputeWriteService"/>.
/// </summary>
public class KtcDisputeService : IKtcDisputeService
{
    private const string Root = "api/v1/DisputeManagement";
    private readonly IKtcApiClient _api; private readonly KtcOptions _ktc; private readonly DisputeOptions _opt;
    private readonly IMemoryCache _cache; private readonly IHttpContextAccessor _http; private readonly ILogger<KtcDisputeService> _log;

    public KtcDisputeService(IKtcApiClient api, IOptions<KtcOptions> ktc, IOptions<DisputeOptions> opt, IMemoryCache cache, IHttpContextAccessor http, ILogger<KtcDisputeService> log)
    { _api = api; _ktc = ktc.Value; _opt = opt.Value; _cache = cache; _http = http; _log = log; }

    private string Scope => KtcTokenScope.IsServiceForced || !(_http.HttpContext?.User.Identity?.IsAuthenticated ?? false) ? "svc" : "u:" + _http.HttpContext!.User.Identity!.Name;
    private string Key(string k) => $"{Scope}|disputes|{k}";
    private static readonly HashSet<string> Keys = new();

    /// <summary>Cached for ttl; an answer that keep() rejects (KTC failed) is not cached, so the next call asks KTC again.</summary>
    private async Task<T> Cached<T>(string key, TimeSpan ttl, Func<Task<T>> f, Func<T, bool>? keep = null)
    {
        var k = Key(key);
        if (_cache.TryGetValue(k, out T? hit) && hit != null) return hit;
        var v = await f();
        if (keep == null || keep(v)) { lock (Keys) Keys.Add(k); _cache.Set(k, v, ttl); }
        return v;
    }

    public void Invalidate()
    {
        string[] all; lock (Keys) all = Keys.Where(k => k.Contains("|disputes|search:")).ToArray();
        foreach (var k in all) _cache.Remove(k);
    }

    private async Task<JsonElement?> Fetch(string url, List<string> w, HttpMethod? m = null, object? body = null, CancellationToken ct = default)
    {
        var r = await _api.SendAsync(m ?? HttpMethod.Get, url, body, new KtcCallOptions { Idempotent = true }, ct);
        if (!r.Success) { lock (w) w.Add(PanMasker.Mask($"{(m ?? HttpMethod.Get).Method} {url.Split('?')[0]}: {r.Error}")); return null; }
        return r.Json;
    }

    // ---------------- reference data ----------------

    private record Atm(string Name, string Client);

    private Task<Dictionary<string, Atm>> AtmsAsync(List<string> w, CancellationToken ct) =>
        Cached("atms", TimeSpan.FromMinutes(5), async () =>
        {
            var tc = Fetch("api/v1/Client?includedeleted=false", w, ct: ct); var tb = Fetch("api/v1/Business?includeDefault=false", w, ct: ct);
            await Task.WhenAll(tc, tb);
            var biz = KtcJson.Items(tb.Result).GroupBy(e => KtcJson.NormId(KtcJson.Str(e, "ID", "Id", "BusinessID"))).ToDictionary(g => g.Key, g => KtcJson.Str(g.First(), "Name", "BusinessName"));
            var map = new Dictionary<string, Atm>();
            foreach (var e in KtcJson.Items(tc.Result))
            {
                var id = KtcJson.NormId(KtcJson.Str(e, "ClientID", "ClientId", "ID", "Id"));
                if (id.Length > 0) map.TryAdd(id, new Atm(KtcJson.Str(e, "Name", "NodeName", "ClientName"), biz.GetValueOrDefault(KtcJson.NormId(KtcJson.Str(e, "BusinessID", "BusinessId"))) ?? ""));
            }
            return map;
        }, m => m.Count > 0);

    private Task<int> AllClientsGroupAsync(List<string> w, CancellationToken ct) =>
        Cached("group-all", TimeSpan.FromMinutes(30), async () =>
        {
            foreach (var g in KtcJson.Items(await Fetch("api/v1/Group", w, ct: ct)))
                if (Regex.IsMatch(KtcJson.Str(g, "Name", "GroupName"), @"^\s*all\s*clients\s*$", RegexOptions.IgnoreCase) && KtcJson.Int(g, -1, "ID", "Id", "GroupID") is > 0 and var id) return id;
            return _ktc.TicketSearchGroupId > 0 ? _ktc.TicketSearchGroupId : 2;
        });

    private Task<(List<DisputeStateDto> States, JsonElement? Raw)> StatesCoreAsync(List<string> w, CancellationToken ct) =>
        Cached("states", TimeSpan.FromMinutes(30), async () =>
        {
            var raw = await Fetch($"{Root}/DisputeState", w, ct: ct);
            return (DisputeRules.ParseStates(raw), raw?.Clone());
        }, x => x.Item2 != null);

    private Task<(Dictionary<int, string> Rules, JsonElement? Raw)> RulesCoreAsync(List<string> w, CancellationToken ct) =>
        Cached("rules", TimeSpan.FromMinutes(30), async () =>
        {
            var raw = await Fetch($"{Root}/DisputeRule", w, ct: ct);
            return (DisputeRules.ParseRules(raw), raw?.Clone());
        }, x => x.Item2 != null);

    public async Task<IReadOnlyList<DisputeStateDto>> GetStatesAsync(CancellationToken ct = default) => (await StatesCoreAsync(new(), ct)).States;

    // ---------------- list ----------------

    /// <summary>
    /// The disputes KTC lists for a group or one ATM, the way KTC's Windows client reads them (trace and screenshots): GET
    /// {groupid}/GroupDisputes or {clientid}/ClientDisputes ?statusfilter=&amp;Numdays=. statusfilter is the state picked in the client's
    /// "Dispute states filter" (a DisputeState id); 0 means all states.
    /// </summary>
    private async Task<(List<JsonElement> Rows, List<string> Warnings, List<string> Sources)> ListAsync(string scope, int statusFilter, int days, CancellationToken ct)
    {
        var w = new List<string>();
        var url = $"{Root}/{scope}?statusfilter={statusFilter}&Numdays={days}";
        var json = await Fetch(url, w, ct: ct);
        var items = KtcJson.Items(json).Where(e => e.ValueKind == JsonValueKind.Object).Select(e => e.Clone()).ToList();
        return (items, w, new List<string> { $"GET {url} ({items.Count})" });
    }

    private DisputeDto? MapRow(JsonElement e, List<DisputeStateDto> states, Dictionary<string, Atm> atms, Dictionary<int, string> rules) =>
        DisputeRules.Map(e, states, _opt.StateNames, id => atms.TryGetValue(id, out var a) ? (a.Name, a.Client) : null, rules);

    public async Task<DisputeSearchResult> SearchAsync(DisputeQuery q, CancellationToken ct = default)
    {
        var days = Math.Clamp(q.Days ?? _opt.LookbackDays, 1, 3650);
        var sf = q.StateId is > 0 and var s0 ? s0 : 0;
        var w = new List<string>();
        var (states, _) = await StatesCoreAsync(w, ct);
        var (rules, _) = await RulesCoreAsync(w, ct);
        var atms = await AtmsAsync(w, ct);

        // One ATM (its KTC client id), as on the ATM page's Disputes tab: that ATM's ClientDisputes. Otherwise the All Clients group.
        var atmText = q.AtmId?.Trim() ?? ""; var atmNorm = KtcJson.NormId(atmText);
        var oneAtm = atmNorm.Length > 0 && long.TryParse(atmNorm, out _) && atms.ContainsKey(atmNorm);
        var scope = oneAtm ? $"{atmNorm}/ClientDisputes" : $"{await AllClientsGroupAsync(w, ct)}/GroupDisputes";
        var (rows, warn, sources) = await Cached($"search:{scope}:{sf}:{days}", TimeSpan.FromSeconds(30), () => ListAsync(scope, sf, days, ct), x => x.Warnings.Count == 0);
        w.AddRange(warn);
        var mapped = rows.Select(e => MapRow(e, states, atms, rules)).Where(d => d != null).Select(d => d!).GroupBy(d => d.Id).Select(g => g.First()).ToList();
        if (rows.Count > 0 && mapped.Count == 0)
            _log.LogWarning("KTC returned {Rows} dispute records but none had an id; fields of the first: {Fields}", rows.Count, string.Join(", ", rows[0].EnumerateObject().Take(40).Select(p => p.Name)));

        // Columns no record filled: KTC's field names are not the ones the portal knows. Say so, and name the record's fields in the log.
        var missing = mapped.Count == 0 ? new List<string>() : mapped.Select(DisputeRules.Missing).Aggregate((a, b) => a.Intersect(b).ToList());
        if (missing.Count > 0)
        {
            w.Add($"KTC's dispute records have no field the portal recognises for: {string.Join(", ", missing)}. Click \"Show KTC's raw records\" below and send what it shows, so the field names can be mapped.");
            _log.LogWarning("Dispute fields not recognised ({Missing}); KTC record fields: {Fields}", string.Join(", ", missing), string.Join(", ", DisputeRules.Leaves(rows[0]).Select(l => l.Path)));
        }

        Func<DisputeDto, bool>? atmMatch = null;
        if (!oneAtm && atmText.Length > 0) atmMatch = d => d.AtmId == atmNorm || d.AtmName.Contains(atmText, StringComparison.OrdinalIgnoreCase);
        var amountKnown = mapped.Count == 0 || mapped.Any(d => d.Amount != null);
        if ((q.MinAmount != null || q.MaxAmount != null) && !amountKnown)
            w.Add("The amount filter could not be applied: KTC's dispute records carry no amount (neither an amount field nor extra data \"Amount\"). Send a sample record so it can be mapped.");
        // The state filter is KTC's (statusfilter), so it is not applied again here.
        var list = DisputeRules.Filter(mapped, q with { StateId = null, MinAmount = amountKnown ? q.MinAmount : null, MaxAmount = amountKnown ? q.MaxAmount : null }, atmMatch)
            .OrderByDescending(d => d.CreatedUtc ?? d.TransactionUtc ?? DateTime.MinValue).Take(Math.Max(1, _opt.MaxRows)).ToList();
        return new DisputeSearchResult(list, states, w.Distinct().ToList(), sources.Append($"GET {Root}/DisputeState").Append($"GET {Root}/DisputeRule").ToList(), amountKnown);
    }

    /// <summary>
    /// KTC's dispute records exactly as the list call returns them (first few), with card data masked, plus every field path: the
    /// one-click way to see KTC's real field names when the portal's columns come out empty.
    /// </summary>
    public async Task<DisputeRawSample> GetRawSampleAsync(DisputeQuery q, int max = 3, CancellationToken ct = default)
    {
        var days = Math.Clamp(q.Days ?? _opt.LookbackDays, 1, 3650);
        var sf = q.StateId is > 0 and var s0 ? s0 : 0;
        var w = new List<string>();
        var atms = await AtmsAsync(w, ct);
        var atmNorm = KtcJson.NormId(q.AtmId ?? "");
        var scope = atmNorm.Length > 0 && long.TryParse(atmNorm, out _) && atms.ContainsKey(atmNorm) ? $"{atmNorm}/ClientDisputes" : $"{await AllClientsGroupAsync(w, ct)}/GroupDisputes";
        var (rows, warn, src) = await ListAsync(scope, sf, days, ct);
        w.AddRange(warn);
        var take = rows.Take(Math.Clamp(max, 1, 20)).ToList();
        var fields = take.SelectMany(r => DisputeRules.Leaves(r).Select(l => (l.Path, Kind: l.Value.ValueKind.ToString(), Sample: DisputeRules.SafeValue(l.Name, l.Value.ValueKind == JsonValueKind.String ? l.Value.GetString() : l.Value.GetRawText()))))
            .GroupBy(x => x.Path).Select(g => new KtcField(g.Key, $"{g.First().Kind}: {Cut(g.First().Sample)}")).ToList();
        foreach (var r in take)
            foreach (var p in r.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Array || (p.Value.ValueKind == JsonValueKind.Object && fields.All(f => !f.Name.StartsWith(p.Name + ".")))))
                if (fields.All(f => f.Name != p.Name)) fields.Add(new KtcField(p.Name, $"{p.Value.ValueKind}: {Cut(DisputeRules.Scrub(System.Text.Json.Nodes.JsonNode.Parse(p.Value.GetRawText()), p.Name)!.ToJsonString())}"));
        var json = take.Select(r => DisputeRules.Scrub(System.Text.Json.Nodes.JsonNode.Parse(r.GetRawText()))!.ToJsonString(new JsonSerializerOptions { WriteIndented = true })).ToList();
        _log.LogInformation("Raw dispute sample from {Source}: {Count} records, fields {Fields}", src.FirstOrDefault(), rows.Count, string.Join(", ", fields.Select(f => f.Name)));
        return new DisputeRawSample(src.FirstOrDefault() ?? "", rows.Count, fields, json, w);
        static string Cut(string? v) => v == null ? "" : v.Length > 120 ? v[..120] + "…" : v;
    }

    // ---------------- one dispute ----------------

    /// <summary>
    /// One dispute as KTC holds it: from its ATM's ClientDisputes list (what the Windows client shows in its details window) when the
    /// ATM is known, else GET Dispute/{id}; each is the fallback of the other.
    /// </summary>
    private async Task<(JsonElement? Raw, DisputeDto? Dto, string Source)> ReadAsync(string id, string? atmId, List<string> w, CancellationToken ct)
    {
        var (states, _) = await StatesCoreAsync(w, ct);
        var (rules, _) = await RulesCoreAsync(w, ct);
        var atms = await AtmsAsync(w, ct);
        var cid = KtcJson.NormId(atmId ?? "");
        async Task<(JsonElement? Raw, DisputeDto? Dto, string Source)> FromClient(string c)
        {
            var (rows, warn, _) = await ListAsync($"{Uri.EscapeDataString(c)}/ClientDisputes", 0, Math.Max(_opt.LookbackDays, _opt.DetailLookupDays), ct);
            w.AddRange(warn);
            foreach (var r in rows) if (KtcJson.NormId(KtcJson.Str(r, DisputeRules.IdF)) == KtcJson.NormId(id)) return (r, MapRow(r, states, atms, rules), $"GET {Root}/{c}/ClientDisputes");
            return (null, null, "");
        }
        async Task<(JsonElement? Raw, DisputeDto? Dto, string Source)> ById()
        {
            var rec = KtcJson.Items(await Fetch($"{Root}/Dispute/{Uri.EscapeDataString(id)}", w, ct: ct)).FirstOrDefault(e => e.ValueKind == JsonValueKind.Object);
            if (rec.ValueKind != JsonValueKind.Object || KtcJson.Str(rec, DisputeRules.IdF).Length == 0) return (null, null, "");
            return (rec.Clone(), MapRow(rec, states, atms, rules), $"GET {Root}/Dispute/{id}");
        }
        if (cid.Length > 0 && long.TryParse(cid, out _))
        {
            var a = await FromClient(cid);
            if (a.Dto != null) return a;
        }
        var b = await ById();
        if (b.Dto != null || cid.Length > 0) return b;
        return (null, null, "");
    }

    public async Task<DisputeDto?> GetAsync(string id, string? atmId = null, CancellationToken ct = default) => (await ReadAsync(id, atmId, new(), ct)).Dto;

    public async Task<DisputeDetails?> GetDetailsAsync(string id, string? atmId = null, CancellationToken ct = default)
    {
        var w = new List<string>();
        var (raw, d, src) = await ReadAsync(id, atmId, w, ct);
        if (raw is not { } e || d == null) return null;
        var sources = new List<string> { src };
        // The Windows client's details window reads the dispute's queued customer messages with POST SMSQueue/Disputes [id] (trace),
        // which answers {smsQueueId: SMSQueue}; it is a read.
        var notes = new List<IReadOnlyList<KtcField>>();
        if (long.TryParse(d.Id, out var lid))
        {
            var sms = await Fetch($"{Root}/SMSQueue/Disputes", w, HttpMethod.Post, new[] { lid }, ct);
            notes = KtcJson.Items(sms).Where(x => x.ValueKind == JsonValueKind.Object).Select(x => (IReadOnlyList<KtcField>)DisputeRules.Fields(x)).ToList();
            sources.Add($"POST {Root}/SMSQueue/Disputes [{d.Id}]");
        }
        var maps = DisputeRules.CustomerF.Concat(DisputeRules.ExtraDataF).Concat(DisputeRules.SessionF).Concat(DisputeRules.TransactionF).Concat(DisputeRules.HistoryF)
            .Concat(new[] { "Comments", "CommentList", "CommentsString", "CommentString" }).ToArray();
        return new DisputeDetails(d, DisputeRules.Fields(e, maps), DisputeRules.CustomerFields(e), DisputeRules.ExtraFields(e),
            DisputeRules.MapFields(e, DisputeRules.SessionF), DisputeRules.MapFields(e, DisputeRules.TransactionF), TicketRules.Comments(e), DisputeRules.History(e),
            notes, sources, w.Distinct().ToList());
    }

    /// <summary>Comments KTC holds on the dispute (read straight from KTC, not cached), for write read-back.</summary>
    public async Task<(DisputeDto? Dispute, IReadOnlyList<TicketComment> Comments)> ReadCommentsAsync(string id, string? atmId, CancellationToken ct = default)
    {
        var (raw, d, _) = await ReadAsync(id, atmId, new(), ct);
        return (d, raw is { } e ? TicketRules.Comments(e) : Array.Empty<TicketComment>());
    }

    public async Task<DisputeSetup> GetSetupAsync(CancellationToken ct = default)
    {
        var w = new List<string>();
        var (states, raw) = await StatesCoreAsync(w, ct);
        var (_, rulesRaw) = await RulesCoreAsync(w, ct);
        var tpl = await Fetch($"{Root}/DisputeMessageTemplate", w, ct: ct);
        static List<IReadOnlyList<KtcField>> All(JsonElement? j) => KtcJson.Items(j).Where(x => x.ValueKind == JsonValueKind.Object).Select(x => (IReadOnlyList<KtcField>)DisputeRules.Fields(x)).ToList();
        return new DisputeSetup(states, All(raw), All(rulesRaw), All(tpl), w);
    }
}
