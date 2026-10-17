// SPDX-License-Identifier: MPL-2.0
using CliWatch.Contracts;
using CliWatch.Projection;

namespace CliWatch.Views;

/// <summary>
/// Default-visible role rows. One owner, each current executor/reviewer/live
/// legion as its own item. Does not collapse same-status items into one phrase.
/// </summary>
public static class RolePresentation
{
    public static IReadOnlyList<DetailsWorkRow> CurrentRows(ProjectView project, UiLang lang)
    {
        var current = project.WorkItems
            .Where(w => !StatusLanguage.IsHistoricalWork(w) && !StatusLanguage.IsDiagnosticNoise(w))
            .Where(w => !IsFoldedLeadAcceptance(w) && KindOf(w) != "progress")
            .ToList();
        return Numbered(current, historical: false, lang);
    }

    public static IReadOnlyList<DetailsWorkRow> HistoricalRows(ProjectView project, UiLang lang)
    {
        var historical = project.WorkItems
            .Where(w => StatusLanguage.IsHistoricalWork(w) && !StatusLanguage.IsDiagnosticNoise(w))
            .ToList();
        return Numbered(historical, historical: true, lang);
    }

    public static bool HasExplicitNoReviewer(ProjectView project) =>
        project.WorkItems.Any(w =>
            !StatusLanguage.IsHistoricalWork(w)
            && string.Equals(w.ReviewAssigned, "false", StringComparison.OrdinalIgnoreCase));

    public static string Missing(UiLang lang) => ConsumerCopy.T(lang, "not_obtained");

    public static string WorkStatus(WorkView w, UiLang lang, IReadOnlyList<WorkView>? siblings = null)
    {
        if ((w.Blocker ?? string.Empty).Contains(CliWatchStatus.AcceptanceUnverifiedZh, StringComparison.Ordinal)
            || (w.Summary ?? string.Empty).Contains(CliWatchStatus.AcceptanceUnverifiedZh, StringComparison.Ordinal))
        {
            return ConsumerCopy.L(lang, CliWatchStatus.AcceptanceUnverifiedZh, CliWatchStatus.AcceptanceUnverifiedEn);
        }

        if (string.Equals(w.ActorKind, "lead", StringComparison.OrdinalIgnoreCase)
            || w.Role.Equals("lead", StringComparison.OrdinalIgnoreCase))
        {
            return ConsumerCopy.Localize(LeadStatus(w, siblings), lang);
        }

        if (KindOf(w) == "reviewer")
            return ConsumerCopy.Localize(StatusLanguage.ReviewerStatusPhrase(w), lang);

        var execPhrase = StatusLanguage.WorkStatusPhrase(w);
        if (string.IsNullOrWhiteSpace(execPhrase) || StatusLanguage.LooksLikeTechnicalDump(execPhrase))
            execPhrase = "未获取";
        return ConsumerCopy.Localize(execPhrase, lang);
    }

