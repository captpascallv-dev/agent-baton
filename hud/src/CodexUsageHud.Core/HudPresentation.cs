using System.Diagnostics;

namespace CodexUsageHud.Core;

public sealed record SessionDisplayRow(
    string ThreadId,
    string Name,
    string ShortId,
    string RoleNickname,
    string Project,
    string Model,
    string Tier,
    string Status,
    string LastActivity,
    DateTimeOffset? LastActivityUtc,
    CanonicalTokenUsage RecentUsage,
    string RecentUsageLabel,
    string RecentUsageConfidence,
    CanonicalTokenUsage SessionTotal,
    bool IsCurrent,
    bool IsInferredCurrent,
    bool IsRunning,
    SessionKind Kind,
    SessionContextMetrics? ContextMetrics = null,
    SessionLifecycleMetrics? LifecycleMetrics = null,
    SessionDriftAssessment? DriftAssessment = null,
    SessionSurface Surface = SessionSurface.Unknown,
    string? ParentThreadId = null,
    string? ParentDisplayName = null,
    int HierarchyDepth = 0,
    bool IsOrphanInternalTask = false,
    int DirectChildCount = 0,
    int DescendantCount = 0,
    int RunningDescendantCount = 0,
    CanonicalTokenUsage DescendantTotal = default,
    bool IsExpanded = false)
{
    public CanonicalTokenUsage LatestTurn => RecentUsage;
    public bool IsInternalTask => Kind == SessionKind.InternalTask;
    public bool IsPinned => IsCurrent && !IsInferredCurrent;
    public bool HasChildren => DirectChildCount > 0;
    public bool EffectiveIsRunning => IsRunning || RunningDescendantCount > 0;
    public CanonicalTokenUsage WorkTotal => SessionTotal + DescendantTotal;
    public double HierarchyIndent => Math.Min(Math.Max(HierarchyDepth, 0), 6) * 15d;
    public string ExpansionGlyph => IsExpanded ? "⌄" : "›";
    public string SourceLabel => Surface switch
    {
        SessionSurface.App => "APP",
        SessionSurface.Cli => "CLI",
        SessionSurface.InternalTask => "子任务",
        _ when IsInternalTask => "子任务",
        _ => "未知来源",
    };
    public string ParentSummaryText => IsInternalTask
        ? ParentThreadId is null
            ? "父会话不可用（历史记录）"
            : $"归属：{ParentDisplayName ?? ShortParentId}"
        : "主会话";
    public string ChildSummaryText => DescendantCount == 0
        ? "无已关联子任务"
        : $"{DescendantCount:N0} 个子任务 · 运行中 {RunningDescendantCount:N0}";
    public string WorkBreakdownText => DescendantCount == 0
        ? $"自身 {TokenFormat.Compact(SessionTotal.Total)}"
        : $"自身 {TokenFormat.Compact(SessionTotal.Total)} + 子任务 {TokenFormat.Compact(DescendantTotal.Total)}";
    public string WorkTotalText => $"{WorkTotal.Total:N0}";
    private string ShortParentId => ParentThreadId is { Length: > 12 } value ? value[..12] : ParentThreadId ?? string.Empty;

    public string CurrentLabel => !IsCurrent ? string.Empty
        : IsInferredCurrent ? "当前（推断）" : "当前（已固定）";

    public string CurrentTooltip => IsInferredCurrent
        ? "尚未固定；当前会话由最近活动推断"
        : IsCurrent ? "此会话已固定为当前会话" : string.Empty;

    public string PostCompactionBaselineText =>
        ContextMetrics is { HasReliablePostCompactionBaseline: true } metrics
            ? $"{metrics.PostCompactionPercent:0}% · " +
              $"{TokenFormat.Compact(metrics.PostCompactionInputTokens!.Value)} / " +
              $"{TokenFormat.Compact(metrics.PostCompactionWindowTokens!.Value)}"
            : "样本不足";

    public string PostCompactionSourceText => ContextMetrics is { HasReliablePostCompactionBaseline: true } metrics
        ? metrics.UsesExplicitCompactionBoundaries ? "官方压缩边界 · 近 3 次中位" : "安全降级估算 · 近 3 次中位"
        : "至少需要 3 个同模型、同窗口样本";

    public string BaselineTrendText => ContextMetrics?.BaselineTrendPercentagePoints switch
    {
        >= 1d and var value => $"近 3 次：+{value:0.0} 个点",
        <= -1d and var value => $"近 3 次：{value:0.0} 个点",
        not null => "近 3 次基本稳定",
        _ => "样本不足",
    };

    public string EffectiveRunwayText
    {
        get
        {
            if (ContextMetrics is not { } metrics) return "样本不足";
            var parts = new List<string>(2);
            if (metrics.TurnRunwayChangePercent.HasValue)
                parts.Add($"回合 {FormatRunwayChange(metrics.TurnRunwayChangePercent.Value)}");
            if (metrics.TokenRunwayChangePercent.HasValue)
                parts.Add($"间隔 token 续航 {FormatRunwayChange(metrics.TokenRunwayChangePercent.Value)}");
            return parts.Count switch
            {
                0 => "样本不足",
                1 => "较此前：" + parts[0],
                _ => $"较此前：{parts[0]}\n{parts[1]}",
            };
        }
    }

    public string HistoryVolumeText => $"{TokenFormat.Compact(SessionTotal.Total)} raw tokens";

    public string LifecycleLoadText => LifecycleMetrics is { } metrics
        ? $"累计 {TokenFormat.Compact(SessionTotal.Total)} · {metrics.AggregatedTurnCount:N0} 回合"
        : "样本不足";

    public string LifecycleRecentText => LifecycleMetrics is { } metrics
        ? $"近 48 小时 {TokenFormat.Compact(metrics.Recent48HourTokens)} · {metrics.Recent48HourTurnCount:N0} 回合"
        : "至少需要可比的同模型主会话";

    public string LifecycleRiskText => EvaluateLifecycle().Label;

    public string LifecycleRiskDetailText => EvaluateLifecycle().Detail;

    public string StructuralGradeText => EvaluateStructural().Grade is { } grade ? GradeText(grade) : "—";

    public string DriftAssessmentText => EffectiveDrift.Level switch
    {
        DriftAssessmentLevel.Occasional => "偶发漂移",
        DriftAssessmentLevel.Repeated => "连续明显漂移",
        _ => "未评估",
    };

    public string DriftAssessmentDetailText => EffectiveDrift.Level == DriftAssessmentLevel.Unassessed
        ? "HUD 不读取正文；未评估不代表没有漂移"
        : $"人工标记 · {EffectiveDrift.ObservedAtUtc?.ToLocalTime():MM-dd HH:mm}";

    public bool IsDriftUnassessed => EffectiveDrift.Level == DriftAssessmentLevel.Unassessed;
    public bool IsDriftOccasional => EffectiveDrift.Level == DriftAssessmentLevel.Occasional;
    public bool IsDriftRepeated => EffectiveDrift.Level == DriftAssessmentLevel.Repeated;

    private SessionDriftAssessment EffectiveDrift => DriftAssessment ?? SessionDriftAssessment.Unassessed;

    public string ContinuationGradeText => EvaluateContinuation().Grade;

    public string ContinuationAdviceText => EvaluateContinuation().Advice;

    public string ContinuationEvidenceText => EvaluateContinuation().Evidence;

    private ContinuationAssessment EvaluateContinuation()
    {
        var structural = EvaluateStructural();
        var lifecycle = EvaluateLifecycle();
        var manualFloor = EffectiveDrift.Level switch
        {
            DriftAssessmentLevel.Occasional => ContinuationGrade.BMinus,
            DriftAssessmentLevel.Repeated => ContinuationGrade.C,
            _ => (ContinuationGrade?)null,
        };
        var grades = new[] { structural.Grade, lifecycle.MinimumGrade, manualFloor }
            .Where(item => item.HasValue).Select(item => item!.Value).ToArray();
        if (grades.Length == 0)
            return new ContinuationAssessment("—", "样本不足，暂不判断",
                "结构样本不足 · 长会话样本不足 · 人工未评估");

        var finalGrade = grades.Max();
        var structuralText = structural.Grade is { } grade ? GradeText(grade) : "不可用";
        var lifecycleText = lifecycle.MinimumGrade is { } floor
            ? $"{lifecycle.Label} {GradeText(floor)}" : lifecycle.Label;
        return new ContinuationAssessment(GradeText(finalGrade), AdviceFor(finalGrade),
            $"{structural.Evidence} · 结构 {structuralText} · 长会话 {lifecycleText} · 人工 {DriftAssessmentText}");
    }

    private StructuralAssessment EvaluateStructural()
    {
        if (ContextMetrics is not { HasReliablePostCompactionBaseline: true } metrics ||
            !metrics.PostCompactionPercent.HasValue)
            return new StructuralAssessment(null, "结构样本不足");

        var baselineGrade = GradeForBaseline(metrics.PostCompactionPercent.Value);
        var trend = AssessTrend(metrics.BaselineTrendPercentagePoints);
        var runway = AssessRunway(metrics.TurnRunwayChangePercent, metrics.TokenRunwayChangePercent);
        var adjustment = trend.Adjustment != 0 ? trend.Adjustment : runway.Adjustment;
        var finalGrade = (ContinuationGrade)Math.Clamp((int)baselineGrade + adjustment,
            (int)ContinuationGrade.S, (int)ContinuationGrade.C);
        return new StructuralAssessment(finalGrade,
            $"底座定级 {GradeText(baselineGrade)} · {trend.Description} · {runway.Description}");
    }

    private LifecycleAssessment EvaluateLifecycle()
    {
        if (LifecycleMetrics is not { ComparableSessionCount: >= 10 } metrics)
            return new LifecycleAssessment(null, "样本不足", "至少需要 10 个本机同模型主会话");

        var lifetimeTokenPercentile = Percentile(metrics.LifetimeTokenRank, metrics.ComparableSessionCount);
        var lifetimeTurnPercentile = Percentile(metrics.LifetimeTurnRank, metrics.ComparableSessionCount);
        var recentTokenPercentile = Percentile(metrics.RecentTokenRank, metrics.ComparableSessionCount);
        var recentTurnPercentile = Percentile(metrics.RecentTurnRank, metrics.ComparableSessionCount);
        var lifetimeExtreme = lifetimeTokenPercentile >= 97d && lifetimeTurnPercentile >= 97d &&
                              metrics.LifetimeTokenMedianMultiple >= 10d &&
                              metrics.LifetimeTurnMedianMultiple >= 10d;
        var lifetimeHigh = lifetimeTokenPercentile >= 90d && lifetimeTurnPercentile >= 90d &&
                           metrics.LifetimeTokenMedianMultiple >= 3d &&
                           metrics.LifetimeTurnMedianMultiple >= 3d;
        var recentExtreme = recentTokenPercentile >= 97d && recentTurnPercentile >= 97d &&
                            metrics.RecentTokenMedianMultiple >= 10d &&
                            metrics.RecentTurnMedianMultiple >= 10d;
        var detail = $"本机同模型主会话：历史量第 {metrics.LifetimeTokenRank}/{metrics.ComparableSessionCount}，" +
                     $"回合第 {metrics.LifetimeTurnRank}/{metrics.ComparableSessionCount}";
        if (lifetimeExtreme || lifetimeHigh && recentExtreme)
            return new LifecycleAssessment(ContinuationGrade.BMinus, "极高", detail);
        if (lifetimeHigh || lifetimeTokenPercentile >= 75d && lifetimeTurnPercentile >= 75d && recentExtreme)
            return new LifecycleAssessment(ContinuationGrade.BPlus, "偏高", detail);
        return new LifecycleAssessment(null, "常规", detail);
    }

    private static double Percentile(int descendingRank, int count) => count <= 1
        ? 100d
        : Math.Clamp((count - descendingRank) * 100d / (count - 1), 0d, 100d);

    private static ContinuationGrade GradeForBaseline(double percent) => percent switch
    {
        < 25d => ContinuationGrade.S,
        < 35d => ContinuationGrade.APlus,
        < 45d => ContinuationGrade.AMinus,
        < 55d => ContinuationGrade.BPlus,
        < 65d => ContinuationGrade.BMinus,
        _ => ContinuationGrade.C,
    };

    private static ContinuationSignal AssessTrend(double? changePercentagePoints)
    {
        if (!changePercentagePoints.HasValue)
            return new ContinuationSignal(0, "趋势样本不足");
        if (changePercentagePoints.Value > 3d)
            return new ContinuationSignal(1, $"底座上升 +{changePercentagePoints.Value:0.0} 点");
        if (changePercentagePoints.Value < -3d)
            return new ContinuationSignal(-1, $"底座下降 {changePercentagePoints.Value:0.0} 点");
        return new ContinuationSignal(0, "底座趋势稳定");
    }

    private static ContinuationSignal AssessRunway(double? turnChangePercent, double? tokenChangePercent)
    {
        if (!turnChangePercent.HasValue || !tokenChangePercent.HasValue)
            return new ContinuationSignal(0, "续航样本不足");

        var turnDirection = MeaningfulDirection(turnChangePercent.Value);
        var tokenDirection = MeaningfulDirection(tokenChangePercent.Value);
        if (turnDirection > 0 && tokenDirection > 0)
            return new ContinuationSignal(-1, "两项续航共同改善");
        if (turnDirection < 0 && tokenDirection < 0)
            return new ContinuationSignal(1, "两项续航共同缩短");
        if (turnDirection * tokenDirection < 0)
            return new ContinuationSignal(0, "续航信号分化");
        return new ContinuationSignal(0, "续航无一致显著变化");
    }

    private static int MeaningfulDirection(double value) => value switch
    {
        >= 20d => 1,
        <= -20d => -1,
        _ => 0,
    };

    private static string GradeText(ContinuationGrade grade) => grade switch
    {
        ContinuationGrade.S => "S",
        ContinuationGrade.APlus => "A+",
        ContinuationGrade.AMinus => "A-",
        ContinuationGrade.BPlus => "B+",
        ContinuationGrade.BMinus => "B-",
        _ => "C",
    };

    private static string AdviceFor(ContinuationGrade grade) => grade switch
    {
        ContinuationGrade.S => "状态轻盈，可以放心继续",
        ContinuationGrade.APlus => "状态健康，正常继续",
        ContinuationGrade.AMinus => "仍可继续，留意底座趋势",
        ContinuationGrade.BPlus => "仍可继续，但建议准备续接",
        ContinuationGrade.BMinus => "建议当前完整工作包结束后续接",
        _ => "建议尽快续接，不要再开启新的大型工作包",
    };

    private static string FormatRunwayChange(double value) => value switch
    {
        <= -1d => $"-{Math.Abs(value):0}%",
        >= 1d => $"+{value:0}%",
        _ => "0%",
    };

    private enum ContinuationGrade
    {
        S,
        APlus,
        AMinus,
        BPlus,
        BMinus,
        C,
    }

    private readonly record struct ContinuationSignal(int Adjustment, string Description);
    private readonly record struct StructuralAssessment(ContinuationGrade? Grade, string Evidence);
    private readonly record struct LifecycleAssessment(ContinuationGrade? MinimumGrade, string Label, string Detail);
    private readonly record struct ContinuationAssessment(string Grade, string Advice, string Evidence);
}

