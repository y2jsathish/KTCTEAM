using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;

namespace Ktc.Infrastructure.Mock;

/// <summary>
/// In-process stand-in for the KTC REST API (Ktc:UseMock=true). Deterministic, time-aware sample data in KTC-shaped JSON so the
/// whole portal can be demonstrated and tested without access to a live KTC server. NOT used when UseMock=false.
/// </summary>
public sealed class MockKtcHttpHandler : HttpMessageHandler
{
    private static readonly string[] Banks = { "Brinks", "ADCB", "Emirates NBD" };
    private static readonly (string Region, string City)[] Places =
        { ("Abu Dhabi", "Abu Dhabi"), ("Abu Dhabi", "Al Ain"), ("Dubai", "Dubai"), ("Dubai", "Jebel Ali"), ("Northern Emirates", "Sharjah"), ("Northern Emirates", "Ajman"), ("Northern Emirates", "Ras Al Khaimah") };
    private static readonly string[] Types = { "NCR SelfServ 84", "Diebold Nixdorf CS 5550", "Hyosung MX8800", "GRG P2700" };
    // code id, name, level, text
    private static readonly (int Id, string Name, int Level)[] Codes =
    {
        (1, "Cash Handler - dispense failure", 2), (2, "Card reader fault", 3), (3, "Host disconnect - authorization host unreachable", 1),
        (4, "Receipt printer - paper low", 4), (5, "Communication failure - network link down", 1), (6, "Cash retract - note not taken", 3),
        (7, "Encrypting PIN pad error", 2), (8, "Low cash - cassette 2", 4)
    };
    private const int AtmCount = 60;
    private static readonly int[] Chronic = { 7, 19, 33, 41 };      // ATMs with recurring faults (for Root Cause Analytics)

    private static readonly JsonSerializerOptions J = new() { PropertyNamingPolicy = null };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        await Task.Delay(Random.Shared.Next(15, 90), ct);
        var path = req.RequestUri!.AbsolutePath.Trim('/');
        var q = HttpUtility.ParseQueryString(req.RequestUri.Query);
        var body = req.Content == null ? "" : await req.Content.ReadAsStringAsync(ct);
        var m = req.Method.Method;
        var now = DateTime.UtcNow;

        if (path.Equals("api/v1/Login", StringComparison.OrdinalIgnoreCase) || path.Equals("api/v1/ReLogin", StringComparison.OrdinalIgnoreCase))
        {
            using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var u = d.RootElement.TryGetProperty("Username", out var uu) ? uu.GetString() : null;
            var p = d.RootElement.TryGetProperty("Password", out var pp) ? pp.GetString() : null;
            if (string.IsNullOrEmpty(u) || string.IsNullOrEmpty(p) || p == "invalid") return Resp(HttpStatusCode.Unauthorized, "\"Invalid credentials\"");
            return Ok(new { access_token = "mock." + Convert.ToBase64String(Encoding.UTF8.GetBytes(u + now.Ticks)).Replace("=", ""), expires_in = 1200 });
        }
        if (path.Equals("api/v1/Logoff", StringComparison.OrdinalIgnoreCase)) return Resp(HttpStatusCode.OK, "true");
        if (!req.Headers.Contains("Authorization")) return Resp(HttpStatusCode.Unauthorized, "\"Missing bearer token\"");

