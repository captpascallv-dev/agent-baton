// SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Nodes;
using CliWatch.Contracts;

namespace CliWatch.Normalization;

internal static class HandlingLogic
{
    public static void TryExtractHandling(RawDocument doc, List<HandlingRecord> handlingRecords)
    {
        var sw = JsonField.Obj(doc.Data, "route_switch", "lead_route_switch", "consumed_route_switch");
        if (sw is not null)
        {
            RecordSwitch(sw, doc, JsonField.Str(doc.Data, "project_id") ?? doc.ProjectHint, handlingRecords);
        }
        else if (NormUtil.HasSwitchFields(doc.Data))
        {
            RecordSwitch(doc.Data, doc, JsonField.Str(doc.Data, "project_id") ?? doc.ProjectHint, handlingRecords);
        }
    }

    public static void RecordSwitch(JsonObject sw, RawDocument doc, string? projectId, List<HandlingRecord> handlingRecords)
    {
        var owner = JsonField.Obj(sw, "owner") ?? JsonField.Obj(doc.Data, "owner");
        handlingRecords.Add(new HandlingRecord
        {
            SourceDoc = doc,
            ProjectId = projectId,
            OldLineJobId = JsonField.Str(sw, "old_line_job_id"),
            OldDirectJobId = JsonField.Str(sw, "old_direct_job_id"),
            NewLineJobId = JsonField.Str(sw, "new_line_job_id"),
            NewDirectJobId = JsonField.Str(sw, "new_direct_job_id"),
            NewWorkspace = JsonField.Str(sw, "new_workspace"),
            NewRoute = JsonField.Str(sw, "new_route"),
            ActualSwitchComplete = JsonField.Bool(sw, "actual_switch_complete") ?? false,
            NewTransportComplete = JsonField.Bool(sw, "new_transport_complete"),
            NewSuccess = JsonField.Bool(sw, "new_grok_success", "new_success", "new_cursor_success"),
            OldRoundResult = JsonField.Str(sw, "old_round_result"),
            OldWriterReleased = JsonField.Bool(sw, "old_writer_released"),
            CallbackLead = JsonField.Str(sw, "callback_lead", "lead_session_id"),
            ConsumedPid = JsonField.Int(owner, "pid") ?? JsonField.Int(sw, "old_pid", "consumed_pid"),
            ConsumedTicks = JsonField.Long(owner, "start_time_utc_ticks")
                            ?? JsonField.Long(sw, "old_start_time_utc_ticks", "consumed_start_ticks")
        });
    }

    public static void ApplySwitchToValues(JsonObject values, Dictionary<string, string> links, JsonObject sw)
    {
        var complete = JsonField.Bool(sw, "actual_switch_complete") ?? false;
        var superseded = JsonField.Str(sw, "old_line_job_id") ?? JsonField.Str(sw, "old_direct_job_id");
        if (complete && !string.IsNullOrWhiteSpace(superseded))
        {
            links["supersedes"] = superseded!;
        }

        NormUtil.PutLink(links, "line_job_id", JsonField.Str(sw, "new_line_job_id") ?? (links.TryGetValue("line_job_id", out var lj) ? lj : null));
        NormUtil.PutLink(links, "direct_job_id", JsonField.Str(sw, "new_direct_job_id") ?? (links.TryGetValue("direct_job_id", out var dj) ? dj : null));
        NormUtil.PutLink(links, "workspace", JsonField.Str(sw, "new_workspace"));

        values["lead_handling_state"] = complete ? "consumed_old_terminal_current_successor" : "route_switch_pending";
        if (JsonField.Bool(sw, "new_transport_complete") == true) values["transport_state"] = "complete";
        if (JsonField.Bool(sw, "new_grok_success", "new_success") == true) values["execution_state"] = "succeeded";
        var route = JsonField.Str(sw, "new_route");
        if (!string.IsNullOrWhiteSpace(route)) values["route"] = route;
    }

