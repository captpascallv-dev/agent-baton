// SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using CliWatch.App;
using CliWatch.Contracts;
namespace CliWatch.Mac;

/// <summary>Conservative fixture-only boundary, not a sandbox against concurrent hostile writers.
/// Revalidate before each collection. No private/native session discovery is enabled.</summary>
public static class FixtureBoundary
{
    public static string CanonicalSystemPath(string path)
    {
        var full=Path.GetFullPath(path);
        // macOS system aliases only; arbitrary user symlinks remain forbidden.
        if (OperatingSystem.IsMacOS())
            foreach(var alias in new[] { "/tmp", "/var" })
                if (full == alias || full.StartsWith(alias+"/",StringComparison.Ordinal)) return "/private"+full;
        return full;
    }
    public static void RequireNoLinks(string path)
    {
        for(string? part=CanonicalSystemPath(path); part is not null; part=Path.GetDirectoryName(part))
            if ((File.Exists(part)||Directory.Exists(part)) && (File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)
                throw new ArgumentException("Symlink paths are not allowed");
    }
    public static string CheckedPath(string path, string allowedRoot)
    {
        var root = CanonicalSystemPath(allowedRoot).TrimEnd(Path.DirectorySeparatorChar);
        var full = CanonicalSystemPath(Path.GetFullPath(path, root));
        if (root == Path.GetPathRoot(root) || root.Equals(CanonicalSystemPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), StringComparison.OrdinalIgnoreCase)
            || !(full == root || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            || full.Split(Path.DirectorySeparatorChar).Any(p => p.Equals(".codex", StringComparison.OrdinalIgnoreCase) || p.Equals(".claude", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Fixture path outside allowed boundary");
        for (string? part = full; part is not null; part = Path.GetDirectoryName(part))
            if ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Fixture symlinks are not allowed");
        return full;
    }
    public static CollectorSettings Validate(AppConfig config, string allowedRoot)
    {
        var root = CheckedPath(allowedRoot, allowedRoot);
        if (!Directory.Exists(root) || config.GrokHome is not null || config.DiscoveryRules.Count != 0
            || config.CurrentScopeSourcesFile is not null || config.DisplayCloseoutsFile is not null
            || config.AllowNetwork || !config.ReadOnly || config.RejectedSecretKeys.Count > 0)
            throw new ArgumentException("Only read-only task fixture sources are supported");
        // Per-read collector gate enforces indirect references; do not read unrelated JSON here.
        string[] Paths(IReadOnlyList<string> paths) => paths.Select(p => CheckedPath(p, root)).ToArray();
        return new CollectorSettings(Paths(config.RegistryPaths), Paths(config.AdditionalSourcePaths),
            Math.Min(config.MaxFileBytes, 1048576), Math.Min(config.MaxFilesPerRefresh, 512))
        {
            MailboxRoots = Paths(config.MailboxRoots), ClaudeMailboxRoots = Paths(config.ClaudeMailboxRoots),
            QuietSeconds = config.QuietSeconds, HistoryHours = config.HistoryHours, SampleBanner = true
        };
    }
    public static IReadOnlyDictionary<int, long> OwnerIdentities(string allowedRoot)
    {
        var result = new Dictionary<int, long>();
        var pending = new Stack<string>(); pending.Push(CheckedPath(allowedRoot,allowedRoot));
        int count = 0;
        while (pending.Count > 0)
        foreach (var file in Directory.EnumerateFileSystemEntries(pending.Pop()))
        {
            CheckedPath(file, allowedRoot);
            if (++count > 4096) throw new ArgumentException("Fixture owner scan budget exceeded");
            if (Directory.Exists(file)) { pending.Push(file); continue; }
            if (Path.GetFileName(file) != "owner.json") continue;
            if (new FileInfo(file).Length > 1048576) throw new ArgumentException("Fixture owner too large");
            using var doc = JsonDocument.Parse(File.ReadAllText(CheckedPath(file,allowedRoot)));
            var data = doc.RootElement;
            if (data.TryGetProperty("protocol_version", out var protocol) && protocol.GetString() == "agent-baton-runner-owner-v1"
                && data.TryGetProperty("pid", out var pid) && pid.TryGetInt32(out var id) && id > 0
                && data.TryGetProperty("start_time_utc_ticks", out var ticks) && ticks.TryGetInt64(out var start) && start > 0)
                result[id] = start;
        }
        return result;
    }
}
