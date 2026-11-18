using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Ktc.Application.Models;
using Ktc.Domain.Enums;

namespace Ktc.Application.Logic;

/// <summary>
/// Reads KTC ticket records the way KTC's own ticket list shows them: status name, comments, last change, SLA summary, and every
/// field KTC returned (for the details popup). Field names are read tolerantly because they differ between KTC versions. Pure.
/// </summary>
public static partial class TicketRules
{
    [GeneratedRegex(@"auto.?clos", RegexOptions.IgnoreCase)] private static partial Regex AutoClosedRx();

    /// <summary>The portal's category for a KTC status name (by its wording).</summary>
    public static TicketStatus Category(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("clos") || n.Contains("cancel") || n.Contains("delet") ? TicketStatus.Closed
            : n.Contains("resolv") || n.Contains("complet") ? TicketStatus.Resolved
            : n.Contains("dispatch") || n.Contains("progress") || n.Contains("suspend") ? TicketStatus.InProgress
            : n.Contains("own") || n.Contains("assign") ? TicketStatus.Assigned
            : TicketStatus.Open;
    }

    /// <summary>
    /// KTC's status name: a text status field when KTC sends one, else the configured name for StatusByte, else inferred from the
    /// times (closed by the system = AutoClosed, closed, dispatched, owned, open). Inferred names are marked with the number.
    /// </summary>
    public static (string Name, TicketStatus Category, int? Byte) Status(JsonElement e, IReadOnlyDictionary<string, string> map, IReadOnlyList<TicketComment> comments)
    {
        int? b = KtcJson.TryProp(e, out var sv, "StatusByte", "Status", "TicketStatus") && sv.ValueKind == JsonValueKind.Number && sv.TryGetInt32(out var bi) ? bi : null;
        var text = KtcJson.Str(e, "StatusName", "StatusString", "StatusText", "StatusDescription");
        if (text.Length == 0 && KtcJson.TryProp(e, out var st, "Status", "TicketStatus") && st.ValueKind == JsonValueKind.String && !int.TryParse(st.GetString(), out _)) text = st.GetString() ?? "";
        if (text.Length == 0 && b != null && map.TryGetValue(b.Value.ToString(), out var m) && m.Trim().Length > 0) text = m.Trim();
        if (text.Length > 0)
        {
            // Older config used the category names themselves; those still work.
            var cat = Enum.TryParse<TicketStatus>(text, true, out var c) && text.All(char.IsLetter) ? c : Category(text);
            return (text, cat, b);
        }
        var closed = KtcJson.Date(e, "TimeClosed", "ClosedDate", "Closed");
        if (closed != null)
        {
            var auto = KtcJson.Str(e, "ClosureMode", "ClosureModeName").Contains("auto", StringComparison.OrdinalIgnoreCase)
                       || comments.Any(x => AutoClosedRx().IsMatch(x.Text));
            return (auto ? "AutoClosed" : "Closed", TicketStatus.Closed, b);
        }
        if (KtcJson.Date(e, "TimeDispatched", "DispatchedTime") != null) return ("Dispatched", TicketStatus.InProgress, b);
        if (KtcJson.Date(e, "TimeOwned", "OwnedTime") != null || KtcJson.Str(e, "OwnerName", "Owner").Length > 0) return ("Owned", TicketStatus.Assigned, b);
        return (b == null ? "Open" : $"Open (status {b})", TicketStatus.Open, b);
    }

