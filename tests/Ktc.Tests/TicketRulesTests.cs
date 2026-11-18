using System.Text.Json;
using Ktc.Application.Logic;
using Ktc.Domain.Enums;
using Xunit;

namespace Ktc.Tests;

public class TicketRulesTests
{
    private static JsonElement J(string s) => JsonDocument.Parse(s).RootElement;
    private static readonly Dictionary<string, string> NoMap = new();

    [Fact]
    public void Uses_the_configured_ktc_status_name_and_files_it_under_a_category()
    {
        var map = new Dictionary<string, string> { ["5"] = "AutoClosed", ["2"] = "Owned" };
        Assert.Equal(("AutoClosed", TicketStatus.Closed, (int?)5), TicketRules.Status(J("""{"StatusByte":5}"""), map, Array.Empty<Ktc.Application.Models.TicketComment>()));
        Assert.Equal(("Owned", TicketStatus.Assigned, (int?)2), TicketRules.Status(J("""{"StatusByte":2}"""), map, Array.Empty<Ktc.Application.Models.TicketComment>()));
    }

    [Fact]
    public void A_text_status_from_ktc_wins()
    {
        var (name, cat, _) = TicketRules.Status(J("""{"Status":"Dispatched","StatusByte":3}"""), NoMap, Array.Empty<Ktc.Application.Models.TicketComment>());
        Assert.Equal("Dispatched", name); Assert.Equal(TicketStatus.InProgress, cat);
    }

    [Fact]
    public void Without_a_name_closed_by_the_system_reads_as_auto_closed()
    {
        var e = J("""{"StatusByte":9,"TimeClosed":"2026-11-05T06:23:09Z","CommentsString":"<Comments><Comment User=\"System\" Timestamp=\"2026-11-05T06:23:08Z\">Auto closed by system as error code is no longer active.</Comment></Comments>"}""");
        var c = TicketRules.Comments(e);
        Assert.Equal("AutoClosed", TicketRules.Status(e, NoMap, c).Name);
        Assert.Equal("Closed", TicketRules.Status(J("""{"TimeClosed":"2026-11-05T06:23:09Z"}"""), NoMap, Array.Empty<Ktc.Application.Models.TicketComment>()).Name);
        Assert.Equal("Open (status 7)", TicketRules.Status(J("""{"StatusByte":7}"""), NoMap, Array.Empty<Ktc.Application.Models.TicketComment>()).Name);
    }

    [Fact]
    public void Old_category_names_in_config_still_work()
    {
        var map = new Dictionary<string, string> { ["3"] = "InProgress" };
        Assert.Equal(TicketStatus.InProgress, TicketRules.Status(J("""{"StatusByte":3}"""), map, Array.Empty<Ktc.Application.Models.TicketComment>()).Category);
    }

    [Fact]
    public void Comments_are_read_in_order_with_card_numbers_masked()
    {
        var c = TicketRules.ParseCommentsString("<Comments><Comment User=\"bob\" Timestamp=\"2026-11-04T23:10:00Z\">Card 4111111111111111 retained</Comment><Comment User=\"System\" Timestamp=\"2026-11-04T23:02:43Z\">Ticket created automatically.</Comment></Comments>");
        Assert.Equal(2, c.Count);
        var sorted = TicketRules.Comments(J(JsonSerializer.Serialize(new { CommentsString = "<Comments><Comment User=\"bob\" Timestamp=\"2026-11-04T23:10:00Z\">Card 4111111111111111 retained</Comment><Comment User=\"System\" Timestamp=\"2026-11-04T23:02:43Z\">Ticket created automatically.</Comment></Comments>" })));
        Assert.Equal("System", sorted[0].User);
        Assert.DoesNotContain("4111111111111111", sorted[1].Text);
    }

    [Fact]
    public void Sla_summary_is_no_sla_without_a_ktc_time()
    {
        var now = new DateTime(2026, 11, 18, 9, 0, 0, DateTimeKind.Utc);
        Assert.Equal(("No SLA", false), TicketRules.SlaSummary(null, null, now));
        Assert.Equal("SLA met", TicketRules.SlaSummary(now, now.AddMinutes(-1), now).Summary);
        Assert.Equal("Breached", TicketRules.SlaSummary(now.AddMinutes(-5), null, now).Summary);
    }

    [Fact]
    public void Fields_mask_card_numbers()
    {
        var f = TicketRules.Fields(J("""{"ID":1,"Note":"pan 4111111111111111","Nested":{"a":1}}"""));
        Assert.Equal(3, f.Count);
        Assert.DoesNotContain("4111111111111111", f[1].Value);
    }
}
