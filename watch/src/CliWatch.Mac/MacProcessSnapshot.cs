// SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using System.Globalization;
using CliWatch.Contracts;
namespace CliWatch.Mac;

/// <summary>Opt-in fixture PIDs only. No session files, executable paths or host-wide argv scan.
/// PID/PPID topology comes from ps; argv is read only for selected roots and descendants.
/// CPU totals are available but IO counters are not, so CountersAvailable remains false.
/// Process presence is not generation, delivery, success or acceptance.</summary>
public sealed class MacProcessSnapshot : IProcessSnapshot, IProcessProbe
{
    readonly HashSet<int> roots;
    readonly IReadOnlyDictionary<int, long> expected;
    HashSet<int> selected = new();
    public MacProcessSnapshot(IEnumerable<int>? allowedPids = null, IReadOnlyDictionary<int, long>? expectedStartTicks = null)
    {
        roots = new((allowedPids ?? Array.Empty<int>()).Where(p => p > 0));
        expected = expectedStartTicks ?? new Dictionary<int, long>();
    }
    bool RootMatches(int pid)
    {
        if (!expected.TryGetValue(pid, out var ticks)) return false;
        try { using var p = Process.GetProcessById(pid); return !p.HasExited && p.StartTime.ToUniversalTime().Ticks == ticks; }
        catch (Exception) { return false; }
    }
    static string Ps(params string[] args)
    {
        var info = new ProcessStartInfo("/bin/ps") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Process metadata unavailable");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(3000)) { process.Kill(); throw new IOException("Process metadata timeout"); }
        Task.WaitAll(output, errors);
        return process.ExitCode == 0 ? output.Result : "";
    }
    public IReadOnlyList<ObservedProcess> List(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS() || roots.Count == 0) return Array.Empty<ObservedProcess>();
        cancellationToken.ThrowIfCancellationRequested();
        var topology = new Dictionary<int, int>();
        try
        {
            foreach (var line in Ps("-axo", "pid=,ppid=").Split('\n'))
            {
                var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length == 2 && int.TryParse(fields[0], out var pid) && int.TryParse(fields[1], out var parent)) topology[pid] = parent;
            }
        }
        catch (Exception) { selected.Clear(); return Array.Empty<ObservedProcess>(); }
        selected = new HashSet<int>(roots.Where(pid => topology.ContainsKey(pid) && RootMatches(pid)));
        while (true)
        {
            var descendants = topology.Where(p => selected.Contains(p.Value)).Select(p => p.Key).ToArray();
            var count = selected.Count; selected.UnionWith(descendants); if (count == selected.Count) break;
        }
        var result = new List<ObservedProcess>();
        foreach (var pid in selected.Order().Take(128))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var process = Process.GetProcessById(pid);
                var started = process.StartTime.ToUniversalTime();
                var command = Ps("-p", pid.ToString(CultureInfo.InvariantCulture), "-o", "command=").Trim();
                if (process.HasExited || process.StartTime.ToUniversalTime() != started) continue;
                result.Add(new ObservedProcess(pid, topology[pid], started.Ticks, new DateTimeOffset(started),
                    process.ProcessName, null, command.Length > 32768 ? null : command,
                    process.TotalProcessorTime.Ticks, null, null, false));
            }
            catch (Exception) { /* inaccessible/raced process is omitted, never called dead */ }
        }
        return result;
    }
    public ProcessIdentityObservation Observe(int pid, long expectedStartTicks)
    {
        var state = "unknown";
        if (OperatingSystem.IsMacOS() && expectedStartTicks > 0 && (roots.Contains(pid) || selected.Contains(pid)))
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (!process.HasExited) state = process.StartTime.ToUniversalTime().Ticks == expectedStartTicks ? "alive" : "dead";
            }
            catch (Exception) { }
        }
        return new ProcessIdentityObservation(pid, expectedStartTicks, state, DateTimeOffset.UtcNow);
    }
    public ValueTask<ProcessIdentityObservation> ObserveAsync(int pid, long expectedStartTicks, CancellationToken cancellationToken) =>
        ValueTask.FromResult(cancellationToken.IsCancellationRequested ? new ProcessIdentityObservation(pid, expectedStartTicks, "unknown", DateTimeOffset.UtcNow) : Observe(pid, expectedStartTicks));
}
public sealed class MacProcessProbe : IProcessProbe
{
    readonly MacProcessSnapshot snapshot;
    public MacProcessProbe(MacProcessSnapshot? snapshot = null) => this.snapshot = snapshot ?? new MacProcessSnapshot();
    public ValueTask<ProcessIdentityObservation> ObserveAsync(int pid, long expectedStartTicks, CancellationToken cancellationToken) => snapshot.ObserveAsync(pid, expectedStartTicks, cancellationToken);
}
