using System.Text.Json;
using System.Text.RegularExpressions;
using Ktc.Application.Models;

namespace Ktc.Application.Logic;

/// <summary>Reads KTC's command types and maps the planned ATM actions to them. Pure and unit-tested.</summary>
public static partial class CommandTypeRules
{
    /// <summary>The planned Actions tab buttons (key, label, what it will do, name patterns that suggest a KTC command).</summary>
    public static readonly (string Key, string Label, string Description, Regex Looks)[] Planned =
    {
        ("Reboot", "Reboot", "Restart the ATM's operating system, after the current transaction and with the ATM out of service.",
            new(@"reboot|restart.*(pc|os|machine|system|atm)|^restart$", RegexOptions.IgnoreCase)),
        ("XfsReset", "XFS reset", "Reset all XFS devices of the ATM.", new(@"xfs.*reset|reset.*xfs|reset.*all.*device", RegexOptions.IgnoreCase)),
        ("DeviceReset", "Device reset", "Reset one XFS device (dispenser, card reader, printer, ...).",
            new(@"device.*reset|reset.*(device|component|module)|^reset$", RegexOptions.IgnoreCase)),
        ("OutOfService", "Out of service", "Take the ATM out of service (step before a reboot).", new(@"out.?of.?service|\boos\b|go.?offline|close.?atm|supervisor.?mode", RegexOptions.IgnoreCase)),
        ("InService", "In service", "Put the ATM back in service (step after a reboot).", new(@"in.?service|go.?online|open.?atm", RegexOptions.IgnoreCase)),
    };

    /// <summary>Planned actions (by label) that are never sent from the generic form.</summary>
    private static readonly HashSet<string> GuidedOnly = new(StringComparer.OrdinalIgnoreCase) { "Reboot" };

    private static readonly string[] IdF = { "ID", "Id", "CommandID", "CommandTypeID" };
    private static readonly string[] NameF = { "CommandName", "Name" };
    private static readonly string[] Known =
        { "ID", "CommandID", "CommandTypeID", "CommandName", "Name", "Description", "Category", "ParameterListTemplateString", "ParameterListTemplate",
          "SubCommandListString", "SubCommandList", "AllowClientInitiation", "MaxRetries", "RetryWaitTime", "MaxSimultaneous", "TargetTypeByte", "TargetType",
          "IsUserDefined", "DualAuthModeByte", "DualAuthMode" };

