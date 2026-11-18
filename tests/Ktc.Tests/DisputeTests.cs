using System.Text.Json;
using Ktc.Application.Abstractions;
using Ktc.Application.Logic;
using Ktc.Application.Models;
using Ktc.Application.Options;
using Ktc.Infrastructure.KtcApi;
using Ktc.Infrastructure.Mock;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ktc.Tests;

public class DisputeRulesTests
{
    private static JsonElement J(string s) { using var d = JsonDocument.Parse(s); return d.RootElement.Clone(); }
    private static readonly List<DisputeStateDto> States = new() { new(1, "New", false), new(6, "Closed", true) };

    [Fact]
    public void Reads_states_from_a_list_or_an_id_keyed_map()
    {
        Assert.Equal(new[] { 1, 2 }, DisputeRules.ParseStates(J("""[{"ID":2,"Name":"Investigating"},{"ID":1,"Name":"New","IsFinal":false}]""")).Select(s => s.Id));
        var m = DisputeRules.ParseStates(J("""{"3":{"Name":"Rejected","IsClosed":true},"4":{"Name":"Paid"}}"""));
        Assert.Equal(("Rejected", (bool?)true), (m[0].Name, m[0].IsFinal));
        Assert.Null(m[1].IsFinal);
    }

    [Fact]
    public void An_unknown_state_number_is_shown_raw_not_guessed()
    {
        Assert.Equal("State 7", DisputeRules.StateName(7, States));
        Assert.Equal("Paid out", DisputeRules.StateName(7, States, new Dictionary<string, string> { ["7"] = "Paid out" }));
        Assert.Equal("New", DisputeRules.StateName(1, States));
        var d = DisputeRules.Map(J("""{"ID":5,"StateID":7}"""), States, null, _ => null)!;
        Assert.Equal(("State 7", (int?)7, false), (d.State, d.StateId, d.IsClosed));
    }

    [Fact]
    public void Maps_a_record_and_masks_every_card_number()
    {
        var e = J("""
        {"ID":42,"ClientID":1003,"StateID":6,"TimeCreated":"2026-11-10T08:00:00Z","Amount":"350.50","Currency":"AED","PAN":"4111111111111111",
         "Reason":"Customer says card 4111 1111 1111 1111 was charged","CustomerDetails":{"Name":"A. Rahman","CardNumber":"5500005555555559"}}
        """);
        var d = DisputeRules.Map(e, States, null, id => id == "1003" ? ("ENB-ATM-004", "Emirates NBD") : null)!;
        Assert.Equal("411111******1111", d.Card);
        Assert.DoesNotContain("4111 1111 1111 1111", d.Reason);
        Assert.Equal(350.50m, d.Amount);
        Assert.Equal(("ENB-ATM-004", "Emirates NBD", "A. Rahman"), (d.AtmName, d.Client, d.Customer));
        Assert.True(d.IsClosed);       // KTC marks state 6 final
    }

    [Fact]
    public void Track_cvv_and_pin_fields_are_hidden_at_any_depth()
    {
        var f = DisputeRules.Fields(J("""{"Track2Data":"4111111111111111=2812","CVV":"123","ExtraData":{"PinBlock":"ABCD","Note":"card 5500005555555559"},"CardNo":"4012888888881881"}"""));
        Assert.Equal("(hidden)", f.Single(x => x.Name == "Track2Data").Value);
        Assert.Equal("(hidden)", f.Single(x => x.Name == "CVV").Value);
        Assert.Equal("401288******1881", f.Single(x => x.Name == "CardNo").Value);
        var extra = f.Single(x => x.Name == "ExtraData").Value;
        Assert.Contains("(hidden)", extra);
        Assert.DoesNotContain("ABCD", extra);
        Assert.DoesNotContain("5500005555555559", extra);
    }

    [Fact]
    public void Reads_name_value_maps_in_every_shape_ktc_may_send()
    {
        Assert.Equal("x@y.com", DisputeRules.MapFields(J("""{"CustomerInfo":[{"Key":"Email","Value":"x@y.com"}]}"""), DisputeRules.CustomerF)[0].Value);
        var xml = DisputeRules.MapFields(J("""{"ExtraData":"<ExtraData><Field Name=\"Branch\" Value=\"Dubai\"/><CardNumber>4111111111111111</CardNumber></ExtraData>"}"""), DisputeRules.ExtraDataF);
        Assert.Equal("Dubai", xml.Single(x => x.Name == "Branch").Value);
        Assert.Equal("411111******1111", xml.Single(x => x.Name == "CardNumber").Value);
        Assert.Equal("Mobile", DisputeRules.MapFields(J("""{"CustomerDetails":"{\"Mobile\":\"0501234567\"}"}"""), DisputeRules.CustomerF)[0].Name);
    }

