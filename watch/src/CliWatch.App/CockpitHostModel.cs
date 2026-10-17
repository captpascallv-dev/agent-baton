// SPDX-License-Identifier: MPL-2.0
using CliWatch.Contracts;

namespace CliWatch.App;

public sealed record HistoryEntry(CockpitSnapshot Snapshot, string? SelectedProjectId, DateTimeOffset NotedAt);

/// <summary>
/// Single host model: one CockpitSnapshot feeds HUD, details, help, and history.
/// Selection is preserved across refresh when the project is still present.
/// Does not re-judge status or clamp project count.
/// </summary>
public sealed class CockpitHostModel
{
    public CockpitSnapshot? Snapshot { get; private set; }
    public string? SelectedProjectId { get; private set; }

    public CockpitSnapshot? HudSnapshot => Snapshot;
    public CockpitSnapshot? DetailsSnapshot => Snapshot;

    public void ApplySnapshot(CockpitSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Snapshot = snapshot;
        if (SelectedProjectId is not null
            && snapshot.Projects.Any(p =>
                string.Equals(p.Id, SelectedProjectId, StringComparison.Ordinal)))
        {
            return;
        }

        SelectedProjectId = null;
    }

    public bool SelectProject(string? projectId)
    {
        if (projectId is null)
        {
            SelectedProjectId = null;
            return true;
        }

        if (Snapshot is null)
        {
            SelectedProjectId = projectId;
            return true;
        }

        if (Snapshot.Projects.Any(p => string.Equals(p.Id, projectId, StringComparison.Ordinal)))
        {
            SelectedProjectId = projectId;
            return true;
        }

        return false;
    }

    public HistoryEntry CreateHistoryEntry()
    {
        if (Snapshot is null)
            throw new InvalidOperationException("No snapshot to record.");
        return new HistoryEntry(Snapshot, SelectedProjectId, DateTimeOffset.UtcNow);
    }

    public ProjectView? SelectedProject
    {
        get
        {
            if (Snapshot is null || SelectedProjectId is null) return null;
            return Snapshot.Projects.FirstOrDefault(p => string.Equals(p.Id, SelectedProjectId, StringComparison.Ordinal));
        }
    }
}