    public static string LeadStatus(WorkView lead, IReadOnlyList<WorkView>? siblings = null)
    {
        var handling = lead.Axes.LeadHandling ?? "";
        var acceptance = lead.Axes.Acceptance ?? "";
        if (handling.Equals("blocked_after_acceptance", StringComparison.OrdinalIgnoreCase))
            return "验收后待派出退修";
        if (handling.Equals("callback_wait", StringComparison.OrdinalIgnoreCase))
            return "等待回叫";
        if (handling.Equals("repair_dispatched", StringComparison.OrdinalIgnoreCase))
            return "已派出退修";
        if (handling.Equals("repair_prepared", StringComparison.OrdinalIgnoreCase))
            return "验收发现问题，等待退修";
        if (handling.Equals("lead_accepting", StringComparison.OrdinalIgnoreCase)
            || acceptance.Equals("acceptance_in_progress", StringComparison.OrdinalIgnoreCase))
            return "正在验收";
        if (acceptance.Contains("fail_repair", StringComparison.OrdinalIgnoreCase)
            || handling.Contains("fail_repair", StringComparison.OrdinalIgnoreCase))
            return "验收发现问题，等待退修";
        if (acceptance.Equals("content_not_accepted", StringComparison.OrdinalIgnoreCase))
            return "内容未验收";
        if (acceptance.Equals("handled_accepted", StringComparison.OrdinalIgnoreCase)
            || StatusLanguage.IsExplicitAccepted(acceptance))
            return "已验收";
        var others = siblings ?? Array.Empty<WorkView>();
        if (others.Any(o => KindOf(o) == "executor"
                            && (o.Axes.LeadHandling.Equals("lead_accepting", StringComparison.OrdinalIgnoreCase)
                                || o.Axes.Acceptance.Equals("acceptance_in_progress", StringComparison.OrdinalIgnoreCase))))
            return "正在验收";
        if (others.Any(o => KindOf(o) == "executor"
                            && o.Axes.LeadHandling.Equals("repair_dispatched", StringComparison.OrdinalIgnoreCase)))
            return "已派出退修";
        if (others.Any(o => KindOf(o) == "executor"
                            && o.Axes.LeadHandling.Equals("repair_prepared", StringComparison.OrdinalIgnoreCase)))
            return "验收发现问题，等待退修";
        if (others.Any(o => KindOf(o) == "executor"
                            && o.Axes.LeadHandling.Equals("dispatch_pending_native", StringComparison.OrdinalIgnoreCase)))
            return "等待回叫";
        if (others.Any(o => KindOf(o) == "executor"
                            && (o.Axes.Execution.Equals("active", StringComparison.OrdinalIgnoreCase)
                                || o.Axes.Execution.Equals("running", StringComparison.OrdinalIgnoreCase))))
            return "等待执行者交回";
        if (others.Any(o => KindOf(o) == "executor"
                            && (o.Axes.Execution.Equals("returned", StringComparison.OrdinalIgnoreCase)
                                || o.Axes.Execution.Equals("succeeded", StringComparison.OrdinalIgnoreCase))))
            return "等待负责人处理";
        if (IsUnknown(handling) && IsUnknown(acceptance)
            && IsUnknown(lead.Axes.Turn) && IsUnknown(lead.Axes.Execution))
            return "未获取";
        if (handling.Equals("planning", StringComparison.OrdinalIgnoreCase)
            || handling.Equals("plan", StringComparison.OrdinalIgnoreCase))
            return "规划中";
        return "未获取";
    }

    public static string ActorDisplay(WorkView w, UiLang lang)
    {
        if (!string.IsNullOrWhiteSpace(w.ActorName) && !IsUnknown(w.ActorName))
            return w.ActorName!;
        if (!string.IsNullOrWhiteSpace(w.Route) && !IsUnknown(w.Route))
            return w.Route!;
        return Missing(lang);
    }

    public static string FieldOrMissing(string? value, UiLang lang) =>
        string.IsNullOrWhiteSpace(value) || IsUnknown(value) ? Missing(lang) : value!;

    public static DetailsWorkRow ToRow(WorkView w, string roleLabel, bool historical, UiLang lang, IReadOnlyList<WorkView>? siblings = null)
    {
        var kind = historical ? "history" : (w.ActorKind ?? KindOf(w));
        var status = historical
            ? ConsumerCopy.Localize(StatusLanguage.WorkStatusPhrase(w), lang)
            : WorkStatus(w, lang, siblings);
        var blocker = FieldOrMissing(w.Blocker, lang);
        if (string.Equals(blocker, Missing(lang), StringComparison.Ordinal)
            && status.Contains("失败", StringComparison.Ordinal))
        {
            blocker = status;
        }

        var artifacts = w.Artifacts.Select(a => new DetailsArtifactRow(a.Id, a.Label, a.State, a.Target)).ToList();
        var qualityLabel = historical
            ? ConsumerCopy.T(lang, "history_item")
            : StatusLanguage.DataQualityShort(w.Quality, lang);
        return new DetailsWorkRow(
            w.Id,
            kind,
            roleLabel,
            ActorDisplay(w, lang),
            string.IsNullOrWhiteSpace(w.TaskName) || IsUnknown(w.TaskName)
                ? string.Empty
                : ConsumerCopy.T(lang, "work_label") + w.TaskName,
            ConsumerCopy.T(lang, "model_label") + FieldOrMissing(w.Model, lang),
            ConsumerCopy.T(lang, "effort_label") + FieldOrMissing(w.Effort, lang),
            status,
            string.Equals(blocker, Missing(lang), StringComparison.Ordinal) ? string.Empty : blocker,
            w.Route,
            ConsumerCopy.Localize(StatusLanguage.WorkStatusPhrase(w), lang),
            string.Empty,
            qualityLabel,
            historical,
            artifacts,
            w.Targets);
    }

