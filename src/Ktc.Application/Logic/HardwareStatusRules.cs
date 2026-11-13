using System.Text.RegularExpressions;

namespace Ktc.Application.Logic;

/// <summary>
/// Turns KTC component/property values (e.g. "Online", "MediaJammed", "Low") into OK / Warning / Fatal / Unknown,
/// and works out which component an active error belongs to.
/// </summary>
public static class HardwareStatusRules
{
    public const string Ok = "OK", Warning = "Warning", Fatal = "Fatal", Unknown = "Unknown";

    private static readonly Regex FatalRx = new(@"fatal|fault|fail|error|jam|offline|off-?line|inoperat|not\s*(present|available|connected|ok)|missing|hardware\s*error|power\s*off|nodevice|no\s*device|out\b|empty|broken|stopped|unavailable", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WarnRx = new(@"warn|low|open|degrad|attention|near|busy|suspend|maint|unknown\s*state|partial|almost|high|full|retract|manip", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OkRx = new(@"^\s*(ok|okay|good|healthy|online|on-?line|ready|normal|present|available|connected|in\s*service|idle|true|yes|0|none|no\s*error|operational|working|closed|locked|inserted)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Status of one property value. "Paper: Low" is a warning, "MediaJammed" is fatal, "Online" is OK.</summary>
    public static string Classify(string? value)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) return Unknown;
        if (OkRx.IsMatch(v)) return Ok;
        // "NoError", "Not jammed" style values are healthy even though they contain a fatal word.
        if (Regex.IsMatch(v, @"^(no\s*error|not\s*jammed|no\s*fault|nofault|noerror)$", RegexOptions.IgnoreCase)) return Ok;
        if (FatalRx.IsMatch(v)) return Fatal;
        if (WarnRx.IsMatch(v)) return Warning;
        return Unknown;
    }

    public static int Rank(string s) => s switch { Fatal => 3, Warning => 2, Ok => 1, _ => 0 };

    /// <summary>The worst status in the list; Unknown only when nothing is known.</summary>
    public static string Worst(IEnumerable<string> statuses)
    {
        var best = Unknown; var rank = 0;
        foreach (var s in statuses) { var r = Rank(s); if (r > rank) { rank = r; best = s; } }
        return best;
    }

    /// <summary>Severity of an active error (1 = most severe) as a component status.</summary>
    public static string FromSeverity(int level) => level <= 2 ? Fatal : Warning;

    /// <summary>Common names of ATM hardware components, used to pin an error text to a component.</summary>
    public static readonly (string Module, string[] Words)[] Keywords =
    {
        ("Card reader", new[] { "card reader", "cardreader", "idc", "card capture", "card retain", "chip", "emv", "mcr" }),
        ("Cash dispenser", new[] { "dispenser", "cash dispens", "cdm", "cassette", "cash unit", "notes", "note ", "retract", "presenter", "shutter", "reject bin", "purge" }),
        ("Cash acceptor", new[] { "acceptor", "deposit", "cash in", "cim", "escrow", "recycl" }),
        ("Receipt printer", new[] { "receipt", "rpr", "paper" }),
        ("Journal printer", new[] { "journal", "jpr" }),
        ("PIN pad", new[] { "pin pad", "pinpad", "epp", "encrypt" }),
        ("Cheque module", new[] { "cheque", "check", "ipm" }),
        ("Sensors / alarms", new[] { "sensor", "alarm", "vandal", "door", "safe", "siu", "tamper" }),
        ("Network / host", new[] { "host", "network", "communication", "comms", "offline", "connection" }),
        ("Application", new[] { "application", "software", "agent", "xfs", "service" }),
    };

    /// <summary>True when <paramref name="text"/> refers to <paramref name="moduleName"/> (by its own name or a known keyword).</summary>
    public static bool Mentions(string text, string moduleName)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(moduleName)) return false;
        if (text.Contains(moduleName, StringComparison.OrdinalIgnoreCase)) return true;
        var key = Normal(moduleName);
        foreach (var (module, words) in Keywords)
            if (Normal(module) == key || words.Any(w => key.Contains(Normal(w))))
                if (words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase))) return true;
        return false;
    }

    /// <summary>The standard module an error text belongs to, or null.</summary>
    public static string? ModuleFor(string text) =>
        Keywords.FirstOrDefault(k => k.Words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase))).Module;

    private static string Normal(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
