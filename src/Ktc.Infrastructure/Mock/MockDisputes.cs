using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Net;
using System.Text.Json;

namespace Ktc.Infrastructure.Mock;

/// <summary>
/// Demo-mode KTC DisputeManagement routes. The record shape is the portal's best reading of KTC's DisputeSearch filters (session,
/// transaction, customer info and extra data) and is NOT taken from a real server: the KTC collection has no response samples.
/// Card numbers are full test PANs on purpose, so the portal's masking can be seen working.
/// </summary>
public sealed partial class MockKtcHttpHandler
{
    // State names as KTC's Windows client lists them on the bank's server (screenshots); the ids and "final" flags are demo assumptions.
    // State 0 "All" is both the list's "all states" filter value and the state a new dispute is in (the screenshot's dispute shows "All").
    private static readonly (int Id, string Name, bool Final)[] DisputeStates =
        { (0, "All", false), (1, "Pending", false), (2, "Settled", true), (3, "False Alert", true), (4, "Pending Notes not Validated", false), (5, "New not closed", false),
          (6, "Resolved - Under the Limit - Notes not Validated", true) };
    private static readonly (int Id, string Name)[] DisputeRuleNames =
        { (1, "CIM-ITEMS-RETRACTED_CashDepositToAccount"), (2, "CASH-NOT-DISPENSED_Withdrawal"), (3, "PARTIAL-DISPENSE_Withdrawal"), (4, "NOTES-RETRACTED_Withdrawal"), (5, "CARD-RETAINED_Withdrawal") };
    private static readonly string[] TestPans = { "4111111111111111", "5500005555555559", "4012888888881881", "5105105105105100", "4000056655665556" };
    private static readonly string[] DisputeReasons =
    {
        "Cash not dispensed but account debited", "Partial dispense: fewer notes than requested", "Deposit not credited to account",
        "Duplicate debit for one withdrawal", "Card retained, customer charged a fee", "Customer says card {pan} was used without consent"
    };
    private static readonly string[] DisputeTxTypes = { "Cash Withdrawal", "Cash Withdrawal", "Fast Cash", "Cash Deposit", "Cash Withdrawal", "Balance Inquiry" };
    private static readonly string[] Customers = { "A. Rahman", "S. Kumar", "M. Al Mansoori", "J. Fernandes", "F. Haddad", "R. Nair", "L. Okafor" };
    private const int DisputeCount = 48;
    private static readonly ConcurrentDictionary<long, (int State, DateTime At, string User)> DisputeChanges = new();

    private record MockDispute(long ID, int AtmIndex, int StateID, int RuleID, DateTime TimeCreated, DateTime TimeUpdated, DateTime? TimeClosed, DateTime TransactionTime,
        string TransactionType, decimal Amount, string Pan, string Reason, string Customer, List<(int State, DateTime At, string User)> History);

    private static IEnumerable<MockDispute> AllDisputes(DateTime now) => Enumerable.Range(0, DisputeCount).Select(n => MakeDispute(n, now));

