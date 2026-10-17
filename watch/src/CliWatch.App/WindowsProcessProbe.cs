// SPDX-License-Identifier: MPL-2.0
using CliWatch.Contracts;

namespace CliWatch.App;

/// <summary>
/// Windows PID + startTicks identity probe. On Linux / non-Windows always
/// returns State=unknown. Missing PID or access errors are unknown, never invented dead.
/// </summary>
public sealed class WindowsProcessProbe : IProcessProbe
{
    public ValueTask<ProcessIdentityObservation> ObserveAsync(int pid, long expectedStartTicks, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromResult(Unknown(pid, expectedStartTicks));

        if (!OperatingSystem.IsWindows())
            return ValueTask.FromResult(Unknown(pid, expectedStartTicks));

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            long ticks;
            try
            {
                ticks = process.StartTime.ToUniversalTime().Ticks;
            }
            catch (Exception)
            {
                return ValueTask.FromResult(Unknown(pid, expectedStartTicks));
            }

            // PID reuse: same pid with different start ticks is not the consumed identity.
            var state = ticks == expectedStartTicks ? "alive" : "dead";
            return ValueTask.FromResult(new ProcessIdentityObservation(pid, expectedStartTicks, state, DateTimeOffset.UtcNow));
        }
        catch (ArgumentException)
        {
            return ValueTask.FromResult(Unknown(pid, expectedStartTicks));
        }
        catch (InvalidOperationException)
        {
            return ValueTask.FromResult(Unknown(pid, expectedStartTicks));
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return ValueTask.FromResult(Unknown(pid, expectedStartTicks));
        }
        catch (Exception)
        {
            return ValueTask.FromResult(Unknown(pid, expectedStartTicks));
        }
    }

    public static ProcessIdentityObservation Unknown(int pid, long expectedStartTicks) =>
        new(pid, expectedStartTicks, "unknown", DateTimeOffset.UtcNow);
}
