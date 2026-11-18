using Ktc.Application.Logic;
using Ktc.Application.Models;
using Ktc.Application.Options;
using Xunit;

namespace Ktc.Tests;

public class RebootWorkflowTests
{
    private sealed class FakeProbe : IRebootProbe
    {
        public List<string> Sent { get; } = new();
        public Dictionary<string, (bool Final, bool Failed)> Actions { get; } = new();
        public bool? Idle = true;
        public DateTime? Contact;
        public (bool, string)? Devices = (true, "");
        public Func<string, bool> SendOk = _ => true;
        private int _n;
        public Task<(bool Ok, string? ActionId, string? Error)> SendAsync(string atmId, string key, string comment, CancellationToken ct)
        {
            Sent.Add(key);
            Assert.Contains("Ticket T-1", comment); Assert.Contains("requested by alice", comment);
            if (!SendOk(key)) return Task.FromResult<(bool, string?, string?)>((false, null, "refused"));
            var id = $"{key}-{++_n}"; Actions[id] = (false, false);
            return Task.FromResult<(bool, string?, string?)>((true, id, null));
        }
        public Task<ActionRecord?> GetActionAsync(string id, CancellationToken ct) =>
            Task.FromResult<ActionRecord?>(Actions.TryGetValue(id, out var a)
                ? new ActionRecord(id, 1, "X", "X", "", a.Final ? (a.Failed ? 3 : 2) : 1, a.Failed ? "Failed" : a.Final ? "Completed" : "In progress", a.Final, a.Failed,
                    null, null, null, null, null, "", "", null, null, null, null, false)
                : null);
        public Task<bool?> IsIdleAsync(string atmId, int s, CancellationToken ct) => Task.FromResult(Idle);
        public Task<DateTime?> LastContactAsync(string atmId, CancellationToken ct) => Task.FromResult(Contact);
        public Task<(bool Ok, string Detail)?> DevicesOkAsync(string atmId, CancellationToken ct) => Task.FromResult(Devices);
        public void Finish(string prefix, bool failed = false) { foreach (var k in Actions.Keys.Where(k => k.StartsWith(prefix)).ToList()) Actions[k] = (true, failed); }
    }

    private static readonly RebootWorkflowOptions O = new() { OfflineAfterSeconds = 60 };
    private static RebootRun Run(DateTime now) => new() { AtmId = "ATM1", User = "alice", Ticket = "T-1", Reason = "Frozen screen", CreatedUtc = now, StepStartedUtc = now };

    [Fact]
    public async Task Full_run_goes_out_of_service_reboots_waits_for_return_and_goes_back_in_service()
    {
        var now = new DateTime(2026, 11, 18, 8, 0, 0, DateTimeKind.Utc);
        var p = new FakeProbe { Contact = now }; var r = Run(now);
        async Task Step(int sec = 5) { now = now.AddSeconds(sec); await RebootWorkflow.StepAsync(r, p, O, now); }

        await Step(); Assert.Equal(RebootStep.WaitingForIdle, r.Step);
        await Step(); Assert.Equal(RebootStep.TakingOutOfService, r.Step); Assert.Equal(new[] { "OutOfService" }, p.Sent);
        await Step(); Assert.Equal(RebootStep.TakingOutOfService, r.Step);          // still running
        p.Finish("OutOfService"); await Step(); Assert.Equal(RebootStep.ReCheckingIdle, r.Step); Assert.NotNull(r.OutOfServiceUtc);
        p.Contact = now; await Step(); Assert.Equal(RebootStep.Rebooting, r.Step); Assert.Equal("Reboot", p.Sent[^1]);
        p.Finish("Reboot"); await Step(); Assert.Equal(RebootStep.WaitingForOffline, r.Step);       // KTC says done, ATM still reporting
        await Step(70); Assert.Equal(RebootStep.WaitingForOnline, r.Step); Assert.NotNull(r.OfflineUtc); // then silent
        await Step(30); Assert.Equal(RebootStep.WaitingForOnline, r.Step);
        p.Contact = now.AddSeconds(29); await Step(30); Assert.Equal(RebootStep.CheckingDevices, r.Step); Assert.NotNull(r.OnlineUtc);
        await Step(); Assert.Equal(RebootStep.PuttingInService, r.Step); Assert.Equal("InService", p.Sent[^1]);
        p.Finish("InService"); await Step();
        Assert.Equal(RebootState.Completed, r.State); Assert.NotNull(r.InServiceUtc);
        Assert.Equal(new[] { "OutOfService", "Reboot", "InService" }, p.Sent);
    }