    /// <summary>KTC comments (XML list, JSON list or plain text) as user / time / text, oldest first, card numbers masked.</summary>
    public static List<TicketComment> Comments(JsonElement e)
    {
        var list = new List<TicketComment>();
        if (KtcJson.TryProp(e, out var arr, "Comments", "CommentList") && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in arr.EnumerateArray())
                if (c.ValueKind == JsonValueKind.Object)
                    list.Add(new TicketComment(NullIfEmpty(KtcJson.Str(c, "User", "UserName", "Author")), KtcJson.Date(c, "Timestamp", "Time", "Date", "Added"),
                        PanMasker.Mask(KtcJson.Str(c, "Comment", "Text", "Value"))));
                else if (c.ValueKind == JsonValueKind.String) list.Add(new TicketComment(null, null, PanMasker.Mask(c.GetString())));
        }
        else list.AddRange(ParseCommentsString(KtcJson.Str(e, "CommentsString", "Comments", "CommentString")));
        return list.Where(c => c.Text.Length > 0).OrderBy(c => c.Utc ?? DateTime.MinValue).ToList();
    }

    public static List<TicketComment> ParseCommentsString(string? s)
    {
        var list = new List<TicketComment>();
        if (string.IsNullOrWhiteSpace(s)) return list;
        try
        {
            var x = XElement.Parse(s);
            var items = x.Elements().Any() ? x.Elements() : new[] { x };
            foreach (var c in items)
            {
                string? A(string n) => c.Attribute(n)?.Value ?? c.Element(n)?.Value;
                var text = A("Comment") ?? A("Text") ?? (c.HasElements ? "" : c.Value);
                var when = A("Timestamp") ?? A("Time") ?? A("Date");
                list.Add(new TicketComment(NullIfEmpty(A("User") ?? A("UserName")),
                    DateTime.TryParse(when, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null,
                    PanMasker.Mask(text?.Trim())));
            }
        }
        catch (System.Xml.XmlException) { list.Add(new TicketComment(null, null, PanMasker.Mask(s.Trim()))); }
        return list;
    }

    /// <summary>KTC's SLA column: "No SLA" unless KTC gives the ticket an SLA time.</summary>
    public static (string Summary, bool Has) SlaSummary(DateTime? due, DateTime? closed, DateTime now)
    {
        if (due == null) return ("No SLA", false);
        if (closed != null) return (closed <= due ? "SLA met" : "SLA breached", true);
        var left = due.Value - now;
        return left < TimeSpan.Zero ? ("Breached", true) : left.TotalMinutes < 60 ? ($"Due in {(int)left.TotalMinutes} min", true) : ("On track", true);
    }

    /// <summary>Every field of a KTC record as name / value text (nested values compact, card numbers masked, secrets hidden).</summary>
    public static List<KtcField> Fields(JsonElement? e)
    {
        var list = new List<KtcField>();
        if (e is not { ValueKind: JsonValueKind.Object } o) return list;
        foreach (var p in o.EnumerateObject())
        {
            var v = p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString() ?? "",
                JsonValueKind.Null or JsonValueKind.Undefined => "",
                JsonValueKind.Object or JsonValueKind.Array => p.Value.GetRawText(),
                _ => p.Value.GetRawText()
            };
            if (v.Length > 4000) v = v[..4000] + "…";
            list.Add(new KtcField(p.Name, CommandTypeRules.Sanitize(v)));
        }
        return list;
    }

    /// <summary>
    /// The ticket as KTC returned it, changed to closed: StatusByte, TimeClosed and (when given) ClosureMode / ClosureModeByte.
    /// Every other field goes back unchanged, as KTC's PUT Ticket/{id} takes the whole record. TimeUpdated is kept as read:
    /// with forceupdate=false KTC uses it to detect a concurrent change, and a new value can make it ignore the update.
    /// </summary>
    public static System.Text.Json.Nodes.JsonObject CloseBody(JsonElement ticket, int closedStatusByte, int? closureMode, DateTime nowUtc)
    {
        var o = System.Text.Json.Nodes.JsonNode.Parse(ticket.GetRawText())!.AsObject();
        void Set(string name, System.Text.Json.Nodes.JsonNode? v, bool onlyIfPresent = false)
        {
            var key = o.Select(p => p.Key).FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (key == null && onlyIfPresent) return;
            o[key ?? name] = v;
        }
        var ts = nowUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        Set("StatusByte", closedStatusByte);
        Set("TimeClosed", ts);
        if (closureMode is int cm) { Set("ClosureMode", cm, onlyIfPresent: true); Set("ClosureModeByte", cm, onlyIfPresent: true); }
        return o;
    }

    /// <summary>
    /// KTC's CommentsString with one more comment, written the way the existing comments are (same element name, attributes or
    /// child elements, and timestamp format); a new &lt;Comments&gt; list when there is none. Card numbers masked.
    /// </summary>
    public static string AppendComment(string? commentsXml, string user, DateTime utc, string text)
    {
        text = PanMasker.Mask(text);
        XElement root;
        try { root = string.IsNullOrWhiteSpace(commentsXml) ? new XElement("Comments") : XElement.Parse(commentsXml); }
        catch (System.Xml.XmlException) { root = new XElement("Comments"); }
        var last = root.Elements().LastOrDefault();
        var ts = utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        XElement n;
        if (last != null && last.HasElements)
        {
            // <Comment><Timestamp/><User/><Comment/><Confidential/></Comment>
            n = new XElement(last.Name);
            foreach (var c in last.Elements())
            {
                var k = c.Name.LocalName;
                n.Add(new XElement(c.Name, k.Equals("User", StringComparison.OrdinalIgnoreCase) || k.Equals("UserName", StringComparison.OrdinalIgnoreCase) ? user
                    : k.Contains("Time", StringComparison.OrdinalIgnoreCase) || k.Contains("Date", StringComparison.OrdinalIgnoreCase) ? ts
                    : k.Equals("Confidential", StringComparison.OrdinalIgnoreCase) ? "false"
                    : k.Equals("Comment", StringComparison.OrdinalIgnoreCase) || k.Equals("Text", StringComparison.OrdinalIgnoreCase) ? text : c.Value));
            }
        }
        else
        {
            // <Comment User="" Timestamp="" Confidential="">text</Comment> (also the form when there is no comment yet)
            n = new XElement(last?.Name ?? "Comment");
            var attrs = last?.Attributes().Select(a => a.Name).ToList() ?? new() { "User", "Timestamp", "Confidential" };
            foreach (var a in attrs)
            {
                var k = a.LocalName;
                n.SetAttributeValue(a, k.Equals("User", StringComparison.OrdinalIgnoreCase) || k.Equals("UserName", StringComparison.OrdinalIgnoreCase) ? user
                    : k.Contains("Time", StringComparison.OrdinalIgnoreCase) || k.Contains("Date", StringComparison.OrdinalIgnoreCase) ? ts
                    : k.Equals("Confidential", StringComparison.OrdinalIgnoreCase) ? "false"
                    : k.Equals("Comment", StringComparison.OrdinalIgnoreCase) || k.Equals("Text", StringComparison.OrdinalIgnoreCase) ? text : last!.Attribute(a)!.Value);
            }
            if (!attrs.Any(a => a.LocalName.Equals("Comment", StringComparison.OrdinalIgnoreCase) || a.LocalName.Equals("Text", StringComparison.OrdinalIgnoreCase))) n.Value = text;
        }
        root.Add(n);
        return root.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>The ticket record with one more comment in its CommentsString (the field name KTC used is kept).</summary>
    public static System.Text.Json.Nodes.JsonObject WithComment(System.Text.Json.Nodes.JsonObject o, string user, DateTime utc, string text)
    {
        var key = o.Select(p => p.Key).FirstOrDefault(k => k.Equals("CommentsString", StringComparison.OrdinalIgnoreCase)) ?? "CommentsString";
        o[key] = AppendComment(o[key]?.GetValue<string>(), user, utc, text);
        return o;
    }

    /// <summary>True when KTC's record shows the ticket closed: a closed time, or the closed status number.</summary>
    public static bool LooksClosed(JsonElement ticket, int? closedStatusByte) =>
        KtcJson.Date(ticket, "TimeClosed", "ClosedDate", "Closed") != null
        || (closedStatusByte is int c && KtcJson.Int(ticket, int.MinValue, "StatusByte") == c);

    /// <summary>Short "StatusByte 1, TimeClosed none" description of a KTC ticket record, for the result and the audit.</summary>
    public static string StateText(JsonElement ticket)
    {
        var sb = KtcJson.Str(ticket, "StatusByte"); var ext = KtcJson.Str(ticket, "ExternalStatusByte"); var tc = KtcJson.Date(ticket, "TimeClosed");
        return $"StatusByte {(sb.Length > 0 ? sb : "none")}{(ext.Length > 0 ? ", ExternalStatusByte " + ext : "")}, TimeClosed {(tc == null ? "none" : tc.Value.ToString("yyyy-MM-dd HH:mm:ss") + " UTC")}";
    }

    /// <summary>POST api/v1/Comment body for a ticket comment (card numbers masked).</summary>
    public static object CommentBody(string ticketId, string user, string comment, int table, int sendStatus, DateTime nowUtc)
    {
        var c = PanMasker.Mask(comment);
        var row = long.TryParse(ticketId, out var id) ? id : 0;
        return new { table, rowID = row, kcComment = new { Timestamp = nowUtc, User = user, Comment = c, Confidential = false }, strComment = c, SendStatusParameter = sendStatus, Confidential = false, BusinessID = 0 };
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
