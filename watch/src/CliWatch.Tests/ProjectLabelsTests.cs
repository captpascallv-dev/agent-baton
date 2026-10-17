// SPDX-License-Identifier: MPL-2.0
using CliWatch.Contracts;
using CliWatch.Projection;
using CliWatch.Views;
using Xunit;

namespace CliWatch.Tests;

public sealed class ProjectLabelsTests
{
    [Fact]
    public void Known_project_maps_to_chinese()
    {
        Assert.Equal("demo-project", ProjectLabels.ProjectLabel("demo-project", UiLang.Zh));
        Assert.Equal("CLI Watch", ProjectLabels.ProjectLabel("cli-watch", UiLang.Zh));
        Assert.Equal("mystery-proj", ProjectLabels.ProjectLabel("mystery-proj", UiLang.Zh));
    }

    [Fact]
    public void Task_title_uses_goal_not_guessed_summary()
    {
        var project = new ProjectView(
            "job--1",
            "demo-project / job #1",
            "summary",
            CliWatchStatus.Delivered,
            "示例任务核对",
            "next",
            true,
            false,
            null,
            Array.Empty<WorkView>(),
            Array.Empty<AttentionItem>(),
            Array.Empty<NavigationTarget>(),
            DataQuality.Fresh,
            DateTimeOffset.UtcNow);
        Assert.Equal("示例任务核对", ProjectLabels.TaskTitle(project, UiLang.Zh));
        Assert.Equal("demo-project", ProjectLabels.ProjectLabel(ProjectLabels.ProjectKeyFromName(project.Name), UiLang.Zh));
    }
}
