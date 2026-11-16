using System.Text.Json;
using Ktc.Application.Abstractions;
using Ktc.Application.Models;
using Ktc.Application.Options;
using Ktc.Infrastructure.KtcApi;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ktc.Tests;

public class AtmGeneralTests
{
    // GET Client/{id} as KTC documents it (Postman PUT Client body), plus the lists the ids are named from.
    private static readonly Dictionary<string, string> Ktc = new()
    {
        ["api/v1/Client"] = """[{"ID":19,"Name":"ATM-NINETEEN","BusinessID":1,"BranchID":3}]""",
        ["api/v1/Client/19"] = """{"ID":19,"GUID":"CB222877-C3AB-45FA-936B-001E1A5E59E2","Address":"10.131.156.50","Subnet":"","Name":"ATM-NINETEEN","Latitude":24.4539,"Longitude":54.3773,"TimeZone":"Arabian Standard Time","CommentsString":"<Comments />","ConnectableByte":1,"TypeByte":1,"GridID":0,"BusinessID":1,"Level1RegionID":2,"Level2RegionID":0,"Level3RegionID":0,"Level4RegionID":0,"Level5RegionID":0,"BranchID":3,"HardwareID":7,"OwnerID":0,"DeleteLater":false,"DeletedTimestamp":"0001-01-01T00:00:00","IsActive":true,"Salt":"s3cr3t","AuthHash":"h4sh","IsHypervisorActive":false,"MergeToClientID":0,"FeatureFlagsString":"UMTB|TRRI|ENBL"}""",
        ["api/v1/Business"] = """[{"ID":1,"Name":"ADCB"}]""",
        ["api/v1/Branch"] = """[{"ID":3,"Name":"Corniche Branch","City":"Abu Dhabi","Address":"Corniche Road","PostCode":""}]""",
        ["api/v1/Region"] = """[{"ID":2,"Name":"Abu Dhabi"}]""",
    };

    [Fact]
    public async Task Shows_client_record_with_names_and_not_set_fields()
    {
        var g = await Service().GetAtmGeneralAsync("19");
        Assert.NotNull(g);
        DetailField F(string name) => g!.Groups.SelectMany(x => x.Fields).First(f => f.Name == name);
        Assert.Equal("ATM-NINETEEN", F("Name").Value);
        Assert.Equal("10.131.156.50", F("IP address").Value);
        Assert.Equal("", F("Subnet").Value);                                  // not set
        Assert.Equal(("ADCB", "1"), (F("Business").Value, F("Business").Id));
        Assert.Equal(("Corniche Branch", "3"), (F("Branch").Value, F("Branch").Id));
        Assert.Equal("Corniche Road", F("Branch address").Value);
        Assert.Equal("Abu Dhabi", F("Region level 1").Value);
        Assert.Equal("", F("Region level 2").Value);
        Assert.Equal("24.4539", F("Latitude").Value);
        Assert.Equal("CB222877-C3AB-45FA-936B-001E1A5E59E2", F("GUID").Value);
        Assert.Equal((24.4539, 54.3773), (g!.Latitude, g.Longitude));
        Assert.Contains("Subnet", g.NotSet); Assert.Contains("Region level 2", g.NotSet);
        Assert.DoesNotContain(g.Groups.SelectMany(x => x.Fields), f => f.Value.Contains("s3cr3t") || f.Value.Contains("h4sh") || f.Name is "Salt" or "Auth Hash");
        Assert.Null(await Service().GetAtmGeneralAsync("NO-SUCH-ATM"));
    }

    private static KtcDataService Service() =>
        new(new Fake(), Microsoft.Extensions.Options.Options.Create(new KtcOptions()), new MemoryCache(new MemoryCacheOptions()), new HttpContextAccessor(), NullLogger<KtcDataService>.Instance);

    private sealed class Fake : IKtcApiClient
    {
        public Task<KtcResponse> SendAsync(HttpMethod method, string url, object? body = null, KtcCallOptions? options = null, CancellationToken ct = default)
        {
            if (!Ktc.TryGetValue(url.Split('?')[0], out var json)) return Task.FromResult(KtcResponse.Fail("HTTP 404 Not Found", 404));
            using var d = JsonDocument.Parse(json);
            return Task.FromResult(new KtcResponse(true, 200, d.RootElement.Clone(), json, null, 1));
        }
    }
}