        Match r;
        if (Is(path, "api/v1/Client", m == "GET")) return Ok(Enumerable.Range(0, AtmCount).Select(i => ClientJson(i)));
        if (Is(path, "api/v1/Business")) return Ok(Banks.Select((b, i) => new { ID = i + 1, Name = b }));
        if (Is(path, "api/v1/Region")) return Ok(Places.Select(p => p.Region).Distinct().Select((x, i) => new { ID = i + 1, Name = x }));
        if (Is(path, "api/v1/Group")) return Ok(new[] { new { ID = 1, Name = "Unknown Clients" }, new { ID = 2, Name = "All Clients" } });
        if (Is(path, "api/v1/Branch")) return Ok(Places.Select((p, i) => new { ID = i + 1, Name = p.City + " Branch", City = p.City, RegionID = Array.IndexOf(Places.Select(z => z.Region).Distinct().ToArray(), p.Region) + 1 }));
        if (Is(path, "api/v1/LastClientContact")) return Ok(Enumerable.Range(0, AtmCount).Select(i => new { ClientID = Id(i), LastContact = LastContact(i, now) }));
        if (Is(path, "api/v1/ActiveErrorExtra") || Is(path, "api/v1/ActiveError")) return Ok(Enumerable.Range(0, AtmCount).SelectMany(i => OpenIncidents(i, now)));
        if (Is(path, "api/v1/HistoricalError")) return Ok(Enumerable.Range(0, AtmCount).SelectMany(i => History(i, now)));
        if ((r = Rx(path, @"api/v1/Client/(\d+)/HistoricalError")).Success) return Ok(History(Idx(r), now));
        if ((r = Rx(path, @"api/v1/Client/(\d+)/OverallAvailability")).Success) return Ok(new { AvailabilityPercent = Availability(Idx(r), now) });
        if ((r = Rx(path, @"api/v1/Client/(\d+)/Top5UnavailableReasons")).Success)
            return Ok(Codes.Take(5).Select((c, k) => new { Reason = c.Name, Hours = Math.Round(Hash(Idx(r), k, 7) % 900 / 100.0 + 0.5, 1) }).OrderByDescending(x => x.Hours));
        if ((r = Rx(path, @"api/v1/Client/(\d+)/CurrentStatus")).Success) return Ok(HardwareStatus(Idx(r), now));
        if ((r = Rx(path, @"api/v1/Client/(\d+)/Counters")).Success) return Ok(Counters(Idx(r), now));
        if (Is(path, "api/v1/ComponentList")) return Ok(HwComponents.Select((c, k) => new { ID = k + 1, Name = c.Name }));
        if (Is(path, "api/v1/PropertyList")) return Ok(HwProps.Select((p, k) => new { ID = k + 1, Name = p }));
        if ((r = Rx(path, @"api/v1/Client/(\d+)/(EJRow|EJPlusTime)")).Success && m == "POST") return Ok(Ej(Idx(r), body, now));
        if ((r = Rx(path, @"api/v1/Client/(\d+)")).Success && m == "GET") return Ok(ClientJson(Idx(r)));
        if (Is(path, "api/v1/Ticket/Search") && m == "POST") return Ok(Enumerable.Range(0, AtmCount).SelectMany(i => Tickets(i, now)));
        if ((r = Rx(path, @"api/v1/Ticket/(\d+)/SLA")).Success) { var t = AllTickets(now).FirstOrDefault(x => x.ID == long.Parse(r.Groups[1].Value)); return t == null ? Resp(HttpStatusCode.NotFound, "null") : Ok(new[] { new { level = 1, startTime = t.TimeCreated, expectedEndTime = t.SLADueTime, endTime = t.TimeClosed } }); }
        if ((r = Rx(path, @"api/v1/Ticket/(\d+)")).Success && m == "GET") { var t = AllTickets(now).FirstOrDefault(x => x.ID == long.Parse(r.Groups[1].Value)); return t == null ? Resp(HttpStatusCode.NotFound, "null") : Ok(t); }
        if ((r = Rx(path, @"api/v1/Ticket/(\d+)")).Success && m == "PUT") return Resp(HttpStatusCode.OK, "true");
        if (Is(path, "api/v1/TransactionData/Search") && m == "POST") return Ok(Transactions(body, now));
        if ((r = Rx(path, @"api/v1/TransactionData/(\d+)")).Success) { var id = long.Parse(r.Groups[1].Value); return Ok(Transactions(null, now, id).FirstOrDefault()); }
        if (m == "GET") return Ok(new { mock = true, note = "Mock KTC server has no sample data for this route.", path });
        return Resp(HttpStatusCode.OK, "true");
    }

    // ------------- world ---------------
    private static int Id(int i) => 1000 + i;
    private static string AtmName(int i) => $"{Banks[i % 3][..3].ToUpper()}-ATM-{i + 1:000}";
    private static int Idx(Match m) => Math.Clamp(int.Parse(m.Groups[1].Value) - 1000, 0, AtmCount - 1);
    // Hardware: KTC CurrentStatus records {CompId, PropId, VID, ManualValue, NumericValue, UseNumeric, Timestamp}.
    private static readonly (string Name, string[] Good, string[] Bad)[] HwComponents =
    {
        ("Card Reader", new[] { "Online" }, new[] { "MediaJammed", "Offline" }),
        ("Cash Dispenser", new[] { "Online" }, new[] { "Cassette low", "Hardware error" }),
        ("Receipt Printer", new[] { "Online" }, new[] { "Paper low", "Paper out" }),
        ("Journal Printer", new[] { "Online" }, new[] { "Paper low" }),
        ("PIN Pad", new[] { "Online" }, new[] { "Offline" }),
        ("Sensors", new[] { "OK" }, new[] { "Safe door open" }),
    };
    private static readonly string[] HwProps = { "Device status", "Fatal error count", "Firmware version" };
    private static IEnumerable<object> HardwareStatus(int i, DateTime now)
    {
        for (int k = 0; k < HwComponents.Length; k++)
        {
            var c = HwComponents[k]; int h = Hash(i, k, 91); bool bad = Chronic.Contains(i) ? h % 100 < 45 : h % 100 < 10;
            var t = now.AddMinutes(-(h % 240));
            yield return new { CID = Id(i), CompId = k + 1, PropId = 1, VID = -1, ManualValue = bad ? c.Bad[h % c.Bad.Length] : c.Good[0], NumericValue = 0, UseNumeric = false, Timestamp = t };
            yield return new { CID = Id(i), CompId = k + 1, PropId = 2, VID = -1, ManualValue = "", NumericValue = bad ? 1 + h % 3 : 0, UseNumeric = true, Timestamp = t };
            yield return new { CID = Id(i), CompId = k + 1, PropId = 3, VID = -1, ManualValue = $"v{1 + h % 4}.{h % 10}", NumericValue = 0, UseNumeric = false, Timestamp = t.AddDays(-3) };
        }
    }

    /// <summary>Same "bad component" rule as <see cref="HardwareStatus"/>, so counters and hardware status agree.</summary>
    private static bool HwBad(int i, string component)
    {
        var k = Array.FindIndex(HwComponents, c => c.Name == component); if (k < 0) return false;
        var h = Hash(i, k, 91); return Chronic.Contains(i) ? h % 100 < 45 : h % 100 < 10;
    }

    // Counters (Ktc:CountersEndpoint): application counters (today / since reset) and CEN/XFS device counters with thresholds.
    // Plausible and stable per ATM and day: "today" grows through the day, "since reset" covers the days since the last replenishment.
    private static object Counters(int i, DateTime now)
    {
        var rng = new Random(Hash(i, now.DayOfYear, 77));
        var daysSinceReset = 1 + Hash(i, 13) % 9;
        var dayFraction = Math.Max(0.05, now.TimeOfDay.TotalHours / 24);
        var activity = IsOffline(i, now) ? 0.15 : 1.0;
        (long Today, long SinceReset) Count(double perDay)
        {
            var today = (long)Math.Round(perDay * dayFraction * activity * (0.75 + rng.NextDouble() * 0.5));
            var earlier = (long)Math.Round(perDay * daysSinceReset * (0.8 + rng.NextDouble() * 0.4));
            return (today, today + earlier);
        }
        var app = new List<object>();
        (long Today, long SinceReset) App(string category, string name, double perDay)
        { var c = Count(perDay); app.Add(new { Category = category, Name = name, c.Today, c.SinceReset }); return c; }

        App("Transactions", "Customer sessions", 260);
        var withdrawals = App("Transactions", "Cash withdrawals", 180);
        App("Transactions", "Balance inquiries", 55);
        App("Transactions", "Mini statements", 18);
        App("Transactions", "Fund transfers", 12);
        App("Transactions", "PIN changes", 4);
        App("Transactions", "Cash deposits", 35);
        App("Exceptions", "Declined by host", 21);
        App("Exceptions", "Host timeouts", HostDown(i, now) || IsOffline(i, now) ? 6 : 1.5);
        App("Exceptions", "Reversals", 2.5);
        App("Exceptions", "Cards captured", 0.8);
        App("Exceptions", "Cash retracts", 0.6);
        App("Operations", "Supervisor entries", 0.4);
        App("Operations", "Application restarts", Chronic.Contains(i) ? 2 : 0.2);

        var xfs = new List<object>();
        void Xfs(string device, string cls, string logical, string counter, long value, long? threshold = null) =>
            xfs.Add(new { Device = device, ServiceClass = cls, LogicalName = logical, Counter = counter, Value = value, Threshold = threshold });
        long N(double perDay) => Count(perDay).SinceReset;
        long Jams(string component) => HwBad(i, component) ? 1 + rng.Next(0, 3) : 0;

        Xfs("Card Reader", "IDC", "CardReader1", "Cards inserted", N(250));
        Xfs("Card Reader", "IDC", "CardReader1", "Read failures", N(HwBad(i, "Card Reader") ? 9 : 2.5), 50);
        Xfs("Card Reader", "IDC", "CardReader1", "Cards retained", N(0.8), 20);
        Xfs("Card Reader", "IDC", "CardReader1", "Media jams", Jams("Card Reader"), 3);

        var dispensed = (long)(withdrawals.SinceReset * 3.2);
        Xfs("Cash Dispenser", "CDM", "CurrencyDispenser1", "Dispense operations", withdrawals.SinceReset);
        Xfs("Cash Dispenser", "CDM", "CurrencyDispenser1", "Notes dispensed", dispensed);
        Xfs("Cash Dispenser", "CDM", "CurrencyDispenser1", "Notes rejected", (long)(dispensed * (0.006 + rng.NextDouble() * 0.01)), 200);
        Xfs("Cash Dispenser", "CDM", "CurrencyDispenser1", "Pick failures", N(HwBad(i, "Cash Dispenser") ? 6 : 1.2), 40);
        Xfs("Cash Dispenser", "CDM", "CurrencyDispenser1", "Retract operations", N(0.6), 10);
        Xfs("Cash Dispenser", "CDM", "CurrencyDispenser1", "Notes retracted", N(1.4));
        Xfs("Cash Dispenser", "CDM", "CurrencyDispenser1", "Purges", N(0.3));

        Xfs("Cash Acceptor", "CIM", "CashAcceptor1", "Cash-in transactions", N(35));
        Xfs("Cash Acceptor", "CIM", "CashAcceptor1", "Notes accepted", N(140));
        Xfs("Cash Acceptor", "CIM", "CashAcceptor1", "Notes refused", N(Chronic.Contains(i) ? 25 : 6), 150);
        Xfs("Cash Acceptor", "CIM", "CashAcceptor1", "Suspect / counterfeit notes", N(0.2), 5);

        Xfs("Receipt Printer", "PTR", "ReceiptPrinter1", "Receipts printed", N(170));
        Xfs("Receipt Printer", "PTR", "ReceiptPrinter1", "Paper low events", N(0.3));
        Xfs("Receipt Printer", "PTR", "ReceiptPrinter1", "Media jams", Jams("Receipt Printer"), 3);
        Xfs("Journal Printer", "PTR", "JournalPrinter1", "Lines printed", N(2600));
        Xfs("Journal Printer", "PTR", "JournalPrinter1", "Media jams", Jams("Journal Printer"), 3);

        Xfs("EPP / PIN Pad", "PIN", "Pinpad1", "PIN entries", N(240));
        Xfs("EPP / PIN Pad", "PIN", "Pinpad1", "PIN block failures", N(HwBad(i, "PIN Pad") ? 4 : 0.3), 10);
        Xfs("EPP / PIN Pad", "PIN", "Pinpad1", "Tamper events", HwBad(i, "PIN Pad") ? 1 : 0, 1);

        Xfs("Sensors", "SIU", "SensorsAndIndicators1", "Safe door opens", daysSinceReset / 3 + 1);
        Xfs("Sensors", "SIU", "SensorsAndIndicators1", "Cabinet door opens", N(0.5));
        Xfs("Sensors", "SIU", "SensorsAndIndicators1", "Tamper alarms", HwBad(i, "Sensors") ? 1 : 0, 1);

        return new
        {
            ClientID = Id(i), CollectedUtc = LastContact(i, now), LastResetUtc = now.Date.AddDays(-daysSinceReset).AddHours(6),
            Application = app, Xfs = xfs
        };
    }

    private static int Hash(int a, int b = 0, int c = 0) { unchecked { var h = 17; h = h * 31 + a; h = h * 31 + b; h = h * 31 + c; h ^= h >> 15; h *= 0x2c1b3c6d; h ^= h >> 12; return Math.Abs(h); } }
    private static object ClientJson(int i) => new
    {
        ID = Id(i), Name = $"{Banks[i % 3][..3].ToUpper()}-ATM-{i + 1:000}", Alias = $"{Places[i % Places.Length].City} Mall #{i + 1}",
        Address = $"10.{20 + i % 3}.{i / 3}.{10 + i}", BusinessID = i % 3 + 1, BranchID = i % Places.Length + 1,
        RegionID = Array.IndexOf(Places.Select(p => p.Region).Distinct().ToArray(), Places[i % Places.Length].Region) + 1,
        HardwareTypeName = Types[i % Types.Length], Active = true, Deleted = false
    };
    private static int Bucket(DateTime now) => (int)(now.Ticks / TimeSpan.TicksPerHour / 3);       // state changes every 3 hours
    private static bool IsOffline(int i, DateTime now) => Hash(i, Bucket(now), 1) % 100 < 7;
    private static bool HostDown(int i, DateTime now) => Hash(i, Bucket(now), 5) % 100 < 5 && !IsOffline(i, now);
    private static DateTime LastContact(int i, DateTime now) =>
        IsOffline(i, now) ? now.AddMinutes(-(90 + Hash(i, 3) % 400)) : Hash(i, Bucket(now), 2) % 100 < 12 ? now.AddMinutes(-(12 + Hash(i, 4) % 40)) : now.AddSeconds(-(10 + Hash(i, now.Minute) % 280));
    private static double Availability(int i, DateTime now) => IsOffline(i, now) ? 91.5 + Hash(i) % 40 / 10.0 : Chronic.Contains(i) ? 96.0 + Hash(i) % 20 / 10.0 : 99.0 + Hash(i) % 10 / 10.0;

    private static IEnumerable<object> OpenIncidents(int i, DateTime now)
    {
        var b = Bucket(now);
        var list = new List<(int code, int ageMin)>();
        if (IsOffline(i, now)) list.Add((5, 120 + Hash(i, 9) % 300));
        if (HostDown(i, now)) list.Add((3, 20 + Hash(i, 8) % 90));
        if (Chronic.Contains(i) && Hash(i, b, 3) % 100 < 70) list.Add((Chronic[0] == i ? 1 : 6, 30 + Hash(i, 6) % 700));
        if (Hash(i, b, 4) % 100 < 22) list.Add((Codes[Hash(i, b, 6) % Codes.Length].Id, 15 + Hash(i, b, 9) % 900));
        foreach (var (code, age) in list.DistinctBy(x => x.code))
        {
            var c = Codes.First(x => x.Id == code);
            yield return new { ID = IncId(i, code, b), ClientID = Id(i), ClientName = AtmName(i), ErrorCodeTypeID = c.Id, ErrorCodeTypeName = c.Name, Level = c.Level, TimeAdded = now.AddMinutes(-age), TimeClosed = (DateTime?)null, Description = c.Name };
        }
    }
    private static long IncId(int i, int code, int b) => 500000L + i * 1000 + code * 10 + b % 10;

    private static IEnumerable<object> History(int i, DateTime now)
    {
        for (int d = 1; d <= 30; d++)
        {
            int p = Chronic.Contains(i) ? 45 : 4;
            if (Hash(i, d, 11) % 100 >= p) continue;
            var c = Chronic.Contains(i) ? Codes[Array.IndexOf(Chronic, i) % 3 == 0 ? 0 : Array.IndexOf(Chronic, i) == 1 ? 5 : 2] : Codes[Hash(i, d, 12) % Codes.Length];
            var start = now.Date.AddDays(-d).AddHours(Hash(i, d, 13) % 20);
            yield return new { ID = 400000L + i * 100 + d, ClientID = Id(i), ErrorCodeTypeID = c.Id, ErrorCodeTypeName = c.Name, Level = c.Level, TimeAdded = start, TimeClosed = (DateTime?)start.AddMinutes(30 + Hash(i, d, 14) % 600), Description = c.Name };
        }
    }

    private record MockTicket(long ID, int ClientID, string ClientName, int StatusByte, DateTime TimeCreated, DateTime TimeUpdated, DateTime? TimeOwned, DateTime? TimeDispatched, DateTime? TimeClosed, string? OwnerName, string Description, int Level, DateTime SLADueTime);
    private static readonly string[] Owners = { "a.rahman", "j.thomas", "m.khan", "s.nair", "l.fernandez" };
    private static IEnumerable<MockTicket> Tickets(int i, DateTime now)
    {
        var b = Bucket(now);
        // one open ticket per open incident (L1/L2 always, others 60%)
        foreach (dynamic inc in OpenIncidents(i, now))
        {
            int level = inc.Level; int code = inc.ErrorCodeTypeID; DateTime opened = inc.TimeAdded;
            if (level > 2 && Hash(i, code, 21) % 100 > 60) continue;
            int stage = Hash(i, code, 22) % 4;     // 0 open 1 owned 2 dispatched 3 resolved
            var created = opened.AddMinutes(3);
            var sla = created.AddHours(level switch { 1 => 4, 2 => 8, 3 => 24, _ => 72 }) ;
            // make a few tickets near breach / breached for realism
            if (Hash(i, code, 23) % 100 < 18) sla = now.AddMinutes(20 + Hash(i, code, 24) % 40);
            if (Hash(i, code, 25) % 100 < 8) sla = now.AddMinutes(-(10 + Hash(i, code, 26) % 120));
            yield return new MockTicket(700000L + i * 100 + code, Id(i), (string)inc.ClientName, stage == 3 ? 4 : stage + 1, created, now.AddMinutes(-5),
                stage >= 1 ? created.AddMinutes(10) : null, stage >= 2 ? created.AddMinutes(25) : null, stage == 3 ? now.AddMinutes(-3) : null,
                stage >= 1 ? Owners[Hash(i, code) % Owners.Length] : null, (string)inc.Description, level, sla);
        }
        // closed tickets in last 3 days
        for (int d = 0; d < 3; d++)
            if (Hash(i, d, 31) % 100 < 14)
            {
                var c = Codes[Hash(i, d, 32) % Codes.Length];
                var cr = now.Date.AddDays(-d).AddHours(Hash(i, d, 33) % 12 + 1);
                var due = cr.AddHours(c.Level switch { 1 => 4, 2 => 8, 3 => 24, _ => 72 });
                var closed = Hash(i, d, 34) % 100 < 12 ? due.AddMinutes(45) : cr.AddHours(1 + Hash(i, d, 35) % 3);
                yield return new MockTicket(600000L + i * 100 + d * 10 + c.Id, Id(i), $"{Banks[i % 3][..3].ToUpper()}-ATM-{i + 1:000}", 5, cr, closed, cr.AddMinutes(5), cr.AddMinutes(20), closed, Owners[Hash(i, d) % Owners.Length], c.Name, c.Level, due);
            }
    }
    private static IEnumerable<MockTicket> AllTickets(DateTime now) => Enumerable.Range(0, AtmCount).SelectMany(i => Tickets(i, now));

    private static IEnumerable<object> Transactions(string? body, DateTime now, long onlyId = 0)
    {
        DateTime start = now.Date, end = now; int business = 0;
        if (!string.IsNullOrWhiteSpace(body))
            try
            {
                using var d = JsonDocument.Parse(body);
                if (d.RootElement.TryGetProperty("Start", out var s) && s.ValueKind == JsonValueKind.String) start = s.GetDateTime().ToUniversalTime();
                if (d.RootElement.TryGetProperty("End", out var e) && e.ValueKind == JsonValueKind.String) end = e.GetDateTime().ToUniversalTime();
                if (d.RootElement.TryGetProperty("BusinessID", out var bz) && bz.ValueKind == JsonValueKind.Number) business = bz.GetInt32();
            }
            catch { }
        if (end > now) end = now;
        if (end - start > TimeSpan.FromDays(8)) start = end.AddDays(-8);
        var types = new[] { "Cash Withdrawal", "Cash Withdrawal", "Cash Withdrawal", "Balance Inquiry", "Deposit", "Transfer", "Reversal" };
        var fails = new[] { "Host timeout - no response from authorization host", "Host declined - issuer unavailable", "Communication failure - connection lost", "Cash retract - note not taken by customer", "Dispense failure - cassette jam", "Card reader error" };
        var rows = new List<object>();
        var hour = new DateTime(start.Year, start.Month, start.Day, start.Hour, 0, 0, DateTimeKind.Utc);
        for (; hour < end; hour = hour.AddHours(1))
        {
            int hi = (int)(hour.Ticks / TimeSpan.TicksPerHour);
            int weight = hour.Hour is >= 6 and <= 20 ? 2 : 1;
            for (int i = 0; i < AtmCount; i++)
            {
                if (business > 0 && i % 3 + 1 != business) continue;
                int n = Hash(i, hi, 41) % (weight * 3 + 1);
                if (IsOffline(i, hour)) n = 0;
                for (int k = 0; k < n; k++)
                {
                    long id = (long)hi * 100000 + i * 100 + k;
                    if (onlyId != 0 && id != onlyId) continue;
                    var t = hour.AddSeconds(Hash(i, hi, 50 + k) % 3600);
                    if (t > end || t < start) continue;
                    int h = Hash(i, hi, 60 + k);
                    int failPct = Chronic.Contains(i) ? 28 : hour.Hour == 14 && now.Date == hour.Date ? 9 : 4;
                    bool fail = h % 100 < failPct;
                    var type = types[h / 7 % types.Length];
                    var f = fails[(h / 13 + (Chronic.Contains(i) ? 3 : 0)) % fails.Length];
                    rows.Add(new
                    {
                        ID = id, UUID = Guid.NewGuid().ToString("D"), ClientID = Id(i), ClientName = $"{Banks[i % 3][..3].ToUpper()}-ATM-{i + 1:000}",
                        TransactionTypeName = type, CompletionCodeName = fail ? f : "Completed OK", ReasonName = fail ? f : (string?)null, ErrorCode = fail ? $"E{100 + h % 60}" : "",
                        Amount = type == "Cash Withdrawal" || type == "Deposit" ? (decimal)(100 * (1 + h % 20)) : 0m, Currency = "AED",
                        StartTime = t, EndTime = t.AddMilliseconds(fail ? 4000 + h % 9000 : 900 + h % 3200)
                    });
                    if (rows.Count > 6000) return rows;
                }
            }
        }
        return rows;
    }

    private static readonly string[] Pans = { "4111111111111111", "5500005555555559", "4000056655665556", "5105105105105100", "378282246310005" };
    private static IEnumerable<object> Ej(int i, string body, DateTime now)
    {
        DateTime start = now.AddHours(-12), end = now;
        try
        {
            using var d = JsonDocument.Parse(body);
            if (d.RootElement.TryGetProperty("Value1", out var s) && s.ValueKind == JsonValueKind.String) start = s.GetDateTime().ToUniversalTime();
            if (d.RootElement.TryGetProperty("Value2", out var e) && e.ValueKind == JsonValueKind.String) end = e.GetDateTime().ToUniversalTime();
        }
        catch { }
        if (end > now) end = now; if (end - start > TimeSpan.FromDays(3)) start = end.AddDays(-3);
        var rows = new List<object>(); long seq = 100000 + (long)(start.Ticks / TimeSpan.TicksPerHour) % 50000 * 10;
        var cursor = start;
        for (int n = 0; cursor < end && rows.Count < 600; n++)
        {
            cursor = cursor.AddMinutes(4 + Hash(i, n, 71) % 40); if (cursor > end) break;
            var pan = Pans[Hash(i, n, 72) % Pans.Length]; bool fail = Hash(i, n, 73) % 100 < 12; var amt = 100 * (1 + Hash(i, n, 74) % 20);
            var lines = new List<string> { "TRANSACTION START", $"CARD INSERTED  PAN: {pan}", "PIN ENTERED", "SERVICE: CASH WITHDRAWAL", $"AMOUNT REQUESTED: AED {amt}.00", "HOST REQUEST SENT" };
            if (fail) { lines.Add("HOST RESPONSE: TIMEOUT (NO RESPONSE)"); lines.Add("TRANSACTION FAILED - REVERSAL SENT"); lines.Add("CARD RETURNED"); }
            else { lines.Add("HOST RESPONSE: APPROVED 00"); lines.Add($"NOTES DISPENSED: AED {amt}.00"); lines.Add("NOTES TAKEN"); lines.Add("CARD RETURNED"); lines.Add("TRANSACTION END - SUCCESS"); }
            int k = 0;
            foreach (var l in lines) rows.Add(new { ID = ++seq, Sequence = seq, TimeStamp = cursor.AddSeconds(k++ * 3), Text = l });
        }
        return rows;
    }

    // ------------- helpers -------------
    private static bool Is(string path, string route, bool cond = true) => cond && path.Equals(route, StringComparison.OrdinalIgnoreCase);
    private static Match Rx(string path, string pattern) => Regex.Match(path, "^" + pattern + "$", RegexOptions.IgnoreCase);
    private static HttpResponseMessage Ok(object? o) => Resp(HttpStatusCode.OK, JsonSerializer.Serialize(o, J));
    private static HttpResponseMessage Resp(HttpStatusCode code, string json) => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
