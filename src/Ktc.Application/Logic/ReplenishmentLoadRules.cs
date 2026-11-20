using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ktc.Application.Models;

namespace Ktc.Application.Logic;

/// <summary>
/// Reads the notes loaded per note value out of KTC's own replenishment records (POST Client/{id}/ReplenishmentsByTimeRange, GET Client/{id}/ReplenishmentOrders).
/// KTC does not document these records. The KTC client's Replenishments tab shows them as one row per replenishment (Id, Component, Timestamp,
/// Transaction) with child rows Property (Loaded, Removed, Remaining, Dispensed, Rejected, ...) x Denomination ("AED 100") with Before and After;
/// the Loaded After value is the notes loaded. KTC names things by id elsewhere (PropertyID, DenominationID), so a child may carry a name or an id:
/// ids are named through <c>resolve</c> (key, id -> name). The children are looked for up to four levels down, and a flat cassette line (unit /
/// note value / count) is accepted too. The newest record with a positive load wins; a record that sets Loaded to 0 is a cash removal and is
/// skipped. When nothing matches, the result is empty and the forecast falls back to the counters.
/// </summary>
public static class ReplenishmentLoadRules
{
    private static readonly string[] TimeNames = { "ReplenishmentTime", "ReplenishedTime", "ReplenishmentDate", "Timestamp", "TimeStamp", "Time", "EndTime", "CompletedTime", "TimeCompleted", "Date", "DateTime", "CreatedTime", "TimeCreated", "StartTime" };
    private static readonly string[] UnitNames = { "CashUnitNumber", "CassetteNumber", "UnitNumber", "CassetteNo", "CassetteID", "Cassette", "Unit", "Number" };
    private static readonly string[] ValueNames = { "Denomination", "CurrencyValue", "NoteValue", "Value" };
    private static readonly string[] PropertyNames = { "PropertyName", "Property", "CounterName", "Name", "Counter", "Type" };
    private static readonly string[] PropertyIds = { "PropertyID", "DBPropertyID", "PropID", "DBPropID", "CounterID" };
    private static readonly string[] DenomNames = { "DenominationName", "Denomination", "DenominationText", "CurrencyValue", "NoteValue", "Value" };
    private static readonly string[] DenomIds = { "DenominationID", "DenomID" };
    private static readonly string[] AfterNames = { "After", "AfterValue", "ValueAfter", "NewValue", "AfterCount", "CounterAfter", "ValueNew", "New" };
    private static readonly string[] CountNames = { "LoadedCount", "NotesLoaded", "LoadedNotes", "CountLoaded", "CountAfter", "AfterCount", "NewCount", "NoteCount", "NotesCount", "Count", "Notes", "Quantity", "Loaded" };

    /// <summary>Loads of the newest record that holds loaded counts in <paramref name="routes"/> (the JSON of each replenishment route). Empty when none does.</summary>
    public static IReadOnlyList<KtcLoad> Parse(IEnumerable<JsonElement?> routes, Func<string, string, string?>? resolve = null)
    {
        var best = new List<KtcLoad>(); DateTime? bestTime = null; var found = false;
        foreach (var root in routes)
            foreach (var rec in KtcJson.Items(root).Where(e => e.ValueKind == JsonValueKind.Object))
            {
                var lines = Lines(rec, resolve);
                if (lines.Count == 0) continue;
                var t = KtcJson.Date(rec, TimeNames) ?? KtcJson.AnyDate(rec);
                if (!found || (t.HasValue && (!bestTime.HasValue || t > bestTime))) { best = lines.Select(l => l with { TimeUtc = t }).ToList(); bestTime = t; found = true; }
            }
        return best;
    }

    /// <summary>What the replenishment records look like, for "What was read": how many, the newest time, and the fields of the first records with their first child.</summary>
    public static IReadOnlyList<string> Describe(JsonElement? root, int maxRecords = 2)
    {
        var recs = KtcJson.Items(root).Where(e => e.ValueKind == JsonValueKind.Object).ToList();
        if (recs.Count == 0) return new[] { "no records" };
        var times = recs.Select(r => KtcJson.Date(r, TimeNames) ?? KtcJson.AnyDate(r)).Where(t => t.HasValue).Select(t => t!.Value).ToList();
        var res = new List<string> { $"{recs.Count} record(s){(times.Count > 0 ? $", times {times.Min():yyyy-MM-dd HH:mm} to {times.Max():yyyy-MM-dd HH:mm} UTC" : "")}" };
        foreach (var r in recs.Take(maxRecords)) res.Add("record: " + Brief(r, 0));
        return res;
    }

    /// <summary>Why the records gave no loaded count: for the first record, what its children are called and which test rejected them.</summary>
    public static string WhyUnusable(JsonElement? root, Func<string, string, string?>? resolve = null)
    {
        var recs = KtcJson.Items(root).Where(e => e.ValueKind == JsonValueKind.Object).ToList();
        if (recs.Count == 0) return "the answer holds no record objects";
        var rec = recs[0];
        var kids = Descendants(rec, 0, null, resolve).ToList();
        if (kids.Count == 0) return "the newest record has no child objects (no Property / Denomination rows), only its own fields";
        var props = kids.Select(k => k.Prop).Where(p => p != null).Distinct().Take(12).ToList();
        if (props.Count == 0) return $"none of the {kids.Count} child object(s) names a Property (looked for {string.Join("/", PropertyNames)} or an id {string.Join("/", PropertyIds)} that PropertyList names)";
        if (!props.Any(p => string.Equals(p, "Loaded", StringComparison.OrdinalIgnoreCase))) return $"no child has Property \"Loaded\" (properties seen: {string.Join(", ", props)})";
        var loaded = kids.Where(k => string.Equals(k.Prop, "Loaded", StringComparison.OrdinalIgnoreCase)).ToList();
        if (!loaded.Any(k => Denomination(k.El, resolve) != null)) return $"{loaded.Count} Loaded child(ren) have no readable note value (looked for {string.Join("/", DenomNames)} or an id {string.Join("/", DenomIds)} that Denomination names)";
        if (!loaded.Any(k => !double.IsNaN(KtcJson.Dbl(k.El, double.NaN, AfterNames)))) return $"{loaded.Count} Loaded child(ren) have no After count (looked for {string.Join("/", AfterNames)})";
        return "every Loaded After count in the newest record is 0 (cash removed), so the record was skipped";
    }

