using Ktc.Application.Options;
using Microsoft.Extensions.Options;

namespace Ktc.Infrastructure.Services;

/// <summary>Operational "today" and hour buckets use the portal time zone (default Asia/Dubai), not the server's.</summary>
public class PortalClock
{
    public TimeZoneInfo Zone { get; }
    public PortalClock(IOptions<PortalOptions> o)
    {
        try { Zone = TimeZoneInfo.FindSystemTimeZoneById(o.Value.TimeZoneId); } catch { Zone = TimeZoneInfo.Utc; }
    }
    public DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);
    public DateTime TodayStartUtc() => TimeZoneInfo.ConvertTimeToUtc(ToLocal(DateTime.UtcNow).Date, Zone);
    public DateTime LocalDateToUtc(DateTime local) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Zone);
}
