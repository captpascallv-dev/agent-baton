using CodexUsageHud.Core;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace CodexUsageHud.App;

public partial class MainWindow : Window, IDisposable
{
    private enum EdgeDock
    {
        None,
        Left,
        Right,
        Top,
    }

    private readonly UsageEngine _engine;
    private readonly Func<Task> _disposeRuntimeAsync;
    private readonly MainViewModel _viewModel = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Forms.NotifyIcon _tray;
    private readonly Forms.ToolStripMenuItem _startupMenuItem;
    private readonly DispatcherTimer _countdownTimer;
    private readonly DispatcherTimer _hideTimer;
    private readonly DispatcherTimer _revealTimer;
    private Drawing.Icon? _ownedTrayIcon;
    private Task _backgroundLoopTask = Task.CompletedTask;
    private Task? _exitTask;
    private int _refreshPending;
    private int _manualRefreshPending;
    private int _driftWritePending;
    private DispatcherOperation? _viewChangeOperation;
    private string? _pendingFilter;
    private string? _pendingSort;
    private bool _exiting;
    private bool _disposed;
    private bool _settingsReady;
    private bool _isEdgeHidden;
    private bool _autoHide = true;
    private bool _restoreHiddenOnNextCompact;
    private bool _topmostPointerInvocation;
    private bool _edgeRevealElevated;
    private bool _isExpandedFullscreen;
    private EdgeDock _dockSide = EdgeDock.Right;
    private string _compactLayout = CompactLayoutModes.Rail;
    private readonly Dictionary<string, string?> _restoredSettings = new(StringComparer.Ordinal);
    private bool _slotDetailOpen;
    private double _compactAxis = double.NaN;
    private Rect _expandedRestoreBounds = Rect.Empty;
    private Rect? _workAreaOverride;

    private const double CompactWidth = 224;
    private const double CompactHeight = 324;
    private const double CompactTopWidth = 660;
    private const double CompactTopHeight = 80;
    private const double RailWidth = 252;
    private const double RailHeightFallback = 520;
    private const double RailTopWidth = 1100;
    private const double RailTopHeight = 78;
    private const double RailTopDelayHeight = 100;
    private const double RailTopNarrowHeight = 96;
    private const double RailTopNarrowDelayHeight = 112;
    private const double RailTopNarrowBreakpoint = 900;
    private const double ExpandedWidth = 1180;
    private const double ExpandedHeight = 820;
    private const double EdgeHandle = 9;
    private const double MinimumVisible = 48;
    private const double DockSnapDistance = 30;
    private const double PointerDockSnapDistance = 64;
    private const double WorkAreaMargin = 12;

    public MainWindow(UsageEngine engine, Func<Task> disposeRuntimeAsync,
        bool startRuntimeOnLoad = true)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyNativeWindowClarity();
        _engine = engine;
        _engine.ProvidersUpdated += OnEngineProvidersUpdated;
        _disposeRuntimeAsync = disposeRuntimeAsync;
        DataContext = _viewModel;
        var tray = CreateTrayIcon();
        _tray = tray.Icon;
        _startupMenuItem = tray.StartupItem;

