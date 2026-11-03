// SPDX-License-Identifier: MPL-2.0
using CliWatch.Contracts;

namespace CliWatch.Collection;

/// <summary>
/// Cross-refresh CPU/IO deltas keyed by PID + start ticks. Absolute totals are not activity.
/// </summary>
public sealed class ProcessActivityStore
{
    readonly Dictionary<string, Sample> _last = new(StringComparer.Ordinal);
    readonly object _gate = new();

    public readonly record struct Delta(
        bool HadBaseline,
        bool CountersAvailable,
        long CpuDelta,
        long ReadDelta,
        long WriteDelta)
    {
        public bool AnyIncrease => CpuDelta > 0 || ReadDelta > 0 || WriteDelta > 0;
    }

    public Delta Diff(ObservedProcess process)
    {
        if (!process.CountersAvailable
            || process.CpuTimeTicks is null
            || process.ReadBytes is null
            || process.WriteBytes is null)
        {
            return new Delta(false, false, 0, 0, 0);
        }

        lock (_gate)
        {
            if (!_last.TryGetValue(Key(process.Pid, process.StartTicks), out var prev))
                return new Delta(false, true, 0, 0, 0);

            return new Delta(
                true,
                true,
                NonNegative(process.CpuTimeTicks.Value - prev.Cpu),
                NonNegative(process.ReadBytes.Value - prev.Read),
                NonNegative(process.WriteBytes.Value - prev.Write));
        }
    }

    public void Commit(IReadOnlyList<ObservedProcess> live)
    {
        lock (_gate)
        {
            var next = new Dictionary<string, Sample>(StringComparer.Ordinal);
            foreach (var p in live)
            {
                if (!p.CountersAvailable || p.CpuTimeTicks is null || p.ReadBytes is null || p.WriteBytes is null)
                    continue;
                next[Key(p.Pid, p.StartTicks)] = new Sample(p.CpuTimeTicks.Value, p.ReadBytes.Value, p.WriteBytes.Value);
            }

            _last.Clear();
            foreach (var kv in next)
                _last[kv.Key] = kv.Value;
        }
    }

    static string Key(int pid, long ticks) => pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + ticks;

    static long NonNegative(long v) => v < 0 ? 0 : v;

    readonly record struct Sample(long Cpu, long Read, long Write);
}