    public static void ApplyHandlingAndConflicts(
        List<SourceFact> facts,
        List<SourceIssue> issues,
        List<HandlingRecord> handlingRecords,
        List<(RawDocument Doc, int Pid, long Ticks, string State)> processObs,
        DateTimeOffset collectedAt)
    {
        foreach (var h in handlingRecords)
        {
            if (!h.ActualSwitchComplete && string.IsNullOrWhiteSpace(h.OldLineJobId) && string.IsNullOrWhiteSpace(h.OldDirectJobId))
            {
                continue;
            }

            ApplySuccessor(facts, h, collectedAt);
            ApplyHistorical(facts, h, collectedAt);
            DetectLiveConflict(facts, issues, h, processObs);
        }

        ApplyAuthorizedCurrentFromActiveWork(facts, handlingRecords, processObs, collectedAt);
    }

    /// <summary>
    /// ACTIVE_WORK line/direct is the authorized current binding when registry IDs lag.
    /// Closed old bindings (Lead-handled or a previous live handler) become historical so
    /// old receipts/blockers cannot cover a newer ACTIVE_WORK. Unhandled old failures stay
    /// pending until producer disposition. Live process identity is not auto-consumed.
    /// </summary>
    public static void ApplyAuthorizedCurrentFromActiveWork(
        List<SourceFact> facts,
        List<HandlingRecord> handlingRecords,
        List<(RawDocument Doc, int Pid, long Ticks, string State)> processObs,
        DateTimeOffset collectedAt)
    {
        _ = collectedAt;
        foreach (var project in facts.Where(f => f.Kind == "project").ToList())
        {
            var pid = project.ProjectId ?? project.EntityId;
            project.Links.TryGetValue("line_job_id", out var regLine);
            project.Links.TryGetValue("direct_job_id", out var regDirect);
            project.Links.TryGetValue("line_job_root_id", out var rootLine);
            project.Links.TryGetValue("direct_job_root_id", out var rootDirect);

            var awLines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var awDirects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var awNatives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            SourceFact? awFact = null;
            foreach (var f in facts.Where(x => x.Kind == "work" && string.Equals(x.ProjectId, pid, StringComparison.Ordinal)))
            {
                if (!f.Evidence.Any(e => e.Kind == "active_work")) continue;
                awFact ??= f;
                if (f.Links.TryGetValue("line_job_id", out var ln) && !string.IsNullOrWhiteSpace(ln))
                    awLines.Add(ln);
                if (f.Links.TryGetValue("direct_job_id", out var dn) && !string.IsNullOrWhiteSpace(dn))
                    awDirects.Add(dn);
                if (f.Links.TryGetValue("native_session_id", out var nn) && !string.IsNullOrWhiteSpace(nn))
                    awNatives.Add(nn);
            }

            var awLine = awLines.FirstOrDefault();
            var awDirect = awDirects.FirstOrDefault();
            var awNative = awNatives.FirstOrDefault();
            if (awLines.Count == 0 && awDirects.Count == 0)
            {
                HistoricizeUnmatchedStarters(facts, pid, regLine, regDirect);
                continue;
            }

            var staleLines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var staleDirects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(regLine) && !awLines.Contains(regLine))
                staleLines.Add(regLine);
            if (!string.IsNullOrWhiteSpace(rootLine) && !awLines.Contains(rootLine))
                staleLines.Add(rootLine);
            if (!string.IsNullOrWhiteSpace(regDirect) && !awDirects.Contains(regDirect))
                staleDirects.Add(regDirect);
            if (!string.IsNullOrWhiteSpace(rootDirect) && !awDirects.Contains(rootDirect))
                staleDirects.Add(rootDirect);

            var staleLine = staleLines.FirstOrDefault();
            var staleDirect = staleDirects.FirstOrDefault();
            var pointerLag = staleLines.Count > 0 || staleDirects.Count > 0;
            var nativeSupersede = facts.Any(x =>
                x.Kind == "work"
                && string.Equals(x.ProjectId, pid, StringComparison.Ordinal)
                && SameNativeSuperseded(x, awNatives, awLines, awDirects));
            var disposition = pointerLag
                || nativeSupersede
                || HasProducerDisposition(pid, awLine, awDirect, staleLine, staleDirect, facts, handlingRecords);

            if (!string.IsNullOrWhiteSpace(rootLine) && !string.IsNullOrWhiteSpace(rootDirect)
                && !string.Equals(rootLine, awLine, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(rootDirect, awDirect, StringComparison.OrdinalIgnoreCase))
            {
                CrossLinkPair(facts, pid, rootLine, rootDirect);
            }

            for (var i = 0; i < facts.Count; i++)
            {
                var f = facts[i];
                if (f.Kind != "work" || !string.Equals(f.ProjectId, pid, StringComparison.Ordinal))
                    continue;

                var authorized = MatchesAny(f, awLines, awDirects);
                var stale = !authorized && (MatchesAny(f, staleLines, staleDirects)
                                            || SameNativeSuperseded(f, awNatives, awLines, awDirects)
                                            || UnmatchedStarter(f, awLines, awDirects));
                if (authorized)
                {
                    var values = NormUtil.CloneValues(f.Values);
                    values["authorized_current"] = "true";
                    var links = new Dictionary<string, string>(f.Links, StringComparer.Ordinal);
                    if (disposition)
                    {
                        var handling = values["lead_handling_state"]?.GetValue<string>();
                        if (string.IsNullOrWhiteSpace(handling) || handling == "unknown")
                            values["lead_handling_state"] = "consumed_old_terminal_current_successor";
                        var sup = staleLine ?? staleDirect;
                        if (!string.IsNullOrWhiteSpace(sup) && !links.ContainsKey("supersedes"))
                            links["supersedes"] = sup!;
                    }
                    facts[i] = new SourceFact(f.Kind, f.EntityId, f.ProjectId, links, values, f.Evidence);
                }
                else if (stale)
                {
                    if (HasLiveIdentity(f, processObs))
                    {
                        var values = NormUtil.CloneValues(f.Values);
                        values["authorized_current"] = "false";
                        values["registry_pointer_lag"] = "true";
                        facts[i] = new SourceFact(f.Kind, f.EntityId, f.ProjectId, f.Links, values, f.Evidence);
                        continue;
                    }

                    var handlingNow = f.Values["lead_handling_state"]?.GetValue<string>();
                    var acceptanceNow = f.Values["acceptance_state"]?.GetValue<string>();
                    var oldBindingClosed = IsCompleteLeadHandled(handlingNow, acceptanceNow)
                        || NormUtil.IsLiveHandlerState(handlingNow, acceptanceNow);
                    var unmatchedStarter = UnmatchedStarter(f, awLines, awDirects);
                    if (disposition || oldBindingClosed || unmatchedStarter)
                    {
                        var consumed = NormUtil.CloneValues(f.Values);
                        consumed["historical"] = "true";
                        consumed["current_active_fault"] = "false";
                        consumed["authorized_current"] = "false";
                        consumed["registry_pointer_lag"] = "true";
                        var existing = consumed["lead_handling_state"]?.GetValue<string>() ?? string.Empty;
                        var existingAcc = consumed["acceptance_state"]?.GetValue<string>() ?? string.Empty;
                        var associatedRepair = awFact is not null && WorkMatchesPriorRepair(f, awFact);
                        if (disposition
                            && !existing.Contains("lead_handled", StringComparison.OrdinalIgnoreCase)
                            && !existing.StartsWith("handled", StringComparison.OrdinalIgnoreCase)
                            && !NormUtil.IsLiveHandlerState(existing, existingAcc))
                        {
                            consumed["lead_handling_state"] = associatedRepair
                                ? "lead_handled_fail_repair"
                                : "consumed_old_terminal";
                        }

                        if (associatedRepair
                            && (string.IsNullOrWhiteSpace(existingAcc) || existingAcc == "unknown"))
                        {
                            consumed["acceptance_state"] = "handled_fail_repair";
                        }

                        facts[i] = new SourceFact(f.Kind, f.EntityId, f.ProjectId, f.Links, consumed, f.Evidence);
                        continue;
                    }

                    var lag = NormUtil.CloneValues(f.Values);
                    lag["authorized_current"] = "false";
                    lag["registry_pointer_lag"] = "true";
                    facts[i] = new SourceFact(f.Kind, f.EntityId, f.ProjectId, f.Links, lag, f.Evidence);
                }
            }

            // Binding ≠ disposition: a complete ordinary Lead result on a non-AW
            // entity is historical even when registry IDs already match AW (no "stale").
            // Unhandled old failures stay pending review; live identities stay uncovered.
            for (var i = 0; i < facts.Count; i++)
            {
                var f = facts[i];
                if (f.Kind != "work" || !string.Equals(f.ProjectId, pid, StringComparison.Ordinal))
                    continue;
                if (MatchesAny(f, awLines, awDirects))
                    continue;
                if (HasLiveIdentity(f, processObs))
                    continue;
                var handling = f.Values["lead_handling_state"]?.GetValue<string>() ?? string.Empty;
                var acceptance = f.Values["acceptance_state"]?.GetValue<string>() ?? string.Empty;
                // Live handler (accepting / repair dispatched) on a sibling line is
                // current concurrent work, not consumed history.
                if (!IsCompleteLeadHandled(handling, acceptance))
                    continue;
                var values = NormUtil.CloneValues(f.Values);
                values["historical"] = "true";
                values["current_active_fault"] = "false";
                values["authorized_current"] = "false";
                facts[i] = new SourceFact(f.Kind, f.EntityId, f.ProjectId, f.Links, values, f.Evidence);
            }
        }
    }

