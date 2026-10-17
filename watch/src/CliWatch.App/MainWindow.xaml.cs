// SPDX-License-Identifier: MPL-2.0
using System.Windows;
using System.Windows.Input;
using CliWatch.Contracts;
using CliWatch.Views;

namespace CliWatch.App;

public partial class MainWindow : Window
{
    readonly CockpitHostModel _host;
    readonly RefreshService _refresh;
    readonly AppConfig _config;
    readonly PreferenceStore _prefs;
    readonly LaunchOptions _launch;
    readonly NavigationService _navigation = new();
    readonly object _refreshGate = new();
    bool _refreshing;
    bool _collapsed = true;
    bool _viewsReady;
    bool _captured;
    int _refreshCount;
    string? _pendingCopyId;
    WindowPreferences _live;
    UiLang _lang = UiLang.Zh;

    public MainWindow(
        CockpitHostModel host,
        RefreshService refresh,
        AppConfig config,
        PreferenceStore prefs,
        WindowPreferences preferences,
        LaunchOptions? launch = null)
    {
        _host = host;
        _refresh = refresh;
        _config = config;
        _prefs = prefs;
        _launch = launch ?? new LaunchOptions();
        _live = preferences;
        _lang = ConsumerCopy.Parse(preferences.Language);
        _live.Language = ConsumerCopy.Code(_lang);
        InitializeComponent();
        Title = ProductPaths.ProductTitle + " · " + ProductPaths.ProductTitleZh;
        TitleText.Text = ProductPaths.ProductTitleZh;
        ApplyPreferences(preferences);
        Hud.NavigationRequested += OnNavigationRequested;
        Details.NavigationRequested += OnNavigationRequested;
        ViewSelectionBinder.TryWireSelectionChanged(Hud, OnViewSelection);
        Closed += (_, _) => PersistPreferences();
        LocationChanged += (_, _) => CaptureGeometry();
        SizeChanged += (_, _) => CaptureGeometry();
        MouseLeftButtonDown += OnDrag;
    }

    public CockpitHostModel Host => _host;

