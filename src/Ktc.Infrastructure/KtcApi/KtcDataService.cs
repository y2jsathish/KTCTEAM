using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ktc.Application.Abstractions;
using Ktc.Application.Logic;
using Ktc.Application.Models;
using Ktc.Application.Options;
using Ktc.Domain.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ktc.Infrastructure.KtcApi;

/// <summary>
/// Maps KTC REST payloads to portal DTOs.
/// KTC exposes no "Device" or "Incident" controllers: devices are <c>Client</c> records (+LastClientContact, OverallAvailability),
/// incidents are <c>ActiveError/ActiveErrorExtra/HistoricalError</c>, and the "client" of the portal (bank) is <c>Business</c>.
/// Field names are looked up tolerantly (see KtcJson) because the supplied collection has no response samples.
/// </summary>
public class KtcDataService : IKtcDataService
{
    private readonly IKtcApiClient _api;
    private readonly KtcOptions _opt;
    private readonly IMemoryCache _cache;
    private readonly IHttpContextAccessor _http;
    private readonly ILogger<KtcDataService> _log;

    public KtcDataService(IKtcApiClient api, IOptions<KtcOptions> opt, IMemoryCache cache, IHttpContextAccessor http, ILogger<KtcDataService> log)
    { _api = api; _opt = opt.Value; _cache = cache; _http = http; _log = log; }

    private record ClientRec(string Id, string Name, string Alias, string Address, string Business, string Branch, string City, string Region, string HardwareType);

    // Cache partition: user-specific unless the service account drives the data.
    private string Scope => KtcTokenScope.IsServiceForced || !(_http.HttpContext?.User.Identity?.IsAuthenticated ?? false) ? "svc" : "u:" + _http.HttpContext!.User.Identity!.Name;

    private sealed class Warn { private readonly List<string> _l = new(); public void Add(string s) { lock (_l) _l.Add(s); } public List<string> ToList() { lock (_l) return _l.ToList(); } }

    private async Task<JsonElement?> Fetch(string url, Warn w, HttpMethod? m = null, object? body = null, bool idempotent = false, CancellationToken ct = default)
    {
        var r = await _api.SendAsync(m ?? HttpMethod.Get, url, body, new KtcCallOptions { Idempotent = idempotent }, ct);
        if (!r.Success) { w.Add($"{(m ?? HttpMethod.Get).Method} {url.Split('?')[0]}: {r.Error}"); return null; }
        return r.Json;
    }

    /// <summary>
    /// GET a route whose query carries dates. If KTC cannot parse the date format, tries the other formats in
    /// <see cref="KtcDateFormats.Candidates"/> once and remembers the first that KTC accepts.
    /// </summary>
    private async Task<KtcResponse> GetWithDatesAsync(Func<Func<DateTime, string>, string> url, CancellationToken ct)
    {
        // A route that fails on the KTC server for every request (a server-side error, not our parameters) is paused so
        // per-ATM widgets do not send hundreds of failing calls and trip API alerts each refresh.
        var routeKey = ApiHealthTracker.Normalize(url(_ => "x").Split('?')[0]);
        return await PausableAsync(routeKey, () => GetWithDatesCoreAsync(url, ct));
    }

