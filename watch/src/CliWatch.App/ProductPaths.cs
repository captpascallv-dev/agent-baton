// SPDX-License-Identifier: MPL-2.0
using System.IO;
using System.Runtime.InteropServices;

namespace CliWatch.App;

/// <summary>
/// CLI Watch identity. Independent of TelephoneLine, CliWatch, and Codex hosts.
/// Does not read TELEPHONE_LINE_STATE_ROOT or LEGACY_COCKPIT_* defaults.
/// </summary>
public static class ProductPaths
{
    public const string ProductIdentity = "AgentBatonWatch";
    public const string ProductTitle = "CLI Watch";
    public const string ProductTitleZh = "本机 CLI 执行窗";
    public const string DefaultMutexName = @"Local\AgentBaton.Watch.SingleInstance.v1";
    public const string SettingsFileName = "window-preferences.json";
    public const string ConfigFileName = "app-config.json";

    static string? _dataDirOverride;
    static string? _mutexOverride;

    public static void ApplyLaunchOverrides(string? dataDir, string? instanceName)
    {
        if (!string.IsNullOrWhiteSpace(dataDir))
            _dataDirOverride = Path.GetFullPath(dataDir);
        if (!string.IsNullOrWhiteSpace(instanceName))
        {
            _mutexOverride = instanceName.StartsWith(@"Local\", StringComparison.OrdinalIgnoreCase)
                ? instanceName
                : @"Local\" + instanceName.Trim();
        }
    }

    public static string MutexName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_mutexOverride))
                return _mutexOverride;
            var env = Environment.GetEnvironmentVariable("CLI_WATCH_INSTANCE_NAME");
            if (string.IsNullOrWhiteSpace(env))
                return DefaultMutexName;
            return env.StartsWith(@"Local\", StringComparison.OrdinalIgnoreCase)
                ? env
                : @"Local\" + env.Trim();
        }
    }

    public static string DataDirectory
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_dataDirOverride))
                return _dataDirOverride;

            var overrideDir = Environment.GetEnvironmentVariable("CLI_WATCH_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(overrideDir))
                return Path.GetFullPath(overrideDir);

            if (OperatingSystem.IsWindows())
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(local, ProductIdentity);
            }

            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (!string.IsNullOrWhiteSpace(xdg))
                return Path.Combine(xdg, ProductIdentity);

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "share",
                ProductIdentity);
        }
    }

    public static string SettingsFile => Path.Combine(DataDirectory, SettingsFileName);

    public static string ConfigFile => Path.Combine(DataDirectory, ConfigFileName);

    public static void EnsureDataDirectory()
    {
        Directory.CreateDirectory(DataDirectory);
    }

    public static bool IsLegacyOrSharedHostPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var n = path.Replace('\\', '/');
        return n.Contains("CodexUsageHUD", StringComparison.OrdinalIgnoreCase)
            || n.Contains("TelephoneLine", StringComparison.OrdinalIgnoreCase)
            || n.Contains("LegacyAutomationHost", StringComparison.OrdinalIgnoreCase)
            || n.Contains("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase)
            || n.Contains("/LegacyCockpit", StringComparison.OrdinalIgnoreCase)
            || n.Contains("\\LegacyCockpit", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsWindowsRuntime => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
}
