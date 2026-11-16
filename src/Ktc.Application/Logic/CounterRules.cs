using System.Globalization;
using System.Linq;
using System.Text.Json;
using Ktc.Application.Models;

namespace Ktc.Application.Logic;

/// <summary>
/// Reads the ATM counters payload of the configurable Ktc:CountersEndpoint and builds the counters export.
/// The expected shape is
/// <c>{ "CollectedUtc", "LastResetUtc", "Application": [{Category, Name, Today, SinceReset}], "Xfs": [{Device, ServiceClass, LogicalName, Counter, Value, Threshold}] }</c>;
/// field names are looked up tolerantly, and a flat list of counter records (application and XFS mixed) is accepted too.
/// </summary>
public static class CounterRules
{
    public const string Ok = "OK";
    public const string Warning = "Warning";

    /// <summary>Order of the application counter categories on the page and in exports.</summary>
    public static readonly string[] Categories = { "Transactions", "Exceptions", "Operations" };

    /// <summary>XFS service classes and their device names, in the order the cards are shown.</summary>
    public static readonly (string Class, string Device)[] ServiceClasses =
        { ("IDC", "Card Reader"), ("CDM", "Cash Dispenser"), ("CIM", "Cash Acceptor"), ("PTR", "Printer"), ("PIN", "EPP / PIN Pad"), ("SIU", "Sensors") };

    /// <summary>A counter is flagged when its value is at or above its threshold.</summary>
    public static string Status(long value, long? threshold) => threshold is { } t && value >= t ? Warning : Ok;

    public static (DateTime? CollectedUtc, DateTime? LastResetUtc, List<ApplicationCounter> Application, List<XfsCounter> Xfs) Parse(JsonElement? root)
    {
        var app = new List<ApplicationCounter>(); var xfs = new List<XfsCounter>();
        DateTime? collected = null, reset = null;
        if (root is not { } r) return (collected, reset, app, xfs);

        var body = r;
        if (r.ValueKind == JsonValueKind.Object && !HasLists(r) && KtcJson.TryProp(r, out var inner, "data", "result", "counters") && inner.ValueKind == JsonValueKind.Object) body = inner;

        if (body.ValueKind == JsonValueKind.Object && HasLists(body))
        {
            collected = KtcJson.Date(body, "CollectedUtc", "Collected", "ReportedUtc", "Timestamp", "TimeStamp", "LastUpdated");
            reset = KtcJson.Date(body, "LastResetUtc", "LastReset", "ResetTime", "LastResetTime");
            if (KtcJson.TryProp(body, out var a, AppListNames) && a.ValueKind == JsonValueKind.Array)
                foreach (var e in a.EnumerateArray()) if (ReadApp(e) is { } c) app.Add(c);
            if (KtcJson.TryProp(body, out var x, XfsListNames) && x.ValueKind == JsonValueKind.Array)
                foreach (var e in x.EnumerateArray()) if (ReadXfs(e) is { } c) xfs.Add(c);
        }
        else
        {
            // A flat list: records with a device / service class are XFS counters, the others application counters.
            foreach (var e in KtcJson.Items(body).Where(e => e.ValueKind == JsonValueKind.Object))
            {
                if (KtcJson.Str(e, "ServiceClass", "Class", "XfsClass", "Device", "DeviceName").Length > 0) { if (ReadXfs(e) is { } c) xfs.Add(c); }
                else if (ReadApp(e) is { } c) app.Add(c);
                collected ??= KtcJson.Date(e, "CollectedUtc", "Timestamp", "TimeStamp");
                reset ??= KtcJson.Date(e, "LastResetUtc", "LastReset", "ResetTime");
            }
        }
        return (collected, reset, app, xfs);
    }

    private static readonly string[] AppListNames = { "Application", "ApplicationCounters", "AppCounters" };
    private static readonly string[] XfsListNames = { "Xfs", "XfsCounters", "DeviceCounters", "Devices" };
    private static bool HasLists(JsonElement o) => KtcJson.TryProp(o, out _, AppListNames) || KtcJson.TryProp(o, out _, XfsListNames);

