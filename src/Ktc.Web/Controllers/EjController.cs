using Ktc.Application.Abstractions;
using Ktc.Application.Models;
using Ktc.Domain.Enums;
using Ktc.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Ktc.Web.Controllers;

public class EjController : Controller
{
    private readonly IKtcDataService _data; private readonly IReportService _reports; private readonly IAuditService _audit;
    public EjController(IKtcDataService d, IReportService r, IAuditService a) { _data = d; _reports = r; _audit = a; }

    public IActionResult Index() => View();

    /// <summary>Export the (masked) journal rows currently searched. PANs are masked in every format.</summary>
    [HttpGet, Audit("Report", "EJ export")]
    public async Task<IActionResult> Export(string atmId, DateTime from, DateTime to, string? card, string? seq, string? text, string format = "csv", CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(atmId)) return BadRequest("atmId is required");
        if (to < from || to - from > TimeSpan.FromDays(7)) return BadRequest("Date range must be 7 days or less.");
        var rows = await _data.SearchEjAsync(new EjQuery(atmId, from.ToUniversalTime(), to.ToUniversalTime(), card, seq, text), ct);
        var rd = new ReportData($"EJ {atmId}", DateTime.UtcNow, $"{from:u} - {to:u} (PAN masked)", new[] { "Sequence", "Time (UTC)", "Transaction #", "Journal text" },
            rows.Select(r => (IReadOnlyList<string>)new[] { r.Sequence, r.TimeUtc.ToString("yyyy-MM-dd HH:mm:ss"), r.TransactionNo.ToString(), r.Text }).ToList(),
            new[] { ("Entries", rows.Count.ToString()), ("Transactions", rows.Select(r => r.TransactionNo).Distinct().Count().ToString()) });
        var f = format.ToLowerInvariant() switch { "xlsx" or "excel" => ExportFormat.Excel, "pdf" => ExportFormat.Pdf, _ => ExportFormat.Csv };
        var file = await _reports.ExportAsync(rd, f);
        return File(file.Data, file.MimeType, file.FileName);
    }
}
