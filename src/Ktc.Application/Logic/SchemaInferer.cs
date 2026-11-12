using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ktc.Application.Logic;

/// <summary>Builds a compact JSON "schema" (field -> type) from a sample response so the catalog can learn response shapes.</summary>
public static class SchemaInferer
{
    public static string? Infer(string? json, int maxDepth = 4)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return Node(doc.RootElement, 0, maxDepth)?.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        catch { return null; }
    }

    private static JsonNode? Node(JsonElement e, int depth, int max)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                if (depth >= max) return "object";
                var o = new JsonObject();
                foreach (var p in e.EnumerateObject()) o[p.Name] = Node(p.Value, depth + 1, max);
                return o;
            case JsonValueKind.Array:
                var a = new JsonArray();
                var first = e.EnumerateArray().FirstOrDefault();
                if (first.ValueKind != JsonValueKind.Undefined && depth < max) a.Add(Node(first, depth + 1, max));
                return a;
            case JsonValueKind.String: return "string";
            case JsonValueKind.Number: return "number";
            case JsonValueKind.True: case JsonValueKind.False: return "boolean";
            default: return "null";
        }
    }

    /// <summary>Flatten a JSON document into (path, value) leaves for numeric/status field mapping UIs.</summary>
    public static IEnumerable<(string Path, string Value)> Leaves(JsonElement e, string prefix = "", int depth = 0)
    {
        if (depth > 5) yield break;
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                    foreach (var l in Leaves(p.Value, prefix.Length == 0 ? p.Name : prefix + "." + p.Name, depth + 1)) yield return l;
                break;
            case JsonValueKind.Array:
                int i = 0;
                foreach (var x in e.EnumerateArray()) { if (i++ >= 3) break; foreach (var l in Leaves(x, prefix + "[]", depth + 1)) yield return l; }
                break;
            default: yield return (prefix, e.ToString()); break;
        }
    }
}
