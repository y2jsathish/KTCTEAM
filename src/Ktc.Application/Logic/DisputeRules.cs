using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Ktc.Application.Models;

namespace Ktc.Application.Logic;

/// <summary>
/// Reads KTC dispute records (api/v1/DisputeManagement) for the Disputes menu. KTC's collection has no response samples, so field
/// names are read tolerantly and every raw field is kept for the popup. Card data is masked everywhere: card-number fields show at
/// most first 6 / last 4 digits, track data, CVV and PIN fields are hidden, and any card number inside free text is masked. Pure.
/// </summary>
public static partial class DisputeRules
{
    // Names of fields that carry a card number (masked to 6+4) and fields that must never be shown (hidden).
    [GeneratedRegex(@"^(pan|maskedpan|cardpan|.*card.?(number|num|no)|.*primary.?account.?(number|num)?|accountnumber)$", RegexOptions.IgnoreCase)] private static partial Regex CardFieldRx();
    [GeneratedRegex(@"(track|cvv|cvc|cvn|cav|pin.?block|^pin$|password|secret|^token$|icc.?data|emv.?data|arqc)", RegexOptions.IgnoreCase)] private static partial Regex HiddenFieldRx();

    public static readonly string[] IdF = { "ID", "Id", "DisputeID", "DisputeId" };
    private static readonly string[] StateIdF = { "StateID", "StateId", "DisputeStateID", "DisputeStateId", "StatusID", "StatusId", "CurrentStateID", "State", "Status" };
    private static readonly string[] StateNameF = { "StateName", "StatusName", "DisputeStateName", "StateDescription", "StatusText" };
    private static readonly string[] CreatedF = { "TimeCreated", "CreatedTime", "DateCreated", "CreationDate", "TimeAdded", "Created", "DisputeDate", "DateRaised", "TimeRaised" };
    private static readonly string[] UpdatedF = { "TimeUpdated", "LastUpdated", "LastUpdate", "LastChangeDate", "LastChangeTime", "Updated", "TimeModified", "DateModified" };
    private static readonly string[] ClosedF = { "TimeClosed", "ClosedTime", "ClosedDate", "DateClosed", "Closed" };
    private static readonly string[] TxTimeF = { "TransactionTime", "TransactionDate", "TransactionDateTime", "TimeOfTransaction", "TxnTime", "TxnDate", "Timestamp" };
    private static readonly string[] AmountF = { "DisputedAmount", "DisputeAmount", "ClaimAmount", "Amount", "TransactionAmount", "TxnAmount", "RequestedAmount" };
    private static readonly string[] CardF = { "MaskedPAN", "PAN", "Pan", "CardNumber", "CardNo", "PrimaryAccountNumber", "CardPAN" };
    private static readonly string[] TxIdF = { "TransactionID", "TransactionId", "TransactionUUID", "TransactionUuid", "TxnID", "TransactionDataID" };
    private static readonly string[] NestedTxF = { "Transaction", "TransactionData", "TransactionFields" };
    public static readonly string[] CustomerF = { "CustomerDetails", "CustomerInfo", "CustomerInfoFields", "CustomerData", "Customer" };
    public static readonly string[] ExtraDataF = { "ExtraData", "ExtraDataFields", "ExtraDataString" };
    public static readonly string[] SessionF = { "SessionFields", "SessionData", "Session" };
    public static readonly string[] TransactionF = { "TransactionFields", "TransactionData", "Transaction" };
    public static readonly string[] HistoryF = { "History", "StateHistory", "DisputeHistory", "StateChanges", "Audit", "AuditTrail" };

    // ---------------- states ----------------

