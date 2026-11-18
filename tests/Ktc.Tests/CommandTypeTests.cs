using System.Text.Json;
using Ktc.Application.Logic;

namespace Ktc.Tests;

public class CommandTypeTests
{
    private static JsonElement J(string s) => JsonDocument.Parse(s).RootElement.Clone();

    // Shape of the Postman collection's POST api/v1/CommandType sample.
    private const string Sample = """
    [{ "ID": 1, "CommandName": "UPLOAD", "Description": "Upload a file", "ParameterListTemplateString": "<Params />", "SubCommandListString": "",
       "AllowClientInitiation": true, "MaxRetries": 0, "RetryWaitTime": 0, "MaxSimultaneous": 50, "TargetTypeByte": 0, "IsUserDefined": false,
       "Category": "File Management", "DualAuthModeByte": 0 },
     { "ID": 7, "CommandName": "REBOOT", "Description": "Reboot the ATM", "Category": "Device Control", "DualAuthModeByte": 2 }]
    """;

    [Fact]
    public void Parses_the_collection_shape()
    {
        var list = CommandTypeRules.Parse(J(Sample));
        Assert.Equal(2, list.Count);
        var up = list.Single(c => c.Name == "UPLOAD");
        Assert.Equal(1, up.Id); Assert.Equal("File Management", up.Category); Assert.Equal("<Params />", up.ParameterTemplate);
        Assert.True(up.AllowClientInitiation); Assert.Equal(50, up.MaxSimultaneous); Assert.False(up.RequiresDualAuth);
        var rb = list.Single(c => c.Name == "REBOOT");
        Assert.True(rb.RequiresDualAuth);
        Assert.Null(rb.MaxRetries);          // missing fields stay unknown instead of 0
        Assert.Null(rb.AllowClientInitiation);
    }

    [Fact]
    public void Parses_an_id_keyed_map_and_a_wrapped_list()
    {
        var map = CommandTypeRules.Parse(J("""{ "12": { "CommandName": "XFS_RESET" }, "13": { "Name": "DEVICE_RESET" } }"""));
        Assert.Equal(new[] { 12, 13 }, map.Select(c => c.Id).OrderBy(i => i));
        var wrapped = CommandTypeRules.Parse(J("""{ "Data": [ { "ID": 3, "CommandName": "REBOOT" } ] }"""));
        Assert.Equal("REBOOT", Assert.Single(wrapped).Name);
        Assert.Empty(CommandTypeRules.Parse(J("[]")));
        Assert.Empty(CommandTypeRules.Parse(null));
    }

    [Theory]
    [InlineData("<Params><Password>hunter2</Password></Params>", "<Params><Password>***</Password></Params>")]
    [InlineData("user=a;pwd=abc123", "user=a;pwd=***")]
    [InlineData("token=\"xyz\"", "token=\"***\"")]
    [InlineData("<Params><Device /></Params>", "<Params><Device /></Params>")]
    [InlineData("<Params><WhenIdle>true</WhenIdle></Params>", "<Params><WhenIdle>true</WhenIdle></Params>")]
    public void Hides_secret_looking_values(string input, string expected) => Assert.Equal(expected, CommandTypeRules.Sanitize(input));

    [Fact]
    public void Masks_card_numbers_in_text()
    {
        var c = Assert.Single(CommandTypeRules.Parse(J("""[{ "ID": 5, "CommandName": "TEST", "Description": "card 4111111111111111" }]""")));
        Assert.DoesNotContain("4111111111111111", c.Description);
        Assert.Contains("411111******1111", c.Description);
    }

    [Fact]
    public void Maps_configured_commands_and_never_enables_them()
    {
        var list = CommandTypeRules.Parse(J("""
          [{ "ID": 3, "CommandName": "REBOOT" }, { "ID": 5, "CommandName": "XFS_RESET" }, { "ID": 6, "CommandName": "DEVICE_RESET" },
           { "ID": 8, "CommandName": "OUT_OF_SERVICE" }, { "ID": 9, "CommandName": "IN_SERVICE", "DualAuthModeByte": 1 }]
        """));
        var cfg = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["Reboot"] = "reboot", ["InService"] = "IN_SERVICE", ["XfsReset"] = "NOPE", ["DeviceReset"] = "6" };
        var p = CommandTypeRules.Map(list, cfg, true).ToDictionary(a => a.Key);
        Assert.Equal(3, p["Reboot"].CommandTypeId);
        Assert.True(p["InService"].RequiresDualAuth);
        Assert.Null(p["XfsReset"].CommandTypeId);
        Assert.Contains("no such command", p["XfsReset"].Status);
        Assert.Equal(6, p["DeviceReset"].CommandTypeId);          // an id works too
        Assert.Null(p["OutOfService"].CommandTypeId);
        Assert.Contains("OUT_OF_SERVICE (#8)", p["OutOfService"].Suggestions);
        Assert.Contains("XFS_RESET (#5)", p["XfsReset"].Suggestions);
        Assert.All(p.Values, a => Assert.False(a.Enabled));
    }

    [Fact]
    public void Suggestions_do_not_mix_up_reboot_and_resets()
    {
        var list = CommandTypeRules.Parse(J("""[{ "ID": 3, "CommandName": "REBOOT" }, { "ID": 5, "CommandName": "XFS_RESET" }, { "ID": 6, "CommandName": "DEVICE_RESET" }, { "ID": 4, "CommandName": "RESTART_APPLICATION" }]"""));
        var p = CommandTypeRules.Map(list, new Dictionary<string, string?>(), true).ToDictionary(a => a.Key);
        Assert.Equal(new[] { "REBOOT (#3)" }, p["Reboot"].Suggestions);
        Assert.Equal(new[] { "XFS_RESET (#5)" }, p["XfsReset"].Suggestions);
        Assert.Equal(new[] { "DEVICE_RESET (#6)" }, p["DeviceReset"].Suggestions);

        // Descriptions are used only when no name looks right.
        var byDesc = CommandTypeRules.Parse(J("""[{ "ID": 1, "CommandName": "CMD_A", "Description": "Reboot the PC" }, { "ID": 2, "CommandName": "CMD_B", "Description": "Reset one XFS device" }]"""));
        var q = CommandTypeRules.Map(byDesc, new Dictionary<string, string?>(), true).ToDictionary(a => a.Key);
        Assert.Equal(new[] { "CMD_A (#1)" }, q["Reboot"].Suggestions);
        Assert.Equal(new[] { "CMD_B (#2)" }, q["DeviceReset"].Suggestions);
    }

    [Fact]
    public void Lists_field_names_known_first()
    {
        var f = CommandTypeRules.Fields(J("""[{ "Zeta": 1, "CommandName": "A", "ID": 1 }]"""));
        Assert.Equal(new[] { "ID", "CommandName", "Zeta" }, f);
    }
}
