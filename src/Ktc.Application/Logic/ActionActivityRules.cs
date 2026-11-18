using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Ktc.Application.Models;
using Ktc.Application.Options;

namespace Ktc.Application.Logic;

/// <summary>Reads KTC actions, their comments and uploaded files, and applies the PCI file rules. Pure and unit-tested.</summary>
public static partial class ActionActivityRules
{
    [GeneratedRegex(@"(?:Ticket\s+(?<t>[^\s:]+):\s*)?(?<r>.*?)\s*\(via KTC Monitoring Portal, ref (?<ref>\w+)\)", RegexOptions.Singleline)]
    private static partial Regex PortalComment();
    // Magnetic stripe track 1 (%B...^NAME^...?) and track 2 (;PAN=YYMM...?) data.
    [GeneratedRegex(@"%B\d{12,19}\^[^\^\r\n]{0,26}\^[^?\r\n]*\??|;?\d{12,19}=\d{4}[^?\s]*\??")]
    private static partial Regex Track();

    private static readonly string[] Binary = { ".zip", ".7z", ".rar", ".gz", ".tgz", ".cab", ".exe", ".dll", ".msi", ".bin", ".img", ".iso", ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".pdf", ".db", ".mdb", ".sdf", ".dmp", ".evtx", ".etl" };

    /// <summary>User, ticket, reason and portal ref from an action's comments (XML or plain text).</summary>
    public static (string? User, string? Ticket, string? Reason, string? Ref) ParseComments(string? comments)
    {
        if (string.IsNullOrWhiteSpace(comments)) return (null, null, null, null);
        string? user = null; var texts = new List<string>();
        try
        {
            var x = XElement.Parse(comments);
            foreach (var c in x.DescendantsAndSelf().Where(e => !e.HasElements))
            {
                user ??= c.Attribute("User")?.Value ?? c.Parent?.Element("User")?.Value;
                if (c.Name.LocalName is "User" or "Timestamp" or "Confidential") continue;
                if (c.Value.Trim().Length > 0) texts.Add(c.Value.Trim());
                if (c.Attribute("Comment")?.Value is { Length: > 0 } a) texts.Add(a);
            }
            foreach (var c in x.DescendantsAndSelf()) user ??= c.Attribute("User")?.Value;
        }
        catch (System.Xml.XmlException) { texts.Add(comments); }
        foreach (var t in texts)
        {
            var m = PortalComment().Match(t);
            if (!m.Success) continue;
            // Guided flows send with the service account and name the person: "... [guided reboot: out of service, requested by alice]".
            var r = m.Groups["r"].Value; var g = Regex.Match(r, @"\s*\[(?<what>guided [^\]]*?), requested by (?<u>[^\]]+)\]\s*$");
            if (g.Success) { r = r[..g.Index] + " (" + g.Groups["what"].Value + ")"; user = g.Groups["u"].Value; }
            return (user, m.Groups["t"].Success ? m.Groups["t"].Value : null, PanMasker.Mask(r), m.Groups["ref"].Value);
        }
        return (user, null, texts.Count > 0 ? PanMasker.Mask(string.Join(" / ", texts)) : null, null);
    }

    public static (string Name, bool Final, bool Failed) StatusOf(int? b, KtcActionsOptions o)
    {
        var name = b == null ? "Unknown" : o.StatusMap.TryGetValue(b.Value.ToString(), out var n) ? n : "Status " + b;
        bool final = b != null && Regex.IsMatch(name, o.FinalStatusPattern, RegexOptions.IgnoreCase);
        return (name, final, final && Regex.IsMatch(name, o.FailedStatusPattern, RegexOptions.IgnoreCase));
    }

