// SPDX-License-Identifier: MPL-2.0
using System.Text;
using CliWatch.Contracts;
using CliWatch.Projection;

namespace CliWatch.Views;

/// <summary>
/// Pure presentation helpers: compose short Chinese status lines from snapshot facts only.
/// Does not invent completion / PASS / all-green; unknown stays unknown; axes stay independent.
/// </summary>
public static class StatusLanguage
{
    public const int DefaultHudMaxLen = 96;

    public static string SnapshotQualityText(DataQuality quality, UiLang lang = UiLang.Zh) =>
        quality == DataQuality.Partial ? ConsumerCopy.L(lang, "本轮已采集，部分来源受限", "Collected this refresh; some sources limited") : ConsumerCopy.Localize(quality switch
        {
            DataQuality.Fresh => "来源可读",
            DataQuality.Partial => "本轮已采集，部分来源受限",
            DataQuality.LastKnown => "沿用上次已知（非本轮新鲜采集）",
            DataQuality.Unavailable => "来源不可用或缺失",
            DataQuality.Conflicting => "来源冲突，结论待核实",
            _ => "来源状态未知"
        }, lang);

    public static string DataQualityShort(DataQuality quality, UiLang lang = UiLang.Zh) =>
        quality == DataQuality.Partial ? ConsumerCopy.L(lang, "本轮部分可读", "Partly readable this refresh") : ConsumerCopy.Localize(quality switch
        {
            DataQuality.Fresh => "新鲜",
            DataQuality.Partial => "本轮部分可读",
            DataQuality.LastKnown => "历史/上次已知",
            DataQuality.Unavailable => "不可用",
            DataQuality.Conflicting => "冲突",
            _ => "未知"
        }, lang);

    public static (string Label, string Basis) OverallJudgment(ProjectView project, UiLang lang = UiLang.Zh)
    {
        if (CliWatchCopy.TryJudgment(project, lang, out var watch))
            return watch;
        var current = project.WorkItems.Where(w => !IsHistoricalWork(w) && !IsDiagnosticNoise(w)).ToList();
        string L(string zh) => ConsumerCopy.Localize(zh, lang);
        if (project.IsPaused)
            return (L("已暂停"), L("项目已暂停"));
        if (!project.IsActive)
            return (L("已完成"), L("项目已结束；结果可在历史中查看"));
        if (project.Attention.Any(a => a.Owner == AttentionOwner.User))
            return (L("需要你处理"), L("当前有需要你决定的事项"));

        var execs = current.Where(IsExecutionLike).ToList();
        var failed = execs.Where(w =>
            IsFailed(w.Axes.Execution)
            && !HasLiveHandler(w)
            && !IsConsumedHistoricalHandling(w.Axes.LeadHandling)).ToList();
        if (failed.Count > 0)
        {
            var phrase = WorkStatusPhrase(failed[0]);
            return (L("故障/部分异常"), L(string.IsNullOrWhiteSpace(phrase) ? "当前执行失败，待处理" : phrase + "；由原负责人处理"));
        }

        if (current.Any(w =>
                (w.Axes.LeadHandling ?? "").Equals("lead_accepting", StringComparison.OrdinalIgnoreCase)
                || (w.Axes.Acceptance ?? "").Equals("acceptance_in_progress", StringComparison.OrdinalIgnoreCase)))
        {
            return (L("等待验收"), L("结果已交回，负责人正在验收"));
        }

        var running = execs.Any(w => IsActive(w.Axes.Execution));
        if (running)
            return (L("正常推进"), L("原执行者正在做，本轮还没交回"));

        if (execs.Any(w => (w.Axes.LeadHandling ?? "").Equals("repair_dispatched", StringComparison.OrdinalIgnoreCase)))
            return (L("等待结果"), L("退修已派出，等待本轮结果"));
        if (execs.Any(w => (w.Axes.LeadHandling ?? "").Equals("repair_prepared", StringComparison.OrdinalIgnoreCase)))
            return (L("等待结果"), L("准备退修续接"));
        if (execs.Any(w => (w.Axes.LeadHandling ?? "").Equals("dispatch_pending_native", StringComparison.OrdinalIgnoreCase))
            || current.Any(w => (w.Axes.LeadHandling ?? "").Equals("callback_wait", StringComparison.OrdinalIgnoreCase)))
            return (L("等待结果"), L("已派出，等待本轮结果证据"));

        if (execs.Any(w =>
                w.Axes.Execution.Equals("returned", StringComparison.OrdinalIgnoreCase)
                || w.Axes.Execution.Equals("succeeded", StringComparison.OrdinalIgnoreCase)))
        {
            return (L("等待验收"), L("结果已交回，等待负责人处理"));
        }

        return (L("状态待核实"), L("缺少足够来源"));
    }

    public static string AttentionOwnerLabel(AttentionOwner owner, UiLang lang = UiLang.Zh) =>
        ConsumerCopy.Localize(owner switch
        {
            AttentionOwner.User => "待你决定",
            AttentionOwner.Secretary => "秘书关注",
            AttentionOwner.Lead => "原Lead待处理",
            _ => "无指定负责人"
        }, lang);

