using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ktc.Application.Abstractions;
using Ktc.Application.Logic;
using Ktc.Application.Models;
using Ktc.Domain.Entities;
using Ktc.Domain.Enums;
using Ktc.Infrastructure.KtcApi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Ktc.Infrastructure.Services;

/// <summary>Generic executor for any catalogued endpoint: Execute, Cache, Retry (via the HTTP pipeline), Export, Schedule.</summary>
public class ApiExecutionService : IApiExecutionService
{
    private static readonly Dictionary<int, DateTime> LastRun = new();
    private readonly IUnitOfWork _uow; private readonly IKtcApiClient _api; private readonly IMemoryCache _cache; private readonly IExportService _export; private readonly ILogger<ApiExecutionService> _log;
    public ApiExecutionService(IUnitOfWork uow, IKtcApiClient api, IMemoryCache cache, IExportService export, ILogger<ApiExecutionService> log)
    { _uow = uow; _api = api; _cache = cache; _export = export; _log = log; }

    private static readonly Regex Placeholder = new(@"\{(\w+)\}", RegexOptions.Compiled);

    public async Task<ApiExecutionResult> ExecuteAsync(int endpointId, IDictionary<string, string>? p, string? body, bool useCache, bool allowMutating, CancellationToken ct = default)
    {
        var ep = await _uow.Repo<ApiEndpoint>().Query().Include(e => e.Mappings).FirstOrDefaultAsync(e => e.Id == endpointId, ct);
        if (ep == null) return Fail("Endpoint not found.");
        if (ep.IsSensitive) return Fail("This endpoint exposes secrets/credentials and is blocked by policy.");
        if (!ep.Enabled) return Fail("Endpoint is disabled. An administrator must enable it in the API Catalog.");
        if (ep.Kind == EndpointKind.Authentication) return Fail("Authentication endpoints are handled by the login module.");
        bool mutating = ep.Kind == EndpointKind.Update;
        if (mutating && !allowMutating) return Fail("Mutating endpoints (PUT/POST/DELETE) require administrator rights.");

        p ??= new Dictionary<string, string>();
        var url = BuildUrl(ep.RelativeUrl, p, out var missing);
        if (missing != null) return Fail($"Missing parameter '{missing}'.");
        if (ApiCatalogRules.DefaultQuery(url, DateTime.UtcNow) is { } dq) url += "?" + dq;

        var ttl = TimeSpan.FromSeconds(Math.Max(5, ep.Mappings.FirstOrDefault()?.RefreshInterval ?? 30));
        var key = "exec:" + endpointId + ":" + Hash(url + "|" + body);
        if (useCache && !mutating && _cache.TryGetValue(key, out ApiExecutionResult? hit) && hit != null) return hit with { FromCache = true };

        var method = new HttpMethod(ep.HttpMethod);
        var payload = method == HttpMethod.Get || method == HttpMethod.Delete ? null : (string.IsNullOrWhiteSpace(body) ? ep.RequestSchema : body);
        var r = await _api.SendAsync(method, url, payload, new KtcCallOptions { Idempotent = !mutating }, ct);
        var res = new ApiExecutionResult(r.Success, r.StatusCode, r.Error, r.Raw, r.ElapsedMs, false, DateTime.UtcNow);

        if (r.Success && string.IsNullOrEmpty(ep.ResponseSchema))
        {
            var schema = SchemaInferer.Infer(r.Raw);            // learn response shape from first real answer
            if (schema != null) { ep.ResponseSchema = schema.Length > 20000 ? schema[..20000] : schema; _uow.Repo<ApiEndpoint>().Update(ep); await _uow.SaveChangesAsync(ct); }
        }
        if (r.Success && !mutating && useCache) _cache.Set(key, res, ttl);
        return res;
    }

