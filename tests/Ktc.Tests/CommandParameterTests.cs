using System.Text.Json;
using Ktc.Application.Logic;
using Ktc.Application.Models;

namespace Ktc.Tests;

public class CommandParameterTests
{
    private const string GetSetting = """<Params><Param Name="Type" Type="Choice" Values="MultiLevelStore,PersistentData,Registry" Default="MultiLevelStore" /><Param Name="Location" Type="String" Required="true" /><Param Name="Use64bit" Type="Bool" Default="false" TrueText="64 Bit" FalseText="32 Bit" /></Params>""";

    [Fact]
    public void Reads_the_get_setting_example()
    {
        var (f, note) = CommandParameterRules.Parse(GetSetting);
        Assert.Null(note);
        Assert.Equal(new[] { "Type", "Location", "Use64bit" }, f.Select(x => x.Name));
        Assert.Equal(CommandParameterRules.Choice, f[0].Type);
        Assert.Equal(new[] { "MultiLevelStore", "PersistentData", "Registry" }, f[0].Choices);
        Assert.Equal("MultiLevelStore", f[0].Default);
        Assert.Equal(CommandParameterRules.Text, f[1].Type); Assert.True(f[1].Required);
        Assert.Equal(CommandParameterRules.Bool, f[2].Type); Assert.False(f[2].Required);
        Assert.Equal("64 Bit", f[2].TrueText); Assert.Equal("32 Bit", f[2].FalseText);
    }

    [Fact]
    public void Reads_other_template_shapes()
    {
        // element per parameter, choices as children, value as text, types inferred
        var (f, _) = CommandParameterRules.Parse("""
          <Parameters>
            <Type><Value>MultiLevelStore</Value><Value>Registry</Value></Type>
            <Use64bit>false</Use64bit>
            <Parameter Key="Count" DataType="System.Int32" Min="1" Max="10" Optional="true" />
            <Parameter Name="Mode" Type="Widget" />
            <Parameter Name="AdminPassword" Type="String" Default="hunter2" />
          </Parameters>
        """);
        Assert.Equal(CommandParameterRules.Choice, f.Single(x => x.Name == "Type").Type);
        Assert.Equal(CommandParameterRules.Bool, f.Single(x => x.Name == "Use64bit").Type);
        var count = f.Single(x => x.Name == "Count");
        Assert.Equal(CommandParameterRules.Integer, count.Type); Assert.False(count.Required); Assert.Equal(1, count.Min); Assert.Equal(10, count.Max);
        var mode = f.Single(x => x.Name == "Mode");
        Assert.Equal(CommandParameterRules.Text, mode.Type); Assert.Contains("Unknown type", mode.Note);
        var pw = f.Single(x => x.Name == "AdminPassword");
        Assert.Equal(CommandParameterRules.Secret, pw.Type); Assert.Null(pw.Default);   // never pre-filled or shown
    }

    [Fact]
    public void Empty_and_broken_templates()
    {
        Assert.Empty(CommandParameterRules.Parse("<Params />").Fields);
        Assert.Empty(CommandParameterRules.Parse(null).Fields);
        var (f, note) = CommandParameterRules.Parse("<Params><oops");
        Assert.Empty(f); Assert.Contains("not valid XML", note);
    }

    [Fact]
    public void Validates_values()
    {
        var (f, _) = CommandParameterRules.Parse(GetSetting + "");
        var (v, e) = CommandParameterRules.Validate(f, new Dictionary<string, string?> { ["type"] = "registry", ["Location"] = @"HKLM\SOFTWARE\KAL", ["Use64bit"] = "true" });
        Assert.Empty(e);
        Assert.Equal("Registry", v["Type"]);              // choice normalised to KTC's spelling
        Assert.Equal("true", v["Use64bit"]);

        (_, e) = CommandParameterRules.Validate(f, new Dictionary<string, string?> { ["Type"] = "Nope", ["Use64bit"] = "maybe", ["Extra"] = "x" });
        Assert.Contains(e, x => x.Contains("must be one of"));
        Assert.Contains(e, x => x.Contains("Location is required"));
        Assert.Contains(e, x => x.Contains("true or false"));
        Assert.Contains(e, x => x.Contains("not a parameter"));

        (v, e) = CommandParameterRules.Validate(f, new Dictionary<string, string?> { ["Type"] = "Registry", ["Location"] = "x" });
        Assert.Empty(e); Assert.Equal("false", v["Use64bit"]);   // bool left out = its default

        var (n, _) = CommandParameterRules.Parse("""<Params><P Name="Days" Type="Int32" Min="1" Max="31" /></Params>""");
        Assert.Contains("at most 31", CommandParameterRules.Validate(n, new Dictionary<string, string?> { ["Days"] = "40" }).Errors.Single());
        Assert.Contains("whole number", CommandParameterRules.Validate(n, new Dictionary<string, string?> { ["Days"] = "1.5" }).Errors.Single());
    }

