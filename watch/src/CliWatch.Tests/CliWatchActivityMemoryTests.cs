// SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using System.Text.Json.Nodes;
using CliWatch.Contracts;
using CliWatch.Projection;
using Xunit;

namespace CliWatch.Tests;

public sealed class CliWatchActivityMemoryTests
{
    [Fact]
    public void Busy_then_quiet_keeps_observed_time_with_and_without_old_file()
    {
        var root = "C:/isolated-lead-probe";
        var initial = DateTimeOffset.Parse("2026-09-20T13:45:00Z");
        var projector = new CliWatchProjector();
        var busy = projector.Project(Batch(root, initial, "cli_compute", available: true, oldArtifact: true));
        var quiet = projector.Project(Batch(root, initial.AddSeconds(5), "quiet", available: true, oldArtifact: true), busy);
        var busyNoFile = projector.Project(Batch(root, initial, "cli_compute", available: true, oldArtifact: false));
        var quietNoFile = projector.Project(Batch(root, initial.AddSeconds(5), "quiet", available: true, oldArtifact: false), busyNoFile);
        var laterQuiet = projector.Project(Batch(root, initial.AddSeconds(20), "quiet", available: true, oldArtifact: true), quiet);

        Assert.Equal(initial, busy.Projects.Single().LastFactAt);
        Assert.Equal(initial, busy.Projects.Single().ObservedActivityAt);
        Assert.Equal(initial, quiet.Projects.Single().LastFactAt);
        Assert.Equal(initial, quietNoFile.Projects.Single().LastFactAt);
        Assert.Equal(initial, laterQuiet.Projects.Single().LastFactAt);
        Assert.Equal(CliWatchStatus.RunningQuiet, quiet.Projects.Single().Phase);
        Assert.DoesNotContain("运行中有活动", quiet.Projects.Single().Summary, StringComparison.Ordinal);
        Assert.Contains("最后活动 5 秒前", quiet.Projects.Single().Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Unavailable_counters_do_not_claim_active()
    {
        var root = "C:/isolated-lead-probe";
        var initial = DateTimeOffset.Parse("2026-09-20T13:45:00Z");
        var unavailable = new CliWatchProjector().Project(Batch(root, initial, "unavailable", available: false, oldArtifact: false));
        var row = unavailable.Projects.Single();
        Assert.NotEqual(CliWatchStatus.RunningActive, row.Phase);
        Assert.Equal(CliWatchStatus.RunningUnknown, row.Phase);
        Assert.DoesNotContain("运行中有活动", row.Summary, StringComparison.Ordinal);
        Assert.Contains("活动未知", row.Summary, StringComparison.Ordinal);
        Assert.True(row.IsActive);
    }

    [Fact]
    public void Other_root_and_new_start_ticks_do_not_inherit_activity()
    {
        var initial = DateTimeOffset.Parse("2026-09-20T13:45:00Z");
        var projector = new CliWatchProjector();
        var busy = projector.Project(Batch("C:/root-a", initial, "cli_compute", true, false, startTicks: 111));
        var otherRoot = projector.Project(Batch("C:/root-b", initial.AddSeconds(5), "quiet", true, false, startTicks: 111), busy);
        var newStart = projector.Project(Batch("C:/root-a", initial.AddSeconds(5), "quiet", true, false, startTicks: 999), busy);
        Assert.Equal(initial, busy.Projects.Single().ObservedActivityAt);
        Assert.Null(otherRoot.Projects.Single().ObservedActivityAt);
        Assert.Null(newStart.Projects.Single().ObservedActivityAt);
        Assert.NotEqual(initial, otherRoot.Projects.Single().LastFactAt);
        Assert.NotEqual(initial, newStart.Projects.Single().LastFactAt);
    }

    static FactBatch Batch(
        string root,
        DateTimeOffset now,
        string activityKind,
        bool available,
        bool oldArtifact,
        long? startTicks = null)
    {
        var initial = DateTimeOffset.Parse("2026-09-20T13:45:00Z");
        var ticks = startTicks ?? initial.AddMinutes(-4).Ticks;
        var started = new DateTimeOffset(ticks, TimeSpan.Zero);
        SourceFact Fact(string kind, object data) => new(
            kind,
            kind + ":probe",
            "probe",
            new Dictionary<string, string> { ["job_key"] = "probe--1", ["mailbox_root"] = root },
            (JsonObject)JsonSerializer.SerializeToNode(data)!,
            Array.Empty<EvidenceRef>());

        var facts = new List<SourceFact>
        {
            Fact("mailbox_job", new
            {
                key = "probe--1",
                project = "probe",
                job_id = "probe",
                attempt_id = "1",
                state = "registered",
                harness = "cursor-cli",
                registered_at = initial.AddMinutes(-5).ToString("o")
            }),
            Fact("adapter_owner", new
            {
                pid = 1001,
                start_time_utc_ticks = ticks,
                started_at_utc = started.ToString("o")
            }),
            Fact("process_bind", new
            {
                cli_alive = true,
                wrapper_alive = true,
                wrapper_state = "alive",
                cli_pid = 2002,
                cli_start_ticks = ticks,
                cli_family = "Cursor",
                activity_kind = activityKind,
                counters_available = available
            })
        };
        if (oldArtifact)
        {
            facts.Add(Fact("outbox_file", new
            {
                name = "STARTED.md",
                activity = true,
                mtime = initial.AddMinutes(-3).ToString("o")
            }));
        }

        return new FactBatch(now, facts, Array.Empty<SourceIssue>());
    }
}