    private static ApplicationCounter? ReadApp(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var name = KtcJson.Str(e, "Name", "Counter", "CounterName", "Description");
        if (name.Length == 0) return null;
        var category = KtcJson.Str(e, "Category", "Group", "Type");
        return new ApplicationCounter(category.Length > 0 ? category : "Transactions", name,
            Long(e, "Today", "TodayCount", "Daily") ?? 0, Long(e, "SinceReset", "SinceLastReset", "Total", "Value", "Count") ?? 0);
    }

    private static XfsCounter? ReadXfs(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var counter = KtcJson.Str(e, "Counter", "CounterName", "Name");
        if (counter.Length == 0) return null;
        var cls = KtcJson.Str(e, "ServiceClass", "Class", "XfsClass").ToUpperInvariant();
        var device = KtcJson.Str(e, "Device", "DeviceName");
        if (device.Length == 0) device = ServiceClasses.FirstOrDefault(s => s.Class == cls).Device ?? (cls.Length > 0 ? cls : "Device");
        var logical = KtcJson.Str(e, "LogicalName", "LogicalService", "ServiceName");
        return new XfsCounter(device, cls, logical.Length > 0 ? logical : null, counter, Long(e, "Value", "Count", "Total") ?? 0, Long(e, "Threshold", "Limit", "WarningThreshold"));
    }

    private static long? Long(JsonElement e, params string[] names)
    {
        if (!KtcJson.TryProp(e, out var v, names)) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.TryGetInt64(out var l) ? l : (long)Math.Round(v.GetDouble());
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) return (long)Math.Round(d);
        return null;
    }

    /// <summary>Rank of a category / service class for display order; unknown ones go last.</summary>
    public static int CategoryRank(string category) { var i = Array.FindIndex(Categories, c => c.Equals(category, StringComparison.OrdinalIgnoreCase)); return i < 0 ? 99 : i; }
    public static int ClassRank(string cls) { var i = Array.FindIndex(ServiceClasses, s => s.Class.Equals(cls, StringComparison.OrdinalIgnoreCase)); return i < 0 ? 99 : i; }

    /// <summary>The counters of one ATM as a report (CSV / Excel / PDF): application counters first, then XFS counters.</summary>
    public static ReportData ToReport(AtmCounters c, DateTime generatedUtc)
    {
        static string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);
        var rows = new List<IReadOnlyList<string>>();
        foreach (var a in c.Application.OrderBy(a => CategoryRank(a.Category)))
            rows.Add(new[] { "Application", a.Category, a.Name, N(a.Today), N(a.SinceReset), "", "" });
        foreach (var x in c.Xfs.OrderBy(x => ClassRank(x.ServiceClass)))
            rows.Add(new[] { "XFS", $"{x.Device} ({x.ServiceClass}{(x.LogicalName is { } l ? " / " + l : "")})", x.Counter, "", N(x.Value), x.Threshold is { } t ? N(t) : "", x.Status });
        var flagged = c.Xfs.Count(x => x.Status != Ok);
        var subtitle = string.Join(" | ", new[]
        {
            c.LastResetUtc is { } r ? $"Last reset {r:yyyy-MM-dd HH:mm} UTC" : null,
            c.CollectedUtc is { } at ? $"Reported {at:yyyy-MM-dd HH:mm} UTC" : null
        }.Where(s => s != null));
        return new ReportData($"Counters {c.AtmName}", generatedUtc, subtitle.Length > 0 ? subtitle : null,
            new[] { "Type", "Group / device", "Counter", "Today", "Since reset / value", "Threshold", "Status" }, rows,
            new[] { ("ATM", $"{c.AtmName} ({c.AtmId})"), ("Application counters", c.Application.Count.ToString()), ("XFS counters", c.Xfs.Count.ToString()), ("XFS at or over threshold", flagged.ToString()) });
    }
}
