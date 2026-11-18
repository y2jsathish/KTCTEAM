using Ktc.Application.Models;
using Ktc.Application.Options;

namespace Ktc.Application.Logic;

/// <summary>What the guided reboot needs from KTC. The live implementation sends KTC commands and reads ATM state.</summary>
public interface IRebootProbe
{
    /// <summary>Sends the command mapped to <paramref name="commandKey"/> (OutOfService, Reboot, InService). Returns the KTC action id.</summary>
    Task<(bool Ok, string? ActionId, string? Error)> SendAsync(string atmId, string commandKey, string comment, CancellationToken ct);
    Task<ActionRecord?> GetActionAsync(string actionId, CancellationToken ct);
    /// <summary>True when no customer transaction ran in the last <paramref name="idleSeconds"/>; null when it cannot be told.</summary>
    Task<bool?> IsIdleAsync(string atmId, int idleSeconds, CancellationToken ct);
    /// <summary>When the ATM last reported to KTC (heartbeat); null when unknown.</summary>
    Task<DateTime?> LastContactAsync(string atmId, CancellationToken ct);
    /// <summary>Device health after the reboot: (true, "") when fine, (false, faults) when not, null when unknown.</summary>
    Task<(bool Ok, string Detail)?> DevicesOkAsync(string atmId, CancellationToken ct);
}

/// <summary>
/// Guided reboot state machine. Each call to <see cref="StepAsync"/> moves the run on by at most one step, so a background job
/// can call it every few seconds and persist the run in between. Order: wait for idle → out of service → re-check idle → reboot →
/// ATM stops reporting → ATM reports again → device check → back in service. Safety rules: if out of service fails, stop (nothing
/// changed); if the reboot fails, put the ATM back in service; if it does not come back, or devices are faulty, stop and flag it.
/// </summary>
public static class RebootWorkflow
{
    private static void Log(RebootRun r, DateTime now, string msg, bool ok = true, string? action = null) { r.Events.Add(new RebootEvent(now, r.Step, msg, ok, action)); r.UpdatedUtc = now; }
    private static void Go(RebootRun r, RebootStep s, DateTime now) { r.Step = s; r.StepStartedUtc = now; r.Retries = 0; r.UpdatedUtc = now; }
    private static void End(RebootRun r, RebootState st, DateTime now, string msg, bool ok) { r.State = st; Log(r, now, msg, ok); }
    private static bool Over(RebootRun r, DateTime now, int minutes) => now - r.StepStartedUtc > TimeSpan.FromMinutes(Math.Max(1, minutes));
    private static string Comment(RebootRun r, string what) => $"{(r.Ticket.Length > 0 ? $"Ticket {r.Ticket}: " : "")}{r.Reason} [guided reboot: {what}, requested by {r.User}] (via KTC Monitoring Portal, ref {r.Id})";

