using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Ktc.Application.Models;

namespace Ktc.Application.Logic;

/// <summary>
/// Turns a KTC command's ParameterListTemplateString into form fields, validates what the user typed, and builds the parameter
/// XML sent as Value2 of POST Client/{id}/Actions. Pure and unit-tested.
///
/// The template format is not documented in the Postman collection (its only sample is "&lt;Params /&gt;"), so the reader is
/// tolerant. Each child element of the root is one parameter:
///   &lt;Param Name="Type" Type="Choice" Values="MultiLevelStore,PersistentData,Registry" Default="MultiLevelStore" /&gt;
///   &lt;Parameter Name="Location" Type="String" Required="true" /&gt;
///   &lt;Use64bit Type="Bool" TrueText="64 Bit" FalseText="32 Bit"&gt;false&lt;/Use64bit&gt;
/// Name: Name/Key/Id attribute, else the element name. Type: Type/DataType/ValueType/Kind attribute or a &lt;Type&gt; child.
/// Choices: Values/Choices/Options/Items/AllowedValues attribute (comma, pipe or semicolon separated) or &lt;Value&gt;/&lt;Choice&gt;/
/// &lt;Option&gt;/&lt;Item&gt; children. Default: Default/DefaultValue/Value attribute or the element's own text.
/// The sent XML follows the only real parameter XML in the collection (AutoInstallConfiguration CommandParamsString):
///   &lt;Parameters&gt;&lt;Parameter Name="Type"&gt;Registry&lt;/Parameter&gt;...&lt;/Parameters&gt; (names configurable).
/// </summary>
public static class CommandParameterRules
{
    public const string Choice = "choice", Text = "string", Bool = "bool", Integer = "integer", Number = "number", Secret = "secret", DateTime = "datetime";

    private static readonly string[] NameA = { "Name", "Key", "Id", "ParamName", "ParameterName" };
    private static readonly string[] TypeA = { "Type", "DataType", "ValueType", "Kind", "ParamType" };
    private static readonly string[] ChoiceA = { "Values", "Choices", "Options", "Items", "AllowedValues", "List", "Enum" };
    private static readonly string[] ChoiceE = { "Value", "Choice", "Option", "Item", "Enum" };
    private static readonly string[] DefaultA = { "Default", "DefaultValue", "Value" };
    private static readonly string[] LabelA = { "DisplayName", "Label", "Caption", "Text", "Prompt" };
    private static readonly string[] Generic = { "Param", "Parameter", "Params", "Parameters", "Item", "Field", "Arg", "Argument" };
    private static readonly Regex Split = new(@"\s*[,|;]\s*", RegexOptions.Compiled);

    private static string? Attr(XElement e, params string[] names) =>
        e.Attributes().FirstOrDefault(a => names.Any(n => n.Equals(a.Name.LocalName, StringComparison.OrdinalIgnoreCase)))?.Value;
    private static XElement? Child(XElement e, params string[] names) =>
        e.Elements().FirstOrDefault(c => names.Any(n => n.Equals(c.Name.LocalName, StringComparison.OrdinalIgnoreCase)));
    private static bool? Flag(string? s) => s == null ? null : s.Trim().ToLowerInvariant() switch { "true" or "1" or "yes" or "y" => true, "false" or "0" or "no" or "n" => false, _ => null };

    /// <summary>Normalised type for a KTC type name; null when the name is not recognised.</summary>
    public static string? NormaliseType(string? raw)
    {
        var t = (raw ?? "").Trim().ToLowerInvariant().Replace("system.", "");
        if (t.Length == 0) return null;
        if (Regex.IsMatch(t, @"^(bool|boolean|bit|checkbox|flag|yesno)$")) return Bool;
        if (Regex.IsMatch(t, @"^(u?int(16|32|64)?|integer|long|ulong|short|ushort|byte|sbyte|whole)$")) return Integer;
        if (Regex.IsMatch(t, @"^(number|numeric|decimal|double|float|single|real)$")) return Number;
        if (Regex.IsMatch(t, @"^(choice|enum|list|select|dropdown|combo|combobox|option|options|radio)$")) return Choice;
        if (Regex.IsMatch(t, @"^(password|secret|securestring|pin)$")) return Secret;
        if (Regex.IsMatch(t, @"^(datetime|date|time|timestamp)$")) return DateTime;
        if (Regex.IsMatch(t, @"^(string|text|str|path|file|filename|folder|directory|location|registry|url|multiline)$")) return Text;
        return null;
    }

