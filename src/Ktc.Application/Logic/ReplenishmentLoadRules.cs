using System.Text.Json;
using Ktc.Application.Models;

namespace Ktc.Application.Logic;

/// <summary>
/// Reads the notes loaded per cassette out of KTC's own replenishment records (POST Client/{id}/ReplenishmentsByTimeRange, GET Client/{id}/ReplenishmentOrders).
/// KTC does not document these records, so the field names are tried leniently: a record is either one cassette line (unit / note value / count)
/// or holds an array of them; the newest record wins. When nothing matches, the result is empty and the forecast falls back to the counters.
/// </summary>
public static class ReplenishmentLoadRules
{
    private static readonly string[] TimeNames = { "ReplenishmentTime", "ReplenishedTime", "ReplenishmentDate", "Timestamp", "TimeStamp", "Time", "EndTime", "CompletedTime", "TimeCompleted", "Date", "DateTime", "CreatedTime", "TimeCreated", "StartTime" };
    private static readonly string[] UnitNames = { "CashUnitNumber", "CassetteNumber", "UnitNumber", "CassetteNo", "CassetteID", "Cassette", "Unit", "Number" };
    private static readonly string[] ValueNames = { "Denomination", "CurrencyValue", "NoteValue", "Value" };
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
        var one = Line(rec);
        if (one != null) res.Add(one);
        foreach (var p in rec.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Array))
            foreach (var e in p.Value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object))
                if (Line(e) is { } l) res.Add(l);
        return res.GroupBy(l => (l.Unit, l.Denomination)).Select(g => g.Last()).ToList();
    }

    private static KtcLoad? Line(JsonElement e)
    {
        var unit = KtcJson.Str(e, UnitNames); var val = KtcJson.Dbl(e, double.NaN, ValueNames); var cnt = KtcJson.Dbl(e, double.NaN, CountNames);
        if (double.IsNaN(cnt) || cnt < 0 || (unit.Length == 0 && (double.IsNaN(val) || val <= 0))) return null;
        return new KtcLoad(unit.Length > 0 ? KtcJson.NormId(unit) : null, double.IsNaN(val) || val <= 0 ? null : (decimal)val, (long)cnt, null);
    }
}