    [Fact]
    public void Filters_by_amount_state_and_card_last_four_digits()
    {
        var all = new[]
        {
            new DisputeDto { Id = "1", Amount = 100, StateId = 1, Card = "411111******1111" },
            new DisputeDto { Id = "2", Amount = 900, StateId = 2, Card = "550000******5559" },
            new DisputeDto { Id = "3", Amount = null, StateId = 1 }
        };
        Assert.Equal(new[] { "2" }, DisputeRules.Filter(all, new DisputeQuery(MinAmount: 500)).Select(d => d.Id));
        Assert.Equal(new[] { "1", "3" }, DisputeRules.Filter(all, new DisputeQuery(StateId: 1)).Select(d => d.Id));
        Assert.Equal(new[] { "2" }, DisputeRules.Filter(all, new DisputeQuery(Card: "5559")).Select(d => d.Id));
        // A full number typed by mistake only ever compares its last 4 digits.
        Assert.Equal(new[] { "1" }, DisputeRules.Filter(all, new DisputeQuery(Card: "4111 1111 1111 1111")).Select(d => d.Id));
    }

    [Fact]
    public void Search_and_change_state_bodies_follow_the_ktc_shapes()
    {
        var s = JsonSerializer.SerializeToElement(DisputeRules.SearchBody(2, 2, new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 18, 0, 0, 0, DateTimeKind.Utc), new[] { 1, 2 }));
        Assert.Equal(new[] { 1, 2 }, s.GetProperty("StatusIDList").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal(JsonValueKind.Object, s.GetProperty("TransactionFieldFilters").ValueKind);
        Assert.Equal(2, s.GetProperty("BusinessID").GetInt32());
        Assert.Equal(JsonValueKind.Null, JsonSerializer.SerializeToElement(DisputeRules.SearchBody(1, 2, DateTime.UtcNow, DateTime.UtcNow, Array.Empty<int>())).GetProperty("StatusIDList").ValueKind);
        var c = JsonSerializer.SerializeToElement(DisputeRules.ChangeStateBody(4, "refund for 4111111111111111"));
        Assert.Equal(4, c.GetProperty("Item1").GetInt32());
        Assert.DoesNotContain("4111111111111111", c.GetProperty("Item2").GetString());
    }
}

public class DisputeServiceTests
{
    private static readonly string[] TestPans = { "4111111111111111", "5500005555555559", "4012888888881881", "5105105105105100", "4000056655665556", "4111 1111 1111 1111" };

    [Fact]
    public async Task Lists_demo_disputes_with_ktc_state_names_and_no_full_card_number()
    {
        var r = await Service().SearchAsync(new DisputeQuery());
        Assert.NotEmpty(r.Rows);
        Assert.Empty(r.Warnings);
        Assert.Contains(r.States, s => s.Name == "Under Investigation");
        Assert.All(r.Rows, d => Assert.Contains('*', d.Card));
        Assert.Contains(r.Rows, d => d.IsClosed);
        Assert.Contains(r.Rows, d => !d.IsClosed);
        var json = JsonSerializer.Serialize(r);
        Assert.DoesNotContain(TestPans, json.Contains);
    }

    [Fact]
    public async Task Filters_by_state_and_amount_on_the_demo_server()
    {
        var svc = Service();
        var all = (await svc.SearchAsync(new DisputeQuery())).Rows;
        var s = all.First().StateId!.Value;
        Assert.All((await svc.SearchAsync(new DisputeQuery(StateId: s))).Rows, d => Assert.Equal(s, d.StateId));
        Assert.All((await svc.SearchAsync(new DisputeQuery(MinAmount: 1000))).Rows, d => Assert.True(d.Amount >= 1000));
    }

    [Fact]
    public async Task Details_show_customer_history_and_notifications_masked()
    {
        var svc = Service();
        var row = (await svc.SearchAsync(new DisputeQuery())).Rows.First(d => d.IsClosed);
        var d = await svc.GetDetailsAsync(row.Id);
        Assert.NotNull(d);
        Assert.Contains(d!.Customer, f => f.Name == "CardNumber" && f.Value.Contains('*'));
        Assert.Equal("(hidden)", d.ExtraData.Single(f => f.Name == "Track2Data").Value);
        Assert.NotEmpty(d.History);
        Assert.NotEmpty(d.Comments);
        Assert.NotEmpty(d.Notifications);
        Assert.DoesNotContain(TestPans, JsonSerializer.Serialize(d).Contains);
        Assert.Null(await svc.GetDetailsAsync("1"));
    }