public sealed record QuotaThresholdState(string Code, string Icon, string Text);

public static class HudPresentation
{
    public const string UnitNote = "raw token 与平台额度不是同一单位，不能直接换算";
    public const string SymbolicDatabasePath = @"%LOCALAPPDATA%\CodexUsageHUD\usage.db";

    public static HudFrame BuildFrame(HudSnapshot snapshot)
    {
        var stopwatch = Stopwatch.StartNew();
        var rows = BuildRows(snapshot);
        var threshold = GetThreshold(snapshot.Quota);
        var quota = GetUsablePrimaryQuota(snapshot.Quota);
        var quotaText = quota is null
            ? "额度：不可用 · 官方重置时间：不可用 · 倒计时：不可用"
            : $"额度：已用 {FormatQuotaPercent(quota.UsedPercent)} · 剩余 {FormatQuotaPercent(quota.RemainingPercent)}（{quota.Name}） · " +
              $"官方重置 {quota.ResetsAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · " +
              $"倒计时 {FormatCountdown(quota.ResetsAtUtc - snapshot.GeneratedAtUtc)}";
        var running = snapshot.MachineLocalRunningCount ?? snapshot.Sessions.Count(IsRunning);
        var runningCycleText = snapshot.RunningCycleTotal.HasValue
            ? $"{TokenFormat.Compact(snapshot.RunningCycleTotal.Value.Total)} raw tokens"
            : "不可用";
        var cycleText = snapshot.CycleTotal.HasValue
            ? $"{TokenFormat.Compact(snapshot.CycleTotal.Value.Total)} raw tokens"
            : "不可用";
        var thresholdText = $"{threshold.Icon} 阈值状态：{threshold.Text}";
        var overview = $"{quotaText}\n运行中会话：{running} · " +
                       $"运行中会话本额度周期合计：{runningCycleText} · " +
                       $"本额度周期全部会话合计：{cycleText}\n{thresholdText}";
        var freshness = snapshot.AggregateMigrationPending
            ? "索引中（统计迁移）"
            : (snapshot.IsIndexing ? "索引中 · " : string.Empty) +
              (snapshot.FreshnessMessage ?? "来源状态未知");
        var recent = snapshot.RecentEvents ?? Array.Empty<HudEvent>();
        var eventParts = recent.Take(6)
            .Select(item => $"{item.ObservedAtUtc.ToLocalTime():MM-dd HH:mm} {item.Summary}")
            .Concat(snapshot.RecentErrorCodes.Select(code => $"状态码 {code}"))
            .Distinct(StringComparer.Ordinal).Take(8).ToArray();
        var events = eventParts.Length == 0 ? "最近事件：暂无" : "最近事件：" + string.Join("；", eventParts);
        stopwatch.Stop();
        return new HudFrame(snapshot, BuildCollapsedText(snapshot), overview, freshness, events,
            thresholdText, rows, Environment.CurrentManagedThreadId, stopwatch.ElapsedMilliseconds);
    }

