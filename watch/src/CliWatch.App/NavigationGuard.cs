// SPDX-License-Identifier: MPL-2.0
using CliWatch.Contracts;

namespace CliWatch.App;

public static class NavigationGuard
{
    public static bool IsAllowed(CockpitSnapshot? snapshot, NavigationTarget? target)
    {
        if (snapshot is null || target is null) return false;
        if (string.IsNullOrWhiteSpace(target.Kind) || string.IsNullOrWhiteSpace(target.Target))
            return false;

        foreach (var project in snapshot.Projects)
        {
            if (Contains(project.Targets, target)) return true;
            foreach (var work in project.WorkItems)
            {
                if (Contains(work.Targets, target)) return true;
                foreach (var artifact in work.Artifacts)
                {
                    if (artifact.Target is { } at && Same(at, target))
                        return true;
                }
            }

            foreach (var attention in project.Attention)
            {
                if (Contains(attention.Targets, target)) return true;
            }
        }

        return false;
    }

    public static bool LooksLikeUnsupportedUrl(NavigationTarget target)
    {
        var value = target.Target;
        if (string.IsNullOrWhiteSpace(value)) return true;
        var idx = value.IndexOf("://", StringComparison.Ordinal);
        if (idx <= 0) return false;
        var scheme = value[..idx];
        return !scheme.Equals("file", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsFileKind(string kind) =>
        kind.Equals("file", StringComparison.OrdinalIgnoreCase)
        || kind.Equals("local_file", StringComparison.OrdinalIgnoreCase)
        || kind.Equals("path", StringComparison.OrdinalIgnoreCase)
        || kind.Equals("folder", StringComparison.OrdinalIgnoreCase)
        || kind.Equals("directory", StringComparison.OrdinalIgnoreCase);

    public static bool IsCodexTaskKind(string kind) =>
        kind.Equals("codex_task", StringComparison.OrdinalIgnoreCase)
        || kind.Equals("task", StringComparison.OrdinalIgnoreCase)
        || kind.Equals("thread", StringComparison.OrdinalIgnoreCase)
        || kind.Equals("codex", StringComparison.OrdinalIgnoreCase)
        || kind.Equals("lead_task", StringComparison.OrdinalIgnoreCase);

    static bool Contains(IReadOnlyList<NavigationTarget> targets, NavigationTarget needle)
    {
        foreach (var t in targets)
        {
            if (Same(t, needle)) return true;
        }
        return false;
    }

    public static bool Same(NavigationTarget a, NavigationTarget b) =>
        string.Equals(a.Kind, b.Kind, StringComparison.Ordinal)
        && string.Equals(a.Target, b.Target, StringComparison.Ordinal)
        && string.Equals(a.ProjectId, b.ProjectId, StringComparison.Ordinal)
        && string.Equals(a.EntityId, b.EntityId, StringComparison.Ordinal);
}
