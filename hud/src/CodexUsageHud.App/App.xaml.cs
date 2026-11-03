using CodexUsageHud.Core;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace CodexUsageHud.App;

public partial class App : System.Windows.Application
{
    private UsageEngine? _engine;
    private MainWindow? _window;
    private SingleInstanceGate? _instanceGate;
    private SingleInstanceActivationServer? _activationServer;
    private AsyncResourceLease? _runtimeLease;
    private int _activationPending;

    private async void OnStartup(object sender, StartupEventArgs e)
    {
        var isolated = IsolatedPreviewLaunch.IsIsolated(e.Args);
        IsolatedPreviewLaunch.MarkCurrentProcess(isolated);
        _ = SetCurrentProcessExplicitAppUserModelID(
            isolated ? IsolatedPreviewLaunch.AppUserModelId : "CodexUsageHud.App");
        if (!IsolatedPreviewLaunch.PreviewPackageAllowsStart(e.Args))
        {
            System.Windows.MessageBox.Show(
                "隔离预览必须同时指定 --data-dir 与 --codex-home，避免写入已安装 HUD 的数据或启动项。",
                "Codex Usage HUD 隔离预览", MessageBoxButton.OK, MessageBoxImage.Information);
            Environment.ExitCode = 6;
            Shutdown(6);
            return;
        }

        if (!isolated)
        {
            try { StartupRegistration.RepairIfEnabled(); }
            catch { }
        }
        StartupRuntime runtime;
        try
        {
            runtime = await Task.Run(() => CreateRuntime(e.Args));
        }
        catch
        {
            Environment.ExitCode = 4;
            Shutdown(4);
            return;
        }

        if (runtime.Outcome == InstanceGateOutcome.Contended)
        {
            var signaled = await SingleInstanceActivationServer.TrySignalAsync(
                runtime.DataDirectory, TimeSpan.FromSeconds(3));
            if (!signaled)
            {
                System.Windows.MessageBox.Show(
                    "Codex Usage HUD 已在运行，但暂时无法唤回。请在任务栏右下角的隐藏图标中打开它。",
                    "Codex Usage HUD", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            Shutdown(signaled ? 0 : 2);
            return;
        }
        if (runtime.Outcome != InstanceGateOutcome.Acquired || runtime.Gate is null ||
            runtime.Engine is null || runtime.ActivationServer is null)
        {
            Environment.ExitCode = 3;
            System.Windows.MessageBox.Show(runtime.GateCode, "Codex Usage HUD",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(3);
            return;
        }
        var gate = runtime.Gate;
        var engine = runtime.Engine;
        var activationServer = runtime.ActivationServer;
        _instanceGate = gate;
        _engine = engine;
        _activationServer = activationServer;
        _runtimeLease = new AsyncResourceLease(() =>
        {
            activationServer.Dispose();
            engine.Dispose();
            gate.Dispose();
            _engine = null;
            _instanceGate = null;
            _activationServer = null;
        });

        try
        {
            _window = new MainWindow(engine, DisposeRuntimeAsync);
            if (e.Args.Contains("--qa-show-taskbar", StringComparer.Ordinal))
                _window.ShowInTaskbar = true;
            if (e.Args.Contains("--qa-offscreen", StringComparer.Ordinal))
            {
                _window.ShowActivated = false;
                _window.ShowInTaskbar = false;
                _window.Left = SystemParameters.VirtualScreenLeft - 4000;
                _window.Top = SystemParameters.VirtualScreenTop - 4000;
            }
            MainWindow = _window;
            _window.Show();
            if (Interlocked.Exchange(ref _activationPending, 0) != 0)
                _window.RestoreFromExternalActivation();
            var qaExitDelay = ResolveQaExitDelay(e.Args);
            if (qaExitDelay.HasValue)
            {
                await Task.Delay(qaExitDelay.Value);
                if (_window is not null) await _window.RequestQualityAssuranceExitAsync();
            }
        }
        catch
        {
            try { await DisposeRuntimeAsync(); }
            catch { }
            Environment.ExitCode = 4;
            Shutdown(4);
        }
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        // Explicit exit drains app I/O and disposes runtime resources before Shutdown.
    }

    private Task DisposeRuntimeAsync()
    {
        var lease = Volatile.Read(ref _runtimeLease);
        return lease?.DisposeAsync() ?? Task.CompletedTask;
    }

    private StartupRuntime CreateRuntime(IReadOnlyList<string> arguments)
    {
        var localData = ResolveDataDirectory(arguments);
        var outcome = SingleInstanceGate.TryAcquire(localData, out var gate, out var gateCode);
        if (outcome != InstanceGateOutcome.Acquired || gate is null)
            return new StartupRuntime(outcome, gateCode, localData, null, null, null);
        SingleInstanceActivationServer? activationServer = null;
        try
        {
            activationServer = new SingleInstanceActivationServer(localData, QueueActivationAsync);
            var codexHome = IsolatedPreviewLaunch.ResolveCodexHome(arguments);
            var engine = new UsageEngine(codexHome, Path.Combine(localData, "usage.db"),
                Path.Combine(localData, "hud.log"),
                productVersion: HudReleaseVersion.FromAssembly(typeof(App).Assembly));
            return new StartupRuntime(outcome, gateCode, localData, gate, engine, activationServer);
        }
        catch
        {
            activationServer?.Dispose();
            gate.Dispose();
            throw;
        }
    }

    private Task QueueActivationAsync()
    {
        Interlocked.Exchange(ref _activationPending, 1);
        return Dispatcher.InvokeAsync(() =>
        {
            if (_window is null) return;
            Interlocked.Exchange(ref _activationPending, 0);
            _window.RestoreFromExternalActivation();
        }).Task;
    }

    private static string ResolveDataDirectory(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (arguments[index].Equals("--data-dir", StringComparison.Ordinal))
                return Path.GetFullPath(arguments[index + 1]);
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageHUD");
    }

    private static TimeSpan? ResolveQaExitDelay(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (arguments[index].Equals("--qa-exit-after-ms", StringComparison.Ordinal) &&
                int.TryParse(arguments[index + 1], out var milliseconds) &&
                milliseconds is >= 500 and <= 60_000)
            {
                return TimeSpan.FromMilliseconds(milliseconds);
            }
        }
        return null;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    private sealed record StartupRuntime(InstanceGateOutcome Outcome, string GateCode,
        string DataDirectory, SingleInstanceGate? Gate, UsageEngine? Engine,
        SingleInstanceActivationServer? ActivationServer);
}

public sealed class AsyncResourceLease
{
    private readonly Lazy<Task> _disposeTask;

    public AsyncResourceLease(Action dispose)
    {
        _disposeTask = new Lazy<Task>(() => Task.Run(dispose),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public Task DisposeAsync() => _disposeTask.Value;
}

public enum InstanceGateOutcome
{
    Acquired,
    Contended,
    Error,
}

public sealed class SingleInstanceGate : IDisposable
{
    public const string LockFileName = "instance.lock";
    private FileStream? _stream;

    private SingleInstanceGate(FileStream stream)
    {
        _stream = stream;
    }

    public static InstanceGateOutcome TryAcquire(string appDataDirectory,
        out SingleInstanceGate? gate, out string gateCode)
    {
        gate = null;
        gateCode = "instance_lock_error";
        try
        {
            var directory = Path.GetFullPath(appDataDirectory);
            Directory.CreateDirectory(directory);
            if (!ValidateDirectoryChain(directory))
            {
                gateCode = "instance_lock_reparse_rejected";
                return InstanceGateOutcome.Error;
            }

            var lockPath = Path.Combine(directory, LockFileName);
            if (File.Exists(lockPath) &&
                (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0)
            {
                gateCode = "instance_lock_reparse_rejected";
                return InstanceGateOutcome.Error;
            }

            var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, 1, FileOptions.WriteThrough);
            if ((File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0)
            {
                stream.Dispose();
                gateCode = "instance_lock_reparse_rejected";
                return InstanceGateOutcome.Error;
            }
            gate = new SingleInstanceGate(stream);
            gateCode = "instance_lock_acquired";
            return InstanceGateOutcome.Acquired;
        }
        catch (IOException exception) when (IsSharingViolation(exception))
        {
            gateCode = "instance_lock_contended";
            return InstanceGateOutcome.Contended;
        }
        catch (UnauthorizedAccessException)
        {
            gateCode = "instance_lock_access_denied";
            return InstanceGateOutcome.Error;
        }
        catch (IOException)
        {
            gateCode = "instance_lock_io";
            return InstanceGateOutcome.Error;
        }
        catch (ArgumentException)
        {
            gateCode = "instance_lock_path";
            return InstanceGateOutcome.Error;
        }
        catch (NotSupportedException)
        {
            gateCode = "instance_lock_path";
            return InstanceGateOutcome.Error;
        }
    }

    private static bool ValidateDirectoryChain(string directory)
    {
        var current = new DirectoryInfo(directory);
        while (current is not null)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            current = current.Parent;
        }
        return true;
    }

    private static bool IsSharingViolation(IOException exception)
    {
        var code = exception.HResult & 0xFFFF;
        return code is 32 or 33;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _stream, null)?.Dispose();
    }
}