    /// <summary>KTC's own templates sometimes list "etc" / "..." as a choice (KTC_RESET_DEVICE: Choices="CardReader,PinPad,etc"); that is not a value.</summary>
    private static bool IsPlaceholder(string s) => s.Trim().TrimEnd('.').Equals("etc", StringComparison.OrdinalIgnoreCase) || s.Trim() is "..." or "\u2026";

    /// <summary>
    /// Replaces the choices of the parameter called <paramref name="name"/> with <paramref name="values"/>. Used for KTC_RESET_DEVICE:
    /// the KTC client sends the full component name (Device = KXDevice.KXCardReader), not the short words in the command's template.
    /// Does nothing when the command has no such parameter or no values are given.
    /// </summary>
    public static IReadOnlyList<CommandParameter> WithChoices(IReadOnlyList<CommandParameter> fields, string name, IReadOnlyList<string> values)
    {
        if (values.Count == 0) return fields;
        return fields.Select(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
            ? f with { Type = Choice, Choices = values.ToList(), Default = null, Required = true, Note = null } : f).ToList();
    }

    /// <summary>The form fields of a command. Unreadable XML gives no fields plus a note (the form then offers no inputs).</summary>
    public static (List<CommandParameter> Fields, string? Note) Parse(string? template)
    {
        var res = new List<CommandParameter>();
        if (string.IsNullOrWhiteSpace(template)) return (res, null);
        XElement root;
        try { root = XElement.Parse(template, LoadOptions.None); }
        catch (XmlException ex) { return (res, "Parameter template is not valid XML: " + ex.Message); }

        foreach (var e in root.Elements())
        {
            var isGeneric = Generic.Any(g => g.Equals(e.Name.LocalName, StringComparison.OrdinalIgnoreCase));
            var name = Attr(e, NameA) ?? (isGeneric ? null : e.Name.LocalName);
            if (string.IsNullOrWhiteSpace(name)) continue;

            var rawType = Attr(e, TypeA) ?? Child(e, TypeA)?.Value;
            var choiceText = Attr(e, ChoiceA);
            var choices = choiceText != null ? Split.Split(choiceText.Trim()).Where(x => x.Length > 0 && !IsPlaceholder(x)).ToList()
                : e.Elements().Where(c => ChoiceE.Any(n => n.Equals(c.Name.LocalName, StringComparison.OrdinalIgnoreCase)))
                    .Select(c => Attr(c, "Value", "Name") ?? c.Value.Trim()).Where(x => x.Length > 0).ToList();
            var listParent = Child(e, ChoiceA);
            if (choices.Count == 0 && listParent != null)
                choices = listParent.Elements().Select(c => Attr(c, "Value", "Name") ?? c.Value.Trim()).Where(x => x.Length > 0).ToList();
            var def = Attr(e, DefaultA) ?? (e.HasElements ? Child(e, DefaultA)?.Value : e.Value.Trim());
            if (def is { Length: 0 }) def = null;

            var type = NormaliseType(rawType);
            string? note = null;
            if (type == null)
            {
                if (choices.Count > 0) type = Choice;
                else if (Flag(def) != null && def!.Trim().Length > 1) type = Bool;
                else
                {
                    type = Text;
                    if (rawType != null) note = $"Unknown type \"{rawType}\": entered as text.";
                }
            }
            if (type == Choice && choices.Count == 0) { type = Text; note = "No choices listed: entered as text."; }
            if (type == Secret || Regex.IsMatch(name, @"pass(word)?|pwd|secret|token|pin\b", RegexOptions.IgnoreCase)) { type = Secret; def = null; }

            var required = Flag(Attr(e, "Required", "Mandatory", "IsRequired")) ?? (Flag(Attr(e, "Optional", "IsOptional")) is bool o ? !o : (bool?)null);
            res.Add(new CommandParameter(name.Trim(), Attr(e, LabelA)?.Trim() is { Length: > 0 } l ? l : name.Trim(), type, rawType, choices, def,
                required ?? (type is not Bool), Num(Attr(e, "Min", "Minimum", "MinValue")), Num(Attr(e, "Max", "Maximum", "MaxValue")),
                Int(Attr(e, "MaxLength", "Length", "Size")), Attr(e, "TrueText", "TrueLabel", "OnText"), Attr(e, "FalseText", "FalseLabel", "OffText"),
                Attr(e, "Description", "Help", "Tooltip", "Hint"), note));
        }
        return (res, null);
    }