    [Fact]
    public async Task Writing_off_sends_nothing_and_is_audited()
    {
        var (svc, audit) = Writer(enabled: false);
        var data = Service();
        var open = (await data.SearchAsync(new DisputeQuery())).Rows.First(d => !d.IsClosed);
        var r = await svc.ChangeStateAsync(open.Id, new DisputeWriteRequest(5, "Customer withdrew the claim", open.Id));
        Assert.False(r.Done);
        Assert.Contains(r.Checks, c => c.Name == "Writing enabled" && !c.Passed);
        Assert.Empty(r.KtcSteps);
        Assert.Equal(open.StateId, (await data.GetAsync(open.Id))!.StateId);
        Assert.Contains(audit.Entries, a => a.Contains("not changed") && a.Contains(open.Id));
    }

    [Fact]
    public async Task Change_state_and_close_are_verified_by_reading_the_dispute_back()
    {
        var (svc, audit) = Writer(enabled: true);
        var data = Service();
        var open = (await data.SearchAsync(new DisputeQuery())).Rows.Where(d => !d.IsClosed).OrderBy(d => d.Id).ToList();
        var a = open[^1]; var b = open[^2];

        var bad = await svc.ChangeStateAsync(a.Id, new DisputeWriteRequest(3 == a.StateId ? 2 : 3, "Asked the customer for a receipt", "wrong"));
        Assert.False(bad.Done); Assert.Empty(bad.KtcSteps);
        var unknown = await svc.ChangeStateAsync(a.Id, new DisputeWriteRequest(99, "Asked the customer for a receipt", a.Id));
        Assert.False(unknown.Done); Assert.Contains(unknown.Checks, c => c.Name == "New state" && !c.Passed);

        var target = a.StateId == 3 ? 2 : 3;
        var ok = await svc.ChangeStateAsync(a.Id, new DisputeWriteRequest(target, "Asked the customer for a receipt", a.Id));
        Assert.True(ok.Done, ok.Message);
        Assert.Equal(target, (await data.GetAsync(a.Id))!.StateId);

        var closed = await svc.CloseAsync(b.Id, new DisputeWriteRequest(null, "Refund paid to the customer", b.Id));
        Assert.True(closed.Done, closed.Message);
        Assert.True((await data.GetAsync(b.Id))!.IsClosed);
        var again = await svc.CloseAsync(b.Id, new DisputeWriteRequest(null, "Refund paid to the customer", b.Id));
        Assert.False(again.Done);
        Assert.Contains(audit.Entries, x => x.Contains("Dispute closed"));
    }

    private static KtcDisputeService Service() =>
        new(new MockClient(), Microsoft.Extensions.Options.Options.Create(new KtcOptions()), Microsoft.Extensions.Options.Options.Create(new DisputeOptions()),
            new MemoryCache(new MemoryCacheOptions()), new HttpContextAccessor(), NullLogger<KtcDisputeService>.Instance);

    private static (DisputeWriteService, FakeAudit) Writer(bool enabled)
    {
        var a = new FakeAudit();
        return (new DisputeWriteService(new MockClient(), Service(), Microsoft.Extensions.Options.Options.Create(new DisputeWriteOptions { Enabled = enabled }), a,
            new HttpContextAccessor(), NullLogger<DisputeWriteService>.Instance), a);
    }

    private sealed class FakeAudit : IAuditService
    {
        public List<string> Entries { get; } = new();
        public Task LogAsync(string category, string action, string? target = null, string? details = null, bool success = true, string? userOverride = null)
        { lock (Entries) Entries.Add($"{category} {action} {target} {details}"); return Task.CompletedTask; }
    }

    private sealed class MockClient : IKtcApiClient
    {
        private readonly HttpClient _http = new(new MockKtcHttpHandler()) { BaseAddress = new Uri("https://ktc.test/") };
        public async Task<KtcResponse> SendAsync(HttpMethod method, string url, object? body = null, KtcCallOptions? options = null, CancellationToken ct = default)
        {
            using var req = new HttpRequestMessage(method, url);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer test");
            if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
            using var res = await _http.SendAsync(req, ct);
            var raw = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) return KtcResponse.Fail($"HTTP {(int)res.StatusCode}", (int)res.StatusCode);
            using var d = JsonDocument.Parse(raw);
            return new KtcResponse(true, (int)res.StatusCode, d.RootElement.Clone(), raw, null, 1);
        }
    }
}
