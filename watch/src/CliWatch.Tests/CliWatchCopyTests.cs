// SPDX-License-Identifier: MPL-2.0
using CliWatch.Contracts;
using CliWatch.Projection;
using CliWatch.Views;
using Xunit;

namespace CliWatch.Tests;

public sealed class CliWatchCopyTests
{
    [Theory]
    [InlineData(CliWatchStatus.Delivered, "已交回", "在执行")]
    [InlineData(CliWatchStatus.ExecutorFinished, "未见交付", "在执行")]
    [InlineData(CliWatchStatus.Failed, "明确失败", "在执行")]
    [InlineData(CliWatchStatus.StartUnconfirmed, "尚未证实", "在执行")]
    [InlineData(CliWatchStatus.Registered, "已登记", "在执行")]
    [InlineData(CliWatchStatus.Unknown, "状态未知", "在执行")]
    [InlineData(CliWatchStatus.Retired, "已退役", "在执行")]
    [InlineData(CliWatchStatus.RunningUnknown, "活动观察未知", "在执行")]
    public void Terminal_or_unconfirmed_who_line_does_not_claim_running(string phase, string mustHave, string mustNot)
    {
        var project = Project(phase, "Codex", "latest");
        var who = CliWatchCopy.WhoLine(project, project.WorkItems[0], UiLang.Zh);
        var situation = CliWatchCopy.Situation(project, UiLang.Zh);
        Assert.Contains(mustHave, who, StringComparison.Ordinal);
        Assert.DoesNotContain(mustNot, who, StringComparison.Ordinal);
        Assert.Equal(who, situation.Who);
        Assert.DoesNotContain("项目完成", who, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CliWatchStatus.Delivered, "handed back", "is running")]
    [InlineData(CliWatchStatus.ExecutorFinished, "no delivery seen", "is running")]
    [InlineData(CliWatchStatus.Failed, "failed or exited abnormally", "is running")]
    [InlineData(CliWatchStatus.StartUnconfirmed, "not confirmed started", "is running")]
    [InlineData(CliWatchStatus.Registered, "start is not confirmed", "is running")]
    [InlineData(CliWatchStatus.Unknown, "status is unknown", "is running")]
    [InlineData(CliWatchStatus.Retired, "retired or superseded", "is running")]
    public void English_who_line_does_not_claim_running(string phase, string mustHave, string mustNot)
    {
        var project = Project(phase, "Codex", "latest");
        var who = CliWatchCopy.WhoLine(project, project.WorkItems[0], UiLang.En);
        Assert.Contains(mustHave, who, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(mustNot, who, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Running_who_line_may_say_executing()
    {
        var project = Project(CliWatchStatus.RunningActive, "Grok", "live-job");
        var zh = CliWatchCopy.WhoLine(project, project.WorkItems[0], UiLang.Zh);
        var en = CliWatchCopy.WhoLine(project, project.WorkItems[0], UiLang.En);
        Assert.Contains("在执行", zh, StringComparison.Ordinal);
        Assert.Contains("is running", en, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Help_summary_uses_same_who_line_as_details()
    {
        var project = Project(CliWatchStatus.Delivered, "Codex", "latest");
        var snapshot = new CockpitSnapshot(DateTimeOffset.UtcNow, new[] { project }, Array.Empty<SourceIssue>(), Array.Empty<RouteCoverage>(), DataQuality.Fresh);
        var details = DetailsPresentation.Build(snapshot, project.Id, UiLang.Zh);
        var summary = HelpSummaryText.Build(snapshot, project.Id, UiLang.Zh);
        Assert.Contains("已交回", details.WhoDoingWhat, StringComparison.Ordinal);
        Assert.DoesNotContain("在执行", details.WhoDoingWhat, StringComparison.Ordinal);
        Assert.Contains(details.WhoDoingWhat, summary, StringComparison.Ordinal);
        Assert.DoesNotContain("在执行「latest」", summary, StringComparison.Ordinal);
    }

    static ProjectView Project(string phase, string actor, string task)
    {
        var axes = new WorkAxes("dead", "unknown", "returned", "complete", "complete", "unknown", "awaiting_lead", "pending", "unknown", "unknown");
        var work = new WorkView(
            "work:" + task,
            "executor",
            "watch:" + phase,
            actor + " · " + CliWatchStatus.LabelZh(phase),
            axes,
            Array.Empty<ArtifactView>(),
            Array.Empty<NavigationTarget>(),
            Array.Empty<EvidenceRef>(),
            DataQuality.Fresh,
            actor,
            task,
            null,
            null,
            null,
            "executor",
            null);
        return new ProjectView(
            task + "--1",
            "fixture-alpha / " + task + " #1",
            work.Summary,
            phase,
            task,
            "下一步",
            true,
            false,
            null,
            new[] { work },
            Array.Empty<AttentionItem>(),
            Array.Empty<NavigationTarget>(),
            DataQuality.Fresh,
            DateTimeOffset.UtcNow);
    }
}