    public async Task StartRefreshLoopAsync(CancellationToken cancellationToken)
    {
        await RefreshOnceAsync(cancellationToken).ConfigureAwait(true);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_config.RefreshSeconds), cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await RefreshOnceAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        await RefreshOnceAsync(CancellationToken.None).ConfigureAwait(true);
    }

    async Task RefreshOnceAsync(CancellationToken cancellationToken)
    {
        lock (_refreshGate)
        {
            if (_refreshing) return;
            _refreshing = true;
        }

        try
        {
            RefreshOutcome outcome;
            try
            {
                outcome = await Task.Run(
                    () => _refresh.RefreshAsync(_config.ToCollectorSettings(), cancellationToken),
                    cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (outcome.Canceled) return;
            ApplyOutcome(outcome);
            _refreshCount++;
            if (_refreshCount >= 2)
                await CaptureIfRequestedAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText.Text = ConsumerCopy.T(_lang, "refresh_fail_keep") + ex.GetType().Name;
        }
        finally
        {
            lock (_refreshGate) { _refreshing = false; }
        }
    }

    void SyncCopySelectedId()
    {
        if (string.IsNullOrWhiteSpace(_host.SelectedProjectId))
        {
            if (string.IsNullOrEmpty(_pendingCopyId))
                CopyIdButton.Visibility = Visibility.Collapsed;
            return;
        }

        // Default surface does not expose internal IDs. The copy button is only
        // shown when a navigation outcome already asked to copy a target.
        if (string.IsNullOrEmpty(_pendingCopyId))
            CopyIdButton.Visibility = Visibility.Collapsed;
    }

    void ApplyOutcome(RefreshOutcome outcome)
    {
        if (outcome.Snapshot is null) return;
        _host.ApplySnapshot(outcome.Snapshot);
        PushSnapshotToViews();
        // Background refresh must not Activate/Focus or become foreground.
        StatusText.Text = DescribeStatus(outcome);
    }

    void PushSnapshotToViews()
    {
        if (_host.Snapshot is null) return;
        var hudOk = ViewSelectionBinder.TrySetSnapshot(Hud, _host.Snapshot, out var hudErr);
        var detailsOk = ViewSelectionBinder.TrySetSnapshot(Details, _host.Snapshot, out var detailsErr);
        ViewSelectionBinder.TrySelectProject(Hud, _host.SelectedProjectId);
        ViewSelectionBinder.TrySelectProject(Details, _host.SelectedProjectId);
        SyncCopySelectedId();
        _viewsReady = hudOk && detailsOk;
        if (!_viewsReady)
        {
            HudFallback.Visibility = Visibility.Visible;
            HudFallback.Text = BuildFallbackText(hudErr ?? detailsErr);
            DetailsFallback.Visibility = Visibility.Visible;
            DetailsFallback.Text = BuildFallbackText(detailsErr ?? hudErr);
        }
        else
        {
            HudFallback.Visibility = Visibility.Collapsed;
            DetailsFallback.Visibility = Visibility.Collapsed;
        }
    }

    string BuildFallbackText(string? error)
    {
        var snap = _host.Snapshot;
        var count = snap?.Projects.Count ?? 0;
        var quality = snap?.Quality.ToString() ?? "none";
        return ConsumerCopy.Localize(
            "宿主已持有同一快照（项目 " + count + "，质量 " + quality + "）。"
            + (string.IsNullOrEmpty(error) ? "" : " Views：" + error + "。")
            + " 刷新与历史入口使用同一 Snapshot，App 不重判状态。",
            _lang);
    }

    string DescribeStatus(RefreshOutcome outcome)
    {
        if (outcome.Succeeded)
        {
            var snap = outcome.Snapshot;
            if (snap is null) return ConsumerCopy.T(_lang, "refresh_no_snap");
            return StatusLanguage.SnapshotQualityText(snap.Quality, _lang)
                + " · " + StatusLanguage.CurrentWindowLine(snap, _lang);
        }
        if (outcome.KeptLastKnown)
            return ConsumerCopy.T(_lang, "refresh_fail_kept") + (outcome.Issue is { } i ? " " + i.Code + "：" + i.Detail : "");
        return ConsumerCopy.T(_lang, "refresh_fail_none") + (outcome.Issue is { } x ? " " + x.Detail : "");
    }

    void OnViewSelection(string? projectId)
    {
        if (!_host.SelectProject(projectId)) return;
        ViewSelectionBinder.TrySelectProject(Details, _host.SelectedProjectId);
        SyncCopySelectedId();
    }

    void OnNavigationRequested(object? sender, NavigationRequestEventArgs e)
    {
        var outcome = _navigation.TryNavigate(_host.Snapshot, e.Target);
        NavMessageText.Visibility = Visibility.Visible;
        NavMessageText.Text = ConsumerCopy.Localize(outcome.Message, _lang);
        if (outcome.CopyText is { } id)
        {
            _pendingCopyId = id;
            CopyIdButton.Visibility = Visibility.Visible;
        }
        else
        {
            _pendingCopyId = null;
            CopyIdButton.Visibility = Visibility.Collapsed;
        }
    }

    void OnCopyHelpClick(object sender, RoutedEventArgs e)
    {
        var summary = HelpSummaryBuilder.Build(_host.Snapshot, _host.SelectedProjectId, _lang);
        try
        {
            Clipboard.SetText(summary);
            StatusText.Text = ConsumerCopy.T(_lang, "copied_summary");
        }
        catch (Exception ex)
        {
            StatusText.Text = ConsumerCopy.T(_lang, "clipboard_fail") + ex.Message;
        }
    }

    void OnCopyIdClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_pendingCopyId)) return;
        try
        {
            Clipboard.SetText(_pendingCopyId);
            StatusText.Text = ConsumerCopy.T(_lang, "copied_id");
        }
        catch (Exception ex)
        {
            StatusText.Text = ConsumerCopy.T(_lang, "clipboard_fail") + ex.Message;
        }
    }

    void OnTopmostClick(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        _live.Topmost = Topmost;
        ApplyChrome();
        PersistPreferences();
    }

    void OnLangZhClick(object sender, RoutedEventArgs e) => SetLanguage(UiLang.Zh);

    void OnLangEnClick(object sender, RoutedEventArgs e) => SetLanguage(UiLang.En);

    void SetLanguage(UiLang lang)
    {
        _lang = lang;
        _live.Language = ConsumerCopy.Code(lang);
        ApplyChrome();
        PushSnapshotToViews();
        if (_host.Snapshot is not null)
            StatusText.Text = StatusLanguage.RefreshStatusLine(_host.Snapshot, _lang);
        PersistPreferences();
    }

    void OnCollapseClick(object sender, RoutedEventArgs e)
    {
        _collapsed = !_collapsed;
        ApplyCollapsed(_collapsed);
        PersistPreferences();
    }

    void ApplyPreferences(WindowPreferences prefs)
    {
        Left = prefs.Left;
        Top = prefs.Top;
        Topmost = prefs.Topmost;
        _collapsed = prefs.Collapsed;
        ApplyCollapsed(_collapsed);
        if (_collapsed)
        {
            Width = prefs.Width;
            Height = prefs.Height;
        }
        else
        {
            Width = prefs.ExpandedWidth;
            Height = prefs.ExpandedHeight;
        }
        ApplyChrome();
    }

    async Task CaptureIfRequestedAsync()
    {
        if (_captured) return;
        if (string.IsNullOrWhiteSpace(_launch.ScreenshotPath)
            && string.IsNullOrWhiteSpace(_launch.ScreenshotExpandedPath)
            && string.IsNullOrWhiteSpace(_launch.ScreenshotHistoryPath))
        {
            return;
        }

        _captured = true;
        try
        {
            await Task.Delay(400).ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(_launch.ScreenshotPath))
            {
                ApplyCollapsed(true);
                Width = _live.Width > 0 ? Math.Max(_live.Width, MinWidth) : MinWidth;
                Height = _live.Height > 0 ? Math.Max(_live.Height, MinHeight) : MinHeight;
                UpdateLayout();
                WriteSizeSidecar(_launch.ScreenshotPath);
                WindowCapture.SavePng(this, _launch.ScreenshotPath);
            }

            if (!string.IsNullOrWhiteSpace(_launch.ScreenshotExpandedPath))
            {
                ApplyCollapsed(false);
                if (_host.Snapshot?.Projects.Count > 0)
                {
                    var current = CurrentList.DefaultItems(_host.Snapshot);
                    var first = CurrentList.ResolveSelection(_host.Snapshot, _host.SelectedProjectId, showHistory: false)
                                ?? current.FirstOrDefault()?.Id
                                ?? _host.Snapshot.Projects[0].Id;
                    _host.SelectProject(first);
                    PushSnapshotToViews();
                }

                UpdateLayout();
                await Task.Delay(250).ConfigureAwait(true);
                WriteSizeSidecar(_launch.ScreenshotExpandedPath);
                WindowCapture.SavePng(this, _launch.ScreenshotExpandedPath);
            }

            if (!string.IsNullOrWhiteSpace(_launch.ScreenshotHistoryPath))
            {
                ApplyCollapsed(false);
                Hud.SetShowHistory(true);
                if (_host.Snapshot is not null)
                {
                    var historical = CurrentList.HistoricalItems(_host.Snapshot);
                    var hist = historical.FirstOrDefault(p =>
                                   p.Name.StartsWith("demo-project", StringComparison.OrdinalIgnoreCase)
                                   && p.Phase is "retired")
                               ?? historical.FirstOrDefault(p => p.Phase is "retired")
                               ?? historical.FirstOrDefault(p =>
                                   p.Name.StartsWith("demo-project", StringComparison.OrdinalIgnoreCase)
                                   && p.Phase is "delivered" or "failed")
                               ?? historical.FirstOrDefault(p => p.Phase is "delivered" or "failed")
                               ?? historical.FirstOrDefault()
                               ?? _host.Snapshot.Projects.FirstOrDefault();
                    if (hist is not null)
                    {
                        _host.SelectProject(hist.Id);
                        PushSnapshotToViews();
                        Hud.SetShowHistory(true);
                    }
                }

                UpdateLayout();
                await Task.Delay(250).ConfigureAwait(true);
                WriteSizeSidecar(_launch.ScreenshotHistoryPath);
                WindowCapture.SavePng(this, _launch.ScreenshotHistoryPath);
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "截图失败：" + ex.Message;
        }

        if (_launch.ExitAfterCapture)
        {
            Close();
        }
    }

    void WriteSizeSidecar(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return;
        try
        {
            var text =
                "ActualWidth=" + ActualWidth.ToString("0.##") + Environment.NewLine
                + "ActualHeight=" + ActualHeight.ToString("0.##") + Environment.NewLine
                + "Width=" + Width.ToString("0.##") + Environment.NewLine
                + "Height=" + Height.ToString("0.##") + Environment.NewLine
                + "MinWidth=" + MinWidth.ToString("0.##") + Environment.NewLine
                + "MinHeight=" + MinHeight.ToString("0.##") + Environment.NewLine
                + "Collapsed=" + _collapsed + Environment.NewLine;
            System.IO.File.WriteAllText(imagePath + ".size.txt", text);
        }
        catch (Exception)
        {
        }
    }

    void ApplyChrome()
    {
        this.Language = System.Windows.Markup.XmlLanguage.GetLanguage(_lang == UiLang.En ? "en-US" : "zh-CN");
        TitleText.Text = _lang == UiLang.En ? ProductPaths.ProductTitle : ProductPaths.ProductTitleZh;
        RefreshButton.Content = ConsumerCopy.T(_lang, "refresh");
        RefreshButton.ToolTip = ConsumerCopy.T(_lang, "refresh_tip");
        CopyHelpButton.Content = ConsumerCopy.T(_lang, "copy_summary");
        CopyHelpButton.ToolTip = ConsumerCopy.T(_lang, "copy_summary_tip");
        CopyIdButton.Content = ConsumerCopy.T(_lang, "copy_id");
        TopmostButton.Content = ConsumerCopy.T(_lang, Topmost ? "untopmost" : "topmost");
        CollapseButton.Content = ConsumerCopy.T(_lang, _collapsed ? "expand" : "collapse");
        LangZhButton.FontWeight = _lang == UiLang.Zh ? FontWeights.SemiBold : FontWeights.Normal;
        LangEnButton.FontWeight = _lang == UiLang.En ? FontWeights.SemiBold : FontWeights.Normal;
        System.Windows.Automation.AutomationProperties.SetName(DetailsHost, ConsumerCopy.T(_lang, "details_host"));
        if (string.IsNullOrWhiteSpace(StatusText.Text) || StatusText.Text == "等待首次刷新。" || StatusText.Text == "Waiting for the first refresh.")
            StatusText.Text = ConsumerCopy.T(_lang, "wait_first");
        Hud.SetLanguage(_lang);
        Details.SetLanguage(_lang);
    }

    void ApplyCollapsed(bool collapsed)
    {
        _collapsed = collapsed;
        _live.Collapsed = collapsed;
        if (collapsed)
        {
            DetailsColumn.Width = new GridLength(0);
            DetailsHost.Visibility = Visibility.Collapsed;
            MinWidth = 280;
            MinHeight = 240;
        }
        else
        {
            DetailsColumn.Width = new GridLength(1, GridUnitType.Star);
            DetailsHost.Visibility = Visibility.Visible;
            MinWidth = 640;
            MinHeight = 360;
            if (Width < _live.ExpandedWidth) Width = _live.ExpandedWidth;
            if (Height < _live.ExpandedHeight) Height = _live.ExpandedHeight;
        }
        ApplyChrome();
    }

    void CaptureGeometry()
    {
        if (_live is null) return;
        _live.Left = Left;
        _live.Top = Top;
        if (_collapsed)
        {
            _live.Width = Width;
            _live.Height = Height;
        }
        else
        {
            _live.ExpandedWidth = Width;
            _live.ExpandedHeight = Height;
        }
        _live.Topmost = Topmost;
        _live.Collapsed = _collapsed;
        _live.Language = ConsumerCopy.Code(_lang);
    }

    void PersistPreferences()
    {
        if (_launch.ExitAfterCapture) return;
        CaptureGeometry();
        try { _prefs.Save(_live); }
        catch (Exception)
        {
        }
    }

    void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            try { DragMove(); }
            catch (InvalidOperationException) { }
        }
    }
}
