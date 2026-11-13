using System.Linq;
using System.Globalization;
using System.Text.Json;

namespace Ktc.Application.Logic;

/// <summary>
/// Tolerant JSON reader. The KTC collection ships without response samples, so every field is looked up by a list of
/// candidate names (case-insensitive). Adjust/extend the alias lists when real payloads are verified.
/// </summary>
public static class KtcJson
{
    public static IEnumerable<JsonElement> Items(JsonElement? root) => Rows(root).Select(r => r.Value);

    /// <summary>
    /// The records in a KTC answer, with the key they were stored under when KTC returns an id-keyed map of records
    /// ({"42348": {...}, "42349": {...}}, as TransactionData/Search does); the key is "" for arrays and wrapped lists.
    /// An empty object is no records.
    /// </summary>
    public static IEnumerable<(string Key, JsonElement Value)> Rows(JsonElement? root)
    {
        if (root is not { } r) yield break;
        if (r.ValueKind == JsonValueKind.Array) { foreach (var e in r.EnumerateArray()) yield return ("", e); yield break; }
        if (r.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "items", "data", "results", "result", "value", "values", "records", "list" })
                if (TryProp(r, out var inner, name) && inner.ValueKind == JsonValueKind.Array)
                { foreach (var e in inner.EnumerateArray()) yield return ("", e); yield break; }
            if (!r.EnumerateObject().Any()) yield break;
            if (IsIdMap(r) && r.EnumerateObject().All(p => p.Value.ValueKind == JsonValueKind.Object))
            { foreach (var p in r.EnumerateObject()) yield return (p.Name, p.Value); yield break; }
            yield return ("", r);
        }
    }

    /// <summary>
    /// Reads "client id -> value" data that KTC returns in either of two shapes: an array of records ({ClientID, LastContact})
    /// or one object keyed by client id ({"3": "2026-11-13T10:00:00", "4": ...}, as CurrentStatus/LastTimeInService does).
    /// </summary>
    public static IEnumerable<(string Id, JsonElement Value)> ById(JsonElement? root, params string[] idNames)
    {
        if (root is not { } r) yield break;
        if (r.ValueKind == JsonValueKind.Object && IsIdMap(r))
        {
            foreach (var p in r.EnumerateObject()) yield return (p.Name, p.Value);
            yield break;
        }
        foreach (var e in Items(r))
        {
            var id = Str(e, idNames);
            if (id.Length > 0) yield return (id, e);
        }
    }

    /// <summary>True for an object whose property names are all whole numbers (an id-keyed map rather than a record).</summary>
    public static bool IsIdMap(JsonElement o)
    {
        var any = false;
        foreach (var p in o.EnumerateObject()) { if (!long.TryParse(p.Name, out _)) return false; any = true; }
        return any;
    }

    /// <summary>A date that is either the element itself (a string) or one of the named properties of it.</summary>
    public static DateTime? DateValue(JsonElement e, params string[] names)
    {
        if (e.ValueKind == JsonValueKind.String && DateTime.TryParse(e.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d))
            return d.Year <= 1901 ? null : d;
        return Date(e, names);
    }

    /// <summary>The first property that holds a plausible date (year 2000+), for records whose date field name is not known.</summary>
    public static DateTime? AnyDate(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in e.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: >= 8 } s && (s.Contains('-') || s.Contains('/')) &&
                DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) && d.Year >= 2000)
                return d;
        return null;
    }

    /// <summary>Normalises an id: trims, and turns 3.0 / "003" style numbers into "3" so ids from different routes match.</summary>
    public static string NormId(string id)
    {
        id = id.Trim();
        if (decimal.TryParse(id, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) && n == decimal.Truncate(n) && n >= 0) return ((long)n).ToString(CultureInfo.InvariantCulture);
        return id;
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
