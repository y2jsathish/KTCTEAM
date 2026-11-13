using System.Globalization;
using Ktc.Application.Options;

namespace Ktc.Infrastructure.KtcApi;

/// <summary>
/// KTC expects query-string dates as yyyyMMddHHmmssfff (e.g. 20261113120000000). Other formats such as "2026-11-06" or ISO strings
/// fail with "FormatException: String was not recognized as a valid DateTime". The portal starts with Ktc:QueryDateFormat and,
/// when KTC rejects a date, tries the other common formats once and keeps the first one KTC accepts for the rest of the run.
/// </summary>
public static class KtcDateFormats
{
    public static readonly string[] Candidates =
    {
        "yyyyMMddHHmmssfff", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss", "MM/dd/yyyy HH:mm:ss", "dd/MM/yyyy HH:mm:ss", "M/d/yyyy h:mm:ss tt",
        "yyyyMMddHHmmss", "yyyyMMdd", "MM/dd/yyyy", "dd/MM/yyyy", "dd-MMM-yyyy HH:mm:ss", "o", "ticks", "unix", "unixms"
    };

    private static string? _resolved;
    public static string? Resolved => Volatile.Read(ref _resolved);
    public static void Remember(string format) => Volatile.Write(ref _resolved, format);
    public static void Reset() { Volatile.Write(ref _resolved, null); Volatile.Write(ref _resolvedBody, null); }

    /// <summary>The format to use now: the one KTC accepted, else the configured one.</summary>
    public static string Current(KtcOptions o) => Resolved ?? (string.IsNullOrWhiteSpace(o.QueryDateFormat) ? Candidates[0] : o.QueryDateFormat);

    /// <summary>Formats a UTC time with <paramref name="format"/> (a .NET format or ticks/unix/unixms/msdate), URL-escaped.</summary>
    public static string Format(DateTime utc, string format, string? timeZone) => Uri.EscapeDataString(Raw(utc, format, timeZone));

    /// <summary>Same as <see cref="Format"/> but not URL-escaped, for JSON bodies.</summary>
    public static string Raw(DateTime utc, string format, string? timeZone)
    {
        var t = DateTime.SpecifyKind(utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : utc, DateTimeKind.Utc);
        switch (format)
        {
            case "unix": return new DateTimeOffset(t).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            case "unixms": return new DateTimeOffset(t).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            case "msdate": return "/Date(" + new DateTimeOffset(t).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + ")/";
        }
        // Formats that state UTC themselves ("o", a literal Z) are never shifted to the KTC time zone.
        var utcFormat = format == "o" || format.Contains("'Z'") || format.EndsWith("Z", StringComparison.Ordinal);
        if (!utcFormat && !string.IsNullOrWhiteSpace(timeZone))
            try { t = DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(t, TimeZoneInfo.FindSystemTimeZoneById(timeZone)), DateTimeKind.Unspecified); }
            catch (TimeZoneNotFoundException) { } catch (InvalidTimeZoneException) { }
        if (format == "ticks") return t.Ticks.ToString(CultureInfo.InvariantCulture);
        return t.ToString(format, CultureInfo.InvariantCulture);
    }

    // ---- HistoricalError: KTC takes the range as a JSON body DateTimePair { dt1, dt2 } on the GET, not in the URL ----

    /// <summary>Date formats tried for the dt1/dt2 body of HistoricalError, best guess first.</summary>
    public static readonly string[] BodyCandidates = { "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss'Z'", "o", "yyyyMMddHHmmssfff", "msdate" };

    private static string? _resolvedBody;
    public static string? ResolvedBody => Volatile.Read(ref _resolvedBody);
    public static void RememberBody(string format) => Volatile.Write(ref _resolvedBody, format);
    public static string CurrentBody => ResolvedBody ?? BodyCandidates[0];

    /// <summary>The DateTimePair body KTC's HistoricalError routes read: {"dt1": from, "dt2": to}.</summary>
    public static string DateTimePair(DateTime fromUtc, DateTime toUtc, string format, string? timeZone) =>
        System.Text.Json.JsonSerializer.Serialize(new { dt1 = Raw(fromUtc, format, timeZone), dt2 = Raw(toUtc, format, timeZone) });

    /// <summary>True for KTC's HistoricalError routes (global and per client), which need a DateTimePair body.</summary>
    public static bool IsHistoricalError(string relativeUrl) =>
        System.Text.RegularExpressions.Regex.IsMatch(relativeUrl.TrimStart('/').Split('?')[0], @"^api/v1/(Client/[^/]+/)?HistoricalError$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>True when KTC's answer says it could not parse a date.</summary>
    public static bool IsDateError(Ktc.Application.Models.KtcResponse r) =>
        r.StatusCode == 500 && (r.Raw ?? r.Error ?? "").Contains("valid DateTime", StringComparison.OrdinalIgnoreCase);
}
