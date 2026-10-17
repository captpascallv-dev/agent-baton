// SPDX-License-Identifier: MPL-2.0
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using CliWatch.Contracts;

namespace CliWatch.App;

/// <summary>
/// Snapshot of local processes with parent pid and optional command line.
/// Missing or inaccessible processes stay unknown; PID reuse uses start ticks.
/// </summary>
public sealed class WindowsProcessSnapshot : IProcessSnapshot
{
    public IReadOnlyList<ObservedProcess> List(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return Array.Empty<ObservedProcess>();

        var parents = NativeProcess.Parents();
        var list = new List<ObservedProcess>();
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception)
        {
            return Array.Empty<ObservedProcess>();
        }

        try
        {
            foreach (var process in processes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int pid;
                string name;
                try
                {
                    pid = process.Id;
                    name = process.ProcessName + ".exe";
                }
                catch (Exception)
                {
                    continue;
                }

                DateTimeOffset started;
                long ticks;
                try
                {
                    var start = process.StartTime.ToUniversalTime();
                    started = new DateTimeOffset(DateTime.SpecifyKind(start, DateTimeKind.Utc));
                    ticks = start.Ticks;
                }
                catch (Exception)
                {
                    continue;
                }

                parents.TryGetValue(pid, out var parent);
                var imageName = name;
                string? imagePath = null;
                if (NeedsImagePath(name))
                {
                    try { imagePath = process.MainModule?.FileName; }
                    catch (Exception) { }
                    if (!string.IsNullOrWhiteSpace(imagePath))
                        imageName = Path.GetFileName(imagePath);
                }

                var command = NeedsCommandLine(imageName) ? NativeProcess.TryCommandLine(pid) : null;
                long? cpu = null;
                try { cpu = process.TotalProcessorTime.Ticks; }
                catch (Exception) { }

                var io = NativeProcess.TryIoCounters(pid);
                var counters = cpu is not null && io is not null;
                list.Add(new ObservedProcess(
                    pid,
                    parent,
                    ticks,
                    started,
                    imageName,
                    imagePath,
                    command,
                    cpu,
                    io?.Read,
                    io?.Write,
                    counters));
            }
        }
        finally
        {
            foreach (var p in processes)
            {
                try { p.Dispose(); } catch (Exception) { }
            }
        }

        return list;
    }

    static bool NeedsCommandLine(string image)
    {
        var n = Path.GetFileName(image);
        return n.Equals("grok.exe", StringComparison.OrdinalIgnoreCase)
               || n.Equals("claude.exe", StringComparison.OrdinalIgnoreCase)
               || n.Equals("codex.exe", StringComparison.OrdinalIgnoreCase)
               || n.Equals("pi.exe", StringComparison.OrdinalIgnoreCase)
               || n.Equals("cursor-agent.exe", StringComparison.OrdinalIgnoreCase)
               || n.Equals("node.exe", StringComparison.OrdinalIgnoreCase)
               || n.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase)
               || n.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase);
    }

    static bool NeedsImagePath(string image) => NeedsCommandLine(image);

    public ProcessIdentityObservation Observe(int pid, long expectedStartTicks)
    {
        if (!OperatingSystem.IsWindows())
            return new ProcessIdentityObservation(pid, expectedStartTicks, "unknown", DateTimeOffset.UtcNow);

        try
        {
            using var process = Process.GetProcessById(pid);
            long ticks;
            try
            {
                ticks = process.StartTime.ToUniversalTime().Ticks;
            }
            catch (Exception)
            {
                return new ProcessIdentityObservation(pid, expectedStartTicks, "unknown", DateTimeOffset.UtcNow);
            }

            var state = ticks == expectedStartTicks ? "alive" : "dead";
            return new ProcessIdentityObservation(pid, expectedStartTicks, state, DateTimeOffset.UtcNow);
        }
        catch (ArgumentException)
        {
            return new ProcessIdentityObservation(pid, expectedStartTicks, "dead", DateTimeOffset.UtcNow);
        }
        catch (InvalidOperationException)
        {
            return new ProcessIdentityObservation(pid, expectedStartTicks, "unknown", DateTimeOffset.UtcNow);
        }
        catch (Win32Exception)
        {
            return new ProcessIdentityObservation(pid, expectedStartTicks, "unknown", DateTimeOffset.UtcNow);
        }
        catch (Exception)
        {
            return new ProcessIdentityObservation(pid, expectedStartTicks, "unknown", DateTimeOffset.UtcNow);
        }
    }
}

internal static class NativeProcess
{
    const uint Th32CsSnapProcess = 0x00000002;
    const int ProcessCommandLineInformation = 60;
    const uint ProcessQueryLimitedInformation = 0x1000;

    public static Dictionary<int, int> Parents()
    {
        var map = new Dictionary<int, int>();
        if (!OperatingSystem.IsWindows()) return map;
        var snap = CreateToolhelp32Snapshot(Th32CsSnapProcess, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1))
            return map;
        try
        {
            var entry = new ProcessEntry32 { DwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snap, ref entry))
                return map;
            do
            {
                map[(int)entry.Th32ProcessID] = (int)entry.Th32ParentProcessID;
            } while (Process32Next(snap, ref entry));
        }
        finally
        {
            CloseHandle(snap);
        }

        return map;
    }

    public static (long Read, long Write)? TryIoCounters(int pid)
    {
        if (!OperatingSystem.IsWindows() || pid <= 0) return null;
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            if (!GetProcessIoCounters(handle, out var io))
                return null;
            return ((long)io.ReadTransferCount, (long)io.WriteTransferCount);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public static string? TryCommandLine(int pid)
    {
        if (!OperatingSystem.IsWindows() || pid <= 0) return null;
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            NtQueryInformationProcess(handle, ProcessCommandLineInformation, IntPtr.Zero, 0, out var length);
            if (length <= 0 || length > 1024 * 1024)
                return null;
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                var status = NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, length, out length);
                if (status != 0) return null;
                var us = Marshal.PtrToStructure<UnicodeString>(buffer);
                if (us.Buffer == IntPtr.Zero || us.Length == 0)
                    return null;
                return Marshal.PtrToStringUni(us.Buffer, us.Length / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(IntPtr handle, int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessIoCounters(IntPtr handle, out IoCounters counters);

    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    struct ProcessEntry32
    {
        public uint DwSize;
        public uint CntUsage;
        public uint Th32ProcessID;
        public UIntPtr Th32DefaultHeapID;
        public uint Th32ModuleID;
        public uint CntThreads;
        public uint Th32ParentProcessID;
        public int PcPriClassBase;
        public uint DwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string SzExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }
}
