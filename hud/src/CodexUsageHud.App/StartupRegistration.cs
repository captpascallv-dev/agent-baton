using Microsoft.Win32;
using System.IO;

namespace CodexUsageHud.App;

public static class StartupRegistration
{
    public const string ValueName = "CodexUsageHUD";
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    public static void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                ?? throw new InvalidOperationException("startup_registry_unavailable");
            key.SetValue(ValueName, BuildCommand(CurrentExecutablePath()), RegistryValueKind.String);
            return;
        }

        using var existing = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        existing?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    public static void RepairIfEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var registered = key?.GetValue(ValueName) as string;
        if (string.IsNullOrWhiteSpace(registered)) return;
        var executable = CurrentExecutablePath();
        if (CommandTargetsExecutable(registered, executable)) return;
        SetEnabled(true);
    }

    public static string BuildCommand(string executablePath)
    {
        var fullPath = Path.GetFullPath(executablePath);
        return $"\"{fullPath}\" --autostart";
    }

    public static bool CommandTargetsExecutable(string? command, string executablePath) =>
        string.Equals(command?.Trim(), BuildCommand(executablePath),
            StringComparison.OrdinalIgnoreCase);

    private static string CurrentExecutablePath() =>
        Path.GetFullPath(Environment.ProcessPath
            ?? throw new InvalidOperationException("startup_executable_unavailable"));
}
