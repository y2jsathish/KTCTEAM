using Ktc.Application.Abstractions;
using Ktc.Application.Logic;
using Ktc.Application.Models;
using Ktc.Domain.Entities;
using Ktc.Domain.Enums;
using Ktc.Web.Infrastructure;
using Ktc.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ktc.Web.Controllers;

/// <summary>JSON endpoints that feed DataTables, charts and widgets.</summary>
[Route("data")]
[ApiController]
public class DataController : ControllerBase
{
    private readonly IDashboardService _dash; private readonly IKtcDataService _data; private readonly IUnitOfWork _uow; private readonly IApiHealthTracker _health;
    private readonly IAlertEngine _alerts; private readonly IAuditService _audit;

    private readonly ILogger<DataController> _log;
    public DataController(IDashboardService d, IKtcDataService k, IUnitOfWork u, IApiHealthTracker h, IAlertEngine a, IAuditService au, ILogger<DataController> log) { _dash = d; _data = k; _uow = u; _health = h; _alerts = a; _audit = au; _log = log; }

    // ---------- dashboard ----------
    [HttpGet("summary")]
    public async Task<IActionResult> Summary(bool refresh, CancellationToken ct) { var s = await _dash.GetSnapshotAsync(refresh, ct); return Ok(s with { Atms = new(), Incidents = new(), Tickets = new() }); }

    [HttpGet("filters")]
    public async Task<IActionResult> Filters(CancellationToken ct)
    {
        var s = await _dash.GetSnapshotAsync(false, ct);
        return Ok(new { clients = s.Atms.Select(a => a.Client).Where(x => x != "").Distinct().Order(), cities = s.Atms.Select(a => a.City).Where(x => x != "").Distinct().Order(), regions = s.Atms.Select(a => a.Region).Where(x => x != "").Distinct().Order() });
    }

    [HttpGet("atms")]
    public async Task<IActionResult> Atms(string? client, string? city, string? region, string? status, CancellationToken ct)
        => Ok(new { data = await _dash.GetAtmsAsync(new AtmFilter(client, city, region, status), ct) });

