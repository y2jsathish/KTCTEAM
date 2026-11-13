using Ktc.Application.Logic;
using Xunit;

namespace Ktc.Tests;

public class HardwareTests
{
    [Theory]
    [InlineData("Online", "OK")]
    [InlineData("OK", "OK")]
    [InlineData("NoError", "OK")]
    [InlineData("0", "OK")]
    [InlineData("MediaJammed", "Fatal")]
    [InlineData("Offline", "Fatal")]
    [InlineData("Paper out", "Fatal")]
    [InlineData("Hardware error", "Fatal")]
    [InlineData("Cassette low", "Warning")]
    [InlineData("Safe door open", "Warning")]
    [InlineData("v1.2", "Unknown")]
    [InlineData("", "Unknown")]
    public void Classifies_component_values(string value, string expected) => Assert.Equal(expected, HardwareStatusRules.Classify(value));

    [Fact]
    public void Worst_status_wins_and_unknown_does_not_hide_others()
    {
        Assert.Equal("Fatal", HardwareStatusRules.Worst(new[] { "OK", "Fatal", "Warning" }));
        Assert.Equal("OK", HardwareStatusRules.Worst(new[] { "Unknown", "OK" }));
        Assert.Equal("Unknown", HardwareStatusRules.Worst(Array.Empty<string>()));
    }

    [Fact]
    public void Pins_errors_to_components()
    {
        Assert.True(HardwareStatusRules.Mentions("Card reader fatal error", "Card Reader"));
        Assert.True(HardwareStatusRules.Mentions("IDC device not responding", "Card Reader"));
        Assert.True(HardwareStatusRules.Mentions("Cassette 2 empty", "Cash Dispenser"));
        Assert.False(HardwareStatusRules.Mentions("Receipt paper low", "Cash Dispenser"));
        Assert.Equal("Receipt printer", HardwareStatusRules.ModuleFor("Receipt paper low or out"));
    }
}
