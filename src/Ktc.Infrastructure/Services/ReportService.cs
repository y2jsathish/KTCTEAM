using System.Globalization;
using Ktc.Application.Abstractions;
using Ktc.Application.Logic;
using Ktc.Application.Models;
using Ktc.Domain.Entities;
using Ktc.Domain.Enums;
using Ktc.Infrastructure.KtcApi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Ktc.Infrastructure.Services;

public class ReportService : IReportService
{
    private readonly IKtcDataService _data; private readonly IDashboardService _dash; private readonly IExportService _export;
    private readonly IUnitOfWork _uow; private readonly INotificationSender _notify; private readonly ILogger<ReportService> _log;

    public ReportService(IKtcDataService data, IDashboardService dash, IExportService export, IUnitOfWork uow, INotificationSender notify, ILogger<ReportService> log)
    { _data = data; _dash = dash; _export = export; _uow = uow; _notify = notify; _log = log; }

    private static string F(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);
    private static string T(DateTime? d) => d?.ToString("yyyy-MM-dd HH:mm") ?? "";

    public async Task<ReportData> BuildAsync(ReportType type, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var period = $"{from:yyyy-MM-dd HH:mm} to {to:yyyy-MM-dd HH:mm} UTC";
        var snap = await _dash.GetSnapshotAsync(false, ct);
        switch (type)
        {
            case ReportType.AtmAvailability:
            {
                var rows = snap.Atms.OrderBy(a => a.AvailabilityPct ?? 101).Select(a => (IReadOnlyList<string>)new[]
                    { a.Name, a.Client, a.Region, a.City, a.DeviceStatus, a.AvailabilityPct.HasValue ? F(a.AvailabilityPct.Value) : "n/a", a.HealthScore.ToString(), a.OpenIncidents.ToString(), T(a.LastCommunicationUtc) }).ToList();
                return new("ATM Availability Report", DateTime.UtcNow, "Current status; availability over the configured look-back",
                    new[] { "ATM", "Client", "Region", "City", "Status", "Availability %", "Health score", "Open incidents", "Last communication (UTC)" }, rows,
                    new[] { ("Fleet availability %", F(snap.AvailabilityPct)), ("ATMs", snap.TotalAtms.ToString()), ("Offline", snap.OfflineAtms.ToString()), ("Critical", snap.CriticalAtms.ToString()) });
            }
            case ReportType.Incident:
            {
                var hist = await _data.GetHistoricalIncidentsAsync(from, to, null, ct);
                var all = snap.Incidents.Where(i => i.OpenedUtc >= from && i.OpenedUtc <= to).Concat(hist).DistinctBy(i => i.Key).OrderByDescending(i => i.OpenedUtc).ToList();
                var rows = all.Select(i => (IReadOnlyList<string>)new[] { i.Number, i.AtmName, i.Client, i.Severity.ToString(), T(i.OpenedUtc), T(i.ClosedUtc),
                    F(((i.ClosedUtc ?? DateTime.UtcNow) - i.OpenedUtc).TotalMinutes), i.Description, i.Sla.ToString() }).ToList();
                return new("Incident Report", DateTime.UtcNow, period, new[] { "Incident", "ATM", "Client", "Severity", "Opened (UTC)", "Closed (UTC)", "Duration (min)", "Description", "SLA" }, rows,
                    new[] { ("Incidents", all.Count.ToString()), ("P1", all.Count(i => i.Severity == Severity.P1).ToString()), ("P2", all.Count(i => i.Severity == Severity.P2).ToString()), ("Still open", all.Count(i => i.IsOpen).ToString()) });
            }
            case ReportType.TicketSummary:
            {
                var tk = (await _data.SearchTicketsAsync(new TicketQuery(From: from, To: to), ct)).ToList();
                var rows = tk.Select(t => (IReadOnlyList<string>)new[] { t.Id, t.AtmName, t.Client, t.Status.ToString(), t.Severity.ToString(), T(t.CreatedUtc), T(t.ClosedUtc), t.Owner ?? "", t.Sla.ToString() }).ToList();
                return new("Ticket Summary", DateTime.UtcNow, period, new[] { "Ticket", "ATM", "Client", "Status", "Severity", "Created (UTC)", "Closed (UTC)", "Owner", "SLA" }, rows,
                    Enum.GetValues<TicketStatus>().Select(s => (s.ToString(), tk.Count(t => t.Status == s).ToString())).Prepend(("Total", tk.Count.ToString())).ToList());
            }
            case ReportType.Transaction:
            {
                var tx = await _data.SearchTransactionsAsync(new TransactionQuery(From: from, To: to), ct);
                var rows = tx.GroupBy(t => (t.Client, t.Type)).OrderBy(g => g.Key.Client).ThenBy(g => g.Key.Type).Select(g => (IReadOnlyList<string>)new[]
                    { g.Key.Client, g.Key.Type, g.Count().ToString(), g.Count(t => t.Success).ToString(), g.Count(t => !t.Success).ToString(), F(100.0 * g.Count(t => !t.Success) / g.Count()), F(g.Average(t => t.ResponseMs)) }).ToList();
                var cat = tx.Where(t => !t.Success).GroupBy(t => t.FailureCategory).Select(g => ($"Fail: {g.Key}", g.Count().ToString())).ToList();
                return new("Transaction Report", DateTime.UtcNow, period, new[] { "Client", "Type", "Total", "Successful", "Failed", "Failure %", "Avg response (ms)" }, rows,
                    new[] { ("Total", tx.Count.ToString()), ("Failed", tx.Count(t => !t.Success).ToString()), ("Success %", tx.Count == 0 ? "n/a" : F(100.0 * tx.Count(t => t.Success) / tx.Count)) }.Concat(cat).ToList());
            }
            case ReportType.Sla:
            {
                var tk = await _data.SearchTicketsAsync(new TicketQuery(From: from, To: to), ct);
                var rows = tk.GroupBy(t => string.IsNullOrEmpty(t.Client) ? "Unassigned" : t.Client).OrderBy(g => g.Key).Select(g => (IReadOnlyList<string>)new[]
                    { g.Key, g.Count().ToString(), g.Count(t => t.Sla == SlaState.Achieved).ToString(), g.Count(t => t.Sla == SlaState.Breached).ToString(), g.Count(t => t.Sla == SlaState.NearBreach).ToString(), F(SlaCalculator.AchievedPercent(g.Select(t => t.Sla))) }).ToList();
                return new("SLA Report", DateTime.UtcNow, period, new[] { "Client", "Tickets", "SLA achieved", "SLA breached", "Near breach", "SLA %" }, rows,
                    new[] { ("Overall SLA %", F(SlaCalculator.AchievedPercent(tk.Select(t => t.Sla)))), ("Breached", tk.Count(t => t.Sla == SlaState.Breached).ToString()), ("Near breach", tk.Count(t => t.Sla == SlaState.NearBreach).ToString()) });
            }
            default:
            {
                var rows = snap.Clients.Select(c => (IReadOnlyList<string>)new[] { c.Name, c.Atms.ToString(), c.Online.ToString(), F(c.AvailabilityPct), c.OpenIncidents.ToString(), c.OpenTickets.ToString(), F(c.SlaPct), c.Critical.ToString() }).ToList();
                return new("Executive Summary", DateTime.UtcNow, "Point-in-time operational overview", new[] { "Client", "ATMs", "Online", "Availability %", "Open incidents", "Open tickets", "SLA %", "Critical ATMs" }, rows,
                    new[] { ("Availability %", F(snap.AvailabilityPct)), ("SLA %", F(snap.SlaPct)), ("Critical incidents", snap.CriticalIncidents.ToString()), ("Open tickets", snap.OpenTickets.ToString()), ("Transactions today", snap.TransactionsToday.ToString()), ("Failed today", snap.FailedTransactions.ToString()) });
            }
        }
    }

