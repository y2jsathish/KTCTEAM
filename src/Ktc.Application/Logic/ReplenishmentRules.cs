using Ktc.Application.Models;

namespace Ktc.Application.Logic;

/// <summary>Replenishment history of one ATM from its transactions (supervisor operations recorded by KTC).</summary>
public static class ReplenishmentRules
{
    /// <summary>
    /// One event per session holding a replenishment / clear-cash / add-cash operation, newest first. Each event counts the customer
    /// transactions from its start until the next event (or <paramref name="nowUtc"/>), with successful withdrawals and deposits and their amounts.
    /// </summary>
    public static List<ReplenishmentEvent> History(IReadOnlyCollection<TransactionDto> tx, DateTime nowUtc)
    {
        var sessions = tx.Where(t => CounterRules.IsResetType(t.Type))
            .GroupBy(t => t.SessionId.Length > 0 ? "s" + t.SessionId : "t" + t.Id)
            .Select(g =>
            {
                var sid = g.First().SessionId;
                var ops = (sid.Length > 0 ? tx.Where(t => t.SessionId == sid) : g).OrderBy(t => t.TimeUtc).ToList();
                return (Start: ops.Min(t => t.TimeUtc), Sid: sid, Ops: ops);
            })
            .OrderBy(s => s.Start).ToList();
        var customer = tx.Where(t => !CounterRules.IsSupervisorType(t.Type)).ToList();
        var res = new List<ReplenishmentEvent>();
        for (int i = 0; i < sessions.Count; i++)
        {
            var (start, sid, ops) = sessions[i];
            DateTime? next = i + 1 < sessions.Count ? sessions[i + 1].Start : null;
            var cycle = customer.Where(t => t.TimeUtc >= start && t.TimeUtc < (next ?? nowUtc.AddMinutes(1))).ToList();
            var wd = cycle.Where(t => t.Success && CounterRules.CountersFor(t).Contains("Cash withdrawals")).ToList();
            var dep = cycle.Where(t => t.Success && CounterRules.CountersFor(t).Contains("Cash deposits")).ToList();
            res.Add(new ReplenishmentEvent(start, next, sid, ops.Select(t => t.Type).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), ops.All(t => t.Success),
                cycle.Count, wd.Count, wd.Sum(t => t.Amount ?? 0), dep.Count, dep.Sum(t => t.Amount ?? 0)));
        }
        res.Reverse();
        return res;
    }
}