    public static string OrUndeclared(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "未声明" : value.Trim();

    public static string Truncate(string text, int maxLen)
    {
        if (string.IsNullOrEmpty(text) || maxLen <= 0) return string.Empty;
        if (text.Length <= maxLen) return text;
        if (maxLen <= 1) return "…";
        return text[..(maxLen - 1)] + "…";
    }

    public static bool IsHistoricalWork(WorkView work)
    {
        if (work.Quality == DataQuality.LastKnown) return true;
        return work.Summary.StartsWith("[historical]", StringComparison.OrdinalIgnoreCase);
    }

    public static bool LooksLikePassClaim(string text) =>
        text.Contains("PASS", StringComparison.OrdinalIgnoreCase)
        || text.Contains("全部正常", StringComparison.Ordinal)
        || text.Contains("项目完成", StringComparison.Ordinal)
        || text.Contains("业务完成", StringComparison.Ordinal)
        || text.Contains("目标完成", StringComparison.Ordinal);

    /// <summary>One-line HUD status for a project; excludes LastKnown works as current faults.</summary>
    public static string ProjectOneLiner(ProjectView project, int maxLen = DefaultHudMaxLen) =>
        ProjectOneLiner(project, maxLen, UiLang.Zh);

    public static string ProjectOneLiner(ProjectView project, int maxLen, UiLang lang)
    {
        if (CliWatchCopy.IsWatch(project))
            return Truncate(CliWatchCopy.OneLiner(project, lang), maxLen);
        var parts = new List<string>();

        if (project.Quality == DataQuality.Unavailable)
            parts.Add("来源不可用");
        else if (project.Quality == DataQuality.Conflicting)
            parts.Add("来源冲突，待核实");

        // Freshness is a single independent hint, not mixed into the status sentence.

        if (project.IsPaused)
            parts.Add("主动暂停");

        var user = project.Attention.Where(a => a.Owner == AttentionOwner.User).ToList();
        if (user.Count > 0)
            parts.Add("待你决定");

        var current = project.WorkItems.Where(w => !IsHistoricalWork(w) && !IsDiagnosticNoise(w)).ToList();
        var historical = project.WorkItems.Where(IsHistoricalWork).ToList();

        var exec = current.Where(w => IsExecutionLike(w) && !IsGenerationInFlight(w)).ToList();
        var awExec = exec.Where(IsActiveWorkBacked).ToList();
        if (awExec.Count > 0) exec = awExec;
        var liveExec = exec.Where(HasLiveHandler).ToList();
        if (liveExec.Count > 0) exec = liveExec;
        else
        {
            var inFlight = exec.Where(w => IsActive(w.Axes.Execution) && !IsComplete(w.Axes.Transport)).ToList();
            if (inFlight.Count > 0) exec = inFlight;
        }
        var gen = current.Where(IsGenerationInFlight).ToList();
        var contrib = current.Where(w => IsContributionLike(w)).ToList();
        var other = current.Where(w =>
            !IsExecutionLike(w)
            && !IsContributionLike(w)
            && !IsGenerationInFlight(w)
            && !IsDiagnosticNoise(w)
            && !w.Role.Contains("lead_acceptance", StringComparison.OrdinalIgnoreCase)
            && !(w.Role.Equals("cli", StringComparison.OrdinalIgnoreCase))).ToList();
        var generationNow = gen.Count > 0 || (project.Progress?.Basis?.Contains("世界", StringComparison.Ordinal) == true);

        if (generationNow)
        {
            parts.Add(GenerationPhrase(gen.FirstOrDefault() ?? exec.FirstOrDefault() ?? current.FirstOrDefault()
                ?? new WorkView("gen", "generation", null, "生成", new WorkAxes("unknown", "unknown", "active", "unknown", "unknown", "unknown", "unknown", "unknown", "unknown", "unknown"), Array.Empty<ArtifactView>(), Array.Empty<NavigationTarget>(), Array.Empty<EvidenceRef>(), DataQuality.Fresh),
                project.Progress));
        }
        else if (exec.Count > 0)
        {
            var phrase = ComposeWorkClusterPhrase(exec);
            if (!string.IsNullOrWhiteSpace(phrase)) parts.Add(phrase);
        }

        if (contrib.Count > 0)
        {
            var pending = contrib.Count(c => IsPendingLead(c));
            var adopted = contrib.Count(c => IsAdopted(c));
            var accepted = contrib.Count(c => IsAccepted(c) && !IsAdopted(c));
            // Count only. Internal AR/GUID identities stay in optional diagnostics.
            if (pending > 0) parts.Add($"另有 {pending} 份军团贡献待处理");
            else if (accepted > 0) parts.Add($"另有 {accepted} 份军团贡献已接受");
            else if (adopted > 0) parts.Add($"另有 {adopted} 份军团贡献已采用");
            else parts.Add($"另有 {contrib.Count} 份军团贡献");
        }

        foreach (var w in other)
        {
            var p = ComposeSingleWorkPhrase(w);
            if (IsConsumerPhrase(p)) parts.Add(p);
        }

        var leadAtt = project.Attention.Count(a => a.Owner == AttentionOwner.Lead && IsPendingLeadAttention(a));
        var secAtt = project.Attention.Count(a => a.Owner == AttentionOwner.Secretary);
        if (!generationNow
            && leadAtt > 0
            && !parts.Any(p => p.Contains("退修", StringComparison.Ordinal) || p.Contains("待处理", StringComparison.Ordinal) || p.Contains("待验收", StringComparison.Ordinal) || p.Contains("待Lead", StringComparison.Ordinal)))
            parts.Add($"原Lead待处理/待验收 {leadAtt}");
        if (secAtt > 0)
            parts.Add($"秘书关注 {secAtt}");

        if (historical.Count > 0 && exec.Count == 0 && contrib.Count == 0 && other.Count == 0 && !project.IsPaused)
        {
            // History only — do not present as current fault
            parts.Add($"{historical.Count} 条历史记录可追溯");
        }

        if (parts.Count == 0)
        {
            var summary = project.Summary?.Trim();
            if (!string.IsNullOrWhiteSpace(summary) && !LooksLikePassClaim(summary))
                parts.Add(summary);
            else if (!string.IsNullOrWhiteSpace(project.Phase) && project.Phase != "unknown")
                parts.Add(HumanizeToken(project.Phase));
            else
                parts.Add("暂无更细当前事实");
        }

        var joined = string.Join(" · ", parts
            .Select(CleanConsumerPhrase)
            .Where(IsConsumerPhrase)
            .Distinct());
        // Never invent PASS in the composed line
        if (LooksLikePassClaim(joined) && project.WorkItems.All(w => !IsGoalComplete(w)))
        {
            joined = joined
                .Replace("PASS", "（未宣称通过）", StringComparison.OrdinalIgnoreCase)
                .Replace("全部正常", "状态待核实", StringComparison.Ordinal)
                .Replace("项目完成", "目标轴未证实完成", StringComparison.Ordinal)
                .Replace("业务完成", "目标轴未证实完成", StringComparison.Ordinal)
                .Replace("目标完成", "目标轴未证实完成", StringComparison.Ordinal);
        }

        return Truncate(ConsumerCopy.Localize(joined, lang), maxLen);
    }

    public static string ProjectOneLinerFull(ProjectView project, UiLang lang) => ProjectOneLiner(project, int.MaxValue, lang);

    public static string ProjectOneLinerFull(ProjectView project) => ProjectOneLiner(project, int.MaxValue, UiLang.Zh);

    public static string WorkAxesHumanPhrase(WorkAxes axes)
    {
        var bits = new List<string>();
        AddAxis(bits, "进程", axes.Process);
        AddAxis(bits, "回合", axes.Turn);
        AddAxis(bits, "执行", axes.Execution);
        AddAxis(bits, "运输", axes.Transport);
        AddAxis(bits, "交付", axes.Delivery);
        AddAxis(bits, "回叫", axes.Callback);
        AddAxis(bits, "Lead处理", axes.LeadHandling);
        AddAxis(bits, "验收", axes.Acceptance);
        AddAxis(bits, "采用", axes.Adoption);
        AddAxis(bits, "目标", axes.Goal);
        return bits.Count == 0 ? "各轴均未知" : string.Join("；", bits);
    }

    public static string AxisHuman(string axisLabel, string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            return $"{axisLabel}：未知";
        return $"{axisLabel}：{HumanizeToken(raw)}";
    }

    public static string WorkRoleLabel(string role)
    {
        if (string.IsNullOrWhiteSpace(role) || role.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            return "未声明角色";
        return HumanizeToken(role);
    }

    public static string ProgressText(ProgressView? progress)
    {
        if (progress is null) return string.Empty;
        if (string.IsNullOrWhiteSpace(progress.Basis)) return string.Empty;
        if (progress.Basis.Contains("世界", StringComparison.Ordinal))
        {
            if (progress.Total > 0)
                return $"已到第{progress.Completed}天 / 目标{progress.Total}天（依据：{progress.Basis}）";
            if (progress.Completed > 0)
                return $"已到第{progress.Completed}天（依据：{progress.Basis}）";
        }
        if (progress.Total <= 0) return string.Empty;
        return $"{progress.Completed}/{progress.Total}（依据：{progress.Basis}）";
    }

    public static string CollectedAtText(DateTimeOffset collectedAt) =>
        collectedAt.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss") + " CST";

    public static string IssuesSummary(IReadOnlyList<SourceIssue> issues, int maxItems = 3) =>
        IssuesSummary(issues, snapshot: null, maxItems, UiLang.Zh);

    public static string IssuesSummary(IReadOnlyList<SourceIssue> issues, CockpitSnapshot? snapshot, int maxItems = 3, UiLang lang = UiLang.Zh)
    {
        if (issues.Count == 0) return string.Empty;
        var historical = CurrentList.HistoricalIssues(issues).Count;
        var global = CurrentList.CurrentIssues(issues).Count(i => IsObservationLimit(i) && !i.Code.Equals("sample_banner", StringComparison.OrdinalIgnoreCase));
        var owned = OwnedInstanceGaps(issues, snapshot).Count;
        var lines = new List<string>();
        if (historical > 0)
            lines.Add(ConsumerCopy.L(lang,
                "历史来源缺口 " + historical + " 条，不影响当前执行。",
                "Historical source gaps: " + historical + ". They do not affect the current execution."));
        if (global > 0)
            lines.Add(ConsumerCopy.L(lang,
                "本轮观察受限，未读完的来源不代表有新的当前任务。",
                "This refresh is limited. Unread sources do not add a current task."));
        if (owned > 0)
            lines.Add(ConsumerCopy.L(lang,
                "当前执行实例有信息未知，不另计为任务。",
                "A current execution instance has unknown detail. That is not an extra task."));
        return string.Join(" ", lines);
    }

    /// <summary>
    /// Group count of the current window, plus the unbound instance count when that group has work items.
    /// History stays the existing non-current rows. Source issues are not instances.
    /// </summary>
    public static string CurrentWindowLine(CockpitSnapshot snapshot, UiLang lang = UiLang.Zh)
    {
        var current = CurrentList.DefaultItems(snapshot);
        var groups = current.Count;
        var history = CurrentList.HistoricalItems(snapshot).Count;
        var unbound = current
            .Where(p => string.Equals(p.Phase, CliWatchStatus.Unbound, StringComparison.Ordinal))
            .Sum(p => p.WorkItems.Count(w => !IsHistoricalWork(w)));
        if (lang == UiLang.En)
        {
            var groupText = groups == 1 ? "1 group" : groups + " groups";
            var line = "current " + groupText;
            if (unbound > 0)
                line += " · " + (unbound == 1 ? "1 unbound instance" : unbound + " unbound instances");
            return line + " · history " + history;
        }

        var zh = "当前 " + groups + " 组";
        if (unbound > 0)
            zh += " · 未关联 " + unbound + " 个实例";
        return zh + " · 历史 " + history;
    }

    public static string UnboundObservation(ProjectView project, UiLang lang = UiLang.Zh)
    {
        var n = project.WorkItems.Count(w => !IsHistoricalWork(w));
        if (lang == UiLang.En)
        {
            var processes = n == 1 ? "1 CLI process is alive" : n + " CLI processes are alive";
            return "Observed " + processes + ". Task association, whether a task is in progress, and waiting for input are not confirmed.";
        }

        return "观察到 " + n + " 个 CLI 进程存活；任务关联、是否在处理任务或等待输入尚未确认。";
    }

    public static string RefreshStatusLine(CockpitSnapshot snapshot, UiLang lang = UiLang.Zh)
    {
        var issueN = CurrentIssueCount(snapshot.Issues, snapshot);
        var counts = CurrentWindowLine(snapshot, lang);
        if (lang == UiLang.En)
        {
            var en = "Refreshed " + CollectedAtText(snapshot.CollectedAt)
                + ". " + SnapshotQualityText(snapshot.Quality, lang)
                + " " + counts;
            return issueN > 0 ? en + " source gaps=" + issueN : en;
        }

        var zh = "已刷新 " + CollectedAtText(snapshot.CollectedAt)
            + "。" + SnapshotQualityText(snapshot.Quality, UiLang.Zh)
            + " " + counts;
        return issueN > 0 ? zh + " 来源缺口=" + issueN : zh;
    }

    public static int CurrentIssueCount(IEnumerable<SourceIssue> issues) =>
        CurrentIssueCount(issues, snapshot: null);

    public static int CurrentIssueCount(IEnumerable<SourceIssue> issues, CockpitSnapshot? snapshot) =>
        OwnedInstanceGaps(issues, snapshot).Count;

    public static IReadOnlyList<SourceIssue> VerifyUnknownIssues(IEnumerable<SourceIssue> issues, CockpitSnapshot? snapshot) =>
        OwnedInstanceGaps(issues, snapshot);

    static List<SourceIssue> OwnedInstanceGaps(IEnumerable<SourceIssue> issues, CockpitSnapshot? snapshot) =>
        CurrentList.CurrentIssues(issues)
            .Where(i => !IsObservationLimit(i)
                        && !i.Code.Equals("sample_banner", StringComparison.OrdinalIgnoreCase)
                        && !IsAwaitingReturn(i, snapshot)
                        && FollowsCurrentInstance(i, snapshot))
            .ToList();

    static bool IsObservationLimit(SourceIssue issue) =>
        issue.Code.Equals("scan_budget_exhausted", StringComparison.OrdinalIgnoreCase)
        || issue.Code.Equals("process_snapshot_failed", StringComparison.OrdinalIgnoreCase)
        || issue.Code.Equals("current_scope_unavailable", StringComparison.OrdinalIgnoreCase)
        || issue.Code.Equals("grok_session_missing", StringComparison.OrdinalIgnoreCase)
        || issue.Code.Equals("mailbox_jobs_unreadable", StringComparison.OrdinalIgnoreCase)
        || issue.Code.Equals("mailbox_root_missing", StringComparison.OrdinalIgnoreCase)
        || issue.Code.Equals("mailbox_roots_empty", StringComparison.OrdinalIgnoreCase)
        || issue.Code.Equals("mailbox_jobs_missing", StringComparison.OrdinalIgnoreCase)
        || issue.Code.Equals("too_large", StringComparison.OrdinalIgnoreCase)
        || issue.Code.Equals("prompt_binding_store_unreadable", StringComparison.OrdinalIgnoreCase)
        || issue.Code.Equals("prompt_binding_store_unwritable", StringComparison.OrdinalIgnoreCase);

    static bool FollowsCurrentInstance(SourceIssue issue, CockpitSnapshot? snapshot)
    {
        if (snapshot is null || string.IsNullOrWhiteSpace(issue.JobKey)) return false;
        return snapshot.Projects.Any(p => p.IsActive && !p.IsPaused
            && (p.Id.Equals(issue.JobKey, StringComparison.OrdinalIgnoreCase)
                || (p.ActivityIdentity ?? string.Empty).EndsWith(issue.JobKey, StringComparison.OrdinalIgnoreCase)));
    }

    public static bool IsNormalInFlight(ProjectView project)
    {
        if (project.IsPaused) return false;
        var current = project.WorkItems.Where(w => !IsHistoricalWork(w) && !IsDiagnosticNoise(w)).ToList();
        if (current.Any(w => (w.Axes.LeadHandling ?? "").Equals("blocked_after_acceptance", StringComparison.OrdinalIgnoreCase)))
            return false;
        if (current.Any(w => IsExecutionLike(w) && IsFailed(w.Axes.Execution) && !HasLiveHandler(w)))
            return false;
        return current.Any(w =>
            IsGenerationInFlight(w)
            || (IsExecutionLike(w)
                && (IsActive(w.Axes.Execution)
                    || (w.Axes.LeadHandling ?? "").Equals("repair_dispatched", StringComparison.OrdinalIgnoreCase))));
    }

    public static bool IsAwaitingReturn(SourceIssue issue, CockpitSnapshot? snapshot)
    {
        if (snapshot is null) return false;
        if (CurrentList.IsHistoricalIssue(issue)) return false;
        if (!issue.Code.Equals("missing", StringComparison.OrdinalIgnoreCase)) return false;
        var parsed = ParseIssue(issue);
        if (parsed.Unread) return false;
        if (parsed.Axis is "acceptance" or "latest_acceptance" or "last_native_acceptance" or "acceptance_method")
            return false;

        var receiptDuty = LooksLikeRoundReceipt(issue.SourceId);
        var returnable =
            parsed.Axis is "current_result" or "expected_result" or "line_receipt" or "route_receipt"
                or "direct_receipt" or "telephone_receipt"
            || (parsed.Axis == "native_identity_source" && receiptDuty);
        if (!returnable) return false;

        var project = snapshot.Projects.FirstOrDefault(p =>
            string.Equals(p.Id, parsed.Project, StringComparison.Ordinal));
        if (project is null) return false;

        var associated = FindAssociatedWorks(issue, project);
        if (associated.Count > 0)
            return associated.Any(WorkStillWaitingForReturn);

        if (parsed.Axis is "current_result" or "expected_result")
            return IsNormalInFlight(project);
        return false;
    }

    public static bool WorkStillWaitingForReturn(WorkView w)
    {
        if (IsHistoricalWork(w)) return false;
        if (HasLiveHandler(w)) return false;
        if (IsGenerationInFlight(w)) return true;
        if (IsActive(w.Axes.Execution)) return true;
        if ((w.Axes.LeadHandling ?? "").Equals("repair_dispatched", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    public static bool LooksLikeRoundReceipt(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var n = path.Replace('/', '\\');
        if (!n.EndsWith("receipt.json", StringComparison.OrdinalIgnoreCase)) return false;
        return n.Contains("\\jobs\\", StringComparison.OrdinalIgnoreCase);
    }

    public static bool AssociatedWithCurrentRound(SourceIssue issue, ProjectView project) =>
        FindAssociatedWorks(issue, project).Any(WorkStillWaitingForReturn);

    static IReadOnlyList<WorkView> FindAssociatedWorks(SourceIssue issue, ProjectView project)
    {
        var path = issue.SourceId ?? "";
        var guids = ExtractGuids(path);
        var list = new List<WorkView>();
        foreach (var w in project.WorkItems)
        {
            if (IsHistoricalWork(w)) continue;
            var blob = WorkBlob(w);
            if (path.Length > 8 && blob.Contains(path, StringComparison.OrdinalIgnoreCase))
            {
                list.Add(w);
                continue;
            }

            if (guids.Any(g => blob.Contains(g, StringComparison.OrdinalIgnoreCase)))
                list.Add(w);
        }

        return list;
    }

    static string WorkBlob(WorkView w)
    {
        var parts = new List<string> { w.Id, w.Summary };
        foreach (var e in w.Evidence)
        {
            parts.Add(e.SourceId);
            parts.Add(e.Location);
        }

        foreach (var a in w.Artifacts)
        {
            parts.Add(a.Id);
            if (a.Target is { } t) parts.Add(t.Target);
        }

        return string.Join("\n", parts);
    }

    static List<string> ExtractGuids(string? text)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(text)) return list;
        for (var i = 0; i + 36 <= text.Length; i++)
        {
            if (text[i + 8] != '-' || text[i + 13] != '-' || text[i + 18] != '-' || text[i + 23] != '-')
                continue;
            var span = text.AsSpan(i, 36);
            var ok = true;
            for (var j = 0; j < 36; j++)
            {
                if (j is 8 or 13 or 18 or 23) continue;
                if (!IsHexChar(span[j])) { ok = false; break; }
            }

            if (!ok) continue;
            list.Add(text.Substring(i, 36));
            i += 35;
        }

        return list;
    }

    static bool IsHexChar(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    static string HandlerLabel(CockpitSnapshot? snapshot, string projectId)
    {
        if (snapshot is null || string.IsNullOrWhiteSpace(projectId))
            return "处理者未指定";
        var project = snapshot.Projects.FirstOrDefault(p => p.Id == projectId);
        if (project is null) return "处理者未指定";
        if (project.Attention.Any(a => a.Owner == AttentionOwner.User)) return "待你决定";
        if (project.Attention.Any(a => a.Owner == AttentionOwner.Lead)) return "原负责人";
        if (project.Attention.Any(a => a.Owner == AttentionOwner.Secretary)) return "秘书关注";
        return "处理者未指定";
    }

    public static string HumanIssue(SourceIssue issue)
    {
        var parsed = ParseIssue(issue);
        if (parsed.Unread)
        {
            var who = string.IsNullOrWhiteSpace(parsed.Who) ? "" : parsed.Who;
            return who + "有文件已存在但暂不能读取这些状态，影响：不能从该文件补充当前状态";
        }

        if (issue.Code.Equals("missing", StringComparison.OrdinalIgnoreCase)
            || issue.Code.Equals("historical_source_missing", StringComparison.OrdinalIgnoreCase))
        {
            var prefix = string.IsNullOrWhiteSpace(parsed.Who) ? parsed.What : parsed.Who + parsed.What;
            if (parsed.What.Contains("必要身份", StringComparison.Ordinal))
                return prefix + "无法确认，影响：" + parsed.Impact;
            if (parsed.What.Contains("结果", StringComparison.Ordinal))
                return prefix + "尚未产出，影响：" + parsed.Impact;
            if (parsed.What.Contains("回执", StringComparison.Ordinal))
                return prefix + "尚未交回，影响：" + parsed.Impact;
            return $"缺{prefix}，影响：{parsed.Impact}";
        }

        if (issue.Code.Contains("conflict", StringComparison.OrdinalIgnoreCase))
            return "来源冲突：" + Truncate(parsed.Message, 60);
        var label = string.IsNullOrWhiteSpace(parsed.Who) ? parsed.What : parsed.Who + parsed.What;
        return label + "：" + Truncate(parsed.Message, 60);
    }

    private static (string Who, string What, string Impact, string Message, bool Missing, bool Unread, string Project, string Axis) ParseIssue(SourceIssue issue)
    {
        var detail = issue.Detail ?? string.Empty;
        var message = detail;
        var axis = "";
        var project = "";
        foreach (var part in detail.Split(';', StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("axis=", StringComparison.OrdinalIgnoreCase))
                axis = part["axis=".Length..];
            else if (part.StartsWith("project=", StringComparison.OrdinalIgnoreCase))
                project = part["project=".Length..];
            else if (!part.StartsWith("scope=", StringComparison.OrdinalIgnoreCase) && part.Length > 0)
                message = part.Trim();
        }

        var who = string.IsNullOrWhiteSpace(project) || project == "unspecified" ? "" : project;
        var receiptDuty = LooksLikeRoundReceipt(issue.SourceId);
        var what = axis switch
        {
            "native_identity_source" when receiptDuty => "本轮执行回执",
            "native_identity_source" => "当前必要身份",
            "line_receipt" or "telephone_receipt" => "本轮回执",
            "route_receipt" or "direct_receipt" => "本轮执行回执",
            "acceptance" or "latest_acceptance" or "last_native_acceptance" => "本轮验收结论",
            "acceptance_method" => "本轮验收方式",
            "generation_heartbeat" => "生成心跳",
            "prepared_next_config" => "当前生成配置",
            "current_result" or "expected_result" => "本轮结果文件",
            _ => string.IsNullOrWhiteSpace(axis) || axis == "source"
                ? "来源"
                : axis.All(static c => c < 128) ? "一项来源" : axis
        };
        var impact = axis switch
        {
            "native_identity_source" when receiptDuty => "不能把本轮显示成已交回",
            "native_identity_source" => "不能确认本轮执行所依据的身份",
            "line_receipt" or "telephone_receipt" or "route_receipt" or "direct_receipt"
                => "不能把本轮显示成已交回",
            "current_result" => "不能把本轮显示成已交付",
            "acceptance" or "acceptance_method" => "本轮验收结论以已读到的文件为准",
            _ => "该项判断暂缺"
        };
        var missing = issue.Code.Equals("missing", StringComparison.OrdinalIgnoreCase)
                      || issue.Code.Equals("historical_source_missing", StringComparison.OrdinalIgnoreCase);
        var unread = issue.Code.Equals("unknown_kind", StringComparison.OrdinalIgnoreCase);
        return (who, what, impact, message, missing, unread, project, axis);
    }

    public static string AttentionLine(AttentionItem item) =>
        Truncate($"{item.Description}（{item.Reason}）", DefaultHudMaxLen);

    public static string AttentionLineFull(AttentionItem item) =>
        $"{item.Description}（原因：{item.Reason}）";

    private static bool IsPendingLeadAttention(AttentionItem item)
    {
        var reason = item.Reason ?? string.Empty;
        if (reason.StartsWith("lead_handled", StringComparison.OrdinalIgnoreCase))
            return false;
        var desc = item.Description ?? string.Empty;
        return !desc.StartsWith("已处理", StringComparison.Ordinal);
    }

    private static void AddAxis(List<string> bits, string label, string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            return;
        bits.Add($"{label} {HumanizeToken(raw)}");
    }

    private static string ComposeWorkClusterPhrase(IReadOnlyList<WorkView> works)
    {
        if (works.Count == 0) return string.Empty;
        var blocker = works.Select(ComposeSingleWorkPhrase).FirstOrDefault(p => p.Contains("平台限制", StringComparison.Ordinal) && IsConsumerPhrase(p));
        if (!string.IsNullOrWhiteSpace(blocker)) return blocker;
        var accepting = works.Select(ComposeSingleWorkPhrase).FirstOrDefault(p => p.Contains("负责人正在验收", StringComparison.Ordinal) && IsConsumerPhrase(p));
        if (!string.IsNullOrWhiteSpace(accepting)) return accepting;
        var repair = works.Select(ComposeSingleWorkPhrase).FirstOrDefault(p => p.Contains("退修", StringComparison.Ordinal) && IsConsumerPhrase(p));
        if (!string.IsNullOrWhiteSpace(repair)) return repair;
        if (works.Count == 1) return ComposeSingleWorkPhrase(works[0]);

        var phrases = works.Select(ComposeSingleWorkPhrase).Where(IsConsumerPhrase).Distinct().ToList();
        if (phrases.Count == 0) return $"{works.Count} 项并发工作";
        if (phrases.Count == 1) return phrases[0];
        return string.Join("；", phrases.Take(2)) + (phrases.Count > 2 ? "…" : string.Empty);
    }

    public static string WorkStatusPhrase(WorkView work) => ComposeSingleWorkPhrase(work);

    public static string ReviewerStatusPhrase(WorkView work)
    {
        var a = work.Axes;
        if (IsFailed(a.Execution))
            return "当前执行失败，待处理";
        if (string.Equals(a.Acceptance, "content_not_accepted", StringComparison.OrdinalIgnoreCase))
            return "内容未验收";
        if (IsExplicitAccepted(a.Acceptance))
            return IsAdoptedValue(a.Adoption) ? "已接受并已采用" : "已验收";
        if (a.Execution.Equals("active", StringComparison.OrdinalIgnoreCase)
            || a.Execution.Equals("running", StringComparison.OrdinalIgnoreCase))
            return "正在审核";
        if (IsComplete(a.Execution) || IsComplete(a.Delivery) || IsComplete(a.Transport))
            return "结果已交回，等待负责人处理";
        if (HasReviewAssignment(work))
            return "已安排审核；当前状态未获取";
        return "未获取";
    }

    public static bool HasReviewAssignment(WorkView work)
    {
        if (string.Equals(work.ReviewAssigned, "true", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(work.ReviewAssigned, "false", StringComparison.OrdinalIgnoreCase))
            return false;
        var role = work.Role ?? string.Empty;
        return role.Contains("review", StringComparison.OrdinalIgnoreCase)
               || role.Contains("审核", StringComparison.Ordinal)
               || string.Equals(work.ActorKind, "reviewer", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsExplicitAccepted(string? s)
    {
        if (IsUnknown(s)) return false;
        var t = s!.Trim();
        if (t.Contains("not_accept", StringComparison.OrdinalIgnoreCase)
            || t.Contains("unaccept", StringComparison.OrdinalIgnoreCase)
            || t.Contains("pending", StringComparison.OrdinalIgnoreCase)
            || t.Contains("in_progress", StringComparison.OrdinalIgnoreCase)
            || t.Contains("fail", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return t.Equals("handled_accepted", StringComparison.OrdinalIgnoreCase)
               || t.Equals("accepted", StringComparison.OrdinalIgnoreCase)
               || t.Equals("accepted_partial_or_full", StringComparison.OrdinalIgnoreCase)
               || t.Equals("已验收", StringComparison.Ordinal)
               || t.Equals("已接受", StringComparison.Ordinal);
    }

    private static string ComposeSingleWorkPhrase(WorkView work)
    {
        if ((work.Route ?? "").StartsWith("watch:", StringComparison.Ordinal))
            return CliWatchCopy.WorkPhrase(work, UiLang.Zh);
        if (IsReviewerWork(work))
            return ReviewerStatusPhrase(work);

        var a = work.Axes;
        if ((a.LeadHandling ?? "").Equals("blocked_after_acceptance", StringComparison.OrdinalIgnoreCase)
            || (a.LeadHandling ?? "").Contains("blocked_after_acceptance", StringComparison.OrdinalIgnoreCase))
        {
            return "结果已交回，负责人已判退修；后续处理被平台限制中断，退修尚未派出";
        }

        if ((a.LeadHandling ?? "").Equals("callback_wait", StringComparison.OrdinalIgnoreCase))
            return "等待回叫";

        if ((a.LeadHandling ?? "").Equals("dispatch_pending_native", StringComparison.OrdinalIgnoreCase))
            return "已派出，等待原生运行/本轮结果证据";

        if ((a.LeadHandling ?? "").Equals("repair_prepared", StringComparison.OrdinalIgnoreCase))
            return "准备退修续接";

        if ((a.LeadHandling ?? "").Equals("repair_dispatched", StringComparison.OrdinalIgnoreCase))
        {
            if (IsActive(a.Execution))
                return "结果已交回；负责人已判退修，原执行者正在按退修继续做";
            return "退修已派出，等待本轮结果";
        }

        if ((a.LeadHandling ?? "").Equals("lead_accepting", StringComparison.OrdinalIgnoreCase)
            || (a.Acceptance ?? "").Equals("acceptance_in_progress", StringComparison.OrdinalIgnoreCase))
        {
            return "结果已交回，负责人正在验收";
        }

        // Prefer factual short phrases; never promote receipt/transport to goal PASS
        if ((a.LeadHandling ?? "").Contains("quota", StringComparison.OrdinalIgnoreCase)
            || (a.Acceptance ?? "").Equals("content_not_accepted", StringComparison.OrdinalIgnoreCase))
        {
            if ((a.LeadHandling ?? "").Contains("resumed", StringComparison.OrdinalIgnoreCase))
                return "已处理（额度失败，资源已另派）；内容未验收";
            return "已处理（额度失败）；内容未验收";
        }

        if ((a.Acceptance ?? "").Contains("fail_repair", StringComparison.OrdinalIgnoreCase)
            || (a.LeadHandling ?? "").Contains("fail_repair", StringComparison.OrdinalIgnoreCase))
            return "结果已交回；负责人验收发现问题，需要原执行者退修";
        if ((a.Acceptance ?? "").Equals("handled_accepted", StringComparison.OrdinalIgnoreCase)
            || IsExplicitAccepted(a.Acceptance))
            return IsAdoptedValue(a.Adoption) ? "已接受并已采用" : "已验收";
        if ((a.LeadHandling ?? "").Contains("lead_handled", StringComparison.OrdinalIgnoreCase)
            || ((a.Acceptance ?? "").StartsWith("handled", StringComparison.OrdinalIgnoreCase)
                && !(a.Acceptance ?? "").Contains("accept", StringComparison.OrdinalIgnoreCase)))
        {
            if ((a.Acceptance ?? "").Contains("partial", StringComparison.OrdinalIgnoreCase))
                return "已处理（部分采用）";
            return "已处理";
        }

        if (IsGenerationInFlight(work))
            return GenerationPhrase(work, null);
        if (IsFailed(a.Execution) && !IsConsumedHistoricalHandling(a.LeadHandling))
            return "当前执行失败，待处理";
        if (IsActive(a.Execution) && !IsComplete(a.Delivery) && !IsComplete(a.Transport))
            return IsGenerationInFlight(work)
                ? GenerationPhrase(work, null)
                : "原执行者正在做，本轮还没交回";
        if (IsActive(a.Execution))
            return "执行中";
        if (IsAlive(a.Process) && IsComplete(a.Turn))
            return "回合已结束 · 进程仍存活";
        if (IsComplete(a.Transport) || IsComplete(a.Delivery) || IsComplete(a.Execution))
        {
            if (work.Role.Contains("generation", StringComparison.OrdinalIgnoreCase)
                || work.Summary.Contains("继续生成", StringComparison.Ordinal))
            {
                return GenerationPhrase(work, null);
            }

            if (IsPending(a.Acceptance) || IsUnknown(a.Acceptance))
                return "结果已交回，等待负责人处理";
            if (IsExplicitAccepted(a.Acceptance))
                return IsAdoptedValue(a.Adoption) ? "已接受并已采用" : "已接受";
            return "结果已交回";
        }
        if (IsComplete(a.Callback) && (IsPending(a.LeadHandling) || IsUnknown(a.LeadHandling)))
            return "回叫已发生，待原Lead处理";
        if (IsPending(a.Acceptance))
            return "待验收";
        if (IsAdoptedValue(a.Adoption))
            return "已采用贡献";
        if (IsExplicitAccepted(a.Acceptance))
            return "已接受";
        if (IsPausedToken(a.LeadHandling) || IsPausedToken(a.Execution))
            return "主动暂停";

        var summary = work.Summary?.Trim();
        if (!string.IsNullOrWhiteSpace(summary)
            && !IsHistoricalWork(work)
            && !LooksLikePassClaim(summary)
            && !LooksLikeStageCode(summary)
            && IsConsumerPhrase(summary))
        {
            return Truncate(summary, 40);
        }

        return "未获取";
    }

    private static bool IsReviewerWork(WorkView work) =>
        string.Equals(work.ActorKind, "reviewer", StringComparison.OrdinalIgnoreCase)
        || (work.Role ?? string.Empty).Contains("review", StringComparison.OrdinalIgnoreCase)
        || (work.Role ?? string.Empty).Contains("审核", StringComparison.Ordinal);

    private static bool LooksLikeStageCode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || HasCjk(text)) return false;
        var t = text.Trim();
        if (t.Contains("assigned", StringComparison.OrdinalIgnoreCase) && t.Contains('-'))
        {
            return true;
        }

        return t.Contains('_', StringComparison.Ordinal)
               && t.Any(char.IsAscii)
               && t.Any(char.IsDigit);
    }

    public static bool IsConsumerPhrase(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        if (t.Equals("unknown", StringComparison.OrdinalIgnoreCase) || t.Equals("未知", StringComparison.Ordinal))
            return false;
        return !LooksLikeTechnicalDump(t);
    }

    public static bool LooksNonConsumerToken(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || value is "unknown" or "未声明"
        || LooksLikeTechnicalDump(value)
        || (value.Contains('_', StringComparison.Ordinal) && value.Any(char.IsAscii) && !value.Any(static ch => ch > 127));

    public static bool HasCjk(string? text) =>
        !string.IsNullOrEmpty(text) && text.Any(static ch => ch > 127);

    /// <summary>
    /// Ordinary next-owner sentence: short Chinese who/action/order from source meaning.
    /// English engineering prose is not copied into the consumer field.
    /// </summary>
    public static string OrdinaryNextFromSource(
        string? sourceNext,
        bool blocked,
        bool accepting,
        bool inFlight,
        bool returned)
    {
        if (string.IsNullOrWhiteSpace(sourceNext) || LooksNonConsumerToken(sourceNext))
            return string.Empty;

        var t = sourceNext.Trim();
        if (HasCjk(t))
        {
            t = NormalizeLeadWord(t);
            if (blocked && (t.Contains("平台", StringComparison.Ordinal) || t.Contains("限制", StringComparison.Ordinal)))
                return t;
            return ApplyNextTiming(t, blocked, accepting, inFlight, returned, leadNext: LooksLeadNext(t));
        }

        return ComposeLatinNext(t, blocked, accepting, inFlight, returned);
    }

    private static string NormalizeLeadWord(string text) =>
        text.Replace("原 Lead", "原负责人", StringComparison.Ordinal)
            .Replace("原Lead", "原负责人", StringComparison.Ordinal);

    private static bool LooksLeadNext(string text) =>
        text.Contains("验收", StringComparison.Ordinal)
        || text.Contains("负责人", StringComparison.Ordinal)
        || text.Contains("Lead", StringComparison.OrdinalIgnoreCase);

    private static string ApplyNextTiming(
        string body,
        bool blocked,
        bool accepting,
        bool inFlight,
        bool returned,
        bool leadNext)
    {
        if (blocked) return body;
        if (accepting && leadNext && !body.Contains("正在验收", StringComparison.Ordinal))
            return body.Contains("负责人", StringComparison.Ordinal) ? body : "原负责人正在验收：" + body;
        if (inFlight && leadNext && !body.Contains("交回后", StringComparison.Ordinal)
            && !body.Contains("原执行者", StringComparison.Ordinal))
            return "交回后，" + body;
        if (returned && leadNext && !body.Contains("接着", StringComparison.Ordinal)
            && !body.Contains("正在验收", StringComparison.Ordinal)
            && !body.Contains("交回后", StringComparison.Ordinal))
            return body;
        return body;
    }

    private static string ComposeLatinNext(
        string source,
        bool blocked,
        bool accepting,
        bool inFlight,
        bool returned)
    {
        var lower = source.ToLowerInvariant();
        var platform = ContainsAny(lower, "platform", "safety restriction", "safety_restriction");
        if (blocked || platform)
        {
            return "按当前源处理平台反馈后接原负责人";
        }

        var generation = ContainsAny(lower, "generation", "generate")
                         && ContainsAny(lower, "resume", "continue", "generation", "generate");
        if (generation && inFlight)
        {
            var g = "原执行者继续生成，交回后负责人验收";
            return HasUnexplainedFragment(source) ? g + "。具体检查事项还未说明" : g;
        }

        var lead = ContainsAny(lower, "original lead", "lead continues", "lead acceptance", "lead resumes", "lead resume")
                   || (lower.Contains("lead", StringComparison.Ordinal)
                       && ContainsAny(lower, "acceptance", "accepting", "receipt"));
        var acceptance = ContainsAny(lower, "acceptance", "accepting");
        var auth = ContainsAny(lower, "authorization", "authorisation");
        var beforeWrite = ContainsAny(lower, "before final native write", "before native write", "before final write")
                          || (lower.Contains("before", StringComparison.Ordinal) && lower.Contains("write", StringComparison.Ordinal));
        var compat = ContainsAny(lower, "correction compatibility", "existing correction", "correction compatib");
        var retainUi = ContainsAny(lower, "admission/output", "output/ui")
                       || (ContainsAny(lower, "retain accepted", "retained") && ContainsAny(lower, "output", "ui", "admission"));
        var checkOrder = ContainsAny(lower, "check ordering", "async check", "check order");

        var actions = new List<string>();
        if (checkOrder) actions.Add("只修检查的先后顺序");
        if (auth && beforeWrite) actions.Add("实际发出操作前做授权检查");
        else if (auth) actions.Add("做授权检查");
        if (compat) actions.Add("确认与原有修正方式是否兼容");
        if (retainUi) actions.Add("已确认的输出和界面成果保留");
        else if (ContainsAny(lower, "retained", "retain") && ContainsAny(lower, "output", "compatib", "revocation"))
            actions.Add("已确认的成果保留");

        var leadNext = lead || acceptance;
        string head;
        if (accepting && leadNext) head = "原负责人正在验收";
        else if (inFlight && checkOrder && leadNext)
        {
            head = "原执行者只修检查的先后顺序；交回后，原负责人继续验收";
            actions.Remove("只修检查的先后顺序");
        }
        else if (inFlight && leadNext) head = "交回后，原负责人继续验收";
        else if (returned && leadNext) head = "原负责人接着验收";
        else if (leadNext) head = "交回后由负责人验收";
        else if (inFlight) head = "交回后由负责人验收";
        else head = "负责人接着验收";

        var unexplained = HasUnexplainedFragment(source) || (actions.Count == 0 && !checkOrder);
        if (actions.Count == 0)
        {
            return unexplained ? head + "。具体检查事项还未说明" : head;
        }

        var joined = string.Join("；", actions);
        var sentence = head.Contains('；', StringComparison.Ordinal) ? head + "。" + joined : head + "：" + joined;
        if (unexplained && !sentence.Contains("还未说明", StringComparison.Ordinal))
            sentence += "。具体检查事项还未说明";
        return sentence;
    }

    private static bool ContainsAny(string hay, params string[] needles) =>
        needles.Any(n => hay.Contains(n, StringComparison.Ordinal));

    private static bool HasUnexplainedFragment(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var i = 0;
        while ((i = text.IndexOf("and", i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var j = i + 3;
            if (j < text.Length && char.IsDigit(text[j])) return true;
            i = j;
        }

        i = 0;
        while ((i = text.IndexOf("original", i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var j = i + 8;
            while (j < text.Length && char.IsDigit(text[j])) j++;
            if (j > i + 8 && j < text.Length && text.AsSpan(j).StartsWith("-day", StringComparison.OrdinalIgnoreCase))
                return true;
            i = i + 8;
        }

        return false;
    }

    public static string CleanConsumerPhrase(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var bits = text.Split(new[] { '；', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsConsumerPhrase)
            .ToList();
        return string.Join("；", bits);
    }

    public static bool LooksLikeTechnicalDump(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var t = text.Trim();
        if (t.Equals("unknown", StringComparison.OrdinalIgnoreCase) || t.Equals("未知", StringComparison.Ordinal))
            return true;
        if (t.Contains("process_observation", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.Contains("adoption_partial", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.Contains("unknown", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.Contains("scope=", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.Contains("axis=", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.Contains("present/attempted", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.Contains("V23_", StringComparison.OrdinalIgnoreCase)) return true;
        if (LooksLikeStageCode(t)) return true;
        return false;
    }

    public static bool HasLiveHandler(WorkView work)
    {
        var lead = work.Axes.LeadHandling ?? "";
        var acc = work.Axes.Acceptance ?? "";
        return lead is "blocked_after_acceptance" or "lead_accepting" or "repair_dispatched"
               || acc is "acceptance_in_progress";
    }

    public static bool IsActiveWorkBacked(WorkView work) =>
        work.Evidence.Any(e => e.Kind.Equals("active_work", StringComparison.OrdinalIgnoreCase));

    public static bool IsDiagnosticNoise(WorkView work)
    {
        if (HasLiveHandler(work))
            return false;
        if (work.ActorKind is "progress") return true;
        if (work.ActorKind is "executor" or "lead" or "reviewer" or "legion")
            return false;
        if (work.Role.Equals("lead", StringComparison.OrdinalIgnoreCase)) return false;
        if (work.Role.Contains("review", StringComparison.OrdinalIgnoreCase)) return false;
        if (work.Role.Contains("exec", StringComparison.OrdinalIgnoreCase)) return false;
        if (work.Role.Equals("process", StringComparison.OrdinalIgnoreCase)) return true;
        if (work.Role.Equals("cli", StringComparison.OrdinalIgnoreCase)) return true;
        if (work.Summary.StartsWith("process_observation", StringComparison.OrdinalIgnoreCase)) return true;
        var role = work.Role ?? string.Empty;
        if (role.Contains("lead", StringComparison.OrdinalIgnoreCase)
            && IsUnknown(work.Axes.LeadHandling)
            && IsUnknown(work.Axes.Acceptance)
            && IsUnknown(work.Axes.Execution)
            && !IsConsumerPhrase(work.Summary))
            return true;
        if (LooksLikeTechnicalDump(work.Summary)
            && IsUnknown(work.Axes.Execution)
            && IsUnknown(work.Axes.LeadHandling)
            && string.IsNullOrWhiteSpace(work.Model))
            return true;
        return false;
    }

    public static bool IsGenerationInFlight(WorkView work)
    {
        if (work.Role.Contains("generation", StringComparison.OrdinalIgnoreCase)) return true;
        return work.Summary.Contains("继续生成", StringComparison.Ordinal)
               || work.Summary.Contains("世界已生成", StringComparison.Ordinal);
    }

    public static string GenerationPhrase(WorkView work, ProgressView? progress)
    {
        var day = progress is { Total: > 0, Basis: not null }
            && progress.Basis.Contains("世界", StringComparison.Ordinal)
            ? $"已到第{progress.Completed}天 / 目标{progress.Total}天"
            : progress is { Completed: > 0, Basis: not null }
                && progress.Basis.Contains("世界", StringComparison.Ordinal)
                ? $"已到第{progress.Completed}天"
                : null;
        if (!string.IsNullOrWhiteSpace(day))
            return "原执行者继续生成与退修，" + day + "，完成后负责人验收";
        _ = work;
        return "原执行者继续生成与退修，本轮还没交回，完成后负责人验收";
    }

    private static bool IsExecutionLike(WorkView w)
    {
        if (HasLiveHandler(w)) return true;
        var role = w.Role ?? string.Empty;
        if (role.Contains("contrib", StringComparison.OrdinalIgnoreCase)) return false;
        if (role.Contains("贡献", StringComparison.Ordinal)) return false;
        if (role.Contains("process", StringComparison.OrdinalIgnoreCase)) return false;
        if (role.Contains("cli", StringComparison.OrdinalIgnoreCase)) return false;
        if (role.Contains("generation", StringComparison.OrdinalIgnoreCase)) return true;
        if (role.Contains("exec", StringComparison.OrdinalIgnoreCase)) return true;
        if (role.Contains("lead_acceptance", StringComparison.OrdinalIgnoreCase)) return false;
        if (role.Contains("lead", StringComparison.OrdinalIgnoreCase))
            return !IsUnknown(w.Axes.LeadHandling) || !IsUnknown(w.Axes.Acceptance) || !IsUnknown(w.Axes.Execution);
        return !IsUnknown(w.Axes.Execution);
    }

    private static bool IsContributionLike(WorkView w)
    {
        var role = w.Role ?? string.Empty;
        if (role.Contains("lead_acceptance", StringComparison.OrdinalIgnoreCase)) return false;
        if (role.Contains("contrib", StringComparison.OrdinalIgnoreCase)) return true;
        if (role.Contains("贡献", StringComparison.Ordinal)) return true;
        if (role.Contains("army", StringComparison.OrdinalIgnoreCase)) return true;
        if (role.Contains("军团", StringComparison.Ordinal)) return true;
        if (w.Id.StartsWith("acceptance:", StringComparison.OrdinalIgnoreCase)) return false;
        if (w.Id.StartsWith("contribution:", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool IsPendingLead(WorkView w) =>
        IsPending(w.Axes.Acceptance) || IsPending(w.Axes.LeadHandling)
        || (IsComplete(w.Axes.Delivery) && (IsUnknown(w.Axes.Acceptance) || IsPending(w.Axes.Acceptance)));

    private static bool IsAdopted(WorkView w) => IsAdoptedValue(w.Axes.Adoption);
    private static bool IsAccepted(WorkView w) => IsExplicitAccepted(w.Axes.Acceptance);
    private static bool IsGoalComplete(WorkView w) =>
        string.Equals(w.Axes.Goal, "complete", StringComparison.OrdinalIgnoreCase)
        || string.Equals(w.Axes.Goal, "completed", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnknown(string? s) =>
        string.IsNullOrWhiteSpace(s) || s.Equals("unknown", StringComparison.OrdinalIgnoreCase);

    private static bool IsPending(string? s) =>
        !IsUnknown(s) && (s!.Contains("pending", StringComparison.OrdinalIgnoreCase)
            || s.Contains("await", StringComparison.OrdinalIgnoreCase)
            || s.Contains("待", StringComparison.Ordinal));

    private static bool IsActive(string? s) =>
        !IsUnknown(s) && (s!.Equals("active", StringComparison.OrdinalIgnoreCase)
            || s.Equals("running", StringComparison.OrdinalIgnoreCase)
            || s.Contains("执行中", StringComparison.Ordinal));

    private static bool IsAlive(string? s) =>
        !IsUnknown(s) && (s!.Equals("alive", StringComparison.OrdinalIgnoreCase)
            || s.Equals("running", StringComparison.OrdinalIgnoreCase));

    private static bool IsFailed(string? s) =>
        !IsUnknown(s) && s!.Equals("failed", StringComparison.OrdinalIgnoreCase);

    private static bool IsComplete(string? s) =>
        !IsUnknown(s) && (s!.Equals("complete", StringComparison.OrdinalIgnoreCase)
            || s.Equals("completed", StringComparison.OrdinalIgnoreCase)
            || s.Equals("success", StringComparison.OrdinalIgnoreCase)
            || s.Equals("succeeded", StringComparison.OrdinalIgnoreCase)
            || s.Equals("returned", StringComparison.OrdinalIgnoreCase)
            || s.Equals("delivered", StringComparison.OrdinalIgnoreCase));

    private static bool IsAcceptedValue(string? s) => IsExplicitAccepted(s);

    private static bool IsAdoptedValue(string? s) =>
        !IsUnknown(s) && (s!.Contains("adopt", StringComparison.OrdinalIgnoreCase)
            || s.Contains("已采用", StringComparison.Ordinal));

    private static bool IsPausedToken(string? s) =>
        !IsUnknown(s) && (s!.Contains("pause", StringComparison.OrdinalIgnoreCase)
            || s.Contains("暂停", StringComparison.Ordinal));

    private static bool IsConsumedHistoricalHandling(string? s) =>
        !IsUnknown(s) && (s!.Contains("consumed", StringComparison.OrdinalIgnoreCase)
            || s.Contains("historical", StringComparison.OrdinalIgnoreCase)
            || s.Contains("supersede", StringComparison.OrdinalIgnoreCase)
            || s.Contains("successor", StringComparison.OrdinalIgnoreCase));

    private static string HumanizeToken(string raw)
    {
        var t = raw.Trim();
        return t.ToLowerInvariant() switch
        {
            "active" or "running" => "进行中",
            "alive" => "存活",
            "dead" or "exited" => "已退出",
            "failed" => "失败",
            "succeeded" or "success" => "成功返回",
            "returned" => "已返回",
            "complete" or "completed" => "已完成（该轴）",
            "pending" => "待处理",
            "unknown" => "未知",
            "paused" => "已暂停",
            "accepted" => "已接受",
            "adopted" or "adoption_partial" => "已采用（部分或全部）",
            "executing" => "执行中",
            "generation" => "生成",
            "awaiting_lead_acceptance" => "待原Lead验收",
            "blocked_after_acceptance" => "验收后退修被中断且未派出",
            "lead_accepting" or "acceptance_in_progress" => "负责人正在验收",
            "repair_dispatched" => "退修已派出",
            "dispatch_pending_native" => "已派出，等待原生运行/本轮结果证据",
            "callback_wait" => "等待回叫",
            "handled_fail_repair" or "lead_handled_fail_repair" => "已处理待修复",
            "handled_partial_adopted" or "lead_handled_partial_adopted" => "已处理（部分采用）",
            "content_not_accepted" => "内容未验收",
            "lead_handled_quota_blocked" => "已处理（额度失败）",
            "lead_handled_quota_resumed" => "已处理（额度失败，资源已另派）",
            "lead_handled" or "handled" => "已处理",
            "fault_pending_handling" => "故障待处理",
            "contribution" or "contrib" or "army_contribution" => "外部贡献",
            "execution" or "exec" or "lead_execution" => "执行",
            "lead" => "原Lead",
            _ => t.Replace('_', ' ')
        };
    }
}
