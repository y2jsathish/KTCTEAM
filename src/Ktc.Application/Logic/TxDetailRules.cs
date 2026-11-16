using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ktc.Application.Models;

namespace Ktc.Application.Logic;

/// <summary>
/// Reads the KTC answers behind the transaction details popup: STX field lists (FieldsFromTransaction, FieldsFromSession),
/// session / transaction records and EJ lines (EJDataForClientBetweenIDs). Field names are not documented, so records are
/// read by content. Every value that leaves here is PAN-masked.
/// </summary>
public static class TxDetailRules
{
    private const RegexOptions Ci = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly string[] FieldIdNames = { "STXFieldID", "STXFieldId", "FieldID", "FieldId", "STXField" };
    private static readonly string[] ValueNames = { "Value", "StringValue", "FieldValue", "LookupValue", "IntValue", "Data", "Text" };
    private static readonly string[] TypeNames = { "Type", "DataType", "ValueType", "FieldType", "DatatypeName" };

    /// <summary>
    /// STX fields of a transaction or session (Dictionary&lt;short, KTCTransactionField&gt; keyed by field id, or a list). The field id is
    /// named through <paramref name="fieldName"/> (GET STXField); a LookupString value is an STXFieldLookup id, named through <paramref name="lookup"/>.
    /// </summary>
    public static List<DetailField> ReadFields(JsonElement? json, Func<string, string?> fieldName, Func<string, string?> lookup)
    {
        var res = new List<DetailField>();
        foreach (var (key, e) in KtcJson.Rows(json))
        {
            string fid, value, type = "";
            if (e.ValueKind == JsonValueKind.Object)
            {
                fid = KtcJson.NormId(KtcJson.Str(e, FieldIdNames) is { Length: > 0 } f ? f : key);
                value = KtcJson.Str(e, ValueNames);
                type = KtcJson.Str(e, TypeNames);
                if (value.Length == 0)
                    value = string.Join(" ", SchemaInferer.Leaves(e).Where(l => !Regex.IsMatch(l.Path, @"id$|^id|time|date|type", Ci) && l.Value.Length > 0).Select(l => l.Value));
            }
            else { fid = KtcJson.NormId(key); value = e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.ToString(); }
            if (fid.Length == 0 && value.Length == 0) continue;
            var shown = value;
            var isLookup = type.Contains("lookup", StringComparison.OrdinalIgnoreCase);
            if (isLookup && lookup(KtcJson.NormId(value)) is { Length: > 0 } ln) shown = ln;
            var name = fieldName(fid) is { Length: > 0 } n ? n : fid.Length > 0 ? "Field " + fid : "Field";
            res.Add(new DetailField(name, Mask(shown), fid.Length > 0 ? fid : null));
        }
        return res.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>All values of one KTC record (session, transaction) as name / value pairs, ids named through <paramref name="names"/> when it knows them.</summary>
    public static List<DetailField> Flatten(JsonElement? e, Func<string, string, string?>? names = null)
    {
        var res = new List<DetailField>();
        if (e is not { } r) return res;
        if (r.ValueKind != JsonValueKind.Object && r.ValueKind != JsonValueKind.Array)
        {
            // A KTC ToString such as "53471;03/11/2026 6:53:13 AM;80;19".
            var s = r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : r.ToString();
            if (s.Length > 0) res.Add(new DetailField("Value", PanMasker.Mask(s)));
            return res;
        }
        foreach (var (path, value) in SchemaInferer.Leaves(r))
        {
            if (value.Length == 0) continue;
            var named = names?.Invoke(path, value);
            res.Add(new DetailField(Pretty(path), Mask(named is { Length: > 0 } ? $"{named} ({value})" : value), Mask(value)));
        }
        return res;
    }

    // UUIDs are not card numbers: masking them would hide the id KTC uses.
    private static string Mask(string v) => Guid.TryParse(v, out _) ? v : PanMasker.Mask(v);

    /// <summary>"timestampUTC" -> "Timestamp UTC"; "completionID" -> "Completion ID"; "a.b" keeps its last part.</summary>
    public static string Pretty(string path)
    {
        var last = path.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? path;
        var w = Regex.Replace(last, @"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ").Replace('_', ' ').Trim();
        return w.Length == 0 ? path : char.ToUpperInvariant(w[0]) + w[1..];
    }

    /// <summary>EJ lines of EJDataForClientBetweenIDs (List&lt;string&gt; of "&lt;EJData&gt;…&lt;/EJData&gt;", or EJ row records), PAN-masked.</summary>
    public static List<string> EjLines(JsonElement? json)
    {
        var res = new List<string>();
        foreach (var e in KtcJson.Items(json))
        {
            var text = e.ValueKind == JsonValueKind.String ? e.GetString() ?? ""
                     : e.ValueKind == JsonValueKind.Object ? KtcJson.Str(e, "ejd", "EJData", "Text", "EJText", "Data", "Line", "Value", "RowText") : e.ToString();
            text = Regex.Replace(text, @"</?EJData>", "", Ci).Replace("\r\n", "\n").TrimEnd();
            if (text.Length > 0) res.Add(PanMasker.Mask(text));
        }
        return res;
    }

    /// <summary>KTC's transactionStartTimeLocal parameter: yyyyMMddHHmmssfff.</summary>
    public static string KtcTime(DateTime t) => t.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
}