    public Task<(byte[] Data, string FileName, string MimeType)> ExportAsync(ReportData d, ExportFormat f)
    {
        var name = new string(d.Title.Where(char.IsLetterOrDigit).ToArray()) + "_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmm");
        return Task.FromResult(f switch
        {
            ExportFormat.Excel => (_export.ToExcel(d), name + ".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
            ExportFormat.Csv => (_export.ToCsv(d), name + ".csv", "text/csv"),
            _ => (_export.ToPdf(d), name + ".pdf", "application/pdf")
        });
    }

    public async Task<int> RunDueSchedulesAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var due = await _uow.Repo<ReportSchedule>().Query().Where(s => s.Enabled && s.NextRunUtc <= now).ToListAsync(ct);
        int ran = 0;
        foreach (var s in due)
        {
            try
            {
                using var scope = KtcTokenScope.ForceService();
                var span = s.Frequency switch { ReportFrequency.Daily => TimeSpan.FromDays(1), ReportFrequency.Weekly => TimeSpan.FromDays(7), _ => TimeSpan.FromDays(30) };
                var data = await BuildAsync(s.ReportType, now - span, now, ct);
                var file = await ExportAsync(data, s.Format);
                var to = s.Recipients.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                await _notify.SendEmailAsync(to, $"[KTC Portal] {s.Name} ({s.Frequency})", $"<p>Scheduled report <b>{System.Net.WebUtility.HtmlEncode(s.Name)}</b> is attached.</p>", (file.FileName, file.Data, file.MimeType), ct);
                s.LastStatus = "OK"; ran++;
            }
            catch (Exception ex) { _log.LogError(ex, "Scheduled report {Name} failed", s.Name); s.LastStatus = "Failed: " + (ex.Message.Length > 150 ? ex.Message[..150] : ex.Message); }
            s.LastRunUtc = now;
            do s.NextRunUtc = s.Frequency switch { ReportFrequency.Daily => s.NextRunUtc.AddDays(1), ReportFrequency.Weekly => s.NextRunUtc.AddDays(7), _ => s.NextRunUtc.AddMonths(1) }; while (s.NextRunUtc <= now);
            _uow.Repo<ReportSchedule>().Update(s);
            await _uow.SaveChangesAsync(ct);
        }
        return ran;
    }
}
