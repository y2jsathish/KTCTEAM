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
/// Disputes menu, read-only, using the calls KTC's Windows client makes (bank trace): list GET {group}/GroupDisputes, one ATM's
/// GET {client}/ClientDisputes, states GET DisputeState, rules GET DisputeRule, templates GET DisputeMessageTemplate, and the
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

    public async Task<IReadOnlyList<DisputeStateDto>> GetStatesAsync(CancellationToken ct = default) => (await StatesCoreAsync(new(), ct)).States;

    // ---------------- list ----------------

    /// <summary>
    /// The disputes KTC lists for a group or one ATM, the way KTC's Windows client reads them (trace): GET {groupid}/GroupDisputes or
    /// {clientid}/ClientDisputes ?statusfilter=&amp;Numdays=. Every configured statusfilter is asked for and the answers merged.
    /// </summary>
    private async Task<(List<JsonElement> Rows, List<string> Warnings, List<string> Sources)> ListAsync(string scope, int days, CancellationToken ct)
    {
        var w = new List<string>(); var src = new List<string>(); var found = new List<JsonElement>();
        var filters = (_opt.StatusFilters is { Length: > 0 } f ? f : new[] { 0 }).Distinct().ToArray();
        await Task.WhenAll(filters.Select(async sf =>
        {
            var url = $"{Root}/{scope}?statusfilter={sf}&Numdays={days}";
            var json = await Fetch(url, w, ct: ct);
            var items = KtcJson.Items(json).Where(e => e.ValueKind == JsonValueKind.Object).Select(e => e.Clone()).ToList();
            lock (found) { found.AddRange(items); src.Add($"GET {Root}/{scope}?statusfilter={sf}&Numdays={days} ({items.Count})"); }
        }));
        return (found, w, src.OrderBy(x => x).ToList());
    }

    private DisputeDto? MapRow(JsonElement e, List<DisputeStateDto> states, Dictionary<string, Atm> atms) =>
        DisputeRules.Map(e, states, _opt.StateNames, id => atms.TryGetValue(id, out var a) ? (a.Name, a.Client) : null);

    public async Task<DisputeSearchResult> SearchAsync(DisputeQuery q, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var to = q.To ?? now.AddMinutes(5);
        var from = q.From ?? now.AddDays(-Math.Max(1, _opt.LookbackDays));
        var days = Math.Clamp((int)Math.Ceiling((now - from).TotalDays), 1, 3650);
        var w = new List<string>();
        var (states, _) = await StatesCoreAsync(w, ct);
        var atms = await AtmsAsync(w, ct);
        var gid = await AllClientsGroupAsync(w, ct);
        var (rows, warn, sources) = await Cached($"search:{gid}:{days}", TimeSpan.FromSeconds(30), () => ListAsync($"{gid}/GroupDisputes", days, ct), x => x.Warnings.Count == 0);
        w.AddRange(warn);
        var mapped = rows.Select(e => MapRow(e, states, atms)).Where(d => d != null).Select(d => d!).GroupBy(d => d.Id).Select(g => g.First()).ToList();
        if (rows.Count > 0 && mapped.Count == 0)
            _log.LogWarning("KTC returned {Rows} dispute records but none had an id; fields of the first: {Fields}", rows.Count, string.Join(", ", rows[0].EnumerateObject().Take(40).Select(p => p.Name)));
        // Numdays is counted back from now; the end of the range (and a start inside the first day) is applied here.
        mapped = mapped.Where(d => (d.CreatedUtc ?? d.TransactionUtc) is not { } c || (c >= from && c <= to)).ToList();

        Func<DisputeDto, bool>? atmMatch = null;
        if (!string.IsNullOrWhiteSpace(q.AtmId))
        {
            var t = q.AtmId.Trim(); var norm = KtcJson.NormId(t);
            atmMatch = d => d.AtmId == norm || d.AtmName.Contains(t, StringComparison.OrdinalIgnoreCase);
        }
        var amountKnown = mapped.Count == 0 || mapped.Any(d => d.Amount != null);
        if ((q.MinAmount != null || q.MaxAmount != null) && !amountKnown)
            w.Add("The amount filter could not be applied: KTC's dispute records carry no amount (neither an amount field nor extra data \"Amount\"). Send a sample record so it can be mapped.");
        var list = DisputeRules.Filter(mapped, amountKnown ? q : q with { MinAmount = null, MaxAmount = null }, atmMatch)
            .OrderByDescending(d => d.CreatedUtc ?? d.TransactionUtc ?? DateTime.MinValue).Take(Math.Max(1, _opt.MaxRows)).ToList();
        return new DisputeSearchResult(list, states, w.Distinct().ToList(), sources.Append($"GET {Root}/DisputeState").ToList(), amountKnown);
    }

    // ---------------- one dispute ----------------

    /// <summary>
    /// One dispute as KTC holds it: from its ATM's ClientDisputes list (what the Windows client shows in its details window) when the
    /// ATM is known, else GET Dispute/{id}; each is the fallback of the other.
    /// </summary>
    private async Task<(JsonElement? Raw, DisputeDto? Dto, string Source)> ReadAsync(string id, string? atmId, List<string> w, CancellationToken ct)
    {
        var (states, _) = await StatesCoreAsync(w, ct);
        var atms = await AtmsAsync(w, ct);
        var cid = KtcJson.NormId(atmId ?? "");
        async Task<(JsonElement? Raw, DisputeDto? Dto, string Source)> FromClient(string c)
        {
            var (rows, warn, _) = await ListAsync($"{Uri.EscapeDataString(c)}/ClientDisputes", Math.Max(_opt.LookbackDays, _opt.DetailLookupDays), ct);
            w.AddRange(warn);
            foreach (var r in rows) if (KtcJson.NormId(KtcJson.Str(r, DisputeRules.IdF)) == KtcJson.NormId(id)) return (r, MapRow(r, states, atms), $"GET {Root}/{c}/ClientDisputes");
            return (null, null, "");
        }
        async Task<(JsonElement? Raw, DisputeDto? Dto, string Source)> ById()
        {
            var rec = KtcJson.Items(await Fetch($"{Root}/Dispute/{Uri.EscapeDataString(id)}", w, ct: ct)).FirstOrDefault(e => e.ValueKind == JsonValueKind.Object);
            if (rec.ValueKind != JsonValueKind.Object || KtcJson.Str(rec, DisputeRules.IdF).Length == 0) return (null, null, "");
            return (rec.Clone(), MapRow(rec, states, atms), $"GET {Root}/Dispute/{id}");
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
        return new DisputeDetails(d, DisputeRules.Fields(e, maps), DisputeRules.MapFields(e, DisputeRules.CustomerF), DisputeRules.MapFields(e, DisputeRules.ExtraDataF),
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
        var rules = Fetch($"{Root}/DisputeRule", w, ct: ct); var tpl = Fetch($"{Root}/DisputeMessageTemplate", w, ct: ct);
        await Task.WhenAll(rules, tpl);
        static List<IReadOnlyList<KtcField>> All(JsonElement? j) => KtcJson.Items(j).Where(x => x.ValueKind == JsonValueKind.Object).Select(x => (IReadOnlyList<KtcField>)DisputeRules.Fields(x)).ToList();
        return new DisputeSetup(states, All(raw), All(rules.Result), All(tpl.Result), w);
    }
}