    [Fact]
    public void Builds_escaped_parameter_xml()
    {
        var (f, _) = CommandParameterRules.Parse(GetSetting);
        var (v, _) = CommandParameterRules.Validate(f, new Dictionary<string, string?> { ["Type"] = "Registry", ["Location"] = "A<&>\"B", ["Use64bit"] = "true" });
        Assert.Equal("""<Parameters><Parameter Name="Type">Registry</Parameter><Parameter Name="Location">A&lt;&amp;&gt;"B</Parameter><Parameter Name="Use64bit">true</Parameter></Parameters>""",
            CommandParameterRules.BuildXml(f, v));
        Assert.Equal("""<Params><Param Key="Type">Registry</Param></Params>""",
            CommandParameterRules.BuildXml(f.Take(1).ToList(), v, "Params", "Param", "Key"));
    }

    [Fact]
    public void Describe_hides_secrets_and_masks_cards()
    {
        var (f, _) = CommandParameterRules.Parse("""<Params><P Name="Pwd" Type="Password" /><P Name="Note" Type="String" /></Params>""");
        var d = CommandParameterRules.Describe(f, new Dictionary<string, string> { ["Pwd"] = "hunter2", ["Note"] = "card 4111111111111111" });
        Assert.DoesNotContain("hunter2", d);
        Assert.DoesNotContain("4111111111111111", d);
    }

    [Fact]
    public void Classifies_special_high_risk_and_dual_auth_commands()
    {
        var list = CommandTypeRules.Parse(JsonDocument.Parse("""
          [{ "ID": 3, "CommandName": "KTC_EMERGENCY_REBOOT", "Description": "Emergency Reboot" },
           { "ID": 5, "CommandName": "KTC_REBOOT", "Description": "Reboot" },
           { "ID": 6, "CommandName": "KTC_SHUTDOWN", "Description": "Shutdown" },
           { "ID": 7, "CommandName": "KTC_GET_SETTING", "Description": "Get Settings" },
           { "ID": 8, "CommandName": "KTC_SET_SETTING", "Description": "Set Settings", "DualAuthModeByte": 1 },
           { "ID": 9, "CommandName": "KTC_PING", "Description": "Ping" }]
        """).RootElement);
        var planned = CommandTypeRules.Map(list, new Dictionary<string, string?> { ["Reboot"] = "KTC_REBOOT" }, true);
        var c = CommandTypeRules.Classify(list, planned, new Ktc.Application.Options.KtcActionsOptions().HighRiskPattern, "PING").ToDictionary(x => x.Id);
        Assert.True(c[3].HighRisk); Assert.Null(c[3].SendBlockedReason);           // emergency reboot: offered, extra confirmation
        Assert.Contains("guided reboot", c[5].SendBlockedReason);                  // reboot: guided flow only
        Assert.True(c[6].HighRisk);
        Assert.Null(c[7].SendBlockedReason); Assert.False(c[7].HighRisk);
        Assert.Contains("second login", c[8].SendBlockedReason);
        Assert.Contains("BlockedPattern", c[9].SendBlockedReason);
    }

    [Fact]
    public void Mapped_device_reset_and_service_commands_use_the_form_with_extra_confirmation()
    {
        var list = CommandTypeRules.Parse(JsonDocument.Parse("""
          [{ "ID": 7, "CommandName": "KTC_REBOOT" }, { "ID": 9, "CommandName": "KTC_RESET_DEVICE", "Description": "Reset one XFS device" },
           { "ID": 11, "CommandName": "KTC_SUPERVISOR_OFF" }, { "ID": 12, "CommandName": "KTC_SUPERVISOR_ON" }, { "ID": 30, "CommandName": "KTC_RESTART_PC" }]
        """).RootElement);
        var opt = new Ktc.Application.Options.KtcActionsOptions();      // defaults: the KTC server's names
        var planned = CommandTypeRules.Map(list, opt.Commands, true);
        var p = planned.ToDictionary(a => a.Key);
        Assert.Equal(7, p["Reboot"].CommandTypeId); Assert.Equal(9, p["DeviceReset"].CommandTypeId);
        Assert.Equal(12, p["OutOfService"].CommandTypeId); Assert.Equal(11, p["InService"].CommandTypeId);
        Assert.Null(p["XfsReset"].CommandTypeId);
        var c = CommandTypeRules.Classify(list, planned, opt.HighRiskPattern, "").ToDictionary(x => x.Id);
        Assert.Contains("guided reboot", c[7].SendBlockedReason);
        foreach (var i in new[] { 9, 11, 12 }) { Assert.Null(c[i].SendBlockedReason); Assert.True(c[i].HighRisk); }
        Assert.Contains("map it", c[30].SendBlockedReason);                       // unmapped look-alike stays out
    }

    [Fact]
    public void Guided_flows_fill_reason_like_text_fields_with_the_ticket()
    {
        var (ps, _) = CommandParameterRules.Parse("<Params><Param Name=\"Reason\" Type=\"String\" Required=\"true\" MaxLength=\"10\" /><Param Name=\"Mode\" Type=\"Choice\" Values=\"A,B\" Default=\"A\" /></Params>");
        var (vals, errs) = CommandParameterRules.Validate(ps, CommandParameterRules.GuidedValues(ps, "Ticket 1: frozen screen"));
        Assert.Empty(errs); Assert.Equal("Ticket 1:", vals["Reason"]); Assert.Equal("A", vals["Mode"]);
    }
}
