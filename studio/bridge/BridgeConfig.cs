using System.Text.Json;
using CliWatch.App;

namespace AiMoeStudio.Bridge;

public sealed class BridgeConfigException : Exception;
public sealed class OutputBoundaryException : Exception;

public static class BridgeConfig
{
    public static AppConfig Load(string path)
    {
        try
        {
            // AppConfig.Load accepts bad files as defaults. Validate first so a broken
            // config cannot turn into a successful empty/idle observation.
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new BridgeConfigException();
            foreach (var key in new[] { "read_only", "allow_network", "send_messages_automatically", "sample_banner" })
                if (root.TryGetProperty(key, out var boolean) && boolean.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                    throw new BridgeConfigException();
            foreach (var key in new[] { "refresh_seconds", "quiet_seconds", "history_hours", "max_file_bytes", "max_files_per_refresh" })
                if (root.TryGetProperty(key, out var number) && !number.TryGetInt32(out _))
                    throw new BridgeConfigException();
            var sourceCount = 0;
            foreach (var key in new[] { "mailbox_roots", "claude_mailbox_roots", "registry_paths", "additional_source_paths", "process_discovery_rules" })
            {
                if (!root.TryGetProperty(key, out var list)) continue;
                if (list.ValueKind != JsonValueKind.Array) throw new BridgeConfigException();
                foreach (var entry in list.EnumerateArray())
                {
                    if (key == "process_discovery_rules")
                    {
                        if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("cli", out var cli)
                            || cli.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(cli.GetString()))
                            throw new BridgeConfigException();
                    }
                    else if (entry.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(entry.GetString()))
                        throw new BridgeConfigException();
                    sourceCount++;
                }
            }
            if (sourceCount == 0) throw new BridgeConfigException();
            var config = AppConfig.Parse(json, path);
            if (!config.ReadOnly || config.AllowNetwork || config.SampleBanner || config.RejectedSecretKeys.Count != 0)
                throw new BridgeConfigException();
            return config;
        }
        catch (BridgeConfigException) { throw; }
        catch (Exception) { throw new BridgeConfigException(); }
    }
}

public static class OutputBoundary
{
    public static void RequireIndependent(string output, string configPath, AppConfig? config)
    {
        var full = Path.GetFullPath(output);
        if (full.Equals(Path.GetFullPath(configPath), StringComparison.OrdinalIgnoreCase)) throw new OutputBoundaryException();
        if (config is null) return;
        foreach (var path in config.MailboxRoots.Concat(config.ClaudeMailboxRoots).Concat(config.RegistryPaths)
                     .Concat(config.AdditionalSourcePaths).Append(config.CurrentScopeSourcesFile ?? "").Append(config.DisplayCloseoutsFile ?? "")
                     .Append(config.GrokHome ?? ""))
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            var source = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)).TrimEnd('\\', '/');
            if (full.Equals(source, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new OutputBoundaryException();
        }
    }
}