    public static ProviderQuotaBoard BoardForSnapshot(HudSnapshot snapshot)
    {
        if (snapshot.Providers is { Slots.Count: > 0 } board) return board;
        var defaults = ProviderAccessSettings.Default();
        var now = snapshot.GeneratedAtUtc;
        var windows = ProviderQuotaPresentation.FromCodex(snapshot.Quota);
        var status = windows.Count == 0
            ? QuotaSlotStatus.Unavailable
            : ProviderQuotaPresentation.CodexStatus(snapshot.Quota, now);
        var slots = defaults.Slots.Select(settings =>
        {
            if (settings.SlotId == ProviderSlotIds.CodexPrimary)
            {
                return new ProviderSlotSnapshot(settings.SlotId, ProviderIds.Codex, settings.Label, true, true,
                    status, ProviderQuotaPresentation.StatusCaption(status), snapshot.Quota.ObservedAtUtc,
                    "当前绑定的 Codex App 主目录", windows, ErrorCode: snapshot.Quota.ErrorCode);
            }

            return ProviderQuotaPresentation.Placeholder(settings, false,
                settings.Enabled ? QuotaSlotStatus.Unavailable : QuotaSlotStatus.Disabled,
                settings.Enabled ? "尚未刷新" : "未启用", "等待独立刷新");
        }).ToArray();
        return new ProviderQuotaBoard(slots, now);
    }

