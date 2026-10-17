// SPDX-License-Identifier: MPL-2.0
using System.Text;
using CliWatch.Contracts;

namespace CliWatch.Views;

/// <summary>
/// Copyable help summary from the same HUD/details semantic path. Never invents PASS.
/// </summary>
public static class HelpSummaryText
{
    public static string Build(CockpitSnapshot? snapshot, string? selectedProjectId, UiLang lang = UiLang.Zh, string? recipient = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine(ConsumerCopy.T(lang, "summary_help_title"));
        var rec = string.IsNullOrWhiteSpace(recipient)
            ? ConsumerCopy.T(lang, "recipient_blank")
            : recipient.Trim();
        sb.AppendLine(ConsumerCopy.T(lang, "recipient") + rec);
        sb.AppendLine(ConsumerCopy.T(lang, "summary_note"));

        if (snapshot is null)
        {
            sb.AppendLine(ConsumerCopy.T(lang, "no_snapshot"));
            return sb.ToString();
        }

        var hud = HudPresentation.Build(snapshot, selectedProjectId, showHistory: false, StatusLanguage.DefaultHudMaxLen, lang);
        var details = DetailsPresentation.Build(snapshot, selectedProjectId, lang);
        sb.AppendLine(ConsumerCopy.T(lang, "collect_at") + hud.CollectedAtText);
        sb.AppendLine(ConsumerCopy.T(lang, "quality") + hud.QualityText);
        sb.AppendLine(ConsumerCopy.T(lang, "summary_current_n") + hud.CurrentCount
            + ConsumerCopy.L(lang, "（", " (")
            + ConsumerCopy.T(lang, "hist_n") + hud.HistoricalCount
            + ConsumerCopy.L(lang, "）", ")"));
        if (hud.CurrentIssueCount > 0)
            sb.AppendLine(ConsumerCopy.T(lang, "verify_n") + hud.CurrentIssueCount);

        if (string.IsNullOrWhiteSpace(details.SelectedProjectId))
        {
            sb.AppendLine(details.EmptyMessage ?? ConsumerCopy.T(lang, "no_projects"));
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine(ConsumerCopy.T(lang, "project_label") + details.Name + "（" + details.SelectedProjectId + "）");
            if (!string.IsNullOrWhiteSpace(details.WhoDoingWhat))
                sb.AppendLine(ConsumerCopy.T(lang, "who_label") + details.WhoDoingWhat);
            if (!string.IsNullOrWhiteSpace(details.HowFar))
                sb.AppendLine(ConsumerCopy.T(lang, "how_far_label") + details.HowFar);
            if (!string.IsNullOrWhiteSpace(details.StuckAt))
                sb.AppendLine(ConsumerCopy.T(lang, "stuck_label") + details.StuckAt);
            if (!string.IsNullOrWhiteSpace(details.NextOwner))
                sb.AppendLine(ConsumerCopy.T(lang, "next_label") + details.NextOwner);
            if (!string.IsNullOrWhiteSpace(details.NeedsUser))
                sb.AppendLine(ConsumerCopy.T(lang, "need_you_label") + details.NeedsUser);
            if (details.CurrentWorks.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(ConsumerCopy.T(lang, "roles_section"));
                foreach (var row in details.CurrentWorks)
                {
                    sb.AppendLine("- " + row.RoleLabel + " / " + row.ActorName + " / " + row.StatusText
                        + " / " + row.ModelText + " / " + row.EffortText);
                    if (!string.IsNullOrWhiteSpace(row.BlockerText))
                        sb.AppendLine("  " + row.BlockerText);
                }
            }
            if (details.HistoricalWorks.Count > 0)
            {
                sb.AppendLine(ConsumerCopy.T(lang, "history_roles_section"));
                foreach (var row in details.HistoricalWorks)
                    sb.AppendLine("- " + row.RoleLabel + " / " + row.ActorName + " / " + row.StatusText);
            }
            if (!string.IsNullOrWhiteSpace(details.UnassignedReviewText))
                sb.AppendLine(details.UnassignedReviewText);
            if (!string.IsNullOrWhiteSpace(details.Goal)
                && details.Goal is not "未声明" and not "未用一句话写明目标" and not "not stated" and not "no one-line goal")
            {
                sb.AppendLine(ConsumerCopy.T(lang, "goal") + "：" + details.Goal);
            }

            sb.AppendLine(ConsumerCopy.T(lang, "project_quality") + details.ProjectQualityText);
            if (!string.IsNullOrWhiteSpace(details.LastFactAtText))
                sb.AppendLine(ConsumerCopy.T(lang, "last_fact") + details.LastFactAtText);

            var project = snapshot.Projects.FirstOrDefault(p => p.Id == details.SelectedProjectId);
            if (project is not null && project.Attention.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(ConsumerCopy.T(lang, "pending_section"));
                foreach (var item in project.Attention)
                {
                    sb.AppendLine("- [" + OwnerText(item.Owner, lang) + "] "
                        + ConsumerCopy.Localize(item.Description + " — " + item.Reason, lang));
                    foreach (var ev in item.Evidence)
                        AppendEvidence(sb, ev, lang);
                }
            }

            if (project is not null)
            {
                var evidence = new List<EvidenceRef>();
                foreach (var work in project.WorkItems)
                {
                    foreach (var ev in work.Evidence)
                        evidence.Add(ev);
                    foreach (var art in work.Artifacts)
                    {
                        foreach (var ev in art.Evidence)
                            evidence.Add(ev);
                    }
                }

                if (evidence.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine(ConsumerCopy.T(lang, "evidence_section"));
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    var shown = 0;
                    foreach (var ev in evidence)
                    {
                        var key = ev.SourceId + "|" + ev.Location + "|" + ev.Kind;
                        if (!seen.Add(key)) continue;
                        AppendEvidence(sb, ev, lang);
                        shown++;
                        if (shown >= 12) break;
                    }
                }
            }
        }

        if (snapshot.Issues.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(ConsumerCopy.T(lang, "summary_issues"));
            foreach (var issue in snapshot.Issues)
                sb.AppendLine("- " + issue.SourceId + " / " + issue.Code + " / " + issue.Detail);
        }

        return sb.ToString();
    }

    public static bool ContainsInventedPass(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return text.Contains("PASS", StringComparison.Ordinal)
            || text.Contains("全部正常", StringComparison.Ordinal)
            || text.Contains("项目完成", StringComparison.Ordinal)
            || text.Contains("官方通过", StringComparison.Ordinal)
            || text.Contains("all normal", StringComparison.OrdinalIgnoreCase)
            || text.Contains("officially passed", StringComparison.OrdinalIgnoreCase);
    }

    static void AppendEvidence(StringBuilder sb, EvidenceRef ev, UiLang lang)
    {
        var fact = ev.FactAt is { } t
            ? t.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss") + " CST"
            : ConsumerCopy.T(lang, "none");
        sb.AppendLine("  · " + ev.SourceId + " " + ev.Location + " (" + ev.Kind + ") "
            + ConsumerCopy.T(lang, "fact_time_eq") + fact + " "
            + ConsumerCopy.T(lang, "quality_eq") + StatusLanguage.DataQualityShort(ev.Quality, lang));
    }

    static string OwnerText(AttentionOwner owner, UiLang lang) => owner switch
    {
        AttentionOwner.User => ConsumerCopy.T(lang, "owner_user"),
        AttentionOwner.Lead => ConsumerCopy.T(lang, "owner_lead"),
        AttentionOwner.Secretary => ConsumerCopy.T(lang, "owner_secretary"),
        _ => ConsumerCopy.T(lang, "owner_none")
    };
}
