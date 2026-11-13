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

public class HardwareNameTests
{
    private sealed class Fake : Ktc.Application.Abstractions.IKtcApiClient
    {
        public Task<Ktc.Application.Models.KtcResponse> SendAsync(HttpMethod method, string url, object? body = null, Ktc.Application.Abstractions.KtcCallOptions? options = null, CancellationToken ct = default)
        {
            var path = url.Split('?')[0];
            string? json = path switch
            {
                "api/v1/Client" => """[{"ID":19,"Name":"ATM-NINETEEN","BusinessID":0}]""",
                "api/v1/Client/19/CurrentStatus" => """[{"CompId":7,"PropId":70,"VID":900,"ManualValue":"","NumericValue":0,"UseNumeric":false,"Timestamp":"2026-11-13T10:00:00"},{"CompId":7,"PropId":71,"VID":-1,"ManualValue":"","NumericValue":3,"UseNumeric":true,"Timestamp":"2026-11-13T10:00:00"},{"CompId":8,"PropId":72,"VID":-1,"ManualValue":"true","NumericValue":0,"UseNumeric":false,"Timestamp":"2026-11-13T10:00:00"}]""",
                "api/v1/ComponentList/7" => """{"ID":7,"Name":"Card Reader"}""",
                "api/v1/ComponentList/8" => """{"ID":8,"Name":"Safe"}""",
                "api/v1/PropertyList/UsedByComponent" when url.EndsWith("=7") => """[{"ID":70,"Name":"Device status"},{"ID":71,"Name":"Cards retained"}]""",
                "api/v1/PropertyList/72" => """{"ID":72,"Name":"Door closed"}""",
                "api/v1/ValueList/900" => """{"ID":900,"Value":"Media jammed"}""",
                _ => null
            };
            if (json == null) return Task.FromResult(new Ktc.Application.Models.KtcResponse(false, 500, null, "", "HTTP 500 NullReferenceException", 1));
            using var d = System.Text.Json.JsonDocument.Parse(json);
            return Task.FromResult(new Ktc.Application.Models.KtcResponse(true, 200, d.RootElement.Clone(), json, null, 1));
        }
    }

    [Fact]
    public async Task Names_and_values_resolve_through_per_id_routes_when_the_lists_fail()
    {
        var svc = new Ktc.Infrastructure.KtcApi.KtcDataService(new Fake(), Microsoft.Extensions.Options.Options.Create(new Ktc.Application.Options.KtcOptions()),
            new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()), new Microsoft.AspNetCore.Http.HttpContextAccessor(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Ktc.Infrastructure.KtcApi.KtcDataService>.Instance);
        var hw = await svc.GetHardwareAsync("19");
        Assert.NotNull(hw);
        var reader = hw!.Modules.Single(m => m.Name == "Card Reader");
        Assert.Contains(reader.Properties, p => p.Name == "Device status" && p.Value == "Media jammed" && p.Status == "Fatal");
        Assert.Contains(reader.Properties, p => p.Name == "Cards retained" && p.Value == "3");
        Assert.Contains(hw.Modules.Single(m => m.Name == "Safe").Properties, p => p.Name == "Door closed" && p.Value == "Yes");
    }
}
