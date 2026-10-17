// SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Nodes;
using CliWatch.Contracts;
using CliWatch.Projection;
using CliWatch.Views;
using Xunit;

namespace CliWatch.Tests;

public sealed class CliWatchEntryRuleTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-04T12:00:00Z");
    const string Root = "C:/isolated-entry-rule";

    [Fact]
    public void Live_cli_waiting_and_exact_launcher_enter_current_unbound_is_not_an_error()
    {
        var live = Project(Job(), Bind(cli: true, waiting: false), Activity());
        var running = Assert.Single(live.Projects);
        Assert.True(running.IsActive);
        Assert.True(CliWatchStatus.IsRunning(running.Phase));
        var waiting = Assert.Single(Project(Job(), Bind(cli: true, waiting: false), Activity()).Projects);
        Assert.True(waiting.IsActive);
        Assert.Equal(CliWatchStatus.Waiting, waiting.Phase);
        Assert.DoesNotContain(waiting.Attention, a => a.Owner == AttentionOwner.User);

        var launching = Assert.Single(Project(Job(), Bind(cli: false, waiting: false)).Projects);
        Assert.True(launching.IsActive);
        Assert.Equal(CliWatchStatus.StartUnconfirmed, launching.Phase);

        var loose = Assert.Single(Project(Unbound(88, 8800)).Projects);
        Assert.True(loose.IsActive);
        Assert.Equal(CliWatchStatus.Unbound, loose.Phase);
        Assert.Equal(0, HudPresentation.Build(Project(Unbound(88, 8800)), null).CurrentIssueCount);
    }

    [Fact]
    public void Ended_delivery_failure_registration_scope_and_unknown_stay_historical()
    {
        var delivered = Assert.Single(Project(Job(), Ready("completed")).Projects);
        Assert.Equal(CliWatchStatus.Delivered, delivered.Phase);
        Assert.False(delivered.IsActive);

        var failed = Assert.Single(Project(Job(), Ready("failed")).Projects);
        Assert.Equal(CliWatchStatus.ReportedFailure, failed.Phase);
        Assert.False(failed.IsActive);

        var registered = Assert.Single(Project(Job()).Projects);
        Assert.Equal(CliWatchStatus.Registered, registered.Phase);
        Assert.False(registered.IsActive);

        var scopeOnly = Job();
        scopeOnly.Values["current_scope_member"] = true;
        scopeOnly.Values["current_scope_at"] = Now.ToString("o");
        Assert.False(Assert.Single(Project(scopeOnly).Projects).IsActive);

        var unknown = Job();
        unknown.Values["corrupt"] = true;
        var unknownRow = Assert.Single(Project(unknown).Projects);
        Assert.Equal(CliWatchStatus.Unknown, unknownRow.Phase);
        Assert.False(unknownRow.IsActive);

        var hud = HudPresentation.Build(Project(Job(), Ready("completed"), Ready("failed")), null);
        Assert.Equal(0, hud.CurrentCount);
        Assert.Null(hud.SelectedProjectId);
    }

    [Fact]
    public void Ready_file_does_not_hide_a_live_execution()
    {
        var row = Assert.Single(Project(Job(), Ready("completed"), Bind(cli: true, waiting: false)).Projects);
        Assert.True(row.IsActive);
        Assert.True(CliWatchStatus.IsRunning(row.Phase));
        Assert.Equal(1, HudPresentation.Build(Project(Job(), Ready("completed"), Bind(cli: true, waiting: false)), null).CurrentCount);
    }

    [Fact]
    public void Partial_refresh_keeps_the_same_process_and_drops_it_when_the_process_is_gone()
    {
        var projector = new CliWatchProjector();
        var first = projector.Project(new FactBatch(Now, new[]
        {
            RootFact(complete: true),
            Job(withEvidence: true),
            Bind(cli: true, waiting: false)
        }, Array.Empty<SourceIssue>()));
        Assert.True(Assert.Single(first.Projects).IsActive);

        var second = projector.Project(new FactBatch(Now.AddMinutes(1), new[]
        {
            RootFact(complete: false),
            Unbound(77, 7700)
        }, Array.Empty<SourceIssue>()), first);
        var kept = Assert.Single(second.Projects, p => p.Id == "work--1");
        Assert.True(kept.IsActive);
        Assert.DoesNotContain(second.Projects, p => p.Phase == CliWatchStatus.Unbound);

        var third = projector.Project(new FactBatch(Now.AddMinutes(2), new[]
        {
            RootFact(complete: false)
        }, Array.Empty<SourceIssue>()), second);
        var held = Assert.Single(third.Projects, p => p.Id == "work--1");
        Assert.False(held.IsActive);
        Assert.Equal(0, HudPresentation.Build(third, null).CurrentCount);
    }

    [Fact]
    public void Read_registration_does_not_swallow_the_same_live_process()
    {
        var projector = new CliWatchProjector();
        var first = projector.Project(new FactBatch(Now, new[]
        {
            RootFact(complete: true),
            Job(withEvidence: true),
            Bind(cli: true, waiting: false)
        }, Array.Empty<SourceIssue>()));
        Assert.True(Assert.Single(first.Projects).IsActive);

        var second = projector.Project(new FactBatch(Now.AddMinutes(1), new[]
        {
            RootFact(complete: true),
            Job(withEvidence: true),
            Unbound(77, 7700)
        }, Array.Empty<SourceIssue>()), first);
        var row = Assert.Single(second.Projects, p => p.Id == "work--1");
        Assert.True(row.IsActive);
        Assert.DoesNotContain(second.Projects, p => p.Phase == CliWatchStatus.Unbound);
        Assert.Equal(1, HudPresentation.Build(second, null).CurrentCount);
    }

    [Fact]
    public void Conflicting_live_process_stays_unbound_instead_of_attaching_to_another_identity()
    {
        var projector = new CliWatchProjector();
        var first = projector.Project(new FactBatch(Now, new[]
        {
            Job(withEvidence: true),
            Bind(cli: true, waiting: false, pid: 77, ticks: 7700)
        }, Array.Empty<SourceIssue>()));
        Assert.True(Assert.Single(first.Projects).IsActive);

        var second = projector.Project(new FactBatch(Now.AddMinutes(1), new[]
        {
            Job(withEvidence: true),
            Bind(cli: true, waiting: false, pid: 88, ticks: 8800),
            Unbound(77, 7700)
        }, Array.Empty<SourceIssue>()), first);
        var row = Assert.Single(second.Projects, p => p.Id == "work--1");
        Assert.True(row.IsActive);
        var loose = Assert.Single(second.Projects, p => p.Phase == CliWatchStatus.Unbound);
        Assert.Contains(loose.WorkItems, w => (w.Summary ?? string.Empty).Contains("77", StringComparison.Ordinal));
        Assert.DoesNotContain(loose.WorkItems, w => (w.Summary ?? string.Empty).Contains("88", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Revived_process_keeps_the_instance_without_replaying_activity_or_wait(bool waiting)
    {
        var start = Now.AddMinutes(-20);
        var ticks = start.UtcTicks;
        var projector = new CliWatchProjector();
        var facts = new List<SourceFact>
        {
            RootFact(complete: true),
            Job(withEvidence: true),
            Bind(cli: true, waiting: false, pid: 77, ticks: ticks, activityKind: waiting ? null : "child_io")
        };
        if (waiting) facts.Add(Activity());
        var first = projector.Project(new FactBatch(Now, facts, Array.Empty<SourceIssue>()));
        var opened = Assert.Single(first.Projects);
        Assert.Equal(waiting ? CliWatchStatus.Waiting : CliWatchStatus.RunningActive, opened.Phase);
        Assert.True(opened.IsActive);

        var second = projector.Project(new FactBatch(Now.AddMinutes(3), new[]
        {
            RootFact(complete: false),
            Unbound(77, ticks)
        }, Array.Empty<SourceIssue>()), first);
        var kept = Assert.Single(second.Projects, p => p.Id == "work--1");
        Assert.True(kept.IsActive);
        Assert.Equal(CliWatchStatus.RunningUnknown, kept.Phase);
        Assert.Equal(DataQuality.Partial, kept.Quality);
        Assert.Equal(opened.ObservedActivityAt, kept.ObservedActivityAt);
        Assert.Contains("23 分", kept.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("运行中有活动", kept.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("明确等待", kept.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("新的过程记录", kept.Summary, StringComparison.Ordinal);
        var work = Assert.Single(kept.WorkItems);
        Assert.Equal(DataQuality.Partial, work.Quality);
        Assert.Equal("unknown", work.Axes.Turn);
        Assert.Equal("running", work.Axes.Execution);
        Assert.DoesNotContain(second.Projects, p => p.Phase == CliWatchStatus.Unbound);
    }

    [Theory]
    [InlineData("ready-completed")]
    [InlineData("ready-failed")]
    [InlineData("terminal")]
    [InlineData("receipt")]
    public void Wrapper_after_exact_end_is_not_starting(string end)
    {
        var facts = new List<SourceFact> { Job(), Bind(cli: false, waiting: false) };
        if (end == "ready-completed") facts.Add(Ready("completed"));
        if (end == "ready-failed") facts.Add(Ready("failed"));
        if (end == "terminal") facts.Add(Terminal());
        if (end == "receipt") facts.Add(Receipt());
        var row = Assert.Single(Project(facts.ToArray()).Projects);
        Assert.False(row.IsActive);
        Assert.NotEqual(CliWatchStatus.StartUnconfirmed, row.Phase);

        var launching = Assert.Single(Project(Job(), Bind(cli: false, waiting: false)).Projects);
        Assert.True(launching.IsActive);
        Assert.Equal(CliWatchStatus.StartUnconfirmed, launching.Phase);

        var corrupt = Ready("completed");
        corrupt.Values["corrupt"] = true;
        var stillLaunching = Assert.Single(Project(Job(), Bind(cli: false, waiting: false), corrupt).Projects);
        Assert.True(stillLaunching.IsActive);
        Assert.Equal(CliWatchStatus.StartUnconfirmed, stillLaunching.Phase);
    }

    [Fact]
    public void Source_gaps_do_not_become_current_tasks()
    {
        var issues = new List<SourceIssue>();
        for (var i = 0; i < 21; i++)
            issues.Add(new SourceIssue("session-" + i, "grok_session_missing", "session file missing", Now));
        issues.Add(new SourceIssue(Root, "scan_budget_exhausted", "budget", Now));
        var snapshot = new CliWatchProjector().Project(new FactBatch(Now, new[] { Job(), Ready("completed") }, issues));
        Assert.Equal(0, HudPresentation.Build(snapshot, null).CurrentCount);
        var summary = StatusLanguage.IssuesSummary(snapshot.Issues, snapshot);
        Assert.Contains("不代表有新的当前任务", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("状态待核实", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("21", summary, StringComparison.Ordinal);
    }

    static CockpitSnapshot Project(params SourceFact[] facts) =>
        new CliWatchProjector().Project(new FactBatch(Now, facts, Array.Empty<SourceIssue>()));

    static SourceFact Job(bool withEvidence = false)
    {
        var evidence = withEvidence
            ? new[] { new EvidenceRef("job", Root + "/jobs/work--1.json", "mailbox_job", Now, Now, DataQuality.Fresh) }
            : Array.Empty<EvidenceRef>();
        var values = new JsonObject
        {
            ["project"] = "alpha",
            ["job_id"] = "work",
            ["attempt_id"] = "1",
            ["job_key"] = "work--1",
            ["mailbox_root"] = Root,
            ["state"] = "registered",
            ["registered_at"] = Now.AddMinutes(-10).ToString("o")
        };
        return new SourceFact("mailbox_job", "mailbox_job:work--1", "alpha", new Dictionary<string, string>
        {
            ["job_key"] = "work--1",
            ["mailbox_root"] = Root
        }, values, evidence);
    }

    static SourceFact Bind(bool cli, bool waiting, int pid = 77, long ticks = 7700, string? activityKind = null) => Fact("process_bind", new JsonObject
    {
        ["cli_alive"] = cli,
        ["wrapper_alive"] = !cli,
        ["wrapper_state"] = "alive",
        ["cli_pid"] = cli ? pid : 0,
        ["cli_start_ticks"] = cli ? ticks : 0,
        ["wrapper_pid"] = cli ? 0 : pid,
        ["wrapper_start_ticks"] = cli ? 0 : ticks,
        ["activity_kind"] = activityKind ?? (waiting ? "wait" : "quiet"),
        ["last_tool_name"] = waiting ? "ask_user" : null
    });

    static SourceFact Ready(string status) => Fact("outbox_ready", new JsonObject { ["status"] = status });

    static SourceFact Terminal() => Fact("adapter_terminal", new JsonObject
    {
        ["identity_ok"] = true,
        ["exit_code"] = 0
    });

    static SourceFact Receipt() => Fact("adapter_receipt", new JsonObject
    {
        ["identity_ok"] = true,
        ["transport_complete"] = true,
        ["grok_success"] = true
    });

    static SourceFact Activity() => Fact("grok_session_activity", new JsonObject
    {
        ["identity_ok"] = true,
        ["last_tool_name"] = "ask_user",
        ["activity_known"] = true
    });

    static SourceFact Unbound(int pid, long ticks) => new(
        "unbound_process",
        "unbound_process:" + pid + ":" + ticks,
        null,
        new Dictionary<string, string>(),
        new JsonObject
        {
            ["pid"] = pid,
            ["start_time_utc_ticks"] = ticks,
            ["cli"] = "Codex",
            ["started_at_utc"] = Now.AddMinutes(-5).ToString("o")
        },
        Array.Empty<EvidenceRef>());

    static SourceFact RootFact(bool complete) => new(
        "mailbox_root",
        "mailbox_root:" + Root,
        null,
        new Dictionary<string, string> { ["mailbox_root"] = Root },
        new JsonObject { ["mailbox_root"] = Root, ["readable"] = true, ["scan_complete"] = complete },
        Array.Empty<EvidenceRef>());

    static SourceFact Fact(string kind, JsonObject values)
    {
        values["job_key"] = "work--1";
        values["mailbox_root"] = Root;
        return new SourceFact(kind, kind + ":work--1", "alpha", new Dictionary<string, string>
        {
            ["job_key"] = "work--1",
            ["mailbox_root"] = Root
        }, values, Array.Empty<EvidenceRef>());
    }
}
