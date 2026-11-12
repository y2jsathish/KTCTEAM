using Ktc.Domain.Enums;

namespace Ktc.Application.Logic;

public record HealthInput(DateTime? LastCommunicationUtc, bool Offline, bool HostDisconnected,
    int OpenP1, int OpenP2, int OpenP3, int OpenP4, int OpenTickets = 0);

public record HealthResult(HealthStatus Status, int Score, string Reason);

/// <summary>
/// Implements the portal health rules:
///  ONLINE   (Green): no active incidents AND last communication &lt; 10 min
///  WARNING  (Amber): open P3/P4 incidents OR last communication &gt; 10 min
///  CRITICAL (Red)  : open P1/P2 incidents, device offline, or host disconnect
/// </summary>
public static class AtmHealthCalculator
{
    public static HealthResult Evaluate(HealthInput i, DateTime nowUtc, int onlineMinutes = 10)
    {
        var minutesSince = i.LastCommunicationUtc.HasValue ? (nowUtc - i.LastCommunicationUtc.Value).TotalMinutes : double.MaxValue;
        var stale = minutesSince > onlineMinutes;
        int score = 100;
        score -= Math.Min(i.OpenP1, 2) * 35;
        score -= Math.Min(i.OpenP2, 2) * 20;
        score -= Math.Min(i.OpenP3, 3) * 8;
        score -= Math.Min(i.OpenP4, 3) * 3;
        if (i.Offline) score -= 40;
        if (i.HostDisconnected) score -= 30;
        if (stale) score -= 15;
        score = Math.Clamp(score, 0, 100);

        if (i.Offline) return new(HealthStatus.Critical, Math.Min(score, 30), "Device offline");
        if (i.HostDisconnected) return new(HealthStatus.Critical, Math.Min(score, 35), "Host disconnect");
        if (i.OpenP1 > 0 || i.OpenP2 > 0) return new(HealthStatus.Critical, Math.Min(score, 40), $"{i.OpenP1 + i.OpenP2} open P1/P2 incident(s)");
        if (i.OpenP3 > 0 || i.OpenP4 > 0) return new(HealthStatus.Warning, Math.Min(score, 80), $"{i.OpenP3 + i.OpenP4} open P3/P4 incident(s)");
        if (stale) return new(HealthStatus.Warning, Math.Min(score, 85),
            i.LastCommunicationUtc.HasValue ? $"No communication for {(int)minutesSince} min" : "No communication recorded");
        return new(HealthStatus.Healthy, score, "Healthy");
    }
}