    internal static bool IsCompleteLeadHandled(string? handling, string? acceptance)
    {
        var h = handling ?? string.Empty;
        var a = acceptance ?? string.Empty;
        if (h.Contains("route_switch_pending", StringComparison.OrdinalIgnoreCase)
            || h.Contains("handling_recorded_incomplete", StringComparison.OrdinalIgnoreCase)
            || h.Contains("awaiting_lead_acceptance", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (h.Contains("lead_handled", StringComparison.OrdinalIgnoreCase)
            || h.Equals("handled", StringComparison.OrdinalIgnoreCase)
            || h.StartsWith("handled", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return a.StartsWith("handled", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasProducerDisposition(
        string? projectId,
        string? awLine,
        string? awDirect,
        string? staleLine,
        string? staleDirect,
        List<SourceFact> facts,
        List<HandlingRecord> handlingRecords)
    {
        foreach (var h in handlingRecords)
        {
            if (!string.IsNullOrWhiteSpace(h.ProjectId)
                && !string.Equals(h.ProjectId, projectId, StringComparison.Ordinal))
            {
                continue;
            }

            var oldMatch = (!string.IsNullOrWhiteSpace(staleLine) && staleLine == h.OldLineJobId)
                           || (!string.IsNullOrWhiteSpace(staleDirect) && staleDirect == h.OldDirectJobId);
            var newMatch = (!string.IsNullOrWhiteSpace(awLine) && awLine == h.NewLineJobId)
                           || (!string.IsNullOrWhiteSpace(awDirect) && awDirect == h.NewDirectJobId);
            if (oldMatch && newMatch && (h.ActualSwitchComplete || h.OldWriterReleased == true))
                return true;
        }

        foreach (var f in facts.Where(x => x.Kind == "work" && string.Equals(x.ProjectId, projectId, StringComparison.Ordinal)))
        {
            if (!f.Links.TryGetValue("supersedes", out var sup) || string.IsNullOrWhiteSpace(sup))
                continue;
            var pointsAtOld = sup == staleLine || sup == staleDirect
                              || (!string.IsNullOrWhiteSpace(staleLine) && f.Links.TryGetValue("line_job_id", out var _) && sup == staleLine)
                              || (!string.IsNullOrWhiteSpace(staleDirect) && sup == staleDirect);
            var isAuthorizedNew = MatchesId(f, awLine, awDirect);
            var handling = f.Values["lead_handling_state"]?.GetValue<string>() ?? string.Empty;
            if (pointsAtOld && isAuthorizedNew
                && (handling.Contains("current_successor", StringComparison.OrdinalIgnoreCase)
                    || handling.Equals("consumed_old_terminal_current_successor", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasLiveIdentity(
        SourceFact f,
        List<(RawDocument Doc, int Pid, long Ticks, string State)> processObs)
    {
        var pidText = f.Values["pid"]?.GetValue<string>();
        var ticksText = f.Values["start_time_utc_ticks"]?.GetValue<string>();
        if (!int.TryParse(pidText, out var pid) || !long.TryParse(ticksText, out var ticks))
            return false;
        return processObs.Any(o =>
            o.Pid == pid
            && o.Ticks == ticks
            && string.Equals(o.State, "alive", StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesId(SourceFact f, string? line, string? direct)
    {
        if (!string.IsNullOrWhiteSpace(line)
            && (f.EntityId.Contains(line, StringComparison.Ordinal)
                || (f.Links.TryGetValue("line_job_id", out var l) && string.Equals(l, line, StringComparison.OrdinalIgnoreCase))))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(direct)
            && (f.EntityId.Contains(direct, StringComparison.Ordinal)
                || (f.Links.TryGetValue("direct_job_id", out var d) && string.Equals(d, direct, StringComparison.OrdinalIgnoreCase))))
        {
            return true;
        }

        return false;
    }

    private static void AddIfLagging(HashSet<string> set, string? candidate, string? current)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(current)) return;
        if (string.Equals(candidate, current, StringComparison.OrdinalIgnoreCase)) return;
        set.Add(candidate);
    }

    private static bool MatchesAny(SourceFact f, HashSet<string> lines, HashSet<string> directs)
    {
        if (f.Links.TryGetValue("line_job_id", out var l) && !string.IsNullOrWhiteSpace(l) && lines.Contains(l))
            return true;
        if (f.Links.TryGetValue("direct_job_id", out var d) && !string.IsNullOrWhiteSpace(d) && directs.Contains(d))
            return true;
        foreach (var id in lines.Concat(directs))
        {
            if (f.EntityId.Contains(id, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static bool SameNativeSuperseded(SourceFact f, HashSet<string> awNatives, HashSet<string> awLines, HashSet<string> awDirects)
    {
        if (awNatives.Count == 0) return false;
        if (!f.Links.TryGetValue("native_session_id", out var native)
            || string.IsNullOrWhiteSpace(native)
            || !awNatives.Contains(native))
        {
            return false;
        }

        var role = f.Values["role"]?.GetValue<string>() ?? string.Empty;
        if (role.Contains("review", StringComparison.OrdinalIgnoreCase)
            || role.Contains("army", StringComparison.OrdinalIgnoreCase)
            || role.Contains("contrib", StringComparison.OrdinalIgnoreCase)
            || role.Equals("lead", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !MatchesAny(f, awLines, awDirects);
    }

    private static bool UnmatchedStarter(SourceFact f, string? currentLine, string? currentDirect)
    {
        var lines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(currentLine)) lines.Add(currentLine);
        if (!string.IsNullOrWhiteSpace(currentDirect)) directs.Add(currentDirect);
        return UnmatchedStarter(f, lines, directs);
    }

    private static bool UnmatchedStarter(SourceFact f, HashSet<string> currentLines, HashSet<string> currentDirects)
    {
        if (!string.Equals(f.Values["starter_wrapper"]?.GetValue<string>(), "true", StringComparison.OrdinalIgnoreCase))
            return false;
        if (currentLines.Count == 0 && currentDirects.Count == 0)
            return false;
        return !MatchesAny(f, currentLines, currentDirects);
    }

    private static void HistoricizeUnmatchedStarters(List<SourceFact> facts, string? projectId, string? currentLine, string? currentDirect)
    {
        if (string.IsNullOrWhiteSpace(currentLine) && string.IsNullOrWhiteSpace(currentDirect))
            return;
        for (var i = 0; i < facts.Count; i++)
        {
            var f = facts[i];
            if (f.Kind != "work" || !string.Equals(f.ProjectId, projectId, StringComparison.Ordinal))
                continue;
            if (!UnmatchedStarter(f, currentLine, currentDirect))
                continue;
            var values = NormUtil.CloneValues(f.Values);
            values["historical"] = "true";
            values["current_active_fault"] = "false";
            values["authorized_current"] = "false";
            facts[i] = new SourceFact(f.Kind, f.EntityId, f.ProjectId, f.Links, values, f.Evidence);
        }
    }

    private static bool WorkMatchesPriorRepair(SourceFact work, SourceFact aw)
    {
        var jobs = SplitCsv(aw.Values["prior_fail_repair_jobs"]?.GetValue<string>());
        var packages = SplitCsv(aw.Values["prior_fail_repair_packages"]?.GetValue<string>());
        if (jobs.Count == 0 && packages.Count == 0)
            return false;

        work.Links.TryGetValue("line_job_id", out var line);
        work.Links.TryGetValue("direct_job_id", out var direct);
        if (jobs.Count > 0)
        {
            if ((!string.IsNullOrWhiteSpace(line) && jobs.Contains(line))
                || (!string.IsNullOrWhiteSpace(direct) && jobs.Contains(direct))
                || jobs.Any(id => work.EntityId.Contains(id, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return false;
        }

        var hay = DocumentNormalizers.PackageStem(
            (work.Values["stage"]?.GetValue<string>() ?? "") + "_" +
            (work.Values["package_id"]?.GetValue<string>() ?? "") + "_" +
            (work.Values["task_name"]?.GetValue<string>() ?? ""));
        if (string.IsNullOrWhiteSpace(hay)) return false;
        return packages.Any(p =>
        {
            var stem = DocumentNormalizers.PackageStem(p);
            return stem.Length >= 8 && hay.Contains(stem, StringComparison.Ordinal);
        });
    }

    private static HashSet<string> SplitCsv(string? raw)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw)) return set;
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            set.Add(part);
        return set;
    }

    private static void CrossLinkPair(List<SourceFact> facts, string? projectId, string lineId, string directId)
    {
        for (var i = 0; i < facts.Count; i++)
        {
            var f = facts[i];
            if (f.Kind != "work" || !string.Equals(f.ProjectId, projectId, StringComparison.Ordinal))
                continue;
            if (!MatchesId(f, lineId, directId)) continue;
            var links = new Dictionary<string, string>(f.Links, StringComparer.Ordinal);
            NormUtil.PutLink(links, "line_job_id", lineId);
            NormUtil.PutLink(links, "direct_job_id", directId);
            facts[i] = new SourceFact(f.Kind, f.EntityId, f.ProjectId, links, f.Values, f.Evidence);
        }
    }

    private static void ApplySuccessor(List<SourceFact> facts, HandlingRecord h, DateTimeOffset collectedAt)
    {
        if (string.IsNullOrWhiteSpace(h.NewLineJobId) && string.IsNullOrWhiteSpace(h.NewDirectJobId)) return;

        var successorId = !string.IsNullOrWhiteSpace(h.NewLineJobId)
            ? "work:line:" + h.NewLineJobId
            : "work:direct:" + h.NewDirectJobId;
        var superseded = h.OldLineJobId ?? h.OldDirectJobId;
        var existing = facts.FindIndex(f => f.Kind == "work" && f.EntityId == successorId);
        if (existing >= 0)
        {
            var f = facts[existing];
            var links = new Dictionary<string, string>(f.Links, StringComparer.Ordinal);
            if (h.ActualSwitchComplete && !string.IsNullOrWhiteSpace(superseded)) links["supersedes"] = superseded!;
            NormUtil.PutLink(links, "line_job_id", h.NewLineJobId ?? (links.TryGetValue("line_job_id", out var lj) ? lj : null));
            NormUtil.PutLink(links, "direct_job_id", h.NewDirectJobId ?? (links.TryGetValue("direct_job_id", out var dj) ? dj : null));
            NormUtil.PutLink(links, "workspace", h.NewWorkspace);
            var values = NormUtil.CloneValues(f.Values);
            values["lead_handling_state"] = h.ActualSwitchComplete ? "consumed_old_terminal_current_successor" : "route_switch_pending";
            if (!string.IsNullOrWhiteSpace(h.NewRoute)) values["route"] = h.NewRoute;
            if (h.NewTransportComplete == true) values["transport_state"] = "complete";
            if (h.NewSuccess == true) values["execution_state"] = "succeeded";
            facts[existing] = new SourceFact(f.Kind, f.EntityId, f.ProjectId ?? h.ProjectId, links, values, f.Evidence);
            return;
        }

        var newLinks = new Dictionary<string, string>(StringComparer.Ordinal);
        NormUtil.PutLink(newLinks, "line_job_id", h.NewLineJobId);
        NormUtil.PutLink(newLinks, "direct_job_id", h.NewDirectJobId);
        NormUtil.PutLink(newLinks, "workspace", h.NewWorkspace);
        NormUtil.PutLink(newLinks, "lead_session_id", h.CallbackLead);
        if (h.ActualSwitchComplete && !string.IsNullOrWhiteSpace(superseded)) newLinks["supersedes"] = superseded!;
        var newValues = NormUtil.BaseAxes("execution", h.NewRoute,
            JsonField.BoolAxis(h.NewTransportComplete, "complete", "incomplete"),
            "unknown",
            h.NewSuccess == true ? "succeeded" : "unknown",
            "unknown",
            "successor_after_route_switch");
        newValues["lead_handling_state"] = h.ActualSwitchComplete ? "consumed_old_terminal_current_successor" : "route_switch_pending";
        facts.Add(new SourceFact("work", successorId, h.ProjectId, newLinks, newValues,
            new[] { EvidenceFromHandling(h, collectedAt) }));
    }

    private static void ApplyHistorical(List<SourceFact> facts, HandlingRecord h, DateTimeOffset collectedAt)
    {
        if (string.IsNullOrWhiteSpace(h.OldLineJobId) && string.IsNullOrWhiteSpace(h.OldDirectJobId)) return;

        var oldId = !string.IsNullOrWhiteSpace(h.OldLineJobId)
            ? "work:line:" + h.OldLineJobId
            : "work:direct:" + h.OldDirectJobId;
        var idx = facts.FindIndex(f => f.Kind == "work" &&
            (f.EntityId == oldId
             || (f.Links.TryGetValue("line_job_id", out var lj) && lj == h.OldLineJobId)
             || (f.Links.TryGetValue("direct_job_id", out var dj) && dj == h.OldDirectJobId)));

        if (idx >= 0)
        {
            var f = facts[idx];
            var values = NormUtil.CloneValues(f.Values);
            values["lead_handling_state"] = h.ActualSwitchComplete ? "consumed_old_terminal" : "handling_recorded_incomplete";
            if (h.ActualSwitchComplete)
            {
                values["historical"] = "true";
                values["current_active_fault"] = "false";
            }
            if (string.Equals(values["execution_state"]?.GetValue<string>(), "unknown", StringComparison.Ordinal)
                && NormUtil.LooksFailed(h.OldRoundResult))
            {
                values["execution_state"] = "failed";
            }
            facts[idx] = new SourceFact(f.Kind, f.EntityId, f.ProjectId ?? h.ProjectId, f.Links, values, f.Evidence);
            return;
        }

        if (!h.ActualSwitchComplete) return;

        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        NormUtil.PutLink(links, "line_job_id", h.OldLineJobId);
        NormUtil.PutLink(links, "direct_job_id", h.OldDirectJobId);
        var valuesNew = NormUtil.BaseAxes("execution", null, "unknown", "unknown",
            NormUtil.LooksFailed(h.OldRoundResult) ? "failed" : "unknown",
            "unknown", h.OldRoundResult ?? "consumed_historical");
        valuesNew["lead_handling_state"] = "consumed_old_terminal";
        valuesNew["historical"] = "true";
        valuesNew["current_active_fault"] = "false";
        facts.Add(new SourceFact("work", oldId, h.ProjectId, links, valuesNew,
            new[] { EvidenceFromHandling(h, collectedAt) }));
    }

    private static void DetectLiveConflict(
        List<SourceFact> facts,
        List<SourceIssue> issues,
        HandlingRecord h,
        List<(RawDocument Doc, int Pid, long Ticks, string State)> processObs)
    {
        if (h.ConsumedPid is not int cpid || h.ConsumedTicks is not long cticks) return;
        foreach (var obs in processObs)
        {
            if (obs.Pid != cpid || obs.Ticks != cticks) continue;
            if (!string.Equals(obs.State, "alive", StringComparison.OrdinalIgnoreCase)) continue;

            issues.Add(new SourceIssue(
                obs.Doc.Id,
                "consumed_identity_still_live",
                $"Lead handling consumed pid={cpid}/ticks={cticks} but process_observation state=alive.",
                obs.Doc.ObservedAt));
            facts.Add(new SourceFact(
                "attention",
                "attention:conflict:" + cpid + ":" + cticks,
                h.ProjectId,
                new Dictionary<string, string> { ["source_object_id"] = obs.Doc.Id },
                new JsonObject
                {
                    ["attention_owner"] = "Lead",
                    ["attention_reason"] = "consumed_identity_still_live",
                    ["summary"] = "Handled/consumed writer identity still observed alive.",
                    ["process_state"] = "alive",
                    ["lead_handling_state"] = "conflict"
                },
                new[] { NormUtil.Evidence(obs.Doc) }));
        }
    }

    public static EvidenceRef EvidenceFromHandling(HandlingRecord h, DateTimeOffset collectedAt) =>
        new(h.SourceDoc.Id, h.SourceDoc.Location, h.SourceDoc.Kind, h.SourceDoc.ObservedAt,
            h.SourceDoc.FactAt ?? collectedAt, DataQuality.Fresh, "route_switch_protocol_fields");

    public static void EmitHistoricalFromSwitchFields(RawDocument doc, string? projectId, List<SourceFact> facts)
    {
        var sw = JsonField.Obj(doc.Data, "route_switch") ?? (NormUtil.HasSwitchFields(doc.Data) ? doc.Data : null);
        if (sw is null) return;
        var oldLine = JsonField.Str(sw, "old_line_job_id");
        var oldDirect = JsonField.Str(sw, "old_direct_job_id");
        if (string.IsNullOrWhiteSpace(oldLine) && string.IsNullOrWhiteSpace(oldDirect)) return;
        var oldId = !string.IsNullOrWhiteSpace(oldLine) ? "work:line:" + oldLine : "work:direct:" + oldDirect;
        if (facts.Any(f => f.EntityId == oldId)) return;

        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        NormUtil.PutLink(links, "line_job_id", oldLine);
        NormUtil.PutLink(links, "direct_job_id", oldDirect);
        var result = JsonField.Str(sw, "old_round_result");
        var values = NormUtil.BaseAxes("execution", null, "unknown", "unknown",
            NormUtil.LooksFailed(result) ? "failed" : "unknown", "unknown", result);
        var complete = JsonField.Bool(sw, "actual_switch_complete") ?? false;
        if (!complete) return;
        values["lead_handling_state"] = "consumed_old_terminal";
        values["historical"] = "true";
        values["current_active_fault"] = "false";
        facts.Add(new SourceFact("work", oldId, projectId, links, values, new[] { NormUtil.Evidence(doc) }));
    }
}
