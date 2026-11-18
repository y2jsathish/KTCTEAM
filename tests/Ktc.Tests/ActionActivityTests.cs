using System.Text;
using System.Text.Json;
using Ktc.Application.Logic;
using Ktc.Application.Options;
using Xunit;

namespace Ktc.Tests;

public class ActionActivityTests
{
    private static readonly KtcActionsOptions O = new();

    [Fact]
    public void Reads_ticket_reason_and_user_from_the_portal_comment_and_masks_card_numbers()
    {
        var (user, ticket, reason, re) = ActionActivityRules.ParseComments(
            "<Comments><Comment User=\"alice\">Ticket INC-42: card 4111111111111111 stuck (via KTC Monitoring Portal, ref abc123)</Comment></Comments>");
        Assert.Equal("alice", user); Assert.Equal("INC-42", ticket); Assert.Equal("abc123", re);
        Assert.DoesNotContain("4111111111111111", reason);
    }

    [Fact]
    public void Status_bytes_map_to_final_and_failed()
    {
        Assert.Equal(("Pending", false, false), ActionActivityRules.StatusOf(0, O));
        Assert.Equal(("Completed", true, false), ActionActivityRules.StatusOf(2, O));
        Assert.Equal(("Failed", true, true), ActionActivityRules.StatusOf(3, O));
        Assert.False(ActionActivityRules.StatusOf(null, O).Final);
    }

    [Fact]
    public void Binary_archives_and_big_files_are_not_opened()
    {
        Assert.NotNull(ActionActivityRules.BlockReason("Logs.zip", 10, O));
        Assert.NotNull(ActionActivityRules.BlockReason("EJ.txt", O.FileMaxBytes + 1, O));
        Assert.Null(ActionActivityRules.BlockReason("EJ_20261118.txt", 2048, O));
    }

    [Fact]
    public void File_text_has_pans_masked_and_track_data_removed()
    {
        var text = "08:00 CARD 4111111111111111\n;4111111111111111=29121011234567890?\nOK";
        var f = ActionActivityRules.Check("EJ.txt", Encoding.UTF8.GetBytes(text), O, preview: true);
        Assert.NotNull(f.Text);
        Assert.DoesNotContain("4111111111111111", f.Text);
        Assert.Contains("[TRACK DATA REMOVED]", f.Text);
        Assert.True(f.MaskedCount >= 2);
        Assert.Equal(64, f.Sha256.Length);
    }

    [Fact]
    public void Binary_content_with_a_text_name_is_refused()
    {
        var f = ActionActivityRules.Check("EJ.txt", new byte[] { 0x50, 0x4B, 0x03, 0x04, 0, 0, 1, 2 }, O, preview: true);
        Assert.Null(f.Text); Assert.NotNull(f.BlockedReason);
    }

    [Fact]
    public void Decodes_base64_file_answers()
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("hello"));
        var (bytes, _) = ActionActivityRules.DecodeFile(JsonDocument.Parse($"{{\"FileData\":\"{b64}\",\"FileName\":\"a.txt\"}}").RootElement, null);
        Assert.Equal("hello", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Guided_comments_name_the_person_not_the_service_account()
    {
        var (user, ticket, reason, _) = ActionActivityRules.ParseComments(
            "<Comments><Comment User=\"KTC Monitoring Portal\">Ticket 7: Frozen [guided reboot: reboot, requested by alice] (via KTC Monitoring Portal, ref r1)</Comment></Comments>");
        Assert.Equal("alice", user); Assert.Equal("7", ticket); Assert.Equal("Frozen (guided reboot: reboot)", reason);
    }

    [Fact]
    public void Portal_comment_without_a_ticket_is_read()
    {
        var (_, ticket, reason, re) = ActionActivityRules.ParseComments("ATM frozen on the welcome screen (via KTC Monitoring Portal, ref abc123)");
        Assert.Null(ticket); Assert.Equal("ATM frozen on the welcome screen", reason); Assert.Equal("abc123", re);
    }
}
