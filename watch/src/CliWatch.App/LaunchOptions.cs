// SPDX-License-Identifier: MPL-2.0
namespace CliWatch.App;

public sealed class LaunchOptions
{
    public string? ConfigPath { get; init; }
    public string? DataDir { get; init; }
    public string? InstanceName { get; init; }
    public string? ScreenshotPath { get; init; }
    public string? ScreenshotExpandedPath { get; init; }
    public string? ScreenshotHistoryPath { get; init; }
    public bool ExitAfterCapture { get; init; }
    public bool StartExpanded { get; init; }
    public string? ProbeOutput { get; init; }

    public static LaunchOptions Parse(string[] args)
    {
        string? config = null, data = null, instance = null, shot = null, shotEx = null, shotHist = null, probe = null;
        var exit = false;
        var expanded = false;
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (Match(a, "--config", "-ConfigPath") && i + 1 < args.Length) config = args[++i];
            else if (a.StartsWith("--config=", StringComparison.OrdinalIgnoreCase)) config = a["--config=".Length..];
            else if (Match(a, "--data-dir", "-DataDir") && i + 1 < args.Length) data = args[++i];
            else if (a.StartsWith("--data-dir=", StringComparison.OrdinalIgnoreCase)) data = a["--data-dir=".Length..];
            else if (Match(a, "--instance-name", "-InstanceName") && i + 1 < args.Length) instance = args[++i];
            else if (a.StartsWith("--instance-name=", StringComparison.OrdinalIgnoreCase)) instance = a["--instance-name=".Length..];
            else if (Match(a, "--screenshot", "-Screenshot") && i + 1 < args.Length) shot = args[++i];
            else if (a.StartsWith("--screenshot=", StringComparison.OrdinalIgnoreCase)) shot = a["--screenshot=".Length..];
            else if (Match(a, "--screenshot-expanded") && i + 1 < args.Length) { shotEx = args[++i]; expanded = true; }
            else if (a.StartsWith("--screenshot-expanded=", StringComparison.OrdinalIgnoreCase)) { shotEx = a["--screenshot-expanded=".Length..]; expanded = true; }
            else if (Match(a, "--screenshot-history") && i + 1 < args.Length) shotHist = args[++i];
            else if (a.StartsWith("--screenshot-history=", StringComparison.OrdinalIgnoreCase)) shotHist = a["--screenshot-history=".Length..];
            else if (Match(a, "--probe-out") && i + 1 < args.Length) probe = args[++i];
            else if (a.StartsWith("--probe-out=", StringComparison.OrdinalIgnoreCase)) probe = a["--probe-out=".Length..];
            else if (Match(a, "--exit-after-capture")) exit = true;
            else if (Match(a, "--expanded")) expanded = true;
        }

        return new LaunchOptions
        {
            ConfigPath = config,
            DataDir = data,
            InstanceName = instance,
            ScreenshotPath = shot,
            ScreenshotExpandedPath = shotEx,
            ScreenshotHistoryPath = shotHist,
            ExitAfterCapture = exit,
            StartExpanded = expanded,
            ProbeOutput = probe
        };
    }

    static bool Match(string actual, params string[] names)
    {
        foreach (var n in names)
        {
            if (actual.Equals(n, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