    public static async Task StepAsync(RebootRun r, IRebootProbe p, RebootWorkflowOptions o, DateTime now, CancellationToken ct = default)
    {
        if (r.Finished) return;
        if (r.CancelRequested && r.Step is RebootStep.Starting or RebootStep.WaitingForIdle)
        { End(r, RebootState.Cancelled, now, "Cancelled before anything was sent. The ATM was not changed.", true); return; }
        if (r.CancelRequested && r.Step is RebootStep.TakingOutOfService or RebootStep.ReCheckingIdle)
        { await BackInService(r, p, now, "Cancelled", ct); if (!r.Finished) End(r, RebootState.Cancelled, now, "Cancelled before the reboot was sent.", true); return; }

        switch (r.Step)
        {
            case RebootStep.Starting:
                Log(r, now, $"Reboot requested by {r.User}{(r.Ticket.Length > 0 ? $" (ticket {r.Ticket})" : "")}.");
                Go(r, RebootStep.WaitingForIdle, now);
                Log(r, now, "Waiting until no customer transaction is in progress.");
                return;

            case RebootStep.WaitingForIdle:
            {
                var idle = await p.IsIdleAsync(r.AtmId, o.IdleSeconds, ct);
                if (idle == false)
                {
                    if (Over(r, now, o.IdleTimeoutMinutes)) End(r, RebootState.Failed, now, $"The ATM was busy for {o.IdleTimeoutMinutes} minutes. Nothing was sent; the ATM was not changed.", false);
                    return;
                }
                Log(r, now, idle == true ? $"ATM idle (no transaction in the last {o.IdleSeconds} s)." : "Could not read the ATM's transactions; continuing (KTC's own checks still apply).", idle == true);
                var s = await p.SendAsync(r.AtmId, "OutOfService", Comment(r, "out of service"), ct);
                Go(r, RebootStep.TakingOutOfService, now);
                if (!s.Ok) { End(r, RebootState.Failed, now, $"Out of service could not be sent ({s.Error}). Stopped without rebooting; the ATM was not changed.", false); return; }
                r.OosActionId = s.ActionId; Log(r, now, "Out of service sent.", true, s.ActionId);
                return;
            }

            case RebootStep.TakingOutOfService:
            {
                var a = r.OosActionId == null ? null : await p.GetActionAsync(r.OosActionId, ct);
                if (a is { Final: true, Failed: false } || (r.OosActionId == null && a == null))
                {
                    r.OutOfServiceUtc = now; Log(r, now, "ATM is out of service.", true, r.OosActionId);
                    Go(r, RebootStep.ReCheckingIdle, now); return;
                }
                if (a is { Failed: true })
                {
                    if (r.Retries == 0)
                    {
                        r.Retries = 1; Log(r, now, $"Out of service failed ({a.Status}{(a.Result.Length > 0 ? ": " + a.Result : "")}); retrying once.", false, a.Id);
                        var s = await p.SendAsync(r.AtmId, "OutOfService", Comment(r, "out of service, retry"), ct);
                        if (s.Ok) { r.OosActionId = s.ActionId; Log(r, now, "Out of service sent again.", true, s.ActionId); return; }
                    }
                    End(r, RebootState.Failed, now, "Out of service failed twice. Stopped without rebooting; the ATM was left as it was.", false); return;
                }
                if (Over(r, now, o.OutOfServiceTimeoutMinutes))
                { await BackInService(r, p, now, "Out of service did not complete in time", ct); if (!r.Finished) End(r, RebootState.NeedsAttention, now, "Out of service did not complete in time. Stopped without rebooting; check the ATM.", false); }
                return;
            }

            case RebootStep.ReCheckingIdle:
            {
                var idle = await p.IsIdleAsync(r.AtmId, o.IdleSeconds, ct);
                if (idle == false)
                {
                    if (Over(r, now, o.IdleTimeoutMinutes)) { await BackInService(r, p, now, "A transaction did not finish", ct); if (!r.Finished) End(r, RebootState.Failed, now, "A transaction was still running. Put back in service without rebooting.", false); }
                    return;
                }
                r.LastContactBeforeUtc = await p.LastContactAsync(r.AtmId, ct);
                var s = await p.SendAsync(r.AtmId, "Reboot", Comment(r, "reboot"), ct);
                if (!s.Ok) { await BackInService(r, p, now, $"Reboot could not be sent ({s.Error})", ct); if (!r.Finished) End(r, RebootState.Failed, now, "Reboot could not be sent. The ATM was put back in service.", false); return; }
                r.RebootActionId = s.ActionId; r.RebootSentUtc = now;
                Go(r, RebootStep.Rebooting, now); Log(r, now, "Reboot sent.", true, s.ActionId);
                return;
            }

            case RebootStep.Rebooting:
            {
                var a = r.RebootActionId == null ? null : await p.GetActionAsync(r.RebootActionId, ct);
                if (a is { Failed: true })
                { await BackInService(r, p, now, $"Reboot failed ({a.Status})", ct); if (!r.Finished) End(r, RebootState.Failed, now, $"Reboot failed ({a.Status}{(a.Result.Length > 0 ? ": " + a.Result : "")}). The ATM was put back in service.", false); return; }
                var lc = await p.LastContactAsync(r.AtmId, ct);
                var silent = lc == null || now - lc.Value > TimeSpan.FromSeconds(o.OfflineAfterSeconds);
                if (a is { Final: true } || silent)
                {
                    Log(r, now, a is { Final: true } ? "KTC reports the reboot command done; the ATM is restarting." : "The ATM stopped reporting; it is restarting.", true, r.RebootActionId);
                    Go(r, RebootStep.WaitingForOffline, now);
                    await StepAsync(r, p, o, now, ct);
                    return;
                }
                if (Over(r, now, o.RebootTimeoutMinutes)) End(r, RebootState.NeedsAttention, now, $"The reboot did not start within {o.RebootTimeoutMinutes} minutes. The ATM is still out of service; check it.", false);
                return;
            }

            case RebootStep.WaitingForOffline:
            {
                var lc = await p.LastContactAsync(r.AtmId, ct);
                if (lc == null || now - lc.Value > TimeSpan.FromSeconds(o.OfflineAfterSeconds))
                { r.OfflineUtc = now; Log(r, now, $"ATM offline (last report {(lc == null ? "unknown" : lc.Value.ToString("HH:mm:ss") + " UTC")})."); Go(r, RebootStep.WaitingForOnline, now); return; }
                if (r.RebootSentUtc is { } sent && lc.Value > sent.AddSeconds(o.OfflineAfterSeconds) && (r.LastContactBeforeUtc == null || lc.Value > r.LastContactBeforeUtc))
                { r.OfflineUtc = sent; r.OnlineUtc = lc; Log(r, now, "ATM is reporting again (it restarted faster than the offline check)."); Go(r, RebootStep.CheckingDevices, now); return; }
                if (Over(r, now, o.ComeBackTimeoutMinutes)) End(r, RebootState.NeedsAttention, now, "The ATM kept reporting and never went offline; the reboot may not have happened. It is still out of service.", false);
                return;
            }

            case RebootStep.WaitingForOnline:
            {
                var lc = await p.LastContactAsync(r.AtmId, ct);
                if (lc is { } t && r.OfflineUtc is { } off && t > off && now - t <= TimeSpan.FromSeconds(o.OfflineAfterSeconds))
                { r.OnlineUtc = t; Log(r, now, $"ATM back online (reported at {t:HH:mm:ss} UTC, {(t - (r.RebootSentUtc ?? off)).TotalMinutes:0.#} min after the reboot)."); Go(r, RebootStep.CheckingDevices, now); return; }
                if (Over(r, now, o.ComeBackTimeoutMinutes)) End(r, RebootState.NeedsAttention, now, $"The ATM has not come back within {o.ComeBackTimeoutMinutes} minutes. Alert raised; it needs an engineer.", false);
                return;
            }

            case RebootStep.CheckingDevices:
            {
                var d = await p.DevicesOkAsync(r.AtmId, ct);
                if (d is { Ok: false })
                { End(r, RebootState.NeedsAttention, now, $"Devices report faults after the reboot ({d.Value.Detail}). The ATM was left out of service for an engineer.", false); return; }
                Log(r, now, d == null ? "Device status could not be read; continuing." : "Devices report OK.", d != null);
                if (!o.AutoInService) { Go(r, RebootStep.Done, now); End(r, RebootState.Completed, now, "Reboot done. The ATM was left out of service (AutoInService is off).", true); return; }
                var s = await p.SendAsync(r.AtmId, "InService", Comment(r, "back in service"), ct);
                Go(r, RebootStep.PuttingInService, now);
                if (!s.Ok) { End(r, RebootState.NeedsAttention, now, $"In service could not be sent ({s.Error}). The ATM is out of service.", false); return; }
                r.InServiceActionId = s.ActionId; Log(r, now, "In service sent.", true, s.ActionId);
                return;
            }

            case RebootStep.PuttingInService:
            {
                var a = r.InServiceActionId == null ? null : await p.GetActionAsync(r.InServiceActionId, ct);
                if (a is { Final: true, Failed: false } || (r.InServiceActionId == null && a == null))
                { r.InServiceUtc = now; Go(r, RebootStep.Done, now); End(r, RebootState.Completed, now, "ATM back in service. Reboot complete.", true); return; }
                if (a is { Failed: true }) { End(r, RebootState.NeedsAttention, now, $"In service failed ({a.Status}). The ATM is out of service.", false); return; }
                if (Over(r, now, o.InServiceTimeoutMinutes)) End(r, RebootState.NeedsAttention, now, "In service did not complete in time. Check the ATM.", false);
                return;
            }
        }
    }

    private static async Task BackInService(RebootRun r, IRebootProbe p, DateTime now, string why, CancellationToken ct)
    {
        var s = await p.SendAsync(r.AtmId, "InService", Comment(r, "back in service after: " + why), ct);
        if (s.Ok) { r.InServiceActionId = s.ActionId; Log(r, now, $"{why}: in service sent.", true, s.ActionId); }
        else End(r, RebootState.NeedsAttention, now, $"{why}, and in service could not be sent ({s.Error}). The ATM may be out of service.", false);
    }
}
