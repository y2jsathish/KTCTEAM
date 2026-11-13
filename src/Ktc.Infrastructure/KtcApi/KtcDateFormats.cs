using System.Globalization;
using Ktc.Application.Options;

namespace Ktc.Infrastructure.KtcApi;

/// <summary>
/// KTC parses query-string dates with its own server-side rules: on the reference server even "2026-11-06" and ISO strings
/// fail with "FormatException: String was not recognized as a valid DateTime". The portal starts with Ktc:QueryDateFormat and,
/// when KTC rejects a date, tries the other common formats once and keeps the first one KTC accepts for the rest of the run.
/// </summary>
public static class KtcDateFormats
{
    public static readonly string[] Candidates =
    {
        "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss", "MM/dd/yyyy HH:mm:ss", "dd/MM/yyyy HH:mm:ss", "M/d/yyyy h:mm:ss tt",
        "yyyyMMddHHmmss", "yyyyMMdd", "MM/dd/yyyy", "dd/MM/yyyy", "dd-MMM-yyyy HH:mm:ss", "o", "ticks", "unix", "unixms"
    };

    private static string? _resolved;
    public static string? Resolved => Volatile.Read(ref _resolved);
    public static void Remember(string format) => Volatile.Write(ref _resolved, format);
    public static void Reset() => Volatile.Write(ref _resolved, null);

    /// <summary>The format to use now: the one KTC accepted, else the configured one.</summary>
    public static string Current(KtcOptions o) => Resolved ?? (string.IsNullOrWhiteSpace(o.QueryDateFormat) ? Candidates[0] : o.QueryDateFormat);

    /// <summary>Formats a UTC time with <paramref name="format"/> (a .NET format or ticks/unix/unixms), URL-escaped.</summary>
    public static string Format(DateTime utc, string format, string? timeZone)
    {
        var t = DateTime.SpecifyKind(utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : utc, DateTimeKind.Utc);
        switch (format)
        {
            case "unix": return new DateTimeOffset(t).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            case "unixms": return new DateTimeOffset(t).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        }
        if (!string.IsNullOrWhiteSpace(timeZone))
            try { t = DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(t, TimeZoneInfo.FindSystemTimeZoneById(timeZone)), DateTimeKind.Unspecified); }
            catch (TimeZoneNotFoundException) { } catch (InvalidTimeZoneException) { }
        if (format == "ticks") return t.Ticks.ToString(CultureInfo.InvariantCulture);
        return Uri.EscapeDataString(t.ToString(format, CultureInfo.InvariantCulture));
    }

    /// <summary>True when KTC's answer says it could not parse a date.</summary>
    public static bool IsDateError(Ktc.Application.Models.KtcResponse r) =>
        r.StatusCode == 500 && (r.Raw ?? r.Error ?? "").Contains("valid DateTime", StringComparison.OrdinalIgnoreCase);
}
