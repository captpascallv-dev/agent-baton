using Microsoft.Win32.SafeHandles;
using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace CodexUsageHud.Core;

public sealed record RolloutFile(
    string FullPath,
    string RelativePath,
    SourceIdentity Identity,
    long Length,
    DateTimeOffset LastWriteTimeUtc);

public static class CodexHomeResolver
{
    public static string Resolve(string? explicitHome = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitHome))
        {
            return Path.GetFullPath(explicitHome);
        }

        var configured = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(profile, ".codex");
    }
}

public sealed class FileIdentityProvider
{
    private readonly bool _forceDegraded;

    public FileIdentityProvider(bool forceDegraded = false)
    {
        _forceDegraded = forceDegraded;
    }

    public SourceIdentity Get(string path, string threadId)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!_forceDegraded && OperatingSystem.IsWindows() &&
            TryGetWindowsIdentity(fullPath, out var volume, out var fileId))
        {
            return new SourceIdentity(threadId, volume, fileId, string.Empty, false);
        }

        var creationTicks = info.Exists ? info.CreationTimeUtc.Ticks : 0L;
        return new SourceIdentity(threadId, string.Empty, string.Empty,
            BuildFallbackDigest(threadId, fullPath, creationTicks), true);
    }

    internal static string BuildFallbackDigest(string threadId, string fullPath, long creationTicks)
    {
        var canonical = string.Concat("fallback-v2\0", threadId, "\0",
            NormalizeCanonicalPath(fullPath), "\0", creationTicks.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    internal static string NormalizeCanonicalPath(string path)
    {
        var full = Path.GetFullPath(path).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) full = full[4..];
        return full.TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
    }

    private static bool TryGetWindowsIdentity(string path, out string volume, out string fileId)
    {
        volume = string.Empty;
        fileId = string.Empty;
        using var handle = CreateFile(path, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero,
            FileMode.Open, FileAttributes.Normal | FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, FileInfoByHandleClass.FileIdInfo,
                out var info, (uint)Marshal.SizeOf<FileIdInfo>()))
        {
            return false;
        }

        Span<byte> identifier = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(identifier[..8], info.FileId.Part0);
        BinaryPrimitives.WriteUInt64LittleEndian(identifier[8..], info.FileId.Part1);
        volume = info.VolumeSerialNumber.ToString("X16");
        fileId = Convert.ToHexString(identifier);
        return true;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        FileMode creationDisposition,
        FileAttributes flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle handle,
        FileInfoByHandleClass fileInformationClass,
        out FileIdInfo fileInformation,
        uint bufferSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public FileId128 FileId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileId128
    {
        public ulong Part0;
        public ulong Part1;
    }

    private enum FileInfoByHandleClass
    {
        FileIdInfo = 18,
    }

    private const FileAttributes FileFlagBackupSemantics = (FileAttributes)0x02000000;
}

public sealed class RolloutDiscovery
{
    private readonly FileIdentityProvider _identityProvider;

    public RolloutDiscovery(FileIdentityProvider? identityProvider = null)
    {
        _identityProvider = identityProvider ?? new FileIdentityProvider();
    }

    public IReadOnlyList<RolloutFile> Discover(string codexHome, string defaultThreadId = "unknown-thread")
    {
        var root = Path.GetFullPath(codexHome);
        var result = new List<RolloutFile>();
        foreach (var folder in new[] { "sessions", "archived_sessions" })
        {
            var directory = Path.Combine(root, folder);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            try
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    ReturnSpecialDirectories = false,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                };
                foreach (var path in Directory.EnumerateFiles(directory, "*.jsonl", options))
                {
                    try
                    {
                        var info = new FileInfo(path);
                        var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                        result.Add(new RolloutFile(path, relative, _identityProvider.Get(path, defaultThreadId),
                            info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)));
                    }
                    catch (FileNotFoundException)
                    {
                        // A concurrently rotated file will be seen on the next periodic reconciliation.
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // An inaccessible source is contained to this source and remains visible as unavailable.
                    }
                    catch (IOException)
                    {
                        // A concurrently changing source is retried by periodic reconciliation.
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }
        }

        return result.OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenBy(file => file.RelativePath, StringComparer.Ordinal).ToArray();
    }
}
