// SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Nodes;
using CliWatch.Contracts;
using CliWatch.Projection;
using CliWatch.Views;
using Xunit;

namespace CliWatch.Tests;

public sealed class CliWatchCurrentHistoryTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-01T06:00:00Z");

    [Fact]
    public void Ended_delivery_with_nonempty_Lead_record_moves_to_history_without_accepting_it()
    {
        var snapshot = Project(Job(Now.AddHours(-1)), Ready(), Acceptance(20));
        var row = Assert.Single(snapshot.Projects);
        Assert.Equal(CliWatchStatus.Delivered, row.Phase);
        Assert.False(row.IsActive);
        Assert.Equal("unknown", Assert.Single(row.WorkItems).Axes.Acceptance);
        Assert.Contains("验收结论未核实", row.NextStep);
        var hud = HudPresentation.Build(snapshot, row.Id);
        Assert.Equal(0, hud.CurrentCount);
        Assert.Equal(1, hud.HistoricalCount);
        Assert.Null(hud.SelectedProjectId);
        Assert.Single(HudPresentation.Build(snapshot, row.Id, showHistory: true).Projects);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ended_delivery_stays_in_history_even_when_old_or_acceptance_placeholder_empty(bool emptyRecord)
    {
        var facts = new List<SourceFact> { Job(Now.AddDays(-10)), Ready() };
        if (emptyRecord) facts.Add(Acceptance(0));
        var row = Assert.Single(Project(facts.ToArray()).Projects);
        Assert.Equal(CliWatchStatus.Delivered, row.Phase);
        Assert.False(row.IsActive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Live_CLI_or_wrapper_is_not_hidden_by_age_or_Lead_record(bool wrapperOnly)
    {
        var bind = Fact("process_bind", new JsonObject
        {
            ["cli_alive"] = !wrapperOnly, ["wrapper_alive"] = wrapperOnly,
            ["wrapper_state"] = "alive", ["activity_kind"] = "quiet"
        });
        var row = Assert.Single(Project(Job(Now.AddDays(-10), timeout: 1), Ready(), Acceptance(20), bind).Projects);
        if (wrapperOnly)
        {
            Assert.False(row.IsActive);
            Assert.Equal(CliWatchStatus.Delivered, row.Phase);
        }
        else
        {
            Assert.True(row.IsActive);
            Assert.True(CliWatchStatus.IsRunning(row.Phase));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Stale_unconfirmed_latest_registration_moves_to_history_without_completion_claim(bool unknown)
    {
        var job = Job(Now.AddHours(-3), timeout: 120);
        if (unknown) job.Values["corrupt"] = true;
        var row = Assert.Single(Project(job).Projects);
        Assert.Equal(unknown ? CliWatchStatus.Unknown : CliWatchStatus.Registered, row.Phase);
        Assert.False(row.IsActive);
        Assert.Contains("不代表完成或失败", row.NextStep);
    }

    [Fact]
    public void History_hours_is_effective_and_refresh_does_not_renew_registration()
    {
        var facts = new[] { Job(Now.AddHours(-1)), Config(historyHours: 2) };
        var projector = new CliWatchProjector();
        var first = projector.Project(new FactBatch(Now, facts, Array.Empty<SourceIssue>()));
        Assert.False(Assert.Single(first.Projects).IsActive);
        var later = projector.Project(new FactBatch(Now.AddHours(2), facts, Array.Empty<SourceIssue>()), first);
        Assert.False(Assert.Single(later.Projects).IsActive);
    }

    [Fact]
    public void Recent_source_output_and_new_attempt_stay_current_beside_expired_old_registration()
    {
        var output = Fact("outbox_file", new JsonObject
        {
            ["name"] = "progress.json", ["mtime"] = Now.AddMinutes(-1).ToString("o")
        });
        var fresh = Assert.Single(Project(Job(Now.AddDays(-10), timeout: 120), output).Projects);
        Assert.False(fresh.IsActive);
        var old = Job(Now.AddDays(-10), timeout: 120);
        var newer = Job(Now.AddMinutes(-1), timeout: 120, key: "work--2");
        var rows = Project(old, newer).Projects;
        Assert.False(rows.Single(p => p.Id == "work--1").IsActive);
        Assert.False(rows.Single(p => p.Id == "work--2").IsActive);
    }

    [Fact]
    public void Ended_executor_without_delivery_expires_but_explicit_unhandled_failure_does_not()
    {
        var terminal = Fact("adapter_terminal", new JsonObject
        {
            ["identity_ok"] = true, ["exit_code"] = 0,
            ["finished_at_utc"] = Now.AddHours(-3).ToString("o")
        });
        var row = Assert.Single(Project(Job(Now.AddDays(-1), timeout: 120), terminal).Projects);
        Assert.Equal(CliWatchStatus.ExecutorFinished, row.Phase);
        Assert.False(row.IsActive);
        terminal.Values["exit_code"] = 1;
        var failure = Assert.Single(Project(Job(Now.AddDays(-1), timeout: 120), terminal).Projects);
        Assert.Equal(CliWatchStatus.Failed, failure.Phase);
        Assert.False(failure.IsActive);
    }

    static CockpitSnapshot Project(params SourceFact[] facts) =>
        new CliWatchProjector().Project(new FactBatch(Now, facts, Array.Empty<SourceIssue>()));

    static SourceFact Job(DateTimeOffset registered, int? timeout = null, string key = "work--1") =>
        Fact("mailbox_job", new JsonObject
        {
            ["project"] = "alpha", ["job_id"] = "work", ["attempt_id"] = key.Split("--")[1],
            ["state"] = "registered", ["registered_at"] = registered.ToString("o"),
            ["timeout_minutes"] = timeout
        }, key);

    static SourceFact Ready() => Fact("outbox_ready", new JsonObject { ["status"] = "completed" });
    static SourceFact Acceptance(long bytes) => Fact("acceptance_record", new JsonObject { ["bytes"] = bytes });
    static SourceFact Config(int historyHours) => Fact("watch_config", new JsonObject { ["history_hours"] = historyHours });

    static SourceFact Fact(string kind, JsonObject values, string key = "work--1")
    {
        values["job_key"] = key;
        values["mailbox_root"] = "C:/isolated-current-history";
        return new SourceFact(kind, kind + ":" + key, "alpha", new Dictionary<string, string>
        {
            ["job_key"] = key, ["mailbox_root"] = "C:/isolated-current-history"
        }, values, Array.Empty<EvidenceRef>());
    }
}
