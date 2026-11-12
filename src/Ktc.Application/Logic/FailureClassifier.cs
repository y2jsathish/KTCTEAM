namespace Ktc.Application.Logic;

public static class FailureClassifier
{
    public const string Host = "Host Failure", Comm = "Communication Failure", Retract = "Cash Retract", Dispense = "Dispense Failure", Other = "Other";

    public static string Classify(string? text)
    {
        var t = (text ?? "").ToLowerInvariant();
        if (t.Length == 0) return Other;
        if (t.Contains("retract") || t.Contains("captured") || t.Contains("swallow") || t.Contains("not taken") || t.Contains("timeout taking")) return Retract;
        if (t.Contains("dispens") || t.Contains("cash handler") || t.Contains("cashhandler") || t.Contains("jam") || t.Contains("present") || t.Contains("cassette") || t.Contains("shutter")) return Dispense;
        if (t.Contains("host") || t.Contains("authori") || t.Contains("declin") || t.Contains("switch") || t.Contains("issuer")) return Host;
        if (t.Contains("comm") || t.Contains("network") || t.Contains("connection") || t.Contains("link") || t.Contains("socket") || t.Contains("offline")) return Comm;
        return Other;
    }

    public static bool IsHostRelated(string? text) => Classify(text) == Host || (text ?? "").Contains("disconnect", StringComparison.OrdinalIgnoreCase) && (text ?? "").Contains("host", StringComparison.OrdinalIgnoreCase);
}
