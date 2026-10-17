// SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Text.Json.Nodes;
using CliWatch.Contracts;

namespace CliWatch.Normalization;

internal static class DocumentNormalizers
{
    public static void NormalizeRegistry(
        RawDocument doc,
        List<SourceFact> facts,
        List<SourceIssue> issues,
        HashSet<string> knownProjects)
    {
        var projects = JsonField.Arr(doc.Data, "projects");
        if (projects is null)
        {
            issues.Add(new SourceIssue(doc.Id, "registry_projects_missing", "Registry has no projects array.", doc.ObservedAt));
            return;
        }

        foreach (var node in projects)
        {
            if (node is not JsonObject p)
            {
                issues.Add(new SourceIssue(doc.Id, "registry_project_invalid", "A project entry is not an object.", doc.ObservedAt));
                continue;
            }

            var id = JsonField.Str(p, "project_id", "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                issues.Add(new SourceIssue(doc.Id, "registry_project_id_missing", "Project entry missing project_id.", doc.ObservedAt));
                continue;
            }

            knownProjects.Add(id);
            var name = JsonField.Str(p, "display_name", "name") ?? id;
            var status = JsonField.Str(p, "status", "registry_status") ?? "unknown";
            var paused = JsonField.Bool(p, "paused_by_user", "paused") ?? false;
            var worktree = JsonField.Str(p, "worktree", "workspace");

            var links = new Dictionary<string, string>(StringComparer.Ordinal);
            NormUtil.PutLink(links, "lead_session_id", JsonField.Str(p, "lead_thread_id", "lead_session_id"));
            NormUtil.PutLink(links, "worktree", worktree);
            NormUtil.PutLink(links, "line_job_id", JsonField.Str(p, "current_line_job_id"));
            NormUtil.PutLink(links, "direct_job_id", JsonField.Str(p, "current_direct_job_id"));
            NormUtil.PutLink(links, "line_job_root_id", NormUtil.JobIdFromPath(JsonField.Str(p, "current_line_job_root")));
            NormUtil.PutLink(links, "direct_job_root_id", NormUtil.JobIdFromPath(JsonField.Str(p, "current_direct_job_root")));
            NormUtil.PutLink(links, "native_session_id", JsonField.Str(p, "current_executor_native_session_id", "executor_native_session_id", "native_session_id"));
            NormUtil.PutLink(links, "source_object_id", id);

            var pass = JsonField.Bool(p, "product_pass");
            var humanSummary = JsonField.Str(p, "current_summary", "summary");
            var values = new JsonObject
            {
                ["name"] = name,
                ["registry_status"] = status,
                ["paused"] = paused ? "true" : "false",
                ["stage"] = JsonField.AxisOrUnknown(JsonField.Str(p, "current_state", "current_stage", "stage")),
                ["goal"] = JsonField.AxisOrUnknown(JsonField.Str(p, "goal")),
                ["goal_state"] = pass == true ? "complete" : pass == false ? "not_complete" : "unknown",
                ["summary"] = JsonField.AxisOrUnknown(humanSummary),
                ["next_step"] = JsonField.AxisOrUnknown(JsonField.Str(p, "next", "next_step"))
            };

            facts.Add(new SourceFact("project", id, id, links, values, new[] { NormUtil.Evidence(doc) }));
        }
    }