    [HttpGet("atm/{id}")]
    [Audit("AtmView", "ATM 360 data")]
    public async Task<IActionResult> AtmDetail(string id, CancellationToken ct)
    {
        var d = await _dash.GetAtmDetailAsync(id, ct);
        if (d == null) return NotFound(new { error = "ATM not found" });
        var timeline = EjTimeline.Build(d.Ej, d.Ej.Count == 0 ? null : d.Ej.Max(e => e.TransactionNo));
        var uid = int.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var i) ? i : 0;
        var fav = await _uow.Repo<FavoriteAtm>().Query().AnyAsync(f => f.UserId == uid && f.AtmId == d.Atm.Id, ct);
        return Ok(new { d.Atm, d.Incidents, d.Tickets, d.Transactions, d.Ej, timeline, availability = new { pct = double.IsNaN(d.Availability.AvailabilityPct) ? (double?)null : d.Availability.AvailabilityPct, reasons = d.Availability.TopUnavailableReasons.Select(r => new { r.Reason, r.Hours }) }, d.Warnings, favorite = fav });
    }

    [HttpPost("atm/{id}/favorite")]
    public async Task<IActionResult> Favorite(string id, string? name, CancellationToken ct)
    {
        var uid = int.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);
        var repo = _uow.Repo<FavoriteAtm>();
        var ex = await repo.Query().FirstOrDefaultAsync(f => f.UserId == uid && f.AtmId == id, ct);
        if (ex != null) repo.Remove(ex); else await repo.AddAsync(new FavoriteAtm { UserId = uid, AtmId = id, AtmName = name }, ct);
        await _uow.SaveChangesAsync(ct);
        return Ok(new { favorite = ex == null });
    }

    [HttpGet("favorites")]
    public async Task<IActionResult> Favorites(CancellationToken ct)
    {
        var uid = int.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);
        return Ok(await _uow.Repo<FavoriteAtm>().Query().Where(f => f.UserId == uid).OrderBy(f => f.AtmName).Select(f => new { f.AtmId, f.AtmName }).ToListAsync(ct));
    }

    // ---------- incidents ----------
    [HttpGet("incidents")]
    public async Task<IActionResult> Incidents(string? severity, string? q, string? state, CancellationToken ct)
    {
        var s = await _dash.GetSnapshotAsync(false, ct);
        IEnumerable<IncidentDto> r = s.Incidents;
        if (Enum.TryParse<Severity>(severity, true, out var sev)) r = r.Where(i => i.Severity == sev);
        if (!string.IsNullOrWhiteSpace(q)) r = r.Where(i => new[] { i.Number, i.Key, i.AtmName, i.AtmId, i.Client, i.Description }.Any(f => f.Contains(q, StringComparison.OrdinalIgnoreCase)));
        var list = r.OrderBy(i => i.Severity).ThenBy(i => i.OpenedUtc).ToList();
        var keys = list.Select(i => i.Key).ToList();
        var actions = await _uow.Repo<IncidentAction>().Query().Where(a => keys.Contains(a.IncidentKey)).OrderBy(a => a.CreatedUtc).ToListAsync(ct);
        var by = actions.ToLookup(a => a.IncidentKey);
        var rows = list.Select(i => new
        {
            i.Key, i.Number, i.AtmId, i.AtmName, i.Client, severity = i.Severity.ToString(), i.OpenedUtc, i.SlaDueUtc, sla = i.Sla.ToString(), i.Status, i.Description, i.ErrorCode,
            assignedTo = by[i.Key].LastOrDefault(a => a.ActionType == IncidentActionType.Assign)?.Value,
            comments = by[i.Key].Count(a => a.ActionType == IncidentActionType.Comment),
            escalated = by[i.Key].Any(a => a.ActionType == IncidentActionType.Escalate)
        });
        return Ok(new { data = rows, counts = Enum.GetValues<Severity>().ToDictionary(x => x.ToString(), x => s.Incidents.Count(i => i.Severity == x)) });
    }

    [HttpGet("incidents/{key}/actions")]
    [Audit("IncidentView", "Incident detail")]
    public async Task<IActionResult> IncidentActions(string key, CancellationToken ct)
        => Ok(await _uow.Repo<IncidentAction>().Query().Where(a => a.IncidentKey == key).OrderByDescending(a => a.CreatedUtc).Select(a => new { type = a.ActionType.ToString(), a.UserName, a.Value, a.CreatedUtc }).ToListAsync(ct));

    [HttpPost("incidents/action")]
    [Authorize(Policy = Policies.Operator)]
    public async Task<IActionResult> IncidentAction([FromBody] IncidentActionInput m, CancellationToken ct)
    {
        var user = User.Identity!.Name!;
        await _uow.Repo<IncidentAction>().AddAsync(new IncidentAction { IncidentKey = m.Key, ActionType = m.Type, UserName = user, Value = m.Value.Trim() }, ct);
        await _uow.SaveChangesAsync(ct);
        await _audit.LogAsync("Incident", m.Type.ToString(), m.Key, m.Type == IncidentActionType.Comment ? "comment added" : m.Value);
        if (m.Type == IncidentActionType.Escalate)
            await _alerts.RaiseManualAsync(AlertRuleType.IncidentEscalation, Severity.P1, $"Incident {m.Key} escalated by {user}", m.Value, "esc" + m.Key, ct);
        return Ok(new { ok = true });
    }

    // ---------- tickets ----------
    [HttpGet("tickets")]
    [Audit("TicketView", "Ticket search")]
    public async Task<IActionResult> Tickets(string? atmId, string? ticketId, string? client, DateTime? from, DateTime? to, string? status, CancellationToken ct)
    {
        var all = await _data.SearchTicketsAsync(new TicketQuery(atmId, ticketId, client, from?.ToUniversalTime(), to?.ToUniversalTime()), ct);
        IEnumerable<TicketDto> r = all;
        if (Enum.TryParse<TicketStatus>(status, true, out var st)) r = r.Where(t => t.Status == st);
        var list = r.Take(3000).ToList();
        var now = DateTime.UtcNow;
        return Ok(new
        {
            data = list.Select(t => new { t.Id, t.AtmId, t.AtmName, t.Client, status = t.Status.ToString(), severity = t.Severity.ToString(), t.CreatedUtc, t.UpdatedUtc, t.Owner, t.Description, t.SlaDueUtc, sla = t.Sla.ToString(), minutesToBreach = t.IsOpen ? (int)(t.SlaDueUtc - now).TotalMinutes : (int?)null }),
            counts = Enum.GetValues<TicketStatus>().ToDictionary(x => x.ToString(), x => all.Count(t => t.Status == x)),
            sla = new { achieved = all.Count(t => t.Sla == SlaState.Achieved), breached = all.Count(t => t.Sla == SlaState.Breached), nearBreach = all.Count(t => t.Sla == SlaState.NearBreach), onTrack = all.Count(t => t.Sla == SlaState.OnTrack) },
            breachSoon = all.Where(t => t.IsOpen && t.Sla == SlaState.NearBreach).OrderBy(t => t.SlaDueUtc).Take(20).Select(t => new { t.Id, t.AtmName, t.Client, minutes = Math.Max(0, (int)(t.SlaDueUtc - now).TotalMinutes) })
        });
    }

    [HttpGet("tickets/{id}")]
    [Audit("TicketView", "Ticket detail")]
    public async Task<IActionResult> Ticket(string id, CancellationToken ct)
    {
        var t = await _data.GetTicketAsync(id, ct);
        return t == null ? NotFound() : Ok(t);
    }

    // ---------- transactions ----------
    [HttpGet("transactions")]
    [Audit("Search", "Transaction search")]
    public async Task<IActionResult> Transactions(string? atmId, string? client, string? region, DateTime? from, DateTime? to, string? status, string? id, CancellationToken ct)
    {
        bool? ok = status?.ToLowerInvariant() switch { "success" => true, "failed" => false, _ => null };
        var f = from?.ToUniversalTime(); var t = to?.ToUniversalTime();
        if (f.HasValue && t.HasValue && t - f > TimeSpan.FromDays(7)) return BadRequest(new { error = "Date range limited to 7 days." });
        var all = await _data.SearchTransactionsAsync(new TransactionQuery(atmId, client, region, f, t, null), ct);
        if (!string.IsNullOrWhiteSpace(id)) all = all.Where(x => x.Id.Contains(id, StringComparison.OrdinalIgnoreCase)).ToList();
        var rows = ok.HasValue ? all.Where(x => x.Success == ok.Value).ToList() : all.ToList();
        var failed = all.Where(x => !x.Success).ToList();
        _log.LogInformation("Transaction search atm={Atm} client={Client} region={Region} status={Status} id={Id} {From:u}..{To:u}: {Total} transactions, {Shown} shown",
            atmId, client, region, status, id, f, t, all.Count, rows.Count);
        string? hint = null;
        if (all.Count == 0 && !string.IsNullOrWhiteSpace(atmId))
        {
            // Say whether the ATM simply had no transactions, and which ATMs did.
            var any = await _data.SearchTransactionsAsync(new TransactionQuery(null, null, null, f, t, null), ct);
            var top = any.GroupBy(x => x.AtmName.Length > 0 ? x.AtmName : x.AtmId).OrderByDescending(g => g.Count()).Take(8).Select(g => $"{g.Key} ({g.Count()})").ToList();
            hint = any.Count == 0 ? "KTC returned no transactions at all for this period."
                 : $"No transactions for ATM \"{atmId.Trim()}\" in this period. {any.Count} transactions came from: {string.Join(", ", top)}.";
        }
        return Ok(new
        {
            data = rows.Take(2000).Select(x => new { x.Id, x.AtmId, x.AtmName, x.Client, x.Region, x.Type, x.Status, x.Amount, x.Currency, x.TimeUtc, x.ResponseMs, x.ErrorCode, x.FailureCategory }),
            hint, total = all.Count, successful = all.Count - failed.Count, failed = failed.Count, truncated = rows.Count > 2000,
            avgResponseMs = all.Count == 0 ? 0 : (int)all.Average(x => x.ResponseMs),
            byCategory = failed.GroupBy(x => x.FailureCategory.Length == 0 ? "Other" : x.FailureCategory).ToDictionary(g => g.Key, g => g.Count()),
            byError = failed.GroupBy(x => x.ErrorCode).OrderByDescending(g => g.Count()).Take(10).Select(g => new { error = g.Key, count = g.Count() }),
            topAtms = failed.GroupBy(x => x.AtmName).OrderByDescending(g => g.Count()).Take(10).Select(g => new { atm = g.Key, count = g.Count() })
        });
    }

    // ---------- hardware ----------
    [HttpGet("hardware")]
    public async Task<IActionResult> Hardware(string atmId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(atmId)) return BadRequest(new { error = "Select an ATM." });
        var hw = await _data.GetHardwareAsync(atmId.Trim(), ct);
        if (hw == null) return NotFound(new { error = $"No ATM matches \"{atmId.Trim()}\"." });
        _log.LogInformation("Hardware status atm={Atm} -> {Id}: {Modules} components, overall {Status}", atmId, hw.AtmId, hw.Modules.Count, hw.Status);
        return Ok(hw);
    }

    // ---------- EJ ----------
    [HttpGet("ej")]
    [Audit("Search", "EJ search")]
    public async Task<IActionResult> Ej(string atmId, DateTime from, DateTime to, string? card, string? seq, string? text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(atmId)) return BadRequest(new { error = "ATM ID or name is required (EJ is retrieved per ATM)." });
        if (to < from || to - from > TimeSpan.FromDays(7)) return BadRequest(new { error = "Date range must be between 0 and 7 days." });
        var rows = await _data.SearchEjAsync(new EjQuery(atmId.Trim(), from.ToUniversalTime(), to.ToUniversalTime(), card, seq, text), ct);
        _log.LogInformation("EJ search atm={Atm} {From:u}..{To:u} card={Card} seq={Seq} text={Text}: {Rows} EJ lines",
            atmId, from.ToUniversalTime(), to.ToUniversalTime(), string.IsNullOrWhiteSpace(card) ? "-" : "(given)", seq, PanMasker.Mask(text), rows.Count);
        var tx = rows.GroupBy(r => r.TransactionNo).Select(g => new { no = g.Key, start = g.Min(x => x.TimeUtc), count = g.Count(), timeline = EjTimeline.Build(g) }).OrderBy(x => x.start).ToList();
        return Ok(new { entries = rows.Take(5000), truncated = rows.Count > 5000, transactions = tx.Take(200) });
    }

    // ---------- global search / rca ----------
    [HttpGet("search")]
    public async Task<IActionResult> Search(string q, CancellationToken ct)
    {
        if (PanMasker.LooksLikePan(q) && PanMasker.Digits(q).Length >= 13) return BadRequest(new { error = "Card numbers cannot be used in global search. Use the EJ Search portal." });
        return Ok(await _dash.GlobalSearchAsync(q, ct));
    }

    [HttpGet("rca")]
    public async Task<IActionResult> Rca(int days = 30, CancellationToken ct = default) => Ok(await _dash.GetRootCauseAsync(days, ct));

    // ---------- alerts ----------
    [HttpGet("alerts")]
    public async Task<IActionResult> Alerts(int days = 7, CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.AddDays(-Math.Clamp(days, 1, 90));
        var rows = await _uow.Repo<AlertHistory>().Query().Where(a => a.RaisedUtc >= since).OrderByDescending(a => a.RaisedUtc).Take(1000).ToListAsync(ct);
        return Ok(new { data = rows.Select(a => new { a.Id, severity = a.Severity.ToString(), type = a.RuleType.ToString(), a.Title, a.Message, a.RaisedUtc, a.AcknowledgedBy, a.AcknowledgedUtc, a.NotificationStatus }) });
    }

    [HttpPost("alerts/{id:int}/ack")]
    [Authorize(Policy = Policies.Operator)]
    public async Task<IActionResult> Ack(int id, CancellationToken ct)
    {
        var a = await _uow.Repo<AlertHistory>().GetAsync(id, ct);
        if (a == null) return NotFound();
        a.AcknowledgedBy = User.Identity!.Name; a.AcknowledgedUtc = DateTime.UtcNow;
        await _uow.SaveChangesAsync(ct);
        await _audit.LogAsync("Alert", "Acknowledge", id.ToString());
        return Ok();
    }

    // ---------- API health ----------
    [HttpGet("apihealth")]
    public IActionResult ApiHealth() => Ok(_health.Snapshot());

    // ---------- audit ----------
    [HttpGet("audit")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Audit(string? category, string? user, DateTime? from, DateTime? to, CancellationToken ct)
    {
        var q = _uow.Repo<AuditLog>().Query();
        if (!string.IsNullOrWhiteSpace(category)) q = q.Where(a => a.Category == category);
        if (!string.IsNullOrWhiteSpace(user)) q = q.Where(a => a.UserName.Contains(user));
        var f = (from ?? DateTime.UtcNow.AddDays(-7)).ToUniversalTime(); var t = (to ?? DateTime.UtcNow.AddDays(1)).ToUniversalTime();
        q = q.Where(a => a.TimestampUtc >= f && a.TimestampUtc <= t);
        return Ok(new { data = await q.OrderByDescending(a => a.TimestampUtc).Take(3000).Select(a => new { a.TimestampUtc, a.UserName, a.Category, a.Action, a.Target, a.Details, a.IpAddress, a.Success }).ToListAsync(ct) });
    }
}