    /// <summary>KTC's GET DisputeState answer (array, wrapped list or id-keyed map) as states, ordered by id.</summary>
    public static List<DisputeStateDto> ParseStates(JsonElement? root)
    {
        var list = new List<DisputeStateDto>();
        foreach (var (k, e) in KtcJson.Rows(root))
        {
            if (e.ValueKind == JsonValueKind.String && int.TryParse(k, out var kid)) { list.Add(new(kid, CommandTypeRules.Sanitize(e.GetString()), null)); continue; }
            if (e.ValueKind != JsonValueKind.Object) continue;
            var id = KtcJson.Int(e, int.MinValue, "ID", "Id", "DisputeStateID", "DisputeStateId", "StateID", "StateId", "StatusID");
            if (id == int.MinValue && !int.TryParse(k, out id)) continue;
            var name = CommandTypeRules.Sanitize(KtcJson.Str(e, "Name", "StateName", "DisputeStateName", "DisplayName", "Description", "Text", "Value"));
            if (name.Length == 0) name = CommandTypeRules.Sanitize(FirstText(e));
            bool? fin = KtcJson.TryProp(e, out _, "IsFinal", "Final", "IsClosed", "Closed", "IsEndState", "EndState", "IsTerminal", "ClosesDispute")
                ? KtcJson.Bool(e, false, "IsFinal", "Final", "IsClosed", "Closed", "IsEndState", "EndState", "IsTerminal", "ClosesDispute") : null;
            list.Add(new(id, name, fin));
        }
        return list.GroupBy(s => s.Id).Select(g => g.First()).OrderBy(s => s.Id).ToList();
    }

    /// <summary>KTC's GET DisputeRule answer (Dictionary&lt;short, DisputeRules&gt;, an array or a wrapped list) as rule id -> name.</summary>
    public static Dictionary<int, string> ParseRules(JsonElement? root)
    {
        var map = new Dictionary<int, string>();
        foreach (var (k, e) in KtcJson.Rows(root))
        {
            if (e.ValueKind == JsonValueKind.String && int.TryParse(k, out var kid)) { map.TryAdd(kid, CommandTypeRules.Sanitize(e.GetString())); continue; }
            if (e.ValueKind != JsonValueKind.Object) continue;
            var id = KtcJson.Int(e, int.MinValue, "ID", "Id", "DisputeRuleID", "DisputeRuleId", "RuleID", "RuleId");
            if (id == int.MinValue && !int.TryParse(k, out id)) continue;
            var rn = KtcJson.Str(e, "Name", "RuleName", "DisputeRuleName", "Description", "Code");
            map.TryAdd(id, CommandTypeRules.Sanitize(rn.Length > 0 ? rn : FirstText(e)));
        }
        return map;
    }

    /// <summary>The first non-numeric text property (a name, when KTC's name field is called something else).</summary>
    private static string FirstText(JsonElement e) =>
        e.EnumerateObject().Select(p => p.Value).FirstOrDefault(v => v.ValueKind == JsonValueKind.String && (v.GetString() ?? "").Trim().Length > 0 && !long.TryParse(v.GetString(), out _) && AnyDateValue(v) == null) is { ValueKind: JsonValueKind.String } t ? t.GetString()! : "";

    /// <summary>The state name KTC gives for a number; then the configured name; else "State 7" (we do not guess names).</summary>
    public static string StateName(int? id, IReadOnlyList<DisputeStateDto> states, IReadOnlyDictionary<string, string>? names = null)
    {
        if (id == null) return "";
        var s = states.FirstOrDefault(x => x.Id == id);
        if (s != null && s.Name.Length > 0) return s.Name;
        if (names != null && names.TryGetValue(id.Value.ToString(), out var n) && n.Trim().Length > 0) return n.Trim();
        return $"State {id}";
    }

    // ---------------- records ----------------

    /// <summary>One KTC dispute record as a list row. atm(clientId) gives the ATM name and bank for a KTC client id.</summary>
    public static DisputeDto? Map(JsonElement e, IReadOnlyList<DisputeStateDto> states, IReadOnlyDictionary<string, string>? names, Func<string, (string Name, string Client)?> atm,
        IReadOnlyDictionary<int, string>? rules = null)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var id = KtcJson.Str(e, IdF);
        if (id.Length == 0) return null;
        var tx = Nested(e, NestedTxF);
        string S(params string[] f) { var v = KtcJson.Str(e, f); return v.Length == 0 && tx is { } t ? KtcJson.Str(t, f) : v; }
        DateTime? D(params string[] f) => KtcJson.Date(e, f) ?? (tx is { } t ? KtcJson.Date(t, f) : null);

