// SPDX-License-Identifier: MPL-2.0
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using CliWatch.Contracts;

namespace CliWatch.Views;

public partial class HudView : UserControl
{
    private CockpitSnapshot? _snapshot;
    private string? _selectedProjectId;
    private bool _suppressSelectionEvent;
    private bool _showHistory;
    private UiLang _lang = UiLang.Zh;

    public HudView()
    {
        InitializeComponent();
        ProjectList.ItemContainerGenerator.StatusChanged += (_, _) =>
        {
            if (ProjectList.ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated
                && CurrentModel is not null)
            {
                StampItemAutomation(CurrentModel);
            }
        };
    }

    public event EventHandler<NavigationRequestEventArgs>? NavigationRequested;
    public event EventHandler? SelectionChanged;

    public string? SelectedProjectId => _selectedProjectId;

    public HudModel? CurrentModel { get; private set; }

    public UiLang UiLanguage => _lang;

    public void SetLanguage(UiLang lang)
    {
        _lang = lang;
        this.Language = System.Windows.Markup.XmlLanguage.GetLanguage(lang == UiLang.En ? "en-US" : "zh-CN");
        if (_snapshot is null)
        {
            ApplyChrome();
            return;
        }

        var model = HudPresentation.Build(_snapshot, _selectedProjectId, _showHistory, StatusLanguage.DefaultHudMaxLen, _lang);
        _selectedProjectId = model.SelectedProjectId;
        CurrentModel = model;
        ApplyModel(model);
    }

    public void SetSnapshot(CockpitSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshot = snapshot;
        var model = HudPresentation.Build(snapshot, _selectedProjectId, _showHistory, StatusLanguage.DefaultHudMaxLen, _lang);
        _selectedProjectId = model.SelectedProjectId;
        CurrentModel = model;
        ApplyModel(model);
    }

    public void SetShowHistory(bool show)
    {
        _showHistory = show;
        if (_snapshot is null)
        {
            ApplyChrome();
            return;
        }

        var model = HudPresentation.Build(_snapshot, _selectedProjectId, _showHistory, StatusLanguage.DefaultHudMaxLen, _lang);
        _selectedProjectId = model.SelectedProjectId;
        CurrentModel = model;
        ApplyModel(model);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SelectProject(string? projectId)
    {
        if (_snapshot is null)
        {
            _selectedProjectId = projectId;
            return;
        }

        var model = HudPresentation.Build(_snapshot, projectId, _showHistory, StatusLanguage.DefaultHudMaxLen, _lang);
        _selectedProjectId = model.SelectedProjectId;
        CurrentModel = model;
        ApplyModel(model);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    protected void RequestNavigation(NavigationTarget target) =>
        NavigationRequested?.Invoke(this, new NavigationRequestEventArgs(target));

    private void ApplyChrome()
    {
        HudTitleText.Text = ConsumerCopy.L(_lang, "任务", "Tasks");
        HistoryToggle.Content = ConsumerCopy.T(_lang, _showHistory ? "hide_history" : "show_history");
        System.Windows.Automation.AutomationProperties.SetName(HistoryToggle, HistoryToggle.Content as string ?? "");
    }

    private void ApplyModel(HudModel model)
    {
        ApplyChrome();
        CollectedAtText.Text = ConsumerCopy.T(_lang, "collect_at") + model.CollectedAtText;
        QualityText.Text = model.QualityText;
        if (string.IsNullOrWhiteSpace(model.FreshnessHint))
        {
            FreshnessText.Visibility = Visibility.Collapsed;
            FreshnessText.Text = string.Empty;
        }
        else
        {
            FreshnessText.Visibility = Visibility.Visible;
            FreshnessText.Text = model.FreshnessHint;
        }

        if (string.IsNullOrWhiteSpace(model.BannerMessage))
        {
            BannerHost.Visibility = Visibility.Collapsed;
            BannerText.Text = string.Empty;
        }
        else
        {
            BannerHost.Visibility = Visibility.Visible;
            BannerText.Text = model.BannerMessage;
        }

        if (model.UserAttentionCount > 0)
        {
            UserStrip.Visibility = Visibility.Visible;
            UserCountText.Text = string.Format(ConsumerCopy.T(_lang, "user_n"), model.UserAttentionCount);
            UserList.ItemsSource = model.UserAttentions;
        }
        else
        {
            UserStrip.Visibility = Visibility.Collapsed;
            UserCountText.Text = string.Empty;
            UserList.ItemsSource = null;
        }

        if (string.IsNullOrWhiteSpace(model.IssuesSummary))
        {
            IssuesText.Visibility = Visibility.Collapsed;
            IssuesText.Text = string.Empty;
        }
        else
        {
            IssuesText.Visibility = Visibility.Visible;
            IssuesText.Text = model.IssuesSummary;
        }

        _suppressSelectionEvent = true;
        var restoreFocusable = ProjectList.Focusable;
        ProjectList.Focusable = false;
        try
        {
            ProjectList.ItemsSource = model.Projects;
            if (model.Projects.Count == 0)
            {
                EmptyText.Visibility = Visibility.Visible;
                EmptyText.Text = model.BannerMessage ?? ConsumerCopy.T(_lang, "empty_projects");
                ProjectList.SelectedItem = null;
            }
            else
            {
                EmptyText.Visibility = Visibility.Collapsed;
                var selectedRow = model.Projects.FirstOrDefault(p => p.IsSelected)
                    ?? model.Projects[0];
                ProjectList.SelectedItem = selectedRow;
                var host = Window.GetWindow(this);
                if (host is { IsActive: true } && model.CurrentCount > 4)
                    ProjectList.ScrollIntoView(selectedRow);
            }
            ProjectList.UpdateLayout();
            StampItemAutomation(model);
        }
        finally
        {
            ProjectList.Focusable = restoreFocusable;
            _suppressSelectionEvent = false;
        }
    }

    private void StampItemAutomation(HudModel model)
    {
        foreach (var row in model.Projects)
        {
            if (ProjectList.ItemContainerGenerator.ContainerFromItem(row) is ListBoxItem item)
            {
                System.Windows.Automation.AutomationProperties.SetAutomationId(item, row.ProjectId);
                System.Windows.Automation.AutomationProperties.SetName(item, row.Name);
            }
        }
    }

    private void OnProjectSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionEvent) return;
        if (ProjectList.SelectedItem is HudProjectRow row)
        {
            if (_selectedProjectId == row.ProjectId) return;
            _selectedProjectId = row.ProjectId;
            if (_snapshot is not null)
            {
                CurrentModel = HudPresentation.Build(_snapshot, _selectedProjectId, _showHistory, StatusLanguage.DefaultHudMaxLen, _lang);
            }

            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnHistoryToggle(object sender, RoutedEventArgs e)
    {
        _showHistory = !_showHistory;
        if (_snapshot is null) return;
        var model = HudPresentation.Build(_snapshot, _selectedProjectId, _showHistory, StatusLanguage.DefaultHudMaxLen, _lang);
        _selectedProjectId = model.SelectedProjectId;
        CurrentModel = model;
        ApplyModel(model);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
}
