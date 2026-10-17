// SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CliWatch.Contracts;

namespace CliWatch.Collection;

/// <summary>
/// Read Grok official session metadata only: timestamps, model, effort, tool names.
/// Never read chat history, prompts, thoughts, or rewind logs.
/// </summary>
internal static class GrokSessionReader
{
    public static JsonObject? TryRead(
        string? grokHome,
        string? workspace,
        string? sessionId,
        DateTimeOffset observedAt,
        List<SourceIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return null;

        var home = string.IsNullOrWhiteSpace(grokHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok")
            : Environment.ExpandEnvironmentVariables(grokHome);
        var sessionsRoot = Path.Combine(home, "sessions");
        if (!Directory.Exists(sessionsRoot))
        {
            issues.Add(new SourceIssue(sessionsRoot, "grok_sessions_missing",
                "未找到官方 Grok 会话目录，过程活动未知。", observedAt));
            return null;
        }

        var dir = FindSessionDir(sessionsRoot, workspace, sessionId);
        if (dir is null)
        {
            issues.Add(new SourceIssue(sessionsRoot, "grok_session_missing",
                "未找到该 session 的官方记录，过程活动未知。", observedAt));
            return ActivityUnknown(sessionId, observedAt);
        }

        var result = new JsonObject
        {
            ["session_id"] = sessionId,
            ["session_dir"] = dir,
            ["activity_known"] = false
        };

        var summaryPath = Path.Combine(dir, "summary.json");
        ReadSummary(summaryPath, result, issues, observedAt);

        var signalsPath = Path.Combine(dir, "signals.json");
        ReadSignals(signalsPath, result, issues, observedAt);

        foreach (var name in new[] { "summary.json", "signals.json", "events.jsonl", "updates.jsonl" })
        {
            var path = Path.Combine(dir, name);
            TryStat(path, result, name);
        }

        var lastTool = TailLastToolName(Path.Combine(dir, "events.jsonl"));
        if (!string.IsNullOrWhiteSpace(lastTool))
            result["last_tool_name"] = lastTool;

        result["activity_known"] = result["last_active_at"] is not null
                                   || result["updated_at"] is not null
                                   || result["events.jsonl_mtime"] is not null
                                   || result["updates.jsonl_mtime"] is not null
                                   || result["summary.json_mtime"] is not null;
        return result;
    }

    static string? FindSessionDir(string sessionsRoot, string? workspace, string sessionId)
    {
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            foreach (var candidate in EncodeWorkspaceCandidates(workspace))
            {
                var direct = Path.Combine(sessionsRoot, candidate, sessionId);
                if (Directory.Exists(direct))
                    return direct;
            }
        }

        try
        {
            foreach (var group in Directory.EnumerateDirectories(sessionsRoot))
            {
                var nested = Path.Combine(group, sessionId);
                if (Directory.Exists(nested))
                    return nested;
            }
        }
        catch (Exception)
        {
            return null;
        }

        return null;
    }

    internal static IEnumerable<string> EncodeWorkspaceCandidates(string workspace)
    {
        var trimmed = workspace.Trim().TrimEnd('\\', '/');
        yield return Uri.EscapeDataString(trimmed);
        var slash = trimmed.Replace('/', '\\');
        if (!slash.Equals(trimmed, StringComparison.Ordinal))
            yield return Uri.EscapeDataString(slash);
        var forward = trimmed.Replace('\\', '/');
        if (!forward.Equals(trimmed, StringComparison.Ordinal))
            yield return Uri.EscapeDataString(forward);
    }

    static void ReadSummary(string path, JsonObject dest, List<SourceIssue> issues, DateTimeOffset observedAt)
    {
        var obj = TryReadObject(path, issues, observedAt);
        if (obj is null) return;
        CopyString(obj, dest, "current_model_id", "model_id", "model");
        CopyString(obj, dest, "reasoning_effort", "effort");
        CopyString(obj, dest, "created_at");
        CopyString(obj, dest, "updated_at");
        CopyString(obj, dest, "last_active_at");
        CopyNumber(obj, dest, "num_messages");
        CopyNumber(obj, dest, "num_chat_messages");
        CopyString(obj, dest, "session_kind");
    }

