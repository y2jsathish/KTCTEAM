using System.Text.Json;
using System.Text.RegularExpressions;
using Ktc.Application.Models;

namespace Ktc.Application.Logic;

/// <summary>Parses a Postman v2.x collection into flat endpoint descriptors. Handles folders, empty url.raw (name carries the route) and HTML-encoded names.</summary>
public static class PostmanImporter
{
    private static readonly Regex NameRx = new(@"^\s*(GET|POST|PUT|DELETE|PATCH|HEAD|OPTIONS)\s+(\S+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ApiPath = new(@"api/v\d+/([^/?]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<ImportedEndpoint> Parse(Stream json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new List<ImportedEndpoint>();
        if (doc.RootElement.TryGetProperty("item", out var items)) Walk(items, result);
        return result.GroupBy(e => (e.Method, e.RelativeUrl)).Select(g => g.First()).ToList();
    }

    private static void Walk(JsonElement items, List<ImportedEndpoint> acc)
    {
        foreach (var item in items.EnumerateArray())
        {
            if (item.TryGetProperty("item", out var children)) { Walk(children, acc); continue; }
            if (!item.TryGetProperty("request", out var req)) continue;

            var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            string method = req.TryGetProperty("method", out var m) ? m.GetString() ?? "GET" : "GET";
            string? url = null;

            if (req.TryGetProperty("url", out var u))
            {
                if (u.ValueKind == JsonValueKind.String) url = u.GetString();
                else if (u.ValueKind == JsonValueKind.Object && u.TryGetProperty("raw", out var raw)) url = raw.GetString();
            }
            var nm = NameRx.Match(System.Net.WebUtility.HtmlDecode(name));
            if (string.IsNullOrWhiteSpace(url) && nm.Success) { method = nm.Groups[1].Value; url = nm.Groups[2].Value; }
            if (string.IsNullOrWhiteSpace(url)) continue;

            url = System.Net.WebUtility.HtmlDecode(url).Replace("{{baseUrl}}", "").Replace("{{base_url}}", "").Trim();
            var ix = url.IndexOf("api/v", StringComparison.OrdinalIgnoreCase);
            if (ix < 0) continue;                       // not an API route (e.g. stray "GET"/"HEAD" probes)
            url = url[ix..];

            var mod = ApiPath.Match(url);
            if (!mod.Success) continue;

            string? body = null;
            if (req.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.Object && b.TryGetProperty("raw", out var r))
                body = r.GetString();

            string? desc = item.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            acc.Add(new ImportedEndpoint(mod.Groups[1].Value, ApiCatalogRules.EndpointDisplayName(method, url), method.ToUpperInvariant(), url, body, desc));
        }
    }
}