        int? stateId = null;
        if (KtcJson.TryProp(e, out var sv, StateIdF))
        {
            if (sv.ValueKind == JsonValueKind.Number && sv.TryGetInt32(out var si)) stateId = si;
            else if (sv.ValueKind == JsonValueKind.String && int.TryParse(sv.GetString(), out var ss)) stateId = ss;
            else if (sv.ValueKind == JsonValueKind.Object) { var x = KtcJson.Int(sv, int.MinValue, "ID", "Id", "StateID"); if (x != int.MinValue) stateId = x; }
        }
        var stateText = CommandTypeRules.Sanitize(KtcJson.Str(e, StateNameF));
        if (stateText.Length == 0 && KtcJson.TryProp(e, out var st, "State", "Status") && st.ValueKind == JsonValueKind.String && !int.TryParse(st.GetString(), out _)) stateText = st.GetString() ?? "";
        if (stateText.Length == 0 && KtcJson.TryProp(e, out var so, "State", "DisputeState") && so.ValueKind == JsonValueKind.Object) stateText = KtcJson.Str(so, "Name", "StateName");
        var leaves = Leaves(e);
        if (stateId == null && stateText.Length == 0)
        {
            if (Find(leaves, StateRx, IsNum) is { } sl) stateId = int.TryParse(Text(sl.Value), out var x) ? x : null;
            else if ((Find(leaves, StateTextRx, IsText) ?? Find(leaves, StateRx, IsText)) is { } stl) stateText = CommandTypeRules.Sanitize(Text(stl.Value));
        }
        var state = stateText.Length > 0 ? stateText : StateName(stateId, states, names);
        if (state.Length == 0) state = "Unknown";

        var clientId = KtcJson.NormId(S("ClientID", "ClientId"));
        if (clientId.Length == 0 && Find(leaves, ClientIdRx, IsNum) is { } cl) clientId = KtcJson.NormId(Text(cl.Value));
        var clientName = CommandTypeRules.Sanitize(S("ClientName", "NodeName", "AtmName"));
        if (clientName.Length == 0 && Find(leaves, ClientNameRx, IsText) is { } cnl) clientName = CommandTypeRules.Sanitize(Text(cnl.Value));
        DateTime? FD(string[] exact, Regex rx) => KtcJson.Date(e, exact) ?? (Find(leaves, rx, IsDate) is { } dl ? AnyDateValue(dl.Value) : null);
        var a = clientId.Length > 0 ? atm(clientId) : null;
        var closedUtc = FD(ClosedF, ClosedRx);
        var final = stateId is int sid ? states.FirstOrDefault(x => x.Id == sid)?.IsFinal : null;
        var closed = closedUtc != null || (KtcJson.TryProp(e, out var cb, "IsClosed", "Closed") && cb.ValueKind == JsonValueKind.True) || final == true;

        var cust = CustomerFields(e);
        string FromCustomer(params string[] keys) => cust.FirstOrDefault(f => keys.Any(k => f.Name.Equals(k, StringComparison.OrdinalIgnoreCase)))?.Value ?? "";

        int? ruleId = KtcJson.Int(e, int.MinValue, "DisputeRuleID", "DisputeRuleId", "RuleID", "RuleId") is var ri && ri != int.MinValue ? ri
            : Find(leaves, RuleIdRx, IsNum) is { } rl && int.TryParse(Text(rl.Value), out var rli) ? rli : null;
        var rule = CommandTypeRules.Sanitize(KtcJson.Str(e, "DisputeRuleName", "RuleName", "DisputeRules"));
        if (rule.Length == 0 && KtcJson.TryProp(e, out var ro, "DisputeRule", "Rule"))
            rule = ro.ValueKind == JsonValueKind.Object ? CommandTypeRules.Sanitize(KtcJson.Str(ro, "Name", "RuleName")) : ro.ValueKind == JsonValueKind.String ? CommandTypeRules.Sanitize(ro.GetString()) : "";
        if (rule.Length == 0 && Find(leaves, RuleTextRx, IsText) is { } rtl) rule = CommandTypeRules.Sanitize(Text(rtl.Value));
        if (rule.Length == 0 && ruleId == null && Find(leaves, RuleTextRx, IsNum) is { } rnl && int.TryParse(Text(rnl.Value), out var rni)) ruleId = rni;
        if (rule.Length == 0 && ruleId is int rid) rule = rules != null && rules.TryGetValue(rid, out var rn) && rn.Length > 0 ? rn : $"Rule {rid}";
        var comments = TicketRules.Comments(e);

