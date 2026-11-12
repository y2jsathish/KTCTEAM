using System.Text;
using ClosedXML.Excel;
using Ktc.Application.Abstractions;
using Ktc.Application.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Ktc.Infrastructure.Services;

public class ExportService : IExportService
{
    static ExportService() { QuestPDF.Settings.License = LicenseType.Community; }   // review QuestPDF licence terms for your organisation size

    public byte[] ToExcel(ReportData d)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(Sheet(d.Title));
        int r = 1;
        ws.Cell(r, 1).Value = d.Title; ws.Cell(r, 1).Style.Font.Bold = true; ws.Cell(r, 1).Style.Font.FontSize = 14; r++;
        ws.Cell(r++, 1).Value = $"Generated {d.GeneratedUtc:yyyy-MM-dd HH:mm} UTC" + (d.Subtitle != null ? " | " + d.Subtitle : "");
        foreach (var (l, v) in d.Summary) { ws.Cell(r, 1).Value = l; ws.Cell(r, 1).Style.Font.Bold = true; ws.Cell(r++, 2).Value = Safe(v); }
        r++;
        int hr = r;
        for (int c = 0; c < d.Columns.Count; c++) { var cell = ws.Cell(r, c + 1); cell.Value = d.Columns[c]; cell.Style.Font.Bold = true; cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#1f2a44"); cell.Style.Font.FontColor = XLColor.White; }
        foreach (var row in d.Rows) { r++; for (int c = 0; c < row.Count; c++) ws.Cell(r, c + 1).SetValue(Safe(row[c])); }
        if (d.Rows.Count > 0) ws.Range(hr, 1, r, d.Columns.Count).SetAutoFilter();
        ws.Columns().AdjustToContents(1, Math.Min(r, 200)); ws.SheetView.FreezeRows(hr);
        using var ms = new MemoryStream(); wb.SaveAs(ms); return ms.ToArray();
    }

    public byte[] ToCsv(ReportData d)
    {
        var sb = new StringBuilder("﻿");
        sb.AppendLine(string.Join(",", d.Columns.Select(Csv)));
        foreach (var row in d.Rows) sb.AppendLine(string.Join(",", row.Select(x => Csv(Safe(x)))));
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public byte[] ToPdf(ReportData d)
    {
        return Document.Create(doc => doc.Page(p =>
        {
            p.Size(d.Columns.Count > 7 ? PageSizes.A4.Landscape() : PageSizes.A4); p.Margin(24); p.DefaultTextStyle(t => t.FontSize(8.5f));
            p.Header().Column(c =>
            {
                c.Item().Text(d.Title).FontSize(16).Bold().FontColor("#1f2a44");
                c.Item().Text($"Generated {d.GeneratedUtc:yyyy-MM-dd HH:mm} UTC" + (d.Subtitle != null ? "  |  " + d.Subtitle : "")).FontColor(Colors.Grey.Darken1);
            });
            p.Content().PaddingVertical(8).Column(c =>
            {
                if (d.Summary.Count > 0)
                    c.Item().PaddingBottom(8).Row(r => { foreach (var (l, v) in d.Summary) r.RelativeItem().Border(0.5f).BorderColor(Colors.Grey.Lighten1).Padding(5).Column(k => { k.Item().Text(l).FontSize(7).FontColor(Colors.Grey.Darken1); k.Item().Text(v).FontSize(12).Bold(); }); });
                c.Item().Table(t =>
                {
                    t.ColumnsDefinition(cd => { foreach (var _ in d.Columns) cd.RelativeColumn(); });
                    t.Header(h => { foreach (var col in d.Columns) h.Cell().Background("#1f2a44").Padding(3).Text(col).FontColor(Colors.White).Bold(); });
                    foreach (var row in d.Rows.Take(2000)) foreach (var cell in row) t.Cell().BorderBottom(0.25f).BorderColor(Colors.Grey.Lighten2).Padding(2).Text(cell);
                });
                if (d.Rows.Count > 2000) c.Item().PaddingTop(6).Text($"Showing first 2000 of {d.Rows.Count} rows - use Excel/CSV export for the full data set.").Italic();
            });
            p.Footer().AlignCenter().Text(x => { x.Span("Page "); x.CurrentPageNumber(); x.Span(" / "); x.TotalPages(); });
        })).GeneratePdf();
    }

    private static string Sheet(string s) { var n = new string(s.Where(ch => !"[]:*?/\\".Contains(ch)).ToArray()); return n.Length > 31 ? n[..31] : n.Length == 0 ? "Report" : n; }
    /// <summary>Neutralise spreadsheet formula injection (values coming from ATM journals / ticket text are untrusted).</summary>
    private static string Safe(string? v) => string.IsNullOrEmpty(v) ? "" : "=+-@\t\r".Contains(v[0]) && !decimal.TryParse(v, out _) ? "'" + v : v;
    private static string Csv(string v) => v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
}