    private static MockDispute MakeDispute(int n, DateTime now)
    {
        var i = Hash(n, 901) % AtmCount;
        var created = now.Date.AddDays(-(Hash(n, 902) % 40)).AddHours(7 + Hash(n, 903) % 13).AddMinutes(Hash(n, 904) % 60);
        if (created > now) created = created.AddDays(-1);
        var tx = created.AddHours(-(2 + Hash(n, 905) % 46));
        var ageDays = (now - created).TotalDays;
        // Older disputes have moved further along.
        var path = ageDays < 1 ? new[] { 0 } : ageDays < 4 ? new[] { 0, 1 } : ageDays < 10 ? new[] { 0, 1, (Hash(n, 906) % 2) == 0 ? 4 : 5 }
            : (Hash(n, 906) % 3) switch { 0 => new[] { 0, 1, 2 }, 1 => new[] { 0, 1, 3 }, _ => new[] { 0, 1, 4, 6 } };
        var users = new[] { "System", "s.kumar@bank.example", "a.rahman@bank.example", "ops.lead@bank.example" };
        var hist = path.Select((s, k) => (s, created.AddHours(k * Math.Max(1, ageDays * 24 / (path.Length + 1))), k == 0 ? "System" : users[(n + k) % users.Length])).ToList();
        var id = 920000L + n;
        foreach (var c in DisputeChanges.Where(x => x.Key == id).Select(x => x.Value)) hist.Add(c);
        var cur = hist[^1];
        var final = DisputeStates.First(s => s.Id == cur.Item1).Final;
        var type = DisputeTxTypes[Hash(n, 907) % DisputeTxTypes.Length];
        var amount = type == "Balance Inquiry" ? 0m : 100m + Hash(n, 908) % 59 * 50m;
        var pan = TestPans[Hash(n, 909) % TestPans.Length];
        var reason = DisputeReasons[Hash(n, 910) % DisputeReasons.Length].Replace("{pan}", string.Join(" ", Enumerable.Range(0, 4).Select(k => pan.Substring(k * 4, 4))));
        var rule = type == "Cash Deposit" ? 1 : 2 + Hash(n, 916) % 4;
        return new MockDispute(id, i, cur.Item1, rule, created, cur.Item2, final ? cur.Item2 : null, tx, type, amount, pan, reason, Customers[Hash(n, 911) % Customers.Length], hist);
    }

    /// <summary>A demo transaction of the dispute's ATM at or before its transaction time, so the Transaction tab finds it (same id scheme as Transactions()).</summary>
    private static (long Id, DateTime Time) DisputedTx(MockDispute d)
    {
        var i = d.AtmIndex;
        var h0 = new DateTime(d.TransactionTime.Year, d.TransactionTime.Month, d.TransactionTime.Day, d.TransactionTime.Hour, 0, 0, DateTimeKind.Utc);
        for (var hour = h0; hour > h0.AddHours(-72); hour = hour.AddHours(-1))
        {
            int hi = (int)(hour.Ticks / TimeSpan.TicksPerHour);
            int weight = hour.Hour is >= 6 and <= 20 ? 2 : 1;
            if (Hash(i, hi, 41) % (weight * 3 + 1) > 0 && !IsOffline(i, hour)) return ((long)hi * 100000 + i * 100, hour.AddSeconds(Hash(i, hi, 50) % 3600));
        }
        return (42000L + d.ID % 100_000 * 7, d.TransactionTime);
    }

    private static object DisputeJson(MockDispute d)
    {
        var name = $"{Banks[d.AtmIndex % 3][..3].ToUpper()}-ATM-{d.AtmIndex + 1:000}";
        var st = DisputeStates.First(s => s.Id == d.StateID);
        var cust = d.Customer.Replace(". ", ".").Replace(" ", "").ToLowerInvariant();
        // Comments: KTC adds the first one when a rule raises the dispute (screenshot); state changes and users' comments follow.
        var comments = new[] { (T: d.TimeCreated, U: "System", C: "Dispute created automatically based on matching rule.") }
            .Concat(d.History.Skip(1).Select(h => (T: h.At, U: h.User, C: h.State == 2 ? $"Settled. Customer refunded; card {d.Pan} checked." : $"State changed to {DisputeStates.First(s => s.Id == h.State).Name}.")))
            .Concat(AddedComments.TryGetValue(d.ID, out var added) ? added.ToList().Select(a => (T: a.T, U: a.U, C: a.C)) : Enumerable.Empty<(DateTime T, string U, string C)>());
        return new
        {
            ID = d.ID, ClientID = Id(d.AtmIndex), ClientName = name, StateID = d.StateID, StateName = st.Name, DisputeRuleID = d.RuleID, BusinessID = d.AtmIndex % 3 + 1,
            TimeCreated = d.TimeCreated, TimeUpdated = d.History.Count > 1 ? d.TimeUpdated : (DateTime?)null, TimeClosed = d.TimeClosed,
            TransactionID = DisputedTx(d).Id, TransactionTime = DisputedTx(d).Time, TransactionType = d.TransactionType, PAN = d.Pan,
            CustomerDetails = new Dictionary<string, object?>
            {
                ["customerName"] = d.Customer, ["customerTelephone"] = "", ["customerMobileNumber"] = $"+9715{Hash((int)d.ID, 912) % 10}{Hash((int)d.ID, 913) % 9000000 + 1000000}",
                ["customerEmail"] = $"{cust}@example.com", ["customerCategory"] = Hash((int)d.ID, 914) % 2 == 0 ? "Retail" : "Priority", ["availableBalance"] = "",
                ["rmName"] = "", ["rmContact"] = "", ["rmEmail"] = ""
            },
            ExtraData = new Dictionary<string, object?> { ["Amount"] = d.Amount, ["EffectiveAmount"] = d.Amount, ["TSN"] = (d.ID % 10000).ToString("0000") },
            CommentsString = "<Comments>" + string.Concat(comments.Select(c => new System.Xml.Linq.XElement("Comment", new System.Xml.Linq.XAttribute("User", c.U),
                new System.Xml.Linq.XAttribute("Timestamp", c.T.ToString("yyyy-MM-dd HH:mm:ss")), new System.Xml.Linq.XAttribute("Confidential", "False"), c.C)
                .ToString(System.Xml.Linq.SaveOptions.DisableFormatting))) + "</Comments>"
        };
    }