    public static void NormalizeActiveWork(
        RawDocument doc,
        List<SourceFact> facts,
        List<SourceIssue> issues,
        HashSet<string> knownProjects,
        List<HandlingRecord> handlingRecords,
        HashSet<string> routesSeenLive,
        HashSet<string> routesSeenProtocol)
    {
        var projectId = NormUtil.ResolveProjectId(doc, knownProjects, issues);
        var lineId = JsonField.Str(doc.Data, "line_job_id", "current_line_job_id");
        var directId = JsonField.Str(doc.Data, "direct_job_id", "current_direct_job_id");
        var session = JsonField.Str(doc.Data, "lead_session_id", "session_id");
        var route = JsonField.Str(doc.Data, "route", "executor_route");
        NormUtil.NoteRoute(route, routesSeenLive, routesSeenProtocol);

        var entityId = !string.IsNullOrWhiteSpace(lineId) ? "work:line:" + lineId
            : !string.IsNullOrWhiteSpace(directId) ? "work:direct:" + directId
            : "work:active:" + doc.Id;

        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        NormUtil.PutLink(links, "line_job_id", lineId);
        NormUtil.PutLink(links, "direct_job_id", directId);
        NormUtil.PutLink(links, "lead_session_id", session);
        NormUtil.PutLink(links, "native_session_id", JsonField.Str(doc.Data, "executor_native_session_id", "native_session_id"));
        NormUtil.PutLink(links, "workspace", JsonField.Str(doc.Data, "workspace", "worktree"));
        NormUtil.PutLink(links, "worktree", JsonField.Str(doc.Data, "worktree", "workspace"));
        NormUtil.PutLink(links, "source_object_id", doc.Id);

        var pendingCb = JsonField.Bool(doc.Data, "pending_callback");
        var productPass = JsonField.Bool(doc.Data, "product_pass");
        var state = JsonField.Str(doc.Data, "state", "status");
        var packageId = JsonField.Str(doc.Data, "package_id", "current_package_id");
        var humanNext = JsonField.Str(doc.Data, "next", "next_step");
        var currentResult = JsonField.Str(doc.Data, "current_result", "result");
        var currentAccepted = JsonField.Bool(doc.Data, "current_package_accepted");
        var stalePending = JsonField.Bool(doc.Data, "acceptance_pending");

        var values = new JsonObject
        {
            ["role"] = "execution",
            ["route"] = JsonField.AxisOrUnknown(route),
            ["stage"] = JsonField.AxisOrUnknown(state),
            ["summary"] = JsonField.AxisOrUnknown(humanNext ?? state),
            ["process_state"] = "unknown",
            ["turn_state"] = "unknown",
            ["execution_state"] = NormUtil.InferExecutionFromActive(state),
            ["transport_state"] = "unknown",
            ["delivery_state"] = "unknown",
            ["callback_state"] = JsonField.BoolAxis(pendingCb, "pending", "idle"),
            ["lead_handling_state"] = "unknown",
            ["acceptance_state"] = "unknown",
            ["adoption_state"] = "unknown",
            ["goal_state"] = productPass == true ? "complete" : productPass == false ? "not_complete" : "unknown",
            ["package_id"] = JsonField.AxisOrUnknown(packageId),
            ["next_step"] = JsonField.AxisOrUnknown(humanNext)
        };
        StampCollectedScope(doc, values);
        if (currentAccepted == true)
            values["prior_package_accepted"] = "true";
        if (stalePending == true)
            values["stale_acceptance_pending_flag"] = "true";
        if (!string.IsNullOrWhiteSpace(currentResult))
            values["current_result_pointer"] = currentResult;
        var switchObj = JsonField.Obj(doc.Data, "route_switch", "lead_route_switch", "consumed_route_switch");
        if (switchObj is not null)
        {
            HandlingLogic.RecordSwitch(switchObj, doc, projectId, handlingRecords);
            HandlingLogic.ApplySwitchToValues(values, links, switchObj);
        }
        else if (NormUtil.HasSwitchFields(doc.Data))
        {
            HandlingLogic.RecordSwitch(doc.Data, doc, projectId, handlingRecords);
            HandlingLogic.ApplySwitchToValues(values, links, doc.Data);
        }

        var currentFacts = new List<SourceFact>();
        var lanes = JsonField.Obj(doc.Data, "lanes");
        if (lanes is not null)
        {
            foreach (var kv in lanes)
            {
                if (kv.Value is not JsonObject lane) continue;
                currentFacts.Add(EmitActiveLaneWork(doc, projectId, kv.Key, lane, productPass, facts, routesSeenLive, routesSeenProtocol));
            }
        }

        var currentWork = JsonField.Arr(doc.Data, "current_work");
        if (currentWork is not null)
        {
            foreach (var item in currentWork)
            {
                if (item is not JsonObject row) continue;
                var role = JsonField.Str(row, "role") ?? string.Empty;
                if (role.Contains("review", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (role.Equals("lead", StringComparison.OrdinalIgnoreCase))
                {
                    MergeOrEmitCurrentLeadWork(doc, projectId, row, session, productPass, facts, currentFacts, routesSeenLive, routesSeenProtocol);
                    continue;
                }

                var workRoute = JsonField.Str(row, "route");
                if (!role.Contains("exec", StringComparison.OrdinalIgnoreCase)
                    && string.IsNullOrWhiteSpace(workRoute))
                    continue;
                var workLine = JsonField.Str(row, "line_job_id");
                var workJob = JsonField.Str(row, "job_id", "direct_job_id");
                var matches = CollectCurrentRoleMatches(currentFacts, "execution", workRoute, workLine, workJob);
                if (matches.Count == 1)
                {
                    MergeComplementaryCurrentWork(matches[0], row, doc);
                    continue;
                }

                var hasIdentity = !string.IsNullOrWhiteSpace(workRoute)
                    || !string.IsNullOrWhiteSpace(workLine)
                    || !string.IsNullOrWhiteSpace(workJob);
                if (matches.Count > 1)
                    continue;
                if (!hasIdentity && currentFacts.Any(IsCurrentExecutionFact))
                    continue;
                var key = !string.IsNullOrWhiteSpace(workRoute) ? workRoute : "exec-" + currentFacts.Count;
                currentFacts.Add(EmitActiveLaneWork(doc, projectId, key, row, productPass, facts, routesSeenLive, routesSeenProtocol));
            }
        }

        if (currentFacts.Count > 0)
        {
            HandlingLogic.EmitHistoricalFromSwitchFields(doc, projectId, facts);
            return;
        }

        ApplyCurrentHandler(values, doc.Data, state, humanNext);
        StampExecutorIdentity(values, doc.Data);
        StampNestedRoundVerdicts(values, doc.Data);

        facts.Add(new SourceFact("work", entityId, projectId, links, values, new[] { NormUtil.Evidence(doc) }));
        HandlingLogic.EmitHistoricalFromSwitchFields(doc, projectId, facts);
    }

    /// <summary>
    /// Current ACTIVE_WORK.lanes are distinct executors. Do not emit a third
    /// generic top-level execution row, and do not copy comparison product_pass
    /// onto a lane as if the whole project were accepted.
    /// </summary>
    private static SourceFact EmitActiveLaneWork(
        RawDocument doc,
        string? projectId,
        string laneKey,
        JsonObject lane,
        bool? comparisonProductPass,
        List<SourceFact> facts,
        HashSet<string> routesSeenLive,
        HashSet<string> routesSeenProtocol)
    {
        var lineId = JsonField.Str(lane, "line_job_id");
        var directId = JsonField.Str(lane, "job_id", "direct_job_id");
        var route = JsonField.Str(lane, "route");
        NormUtil.NoteRoute(route, routesSeenLive, routesSeenProtocol);
        var state = JsonField.Str(lane, "state", "status");
        var entityId = !string.IsNullOrWhiteSpace(lineId) ? "work:line:" + lineId
            : !string.IsNullOrWhiteSpace(directId) ? "work:direct:" + directId
            : "work:lane:" + laneKey + ":" + doc.Id;

        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        NormUtil.PutLink(links, "line_job_id", lineId);
        NormUtil.PutLink(links, "direct_job_id", directId);
        NormUtil.PutLink(links, "native_session_id", JsonField.Str(lane, "native_session_id", "executor_native_session_id"));
        NormUtil.PutLink(links, "lead_session_id", JsonField.Str(JsonField.Obj(doc.Data, "lead"), "session_id", "lead_session_id"));
        NormUtil.PutLink(links, "source_object_id", doc.Id);

        var values = new JsonObject
        {
            ["role"] = JsonField.Str(lane, "role") ?? "execution",
            ["route"] = JsonField.AxisOrUnknown(route),
            ["stage"] = "unknown",
            ["summary"] = "unknown",
            ["process_state"] = "unknown",
            ["turn_state"] = "unknown",
            ["execution_state"] = "unknown",
            ["transport_state"] = "unknown",
            ["delivery_state"] = "unknown",
            ["callback_state"] = "unknown",
            ["lead_handling_state"] = "unknown",
            ["acceptance_state"] = "unknown",
            ["adoption_state"] = "unknown",
            ["goal_state"] = comparisonProductPass == true ? "complete" : comparisonProductPass == false ? "not_complete" : "unknown",
            ["package_id"] = "unknown",
            ["next_step"] = "unknown",
            ["lane_key"] = laneKey
        };
        if (comparisonProductPass == false)
            values["comparison_product_pass"] = "false";
        else if (comparisonProductPass == true)
            values["comparison_product_pass"] = "true";

        StampCollectedScope(doc, values);
        ApplyLaneState(values, state);
        StampExecutorIdentity(values, lane);
        if (IsMissingIdentity(values["actor_name"]?.GetValue<string>()) && !string.IsNullOrWhiteSpace(route))
            values["actor_name"] = route;
        var fact = new SourceFact("work", entityId, projectId, links, values, new[] { NormUtil.Evidence(doc) });
        facts.Add(fact);
        return fact;
    }

    private static void MergeOrEmitCurrentLeadWork(
        RawDocument doc,
        string? projectId,
        JsonObject row,
        string? session,
        bool? comparisonProductPass,
        List<SourceFact> facts,
        List<SourceFact> currentFacts,
        HashSet<string> routesSeenLive,
        HashSet<string> routesSeenProtocol)
    {
        var existing = currentFacts.FirstOrDefault(f =>
            string.Equals(f.Values["role"]?.GetValue<string>(), "lead", StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            MergeComplementaryCurrentWork(existing, row, doc);
            return;
        }

        var leadRow = new JsonObject();
        foreach (var kv in row)
            leadRow[kv.Key] = kv.Value?.DeepClone();
        if (!leadRow.ContainsKey("role"))
            leadRow["role"] = "lead";
        var emitted = EmitActiveLaneWork(doc, projectId, "lead", leadRow, comparisonProductPass, facts, routesSeenLive, routesSeenProtocol);
        if (emitted.Links is Dictionary<string, string> leadLinks)
            PutLinkIfMissing(leadLinks, "lead_session_id", session);
        currentFacts.Add(emitted);
    }

    private static bool IsCurrentExecutionFact(SourceFact fact)
    {
        var role = fact.Values["role"]?.GetValue<string>() ?? string.Empty;
        return role.Equals("execution", StringComparison.OrdinalIgnoreCase)
               || role.Contains("exec", StringComparison.OrdinalIgnoreCase);
    }

    private static List<SourceFact> CollectCurrentRoleMatches(
        List<SourceFact> currentFacts,
        string role,
        string? route,
        string? lineId,
        string? jobId)
    {
        var matches = new List<SourceFact>();
        foreach (var fact in currentFacts)
        {
            if (!string.Equals(fact.Values["role"]?.GetValue<string>(), role, StringComparison.OrdinalIgnoreCase)
                && !(role.Equals("execution", StringComparison.OrdinalIgnoreCase) && IsCurrentExecutionFact(fact)))
                continue;
            var factRoute = fact.Values["route"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(route)
                && !string.Equals(factRoute, route, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.IsNullOrWhiteSpace(lineId)
                && fact.Links.TryGetValue("line_job_id", out var factLine)
                && !string.IsNullOrWhiteSpace(factLine)
                && !string.Equals(factLine, lineId, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.IsNullOrWhiteSpace(jobId)
                && fact.Links.TryGetValue("direct_job_id", out var factJob)
                && !string.IsNullOrWhiteSpace(factJob)
                && !string.Equals(factJob, jobId, StringComparison.OrdinalIgnoreCase))
                continue;
            matches.Add(fact);
        }

        return matches;
    }

    private static SourceFact? FindUniqueCurrentRoleMatch(
        List<SourceFact> currentFacts,
        string role,
        string? route,
        string? lineId,
        string? jobId)
    {
        var matches = CollectCurrentRoleMatches(currentFacts, role, route, lineId, jobId);
        return matches.Count == 1 ? matches[0] : null;
    }

    private static void MergeComplementaryCurrentWork(SourceFact target, JsonObject row, RawDocument doc)
    {
        var handling = target.Values["lead_handling_state"]?.GetValue<string>();
        var execution = target.Values["execution_state"]?.GetValue<string>();
        if (IsMissingIdentity(handling) && IsMissingIdentity(execution))
            ApplyLaneState(target.Values, JsonField.Str(row, "state", "status"));
        StampExecutorIdentity(target.Values, row);
        if (target.Links is Dictionary<string, string> links)
        {
            PutLinkIfMissing(links, "line_job_id", JsonField.Str(row, "line_job_id"));
            PutLinkIfMissing(links, "direct_job_id", JsonField.Str(row, "job_id", "direct_job_id"));
            PutLinkIfMissing(links, "native_session_id", JsonField.Str(row, "native_session_id", "executor_native_session_id"));
        }
        _ = doc;
    }

    private static void PutLinkIfMissing(Dictionary<string, string> links, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (links.TryGetValue(key, out var existing) && !string.IsNullOrWhiteSpace(existing))
        {
            if (!string.Equals(existing, value, StringComparison.OrdinalIgnoreCase))
                return;
            return;
        }

        NormUtil.PutLink(links, key, value);
    }

    private static bool IsMissingIdentity(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Equals("unknown", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Lane state tokens are Lead declarations, not proof that a model is running.
    /// Prepared is not dispatched; dispatched-pending is not native execution.
    /// </summary>
    private static void ApplyLaneState(JsonObject values, string? state)
    {
        var s = state ?? string.Empty;
        if (s.Contains("PREPARED", StringComparison.OrdinalIgnoreCase)
            && !s.Contains("DISPATCH", StringComparison.OrdinalIgnoreCase))
        {
            values["lead_handling_state"] = "repair_prepared";
            values["acceptance_state"] = "handled_fail_repair";
            values["execution_state"] = "returned";
            return;
        }

        if (s.Contains("not_dispatched", StringComparison.OrdinalIgnoreCase))
        {
            values["execution_state"] = "unknown";
            return;
        }

        if (s.Equals("callback_wait", StringComparison.OrdinalIgnoreCase)
            || s.Contains("callback_wait", StringComparison.OrdinalIgnoreCase))
        {
            values["lead_handling_state"] = "callback_wait";
            values["execution_state"] = "unknown";
            return;
        }

        var repair = s.Contains("REPAIR", StringComparison.OrdinalIgnoreCase)
                     || s.Contains("fail_repair", StringComparison.OrdinalIgnoreCase);
        if (repair
            && s.Contains("DISPATCH", StringComparison.OrdinalIgnoreCase)
            && (s.Contains("PENDING", StringComparison.OrdinalIgnoreCase)
                || s.Contains("NATIVE_RESULT", StringComparison.OrdinalIgnoreCase)
                || s.Contains("WAITING", StringComparison.OrdinalIgnoreCase)))
        {
            values["lead_handling_state"] = "repair_dispatched";
            values["acceptance_state"] = "handled_fail_repair";
            values["execution_state"] = "unknown";
            return;
        }

        if (s.Contains("dispatched_native_evidence_pending", StringComparison.OrdinalIgnoreCase)
            || (!repair
                && s.Contains("DISPATCH", StringComparison.OrdinalIgnoreCase)
                && (s.Contains("PENDING", StringComparison.OrdinalIgnoreCase)
                    || s.Contains("NATIVE_EVIDENCE", StringComparison.OrdinalIgnoreCase)
                    || s.Contains("WAITING", StringComparison.OrdinalIgnoreCase))
                && !s.Contains("ACCEPTANCE", StringComparison.OrdinalIgnoreCase)))
        {
            values["lead_handling_state"] = "dispatch_pending_native";
            values["execution_state"] = "unknown";
            return;
        }

        if (s.Contains("ACCEPTANCE_PASS", StringComparison.OrdinalIgnoreCase)
            || s.Contains("BOUNDED_WINDOWS_ACCEPTANCE_PASS", StringComparison.OrdinalIgnoreCase)
            || (s.Contains("ACCEPTED", StringComparison.OrdinalIgnoreCase)
                && s.Contains("PASS", StringComparison.OrdinalIgnoreCase)))
        {
            values["acceptance_state"] = "handled_accepted";
            values["lead_handling_state"] = "lead_handled";
            values["execution_state"] = "succeeded";
            return;
        }

        values["execution_state"] = NormUtil.InferExecutionFromActive(state);
    }

    /// <summary>
    /// Nested package objects on ACTIVE_WORK carry verdict/integrated only.
    /// Job/package tokens are recorded so later history stamping can associate
    /// the then-repair conclusion with the matching work, not every stale job.
    /// </summary>
    private static void StampNestedRoundVerdicts(JsonObject values, JsonObject data)
    {
        var failJobs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var consumedJobs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var consumedPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in data)
        {
            if (kv.Value is not JsonObject nested) continue;
            var verdict = JsonField.Str(nested, "verdict", "result");
            var integrated = JsonField.Bool(nested, "integrated");
            if (string.IsNullOrWhiteSpace(verdict)) continue;
            var jobs = NestedJobIds(nested);
            var packages = NestedPackageTokens(kv.Key, nested);
            if (verdict.Contains("FAIL", StringComparison.OrdinalIgnoreCase) && integrated != true)
            {
                foreach (var id in jobs) failJobs.Add(id);
                foreach (var p in packages) failPackages.Add(p);
            }

            if ((verdict.Contains("PASS", StringComparison.OrdinalIgnoreCase)
                 || verdict.Equals("accepted", StringComparison.OrdinalIgnoreCase))
                && integrated == true)
            {
                foreach (var id in jobs) consumedJobs.Add(id);
                foreach (var p in packages) consumedPackages.Add(p);
            }
        }

        if (failJobs.Count > 0) values["prior_fail_repair_jobs"] = string.Join(",", failJobs);
        if (failPackages.Count > 0) values["prior_fail_repair_packages"] = string.Join(",", failPackages);
        if (failJobs.Count > 0 && consumedJobs.Count > 0)
            values["prior_fail_consumed_jobs"] = string.Join(",", failJobs.Intersect(consumedJobs, StringComparer.OrdinalIgnoreCase));
        if (failPackages.Count > 0 && consumedPackages.Count > 0)
            values["prior_fail_consumed_packages"] = string.Join(",", failPackages);
    }

    private static List<string> NestedJobIds(JsonObject nested)
    {
        var ids = new List<string>();
        void Add(string? raw)
        {
            var id = NormUtil.JobIdFromPath(raw) ?? raw;
            if (!string.IsNullOrWhiteSpace(id) && Guid.TryParse(id, out _))
                ids.Add(id!);
        }

        Add(JsonField.Str(nested, "line_job_id", "direct_job_id", "job_id"));
        foreach (var key in new[] { "line_receipt", "route_receipt", "direct_receipt", "receipt", "acceptance" })
        {
            var obj = JsonField.Obj(nested, key);
            Add(JsonField.Str(obj, "path"));
            Add(JsonField.Str(nested, key));
        }

        return ids;
    }

    private static List<string> NestedPackageTokens(string key, JsonObject nested)
    {
        var tokens = new List<string>();
        void Add(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            var t = PackageStem(raw);
            if (t.Length >= 8) tokens.Add(t);
        }

        Add(key);
        Add(JsonField.Str(nested, "package_id", "stage", "package"));
        var acceptance = JsonField.Str(nested, "acceptance");
        Add(acceptance);
        if (!string.IsNullOrWhiteSpace(acceptance))
        {
            var n = acceptance.Replace('\\', '/');
            foreach (var part in n.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.Contains('.', StringComparison.Ordinal)) continue;
                Add(part);
            }
        }

        return tokens;
    }

    internal static string PackageStem(string raw)
    {
        var t = raw.Trim().ToLowerInvariant().Replace('-', '_');
        foreach (var suffix in new[] { "_attempt", "_final", "_acceptance", "_result" })
        {
            if (t.EndsWith(suffix, StringComparison.Ordinal))
                t = t[..^suffix.Length];
        }

        var slash = t.LastIndexOf('/');
        if (slash >= 0 && slash < t.Length - 1) t = t[(slash + 1)..];
        return t;
    }

    private static void ApplyCurrentHandler(JsonObject values, JsonObject data, string? state, string? humanNext)
    {
        var handler = JsonField.Str(data, "current_handler");
        if (!string.IsNullOrWhiteSpace(handler))
            values["current_handler"] = handler;
        var userReq = JsonField.Bool(data, "user_decision_required");
        if (userReq is not null)
            values["user_decision_required"] = userReq.Value ? "true" : "false";

        var blocker = JsonField.Obj(data, "execution_blocker");
        if (blocker is not null)
        {
            var code = JsonField.Str(blocker, "code", "classification") ?? "platform_restriction";
            values["execution_blocker"] = code;
            var dispatched = JsonField.Bool(blocker, "remaining_repair_dispatched");
            values["remaining_repair_dispatched"] = dispatched == true ? "true" : "false";
            var blockerNext = JsonField.Str(blocker, "next_action") ?? humanNext;
            if (!string.IsNullOrWhiteSpace(blockerNext))
                values["next_step"] = blockerNext;
            values["execution_state"] = "returned";
            if (JsonField.Bool(blocker, "acceptance_judgment_completed") == true
                || NormUtil.LooksLeadRepair(JsonField.Str(data, "state_before_second_platform_interruption")))
            {
                values["acceptance_state"] = "handled_fail_repair";
            }

            values["lead_handling_state"] = dispatched == true ? "repair_dispatched" : "blocked_after_acceptance";
            return;
        }

        if (NormUtil.LooksLeadAccepting(state) || (handler is not null && handler.Contains("验收", StringComparison.Ordinal)))
        {
            values["lead_handling_state"] = "lead_accepting";
            values["acceptance_state"] = "acceptance_in_progress";
            values["execution_state"] = "returned";
            return;
        }

        if (NormUtil.LooksLeadRepair(state))
        {
            values["lead_handling_state"] = "lead_handled_fail_repair";
            values["acceptance_state"] = "handled_fail_repair";
            values["execution_state"] = "returned";
        }
    }

    public static void NormalizeCurrentResult(
        RawDocument doc,
        List<SourceFact> facts,
        List<SourceIssue> issues,
        HashSet<string> knownProjects)
    {
        var projectId = NormUtil.ResolveProjectId(doc, knownProjects, issues);
        var lineId = JsonField.Str(doc.Data, "line_job_id", "current_line_job_id");
        var directId = JsonField.Str(doc.Data, "direct_job_id");
        var entityId = "work:current_result:" + (lineId ?? doc.Id);
        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        NormUtil.PutLink(links, "line_job_id", lineId);
        NormUtil.PutLink(links, "direct_job_id", directId);
        NormUtil.PutLink(links, "source_object_id", doc.Id);
        var values = NormUtil.BaseAxes("current_result", null, "unknown", "unknown", "unknown", "unknown",
            "本轮结果文件已存在");
        values["diagnostic_only"] = "true";
        values["current_result_present"] = "true";
        var selfAcc = JsonField.Bool(doc.Data, "self_accepted");
        var productPass = JsonField.Bool(doc.Data, "product_pass");
        if (selfAcc is not null) values["executor_self_accepted"] = selfAcc.Value ? "true" : "false";
        if (productPass is not null) values["executor_product_pass"] = productPass.Value ? "true" : "false";
        values["goal_state"] = "unknown";
        values["delivery_state"] = "unknown";
        values["acceptance_state"] = "unknown";
        facts.Add(new SourceFact("work", entityId, projectId, links, values, new[] { NormUtil.Evidence(doc) }));
    }

    private static void StampCollectedScope(RawDocument doc, JsonObject values)
    {
        if (string.Equals(doc.Scope, "historical", StringComparison.OrdinalIgnoreCase))
        {
            values["historical"] = "true";
            values["current_active_fault"] = "false";
        }
    }

    /// <summary>
    /// Lead identity only. Nested launcher Model/ReasoningEffort and resume session
    /// belong here. Executor fields are not copied onto Lead.
    /// </summary>
    private static void StampLeadIdentity(JsonObject values, JsonObject data)
    {
        PutIfMissing(values, "actor_name", JsonField.Str(data, "actor_name", "display_name", "lead_name"));
        PutIfMissing(values, "task_name", JsonField.Str(data, "task_name", "task", "title"));
        PutIfMissing(values, "model",
            JsonField.Str(data, "model", "lead_model", "model_name")
            ?? LauncherFlag(data, "-Model", "--model"));
        PutIfMissing(values, "effort",
            JsonField.Str(data, "reasoning", "reasoning_effort", "effort", "lead_reasoning")
            ?? LauncherFlag(data, "-ReasoningEffort", "--reasoning-effort"));
        var role = JsonField.Str(data, "role");
        if (string.Equals(role, "host", StringComparison.OrdinalIgnoreCase))
            values["lead_source_kind"] = "host_owner";
        else
            values["lead_source_kind"] = "lead_run";
    }

    /// <summary>
    /// Executor identity from this document only. Nested lead.model/effort stays on Lead.
    /// </summary>
    private static void StampExecutorIdentity(JsonObject values, JsonObject data)
    {
        PutIfMissing(values, "actor_name",
            JsonField.Str(data, "actor_name", "display_name", "executor_name", "client", "client_name", "agent_name"));
        PutIfMissing(values, "task_name",
            JsonField.Str(data, "task_name", "task", "package_id", "current_package_id", "title", "stage"));
        PutIfMissing(values, "model",
            JsonField.Str(data, "executor_model", "model", "model_name", "model_id"));
        PutIfMissing(values, "effort",
            JsonField.Str(data, "executor_reasoning", "executor_reasoning_effort", "reasoning_effort", "effort"));
        PutIfMissing(values, "blocker",
            JsonField.Str(data, "blocker", "execution_blocker")
            ?? JsonField.Str(JsonField.Obj(data, "execution_blocker"), "code", "classification", "detail"));

        var reviewAssigned = JsonField.Bool(data, "review_assigned", "reviewer_assigned");
        if (reviewAssigned is not null)
            values["review_assigned"] = reviewAssigned.Value ? "true" : "false";

        var role = values["role"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(role) || role.Equals("unknown", StringComparison.OrdinalIgnoreCase))
        {
            var rawRole = JsonField.Str(data, "role");
            if (!string.IsNullOrWhiteSpace(rawRole))
                values["role"] = rawRole;
        }
    }

    private static string? LauncherFlag(JsonObject data, params string[] flags)
    {
        var launcher = JsonField.Obj(data, "launcher") ?? JsonField.Obj(JsonField.Obj(data, "lead", "lead_binding", "binding"), "launcher");
        if (launcher is null) return null;
        if (launcher["arguments"] is not JsonArray args) return null;
        for (var i = 0; i < args.Count - 1; i++)
        {
            var token = args[i]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(token)) continue;
            foreach (var flag in flags)
            {
                if (token.Equals(flag, StringComparison.OrdinalIgnoreCase))
                {
                    var next = args[i + 1]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(next) && next[0] != '-')
                        return next.Trim();
                }
            }
        }

        return null;
    }

    private static void PutIfMissing(JsonObject values, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var existing = values[key]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(existing) || existing.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            values[key] = value;
    }

    public static void NormalizeLead(RawDocument doc, List<SourceFact> facts, List<SourceIssue> issues, HashSet<string> knownProjects)
    {
        var projectId = NormUtil.ResolveProjectId(doc, knownProjects, issues);
        var session = JsonField.Str(doc.Data, "resume_session_id", "session_id", "lead_session_id", "thread_id");
        var runId = JsonField.Str(doc.Data, "run_id", "lead_run_id", "requested_run_id");
        var entityId = "lead:" + (session ?? runId ?? doc.Id);
        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        NormUtil.PutLink(links, "lead_session_id", session);
        NormUtil.PutLink(links, "source_object_id", doc.Id);
        NormUtil.PutLink(links, "lead_run_id", runId);
        NormUtil.PutLink(links, "line_job_id", JsonField.Str(doc.Data, "line_job_id", "current_line_job_id"));

        var turn = JsonField.Str(doc.Data, "turn_state", "turn_status");
        var lifecycle = JsonField.Str(doc.Data, "lifecycle_state", "state", "status");
        var values = new JsonObject
        {
            ["role"] = JsonField.AxisOrUnknown(JsonField.Str(doc.Data, "role") ?? "lead"),
            ["process_state"] = "unknown",
            ["turn_state"] = JsonField.AxisOrUnknown(turn ?? (lifecycle is not null && NormUtil.LooksCompleted(lifecycle) ? "completed" : null)),
            ["execution_state"] = "unknown",
            ["transport_state"] = "unknown",
            ["delivery_state"] = "unknown",
            ["callback_state"] = "unknown",
            ["lead_handling_state"] = JsonField.AxisOrUnknown(JsonField.Str(doc.Data, "handling_state", "lead_handling_state")),
            ["acceptance_state"] = "unknown",
            ["adoption_state"] = "unknown",
            ["goal_state"] = "unknown",
            ["summary"] = JsonField.AxisOrUnknown(lifecycle ?? turn)
        };
        var pid = JsonField.Int(doc.Data, "pid");
        var ticks = JsonField.Long(doc.Data, "start_time_utc_ticks", "start_ticks");
        if (pid is not null) values["pid"] = pid.Value.ToString(CultureInfo.InvariantCulture);
        if (ticks is not null) values["start_time_utc_ticks"] = ticks.Value.ToString(CultureInfo.InvariantCulture);
        StampLeadIdentity(values, doc.Data);
        facts.Add(new SourceFact("lead", entityId, projectId, links, values, new[] { NormUtil.Evidence(doc) }));
    }

    public static void NormalizeCli(RawDocument doc, List<SourceFact> facts, List<SourceIssue> issues, HashSet<string> knownProjects)
    {
        var projectId = NormUtil.ResolveProjectId(doc, knownProjects, issues);
        var entityId = "work:cli:" + doc.Id;
        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        NormUtil.PutLink(links, "lead_session_id", JsonField.Str(doc.Data, "session_id", "lead_session_id"));
        NormUtil.PutLink(links, "parent_work_id", JsonField.Str(doc.Data, "parent_run_id", "parent_work_id"));
        NormUtil.PutLink(links, "source_object_id", doc.Id);
        var turnCompleted = JsonField.Bool(doc.Data, "turn_completed")
            ?? (NormUtil.LooksCompleted(JsonField.Str(doc.Data, "turn_state", "state")) ? true : null);
        var nativeTurnComplete = JsonField.Bool(doc.Data, "native_turn_complete");
        var exitCode = JsonField.Str(doc.Data, "exit_code");
        var values = NormUtil.BaseAxes("cli", null, "unknown", "unknown", "unknown", "unknown",
            JsonField.Str(doc.Data, "summary", "state"));
        values["turn_state"] = JsonField.BoolAxis(turnCompleted, "completed", "open");
        values["process_state"] = JsonField.AxisOrUnknown(JsonField.Str(doc.Data, "process_state"));
        if (nativeTurnComplete == false
            || (exitCode is "1" && nativeTurnComplete != true))
        {
            values["turn_state"] = "incomplete";
            values["execution_state"] = "unknown";
            values["cli_exit_not_executor_failure"] = "true";
            values["summary"] = "负责人验收回合未完成；子进程退出不能当成原执行失败";
        }
        StampCollectedScope(doc, values);
        facts.Add(new SourceFact("work", entityId, projectId, links, values, new[] { NormUtil.Evidence(doc) }));
    }

    public static void NormalizeProcess(
        RawDocument doc,
        List<SourceFact> facts,
        List<SourceIssue> issues,
        HashSet<string> knownProjects,
        List<(RawDocument Doc, int Pid, long Ticks, string State)> processObs)
    {
        var projectId = NormUtil.ResolveProjectId(doc, knownProjects, issues);
        var pid = JsonField.Int(doc.Data, "pid") ?? 0;
        var ticks = JsonField.Long(doc.Data, "expected_start_ticks", "start_time_utc_ticks", "start_ticks") ?? 0;
        var state = JsonField.Str(doc.Data, "state") ?? "unknown";
        processObs.Add((doc, pid, ticks, state));
        var entityId = "work:process:" + pid + ":" + ticks;
        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        NormUtil.PutLink(links, "source_object_id", doc.Id);
        NormUtil.PutLink(links, "workspace", JsonField.Str(doc.Data, "workspace"));
        var values = NormUtil.BaseAxes("process", null, "unknown", "unknown", "unknown", "unknown", "process");
        values["process_state"] = JsonField.AxisOrUnknown(state);
        values["pid"] = pid.ToString(CultureInfo.InvariantCulture);
        values["start_time_utc_ticks"] = ticks.ToString(CultureInfo.InvariantCulture);
        values["diagnostic_only"] = "true";
        values["current_active_fault"] = "false";
        StampCollectedScope(doc, values);
        facts.Add(new SourceFact("work", entityId, projectId, links, values, new[] { NormUtil.Evidence(doc) }));
    }

    public static void NormalizeGenerationStatus(
        RawDocument doc,
        List<SourceFact> facts,
        List<SourceIssue> issues,
        HashSet<string> knownProjects)
    {
        var projectId = NormUtil.ResolveProjectId(doc, knownProjects, issues);
        var worldDay = JsonField.Str(doc.Data, "worldDay", "world_day", "day");
        var target = JsonField.Str(doc.Data, "target", "targetDay", "days", "total");
        var at = JsonField.Str(doc.Data, "at", "recorded_at_utc", "updated_at");
        var pid = JsonField.Int(doc.Data, "pid");
        var startedAt = JsonField.Str(doc.Data, "startedAt", "started_at");
        var publication = JsonField.Str(doc.Data, "publication");
        var packageId = JsonField.Str(doc.Data, "package_id");
        var entityId = "work:generation:" + (packageId ?? doc.Id);
        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        NormUtil.PutLink(links, "line_job_id", JsonField.Str(doc.Data, "line_job_id"));
        NormUtil.PutLink(links, "direct_job_id", JsonField.Str(doc.Data, "direct_job_id"));
        NormUtil.PutLink(links, "source_object_id", doc.Id);
        NormUtil.PutLink(links, "workspace", JsonField.Str(doc.Data, "workspace"));

        var values = NormUtil.BaseAxes("generation", JsonField.Str(doc.Data, "route"), "unknown", "unknown", "active", "unknown",
            "原执行者继续生成");
        values["execution_state"] = "active";
        values["diagnostic_only"] = "true";
        values["delivery_state"] = "unknown";
        values["acceptance_state"] = "unknown";
        values["current_round_receipt_missing"] = "true";
        if (!string.IsNullOrWhiteSpace(worldDay))
        {
            values["completed"] = worldDay;
            values["progress_basis"] = "世界已生成天数";
            values["progress_at"] = JsonField.AxisOrUnknown(at);
        }
        if (!string.IsNullOrWhiteSpace(target))
            values["total"] = target;
        if (pid is not null)
        {
            values["generation_pid"] = pid.Value.ToString(CultureInfo.InvariantCulture);
            values["generation_started_at"] = JsonField.AxisOrUnknown(startedAt);
            values["generation_pid_tick_match"] = "not_claimed";
        }
        if (!string.IsNullOrWhiteSpace(publication))
            values["publication"] = publication;
        StampCollectedScope(doc, values);
        facts.Add(new SourceFact("work", entityId, projectId, links, values, new[] { NormUtil.Evidence(doc) }));

        if (!string.IsNullOrWhiteSpace(worldDay))
        {
            AttachGenerationProgressToCurrentWork(facts, projectId, worldDay, target, at, pid, startedAt);
        }
    }

    private static void AttachGenerationProgressToCurrentWork(
        List<SourceFact> facts,
        string? projectId,
        string worldDay,
        string? target,
        string? at,
        int? pid,
        string? startedAt)
    {
        for (var i = 0; i < facts.Count; i++)
        {
            var f = facts[i];
            if (f.Kind != "work") continue;
            if (!string.IsNullOrWhiteSpace(projectId) && !string.Equals(f.ProjectId, projectId, StringComparison.Ordinal))
                continue;
            if (string.Equals(f.Values["historical"]?.GetValue<string>(), "true", StringComparison.OrdinalIgnoreCase))
                continue;
            var role = f.Values["role"]?.GetValue<string>() ?? string.Empty;
            if (role.Contains("contrib", StringComparison.OrdinalIgnoreCase)
                || role.Contains("process", StringComparison.OrdinalIgnoreCase)
                || role.Contains("cli", StringComparison.OrdinalIgnoreCase)
                || role.Contains("acceptance", StringComparison.OrdinalIgnoreCase)
                || role.Contains("review", StringComparison.OrdinalIgnoreCase)
                || role.Contains("审核", StringComparison.Ordinal))
            {
                continue;
            }

            var values = NormUtil.CloneValues(f.Values);
            values["completed"] = worldDay;
            values["progress_basis"] = "世界已生成天数";
            if (!string.IsNullOrWhiteSpace(target)) values["total"] = target;
            if (!string.IsNullOrWhiteSpace(at)) values["progress_at"] = at;
            if (pid is not null) values["generation_pid"] = pid.Value.ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(startedAt)) values["generation_started_at"] = startedAt;
            if (values["execution_state"]?.GetValue<string>() is null or "unknown")
                values["execution_state"] = "active";
            if (values["delivery_state"]?.GetValue<string>() is "present" or "complete")
            {
                if (string.Equals(values["current_round_receipt_missing"]?.GetValue<string>(), "true", StringComparison.OrdinalIgnoreCase)
                    || values["prior_package_accepted"]?.GetValue<string>() == "true")
                {
                    values["delivery_state"] = "unknown";
                }
            }
            facts[i] = new SourceFact(f.Kind, f.EntityId, f.ProjectId, f.Links, values, f.Evidence);
        }
    }

    public static void NormalizeLine(
        RawDocument doc,
        List<SourceFact> facts,
        List<SourceIssue> issues,
        HashSet<string> knownProjects,
        HashSet<string> routesSeenLive,
        HashSet<string> routesSeenProtocol)
    {
        var projectId = NormUtil.ResolveProjectId(doc, knownProjects, issues);
        var claimed = JsonField.Str(doc.Data, "project", "project_id");
        if (!string.IsNullOrWhiteSpace(claimed) && !string.IsNullOrWhiteSpace(doc.ProjectHint)
            && !string.Equals(claimed, doc.ProjectHint, StringComparison.Ordinal)
            && knownProjects.Contains(claimed) && knownProjects.Contains(doc.ProjectHint))
        {
            issues.Add(new SourceIssue(doc.Id, "cross_project_reject",
                $"Document project '{claimed}' conflicts with ProjectHint '{doc.ProjectHint}'.", doc.ObservedAt));
            projectId = null;
        }
        else if (!string.IsNullOrWhiteSpace(claimed) && (knownProjects.Count == 0 || knownProjects.Contains(claimed)))
        {
            projectId = claimed;
        }

        var lineId = JsonField.Str(doc.Data, "line_job_id")
                     ?? LineIdFromReceiptPath(doc.Location)
                     ?? NormUtil.JobIdFromPath(JsonField.Str(doc.Data, "job_root"));
        if (string.IsNullOrWhiteSpace(lineId))
            lineId = JsonField.Str(doc.Data, "job_id");
        var route = JsonField.Str(doc.Data, "route");
        NormUtil.NoteRoute(route, routesSeenLive, routesSeenProtocol);
        var entityId = "work:line:" + (lineId ?? doc.Id);
        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        var directId = JsonField.Str(doc.Data, "direct_job_id")
                       ?? ExtractDirectFromCommand(doc.Data)
                       ?? ExtractRouteShellJobId(doc.Data, lineId);
        NormUtil.PutLink(links, "line_job_id", lineId);
        NormUtil.PutLink(links, "direct_job_id", directId);
        NormUtil.PutLink(links, "lead_session_id", JsonField.Str(doc.Data, "lead_session_id")
                                                  ?? JsonField.Str(JsonField.Obj(doc.Data, "lead"), "session_id", "lead_session_id"));
        NormUtil.PutLink(links, "native_session_id", JsonField.Str(doc.Data, "native_session_id", "executor_native_session_id"));
        NormUtil.PutLink(links, "workspace", JsonField.Str(doc.Data, "workspace"));
        NormUtil.PutLink(links, "source_object_id", doc.Id);

        var transport = JsonField.Bool(doc.Data, "transport_complete");
        var phase = JsonField.Str(doc.Data, "phase", "lifecycle_phase");
        if (transport is null && phase is not null
            && (phase.Equals("delivered", StringComparison.OrdinalIgnoreCase)
                || phase.Equals("complete", StringComparison.OrdinalIgnoreCase)
                || phase.Equals("completed", StringComparison.OrdinalIgnoreCase)
                || phase.Equals("terminal", StringComparison.OrdinalIgnoreCase)))
        {
            transport = true;
        }

        string execution = "unknown";
        var success = JsonField.Bool(doc.Data, "success", "grok_success", "cursor_success");
        if (success == true) execution = "succeeded";
        else if (success == false) execution = "failed";
        else if (transport == true) execution = "returned";
        else if (phase is not null
                 && (phase.Equals("running", StringComparison.OrdinalIgnoreCase)
                     || phase.Equals("active", StringComparison.OrdinalIgnoreCase)
                     || phase.Equals("starting", StringComparison.OrdinalIgnoreCase)))
            execution = "active";

        var values = NormUtil.BaseAxes(
            JsonField.Str(doc.Data, "role") ?? "execution",
            route,
            JsonField.BoolAxis(transport, "complete", "incomplete"),
            doc.Kind == RawDocumentKinds.LineDelivery
                ? JsonField.AxisOrUnknown(JsonField.Str(doc.Data, "delivery_state", "state") ?? "present")
                : "unknown",
            execution,
            "unknown",
            JsonField.Str(doc.Data, "stage", "state", "status"));
        var stage = JsonField.Str(doc.Data, "stage", "phase", "current_stage");
        if (!string.IsNullOrWhiteSpace(stage))
            values["stage"] = stage;

        var waiting = JsonField.Bool(doc.Data, "waiting");
        if (waiting == true)
        {
            values["callback_state"] = "waiting_mailbox";
            values["mailbox_waiting"] = "true";
            values["mailbox_implicit"] = JsonField.BoolAxis(JsonField.Bool(doc.Data, "implicit"), "true", "false");
        }

        if (NormUtil.LooksCancelledArchived(doc.Data))
            NormUtil.MarkCancelledArchived(values);

        StampExecutorIdentity(values, doc.Data);
        StampCollectedScope(doc, values);
        var loc = doc.Location ?? string.Empty;
        if (loc.Contains("DISPATCH_RECEIPT", StringComparison.OrdinalIgnoreCase)
            || (JsonField.Bool(doc.Data, "dispatched") == true
                && JsonField.Bool(doc.Data, "transport_complete") is null
                && JsonField.Str(doc.Data, "job_root") is not null))
        {
            values["starter_wrapper"] = "true";
            if (string.Equals(doc.Scope, "historical", StringComparison.OrdinalIgnoreCase))
            {
                values["historical"] = "true";
                values["current_active_fault"] = "false";
            }
        }

        facts.Add(new SourceFact("work", entityId, projectId, links, values, new[] { NormUtil.Evidence(doc) }));
    }

    public static void NormalizeWake(
        RawDocument doc,
        List<SourceFact> facts,
        List<SourceIssue> issues,
        HashSet<string> knownProjects,
        List<HandlingRecord> handlingRecords)
    {
        var projectId = NormUtil.ResolveProjectId(doc, knownProjects, issues);
        var entityId = "work:wake:" + doc.Id;
        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        NormUtil.PutLink(links, "line_job_id", JsonField.Str(doc.Data, "line_job_id"));
        NormUtil.PutLink(links, "direct_job_id", JsonField.Str(doc.Data, "direct_job_id"));
        NormUtil.PutLink(links, "lead_session_id", JsonField.Str(doc.Data, "lead_session_id", "callback_lead", "session_id"));
        NormUtil.PutLink(links, "source_object_id", doc.Id);
        var values = NormUtil.BaseAxes("wake", JsonField.Str(doc.Data, "route"), "unknown", "unknown", "unknown",
            doc.Kind == RawDocumentKinds.WakeAttempt
                ? JsonField.AxisOrUnknown(JsonField.Str(doc.Data, "result", "state") ?? "attempted")
                : JsonField.AxisOrUnknown(JsonField.Str(doc.Data, "state") ?? "intent"),
            JsonField.Str(doc.Data, "summary", "state"));
        if (NormUtil.HasSwitchFields(doc.Data) || JsonField.Obj(doc.Data, "route_switch") is not null)
        {
            var sw = JsonField.Obj(doc.Data, "route_switch") ?? doc.Data;
            HandlingLogic.RecordSwitch(sw, doc, projectId, handlingRecords);
            HandlingLogic.ApplySwitchToValues(values, links, sw);
        }
        facts.Add(new SourceFact("work", entityId, projectId, links, values, new[] { NormUtil.Evidence(doc) }));
    }

    public static void NormalizeDirect(
        RawDocument doc,
        List<SourceFact> facts,
        List<SourceIssue> issues,
        HashSet<string> knownProjects,
        HashSet<string> routesSeenLive,
        HashSet<string> routesSeenProtocol)
    {
        var projectId = NormUtil.ResolveProjectId(doc, knownProjects, issues);
        var jobId = JsonField.Str(doc.Data, "job_id", "direct_job_id");
        var route = NormUtil.InferDirectRoute(doc.Data) ?? JsonField.Str(doc.Data, "route");
        NormUtil.NoteRoute(route, routesSeenLive, routesSeenProtocol);
        var entityId = "work:direct:" + (jobId ?? doc.Id);
        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        NormUtil.PutLink(links, "direct_job_id", jobId);
        NormUtil.PutLink(links, "line_job_id", JsonField.Str(doc.Data, "line_job_id") ?? ExtractRouteShellLineId(doc.Data, jobId));
        NormUtil.PutLink(links, "lead_session_id", JsonField.Str(doc.Data, "lead_session_id"));
        NormUtil.PutLink(links, "native_session_id", JsonField.Str(doc.Data, "native_session_id", "executor_native_session_id", "session_id"));
        NormUtil.PutLink(links, "workspace", JsonField.Str(doc.Data, "workspace"));
        NormUtil.PutLink(links, "source_object_id", doc.Id);

        var transport = JsonField.Bool(doc.Data, "transport_complete");
        var success = JsonField.Bool(doc.Data, "grok_success", "cursor_success", "success");
        var errorCode = JsonField.Str(doc.Data, "error_code", "failure_code");
        var exitCode = JsonField.Int(doc.Data, "command_exit_code", "exit_code", "pi_exit_code");
        string execution;
        if (!string.IsNullOrWhiteSpace(errorCode) && !errorCode.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            execution = "failed";
        else if (exitCode is > 0)
            execution = "failed";
        else if (success == true) execution = "succeeded";
        else if (success == false) execution = "failed";
        else if (transport == false && !string.IsNullOrWhiteSpace(errorCode)) execution = "failed";
        else execution = "unknown";

        var values = NormUtil.BaseAxes("execution", route,
            JsonField.BoolAxis(transport, "complete", "incomplete"),
            "unknown",
            execution,
            "unknown",
            JsonField.Str(doc.Data, "failure_code", "error_code", "state", "status"));
        var stage = JsonField.Str(doc.Data, "stage", "phase", "current_stage");
        if (!string.IsNullOrWhiteSpace(stage))
            values["stage"] = stage;

        if (!string.IsNullOrWhiteSpace(errorCode) && !errorCode.Equals("unknown", StringComparison.OrdinalIgnoreCase))
        {
            values["failure_code"] = errorCode;
            values["execution_state"] = "failed";
        }

        var owner = JsonField.Obj(doc.Data, "owner");
        if (owner is not null)
        {
            var pid = JsonField.Int(owner, "pid");
            var ticks = JsonField.Long(owner, "start_time_utc_ticks", "start_ticks");
            if (pid is not null) values["pid"] = pid.Value.ToString(CultureInfo.InvariantCulture);
            if (ticks is not null) values["start_time_utc_ticks"] = ticks.Value.ToString(CultureInfo.InvariantCulture);
        }

        var failure = JsonField.Obj(doc.Data, "failure", "old_cursor_failure");
        if (failure is not null)
        {
            values["execution_state"] = "failed";
            values["failure_code"] = JsonField.AxisOrUnknown(JsonField.Str(failure, "failure_code"));
            var ws = JsonField.Str(failure, "workspace");
            if (!string.IsNullOrWhiteSpace(ws)) links["workspace"] = ws!;
        }

        if (NormUtil.LooksCancelledArchived(doc.Data))
            NormUtil.MarkCancelledArchived(values);

        StampExecutorIdentity(values, doc.Data);
        StampCollectedScope(doc, values);
        facts.Add(new SourceFact("work", entityId, projectId, links, values, new[] { NormUtil.Evidence(doc) }));
    }

    public static void NormalizeRouteStatus(
        RawDocument doc,
        List<SourceFact> facts,
        List<SourceIssue> issues,
        HashSet<string> routesSeenProtocol,
        HashSet<string> routesSeenLive)
    {
        var routeId = JsonField.Str(doc.Data, "route_id", "route");
        if (string.IsNullOrWhiteSpace(routeId))
        {
            issues.Add(new SourceIssue(doc.Id, "route_id_missing", "route_status missing route_id.", doc.ObservedAt));
            return;
        }

        var level = JsonField.Str(doc.Data, "coverage_level", "level", "verification_level");
        if (string.Equals(level, "live", StringComparison.OrdinalIgnoreCase)
            || string.Equals(level, "live_verified", StringComparison.OrdinalIgnoreCase))
        {
            routesSeenLive.Add(routeId);
            routesSeenProtocol.Add(routeId);
        }
        else if (string.Equals(level, "catalog", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(level, "declared", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(level, "catalog_declared_only", StringComparison.OrdinalIgnoreCase))
        {
            // Catalog claim only — do NOT promote to live verification.
            routesSeenProtocol.Add(routeId);
        }
        else
        {
            routesSeenProtocol.Add(routeId);
        }
        var values = new JsonObject
        {
            ["route"] = routeId,
            ["coverage_level"] = JsonField.AxisOrUnknown(level),
            ["coverage_gap"] = JsonField.AxisOrUnknown(JsonField.Str(doc.Data, "coverage_gap", "gap")),
            ["summary"] = JsonField.AxisOrUnknown(JsonField.Str(doc.Data, "summary", "status"))
        };
        facts.Add(new SourceFact("route_coverage", "route:" + routeId, null,
            new Dictionary<string, string> { ["source_object_id"] = doc.Id },
            values, new[] { NormUtil.Evidence(doc) }));
    }

    public static void NormalizeBotRegistry(RawDocument doc, List<SourceFact> facts, List<SourceIssue> issues, HashSet<string> knownProjects)
    {
        var projectId = NormUtil.ResolveProjectId(doc, knownProjects, issues);
        var links = new Dictionary<string, string>(StringComparer.Ordinal) { ["source_object_id"] = doc.Id };
        NormUtil.PutLink(links, "lead_session_id", JsonField.Str(doc.Data, "original_lead", "lead_session_id"));
        var values = new JsonObject
        {
            ["role"] = "army_registry",
            ["acceptance_state"] = "unknown",
            ["adoption_state"] = "unknown",
            ["goal_state"] = "unknown",
            ["summary"] = JsonField.AxisOrUnknown(JsonField.Str(doc.Data, "state", "status"))
        };
        facts.Add(new SourceFact("contribution", "contribution:registry:" + doc.Id, projectId, links, values, new[] { NormUtil.Evidence(doc) }));
    }

    public static void NormalizeBotContribution(RawDocument doc, List<SourceFact> facts, List<SourceIssue> issues, HashSet<string> knownProjects)
    {
        var projectId = NormUtil.ResolveProjectId(doc, knownProjects, issues);
        var packages = JsonField.Arr(doc.Data, "packages");
        if (packages is null || packages.Count == 0)
        {
            var links = new Dictionary<string, string>(StringComparer.Ordinal);
            NormUtil.PutLink(links, "lead_session_id", JsonField.Str(doc.Data, "original_lead", "lead_session_id"));
            NormUtil.PutLink(links, "source_object_id", doc.Id);
            var formal = JsonField.Bool(doc.Data, "formal_product_acceptance", "product_pass");
            var values = new JsonObject
            {
                ["role"] = "army_contribution",
                ["acceptance_state"] = "pending",
                ["adoption_state"] = "delivered_unaccepted",
                ["goal_state"] = formal == true ? "complete" : "not_complete",
                ["summary"] = JsonField.AxisOrUnknown(JsonField.Str(doc.Data, "state", "status")),
                ["contribution_item"] = "true",
                ["counts_as_adopted_outcome"] = "false",
                ["contribution_id"] = JsonField.Str(doc.Data, "dispatch_id") ?? doc.Id
            };
            facts.Add(new SourceFact("contribution", "contribution:" + (JsonField.Str(doc.Data, "dispatch_id") ?? doc.Id),
                projectId, links, values, new[] { NormUtil.Evidence(doc) }));
            return;
        }

        foreach (var node in packages)
        {
            if (node is not JsonObject pkg) continue;
            var name = JsonField.Str(pkg, "name", "package") ?? "package";
            var links = new Dictionary<string, string>(StringComparer.Ordinal);
            NormUtil.PutLink(links, "lead_session_id", JsonField.Str(doc.Data, "original_lead"));
            NormUtil.PutLink(links, "source_object_id", doc.Id + "#" + name);
            var status = JsonField.Str(pkg, "status", "result");
            var values = new JsonObject
            {
                ["role"] = "army_contribution",
                ["name"] = name,
                ["acceptance_state"] = "pending",
                ["adoption_state"] = NormUtil.LooksAdopted(status) ? "adopted" : "delivered_unaccepted",
                ["goal_state"] = "not_complete",
                ["summary"] = JsonField.AxisOrUnknown(status),
                ["contribution_item"] = "true",
                ["counts_as_adopted_outcome"] = NormUtil.LooksAdopted(status) ? "true" : "false",
                ["contribution_id"] = name
            };
            facts.Add(new SourceFact("contribution", "contribution:" + name, projectId, links, values, new[] { NormUtil.Evidence(doc) }));
        }
    }

    public static void NormalizeBotAcceptance(
        RawDocument doc,
        List<SourceFact> facts,
        List<SourceIssue> issues,
        HashSet<string> knownProjects,
        List<HandlingRecord> handlingRecords)
    {
        var projectId = NormUtil.ResolveProjectId(doc, knownProjects, issues);
        if (IsCapacityResume(doc.Data))
        {
            ApplyCapacityResume(facts, doc, projectId);
        }

        var targetLine = JsonField.Str(doc.Data, "line_job_id", "current_line_job_id", "preserved_execution_line")
                         ?? (IsCapacityResume(doc.Data) ? LineIdFromReceiptPath(JsonField.Str(doc.Data, "previous_402_receipt")) : null);
        var targetDirect = JsonField.Str(doc.Data, "direct_job_id", "current_direct_job_id");
        var result = JsonField.Str(doc.Data, "result", "state");
        if (!IsCapacityResume(doc.Data) && (!string.IsNullOrWhiteSpace(targetLine) || !string.IsNullOrWhiteSpace(targetDirect)))
        {
            AttachOrdinaryLeadAcceptance(facts, doc, projectId, targetLine, targetDirect, result);
        }

        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        NormUtil.PutLink(links, "lead_session_id", JsonField.Str(doc.Data, "original_lead", "lead_session_id"));
        NormUtil.PutLink(links, "source_object_id", doc.Id);
        NormUtil.PutLink(links, "line_job_id", targetLine);
        NormUtil.PutLink(links, "direct_job_id", targetDirect);
        var productPass = JsonField.Bool(doc.Data, "product_pass", "formal_product_acceptance");
        string adoption;
        if (NormUtil.LooksAdopted(result)) adoption = "adopted_partial_or_full";
        else if (JsonField.Arr(doc.Data, "original_ar_closed_now") is { Count: > 0 }) adoption = "adopted_partial_or_full";
        else adoption = JsonField.AxisOrUnknown(result);

        var attentionOwner = JsonField.Str(doc.Data, "attention_owner");
        var attentionReason = JsonField.Str(doc.Data, "attention_reason");
        var decisionSummary = JsonField.Str(doc.Data, "summary");
        var pendingUser = IsUserOwner(attentionOwner)
            && productPass != true
            && HasExplicitDecisionSignal(doc.Data, result, attentionReason, decisionSummary);

        var mapped = MapOrdinaryLeadResult(result);
        if (JsonField.Bool(doc.Data, "content_acceptance_performed") == false
            && mapped.Acceptance is "pending" or "unknown")
        {
            mapped = ("content_not_accepted", mapped.Handling.Contains("lead_handled", StringComparison.OrdinalIgnoreCase)
                ? mapped.Handling
                : "lead_handled_quota_blocked", mapped.ResultToken);
        }

        if (IsCapacityResume(doc.Data))
        {
            mapped = ("content_not_accepted", "lead_handled_quota_resumed", result ?? "resume");
        }

        var acceptanceState = pendingUser
            ? "pending_user_decision"
            : (!string.IsNullOrWhiteSpace(targetLine) || !string.IsNullOrWhiteSpace(targetDirect)
                ? mapped.Acceptance
                : (NormUtil.LooksAdopted(result) || JsonField.Arr(doc.Data, "original_ar_closed_now") is { Count: > 0 }
                    ? "accepted_partial_or_full"
                    : JsonField.AxisOrUnknown(result)));

        var values = new JsonObject
        {
            ["role"] = "lead_acceptance",
            ["acceptance_state"] = acceptanceState,
            ["adoption_state"] = pendingUser ? "not_adopted_pending_decision" : adoption,
            ["goal_state"] = productPass == true ? "complete" : "not_complete",
            ["lead_handling_state"] = pendingUser ? "awaiting_user" : "handled",
            ["summary"] = JsonField.AxisOrUnknown(decisionSummary ?? result),
            ["completed"] = JsonField.AxisOrUnknown(JsonField.Str(doc.Data, "original_ar_complete_count")),
            ["progress_basis"] = string.IsNullOrWhiteSpace(JsonField.Str(doc.Data, "original_ar_complete_count"))
                ? "unknown" : "original_lead_ar_count",
            ["contribution_item"] = "false",
            ["counts_as_adopted_outcome"] = "false"
        };
        if (JsonField.Bool(doc.Data, "source_adopted") == true
            || JsonField.Arr(doc.Data, "original_ar_closed_now") is { Count: > 0 })
        {
            values["source_adopted"] = "true";
            values["counts_as_adopted_outcome"] = "true";
        }
        if (!string.IsNullOrWhiteSpace(attentionOwner))
        {
            values["attention_owner"] = attentionOwner;
        }
        if (!string.IsNullOrWhiteSpace(attentionReason))
        {
            values["attention_reason"] = attentionReason;
        }
        if (productPass != true && NormUtil.LooksAdopted(result) && !pendingUser)
        {
            values["goal_state"] = "not_complete";
            values["summary"] = (result ?? "adopted") + ";product_pass=false";
        }

        StampCollectedScope(doc, values);
        var acceptanceEntity = "acceptance:" + (targetLine ?? targetDirect ?? JsonField.Str(doc.Data, "original_lead") ?? doc.Id);
        facts.Add(new SourceFact("acceptance", acceptanceEntity,
            projectId, links, values, new[] { NormUtil.Evidence(doc) }));

        if (JsonField.Arr(doc.Data, "original_ar_closed_now") is { Count: > 0 } closedNow && !pendingUser)
        {
            foreach (var node in closedNow)
            {
                string? arName = null;
                if (node is JsonValue jv)
                {
                    if (!jv.TryGetValue<string>(out arName))
                        arName = jv.ToJsonString().Trim('"');
                }
                else
                {
                    arName = node?.ToJsonString().Trim('"');
                }
                if (string.IsNullOrWhiteSpace(arName)) continue;
                var arLinks = new Dictionary<string, string>(links, StringComparer.Ordinal);
                arLinks["source_object_id"] = doc.Id + "#" + arName;
                var arValues = new JsonObject
                {
                    ["role"] = "army_contribution",
                    ["name"] = arName,
                    ["acceptance_state"] = "accepted_partial_or_full",
                    ["adoption_state"] = "adopted",
                    ["goal_state"] = "not_complete",
                    ["summary"] = arName + " adopted;product_pass=false",
                    ["contribution_item"] = "true",
                    ["counts_as_adopted_outcome"] = "true",
                    ["contribution_id"] = arName
                };
                facts.Add(new SourceFact("contribution", "contribution:" + arName, projectId, arLinks, arValues,
                    new[] { NormUtil.Evidence(doc) }));
            }
        }

        // Contract: true User decision must be a User-owned attention fact (distinct from Lead/Secretary).
        // Driven by protocol fields only — never UUID hardcodes.
        if (pendingUser)
        {
            var attLinks = new Dictionary<string, string>(links, StringComparer.Ordinal)
            {
                ["parent_work_id"] = acceptanceEntity
            };
            var attValues = new JsonObject
            {
                ["attention_owner"] = "User",
                ["attention_reason"] = JsonField.AxisOrUnknown(attentionReason ?? "explicit_user_decision"),
                ["summary"] = JsonField.AxisOrUnknown(decisionSummary ?? result ?? "pending_user_decision"),
                ["acceptance_state"] = "pending_user_decision",
                ["goal_state"] = "not_complete",
                ["lead_handling_state"] = "awaiting_user"
            };
            facts.Add(new SourceFact(
                "attention",
                "attention:user_decision:" + acceptanceEntity,
                projectId,
                attLinks,
                attValues,
                new[] { NormUtil.Evidence(doc) }));
        }
        else if (!string.IsNullOrWhiteSpace(attentionOwner)
                 && productPass != true
                 && IsRecognizedAttentionOwner(attentionOwner)
                 && !IsUserOwner(attentionOwner))
        {
            // Preserve non-User explicit owners (Lead/Secretary) as attention facts too.
            var attLinks = new Dictionary<string, string>(links, StringComparer.Ordinal)
            {
                ["parent_work_id"] = acceptanceEntity
            };
            facts.Add(new SourceFact(
                "attention",
                "attention:from_acceptance:" + acceptanceEntity,
                projectId,
                attLinks,
                new JsonObject
                {
                    ["attention_owner"] = NormalizeOwnerLabel(attentionOwner),
                    ["attention_reason"] = JsonField.AxisOrUnknown(attentionReason ?? "explicit_attention_owner"),
                    ["summary"] = JsonField.AxisOrUnknown(decisionSummary ?? result),
                    ["goal_state"] = "not_complete"
                },
                new[] { NormUtil.Evidence(doc) }));
        }

        if (NormUtil.HasSwitchFields(doc.Data) || JsonField.Obj(doc.Data, "route_switch") is not null)
        {
            var sw = JsonField.Obj(doc.Data, "route_switch") ?? doc.Data;
            HandlingLogic.RecordSwitch(sw, doc, projectId, handlingRecords);
        }
    }

    internal static void AttachOrdinaryLeadAcceptance(
        List<SourceFact> facts,
        RawDocument doc,
        string? projectId,
        string? targetLine,
        string? targetDirect,
        string? result)
    {
        var mapped = MapOrdinaryLeadResult(result);
        var productPass = JsonField.Bool(doc.Data, "product_pass") == true;
        for (var i = 0; i < facts.Count; i++)
        {
            var f = facts[i];
            if (f.Kind != "work") continue;
            if (!string.IsNullOrWhiteSpace(projectId) && !string.Equals(f.ProjectId, projectId, StringComparison.Ordinal))
                continue;
            var match = (!string.IsNullOrWhiteSpace(targetLine)
                         && ((f.Links.TryGetValue("line_job_id", out var l) && l == targetLine)
                             || f.EntityId.Contains(targetLine, StringComparison.Ordinal)))
                        || (!string.IsNullOrWhiteSpace(targetDirect)
                            && ((f.Links.TryGetValue("direct_job_id", out var d) && d == targetDirect)
                                || f.EntityId.Contains(targetDirect, StringComparison.Ordinal)));
            if (!match) continue;

            var links = new Dictionary<string, string>(f.Links, StringComparer.Ordinal);
            NormUtil.PutLink(links, "line_job_id", targetLine ?? (links.TryGetValue("line_job_id", out var el) ? el : null));
            NormUtil.PutLink(links, "direct_job_id", targetDirect ?? (links.TryGetValue("direct_job_id", out var ed) ? ed : null));
            var values = NormUtil.CloneValues(f.Values);
            var acceptance = mapped.Acceptance;
            var handling = mapped.Handling;
            if (JsonField.Bool(doc.Data, "content_acceptance_performed") == false)
            {
                acceptance = "content_not_accepted";
                if (!handling.Contains("lead_handled", StringComparison.OrdinalIgnoreCase))
                    handling = "lead_handled_quota_blocked";
            }

            if (string.Equals(doc.Scope, "historical", StringComparison.OrdinalIgnoreCase)
                && string.Equals(values["authorized_current"]?.GetValue<string>(), "true", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            values["acceptance_state"] = acceptance;
            var existingHandling = values["lead_handling_state"]?.GetValue<string>();
            if (existingHandling is not "blocked_after_acceptance" and not "lead_accepting" and not "repair_dispatched")
                values["lead_handling_state"] = handling;
            values["lead_result"] = mapped.ResultToken;
            if (productPass) values["goal_state"] = "complete";
            else if (values["goal_state"]?.GetValue<string>() is null or "unknown")
                values["goal_state"] = "not_complete";
            if (string.Equals(doc.Scope, "historical", StringComparison.OrdinalIgnoreCase))
            {
                values["historical"] = "true";
                values["current_active_fault"] = "false";
            }
            facts[i] = new SourceFact(f.Kind, f.EntityId, f.ProjectId ?? projectId, links, values, f.Evidence);
        }
    }

    internal static (string Acceptance, string Handling, string ResultToken) MapOrdinaryLeadResult(string? result)
    {
        var raw = result ?? "unknown";
        if (string.IsNullOrWhiteSpace(result) || result.Equals("unknown", StringComparison.OrdinalIgnoreCase)
            || result.Contains("pending", StringComparison.OrdinalIgnoreCase))
        {
            return ("pending", "awaiting_lead_acceptance", raw);
        }

        if (result.Contains("BLOCKED_EXECUTION_QUOTA", StringComparison.OrdinalIgnoreCase)
            || result.Contains("QUOTA_NO_PRODUCT_DELIVERY", StringComparison.OrdinalIgnoreCase))
        {
            return ("content_not_accepted", "lead_handled_quota_blocked", raw);
        }

        if (result.Contains("FAIL", StringComparison.OrdinalIgnoreCase)
            || result.Contains("REPAIR_REQUIRED", StringComparison.OrdinalIgnoreCase))
        {
            return ("handled_fail_repair", "lead_handled_fail_repair", raw);
        }

        if (result.Contains("PARTIAL", StringComparison.OrdinalIgnoreCase)
            || (result.Contains("ADOPT", StringComparison.OrdinalIgnoreCase)
                && result.Contains("CLOSED", StringComparison.OrdinalIgnoreCase)))
        {
            return ("handled_partial_adopted", "lead_handled_partial_adopted", raw);
        }

        if (NormUtil.LooksAdopted(result) || result.Contains("PASS", StringComparison.OrdinalIgnoreCase)
            || result.Contains("ACCEPT", StringComparison.OrdinalIgnoreCase))
        {
            return ("handled_accepted", "lead_handled", raw);
        }

        return ("handled", "lead_handled", raw);
    }

    internal static bool IsCapacityResume(JsonObject data)
    {
        var schema = JsonField.Str(data, "schema") ?? string.Empty;
        if (schema.Contains("capacity-resume", StringComparison.OrdinalIgnoreCase)
            || schema.Contains("grok-capacity-resume", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return JsonField.Str(data, "previous_402_receipt") is not null
               && JsonField.Str(data, "new_line_job_id", "new_direct_job_id") is not null;
    }

    internal static void ApplyCapacityResume(List<SourceFact> facts, RawDocument doc, string? projectId)
    {
        var dispatched = JsonField.Bool(doc.Data, "dispatched") == true;
        var replayed = JsonField.Bool(doc.Data, "old_job_replayed") == true;
        var newLine = JsonField.Str(doc.Data, "new_line_job_id");
        var newDirect = JsonField.Str(doc.Data, "new_direct_job_id");
        var oldLine = JsonField.Str(doc.Data, "line_job_id", "old_line_job_id")
                      ?? LineIdFromReceiptPath(JsonField.Str(doc.Data, "previous_402_receipt"));
        var oldDirect = JsonField.Str(doc.Data, "direct_job_id", "old_direct_job_id");
        if (!dispatched || replayed) return;
        if (string.IsNullOrWhiteSpace(newLine) && string.IsNullOrWhiteSpace(newDirect)) return;
        if (string.IsNullOrWhiteSpace(oldLine) && string.IsNullOrWhiteSpace(oldDirect)) return;

        for (var i = 0; i < facts.Count; i++)
        {
            var f = facts[i];
            if (f.Kind != "work") continue;
            if (!string.IsNullOrWhiteSpace(projectId) && !string.Equals(f.ProjectId, projectId, StringComparison.Ordinal))
                continue;
            var match = (!string.IsNullOrWhiteSpace(oldLine)
                         && ((f.Links.TryGetValue("line_job_id", out var l) && l == oldLine)
                             || f.EntityId.Contains(oldLine, StringComparison.Ordinal)))
                        || (!string.IsNullOrWhiteSpace(oldDirect)
                            && ((f.Links.TryGetValue("direct_job_id", out var d) && d == oldDirect)
                                || f.EntityId.Contains(oldDirect, StringComparison.Ordinal)));
            if (!match) continue;

            var links = new Dictionary<string, string>(f.Links, StringComparer.Ordinal);
            NormUtil.PutLink(links, "line_job_id", oldLine ?? (links.TryGetValue("line_job_id", out var el) ? el : null));
            NormUtil.PutLink(links, "direct_job_id", oldDirect ?? (links.TryGetValue("direct_job_id", out var ed) ? ed : null));
            NormUtil.PutLink(links, "resumed_by_line_job_id", newLine);
            NormUtil.PutLink(links, "resumed_by_direct_job_id", newDirect);
            var values = NormUtil.CloneValues(f.Values);
            var existing = values["lead_handling_state"]?.GetValue<string>() ?? string.Empty;
            if (!existing.Contains("lead_handled", StringComparison.OrdinalIgnoreCase))
                values["lead_handling_state"] = "lead_handled_quota_resumed";
            if (values["acceptance_state"]?.GetValue<string>() is null or "unknown" or "pending")
                values["acceptance_state"] = "content_not_accepted";
            values["historical"] = "true";
            values["current_active_fault"] = "false";
            values["goal_state"] = "not_complete";
            facts[i] = new SourceFact(f.Kind, f.EntityId, f.ProjectId ?? projectId, links, values, f.Evidence);
        }
    }

    internal static string? LineIdFromReceiptPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var norm = path.Replace('\\', '/');
        var marker = "/jobs/";
        var idx = norm.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var rest = norm[(idx + marker.Length)..];
        var slash = rest.IndexOf('/');
        var id = slash >= 0 ? rest[..slash] : rest;
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    internal static string? ExtractRouteShellJobId(JsonObject data, string? selfId)
    {
        foreach (var key in new[] { "route_return", "route", "native", "terminal", "receipt" })
        {
            var obj = JsonField.Obj(data, key);
            var id = JsonField.Str(obj, "direct_job_id", "job_id", "native_job_id");
            if (!string.IsNullOrWhiteSpace(id)
                && !string.Equals(id, selfId, StringComparison.OrdinalIgnoreCase)
                && Guid.TryParse(id, out _))
            {
                return id;
            }
        }

        var lifted = JsonField.Str(data, "direct_job_id");
        return string.IsNullOrWhiteSpace(lifted) || string.Equals(lifted, selfId, StringComparison.OrdinalIgnoreCase)
            ? null
            : lifted;
    }

    internal static string? ExtractRouteShellLineId(JsonObject data, string? selfId)
    {
        foreach (var key in new[] { "route_return", "route", "native", "terminal", "line" })
        {
            var obj = JsonField.Obj(data, key);
            var id = JsonField.Str(obj, "line_job_id", "line_id");
            if (!string.IsNullOrWhiteSpace(id)
                && !string.Equals(id, selfId, StringComparison.OrdinalIgnoreCase)
                && Guid.TryParse(id, out _))
            {
                return id;
            }
        }

        return null;
    }

    internal static string? ExtractDirectFromCommand(JsonObject data)
    {
        var existing = JsonField.Str(data, "direct_job_id");
        var cmd = JsonField.Obj(data, "command");
        if (cmd is null) return existing;
        var fromObj = JsonField.Str(JsonField.Obj(cmd, "arguments"), "DirectJobId", "direct_job_id", "JobId", "job_id");
        if (!string.IsNullOrWhiteSpace(fromObj)) return fromObj;
        if (cmd["arguments"] is not JsonArray arr) return existing;
        for (var i = 0; i < arr.Count; i++)
        {
            if (arr[i] is not JsonValue v || !v.TryGetValue<string>(out var token) || string.IsNullOrWhiteSpace(token))
                continue;
            if (LooksDirectFlag(token) && i + 1 < arr.Count && arr[i + 1] is JsonValue nv
                && nv.TryGetValue<string>(out var next) && LooksJobId(next))
            {
                return next.Trim();
            }

            if (token.Contains("Invoke-Executor", StringComparison.OrdinalIgnoreCase)
                || token.Contains("Invoke-Route", StringComparison.OrdinalIgnoreCase)
                || token.Contains("Invoke-Direct", StringComparison.OrdinalIgnoreCase))
            {
                // wrapper script; keep scanning flags
            }
        }

        return existing;
    }

    private static bool LooksDirectFlag(string token) =>
        token.Equals("-DirectJobId", StringComparison.OrdinalIgnoreCase)
        || token.Equals("--direct-job-id", StringComparison.OrdinalIgnoreCase)
        || token.Equals("-JobId", StringComparison.OrdinalIgnoreCase)
        || token.Equals("-DirectId", StringComparison.OrdinalIgnoreCase);

    private static bool LooksJobId(string token) =>
        token.Length >= 8 && token.Contains('-', StringComparison.Ordinal) && !token.Contains('\\') && !token.Contains('/');

    private static bool IsUserOwner(string? owner) =>
        !string.IsNullOrWhiteSpace(owner)
        && owner.Equals("User", StringComparison.OrdinalIgnoreCase);

    private static bool IsRecognizedAttentionOwner(string? owner)
    {
        if (string.IsNullOrWhiteSpace(owner)) return false;
        return owner.Equals("User", StringComparison.OrdinalIgnoreCase)
               || owner.Equals("Lead", StringComparison.OrdinalIgnoreCase)
               || owner.Equals("Secretary", StringComparison.OrdinalIgnoreCase)
               || owner.Equals("秘书", StringComparison.OrdinalIgnoreCase)
               || owner.Equals("原Lead", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeOwnerLabel(string owner)
    {
        if (owner.Equals("User", StringComparison.OrdinalIgnoreCase)) return "User";
        if (owner.Equals("Secretary", StringComparison.OrdinalIgnoreCase)
            || owner.Equals("秘书", StringComparison.OrdinalIgnoreCase)) return "Secretary";
        if (owner.Equals("Lead", StringComparison.OrdinalIgnoreCase)
            || owner.Equals("原Lead", StringComparison.OrdinalIgnoreCase)) return "Lead";
        return owner;
    }

    private static bool HasExplicitDecisionSignal(JsonObject data, string? result, string? reason, string? summary)
    {
        if (!string.IsNullOrWhiteSpace(reason)) return true;
        if (!string.IsNullOrWhiteSpace(summary)) return true;
        if (!string.IsNullOrWhiteSpace(result)
            && (result.Contains("pending_user", StringComparison.OrdinalIgnoreCase)
                || result.Contains("user", StringComparison.OrdinalIgnoreCase)
                || result.Contains("decision", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        // Protocol may mark an explicit decision object/flag without free text.
        if (data["decision"] is not null || data["explicit_decision"] is not null) return true;
        var flag = JsonField.Bool(data, "explicit_decision", "requires_user_decision", "user_decision");
        return flag == true;
    }
}
