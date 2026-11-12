using System.Net;
using System.Text;
using System.Text.Json;
using Ktc.Application.Abstractions;
using Ktc.Application.Options;
using Ktc.Domain.Enums;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Ktc.Infrastructure.Services;

public class NotificationSender : INotificationSender
{
    private readonly NotificationOptions _opt; private readonly IHttpClientFactory _http; private readonly ILogger<NotificationSender> _log;
    public NotificationSender(IOptions<NotificationOptions> o, IHttpClientFactory http, ILogger<NotificationSender> log) { _opt = o.Value; _http = http; _log = log; }

    public async Task<string> SendAsync(AlertChannels channels, string subject, string body, IEnumerable<string>? recipients, CancellationToken ct = default)
    {
        var status = new List<string>();
        if (channels.HasFlag(AlertChannels.Email))
        {
            var to = (recipients?.Where(r => !string.IsNullOrWhiteSpace(r)).ToList() is { Count: > 0 } rl ? rl : _opt.DefaultRecipients.ToList());
            if (string.IsNullOrWhiteSpace(_opt.Smtp.Host)) { status.Add("Email:not-configured"); }
            else try { await SendEmailAsync(to, subject, $"<div style='font-family:Segoe UI,Arial'><h3>{WebUtility.HtmlEncode(subject)}</h3><p>{WebUtility.HtmlEncode(body).Replace("\n", "<br/>")}</p>{PortalLink()}</div>", null, ct); status.Add("Email:sent"); }
            catch (Exception ex) { _log.LogError(ex, "Alert e-mail failed"); status.Add("Email:failed"); }
        }
        if (channels.HasFlag(AlertChannels.Teams))
        {
            try { status.Add(await SendTeamsAsync(subject, body, ct) ? "Teams:sent" : "Teams:not-configured"); }
            catch (Exception ex) { _log.LogError(ex, "Teams webhook failed"); status.Add("Teams:failed"); }
        }
        return string.Join(", ", status);
    }

    private string PortalLink() => string.IsNullOrWhiteSpace(_opt.PortalBaseUrl) ? "" : $"<p><a href='{WebUtility.HtmlEncode(_opt.PortalBaseUrl)}'>Open KTC Monitoring Portal</a></p>";

    public async Task SendEmailAsync(IEnumerable<string> to, string subject, string htmlBody, (string Name, byte[] Data, string MimeType)? attachment = null, CancellationToken ct = default)
    {
        var s = _opt.Smtp;
        var list = to.ToList();
        if (string.IsNullOrWhiteSpace(s.Host)) { _log.LogWarning("SMTP not configured - e-mail '{Subject}' not sent", subject); throw new InvalidOperationException("SMTP is not configured."); }
        if (list.Count == 0) throw new InvalidOperationException("No e-mail recipients configured.");
        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(s.From));
        foreach (var r in list) msg.To.Add(MailboxAddress.Parse(r.Trim()));
        msg.Subject = subject;
        var b = new BodyBuilder { HtmlBody = htmlBody };
        if (attachment is { } a) b.Attachments.Add(a.Name, a.Data, ContentType.Parse(a.MimeType));
        msg.Body = b.ToMessageBody();
        using var client = new SmtpClient();
        await client.ConnectAsync(s.Host, s.Port, s.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto, ct);
        if (!string.IsNullOrWhiteSpace(s.Username)) await client.AuthenticateAsync(s.Username, s.Password ?? "", ct);
        await client.SendAsync(msg, ct);
        await client.DisconnectAsync(true, ct);
    }

    private async Task<bool> SendTeamsAsync(string title, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_opt.TeamsWebhookUrl)) return false;
        // Adaptive Card payload (works with Workflows "When a Teams webhook request is received" and legacy connectors that accept cards)
        var payload = new
        {
            type = "message",
            attachments = new[] { new { contentType = "application/vnd.microsoft.card.adaptive", contentUrl = (string?)null,
                content = new { type = "AdaptiveCard", version = "1.4", schema = "http://adaptivecards.io/schemas/adaptive-card.json",
                    body = new object[] { new { type = "TextBlock", size = "Medium", weight = "Bolder", text = title, wrap = true }, new { type = "TextBlock", text = text, wrap = true } } } } }
        };
        var json = JsonSerializer.Serialize(payload).Replace("\"schema\"", "\"$schema\"");
        using var res = await _http.CreateClient("notify").PostAsync(_opt.TeamsWebhookUrl, new StringContent(json, Encoding.UTF8, "application/json"), ct);
        res.EnsureSuccessStatusCode();
        return true;
    }
}