    private static double? Num(string? s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    private static int? Int(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i > 0 ? i : null;

    /// <summary>Checks the values against the fields; returns the cleaned values (bools as true/false, numbers invariant) and the errors.</summary>
    public static (Dictionary<string, string> Values, List<string> Errors) Validate(IReadOnlyList<CommandParameter> fields, IReadOnlyDictionary<string, string?>? input)
    {
        var vals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var errs = new List<string>();
        input ??= new Dictionary<string, string?>();
        var lookup = new Dictionary<string, string?>(input, StringComparer.OrdinalIgnoreCase);
        foreach (var unknown in lookup.Keys.Where(k => !fields.Any(f => f.Name.Equals(k, StringComparison.OrdinalIgnoreCase))))
            errs.Add($"\"{unknown}\" is not a parameter of this command.");
        foreach (var f in fields)
        {
            var v = (lookup.TryGetValue(f.Name, out var x) ? x : null)?.Trim();
            if (string.IsNullOrEmpty(v))
            {
                if (f.Type == Bool) { vals[f.Name] = (Flag(f.Default) ?? false) ? "true" : "false"; continue; }
                if (f.Required) errs.Add($"{f.Label} is required.");
                continue;
            }
            if (v.Length > 2000) { errs.Add($"{f.Label} is too long."); continue; }
            if (v.Any(c => char.IsControl(c) && c is not '\t')) { errs.Add($"{f.Label} contains control characters."); continue; }
            switch (f.Type)
            {
                case Bool:
                    if (Flag(v) is not bool b) { errs.Add($"{f.Label} must be true or false."); continue; }
                    vals[f.Name] = b ? "true" : "false"; continue;
                case Choice:
                    var hit = f.Choices.FirstOrDefault(c => c.Equals(v, StringComparison.OrdinalIgnoreCase));
                    if (hit == null) { errs.Add($"{f.Label} must be one of: {string.Join(", ", f.Choices)}."); continue; }
                    vals[f.Name] = hit; continue;
                case Integer:
                case Number:
                    if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || (f.Type == Integer && d != Math.Floor(d)))
                    { errs.Add($"{f.Label} must be {(f.Type == Integer ? "a whole number" : "a number")}."); continue; }
                    if (f.Min is { } mn && d < mn) { errs.Add($"{f.Label} must be at least {mn.ToString(CultureInfo.InvariantCulture)}."); continue; }
                    if (f.Max is { } mx && d > mx) { errs.Add($"{f.Label} must be at most {mx.ToString(CultureInfo.InvariantCulture)}."); continue; }
                    vals[f.Name] = d.ToString(CultureInfo.InvariantCulture); continue;
                case DateTime:
                    if (!System.DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
                    { errs.Add($"{f.Label} must be a date and time."); continue; }
                    vals[f.Name] = dt.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture); continue;
                default:
                    if (f.MaxLength is { } ml && v.Length > ml) { errs.Add($"{f.Label} must be at most {ml} characters."); continue; }
                    vals[f.Name] = v; continue;
            }
        }
        return (vals, errs);
    }

    /// <summary>The parameter XML for Value2, e.g. &lt;Parameters&gt;&lt;Parameter Name="Type"&gt;Registry&lt;/Parameter&gt;&lt;/Parameters&gt;. Values are XML-escaped.</summary>
    public static string BuildXml(IReadOnlyList<CommandParameter> fields, IReadOnlyDictionary<string, string> values,
        string root = "Parameters", string element = "Parameter", string nameAttribute = "Name")
    {
        var x = new XElement(root);
        foreach (var f in fields)
            if (values.TryGetValue(f.Name, out var v)) x.Add(new XElement(element, new XAttribute(nameAttribute, f.Name), v));
        return x.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>The values as shown in confirmations, logs and the audit: secrets hidden, card numbers masked.</summary>
    public static string Describe(IReadOnlyList<CommandParameter> fields, IReadOnlyDictionary<string, string> values) =>
        string.Join("; ", fields.Where(f => values.ContainsKey(f.Name)).Select(f => $"{f.Name}={(f.Type == Secret ? "***" : PanMasker.Mask(values[f.Name]))}"));

    private static readonly System.Text.RegularExpressions.Regex NoteName = new(@"reason|comment|description|message|note|text", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Values for a command sent by a guided flow (no form): text parameters named like Reason / Comment / Message get the ticket and
    /// reason (cut to their maximum length); everything else uses its default. Validate then reports anything still missing.
    /// </summary>
    public static Dictionary<string, string?> GuidedValues(IReadOnlyList<CommandParameter> ps, string note)
    {
        var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in ps.Where(p => !string.IsNullOrEmpty(p.Default))) d[p.Name] = p.Default;
        foreach (var p in ps.Where(p => p.Type == "string" && string.IsNullOrEmpty(p.Default) && NoteName.IsMatch(p.Name)))
            d[p.Name] = p.MaxLength is int m && note.Length > m ? note[..m] : note;
        return d;
    }
}