    private static string Brief(JsonElement e, int depth)
    {
        var sb = new StringBuilder("{");
        var first = true;
        foreach (var p in e.EnumerateObject())
        {
            if (!first) sb.Append(", "); first = false;
            switch (p.Value.ValueKind)
            {
                case JsonValueKind.Array:
                    var n = p.Value.GetArrayLength();
                    sb.Append($"{p.Name}[{n}]");
                    if (n > 0 && depth < 2 && p.Value[0].ValueKind == JsonValueKind.Object) sb.Append(" first: ").Append(Brief(p.Value[0], depth + 1));
                    break;
                case JsonValueKind.Object: sb.Append(p.Name).Append(depth < 2 ? ": " + Brief(p.Value, depth + 1) : ": {..}"); break;
                default: var t = p.Value.ToString(); sb.Append($"{p.Name}={(t.Length > 40 ? t[..40] + "..." : t)}"); break;
            }
        }
        return sb.Append('}').ToString();
    }

    /// <summary>Every object below <paramref name="e"/> (up to four levels) with the property name ("Loaded", ...) of the nearest ancestor that names one.</summary>
    private static IEnumerable<(JsonElement El, string? Prop)> Descendants(JsonElement e, int depth, string? prop, Func<string, string, string?>? resolve)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            var own = PropName(e, resolve);
            var p = own ?? prop;
            if (depth > 0) yield return (e, p);
            if (depth < 4) foreach (var c in e.EnumerateObject()) foreach (var d in Descendants(c.Value, depth + 1, p, resolve)) yield return d;
        }
        else if (e.ValueKind == JsonValueKind.Array && depth < 4)
            foreach (var x in e.EnumerateArray()) foreach (var d in Descendants(x, depth + 1, prop, resolve)) yield return d;
    }

    private static List<KtcLoad> Lines(JsonElement rec, Func<string, string, string?>? resolve)
    {
        var res = new List<KtcLoad>(); var cleared = false;
        foreach (var (e, prop) in Descendants(rec, 0, null, resolve))
            if (PropertyLine(e, prop, resolve) is { } pl) { if (pl.Notes > 0) res.Add(pl); else cleared = true; }
        if (res.Count == 0 && !cleared)
        {
            foreach (var p in rec.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Array))
                foreach (var e in p.Value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object))
                    if (Line(e) is { } l) res.Add(l);
            if (res.Count == 0 && Line(rec) is { } one) res.Add(one);
        }
        return res.GroupBy(l => (l.Unit, l.Denomination)).Select(g => g.Last()).ToList();
    }

    /// <summary>The property of an object: its name, or the name of its PropertyID. Null when it names none.</summary>
    private static string? PropName(JsonElement e, Func<string, string, string?>? resolve)
    {
        var prop = KtcJson.Str(e, PropertyNames);
        if (prop.Length > 0 && !double.TryParse(prop, NumberStyles.Any, CultureInfo.InvariantCulture, out _)) return prop.Trim();
        return KtcJson.Str(e, PropertyIds) is { Length: > 0 } pid && resolve?.Invoke("propertyid", pid) is { Length: > 0 } pn ? pn.Trim() : null;
    }

    /// <summary>One child of the Replenishments tab shape: under Property "Loaded" (its own or its parent's), a note value (text or id) and the After count.</summary>
    private static KtcLoad? PropertyLine(JsonElement e, string? prop, Func<string, string, string?>? resolve)
    {
        if (!string.Equals(prop, "Loaded", StringComparison.OrdinalIgnoreCase)) return null;
        var after = KtcJson.Dbl(e, double.NaN, AfterNames);
        var den = Denomination(e, resolve);
        if (double.IsNaN(after) || after < 0 || den == null) return null;
        return new KtcLoad(null, den, (long)after, null);
    }

    /// <summary>The note value of "AED 100", 100 or "100", or of a DenominationID named through <paramref name="resolve"/>.</summary>
    private static decimal? Denomination(JsonElement e, Func<string, string, string?>? resolve)
    {
        string? text = null;
        if (KtcJson.Str(e, DenomIds) is { Length: > 0 } did && resolve?.Invoke("denominationid", did) is { Length: > 0 } named) text = named;
        text ??= KtcJson.Str(e, DenomNames);
        var m = Regex.Match(text ?? "", @"(\d+(?:\.\d+)?)\s*$");
        return m.Success && decimal.TryParse(m.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;
    }

    private static KtcLoad? Line(JsonElement e)
    {
        var unit = KtcJson.Str(e, UnitNames); var val = KtcJson.Dbl(e, double.NaN, ValueNames); var cnt = KtcJson.Dbl(e, double.NaN, CountNames);
        if (double.IsNaN(cnt) || cnt < 0 || (unit.Length == 0 && (double.IsNaN(val) || val <= 0))) return null;
        return new KtcLoad(unit.Length > 0 ? KtcJson.NormId(unit) : null, double.IsNaN(val) || val <= 0 ? null : (decimal)val, (long)cnt, null);
    }
}
