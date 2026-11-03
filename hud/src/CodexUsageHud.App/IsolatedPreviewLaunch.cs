using System.IO;
using CodexUsageHud.Core;

namespace CodexUsageHud.App;

public static class IsolatedPreviewLaunch
{
    public const string MarkerFileName = "ISOLATED_PREVIEW.marker";
    public const string AppUserModelId = "CodexUsageHud.Preview";
    public const string DataDirOption = "--data-dir";
    public const string CodexHomeOption = "--codex-home";
    public const string IsolatedFlag = "--isolated-preview";
    public const string ProcessEnvironmentName = "CODEX_USAGE_HUD_ISOLATED_PREVIEW";

    public static bool CurrentProcessIsolated { get; private set; }

    public static bool MarkerPresent(string? baseDirectory = null) =>
        File.Exists(Path.Combine(baseDirectory ?? AppContext.BaseDirectory, MarkerFileName));

    public static bool HasFlag(IReadOnlyList<string> arguments, string flag) =>
        arguments.Any(argument => argument.Equals(flag, StringComparison.Ordinal));

    public static bool HasOption(IReadOnlyList<string> arguments, string option) =>
        !string.IsNullOrWhiteSpace(OptionValue(arguments, option));

    public static string? OptionValue(IReadOnlyList<string> arguments, string option)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (arguments[index].Equals(option, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(arguments[index + 1]))
            {
                return arguments[index + 1];
            }
        }

        return null;
    }

    public static bool IsIsolated(IReadOnlyList<string> arguments, string? baseDirectory = null) =>
        MarkerPresent(baseDirectory) || HasFlag(arguments, IsolatedFlag) || HasOption(arguments, DataDirOption);

    public static bool PreviewPackageAllowsStart(IReadOnlyList<string> arguments, string? baseDirectory = null) =>
        !MarkerPresent(baseDirectory) ||
        (HasOption(arguments, DataDirOption) && HasOption(arguments, CodexHomeOption));

    public static string ResolveCodexHome(IReadOnlyList<string> arguments)
    {
        var configured = OptionValue(arguments, CodexHomeOption);
        return CodexHomeResolver.Resolve(configured);
    }

    public static void MarkCurrentProcess(bool isolated)
    {
        CurrentProcessIsolated = isolated;
        if (isolated)
            Environment.SetEnvironmentVariable(ProcessEnvironmentName, "1");
    }
}