        _countdownTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = RefreshCadence.UiCountdown,
        };
        _countdownTimer.Tick += (_, _) =>
        {
            _viewModel.Tick(DateTimeOffset.UtcNow);
            _viewModel.ApplyProviders(_engine.GetProviderBoard());
        };
        _hideTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(520),
        };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            if (!IsMouseOver)
            {
                SetEdgeHidden(true);
                return;
            }

            // DragMove can swallow the first MouseLeave after a side snap. Keep a
            // low-frequency guard alive until the pointer really leaves the card.
            if (_autoHide && !_viewModel.IsExpanded && _dockSide != EdgeDock.None && IsVisible)
                _hideTimer.Start();
        };
        _revealTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(110),
        };
        _revealTimer.Tick += (_, _) =>
        {
            _revealTimer.Stop();
            RevealEdgeFromPointer();
        };
        MouseEnter += OnWindowMouseEnter;
        MouseLeave += OnWindowMouseLeave;

        ApplyExpansionState();
        UpdateTopmostState();
        UpdateFullscreenState();
        if (startRuntimeOnLoad) Loaded += OnLoaded;
        Closing += OnClosing;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    public void OverrideWorkAreaForTests(Rect? workArea) => _workAreaOverride = workArea;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await RestoreWindowStateAsync();
        ApplyExpansionState();
        EnsureVisibleOnCurrentScreens();
        _countdownTimer.Start();
        _backgroundLoopTask = BackgroundLoopAsync(_shutdown.Token);
        await RefreshAsync(true);
        UpdateTrayIconFromDial();
        ScheduleEdgeHide();
    }

    private async Task BackgroundLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(RefreshCadence.AppendScan);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
                await RefreshAsync(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            await WriteLogSafelyAsync("background_loop_failed");
        }
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync(true);
    private void OnCloseToTray(object sender, RoutedEventArgs e) => Close();

    private async Task RefreshAsync(bool manualQuota)
    {
        if (_disposed || _exiting) return;
        Interlocked.Exchange(ref _refreshPending, 1);
        if (manualQuota) Interlocked.Exchange(ref _manualRefreshPending, 1);
        if (!await _refreshGate.WaitAsync(0)) return;
        try
        {
            while (Interlocked.Exchange(ref _refreshPending, 0) != 0 && !_disposed && !_exiting)
            {
                var manual = Interlocked.Exchange(ref _manualRefreshPending, 0) != 0;
                await RefreshCoreAsync(manual);
            }
        }
        finally
        {
            _refreshGate.Release();
            if (Volatile.Read(ref _refreshPending) != 0 && !_disposed && !_exiting)
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action(() => _ = RefreshAsync(false)));
        }
    }

    private async Task RefreshCoreAsync(bool manualQuota)
    {
        string? selectedThreadId = null;
        await Dispatcher.InvokeAsync(() =>
        {
            selectedThreadId = (SessionGrid.SelectedItem as SessionDisplayRow)?.ThreadId;
            _viewModel.IsRefreshing = true;
            RefreshButton.IsEnabled = false;
            RefreshButton.ToolTip = "刷新中…";
        }, DispatcherPriority.Input);
        try
        {
            var frame = await _engine.RefreshAsync(manualQuota, _shutdown.Token);
            await Dispatcher.InvokeAsync(() =>
            {
                _viewModel.Apply(frame);
                if (selectedThreadId is not null)
                {
                    var selected = _viewModel.Rows.FirstOrDefault(row =>
                        string.Equals(row.ThreadId, selectedThreadId, StringComparison.Ordinal));
                    if (selected is not null) SessionGrid.SelectedItem = selected;
                }
                UpdateTextBlocks();
                RemeasureCompactRail();
            }, DispatcherPriority.DataBind);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            if (!_disposed)
            {
                await WriteLogSafelyAsync("refresh_failed");
                await ApplyUnavailableAsync();
            }
        }
        finally
        {
            if (!_disposed)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    _viewModel.IsRefreshing = false;
                    RefreshButton.IsEnabled = true;
                    RefreshButton.ToolTip = "刷新额度与本地索引";
                }, DispatcherPriority.Input);
            }
        }
    }

    private async Task ApplyUnavailableAsync()
    {
        var snapshot = new HudSnapshot(
            new QuotaObservation(null, Array.Empty<QuotaBucket>(), QuotaSource.Unavailable,
                DateTimeOffset.UtcNow, false, "refresh_failed"),
            Array.Empty<SessionAggregate>(), null, DateTimeOffset.UtcNow, false,
            "来源暂时不可用", new[] { "refresh_failed" }, Array.Empty<HudEvent>());
        var frame = await Task.Run(() => HudPresentation.BuildFrame(snapshot));
        await Dispatcher.InvokeAsync(() =>
        {
            _viewModel.Apply(frame);
            UpdateTextBlocks();
            RemeasureCompactRail();
        });
    }

    private void UpdateTextBlocks()
    {
        var threshold = HudPresentation.GetThreshold(_viewModel.Snapshot?.Quota ??
            new QuotaObservation(null, Array.Empty<QuotaBucket>(), QuotaSource.Unavailable,
                DateTimeOffset.UtcNow, false));
        var accent = threshold.Code switch
        {
            "critical" => System.Windows.Media.Color.FromRgb(181, 40, 45),
            "warning" => System.Windows.Media.Color.FromRgb(151, 92, 0),
            "normal" => System.Windows.Media.Color.FromRgb(20, 108, 75),
            _ => System.Windows.Media.Color.FromRgb(101, 116, 107),
        };
        var accentBrush = new SolidColorBrush(accent);
        if (accentBrush.CanFreeze) accentBrush.Freeze();
        var borderBrush = threshold.Code is "critical" or "warning"
            ? accentBrush : (System.Windows.Media.Brush)FindResource("HudLine");
        QuotaPercentText.Foreground = accentBrush;
        CompactQuotaStateText.Foreground = accentBrush;
        CompactTopRemainingText.Foreground = accentBrush;
        ExpandedRemainingText.Foreground = accentBrush;
        ThresholdStateText.Foreground = accentBrush;
        OverviewProgress.Foreground = accentBrush;
        CompactProgress.Foreground = accentBrush;
        CompactVerticalShell.BorderBrush = borderBrush;
        CompactTopShell.BorderBrush = borderBrush;
        RailVerticalShell.BorderBrush = borderBrush;
        RailTopShell.BorderBrush = borderBrush;
        ExpandedShell.BorderBrush = borderBrush;
        if (SessionGrid.SelectedItem is null && SessionGrid.Items.Count > 0)
            SessionGrid.SelectedIndex = 0;
    }

    private void OnToggleExpand(object sender, RoutedEventArgs e)
    {
        _hideTimer.Stop();
        _revealTimer.Stop();
        if (_viewModel.IsExpanded)
        {
            ExitFullscreen(restoreBounds: false);
            _restoreHiddenOnNextCompact = _autoHide && _dockSide != EdgeDock.None;
            _viewModel.IsExpanded = false;
        }
        else
        {
            _restoreHiddenOnNextCompact = false;
            _isEdgeHidden = false;
            _viewModel.IsExpanded = true;
        }
        ApplyExpansionState(true);
        if (!_viewModel.IsExpanded) ScheduleEdgeHide();
    }

    private void OnTopmostPointerDown(object sender, MouseButtonEventArgs e) =>
        _topmostPointerInvocation = true;

    private async void OnToggleTopmost(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        UpdateTopmostState();
        if (_topmostPointerInvocation)
        {
            _topmostPointerInvocation = false;
            Keyboard.ClearFocus();
        }
        await SaveSettingsSafelyAsync(CaptureWindowSettings());
    }

    private async void OnMinimizeToTray(object sender, RoutedEventArgs e) =>
        await HideToTrayAsync(true);

    private void UpdateTopmostState()
    {
        if (PanelTopmostButton is null) return;
        ApplyTopmostButtonState(PanelTopmostButton);
        ApplyTopmostButtonState(CompactTopmostButton);
        ApplyTopmostButtonState(CompactTopTopmostButton);
        ApplyTopmostButtonState(RailTopmostButton);
        ApplyTopmostButtonState(RailTopTopmostButton);
        if (TopmostCheckBox is not null) TopmostCheckBox.IsChecked = Topmost;
    }

    private void ApplyTopmostButtonState(System.Windows.Controls.Button button)
    {
        button.Foreground = (System.Windows.Media.Brush)FindResource(Topmost ? "HudMint" : "HudMuted");
        button.Background = Topmost
            ? (System.Windows.Media.Brush)FindResource("HudSelected")
            : System.Windows.Media.Brushes.Transparent;
        button.BorderBrush = Topmost
            ? (System.Windows.Media.Brush)FindResource("HudMint")
            : System.Windows.Media.Brushes.Transparent;
        button.ToolTip = Topmost ? "取消窗口始终置顶" : "窗口始终置顶";
    }

    private void OnToggleFullscreen(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.IsExpanded) return;
        if (_isExpandedFullscreen)
        {
            ExitFullscreen(restoreBounds: true);
            return;
        }

        _expandedRestoreBounds = new Rect(Left, Top, Width, Height);
        _isExpandedFullscreen = true;
        ApplyFullscreenBounds();
        UpdateFullscreenState();
    }

    private void ApplyFullscreenBounds()
    {
        var work = GetWorkAreaLogical();
        WindowState = WindowState.Normal;
        Left = work.Left;
        Top = work.Top;
        Width = work.Width;
        Height = work.Height;
        ApplyExpandedResponsiveLayout(fullscreen: true, work.Width);
    }

    private void ExitFullscreen(bool restoreBounds)
    {
        if (!_isExpandedFullscreen) return;
        _isExpandedFullscreen = false;
        ApplyExpandedResponsiveLayout(fullscreen: false, ExpandedWidth);
        if (restoreBounds && !_expandedRestoreBounds.IsEmpty)
        {
            var work = GetWorkAreaLogical();
            Width = Math.Min(_expandedRestoreBounds.Width, work.Width - WorkAreaMargin * 2);
            Height = Math.Min(_expandedRestoreBounds.Height, work.Height - WorkAreaMargin * 2);
            Left = Clamp(_expandedRestoreBounds.Left, work.Left + WorkAreaMargin,
                work.Right - Width - WorkAreaMargin);
            Top = Clamp(_expandedRestoreBounds.Top, work.Top + WorkAreaMargin,
                work.Bottom - Height - WorkAreaMargin);
        }
        _expandedRestoreBounds = Rect.Empty;
        UpdateFullscreenState();
    }

    private void ApplyExpandedResponsiveLayout(bool fullscreen, double workWidth)
    {
        DetailColumn.Width = new GridLength(fullscreen
            ? Math.Min(640, Math.Max(404, workWidth * 0.34))
            : Math.Min(404, Math.Max(340, workWidth * 0.35)));
        DetailPanel.Padding = new Thickness(20);
        // Change font sizes at layout time, never scale a rendered text bitmap.
        Resources["HudBodyFontSize"] = fullscreen ? 16d : 14d;
        Resources["HudCaptionFontSize"] = fullscreen ? 14d : 13d;
        Resources["HudNumberFontSize"] = fullscreen ? 16d : 14d;
        Resources["HudSectionFontSize"] = fullscreen ? 18d : 16d;
        DetailContentGrid.LayoutTransform = Transform.Identity;
    }

    private void UpdateFullscreenState()
    {
        if (FullscreenButton is null) return;
        FullscreenButton.Content = _isExpandedFullscreen ? "\uE73F" : "\uE740";
        FullscreenButton.ToolTip = _isExpandedFullscreen ? "恢复窗口大小" : "全屏显示";
        FullscreenButton.Foreground = (System.Windows.Media.Brush)FindResource(
            _isExpandedFullscreen ? "HudMint" : "HudMuted");
        FullscreenButton.Background = _isExpandedFullscreen
            ? (System.Windows.Media.Brush)FindResource("HudSelected")
            : System.Windows.Media.Brushes.Transparent;
        FullscreenButton.BorderBrush = _isExpandedFullscreen
            ? (System.Windows.Media.Brush)FindResource("HudMint")
            : System.Windows.Media.Brushes.Transparent;
    }

    private void CollapseToCompact()
    {
        if (!_viewModel.IsExpanded) return;
        ExitFullscreen(restoreBounds: false);
        _viewModel.IsExpanded = false;
        ApplyExpansionState(true);
    }

    private void ShowCompact()
    {
        CollapseToCompact();
        EndEdgeRevealElevation();
        _isEdgeHidden = false;
        Show();
        WindowState = WindowState.Normal;
        ApplyExpansionState();
        EnsureVisibleOnCurrentScreens();
        var preserveTopmost = Topmost;
        if (!preserveTopmost) Topmost = true;
        Activate();
        Focus();
        if (!preserveTopmost) Topmost = false;
        ScheduleEdgeHide();
    }

    internal void RestoreFromExternalActivation()
    {
        if (_disposed || _exiting) return;
        ShowCompact();
        _ = WriteLogSafelyAsync("instance_activation_received");
    }

    private async Task HideToTrayAsync(bool showBalloon)
    {
        EndEdgeRevealElevation();
        CollapseToCompact();
        Hide();
        if (showBalloon)
        {
            _tray.ShowBalloonTip(1500, "Codex Usage HUD",
                "已最小化到系统托盘；双击托盘图标或再次启动程序可唤回。",
                Forms.ToolTipIcon.Info);
        }
        await SaveSettingsSafelyAsync(CaptureWindowSettings());
    }

    private void ApplyExpansionState(bool preserveCompactPosition = false)
    {
        if (!IsLoaded && PresentationSource.FromVisual(this) is null)
        {
            ExpandedShell.Visibility = _viewModel.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
            CompactVerticalShell.Visibility = Visibility.Collapsed;
            CompactTopShell.Visibility = Visibility.Collapsed;
            RailVerticalShell.Visibility = !_viewModel.IsExpanded && !UsesCardLayout && _dockSide != EdgeDock.Top
                ? Visibility.Visible : Visibility.Collapsed;
            RailTopShell.Visibility = !_viewModel.IsExpanded && !UsesCardLayout && _dockSide == EdgeDock.Top
                ? Visibility.Visible : Visibility.Collapsed;
            if (UsesCardLayout && !_viewModel.IsExpanded)
            {
                CompactVerticalShell.Visibility = _dockSide == EdgeDock.Top ? Visibility.Collapsed : Visibility.Visible;
                CompactTopShell.Visibility = _dockSide == EdgeDock.Top ? Visibility.Visible : Visibility.Collapsed;
            }
            InvalidateRailMeasure();
            var compact = CompactSize();
            Width = _viewModel.IsExpanded ? ExpandedWidth : compact.Width;
            Height = _viewModel.IsExpanded ? ExpandedHeight : compact.Height;
            _viewModel.TopBarNarrowLayout = compact.Width < RailTopNarrowBreakpoint;
            return;
        }

        var work = GetWorkAreaLogical();
        if (_viewModel.IsExpanded)
        {
            EndEdgeRevealElevation();
            CloseSlotDetail();
            if (!preserveCompactPosition || !double.IsFinite(_compactAxis))
                _compactAxis = _dockSide == EdgeDock.Top ? Left : Top;
            _isEdgeHidden = false;
            ExpandedShell.Visibility = Visibility.Visible;
            CompactVerticalShell.Visibility = Visibility.Collapsed;
            CompactTopShell.Visibility = Visibility.Collapsed;
            RailVerticalShell.Visibility = Visibility.Collapsed;
            RailTopShell.Visibility = Visibility.Collapsed;
            Width = Math.Min(ExpandedWidth, Math.Max(720, work.Width - WorkAreaMargin * 2));
            Height = Math.Min(ExpandedHeight, Math.Max(520, work.Height - WorkAreaMargin * 2));
            ApplyExpandedResponsiveLayout(fullscreen: false, work.Width);
            switch (_dockSide)
            {
                case EdgeDock.Left:
                    Left = work.Left + WorkAreaMargin;
                    Top = Clamp(_compactAxis, work.Top + WorkAreaMargin, work.Bottom - Height - WorkAreaMargin);
                    break;
                case EdgeDock.Right:
                    Left = work.Right - Width - WorkAreaMargin;
                    Top = Clamp(_compactAxis, work.Top + WorkAreaMargin, work.Bottom - Height - WorkAreaMargin);
                    break;
                case EdgeDock.Top:
                    Left = Clamp(_compactAxis, work.Left + WorkAreaMargin, work.Right - Width - WorkAreaMargin);
                    Top = work.Top + WorkAreaMargin;
                    break;
                default:
                    Left = Clamp(Left, work.Left + WorkAreaMargin, work.Right - Width - WorkAreaMargin);
                    Top = Clamp(Top, work.Top + WorkAreaMargin, work.Bottom - Height - WorkAreaMargin);
                    break;
            }
            BeginShellFade(ExpandedShell);
            if (SessionGrid.SelectedItem is null && SessionGrid.Items.Count > 0)
                SessionGrid.SelectedIndex = 0;
        }
        else
        {
            SettingsPanel.Visibility = Visibility.Collapsed;
            DiagnosticsPanel.Visibility = Visibility.Collapsed;
            ExpandedShell.Visibility = Visibility.Collapsed;
            var topDock = _dockSide == EdgeDock.Top;
            var card = UsesCardLayout;
            CompactTopShell.Visibility = card && topDock ? Visibility.Visible : Visibility.Collapsed;
            CompactVerticalShell.Visibility = card && !topDock ? Visibility.Visible : Visibility.Collapsed;
            RailTopShell.Visibility = !card && topDock ? Visibility.Visible : Visibility.Collapsed;
            RailVerticalShell.Visibility = !card && !topDock ? Visibility.Visible : Visibility.Collapsed;
            InvalidateRailMeasure();
            UpdateLayout();
            var size = CompactSize();
            Width = size.Width;
            Height = size.Height;
            _viewModel.TopBarNarrowLayout = size.Width < RailTopNarrowBreakpoint;
            ConstrainRailScroller(size.Height);
            UpdateLayout();
            var measured = CompactSize();
            Width = measured.Width;
            Height = measured.Height;
            _viewModel.TopBarNarrowLayout = measured.Width < RailTopNarrowBreakpoint;
            ConstrainRailScroller(measured.Height);
            var restoreHidden = _restoreHiddenOnNextCompact && _autoHide && _dockSide != EdgeDock.None;
            _restoreHiddenOnNextCompact = false;
            _isEdgeHidden = restoreHidden;
            PlaceCompact(work, hidden: restoreHidden);
            if (restoreHidden)
                EndEdgeRevealElevation();
            if (!restoreHidden)
            {
                UIElement shell = card
                    ? (topDock ? CompactTopShell : CompactVerticalShell)
                    : (topDock ? RailTopShell : RailVerticalShell);
                BeginShellFade(shell);
            }
            PlaceSlotDetailPopup();
        }
    }

    private bool UsesCardLayout =>
        string.Equals(_compactLayout, CompactLayoutModes.Card, StringComparison.OrdinalIgnoreCase);

    private System.Windows.Size CompactSize()
    {
        var work = GetWorkAreaLogical();
        if (UsesCardLayout)
        {
            return _dockSide == EdgeDock.Top
                ? new System.Windows.Size(CompactTopWidth, CompactTopHeight)
                : new System.Windows.Size(CompactWidth, CompactHeight);
        }

        if (_dockSide == EdgeDock.Top)
        {
            var available = Math.Max(8, work.Width - 24);
            var width = Math.Min(RailTopWidth, available);
            var delayed = _viewModel.Slots.Any(slot => slot.UpdateDelayed);
            var narrow = width < RailTopNarrowBreakpoint;
            var height = narrow
                ? delayed ? RailTopNarrowDelayHeight : RailTopNarrowHeight
                : delayed ? RailTopDelayHeight : RailTopHeight;
            return new System.Windows.Size(width, height);
        }

        var maxHeight = Math.Max(120, work.Height - 24);
        var desired = MeasureRailDesiredHeight();
        return new System.Windows.Size(RailWidth, Math.Min(desired, maxHeight));
    }

    private double MeasureRailDesiredHeight()
    {
        if (RailVerticalShell is null || RailSlotList is null)
            return EstimatedRailHeight();
        InvalidateRailMeasure();
        var contentWidth = Math.Max(1, RailWidth - 22);
        RailSlotList.Measure(new System.Windows.Size(contentWidth, double.PositiveInfinity));
        var slotsHeight = RailSlotList.DesiredSize.Height;
        if (slotsHeight < 8)
        {
            RailVerticalShell.Measure(new System.Windows.Size(RailWidth, double.PositiveInfinity));
            var shell = RailVerticalShell.DesiredSize.Height;
            if (shell > 120) return shell;
            return EstimatedRailHeight();
        }

        var header = RailHeaderBar is null ? 28 : Math.Max(RailHeaderBar.ActualHeight, RailHeaderBar.DesiredSize.Height);
        if (header < 1) header = 28;
        var footer = RailExpandHint is null ? 32 : Math.Max(RailExpandHint.Height, RailExpandHint.DesiredSize.Height);
        if (footer < 1) footer = 32;
        var padding = RailVerticalShell.Padding.Top + RailVerticalShell.Padding.Bottom + 8;
        return header + slotsHeight + footer + padding + 8;
    }

    private double EstimatedRailHeight()
    {
        var rows = Math.Max(_viewModel.Slots.Count, ProviderSlotIds.All.Count);
        return Math.Max(RailHeightFallback, 36 + rows * 72 + 50);
    }

    private void InvalidateRailMeasure()
    {
        RailSlotList?.InvalidateMeasure();
        RailSlotScroller?.InvalidateMeasure();
        RailVerticalShell?.InvalidateMeasure();
    }

    private void ConstrainRailScroller(double windowHeight)
    {
        if (RailSlotScroller is null || RailSlotList is null || RailVerticalShell.Visibility != Visibility.Visible)
            return;
        var header = RailHeaderBar is null ? 28 : Math.Max(RailHeaderBar.ActualHeight, 28);
        var footer = RailExpandHint is null ? 32 : Math.Max(RailExpandHint.Height, 32);
        var chrome = RailVerticalShell.Padding.Top + RailVerticalShell.Padding.Bottom + 8 + header + footer;
        var budget = windowHeight - chrome;
        RailSlotList.Measure(new System.Windows.Size(Math.Max(1, RailWidth - 22), double.PositiveInfinity));
        if (budget > 40 && RailSlotList.DesiredSize.Height > budget + 8)
            RailSlotScroller.MaxHeight = budget;
        else
            RailSlotScroller.ClearValue(MaxHeightProperty);
    }

    private void RemeasureCompactRail()
    {
        if (_viewModel.IsExpanded || UsesCardLayout) return;
        InvalidateRailMeasure();
        UpdateLayout();
        var size = CompactSize();
        Width = size.Width;
        Height = size.Height;
        _viewModel.TopBarNarrowLayout = size.Width < RailTopNarrowBreakpoint;
        ConstrainRailScroller(size.Height);
    }

    private static void BeginShellFade(UIElement shell)
    {
        shell.BeginAnimation(OpacityProperty, null);
        shell.Opacity = 0.86;
        shell.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        }, HandoffBehavior.SnapshotAndReplace);
    }

    private void PlaceCompact(Rect work, bool hidden)
    {
        var axis = double.IsFinite(_compactAxis)
            ? _compactAxis
            : _dockSide == EdgeDock.Top ? work.Left + (work.Width - Width) / 2 : work.Top + 80;
        switch (_dockSide)
        {
            case EdgeDock.Left:
                Left = hidden ? work.Left - Width + EdgeHandle : work.Left;
                Top = Clamp(axis, work.Top + 6, work.Bottom - Height - 6);
                _compactAxis = Top;
                break;
            case EdgeDock.Right:
                Left = hidden ? work.Right - EdgeHandle : work.Right - Width;
                Top = Clamp(axis, work.Top + 6, work.Bottom - Height - 6);
                _compactAxis = Top;
                break;
            case EdgeDock.Top:
                Left = Clamp(axis, work.Left + 6, work.Right - Width - 6);
                Top = hidden ? work.Top - Height + EdgeHandle : work.Top;
                _compactAxis = Left;
                break;
            default:
                Left = Clamp(Left, work.Left, work.Right - Width);
                Top = Clamp(Top, work.Top, work.Bottom - Height);
                break;
        }
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.IsExpanded)
        {
            _viewModel.IsExpanded = true;
            ApplyExpansionState(true);
        }
        DiagnosticsPanel.Visibility = Visibility.Collapsed;
        SyncSettingsControls();
        SettingsPanel.Visibility = Visibility.Visible;
    }

    private void OnCloseSettings(object sender, RoutedEventArgs e) =>
        SettingsPanel.Visibility = Visibility.Collapsed;

    private void SyncSettingsControls()
    {
        _settingsReady = false;
        StartupCheckBox.IsChecked = IsStartupEnabledSafely();
        TopmostCheckBox.IsChecked = Topmost;
        AutoHideCheckBox.IsChecked = _autoHide;
        QuotaSnapshotCheckBox.IsChecked = QuotaSnapshotWriter.IsEnabledValue(
            _restoredSettings.GetValueOrDefault(QuotaSnapshotWriter.SettingKey));
        DockSideComboBox.SelectedIndex = _dockSide switch
        {
            EdgeDock.Right => 0,
            EdgeDock.Left => 1,
            EdgeDock.Top => 2,
            _ => 3,
        };
        CompactLayoutComboBox.SelectedIndex = UsesCardLayout ? 1 : 0;
        var stored = ProviderSettingKeys.FromStored(_restoredSettings);
        ApplySlotEditor(stored.Slot(ProviderSlotIds.CodexPrimary), SlotCodexPrimaryEnabled, SlotCodexPrimaryLabel);
        ApplySlotEditor(stored.Slot(ProviderSlotIds.CodexSecondary), SlotCodexSecondaryEnabled, SlotCodexSecondaryLabel);
        SlotCodexSecondaryHome.Text = stored.Slot(ProviderSlotIds.CodexSecondary).CodexHome ?? string.Empty;
        ApplySlotEditor(stored.Slot(ProviderSlotIds.Cursor), SlotCursorEnabled, SlotCursorLabel);
        ApplySlotEditor(stored.Slot(ProviderSlotIds.Grok), SlotGrokEnabled, SlotGrokLabel);
        ApplySlotEditor(stored.Slot(ProviderSlotIds.GrokBot), SlotGrokBotEnabled, SlotGrokBotLabel);
        ApplySlotEditor(stored.Slot(ProviderSlotIds.Claude), SlotClaudeEnabled, SlotClaudeLabel);
        SettingsStatusText.Text = string.Empty;
        if (IsolatedPreviewLaunch.CurrentProcessIsolated)
        {
            StartupCheckBox.IsChecked = false;
            StartupCheckBox.IsEnabled = false;
            _startupMenuItem.Enabled = false;
            SettingsStatusText.Text = "隔离预览：不会改写已安装 HUD 的开机启动或桌面快捷方式。";
        }
        var cursor = WindowsLoginPresence.Cursor();
        var grok = WindowsLoginPresence.Grok();
        var grokBot = WindowsLoginPresence.GrokBot();
        var claude = WindowsLoginPresence.ClaudeCredentialsFile();
        var claudeDesktop = WindowsLoginPresence.ClaudeDesktopProfile();
        UpdateSecondarySourcePresence();
        var presence =
            $"Cursor 登录文件：{(cursor.Present ? "存在" : "未找到")}（{cursor.RelativeHint}）\n" +
            $"Grok 登录目录：{(grok.Present ? "存在" : "未找到")}（{grok.RelativeHint}）\n" +
            $"Grok Bot：{(grokBot.Present ? "Cursor 登录文件存在" : "未找到 Cursor 登录文件")}（{grokBot.RelativeHint}）\n" +
            $"Claude Code 登录文件：{(claude.Present ? "存在" : "未找到")}（{claude.RelativeHint}）\n" +
            $"Claude Desktop 配置：{(claudeDesktop.Present ? "存在" : "未找到")}（{claudeDesktop.RelativeHint}）";
        SettingsStatusText.Text = IsolatedPreviewLaunch.CurrentProcessIsolated
            ? "隔离预览：不会改写已安装 HUD 的开机启动或桌面快捷方式。\n" + presence
            : presence;
        _settingsReady = true;
    }

    private static void ApplySlotEditor(ProviderSlotSettings slot, System.Windows.Controls.CheckBox enabled,
        System.Windows.Controls.TextBox label)
    {
        enabled.IsChecked = slot.Enabled;
        label.Text = slot.Label;
    }

    private ProviderAccessSettings CaptureProviderSettings()
    {
        if (!_settingsReady)
            return ProviderSettingKeys.FromStored(_restoredSettings);
        return new ProviderAccessSettings(new[]
        {
            new ProviderSlotSettings(ProviderSlotIds.CodexPrimary, TextOrDefault(SlotCodexPrimaryLabel, "Codex 当前"),
                SlotCodexPrimaryEnabled.IsChecked == true),
            new ProviderSlotSettings(ProviderSlotIds.CodexSecondary, TextOrDefault(SlotCodexSecondaryLabel, "Codex 第二账户"),
                SlotCodexSecondaryEnabled.IsChecked == true, SecondaryCodexHome.Normalize(SlotCodexSecondaryHome.Text)),
            new ProviderSlotSettings(ProviderSlotIds.Cursor, TextOrDefault(SlotCursorLabel, "Cursor"),
                SlotCursorEnabled.IsChecked == true),
            new ProviderSlotSettings(ProviderSlotIds.Grok, TextOrDefault(SlotGrokLabel, "Grok"),
                SlotGrokEnabled.IsChecked == true),
            new ProviderSlotSettings(ProviderSlotIds.GrokBot, TextOrDefault(SlotGrokBotLabel, "Grok Bot"),
                SlotGrokBotEnabled.IsChecked == true),
            new ProviderSlotSettings(ProviderSlotIds.Claude, TextOrDefault(SlotClaudeLabel, "Claude 订阅"),
                SlotClaudeEnabled.IsChecked == true),
        }, _compactLayout, true);
    }

    private static string TextOrDefault(System.Windows.Controls.TextBox box, string fallback) =>
        string.IsNullOrWhiteSpace(box.Text) ? fallback : box.Text.Trim();

    private async void OnCompactLayoutChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsReady || CompactLayoutComboBox.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        _compactLayout = tag;
        _viewModel.CompactLayout = tag;
        ApplyExpansionState(true);
        await SaveSettingsSafelyAsync(CaptureWindowSettings());
    }

    private async void OnProviderSettingsChanged(object sender, RoutedEventArgs e)
    {
        if (!_settingsReady) return;
        UpdateSecondarySourcePresence();
        await SaveSettingsSafelyAsync(CaptureWindowSettings());
    }

    private void UpdateSecondarySourcePresence()
    {
        var home = SecondaryCodexHome.Normalize(SlotCodexSecondaryHome.Text);
        if (home is null)
        {
            var piCodex = WindowsLoginPresence.PiCodexAuthFile();
            PiCodexPresenceText.Text = "留空：使用 PI ChatGPT/Codex 订阅。登录文件" +
                (piCodex.Present ? "存在" : "未找到") + "（" + piCodex.RelativeHint + "）";
            return;
        }

        var kind = SecondaryCodexHome.Classify(home, _engine.BoundCodexHome);
        if (kind == SecondaryHomeStatus.SameAsPrimary)
        {
            PiCodexPresenceText.Text = SecondaryCodexHome.SameStatusText;
            return;
        }

        if (kind != SecondaryHomeStatus.Ready)
        {
            PiCodexPresenceText.Text = SecondaryCodexHome.InvalidStatusText + "。不会改回 PI。";
            return;
        }

        var auth = WindowsLoginPresence.CodexCliAuthFile(home);
        PiCodexPresenceText.Text = "Codex CLI 主目录已配置。登录文件" +
            (auth.Present ? "存在" : "未找到") + "（" + auth.RelativeHint + "，只看时间与大小）";
    }

    private void OnEngineProvidersUpdated()
    {
        if (_disposed || _exiting) return;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
        {
            if (_disposed || _exiting) return;
            _viewModel.ApplyProviders(_engine.GetProviderBoard());
            RemeasureCompactRail();
            PlaceSlotDetailPopup();
        }));
    }

    private void OnRailSlotClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.FrameworkElement element || element.Tag is not string slotId) return;
        _viewModel.SelectSlot(slotId, true);
        _slotDetailOpen = true;
        PlaceSlotDetailPopup();
        if (_viewModel.SelectedSlotSuppliesAnalysis)
            OnOpenPrimaryAnalysis(sender, e);
    }

    private void OnRailSlotMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not System.Windows.FrameworkElement element || element.Tag is not string slotId) return;
        if (!_viewModel.SlotDetailPinned)
            _viewModel.SelectSlot(slotId, false);
        _slotDetailOpen = true;
        PlaceSlotDetailPopup();
    }

    private void OnRailSlotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        OnRailSlotMouseEnter(sender, new System.Windows.Input.MouseEventArgs(Mouse.PrimaryDevice, 0));
    }

    private void OnCloseSlotDetail(object sender, RoutedEventArgs e) => CloseSlotDetail();

    private void OnOpenPrimaryAnalysis(object sender, RoutedEventArgs e)
    {
        _viewModel.SelectSlot(ProviderSlotIds.CodexPrimary, false);
        CloseSlotDetail();
        if (!_viewModel.IsExpanded)
            OnToggleExpand(sender, e);
    }

    private void CloseSlotDetail()
    {
        _slotDetailOpen = false;
        _viewModel.SlotDetailPinned = false;
        if (SlotDetailPopup is not null) SlotDetailPopup.IsOpen = false;
    }

    private void PlaceSlotDetailPopup()
    {
        if (SlotDetailPopup is null || OpenPrimaryAnalysisButton is null) return;
        if (_viewModel.IsExpanded || _isEdgeHidden || !_slotDetailOpen || _viewModel.SelectedSlot is null)
        {
            SlotDetailPopup.IsOpen = false;
            return;
        }

        var target = _dockSide == EdgeDock.Top ? (System.Windows.UIElement)RailTopShell : RailVerticalShell;
        if (target.Visibility != Visibility.Visible)
            target = CompactVerticalShell.Visibility == Visibility.Visible ? CompactVerticalShell : CompactTopShell;
        SlotDetailPopup.PlacementTarget = target;
        SlotDetailPopup.Placement = _dockSide switch
        {
            EdgeDock.Left => PlacementMode.Right,
            EdgeDock.Top => PlacementMode.Bottom,
            _ => PlacementMode.Left,
        };
        OpenPrimaryAnalysisButton.Visibility = _viewModel.SelectedSlotSuppliesAnalysis
            ? Visibility.Visible : Visibility.Collapsed;
        SlotDetailPopup.IsOpen = true;
    }

    private async void OnStartupSettingClick(object sender, RoutedEventArgs e)
    {
        if (!_settingsReady) return;
        if (IsolatedPreviewLaunch.CurrentProcessIsolated)
        {
            StartupCheckBox.IsChecked = false;
            SettingsStatusText.Text = "隔离预览不会改写已安装 HUD 的开机启动。";
            return;
        }
        var enable = StartupCheckBox.IsChecked == true;
        StartupCheckBox.IsEnabled = false;
        try
        {
            await Task.Run(() => StartupRegistration.SetEnabled(enable));
            _startupMenuItem.Checked = enable;
            SettingsStatusText.Text = enable ? "已启用开机启动" : "已关闭开机启动";
        }
        catch
        {
            StartupCheckBox.IsChecked = IsStartupEnabledSafely();
            SettingsStatusText.Text = "无法修改开机启动设置";
        }
        finally
        {
            StartupCheckBox.IsEnabled = true;
        }
    }

    private async void OnTopmostSettingClick(object sender, RoutedEventArgs e)
    {
        if (!_settingsReady) return;
        Topmost = TopmostCheckBox.IsChecked == true;
        UpdateTopmostState();
        await SaveSettingsSafelyAsync(CaptureWindowSettings());
    }

    private async void OnAutoHideSettingClick(object sender, RoutedEventArgs e)
    {
        if (!_settingsReady) return;
        _autoHide = AutoHideCheckBox.IsChecked == true;
        if (!_autoHide)
        {
            EndEdgeRevealElevation();
            SetEdgeHidden(false);
        }
        await SaveSettingsSafelyAsync(CaptureWindowSettings());
        if (_autoHide) ScheduleEdgeHide();
    }

    private async void OnQuotaSnapshotSettingClick(object sender, RoutedEventArgs e)
    {
        if (!_settingsReady) return;
        var enabled = QuotaSnapshotCheckBox.IsChecked == true;
        _engine.SetQuotaSnapshotExportEnabled(enabled);
        await SaveSettingsSafelyAsync(CaptureWindowSettings());
        if (enabled && QuotaSnapshotCheckBox.IsChecked == true)
            _engine.ExportCurrentQuotaSnapshot();
    }

    private async void OnDockSideSettingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsReady || DockSideComboBox.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        EndEdgeRevealElevation();
        _dockSide = ParseDock(tag);
        _isEdgeHidden = false;
        ApplyExpansionState(true);
        await SaveSettingsSafelyAsync(CaptureWindowSettings());
    }

    private async void OnCreateShortcut(object sender, RoutedEventArgs e)
    {
        if (IsolatedPreviewLaunch.CurrentProcessIsolated)
        {
            SettingsStatusText.Text = "隔离预览不会创建指向本包的桌面快捷方式。";
            return;
        }

        SettingsStatusText.Text = "正在创建桌面快捷方式…";
        try
        {
            var path = await Task.Run(DesktopShortcutRegistration.CreateOrRepair);
            SettingsStatusText.Text = $"快捷方式已就绪：{Path.GetFileName(path)}";
        }
        catch
        {
            SettingsStatusText.Text = "创建快捷方式失败";
        }
    }

    private async void OnRemoveShortcut(object sender, RoutedEventArgs e)
    {
        try
        {
            var removed = await Task.Run(DesktopShortcutRegistration.Remove);
            SettingsStatusText.Text = removed ? "已移除桌面快捷方式" : "桌面快捷方式不存在";
        }
        catch
        {
            SettingsStatusText.Text = "移除快捷方式失败";
        }
    }

    private void OnFilterTabChecked(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton { Tag: string filter })
            QueueViewChange(filter, null);
    }

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.ComboBox { SelectedItem: ComboBoxItem { Tag: string sort } })
            QueueViewChange(null, sort);
    }

    private void QueueViewChange(string? filter, string? sort)
    {
        if (filter is not null) _pendingFilter = filter;
        if (sort is not null) _pendingSort = sort;
        if (_viewChangeOperation is { Status: DispatcherOperationStatus.Pending }) return;

        _viewChangeOperation = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            var pendingFilter = _pendingFilter;
            var pendingSort = _pendingSort;
            _pendingFilter = null;
            _pendingSort = null;
            _viewChangeOperation = null;
            _viewModel.SetView(pendingFilter, pendingSort);
            if (SessionGrid is not null)
                SessionGrid.SelectedIndex = SessionGrid.Items.Count > 0 ? 0 : -1;
        }));
    }

    private void OnToggleDiagnostics(object sender, RoutedEventArgs e)
    {
        SettingsPanel.Visibility = Visibility.Collapsed;
        DiagnosticsPanel.Visibility = Visibility.Visible;
    }

    private void OnCloseDiagnostics(object sender, RoutedEventArgs e) =>
        DiagnosticsPanel.Visibility = Visibility.Collapsed;

    private void OnToggleSessionChildren(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { DataContext: SessionDisplayRow row }) return;
        _viewModel.ToggleChildren(row.ThreadId);
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            var selected = _viewModel.Rows.FirstOrDefault(item =>
                string.Equals(item.ThreadId, row.ThreadId, StringComparison.Ordinal));
            if (selected is not null) SessionGrid.SelectedItem = selected;
        }));
        e.Handled = true;
    }

    private async void OnTogglePin(object sender, RoutedEventArgs e)
    {
        if (SessionGrid.SelectedItem is not SessionDisplayRow row) return;
        try
        {
            await _engine.SetPinnedThreadAsync(row.IsPinned ? null : row.ThreadId);
        }
        catch (Exception)
        {
            await WriteLogSafelyAsync("pin_write_failed");
            return;
        }
        await RefreshAsync(false);
    }

    private async void OnSetDriftAssessment(object sender, RoutedEventArgs e)
    {
        if (SessionGrid.SelectedItem is not SessionDisplayRow row ||
            sender is not System.Windows.Controls.Button { Tag: string tag } ||
            Interlocked.Exchange(ref _driftWritePending, 1) != 0) return;
        var level = tag switch
        {
            "occasional" => DriftAssessmentLevel.Occasional,
            "repeated" => DriftAssessmentLevel.Repeated,
            _ => DriftAssessmentLevel.Unassessed,
        };
        SetDriftButtonsEnabled(false);
        try
        {
            await _engine.SetSessionDriftAssessmentAsync(row.ThreadId, level);
            await RefreshAsync(false);
        }
        catch (Exception)
        {
            await WriteLogSafelyAsync("drift_assessment_write_failed");
        }
        finally
        {
            Interlocked.Exchange(ref _driftWritePending, 0);
            if (!_disposed) SetDriftButtonsEnabled(true);
        }
    }

    private void SetDriftButtonsEnabled(bool enabled)
    {
        DriftUnassessedButton.IsEnabled = enabled;
        DriftOccasionalButton.IsEnabled = enabled;
        DriftRepeatedButton.IsEnabled = enabled;
    }

    private async void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || IsInteractiveSource(e.OriginalSource as DependencyObject))
            return;
        if (_isExpandedFullscreen) return;
        _hideTimer.Stop();
        _revealTimer.Stop();
        if (_isEdgeHidden) SetEdgeHidden(false);
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        var work = GetWorkAreaLogical();
        if (!_viewModel.IsExpanded)
        {
            _dockSide = ResolveDockSide(work, new Rect(Left, Top, Width, Height), GetCursorLogical());
            _compactAxis = _dockSide == EdgeDock.Top ? Left : Top;
            if (_dockSide == EdgeDock.None)
                EndEdgeRevealElevation();
            ApplyExpansionState(true);
            ScheduleEdgeHide();
        }
        await SaveSettingsSafelyAsync(CaptureWindowSettings());
    }

    private static bool IsInteractiveSource(DependencyObject? source)
    {
        for (var current = source; current is not null; current = GetParent(current))
        {
            if (current is System.Windows.Controls.Primitives.ButtonBase or Selector or
                System.Windows.Controls.Primitives.ScrollBar or DataGridColumnHeader or DataGridCell)
                return true;
            if (current is MainWindow) break;
        }
        return false;
    }

    private static DependencyObject? GetParent(DependencyObject current) => current switch
    {
        Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(current),
        FrameworkContentElement content => content.Parent,
        _ => LogicalTreeHelper.GetParent(current),
    };

    private System.Windows.Point GetCursorLogical()
    {
        var cursor = Forms.Cursor.Position;
        if (PresentationSource.FromVisual(this)?.CompositionTarget is not { } target)
            return new System.Windows.Point(cursor.X, cursor.Y);
        return target.TransformFromDevice.Transform(new System.Windows.Point(cursor.X, cursor.Y));
    }

    private static EdgeDock ResolveDockSide(Rect work, Rect windowBounds, System.Windows.Point pointer)
    {
        var pointerDistances = new[]
        {
            (Side: EdgeDock.Left, Distance: pointer.Y >= work.Top - PointerDockSnapDistance &&
                pointer.Y <= work.Bottom + PointerDockSnapDistance
                    ? Math.Abs(pointer.X - work.Left) : double.PositiveInfinity),
            (Side: EdgeDock.Right, Distance: pointer.Y >= work.Top - PointerDockSnapDistance &&
                pointer.Y <= work.Bottom + PointerDockSnapDistance
                    ? Math.Abs(pointer.X - work.Right) : double.PositiveInfinity),
            (Side: EdgeDock.Top, Distance: pointer.X >= work.Left - PointerDockSnapDistance &&
                pointer.X <= work.Right + PointerDockSnapDistance
                    ? Math.Abs(pointer.Y - work.Top) : double.PositiveInfinity),
        };
        var pointerMatch = pointerDistances.OrderBy(item => item.Distance).First();
        if (pointerMatch.Distance <= PointerDockSnapDistance)
            return pointerMatch.Side;

        var windowDistances = new[]
        {
            (Side: EdgeDock.Left, Distance: Math.Abs(windowBounds.Left - work.Left)),
            (Side: EdgeDock.Right, Distance: Math.Abs(windowBounds.Right - work.Right)),
            (Side: EdgeDock.Top, Distance: Math.Abs(windowBounds.Top - work.Top)),
        };
        var windowMatch = windowDistances.OrderBy(item => item.Distance).First();
        return windowMatch.Distance <= DockSnapDistance ? windowMatch.Side : EdgeDock.None;
    }

    private void OnWindowMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _hideTimer.Stop();
        if (_isEdgeHidden)
        {
            _revealTimer.Stop();
            RevealEdgeFromPointer();
        }
    }

    private void RevealEdgeFromPointer()
    {
        if (!_isEdgeHidden)
        {
            SetEdgeHidden(false);
            return;
        }

        SetEdgeHidden(false);
        BeginEdgeRevealElevation();
    }

    private void BeginEdgeRevealElevation()
    {
        if (_edgeRevealElevated || _isEdgeHidden || _viewModel.IsExpanded ||
            _dockSide == EdgeDock.None || !_autoHide)
            return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        // Keep Window.Topmost as the saved pin. Reordering here must not activate or focus.
        _edgeRevealElevated = true;
        RaiseWindowBand(handle, topmost: true);
    }

    private void EndEdgeRevealElevation()
    {
        if (!_edgeRevealElevated) return;
        _edgeRevealElevated = false;
        if (Topmost) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        RaiseWindowBand(handle, topmost: false);
    }

    private static void RaiseWindowBand(IntPtr handle, bool topmost)
    {
        _ = SetWindowPos(handle, topmost ? HwndTopmost : HwndNotTopmost,
            0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    private void OnWindowMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _revealTimer.Stop();
        if (!_viewModel.SlotDetailPinned && SlotDetailPopup is { IsMouseOver: false })
            CloseSlotDetail();
        ScheduleEdgeHide();
    }

    private void ScheduleEdgeHide()
    {
        _hideTimer.Stop();
        if (_autoHide && !_viewModel.IsExpanded && _dockSide != EdgeDock.None && IsVisible)
            _hideTimer.Start();
    }

    private void SetEdgeHidden(bool hidden)
    {
        if (_viewModel.IsExpanded || _dockSide == EdgeDock.None || !_autoHide)
            hidden = false;
        if (_isEdgeHidden == hidden)
        {
            if (hidden) EndEdgeRevealElevation();
            return;
        }
        _isEdgeHidden = hidden;
        if (hidden) CloseSlotDetail();
        PlaceCompact(GetWorkAreaLogical(), hidden);
        if (hidden) EndEdgeRevealElevation();
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_exiting)
        {
            e.Cancel = true;
            await HideToTrayAsync(true);
        }
    }

    private (Forms.NotifyIcon Icon, Forms.ToolStripMenuItem StartupItem) CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示悬浮卡片 / 隐藏到托盘", null, async (_, _) =>
        {
            if (IsVisible) await HideToTrayAsync(false); else ShowCompact();
        });
        menu.Items.Add("刷新", null, async (_, _) => await RefreshAsync(true));
        menu.Items.Add("设置", null, (_, _) =>
        {
            ShowCompact();
            OnSettings(this, new RoutedEventArgs());
        });
        menu.Items.Add("创建 / 修复桌面快捷方式", null, (_, _) =>
        {
            if (IsolatedPreviewLaunch.CurrentProcessIsolated)
            {
                _tray.ShowBalloonTip(1500, "Codex Usage HUD", "隔离预览不会创建桌面快捷方式", Forms.ToolTipIcon.Info);
                return;
            }
            try
            {
                DesktopShortcutRegistration.CreateOrRepair();
                _tray.ShowBalloonTip(1500, "Codex Usage HUD", "桌面快捷方式已就绪", Forms.ToolTipIcon.Info);
            }
            catch
            {
                _tray.ShowBalloonTip(1500, "Codex Usage HUD", "创建桌面快捷方式失败", Forms.ToolTipIcon.Warning);
            }
        });
        var startupItem = new Forms.ToolStripMenuItem("随 Windows 登录启动")
        {
            Checked = IsStartupEnabledSafely(),
        };
        startupItem.Click += OnToggleStartup;
        menu.Items.Add(startupItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, async (_, _) => await ExitFromTrayAsync());
        var icon = new Forms.NotifyIcon
        {
            Icon = Drawing.SystemIcons.Application,
            Visible = true,
            Text = "Codex Usage HUD",
            ContextMenuStrip = menu,
        };
        menu.Opening += (_, _) => startupItem.Checked = IsStartupEnabledSafely();
        icon.DoubleClick += (_, _) => ShowCompact();
        return (icon, startupItem);
    }

    private async void OnToggleStartup(object? sender, EventArgs e)
    {
        if (IsolatedPreviewLaunch.CurrentProcessIsolated)
        {
            _startupMenuItem.Checked = false;
            return;
        }
        var enable = !_startupMenuItem.Checked;
        try
        {
            await Task.Run(() => StartupRegistration.SetEnabled(enable));
            _startupMenuItem.Checked = enable;
            _tray.ShowBalloonTip(1800, "Codex Usage HUD",
                enable ? "已设置为登录 Windows 后自动启动" : "已取消登录 Windows 后自动启动",
                Forms.ToolTipIcon.Info);
        }
        catch
        {
            _startupMenuItem.Checked = IsStartupEnabledSafely();
            _tray.ShowBalloonTip(1800, "Codex Usage HUD", "无法修改开机启动设置",
                Forms.ToolTipIcon.Warning);
        }
    }

    private static bool IsStartupEnabledSafely()
    {
        try { return StartupRegistration.IsEnabled(); }
        catch { return false; }
    }

    private void UpdateTrayIconFromDial()
    {
        try
        {
            const int renderSize = 64;
            const int iconSize = 32;
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                // A tray-sized mark stays recognizable even when the compact
                // panel is hidden; shrinking the entire card loses its detail.
                context.DrawRoundedRectangle((System.Windows.Media.Brush)FindResource("HudMint"),
                    null, new Rect(0, 0, renderSize, renderSize), 11, 11);
                var mark = new FormattedText("\uE9D2", CultureInfo.InvariantCulture,
                    System.Windows.FlowDirection.LeftToRight,
                    new Typeface((System.Windows.Media.FontFamily)FindResource("HudIconFont"), FontStyles.Normal,
                        FontWeights.Normal, FontStretches.Normal), 41, System.Windows.Media.Brushes.White, 1d);
                context.DrawText(mark, new System.Windows.Point(
                    (renderSize - mark.Width) / 2, (renderSize - mark.Height) / 2));
            }
            var rendered = new RenderTargetBitmap(renderSize, renderSize, 96, 96, PixelFormats.Pbgra32);
            rendered.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rendered));
            using var png = new MemoryStream();
            encoder.Save(png);
            png.Position = 0;
            using var source = new Drawing.Bitmap(png);
            using var scaled = new Drawing.Bitmap(source, new Drawing.Size(iconSize, iconSize));
            var handle = scaled.GetHicon();
            try
            {
                var next = (Drawing.Icon)Drawing.Icon.FromHandle(handle).Clone();
                var previous = _ownedTrayIcon;
                _ownedTrayIcon = next;
                _tray.Icon = next;
                previous?.Dispose();
            }
            finally
            {
                _ = DestroyIcon(handle);
            }
        }
        catch
        {
            _ = WriteLogSafelyAsync("tray_icon_render_failed");
        }
    }

    private async Task ExitFromTrayAsync()
    {
        _exitTask ??= ExitCoreAsync();
        await _exitTask;
    }

    internal Task RequestQualityAssuranceExitAsync() => ExitFromTrayAsync();

    private async Task ExitCoreAsync()
    {
        _exiting = true;
        _hideTimer.Stop();
        _revealTimer.Stop();
        var finalSettings = CaptureWindowSettings();
        var failed = false;
        try { await _engine.StopAcceptingAppIoAsync(); }
        catch (Exception) { failed = true; }
        _countdownTimer.Stop();
        _shutdown.Cancel();
        try { await _backgroundLoopTask; }
        catch (OperationCanceledException) { }
        catch (Exception) { failed = true; }
        try
        {
            await _refreshGate.WaitAsync();
            _refreshGate.Release();
        }
        catch (ObjectDisposedException) { }
        try { await _engine.CompleteAppIoAsync(finalSettings); }
        catch (Exception) { failed = true; }
        try { DisposeResources(); }
        catch (Exception) { failed = true; }
        try { await _disposeRuntimeAsync(); }
        catch (Exception) { failed = true; }
        if (failed) Environment.ExitCode = 5;
        System.Windows.Application.Current.Shutdown();
    }

    private async Task RestoreWindowStateAsync()
    {
        IReadOnlyDictionary<string, string?> settings;
        try
        {
            settings = await _engine.LoadSettingsAsync("always_on_top", "window_left", "window_top",
                "dock_side", "auto_hide", ProviderSettingKeys.CompactLayout, ProviderSettingKeys.AccessNotice,
                ProviderSettingKeys.Label(ProviderSlotIds.CodexPrimary),
                ProviderSettingKeys.Enabled(ProviderSlotIds.CodexPrimary),
                ProviderSettingKeys.Label(ProviderSlotIds.CodexSecondary),
                ProviderSettingKeys.Enabled(ProviderSlotIds.CodexSecondary),
                ProviderSettingKeys.CodexHome(ProviderSlotIds.CodexSecondary),
                ProviderSettingKeys.Label(ProviderSlotIds.Cursor),
                ProviderSettingKeys.Enabled(ProviderSlotIds.Cursor),
                ProviderSettingKeys.Label(ProviderSlotIds.Grok),
                ProviderSettingKeys.Enabled(ProviderSlotIds.Grok),
                ProviderSettingKeys.Label(ProviderSlotIds.GrokBot),
                ProviderSettingKeys.Enabled(ProviderSlotIds.GrokBot),
                ProviderSettingKeys.Label(ProviderSlotIds.Claude),
                ProviderSettingKeys.Enabled(ProviderSlotIds.Claude),
                QuotaSnapshotWriter.SettingKey);
        }
        catch (Exception)
        {
            await WriteLogSafelyAsync("settings_load_failed");
            return;
        }
        _viewModel.IsExpanded = false;
        _isExpandedFullscreen = false;
        _expandedRestoreBounds = Rect.Empty;
        _restoredSettings.Clear();
        foreach (var pair in settings) _restoredSettings[pair.Key] = pair.Value;
        Topmost = settings.GetValueOrDefault("always_on_top") == "1";
        _dockSide = ParseDock(settings.GetValueOrDefault("dock_side"));
        if (!settings.ContainsKey("dock_side")) _dockSide = EdgeDock.Right;
        _autoHide = settings.GetValueOrDefault("auto_hide") != "0";
        var layout = settings.GetValueOrDefault(ProviderSettingKeys.CompactLayout);
        _compactLayout = string.Equals(layout, CompactLayoutModes.Card, StringComparison.OrdinalIgnoreCase)
            ? CompactLayoutModes.Card
            : CompactLayoutModes.Rail;
        _viewModel.CompactLayout = _compactLayout;
        _engine.SetQuotaSnapshotExportEnabled(
            QuotaSnapshotWriter.IsEnabledValue(settings.GetValueOrDefault(QuotaSnapshotWriter.SettingKey)));
        UpdateTopmostState();
        if (double.TryParse(settings.GetValueOrDefault("window_left"), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var left) &&
            double.TryParse(settings.GetValueOrDefault("window_top"), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var top) && IsSafePosition(left, top))
        {
            Left = left;
            Top = top;
            _compactAxis = _dockSide == EdgeDock.Top ? left : top;
        }
        SyncSettingsControls();
    }

    private static EdgeDock ParseDock(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "left" => EdgeDock.Left,
        "top" => EdgeDock.Top,
        "none" => EdgeDock.None,
        _ => EdgeDock.Right,
    };

    private static string DockText(EdgeDock value) => value switch
    {
        EdgeDock.Left => "left",
        EdgeDock.Top => "top",
        EdgeDock.None => "none",
        _ => "right",
    };

    private static bool IsSafePosition(double left, double top) =>
        double.IsFinite(left) && double.IsFinite(top) &&
        left >= SystemParameters.VirtualScreenLeft - (CompactTopWidth - MinimumVisible) &&
        top >= SystemParameters.VirtualScreenTop - (CompactHeight - MinimumVisible) &&
        left <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - MinimumVisible &&
        top <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - MinimumVisible;

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (_disposed || _exiting) return;
        try
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                new Action(() =>
                {
                    if (_disposed || _exiting) return;
                    if (_isExpandedFullscreen) ApplyFullscreenBounds();
                    else EnsureVisibleOnCurrentScreens();
                }));
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void EnsureVisibleOnCurrentScreens()
    {
        if (!IsLoaded || PresentationSource.FromVisual(this)?.CompositionTarget is not { } target) return;
        var deviceOrigin = PointToScreen(new System.Windows.Point(0, 0));
        var toDevice = target.TransformToDevice;
        var deviceWidth = Math.Max(1, (int)Math.Ceiling(Width * Math.Abs(toDevice.M11)));
        var deviceHeight = Math.Max(1, (int)Math.Ceiling(Height * Math.Abs(toDevice.M22)));
        var minimum = _isEdgeHidden ? EdgeHandle : MinimumVisible;
        var minimumWidth = Math.Max(1, (int)Math.Ceiling(minimum * Math.Abs(toDevice.M11)));
        var minimumHeight = Math.Max(1, (int)Math.Ceiling(minimum * Math.Abs(toDevice.M22)));
        var bounds = new Drawing.Rectangle((int)Math.Floor(deviceOrigin.X), (int)Math.Floor(deviceOrigin.Y),
            deviceWidth, deviceHeight);
        var workAreas = Forms.Screen.AllScreens.Select(screen => screen.WorkingArea).ToArray();
        if (HasUsableScreenIntersection(bounds, workAreas, minimumWidth, minimumHeight)) return;
        _dockSide = EdgeDock.Right;
        _compactAxis = double.NaN;
        _isEdgeHidden = false;
        EndEdgeRevealElevation();
        ApplyExpansionState();
    }

    private static bool HasUsableScreenIntersection(Drawing.Rectangle bounds,
        IReadOnlyList<Drawing.Rectangle> workAreas, int minimumWidth, int minimumHeight)
    {
        if (minimumWidth <= 0 || minimumHeight <= 0) return false;
        foreach (var workArea in workAreas)
        {
            var intersection = Drawing.Rectangle.Intersect(bounds, workArea);
            if (intersection.Width >= minimumWidth && intersection.Height >= minimumHeight) return true;
        }
        return false;
    }

    private Rect GetWorkAreaLogical()
    {
        if (_workAreaOverride is { } overrideRect)
            return overrideRect;
        if (!IsLoaded && PresentationSource.FromVisual(this) is null)
            return SystemParameters.WorkArea;
        var screen = new WindowInteropHelper(this).Handle is { } handle && handle != IntPtr.Zero
            ? Forms.Screen.FromHandle(handle)
            : Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens.First();
        if (PresentationSource.FromVisual(this)?.CompositionTarget is not { } target)
            return SystemParameters.WorkArea;
        var topLeft = target.TransformFromDevice.Transform(
            new System.Windows.Point(screen.WorkingArea.Left, screen.WorkingArea.Top));
        var bottomRight = target.TransformFromDevice.Transform(
            new System.Windows.Point(screen.WorkingArea.Right, screen.WorkingArea.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    private Dictionary<string, string> CaptureWindowSettings()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["always_on_top"] = Topmost ? "1" : "0",
            ["dock_side"] = DockText(_dockSide),
            ["auto_hide"] = _autoHide ? "1" : "0",
            [ProviderSettingKeys.CompactLayout] = _compactLayout,
        };
        if (_settingsReady)
        {
            var snapshot = QuotaSnapshotCheckBox.IsChecked == true ? "1" : "0";
            values[QuotaSnapshotWriter.SettingKey] = snapshot;
            _restoredSettings[QuotaSnapshotWriter.SettingKey] = snapshot;
        }
        else if (_restoredSettings.TryGetValue(QuotaSnapshotWriter.SettingKey, out var stored) && stored is not null)
        {
            values[QuotaSnapshotWriter.SettingKey] = stored;
        }
        foreach (var pair in ProviderSettingKeys.ToStored(CaptureProviderSettings()))
        {
            values[pair.Key] = pair.Value;
            _restoredSettings[pair.Key] = pair.Value;
        }
        var left = _dockSide == EdgeDock.Top && double.IsFinite(_compactAxis) ? _compactAxis : Left;
        var top = _dockSide is EdgeDock.Left or EdgeDock.Right && double.IsFinite(_compactAxis) ? _compactAxis : Top;
        if (IsSafePosition(left, top))
        {
            values["window_left"] = left.ToString(CultureInfo.InvariantCulture);
            values["window_top"] = top.ToString(CultureInfo.InvariantCulture);
        }
        return values;
    }

    private async Task SaveSettingsSafelyAsync(IReadOnlyDictionary<string, string> values)
    {
        try { await _engine.SaveSettingsAsync(values); }
        catch (Exception) { await WriteLogSafelyAsync("settings_write_failed"); }
    }

    private async Task WriteLogSafelyAsync(string code)
    {
        try { await _engine.WriteLogCodeAsync(code); }
        catch (Exception) { }
    }

    private static double Clamp(double value, double minimum, double maximum)
    {
        if (maximum < minimum) return minimum;
        if (!double.IsFinite(value)) return minimum;
        return Math.Max(minimum, Math.Min(maximum, value));
    }

    public void DisposeResources()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.ProvidersUpdated -= OnEngineProvidersUpdated;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _countdownTimer.Stop();
        _hideTimer.Stop();
        _revealTimer.Stop();
        _shutdown.Cancel();
        _tray.Visible = false;
        _tray.Dispose();
        _ownedTrayIcon?.Dispose();
        _ownedTrayIcon = null;
        _shutdown.Dispose();
        _refreshGate.Dispose();
    }

    public void Dispose() => DisposeResources();

    private void ApplyNativeWindowClarity()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var preference = 2; // DWMWCP_ROUND
        _ = DwmSetWindowAttribute(handle, 33, ref preference, sizeof(int));
    }

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute,
        ref int value, int valueSize);
}
