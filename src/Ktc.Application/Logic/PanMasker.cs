using System.Text.RegularExpressions;

namespace Ktc.Application.Logic;

/// <summary>PCI-DSS style masking: show at most first 6 and last 4 digits of a PAN.</summary>
public static partial class PanMasker
{
    // 13-19 digits, optionally grouped by space/dash, not part of a longer number
    [GeneratedRegex(@"(?<![\d])(\d[ -]?){12,18}\d(?![\d])")]
    private static partial Regex PanRegex();

    // Already partially masked PANs such as 123456******7890 or 1234 56** **** 7890
    [GeneratedRegex(@"(?<![\dA-Za-z])\d{4,6}[\dXx\*]{4,12}\d{4}(?![\dA-Za-z])")]
    private static partial Regex MaskedRegex();

    public static string Mask(string? input)
    {
        if (string.IsNullOrEmpty(input)) return input ?? "";
        return PanRegex().Replace(input, m =>
        {
            var digits = new string(m.Value.Where(char.IsDigit).ToArray());
            if (!PassesLuhn(digits) && digits.Length < 15) return m.Value;      // avoid masking ids/timestamps that are not PANs
            return MaskDigits(digits);
        });
    }

    public static string MaskDigits(string digits)
    {
        if (digits.Length < 13) return new string('*', digits.Length);
        return digits[..6] + new string('*', digits.Length - 10) + digits[^4..];
    }

    public static bool PassesLuhn(string digits)
    {
        int sum = 0; bool alt = false;
        for (int i = digits.Length - 1; i >= 0; i--)
        {
            int n = digits[i] - '0';
            if (alt) { n *= 2; if (n > 9) n -= 9; }
            sum += n; alt = !alt;
        }
        return digits.Length > 0 && sum % 10 == 0;
    }

    /// <summary>True when the user's search term looks like a card number (so it can be matched against raw data without echoing it).</summary>
    public static bool LooksLikePan(string? s) => !string.IsNullOrWhiteSpace(s) && s.Count(char.IsDigit) >= 6 && s.All(c => char.IsDigit(c) || c is ' ' or '-' or '*');

    public static string Digits(string s) => new(s.Where(char.IsDigit).ToArray());
}
