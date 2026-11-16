using System.Globalization;
using System.Text.RegularExpressions;
using Ktc.Application.Models;

namespace Ktc.Application.Logic;

/// <summary>
/// Reads the cash counter report an ATM prints to its electronic journal (EJ) when it restarts:
/// <code>
/// MACHINE NO =ADB07076
/// DATE-TIME =11/10/26 10:21:43
///              TYPE1-1000 TYPE2-500
///  CASSETTE      00007      01048
/// +REJECTED      00002      00003
/// +DISPENSED     02491      01449
/// -DEPOSITED     00000      00000
/// =TOTAL         02500      02500
///              TYPE3-50   TYPE4-100
///  ...
///  LAST CLEARED  11/06/26 11:42
/// </code>
/// The block may come as one EJ row with line breaks or as one row per line; KTC's EJ markup (&lt;EJData&gt;, "_[020t hh:mm:ss") is ignored.
/// A block starts at MACHINE NO (or DATE-TIME / a TYPEn-value header when MACHINE NO is missing) and ends at LAST CLEARED, the next
/// block, or 40 lines without counter rows.
/// </summary>
public static class EjCounterRules
{
    private static readonly RegexOptions Ci = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly Regex Markup = new(@"</?EJData>|_\[\d{3}t(\s+\d{1,2}:\d{2}(:\d{2})?)?|\x1b\[[0-9;]*[A-Za-z]|[\x00-\x08\x0b\x0c\x0e-\x1f]", Ci);
    private static readonly Regex Machine = new(@"^\s*MACHINE\s*(NO|NUMBER|ID)\.?\s*[=:]?\s*(?<v>\S+)", Ci);
    private static readonly Regex DateTimeLine = new(@"^\s*DATE\s*[-/ ]?\s*TIME\s*[=:]?\s*(?<v>\d{1,2}[/.-]\d{1,2}[/.-]\d{2,4}\s+\d{1,2}:\d{2}(:\d{2})?)", Ci);
    private static readonly Regex Cleared = new(@"^\s*LAST\s+CLEAR(ED)?\s*[=:]?\s*(?<v>\d{1,2}[/.-]\d{1,2}[/.-]\d{2,4}(\s+\d{1,2}:\d{2}(:\d{2})?)?)", Ci);
    private static readonly Regex TypeHead = new(@"TYPE\s*(?<n>\d+)\s*-\s*(?<d>\d+(\.\d+)?)", Ci);
    private static readonly Regex Row = new(@"^\s*(?<sign>[+\-=])?\s*(?<name>[A-Z][A-Z .]*?[A-Z])\s*[=:]?\s+(?<nums>-?\d+(\s+-?\d+)*)\s*$", Ci);

    /// <summary>The counter rows of the report, in print order: sign and the name shown.</summary>
    public static readonly (string Key, string Label)[] Rows =
    {
        ("CASSETTE", "Cassette"), ("REJECTED", "Rejected"), ("DISPENSED", "Dispensed"), ("DEPOSITED", "Deposited"), ("TOTAL", "Total"),
    };

