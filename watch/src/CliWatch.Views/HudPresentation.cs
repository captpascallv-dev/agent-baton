// SPDX-License-Identifier: MPL-2.0
using CliWatch.Contracts;
using CliWatch.Projection;

namespace CliWatch.Views;

public sealed record HudAttentionLine(string Id, string Text, string FullText, IReadOnlyList<NavigationTarget> Targets);

public sealed record HudProjectRow(
    string ProjectId,
    string Name,
    string OverallLine,
    string OverallLineFull,
    string StatusLine,
    string StatusLineFull,
    bool IsSelected,
    bool IsPaused,
    bool IsActive,
    DataQuality Quality,
    string ProjectLabel = "",
    string TaskTitle = "",
    string ActorChip = "",
    string ModelChip = "",
    string LastActivityText = "",
    string BadgeKind = "",
    string JobKey = "",
    string MetaLine = "",
    string RuntimeText = "");

public sealed record HudModel(
    DateTimeOffset? CollectedAt,
    string CollectedAtText,
    string QualityText,
    DataQuality Quality,
    int UserAttentionCount,
    IReadOnlyList<HudAttentionLine> UserAttentions,
    IReadOnlyList<HudProjectRow> Projects,
    string? IssuesSummary,
    string? BannerMessage,
    string? SelectedProjectId,
    IReadOnlyList<HudProjectRow> HistoricalProjects,
    int CurrentCount,
    int HistoricalCount,
    int CurrentIssueCount,
    bool ShowingHistory,
    string FreshnessHint);

public static class HudPresentation
{
    public static string? ResolveSelection(CockpitSnapshot snapshot, string? previousSelectedProjectId) =>
        CurrentList.ResolveSelection(snapshot, previousSelectedProjectId, showHistory: false);

    public static string DisplayName(ProjectView project)
    {
        var name = string.IsNullOrWhiteSpace(project.Name) ? project.Id : project.Name.Trim();
        if (name.Contains("驾驶舱", StringComparison.Ordinal)
            && (project.IsActive && !project.IsPaused)
            && (project.Phase.Contains("修复", StringComparison.Ordinal)
                || project.Summary.Contains("退修", StringComparison.Ordinal)
                || project.Summary.Contains("实窗", StringComparison.Ordinal)
                || project.Id.Contains("cockpit", StringComparison.OrdinalIgnoreCase)))
        {
            return "驾驶舱状态修复";
        }

        return name;
    }

    public static HudModel Build(CockpitSnapshot? snapshot, string? previousSelectedProjectId, int statusMaxLen = StatusLanguage.DefaultHudMaxLen) =>
        Build(snapshot, previousSelectedProjectId, showHistory: false, statusMaxLen, UiLang.Zh);

    public static HudModel Build(CockpitSnapshot? snapshot, string? previousSelectedProjectId, bool showHistory, int statusMaxLen = StatusLanguage.DefaultHudMaxLen) =>
        Build(snapshot, previousSelectedProjectId, showHistory, statusMaxLen, UiLang.Zh);