    public static string BuildCollapsedText(HudSnapshot snapshot)
    {
        if (snapshot.AggregateMigrationPending) return "? 索引中（统计迁移） · raw token 统计暂不可用";
        var quota = GetUsablePrimaryQuota(snapshot.Quota);
        var remaining = quota is null ? "不可用" : $"{FormatQuotaPercent(quota.RemainingPercent)}（{quota.Name}）";
        var countdown = quota is null ? "额度不可用" : FormatCountdown(quota.ResetsAtUtc - snapshot.GeneratedAtUtc);
        var running = snapshot.MachineLocalRunningCount ?? snapshot.Sessions.Count(IsRunning);
        var cycle = snapshot.CycleTotal.HasValue
            ? $"本周期 {TokenFormat.Compact(snapshot.CycleTotal.Value.Total)} raw tokens"
            : "本周期不可用";
        return $"{GetThreshold(snapshot.Quota).Icon} 剩余 {remaining} · {countdown} · 运行中 {running} · {cycle}";
    }

    public static IReadOnlyList<SessionDisplayRow> BuildRows(HudSnapshot snapshot)
    {
        var sessionsById = snapshot.Sessions
            .GroupBy(item => item.Metadata.ThreadId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var uniqueSessions = sessionsById.Values.ToArray();
        var rawParents = uniqueSessions
            .Where(item => item.Metadata.Kind == SessionKind.InternalTask &&
                           !string.IsNullOrWhiteSpace(item.Metadata.ParentThreadId) &&
                           sessionsById.ContainsKey(item.Metadata.ParentThreadId!))
            .ToDictionary(item => item.Metadata.ThreadId, item => item.Metadata.ParentThreadId!,
                StringComparer.Ordinal);
        var parents = rawParents
            .Where(pair => !HasParentCycle(pair.Key, rawParents))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var directChildCounts = parents.Values
            .GroupBy(parent => parent, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var descendantTotals = new Dictionary<string, CanonicalTokenUsage>(StringComparer.Ordinal);
        var descendantCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var runningDescendantCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var workLastActivity = uniqueSessions.ToDictionary(item => item.Metadata.ThreadId,
            item => item.LastActivityUtc, StringComparer.Ordinal);
        foreach (var descendant in uniqueSessions.Where(item => parents.ContainsKey(item.Metadata.ThreadId)))
        {
            var current = descendant.Metadata.ThreadId;
            var visited = new HashSet<string>(StringComparer.Ordinal) { current };
            while (parents.TryGetValue(current, out var parent) && visited.Add(parent))
            {
                descendantTotals[parent] = descendantTotals.GetValueOrDefault(parent) + descendant.SessionTotal;
                descendantCounts[parent] = descendantCounts.GetValueOrDefault(parent) + 1;
                if (IsRunning(descendant))
                    runningDescendantCounts[parent] = runningDescendantCounts.GetValueOrDefault(parent) + 1;
                if (descendant.LastActivityUtc is { } descendantActivity &&
                    (workLastActivity.GetValueOrDefault(parent) is not { } parentActivity ||
                     descendantActivity > parentActivity))
                {
                    workLastActivity[parent] = descendantActivity;
                }
                current = parent;
            }
        }

        var pinned = uniqueSessions.FirstOrDefault(item => item.Metadata.Pinned)?.Metadata.ThreadId;
        var inferred = pinned is null
            ? uniqueSessions.Where(item => item.LastActivityUtc.HasValue &&
                                           (item.Metadata.Kind != SessionKind.InternalTask ||
                                            parents.ContainsKey(item.Metadata.ThreadId)))
                .OrderByDescending(item => item.LastActivityUtc).Select(item => RootThreadId(item.Metadata.ThreadId, parents))
                .FirstOrDefault()
            : null;
        return uniqueSessions.Select(session =>
        {
            var current = string.Equals(session.Metadata.ThreadId, pinned ?? inferred, StringComparison.Ordinal);
            var threadId = session.Metadata.ThreadId;
            parents.TryGetValue(threadId, out var parentThreadId);
            sessionsById.TryGetValue(parentThreadId ?? string.Empty, out var parentSession);
            var runningChildren = runningDescendantCounts.GetValueOrDefault(threadId);
            var status = IsRunning(session)
                ? runningChildren > 0 ? $"运行中 +{runningChildren}" : "运行中"
                : runningChildren > 0 ? $"子任务 {runningChildren}" : StatusText(session.Status);
            var surface = session.Metadata.Surface == SessionSurface.Unknown &&
                          session.Metadata.Kind == SessionKind.InternalTask
                ? SessionSurface.InternalTask
                : session.Metadata.Surface;
            var activity = workLastActivity.GetValueOrDefault(threadId) ?? session.LastActivityUtc;
            return new SessionDisplayRow(session.Metadata.ThreadId, session.Metadata.SafeName,
                session.Metadata.ShortThreadId, BuildRoleNickname(session.Metadata.Role, session.Metadata.Nickname),
                session.Metadata.ProjectTag ?? "未标记项目", session.Metadata.Model ?? "不可用",
                session.Metadata.ServiceTier ?? "不可用", status,
                activity?.ToLocalTime().ToString("MM-dd HH:mm:ss") ?? "不可用",
                activity, session.RecentUsage, session.RecentUsageLabel,
                session.RecentUsageConfidence, session.SessionTotal, current, current && pinned is null,
                IsRunning(session), session.Metadata.Kind, session.ContextMetrics, session.LifecycleMetrics,
                session.DriftAssessment, surface, parentThreadId, parentSession?.Metadata.SafeName,
                HierarchyDepth(threadId, parents),
                session.Metadata.Kind == SessionKind.InternalTask && parentThreadId is null,
                directChildCounts.GetValueOrDefault(threadId), descendantCounts.GetValueOrDefault(threadId),
                runningChildren, descendantTotals.GetValueOrDefault(threadId));
        }).ToArray();
    }

    private static bool HasParentCycle(string threadId, IReadOnlyDictionary<string, string> parents)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = threadId;
        while (parents.TryGetValue(current, out var parent))
        {
            if (!visited.Add(current) || string.Equals(parent, threadId, StringComparison.Ordinal)) return true;
            current = parent;
        }
        return false;
    }

    private static int HierarchyDepth(string threadId, IReadOnlyDictionary<string, string> parents)
    {
        var depth = 0;
        var current = threadId;
        while (depth < 32 && parents.TryGetValue(current, out var parent))
        {
            depth++;
            current = parent;
        }
        return depth;
    }

    private static string RootThreadId(string threadId, IReadOnlyDictionary<string, string> parents)
    {
        var current = threadId;
        var depth = 0;
        while (depth++ < 32 && parents.TryGetValue(current, out var parent)) current = parent;
        return current;
    }

    public static QuotaThresholdState GetThreshold(QuotaObservation observation)
    {
        var primary = GetUsablePrimaryQuota(observation);
        if (primary is null) return new QuotaThresholdState("unavailable", "?", "不可用");
        if (observation.IsStale) return new QuotaThresholdState("stale", "?", "陈旧观测");
        if (primary.RemainingPercent < 10)
            return new QuotaThresholdState("critical", "‼", "紧急：剩余不足 10%");
        if (primary.RemainingPercent <= 20)
            return new QuotaThresholdState("warning", "⚠", "注意：剩余不高于 20%");
        return new QuotaThresholdState("normal", "✓", "正常");
    }

    public static QuotaBucket? GetUsablePrimaryQuota(QuotaObservation observation) =>
        observation.Primary is { HasValidWindow: true } primary ? primary : null;

    public static string FormatQuotaPercent(double value) =>
        ProviderQuotaPresentation.FormatFinePercent(value) + "%";

    public static string FormatCountdown(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero) return "已到期";
        return $"{remaining.Days}天{remaining.Hours}时{remaining.Minutes}分";
    }

    public static string StatusText(SessionStatus status) => status switch
    {
        SessionStatus.Running => "运行中",
        SessionStatus.Unknown => "未知",
        _ => "空闲",
    };

    public static bool IsRunning(SessionAggregate session) => session.Status == SessionStatus.Running;

    private static string BuildRoleNickname(string? role, string? nickname)
    {
        if (string.IsNullOrWhiteSpace(role) && string.IsNullOrWhiteSpace(nickname)) return "不可用";
        if (string.IsNullOrWhiteSpace(role)) return nickname!;
        if (string.IsNullOrWhiteSpace(nickname)) return role;
        return $"{role} / {nickname}";
    }
}

public sealed class ShutdownCoordinator
{
    public bool ExitRequested { get; private set; }

    public void RequestExit(Action closeWindow, Action shutdownApplication)
    {
        ArgumentNullException.ThrowIfNull(closeWindow);
        ArgumentNullException.ThrowIfNull(shutdownApplication);
        if (ExitRequested) return;
        ExitRequested = true;
        closeWindow();
        shutdownApplication();
    }
}
