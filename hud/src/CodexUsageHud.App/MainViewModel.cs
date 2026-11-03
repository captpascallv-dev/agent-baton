using CodexUsageHud.Core;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace CodexUsageHud.App;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private string _collapsedText = "索引中…";
    private string _overviewText = "索引中…";
    private string _freshnessText = "索引中";
    private string _eventsText = "暂无事件";
    private string _thresholdText = "? 不可用";
    private string _remainingText = "—";
    private string _usedText = "不可用";
    private string _countdownText = "不可用";
    private string _countdownCompactText = "不可用";
    private string _resetTimeText = "官方时间不可用";
    private string _runningCountText = "0";
    private string _runningCycleTotalText = "本周期不可用";
    private string _runningCycleCompactText = "—";
    private string _cycleTotalText = "不可用";
    private string _cycleCompactText = "—";
    private string _lastUpdatedText = "尚未更新";
    private string _quotaSourceText = "官方额度不可用";
    private string _quotaStateText = "不可用";
    private string _quotaStateDetailText = "官方额度不可用";
    private string _remainingWindowText = "剩余额度";
    private string _visibleSessionCountText = "0 个会话";
    private string _primarySessionCountText = "0";
    private string _internalTaskCountText = "0";
    private string _appSessionCountText = "0";
    private string _cliSessionCountText = "0";
    private string _orphanTaskCountText = "0";
    private double _quotaRingValue;
    private bool _isExpanded;
    private bool _isRefreshing;
    private HudSnapshot? _snapshot;
    private IReadOnlyList<SessionDisplayRow> _frameRows = Array.Empty<SessionDisplayRow>();
    private IReadOnlyList<SessionDisplayRow> _rows = Array.Empty<SessionDisplayRow>();
    private readonly Dictionary<(string Filter, string Sort), IReadOnlyList<SessionDisplayRow>> _rowViews = new();
    private readonly HashSet<string> _expandedParents = new(StringComparer.Ordinal);
    private IReadOnlyList<ProviderSlotSnapshot> _slots = Array.Empty<ProviderSlotSnapshot>();
    private ProviderSlotSnapshot? _selectedSlot;
    private string _slotDetailTitle = "额度详情";
    private string _slotDetailPercent = "—";
    private string _slotDetailBody = "尚未选择账户";
    private string _slotDetailStatus = "";
    private string _slotDetailSource = "";
    private string _slotDetailObserved = "";
    private string _slotDetailAccountHint = "";
    private string _slotDetailShareNote = "";
    private IReadOnlyList<SlotWindowRow> _slotDetailWindows = Array.Empty<SlotWindowRow>();
    private string _bindingNote = "仅当前绑定的 Codex App 槽提供会话分析。主目录已由所有者确认。";
    private bool _slotDetailPinned;
    private bool _topBarNarrowLayout;
    private string _compactLayout = CompactLayoutModes.Rail;
    private string _filter = "recent";
    private string _sort = "activity";
    private string _analysisEmptyTitle = "这里暂时没有会话";
    private string _analysisEmptyDetail = "可切换筛选，或等待本地记录更新";
    private bool _analysisEmptyVisible;
    private string _analysisRunningCountText = "0";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string CollapsedText { get => _collapsedText; private set => SetField(ref _collapsedText, value); }
    public string OverviewText { get => _overviewText; private set => SetField(ref _overviewText, value); }
    public string FreshnessText { get => _freshnessText; private set => SetField(ref _freshnessText, value); }
    public string EventsText { get => _eventsText; private set => SetField(ref _eventsText, value); }
    public string ThresholdText { get => _thresholdText; private set => SetField(ref _thresholdText, value); }
    public string RemainingText { get => _remainingText; private set => SetField(ref _remainingText, value); }
    public string UsedText { get => _usedText; private set => SetField(ref _usedText, value); }
    public string CountdownText { get => _countdownText; private set => SetField(ref _countdownText, value); }
    public string CountdownCompactText { get => _countdownCompactText; private set => SetField(ref _countdownCompactText, value); }
    public string ResetTimeText { get => _resetTimeText; private set => SetField(ref _resetTimeText, value); }
    public string RunningCountText { get => _runningCountText; private set => SetField(ref _runningCountText, value); }
    public string RunningCycleTotalText { get => _runningCycleTotalText; private set => SetField(ref _runningCycleTotalText, value); }
    public string RunningCycleCompactText { get => _runningCycleCompactText; private set => SetField(ref _runningCycleCompactText, value); }
    public string CycleTotalText { get => _cycleTotalText; private set => SetField(ref _cycleTotalText, value); }
    public string CycleCompactText { get => _cycleCompactText; private set => SetField(ref _cycleCompactText, value); }
    public string LastUpdatedText { get => _lastUpdatedText; private set => SetField(ref _lastUpdatedText, value); }
    public string QuotaSourceText { get => _quotaSourceText; private set => SetField(ref _quotaSourceText, value); }
    public string QuotaStateText { get => _quotaStateText; private set => SetField(ref _quotaStateText, value); }
    public string QuotaStateDetailText { get => _quotaStateDetailText; private set => SetField(ref _quotaStateDetailText, value); }
    public string RemainingWindowText { get => _remainingWindowText; private set => SetField(ref _remainingWindowText, value); }
    public string VisibleSessionCountText { get => _visibleSessionCountText; private set => SetField(ref _visibleSessionCountText, value); }
    public string PrimarySessionCountText { get => _primarySessionCountText; private set => SetField(ref _primarySessionCountText, value); }
    public string InternalTaskCountText { get => _internalTaskCountText; private set => SetField(ref _internalTaskCountText, value); }
    public string AppSessionCountText { get => _appSessionCountText; private set => SetField(ref _appSessionCountText, value); }
    public string CliSessionCountText { get => _cliSessionCountText; private set => SetField(ref _cliSessionCountText, value); }
    public string OrphanTaskCountText { get => _orphanTaskCountText; private set => SetField(ref _orphanTaskCountText, value); }
    public string AnalysisEmptyTitle { get => _analysisEmptyTitle; private set => SetField(ref _analysisEmptyTitle, value); }
    public string AnalysisEmptyDetail { get => _analysisEmptyDetail; private set => SetField(ref _analysisEmptyDetail, value); }
    public bool AnalysisEmptyVisible { get => _analysisEmptyVisible; private set => SetField(ref _analysisEmptyVisible, value); }
    public string AnalysisRunningCountText { get => _analysisRunningCountText; private set => SetField(ref _analysisRunningCountText, value); }
    public double QuotaRingValue { get => _quotaRingValue; private set => SetField(ref _quotaRingValue, value); }
    public bool IsRefreshing { get => _isRefreshing; set => SetField(ref _isRefreshing, value); }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }

    public IReadOnlyList<SessionDisplayRow> Rows
    {
        get => _rows;
        private set => SetField(ref _rows, value);
    }
    public IReadOnlyList<ProviderSlotSnapshot> Slots
    {
        get => _slots;
        private set => SetField(ref _slots, value);
    }
    public ProviderSlotSnapshot? SelectedSlot => _selectedSlot;
    public string? SelectedSlotId => _selectedSlot?.SlotId;
    public string SlotDetailTitle { get => _slotDetailTitle; private set => SetField(ref _slotDetailTitle, value); }
    public string SlotDetailPercent { get => _slotDetailPercent; private set => SetField(ref _slotDetailPercent, value); }
    public string SlotDetailBody { get => _slotDetailBody; private set => SetField(ref _slotDetailBody, value); }
    public string SlotDetailStatus { get => _slotDetailStatus; private set => SetField(ref _slotDetailStatus, value); }
    public string SlotDetailSource { get => _slotDetailSource; private set => SetField(ref _slotDetailSource, value); }
    public string SlotDetailObserved { get => _slotDetailObserved; private set => SetField(ref _slotDetailObserved, value); }
    public string SlotDetailAccountHint { get => _slotDetailAccountHint; private set => SetField(ref _slotDetailAccountHint, value); }
    public bool SlotDetailAccountHintVisible => !string.IsNullOrWhiteSpace(SlotDetailAccountHint);
    public string SlotDetailShareNote { get => _slotDetailShareNote; private set => SetField(ref _slotDetailShareNote, value); }
    public bool SlotDetailShareNoteVisible => !string.IsNullOrWhiteSpace(SlotDetailShareNote);

    public IReadOnlyList<SlotWindowRow> SlotDetailWindows
    {
        get => _slotDetailWindows;
        private set => SetField(ref _slotDetailWindows, value);
    }
    public string BindingNote { get => _bindingNote; private set => SetField(ref _bindingNote, value); }
    public bool SlotDetailPinned { get => _slotDetailPinned; set => SetField(ref _slotDetailPinned, value); }
    public bool TopBarNarrowLayout
    {
        get => _topBarNarrowLayout;
        set => SetField(ref _topBarNarrowLayout, value);
    }
    public string CompactLayout
    {
        get => _compactLayout;
        set => SetField(ref _compactLayout, value);
    }
    public bool SelectedSlotSuppliesAnalysis => _selectedSlot?.SuppliesLocalAnalysis == true;
    public HudSnapshot? Snapshot => _snapshot;

    public void Apply(HudFrame frame)
    {
        _snapshot = frame.Snapshot;
        CollapsedText = frame.CollapsedText;
        OverviewText = frame.OverviewText;
        FreshnessText = frame.FreshnessText;
        EventsText = frame.EventsText;
        ThresholdText = frame.ThresholdText;
        UpdateSummary(frame.Snapshot, frame.Snapshot.GeneratedAtUtc);
        UpdateSlots(frame.Snapshot);
        RebuildAnalysisRows();
    }

    public void Apply(HudSnapshot snapshot) => Apply(HudPresentation.BuildFrame(snapshot));

    public void ApplyProviders(ProviderQuotaBoard board)
    {
        if (_snapshot is null) return;
        var now = DateTimeOffset.UtcNow;
        _snapshot = _snapshot with { Providers = board, GeneratedAtUtc = now };
        UpdateSlots(_snapshot);
        UpdateSummary(_snapshot, now);
    }

    public void Tick(DateTimeOffset nowUtc)
    {
        if (_snapshot is null) return;
        var timed = _snapshot with { GeneratedAtUtc = nowUtc };
        CollapsedText = HudPresentation.BuildCollapsedText(timed);
        var quota = HudPresentation.GetUsablePrimaryQuota(timed.Quota);
        var firstLine = quota is null
            ? "额度：不可用 · 官方重置时间：不可用 · 倒计时：不可用"
            : $"额度：已用 {HudPresentation.FormatQuotaPercent(quota.UsedPercent)} · 剩余 {HudPresentation.FormatQuotaPercent(quota.RemainingPercent)}（{quota.Name}） · " +
              $"官方重置 {quota.ResetsAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · " +
              $"倒计时 {HudPresentation.FormatCountdown(quota.ResetsAtUtc - nowUtc)}";
        var separator = OverviewText.IndexOf('\n');
        OverviewText = separator < 0 ? firstLine : firstLine + OverviewText[separator..];
        UpdateSummary(_snapshot, nowUtc);
    }

    public void SetFilter(string filter)
    {
        SetView(filter, null);
    }

    public void SetSort(string sort)
    {
        SetView(null, sort);
    }

    public void SetView(string? filter, string? sort)
    {
        if (!string.IsNullOrWhiteSpace(filter)) _filter = filter;
        if (!string.IsNullOrWhiteSpace(sort)) _sort = sort;
        RefreshRows();
    }

    public bool ToggleChildren(string threadId)
    {
        if (!_frameRows.Any(row => row.ThreadId == threadId && row.HasChildren)) return false;
        var expanded = _expandedParents.Add(threadId);
        if (!expanded) _expandedParents.Remove(threadId);
        _rowViews.Clear();
        RefreshRows();
        return expanded;
    }

    public IReadOnlyList<string> RenderedStrings()
    {
        var values = new List<string>
        {
            CollapsedText, OverviewText, FreshnessText, EventsText, ThresholdText, RemainingText,
            UsedText, CountdownText, CountdownCompactText, ResetTimeText, RunningCountText,
            RunningCycleTotalText, RunningCycleCompactText, CycleTotalText, CycleCompactText, LastUpdatedText, QuotaSourceText,
            QuotaStateText, QuotaStateDetailText, RemainingWindowText, VisibleSessionCountText,
            PrimarySessionCountText, InternalTaskCountText, AppSessionCountText,
            CliSessionCountText, OrphanTaskCountText, SlotDetailTitle, SlotDetailPercent, SlotDetailBody, BindingNote,
            SlotDetailStatus, SlotDetailSource, SlotDetailObserved, SlotDetailAccountHint, SlotDetailShareNote,
            AnalysisEmptyTitle, AnalysisEmptyDetail,
            AnalysisRunningCountText,
        };
        foreach (var windowRow in SlotDetailWindows)
        {
            values.Add(windowRow.Name);
            values.Add(windowRow.RemainingText);
            values.Add(windowRow.ResetText);
        }
        foreach (var slot in Slots)
        {
            values.Add(slot.Label);
            values.Add(slot.GlanceText);
            values.Add(slot.DelayText ?? string.Empty);
            values.Add(slot.StatusText);
            values.Add(slot.SourceDescription);
        }
        foreach (var row in Rows)
        {
            values.AddRange(new[] { row.Name, row.ShortId, row.RoleNickname, row.Project, row.Model,
                row.Tier, row.Status, row.LastActivity, row.RecentUsageLabel,
                row.RecentUsageConfidence, row.CurrentLabel, row.CurrentTooltip,
                row.PostCompactionBaselineText, row.PostCompactionSourceText,
                row.BaselineTrendText, row.EffectiveRunwayText, row.HistoryVolumeText,
                row.StructuralGradeText, row.LifecycleLoadText, row.LifecycleRecentText,
                row.LifecycleRiskText, row.LifecycleRiskDetailText, row.DriftAssessmentText,
                row.DriftAssessmentDetailText,
                row.ContinuationGradeText, row.ContinuationAdviceText,
                row.ContinuationEvidenceText, row.SourceLabel, row.ParentSummaryText,
                row.ChildSummaryText, row.WorkBreakdownText, row.WorkTotalText });
        }
        return values;
    }

    private void RebuildAnalysisRows()
    {
        if (_snapshot is null)
        {
            _frameRows = Array.Empty<SessionDisplayRow>();
            _rowViews.Clear();
            RefreshRows();
            return;
        }

        var sessions = _snapshot.Sessions;
        _frameRows = HudPresentation.BuildRows(_snapshot with { Sessions = sessions });
        _expandedParents.IntersectWith(_frameRows.Where(row => row.HasChildren).Select(row => row.ThreadId));
        _rowViews.Clear();
        RefreshRows();
    }

    private void RefreshRows()
    {
        if (_snapshot is null)
        {
            Rows = Array.Empty<SessionDisplayRow>();
            VisibleSessionCountText = "0 个会话";
            AnalysisRunningCountText = "0";
            UpdateAnalysisEmptyState();
            return;
        }
        var viewKey = (_filter, _sort);
        if (!_rowViews.TryGetValue(viewKey, out var visibleRows))
        {
            IEnumerable<SessionDisplayRow> roots = _filter switch
            {
                "running" => _frameRows.Where(row =>
                    (!row.IsInternalTask || row.IsOrphanInternalTask) && row.EffectiveIsRunning),
                "recent" => _frameRows.Where(row => !row.IsInternalTask),
                "internal" => _frameRows.Where(row => row.IsOrphanInternalTask),
                _ => _frameRows.Where(row => !row.IsInternalTask),
            };
            roots = SortRows(roots);
            if (_filter == "recent") roots = roots.Take(30);
            var children = _frameRows.Where(row => row.ParentThreadId is not null)
                .GroupBy(row => row.ParentThreadId!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => SortRows(group).ToArray(), StringComparer.Ordinal);
            var flattened = new List<SessionDisplayRow>();
            foreach (var root in roots) AppendRow(root, children, flattened, new HashSet<string>(StringComparer.Ordinal));
            visibleRows = flattened;
            _rowViews[viewKey] = visibleRows;
        }
        Rows = visibleRows;
        var topLevelCount = Rows.Count(row => !row.IsInternalTask || row.IsOrphanInternalTask);
        var shownChildren = Rows.Count - topLevelCount;
        VisibleSessionCountText = shownChildren > 0
            ? $"{topLevelCount} 个会话 · 展开 {shownChildren} 个子任务"
            : $"{topLevelCount} 个会话";
        PrimarySessionCountText = _frameRows.Count(row => !row.IsInternalTask).ToString(CultureInfo.InvariantCulture);
        InternalTaskCountText = _frameRows.Count(row => row.IsInternalTask).ToString(CultureInfo.InvariantCulture);
        AppSessionCountText = _frameRows.Count(row => row.Surface == SessionSurface.App).ToString(CultureInfo.InvariantCulture);
        CliSessionCountText = _frameRows.Count(row => row.Surface == SessionSurface.Cli).ToString(CultureInfo.InvariantCulture);
        OrphanTaskCountText = _frameRows.Count(row => row.IsOrphanInternalTask).ToString(CultureInfo.InvariantCulture);
        AnalysisRunningCountText = _frameRows.Count(row =>
                (!row.IsInternalTask || row.IsOrphanInternalTask) && row.EffectiveIsRunning)
            .ToString(CultureInfo.InvariantCulture);
        UpdateAnalysisEmptyState();
    }

    private void UpdateAnalysisEmptyState()
    {
        var snapshot = _snapshot;
        if (snapshot is null)
        {
            AnalysisEmptyVisible = true;
            AnalysisEmptyTitle = "这里暂时没有会话";
            AnalysisEmptyDetail = "可切换筛选，或等待本地记录更新";
            return;
        }

        var rowsEmpty = Rows.Count == 0;
        AnalysisEmptyVisible = rowsEmpty;
        if (!rowsEmpty) return;

        var localCount = snapshot.MachineLocalSessionCount ?? 0;
        var foreignCount = snapshot.ForeignHistoryCount;
        if (snapshot.AccountAnalysisState == AccountAnalysisState.IdentityMismatch)
        {
            AnalysisEmptyTitle = "当前账户分析未改绑";
            AnalysisEmptyDetail = snapshot.PrimaryBindingNote ??
                                  "当前 App 身份已变化，未把旧主目录历史改绑到新身份。本机记录仍保留。";
            return;
        }

        if (localCount == 0)
        {
            AnalysisEmptyTitle = "这里暂时没有会话";
            AnalysisEmptyDetail = "本机索引里没有会话记录。";
            return;
        }

        if (snapshot.Sessions.Count == 0 && foreignCount > 0)
        {
            AnalysisEmptyTitle = "已排除确认他户记录";
            AnalysisEmptyDetail = $"本机另有 {foreignCount} 条确认他户会话未列入当前主目录分析。第二账户与其他服务不进入此列表。";
            return;
        }

        AnalysisEmptyTitle = "这里暂时没有会话";
        AnalysisEmptyDetail = "可切换筛选，或等待本地记录更新";
    }

    private IEnumerable<SessionDisplayRow> SortRows(IEnumerable<SessionDisplayRow> rows) => _sort == "activity"
        ? rows.OrderByDescending(row => row.IsPinned)
            .ThenByDescending(row => row.LastActivityUtc ?? DateTimeOffset.MinValue)
        : rows.OrderByDescending(row => row.IsPinned)
            .ThenByDescending(row => row.WorkTotal.Total);

    private void AppendRow(SessionDisplayRow row,
        IReadOnlyDictionary<string, SessionDisplayRow[]> children,
        ICollection<SessionDisplayRow> output, ISet<string> path)
    {
        if (!path.Add(row.ThreadId)) return;
        var expanded = row.HasChildren && _expandedParents.Contains(row.ThreadId);
        output.Add(row with { IsExpanded = expanded });
        if (expanded && children.TryGetValue(row.ThreadId, out var childRows))
        {
            foreach (var child in childRows) AppendRow(child, children, output, path);
        }
        path.Remove(row.ThreadId);
    }

    private void UpdateSummary(HudSnapshot snapshot, DateTimeOffset nowUtc)
    {
        var quota = HudPresentation.GetUsablePrimaryQuota(snapshot.Quota);
        var threshold = HudPresentation.GetThreshold(snapshot.Quota);
        if (quota is null)
        {
            RemainingText = "—";
            RemainingWindowText = "剩余额度";
            UsedText = "不可用";
            CountdownText = "不可用";
            CountdownCompactText = "不可用";
            ResetTimeText = "官方时间不可用";
            QuotaRingValue = 0;
        }
        else
        {
            var remaining = quota.ResetsAtUtc - nowUtc;
            RemainingText = HudPresentation.FormatQuotaPercent(quota.RemainingPercent);
            RemainingWindowText = string.IsNullOrWhiteSpace(quota.Name) ? "剩余额度" : quota.Name;
            UsedText = HudPresentation.FormatQuotaPercent(quota.UsedPercent);
            CountdownText = HudPresentation.FormatCountdown(remaining);
            CountdownCompactText = FormatCompactCountdown(remaining);
            ResetTimeText = quota.ResetsAtUtc.ToLocalTime().ToString("MM月dd日 HH:mm");
            QuotaRingValue = quota.RemainingPercent;
        }

        RunningCountText = (snapshot.MachineLocalRunningCount ?? snapshot.Sessions.Count(HudPresentation.IsRunning))
            .ToString();
        RunningCycleTotalText = snapshot.RunningCycleTotal.HasValue
            ? $"本周期 {TokenFormat.Compact(snapshot.RunningCycleTotal.Value.Total)} raw tokens"
            : "本周期不可用";
        RunningCycleCompactText = snapshot.RunningCycleTotal.HasValue
            ? FormatDialTokens(snapshot.RunningCycleTotal.Value.Total)
            : "—";
        CycleCompactText = snapshot.CycleTotal.HasValue
            ? FormatDialTokens(snapshot.CycleTotal.Value.Total)
            : "—";
        CycleTotalText = snapshot.CycleTotal.HasValue
            ? $"{CycleCompactText} raw tokens"
            : "不可用";
        LastUpdatedText = snapshot.GeneratedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        QuotaSourceText = quota is null
            ? "官方额度不可用"
            : snapshot.Quota.Source switch
            {
                QuotaSource.OfficialAppServer => snapshot.Quota.IsStale ? "官方额度 · 观测已陈旧" : "官方额度",
                QuotaSource.RolloutFallback => "本机最后观测",
                _ => "官方额度不可用",
            };
        QuotaStateText = threshold.Code switch
        {
            "critical" => "额度紧急",
            "warning" => "额度偏低",
            "normal" => "正常",
            "stale" => "数据陈旧",
            _ => "不可用",
        };
        QuotaStateDetailText = threshold.Text;
        BindingNote = snapshot.PrimaryBindingNote ?? "仅当前绑定的 Codex App 槽提供会话分析。主目录已由所有者确认。";
    }

    public void SelectSlot(string? slotId, bool pin)
    {
        if (string.IsNullOrWhiteSpace(slotId) || _snapshot is null)
        {
            _selectedSlot = null;
            ClearSlotDetail("尚未选择账户");
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedSlot)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedSlotId)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedSlotSuppliesAnalysis)));
            return;
        }

        var board = HudPresentation.BoardForSnapshot(_snapshot);
        var slot = board.Find(slotId);
        _selectedSlot = slot;
        if (pin) SlotDetailPinned = true;
        if (slot is null)
        {
            ClearSlotDetail("未找到该槽");
        }
        else
        {
            SlotDetailTitle = slot.ShortAlias;
            SlotDetailPercent = slot.GlancePercentText;
            SlotDetailStatus = slot.UpdateDelayed
                ? "更新延迟"
                : ProviderQuotaPresentation.StatusCaption(slot.Status);
            SlotDetailSource = "来源：" + slot.SourceDescription;
            var observedAge = ProviderQuotaPresentation.FormatAge(slot.ObservationAge(_snapshot.GeneratedAtUtc));
            SlotDetailObserved = slot.UpdateDelayed
                ? "观测：" + observedAge + " · 更新延迟"
                : "观测：" + observedAge;
            SlotDetailAccountHint = SlotAccountHint(slot);
            SlotDetailWindows = BuildWindowRows(slot, _snapshot.GeneratedAtUtc);
            SlotDetailShareNote = ShareNote(slot);
            SlotDetailBody = BuildSlotDetail(slot, _snapshot.GeneratedAtUtc);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SlotDetailAccountHintVisible)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SlotDetailShareNoteVisible)));
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedSlot)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedSlotId)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedSlotSuppliesAnalysis)));
    }

    private void ClearSlotDetail(string body)
    {
        SlotDetailTitle = "额度详情";
        SlotDetailPercent = "—";
        SlotDetailStatus = "";
        SlotDetailSource = "";
        SlotDetailObserved = "";
        SlotDetailAccountHint = "";
        SlotDetailShareNote = "";
        SlotDetailWindows = Array.Empty<SlotWindowRow>();
        SlotDetailBody = body;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SlotDetailAccountHintVisible)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SlotDetailShareNoteVisible)));
    }

    public bool ClearSlotSelectionIfUnpinned()
    {
        if (SlotDetailPinned) return false;
        SelectSlot(null, false);
        return true;
    }

    private void UpdateSlots(HudSnapshot snapshot)
    {
        var board = HudPresentation.BoardForSnapshot(snapshot);
        Slots = board.Slots;
        if (_selectedSlot is not null)
            SelectSlot(_selectedSlot.SlotId, SlotDetailPinned);
    }

    private static string BuildSlotDetail(ProviderSlotSnapshot slot, DateTimeOffset nowUtc)
    {
        var lines = new List<string>
        {
            slot.StatusText,
            "来源：" + slot.SourceDescription,
            "观测：" + ProviderQuotaPresentation.FormatAge(slot.ObservationAge(nowUtc)),
        };
        var hint = SlotAccountHint(slot);
        if (!string.IsNullOrWhiteSpace(hint))
            lines.Add(hint);
        if (!slot.SuppliesLocalAnalysis)
            lines.Add("此槽不提供会话或续接分析。");
        var share = ShareNote(slot);
        if (!string.IsNullOrWhiteSpace(share))
            lines.Add(share);
        if (slot.Windows.Count == 0)
            lines.Add(slot.Status == QuotaSlotStatus.NoAllowance
                ? "没有计入计划的额度，不显示 100% 可用。"
                : "没有可展示的额度窗口。");
        else
            lines.AddRange(slot.Windows.Select(window =>
            {
                var line = ProviderQuotaPresentation.FormatWindowLine(window, nowUtc);
                return slot.UpdateDelayed && window.HasUsablePercent ? line + " · 旧值" : line;
            }));
        if (slot.UpdateDelayed && !string.IsNullOrWhiteSpace(slot.ErrorCode))
            lines.Add("状态码 " + slot.ErrorCode);
        if (slot.ForeignHistoryCount > 0 || slot.UnverifiedHistoryCount > 0)
        {
            lines.Add($"确认他户 {slot.ForeignHistoryCount} 已排除；行级 account_id 未匹配 {slot.UnverifiedHistoryCount}（目录绑定不是机器核验）");
        }

        return string.Join('\n', lines);
    }

    private static string ShareNote(ProviderSlotSnapshot slot)
    {
        if (slot.SlotId is ProviderSlotIds.Cursor or ProviderSlotIds.GrokBot &&
            slot.Windows.Any(window => window.WindowId == SharedWeeklyQuota.WindowId))
            return SharedWeeklyQuota.SharedSeatNote;
        if (slot.SlotId == ProviderSlotIds.Grok)
            return SharedWeeklyQuota.GrokSeatNote;
        return "";
    }

    private static string SlotAccountHint(ProviderSlotSnapshot slot)
    {
        if (slot.Status == QuotaSlotStatus.IdentityCollision)
            return "与另一槽为同一账户";
        var hasIdentity = !string.IsNullOrWhiteSpace(slot.OpaqueIdentityHash);
        if (slot.SuppliesLocalAnalysis)
            return hasIdentity ? "当前绑定账户" : "主目录已确认绑定";
        return "";
    }

    private static IReadOnlyList<SlotWindowRow> BuildWindowRows(ProviderSlotSnapshot slot, DateTimeOffset nowUtc)
    {
        if (slot.Windows.Count == 0)
        {
            var empty = slot.Status == QuotaSlotStatus.NoAllowance
                ? "没有计入计划的额度"
                : "没有可展示的额度窗口";
            return new[] { new SlotWindowRow(empty, "—", slot.StatusText, 0, false, true) };
        }

        var live = slot.Status == QuotaSlotStatus.Live;
        return slot.Windows.Select(window =>
        {
            var reset = ProviderQuotaPresentation.FormatReset(window, nowUtc);
            if (window.ResetsAtUtc is { } resetAt && resetAt <= nowUtc)
                return new SlotWindowRow(window.DisplayName, "已到期", reset, 0, false, true);
            if (!window.HasUsablePercent)
                return new SlotWindowRow(window.DisplayName, "—", reset, 0, false, true);

            var remaining = window.RemainingPercent!.Value;
            var remainingText = ProviderQuotaPresentation.FormatPercent(window, remaining);
            if (slot.UpdateDelayed)
                remainingText += " · 旧值";
            else if (!live)
                remainingText += " · 陈旧";
            return new SlotWindowRow(window.DisplayName + " · 剩余", remainingText, reset, remaining, live, !live);
        }).ToArray();
    }

    private static string FormatCompactCountdown(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero) return "已到期";
        if (remaining.Days > 0) return $"{remaining.Days}天{remaining.Hours}时";
        if (remaining.Hours > 0) return $"{remaining.Hours}时{remaining.Minutes}分";
        return $"{Math.Max(remaining.Minutes, 1)}分";
    }

    private static string FormatDialTokens(long value)
    {
        if (value >= 1_000_000_000)
            return $"{(value / 1_000_000_000d).ToString("0.00", CultureInfo.InvariantCulture)}B";
        if (value >= 100_000_000)
            return $"{(value / 1_000_000d).ToString("0", CultureInfo.InvariantCulture)}M";
        return TokenFormat.Compact(value);
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

// Display formatting only: precise counts remain available in the inspector
// and tooltips. All arithmetic continues to use the original token values.
public sealed record SlotWindowRow(
    string Name,
    string RemainingText,
    string ResetText,
    double ProgressValue,
    bool HasLiveProgress,
    bool HasDashedTrack);

public sealed class SlotSelectedConverter : System.Windows.Data.IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not string slotId || values[1] is not string selected)
            return false;
        return string.Equals(slotId, selected, StringComparison.Ordinal);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class CompactTokenConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is long count ? TokenFormat.Compact(count) : "—";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;
}
