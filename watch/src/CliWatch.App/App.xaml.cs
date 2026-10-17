// SPDX-License-Identifier: MPL-2.0
using System.Windows;
using CliWatch.Collection;
using CliWatch.Normalization;
using CliWatch.Projection;

namespace CliWatch.App;

public partial class App : Application
{
    SingleInstance? _singleInstance;
    CancellationTokenSource? _refreshCts;
    RefreshService? _refresh;
    PreferenceStore? _prefs;
    MainWindow? _main;

    void OnStartup(object sender, StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var launch = LaunchOptions.Parse(e.Args);
        if (!string.IsNullOrWhiteSpace(launch.ProbeOutput))
        {
            var code = ProbeOnce.Run(launch);
            Shutdown(code);
            return;
        }

        ProductPaths.ApplyLaunchOverrides(launch.DataDir, launch.InstanceName);

        _singleInstance = new SingleInstance();
        if (!_singleInstance.TryAcquire())
        {
            Shutdown();
            return;
        }

        ProductPaths.EnsureDataDirectory();
        var config = AppConfig.Load(launch.ConfigPath ?? ReadExplicitConfigPath(e.Args));
        _prefs = new PreferenceStore(ProductPaths.SettingsFile);
        var preferences = _prefs.Load();
        if (launch.StartExpanded)
            preferences.Collapsed = false;

        var snapshot = new WindowsProcessSnapshot();
        var probe = new WindowsProcessProbe();
        var collector = new MailboxCollector(snapshot, probe, promptBindingDirectory: ProductPaths.DataDirectory);
        var normalizer = new IdentityNormalizer();
        var projector = new CliWatchProjector();
        _refresh = new RefreshService(collector, normalizer, projector);

        var host = new CockpitHostModel();
        _main = new MainWindow(host, _refresh, config, _prefs, preferences, launch);
        _main.Closed += (_, _) =>
        {
            try { _refreshCts?.Cancel(); } catch (ObjectDisposedException) { }
            Shutdown();
        };
        _main.ShowActivated = false;
        if (launch.ExitAfterCapture)
        {
            _main.ShowInTaskbar = false;
            _main.WindowStartupLocation = WindowStartupLocation.Manual;
            _main.Left = -10000;
            _main.Top = -10000;
            _main.Topmost = false;
        }
        _main.Show();

        DispatcherUnhandledException += (_, args) =>
        {
            WriteUiError(args.Exception);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteUiError(args.Exception);
            args.SetObserved();
        };

        _refreshCts = new CancellationTokenSource();
        _ = _main.StartRefreshLoopAsync(_refreshCts.Token).ContinueWith(t =>
        {
            if (t.Exception is not null) WriteUiError(t.Exception);
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    static void WriteUiError(Exception ex)
    {
        try
        {
            var path = System.IO.Path.Combine(ProductPaths.DataDirectory, "ui-error.txt");
            System.IO.File.WriteAllText(path, DateTimeOffset.UtcNow.ToString("o") + Environment.NewLine + ex);
        }
        catch
        {
        }
    }

    void OnExit(object sender, ExitEventArgs e)
    {
        try { _refreshCts?.Cancel(); } catch (ObjectDisposedException) { }
        _refreshCts?.Dispose();
        _refresh?.Dispose();
        _singleInstance?.Dispose();
    }

    static string? ReadExplicitConfigPath(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if ((a.Equals("--config", StringComparison.OrdinalIgnoreCase)
                 || a.Equals("-ConfigPath", StringComparison.OrdinalIgnoreCase))
                && i + 1 < args.Length)
            {
                return args[i + 1];
            }

            const string prefix = "--config=";
            if (a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return a[prefix.Length..];
        }

        return null;
    }
}