    /// <summary>
    /// Every counter report in the EJ rows, newest first. <paramref name="dayFirst"/> reads DATE-TIME as dd/MM/yy; otherwise MM/dd/yy is
    /// tried first and a date in the future (by more than a day) falls back to dd/MM/yy.
    /// </summary>
    public static List<EjCounterReport> Parse(IEnumerable<(string Seq, DateTime TimeUtc, string Text)> rows, DateTime nowUtc, bool dayFirst = false)
    {
        // One list of lines, each with the time and sequence of the EJ row it came from.
        var lines = new List<(string Text, DateTime Time, string Seq)>();
        foreach (var r in rows.OrderBy(r => r.TimeUtc).ThenBy(r => r.Seq.Length).ThenBy(r => r.Seq, StringComparer.Ordinal))
            foreach (var l in Markup.Replace(r.Text.Replace("\\n", "\n").Replace("\r", ""), " ").Split('\n'))
                if (l.Trim().Length > 0) lines.Add((l.TrimEnd(), r.TimeUtc, r.Seq));

        var res = new List<EjCounterReport>();
        Builder? b = null; int idle = 0;
        void Close() { if (b != null && b.Types.Count > 0 && b.Types.Any(t => t.Values.Count > 0)) res.Add(b.Build(nowUtc, dayFirst)); b = null; }
        foreach (var (text, time, seq) in lines)
        {
            Match m;
            if ((m = Machine.Match(text)).Success) { Close(); b = new Builder(time, seq) { MachineNo = m.Groups["v"].Value }; idle = 0; continue; }
            if ((m = DateTimeLine.Match(text)).Success)
            {
                if (b == null || b.DateText != null && b.Types.Count > 0) { Close(); b = new Builder(time, seq); }
                b.DateText = m.Groups["v"].Value; idle = 0; continue;
            }
            var heads = TypeHead.Matches(text);
            if (heads.Count > 0 && Regex.Replace(TypeHead.Replace(text, ""), @"[^A-Za-z0-9]", "").Length == 0)
            {
                b ??= new Builder(time, seq);
                b.Columns = heads.Select(h => b.Type(h.Groups["n"].Value, h.Groups["d"].Value)).ToList();
                idle = 0; continue;
            }
            if (b == null) continue;
            if ((m = Cleared.Match(text)).Success) { b.ClearedText = m.Groups["v"].Value; Close(); continue; }
            if ((m = Row.Match(text)).Success && b.Columns.Count > 0)
            {
                var name = Regex.Replace(m.Groups["name"].Value.ToUpperInvariant(), @"[\s.]+", " ").Trim();
                var nums = m.Groups["nums"].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < nums.Length && i < b.Columns.Count; i++)
                    if (long.TryParse(nums[i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v)) b.Columns[i].Values[name] = v;
                idle = 0; continue;
            }
            if (++idle > 40) Close();
        }
        Close();
        return res.OrderByDescending(r => r.ReportTime ?? r.EjTimeUtc).ThenByDescending(r => r.EjTimeUtc).ToList();
    }

    /// <summary>Reads an EJ date ("11/10/26 10:21:43", "11/06/26 11:42"): month first, unless that is in the future or dayFirst is set.</summary>
    public static DateTime? ParseDate(string? s, DateTime nowUtc, bool dayFirst)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = Regex.Replace(s.Trim(), @"[.-]", "/");
        string[] md = { "MM/dd/yy HH:mm:ss", "M/d/yy H:mm:ss", "MM/dd/yy HH:mm", "M/d/yy H:mm", "MM/dd/yyyy HH:mm:ss", "M/d/yyyy H:mm:ss", "MM/dd/yyyy HH:mm", "M/d/yyyy H:mm", "MM/dd/yy", "M/d/yy", "MM/dd/yyyy", "M/d/yyyy" };
        var dm = md.Select(f => f.Replace("MM/dd", "dd/MM").Replace("M/d", "d/M")).ToArray();
        DateTime? Try(string[] f) => DateTime.TryParseExact(t, f, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        var first = Try(dayFirst ? dm : md); var second = Try(dayFirst ? md : dm);
        if (first is { } a && a <= nowUtc.AddDays(1)) return a;
        if (second is { } c && c <= nowUtc.AddDays(1)) return c;
        return first ?? second;
    }

    private sealed class TypeCol(string no, string denom)
    {
        public string No { get; } = no; public string Denomination { get; } = denom;
        public Dictionary<string, long> Values { get; } = new();
    }

    private sealed class Builder(DateTime time, string seq)
    {
        public DateTime Time { get; } = time; public string Seq { get; } = seq;
        public string? MachineNo; public string? DateText; public string? ClearedText;
        public List<TypeCol> Types { get; } = new();
        public List<TypeCol> Columns { get; set; } = new();
        public TypeCol Type(string no, string denom)
        {
            var t = Types.FirstOrDefault(x => x.No == no && x.Denomination == denom);
            if (t == null) { t = new TypeCol(no, denom); Types.Add(t); }
            return t;
        }
        public EjCounterReport Build(DateTime nowUtc, bool dayFirst)
        {
            var types = Types.Where(t => t.Values.Count > 0).Select(t =>
            {
                long? V(string k) => t.Values.TryGetValue(k, out var v) ? v : null;
                var extra = t.Values.Where(kv => !Rows.Any(r => r.Key == kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
                decimal? denom = decimal.TryParse(t.Denomination, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;
                return new EjCassetteCounter("TYPE" + t.No, denom, V("CASSETTE"), V("REJECTED"), V("DISPENSED"), V("DEPOSITED"), V("TOTAL"), extra);
            }).ToList();
            return new EjCounterReport(Time, Seq, MachineNo, ParseDate(DateText, nowUtc, dayFirst), DateText, ParseDate(ClearedText, nowUtc, dayFirst), ClearedText, types);
        }
    }
}