    public static HudModel Build(CockpitSnapshot? snapshot, string? previousSelectedProjectId, bool showHistory, int statusMaxLen, UiLang lang)
    {
        if (snapshot is null)
        {
            return new HudModel(
                null,
                ConsumerCopy.T(lang, "wait_first"),
                ConsumerCopy.Localize("无快照", lang),
                DataQuality.Unavailable,
                0,
                Array.Empty<HudAttentionLine>(),
                Array.Empty<HudProjectRow>(),
                null,
                ConsumerCopy.Localize("暂无快照。等待采集结果。", lang),
                null,
                Array.Empty<HudProjectRow>(),
                0,
                0,
                0,
                showHistory,
                string.Empty);
        }

        var currentProjects = CurrentList.DefaultItems(snapshot);
        var historicalProjects = CurrentList.HistoricalItems(snapshot);
        var selected = CurrentList.ResolveSelection(snapshot, previousSelectedProjectId, showHistory);
        var userItems = currentProjects
            .SelectMany(p => p.Attention.Select(a => (Project: p, Item: a)))
            .Where(x => x.Item.Owner == AttentionOwner.User)
            .Select(x => new HudAttentionLine(
                x.Item.Id,
                ConsumerCopy.Localize(StatusLanguage.AttentionLine(x.Item), lang),
                ConsumerCopy.Localize(StatusLanguage.AttentionLineFull(x.Item), lang),
                x.Item.Targets))
            .ToList();

        string? banner = null;
        if (snapshot.Quality == DataQuality.Unavailable)
            banner = ConsumerCopy.Localize("来源不可用：空列表不能当成没有任务。请查看来源问题。", lang);
        else if (snapshot.Quality == DataQuality.Conflicting)
            banner = ConsumerCopy.Localize("快照存在来源冲突：请展开详情核实，勿当作全部正常。", lang);
        else if (snapshot.Quality == DataQuality.LastKnown)
            banner = ConsumerCopy.Localize("当前展示为上次已知状态，非本轮新鲜采集。", lang);
        else if (snapshot.Quality == DataQuality.Partial)
            banner = ConsumerCopy.L(lang, "本轮采集已更新；部分来源受限。观察缺口不新增当前任务。", "This refresh is current; some sources are limited. A collection gap does not add a current task.");
        else if (currentProjects.Count == 0)
            banner = ConsumerCopy.Localize(snapshot.Projects.Count == 0 ? "当前没有已登记的 CLI 任务。" : "当前没有进行中的 CLI 任务。近期历史可按需查看。", lang);
        if (snapshot.Issues.Any(i => i.Code.Equals("sample_banner", StringComparison.OrdinalIgnoreCase)))
        {
            var sample = ConsumerCopy.Localize("样例（模拟状态，非本机实况）", lang);
            banner = string.IsNullOrWhiteSpace(banner) ? sample : sample + " · " + banner;
        }

        HudProjectRow Row(ProjectView p)
        {
            var overall = StatusLanguage.OverallJudgment(p, lang);
            var display = DisplayName(p);
            var one = StatusLanguage.ProjectOneLiner(p, statusMaxLen, lang);
            var work = p.WorkItems.FirstOrDefault();
            var actor = work?.ActorName ?? string.Empty;
            var model = string.Join(" · ", new[] { work?.Model, work?.Effort }.Where(s => !string.IsNullOrWhiteSpace(s)));
            var last = ProjectLabels.ActivityLine(p, snapshot.CollectedAt, lang);
            var runtime = ProjectLabels.RuntimeLine(p, lang);
            string meta;
            if (p.Phase == CliWatchStatus.Unbound)
            {
                var identities = string.Join(" | ", p.WorkItems
                    .Where(w => !StatusLanguage.IsHistoricalWork(w))
                    .Select(MetaOf)
                    .Where(s => !string.IsNullOrWhiteSpace(s)));
                var note = ConsumerCopy.L(lang,
                    "观察到 CLI 进程存活；任务关联、是否在处理任务或等待输入尚未确认。",
                    "Observed CLI processes are alive. Task association, whether a task is in progress, and waiting for input are not confirmed.");
                meta = string.IsNullOrWhiteSpace(identities) ? note : note + " " + identities;
            }
            else
            {
                meta = string.Join(" · ", new[] { actor, model }.Where(s => !string.IsNullOrWhiteSpace(s)));
            }
            var pathChip = PathChip(p);
            if (!string.IsNullOrWhiteSpace(pathChip))
                meta = string.Join(" · ", new[] { meta, pathChip }.Where(s => !string.IsNullOrWhiteSpace(s)));
            return new(
                p.Id,
                display,
                overall.Label,
                overall.Label + " · " + overall.Basis,
                one,
                StatusLanguage.ProjectOneLinerFull(p, lang),
                selected is not null && p.Id == selected,
                p.IsPaused,
                p.IsActive,
                p.Quality,
                ProjectLabels.ProjectLabel(ProjectLabels.ProjectKeyFromName(p.Name), lang),
                ProjectLabels.TaskTitle(p, lang),
                actor,
                model,
                last,
                StatusTone.Kind(p.Phase),
                p.Id,
                meta,
                runtime);
        }

        var visible = showHistory
            ? currentProjects.Concat(historicalProjects).Select(Row).ToList()
            : currentProjects.Select(Row).ToList();
        var historicalRows = historicalProjects.Select(Row).ToList();
        var issues = StatusLanguage.IssuesSummary(snapshot.Issues, snapshot, 3, lang);
        if (string.IsNullOrWhiteSpace(issues)) issues = null;
        var freshness = snapshot.Quality == DataQuality.LastKnown
            ? ConsumerCopy.Localize("沿用上次已知，不是这一轮刚采到的。", lang)
            : string.Empty;

        static string MetaOf(WorkView item)
        {
            var itemModel = string.Join(" · ", new[] { item.Model, item.Effort }.Where(s => !string.IsNullOrWhiteSpace(s)));
            return string.Join(" · ", new[] { item.ActorName, itemModel }.Where(s => !string.IsNullOrWhiteSpace(s)));
        }

        static string? PathChip(ProjectView project)
        {
            var marked = project.WorkItems.Any(item =>
                (item.ActorName ?? string.Empty).Contains("Claude Lead发起", StringComparison.Ordinal)
                || (item.Model ?? string.Empty).Contains("登记指定", StringComparison.Ordinal)
                || (item.Model ?? string.Empty).Contains("已观察", StringComparison.Ordinal)
                || (item.Effort ?? string.Empty).Contains("登记指定", StringComparison.Ordinal)
                || (item.Effort ?? string.Empty).Contains("已观察", StringComparison.Ordinal)
                || item.Artifacts.Any(artifact => artifact.Label is "启动入口" or "Claude Lead登记"));
            if (!marked) return null;
            var path = project.WorkItems
                .SelectMany(item => item.Artifacts)
                .FirstOrDefault(artifact => artifact.Label is "Claude Lead登记" or "启动入口")
                ?.Target?.Target;
            if (string.IsNullOrWhiteSpace(path)) return null;
            var norm = path.Replace('\\', '/');
            return norm.Length <= 72 ? norm : "…" + norm[^60..];
        }

        return new HudModel(
            snapshot.CollectedAt,
            StatusLanguage.CollectedAtText(snapshot.CollectedAt),
            StatusLanguage.SnapshotQualityText(snapshot.Quality, lang)
                + " · " + StatusLanguage.CurrentWindowLine(snapshot, lang),
            snapshot.Quality,
            userItems.Count,
            userItems,
            visible,
            issues,
            banner,
            selected,
            historicalRows,
            currentProjects.Count,
            historicalProjects.Count,
            StatusLanguage.CurrentIssueCount(snapshot.Issues, snapshot),
            showHistory,
            freshness);
    }
}
