// SPDX-License-Identifier: MPL-2.0
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using CliWatch.Contracts;

namespace CliWatch.App;

/// <summary>
/// Launch configuration. Credentials / tokens / cookies are never accepted.
/// send_messages_automatically is forced false for this product.
/// </summary>
public sealed class AppConfig
{
    public IReadOnlyList<string> RegistryPaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AdditionalSourcePaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> MailboxRoots { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ClaudeMailboxRoots { get; init; } = Array.Empty<string>();
    public IReadOnlyList<ProcessDiscoveryRule> DiscoveryRules { get; init; } = Array.Empty<ProcessDiscoveryRule>();
    public string? GrokHome { get; init; }
    public string? CurrentScopeSourcesFile { get; init; }
    public string? DisplayCloseoutsFile { get; init; }
    public int QuietSeconds { get; init; } = 120;
    public int HistoryHours { get; init; } = 72;
    public bool SampleBanner { get; init; }
    public int RefreshSeconds { get; init; } = 5;
    public int MaxFileBytes { get; init; } = 1_048_576;
    public int MaxFilesPerRefresh { get; init; } = 512;
    public bool ReadOnly { get; init; } = true;
    public bool AllowNetwork { get; init; }
    public bool SendMessagesAutomatically { get; init; }
    public string? LoadedFrom { get; init; }
    public IReadOnlyList<string> RejectedSecretKeys { get; init; } = Array.Empty<string>();

    public static readonly HashSet<string> ForbiddenKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "token", "access_token", "refresh_token", "password", "cookie", "cookies",
        "authorization", "api_key", "apikey", "secret", "client_secret",
        "credential", "credentials", "auth", "session"
    };

    public static AppConfig Load(string? explicitPath = null)
    {
        var path = explicitPath ?? FindConfigPath();
        if (path is null || !File.Exists(path))
        {
            return new AppConfig { LoadedFrom = null };
        }

        try
        {
            var json = File.ReadAllText(path);
            return Parse(json, path);
        }
        catch (Exception)
        {
            return new AppConfig { LoadedFrom = path };
        }
    }

    public static AppConfig Parse(string json, string? loadedFrom = null)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (JsonException) { return new AppConfig { LoadedFrom = loadedFrom }; }

        if (node is not JsonObject obj)
            return new AppConfig { LoadedFrom = loadedFrom };

        var rejected = new List<string>();
        foreach (var kv in obj)
        {
            if (ForbiddenKeys.Contains(kv.Key))
                rejected.Add(kv.Key);
        }

        var registry = ReadStringList(obj, "registry_paths");
        var extra = ReadStringList(obj, "additional_source_paths");
        var mailbox = ReadStringList(obj, "mailbox_roots");
        var claude = obj.ContainsKey("claude_mailbox_roots")
            ? ReadStringList(obj, "claude_mailbox_roots")
            : Array.Empty<string>();
        var refresh = ReadInt(obj, "refresh_seconds", 5);
        if (refresh < 1) refresh = 1;
        if (refresh > 3600) refresh = 3600;
        var maxBytes = ReadInt(obj, "max_file_bytes", 1_048_576);
        var maxFiles = ReadInt(obj, "max_files_per_refresh", 512);
        var quiet = ReadInt(obj, "quiet_seconds", 120);
        if (quiet < 15) quiet = 15;
        var history = ReadInt(obj, "history_hours", 72);
        if (history < 1) history = 1;