    /// <summary>One KTC action record as an <see cref="ActionRecord"/> (command names from the command list).</summary>
    public static ActionRecord? ParseAction(JsonElement e, string key, IReadOnlyDictionary<int, KtcCommandTypeDto> commands, KtcActionsOptions o)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var id = KtcJson.Str(e, "ID", "Id", "ActionID");
        if (id.Length == 0) id = key;
        if (id.Length == 0) return null;
        var cmdId = KtcJson.Int(e, 0, "CommandID", "CommandTypeID", "CommandId");
        commands.TryGetValue(cmdId, out var cmd);
        int? sb = KtcJson.TryProp(e, out _, "StatusByte", "Status") ? KtcJson.Int(e, -1, "StatusByte", "Status") : null;
        if (sb == -1) sb = null;
        var (status, final, failed) = StatusOf(sb, o);
        var (user, ticket, reason, portalRef) = ParseComments(KtcJson.Str(e, "CommentsString", "Comments"));
        int? Opt(params string[] n) => KtcJson.TryProp(e, out _, n) ? KtcJson.Int(e, 0, n) : null;
        DateTime? D(params string[] n) => KtcJson.Date(e, n) is { } d && d.Year > 1900 ? d : null;
        var name = cmd?.Name ?? (cmdId > 0 ? "Command " + cmdId : "Unknown command");
        var fileish = cmd != null && Regex.IsMatch($"{cmd.Name} {cmd.Description} {cmd.Category}", o.FileCommandPattern, RegexOptions.IgnoreCase);
        return new ActionRecord(id, cmdId, name, cmd is { Description.Length: > 0 } ? cmd.Description : name, cmd?.Category ?? "", sb, status, final, failed,
            Opt("ProgressPercent", "Progress"), Opt("RetryCount", "Retries"), D("AddedTime", "Added", "TimeAdded"), D("StartTime", "Started"), D("EndTime", "Ended", "Finished"),
            Trunc(CommandTypeRules.Sanitize(KtcJson.Str(e, "ResultString", "Result")), 4000), Trunc(CommandTypeRules.Sanitize(KtcJson.Str(e, "CommandParametersString", "Parameters", "ParametersString")), 2000),
            user, ticket, reason, portalRef, fileish);
    }

    public static List<ActionRecord> ParseActions(JsonElement? root, IReadOnlyDictionary<int, KtcCommandTypeDto> commands, KtcActionsOptions o) =>
        KtcJson.Rows(root).Select(r => ParseAction(r.Value, r.Key, commands, o)).OfType<ActionRecord>()
            .OrderByDescending(a => a.AddedUtc ?? a.StartUtc ?? DateTime.MinValue).ThenByDescending(a => long.TryParse(a.Id, out var n) ? n : 0).ToList();

    private static string Trunc(string s, int n) => s.Length > n ? s[..n] + "…" : s;

    /// <summary>File names in UploadedDirectoryListing: an array of names or of {Name, Size, Modified} records, or one text listing.</summary>
    public static List<ActionFile> ParseListing(JsonElement? root, string? raw, KtcActionsOptions o)
    {
        var res = new List<ActionFile>();
        void Add(string name, long? size, DateTime? mod)
        {
            name = name.Trim(); if (name.Length == 0 || res.Any(f => f.Name == name)) return;
            var why = BlockReason(name, size, o);
            res.Add(new ActionFile(name, size, mod, why == null, why));
        }
        if (root is { } r)
        {
            if (r.ValueKind == JsonValueKind.String) foreach (var l in (r.GetString() ?? "").Split('\n', '\r', ';')) Add(l, null, null);
            else foreach (var e in KtcJson.Items(r))
                {
                    if (e.ValueKind == JsonValueKind.String) Add(e.GetString() ?? "", null, null);
                    else if (e.ValueKind == JsonValueKind.Object)
                        Add(KtcJson.Str(e, "Name", "FileName", "Filename", "Path", "FullName"), KtcJson.TryProp(e, out _, "Size", "Length", "FileSize") ? (long)KtcJson.Dbl(e, 0, "Size", "Length", "FileSize") : null,
                            KtcJson.Date(e, "Modified", "LastWriteTime", "LastModified", "Time", "Date"));
                }
        }
        else if (!string.IsNullOrWhiteSpace(raw)) foreach (var l in raw.Split('\n', '\r')) Add(l, null, null);
        return res;
    }

    public static string? BlockReason(string name, long? size, KtcActionsOptions o)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (Binary.Contains(ext)) return $"{ext} files are binary or compressed and cannot be checked for card data, so the portal does not open them.";
        if (size is { } s && s > o.FileMaxBytes) return $"Larger than {o.FileMaxBytes / 1024 / 1024} MB.";
        return null;
    }

    /// <summary>
    /// The bytes of an UploadedFile answer: a JSON string (base64 or text), an object with the content under Data/Content/FileData/
    /// Bytes/File (base64 or text), or a raw body. Also returns the file name when KTC gives one.
    /// </summary>
    public static (byte[] Bytes, string? Name) DecodeFile(JsonElement? json, string? raw)
    {
        static byte[] FromText(string s)
        {
            var t = s.Trim();
            if (t.Length >= 8 && t.Length % 4 == 0 && Regex.IsMatch(t, @"^[A-Za-z0-9+/]+={0,2}$"))
                try { return Convert.FromBase64String(t); } catch (FormatException) { }
            return Encoding.UTF8.GetBytes(s);
        }
        if (json is { } j)
        {
            if (j.ValueKind == JsonValueKind.String) return (FromText(j.GetString() ?? ""), null);
            if (j.ValueKind == JsonValueKind.Object)
            {
                var name = KtcJson.Str(j, "FileName", "Name", "Filename");
                if (KtcJson.TryProp(j, out var v, "Data", "Content", "FileData", "Bytes", "File", "Contents"))
                {
                    if (v.ValueKind == JsonValueKind.String) return (FromText(v.GetString() ?? ""), name.Length > 0 ? name : null);
                    if (v.ValueKind == JsonValueKind.Array) return (v.EnumerateArray().Select(b => (byte)b.GetInt32()).ToArray(), name.Length > 0 ? name : null);
                }
            }
            if (j.ValueKind == JsonValueKind.Array && j.EnumerateArray().All(b => b.ValueKind == JsonValueKind.Number))
                return (j.EnumerateArray().Select(b => (byte)b.GetInt32()).ToArray(), null);
        }
        return (Encoding.UTF8.GetBytes(raw ?? ""), null);
    }

    /// <summary>Text the portal can check: valid UTF-8 (or Latin-1 with no control bytes), no NUL bytes, no replacement characters.</summary>
    public static string? AsText(byte[] b)
    {
        if (b.Length == 0) return "";
        if (b.Take(8192).Any(x => x == 0)) return null;
        string s;
        try { s = new UTF8Encoding(false, true).GetString(b); }
        catch (DecoderFallbackException) { s = Encoding.Latin1.GetString(b); }
        if (s.Length > 0 && s[0] == '﻿') s = s[1..];
        var ctrl = s.Take(8192).Count(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t' and not '\f');
        return ctrl > 8 || s.Contains('�') ? null : s;
    }

    /// <summary>PCI masking of a whole file: card numbers to first 6 / last 4, track data removed. Returns the text and how many were masked.</summary>
    public static (string Text, int Masked) MaskFile(string text)
    {
        int n = 0;
        var t = Track().Replace(text, _ => { n++; return "[TRACK DATA REMOVED]"; });
        var lines = t.Split('\n');
        for (int i = 0; i < lines.Length; i++) { var m = PanMasker.Mask(lines[i]); if (m != lines[i]) { n += Math.Max(1, Diff(lines[i], m)); lines[i] = m; } }
        return (string.Join('\n', lines), n);
    }
    private static int Diff(string a, string b) => Regex.Matches(b, @"\d{4,6}\*{3,}\d{4}").Count - Regex.Matches(a, @"\d{4,6}\*{3,}\d{4}").Count;

    /// <summary>The PCI file rules applied to one file's bytes.</summary>
    public static ActionFileContent Check(string name, byte[] bytes, KtcActionsOptions o, bool preview)
    {
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var why = BlockReason(name, bytes.LongLength, o);
        if (why != null) return new ActionFileContent(name, bytes.LongLength, null, 0, false, why, sha);
        var text = AsText(bytes);
        if (text == null) return new ActionFileContent(name, bytes.LongLength, null, 0, false, "Not a text file, so it cannot be checked for card data. The portal does not open it.", sha);
        var (masked, n) = MaskFile(text);
        var truncated = false;
        if (preview)
        {
            var lines = masked.Split('\n');
            if (lines.Length > o.FilePreviewLines) { masked = string.Join('\n', lines.Take(o.FilePreviewLines)); truncated = true; }
        }
        return new ActionFileContent(name, bytes.LongLength, masked, n, truncated, null, sha);
    }
}
