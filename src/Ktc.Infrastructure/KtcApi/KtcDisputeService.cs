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
/// Disputes menu, read-only. List: POST api/v1/DisputeManagement/Search once per KTC business (like Ticket/Search), with every state id
/// from GET DisputeState. Details: GET Dispute/{id} and the dispute's entries in GET SMSQueue. Set-up: GET DisputeState and DisputeRule.
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

    private Task<List<int>> BusinessIdsAsync(List<string> w, CancellationToken ct) =>
        Cached("business-ids", TimeSpan.FromMinutes(10), async () =>
        {
            var json = await Fetch("api/v1/Business?includeDefault=false", w, ct: ct);
            return KtcJson.Items(json).Select(e => KtcJson.Int(e, -1, "ID", "Id", "BusinessID")).Where(i => i >= 0).Distinct().Take(Math.Max(1, _ktc.MaxBusinessesPerSearch)).ToList();
        }, l => l.Count > 0);

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

    public async Task<DisputeSearchResult> SearchAsync(DisputeQuery q, CancellationToken ct = default)
    {
        var to = q.To ?? DateTime.UtcNow.AddMinutes(5);
        var from = q.From ?? to.AddDays(-Math.Max(1, _opt.LookbackDays));
        var w = new List<string>();
        var (states, _) = await StatesCoreAsync(w, ct);
        var atms = await AtmsAsync(w, ct);
        var (rows, warn) = await Cached($"search:{from:yyyyMMddHHmm}:{to:yyyyMMddHHmm}", TimeSpan.FromSeconds(30), async () =>
        {
            var sw = new List<string>();
            var biz = await BusinessIdsAsync(sw, ct); var gid = await AllClientsGroupAsync(sw, ct);
            var targets = biz.Count == 0 ? new List<int?> { 0 } : biz.Select(i => (int?)i).ToList();
            var ids = states.Select(s => s.Id).ToList();
            if (ids.Count == 0) sw.Add("KTC returned no dispute states (GET DisputeManagement/DisputeState), so the search was sent without a state list.");
            var found = new List<JsonElement>();
            using var gate = new SemaphoreSlim(Math.Max(1, _ktc.MaxParallelCalls));
            await Task.WhenAll(targets.Select(async b =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var json = await Fetch($"{Root}/Search", sw, HttpMethod.Post, DisputeRules.SearchBody(b, gid, from, to, ids), ct);
                    var items = KtcJson.Items(json).Select(e => e.Clone()).ToList();
                    lock (found) found.AddRange(items);
                }
                finally { gate.Release(); }
            }));
            return (found, sw);
        }, x => x.Item2.Count == 0);
        w.AddRange(warn);
        var mapped = rows.Select(e => DisputeRules.Map(e, states, _opt.StateNames, id => atms.TryGetValue(id, out var a) ? (a.Name, a.Client) : null))
            .Where(d => d != null).Select(d => d!).GroupBy(d => d.Id).Select(g => g.First()).ToList();
        if (rows.Count > 0 && mapped.Count == 0 && rows[0].ValueKind == JsonValueKind.Object)
            _log.LogWarning("KTC returned {Rows} dispute records but none had an id; fields of the first: {Fields}", rows.Count, string.Join(", ", rows[0].EnumerateObject().Take(40).Select(p => p.Name)));

        Func<DisputeDto, bool>? atmMatch = null;
        if (!string.IsNullOrWhiteSpace(q.AtmId))
        {
            var t = q.AtmId.Trim(); var norm = KtcJson.NormId(t);
            atmMatch = d => d.AtmId == norm || d.AtmName.Contains(t, StringComparison.OrdinalIgnoreCase);
        }
        var amountKnown = mapped.Count == 0 || mapped.Any(d => d.Amount != null);
        if ((q.MinAmount != null || q.MaxAmount != null) && !amountKnown)
            w.Add("The amount filter could not be applied: KTC's dispute records carry no amount field the portal recognises. Send a sample record so it can be mapped.");
        var list = DisputeRules.Filter(mapped, amountKnown ? q : q with { MinAmount = null, MaxAmount = null }, atmMatch)
            .OrderByDescending(d => d.CreatedUtc ?? d.TransactionUtc ?? DateTime.MinValue).Take(Math.Max(1, _opt.MaxRows)).ToList();
        return new DisputeSearchResult(list, states, w.Distinct().ToList(), new[] { $"POST {Root}/Search", $"GET {Root}/DisputeState" }, amountKnown);
    }

    // ---------------- one dispute ----------------

    private async Task<(JsonElement? Raw, DisputeDto? Dto, List<DisputeStateDto> States)> ReadAsync(string id, List<string> w, CancellationToken ct)
    {
        var (states, _) = await StatesCoreAsync(w, ct);
        var atms = await AtmsAsync(w, ct);
        var json = await Fetch($"{Root}/Dispute/{Uri.EscapeDataString(id)}", w, ct: ct);
        var rec = KtcJson.Items(json).FirstOrDefault(e => e.ValueKind == JsonValueKind.Object);
        if (rec.ValueKind != JsonValueKind.Object) return (null, null, states);
        var dto = DisputeRules.Map(rec, states, _opt.StateNames, x => atms.TryGetValue(x, out var a) ? (a.Name, a.Client) : null);
        return (rec.Clone(), dto, states);
    }

    public async Task<DisputeDto?> GetAsync(string id, CancellationToken ct = default) => (await ReadAsync(id, new(), ct)).Dto;

    public async Task<DisputeDetails?> GetDetailsAsync(string id, CancellationToken ct = default)
    {
        var w = new List<string>();
        var (raw, d, _) = await ReadAsync(id, w, ct);
        if (raw is not { } e || d == null) return null;
        var sources = new List<string> { $"GET {Root}/Dispute/{id}" };
        // KTC's GET SMSQueue lists queued customer messages; only this dispute's entries are shown. POST SMSQueue/Disputes is never
        // called because it may queue messages to customers.
        var sms = await Cached("sms", TimeSpan.FromMinutes(1), async () => (await Fetch($"{Root}/SMSQueue", w, ct: ct))?.Clone(), x => x != null);
        sources.Add($"GET {Root}/SMSQueue");
        var maps = DisputeRules.CustomerF.Concat(DisputeRules.ExtraDataF).Concat(DisputeRules.SessionF).Concat(DisputeRules.TransactionF).Concat(DisputeRules.HistoryF)
            .Concat(new[] { "Comments", "CommentList", "CommentsString", "CommentString" }).ToArray();
        return new DisputeDetails(d, DisputeRules.Fields(e, maps), DisputeRules.MapFields(e, DisputeRules.CustomerF), DisputeRules.MapFields(e, DisputeRules.ExtraDataF),
            DisputeRules.MapFields(e, DisputeRules.SessionF), DisputeRules.MapFields(e, DisputeRules.TransactionF), TicketRules.Comments(e), DisputeRules.History(e),
            DisputeRules.NotificationsFor(sms, d.Id), sources, w.Distinct().ToList());
    }

    public async Task<DisputeSetup> GetSetupAsync(CancellationToken ct = default)
    {
        var w = new List<string>();
        var (states, raw) = await StatesCoreAsync(w, ct);
        var rules = await Fetch($"{Root}/DisputeRule", w, ct: ct);
        return new DisputeSetup(states, KtcJson.Items(raw).Where(x => x.ValueKind == JsonValueKind.Object).Select(x => (IReadOnlyList<KtcField>)DisputeRules.Fields(x)).ToList(),
            KtcJson.Items(rules).Where(x => x.ValueKind == JsonValueKind.Object).Select(x => (IReadOnlyList<KtcField>)DisputeRules.Fields(x)).ToList(), w);
    }
}
