using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    public static readonly string[] Categories = { "Transactions", "Cash", "Exceptions", "Operations" };

    /// <summary>XFS service classes and their device names, in the order the cards are shown.</summary>
    public static readonly (string Class, string Device)[] ServiceClasses =
        { ("IDC", "Card Reader"), ("CDM", "Cash Dispenser"), ("CIM", "Cash Acceptor"), ("IPM", "Cheque Scanner"), ("PTR", "Printer"), ("PIN", "EPP / PIN Pad"), ("SIU", "Sensors") };

    /// <summary>Value of an application counter that KTC cannot provide (shown as "n/a").</summary>
    public const long Unknown = -1;

    /// <summary>Keeps only the XFS counters of the given service classes (comma separated, e.g. "CDM,CIM,IPM"); empty or null keeps all.</summary>
    public static IReadOnlyList<XfsCounter> FilterClasses(IReadOnlyList<XfsCounter> xfs, string? classes)
    {
        var set = (classes ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return set.Count == 0 ? xfs : xfs.Where(x => set.Contains(x.ServiceClass)).ToList();
    }

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
                if (KtcJson.Str(e, "ServiceClass", "Class", "XfsClass").Length > 0) { if (ReadXfs(e) is { } c) xfs.Add(c); }
                else if (ReadKtcCounter(e) is { } k) { xfs.Add(k); collected ??= KtcJson.AnyDate(e); }
                else if (KtcJson.Str(e, "Device", "DeviceName").Length > 0) { if (ReadXfs(e) is { } c) xfs.Add(c); }
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

    /// <summary>
    /// One record of KTC's GET Client/{id}/Counters (List&lt;KTCCounter&gt;: client id, component DBName such as
    /// "KXDevice.KXCardReader", counter name such as "GoodReads", value, ...). The field names are not documented, so the
    /// record is read by content: the component is the string that names a KXDevice (or a field called component / device),
    /// the counter is the other name-like string, and the value is the first whole number that is not an id.
    /// </summary>
    public static XfsCounter? ReadKtcCounter(JsonElement e)
    {
        string? comp = null, compField = null;
        foreach (var p in e.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.String && (p.Value.GetString() ?? "").StartsWith("KXDevice", StringComparison.OrdinalIgnoreCase)) { comp = p.Value.GetString(); compField = p.Name; break; }
        if (comp == null)
            foreach (var p in e.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String && Regex.IsMatch(p.Name, "comp|device", Ci) && !Regex.IsMatch(p.Name, "id$", Ci) && (p.Value.GetString() ?? "").Length > 0)
                { comp = p.Value.GetString(); compField = p.Name; break; }
        if (comp == null) return null;
        string? name = null;
        foreach (var p in e.EnumerateObject().Where(p => p.Name != compField && p.Value.ValueKind == JsonValueKind.String).OrderBy(p => Regex.IsMatch(p.Name, "counter|name|key|type", Ci) ? 0 : 1))
            if ((p.Value.GetString() ?? "").Trim() is { Length: > 0 } v && !double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _) && !DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) { name = v; break; }
        if (name == null) return null;
        long? value = null;
        foreach (var p in e.EnumerateObject().Where(p => !Regex.IsMatch(p.Name, @"id$|^id|client|component|comp$", Ci)).OrderBy(p => Regex.IsMatch(p.Name, "^(value|count|total|current|counter.?value)$", Ci) ? 0 : 1))
            if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt64(out var l)) { value = l; break; }
            else if (p.Value.ValueKind == JsonValueKind.Number) { value = (long)Math.Round(p.Value.GetDouble()); break; }
            else if (p.Value.ValueKind == JsonValueKind.String && Regex.IsMatch(p.Name, "value|count|total", Ci) && long.TryParse(p.Value.GetString(), out l)) { value = l; break; }
        if (value == null) return null;
        var device = DeviceName(comp);
        return new XfsCounter(device, XfsClass(comp) ?? XfsClass(device) ?? "", comp, SplitWords(name), value.Value, null);
    }

    /// <summary>"KXDevice.KXCardReader.ContactlessICCardReader" -> "Contactless IC Card Reader"; "KXDevice.KXCashDispenser" -> "Cash Dispenser".</summary>
    public static string DeviceName(string dbName)
    {
        var last = (dbName ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        if (last.StartsWith("KX") && last.Length > 2 && char.IsUpper(last[2])) last = last[2..];
        return SplitWords(last, keepCase: true);
    }

    /// <summary>"GoodChipReads" -> "Good chip reads"; "Deposited_L4A_Recycle" -> "Deposited L4A recycle"; "RetainedND" -> "Retained ND".</summary>
    public static string SplitWords(string s, bool keepCase = false)
    {
        var w = Regex.Replace((s ?? "").Replace('_', ' '), @"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ").Trim();
        w = Regex.Replace(w, @"\s+", " ");
        if (keepCase) return w;
        var parts = w.Split(' ');
        for (int i = 1; i < parts.Length; i++) if (parts[i].Length > 1 && parts[i].Skip(1).All(char.IsLower)) parts[i] = parts[i].ToLowerInvariant();
        return string.Join(' ', parts);
    }

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

    /// <summary>Field names of a JSON answer, for logs and the "What KTC returned" note: kind, record count and the fields of the first record.</summary>
    public static string Shape(JsonElement? root)
    {
        if (root is not { } r) return "empty";
        var rows = KtcJson.Rows(r).ToList();
        var first = rows.FirstOrDefault().Value;
        var fields = first.ValueKind == JsonValueKind.Object
            ? string.Join(", ", first.EnumerateObject().Take(25).Select(p => $"{p.Name}:{p.Value.ValueKind.ToString().ToLowerInvariant()}"))
            : first.ValueKind.ToString().ToLowerInvariant();
        return $"{r.ValueKind.ToString().ToLowerInvariant()} with {rows.Count} record(s); first record: {{{fields}}}";
    }

    /// <summary>Rank of a category / service class for display order; unknown ones go last.</summary>
    public static int CategoryRank(string category) { var i = Array.FindIndex(Categories, c => c.Equals(category, StringComparison.OrdinalIgnoreCase)); return i < 0 ? 99 : i; }
    public static int ClassRank(string cls) { var i = Array.FindIndex(ServiceClasses, s => s.Class.Equals(cls, StringComparison.OrdinalIgnoreCase)); return i < 0 ? 99 : i; }

    // ------------------------------------------------------------------------------------------------------------
    // Counters built from standard KTC data (no counters route needed).
    // ------------------------------------------------------------------------------------------------------------

    private const RegexOptions Ci = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>XFS service class of a KTC component (ComponentList DisplayName or DBName such as "KXDevice.KXCardReader"); null when it is not an XFS device.</summary>
    public static string? XfsClass(string component)
    {
        var c = Regex.Replace(component ?? "", @"[\s_.\-/]", "").ToLowerInvariant();
        if (c.Length == 0) return null;
        if (c.Contains("cardreader") || c.Contains("idc") || c.Contains("chipcard") || c.Contains("contactless")) return "IDC";
        if (c.Contains("dispenser") || c.Contains("cdm") || c.Contains("recycler")) return "CDM";
        if (c.Contains("chequ") || c.Contains("check") || c.Contains("bundle") || c.Contains("ipm") || c.Contains("itemprocess")) return "IPM";
        if (c.Contains("cashacceptor") || c.Contains("cashin") || c.Contains("cim") || c.Contains("depository")) return "CIM";
        if (c.Contains("printer") || c.Contains("ptr") || c.Contains("passbook")) return "PTR";
        if (c.Contains("pinpad") || c.Contains("epp") || c.Contains("pin")) return "PIN";
        if (c.Contains("sensor") || c.Contains("door") || c.Contains("indicator") || c.Contains("siu") || c.Contains("guidelight") || c.Contains("alarm") || c.Contains("safe")) return "SIU";
        return null;
    }

    private static readonly Regex CounterName = new(@"count|counter|total|number of|\bno\.? of|jam|fail|retain|retract|reject|refus|captur|dispens|insert|printed|accepted|purge|opens?\b|opened|tamper|suspect|counterfeit|cycles?\b|operations?\b|entries|events", Ci);
    private static readonly Regex NotCounter = new(@"version|firmware|serial|model|vendor|percent|%|temperature|duration|time|date|status|state|level|severity|address|\bid\b|capabilit|type\b|mode\b|code\b", Ci);

    /// <summary>
    /// True when a CurrentStatus property is a counter: its value is a whole number and its name says so
    /// (count, jams, failures, retains, ...), or KTC marked it numeric and the name is not a version, time, status, etc.
    /// </summary>
    public static bool IsCounter(string propertyName, bool useNumeric, string value, out long number)
    {
        number = 0;
        if (!double.TryParse((value ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || d < 0 || d != Math.Floor(d) || d > long.MaxValue) return false;
        number = (long)d;
        if (CounterName.IsMatch(propertyName)) return true;
        return useNumeric && !NotCounter.IsMatch(propertyName);
    }

    /// <summary>Threshold for a counter from Ktc:CounterThresholds: the longest key that the counter name contains.</summary>
    public static long? ThresholdFor(string counter, IReadOnlyDictionary<string, long>? thresholds)
    {
        if (thresholds == null || string.IsNullOrWhiteSpace(counter)) return null;
        var hit = thresholds.Where(t => t.Key.Length > 0 && counter.Contains(t.Key, StringComparison.OrdinalIgnoreCase)).OrderByDescending(t => t.Key.Length).FirstOrDefault();
        return hit.Key == null ? null : hit.Value;
    }

    // Transaction type and reason names as KTC's STXFieldLookup spells them (CashWithdrawal, BalanceInquiry, HostDeclined, ...)
    // or as other servers do ("Cash Withdrawal"); spaces and underscores are ignored.
    private static string Flat(string s) => Regex.Replace(s ?? "", @"[\s_\-]", "").ToLowerInvariant();
    private static readonly Regex Supervisor = new(@"^(select|exit|checkpassword|replenish|prntcntrs|printcounters|clrcash|clearcash|addcash|addcashcomplete|testcash|dispcashrecycler|supervisor.*|.*supervisor|balancing|settlement|cassette.*)$", Ci);
    private static readonly Regex Reset = new(@"^(replenish|replenishment|clrcash|clearcash|addcash|addcashcomplete|settlement|balancing|cassettereplace.*)$", Ci);

    /// <summary>True for supervisor / replenishment operations recorded as transactions (REPLENISH, ADD_CASH, CLR_CASH, PRNT_CNTRS, ...).</summary>
    public static bool IsSupervisorType(string type) => Supervisor.IsMatch(Flat(type));
    /// <summary>True for operations that reset the ATM counters (replenishment, clear cash, add cash).</summary>
    public static bool IsResetType(string type) => Reset.IsMatch(Flat(type));

    /// <summary>The application counters and the list of counter names, in display order.</summary>
    public static readonly (string Category, string Name)[] ApplicationCounters =
    {
        ("Transactions", "Customer sessions"), ("Transactions", "Cash withdrawals"), ("Transactions", "Balance inquiries"), ("Transactions", "Mini statements"),
        ("Transactions", "Fund transfers"), ("Transactions", "PIN changes"), ("Transactions", "Cash deposits"),
        ("Exceptions", "Declined by host"), ("Exceptions", "Host timeouts"), ("Exceptions", "Reversals"), ("Exceptions", "Cards captured"), ("Exceptions", "Cash retracts"),
        ("Operations", "Supervisor entries"), ("Operations", "Application restarts"),
    };

    /// <summary>Which application counter(s) one customer transaction adds to (besides "Customer sessions").</summary>
    public static IEnumerable<string> CountersFor(TransactionDto t)
    {
        var type = Flat(t.Type);
        var why = Flat($"{t.Reason} {t.ErrorCode} {(t.Success ? "" : t.FailureCategory)}");
        if (Regex.IsMatch(type, "withdraw|fastcash")) yield return "Cash withdrawals";
        else if (type.Contains("balance")) yield return "Balance inquiries";
        else if (type.Contains("statement")) yield return "Mini statements";
        else if (type.Contains("transfer")) yield return "Fund transfers";
        else if (Regex.IsMatch(type, "pin(change|reset|unlock)")) yield return "PIN changes";
        else if (Regex.IsMatch(type, "deposit|cashin|paymentbycash")) yield return "Cash deposits";
        if (type.Contains("revers") || why.Contains("revers")) yield return "Reversals";
        if (why.Contains("declin")) yield return "Declined by host";
        if (Regex.IsMatch(why, "hosttimeout|noresponsefrom.*host|hostnotresponding")) yield return "Host timeouts";
        if (Regex.IsMatch(why, "captur|retain")) yield return "Cards captured";
        if (why.Contains("retract") || t.FailureCategory == "CashRetract") yield return "Cash retracts";
        if (Regex.IsMatch(why, "restart|powerfail|apprestart")) yield return "Application restarts";
    }

    /// <summary>
    /// Application counters of one ATM from its transactions: "Today" counts from <paramref name="todayStartUtc"/>,
    /// "Since reset" from the last replenishment / clear-cash operation in the list (or from the oldest transaction searched when there is none).
    /// </summary>
    public static (List<ApplicationCounter> Counters, DateTime? ResetUtc, string? ResetType) ApplicationFromTransactions(IReadOnlyCollection<TransactionDto> tx, DateTime todayStartUtc)
    {
        // The reset point is the start of the latest replenishment session (REPLENISH, ADD_CASH, ... share one sessionID),
        // so that supervisor session itself counts as the first supervisor entry since the reset.
        var last = tx.Where(t => IsResetType(t.Type)).OrderByDescending(t => t.TimeUtc).FirstOrDefault();
        var reset = last == null ? null : last.SessionId.Length == 0 ? last
            : tx.Where(t => t.SessionId == last.SessionId).OrderBy(t => t.TimeUtc).ThenBy(t => IsResetType(t.Type) ? 0 : 1).First();
        var since = reset?.TimeUtc;
        var today = new Dictionary<string, long>(); var total = new Dictionary<string, long>();
        void Add(string name, DateTime when)
        {
            if (since is { } s && when < s) return;
            total[name] = total.GetValueOrDefault(name) + 1;
            if (when >= todayStartUtc) today[name] = today.GetValueOrDefault(name) + 1;
        }
        // Sessions: one per KTC sessionID (or per transaction when KTC sends no session id); supervisor sessions count as supervisor entries.
        foreach (var g in tx.GroupBy(t => t.SessionId.Length > 0 ? "s" + t.SessionId : "t" + t.Id))
        {
            var first = g.Min(t => t.TimeUtc);
            if (g.Any(t => IsSupervisorType(t.Type))) Add("Supervisor entries", first);
            if (g.Any(t => !IsSupervisorType(t.Type))) Add("Customer sessions", first);
        }
        foreach (var t in tx.Where(t => !IsSupervisorType(t.Type)))
            foreach (var name in CountersFor(t)) Add(name, t.TimeUtc);
        var list = ApplicationCounters.Select(c => new ApplicationCounter(c.Category, c.Name, today.GetValueOrDefault(c.Name), total.GetValueOrDefault(c.Name))).ToList();
        return (list, since, last == null ? null : (reset != null && IsResetType(reset.Type) ? reset.Type : last.Type));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Notes dispensed / accepted per transaction (GET HistoricalCounter/ClientTransactionCounters?clientid=&transactionid=).
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>True for a customer transaction that moves notes (cash withdrawal or cash deposit).</summary>
    public static bool IsCashTransaction(TransactionDto t) =>
        !IsSupervisorType(t.Type) && CountersFor(t).Any(n => n is "Cash withdrawals" or "Cash deposits");

    private static readonly Regex DispensedExact = new(@"^(dispensed|dispense|notesdispensed|cashdispensed|presented|notespresented)$", Ci);
    private static readonly Regex AcceptedExact = new(@"^(deposited|notesdeposited|cashdeposited|accepted|notesaccepted|cashin|cashinnotes|stored|notesstored)$", Ci);
    private static readonly Regex NotMoved = new(@"remov|reject|retract|retain|return|remain|loaded|initial|recycl|operation|item|cheque|check|capture", Ci);

    /// <summary>
    /// Notes moved by one transaction from KTC's per-transaction counters (List&lt;HistoricalCounter&gt;). The field names are not
    /// documented, so each record is read by content: a counter name (Dispensed, Deposited, ...), an optional component and
    /// denomination, and a whole-number value. Returns null when there is no answer or the records hold no note counter
    /// the portal recognises; an empty list means the transaction moved no notes.
    /// </summary>
    public static TxNotes? ReadTransactionNotes(JsonElement? root)
    {
        if (root is not { } r) return null;
        if (r.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        var recs = KtcJson.Items(r).Where(e => e.ValueKind == JsonValueKind.Object).ToList();
        if (recs.Count == 0) return r.ValueKind == JsonValueKind.Array ? new TxNotes(0, 0) : null;
        var disp = new List<(bool Exact, string Denom, long V)>(); var acc = new List<(bool Exact, string Denom, long V)>();
        foreach (var e in recs)
        {
            var (name, value) = ReadNamedCount(e);
            if (name == null || value is not { } v || v < 0) continue;
            var f = Flat(name);
            var denom = KtcJson.Str(e, "DenominationID", "DenominationId", "DenomID", "Denomination", "CashUnitNumber", "UnitNumber", "CashUnitID");
            if (DispensedExact.IsMatch(f)) disp.Add((true, denom, v));
            else if (AcceptedExact.IsMatch(f)) acc.Add((true, denom, v));
            else if (NotMoved.IsMatch(f)) continue;
            else if (f.StartsWith("dispens") || f.StartsWith("present")) disp.Add((false, denom, v));
            else if (f.StartsWith("deposit") || f.StartsWith("accept") || f.StartsWith("cashin")) acc.Add((false, denom, v));
        }
        if (disp.Count == 0 && acc.Count == 0) return null;
        return new TxNotes(SumNotes(disp), SumNotes(acc));
    }

    // Exact counter names win over variants (Deposited_L2 / _L3 / _L4 are breakdowns of Deposited). Records per denomination are
    // summed; a record without a denomination (id 0) next to per-denomination records is taken as their total, not added to them.
    private static long SumNotes(List<(bool Exact, string Denom, long V)> l)
    {
        if (l.Count == 0) return 0;
        var use = l.Any(x => x.Exact) ? l.Where(x => x.Exact).ToList() : l;
        long zero = use.Where(x => x.Denom is "" or "0").Sum(x => x.V), per = use.Where(x => x.Denom is not ("" or "0")).Sum(x => x.V);
        return Math.Max(zero, per);
    }

    private static (string? Name, long? Value) ReadNamedCount(JsonElement e)
    {
        string? name = null;
        foreach (var p in e.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String && !Regex.IsMatch(p.Name, "comp|device|currency|uuid|time|date", Ci))
                     .OrderBy(p => Regex.IsMatch(p.Name, "counter|name|type|key", Ci) ? 0 : 1))
            if ((p.Value.GetString() ?? "").Trim() is { Length: > 0 } v && !v.StartsWith("KXDevice", StringComparison.OrdinalIgnoreCase)
                && !double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _) && !DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) { name = v; break; }
        var value = Long(e, "Value", "Count", "CounterValue", "NoteCount", "Notes", "Total", "Current");
        if (value == null)
            foreach (var p in e.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Number && !Regex.IsMatch(p.Name, @"id$|^id|client|component|comp$|denom|currency|unit|delta", Ci)))
            { value = p.Value.TryGetInt64(out var l) ? l : (long)Math.Round(p.Value.GetDouble()); break; }
        return (name, value);
    }

    /// <summary>
    /// "Notes dispensed" and "Notes accepted" (today and since the reset) and the notes per transaction type, from the notes of each
    /// cash transaction since the reset. When KTC's values grow from one transaction to the next (running totals rather than the
    /// transaction's own notes), the difference to the previous transaction is used.
    /// </summary>
    public static (List<ApplicationCounter> Counters, List<NotesByType> ByType, bool RunningTotals) NotesFromTransactions(
        IReadOnlyList<(TransactionDto Tx, TxNotes Notes)> rows, DateTime todayStartUtc)
    {
        var list = rows.OrderBy(r => r.Tx.TimeUtc).ToList();
        var d = Own(list.Select(r => r.Notes.Dispensed).ToList(), 100, out var runD);
        var a = Own(list.Select(r => r.Notes.Accepted).ToList(), 300, out var runA);
        long dt = 0, at = 0, ds = 0, as_ = 0;
        var byType = new Dictionary<string, (long N, long D, long A)>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < list.Count; i++)
        {
            ds += d[i]; as_ += a[i];
            if (list[i].Tx.TimeUtc >= todayStartUtc) { dt += d[i]; at += a[i]; }
            var type = list[i].Tx.Type.Length > 0 ? list[i].Tx.Type : "Unknown";
            var c = byType.GetValueOrDefault(type); byType[type] = (c.N + 1, c.D + d[i], c.A + a[i]);
        }
        var counters = new List<ApplicationCounter> { new("Cash", "Notes dispensed", dt, ds), new("Cash", "Notes accepted", at, as_) };
        var types = byType.Select(kv => new NotesByType(kv.Key, kv.Value.N, kv.Value.D, kv.Value.A))
                          .OrderByDescending(x => x.NotesDispensed + x.NotesAccepted).ThenBy(x => x.Type).ToList();
        return (counters, types, runD || runA);
    }

    // The transaction's own notes: the values as they are, or, when they never go down and are larger than one transaction can move,
    // the difference to the previous transaction (the first one then counts 0).
    private static List<long> Own(List<long> v, long maxPerTx, out bool running)
    {
        var nz = v.Where(x => x > 0).OrderBy(x => x).ToList();
        running = nz.Count >= 3 && nz[nz.Count / 2] > maxPerTx && v.Zip(v.Skip(1)).All(p => p.Second >= p.First || p.Second == 0);
        if (!running) return v;
        var res = new List<long>(v.Count); long prev = -1;
        foreach (var x in v)
        {
            if (x == 0) { res.Add(0); continue; }
            res.Add(prev < 0 ? 0 : Math.Max(0, x - prev)); prev = x;
        }
        return res;
    }

    /// <summary>The counters of one ATM as a report (CSV / Excel / PDF): application counters first, then XFS counters.</summary>
    public static ReportData ToReport(AtmCounters c, DateTime generatedUtc)
    {
        static string N(long v) => v < 0 ? "n/a" : v.ToString("N0", CultureInfo.InvariantCulture);
        var rows = new List<IReadOnlyList<string>>();
        foreach (var a in c.Application.OrderBy(a => CategoryRank(a.Category)))
            rows.Add(new[] { "Application", a.Category, a.Name, N(a.Today), N(a.SinceReset), "", "" });
        foreach (var t in c.NotesByType ?? Array.Empty<NotesByType>())
            rows.Add(new[] { "Notes by type", t.Type, $"{N(t.Transactions)} transactions", "", $"{N(t.NotesDispensed)} dispensed / {N(t.NotesAccepted)} accepted", "", "" });
        foreach (var x in c.Xfs.OrderBy(x => ClassRank(x.ServiceClass)))
            rows.Add(new[] { "XFS", $"{x.Device} ({x.ServiceClass}{(x.LogicalName is { } l ? " / " + l : "")})", x.Counter, "", N(x.Value), x.Threshold is { } t ? N(t) : "", x.Status });
        var flagged = c.Xfs.Count(x => x.Status != Ok);
        var subtitle = string.Join(" | ", new[]
        {
            c.ResetSource is { Length: > 0 } rs ? $"Since reset = since {rs}" : c.LastResetUtc is { } r ? $"Last reset {r:yyyy-MM-dd HH:mm} UTC" : null,
            c.CollectedUtc is { } at ? $"Reported {at:yyyy-MM-dd HH:mm} UTC" : null
        }.Where(s => s != null));
        return new ReportData($"Counters {c.AtmName}", generatedUtc, subtitle.Length > 0 ? subtitle : null,
            new[] { "Type", "Group / device", "Counter", "Today", "Since reset / value", "Threshold", "Status" }, rows,
            new[] { ("ATM", $"{c.AtmName} ({c.AtmId})"), ("Application counters", c.Application.Count.ToString()), ("XFS counters", c.Xfs.Count.ToString()), ("XFS at or over threshold", flagged.ToString()) });
    }
}