    static void ReadSignals(string path, JsonObject dest, List<SourceIssue> issues, DateTimeOffset observedAt)
    {
        var obj = TryReadObject(path, issues, observedAt);
        if (obj is null) return;
        CopyNumber(obj, dest, "toolCallCount");
        CopyString(obj, dest, "primaryModelId");
        if (obj["toolsUsed"] is JsonArray arr)
        {
            var names = new JsonArray();
            foreach (var item in arr)
            {
                if (item is JsonValue v && v.TryGetValue<string>(out var name) && !string.IsNullOrWhiteSpace(name))
                    names.Add(name);
                if (names.Count >= 24) break;
            }

            dest["tools_used"] = names;
        }
    }

    static JsonObject? TryReadObject(string path, List<SourceIssue> issues, DateTimeOffset observedAt)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > 1_048_576) return null;
            var text = PromptFileIdentity.ReadMetadata(path);
            if (string.IsNullOrWhiteSpace(text)) return null;
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (Exception ex)
        {
            issues.Add(new SourceIssue(path, "grok_session_unread",
                "官方会话元数据无法读取，过程活动按未知处理：" + ex.GetType().Name, observedAt));
            return null;
        }
    }

    static void TryStat(string path, JsonObject dest, string name)
    {
        try
        {
            if (!File.Exists(path)) return;
            var info = new FileInfo(path);
            dest[name + "_mtime"] = new DateTimeOffset(DateTime.SpecifyKind(info.LastWriteTimeUtc, DateTimeKind.Utc)).ToString("o");
            dest[name + "_bytes"] = info.Length;
        }
        catch (Exception)
        {
        }
    }

    static string? TailLastToolName(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var info = new FileInfo(path);
            if (info.Length <= 0) return null;
            var take = (int)Math.Min(8192, info.Length);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(Math.Max(0, info.Length - take), SeekOrigin.Begin);
            var buf = new byte[take];
            var n = stream.Read(buf, 0, take);
            var text = Encoding.UTF8.GetString(buf, 0, n);
            var lines = text.Split('\n');
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i].Trim();
                if (line.Length < 2 || line[0] != '{') continue;
                JsonObject? obj;
                try { obj = JsonNode.Parse(line) as JsonObject; }
                catch (JsonException) { continue; }
                if (obj is null) continue;
                var type = Str(obj, "type", "event", "phase");
                var tool = Str(obj, "tool", "toolName", "tool_name", "name");
                if (!string.IsNullOrWhiteSpace(tool)
                    && (type is null
                        || type.Contains("tool", StringComparison.OrdinalIgnoreCase)
                        || tool.Contains('_', StringComparison.Ordinal)))
                {
                    return tool;
                }
            }
        }
        catch (Exception)
        {
            return null;
        }

        return null;
    }

    static JsonObject ActivityUnknown(string sessionId, DateTimeOffset observedAt) =>
        new()
        {
            ["session_id"] = sessionId,
            ["activity_known"] = false,
            ["observed_at"] = observedAt.ToString("o")
        };

    static void CopyString(JsonObject src, JsonObject dest, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (src[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
            {
                dest[keys[0]] = s;
                return;
            }
        }
    }

    static void CopyNumber(JsonObject src, JsonObject dest, string key)
    {
        if (src[key] is JsonValue v)
        {
            if (v.TryGetValue<long>(out var l)) dest[key] = l;
            else if (v.TryGetValue<int>(out var i)) dest[key] = i;
            else if (v.TryGetValue<double>(out var d)) dest[key] = d;
        }
    }

    static string? Str(JsonObject obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (obj[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                return s;
        }

        return null;
    }
}
