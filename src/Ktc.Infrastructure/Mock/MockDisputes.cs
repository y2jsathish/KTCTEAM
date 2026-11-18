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
    private static readonly (int Id, string Name, bool Final)[] DisputeStates =
        { (1, "New", false), (2, "Under Investigation", false), (3, "Awaiting Customer", false), (4, "Refund Approved", false), (5, "Rejected", true), (6, "Closed", true) };
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

    private record MockDispute(long ID, int AtmIndex, int StateID, DateTime TimeCreated, DateTime TimeUpdated, DateTime? TimeClosed, DateTime TransactionTime,
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
        var path = ageDays < 1 ? new[] { 1 } : ageDays < 4 ? new[] { 1, 2 } : ageDays < 10 ? new[] { 1, 2, 3 }
            : (Hash(n, 906) % 3) switch { 0 => new[] { 1, 2, 4, 6 }, 1 => new[] { 1, 2, 5 }, _ => new[] { 1, 2, 3, 4 } };
        var users = new[] { "system", "s.kumar", "a.rahman", "ops.lead" };
        var hist = path.Select((s, k) => (s, created.AddHours(k * Math.Max(1, ageDays * 24 / (path.Length + 1))), k == 0 ? "system" : users[(n + k) % users.Length])).ToList();
        var id = 920000L + n;
        foreach (var c in DisputeChanges.Where(x => x.Key == id).Select(x => x.Value)) hist.Add(c);
        var cur = hist[^1];
        var final = DisputeStates.First(s => s.Id == cur.Item1).Final;
        var type = DisputeTxTypes[Hash(n, 907) % DisputeTxTypes.Length];
        var amount = type == "Balance Inquiry" ? 0m : 100m + Hash(n, 908) % 59 * 50m;
        var pan = TestPans[Hash(n, 909) % TestPans.Length];
        var reason = DisputeReasons[Hash(n, 910) % DisputeReasons.Length].Replace("{pan}", string.Join(" ", Enumerable.Range(0, 4).Select(k => pan.Substring(k * 4, 4))));
        return new MockDispute(id, i, cur.Item1, created, cur.Item2, final ? cur.Item2 : null, tx, type, amount, pan, reason, Customers[Hash(n, 911) % Customers.Length], hist);
    }

    private static object DisputeJson(MockDispute d)
    {
        var name = $"{Banks[d.AtmIndex % 3][..3].ToUpper()}-ATM-{d.AtmIndex + 1:000}";
        var st = DisputeStates.First(s => s.Id == d.StateID);
        return new
        {
            ID = d.ID, ClientID = Id(d.AtmIndex), ClientName = name, StateID = d.StateID, StateName = st.Name, BusinessID = d.AtmIndex % 3 + 1,
            TimeCreated = d.TimeCreated, TimeUpdated = d.TimeUpdated, TimeClosed = d.TimeClosed, CreatedBy = d.History[0].User, LastChangeBy = d.History[^1].User,
            TransactionID = 5_000_000L + d.ID % 100_000 * 7, SessionID = 2_500_000L + d.ID % 100_000 * 3, TransactionTime = d.TransactionTime, TransactionType = d.TransactionType,
            Amount = d.Amount, Currency = "AED", PAN = d.Pan, Reason = d.Reason,
            CustomerDetails = new Dictionary<string, object?>
            {
                ["Name"] = d.Customer, ["MobileNumber"] = $"+9715{Hash((int)d.ID, 912) % 10}{Hash((int)d.ID, 913) % 9000000 + 1000000}", ["Email"] = $"{d.Customer.Replace(". ", ".").Replace(" ", "").ToLowerInvariant()}@example.com",
                ["CardNumber"] = d.Pan, ["PreferredContact"] = Hash((int)d.ID, 914) % 2 == 0 ? "SMS" : "Email"
            },
            ExtraData = new Dictionary<string, object?> { ["BankReference"] = $"DSP-{d.ID % 100000:00000}", ["Channel"] = Hash((int)d.ID, 915) % 2 == 0 ? "Branch" : "Call Centre", ["Track2Data"] = $"{d.Pan}=2812101000000000" },
            History = d.History.Select(h => new { StateID = h.State, StateName = DisputeStates.First(s => s.Id == h.State).Name, Time = h.At, User = h.User }),
            CommentsString = $"<Comments><Comment User=\"{d.History[0].User}\" Timestamp=\"{d.TimeCreated:yyyy-MM-dd HH:mm:ss}\" Confidential=\"False\">Dispute raised: {d.Reason}</Comment></Comments>"
        };
    }

    private HttpResponseMessage? Disputes(string path, string m, NameValueCollection q, string body, DateTime now)
    {
        const string R = "api/v1/DisputeManagement/";
        var p = path[R.Length..];
        if (p.Equals("DisputeState", StringComparison.OrdinalIgnoreCase) && m == "GET")
            return Ok(DisputeStates.Select(s => new { ID = s.Id, Name = s.Name, IsFinal = s.Final }));
        if (p.Equals("DisputeRule", StringComparison.OrdinalIgnoreCase) && m == "GET")
            return Ok(new object[]
            {
                new { ID = 1, Name = "Acknowledge new disputes", TransactionTypeID = 1, FromStateID = 1, ToStateID = 2, AfterHours = 1, SendsMessage = true },
                new { ID = 2, Name = "Remind customer after 3 days", TransactionTypeID = 1, FromStateID = 3, ToStateID = 3, AfterHours = 72, SendsMessage = true },
                new { ID = 3, Name = "Close approved refunds after 7 days", TransactionTypeID = 1, FromStateID = 4, ToStateID = 6, AfterHours = 168, SendsMessage = false }
            });
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
            if (!DisputeStates.Any(s => s.Id == st)) return Resp(HttpStatusCode.InternalServerError, "{\"Message\":\"Exception: Unknown dispute state\"}");
            if (cur.TimeClosed != null) return Resp(HttpStatusCode.OK, "false");
            DisputeChanges[id] = (st, now, "demo");
            return Resp(HttpStatusCode.OK, "true");
        }
        if (p.StartsWith("Dispute/Close", StringComparison.OrdinalIgnoreCase) && m == "POST" && long.TryParse(q["disputeId"], out var cid))
        {
            var cur = AllDisputes(now).FirstOrDefault(x => x.ID == cid);
            if (cur == null) return Resp(HttpStatusCode.InternalServerError, "{\"Message\":\"Exception: Dispute not found\"}");
            if (cur.TimeClosed == null) DisputeChanges[cid] = (6, now, "demo");
            return Resp(HttpStatusCode.OK, "true");
        }
        if ((r = Rx(path, @"api/v1/DisputeManagement/Dispute/(\d+)")).Success && m == "GET")
        {
            var x = AllDisputes(now).FirstOrDefault(z => z.ID == long.Parse(r.Groups[1].Value));
            return x == null ? Resp(HttpStatusCode.NotFound, "null") : Ok(DisputeJson(x));
        }
        if (p.Equals("SMSQueue", StringComparison.OrdinalIgnoreCase) && m == "GET")
            return Ok(AllDisputes(now).Where(x => x.History.Count > 1).SelectMany(x => x.History.Skip(1).Where(h => h.State is 2 or 4 or 5 or 6).Select((h, k) => new
            {
                ID = x.ID * 10 + k, DisputeID = x.ID, PhoneNumber = "+97150*****" + (x.ID % 1000).ToString("000"),
                Message = $"Your ATM dispute DSP-{x.ID % 100000:00000} is now: {DisputeStates.First(s => s.Id == h.State).Name}.",
                TimeQueued = h.At, Sent = h.At < now.AddMinutes(-10), TimeSent = h.At < now.AddMinutes(-10) ? h.At.AddMinutes(2) : (DateTime?)null
            })));
        // Every other DisputeManagement call (create, update, delete, SMS writes, templates) is not used by the portal.
        return Resp(HttpStatusCode.NotImplemented, "{\"Message\":\"Demo KTC server: this dispute route is not simulated.\"}");
    }
}
