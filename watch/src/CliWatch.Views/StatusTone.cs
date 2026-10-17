// SPDX-License-Identifier: MPL-2.0
using CliWatch.Projection;

namespace CliWatch.Views;

public static class StatusTone
{
    public static string Kind(string? phase) => phase switch
    {
        CliWatchStatus.RunningActive => "active",
        CliWatchStatus.Waiting => "wait",
        CliWatchStatus.RunningQuiet => "quiet",
        CliWatchStatus.RunningUnknown => "unknown",
        CliWatchStatus.Delivered => "delivered",
        CliWatchStatus.ReportedFailure => "wait",
        CliWatchStatus.ProcessGone => "unknown",
        CliWatchStatus.TransportFailed => "failed",
        CliWatchStatus.Failed => "failed",
        CliWatchStatus.Retired => "retired",
        CliWatchStatus.ExecutorFinished => "finished",
        _ => "idle"
    };
}