    [Fact]
    public async Task Waits_while_a_customer_is_using_the_atm_and_gives_up_without_changing_it()
    {
        var now = DateTime.UtcNow; var p = new FakeProbe { Idle = false }; var r = Run(now);
        await RebootWorkflow.StepAsync(r, p, O, now);
        await RebootWorkflow.StepAsync(r, p, O, now.AddMinutes(1));
        Assert.Equal(RebootState.Running, r.State); Assert.Empty(p.Sent);
        await RebootWorkflow.StepAsync(r, p, O, now.AddMinutes(O.IdleTimeoutMinutes + 1));
        Assert.Equal(RebootState.Failed, r.State); Assert.Empty(p.Sent);
    }

    [Fact]
    public async Task Out_of_service_failing_twice_stops_without_rebooting()
    {
        var now = DateTime.UtcNow; var p = new FakeProbe(); var r = Run(now);
        await RebootWorkflow.StepAsync(r, p, O, now); await RebootWorkflow.StepAsync(r, p, O, now);
        p.Finish("OutOfService", failed: true); await RebootWorkflow.StepAsync(r, p, O, now);
        Assert.Equal(RebootState.Running, r.State); Assert.Equal(2, p.Sent.Count);  // retried once
        p.Finish("OutOfService", failed: true); await RebootWorkflow.StepAsync(r, p, O, now);
        Assert.Equal(RebootState.Failed, r.State); Assert.DoesNotContain("Reboot", p.Sent);
    }

    [Fact]
    public async Task Failed_reboot_puts_the_atm_back_in_service()
    {
        var now = DateTime.UtcNow; var p = new FakeProbe { Contact = now }; var r = Run(now);
        await RebootWorkflow.StepAsync(r, p, O, now); await RebootWorkflow.StepAsync(r, p, O, now);
        p.Finish("OutOfService"); await RebootWorkflow.StepAsync(r, p, O, now); await RebootWorkflow.StepAsync(r, p, O, now);
        p.Finish("Reboot", failed: true); await RebootWorkflow.StepAsync(r, p, O, now);
        Assert.Equal(RebootState.Failed, r.State); Assert.Equal("InService", p.Sent[^1]);
    }

    [Fact]
    public async Task Atm_that_does_not_come_back_needs_attention_and_stays_out_of_service()
    {
        var now = DateTime.UtcNow; var p = new FakeProbe { Contact = now }; var r = Run(now);
        await RebootWorkflow.StepAsync(r, p, O, now); await RebootWorkflow.StepAsync(r, p, O, now);
        p.Finish("OutOfService"); await RebootWorkflow.StepAsync(r, p, O, now); await RebootWorkflow.StepAsync(r, p, O, now);
        p.Contact = null; await RebootWorkflow.StepAsync(r, p, O, now.AddSeconds(5));
        Assert.Equal(RebootStep.WaitingForOnline, r.Step);
        await RebootWorkflow.StepAsync(r, p, O, now.AddMinutes(O.ComeBackTimeoutMinutes + 1));
        Assert.Equal(RebootState.NeedsAttention, r.State); Assert.DoesNotContain("InService", p.Sent);
    }

    [Fact]
    public async Task Device_faults_after_reboot_leave_the_atm_out_of_service()
    {
        var now = DateTime.UtcNow; var p = new FakeProbe { Devices = (false, "Dispenser: Fatal") }; var r = Run(now);
        r.Step = RebootStep.CheckingDevices;
        await RebootWorkflow.StepAsync(r, p, O, now);
        Assert.Equal(RebootState.NeedsAttention, r.State); Assert.Contains("Dispenser", r.Events[^1].Message); Assert.Empty(p.Sent);
    }

    [Fact]
    public async Task Cancel_after_out_of_service_puts_it_back_in_service()
    {
        var now = DateTime.UtcNow; var p = new FakeProbe(); var r = Run(now);
        await RebootWorkflow.StepAsync(r, p, O, now); await RebootWorkflow.StepAsync(r, p, O, now);
        r.CancelRequested = true; await RebootWorkflow.StepAsync(r, p, O, now);
        Assert.Equal(RebootState.Cancelled, r.State); Assert.Equal(new[] { "OutOfService", "InService" }, p.Sent);
    }
}