        return new AppConfig
        {
            RegistryPaths = registry,
            AdditionalSourcePaths = extra,
            MailboxRoots = mailbox,
            ClaudeMailboxRoots = claude,
            DiscoveryRules = ReadDiscoveryRules(obj),
            GrokHome = ReadString(obj, "grok_home"),
            CurrentScopeSourcesFile = ResolveOwnedPath(ReadString(obj, "current_scope_sources_file"), loadedFrom),
            DisplayCloseoutsFile = ResolveOwnedPath(ReadString(obj, "display_closeouts_file"), loadedFrom),
            QuietSeconds = quiet,
            HistoryHours = history,
            SampleBanner = ReadBool(obj, "sample_banner", false),
            RefreshSeconds = refresh,
            MaxFileBytes = maxBytes > 0 ? maxBytes : 1_048_576,
            MaxFilesPerRefresh = maxFiles > 0 ? maxFiles : 512,
            ReadOnly = ReadBool(obj, "read_only", true),
            AllowNetwork = ReadBool(obj, "allow_network", false),
            SendMessagesAutomatically = false,
            LoadedFrom = loadedFrom,
            RejectedSecretKeys = rejected
        };
    }

    public CollectorSettings ToCollectorSettings() =>
        new(RegistryPaths, AdditionalSourcePaths, MaxFileBytes, MaxFilesPerRefresh)
        {
            MailboxRoots = MailboxRoots,
            ClaudeMailboxRoots = ClaudeMailboxRoots,
            DiscoveryRules = DiscoveryRules,
            GrokHome = GrokHome,
            CurrentScopeSourcesFile = CurrentScopeSourcesFile,
            DisplayCloseoutsFile = DisplayCloseoutsFile,
            QuietSeconds = QuietSeconds,
            HistoryHours = HistoryHours,
            SampleBanner = SampleBanner
        };

    static string? ResolveOwnedPath(string? path, string? config)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (Path.IsPathFullyQualified(path)) return path;
        return config is null ? null : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(config)!, path));
    }

    public static string? FindConfigPath()
    {
        var env = Environment.GetEnvironmentVariable("CLI_WATCH_CONFIG");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            return env;

        if (File.Exists(ProductPaths.ConfigFile))
            return ProductPaths.ConfigFile;

        var baseDir = AppContext.BaseDirectory;
        foreach (var rel in new[]
                 {
                     System.IO.Path.Combine("config", "default.json"),
                     System.IO.Path.Combine("config", "production.json"),
                     System.IO.Path.Combine("config", "development.json"),
                     System.IO.Path.Combine("..", "..", "..", "..", "config", "default.json"),
                     System.IO.Path.Combine("..", "..", "..", "..", "config", "development.sample.json"),
                     System.IO.Path.Combine("..", "..", "..", "..", "..", "config", "default.json")
                 })
        {
            var candidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, rel));
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    static IReadOnlyList<string> ReadStringList(JsonObject obj, string key)
    {
        if (obj[key] is not JsonArray arr)
            return Array.Empty<string>();
        var list = new List<string>();
        foreach (var item in arr)
        {
            var s = item?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(s))
                list.Add(s);
        }
        return list;
    }

    static string? ReadString(JsonObject obj, string key)
    {
        if (obj[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
            return s;
        return null;
    }

    static IReadOnlyList<ProcessDiscoveryRule> ReadDiscoveryRules(JsonObject obj)
    {
        if (obj["process_discovery_rules"] is not JsonArray arr)
            return Array.Empty<ProcessDiscoveryRule>();
        var list = new List<ProcessDiscoveryRule>();
        foreach (var item in arr)
        {
            if (item is not JsonObject o) continue;
            var cli = ReadString(o, "cli");
            if (string.IsNullOrWhiteSpace(cli)) continue;
            var packages = ReadStringList(o, "script_packages");
            list.Add(new ProcessDiscoveryRule(
                cli,
                ReadString(o, "image"),
                ReadStringList(o, "command_contains"),
                ReadStringList(o, "command_excludes"),
                ReadStringList(o, "exclude_images"),
                packages.Count == 0 ? null : packages));
        }

        return list;
    }

    static int ReadInt(JsonObject obj, string key, int fallback)
    {
        if (obj[key] is JsonValue v && v.TryGetValue<int>(out var n))
            return n;
        return fallback;
    }

    static bool ReadBool(JsonObject obj, string key, bool fallback)
    {
        if (obj[key] is JsonValue v && v.TryGetValue<bool>(out var b))
            return b;
        return fallback;
    }
}