        return new DisputeDto
        {
            RuleId = ruleId, Rule = rule, LastComment = comments.Count > 0 ? comments[^1].Text : PanMasker.Mask(CommandTypeRules.Sanitize(KtcJson.Str(e, "LastComment", "LastCommentText"))),
            Id = id, AtmId = clientId, AtmName = a?.Name is { Length: > 0 } an ? an : clientName, Client = a?.Client ?? "",
            StateId = stateId, State = state, IsClosed = closed,
            CreatedUtc = FD(CreatedF, CreatedRx) ?? KtcJson.AnyDate(e), UpdatedUtc = FD(UpdatedF, UpdatedRx), ClosedUtc = closedUtc, TransactionUtc = D(TxTimeF),
            Amount = Amount(e) ?? (tx is { } t2 ? Amount(t2) : null) ?? ExtraAmount(e), Currency = CommandTypeRules.Sanitize(S("Currency", "CurrencyCode", "CurrencyName")),
            Card = MaskCard(S(CardF)), TransactionId = TxId(S(TxIdF) is { Length: > 0 } txv ? txv : Find(leaves, TxIdRx, v => IsNum(v) || IsText(v)) is { } tl ? Text(tl.Value) : ""), TransactionType = CommandTypeRules.Sanitize(S("TransactionType", "TransactionTypeName", "TxnType", "TransactionTypeID")),
            Customer = CommandTypeRules.Sanitize(KtcJson.Str(e, "CustomerName") is { Length: > 0 } cn ? cn : FromCustomer("Name", "CustomerName", "FullName")),
            Reason = CommandTypeRules.Sanitize(KtcJson.Str(e, "Reason", "DisputeReason", "Description", "Details", "Comment")),
            CreatedBy = CommandTypeRules.Sanitize(KtcJson.Str(e, "CreatedBy", "CreatedByUser", "UserName", "User", "Operator", "RaisedBy")),
            LastChangeBy = CommandTypeRules.Sanitize(KtcJson.Str(e, "LastChangeBy", "LastChangedBy", "UpdatedBy", "LastUpdatedBy", "ModifiedBy"))
        };
    }

    /// <summary>KTC's numeric transaction id kept as is (a long id can look like a card number to the text masker); anything else is sanitized.</summary>
    private static string TxId(string v) => long.TryParse(v.Trim(), out _) ? v.Trim() : CommandTypeRules.Sanitize(v);

    private static decimal? Amount(JsonElement e)
    {
        if (!KtcJson.TryProp(e, out var v, AmountF)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var s)) return s;
        return null;
    }

    /// <summary>
    /// The amount from the dispute's extra data. KTC's DisputeManagement configuration (DefaultDisputeExtraData, seen in the bank's
    /// client trace) defines extra data "Amount" and "EffectiveAmount" (NUMBER) and "TSN" (STRING) on every dispute.
    /// </summary>
    private static decimal? ExtraAmount(JsonElement e)
    {
        var x = ExtraFields(e);
        var v = x.FirstOrDefault(f => f.Name.Equals("Amount", StringComparison.OrdinalIgnoreCase))?.Value
             ?? x.FirstOrDefault(f => f.Name.Equals("EffectiveAmount", StringComparison.OrdinalIgnoreCase))?.Value;
        return decimal.TryParse(v, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private static JsonElement? Nested(JsonElement e, string[] names)
    {
        if (!KtcJson.TryProp(e, out var v, names)) return null;
        if (v.ValueKind == JsonValueKind.Object) return v;
        if (v.ValueKind == JsonValueKind.String && v.GetString() is { } s && s.TrimStart().StartsWith('{'))
            try { return JsonDocument.Parse(s).RootElement.Clone(); } catch (JsonException) { }
        return null;
    }


    // ---------------- tolerant field finding ----------------
    // KTC's real GroupDisputes records did not use the names above for several fields (bank screenshot: transaction id, created
    // time, client, state and rule came out empty). When no exact name matches, fields are found by the shape of their name at any
    // depth (up to 3 levels), e.g. "TransactionDataID", "ATMID", "DateAdded", "DisputeStatus", "RuleID", "Transaction.ID".

    private static readonly Regex Containers = new(@"customer|extra|comment|history|smsqueue|notification", RegexOptions.IgnoreCase);
    public record Leaf(string Path, string Name, JsonElement Value);

    /// <summary>Every primitive value of the record with its path ("Transaction.ID"), skipping customer info, extra data and comments.</summary>
    public static List<Leaf> Leaves(JsonElement e)
    {
        var list = new List<Leaf>();
        void Walk(JsonElement o, string path, string parent, int depth)
        {
            foreach (var p in o.EnumerateObject())
            {
                var full = path.Length == 0 ? p.Name : path + "." + p.Name;
                if (p.Value.ValueKind == JsonValueKind.Object) { if (depth < 3 && !Containers.IsMatch(p.Name)) Walk(p.Value, full, p.Name, depth + 1); }
                else if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { } js && js.TrimStart().StartsWith('{') && depth < 3 && !Containers.IsMatch(p.Name))
                {
                    try { using var d = JsonDocument.Parse(js); if (d.RootElement.ValueKind == JsonValueKind.Object) Walk(d.RootElement.Clone(), full, p.Name, depth + 1); } catch (JsonException) { }
                }
                else if (p.Value.ValueKind is not (JsonValueKind.Array or JsonValueKind.Null or JsonValueKind.Undefined))
                    // A nested object's plain "ID"/"Name" is named after its parent: Transaction.ID -> "TransactionID".
                    list.Add(new(full, depth > 0 && Regex.IsMatch(p.Name, @"^(id|name|code|number|no)$", RegexOptions.IgnoreCase) ? parent + p.Name : p.Name, p.Value));
            }
        }
        if (e.ValueKind == JsonValueKind.Object) Walk(e, "", "", 0);
        return list;
    }

    private static readonly Regex TxIdRx = new(@"^(dispute_?)?(trans(action)?|txn|tran|trx)_?(data_?)?(id|no|num|number|uuid|ref)$", RegexOptions.IgnoreCase);
    private static readonly Regex ClientIdRx = new(@"^(client|atm|terminal|node|device|machine)_?(id|no|num)$", RegexOptions.IgnoreCase);
    private static readonly Regex ClientNameRx = new(@"^(client|atm|terminal|node|device|machine)_?(name|code|label|desc(ription)?)?$", RegexOptions.IgnoreCase);
    private static readonly Regex CreatedRx = new(@"^((date|time)_?(created|added|inserted|raised|opened|logged|registered|of_?creation)|(created|added|inserted|raised|opened|logged|registered|creation)_?(date|time|on|at|stamp|datetime|timestamp)?)$", RegexOptions.IgnoreCase);
    private static readonly Regex UpdatedRx = new(@"^((date|time)_?(updated|modified|changed|edited)|(last_?)?(updated|modified|changed|edited|update|modification|change)_?(date|time|on|at|stamp|datetime|timestamp)?)$", RegexOptions.IgnoreCase);
    private static readonly Regex ClosedRx = new(@"^((date|time)_?closed|closed?_?(date|time|on|at|stamp|datetime|timestamp))$", RegexOptions.IgnoreCase);
    private static readonly Regex StateRx = new(@"^(current_?|dispute_?|record_?)?(state|status)(_?(id|no|code|num|value))?$", RegexOptions.IgnoreCase);
    private static readonly Regex StateTextRx = new(@"^(current_?|dispute_?|record_?)?(state|status)_?(name|text|desc(ription)?|label)$", RegexOptions.IgnoreCase);
    private static readonly Regex RuleIdRx = new(@"^(dispute_?)?(management_?)?rules?_?(id|no|num)$", RegexOptions.IgnoreCase);
    private static readonly Regex RuleTextRx = new(@"^(dispute_?)?(management_?)?rules?(_?(name|text|desc(ription)?|code|label))?$", RegexOptions.IgnoreCase);

    private static Leaf? Find(List<Leaf> l, Regex rx, Func<JsonElement, bool>? ok = null) => l.FirstOrDefault(x => rx.IsMatch(x.Name) && (ok == null || ok(x.Value)));
    private static bool IsNum(JsonElement v) => v.ValueKind == JsonValueKind.Number || (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out _));
    private static bool IsText(JsonElement v) => v.ValueKind == JsonValueKind.String && (v.GetString() ?? "").Trim().Length > 0 && !long.TryParse(v.GetString(), out _);
    private static string Text(JsonElement v) => v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText();

    /// <summary>A date in any form KTC uses: ISO text, "/Date(1731...)/", or epoch milliseconds; .NET / SQL "not set" dates give null.</summary>
    public static DateTime? AnyDateValue(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString() ?? "";
            var m = Regex.Match(s, @"^/Date\((-?\d+)([+-]\d{4})?\)/$");
            if (m.Success) { var d0 = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(m.Groups[1].Value)).UtcDateTime; return d0.Year <= 1901 ? null : d0; }
            if (s.Length >= 8 && DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d)) return d.Year <= 1901 ? null : d;
        }
        return null;
    }
    private static bool IsDate(JsonElement v) => AnyDateValue(v) != null;

    /// <summary>The record's customer info map: the known names, else any property named like "customer…".</summary>
    public static List<KtcField> CustomerFields(JsonElement e) => MapLike(e, CustomerF, @"customer");
    /// <summary>The record's extra data map (Amount, EffectiveAmount, TSN): the known names, else any property named like "…extra…".</summary>
    public static List<KtcField> ExtraFields(JsonElement e) => MapLike(e, ExtraDataF, @"extra");

    private static List<KtcField> MapLike(JsonElement e, string[] names, string like)
    {
        var f = MapFields(e, names);
        if (f.Count > 0 || e.ValueKind != JsonValueKind.Object) return f;
        foreach (var p in e.EnumerateObject())
            if (Regex.IsMatch(p.Name, like, RegexOptions.IgnoreCase) && p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.String)
            {
                f = MapFields(e, new[] { p.Name });
                if (f.Count > 0) return f;
            }
        return f;
    }

    /// <summary>List columns that came out empty for this record (for the "field names not recognised" warning).</summary>
    public static List<string> Missing(DisputeDto d)
    {
        var m = new List<string>();
        if (d.TransactionId.Length == 0) m.Add("Transaction Id");
        if (d.CreatedUtc == null) m.Add("Created At");
        if (d.AtmId.Length == 0 && d.AtmName.Length == 0) m.Add("Client");
        if (d.StateId == null && d.State is "" or "Unknown") m.Add("Status");
        if (d.Rule.Length == 0) m.Add("Dispute Rules");
        return m;
    }

    // ---------------- masking ----------------

    /// <summary>A card number as at most first 6 / last 4 digits; an already-masked value is kept; anything else is starred.</summary>
    public static string MaskCard(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return "";
        var t = v.Trim();
        var digits = PanMasker.Digits(t);
        if (t.Any(c => c is '*' or 'X' or 'x') && digits.Length <= 10) return t;     // already masked by KTC
        if (digits.Length >= 12) return PanMasker.MaskDigits(digits);
        return digits.Length > 4 ? new string('*', digits.Length - 4) + digits[^4..] : new string('*', Math.Max(4, t.Length));
    }

    /// <summary>The value to show for a field: card fields masked to 6+4, track / CVV / PIN fields hidden, card numbers in text masked.</summary>
    public static string SafeValue(string name, string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (HiddenFieldRx().IsMatch(name)) return "(hidden)";
        if (CardFieldRx().IsMatch(name)) return MaskCard(value);
        return CommandTypeRules.Sanitize(value);
    }

    public static bool IsSensitiveName(string name) => HiddenFieldRx().IsMatch(name) || CardFieldRx().IsMatch(name);

    /// <summary>A copy of a KTC JSON value with every card / track / CVV / PIN value masked or hidden, at any depth.</summary>
    public static JsonNode? Scrub(JsonNode? n, string name = "")
    {
        switch (n)
        {
            case JsonObject o:
                var r = new JsonObject();
                foreach (var (k, v) in o) r[k] = Scrub(v, k);
                return r;
            case JsonArray a:
                var ra = new JsonArray();
                foreach (var v in a) ra.Add(Scrub(v, name));
                return ra;
            case JsonValue v:
                var el = v.GetValue<JsonElement>();
                if (el.ValueKind == JsonValueKind.String) return JsonValue.Create(SafeValue(name, el.GetString()));
                if (el.ValueKind == JsonValueKind.Number && IsSensitiveName(name)) return JsonValue.Create(SafeValue(name, el.GetRawText()));
                return JsonNode.Parse(el.GetRawText());
            default: return null;
        }
    }

    /// <summary>Every field of a KTC record as name / value text, nested values compact, all card data masked.</summary>
    public static List<KtcField> Fields(JsonElement? e, params string[] skip)
    {
        var list = new List<KtcField>();
        if (e is not { ValueKind: JsonValueKind.Object } o) return list;
        foreach (var p in o.EnumerateObject())
        {
            if (skip.Any(s => s.Equals(p.Name, StringComparison.OrdinalIgnoreCase))) continue;
            string v = p.Value.ValueKind switch
            {
                JsonValueKind.String => SafeValue(p.Name, p.Value.GetString()),
                JsonValueKind.Null or JsonValueKind.Undefined => "",
                JsonValueKind.Object or JsonValueKind.Array => HiddenFieldRx().IsMatch(p.Name) ? "(hidden)" : Scrub(JsonNode.Parse(p.Value.GetRawText()), p.Name)!.ToJsonString(),
                _ => SafeValue(p.Name, p.Value.GetRawText())
            };
            if (v.Length > 4000) v = v[..4000] + "…";
            list.Add(new KtcField(p.Name, v));
        }
        return list;
    }

    /// <summary>
    /// A name/value map inside the record (customer details, extra data, session or transaction fields) as fields, masked. KTC may
    /// send it as an object, a list of {Key|Name|FieldName, Value} pairs, a JSON string, or an XML string.
    /// </summary>
    public static List<KtcField> MapFields(JsonElement e, string[] names)
    {
        var list = new List<KtcField>();
        if (!KtcJson.TryProp(e, out var v, names)) return list;
        Add(v, list);
        return list;

        static void Add(JsonElement v, List<KtcField> list)
        {
            switch (v.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var p in v.EnumerateObject())
                        list.Add(new(p.Name, p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                            ? Scrub(JsonNode.Parse(p.Value.GetRawText()), p.Name)!.ToJsonString()
                            : p.Value.ValueKind == JsonValueKind.Null ? "" : SafeValue(p.Name, p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText())));
                    break;
                case JsonValueKind.Array:
                    foreach (var x in v.EnumerateArray())
                    {
                        if (x.ValueKind != JsonValueKind.Object) continue;
                        var k = KtcJson.Str(x, "Key", "Name", "FieldName", "Field", "FieldID", "ID");
                        var val = KtcJson.Str(x, "Value", "StringValue", "FieldValue", "Text");
                        if (k.Length > 0) list.Add(new(k, SafeValue(k, val)));
                    }
                    break;
                case JsonValueKind.String:
                    var s = v.GetString() ?? "";
                    if (s.Trim().Length == 0) return;
                    if (s.TrimStart().StartsWith('{') || s.TrimStart().StartsWith('['))
                        try { using var d = JsonDocument.Parse(s); Add(d.RootElement.Clone(), list); return; } catch (JsonException) { }
                    if (s.TrimStart().StartsWith('<'))
                        try
                        {
                            var x = XElement.Parse(s);
                            foreach (var c in x.Elements())
                            {
                                var k = c.Attribute("Name")?.Value ?? c.Attribute("Key")?.Value ?? c.Name.LocalName;
                                list.Add(new(k, SafeValue(k, c.Attribute("Value")?.Value ?? c.Value)));
                            }
                            foreach (var at in x.Attributes()) list.Add(new(at.Name.LocalName, SafeValue(at.Name.LocalName, at.Value)));
                            return;
                        }
                        catch (System.Xml.XmlException) { }
                    list.Add(new("Value", CommandTypeRules.Sanitize(s)));
                    break;
            }
        }
    }

    /// <summary>History entries (state changes) when the record carries them, each as masked fields.</summary>
    public static List<IReadOnlyList<KtcField>> History(JsonElement e) =>
        KtcJson.TryProp(e, out var h, HistoryF) && h.ValueKind == JsonValueKind.Array
            ? h.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).Select(x => (IReadOnlyList<KtcField>)Fields(x)).ToList()
            : new List<IReadOnlyList<KtcField>>();

    /// <summary>GET SMSQueue entries that belong to this dispute (by a dispute id field), masked.</summary>
    public static List<IReadOnlyList<KtcField>> NotificationsFor(JsonElement? root, string disputeId) =>
        KtcJson.Items(root).Where(x => x.ValueKind == JsonValueKind.Object && KtcJson.Str(x, "DisputeID", "DisputeId", "Dispute_ID", "dispute_id") == disputeId)
            .Select(x => (IReadOnlyList<KtcField>)Fields(x)).ToList();

    // ---------------- search ----------------

    /// <summary>
    /// POST DisputeManagement/Search body, in the shape of KTC's DisputeSearch: state ids (null = none known), date range, business,
    /// group, and empty field-filter maps (filters by STX field id would need ids we do not know).
    /// </summary>
    public static object SearchBody(int? businessId, int groupId, DateTime from, DateTime to, IReadOnlyList<int>? stateIds) => new
    {
        StatusIDList = stateIds is { Count: > 0 } ? stateIds : null,
        SessionFieldFilters = new Dictionary<string, object>(), TransactionFieldFilters = new Dictionary<string, object>(),
        CustomerInfoFieldFilters = new Dictionary<string, object>(), ExtraDataFieldFilters = new Dictionary<string, object>(),
        BusinessID = businessId, GroupID = groupId, Start = from, End = to, ClientName = (string?)null
    };

    /// <summary>Portal-side filters: state, ATM (via atmMatch), amount range, dispute id and card (last digits).</summary>
    public static IEnumerable<DisputeDto> Filter(IEnumerable<DisputeDto> all, DisputeQuery q, Func<DisputeDto, bool>? atmMatch = null)
    {
        var r = all;
        if (q.StateId is int s) r = r.Where(d => d.StateId == s);
        if (atmMatch != null) r = r.Where(atmMatch);
        if (q.MinAmount is decimal min) r = r.Where(d => d.Amount >= min);
        if (q.MaxAmount is decimal max) r = r.Where(d => d.Amount <= max);
        if (!string.IsNullOrWhiteSpace(q.DisputeId)) r = r.Where(d => d.Id.Contains(q.DisputeId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(q.Card))
        {
            // Only the last 4 digits are ever compared: the full number is never kept or shown.
            var digits = PanMasker.Digits(q.Card);
            var last = digits.Length >= 4 ? digits[^4..] : digits;
            if (last.Length > 0) r = r.Where(d => PanMasker.Digits(d.Card).EndsWith(last, StringComparison.Ordinal));
        }
        return r;
    }

    // ---------------- writes ----------------

    /// <summary>POST Dispute/{id}/ChangeState body: KTC's Tuple&lt;int,string&gt; {"Item1": state id, "Item2": text}. Card numbers masked.</summary>
    public static object ChangeStateBody(int stateId, string reason) => new { Item1 = stateId, Item2 = PanMasker.Mask(reason) };

    /// <summary>
    /// POST api/v1/Comment/User body for a dispute comment, exactly as KTC's Windows client sends it (trace of dispute 10325):
    /// {"table":13,"rowID":10325,"kcComment":null,"strComment":"...","SendStatusParameter":5,"Confidential":false,"BusinessID":-1}.
    /// table 13 = Dispute, SendStatusParameter = the dispute's ClientID (KTC then raises DisputeChange for that ATM). Card numbers masked.
    /// </summary>
    public static object CommentBody(string disputeId, string comment, int table, long clientId) =>
        new { table, rowID = long.TryParse(disputeId, out var id) ? id : 0, kcComment = (object?)null, strComment = PanMasker.Mask(comment), SendStatusParameter = clientId, Confidential = false, BusinessID = -1 };

    /// <summary>"2 comments, last: a.rahman 2026-11-18 10:27:47" for the steps shown under "What KTC answered".</summary>
    public static string CommentsText(IReadOnlyList<TicketComment> all)
    {
        if (all.Count == 0) return "no comments";
        var l = all[^1]; var t = l.Text.Length > 60 ? l.Text[..60] + "…" : l.Text;
        return $"{all.Count} comment{(all.Count == 1 ? "" : "s")}, last: {l.User} {l.Utc:yyyy-MM-dd HH:mm:ss} \"{PanMasker.Mask(t)}\"";
    }

    /// <summary>"state 3 (Investigating), closed no" for the steps and the audit.</summary>
    public static string StateText(DisputeDto? d) =>
        d == null ? "not readable" : $"state {(d.StateId?.ToString() ?? "none")} ({d.State}), closed {(d.IsClosed ? "yes" + (d.ClosedUtc is { } c ? " " + c.ToString("yyyy-MM-dd HH:mm:ss") + " UTC" : "") : "no")}";
}
