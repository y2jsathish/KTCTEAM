using System.Globalization;
using System.Text.Json;

namespace Ktc.Application.Logic;

/// <summary>
/// Tolerant JSON reader. The KTC collection ships without response samples, so every field is looked up by a list of
/// candidate names (case-insensitive). Adjust/extend the alias lists when real payloads are verified.
/// </summary>
public static class KtcJson
{
    public static IEnumerable<JsonElement> Items(JsonElement? root)
    {
        if (root is not { } r) yield break;
        if (r.ValueKind == JsonValueKind.Array) { foreach (var e in r.EnumerateArray()) yield return e; yield break; }
        if (r.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "items", "data", "results", "result", "value", "values", "records", "list" })
                if (TryProp(r, out var inner, name) && inner.ValueKind == JsonValueKind.Array)
                { foreach (var e in inner.EnumerateArray()) yield return e; yield break; }
            yield return r;
        }
    }

    public static bool TryProp(JsonElement e, out JsonElement value, params string[] names)
    {
        value = default;
        if (e.ValueKind != JsonValueKind.Object) return false;
        foreach (var n in names)
            foreach (var p in e.EnumerateObject())
                if (string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind != JsonValueKind.Null)
                { value = p.Value; return true; }
        return false;
    }

    public static string Str(JsonElement e, params string[] names)
    {
        if (!TryProp(e, out var v, names)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => ""
        };
    }

    public static int Int(JsonElement e, int def, params string[] names)
    {
        if (!TryProp(e, out var v, names)) return def;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out i)) return i;
        return def;
    }

    public static double Dbl(JsonElement e, double def, params string[] names)
    {
        if (!TryProp(e, out var v, names)) return def;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out d)) return d;
        return def;
    }

    public static bool Bool(JsonElement e, bool def, params string[] names)
    {
        if (!TryProp(e, out var v, names)) return def;
        return v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(v.GetString(), out var b) ? b : def, JsonValueKind.Number => v.GetDouble() != 0, _ => def };
    }

    public static DateTime? Date(JsonElement e, params string[] names)
    {
        if (!TryProp(e, out var v, names)) return null;
        if (v.ValueKind == JsonValueKind.String && DateTime.TryParse(v.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d))
            return d.Year <= 1901 ? null : d;      // .NET MinValue / SQL default dates mean "not set"
        return null;
    }
}