    public static string BuildUrl(string template, IDictionary<string, string> p, out string? missing)
    {
        missing = null;
        var qIdx = template.IndexOf('?');
        var path = qIdx < 0 ? template : template[..qIdx];
        string? miss = null;
        path = Placeholder.Replace(path, m => { if (p.TryGetValue(m.Groups[1].Value, out var v) && !string.IsNullOrWhiteSpace(v)) return Uri.EscapeDataString(v); miss ??= m.Groups[1].Value; return m.Value; });
        missing = miss;
        if (qIdx < 0) return path;
        var parts = template[(qIdx + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries).Select(part =>
        {
            var kv = part.Split('=', 2); var m = kv.Length == 2 ? Placeholder.Match(kv[1]) : null;
            var name = m is { Success: true } ? m.Groups[1].Value : null;
            if (name == null) return part;
            return p.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? kv[0] + "=" + Uri.EscapeDataString(v) : null;   // omit unset optional query params
        }).Where(x => x != null);
        var q = string.Join("&", parts);
        return q.Length == 0 ? path : path + "?" + q;
    }

    public async Task<(byte[] Data, string FileName, string MimeType)> ExportAsync(int id, IDictionary<string, string>? p, ExportFormat format, CancellationToken ct = default)
    {
        var r = await ExecuteAsync(id, p, null, false, false, ct);
        if (!r.Success || r.Body == null) throw new InvalidOperationException(r.Error ?? "Execution failed.");
        var ep = await _uow.Repo<ApiEndpoint>().GetAsync(id, ct);
        var data = ToReport(ep?.EndpointName ?? "API result", r.Body);
        var name = new string((ep?.EndpointName ?? "api").Where(char.IsLetterOrDigit).ToArray()) + "_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmm");
        return format switch
        {
            ExportFormat.Excel => (_export.ToExcel(data), name + ".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
            ExportFormat.Pdf => (_export.ToPdf(data), name + ".pdf", "application/pdf"),
            _ => (_export.ToCsv(data), name + ".csv", "text/csv")
        };
    }

    public static ReportData ToReport(string title, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var items = KtcJson.Items(doc.RootElement).Take(5000).ToList();
        var cols = items.Where(i => i.ValueKind == JsonValueKind.Object).SelectMany(i => i.EnumerateObject().Select(x => x.Name)).Distinct().Take(30).ToList();
        if (cols.Count == 0) cols.Add("value");
        var rows = items.Select(i => (IReadOnlyList<string>)cols.Select(c => i.ValueKind == JsonValueKind.Object && i.TryGetProperty(c, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : (c == "value" ? i.ToString() : "")).ToList()).ToList();
        return new ReportData(title, DateTime.UtcNow, null, cols, rows, new[] { ("Rows", rows.Count.ToString()) });
    }

    public async Task<int> RunScheduledMappingsAsync(CancellationToken ct = default)
    {
        var maps = await _uow.Repo<ApiMapping>().Query().Include(m => m.ApiEndpoint)
            .Where(m => m.ExecutionMode != ExecutionMode.Manual && m.ApiEndpoint!.Enabled && !m.ApiEndpoint.IsSensitive && m.ApiEndpoint.HttpMethod == "GET" && !m.ApiEndpoint.RelativeUrl.Contains("{"))
            .ToListAsync(ct);
        int n = 0;
        foreach (var m in maps)
        {
            lock (LastRun) { if (LastRun.TryGetValue(m.Id, out var last) && (DateTime.UtcNow - last).TotalSeconds < Math.Max(15, m.RefreshInterval)) continue; LastRun[m.Id] = DateTime.UtcNow; }
            using var scope = KtcTokenScope.ForceService();
            var r = await ExecuteAsync(m.ApiEndpointId, null, null, false, false, ct);
            if (!r.Success) continue;
            var key = "api:" + m.ApiEndpointId;
            var repo = _uow.Repo<DashboardCache>();
            var row = await repo.Query().FirstOrDefaultAsync(c => c.Key == key, ct);
            var payload = r.Body!.Length > 1_000_000 ? r.Body[..1_000_000] : r.Body;
            if (row == null) await repo.AddAsync(new DashboardCache { Key = key, PayloadJson = payload }, ct); else { row.PayloadJson = payload; row.UpdatedUtc = DateTime.UtcNow; repo.Update(row); }
            await _uow.SaveChangesAsync(ct); n++;
        }
        return n;
    }

    private static ApiExecutionResult Fail(string e) => new(false, 0, e, null, 0, false, DateTime.UtcNow);
    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16];
}
