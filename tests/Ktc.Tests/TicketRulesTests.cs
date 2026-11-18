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

    [Fact]
    public void Close_body_keeps_the_record_and_sets_closed_fields()
    {
        var now = new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);
        var o = TicketRules.CloseBody(J("""{"ID":5,"StatusByte":1,"TimeClosed":null,"TimeUpdated":"2026-11-18T09:00:00Z","ClosureModeByte":1,"OwnerID":7}"""), 4, 0, now);
        Assert.Equal(4, (int)o["StatusByte"]!); Assert.Equal(0, (int)o["ClosureModeByte"]!); Assert.Equal(7, (int)o["OwnerID"]!);
        Assert.StartsWith("2026-11-18T10:00:00", (string)o["TimeClosed"]!); Assert.Equal("2026-11-18T09:00:00Z", (string)o["TimeUpdated"]!);
        Assert.False(o.ContainsKey("ClosureMode"));                          // only fields KTC already had
    }

    [Fact]
    public void Comment_body_masks_card_numbers()
    {
        var json = JsonSerializer.Serialize(TicketRules.CommentBody("543110", "alice", "card 4111111111111111 retained", 4, 2, DateTime.UtcNow));
        Assert.DoesNotContain("4111111111111111", json); Assert.Contains("\"rowID\":543110", json); Assert.Contains("\"table\":4", json);
    }

    private static readonly DateTime T0 = new(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Append_comment_starts_a_list_when_there_is_none()
    {
        var x = TicketRules.AppendComment(null, "alice", T0, "Checked on site, card 4111111111111111");
        Assert.StartsWith("<Comments><Comment ", x);
        Assert.Contains("User=\"alice\"", x); Assert.Contains("2026-11-18T10:00:00.000Z", x); Assert.DoesNotContain("4111111111111111", x);
        Assert.Single(TicketRules.ParseCommentsString(x));
    }

    [Fact]
    public void Append_comment_copies_the_attribute_form()
    {
        var x = TicketRules.AppendComment("<Comments><Comment User=\"bob\" Timestamp=\"2026-11-17T08:00:00Z\">first</Comment></Comments>", "alice", T0, "second note");
        var all = TicketRules.ParseCommentsString(x);
        Assert.Equal(2, all.Count); Assert.Contains(all, c => c.Text == "second note" && c.User == "alice");
    }

    [Fact]
    public void Append_comment_copies_the_child_element_form()
    {
        var x = TicketRules.AppendComment("<Comments><KtcComment><Timestamp>2026-11-17T08:00:00Z</Timestamp><User>bob</User><Comment>first</Comment></KtcComment></Comments>", "alice", T0, "second note");
        Assert.Contains("<KtcComment><Timestamp>2026-11-18T10:00:00.000Z</Timestamp><User>alice</User><Comment>second note</Comment></KtcComment>", x);
    }

    [Fact]
    public void Looks_closed_uses_the_closed_time_or_status()
    {
        Assert.True(TicketRules.LooksClosed(J("""{"StatusByte":1,"TimeClosed":"2026-11-18T10:00:00Z"}"""), null));
        Assert.True(TicketRules.LooksClosed(J("""{"StatusByte":4,"TimeClosed":null}"""), 4));
        Assert.False(TicketRules.LooksClosed(J("""{"StatusByte":1,"TimeClosed":null}"""), 4));
    }
}
