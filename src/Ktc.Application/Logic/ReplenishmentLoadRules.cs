using System.Text.Json;
using Ktc.Application.Models;

namespace Ktc.Application.Logic;

/// <summary>
/// Reads the notes loaded per note value out of KTC's own replenishment records (POST Client/{id}/ReplenishmentsByTimeRange, GET Client/{id}/ReplenishmentOrders).
/// KTC does not document these records. The KTC client's Replenishments tab shows them as one row per replenishment (Id, Component, Timestamp,
/// Transaction) with child rows Property (Loaded, Removed, Remaining, Dispensed, Rejected, ...) x Denomination ("AED 100") with Before and After;
/// the Loaded After value is the notes loaded. The field names behind that are not in any trace we have, so they are tried leniently, and a flat
/// cassette line (unit / note value / count) is accepted too. The newest record with a positive load wins; a record that sets Loaded to 0 is a
/// cash removal and is skipped. When nothing matches, the result is empty and the forecast falls back to the counters.
/// </summary>
public static class ReplenishmentLoadRules
{
    private static readonly string[] TimeNames = { "ReplenishmentTime", "ReplenishedTime", "ReplenishmentDate", "Timestamp", "TimeStamp", "Time", "EndTime", "CompletedTime", "TimeCompleted", "Date", "DateTime", "CreatedTime", "TimeCreated", "StartTime" };
    private static readonly string[] UnitNames = { "CashUnitNumber", "CassetteNumber", "UnitNumber", "CassetteNo", "CassetteID", "Cassette", "Unit", "Number" };
    private static readonly string[] ValueNames = { "Denomination", "CurrencyValue", "NoteValue", "Value" };
    private static readonly string[] PropertyNames = { "PropertyName", "Property", "CounterName", "Name", "Counter", "Type" };
    private static readonly string[] DenomNames = { "DenominationName", "Denomination", "DenominationText", "CurrencyValue", "NoteValue", "Value" };
    private static readonly string[] AfterNames = { "After", "AfterValue", "ValueAfter", "NewValue", "AfterCount" };
    private static readonly string[] CountNames = { "LoadedCount", "NotesLoaded", "LoadedNotes", "CountLoaded", "CountAfter", "AfterCount", "NewCount", "NoteCount", "NotesCount", "Count", "Notes", "Quantity", "Loaded" };

    /// <summary>Loads of the newest record that holds cassette lines in <paramref name="routes"/> (the JSON of each replenishment route). Empty when none does.</summary>
    public static IReadOnlyList<KtcLoad> Parse(IEnumerable<JsonElement?> routes)
    {
        var best = new List<KtcLoad>(); DateTime? bestTime = null; var found = false;
        foreach (var root in routes)
            foreach (var rec in KtcJson.Items(root).Where(e => e.ValueKind == JsonValueKind.Object))
            {
                var lines = Lines(rec);
                if (lines.Count == 0) continue;
                var t = KtcJson.Date(rec, TimeNames) ?? KtcJson.AnyDate(rec);
                if (!found || (t.HasValue && (!bestTime.HasValue || t > bestTime))) { best = lines.Select(l => l with { TimeUtc = t }).ToList(); bestTime = t; found = true; }
            }
        return best;
    }

    private static List<KtcLoad> Lines(JsonElement rec)
    {
        var res = new List<KtcLoad>();
        var cleared = false;
        foreach (var p in rec.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Array))
            foreach (var e in p.Value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object))
            {
                if (PropertyLine(e) is { } pl) { if (pl.Notes > 0) res.Add(pl); else cleared = true; }
                else if (Line(e) is { } l) res.Add(l);
            }
        if (cleared && res.Count == 0) return res;   // Loaded set to 0: the cash was removed, not loaded
        if (res.Count == 0 && Line(rec) is { } one) res.Add(one);
        return res.GroupBy(l => (l.Unit, l.Denomination)).Select(g => g.Last()).ToList();
    }

    /// <summary>One child of the Replenishments tab shape: Property "Loaded", a note value, and the After count.</summary>
    private static KtcLoad? PropertyLine(JsonElement e)
    {
        if (!string.Equals(KtcJson.Str(e, PropertyNames).Trim(), "Loaded", StringComparison.OrdinalIgnoreCase)) return null;
        var after = KtcJson.Dbl(e, double.NaN, AfterNames); var den = Denomination(e);
        if (double.IsNaN(after) || after < 0 || den == null) return null;
        return new KtcLoad(null, den, (long)after, null);
    }

    /// <summary>The note value of "AED 100", 100 or "100".</summary>
    private static decimal? Denomination(JsonElement e)
    {
        var raw = KtcJson.Str(e, DenomNames);
        var m = System.Text.RegularExpressions.Regex.Match(raw, @"(\d+(?:\.\d+)?)\s*$");
        return m.Success && decimal.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;
    }

    private static KtcLoad? Line(JsonElement e)
    {
        var unit = KtcJson.Str(e, UnitNames); var val = KtcJson.Dbl(e, double.NaN, ValueNames); var cnt = KtcJson.Dbl(e, double.NaN, CountNames);
        if (double.IsNaN(cnt) || cnt < 0 || (unit.Length == 0 && (double.IsNaN(val) || val <= 0))) return null;
        return new KtcLoad(unit.Length > 0 ? KtcJson.NormId(unit) : null, double.IsNaN(val) || val <= 0 ? null : (decimal)val, (long)cnt, null);
    }
}
