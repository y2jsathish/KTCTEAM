using Ktc.Application.Models;

namespace Ktc.Application.Logic;

public static class EjTimeline
{
    private static readonly string[] Bad = { "fail", "error", "timeout", "declin", "retract", "reversal", "jam", "fault", "captured" };
    private static readonly string[] Key = { "transaction start", "card inserted", "card read", "pin", "service", "amount", "host request", "host response", "dispens", "present", "taken", "card returned", "transaction end", "reversal", "retract" };

    /// <summary>Groups journal rows into transactions (a new one starts at TRANSACTION START / CARD INSERTED) and numbers them.</summary>
    public static List<EjEntryDto> Number(IEnumerable<(string Seq, DateTime Time, string Text)> rows)
    {
        var res = new List<EjEntryDto>(); int tx = 0; bool inTx = false;
        foreach (var r in rows.OrderBy(x => x.Time).ThenBy(x => x.Seq, StringComparer.Ordinal))
        {
            var t = r.Text.ToLowerInvariant();
            if (t.Contains("transaction start") || (t.Contains("card inserted") && !inTx)) { tx++; inTx = true; }
            else if (!inTx) tx = Math.Max(tx, 1);
            res.Add(new EjEntryDto(r.Seq, r.Time, PanMasker.Mask(r.Text), tx));
            if (t.Contains("transaction end") || t.Contains("card returned") && t.Contains("fail")) inTx = false;
        }
        return res;
    }

    public static List<TimelineStep> Build(IEnumerable<EjEntryDto> entries, int? transactionNo = null)
    {
        return entries.Where(e => transactionNo == null || e.TransactionNo == transactionNo)
            .Where(e => Key.Any(k => e.Text.Contains(k, StringComparison.OrdinalIgnoreCase)) || Bad.Any(k => e.Text.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .Select(e =>
            {
                var bad = Bad.Any(k => e.Text.Contains(k, StringComparison.OrdinalIgnoreCase));
                var ok = e.Text.Contains("success", StringComparison.OrdinalIgnoreCase) || e.Text.Contains("approved", StringComparison.OrdinalIgnoreCase);
                var idx = e.Text.IndexOf(':');
                var label = idx > 0 && idx < 28 ? e.Text[..idx].Trim() : e.Text.Length > 32 ? e.Text[..32] : e.Text;
                return new TimelineStep(e.TimeUtc, label, e.Text, bad ? "danger" : ok ? "success" : "info");
            }).ToList();
    }
}
