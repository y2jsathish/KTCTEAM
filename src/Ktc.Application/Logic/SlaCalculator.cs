using Ktc.Domain.Enums;

namespace Ktc.Application.Logic;

public static class SlaCalculator
{
    /// <summary>Derive SLA state. <paramref name="completedUtc"/> is the resolution/close time, null while open.</summary>
    public static SlaState Evaluate(DateTime dueUtc, DateTime? completedUtc, DateTime nowUtc, int nearBreachMinutes = 60)
    {
        if (completedUtc.HasValue) return completedUtc.Value <= dueUtc ? SlaState.Achieved : SlaState.Breached;
        if (nowUtc > dueUtc) return SlaState.Breached;
        if (dueUtc - nowUtc <= TimeSpan.FromMinutes(nearBreachMinutes)) return SlaState.NearBreach;
        return SlaState.OnTrack;
    }

    public static double AchievedPercent(IEnumerable<SlaState> states)
    {
        var l = states.ToList();
        var judged = l.Count(s => s is SlaState.Achieved or SlaState.Breached or SlaState.OnTrack or SlaState.NearBreach);
        if (judged == 0) return 100;
        var bad = l.Count(s => s == SlaState.Breached);
        return Math.Round(100.0 * (judged - bad) / judged, 1);
    }
}
