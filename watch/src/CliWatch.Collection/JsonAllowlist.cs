// SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Nodes;

namespace CliWatch.Collection;

/// <summary>
/// Copy observation metadata only. Drop prompt/response/transcript/secret bodies.
/// </summary>
internal static class JsonAllowlist
{
    static readonly HashSet<string> TextFields = new(StringComparer.Ordinal)
    {
        "key", "job_key", "job_id", "mailbox_root", "root", "project", "status", "state",
        "outbox", "state_root", "adapter_state_root", "adapter_job_id", "adapter_receipt", "adapter_result",
        "executor", "harness", "model", "effort", "reasoning_effort", "session_id", "workspace",
        "registered_at", "dispatched_at", "started_at_utc", "finished_at_utc", "completed_at_utc"
    };

    public static string? InvalidMetadataField(JsonObject source)
    {
        foreach (var key in TextFields)
            if (source[key] is { } node && (node is not JsonValue value || !value.TryGetValue<string>(out _)))
                return key;
        if (!CliWatch.Contracts.ObservationIdentity.TryAttempt(source, out _)) return "attempt/attempt_id";
        foreach (var key in new[] { "pid", "host_pid", "start_time_utc_ticks", "exit_code" })
        {
            if (source[key] is null) continue;
            if (source[key] is not JsonValue value) return key;
            if (value.TryGetValue<long>(out _)) continue;
            if (value.TryGetValue<string>(out var text) && long.TryParse(text, out _)) continue;
            return key;
        }
        foreach (var key in new[] { "transport_complete", "grok_success", "success" })
            if (source[key] is { } node && (node is not JsonValue value || !value.TryGetValue<bool>(out _))) return key;
        return null;
    }

    static readonly HashSet<string> DeniedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "token", "access_token", "refresh_token", "password", "cookie", "cookies",
        "authorization", "api_key", "apikey", "secret", "client_secret",
        "credential", "credentials", "auth", "session",
        "prompt", "response", "messages", "transcript", "raw_prompt", "hidden_reasoning",
        "diagnostic", "diagnostics", "reasoning", "thinking", "thought",
        "grok_result", "grok-result", "text", "content", "chat"
    };

    static readonly HashSet<string> LiftFromDenied = new(StringComparer.OrdinalIgnoreCase)
    {
        "job_id", "session_id", "native_session_id", "transport_complete",
        "success", "grok_exit_code", "error", "duration_ms", "model_id",
        "reasoning_effort", "model", "completed_at_utc", "path", "bytes", "sha256"
    };

    static readonly HashSet<string> ShortEffort = new(StringComparer.OrdinalIgnoreCase)
    {
        "xhigh", "high", "medium", "low", "xlow", "max", "minimal", "min", "default", "none"
    };

    public static JsonObject CopyOwnerHandoffAuthority(JsonObject source)
    {
        var copy = new JsonObject();
        foreach (var key in new[] { "accepted", "date", "old_thread_id", "new_thread_id" })
        {
            if (source[key] is JsonValue value)
                copy[key] = value.DeepClone();
        }

        return copy;
    }

    public static JsonObject CopySessionMetadata(JsonObject source)
    {
        var copy = new JsonObject();
        foreach (var key in new[] { "current_model_id", "model_id", "model", "reasoning_effort", "effort",
            "created_at", "updated_at", "last_active_at", "num_messages", "num_chat_messages", "session_kind" })
            if (source[key] is JsonValue value) copy[key] = value.DeepClone();
        if (source["tools_used"] is JsonArray tools)
        {
            var names = new JsonArray();
            foreach (var tool in tools)
                if (tool is JsonValue value && value.TryGetValue<string>(out var name))
                {
                    names.Add(name);
                    if (names.Count >= 24) break;
                }
            copy["tools_used"] = names;
        }
        return copy;
    }

    public static JsonObject Copy(JsonObject source)
    {
        var copy = new JsonObject();
        foreach (var kv in source)
        {
            if (DeniedKeys.Contains(kv.Key))
            {
                if (IsShortEffort(kv.Key, kv.Value))
                {
                    copy[kv.Key] = kv.Value is null ? null : Strip(kv.Value);
                    continue;
                }

                if (kv.Value is JsonObject denied)
                    Lift(copy, denied);
                continue;
            }

            copy[kv.Key] = kv.Value is null ? null : Strip(kv.Value);
        }

        return copy;
    }

    static JsonNode Strip(JsonNode node)
    {
        if (node is JsonObject obj)
            return Copy(obj);
        if (node is JsonArray arr)
        {
            var copy = new JsonArray();
            foreach (var item in arr)
                copy.Add(item is null ? null : Strip(item));
            return copy;
        }

        return node.DeepClone();
    }

    static void Lift(JsonObject dest, JsonObject src)
    {
        foreach (var key in LiftFromDenied)
        {
            if (dest[key] is not null) continue;
            if (src[key] is JsonValue v)
                dest[key] = v.DeepClone();
        }
    }

    static bool IsShortEffort(string key, JsonNode? value)
    {
        if (key is not "reasoning" and not "reasoning_effort" and not "effort")
            return false;
        if (value is not JsonValue v || !v.TryGetValue<string>(out var s) || string.IsNullOrWhiteSpace(s))
            return false;
        s = s.Trim();
        return s.Length <= 16 && ShortEffort.Contains(s);
    }
}
