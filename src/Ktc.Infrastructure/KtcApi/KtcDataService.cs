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
            ErrorCode = ok ? "" : (err.Length > 0 ? err : result), FailureCategory = ok ? "" : FailureClassifier.Classify(result + " " + reason)
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
