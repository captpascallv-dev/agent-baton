// SPDX-License-Identifier: MPL-2.0
using CliWatch.Contracts;

namespace CliWatch.Views;

/// <summary>
/// The default list is this round's confirmed execution or confirmed launch,
/// plus a real CLI that is not associated with a job. Counts, selection, and
/// diagnostic scope share that membership. Ended deliveries, failures, and
/// registrations stay on the snapshot for history.
/// </summary>
public static class CurrentList
{
    public static bool IsDefaultCurrent(ProjectView project) =>
        project.IsActive && !project.IsPaused;

    public static IReadOnlyList<ProjectView> DefaultItems(CockpitSnapshot snapshot) =>
        snapshot.Projects.Where(IsDefaultCurrent).ToList();

    public static IReadOnlyList<ProjectView> HistoricalItems(CockpitSnapshot snapshot) =>
        snapshot.Projects.Where(p => !IsDefaultCurrent(p)).ToList();

    public static bool IsHistoricalIssue(SourceIssue issue)
    {
        if (issue.Code.Equals("historical_source_missing", StringComparison.OrdinalIgnoreCase))
            return true;
        return issue.Detail.Contains("scope=historical", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<SourceIssue> CurrentIssues(IEnumerable<SourceIssue> issues) =>
        issues.Where(i => !IsHistoricalIssue(i)).ToList();

    public static IReadOnlyList<SourceIssue> HistoricalIssues(IEnumerable<SourceIssue> issues) =>
        issues.Where(IsHistoricalIssue).ToList();

    public static string? ResolveSelection(CockpitSnapshot snapshot, string? previousSelectedProjectId, bool showHistory)
    {
        var visible = showHistory
            ? snapshot.Projects.ToList()
            : DefaultItems(snapshot).ToList();
        if (visible.Count == 0)
        {
            return showHistory ? snapshot.Projects.FirstOrDefault()?.Id : null;
        }

        if (!string.IsNullOrWhiteSpace(previousSelectedProjectId)
            && visible.Any(p => p.Id == previousSelectedProjectId))
        {
            return previousSelectedProjectId;
        }

        var preferred = visible.FirstOrDefault(IsDefaultCurrent) ?? visible[0];
        return preferred.Id;
    }
}
