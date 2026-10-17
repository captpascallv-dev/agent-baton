// SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Nodes;
using CliWatch.Contracts;

namespace CliWatch.Normalization;

internal static class NormUtil
{
    public static string? ResolveProjectId(RawDocument doc, HashSet<string> knownProjects, List<SourceIssue> issues)
    {
        var fromData = JsonField.Str(doc.Data, "project_id", "project");
        if (!string.IsNullOrWhiteSpace(fromData))
        {
            return fromData;
        }

        if (!string.IsNullOrWhiteSpace(doc.ProjectHint))
        {
            if (knownProjects.Count == 0 || knownProjects.Contains(doc.ProjectHint))
            {
                return doc.ProjectHint;
            }

            issues.Add(new SourceIssue(
                doc.Id,
                "project_hint_unverified",
                $"ProjectHint '{doc.ProjectHint}' not in registry and no protocol project_id; leaving unassigned.",
                doc.ObservedAt));
            return null;
        }

        return null;
    }

    public static void PutLink(Dictionary<string, string> links, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            links[key] = value!;
        }
    }

    public static JsonObject CloneValues(JsonObject values) => (JsonObject)values.DeepClone()!;

    public static EvidenceRef Evidence(RawDocument doc) =>
        new(doc.Id, doc.Location, doc.Kind, doc.ObservedAt, doc.FactAt, DataQuality.Fresh);

    public static JsonObject BaseAxes(string? role, string? route, string transport, string delivery, string execution, string callback, string? summary) =>
        new()
        {
            ["role"] = JsonField.AxisOrUnknown(role),
            ["route"] = JsonField.AxisOrUnknown(route),
            ["process_state"] = "unknown",
            ["turn_state"] = "unknown",
            ["execution_state"] = JsonField.AxisOrUnknown(execution),
            ["transport_state"] = JsonField.AxisOrUnknown(transport),
            ["delivery_state"] = JsonField.AxisOrUnknown(delivery),
            ["callback_state"] = JsonField.AxisOrUnknown(callback),
            ["lead_handling_state"] = "unknown",
            ["acceptance_state"] = "unknown",
            ["adoption_state"] = "unknown",
            ["goal_state"] = "unknown",
            ["summary"] = JsonField.AxisOrUnknown(summary)
        };

    public static bool LooksFailed(string? s) =>
        !string.IsNullOrWhiteSpace(s)
        && (s.Contains("FAIL", StringComparison.OrdinalIgnoreCase)
            || s.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
            || s.Contains("BLOCKED", StringComparison.OrdinalIgnoreCase));

    public static bool LooksCompleted(string? s) =>
        !string.IsNullOrWhiteSpace(s)
        && (s.Contains("COMPLETE", StringComparison.OrdinalIgnoreCase)
            || s.Equals("DONE", StringComparison.OrdinalIgnoreCase)
            || s.Equals("PASSED", StringComparison.OrdinalIgnoreCase)
            || s.Equals("PASS", StringComparison.OrdinalIgnoreCase));

    public static bool LooksCancelledArchived(JsonObject data)
    {
        var cancelled = JsonField.Bool(data, "cancelled", "canceled") == true;
        var archived = JsonField.Bool(data, "archived") == true;
        if (cancelled && archived)
            return true;

        var token = JsonField.Str(data, "archive_disposition", "disposition");
        if (string.Equals(token, "cancelled_archived", StringComparison.OrdinalIgnoreCase)
            || string.Equals(token, "canceled_archived", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var phase = JsonField.Str(data, "phase", "state");
        if (cancelled && (string.Equals(phase, "cancelled", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(phase, "canceled", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var nested = JsonField.Obj(data, "archive", "cancellation", "archived_cancellation");
        if (nested is not null && LooksCancelledArchived(nested))
            return true;

        return false;
    }

    public static void MarkCancelledArchived(JsonObject values)
    {
        values["historical"] = "true";
        values["current_active_fault"] = "false";
        values["lead_handling_state"] = "cancelled_archived";
        if (IsMissingAxis(values, "execution_state") || values["execution_state"]?.GetValue<string>() == "unknown")
            values["execution_state"] = "cancelled";
    }

    static bool IsMissingAxis(JsonObject values, string key)
    {
        if (values[key] is not JsonValue v) return true;
        return !v.TryGetValue<string>(out var s) || string.IsNullOrWhiteSpace(s) || s == "unknown";
    }

    public static bool LooksAdopted(string? s) =>
        !string.IsNullOrWhiteSpace(s)
        && (s.Contains("ADOPT", StringComparison.OrdinalIgnoreCase)
            || s.Contains("PASS_ADOPTED", StringComparison.OrdinalIgnoreCase));

    public static bool LooksLeadRepair(string? s) =>
        !string.IsNullOrWhiteSpace(s)
        && (s.Contains("FAIL_REPAIR", StringComparison.OrdinalIgnoreCase)
            || s.Contains("REPAIR_REQUIRED", StringComparison.OrdinalIgnoreCase)
            || s.Contains("NATIVE_EFFECT_BOUNDARY", StringComparison.OrdinalIgnoreCase))
        && !LooksPlatformBlock(s);

    public static bool LooksPlatformBlock(string? s) =>
        !string.IsNullOrWhiteSpace(s)
        && (s.Contains("SAFETY_RESTRICTION", StringComparison.OrdinalIgnoreCase)
            || (s.Contains("PLATFORM", StringComparison.OrdinalIgnoreCase)
                && s.Contains("BLOCK", StringComparison.OrdinalIgnoreCase)));

    public static bool LooksLeadAccepting(string? s) =>
        !string.IsNullOrWhiteSpace(s)
        && s.Contains("ACCEPTANCE_IN_PROGRESS", StringComparison.OrdinalIgnoreCase);

    public static bool IsLiveHandlerState(string? handling, string? acceptance)
    {
        var h = handling ?? string.Empty;
        var a = acceptance ?? string.Empty;
        return h.Equals("blocked_after_acceptance", StringComparison.OrdinalIgnoreCase)
               || h.Equals("lead_accepting", StringComparison.OrdinalIgnoreCase)
               || h.Equals("repair_dispatched", StringComparison.OrdinalIgnoreCase)
               || a.Equals("acceptance_in_progress", StringComparison.OrdinalIgnoreCase);
    }

    public static bool LooksInFlightDispatch(string? s) =>
        !string.IsNullOrWhiteSpace(s)
        && (s.Contains("DISPATCH", StringComparison.OrdinalIgnoreCase)
            || s.Contains("GENERATION", StringComparison.OrdinalIgnoreCase)
            || s.Contains("CORRECTION", StringComparison.OrdinalIgnoreCase)
            || s.Contains("RUNNING", StringComparison.OrdinalIgnoreCase)
            || (s.Contains("IN_PROGRESS", StringComparison.OrdinalIgnoreCase) && !LooksLeadAccepting(s)));

    public static string InferExecutionFromActive(string? state)
    {
        if (string.IsNullOrWhiteSpace(state)) return "unknown";
        if (LooksLeadRepair(state)) return "returned";
        if (LooksPlatformBlock(state) || LooksLeadAccepting(state)) return "returned";
        if (LooksInFlightDispatch(state)) return "active";
        if (LooksFailed(state) && !LooksPlatformBlock(state)) return "failed";
        if (LooksCompleted(state)) return "succeeded";
        return "active";
    }

    public static string? InferDirectRoute(JsonObject data)
    {
        var proto = JsonField.Str(data, "protocol_version") ?? string.Empty;
        if (proto.Contains("grok", StringComparison.OrdinalIgnoreCase)) return "direct-grok-cli";
        if (proto.Contains("cursor", StringComparison.OrdinalIgnoreCase)) return "direct-cursor";
        if (proto.Contains("claude", StringComparison.OrdinalIgnoreCase)) return "direct-claude-code";
        if (proto.Contains("codex", StringComparison.OrdinalIgnoreCase)) return "direct-codex-cli";
        if (proto.Contains("pi", StringComparison.OrdinalIgnoreCase)) return "direct-pi";
        return null;
    }

    public static void NoteRoute(string? route, HashSet<string> live, HashSet<string> protocol)
    {
        if (string.IsNullOrWhiteSpace(route)) return;
        // Job/receipt presence is protocol/recorded evidence, not Windows live verification.
        protocol.Add(route);
        _ = live;
    }

    public static string? ExtractLineFromRunId(string? runId)
    {
        if (string.IsNullOrWhiteSpace(runId)) return null;
        const string prefix = "telephone-";
        return runId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? runId[prefix.Length..] : null;
    }

    public static bool HasSwitchFields(JsonObject data) =>
        JsonField.Str(data, "old_line_job_id") is not null
        || JsonField.Str(data, "old_direct_job_id") is not null
        || JsonField.Bool(data, "actual_switch_complete") is not null;

    public static string? JobIdFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var n = path.Replace('\\', '/').Trim().TrimEnd('/');
        const string longPrefix = "//?/";
        if (n.StartsWith(longPrefix, StringComparison.Ordinal))
            n = n[longPrefix.Length..];
        var parts = n.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            var part = parts[i];
            if (part.Contains('.', StringComparison.Ordinal)) continue;
            if (Guid.TryParse(part, out _)) return part;
        }

        return null;
    }
}

internal sealed class HandlingRecord
{
    public required RawDocument SourceDoc { get; init; }
    public string? ProjectId { get; init; }
    public string? OldLineJobId { get; init; }
    public string? OldDirectJobId { get; init; }
    public string? NewLineJobId { get; init; }
    public string? NewDirectJobId { get; init; }
    public string? NewWorkspace { get; init; }
    public string? NewRoute { get; init; }
    public bool ActualSwitchComplete { get; init; }
    public bool? NewTransportComplete { get; init; }
    public bool? NewSuccess { get; init; }
    public string? OldRoundResult { get; init; }
    public bool? OldWriterReleased { get; init; }
    public string? CallbackLead { get; init; }
    public int? ConsumedPid { get; init; }
    public long? ConsumedTicks { get; init; }
}
