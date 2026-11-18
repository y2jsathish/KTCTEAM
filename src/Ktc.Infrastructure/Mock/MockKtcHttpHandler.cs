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
    private static readonly (double Lat, double Lon)[] Coords = { (24.4539, 54.3773), (24.2075, 55.7447), (25.2048, 55.2708), (25.0118, 55.1121), (25.3463, 55.4209), (25.4052, 55.5136), (25.8007, 55.9762) };
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
        if (Is(path, "api/v1/CommandType", m == "GET")) return Ok(CommandTypes);
        if (Is(path, "api/v1/Action/ClientIDsPIP", m == "GET"))
            return Ok(MockActions.Values.Where(a => a.CommandId.ToString() == q["commandtypeID"] && a.StatusAt(now) is 0 or 1).Select(a => a.ClientId).Distinct());
        if ((r = Rx(path, @"api/v1/Client/(\d+)/Actions")).Success && m == "POST") return Ok(AddAction(int.Parse(r.Groups[1].Value), body, now));
        if ((r = Rx(path, @"api/v1/Client/(\d+)/Actions")).Success && m == "GET")
        {
            var cid = int.Parse(r.Groups[1].Value); SeedActions(cid, now);
            var days = int.TryParse(q["numdays"], out var nd) ? nd : 7;
            return Ok(MockActions.Values.Where(a => a.ClientId == cid && a.Added >= now.AddDays(-days)).OrderByDescending(a => a.Added).Select(a => a.Json(now)));
        }
        if ((r = Rx(path, @"api/v1/Action/(\d+)/UploadedDirectoryListing")).Success)
            return MockActions.TryGetValue(long.Parse(r.Groups[1].Value), out var la) && la.Files && la.StatusAt(now) == 2
                ? Ok(new object[] { new { Name = $"EJ_{la.Added:yyyyMMdd}.txt", Size = 18432, Modified = la.Added }, new { Name = "KALDiag.log", Size = 5120, Modified = la.Added }, new { Name = $"Logs_{la.Added:yyyyMMdd}.zip", Size = 734003, Modified = la.Added } })
                : Ok(Array.Empty<object>());
        if ((r = Rx(path, @"api/v1/Action/(\d+)/UploadedFile")).Success)
        {
            if (!MockActions.TryGetValue(long.Parse(r.Groups[1].Value), out var fa) || !fa.Files) return Resp(HttpStatusCode.NotFound, "{\"Message\":\"No uploaded file\"}");
            var fn = q["filename"] ?? "";
            var bytes = fn.EndsWith(".zip") ? new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0, 0, 0, 8, 0, 0x21, 0x00, 0x7F, 0x80, 0x00, 0x00, 1, 2, 3 } : Encoding.UTF8.GetBytes(SampleFile(fn, fa));
            return Ok(new { FileName = fn, Data = Convert.ToBase64String(bytes) });
        }
        if ((r = Rx(path, @"api/v1/Action/(\d+)")).Success && m == "GET")
            return MockActions.TryGetValue(long.Parse(r.Groups[1].Value), out var ga) ? Ok(ga.Json(now)) : Resp(HttpStatusCode.NotFound, "{\"Message\":\"No such action\"}");
        if (Is(path, "api/v1/Client", m == "GET")) return Ok(Enumerable.Range(0, AtmCount).Select(i => ClientJson(i)));
        if (Is(path, "api/v1/Business")) return Ok(Banks.Select((b, i) => new { ID = i + 1, Name = b }));
        if (Is(path, "api/v1/Region")) return Ok(Places.Select(p => p.Region).Distinct().Select((x, i) => new { ID = i + 1, Name = x }));
        if (Is(path, "api/v1/Group")) return Ok(new[] { new { ID = 1, Name = "Unknown Clients" }, new { ID = 2, Name = "All Clients" } });
        if (Is(path, "api/v1/Branch")) return Ok(Places.Select((p, i) => new { ID = i + 1, Name = p.City + " Branch", City = p.City, Address = $"{10 + i} Main Street", PostCode = i % 2 == 0 ? $"P.O. Box {1000 + i * 7}" : "", RegionID = Array.IndexOf(Places.Select(z => z.Region).Distinct().ToArray(), p.Region) + 1 }));
        if (Is(path, "api/v1/LastClientContact")) return Ok(Enumerable.Range(0, AtmCount).Select(i => new { ClientID = Id(i), LastContact = Rebooted.TryGetValue(Id(i), out var rb) && now >= rb && now < rb.AddSeconds(RebootDownSeconds) ? rb.AddSeconds(-2) : Rebooted.ContainsKey(Id(i)) ? now.AddSeconds(-3) : LastContact(i, now) }));
        if (Is(path, "api/v1/ActiveErrorExtra") || Is(path, "api/v1/ActiveError")) return Ok(Enumerable.Range(0, AtmCount).SelectMany(i => OpenIncidents(i, now)));
        if (Is(path, "api/v1/HistoricalError")) return Ok(Enumerable.Range(0, AtmCount).SelectMany(i => History(i, now)));
        if ((r = Rx(path, @"api/v1/Client/(\d+)/HistoricalError")).Success) return Ok(History(Idx(r), now));
        if ((r = Rx(path, @"api/v1/Client/(\d+)/OverallAvailability")).Success) return Ok(new { AvailabilityPercent = Availability(Idx(r), now) });
        if ((r = Rx(path, @"api/v1/Client/(\d+)/Top5UnavailableReasons")).Success)
            return Ok(Codes.Take(5).Select((c, k) => new { Reason = c.Name, Hours = Math.Round(Hash(Idx(r), k, 7) % 900 / 100.0 + 0.5, 1) }).OrderByDescending(x => x.Hours));
        if ((r = Rx(path, @"api/v1/Client/(\d+)/CurrentStatus")).Success) return Ok(HardwareStatus(Idx(r), now));
        if ((r = Rx(path, @"api/v1/Client/(\d+)/CurrentCashUnitStatus")).Success) return Ok(CashUnits(Idx(r), now));
        if (Is(path, "api/v1/CashUnitType")) return Ok(new[] { new { ID = 21, Name = "Recycling" }, new { ID = 22, Name = "Dispense" }, new { ID = 23, Name = "Cash in" }, new { ID = 24, Name = "Reject" } });
        if (Is(path, "api/v1/CashUnitStatus")) return Ok(new[] { new { ID = 1, Name = "OK" }, new { ID = 2, Name = "Low" }, new { ID = 3, Name = "Empty" }, new { ID = 4, Name = "Full" } });
        if (Rx(path, @"api/v1/Client/(\d+)/ReplenishmentOrders").Success) return Ok(Array.Empty<object>());
        if ((r = Rx(path, @"api/v1/Client/(\d+)/PhysicalCassettes")).Success)
            return Ok(new[] { 100, 200, 500, 1000 }.Select((v, k) => new { ClientID = Id(Idx(r)), CassetteNumber = k + 1, Denomination = v, Currency = "AED", Capacity = 2500, Inserted = true }));
        if ((r = Rx(path, @"api/v1/Client/(\d+)/Counters")).Success) return Ok(KtcCounters(Idx(r), now));
        if (Is(path, "api/v1/HistoricalCounter/ClientTransactionCounters")) return Ok(long.TryParse(q["transactionid"], out var txc) ? TransactionCounters(txc).ToList() : new List<object>());
        if (Is(path, "api/v1/ComponentList")) return Ok(HwComponents.Select((c, k) => new { ID = k + 1, Name = c.Name }));
        if (Is(path, "api/v1/Currency")) return Ok(new[] { new { ID = 1, Code = "AED", Description = "UAE Dirham" } });
        if (Is(path, "api/v1/Denomination")) return Ok(Notes.Select((v, k) => new { ID = k + 1, CurrencyID = 1, CurrencyValue = (decimal)v }));
        if (Is(path, "api/v1/PropertyList")) return Ok(AllProps.Select((p, k) => new { ID = k + 1, Name = p }));
        if ((r = Rx(path, @"api/v1/Client/(\d+)/(EJRow|EJPlusTime)")).Success && m == "POST") return Ok(Ej(Idx(r), body, now));
        if ((r = Rx(path, @"api/v1/Client/(\d+)")).Success && m == "GET") return Ok(ClientJson(Idx(r)));
        if (Is(path, "api/v1/Ticket/Search") && m == "POST") return Ok(Enumerable.Range(0, AtmCount).SelectMany(i => Tickets(i, now)));
        if (Is(path, "api/v1/TroubleTicketType")) return Ok(TicketTypes());
        if (Is(path, "api/v1/ErrorCodeType")) return Ok(ErrorCodeTypes());
        if ((r = Rx(path, @"api/v1/Ticket/(\d+)/Children")).Success) return Ok(Array.Empty<object>());
        if ((r = Rx(path, @"api/v1/HistoricalError/(\d+)")).Success)
        {
            var hid = long.Parse(r.Groups[1].Value); var t = AllTickets(now).FirstOrDefault(x => x.ErrorID == hid);
            return t == null ? Resp(HttpStatusCode.NotFound, "null") : Ok(new { ID = hid, ClientID = t.ClientID, ClientName = t.ClientName, ErrorCodeTypeID = t.ErrorCodeTypeID, ErrorCode = MockCode(t.ErrorCodeTypeID),
                OccurrenceTime = t.TimeCreated.AddMinutes(-4), ReceivedTime = t.TimeCreated.AddMinutes(-4), TimeClosed = t.TimeClosed?.AddSeconds(-1), CountsAsDowntime = t.Level <= 2 });
        }
        if ((r = Rx(path, @"api/v1/Ticket/(\d+)/SLA")).Success) { var t = AllTickets(now).FirstOrDefault(x => x.ID == long.Parse(r.Groups[1].Value)); return t == null ? Resp(HttpStatusCode.NotFound, "null") : Ok(t.SLADueTime == null ? Array.Empty<object>() : new object[] { new { level = 1, startTime = t.TimeCreated, expectedEndTime = t.SLADueTime, endTime = t.TimeClosed } }); }
        if ((r = Rx(path, @"api/v1/Ticket/(\d+)")).Success && m == "GET") { var t = AllTickets(now).FirstOrDefault(x => x.ID == long.Parse(r.Groups[1].Value)); return t == null ? Resp(HttpStatusCode.NotFound, "null") : Ok(t); }
        if ((r = Rx(path, @"api/v1/Ticket/(\d+)")).Success && m == "PUT") return Resp(HttpStatusCode.OK, "true");
        if (Is(path, "api/v1/TransactionData/Search") && m == "POST") return Ok(Transactions(body, now));
        if ((r = Rx(path, @"api/v1/TransactionData/(\d+)")).Success) { var id = long.Parse(r.Groups[1].Value); return Ok(Transactions(null, now, id).FirstOrDefault()); }
        // Transaction details, as KTC's UI reads them when a transaction is opened.
        if (Is(path, "api/v1/TransactionData/AllTransactionsWithSessionID") && long.TryParse(q["sessionid"], out var sess))
            return Ok(new[] { sess * 2, sess * 2 + 1 }.SelectMany(x => Transactions(null, now, x)).Select((t, k) => (k, t)).ToDictionary(x => x.k.ToString(), x => x.t));
        if ((r = Rx(path, @"api/v1/SessionData/(\d+)")).Success) { var sid = long.Parse(r.Groups[1].Value); var first = Transactions(null, now, sid * 2).Concat(Transactions(null, now, sid * 2 + 1)).FirstOrDefault();
            return first == null ? Resp(HttpStatusCode.NotFound, "null") : Ok(new { ID = sid, StartTime = ((dynamic)first).StartTime, LanguageLookupID = 20, InterfaceLookupID = 59, ClientID = ((dynamic)first).ClientID }); }
        if ((r = Rx(path, @"api/v1/SessionData/(\d+)/FieldsFromSession")).Success)
            return Ok(new Dictionary<string, object> { ["5"] = new { ID = 1, STXFieldID = 5, Value = "20", Type = "LookupString" }, ["6"] = new { ID = 2, STXFieldID = 6, Value = "59", Type = "LookupString" } });
        if ((r = Rx(path, @"api/v1/TransactionData/([0-9a-fA-F-]{36})/FieldsFromTransaction")).Success) return Ok(TxFields(TxIdFromUuid(r.Groups[1].Value), now));
        if (Is(path, "api/v1/STXField")) return Ok(StxFields.Select((f, k) => (k, f)).ToDictionary(x => (x.k + 1).ToString(), x => new { ID = x.k + 1, Name = x.f }));
        if (Is(path, "api/v1/STXFieldLookup")) return Ok(new Dictionary<string, object> { ["20"] = new { ID = 20, Value = "en" }, ["22"] = new { ID = 22, Value = "AED" }, ["59"] = new { ID = 59, Value = "TOUCH" } });
        if (Is(path, "api/v1/EJData/EJDataForClientBetweenIDs") && long.TryParse(q["startid"], out var ejStart)) return Ok(TxEj(q["clientid"], ejStart, now));
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
        ("Cash Acceptor", new[] { "Online" }, new[] { "Notes refused - cassette full" }),
        ("Sensors", new[] { "OK" }, new[] { "Safe door open" }),
        ("Cheque Scanner", new[] { "Online" }, new[] { "Item jam" }),
    };
    // Counter properties per component (name, per-day rate, rate when the component is faulty). Like KTC, the device reports them
    // as numeric CurrentStatus properties; the portal reads them as XFS counters.
    private static readonly Dictionary<string, (string Name, double PerDay, double Bad)[]> HwCounters = new()
    {
        ["Card Reader"] = new[] { ("Cards inserted", 250.0, 250.0), ("Read failures", 2.5, 9), ("Cards retained", 0.8, 3), ("Media jams", 0, 0.4) },
        ["Cash Dispenser"] = new[] { ("Dispense operations", 180.0, 180.0), ("Notes dispensed", 576, 576), ("Notes rejected", 6, 30), ("Pick failures", 1.2, 6), ("Retract operations", 0.6, 2), ("Purges", 0.3, 0.3) },
        ["Cash Acceptor"] = new[] { ("Cash-in transactions", 35.0, 35.0), ("Notes accepted", 140, 140), ("Notes refused", 6, 25), ("Suspect notes", 0.2, 0.8) },
        ["Receipt Printer"] = new[] { ("Receipts printed", 170.0, 170.0), ("Media jams", 0, 0.4) },
        ["Journal Printer"] = new[] { ("Lines printed", 2600.0, 2600.0), ("Media jams", 0, 0.4) },
        ["PIN Pad"] = new[] { ("PIN entries", 240.0, 240.0), ("PIN block failures", 0.3, 1.5), ("Tamper events", 0, 0.2) },
        ["Sensors"] = new[] { ("Safe door opens", 0.4, 0.4), ("Cabinet door opens", 0.5, 0.5), ("Tamper alarms", 0, 0.2) },
    };
    private static readonly string[] HwProps = new[] { "Device status", "Fatal error count", "Firmware version" }
        .Concat(HwCounters.Values.SelectMany(v => v.Select(c => c.Name))).Distinct().ToArray();
    private static DateTime LastReplenish(int i, DateTime now) { var t = now.Date.AddDays(-(Hash(i, 13) % 6)).AddHours(6).AddMinutes(Hash(i, 14) % 50); return t > now ? t.AddDays(-1) : t; }
    private static IEnumerable<object> HardwareStatus(int i, DateTime now)
    {
        for (int k = 0; k < HwComponents.Length; k++)
        {
            var c = HwComponents[k]; int h = Hash(i, k, 91); bool bad = Chronic.Contains(i) ? h % 100 < 45 : h % 100 < 10;
            var t = now.AddMinutes(-(h % 240));
            yield return new { CID = Id(i), CompId = k + 1, PropId = 1, VID = -1, ManualValue = bad ? c.Bad[h % c.Bad.Length] : c.Good[0], NumericValue = 0, UseNumeric = false, Timestamp = t };
            yield return new { CID = Id(i), CompId = k + 1, PropId = 2, VID = -1, ManualValue = "", NumericValue = bad ? 1 + h % 3 : 0, UseNumeric = true, Timestamp = t };
            yield return new { CID = Id(i), CompId = k + 1, PropId = 3, VID = -1, ManualValue = $"v{1 + h % 4}.{h % 10}", NumericValue = 0, UseNumeric = false, Timestamp = t.AddDays(-3) };
            if (!HwCounters.TryGetValue(c.Name, out var counters)) continue;
            var days = Math.Max(0.1, (now - LastReplenish(i, now)).TotalDays);
            for (int j = 0; j < counters.Length; j++)
            {
                var (name, perDay, badRate) = counters[j];
                var v = Math.Round((bad ? badRate : perDay) * days * (0.8 + Hash(i, k, 200 + j) % 40 / 100.0));
                if (name == "Media jams" && bad) v = Math.Max(v, 1 + h % 3);
                if (name.Contains("Tamper") && bad) v = Math.Max(v, 1);
                yield return new { CID = Id(i), CompId = k + 1, PropId = Array.IndexOf(HwProps, name) + 1, VID = -1, ManualValue = "", NumericValue = v, UseNumeric = true, Timestamp = t };
            }
        }
    }

    /// <summary>Same "bad component" rule as <see cref="HardwareStatus"/>, so counters and hardware status agree.</summary>
    private static bool HwBad(int i, string component)
    {
        var k = Array.FindIndex(HwComponents, c => c.Name == component); if (k < 0) return false;
        var h = Hash(i, k, 91); return Chronic.Contains(i) ? h % 100 < 45 : h % 100 < 10;
    }

    // KTC GET Client/{id}/Counters: List<KTCCounter> - client id, component DBName, counter name, value, (delta). Names as the real
    // KTC reports them (seen in KTC UI logs). The JSON field names are a guess; the portal reads the record by content.
    private static readonly (string Comp, string Name, double PerDay, double Bad)[] KtcCounterDefs =
    {
        ("KXDevice.KXCardReader", "GoodReads", 120, 120), ("KXDevice.KXCardReader", "GoodChipReads", 200, 200), ("KXDevice.KXCardReader", "BadReads", 3, 12),
        ("KXDevice.KXCardReader", "BadChipReads", 1, 6), ("KXDevice.KXCardReader", "Captured", 0.3, 4),
        ("KXDevice.KXCashDispenser", "Dispensed", 560, 560), ("KXDevice.KXCashDispenser", "Rejected", 5, 40), ("KXDevice.KXCashDispenser", "Retracted", 1, 4),
        ("KXDevice.KXCashDispenser", "RetractOperations", 0.5, 3), ("KXDevice.KXCashDispenser", "Remaining", 0, 0),
        ("KXDevice.KXCashAcceptor", "Deposited", 140, 140), ("KXDevice.KXCashAcceptor", "Retained", 1, 6), ("KXDevice.KXCashAcceptor", "Returned", 6, 25), ("KXDevice.KXCashAcceptor", "RetainedOperations", 0.3, 2),
        ("KXDevice.KXReceiptPrinter", "GoodPrints", 170, 170), ("KXDevice.KXReceiptPrinter", "PrintErrors", 0.5, 6),
        ("KXDevice.KXBundleCheckScanner", "ItemsAccepted", 25, 25), ("KXDevice.KXBundleCheckScanner", "ItemsRejected", 0.5, 5),
        ("KXDevice.KXBundleCheckScanner", "ItemsRetracted", 0.2, 2), ("KXDevice.KXBundleCheckScanner", "ItemsReturned", 1, 4),
    };

    // KTC GET HistoricalCounter/ClientTransactionCounters?clientid=&transactionid=, as the live KTC answers: {ClientID, ComponentID, PropertyID,
    // DenominationID, Timestamp, Value (running), Delta (this transaction), Reset, TransactionID}, here per denomination. Decoded from the mock transaction id.
    private static readonly int[] Notes = { 1000, 500, 200, 100 };
    /// <summary>About one deposit in three is a cheque deposit (cheque scanner); it moves no notes.</summary>
    private static bool IsChequeDeposit(int h) => h / 11 % 3 == 0;

    private static IEnumerable<object> TransactionCounters(long txId)
    {
        long hi = txId / 100000; int i = (int)(txId % 100000 / 100), k = (int)(txId % 100);
        if (i < 0 || i >= AtmCount || txId < 100_000_000_000L) yield break;   // supervisor rows (ids ~9e9) move no notes
        var hour = new DateTime(hi * TimeSpan.TicksPerHour, DateTimeKind.Utc);
        int h = Hash(i, (int)hi, 60 + k);
        var types = new[] { "Cash Withdrawal", "Cash Withdrawal", "Cash Withdrawal", "Balance Inquiry", "Deposit", "Transfer", "Reversal", "Mini Statement", "Cash Withdrawal", "PIN Change" };
        var type = types[h / 7 % types.Length];
        if (type == "Deposit" && IsChequeDeposit(h)) type = "Cheque Deposit";
        bool fail = h % 100 < (Chronic.Contains(i) ? 28 : 4);
        if (fail || type is not ("Cash Withdrawal" or "Deposit")) yield break;
        var left = 100 * (1 + h % 20);
        var comp = type == "Deposit" ? "KXDevice.KXCashAcceptor" : "KXDevice.KXCashDispenser";
        var name = type == "Deposit" ? "Deposited" : "Dispensed";
        for (int d = 0; d < Notes.Length; d++)
        {
            int count = left / Notes[d]; left -= count * Notes[d];
            if (count > 0) yield return new { ClientID = Id(i), ComponentID = CompId(comp), PropertyID = PropId(name), DenominationID = d + 1, Timestamp = hour,
                Value = 500 + Hash(i, d, 77) % 400, Delta = count, Reset = false, TransactionID = txId };
        }
    }
    // Property ids of KTC's counter names (Dispensed, Deposited, ...) follow the CurrentStatus properties in PropertyList.
    private static string[] AllProps => HwProps.Concat(KtcCounterDefs.Select(d => d.Name)).Distinct().ToArray();
    private static int PropId(string name) => Array.IndexOf(AllProps, name) + 1;
    private static int CompId(string dbName) => Array.FindIndex(HwComponents, c => c.Name == dbName switch
    {
        "KXDevice.KXCardReader" => "Card Reader", "KXDevice.KXCashDispenser" => "Cash Dispenser", "KXDevice.KXCashAcceptor" => "Cash Acceptor",
        "KXDevice.KXReceiptPrinter" => "Receipt Printer", "KXDevice.KXBundleCheckScanner" => "Cheque Scanner", _ => dbName
    }) + 1;
    // As the live KTC answers: {ClientID, DBComponentID, DBPropertyID, DenominationID, CounterValue, Timestamp, LastResetTimestamp, LastMismatchSize}.
    private static IEnumerable<object> KtcCounters(int i, DateTime now)
    {
        var days = Math.Max(0.1, (now - LastReplenish(i, now)).TotalDays);
        bool Bad(string comp) => comp switch { "KXDevice.KXCardReader" => HwBad(i, "Card Reader"), "KXDevice.KXCashDispenser" => HwBad(i, "Cash Dispenser"),
            "KXDevice.KXCashAcceptor" => HwBad(i, "Cash Acceptor"), "KXDevice.KXReceiptPrinter" => HwBad(i, "Receipt Printer"), _ => false };
        for (int k = 0; k < KtcCounterDefs.Length; k++)
        {
            var d = KtcCounterDefs[k];
            var v = d.Name == "Remaining" ? Math.Max(0, 8000 - 560 * days) : Math.Round((Bad(d.Comp) ? d.Bad : d.PerDay) * days * (0.8 + Hash(i, k, 400) % 40 / 100.0));
            yield return new { ClientID = Id(i), DBComponentID = CompId(d.Comp), DBPropertyID = PropId(d.Name), DenominationID = 0, CounterValue = (long)v,
                Timestamp = now.AddMinutes(-(Hash(i, k) % 60)), LastResetTimestamp = LastReplenish(i, now), LastMismatchSize = 0 };
        }
    }

    // KTC CurrentCashUnitStatus: {ClientID, DBComponentID, TypeID, StatusID, CurrencyID, CashUnitNumber, Value, Count, Total, Timestamp}.
    private static IEnumerable<object> CashUnits(int i, DateTime now)
    {
        var disp = Array.FindIndex(HwComponents, c => c.Name == "Cash Dispenser") + 1; var acc = Array.FindIndex(HwComponents, c => c.Name == "Cash Acceptor") + 1;
        var since = (now - LastReplenish(i, now)).TotalDays;
        for (int u = 1; u <= 4; u++)
            yield return new { ClientID = Id(i), DBComponentID = disp, TypeID = 21, StatusID = 1, CurrencyID = 0, CashUnitNumber = u, Value = new[] { 100m, 200m, 500m, 1000m }[u - 1],
                Count = Math.Max(0, 2000 - (int)(since * 150 * (0.7 + Hash(i, u, 300) % 60 / 100.0))), Total = 0m, Timestamp = now.AddMinutes(-(Hash(i, u) % 90)) };
        yield return new { ClientID = Id(i), DBComponentID = acc, TypeID = 23, StatusID = 1, CurrencyID = 0, CashUnitNumber = 5, Value = 0m, Count = (int)(since * 140), Total = 0m, Timestamp = now.AddMinutes(-(Hash(i, 5) % 90)) };
    }

    private static int Hash(int a, int b = 0, int c = 0) { unchecked { var h = 17; h = h * 31 + a; h = h * 31 + b; h = h * 31 + c; h ^= h >> 15; h *= 0x2c1b3c6d; h ^= h >> 12; return Math.Abs(h); } }
    private static object ClientJson(int i) => new
    {
        ID = Id(i), Name = $"{Banks[i % 3][..3].ToUpper()}-ATM-{i + 1:000}", Alias = $"{Places[i % Places.Length].City} Mall #{i + 1}",
        Address = $"10.{20 + i % 3}.{i / 3}.{10 + i}", BusinessID = i % 3 + 1, BranchID = i % Places.Length + 1,
        RegionID = Array.IndexOf(Places.Select(p => p.Region).Distinct().ToArray(), Places[i % Places.Length].Region) + 1,
        HardwareTypeName = Types[i % Types.Length], Active = true, Deleted = false,
        GUID = new Guid(Hash(i, 501), 0, 0, new byte[8]).ToString("D").ToUpperInvariant(), Subnet = i % 4 == 0 ? $"10.{20 + i % 3}.{i / 3}.0/24" : "",
        Latitude = i % 5 == 4 ? 0.0 : Math.Round(Coords[i % Places.Length].Lat + Hash(i, 502) % 200 / 10000.0, 6),
        Longitude = i % 5 == 4 ? 0.0 : Math.Round(Coords[i % Places.Length].Lon + Hash(i, 503) % 200 / 10000.0, 6),
        TimeZone = "Arabian Standard Time", Level1RegionID = Array.IndexOf(Places.Select(p => p.Region).Distinct().ToArray(), Places[i % Places.Length].Region) + 1,
        Level2RegionID = 0, HardwareID = 100 + i % Types.Length, ConnectableByte = 1, TypeByte = 1, Salt = "mock-salt", AuthHash = "mock-hash", FeatureFlagsString = "UMTB|TRRI|ENBL"
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

    private record MockTicket(long ID, int ClientID, string ClientName, int StatusByte, DateTime TimeCreated, DateTime TimeUpdated, DateTime? TimeOwned, DateTime? TimeDispatched, DateTime? TimeClosed, string? OwnerName, string Description, int Level, DateTime? SLADueTime)
    {
        // KTC-like extras for the ticket list and details popup.
        public int TicketTypeID { get; init; }
        public int ErrorCodeTypeID { get; init; }
        public long ErrorID { get; init; }
        public DateTime TimeOpened => TimeCreated;
        public int EscalationLevel { get; init; }
        public string BankRefNum { get; init; } = "";
        public string ClosureMode => TimeClosed == null ? "" : StatusByte == 5 ? "Automatic" : "Manual";
        public bool PlannedTicket { get; init; }
        public string? DispatchedTo { get; init; }
        public int ReopenCount { get; init; }
        public string LastChangeBy { get; init; } = "System";
        public string CommentsString { get; init; } = "";
    }
    private static string MockCode(int id) => "KW" + (20 + id);
    private static readonly string[] CodeCategory = { "Cash", "Card", "Host", "Consumables", "Network", "Cash", "Security", "Replenish" };
    private static object ErrorCodeTypes() => Codes.Select(c => new { ID = c.Id, Code = MockCode(c.Id), ErrorText = c.Name, Description = c.Name + ". Check the device and the ATM application log.", Level = c.Level, Category = CodeCategory[(c.Id - 1) % CodeCategory.Length], HelpText = c.Level <= 2 ? "Dispatch an engineer if it does not clear in 15 minutes." : "Send CVIT / branch staff on the next visit." });
    private static object TicketTypes() => Codes.Select(c => new { ID = 100 + c.Id, Name = c.Name, AppliesToBusiness = "All Businesses", SendsEmail = "On Creation", PerformsAction = false, MinimumPoints = 0, Category = CodeCategory[(c.Id - 1) % CodeCategory.Length], ConflictCategory = "None", NoTicketsWhenPlannedActive = false });
    private static string MockComments(DateTime created, DateTime? closed, string? owner, bool auto, string desc)
    {
        var x = new System.Text.StringBuilder("<Comments>");
        void Add(DateTime t, string u, string c) => x.Append($"<Comment User=\"{u}\" Timestamp=\"{t:yyyy-MM-ddTHH:mm:ssZ}\">{System.Security.SecurityElement.Escape(c)}</Comment>");
        Add(created, "System", "Ticket created automatically.");
        Add(created, "System", "Email successfully sent TO: atm.support@example.com;cash.ops@example.com");
        if (owner != null) Add(created.AddMinutes(10), owner, $"Taking ownership. Customer card 4111111111111111 reported retained; checking {desc.ToLowerInvariant()}.");
        if (closed != null) Add(closed.Value, auto ? "System" : owner ?? "System", auto ? "Auto closed by system as error code is no longer active." : "Fixed on site, ticket closed.");
        return x.Append("</Comments>").ToString();
    }
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
            var own = stage >= 1 ? Owners[Hash(i, code) % Owners.Length] : null; DateTime? cl = stage == 3 ? now.AddMinutes(-3) : null;
            yield return new MockTicket(700000L + i * 100 + code, Id(i), (string)inc.ClientName, stage == 3 ? 4 : stage + 1, created, cl ?? now.AddMinutes(-5),
                stage >= 1 ? created.AddMinutes(10) : null, stage >= 2 ? created.AddMinutes(25) : null, cl,
                own, (string)inc.Description, level, level <= 2 ? sla : null)
            { TicketTypeID = 100 + code, ErrorCodeTypeID = code, ErrorID = (long)inc.ID, BankRefNum = (160000 + Hash(i, code, 27) % 9999).ToString(), DispatchedTo = stage >= 2 ? "CMS Field Services" : null,
              EscalationLevel = level <= 2 && stage == 0 ? 1 : 0, LastChangeBy = own ?? "System", CommentsString = MockComments(created, cl, own, false, (string)inc.Description) };
        }
        // closed tickets in last 3 days
        for (int d = 0; d < 3; d++)
            if (Hash(i, d, 31) % 100 < 14)
            {
                var c = Codes[Hash(i, d, 32) % Codes.Length];
                var cr = now.Date.AddDays(-d).AddHours(Hash(i, d, 33) % 12 + 1);
                var due = cr.AddHours(c.Level switch { 1 => 4, 2 => 8, 3 => 24, _ => 72 });
                var closed = Hash(i, d, 34) % 100 < 12 ? due.AddMinutes(45) : cr.AddHours(1 + Hash(i, d, 35) % 3);
                var auto = Hash(i, d, 36) % 100 < 70; var own = auto ? null : Owners[Hash(i, d) % Owners.Length];
                yield return new MockTicket(600000L + i * 100 + d * 10 + c.Id, Id(i), $"{Banks[i % 3][..3].ToUpper()}-ATM-{i + 1:000}", auto ? 5 : 4, cr, closed, auto ? null : cr.AddMinutes(5), auto ? null : cr.AddMinutes(20), closed, own, c.Name, c.Level, auto ? null : due)
                { TicketTypeID = 100 + c.Id, ErrorCodeTypeID = c.Id, ErrorID = 400000L + i * 100 + d, BankRefNum = (160000 + Hash(i, d, 37) % 9999).ToString(), DispatchedTo = auto ? null : "CMS Field Services",
                  LastChangeBy = auto ? "System" : own!, CommentsString = MockComments(cr, closed, own, auto, c.Name) };
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
        if (onlyId > 100_000_000_000L) { start = new DateTime(onlyId / 100000 * TimeSpan.TicksPerHour, DateTimeKind.Utc); end = start.AddHours(1); }
        if (end > now) end = now;
        if (end - start > TimeSpan.FromDays(8)) start = end.AddDays(-8);
        var types = new[] { "Cash Withdrawal", "Cash Withdrawal", "Cash Withdrawal", "Balance Inquiry", "Deposit", "Transfer", "Reversal", "Mini Statement", "Cash Withdrawal", "PIN Change" };
        var fails = new[] { "Host timeout - no response from authorization host", "Host declined - issuer unavailable", "Communication failure - connection lost", "Cash retract - note not taken by customer", "Dispense failure - cassette jam", "Card reader error", "Card captured - retained by ATM" };
        var rows = new List<object>();
        // Supervisor sessions: the replenishment that resets the counters (REPLENISH + ADD_CASH + PRNT_CNTRS in one session).
        if (onlyId == 0)
            for (int i = 0; i < AtmCount; i++)
            {
                if (business > 0 && i % 3 + 1 != business) continue;
                var at = LastReplenish(i, now);
                if (at < start || at > end) continue;
                long sid = 900000000L + i * 1000 + at.DayOfYear;
                foreach (var (op, k) in new[] { ("REPLENISH", 0), ("ADD_CASH", 1), ("PRNT_CNTRS", 2) })
                    rows.Add(new { ID = sid * 10 + k, UUID = TxUuid(sid * 10 + k), sessionID = sid, ClientID = Id(i), ClientName = AtmName(i), TransactionTypeName = op,
                        CompletionCodeName = "COMPLETED", ReasonName = (string?)null, ErrorCode = "", Amount = 0m, Currency = "AED", StartTime = at.AddMinutes(k), EndTime = at.AddMinutes(k).AddSeconds(30) });
            }
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
                    if (type == "Deposit" && IsChequeDeposit(h)) type = "Cheque Deposit";
                    var f = fails[(h / 13 + (Chronic.Contains(i) ? 3 : 0)) % fails.Length];
                    rows.Add(new
                    {
                        ID = id, UUID = TxUuid(id), sessionID = id / 2, startEJID = id % 1_000_000 * 10, endEJID = id % 1_000_000 * 10 + 7, ClientID = Id(i), ClientName = $"{Banks[i % 3][..3].ToUpper()}-ATM-{i + 1:000}",
                        TransactionTypeName = type, CompletionCodeName = fail ? f : "Completed OK", ReasonName = fail ? f : (string?)null, ErrorCode = fail ? $"E{100 + h % 60}" : "",
                        Amount = type is "Cash Withdrawal" or "Deposit" ? (decimal)(100 * (1 + h % 20)) : type == "Cheque Deposit" ? (decimal)(500 * (1 + h % 40)) : 0m, Currency = "AED",
                        StartTime = t, EndTime = t.AddMilliseconds(fail ? 4000 + h % 9000 : 900 + h % 3200)
                    });
                    if (rows.Count > 40000) return rows;
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
        // The cash counter report the ATM prints when it restarts (daily at 05:10 here): one row on even ATMs, one row per line on odd ones.
        for (var day = start.Date; day <= end; day = day.AddDays(1))
        {
            var at = day.AddHours(5).AddMinutes(10 + Hash(i, day.DayOfYear, 90) % 20);
            if (at < start || at > end) continue;
            var report = EjCounterReport(i, at);
            if (i % 2 == 0) rows.Add(new { ID = ++seq, Sequence = seq, TimeStamp = at, Text = string.Join("\n", report) });
            else { int k = 0; foreach (var l in report) rows.Add(new { ID = ++seq, Sequence = seq, TimeStamp = at.AddMilliseconds(k++ * 10), Text = l }); }
        }
        return rows;
    }

    private static List<string> EjCounterReport(int i, DateTime at)
    {
        var types = new[] { (1, 1000), (2, 500), (3, 50), (4, 100) };
        var lines = new List<string> { $"MACHINE NO ={AtmName(i).Replace("-", "")[..Math.Min(8, AtmName(i).Replace("-", "").Length)]}", $"DATE-TIME ={at:MM/dd/yy HH:mm:ss}" };
        foreach (var pair in types.Chunk(2))
        {
            lines.Add("             " + string.Join(" ", pair.Select(t => $"TYPE{t.Item1}-{t.Item2}".PadRight(10))));
            var vals = pair.Select(t =>
            {
                long total = t.Item2 == 50 ? 0 : 2500 + 500 * (Hash(i, t.Item1, 91) % 2);
                long disp = total == 0 ? 0 : Math.Min(total - 10, (long)((at - at.Date.AddDays(-3)).TotalHours * 20 * (1 + Hash(i, t.Item1, 92) % 3)));
                long rej = total == 0 ? 0 : Math.Min(Hash(i, t.Item1, at.DayOfYear) % 20, total - disp);
                return (Cas: total - disp - rej, Rej: rej, Disp: disp, Dep: 0L, Tot: total);
            }).ToList();
            string Row(string label, Func<(long Cas, long Rej, long Disp, long Dep, long Tot), long> f) => label.PadRight(14) + string.Join("", vals.Select(v => f(v).ToString("00000").PadRight(11)));
            lines.Add(Row(" CASSETTE", v => v.Cas)); lines.Add(Row("+REJECTED", v => v.Rej)); lines.Add(Row("+DISPENSED", v => v.Disp));
            lines.Add(Row("-DEPOSITED", v => v.Dep)); lines.Add(Row("=TOTAL", v => v.Tot));
        }
        lines.Add($" LAST CLEARED  {at.AddDays(-3):MM/dd/yy HH:mm}");
        return lines;
    }

    // ------------- transaction details -------------
    private static string TxUuid(long id) => new Guid(id.ToString("x32")).ToString("D");
    private static long TxIdFromUuid(string uuid) => long.TryParse(uuid.Replace("-", ""), System.Globalization.NumberStyles.HexNumber, null, out var l) ? l : 0;
    private static readonly string[] StxFields = { "Account type", "Card type", "Currency", "Card entry", "Language", "Interface", "Requested amount", "Authorisation code", "Host response code", "Masked PAN" };
    private static object TxFields(long id, DateTime now)
    {
        dynamic? t = Transactions(null, now, id).FirstOrDefault();
        if (t == null) return new Dictionary<string, object>();
        int h = (int)(id % 997);
        var f = new Dictionary<string, object>
        {
            ["1"] = new { ID = id * 10 + 1, TransactionID = id, STXFieldID = 1, Value = h % 2 == 0 ? "SAVINGS" : "CURRENT", Type = "String" },
            ["2"] = new { ID = id * 10 + 2, TransactionID = id, STXFieldID = 2, Value = h % 3 == 0 ? "VISA" : "MASTERCARD", Type = "String" },
            ["3"] = new { ID = id * 10 + 3, TransactionID = id, STXFieldID = 3, Value = "22", Type = "LookupString" },
            ["4"] = new { ID = id * 10 + 4, TransactionID = id, STXFieldID = 4, Value = h % 4 == 0 ? "CONTACTLESS" : "CHIP", Type = "String" },
            ["7"] = new { ID = id * 10 + 7, TransactionID = id, STXFieldID = 7, Value = ((decimal)t.Amount).ToString("0.00"), Type = "Decimal" },
            ["9"] = new { ID = id * 10 + 9, TransactionID = id, STXFieldID = 9, Value = (string)t.ErrorCode == "" ? "00" : "91", Type = "String" },
            ["10"] = new { ID = id * 10 + 10, TransactionID = id, STXFieldID = 10, Value = Pans[h % Pans.Length], Type = "String" },
        };
        if ((string)t.ErrorCode == "") f["8"] = new { ID = id * 10 + 8, TransactionID = id, STXFieldID = 8, Value = (100000 + h * 37 % 900000).ToString(), Type = "String" };
        return f;
    }
    private static IEnumerable<string> TxEj(string? clientId, long startEj, DateTime now)
    {
        // startEJID = id % 1e6 * 10: find the transaction of this ATM in the last 8 days with that EJ start.
        var i = int.TryParse(clientId, out var c) ? Math.Clamp(c - 1000, 0, AtmCount - 1) : 0;
        var hit = Transactions(null, now).Concat(Transactions(System.Text.Json.JsonSerializer.Serialize(new { Start = now.AddDays(-8), End = now }), now))
            .Cast<dynamic>().FirstOrDefault(t => (int)t.ClientID == Id(i) && HasEj(t) && (long)t.startEJID == startEj);
        if (hit == null) yield break;
        string type = hit.TransactionTypeName; DateTime at = hit.StartTime; decimal amt = hit.Amount; bool ok = (string)hit.ErrorCode == "";
        var pan = Pans[(int)((long)hit.ID % Pans.Length)];
        yield return $"<EJData>_[020t {at:HH:mm:ss}\n_[020t*{startEj % 1000}*{at:dd/MM/yyyy}*{at:HH:mm}*\n     *TRANSACTION STARTED*\nDATE {at:dd/MM/yy}    TIME {at:HH:mm:ss}</EJData>";
        yield return $"<EJData>_[020t {at.AddSeconds(4):HH:mm:ss} CARD INSERTED  PAN: {pan}</EJData>";
        yield return $"<EJData>_[020t {at.AddSeconds(15):HH:mm:ss} PIN ENTERED</EJData>";
        yield return $"<EJData>_[020t {at.AddSeconds(20):HH:mm:ss} {type.ToUpperInvariant()}{(amt > 0 ? $" AED {amt:0.00}" : "")}</EJData>";
        yield return $"<EJData>_[020t {at.AddSeconds(24):HH:mm:ss} HOST RESPONSE: {(ok ? "APPROVED 00" : (string)hit.ReasonName)}</EJData>";
        if (ok && type == "Cash Withdrawal") yield return $"<EJData>_[020t {at.AddSeconds(30):HH:mm:ss} NOTES PRESENTED / TAKEN</EJData>";
        yield return $"<EJData>_[020t {at.AddSeconds(40):HH:mm:ss} TRANSACTION END</EJData>";
    }
    private static bool HasEj(dynamic t) { try { return t.startEJID != null; } catch { return false; } }

    // ------------- helpers -------------
    // KTC CommandType records, shaped like the Postman collection's POST CommandType sample. Names are DEMO values, not a real server's.
    private static readonly object[] CommandTypes =
    {
        Cmd(1, "KTC_DIR_LISTING", "ATM Directory Listing", "ATM Management", "<Params><Param Name=\"Folder\" Type=\"String\" Required=\"true\" /><Param Name=\"Recursive\" Type=\"Bool\" Default=\"false\" /></Params>", 0),
        Cmd(2, "KTC_CLEAR_MSG_QUEUE", "Clear Message Queue", "ATM Management", "<Params />", 0),
        Cmd(3, "KTC_EMERGENCY_REBOOT", "Emergency Reboot", "ATM Management", "<Params />", 0),
        Cmd(4, "KTC_PING", "Ping", "ATM Management", "<Params />", 0),
        Cmd(5, "KTC_REBOOT", "Reboot", "ATM Management", "<Params><Param Name=\"WhenIdle\" Type=\"Bool\" Default=\"true\" /></Params>", 0),
        Cmd(6, "KTC_REFRESH_STATUS", "Refresh Status", "ATM Management", "<Params />", 0),
        Cmd(7, "KTC_RENAME", "Rename File or Folder", "ATM Management", "<Params><Param Name=\"From\" Type=\"String\" Required=\"true\" /><Param Name=\"To\" Type=\"String\" Required=\"true\" /></Params>", 0),
        Cmd(8, "KTC_RUN_SCRIPT", "Run ATM Script and Upload Result", "ATM Management", "<Params><Param Name=\"Script\" Type=\"String\" Required=\"true\" /><Param Name=\"TimeoutSeconds\" Type=\"Int32\" Default=\"300\" Min=\"10\" Max=\"3600\" /></Params>", 0),
        Cmd(9, "KTC_SET_TIME", "Set ATM Time", "ATM Management", "<Params><Param Name=\"Time\" Type=\"DateTime\" Required=\"true\" /></Params>", 0),
        Cmd(10, "KTC_SET_TIME_TZ", "Set ATM Time and Time Zone", "ATM Management", "<Params><Param Name=\"Time\" Type=\"DateTime\" Required=\"true\" /><Param Name=\"TimeZone\" Type=\"Choice\" Values=\"Arabian Standard Time,UTC,India Standard Time\" /></Params>", 0),
        Cmd(11, "KTC_SHUTDOWN", "Shutdown", "ATM Management", "<Params />", 0),
        Cmd(12, "KTC_RESCUE_IMAGE", "Trigger Rescue Image", "ATM Management", "<Params />", 1),
        Cmd(13, "KTC_GET_SETTING", "Get Settings", "ATM Settings", "<Params><Param Name=\"Type\" Type=\"Choice\" Values=\"MultiLevelStore,PersistentData,Registry\" Default=\"MultiLevelStore\" /><Param Name=\"Location\" Type=\"String\" Required=\"true\" /><Param Name=\"Use64bit\" Type=\"Bool\" Default=\"false\" TrueText=\"64 Bit\" FalseText=\"32 Bit\" /></Params>", 0),
        Cmd(14, "KTC_SET_SETTING", "Set Settings", "ATM Settings", "<Params><Param Name=\"Type\" Type=\"Choice\" Values=\"MultiLevelStore,PersistentData,Registry\" /><Param Name=\"Location\" Type=\"String\" Required=\"true\" /><Param Name=\"Value\" Type=\"String\" Required=\"true\" /><Param Name=\"Use64bit\" Type=\"Bool\" Default=\"false\" TrueText=\"64 Bit\" FalseText=\"32 Bit\" /></Params>", 1),
        Cmd(15, "KTC_SELF_TEST", "Run Device Self Test", "Device Management", "<Params />", 0),
        Cmd(16, "KTC_RESET_DEVICE", "Reset the ATM devices (the ATM application decides which)", "Device Management", "<Params />", 0),
        Cmd(17, "KTC_SUPERVISOR_ON", "Supervisor mode on (ATM out of service)", "Device Management", "<Params><Param Name=\"Reason\" Type=\"String\" /></Params>", 0),
        Cmd(18, "KTC_SUPERVISOR_OFF", "Supervisor mode off (ATM in service)", "Device Management", "<Params />", 0),
        Cmd(19, "KTC_UPLOAD_EJ", "Upload Journal Data", "Journal Data", "<Params><Param Name=\"Days\" Type=\"Int32\" Default=\"1\" Min=\"1\" Max=\"31\" /></Params>", 0),
        Cmd(20, "KTC_UPLOAD_FILE", "Upload File", "Uploads", "<Params><Param Name=\"Path\" Type=\"String\" Required=\"true\" /></Params>", 0),
        Cmd(21, "KTC_CERT_RENEW", "Renew Certificate", "Certificates", "<Params><Param Name=\"Store\" Type=\"Choice\" Values=\"Machine,User\" /></Params>", 1),
        Cmd(22, "KTC_APP_COMMAND", "Application Command", "Application Command", "<Params><Param Name=\"Command\" Type=\"String\" Required=\"true\" /><Param Name=\"Mode\" Type=\"Widget\" /></Params>", 0),
        Cmd(23, "KTC_INSTALL_PACKAGE", "Install Package", "Software Distribution", "<Params><Param Name=\"PackageID\" Type=\"Int32\" Required=\"true\" /></Params>", 1),
        Cmd(24, "KTC_LINKED_ACTIONS", "Linked Actions", "Linked Actions", "<Params />", 0),
    };
    private static object Cmd(int id, string name, string desc, string cat, string tpl, int dual) => new
    {
        ID = id, CommandName = name, Description = desc, ParameterListTemplateString = tpl, SubCommandListString = "", AllowClientInitiation = false,
        MaxRetries = 3, RetryWaitTime = 60, MaxSimultaneous = 50, TargetTypeByte = 0, IsUserDefined = false, Category = cat, DualAuthModeByte = dual
    };
    // ---- Actions: an in-memory store, so sent commands go Pending -> In progress -> Completed / Failed over a few seconds. ----
    private static long _nextAction = 880000;
    private const int RebootDownSeconds = 45;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, MockAction> MockActions = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, DateTime> Rebooted = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> Seeded = new();
    private static readonly int[] FileCommands = { 1, 19, 20 };

    private sealed record MockAction(long Id, int ClientId, int CommandId, string Params, string Comments, DateTime Added, int PendingS, int RunS, bool Fails, string Result, bool Files)
    {
        public int StatusAt(DateTime now) { var s = (now - Added).TotalSeconds; return s < PendingS ? 0 : s < PendingS + RunS ? 1 : Fails ? 3 : 2; }
        public object Json(DateTime now)
        {
            var st = StatusAt(now); var s = (now - Added).TotalSeconds;
            return new
            {
                ID = Id, CommandID = CommandId, ClientID = ClientId, DependantID = 0, IsEndOfChain = true, ScheduleID = 0, CommandParametersString = Params,
                StartTime = st == 0 ? DateTime.MinValue : Added.AddSeconds(PendingS), EndTime = st >= 2 ? Added.AddSeconds(PendingS + RunS) : DateTime.MinValue,
                StatusByte = st, CommentsString = Comments, AddedTime = Added, RetryCount = Fails && st == 3 ? 3 : 0,
                ProgressPercent = st == 0 ? 0 : st == 1 ? (int)Math.Min(95, (s - PendingS) * 100 / Math.Max(1, RunS)) : 100, ResultString = st >= 2 ? Result : ""
            };
        }
    }

    private static string CommentXml(string user, string text, DateTime t) =>
        new System.Xml.Linq.XElement("Comments", new System.Xml.Linq.XElement("Comment", new System.Xml.Linq.XAttribute("User", user), new System.Xml.Linq.XAttribute("Timestamp", t.ToString("o")), new System.Xml.Linq.XAttribute("Confidential", "false"), text)).ToString(System.Xml.Linq.SaveOptions.DisableFormatting);

    private static long AddAction(int clientId, string body, DateTime now)
    {
        using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        var root = d.RootElement;
        var cmd = root.TryGetProperty("Value1", out var v1) && v1.TryGetInt32(out var c) ? c : 0;
        var prm = root.TryGetProperty("Value2", out var v2) ? v2.GetString() ?? "" : "";
        string user = "?", text = "";
        if (root.TryGetProperty("Value3", out var v3) && v3.ValueKind == JsonValueKind.Array && v3.GetArrayLength() > 0)
        { var c0 = v3[0]; user = c0.TryGetProperty("User", out var u) ? u.GetString() ?? "?" : "?"; text = c0.TryGetProperty("Comment", out var t) ? t.GetString() ?? "" : ""; }
        var id = Interlocked.Increment(ref _nextAction);
        var (pending, run, result) = cmd switch
        {
            5 or 3 => (2, 6, "Reboot command accepted by the agent; restarting."),
            17 => (1, 4, "ATM is out of service."),
            18 => (1, 4, "ATM is in service."),
            13 => (2, 5, "HKLM\\SOFTWARE\\KAL\\Kalignite (64-bit)\nLogLevel = 3\nTerminalID = ADC00042\nHostPort = 7002"),
            4 => (1, 2, "Reply from the ATM agent in 38 ms."),
            _ => (2, 8, FileCommands.Contains(cmd) ? "3 files uploaded (EJ, diagnostics log, log archive)." : "Done.")
        };
        var a = new MockAction(id, clientId, cmd, prm, CommentXml(user, text, now), now, pending, run, false, result, FileCommands.Contains(cmd));
        MockActions[id] = a;
        if (cmd is 5 or 3) Rebooted[clientId] = now.AddSeconds(pending + run);
        return id;
    }

    private static void SeedActions(int clientId, DateTime now)
    {
        if (!Seeded.TryAdd(clientId, true)) return;
        var seed = new (int Cmd, double HoursAgo, bool Fails, string Result, string Params, string Who, string Text)[]
        {
            (19, 5.2, false, "3 files uploaded (EJ, diagnostics log, log archive).", "<Parameters><Parameter Name=\"Days\">1</Parameter></Parameters>", "s.kumar", "Ticket 600123: Customer disputes a dispense of 4111 1111 1111 1111 last night (via KTC Monitoring Portal, ref 3fa9c2d1e0b7)"),
            (6, 26, true, "Agent did not respond after 3 retries.", "<Parameters />", "ops.bot", "Scheduled status refresh"),
            (4, 30, false, "Reply from the ATM agent in 41 ms.", "<Parameters />", "a.rahman", "Ticket 600087: Check connectivity after network change (via KTC Monitoring Portal, ref 9b1e77aa0c12)"),
        };
        foreach (var x in seed)
        {
            var id = Interlocked.Increment(ref _nextAction);
            var t = now.AddHours(-x.HoursAgo);
            MockActions[id] = new MockAction(id, clientId, x.Cmd, x.Params, CommentXml(x.Who, x.Text, t), t, 30, 90, x.Fails, x.Result, FileCommands.Contains(x.Cmd));
        }
    }

    private static string SampleFile(string name, MockAction a) => name.EndsWith(".log")
        ? $"{a.Added:yyyy-MM-dd HH:mm:ss} INFO  KALDiag started\n{a.Added:yyyy-MM-dd HH:mm:ss} INFO  XFS manager version 3.30\n{a.Added:yyyy-MM-dd HH:mm:ss} WARN  CDM cassette 2 low (412 notes)\n{a.Added:yyyy-MM-dd HH:mm:ss} INFO  Card read track2 ;4111111111111111=29121011000012345678?\n{a.Added:yyyy-MM-dd HH:mm:ss} INFO  KALDiag finished\n"
        : string.Join("\n", Enumerable.Range(0, 60).Select(k => (k % 12) switch
        {
            0 => $"[{a.Added.AddMinutes(-k * 7):dd/MM/yy HH:mm:ss}] TRANSACTION START",
            1 => "CARD INSERTED",
            2 => $"PAN: {(k % 24 == 1 ? "4111111111111111" : "5500 0000 0000 0004")}",
            3 => "PIN ENTERED",
            4 => "CASH WITHDRAWAL AED 500",
            5 => "NOTES PRESENTED 100x5",
            6 => "NOTES TAKEN",
            7 => "CARD TAKEN",
            8 => "TRANSACTION END",
            _ => ""
        }));

    private static bool Is(string path, string route, bool cond = true) => cond && path.Equals(route, StringComparison.OrdinalIgnoreCase);
    private static Match Rx(string path, string pattern) => Regex.Match(path, "^" + pattern + "$", RegexOptions.IgnoreCase);
    private static HttpResponseMessage Ok(object? o) => Resp(HttpStatusCode.OK, JsonSerializer.Serialize(o, J));
    private static HttpResponseMessage Resp(HttpStatusCode code, string json) => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