    private record MockSms(long ID, long DisputeID, string PhoneNumber, string Message, DateTime TimeQueued, bool Sent, DateTime? TimeSent);
    private static IEnumerable<MockSms> SmsQueue(DateTime now) =>
        AllDisputes(now).Where(x => x.History.Count > 1).SelectMany(x => x.History.Skip(1).Where(h => h.State is 1 or 2 or 3 or 6).Select((h, k) => new MockSms(
            x.ID * 10 + k, x.ID, "+97150*****" + (x.ID % 1000).ToString("000"), $"Your ATM dispute DSP-{x.ID % 100000:00000} is now: {DisputeStates.First(s => s.Id == h.State).Name}.",
            h.At, h.At < now.AddMinutes(-10), h.At < now.AddMinutes(-10) ? h.At.AddMinutes(2) : null)));

    private HttpResponseMessage? Disputes(string path, string m, NameValueCollection q, string body, DateTime now)
    {
        const string R = "api/v1/DisputeManagement/";
        var p = path[R.Length..];
        if (p.Equals("DisputeState", StringComparison.OrdinalIgnoreCase) && m == "GET")
            return Ok(DisputeStates.ToDictionary(s => s.Id.ToString(), s => (object)new { ID = s.Id, Name = s.Name, IsFinal = s.Final }));
        if (p.Equals("DisputeRule", StringComparison.OrdinalIgnoreCase) && m == "GET")
            return Ok(DisputeRuleNames.ToDictionary(r => r.Id.ToString(), r => (object)new { ID = r.Id, Name = r.Name, Enabled = true }));
        // The list calls KTC's Windows client makes (bank trace): {groupid}/GroupDisputes and {clientid}/ClientDisputes ?statusfilter=&Numdays=.
        // statusfilter is the state picked in the client's "Dispute states filter"; 0 = all states (confirmed by the bank).
        System.Text.RegularExpressions.Match lr;
        if ((lr = Rx(path, @"api/v1/DisputeManagement/(\d+)/(GroupDisputes|ClientDisputes)")).Success && m == "GET")
        {
            var sf = int.TryParse(q["statusfilter"], out var f) ? f : 0; var nd = int.TryParse(q["Numdays"] ?? q["numdays"], out var dd) ? dd : 14;
            var idv = int.Parse(lr.Groups[1].Value); var byClient = lr.Groups[2].Value == "ClientDisputes";
            return Ok(AllDisputes(now).Where(x => (byClient ? Id(x.AtmIndex) == idv : true) && x.TimeCreated >= now.AddDays(-nd) && (sf == 0 || x.StateID == sf))
                .Select(DisputeJson));
        }
        if (p.Equals("SMSQueue/Disputes", StringComparison.OrdinalIgnoreCase) && m == "POST")
        {
            using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "[]" : body);
            var ids = d.RootElement.ValueKind == JsonValueKind.Array ? d.RootElement.EnumerateArray().Select(x => x.GetInt64()).ToHashSet() : new HashSet<long>();
            return Ok(SmsQueue(now).Where(x => ids.Contains(x.DisputeID)).ToDictionary(x => x.ID.ToString(), x => (object)x));
        }
        if (p.Equals("DisputeMessageTemplate", StringComparison.OrdinalIgnoreCase) && m == "GET")
            return Ok(new Dictionary<string, object> { ["1"] = new { ID = 1, Name = "State change", Template = "Your ATM dispute <DisputeRef> is now: <StateName>." } });
        if (p.Equals("Search", StringComparison.OrdinalIgnoreCase) && m == "POST")
        {
            using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body); var b = d.RootElement;
            var ids = b.TryGetProperty("StatusIDList", out var sl) && sl.ValueKind == JsonValueKind.Array ? sl.EnumerateArray().Select(x => x.GetInt32()).ToHashSet() : null;
            DateTime? from = b.TryGetProperty("Start", out var s) && s.ValueKind == JsonValueKind.String ? s.GetDateTime().ToUniversalTime() : null;
            DateTime? to = b.TryGetProperty("End", out var e) && e.ValueKind == JsonValueKind.String ? e.GetDateTime().ToUniversalTime() : null;
            int? biz = b.TryGetProperty("BusinessID", out var bz) && bz.ValueKind == JsonValueKind.Number && bz.GetInt32() > 0 ? bz.GetInt32() : null;
            return Ok(AllDisputes(now).Where(x => (ids == null || ids.Contains(x.StateID)) && (from == null || x.TimeCreated >= from) && (to == null || x.TimeCreated <= to)
                && (biz == null || x.AtmIndex % 3 + 1 == biz)).Select(DisputeJson));
        }
        System.Text.RegularExpressions.Match r;
        if ((r = Rx(path, @"api/v1/DisputeManagement/Dispute/(\d+)/ChangeState")).Success && m == "POST")
        {
            var id = long.Parse(r.Groups[1].Value);
            var cur = AllDisputes(now).FirstOrDefault(x => x.ID == id);
            if (cur == null) return Resp(HttpStatusCode.InternalServerError, "{\"Message\":\"Exception: Dispute not found\"}");
            using var d = JsonDocument.Parse(body);
            var st = d.RootElement.TryGetProperty("Item1", out var i1) && i1.ValueKind == JsonValueKind.Number ? i1.GetInt32() : -1;
            if (st == 0 || !DisputeStates.Any(s => s.Id == st)) return Resp(HttpStatusCode.InternalServerError, "{\"Message\":\"Exception: Unknown dispute state\"}");
            if (cur.TimeClosed != null) return Resp(HttpStatusCode.OK, "false");
            DisputeChanges[id] = (st, now, "demo");
            return Resp(HttpStatusCode.OK, "true");
        }
        if (p.StartsWith("Dispute/Close", StringComparison.OrdinalIgnoreCase) && m == "POST" && long.TryParse(q["disputeId"], out var cid))
        {
            var cur = AllDisputes(now).FirstOrDefault(x => x.ID == cid);
            if (cur == null) return Resp(HttpStatusCode.InternalServerError, "{\"Message\":\"Exception: Dispute not found\"}");
            if (cur.TimeClosed == null) DisputeChanges[cid] = (2, now, "demo");
            return Resp(HttpStatusCode.OK, "true");
        }
        if ((r = Rx(path, @"api/v1/DisputeManagement/Dispute/(\d+)")).Success && m == "GET")
        {
            var x = AllDisputes(now).FirstOrDefault(z => z.ID == long.Parse(r.Groups[1].Value));
            return x == null ? Resp(HttpStatusCode.NotFound, "null") : Ok(DisputeJson(x));
        }
        if (p.Equals("SMSQueue", StringComparison.OrdinalIgnoreCase) && m == "GET") return Ok(SmsQueue(now));
        // Every other DisputeManagement call (create, update, delete, SMS writes, templates) is not used by the portal.
        return Resp(HttpStatusCode.NotImplemented, "{\"Message\":\"Demo KTC server: this dispute route is not simulated.\"}");
    }
}