    /// <summary>Runs a KTC call, pausing its route for <see cref="BrokenFor"/> after 3 server-side failures in a row.</summary>
    private async Task<KtcResponse> PausableAsync(string routeKey, Func<Task<KtcResponse>> call)
    {
        if (BrokenRoutes.TryGetValue(routeKey, out var paused) && paused.Until > DateTime.UtcNow) return paused.Response;
        var res = await call();
        if (res.StatusCode >= 500 && res.StatusCode != 503 && res.StatusCode != 504)
        {
            var n = BrokenCounts.AddOrUpdate(routeKey, 1, (_, c) => c + 1);
            if (n >= 3)
            {
                var msg = KtcResponse.Fail($"KTC server error on {routeKey} ({res.Error}). Paused for {BrokenFor.TotalMinutes:0} min; this is a KTC-side problem, not the request.", res.StatusCode, res.ElapsedMs);
                BrokenRoutes[routeKey] = (DateTime.UtcNow + BrokenFor, msg);
                BrokenCounts.TryRemove(routeKey, out _);
                _log.LogWarning("KTC {Route} keeps failing on the server ({Error}); pausing it for {Minutes} min", routeKey, res.Error, BrokenFor.TotalMinutes);
                return msg;
            }
        }
        else if (res.Success) BrokenCounts.TryRemove(routeKey, out _);
        return res;
    }
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Until, KtcResponse Response)> BrokenRoutes = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> BrokenCounts = new();
    private static readonly TimeSpan BrokenFor = TimeSpan.FromMinutes(30);

    private async Task<KtcResponse> GetWithDatesCoreAsync(Func<Func<DateTime, string>, string> url, CancellationToken ct)
    {
        string F(DateTime d, string f) => KtcDateFormats.Format(d, f, _opt.QueryTimeZone);
        var current = KtcDateFormats.Current(_opt);
        var r = await _api.GetAsync(url(d => F(d, current)), ct);
        if (!KtcDateFormats.IsDateError(r) || KtcDateFormats.Resolved != null) return r;
        await DateProbeGate.WaitAsync(ct);
        try
        {
            if (KtcDateFormats.Resolved is { } other) return await _api.GetAsync(url(d => F(d, other)), ct);
            foreach (var f in KtcDateFormats.Candidates.Where(c => c != current))
            {
                var t = await _api.GetAsync(url(d => F(d, f)), ct);
                if (KtcDateFormats.IsDateError(t)) continue;
                if (t.StatusCode < 500) { KtcDateFormats.Remember(f); _log.LogInformation("KTC accepts query dates as {Format}; using it from now on (set Ktc:QueryDateFormat to keep it).", f); }
                return t;
            }
            _log.LogWarning("KTC rejected every known query date format for {Url}", url(d => "{date}"));
            return r;
        }
        finally { DateProbeGate.Release(); }
    }
    private static readonly SemaphoreSlim DateProbeGate = new(1, 1);

    /// <summary>
    /// KTC's HistoricalError routes have no query parameters: the range is a DateTimePair JSON body {"dt1","dt2"} on the GET.
    /// Sends it in the remembered format; if KTC fails on it, tries the other body formats once and keeps the first that works.
    /// </summary>
    private async Task<KtcResponse> HistoricalErrorCoreAsync(string url, DateTime from, DateTime to, CancellationToken ct)
    {
        Task<KtcResponse> Send(string f) => _api.SendAsync(HttpMethod.Get, url, KtcDateFormats.DateTimePair(from, to, f, _opt.QueryTimeZone), new KtcCallOptions { Idempotent = true }, ct);
        bool ServerFailed(KtcResponse x) => x.StatusCode >= 500 && x.StatusCode != 503 && x.StatusCode != 504;
        var current = KtcDateFormats.CurrentBody;
        var r = await Send(current);
        if ((!ServerFailed(r) && r.StatusCode != 400) || KtcDateFormats.ResolvedBody != null) return r;
        await DateProbeGate.WaitAsync(ct);
        try
        {
            if (KtcDateFormats.ResolvedBody is { } other) return await Send(other);
            foreach (var f in KtcDateFormats.BodyCandidates.Where(c => c != current))
            {
                var t = await Send(f);
                if (ServerFailed(t) || t.StatusCode == 400) continue;
                if (t.Success) { KtcDateFormats.RememberBody(f); _log.LogInformation("KTC HistoricalError accepts dt1/dt2 dates as {Format}; using it from now on.", f); }
                return t;
            }
            _log.LogWarning("KTC {Url} failed with every dt1/dt2 date format: {Error}", url, r.Error);
            return r;
        }
        finally { DateProbeGate.Release(); }
    }

    /// <summary>ToDictionary that tolerates missing/duplicate ids (first one wins) instead of throwing.</summary>
    private static Dictionary<string, V> ToMap<V>(IEnumerable<JsonElement> items, Func<JsonElement, string> key, Func<JsonElement, V> val)
    {
        var d = new Dictionary<string, V>();
        foreach (var e in items) { var k = KtcJson.NormId(key(e)); if (k.Length > 0) d.TryAdd(k, val(e)); }
        return d;
    }

    /// <summary>
    /// KTC Ticket/Search and TransactionData/Search refuse a search without a business ("KXArgumentException: ... there is no
    /// business ID in the search parameters"), so searches run once per business from GET Business and are merged.
    /// </summary>
    private Task<List<int>> BusinessIdsAsync(Warn w, CancellationToken ct) =>
        Cached("business-ids", TimeSpan.FromMinutes(10), async () =>
        {
            var json = await Fetch("api/v1/Business?includeDefault=false", w, ct: ct);
            var ids = KtcJson.Items(json).Select(e => KtcJson.Int(e, -1, "ID", "Id", "BusinessID")).Where(i => i >= 0).Distinct().Take(Math.Max(1, _opt.MaxBusinessesPerSearch)).ToList();
            if (ids.Count == 0)
            {
                json = await Fetch("api/v1/Business?includeDefault=true", w, ct: ct);
                ids = KtcJson.Items(json).Select(e => KtcJson.Int(e, -1, "ID", "Id", "BusinessID")).Where(i => i >= 0).Distinct().Take(Math.Max(1, _opt.MaxBusinessesPerSearch)).ToList();
            }
            return ids;
        });

    /// <summary>Runs a KTC search once per business and returns all result rows (each tagged with nothing extra; ids stay unique per KTC).</summary>
    private async Task<List<JsonElement>> SearchPerBusinessAsync(string route, Func<int?, object> body, Warn w, CancellationToken ct) =>
        (await SearchRowsPerBusinessAsync(route, body, w, ct)).Select(r => r.Value).ToList();

    /// <summary>Like <see cref="SearchPerBusinessAsync"/> but keeps each record's key when KTC returns an id-keyed map.</summary>
    private async Task<List<(string Key, JsonElement Value)>> SearchRowsPerBusinessAsync(string route, Func<int?, object> body, Warn w, CancellationToken ct)
    {
        var ids = await BusinessIdsAsync(w, ct);
        // KTC lists no business ids on some servers; BusinessID 0 (the default business) is then what it accepts.
        var targets = ids.Count == 0 ? new List<int?> { 0 } : ids.Select(i => (int?)i).ToList();
        var rows = new List<(string Key, JsonElement Value)>();
        using var gate = new SemaphoreSlim(Math.Max(1, _opt.MaxParallelCalls));
        await Task.WhenAll(targets.Select(async b =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var json = await Fetch(route, w, HttpMethod.Post, body(b), true, ct);
                var items = KtcJson.Rows(json).ToList();
                lock (rows) rows.AddRange(items);
            }
            finally { gate.Release(); }
        }));
        return rows;
    }

    /// <summary>The id of KTC's "All Clients" group (2 on standard installs), so searches cover every ATM.</summary>
    private Task<int> AllClientsGroupIdAsync(Warn w, CancellationToken ct) =>
        Cached("group-all", TimeSpan.FromMinutes(30), async () =>
        {
            var json = await Fetch("api/v1/Group", w, ct: ct);
            foreach (var g in KtcJson.Items(json))
                if (Regex.IsMatch(KtcJson.Str(g, "Name", "GroupName"), @"^\s*all\s*clients\s*$", RegexOptions.IgnoreCase))
                {
                    var id = KtcJson.Int(g, -1, "ID", "Id", "GroupID");
                    if (id > 0) return id;
                }
            return 2;
        });

    private Task<T> Cached<T>(string key, TimeSpan ttl, Func<Task<T>> factory) =>
        _cache.GetOrCreateAsync($"{Scope}|{key}", e => { e.AbsoluteExpirationRelativeToNow = ttl; return factory(); })!;

    // ---------------- reference data ----------------
    private async Task<Dictionary<string, ClientRec>> LookupAsync(Warn w, CancellationToken ct)
    {
        return await Cached("lookup", TimeSpan.FromMinutes(5), async () =>
        {
            var tc = Fetch("api/v1/Client?includedeleted=false", w, ct: ct); var tb = Fetch("api/v1/Business?includeDefault=false", w, ct: ct);
            var tr = Fetch("api/v1/Region?includeDefault=false", w, ct: ct); var tbr = Fetch("api/v1/Branch?includeDefault=false", w, ct: ct);
            await Task.WhenAll(tc, tb, tr, tbr);
            var biz = ToMap(KtcJson.Items(tb.Result), e => KtcJson.Str(e, "ID", "Id", "BusinessID"), e => KtcJson.Str(e, "Name", "BusinessName"));
            var reg = ToMap(KtcJson.Items(tr.Result), e => KtcJson.Str(e, "ID", "Id", "RegionID"), e => KtcJson.Str(e, "Name", "RegionName"));
            var br = ToMap(KtcJson.Items(tbr.Result), e => KtcJson.Str(e, "ID", "Id", "BranchID"), e => (Name: KtcJson.Str(e, "Name", "BranchName"), City: KtcJson.Str(e, "City", "Town")));
            var map = new Dictionary<string, ClientRec>();
            foreach (var e in KtcJson.Items(tc.Result))
            {
                var id = KtcJson.NormId(KtcJson.Str(e, "ClientID", "ClientId", "ID", "Id"));
                if (id.Length == 0 || KtcJson.Bool(e, false, "Deleted", "IsDeleted")) continue;
                br.TryGetValue(KtcJson.Str(e, "BranchID", "BranchId"), out var b);
                var rid = KtcJson.Str(e, "RegionID", "RegionId", "Level1RegionID", "Level2RegionID");
                map[id] = new ClientRec(id, KtcJson.Str(e, "Name", "NodeName", "ClientName"), KtcJson.Str(e, "Alias", "DisplayName"), KtcJson.Str(e, "Address", "NetworkAddress"),
                    biz.GetValueOrDefault(KtcJson.Str(e, "BusinessID", "BusinessId")) ?? "", b.Name ?? "", string.IsNullOrEmpty(b.City) ? (b.Name ?? "") : b.City,
                    reg.GetValueOrDefault(rid) ?? "", KtcJson.Str(e, "HardwareTypeName", "HardwareType", "ModelName"));
            }
            return map;
        });
    }

    /// <summary>
    /// KTC client ids an ATM search text refers to: the id itself, or any ATM whose name, alias, address or branch contains the
    /// text (case-insensitive). Transactions and EJ carry only the client id, so names must be resolved through the client list.
    /// </summary>
    private static HashSet<string> MatchAtms(Dictionary<string, ClientRec> lookup, string text)
    {
        var t = text.Trim(); var norm = KtcJson.NormId(t);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (lookup.ContainsKey(norm) || long.TryParse(norm, out _)) ids.Add(norm);
        foreach (var c in lookup.Values.Where(c => Exact(c, t))) ids.Add(c.Id);
        foreach (var c in lookup.Values)
            if (new[] { c.Name, c.Alias, c.Address, c.Branch }.Any(v => v.Length > 0 && v.Contains(t, StringComparison.OrdinalIgnoreCase))) ids.Add(c.Id);
        return ids;
    }
    private static bool Exact(ClientRec c, string t) =>
        c.Name.Equals(t, StringComparison.OrdinalIgnoreCase) || c.Alias.Equals(t, StringComparison.OrdinalIgnoreCase);

    // ---------------- ATMs ----------------
    public async Task<(IReadOnlyList<AtmDto> Atms, List<string> Warnings)> GetAtmsAsync(CancellationToken ct = default)
    {
        var w = new Warn();
        var tLookup = LookupAsync(w, ct);
        var tContact = Fetch("api/v1/LastClientContact", w, ct: ct);
        var tInc = GetIncidentsInternal(true, w, ct);
        var tTk = TicketsCore(w, ct);
        await Task.WhenAll(tLookup, tContact, tInc, tTk);

        var contacts = new Dictionary<string, DateTime>();
        foreach (var (rawId, v) in KtcJson.ById(tContact.Result, "ClientID", "ClientId", "CID", "ID", "Id"))
        {
            var d = KtcJson.DateValue(v, "LastContact", "LastClientContact", "LastContactTime", "LastContactTimeStamp", "Timestamp", "TimeStamp", "Time", "DateTime", "LCT", "LastTime", "Last")
                    ?? KtcJson.AnyDate(v);
            if (d.HasValue) contacts[KtcJson.NormId(rawId)] = d.Value;
        }
        // Only trust contact ages when the response could be matched to the client list; otherwise every ATM would look offline.
        var contactsKnown = contacts.Count > 0 && tLookup.Result.Keys.Any(contacts.ContainsKey);
        if (tContact.Result != null && !contactsKnown) w.Add("GET api/v1/LastClientContact: response could not be matched to the client list, so contact times are ignored (run /diag/ktc-token and send the report)");
        var incByAtm = tInc.Result.Where(i => i.IsOpen).ToLookup(i => i.AtmId);
        var tkByAtm = tTk.Result.Where(t => t.IsOpen).ToLookup(t => t.AtmId);
        var avail = await AvailabilityBatchAsync(tLookup.Result.Keys, w, ct);
        var now = DateTime.UtcNow;

        var list = new List<AtmDto>(tLookup.Result.Count);
        foreach (var c in tLookup.Result.Values)
        {
            var inc = incByAtm[c.Id].ToList();
            DateTime? last = contacts.TryGetValue(c.Id, out var d) ? d : null;
            bool commFault = inc.Any(i => Regex.IsMatch(i.Description, @"communication failure|link down|offline|unreachable device", RegexOptions.IgnoreCase) && !Regex.IsMatch(i.Description, "host", RegexOptions.IgnoreCase));
            // Without LastClientContact (e.g. the KTC user lacks the Administrator/MergeClients role) contact age is unknown:
            // judge the ATM on its incidents only instead of marking every ATM offline.
            bool offline = commFault || (contactsKnown && (!last.HasValue || (now - last.Value).TotalMinutes > _opt.OfflineMinutes));
            bool host = inc.Any(i => i.HostRelated && Regex.IsMatch(i.Description, @"disconnect|unreachable|down|no response|timeout", RegexOptions.IgnoreCase));
            var hr = AtmHealthCalculator.Evaluate(new HealthInput(contactsKnown ? last : now, offline, host,
                inc.Count(i => i.Severity == Severity.P1), inc.Count(i => i.Severity == Severity.P2), inc.Count(i => i.Severity == Severity.P3), inc.Count(i => i.Severity == Severity.P4)), now, _opt.AtmOnlineMinutes);
            list.Add(new AtmDto
            {
                Id = c.Id, Name = c.Name, Client = c.Business, Branch = c.Branch, City = c.City, Region = c.Region, AtmType = c.HardwareType,
                Location = string.IsNullOrEmpty(c.Alias) ? c.Branch : c.Alias, LastCommunicationUtc = last,
                Health = hr.Status, HealthScore = hr.Score, HealthReason = hr.Reason, Offline = offline, HostDisconnected = host,
                DeviceStatus = offline ? "Offline" : host ? "Host Disconnected" : hr.Status == HealthStatus.Warning ? "Degraded" : hr.Status == HealthStatus.Critical ? "Faulted" : "Online",
                AvailabilityPct = avail.TryGetValue(c.Id, out var a) ? a : null, OpenIncidents = inc.Count, OpenTickets = tkByAtm[c.Id].Count()
            });
        }
        return (list.OrderBy(a => a.Name).ToList(), w.ToList());
    }

    public async Task<AtmDto?> GetAtmAsync(string id, CancellationToken ct = default)
    {
        var (atms, _) = await GetAtmsAsync(ct);
        return atms.FirstOrDefault(a => a.Id == id) ?? atms.FirstOrDefault(a => a.Name.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<Dictionary<string, double>> AvailabilityBatchAsync(IEnumerable<string> ids, Warn w, CancellationToken ct)
    {
        var res = new Dictionary<string, double>();
        var missing = new List<string>();
        foreach (var id in ids)
        {
            if (_cache.TryGetValue($"avail|{Scope}|{id}", out double v)) res[id] = v; else missing.Add(id);
        }
        var batch = missing.Take(_opt.AvailabilityWarmupPerCycle).ToList();      // warm the cache progressively; protects KTC from N calls per refresh
        using var gate = new SemaphoreSlim(Math.Max(1, _opt.MaxParallelCalls));
        var failures = 0;
        await Task.WhenAll(batch.Select(async id =>
        {
            await gate.WaitAsync(ct);
            try
            {
                if (Volatile.Read(ref failures) > 2) return;               // KTC is unhealthy - stop hammering it
                var a = await AvailabilityValueAsync(id, DateTime.UtcNow.AddDays(-_opt.AvailabilityLookbackDays), DateTime.UtcNow, ct);
                if (a.HasValue) { lock (res) res[id] = a.Value; _cache.Set($"avail|{Scope}|{id}", a.Value, TimeSpan.FromMinutes(_opt.AvailabilityCacheMinutes)); }
                else Interlocked.Increment(ref failures);
            }
            finally { gate.Release(); }
        }));
        if (failures > 0) w.Add(BrokenRoutes.Keys.Any(k => k.Contains("OverallAvailability"))
            ? "Availability is not available: the KTC server fails on Client/{id}/OverallAvailability (server-side error). Values shown as n/a."
            : $"Availability lookup failed for {failures} ATM(s); values shown as n/a.");
        return res;
    }

    private async Task<double?> AvailabilityValueAsync(string id, DateTime from, DateTime to, CancellationToken ct)
    {
        var r = await GetWithDatesAsync(f => $"api/v1/Client/{id}/OverallAvailability?start={f(from)}&end={f(to)}&starttimeindex=0&endtimeindex=0", ct);
        if (!r.Success || r.Json is not { } j) return null;
        if (j.ValueKind == JsonValueKind.Number) return Norm(j.GetDouble());
        foreach (var e in KtcJson.Items(j))
        {
            var d = KtcJson.Dbl(e, double.NaN, "AvailabilityPercent", "AvailabilityPercentage", "Availability", "OverallAvailability", "Percentage", "Percent");
            if (!double.IsNaN(d)) return Norm(d);
        }
        return null;
        static double Norm(double d) => Math.Round(Math.Clamp(d <= 1.0 && d > 0 ? d * 100 : d, 0, 100), 2);
    }

    // ---------------- hardware ----------------
    /// <summary>
    /// Per-component status of one ATM. KTC keeps the current value of every component property in
    /// GET Client/{id}/CurrentStatus (records like {CompId, PropId, VID, ManualValue, NumericValue, UseNumeric, Timestamp});
    /// component and property names come from ComponentList / PropertyList, and open errors are pinned to the component they mention.
    /// </summary>
    public async Task<AtmHardware?> GetHardwareAsync(string atmId, CancellationToken ct = default)
    {
        var w = new Warn(); var lookup = await LookupAsync(w, ct);
        var norm = KtcJson.NormId(atmId);
        var id = lookup.ContainsKey(norm) ? norm : lookup.Values.FirstOrDefault(c => Exact(c, atmId.Trim()))?.Id ?? MatchAtms(lookup, atmId).FirstOrDefault()
                 ?? (long.TryParse(norm, out _) ? norm : null);
        if (id == null) return null;
        lookup.TryGetValue(id, out var atm);

        var tStatus = Fetch($"api/v1/Client/{id}/CurrentStatus", w, ct: ct);
        var tInc = GetIncidentsInternal(true, w, ct);
        await Task.WhenAll(tStatus, tInc);
        var rows = KtcJson.Rows(tStatus.Result).Where(r => r.Value.ValueKind == JsonValueKind.Object).ToList();
        var names = await HardwareNamesAsync(rows.Select(r => r.Value).ToList(), ct);
        var comps = names.Components; var props = names.Properties;

        var byComp = new Dictionary<string, List<HardwareProperty>>();
        var compTime = new Dictionary<string, DateTime?>();
        var compIdByName = new Dictionary<string, string>();
        foreach (var (key, e) in rows)
        {
            var cid = KtcJson.NormId(KtcJson.Str(e, CompIdFields));
            var compName = KtcJson.Str(e, "ComponentName", "Component") is { Length: > 0 } cn ? cn : comps.GetValueOrDefault(cid) ?? (cid.Length > 0 ? "Component " + cid : "Device");
            var pid = KtcJson.NormId(KtcJson.Str(e, PropIdFields));
            var pinfo = props.GetValueOrDefault(pid);
            var propName = KtcJson.Str(e, "PropertyName", "Property") is { Length: > 0 } pn ? pn : pinfo?.Name ?? (pid.Length > 0 ? "Property " + pid : key);
            var value = HardwareValue(e, pinfo, names.Values, propName);
            var when = KtcJson.Date(e, "Timestamp", "TimeStamp", "LastUpdated", "Time");
            var status = HardwareStatusRules.Classify(value);
            if (status == HardwareStatusRules.Unknown && HardwareStatusRules.Classify(propName) is var byName && byName != HardwareStatusRules.Unknown
                && KtcJson.Bool(e, false, "UseNumeric") && KtcJson.Dbl(e, 0, "NumericValue") is var nv)
                status = nv == 0 ? HardwareStatusRules.Ok : byName;   // e.g. "Fatal error count" = 0 is healthy
            if (cid.Length > 0) compIdByName.TryAdd(compName, cid);
            var list = byComp.TryGetValue(compName, out var l) ? l : byComp[compName] = new List<HardwareProperty>();
            list.Add(new HardwareProperty(propName, value, status, when));
            if (when > (compTime.GetValueOrDefault(compName) ?? DateTime.MinValue)) compTime[compName] = when;
        }

        // Open errors of this ATM, pinned to the component whose name (or a known keyword) the error text mentions.
        var errors = tInc.Result.Where(i => i.AtmId == id).ToList();
        var errByModule = new Dictionary<string, List<(string Text, string Status)>>();
        foreach (var i in errors)
        {
            var text = i.Description.Length > 0 ? i.Description : i.ErrorCode;
            var module = byComp.Keys.FirstOrDefault(m => HardwareStatusRules.Mentions(text, m)) ?? HardwareStatusRules.ModuleFor(text) ?? "Other active errors";
            (errByModule.TryGetValue(module, out var el) ? el : errByModule[module] = new()).Add(($"{text} (since {i.OpenedUtc:yyyy-MM-dd HH:mm} UTC)", HardwareStatusRules.FromSeverity((int)i.Severity)));
        }

        var modules = byComp.Keys.Union(errByModule.Keys).Select(m =>
        {
            var ps = byComp.GetValueOrDefault(m) ?? new List<HardwareProperty>();
            var es = errByModule.GetValueOrDefault(m) ?? new();
            var st = HardwareStatusRules.Worst(ps.Select(p => p.Status).Concat(es.Select(x => x.Status)));
            return new HardwareModule(compIdByName.GetValueOrDefault(m) ?? m, m, st, compTime.GetValueOrDefault(m), ps.OrderByDescending(p => HardwareStatusRules.Rank(p.Status)).ThenBy(p => p.Name).ToList(), es.Select(x => x.Text).ToList());
        }).OrderByDescending(m => HardwareStatusRules.Rank(m.Status)).ThenBy(m => m.Name).ToList();

        var warnings = w.ToList();
        if (rows.Count > 0 && comps.Count == 0) warnings.Add("KTC did not return component names (ComponentList answered with an error), so components are shown by id. Open /diag/ktc-token and send the Hardware section.");
        if (modules.Count == 0 && tStatus.Result != null) warnings.Add("KTC returned no component status for this ATM.");
        return new AtmHardware(id, atm?.Name ?? id, HardwareStatusRules.Worst(modules.Select(m => m.Status)), compTime.Values.Where(v => v.HasValue).DefaultIfEmpty().Max(), modules, warnings);
    }

    // ---------------- general details ----------------
    private static readonly HashSet<string> Secret = new(StringComparer.OrdinalIgnoreCase) { "Salt", "AuthHash", "Password", "Secret", "Token" };

    /// <summary>
    /// General details of one ATM from GET Client/{id} (or the client list): name, GUID, IP (Address), subnet, latitude / longitude, time zone,
    /// business, branch (with the branch record's address fields), regions level 1-5, hardware, flags. Ids are named through GET Business /
    /// Branch / Region; a field KTC leaves empty (or 0 for an id) is kept and listed as not set. Salt / AuthHash are never shown.
    /// </summary>
    public async Task<AtmGeneral?> GetAtmGeneralAsync(string atmId, CancellationToken ct = default)
    {
        var w = new Warn(); var lookup = await LookupAsync(w, ct);
        var norm = KtcJson.NormId(atmId);
        var id = lookup.ContainsKey(norm) ? norm : lookup.Values.FirstOrDefault(c => Exact(c, atmId.Trim()))?.Id ?? MatchAtms(lookup, atmId).FirstOrDefault()
                 ?? (long.TryParse(norm, out _) ? norm : null);
        if (id == null) return null;

        var tRec = Fetch($"api/v1/Client/{id}", new Warn(), ct: ct);
        var tBiz = Cached("gen-business", TimeSpan.FromMinutes(30), async () => KtcJson.Items(await Quiet("api/v1/Business?includeDefault=false", ct)).ToList());
        var tBr = Cached("gen-branch", TimeSpan.FromMinutes(30), async () => KtcJson.Items(await Quiet("api/v1/Branch?includeDefault=false", ct)).ToList());
        var tReg = Cached("gen-region", TimeSpan.FromMinutes(30), async () => KtcJson.Items(await Quiet("api/v1/Region?includeDefault=false", ct)).ToList());
        await Task.WhenAll(tRec, tBiz, tBr, tReg);
        JsonElement? rec = KtcJson.Items(tRec.Result).FirstOrDefault(e => e.ValueKind == JsonValueKind.Object) is { ValueKind: JsonValueKind.Object } r1 ? r1 : null;
        if (rec == null)
        {
            // Client/{id} not readable: take the record from the client list.
            var all = await Fetch("api/v1/Client?includedeleted=false", w, ct: ct);
            rec = KtcJson.Items(all).FirstOrDefault(e => e.ValueKind == JsonValueKind.Object && KtcJson.NormId(KtcJson.Str(e, "ClientID", "ClientId", "ID", "Id")) == id) is { ValueKind: JsonValueKind.Object } r2 ? r2 : null;
            if (rec == null) w.Add($"KTC returned no Client record for {id} (GET Client/{id} and the client list).");
        }
        static string IdOf(JsonElement e, params string[] f) => KtcJson.NormId(KtcJson.Str(e, f.Concat(new[] { "ID", "Id" }).ToArray()));
        JsonElement? Find(List<JsonElement> list, string key, string[] f) => key is "" or "0" ? null : list.FirstOrDefault(e => e.ValueKind == JsonValueKind.Object && IdOf(e, f) == key) is { ValueKind: JsonValueKind.Object } x ? x : null;
        string NameOf(JsonElement? e) => e is { } x ? KtcJson.Str(x, "Name", "DisplayName", "BusinessName", "BranchName", "RegionName", "Description") : "";

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string Raw(params string[] names) { foreach (var n in names) used.Add(n); return rec is { } r ? KtcJson.Str(r, names) : ""; }
        static bool Empty(string v) => v.Length == 0 || v == "0" || v == "0001-01-01T00:00:00" || v.Equals("<Comments />", StringComparison.OrdinalIgnoreCase);
        DetailField F(string label, string value, string? id = null) => new(label, Empty(value) ? "" : Guid.TryParse(value, out _) ? value : PanMasker.Mask(value), id);
        DetailField Named(string label, string rawId, List<JsonElement> list, string[] f)
        {
            if (Empty(rawId)) return new DetailField(label, "", null);
            var n = NameOf(Find(list, KtcJson.NormId(rawId), f));
            return new DetailField(label, n.Length > 0 ? n : $"id {rawId} (no name in KTC)", rawId);
        }

        var lat = KtcJson.Dbl(rec ?? default, double.NaN, "Latitude", "Lat"); var lon = KtcJson.Dbl(rec ?? default, double.NaN, "Longitude", "Lon", "Lng");
        used.UnionWith(new[] { "Latitude", "Lat", "Longitude", "Lon", "Lng" });
        bool hasPos = !double.IsNaN(lat) && !double.IsNaN(lon) && !(lat == 0 && lon == 0);
        string Coord(double v) => double.IsNaN(v) || !hasPos ? "" : v.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

        var branchId = Raw("BranchID", "BranchId");
        var branch = Find(tBr.Result, KtcJson.NormId(branchId), new[] { "BranchID" });
        var location = new List<DetailField>
        {
            Named("Branch", branchId, tBr.Result, new[] { "BranchID" }),
        };
        if (branch is { } b)
            foreach (var (path, value) in SchemaInferer.Leaves(b))
                if (!Regex.IsMatch(path, @"(^|\.)(ID|Id|BranchID|Name|BranchName|Deleted.*|IsDeleted|BusinessID|.*RegionID)$") && !Secret.Contains(path) && value.Length > 0)
                    location.Add(F("Branch " + TxDetailRules.Pretty(path).ToLowerInvariant(), value));
        for (int lv = 1; lv <= 5; lv++)
        {
            var rid = Raw($"Level{lv}RegionID", $"Level{lv}RegionId");
            if (lv == 1 && Empty(rid)) rid = Raw("RegionID", "RegionId");
            location.Add(Named($"Region level {lv}", rid, tReg.Result, new[] { "RegionID" }));
        }
        location.Add(F("Latitude", Coord(lat))); location.Add(F("Longitude", Coord(lon)));
        location.Add(F("Time zone", Raw("TimeZone", "TimeZoneName")));

        var groups = new List<DetailGroup>
        {
            new("Identity", new List<DetailField>
            {
                F("Name", Raw("Name", "NodeName", "ClientName")), F("Client ID", id), F("Alias", Raw("Alias", "DisplayName")), F("GUID", Raw("GUID", "Guid")),
                F("Hardware", Raw("HardwareTypeName", "HardwareType", "ModelName")), F("Hardware ID", Raw("HardwareID", "HardwareId")),
                F("Type", Raw("TypeByte", "Type")), F("Active", Raw("IsActive", "Active")), F("Owner ID", Raw("OwnerID", "OwnerId")),
            }),
            new("Network", new List<DetailField> { F("IP address", Raw("Address", "NetworkAddress", "IPAddress", "IP")), F("Subnet", Raw("Subnet")), F("Connectable", Raw("ConnectableByte", "Connectable")) }),
            new("Business", new List<DetailField> { Named("Business", Raw("BusinessID", "BusinessId"), tBiz.Result, new[] { "BusinessID" }) }),
            new("Location", location),
        };
        // Everything else the record holds (feature flags, comments, grid, ...), never the secrets.
        if (rec is { } rr)
        {
            var other = rr.EnumerateObject().Where(p => !used.Contains(p.Name) && !Secret.Contains(p.Name) && !Regex.IsMatch(p.Name, "^(ID|Id|ClientID|ClientId)$") && p.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                .Select(p => F(TxDetailRules.Pretty(p.Name), p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString())).ToList();
            if (other.Count > 0) groups.Add(new DetailGroup("Other KTC fields", other));
        }
        var notSet = groups.SelectMany(g => g.Fields).Where(f => f.Value.Length == 0).Select(f => f.Name).ToList();
        _log.LogInformation("ATM general details {Atm} -> {Id}: {Set} values set, not set: {NotSet}", atmId, id, groups.Sum(g => g.Fields.Count) - notSet.Count, string.Join(", ", notSet));
        return new AtmGeneral(id, rec is { } n0 ? KtcJson.Str(n0, "Name", "NodeName", "ClientName") : lookup.GetValueOrDefault(id)?.Name ?? id, groups,
            hasPos ? lat : null, hasPos ? lon : null, notSet, w.ToList());
    }

    // ---------------- counters ----------------
    /// <summary>
    /// Application and CEN/XFS device counters of one ATM, built from standard KTC data:
    /// XFS counters are the counter-like properties (counts, jams, failures, retains, ...) of the XFS device components in
    /// GET Client/{id}/CurrentStatus plus the note counts in GET Client/{id}/CurrentCashUnitStatus; application counters are counted
    /// from POST TransactionData/Search (today and since the last replenishment). Ktc:CountersEndpoint, when set, is tried first.
    /// Never null for a known ATM: missing data gives empty lists with warnings and diagnostics that say what KTC returned.
    /// </summary>
    public async Task<AtmCounters?> GetCountersAsync(string atmId, CancellationToken ct = default)
    {
        var w = new Warn(); var lookup = await LookupAsync(w, ct);
        var norm = KtcJson.NormId(atmId);
        var id = lookup.ContainsKey(norm) ? norm : lookup.Values.FirstOrDefault(c => Exact(c, atmId.Trim()))?.Id ?? MatchAtms(lookup, atmId).FirstOrDefault()
                 ?? (long.TryParse(norm, out _) ? norm : null);
        if (id == null) return null;
        var name = lookup.TryGetValue(id, out var atm) ? atm.Name : id;
        var diag = new List<string>();

        // 1. KTC's counters route (GET Client/{id}/Counters, what KTC's own UI reads): device counters per component.
        var route = _opt.CountersEndpoint?.Trim() ?? "";
        List<ApplicationCounter> routeApp = new(); List<XfsCounter> routeXfs = new(); DateTime? routeReset = null, routeAt = null;
        var idRecs = new List<JsonElement>(); var routePath = ""; var routeDiag = -1;
        if (route.Length > 0)
        {
            var url = route.TrimStart('/').Replace("{atmId}", Uri.EscapeDataString(id), StringComparison.OrdinalIgnoreCase).Replace("{id}", Uri.EscapeDataString(id), StringComparison.OrdinalIgnoreCase);
            var rw = new Warn();
            var raw = await Fetch(url, rw, ct: ct);
            (routeAt, routeReset, routeApp, routeXfs) = CounterRules.Parse(raw);
            var path = routePath = url.Split('?')[0];
            // KTC's Counters records as the live server sends them: {ClientID, DBComponentID, DBPropertyID, DenominationID, CounterValue,
            // Timestamp, LastResetTimestamp, LastMismatchSize} - ids only, named below through ComponentList / PropertyList / Denomination.
            if (routeApp.Count == 0 && routeXfs.Count == 0)
                idRecs = KtcJson.Items(raw).Where(e => e.ValueKind == JsonValueKind.Object && KtcJson.Str(e, CompIdFields).Length > 0 && KtcJson.Str(e, PropIdFields).Length > 0).ToList();
            routeDiag = diag.Count;
            if (raw == null) diag.Add($"{path}: {rw.ToList().FirstOrDefault() ?? "no answer"}.");
            else if (routeApp.Count == 0 && routeXfs.Count == 0 && idRecs.Count > 0) diag.Add($"{path}: {idRecs.Count} records.");   // replaced once named
            else if (routeApp.Count == 0 && routeXfs.Count == 0)
            {
                var shape = CounterRules.Shape(raw);
                var sample = raw.Value.GetRawText(); sample = sample.Length > 300 ? sample[..300] + "…" : sample;
                _log.LogInformation("KTC {Route} answered without counters the portal can read; shape: {Shape}; start: {Sample}", path, shape, sample);
                diag.Add($"{path}: no counters the portal can read ({shape}).");
            }
            else diag.Add($"{path}: {routeXfs.Count} device counters on {routeXfs.Select(x => x.Device).Distinct().Count()} devices{(routeApp.Count > 0 ? $", {routeApp.Count} application counters" : "")}.");
            routeXfs = routeXfs.Select(x => x with { Threshold = x.Threshold ?? CounterRules.ThresholdFor(x.Counter, _opt.CounterThresholds) }).ToList();
        }

        // 2. XFS counters from CurrentStatus + cash units; 3. application counters from transactions. In parallel.
        var now = DateTime.UtcNow; var days = Math.Clamp(_opt.CounterLookbackDays, 1, 31);
        var tStatus = Fetch($"api/v1/Client/{id}/CurrentStatus", w, ct: ct);
        var tCash = Fetch($"api/v1/Client/{id}/CurrentCashUnitStatus", new Warn(), ct: ct);   // optional route: no warning when missing
        var tTx = SearchTransactionsAsync(new TransactionQuery(null, null, null, now.AddDays(-days), now), ct);
        await Task.WhenAll(tStatus, tCash, tTx);

        // ---- XFS ----
        var rows = KtcJson.Rows(tStatus.Result).Where(r => r.Value.ValueKind == JsonValueKind.Object).Select(r => r.Value).ToList();
        var names = await HardwareNamesAsync(rows.Concat(idRecs).ToList(), ct);
        var xfs = new List<XfsCounter>(); DateTime? collected = null;
        var comps = new HashSet<string>(); var xfsComps = new HashSet<string>(); var other = new HashSet<string>();
        string CompName(string cid) => names.Components.GetValueOrDefault(cid) ?? (cid.Length > 0 ? "Component " + cid : "Device");
        if (idRecs.Count > 0)
        {
            var denoms = await DenominationNamesAsync(ct);
            DateTime? lastReset = null;
            foreach (var e in idRecs)
            {
                var cid = KtcJson.NormId(KtcJson.Str(e, CompIdFields)); var pid = KtcJson.NormId(KtcJson.Str(e, PropIdFields));
                var comp = CompName(cid);
                var prop = names.Properties.GetValueOrDefault(pid)?.Name ?? "Property " + pid;
                var did = KtcJson.NormId(KtcJson.Str(e, "DenominationID", "DenominationId"));
                var counter = CounterRules.SplitWords(prop) + (did is "" or "0" ? "" : $" ({denoms.GetValueOrDefault(did) ?? "denomination " + did})");
                var value = (long)Math.Round(KtcJson.Dbl(e, 0, "CounterValue", "Value", "Count"));
                routeXfs.Add(new XfsCounter(comp, CounterRules.XfsClass(comp) ?? "", $"component {cid}", counter, value, CounterRules.ThresholdFor(CounterRules.SplitWords(prop), _opt.CounterThresholds)));
                if (KtcJson.Date(e, "Timestamp", "TimeStamp") is { } t && t > (routeAt ?? DateTime.MinValue)) routeAt = t;
                if (KtcJson.Date(e, "LastResetTimestamp", "LastReset") is { } lr && lr > (lastReset ?? DateTime.MinValue)) lastReset = lr;
            }
            diag[routeDiag] = $"{routePath}: {idRecs.Count} counters named through ComponentList / PropertyList: " +
                string.Join("; ", idRecs.Take(12).Select((e, i) => $"{routeXfs[i].Device} [{routeXfs[i].LogicalName}] / {routeXfs[i].Counter} [property {KtcJson.NormId(KtcJson.Str(e, PropIdFields))}] = {routeXfs[i].Value}")) + (lastReset is { } r0 ? $"; last reset {r0:yyyy-MM-dd HH:mm}" : "") + ".";
        }
        foreach (var e in rows)
        {
            var cid = KtcJson.NormId(KtcJson.Str(e, CompIdFields));
            var compName = KtcJson.Str(e, "ComponentName", "Component") is { Length: > 0 } cn ? cn : CompName(cid);
            comps.Add(compName);
            var cls = CounterRules.XfsClass(compName);
            if (cls == null) { other.Add(compName); continue; }
            xfsComps.Add(compName);
            var pid = KtcJson.NormId(KtcJson.Str(e, PropIdFields));
            var pinfo = names.Properties.GetValueOrDefault(pid);
            var propName = KtcJson.Str(e, "PropertyName", "Property") is { Length: > 0 } pn ? pn : pinfo?.Name ?? (pid.Length > 0 ? "Property " + pid : "Value");
            var useNum = KtcJson.Bool(e, false, "UseNumeric");
            var value = useNum ? KtcJson.Str(e, "NumericValue") : HardwareValue(e, pinfo, names.Values, propName);
            if (!CounterRules.IsCounter(propName, useNum, value, out var n)) continue;
            xfs.Add(new XfsCounter(compName, cls, null, propName, n, CounterRules.ThresholdFor(propName, _opt.CounterThresholds)));
            if (KtcJson.Date(e, "Timestamp", "TimeStamp", "LastUpdated", "Time") is { } t && t > (collected ?? DateTime.MinValue)) collected = t;
        }
        var cashRows = KtcJson.Rows(tCash.Result).Where(r => r.Value.ValueKind == JsonValueKind.Object).Select(r => r.Value).ToList();
        foreach (var e in cashRows)
        {
            var cid = KtcJson.NormId(KtcJson.Str(e, "DBComponentID", "ComponentID", "CompId"));
            var dev = CompName(cid);
            var unit = KtcJson.Str(e, "CashUnitNumber", "UnitNumber", "Number");
            var count = (long)Math.Max(0, KtcJson.Dbl(e, 0, "Count", "CurrentCount", "Notes"));
            xfs.Add(new XfsCounter(dev, CounterRules.XfsClass(dev) ?? "CDM", null, $"Cash unit {(unit.Length > 0 ? unit : "?")} notes", count, null));
            if (KtcJson.Date(e, "Timestamp", "TimeStamp") is { } t && t > (collected ?? DateTime.MinValue)) collected = t;
        }
        var statusCounters = xfs.Count - cashRows.Count;
        if (routeXfs.Count > 0)
        {
            // KTC's own counters first; CurrentStatus counter properties only for devices the counters route does not cover; then the cash units.
            var covered = routeXfs.Select(x => x.Device).ToHashSet(StringComparer.OrdinalIgnoreCase);
            xfs = routeXfs.Concat(xfs.Take(statusCounters).Where(x => !covered.Contains(x.Device))).Concat(xfs.Skip(statusCounters)).ToList();
        }
        diag.Add($"CurrentStatus: {rows.Count} rows, {comps.Count} components, {xfsComps.Count} XFS devices ({string.Join(", ", xfsComps.OrderBy(x => x))}), {statusCounters} counter-like properties{(routeXfs.Count > 0 ? " (used only for devices the counters route does not list)" : "")}; CurrentCashUnitStatus: {cashRows.Count} cash units.");
        if (xfs.Count > 0) { }
        else if (tStatus.Result == null) w.Add("XFS counters: KTC did not answer Client/" + id + "/CurrentStatus.");
        else if (rows.Count == 0) w.Add("XFS counters: KTC returned no counters and no CurrentStatus rows for this ATM.");
        else if (xfsComps.Count == 0) w.Add($"XFS counters: none of this ATM's {comps.Count} components is an XFS device (card reader, dispenser, acceptor, printer, PIN pad, sensors).");
        else if (xfs.Count == 0) w.Add("XFS counters: no counters reported by this ATM (neither Client/{id}/Counters nor counter properties in CurrentStatus). Open /diag/ktc-token and send the Counters section.");

        // ---- application ----
        var tx = tTx.Result.Where(t => t.AtmId == id).ToList();
        var (app, resetUtc, resetType) = CounterRules.ApplicationFromTransactions(tx, now.Date);
        var types = tx.GroupBy(t => t.Type.Length > 0 ? t.Type : "?").OrderByDescending(g => g.Count()).Take(12).Select(g => $"{g.Key} {g.Count()}");
        diag.Add($"Transactions (last {days} days): {tTx.Result.Count} for all ATMs, {tx.Count} for this ATM{(tx.Count > 0 ? ": " + string.Join(", ", types) : "")}.");
        if (tx.Count == 0)
        {
            w.Add($"Application counters: KTC returned no transactions for this ATM in the last {days} days.");
            app = new List<ApplicationCounter>();
        }
        var resetSource = resetUtc is { } ru ? $"last {resetType} {ru:yyyy-MM-dd HH:mm} UTC" : tx.Count > 0 ? $"start of the {days}-day lookback (no replenishment found)" : null;
        if (tx.Count > 0 && resetUtc == null) resetUtc = now.AddDays(-days);

        if (routeApp.Count > 0) { app = routeApp; resetUtc = routeReset ?? resetUtc; resetSource = routeReset.HasValue ? "last reset reported by KTC" : resetSource; }
        if (routeAt > (collected ?? DateTime.MinValue)) collected = routeAt;

        // ---- notes dispensed / accepted: KTC's per-transaction counters of each cash transaction since the reset ----
        var (notes, notesByType, notesSource, notesByDenom) = await NotesAsync(id, tx, resetUtc ?? now.AddDays(-days), now, routeXfs, w, diag, ct);
        app = app.Concat(notes).ToList();
        return new AtmCounters(id, name, collected, resetUtc, app, xfs.OrderBy(x => CounterRules.ClassRank(x.ServiceClass)).ThenBy(x => x.Device).ToList(), w.ToList(), resetSource, diag,
            notesByType, notesSource, notesByDenom);
    }

    /// <summary>
    /// "Notes dispensed" and "Notes accepted": KTC's per-transaction counters (Ktc:TransactionCountersEndpoint, what KTC's UI reads for one
    /// transaction) for the latest Ktc:TransactionNotesMax cash transactions since the reset, today and since the reset, plus a split per
    /// transaction type. When KTC has no per-transaction counters, the totals come from the dispenser "Dispensed" and acceptor "Deposited"
    /// device counters, with Today shown as not available.
    /// </summary>
    private async Task<(List<ApplicationCounter> Counters, List<NotesByType>? ByType, string? Source, List<NotesByDenomination>? ByDenomination)> NotesAsync(string id, List<TransactionDto> tx, DateTime sinceUtc, DateTime now,
        List<XfsCounter> deviceCounters, Warn w, List<string> diag, CancellationToken ct)
    {
        var cashTx = tx.Where(t => t.TimeUtc >= sinceUtc && CounterRules.IsCashTransaction(t)).OrderByDescending(t => t.TimeUtc).ToList();
        var route = _opt.TransactionCountersEndpoint?.Trim() ?? "";
        var max = Math.Clamp(_opt.TransactionNotesMax, 0, 5000);
        var read = new List<(TransactionDto Tx, TxNotes Notes)>(); int asked = 0, answered = 0, unreadable = 0; string? sample = null;
        var missing = new List<(TransactionDto Tx, string Why)>();
        if (route.Length > 0 && cashTx.Count > 0 && max > 0)
        {
            var pick = cashTx.Where(t => long.TryParse(t.Id, out _)).Take(max).ToList();
            asked = pick.Count;
            var answers = await Task.WhenAll(pick.Select(async t => (t, a: await TransactionCountersAsync(route, id, t.Id, ct))));
            // A successful answer with no body (or "null") is KTC saying the transaction changed no counters: the same as an empty list.
            var empty = JsonDocument.Parse("[]").RootElement.Clone();
            var res = answers.Select(x => (x.t, raw: x.a.Ok ? x.a.Json is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } j ? j : empty : (JsonElement?)null)).ToList();
            var failedHttp = answers.Where(x => !x.a.Ok).GroupBy(x => x.a.Status).ToDictionary(g => g.Key, g => g.Select(x => x.t).ToList());
            var noBody = answers.Count(x => x.a.Ok && x.a.Json is not { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) });
            // KTC's records are {ClientID, ComponentID, PropertyID, DenominationID, Value, Delta, TransactionID, ...}: name the ids first.
            var recs = res.Where(r => r.raw != null).SelectMany(r => KtcJson.Items(r.raw)).Where(e => e.ValueKind == JsonValueKind.Object).ToList();
            var names = recs.Any(e => KtcJson.Str(e, PropIdFields).Length > 0) ? await HardwareNamesAsync(recs, ct) : null;
            (string?, string?) Resolve(JsonElement e)
            {
                if (names == null) return (null, null);
                var cid = KtcJson.NormId(KtcJson.Str(e, CompIdFields)); var pid = KtcJson.NormId(KtcJson.Str(e, PropIdFields));
                return (cid.Length > 0 ? names.Components.GetValueOrDefault(cid) ?? "Component " + cid : null, pid.Length > 0 ? names.Properties.GetValueOrDefault(pid)?.Name : null);
            }
            foreach (var (t, raw) in res)
            {
                if (raw == null) continue;
                answered++;
                if (CounterRules.ReadTransactionNotes(raw, names == null ? null : Resolve) is { } n) read.Add((t, n));
                else if (!t.Success) read.Add((t, new TxNotes(0, 0)));   // a failed transaction whose counters hold no note counter moved no notes
                else { unreadable++; missing.Add((t, "counters without a dispensed / deposited counter")); sample ??= raw.Value.GetRawText(); }
            }
            var path = route.Split('?')[0];
            var s = sample == null ? "" : sample.Length > 300 ? sample[..300] + "…" : sample;
            if (sample != null) _log.LogInformation("KTC {Route} answered without note counts the portal can read; shape: {Shape}; start: {Sample}", path, CounterRules.Shape(JsonDocument.Parse(sample).RootElement.Clone()), s);
            foreach (var (st, l) in failedHttp) foreach (var t in l) missing.Add((t, st == 0 ? "no answer" : $"HTTP {st}"));
            diag.Add($"{path}: {cashTx.Count} cash transactions since the reset, {asked} asked, {answered} answered{(noBody > 0 ? $" ({noBody} with an empty answer, counted as no notes)" : "")}, {read.Count} with note counts{(unreadable > 0 ? $", {unreadable} without note counts the portal can read (first answer: {s})" : "")}"
                     + (failedHttp.Count > 0 ? $", {failedHttp.Sum(f => f.Value.Count)} not answered ({string.Join(", ", failedHttp.Select(f => $"{(f.Key == 0 ? "no answer" : "HTTP " + f.Key)} x{f.Value.Count}"))})" : "") + ".");
            if (missing.Count > 0)
                diag.Add("Cash transactions without note counts: " + string.Join("; ", missing.OrderByDescending(m => m.Tx.TimeUtc).Take(20).Select(m =>
                    $"{m.Tx.Id} {m.Tx.TimeUtc:yyyy-MM-dd HH:mm} {m.Tx.Type} {(m.Tx.Success ? "success" : "failed")}{(m.Tx.Amount is { } am ? $" amount {am:0.##}" : "")}: {m.Why}")) + (missing.Count > 20 ? $"; and {missing.Count - 20} more" : "") + ".");
            // Which component / property ids KTC reported for these transactions, with their names and the sum of their Delta:
            // shows which counters are the notes dispensed / accepted (or why none matched).
            if (names != null)
            {
                var seen = recs.GroupBy(e => (C: KtcJson.NormId(KtcJson.Str(e, CompIdFields)), P: KtcJson.NormId(KtcJson.Str(e, PropIdFields))))
                    .Select(g =>
                    {
                        var (cn, pn) = Resolve(g.First());
                        var kind = CounterRules.NoteKind(g.First(), Resolve, out _, out _, out _);
                        var delta = g.Sum(e => (long)Math.Round(KtcJson.Dbl(e, 0, "Delta")));
                        return $"{cn ?? "?"} [component {g.Key.C}] / {pn ?? "?"} [property {g.Key.P}]: delta {delta} in {g.Select(e => KtcJson.Str(e, "TransactionID")).Distinct().Count()} tx{(kind == 1 ? " = NOTES DISPENSED" : kind == 2 ? " = NOTES ACCEPTED" : "")}";
                    }).Take(20).ToList();
                diag.Add("Per-transaction counters reported: " + string.Join("; ", seen) + ".");
            }
        }
        else if (cashTx.Count > 0 && route.Length == 0) diag.Add("Notes per transaction: Ktc:TransactionCountersEndpoint is empty, so per-transaction counters are not read.");

        if (read.Count > 0)
        {
            var (counters, byType, running) = CounterRules.NotesFromTransactions(read, now.Date);
            // Split by note value: KTC DenominationID -> value through GET Denomination. Running totals can't be split per transaction.
            List<NotesByDenomination>? byDenom = null;
            if (!running)
            {
                var wanted = read.SelectMany(r => r.Notes.DispensedByDenomination.Keys.Concat(r.Notes.AcceptedByDenomination.Keys)).Where(k => k.Length > 0).Distinct().ToList();
                var values = await DenominationValuesAsync(wanted, ct);
                byDenom = CounterRules.ByDenomination(read.Select(r => r.Notes), values, standard: true);
                static string TypeOf(TransactionDto t) => t.Type.Length > 0 ? t.Type : "Unknown";
                byType = byType.Select(t => t with { Denominations = CounterRules.ByDenomination(read.Where(r => TypeOf(r.Tx).Equals(t.Type, StringComparison.OrdinalIgnoreCase)).Select(r => r.Notes), values, standard: false) }).ToList();
                var ids = read.SelectMany(r => r.Notes.DispensedByDenomination.Keys.Concat(r.Notes.AcceptedByDenomination.Keys)).Where(k => k.Length > 0).Distinct().OrderBy(k => k.Length).ThenBy(k => k, StringComparer.Ordinal).ToList();
                var unknown = ids.Where(k => !values.ContainsKey(k)).ToList();
                var list = values.Count == 0 ? "returned no note values" : string.Join(", ", values.OrderBy(v => v.Key.Length).ThenBy(v => v.Key, StringComparer.Ordinal).Select(v => $"id {v.Key} = {v.Value:0.##}"));
                diag.Add($"Denominations: KTC Denomination list {list}; ids in these transactions: {(ids.Count == 0 ? "none" : string.Join(", ", ids))}{(unknown.Count > 0 ? "; without a note value: " + string.Join(", ", unknown) : "")}.");
                if (unknown.Count > 0) w.Add($"Application counters: KTC denomination id(s) {string.Join(", ", unknown)} have no note value in KTC (Denomination list or Denomination/{{id}}), so they show as unknown. Set their values in Ktc:DenominationValues, e.g. Ktc__DenominationValues__{unknown[0]}=100.");
            }
            else w.Add("Application counters: KTC reports running note totals, so the notes can't be split by denomination per transaction.");
            if (read.Count < cashTx.Count)
            {
                var why = missing.GroupBy(m => m.Why).Select(g => $"{g.Count()} {g.Key}").ToList();
                w.Add($"Application counters: notes are counted for {read.Count} of the {cashTx.Count} cash transactions since the reset" +
                      (asked < cashTx.Count ? $" (the latest {asked}, Ktc:TransactionNotesMax)" : "") +
                      (why.Count > 0 ? $". KTC's per-transaction counters route gave no note counts for the others: {string.Join(", ", why)} (listed under What KTC returned)." : "."));
            }
            return (counters, byType, $"KTC per-transaction counters of {read.Count} cash transaction(s){(running ? ", counted as the change from one transaction to the next" : "")}", byDenom);
        }

        // No per-transaction counters: totals from the device counters (since those counters were last reset), Today not available.
        long? Dev(string cls, string counter) { var l = deviceCounters.Where(x => x.ServiceClass == cls && x.Counter.Equals(counter, StringComparison.OrdinalIgnoreCase)).ToList(); return l.Count == 0 ? null : l.Sum(x => x.Value); }
        var disp = Dev("CDM", "Dispensed"); var dep = Dev("CIM", "Deposited");
        if (disp == null && dep == null)
        {
            if (cashTx.Count > 0 || tx.Count > 0)
                w.Add("Application counters: notes dispensed and accepted are not available. KTC returned no per-transaction counters and no Dispensed / Deposited device counters for this ATM.");
            return (new List<ApplicationCounter>(), null, null, null);
        }
        w.Add("Application counters: KTC returned no per-transaction note counts, so Notes dispensed / accepted show the dispenser and acceptor counters (since those were last reset) and Today is not available.");
        return (new List<ApplicationCounter>
        {
            new("Cash", "Notes dispensed", CounterRules.Unknown, disp ?? CounterRules.Unknown),
            new("Cash", "Notes accepted", CounterRules.Unknown, dep ?? CounterRules.Unknown),
        }, null, "the dispenser and acceptor device counters (no per-transaction counters from KTC)", null);
    }

    // KTC's Denomination record is {ID, CurrencyID, CurrencyValue} (Postman POST Denomination); other names are kept for other versions.
    private static readonly string[] DenomValueFields = { "CurrencyValue", "Value", "Amount", "FaceValue", "DenominationValue", "NoteValue" };

    private static double DenomValue(JsonElement e)
    {
        var val = KtcJson.Dbl(e, double.NaN, DenomValueFields);
        if (double.IsNaN(val) && Regex.Match(KtcJson.Str(e, "Name", "DisplayName", "Description"), @"\d+(\.\d+)?") is { Success: true } mm)
            val = double.Parse(mm.Value, System.Globalization.CultureInfo.InvariantCulture);
        return val;
    }

    /// <summary>
    /// KTC denomination id -> note value (100): Ktc:DenominationValues first, then GET Denomination, then GET Denomination/{id} for the
    /// <paramref name="wanted"/> ids still missing. Cached.
    /// </summary>
    private async Task<Dictionary<string, decimal>> DenominationValuesAsync(IReadOnlyCollection<string> wanted, CancellationToken ct)
    {
        var list = await Cached("denomination-values", TimeSpan.FromMinutes(30), async () =>
        {
            var map = new Dictionary<string, decimal>();
            foreach (var (k, e) in KtcJson.Rows(await Quiet("api/v1/Denomination", ct)))
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var did = KtcJson.NormId(KtcJson.Str(e, "ID", "Id", "DenominationID") is { Length: > 0 } x ? x : k);
                var val = DenomValue(e);
                if (did.Length > 0 && !double.IsNaN(val) && val > 0) map.TryAdd(did, (decimal)val);
            }
            return map;
        });
        var res = new Dictionary<string, decimal>(list);
        foreach (var (k, v) in _opt.DenominationValues ?? new()) if (v > 0) res[KtcJson.NormId(k)] = v;
        foreach (var did in wanted.Where(d => !res.ContainsKey(d)).Take(20))
        {
            var one = await Cached("denomination-value|" + did, TimeSpan.FromMinutes(30), async () =>
            {
                var j = await Quiet($"api/v1/Denomination/{Uri.EscapeDataString(did)}", ct);
                var e = j is { ValueKind: JsonValueKind.Object } o ? o : KtcJson.Items(j).FirstOrDefault(x => x.ValueKind == JsonValueKind.Object);
                return e.ValueKind == JsonValueKind.Object && DenomValue(e) is var v && !double.IsNaN(v) && v > 0 ? (decimal?)v : null;
            });
            if (one is { } ov) res[did] = ov;
        }
        return res;
    }

    /// <summary>KTC denomination id -> name ("AED 100"), from GET Denomination: a name field, else currency and value. Cached.</summary>
    private Task<Dictionary<string, string>> DenominationNamesAsync(CancellationToken ct) =>
        Cached("denominations", TimeSpan.FromMinutes(30), async () =>
        {
            var map = new Dictionary<string, string>();
            var denoms = KtcJson.Rows(await Quiet("api/v1/Denomination", ct)).ToList();
            // CurrencyID -> code ("AED") from GET Currency, only when a denomination names its currency by id.
            var currencies = new Dictionary<string, string>();
            if (denoms.Any(d => d.Item2.ValueKind == JsonValueKind.Object && KtcJson.Str(d.Item2, "CurrencyID", "CurrencyId").Length > 0))
                foreach (var (ck, ce) in KtcJson.Rows(await Quiet("api/v1/Currency", ct)))
                    if (ce.ValueKind == JsonValueKind.Object && KtcJson.Str(ce, "Code", "CurrencyCode", "ISOCode", "Symbol", "Name", "Description") is { Length: > 0 } cn)
                        currencies.TryAdd(KtcJson.NormId(KtcJson.Str(ce, "ID", "Id", "CurrencyID") is { Length: > 0 } ci ? ci : ck), cn);
            foreach (var (k, e) in denoms)
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var did = KtcJson.NormId(KtcJson.Str(e, "ID", "Id", "DenominationID") is { Length: > 0 } x ? x : k);
                var name = KtcJson.Str(e, "Name", "DisplayName", "Description");
                if (name.Length == 0)
                {
                    var cur = KtcJson.Str(e, "CurrencyCode", "Currency", "CurrencyName");
                    if (cur.Length == 0) cur = currencies.GetValueOrDefault(KtcJson.NormId(KtcJson.Str(e, "CurrencyID", "CurrencyId"))) ?? "";
                    var val = DenomValue(e);
                    name = double.IsNaN(val) ? "" : $"{cur} {val:0.##}".Trim();
                }
                if (did.Length > 0 && name.Length > 0) map.TryAdd(did, name);
            }
            return map;
        });

    /// <summary>One transaction's KTC counters; a successful answer is cached for 12 hours (a finished transaction's counters do not change).</summary>
    private async Task<JsonElement?> TxCountersJsonAsync(string route, string atmId, string txId, CancellationToken ct) =>
        (await TransactionCountersAsync(route, atmId, txId, ct)).Json;

    private async Task<(bool Ok, int Status, JsonElement? Json)> TransactionCountersAsync(string route, string atmId, string txId, CancellationToken ct)
    {
        var key = $"txc|{Scope}|{atmId}|{txId}";
        if (_cache.TryGetValue(key, out (bool, int, JsonElement?) cached)) return cached;
        var url = route.TrimStart('/').Replace("{atmId}", Uri.EscapeDataString(atmId), StringComparison.OrdinalIgnoreCase)
                       .Replace("{transactionId}", Uri.EscapeDataString(txId), StringComparison.OrdinalIgnoreCase);
        await HwGate.WaitAsync(ct);
        KtcResponse r;
        try { r = await _api.SendAsync(HttpMethod.Get, url, null, new KtcCallOptions(), ct); }
        finally { HwGate.Release(); }
        var res = (r.Success, r.StatusCode, r.Success ? r.Json?.Clone() : null);
        if (r.Success) _cache.Set(key, res, TimeSpan.FromHours(12));
        return res;
    }

    private sealed record PropInfo(string Name, Dictionary<string, string> Values);
    private sealed record HwNames(Dictionary<string, string> Components, Dictionary<string, PropInfo> Properties, Dictionary<string, string> Values);

    private static readonly string[] CompIdFields = { "CompId", "ComponentID", "ComponentId", "CompID", "DBComponentID", "DBComponentId" };
    private static readonly string[] PropIdFields = { "PropId", "PropertyID", "PropertyId", "PropID", "DBPropertyID", "DBPropertyId" };
    private static readonly string[] ValueIdFields = { "VID", "ValueID", "ValueId" };
    private static readonly string[] NameFields = { "Name", "DisplayName", "Description", "ComponentName", "PropertyName", "Text" };
    private static readonly SemaphoreSlim HwGate = new(6, 6);

    /// <summary>
    /// GET a lookup route without adding its failure to the page warnings (several routes are tried in turn), at most six at a time.
    /// </summary>
    private async Task<JsonElement?> Quiet(string url, CancellationToken ct)
    {
        await HwGate.WaitAsync(ct);
        try { var r = await _api.SendAsync(HttpMethod.Get, url, null, new KtcCallOptions(), ct); return r.Success ? r.Json : null; }
        finally { HwGate.Release(); }
    }

    /// <summary>
    /// Names for the component, property and value ids in CurrentStatus records. KTC's ComponentList?name= and PropertyList?name=
    /// answer 500 on some servers, so this tries the list routes first and then the per-id routes (ComponentList/{id},
    /// PropertyList/UsedByComponent, PropertyList/{id}, ValueList/{id}) for the ids that are still missing. Every answer is cached.
    /// </summary>
    private async Task<HwNames> HardwareNamesAsync(IReadOnlyList<JsonElement> recs, CancellationToken ct)
    {
        List<string> Ids(string[] f) => recs.Select(e => KtcJson.NormId(KtcJson.Str(e, f))).Where(x => x.Length > 0 && x != "-1").Distinct().ToList();
        var cids = Ids(CompIdFields); var pids = Ids(PropIdFields); var vids = Ids(ValueIdFields);
        var tc = ComponentNamesAsync(cids, ct); var tp = PropertyNamesAsync(cids, pids, ct);
        await Task.WhenAll(tc, tp);
        var values = await ValueNamesAsync(vids.Where(v => !tp.Result.Values.Any(p => p.Values.ContainsKey(v))).ToList(), ct);
        return new HwNames(tc.Result, tp.Result, values);
    }

    /// <summary>id -> name from any KTC lookup answer: an id-keyed map of names, an id-keyed map of records, or a list of records.</summary>
    internal static Dictionary<string, string> NameMap(JsonElement? json, string[] idFields, string fallbackId = "")
    {
        var map = new Dictionary<string, string>();
        if (json is { ValueKind: JsonValueKind.Object } o && KtcJson.IsIdMap(o))
            foreach (var p in o.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } v) map.TryAdd(KtcJson.NormId(p.Name), v);
        foreach (var (k, e) in KtcJson.Rows(json))
        {
            var id = KtcJson.NormId(KtcJson.Str(e, idFields.Concat(new[] { "ID", "Id" }).ToArray()) is { Length: > 0 } x ? x : k.Length > 0 ? k : fallbackId);
            var name = KtcJson.Str(e, NameFields);
            if (id.Length > 0 && name.Length > 0) map.TryAdd(id, name);
        }
        return map;
    }

    /// <summary>KTC component id -> name.</summary>
    private async Task<Dictionary<string, string>> ComponentNamesAsync(List<string> cids, CancellationToken ct)
    {
        var all = await Cached("hw-components", TimeSpan.FromMinutes(30), async () =>
        {
            foreach (var url in new[] { "api/v1/ComponentList", "api/v1/ComponentList?name=%25" })
                if (NameMap(await Quiet(url, ct), CompIdFields) is { Count: > 0 } m) { _log.LogInformation("KTC component names from {Route}: {Count}", url, m.Count); return m; }
            return new Dictionary<string, string>();
        });
        var res = new Dictionary<string, string>(all);
        var found = await Task.WhenAll(cids.Where(c => !all.ContainsKey(c)).Select(c => Cached($"hw-component-{c}", TimeSpan.FromMinutes(30), async () =>
            (Id: c, Name: NameMap(await Quiet($"api/v1/ComponentList/{c}", ct), CompIdFields, c).GetValueOrDefault(c) ?? ""))));
        foreach (var (c, n) in found) if (n.Length > 0) res[c] = n;
        return res;
    }

    /// <summary>KTC property id -> name and, when KTC lists them, value id -> value name.</summary>
    private async Task<Dictionary<string, PropInfo>> PropertyNamesAsync(List<string> cids, List<string> pids, CancellationToken ct)
    {
        var all = await Cached("hw-properties", TimeSpan.FromMinutes(30), async () =>
        {
            foreach (var url in new[] { "api/v1/PropertyList", "api/v1/PropertyList?name=%25" })
                if (ParseProps(await Quiet(url, ct), "") is { Count: > 0 } m) { _log.LogInformation("KTC property names from {Route}: {Count}", url, m.Count); return m; }
            return new Dictionary<string, PropInfo>();
        });
        var res = new Dictionary<string, PropInfo>(all);
        if (pids.All(res.ContainsKey)) return res;
        // The properties each component uses, then each property that is still missing on its own.
        foreach (var m in await Task.WhenAll(cids.Select(c => Cached($"hw-props-of-{c}", TimeSpan.FromMinutes(30), async () =>
                     ParseProps(await Quiet($"api/v1/PropertyList/UsedByComponent?componentid={c}", ct), "")))))
            foreach (var (k, v) in m) res.TryAdd(k, v);
        foreach (var m in await Task.WhenAll(pids.Where(p => !res.ContainsKey(p)).Select(p => Cached($"hw-prop-{p}", TimeSpan.FromMinutes(30), async () =>
                     ParseProps(await Quiet($"api/v1/PropertyList/{p}", ct), p)))))
            foreach (var (k, v) in m) res.TryAdd(k, v);
        return res;
    }

    private static Dictionary<string, PropInfo> ParseProps(JsonElement? json, string fallbackId)
    {
        var map = new Dictionary<string, PropInfo>();
        foreach (var (id, name) in NameMap(json, PropIdFields, fallbackId)) map.TryAdd(id, new PropInfo(name, new()));
        foreach (var (k, e) in KtcJson.Rows(json))
        {
            var id = KtcJson.NormId(KtcJson.Str(e, PropIdFields.Concat(new[] { "ID", "Id" }).ToArray()) is { Length: > 0 } x ? x : k.Length > 0 ? k : fallbackId);
            if (!map.TryGetValue(id, out var info) || e.ValueKind != JsonValueKind.Object) continue;
            foreach (var p in e.EnumerateObject().Where(p => p.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object))
                foreach (var (vid, vn) in NameMap(p.Value, ValueIdFields))
                    info.Values.TryAdd(vid, vn);
            foreach (var p in e.EnumerateObject().Where(p => p.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object))
                foreach (var v in KtcJson.Items(p.Value).Where(v => v.ValueKind == JsonValueKind.Object))
                    if (KtcJson.NormId(KtcJson.Str(v, ValueIdFields.Concat(new[] { "ID", "Id" }).ToArray())) is { Length: > 0 } vid
                        && KtcJson.Str(v, "Value", "Name", "DisplayName", "Description", "Text") is { Length: > 0 } vn)
                        info.Values.TryAdd(vid, vn);
        }
        return map;
    }

    /// <summary>KTC value id (VID) -> value text, from ValueList, or ValueList/{id} for the ids that list does not hold.</summary>
    private async Task<Dictionary<string, string>> ValueNamesAsync(List<string> vids, CancellationToken ct)
    {
        var all = await Cached("hw-values", TimeSpan.FromMinutes(30), async () =>
        {
            var json = await Quiet("api/v1/ValueList", ct);
            var m = ValueMap(json, "");
            if (m.Count > 0) _log.LogInformation("KTC value names from api/v1/ValueList: {Count}", m.Count);
            return m;
        });
        var res = new Dictionary<string, string>(all);
        var found = await Task.WhenAll(vids.Where(v => !all.ContainsKey(v)).Select(v => Cached($"hw-value-{v}", TimeSpan.FromMinutes(30), async () =>
            (Id: v, Name: ValueMap(await Quiet($"api/v1/ValueList/{v}", ct), v).GetValueOrDefault(v) ?? ""))));
        foreach (var (v, n) in found) if (n.Length > 0) res[v] = n;
        return res;

        static Dictionary<string, string> ValueMap(JsonElement? json, string fallbackId)
        {
            var map = new Dictionary<string, string>();
            foreach (var (k, e) in KtcJson.Rows(json))
            {
                var id = KtcJson.NormId(KtcJson.Str(e, "VID", "ValueID", "ValueId", "ID", "Id") is { Length: > 0 } x ? x : k.Length > 0 ? k : fallbackId);
                var name = KtcJson.Str(e, "Value", "Name", "DisplayName", "Description", "Text", "ValueName");
                if (id.Length > 0 && name.Length > 0) map.TryAdd(id, name);
            }
            foreach (var (id, n) in NameMap(json, ValueIdFields, fallbackId)) map.TryAdd(id, n);
            return map;
        }
    }

    /// <summary>
    /// The readable value of a CurrentStatus record: a value name on the record, NumericValue (when UseNumeric), ManualValue,
    /// or the VID looked up in the property's value list or ValueList. Booleans read Yes/No and numbers lose trailing zeros.
    /// </summary>
    private static string HardwareValue(JsonElement e, PropInfo? p, Dictionary<string, string> values, string propName)
    {
        var named = KtcJson.Str(e, "ValueName", "StatusName", "Value", "StringValue", "Text");
        if (named.Length > 0) return Pretty(named, propName);
        if (KtcJson.Bool(e, false, "UseNumeric")) return Pretty(KtcJson.Str(e, "NumericValue"), propName);
        var manual = KtcJson.Str(e, "ManualValue");
        if (manual.Length > 0) return Pretty(manual, propName);
        var vid = KtcJson.NormId(KtcJson.Str(e, ValueIdFields));
        if (vid.Length > 0 && vid != "-1")
        {
            if (p != null && p.Values.TryGetValue(vid, out var vn)) return vn;
            if (values.TryGetValue(vid, out vn)) return vn;
        }
        var num = KtcJson.Str(e, "NumericValue");
        if (vid.Length > 0 && vid != "-1") return "Value " + vid;
        return Pretty(num, propName);
    }

    internal static string Pretty(string v, string propName)
    {
        v = v.Trim();
        if (bool.TryParse(v, out var b)) return b ? "Yes" : "No";
        if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && !v.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            var s = d.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            if (System.Text.RegularExpressions.Regex.IsMatch(propName, @"percent|%", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) s += "%";
            else if (System.Text.RegularExpressions.Regex.IsMatch(propName, @"temperature|temp\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) s += " °C";
            return s;
        }
        return v;
    }

    public async Task<AtmAvailability> GetAvailabilityAsync(string atmId, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var tA = AvailabilityValueAsync(atmId, from, to, ct);
        var tR = GetWithDatesAsync(f => $"api/v1/Client/{atmId}/Top5UnavailableReasons?start={f(from)}&end={f(to)}", ct);
        await Task.WhenAll(tA, tR);
        var reasons = new List<(string, double)>();
        if (tR.Result.Success)
            foreach (var e in KtcJson.Items(tR.Result.Json))
                reasons.Add((KtcJson.Str(e, "Reason", "Name", "Description", "ReasonName"), KtcJson.Dbl(e, 0, "Hours", "Duration", "TotalHours", "Minutes") ));
        return new AtmAvailability(tA.Result ?? double.NaN, reasons);
    }

    // ---------------- incidents ----------------
    public async Task<IReadOnlyList<IncidentDto>> GetIncidentsAsync(bool openOnly, CancellationToken ct = default) => await GetIncidentsInternal(openOnly, new Warn(), ct);

    private Task<List<IncidentDto>> GetIncidentsInternal(bool openOnly, Warn w, CancellationToken ct) =>
        Cached("incidents:" + openOnly, TimeSpan.FromSeconds(20), async () =>
        {
            var lookup = await LookupAsync(w, ct);
            var json = await Fetch("api/v1/ActiveErrorExtra?abovelevel=0", w, ct: ct);
            return KtcJson.Items(json).Select(e => MapIncident(e, lookup)).Where(i => i != null).Select(i => i!).ToList();
        });

    public async Task<IReadOnlyList<IncidentDto>> GetHistoricalIncidentsAsync(DateTime from, DateTime to, string? atmId = null, CancellationToken ct = default)
    {
        var w = new Warn(); var lookup = await LookupAsync(w, ct);
        var url = string.IsNullOrEmpty(atmId) ? "api/v1/HistoricalError" : $"api/v1/Client/{atmId}/HistoricalError";
        var r = await PausableAsync(ApiHealthTracker.Normalize(url), () => HistoricalErrorCoreAsync(url, from, to, ct));
        if (!r.Success) w.Add($"GET {url}: {r.Error}");
        var json = r.Success ? r.Json : null;
        return KtcJson.Items(json).Select(e => MapIncident(e, lookup)).Where(i => i != null && i.OpenedUtc >= from && i.OpenedUtc <= to).Select(i => i!).ToList();
    }

    private IncidentDto? MapIncident(JsonElement e, Dictionary<string, ClientRec> lookup)
    {
        var id = KtcJson.Str(e, "ID", "Id", "ErrorID", "ActiveErrorID");
        if (id.Length == 0) return null;
        var atm = KtcJson.NormId(KtcJson.Str(e, "ClientID", "ClientId", "AtmID"));
        lookup.TryGetValue(atm, out var c);
        var desc = KtcJson.Str(e, "ErrorCodeTypeName", "Description", "ErrorText", "Name", "Text", "ErrorDescription");
        var level = KtcJson.Int(e, 3, "Level", "ErrorLevel", "Severity", "Priority");
        var sev = _opt.SeverityMap.TryGetValue(level.ToString(), out var s) ? (Severity)Math.Clamp(s, 1, 4) : Severity.P3;
        var opened = KtcJson.Date(e, "TimeAdded", "TimeCreated", "StartTime", "Added", "TimeStamp", "Time") ?? DateTime.UtcNow;
        var closed = KtcJson.Date(e, "TimeClosed", "ClosedTime", "EndTime", "Closed");
        var due = opened.AddHours(_opt.DefaultSlaHours.GetValueOrDefault(sev.ToString(), 24));
        return new IncidentDto
        {
            Key = id, Number = "INC-" + id, AtmId = atm, AtmName = c?.Name ?? KtcJson.Str(e, "ClientName"), Client = c?.Business ?? "",
            Severity = sev, OpenedUtc = opened, ClosedUtc = closed, SlaDueUtc = due, Sla = SlaCalculator.Evaluate(due, closed, DateTime.UtcNow, _opt.NearBreachMinutes),
            Status = closed.HasValue ? "Closed" : "Open", ErrorCode = KtcJson.Str(e, "ErrorCodeTypeID", "ErrorCodeID", "ErrorCode", "Code"), Description = desc,
            HostRelated = FailureClassifier.Classify(desc) == FailureClassifier.Host
        };
    }

    // ---------------- tickets ----------------
    private object TicketBody(int? businessId, DateTime from, DateTime to) => new
    {
        BusinessID = businessId, BranchID = (int?)null, TicketTypeID = (int?)null, ErrorCodeTypeID = (int?)null, DispatchID = (int?)null, Status = (int[]?)null,
        Start = from, End = to, SLAFilterType = 0, SLAHours = 0,
        OwnerName = (string?)null, ClientName = (string?)null, GroupID = _opt.TicketSearchGroupId, ExtraDataField = (string?)null, ExtraDataValue = (string?)null
    };

    private static List<TicketDto> DistinctTickets(IEnumerable<TicketDto?> t) =>
        t.Where(x => x != null).Select(x => x!).GroupBy(x => x.Id).Select(g => g.First()).ToList();

    private Task<List<TicketDto>> TicketsCore(Warn w, CancellationToken ct) =>
        Cached("tickets", TimeSpan.FromSeconds(30), async () =>
        {
            var lookup = await LookupAsync(w, ct);
            var rows = await SearchPerBusinessAsync("api/v1/Ticket/Search", b => TicketBody(b, DateTime.UtcNow.AddDays(-_opt.TicketLookbackDays), DateTime.UtcNow.AddMinutes(5)), w, ct);
            return DistinctTickets(rows.Select(e => MapTicket(e, lookup)));
        });

    public async Task<IReadOnlyList<TicketDto>> SearchTicketsAsync(TicketQuery q, CancellationToken ct = default)
    {
        var w = new Warn();
        IEnumerable<TicketDto> all;
        if (q.From.HasValue || q.To.HasValue)
        {
            var lookup = await LookupAsync(w, ct);
            var rows = await SearchPerBusinessAsync("api/v1/Ticket/Search", b => TicketBody(b, q.From ?? DateTime.UtcNow.AddDays(-_opt.TicketLookbackDays), q.To ?? DateTime.UtcNow), w, ct);
            all = DistinctTickets(rows.Select(e => MapTicket(e, lookup)));
        }
        else all = await TicketsCore(w, ct);

        if (!string.IsNullOrWhiteSpace(q.AtmId))
        {
            var ids = MatchAtms(await LookupAsync(new Warn(), ct), q.AtmId);
            var text = q.AtmId.Trim();
            all = all.Where(t => ids.Contains(t.AtmId) || t.AtmName.Contains(text, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(q.TicketId)) all = all.Where(t => t.Id.Contains(q.TicketId, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(q.Client)) all = all.Where(t => t.Client.Contains(q.Client, StringComparison.OrdinalIgnoreCase));
        return all.OrderByDescending(t => t.CreatedUtc).ToList();
    }

    public async Task<TicketDto?> GetTicketAsync(string id, CancellationToken ct = default)
    {
        var w = new Warn(); var lookup = await LookupAsync(w, ct);
        var json = await Fetch($"api/v1/Ticket/{Uri.EscapeDataString(id)}", w, ct: ct);
        return KtcJson.Items(json).Select(e => MapTicket(e, lookup)).FirstOrDefault();
    }

    private TicketDto? MapTicket(JsonElement e, Dictionary<string, ClientRec> lookup)
    {
        var id = KtcJson.Str(e, "ID", "Id", "TicketID");
        if (id.Length == 0) return null;
        var atm = KtcJson.NormId(KtcJson.Str(e, "ClientID", "ClientId")); lookup.TryGetValue(atm, out var c);
        var created = KtcJson.Date(e, "TimeCreated", "CreationDate", "Created") ?? DateTime.UtcNow;
        var updated = KtcJson.Date(e, "TimeUpdated", "Updated");
        var closed = KtcJson.Date(e, "TimeClosed", "ClosedDate", "Closed");
        var owned = KtcJson.Date(e, "TimeOwned"); var dispatched = KtcJson.Date(e, "TimeDispatched");
        var owner = KtcJson.Str(e, "OwnerName", "Owner", "AssignedTo");
        var sb = KtcJson.Int(e, -1, "StatusByte", "Status");
        TicketStatus st;
        if (sb >= 0 && _opt.TicketStatusMap.TryGetValue(sb.ToString(), out var mapped) && Enum.TryParse<TicketStatus>(mapped, true, out var ms)) st = ms;
        else if (closed.HasValue) st = TicketStatus.Closed;
        else if (dispatched.HasValue) st = TicketStatus.InProgress;
        else if (owned.HasValue || owner.Length > 0) st = TicketStatus.Assigned;
        else st = TicketStatus.Open;
        var level = KtcJson.Int(e, 3, "Level", "Priority", "Severity", "ErrorLevel");
        var sev = _opt.SeverityMap.TryGetValue(level.ToString(), out var s) ? (Severity)Math.Clamp(s, 1, 4) : Severity.P3;
        var due = KtcJson.Date(e, "SLADueTime", "ExpectedEndTime", "SlaDue", "DueTime") ?? created.AddHours(_opt.DefaultSlaHours.GetValueOrDefault(sev.ToString(), 24));
        DateTime? completed = st is TicketStatus.Closed or TicketStatus.Resolved ? (closed ?? updated ?? DateTime.UtcNow) : null;
        var desc = KtcJson.Str(e, "Description", "Summary", "ErrorCodeTypeName", "TicketTypeName", "Title");
        if (desc.Length == 0) desc = Regex.Replace(KtcJson.Str(e, "CommentsString"), "<[^>]+>", " ").Trim();
        return new TicketDto
        {
            Id = id, AtmId = atm, AtmName = c?.Name ?? KtcJson.Str(e, "ClientName"), Client = c?.Business ?? "", Status = st, Severity = sev,
            CreatedUtc = created, UpdatedUtc = updated, ClosedUtc = closed, Owner = owner.Length == 0 ? null : owner, Description = desc,
            SlaDueUtc = due, Sla = SlaCalculator.Evaluate(due, completed, DateTime.UtcNow, _opt.NearBreachMinutes)
        };
    }

    // ---------------- transactions ----------------
    public async Task<IReadOnlyList<TransactionDto>> SearchTransactionsAsync(TransactionQuery q, CancellationToken ct = default)
    {
        var from = q.From ?? DateTime.UtcNow.Date; var to = q.To ?? DateTime.UtcNow;
        var key = $"tx:{from:yyyyMMddHHmm}:{to:yyyyMMddHHmm}";
        // Short cache: the portal polls this every 30 s from several widgets.
        var all = await Cached(key, TimeSpan.FromSeconds(25), async () =>
        {
            var w = new Warn(); var lookup = await LookupAsync(w, ct); var types = await TxLookupsAsync(w, ct);
            var gid = _opt.TransactionSearchGroupId > 0 ? _opt.TransactionSearchGroupId : await AllClientsGroupIdAsync(w, ct);
            var rowsAll = await SearchRowsPerBusinessAsync("api/v1/TransactionData/Search", b => new { BusinessID = b, GroupID = gid, Start = from, End = to, ClientName = (string?)null }, w, ct);
            var errs = w.ToList();
            if (errs.Count > 0) _log.LogWarning("Transaction search {From:u}..{To:u} (group {Group}) had KTC errors: {Errors}", from, to, gid, string.Join("; ", errs.Take(3)));
            else _log.LogDebug("Transaction search {From:u}..{To:u} (group {Group}) returned {Rows} KTC rows", from, to, gid, rowsAll.Count);
            var res = new List<TransactionDto>();
            foreach (var (k, e) in rowsAll)
            {
                if (KtcJson.TryProp(e, out var nested, "Transactions", "TransactionList") && nested.ValueKind == JsonValueKind.Array)
                    foreach (var t in nested.EnumerateArray()) { var m = MapTx(t, lookup, e, "", types); if (m != null) res.Add(m); }
                else { var m = MapTx(e, lookup, null, k, types); if (m != null) res.Add(m); }
            }
            if (rowsAll.Count > 0 && res.Count == 0 && rowsAll[0].Value.ValueKind == JsonValueKind.Object)
                _log.LogWarning("KTC returned {Rows} transaction records but none could be read; fields of the first: {Fields}", rowsAll.Count,
                    string.Join(", ", rowsAll[0].Value.EnumerateObject().Take(40).Select(p => p.Name)));
            return res;
        });
        IEnumerable<TransactionDto> r = all;
        if (!string.IsNullOrWhiteSpace(q.AtmId))
        {
            var ids = MatchAtms(await LookupAsync(new Warn(), ct), q.AtmId);
            var text = q.AtmId.Trim();
            r = r.Where(t => ids.Contains(t.AtmId) || t.AtmName.Contains(text, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(q.Client)) r = r.Where(t => t.Client.Equals(q.Client, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(q.Region)) r = r.Where(t => t.Region.Equals(q.Region, StringComparison.OrdinalIgnoreCase));
        if (q.Success.HasValue) r = r.Where(t => t.Success == q.Success.Value);
        return r.OrderByDescending(t => t.TimeUtc).ToList();
    }

    public async Task<TransactionDto?> GetTransactionAsync(string id, CancellationToken ct = default)
    {
        var w = new Warn(); var lookup = await LookupAsync(w, ct);
        var json = await Fetch($"api/v1/TransactionData/{Uri.EscapeDataString(id)}", w, ct: ct);
        var types = await TxLookupsAsync(w, ct);
        return KtcJson.Items(json).Select(e => MapTx(e, lookup, null, "", types)).FirstOrDefault(t => t != null);
    }

    /// <summary>
    /// The transaction details popup, from the routes KTC's own UI calls when a transaction is opened:
    /// TransactionData/{id} (or the transaction search), SessionData/{sessionId}, TransactionData/AllTransactionsWithSessionID,
    /// SessionData/{sessionId}/FieldsFromSession, TransactionData/{uuid}/FieldsFromTransaction (STX fields named through GET STXField,
    /// lookup values through STXFieldLookup), EJData/EJDataForClientBetweenIDs (startEJID..endEJID; EJRow by time when the ids are missing)
    /// and HistoricalCounter/ClientTransactionCounters (notes / items per device and denomination).
    /// </summary>
    public async Task<TransactionDetails?> GetTransactionDetailsAsync(string id, string? atmId, DateTime? timeUtc, CancellationToken ct = default)
    {
        var w = new Warn(); var lookup = await LookupAsync(w, ct); var types = await TxLookupsAsync(w, ct);
        var diag = new List<string>(); var missing = new Dictionary<string, string>();
        id = id.Trim();

        // ---- the transaction ----
        var raw = long.TryParse(id, out _) || Guid.TryParse(id, out _) ? await Fetch($"api/v1/TransactionData/{Uri.EscapeDataString(id)}", new Warn(), ct: ct) : null;
        JsonElement? rec = KtcJson.Items(raw).FirstOrDefault(e => e.ValueKind == JsonValueKind.Object) is { ValueKind: JsonValueKind.Object } r0 ? r0 : null;
        var tx = rec is { } re ? MapTx(re, lookup, null, id, types) : null;
        diag.Add($"TransactionData/{id}: {(rec != null ? "found" : "no record")}.");
        if (tx == null && timeUtc is { } at)
        {
            // Not readable by id: find it in the transaction search around its time.
            var found = await SearchTransactionsAsync(new TransactionQuery(atmId, null, null, at.AddHours(-1), at.AddHours(1)), ct);
            tx = found.FirstOrDefault(t => t.Id == id);
            diag.Add($"TransactionData/Search {at.AddHours(-1):HH:mm}-{at.AddHours(1):HH:mm} UTC: {(tx != null ? "found" : "not found")} among {found.Count}.");
        }
        if (tx == null) return null;
        var atm = tx.AtmId.Length > 0 ? tx.AtmId : KtcJson.NormId(atmId ?? "");
        string StxName(string sid) => sid.Length == 0 ? "" : types.Stx.GetValueOrDefault(sid) ?? "";
        var record = rec is { } rr
            ? TxDetailRules.Flatten(rr, (path, v) => Regex.IsMatch(path, @"^(typeID|completionID|reasonID|currencyID)$", RegexOptions.IgnoreCase) ? StxName(KtcJson.NormId(v)) : null)
            : new List<DetailField>
            {
                new("Transaction ID", tx.Id), new("ATM", $"{tx.AtmName} ({tx.AtmId})"), new("Type", tx.Type), new("Status", tx.Status), new("Reason", tx.Reason),
                new("Time (UTC)", tx.TimeUtc.ToString("yyyy-MM-dd HH:mm:ss")), new("Session ID", tx.SessionId), new("Amount", tx.Amount is { } a ? $"{a:0.##} {tx.Currency}" : ""),
                new("UUID", tx.Uuid), new("EJ rows", tx.StartEjId.Length > 0 ? $"{tx.StartEjId} - {tx.EndEjId}" : ""),
            }.Where(f => f.Value.Length > 0).ToList();

        // ---- session, fields, EJ, dispense in parallel ----
        var tSession = tx.SessionId.Length > 0 ? Fetch($"api/v1/SessionData/{tx.SessionId}", new Warn(), ct: ct) : Task.FromResult<JsonElement?>(null);
        var tSessionTx = tx.SessionId.Length > 0 ? Fetch($"api/v1/TransactionData/AllTransactionsWithSessionID?sessionid={tx.SessionId}", new Warn(), ct: ct) : Task.FromResult<JsonElement?>(null);
        var tSessionFields = tx.SessionId.Length > 0 ? Fetch($"api/v1/SessionData/{tx.SessionId}/FieldsFromSession", new Warn(), ct: ct) : Task.FromResult<JsonElement?>(null);
        var tFields = tx.Uuid.Length > 0 ? Fetch($"api/v1/TransactionData/{Uri.EscapeDataString(tx.Uuid)}/FieldsFromTransaction", new Warn(), ct: ct) : Task.FromResult<JsonElement?>(null);
        var tStx = StxFieldNamesAsync(ct);
        var tEj = TransactionEjAsync(atm, tx, ct);
        var route = _opt.TransactionCountersEndpoint?.Trim() ?? "";
        var tCounters = route.Length > 0 && long.TryParse(tx.Id, out _) && atm.Length > 0 ? TxCountersJsonAsync(route, atm, tx.Id, ct) : Task.FromResult<JsonElement?>(null);
        await Task.WhenAll(tSession, tSessionTx, tSessionFields, tFields, tStx, tEj, tCounters);

        // STX lookup values (LookupString fields) not in the cached STXFieldLookup list: one batch call.
        var stxFields = tStx.Result;
        var wanted = new List<string>();
        void CollectLookups(JsonElement? j) { foreach (var (_, e) in KtcJson.Rows(j)) if (e.ValueKind == JsonValueKind.Object && KtcJson.Str(e, "Type", "DataType", "ValueType", "FieldType").Contains("lookup", StringComparison.OrdinalIgnoreCase) && KtcJson.NormId(KtcJson.Str(e, "Value", "LookupValue", "IntValue")) is { Length: > 0 } v && !types.Stx.ContainsKey(v)) wanted.Add(v); }
        CollectLookups(tFields.Result); CollectLookups(tSessionFields.Result);
        var extra = wanted.Count > 0 ? await StxBatchAsync(wanted.Distinct().ToList(), ct) : new Dictionary<string, string>();
        string? LookupName(string v) => types.Stx.GetValueOrDefault(v) ?? extra.GetValueOrDefault(v);
        string? FieldName(string f) => stxFields.GetValueOrDefault(f);

        var session = TxDetailRules.Flatten(tSession.Result);
        var sessionTx = KtcJson.Rows(tSessionTx.Result).Where(x => x.Value.ValueKind == JsonValueKind.Object).Select(x => MapTx(x.Value, lookup, null, x.Key, types)).Where(t => t != null).Select(t => t!).OrderBy(t => t.TimeUtc).ToList();
        var sessionFields = TxDetailRules.ReadFields(tSessionFields.Result, FieldName, LookupName);
        var fields = TxDetailRules.ReadFields(tFields.Result, FieldName, LookupName);
        diag.Add($"SessionData/{(tx.SessionId.Length > 0 ? tx.SessionId : "-")}: {(tSession.Result != null ? "found" : "no answer")}; {sessionTx.Count} transactions in the session; {sessionFields.Count} session fields.");
        diag.Add($"FieldsFromTransaction: {(tx.Uuid.Length == 0 ? "no UUID on this transaction" : $"{fields.Count} fields")}; GET STXField: {stxFields.Count} field names.");
        if (tx.SessionId.Length == 0) missing["session"] = "KTC sent no session id for this transaction.";
        else if (tSession.Result == null && sessionTx.Count == 0) missing["session"] = $"KTC returned no data for session {tx.SessionId} (SessionData / AllTransactionsWithSessionID).";
        if (fields.Count == 0)
            missing["fields"] = tx.Uuid.Length == 0 ? "KTC sent no UUID for this transaction, which FieldsFromTransaction needs." : "KTC returned no STX fields for this transaction (TransactionData/{uuid}/FieldsFromTransaction).";

        var (ej, ejSource, ejDiag) = tEj.Result;
        diag.Add(ejDiag);
        if (ej.Count == 0) missing["ej"] = "KTC returned no EJ lines for this transaction.";

        // ---- dispense details: the transaction's device counters, named ----
        var dispense = new List<DispenseLine>();
        var recs = KtcJson.Items(tCounters.Result).Where(e => e.ValueKind == JsonValueKind.Object).ToList();
        if (recs.Count > 0)
        {
            var names = await HardwareNamesAsync(recs, ct); var denoms = await DenominationNamesAsync(ct);
            (string?, string?) Resolve(JsonElement e)
            {
                var cid = KtcJson.NormId(KtcJson.Str(e, CompIdFields)); var pid = KtcJson.NormId(KtcJson.Str(e, PropIdFields));
                return (cid.Length > 0 ? names.Components.GetValueOrDefault(cid) ?? "Component " + cid : null, pid.Length > 0 ? names.Properties.GetValueOrDefault(pid)?.Name : null);
            }
            foreach (var e in recs)
            {
                var kind = CounterRules.NoteKind(e, Resolve, out var comp, out _, out _);
                var (cn, pn) = Resolve(e);
                var nm = KtcJson.Str(e, "CounterName", "Counter", "Name");
                var counter = pn ?? (nm.Length > 0 ? nm : "Property " + KtcJson.Str(e, PropIdFields));
                var did = KtcJson.NormId(KtcJson.Str(e, "DenominationID", "DenominationId"));
                var delta = KtcJson.TryProp(e, out _, "Delta") ? (long)Math.Round(KtcJson.Dbl(e, 0, "Delta")) : (long)Math.Round(KtcJson.Dbl(e, 0, "Value", "Count"));
                long? running = KtcJson.TryProp(e, out _, "Delta") && KtcJson.TryProp(e, out _, "Value") ? (long)Math.Round(KtcJson.Dbl(e, 0, "Value")) : null;
                dispense.Add(new DispenseLine(cn ?? (comp.Length > 0 ? CounterRules.DeviceName(comp) : "Device"), CounterRules.SplitWords(counter),
                    did is "" or "0" ? "All" : denoms.GetValueOrDefault(did) ?? "Denomination " + did, delta, running, kind == 1 ? "Dispensed" : kind == 2 ? "Accepted" : ""));
            }
        }
        diag.Add(route.Length == 0 ? "Dispense: Ktc:TransactionCountersEndpoint is empty." : $"HistoricalCounter/ClientTransactionCounters: {recs.Count} records.");
        if (dispense.Count == 0)
            missing["dispense"] = route.Length == 0 ? "Per-transaction counters are turned off (Ktc:TransactionCountersEndpoint)."
                : tCounters.Result == null ? "KTC did not answer HistoricalCounter/ClientTransactionCounters for this transaction."
                : "KTC recorded no device counter changes for this transaction (nothing dispensed or deposited).";

        _log.LogInformation("Transaction details {Id} atm={Atm}: {Fields} fields, {SessionFields} session fields, {SessionTx} session transactions, {Ej} EJ lines ({EjSource}), {Dispense} counter lines",
            id, atm, fields.Count, sessionFields.Count, sessionTx.Count, ej.Count, ejSource, dispense.Count);
        return new TransactionDetails(tx, record, session, sessionTx, sessionFields, fields, ej, ejSource, dispense, missing, diag);
    }

    /// <summary>
    /// EJ lines of one transaction: EJData/EJDataForClientBetweenIDs with its startEJID / endEJID (what KTC's UI calls), else the EJ rows
    /// from one minute before to five minutes after the transaction start (EJRow), cut to the transaction when the journal marks it.
    /// </summary>
    private async Task<(List<string> Lines, string? Source, string Diag)> TransactionEjAsync(string atm, TransactionDto tx, CancellationToken ct)
    {
        if (atm.Length == 0) return (new(), null, "EJ: no ATM id.");
        if (tx.StartEjId.Length > 0 && tx.EndEjId.Length > 0)
        {
            var local = tx.LocalTime ?? tx.TimeUtc;
            var url = $"api/v1/EJData/EJDataForClientBetweenIDs?clientid={atm}&transactionStartTimeLocal={TxDetailRules.KtcTime(local)}&startid={tx.StartEjId}&endid={tx.EndEjId}";
            var lines = TxDetailRules.EjLines(await Quiet(url, ct));
            if (lines.Count > 0) return (lines, $"EJ rows {tx.StartEjId}-{tx.EndEjId}", $"EJDataForClientBetweenIDs {tx.StartEjId}-{tx.EndEjId}: {lines.Count} lines.");
        }
        var rows = await SearchEjAsync(new EjQuery(atm, tx.TimeUtc.AddMinutes(-1), tx.TimeUtc.AddMinutes(5), null, null, null), ct);
        var text = rows.OrderBy(r => r.TimeUtc).Select(r => r.Text).ToList();
        return (text, text.Count > 0 ? "EJ rows from 1 minute before to 5 minutes after the transaction start" : null,
            $"EJ: {(tx.StartEjId.Length > 0 ? "EJDataForClientBetweenIDs returned nothing; " : "no startEJID / endEJID on the transaction; ")}EJRow by time: {text.Count} lines.");
    }

    /// <summary>STX field id -> name (GET STXField). Cached.</summary>
    private Task<Dictionary<string, string>> StxFieldNamesAsync(CancellationToken ct) =>
        Cached("stx-fields", TimeSpan.FromMinutes(30), async () => NameMap(await Quiet("api/v1/STXField", ct), new[] { "STXFieldID", "FieldID" }));

    /// <summary>Names of STX lookup ids (POST STXFieldLookup/Batch with the ids, as KTC's UI does).</summary>
    private async Task<Dictionary<string, string>> StxBatchAsync(List<string> ids, CancellationToken ct)
    {
        var body = ids.Select(i => long.TryParse(i, out var l) ? l : 0).Where(l => l > 0).ToArray();
        foreach (var url in new[] { "api/v1/STXFieldLookup/Batch", "api/v1/STXFieldLookup//Batch" })
        {
            var r = await _api.SendAsync(HttpMethod.Post, url, body, new KtcCallOptions { Idempotent = true }, ct);
            if (!r.Success) continue;
            var map = new Dictionary<string, string>();
            foreach (var (k, e) in KtcJson.Rows(r.Json))
            {
                var lid = KtcJson.NormId(e.ValueKind == JsonValueKind.Object && KtcJson.Str(e, "ID", "Id", "STXFieldLookupID") is { Length: > 0 } x ? x : k);
                var name = e.ValueKind == JsonValueKind.Object ? KtcJson.Str(e, "Name", "Value", "DisplayName", "Description", "Text", "StringValue") : e.ToString();
                if (lid.Length > 0 && name.Length > 0) map.TryAdd(lid, name);
            }
            return map;
        }
        return new Dictionary<string, string>();
    }

    /// <summary>
    /// Names for the ids on a KTC transaction. typeID, completionID and reasonID are STX field lookup ids (GET STXFieldLookup);
    /// GET TransactionType lists the base types (1 CashWithdrawal, ...) with the lookup ids that belong to each (STXFieldLookupIDs).
    /// </summary>
    private sealed record TxLookups(Dictionary<string, string> Stx, Dictionary<string, string> Types, Dictionary<string, string> TypeByStx);

    private Task<TxLookups> TxLookupsAsync(Warn w, CancellationToken ct) =>
        Cached("tx-lookups", TimeSpan.FromMinutes(30), async () =>
        {
            var tj = Fetch("api/v1/TransactionType", w, ct: ct); var sj = Fetch("api/v1/STXFieldLookup", w, ct: ct);
            await Task.WhenAll(tj, sj);
            var types = new Dictionary<string, string>(); var byStx = new Dictionary<string, string>(); var stx = new Dictionary<string, string>();
            foreach (var (k, e) in KtcJson.Rows(tj.Result))
            {
                var id = KtcJson.NormId(KtcJson.Str(e, "ID", "Id", "TransactionTypeID") is { Length: > 0 } x ? x : k);
                var name = KtcJson.Str(e, "Name", "TypeName", "Description", "DisplayName");
                if (id.Length == 0 || name.Length == 0) continue;
                types.TryAdd(id, name);
                if (KtcJson.TryProp(e, out var ids, "STXFieldLookupIDs") && ids.ValueKind == JsonValueKind.Array)
                    foreach (var i in ids.EnumerateArray()) byStx.TryAdd(KtcJson.NormId(i.ToString()), name);
            }
            foreach (var (k, e) in KtcJson.Rows(sj.Result))
            {
                var id = KtcJson.NormId(KtcJson.Str(e, "ID", "Id", "STXFieldLookupID", "LookupID") is { Length: > 0 } x ? x : k);
                var name = KtcJson.Str(e, "Name", "Value", "DisplayName", "Description", "Text", "StringValue");
                if (id.Length > 0 && name.Length > 0) stx.TryAdd(id, name);
            }
            return new TxLookups(stx, types, byStx);
        });

    private static readonly Regex OkRx = new(@"success|approved|completed ok|\bok\b|completed$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private TransactionDto? MapTx(JsonElement e, Dictionary<string, ClientRec> lookup, JsonElement? parent, string key = "", TxLookups? types = null)
    {
        var id = KtcJson.Str(e, "ID", "Id", "TransactionID", "TransactionId", "TransactionDataID", "TransactionDataId", "UUID", "GUID");
        if (id.Length == 0) id = key;
        var atm = KtcJson.NormId(KtcJson.Str(e, "ClientID", "ClientId", "CID", "ClientIDInt"));
        if (atm.Length == 0 && parent is { } p) atm = KtcJson.NormId(KtcJson.Str(p, "ClientID", "ClientId", "CID"));
        if (id.Length == 0)
        {
            // Rows without an id field still count: key them by ATM + start time so the grid and totals show them.
            var when = KtcJson.Str(e, "StartTime", "TransactionStartTime", "TimeStamp", "Time", "TransactionTime");
            if (when.Length == 0) return null;
            id = (atm.Length > 0 ? atm + "-" : "") + when;
        }
        lookup.TryGetValue(atm, out var c);
        var result = KtcJson.Str(e, "CompletionCodeName", "CompletionCode", "Result", "StatusName", "Status", "Completion");
        if (result.Length == 0) result = Lookup(types, KtcJson.Str(e, "completionID", "CompletionCodeID"));
        var reason = KtcJson.Str(e, "ReasonName", "Reason", "ErrorText", "ErrorDescription");
        if (reason.Length == 0) reason = Lookup(types, KtcJson.Str(e, "reasonID"));
        var err = KtcJson.Str(e, "ErrorCode", "ErrorID");
        if (err.Length == 0) err = reason;
        bool ok = result.Length > 0 ? OkRx.IsMatch(result) && !Regex.IsMatch(result, "fail|error|declin|timeout|retract|revers", RegexOptions.IgnoreCase)
                                    : KtcJson.Int(e, 0, "CompletionCodeID", "CompletionID") == 0 && reason.Length == 0;
        var start = KtcJson.Date(e, "timestampUTC", "TimestampUtc", "StartTime", "TransactionStartTime", "Start", "StartTimestamp", "Timestamp_Start", "TimeStamp", "Timestamp", "Time", "TransactionTime", "DateTime")
                    ?? KtcJson.AnyDate(e) ?? DateTime.UtcNow;
        var end = KtcJson.Date(e, "EndTime", "TransactionEndTime", "End", "EndTimestamp", "Timestamp_End");
        var amount = KtcJson.Dbl(e, double.NaN, "Amount", "TransactionAmount", "RequestedAmount");
        return new TransactionDto
        {
            Id = id, AtmId = atm, AtmName = c?.Name ?? KtcJson.Str(e, "ClientName"), Client = c?.Business ?? "", Region = c?.Region ?? "",
            Type = TxType(e, types), Success = ok,
            Amount = double.IsNaN(amount) ? null : (decimal)amount, Currency = KtcJson.Str(e, "Currency", "CurrencyCode"), TimeUtc = start,
            ResponseMs = end.HasValue ? (int)Math.Max(0, (end.Value - start).TotalMilliseconds) : KtcJson.Int(e, 0, "ResponseTime", "ResponseMs"),
            ErrorCode = ok ? "" : (err.Length > 0 ? err : result), FailureCategory = ok ? "" : FailureClassifier.Classify(result + " " + reason),
            SessionId = KtcJson.NormId(KtcJson.Str(e, "sessionID", "SessionID", "SessionId")), Reason = reason,
            Uuid = KtcJson.Str(e, "UUID", "transactionUUID", "TransactionUUID", "Uuid", "GUID"),
            StartEjId = KtcJson.NormId(KtcJson.Str(e, "startEJID", "StartEJID", "StartEjId", "EJStartID")),
            EndEjId = KtcJson.NormId(KtcJson.Str(e, "endEJID", "EndEJID", "EndEjId", "EJEndID")),
            LocalTime = KtcJson.Date(e, "timestampLocal", "TimestampLocal", "localTimestamp", "LocalTime", "transactionStartTimeLocal")
                        ?? (KtcJson.TryProp(e, out _, "timestampUTC") ? KtcJson.Date(e, "timestamp") : null)
        };
    }

    /// <summary>The transaction type name: a name field if KTC sends one, else the TransactionTypeID looked up in GET TransactionType.</summary>
    private static string TxType(JsonElement e, TxLookups? t)
    {
        var name = KtcJson.Str(e, "TransactionTypeName", "TypeName", "TransactionTypeDescription");
        if (name.Length > 0) return name;
        var raw = KtcJson.Str(e, "TransactionTypeID", "TransactionTypeId", "TypeID", "TypeId", "TransactionType", "Type", "TxnType");
        if (raw.Length == 0) return "";
        var id = KtcJson.NormId(raw);
        if (t != null)
        {
            if (t.TypeByStx.TryGetValue(id, out var bt) && t.Stx.TryGetValue(id, out var sn) && !sn.Equals(bt, StringComparison.OrdinalIgnoreCase)) return $"{bt} ({sn})";
            if (t.TypeByStx.TryGetValue(id, out bt)) return bt;
            if (t.Stx.TryGetValue(id, out sn)) return sn;
            if (t.Types.TryGetValue(id, out var tn)) return tn;
        }
        return long.TryParse(id, out _) ? "Type " + id : raw;
    }

    private static string Lookup(TxLookups? t, string rawId)
    {
        if (rawId.Length == 0) return "";
        var id = KtcJson.NormId(rawId);
        return t != null && t.Stx.TryGetValue(id, out var n) ? n : "";
    }

    // ---------------- EJ ----------------
    public async Task<IReadOnlyList<EjEntryDto>> SearchEjAsync(EjQuery q, CancellationToken ct = default)
    {
        var w = new Warn(); var lookup = await LookupAsync(w, ct);
        var norm = KtcJson.NormId(q.AtmId);
        var matches = MatchAtms(lookup, q.AtmId);
        var id = lookup.ContainsKey(norm) ? norm
               : lookup.Values.FirstOrDefault(c => Exact(c, q.AtmId.Trim()))?.Id
               ?? matches.FirstOrDefault()
               ?? (long.TryParse(norm, out _) ? norm : null);
        if (matches.Count > 1 && !lookup.ContainsKey(norm)) _log.LogInformation("EJ search: '{Atm}' matches {Count} ATMs; using {Id}", q.AtmId, matches.Count, id);
        if (id == null) { _log.LogInformation("EJ search: no ATM matches '{Atm}'", q.AtmId); return Array.Empty<EjEntryDto>(); }

        // KTC docs: EJRow body is {Value1: start, Value2: end, Value3: [app names to EXCLUDE]}, dates as ISO (yyyyMMddHHmmssfff fails here).
        var r = await _api.SendAsync(HttpMethod.Post, $"api/v1/Client/{id}/EJRow", new { Value1 = q.From, Value2 = q.To, Value3 = _opt.EjExcludeApps ?? Array.Empty<string>() }, new KtcCallOptions { Idempotent = true }, ct);
        if (!r.Success) _log.LogWarning("EJ search for client {Id} {From:u}..{To:u} failed: {Error}", id, q.From, q.To, r.Error);
        var json = r.Success ? r.Json : null;

        // Rows are UDT_EJROW {rid, aid, tsp, ssid, oid, ejd, atsp}: rid = row id, tsp = ATM local time, ejd = journal text,
        // atsp = when the server received it (UTC). The ATM's UTC offset is taken from tsp - atsp, rounded to 15 minutes.
        var rows = new List<(string Seq, DateTime Time, string Text)>();
        foreach (var (key, e) in KtcJson.Rows(json))
        {
            string text;
            if (e.ValueKind == JsonValueKind.String) text = e.GetString() ?? "";
            else
            {
                text = KtcJson.Str(e, "ejd", "Text", "EJText", "Data", "Line", "Value2", "Value", "Description", "RowText", "Content");
                if (text.Length == 0) text = string.Join(" | ", SchemaInferer.Leaves(e).Where(l => l.Value.Length > 0).Select(l => l.Value));
            }
            var seq = e.ValueKind == JsonValueKind.Object ? KtcJson.Str(e, "rid", "Sequence", "RowNumber", "EJID", "EJRowID", "ID", "Id") : "";
            DateTime? time = null;
            if (e.ValueKind == JsonValueKind.Object)
            {
                var local = KtcJson.Date(e, "tsp", "TimeStamp", "Timestamp", "Time", "Value1", "TransactionStartTime", "DateTime", "Date");
                var recv = KtcJson.Date(e, "atsp");
                if (local is { } l && recv is { Year: > 1900 } rv && Math.Abs((l - rv).TotalHours) <= 14)
                {
                    var off = TimeSpan.FromMinutes(Math.Round((l - rv).TotalMinutes / 15) * 15);
                    time = l - off;
                }
                else time = local ?? KtcJson.AnyDate(e);
            }
            rows.Add((seq.Length > 0 ? seq : key, time ?? q.From, text));
        }
        if (rows.Count == 0 && r.Success) _log.LogInformation("EJ search for client {Id} {From:u}..{To:u}: KTC returned no rows ({Shape})", id, q.From, q.To, r.Json?.ValueKind.ToString() ?? "empty");

        // Filtering runs on RAW text (so a full PAN search works) - only masked text is ever returned.
        IEnumerable<(string Seq, DateTime Time, string Text)> f = rows;
        if (!string.IsNullOrWhiteSpace(q.Sequence)) f = f.Where(r => r.Seq.Contains(q.Sequence.Trim(), StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(q.Text)) f = f.Where(r => r.Text.Contains(q.Text.Trim(), StringComparison.OrdinalIgnoreCase));
        var numbered = EjTimeline.Number(rows);                     // numbering uses all rows so transactions stay whole
        if (!string.IsNullOrWhiteSpace(q.CardNumber))
        {
            var digits = PanMasker.Digits(q.CardNumber);
            var rawTx = new HashSet<int>();
            var sorted = rows.OrderBy(x => x.Time).ThenBy(x => x.Seq, StringComparer.Ordinal).ToList();
            for (int i = 0; i < sorted.Count; i++) if (PanMasker.Digits(sorted[i].Text).Contains(digits)) rawTx.Add(numbered[i].TransactionNo);
            numbered = numbered.Where(n => rawTx.Contains(n.TransactionNo)).ToList();
        }
        var keep = f.Select(r => (r.Seq, r.Time)).ToHashSet();
        if (!string.IsNullOrWhiteSpace(q.Sequence) || !string.IsNullOrWhiteSpace(q.Text))
        {
            var txs = numbered.Where(n => keep.Contains((n.Sequence, n.TimeUtc))).Select(n => n.TransactionNo).ToHashSet();
            numbered = numbered.Where(n => txs.Contains(n.TransactionNo)).ToList();
        }
        return numbered;
    }
}