    public static string FailedItemsStuck(IReadOnlyList<WorkView> current, UiLang lang)
    {
        var failed = current.Where(w =>
            !StatusLanguage.HasLiveHandler(w)
            && string.Equals(w.Axes.Execution, "failed", StringComparison.OrdinalIgnoreCase)
            && KindOf(w) is "executor" or "reviewer" or "legion" or "unknown").ToList();
        if (failed.Count == 0) return string.Empty;
        var names = failed.Select(w => ActorDisplay(w, UiLang.Zh)).Distinct().ToList();
        var zh = names.Count == 1
            ? names[0] + " 当前执行失败，该项未被其他在飞工作覆盖"
            : string.Join("、", names) + " 当前失败，不能被其他执行中工作盖住";
        return ConsumerCopy.Localize(zh, lang);
    }

    static IReadOnlyList<DetailsWorkRow> Numbered(List<WorkView> works, bool historical, UiLang lang)
    {
        var leads = works.Where(w => KindOf(w) == "lead").ToList();
        var executors = works.Where(w => KindOf(w) == "executor").ToList();
        var reviewers = works.Where(w => KindOf(w) == "reviewer").ToList();
        var legion = works.Where(w => KindOf(w) == "legion").ToList();
        var other = works.Where(w => KindOf(w) is not ("lead" or "executor" or "reviewer" or "legion")).ToList();

        var rows = new List<DetailsWorkRow>();
        if (leads.Count > 0)
            rows.Add(ToRow(leads[0], ConsumerCopy.T(lang, "role_lead"), historical, lang, works));
        for (var i = 0; i < executors.Count; i++)
            rows.Add(ToRow(executors[i], string.Format(ConsumerCopy.T(lang, "role_executor_n"), i + 1), historical, lang, works));
        for (var i = 0; i < reviewers.Count; i++)
            rows.Add(ToRow(reviewers[i], string.Format(ConsumerCopy.T(lang, "role_reviewer_n"), i + 1), historical, lang, works));
        for (var i = 0; i < legion.Count; i++)
            rows.Add(ToRow(legion[i], string.Format(ConsumerCopy.T(lang, "role_legion_n"), i + 1), historical, lang, works));
        foreach (var w in other)
            rows.Add(ToRow(w, StatusLanguage.WorkRoleLabel(w.Role), historical, lang, works));
        return rows;
    }

    static string KindOf(WorkView w)
    {
        if (!string.IsNullOrWhiteSpace(w.ActorKind) && w.ActorKind is not "unknown" and not "history")
            return w.ActorKind!;
        var role = w.Role ?? string.Empty;
        if (role.Contains("generation", StringComparison.OrdinalIgnoreCase))
            return "progress";
        if (role.Contains("review", StringComparison.OrdinalIgnoreCase) || role.Contains("审核", StringComparison.Ordinal))
            return "reviewer";
        if (role.Contains("army", StringComparison.OrdinalIgnoreCase)
            || role.Contains("contrib", StringComparison.OrdinalIgnoreCase)
            || role.Contains("军团", StringComparison.Ordinal))
            return "legion";
        if (role.Equals("lead", StringComparison.OrdinalIgnoreCase) || role.Contains("负责人", StringComparison.Ordinal))
            return "lead";
        if (role.Contains("exec", StringComparison.OrdinalIgnoreCase) || role.Contains("generation", StringComparison.OrdinalIgnoreCase))
            return "executor";
        return "unknown";
    }

    static bool IsFoldedLeadAcceptance(WorkView w)
    {
        var role = w.Role ?? string.Empty;
        return role.Contains("lead_acceptance", StringComparison.OrdinalIgnoreCase);
    }

    static bool IsUnknown(string? s) =>
        string.IsNullOrWhiteSpace(s) || s.Equals("unknown", StringComparison.OrdinalIgnoreCase);
}
