// SPDX-License-Identifier: MPL-2.0
using CliWatch.Projection;
using Xunit;

namespace CliWatch.Tests;

public sealed class CliWatchStatusTests
{
    [Fact]
    public void Silence_is_quiet_not_failed()
    {
        var now = DateTimeOffset.Parse("2026-09-20T16:00:00Z");
        var last = now.AddMinutes(-10);
        var status = CliWatchProjector.Classify(
            corrupt: false,
            ready: null,
            receipt: null,
            result: null,
            cliAlive: true,
            wrapperAlive: true,
            wrapperState: "alive",
            ownerPresent: true,
            waiting: false,
            lastActivity: last,
            activityKnown: true,
            now: now,
            quietSeconds: 120);
        Assert.Equal(CliWatchStatus.RunningQuiet, status);
        Assert.Contains("不能据此认定卡死", CliWatchStatus.BasisZh(status), StringComparison.Ordinal);
        Assert.Contains("not a hang", CliWatchStatus.BasisEn(status), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Recent_activity_is_running_active()
    {
        var now = DateTimeOffset.Parse("2026-09-20T16:00:00Z");
        var status = CliWatchProjector.Classify(
            false, null, null, null,
            cliAlive: true, wrapperAlive: true, wrapperState: "alive", ownerPresent: true,
            waiting: false, lastActivity: now.AddSeconds(-20), activityKnown: true, now: now, quietSeconds: 120,
            processBusy: true);
        Assert.Equal(CliWatchStatus.RunningActive, status);
    }

    [Fact]
    public void Missing_counters_are_unknown_not_active()
    {
        var now = DateTimeOffset.Parse("2026-09-20T16:00:00Z");
        var status = CliWatchProjector.Classify(
            false, null, null, null,
            cliAlive: true, wrapperAlive: true, wrapperState: "alive", ownerPresent: true,
            waiting: false, lastActivity: null, activityKnown: false, now: now, quietSeconds: 120,
            processBusy: false, deliveryState: null, activityKind: "unavailable");
        Assert.Equal(CliWatchStatus.RunningUnknown, status);
        Assert.NotEqual(CliWatchStatus.RunningActive, status);
        Assert.Contains("活动观察不足", CliWatchStatus.BasisZh(status), StringComparison.Ordinal);
    }

    [Fact]
    public void Wrapper_alive_without_cli_is_unconfirmed()
    {
        var now = DateTimeOffset.UtcNow;
        var status = CliWatchProjector.Classify(
            false, null, null, null,
            cliAlive: false, wrapperAlive: true, wrapperState: "alive", ownerPresent: true,
            waiting: false, lastActivity: null, activityKnown: false, now: now, quietSeconds: 120);
        Assert.Equal(CliWatchStatus.StartUnconfirmed, status);
    }

    [Fact]
    public void Ready_is_delivered_not_accepted()
    {
        var ready = Fact("outbox_ready", "key-1", ("status", "completed"));
        var status = CliWatchProjector.Classify(
            false, ready, null, null,
            cliAlive: false, wrapperAlive: false, wrapperState: "dead", ownerPresent: true,
            waiting: false, lastActivity: null, activityKnown: false, now: DateTimeOffset.UtcNow, quietSeconds: 120);
        Assert.Equal(CliWatchStatus.Delivered, status);
        Assert.Contains("不是 Lead 验收", CliWatchStatus.BasisZh(status), StringComparison.Ordinal);
    }

    [Fact]
    public void Receipt_success_without_ready_is_executor_finished()
    {
        var receipt = Fact("adapter_receipt", "key-1", ("transport_complete", true), ("grok_success", true));
        var status = CliWatchProjector.Classify(
            false, null, receipt, null,
            cliAlive: false, wrapperAlive: false, wrapperState: "dead", ownerPresent: true,
            waiting: false, lastActivity: null, activityKnown: false, now: DateTimeOffset.UtcNow, quietSeconds: 120);
        Assert.Equal(CliWatchStatus.ExecutorFinished, status);
    }

    [Fact]
    public void Receipt_failure_is_failed()
    {
        var receipt = Fact("adapter_receipt", "key-1", ("transport_complete", true), ("grok_success", false));
        var status = CliWatchProjector.Classify(
            false, null, receipt, null,
            cliAlive: false, wrapperAlive: false, wrapperState: "dead", ownerPresent: true,
            waiting: false, lastActivity: null, activityKnown: false, now: DateTimeOffset.UtcNow, quietSeconds: 120);
        Assert.Equal(CliWatchStatus.Failed, status);
    }

    [Fact]
    public void Owner_dead_without_delivery_does_not_prove_cli_failure()
    {
        var status = CliWatchProjector.Classify(
            false, null, null, null,
            cliAlive: false, wrapperAlive: false, wrapperState: "dead", ownerPresent: true,
            waiting: false, lastActivity: null, activityKnown: false, now: DateTimeOffset.UtcNow, quietSeconds: 120);
        Assert.Equal(CliWatchStatus.Unknown, status);
    }

    [Fact]
    public void No_owner_is_registered()
    {
        var status = CliWatchProjector.Classify(
            false, null, null, null,
            cliAlive: false, wrapperAlive: false, wrapperState: "absent", ownerPresent: false,
            waiting: false, lastActivity: null, activityKnown: false, now: DateTimeOffset.UtcNow, quietSeconds: 120);
        Assert.Equal(CliWatchStatus.Registered, status);
    }

    [Fact]
    public void Corrupt_is_unknown()
    {
        var status = CliWatchProjector.Classify(
            corrupt: true, ready: null, receipt: null, result: null,
            cliAlive: true, wrapperAlive: true, wrapperState: "alive", ownerPresent: true,
            waiting: false, lastActivity: DateTimeOffset.UtcNow, activityKnown: true,
            now: DateTimeOffset.UtcNow, quietSeconds: 120);
        Assert.Equal(CliWatchStatus.Unknown, status);
    }

    [Fact]
    public void Live_cli_beats_ready_file()
    {
        var ready = Fact("outbox_ready", "key-1", ("status", "completed"));
        var now = DateTimeOffset.UtcNow;
        var status = CliWatchProjector.Classify(
            false, ready, null, null,
            cliAlive: true, wrapperAlive: true, wrapperState: "alive", ownerPresent: true,
            waiting: false, lastActivity: now, activityKnown: true, now: now, quietSeconds: 120,
            processBusy: true);
        Assert.Equal(CliWatchStatus.RunningActive, status);
    }

    [Fact]
    public void Waiting_requires_flag()
    {
        var now = DateTimeOffset.UtcNow;
        var status = CliWatchProjector.Classify(
            false, null, null, null,
            cliAlive: true, wrapperAlive: true, wrapperState: "alive", ownerPresent: true,
            waiting: true, lastActivity: now, activityKnown: true, now: now, quietSeconds: 120);
        Assert.Equal(CliWatchStatus.Waiting, status);
    }

    static CliWatch.Contracts.SourceFact Fact(string kind, string key, params (string, object)[] values)
    {
        var obj = new System.Text.Json.Nodes.JsonObject();
        foreach (var (k, v) in values)
        {
            obj[k] = v switch
            {
                bool b => b,
                string s => s,
                int i => i,
                _ => v.ToString()
            };
        }

        obj["job_key"] = key;
        return new CliWatch.Contracts.SourceFact(
            kind, kind + ":" + key, "p",
            new Dictionary<string, string> { ["job_key"] = key },
            obj,
            Array.Empty<CliWatch.Contracts.EvidenceRef>());
    }
}