    // name="password" value="x" | <Password>x</Password> | password=x : the value is hidden.
    [GeneratedRegex(@"(?<k>(pass(word)?|pwd|pin|secret|token|key|credential)[\w-]*)(?<sep>\s*=\s*""|\s*=\s*'|>|\s*=\s*)(?<v>[^""'<\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex SecretRx();

    /// <summary>PAN-masks the text and hides values that look like passwords, keys or tokens.</summary>
    public static string Sanitize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var t = PanMasker.Mask(s);
        return SecretRx().Replace(t, m => m.Groups["k"].Value + m.Groups["sep"].Value + "***");
    }

    private static int? OptInt(JsonElement e, params string[] n) { var v = KtcJson.Int(e, int.MinValue, n); return v == int.MinValue ? null : v; }
    private static bool? OptBool(JsonElement e, params string[] n) => KtcJson.TryProp(e, out _, n) ? KtcJson.Bool(e, false, n) : null;

    /// <summary>KTC's CommandType answer (array, wrapped list or id-keyed map) as command types, ordered by category then name.</summary>
    public static List<KtcCommandTypeDto> Parse(JsonElement? root)
    {
        var list = new List<KtcCommandTypeDto>();
        foreach (var (key, e) in KtcJson.Rows(root))
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            var id = KtcJson.Int(e, int.TryParse(key, out var k) ? k : 0, IdF);
            var name = Sanitize(KtcJson.Str(e, NameF));
            if (id == 0 && name.Length == 0) continue;
            var dto = new KtcCommandTypeDto(id, name, Sanitize(KtcJson.Str(e, "Description")), Sanitize(KtcJson.Str(e, "Category")),
                Sanitize(KtcJson.Str(e, "ParameterListTemplateString", "ParameterListTemplate")), Sanitize(KtcJson.Str(e, "SubCommandListString", "SubCommandList")),
                OptBool(e, "AllowClientInitiation"), OptInt(e, "MaxRetries"), OptInt(e, "RetryWaitTime"), OptInt(e, "MaxSimultaneous"),
                OptInt(e, "TargetTypeByte", "TargetType"), OptBool(e, "IsUserDefined"), OptInt(e, "DualAuthModeByte", "DualAuthMode"));
            // Fields come from the PAN-masked template (not the display copy, whose secret hiding could clip attribute names);
            // the parser itself drops defaults of secret fields.
            var (fields, note) = CommandParameterRules.Parse(PanMasker.Mask(KtcJson.Str(e, "ParameterListTemplateString", "ParameterListTemplate")));
            list.Add(dto with { Parameters = fields, ParametersNote = note });
        }
        return list.OrderBy(c => c.Category.Length == 0).ThenBy(c => c.Category, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Every field name KTC used in the command records (so unexpected fields can be spotted).</summary>
    public static List<string> Fields(JsonElement? root) =>
        KtcJson.Rows(root).Where(r => r.Value.ValueKind == JsonValueKind.Object).SelectMany(r => r.Value.EnumerateObject().Select(p => p.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => Array.FindIndex(Known, x => x.Equals(n, StringComparison.OrdinalIgnoreCase)) is var i && i < 0 ? int.MaxValue : i)
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The planned actions mapped to the commands (configured name or id).</summary>
    public static List<PlannedAtmAction> Map(IReadOnlyList<KtcCommandTypeDto> commands, IReadOnlyDictionary<string, string?> configured, bool loaded)
    {
        var res = new List<PlannedAtmAction>();
        foreach (var (key, label, desc, looks) in Planned)
        {
            var cfg = configured.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
            var hit = cfg == null ? null : commands.FirstOrDefault(c => c.Name.Equals(cfg, StringComparison.OrdinalIgnoreCase))
                                           ?? (int.TryParse(cfg, out var cid) ? commands.FirstOrDefault(c => c.Id == cid) : null);
            // Names first; descriptions only when no name looks right ("Reset one XFS device" would otherwise suggest it for XFS reset too).
            var byName = commands.Where(c => looks.IsMatch(c.Name)).ToList();
            var sugg = (byName.Count > 0 ? byName : commands.Where(c => looks.IsMatch(c.Description)).ToList()).Select(c => $"{c.Name} (#{c.Id})").Take(5).ToList();
            var status = !loaded ? "Command list not read from KTC"
                : cfg == null ? (sugg.Count > 0 ? "Not configured - see suggestions" : "Not configured - no matching command found")
                : hit == null ? $"Configured as \"{cfg}\" but KTC has no such command"
                : "Mapped";
            res.Add(new PlannedAtmAction(key, label, desc, cfg, hit?.Id, hit?.Name, hit?.RequiresDualAuth, sugg, false, status));
        }
        return res;
    }

    /// <summary>
    /// Marks each command: high-risk (extra confirmation), or blocked from the generic form because it is a special flow (reboot,
    /// resets, in/out of service: mapped in config or named like one, except high-risk ones like Emergency Reboot), needs a second
    /// login (not supported yet), or matches the blocked pattern.
    /// </summary>
    public static List<KtcCommandTypeDto> Classify(IReadOnlyList<KtcCommandTypeDto> commands, IReadOnlyList<PlannedAtmAction> planned, string? highRisk, string? blocked)
    {
        Regex? hr = string.IsNullOrWhiteSpace(highRisk) ? null : new(highRisk, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        Regex? bl = string.IsNullOrWhiteSpace(blocked) ? null : new(blocked, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        var mapped = planned.Where(p => p.CommandTypeId != null).ToDictionary(p => p.CommandTypeId!.Value, p => p.Label);
        return commands.Select(c =>
        {
            var risky = hr != null && (hr.IsMatch(c.Name) || hr.IsMatch(c.Description));
            // Mapped reboot: only through the guided reboot. Mapped device reset / out of service / in service: through this form
            // (their buttons open it) with the high-risk confirmation. Unmapped look-alikes stay out until they are mapped.
            var isMapped = mapped.TryGetValue(c.Id, out var lbl);
            var guidedOnly = isMapped && GuidedOnly.Contains(lbl!);
            var lookAlike = !isMapped && !risky ? Planned.FirstOrDefault(p => p.Looks.IsMatch(c.Name)).Label : null;
            if (isMapped && !guidedOnly) risky = true;
            var why = guidedOnly ? $"{lbl} runs only as the guided reboot (out of service, wait for idle, reboot, back in service)."
                : lookAlike != null ? $"Looks like {lookAlike}; map it in KtcActions:Commands to use it."
                : c.RequiresDualAuth ? "Needs a second login (dual authorization), which is not supported yet."
                : bl != null && (bl.IsMatch(c.Name) || bl.IsMatch(c.Description)) ? "Blocked by configuration (KtcActions:BlockedPattern)."
                : c.ParametersNote != null ? c.ParametersNote
                : null;
            return c with { HighRisk = risky, SendBlockedReason = why };
        }).ToList();
    }
}
