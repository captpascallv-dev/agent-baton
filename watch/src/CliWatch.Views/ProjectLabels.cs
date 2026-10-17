// SPDX-License-Identifier: MPL-2.0
using CliWatch.Contracts;
using CliWatch.Projection;

namespace CliWatch.Views;

/// <summary>
/// Known project display names. Unknown ids stay as registered.
/// </summary>
public static class ProjectLabels
{
    public static string ProjectLabel(string? raw, UiLang lang)
    {
        var key = (raw ?? string.Empty).Trim();
        if (key.Length == 0) return lang == UiLang.En ? "unspecified" : "未标明项目";
        return key.ToLowerInvariant() switch
        {
            "cli-watch" => "CLI Watch",
            "relay-test" => lang == UiLang.En ? "Relay test" : "中继测试",
            "unspecified" => lang == UiLang.En ? "unspecified" : "未标明项目",
            _ => key
        };
    }

    public static string ProjectKeyFromName(string? displayName)
    {
        var name = (displayName ?? string.Empty).Trim();
        var slash = name.IndexOf(" / ", StringComparison.Ordinal);
        if (slash <= 0) return name;
        return name[..slash].Trim();
    }

    public static string JobPartFromName(string? displayName)
    {
        var name = (displayName ?? string.Empty).Trim();
        var slash = name.IndexOf(" / ", StringComparison.Ordinal);
        if (slash < 0) return name;
        return name[(slash + 3)..].Trim();
    }

    public static string TaskTitle(ProjectView project, UiLang lang)
    {
        if (string.Equals(project.Phase, CliWatchStatus.Unbound, StringComparison.Ordinal))
        {
            var n = project.WorkItems.Count(w => !StatusLanguage.IsHistoricalWork(w));
            if (lang == UiLang.En)
                return n == 1 ? "1 unbound instance" : n + " unbound instances";
            return "未关联 " + n + " 个实例";
        }

        var goal = project.Goal?.Trim();
        if (!string.IsNullOrWhiteSpace(goal)
            && goal is not "未声明" and not "未用一句话写明目标" and not "not stated" and not "no one-line goal")
            return goal;

        var work = project.WorkItems.FirstOrDefault();
        var task = work?.TaskName?.Trim();
        if (!string.IsNullOrWhiteSpace(task) && task is not "未关联任务")
            return task;

        var job = JobPartFromName(project.Name);
        if (!string.IsNullOrWhiteSpace(job)) return job;
        return string.IsNullOrWhiteSpace(project.Id) ? (lang == UiLang.En ? "untitled task" : "未写明任务") : project.Id;
    }

    public static string LastActivitySnippet(string oneLiner)
    {
        if (string.IsNullOrWhiteSpace(oneLiner)) return string.Empty;
        foreach (var marker in new[] { "最后活动", "Last activity", "last activity" })
        {
            var i = oneLiner.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (i < 0) continue;
            var slice = oneLiner[i..];
            var cut = slice.IndexOf('·');
            return (cut > 0 ? slice[..cut] : slice).Trim();
        }

        return string.Empty;
    }

    public static string ActivityLine(ProjectView project, DateTimeOffset now, UiLang lang)
    {
        var english = lang == UiLang.En;
        if (project.ObservedActivityAt is { } observed)
            return CliWatchStatus.FormatAgo(observed, now, english);

        var full = string.IsNullOrWhiteSpace(project.Summary) ? string.Empty : project.Summary;
        var fromFull = LastActivitySnippet(full);
        if (!string.IsNullOrWhiteSpace(fromFull))
            return english ? ConsumerCopy.Localize(fromFull, lang) : fromFull;

        return english ? "Last activity: unknown" : "最后活动未知";
    }

    public static string RuntimeLine(ProjectView project, UiLang lang)
    {
        var full = project.Summary ?? string.Empty;
        foreach (var marker in new[] { "运行 ", "启动等待已过 ", "登记已过 " })
        {
            var i = full.IndexOf(marker, StringComparison.Ordinal);
            if (i < 0) continue;
            var slice = full[i..];
            var cut = slice.IndexOf(" · ", StringComparison.Ordinal);
            var raw = (cut > 0 ? slice[..cut] : slice).Trim();
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (lang != UiLang.En) return raw;
            return raw
                .Replace("运行 ", "Ran ", StringComparison.Ordinal)
                .Replace("启动等待已过 ", "Waiting for CLI start ", StringComparison.Ordinal)
                .Replace("登记已过 ", "Registered for ", StringComparison.Ordinal)
                .Replace(" 小时 ", "h ", StringComparison.Ordinal)
                .Replace(" 分 ", "m ", StringComparison.Ordinal)
                .Replace(" 秒", "s", StringComparison.Ordinal);
        }

        return string.Empty;
    }
}
