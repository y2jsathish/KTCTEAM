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
    public void Comment_body_matches_the_windows_client_trace()
    {
        var c = JsonSerializer.SerializeToElement(DisputeRules.CommentBody("10325", "tesst 4111111111111111", 13, 5));
        Assert.Equal(13, c.GetProperty("table").GetInt32());
        Assert.Equal(10325, c.GetProperty("rowID").GetInt64());
        Assert.Equal(JsonValueKind.Null, c.GetProperty("kcComment").ValueKind);
        Assert.Equal(5, c.GetProperty("SendStatusParameter").GetInt64());
        Assert.Equal(-1, c.GetProperty("BusinessID").GetInt32());
        Assert.False(c.GetProperty("Confidential").GetBoolean());
        Assert.Equal("tesst 411111******1111", c.GetProperty("strComment").GetString());
    }

    [Fact]
    public void Amount_falls_back_to_the_amount_extra_data_ktc_defines()
    {
        var d = DisputeRules.Map(J("""{"ID":1,"ExtraData":"<ExtraDataValues><ExtraData Name=\"Amount\" Value=\"250.00\"/><ExtraData Name=\"TSN\" Value=\"0042\"/></ExtraDataValues>"}"""), States, null, _ => null)!;
        Assert.Equal(250m, d.Amount);
        Assert.Equal(400m, DisputeRules.Map(J("""{"ID":1,"ExtraData":{"EffectiveAmount":400}}"""), States, null, _ => null)!.Amount);
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

    [Fact]
    public void Rules_and_last_comment_come_from_ktc_records()
    {
        using var rules = JsonDocument.Parse("""{"3":{"ID":3,"Name":"CIM-ITEMS-RETRACTED_CashDepositToAccount"}}""");
        Assert.Equal("CIM-ITEMS-RETRACTED_CashDepositToAccount", DisputeRules.ParseRules(rules.RootElement)[3]);
        using var rec = JsonDocument.Parse("""{"ID":10325,"ClientID":5,"DisputeRuleID":3,"CommentsString":"<Comments><Comment User=\"System\" Timestamp=\"2026-11-11 12:45:18\">Dispute created automatically based on matching rule.</Comment><Comment User=\"ops\" Timestamp=\"2026-11-18 16:57:24\">card 4111111111111111 checked</Comment></Comments>"}""");
        var d = DisputeRules.Map(rec.RootElement, new List<DisputeStateDto>(), null, _ => null, DisputeRules.ParseRules(rules.RootElement))!;
        Assert.Equal("CIM-ITEMS-RETRACTED_CashDepositToAccount", d.Rule);
        Assert.Equal("card 411111******1111 checked", d.LastComment);
        using var tx = JsonDocument.Parse("""{"ID":2,"TransactionID":1775843605200}""");
        Assert.Equal("1775843605200", DisputeRules.Map(tx.RootElement, new List<DisputeStateDto>(), null, _ => null)!.TransactionId);
        Assert.Equal("Rule 9", DisputeRules.Map(JsonDocument.Parse("""{"ID":1,"DisputeRuleID":9}""").RootElement, new List<DisputeStateDto>(), null, _ => null)!.Rule);
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
        Assert.Contains(r.States, s => s.Id == 0 && s.Name == "All");
        Assert.Contains(r.Sources, x => x.Contains("GroupDisputes?statusfilter=0&Numdays=14"));
        Assert.Contains(r.Rows, d => d.Rule.Length > 0 && !d.Rule.StartsWith("Rule "));
        Assert.All(r.Rows, d => Assert.NotEmpty(d.LastComment));
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
        var s = all.First(d => d.StateId > 0).StateId!.Value;
        // statusfilter: the state id, sent to KTC; 0 = all states.
        var one = await svc.SearchAsync(new DisputeQuery(StateId: s));
        Assert.NotEmpty(one.Rows);
        Assert.All(one.Rows, d => Assert.Equal(s, d.StateId));
        Assert.Contains(one.Sources, x => x.Contains($"statusfilter={s}&"));
        Assert.Equal(all.Count, (await svc.SearchAsync(new DisputeQuery(StateId: 0))).Rows.Count);
        Assert.All((await svc.SearchAsync(new DisputeQuery(MinAmount: 1000))).Rows, d => Assert.True(d.Amount >= 1000));
    }

    [Fact]
    public async Task Details_show_customer_history_and_notifications_masked()
    {
        var svc = Service();
        var row = (await svc.SearchAsync(new DisputeQuery())).Rows.First(d => d.IsClosed);
        var d = await svc.GetDetailsAsync(row.Id, row.AtmId);
        Assert.NotNull(d);
        Assert.StartsWith($"GET api/v1/DisputeManagement/{row.AtmId}/ClientDisputes", d!.Sources[0]);
        Assert.Contains(d.Sources, x => x.StartsWith("POST api/v1/DisputeManagement/SMSQueue/Disputes"));
        Assert.NotNull(d.Dispute.Amount);      // from extra data "Amount"
        Assert.Contains(d!.Customer, f => f.Name == "customerName" && f.Value.Length > 0);
        Assert.Equal(new[] { "Amount", "EffectiveAmount", "TSN" }, d.ExtraData.Select(f => f.Name));
        Assert.Contains(d.Fields, f => f.Name == "PAN" && f.Value.Contains('*'));
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
        var (svc, audit) = Writer(enabled: true, allowState: true);
        var data = Service();
        var open = (await data.SearchAsync(new DisputeQuery())).Rows.Where(d => !d.IsClosed).OrderBy(d => d.Id).ToList();
        var a = open[^1]; var b = open[^2];

        // KTC's Change Dispute State dialog: a comment is required, and state 0 ("All") is not offered.
        var noComment = await svc.ChangeStateAsync(a.Id, new DisputeWriteRequest(4 == a.StateId ? 5 : 4, "", null));
        Assert.False(noComment.Done); Assert.Empty(noComment.KtcSteps); Assert.Contains(noComment.Checks, c => c.Name == "Comment" && !c.Passed);
        var toAll = await svc.ChangeStateAsync(a.Id, new DisputeWriteRequest(0, "Asked the customer for a receipt", null));
        Assert.False(toAll.Done); Assert.Contains(toAll.Checks, c => c.Name == "New state" && !c.Passed);
        var unknown = await svc.ChangeStateAsync(a.Id, new DisputeWriteRequest(99, "Asked the customer for a receipt", a.Id));
        Assert.False(unknown.Done); Assert.Contains(unknown.Checks, c => c.Name == "New state" && !c.Passed);

        var target = a.StateId == 4 ? 5 : 4;
        var ok = await svc.ChangeStateAsync(a.Id, new DisputeWriteRequest(target, "Asked the customer for a receipt", null));
        Assert.True(ok.Done, ok.Message);
        Assert.Equal(target, (await data.GetAsync(a.Id))!.StateId);

        var closed = await svc.CloseAsync(b.Id, new DisputeWriteRequest(null, "Refund paid to the customer", b.Id));
        Assert.True(closed.Done, closed.Message);
        Assert.True((await data.GetAsync(b.Id))!.IsClosed);
        var again = await svc.CloseAsync(b.Id, new DisputeWriteRequest(null, "Refund paid to the customer", b.Id));
        Assert.False(again.Done);
        Assert.Contains(audit.Entries, x => x.Contains("Dispute closed"));
    }

    [Fact]
    public async Task Comments_go_through_comment_api_table_13_and_are_read_back()
    {
        var (svc, audit) = Writer(enabled: true);
        var data = Service();
        var d = (await data.SearchAsync(new DisputeQuery())).Rows.Where(x => !x.IsClosed).OrderBy(x => x.Id).Skip(2).First();
        var r = await svc.AddCommentAsync(d.Id, new DisputeWriteRequest(null, "Customer sent receipt for card 4111111111111111", null, d.AtmId));
        Assert.True(r.Done, r.Message);
        Assert.Contains(r.KtcSteps, x => x.Contains("table 13") && x.Contains($"SendStatusParameter {d.AtmId}"));
        var after = await data.GetDetailsAsync(d.Id, d.AtmId);
        Assert.Contains(after!.Comments, c => c.Text.Contains("Customer sent receipt") && c.Text.Contains("411111******1111"));
        Assert.DoesNotContain(audit.Entries, x => x.Contains("4111111111111111"));
    }

    [Fact]
    public async Task State_change_and_close_stay_off_until_confirmed_even_when_writing_is_on()
    {
        var (svc, _) = Writer(enabled: true);
        var d = (await Service().SearchAsync(new DisputeQuery())).Rows.First(x => !x.IsClosed);
        var st = await svc.StateAsync(d.Id, d.AtmId);
        Assert.True(st.CanComment); Assert.False(st.CanChangeState); Assert.False(st.CanClose);
        var r = await svc.CloseAsync(d.Id, new DisputeWriteRequest(null, "Refund paid to the customer", d.Id, d.AtmId));
        Assert.False(r.Done); Assert.Empty(r.KtcSteps);
        Assert.Contains(r.Checks, c => c.Name == "State change confirmed" && !c.Passed);
    }

    private static KtcDisputeService Service() =>
        new(new MockClient(), Microsoft.Extensions.Options.Options.Create(new KtcOptions()), Microsoft.Extensions.Options.Options.Create(new DisputeOptions()),
            new MemoryCache(new MemoryCacheOptions()), new HttpContextAccessor(), NullLogger<KtcDisputeService>.Instance);

    private static (DisputeWriteService, FakeAudit) Writer(bool enabled, bool allowState = false)
    {
        var a = new FakeAudit();
        return (new DisputeWriteService(new MockClient(), Service(), Microsoft.Extensions.Options.Options.Create(new DisputeWriteOptions { Enabled = enabled, AllowStateChange = allowState }), a,
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
