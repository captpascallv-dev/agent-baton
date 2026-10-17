// SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Text.Json.Nodes;
using CliWatch.Contracts;

namespace CliWatch.Projection;

/// <summary>
/// Projects SourceFact batches into CockpitSnapshot.
/// Aggregate by stable ProjectId; never invent projects from folder names.
/// Consumed/superseded failed work exits current abnormal set when LeadHandling
/// evidence is present; missing handling raises Lead attention instead of silent drop.
/// TRUTH_CASE_20260913_01: general link/axis logic — no hardcoded case UUIDs.
/// </summary>
public sealed class StateProjector : IProjector
{
    private static readonly string[] AxisKeys =
    {
        "process_state", "turn_state", "execution_state", "transport_state",
        "delivery_state", "callback_state", "lead_handling_state",
        "acceptance_state", "adoption_state", "goal_state"
    };

    public CockpitSnapshot Project(FactBatch batch, CockpitSnapshot? previous = null)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var issues = new List<SourceIssue>(batch.Issues ?? Array.Empty<SourceIssue>());
        var facts = (batch.Facts ?? Array.Empty<SourceFact>()).ToList();

        if (facts.Count == 0 && issues.Count > 0)
        {
            return ProjectEmptyOrFailed(batch.CollectedAt, issues, previous, DataQuality.Unavailable);
        }

        var conflicting = issues.Any(i =>
            i.Code.Contains("conflict", StringComparison.OrdinalIgnoreCase)
            || i.Code.Contains("cross_project", StringComparison.OrdinalIgnoreCase)
            || i.Code.Equals("consumed_identity_still_live", StringComparison.OrdinalIgnoreCase));

        var projectFacts = facts.Where(f => f.Kind == "project").ToList();
        var workFacts = facts.Where(f => f.Kind == "work").ToList();
        var attentionFacts = facts.Where(f => f.Kind == "attention").ToList();
        var contributionFacts = facts.Where(f => f.Kind is "contribution" or "acceptance").ToList();
        var routeFacts = facts.Where(f => f.Kind == "route_coverage").ToList();
        var leadFacts = facts.Where(f => f.Kind == "lead").ToList();

        // Index successor / supersede relationships from Links (general — no UUID hardcode).
        var supersedeMap = BuildSupersedeIndex(workFacts);
        var consumedKeys = BuildConsumedKeySet(workFacts, supersedeMap);

        // Deduplicate line+direct same binding once.
        var dedupedWorks = DeduplicateWorks(workFacts);

        // Partition works into current vs historical based on LeadHandling / supersedes.
        ClassifyHistorical(dedupedWorks, supersedeMap, consumedKeys, out var currentWorks, out var historicalWorks);

        // Unassociated work (no ProjectId) — never invent project from folder/workspace.
        var unassociated = currentWorks.Where(w => string.IsNullOrWhiteSpace(w.ProjectId))
            .Concat(historicalWorks.Where(w => string.IsNullOrWhiteSpace(w.ProjectId)))
            .ToList();
        foreach (var u in unassociated)
        {
            issues.Add(new SourceIssue(
                u.EntityId,
                "unassociated_work",
                "Work lacks reliable ProjectId; left unassigned (no directory-title invent).",
                batch.CollectedAt));
        }

        // Discover project ids: registry + any fact ProjectId (ACTIVE_WORK may ahead of registry).
        var projectIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in projectFacts)
        {
            if (!string.IsNullOrWhiteSpace(p.ProjectId)) projectIds.Add(p.ProjectId!);
            else if (!string.IsNullOrWhiteSpace(p.EntityId)) projectIds.Add(p.EntityId);
        }

        foreach (var f in facts)
        {
            if (!string.IsNullOrWhiteSpace(f.ProjectId)) projectIds.Add(f.ProjectId!);
        }

        // Build project views
        var projects = new List<ProjectView>();
        foreach (var pid in projectIds.OrderBy(x => x, StringComparer.Ordinal))
        {
            var pf = projectFacts.FirstOrDefault(p =>
                string.Equals(p.ProjectId ?? p.EntityId, pid, StringComparison.Ordinal));

            var pWorksCurrent = currentWorks.Where(w => string.Equals(w.ProjectId, pid, StringComparison.Ordinal)).ToList();
            var pWorksHist = historicalWorks.Where(w => string.Equals(w.ProjectId, pid, StringComparison.Ordinal)).ToList();
            RelocateAdoptedFolderWorks(pWorksCurrent, pWorksHist, contributionFacts.Concat(facts.Where(f => f.Kind == "acceptance")));
            var pAttFacts = attentionFacts.Where(a => string.Equals(a.ProjectId, pid, StringComparison.Ordinal)).ToList();
            var pContrib = contributionFacts.Where(c => string.Equals(c.ProjectId, pid, StringComparison.Ordinal)).ToList();
            var pLeads = leadFacts.Where(l => string.Equals(l.ProjectId, pid, StringComparison.Ordinal)).ToList();

            var workViews = new List<WorkView>();
            foreach (var w in pWorksCurrent)
            {
                if (string.Equals(Val(w, "role"), "lead", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.Equals(Val(w, "diagnostic_only"), "true", StringComparison.OrdinalIgnoreCase)
                    && IsMissing(Val(w, "process_state")))
                {
                    continue;
                }

                workViews.Add(ToWorkView(w, DataQuality.Fresh, historical: false));
            }

            foreach (var w in pWorksHist)
            {
                if (string.Equals(Val(w, "role"), "lead", StringComparison.OrdinalIgnoreCase))
                    continue;
                // History-capable form: LastKnown quality; not raised as current active fault.
                workViews.Add(ToWorkView(w, DataQuality.LastKnown, historical: true));
            }

            foreach (var c in pContrib)
            {
                if (IsLeadAcceptanceCard(c))
                    continue;
                var adopted = string.Equals(Val(c, "counts_as_adopted_outcome"), "true", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Val(c, "adoption_state"), "adopted", StringComparison.OrdinalIgnoreCase);
                var hist = string.Equals(Val(c, "historical"), "true", StringComparison.OrdinalIgnoreCase)
                    || adopted
                    || !IsLiveLegion(c);
                workViews.Add(ToWorkView(c, hist ? DataQuality.LastKnown : DataQuality.Fresh, historical: hist));
            }

            var leadView = BuildCurrentLeadView(pid, pLeads, pWorksCurrent, pContrib, workViews);
            if (leadView is not null)
                workViews.Insert(0, leadView);

            var attentions = new List<AttentionItem>();
            foreach (var a in pAttFacts)
            {
                attentions.Add(ToAttentionItem(a));
            }

            // Truth-case derived attentions from work axes (general rules).
            EmitDerivedAttentions(pid, pWorksCurrent, pWorksHist, pContrib, pLeads, attentions, issues, batch.CollectedAt);

            var name = Val(pf, "name") ?? pid;
            var summary = BuildProjectSummary(pf, pWorksCurrent, pWorksHist, attentions, pContrib);
            var phase = ResolveCurrentPhase(pf, pWorksCurrent, pContrib);
            var goal = NullIfUnknown(Val(pf, "goal"));
            var nextStep = NullIfUnknown(Val(pf, "next_step"))
                ?? pWorksCurrent.Select(w => Val(w, "next_step")).FirstOrDefault(s => !IsMissing(s))
                ?? InferNextStep(attentions, pWorksCurrent, pContrib);
            var registryStatus = Val(pf, "registry_status") ?? string.Empty;
            var paused = string.Equals(Val(pf, "paused"), "true", StringComparison.OrdinalIgnoreCase)
                || registryStatus.Equals("PAUSED", StringComparison.OrdinalIgnoreCase);
            var registryActive = registryStatus.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase)
                || registryStatus.Equals("active", StringComparison.OrdinalIgnoreCase);
            var hasCurrentSignal = pWorksCurrent.Count > 0
                || pContrib.Count > 0
                || attentions.Count > 0
                || pLeads.Count > 0;
            var waiting = IsWaitingRegistryStatus(registryStatus);
            var terminal = IsTerminalRegistryStatus(registryStatus);
            if (pf is null)
            {
                if (!hasCurrentSignal) continue;
            }
            else if (!terminal && !registryActive && !paused && !waiting && !hasCurrentSignal)
            {
                continue;
            }

            // Explicit completion stays historical even if old AW/receipts still exist.
            var isActive = !paused && !terminal && (hasCurrentSignal || registryActive);

            var progress = BuildProgress(pf, pContrib, pWorksCurrent);
            var targets = BuildTargets(pid, pf, pWorksCurrent);
            var lastFactAt = MaxFactAt(
                new[] { pf }.Where(x => x is not null).Cast<SourceFact>()
                    .Concat(pWorksCurrent).Concat(pWorksHist).Concat(pContrib).Concat(pAttFacts));

            var quality = conflicting ? DataQuality.Conflicting : DataQuality.Fresh;
            if (pWorksHist.Count > 0 && pWorksCurrent.All(w => !IsFailed(Val(w, "execution_state"))))
            {
                // Historical retained; current may still be Fresh.
            }

            projects.Add(new ProjectView(
                pid,
                name,
                summary,
                phase ?? "unknown",
                goal,
                nextStep,
                isActive,
                paused,
                progress,
                workViews,
                attentions,
                targets,
                quality,
                lastFactAt));
        }

        // Unassociated synthetic bucket only as issues/attention — do NOT invent a project.
        foreach (var u in unassociated.DistinctBy(x => x.EntityId))
        {
            var already = attentionFacts.Any(a =>
                a.EntityId.Contains(u.EntityId, StringComparison.Ordinal)
                || (a.Links.TryGetValue("parent_work_id", out var pw) && pw == u.EntityId));
            if (!already)
            {
                // Surface via snapshot-level issue already added; optional Secretary attention without project.
                // Contract ProjectView requires ProjectId — leave as Issues only.
            }
        }

        // Preserve previous projects as LastKnown when useful on gaps / conflicts.
        if (previous is not null && (conflicting || issues.Any(i =>
                i.Code.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
                || i.Code.Contains("missing", StringComparison.OrdinalIgnoreCase)
                || i.Code.Contains("fail", StringComparison.OrdinalIgnoreCase))))
        {
            foreach (var prev in previous.Projects)
            {
                if (projects.Any(p => p.Id == prev.Id)) continue;
                projects.Add(prev with
                {
                    Quality = DataQuality.LastKnown,
                    Summary = StripStaleFreshnessPrefixes(prev.Summary),
                    IsActive = false
                });
            }

            // Mark overlapping projects that lost current evidence as LastKnown merge.
            for (var i = 0; i < projects.Count; i++)
            {
                var cur = projects[i];
                var prevMatch = previous.Projects.FirstOrDefault(p => p.Id == cur.Id);
                if (prevMatch is null) continue;
                if (conflicting)
                {
                    projects[i] = cur with { Quality = DataQuality.Conflicting };
                }
            }
        }

        var routes = BuildRoutes(routeFacts);
        var materialGap = issues.Any(i =>
            !IsHistoricalOnlyIssue(i)
            && (i.Code.Contains("conflict", StringComparison.OrdinalIgnoreCase)
                || i.Code.Equals("missing", StringComparison.OrdinalIgnoreCase)
                || i.Code.Contains("unassociated", StringComparison.OrdinalIgnoreCase)
                || i.Code.Contains("parse_error", StringComparison.OrdinalIgnoreCase)
                || i.Code.Contains("registry_projects_missing", StringComparison.OrdinalIgnoreCase)
                || i.Code.Equals("consumed_identity_still_live", StringComparison.OrdinalIgnoreCase)
                || i.Code.Equals("empty_file", StringComparison.OrdinalIgnoreCase)
                || i.Code.Equals("read_failed", StringComparison.OrdinalIgnoreCase)));
        var snapQuality = conflicting
            ? DataQuality.Conflicting
            : issues.Count > 0 && projects.Count == 0
                ? DataQuality.Unavailable
                : materialGap && !conflicting
                    ? DataQuality.Fresh
                    : DataQuality.Fresh;

        return new CockpitSnapshot(batch.CollectedAt, projects, issues, routes, snapQuality);
    }

    private static CockpitSnapshot ProjectEmptyOrFailed(
        DateTimeOffset collectedAt,
        List<SourceIssue> issues,
        CockpitSnapshot? previous,
        DataQuality quality)
    {
        var projects = new List<ProjectView>();
        if (previous is not null)
        {
            foreach (var p in previous.Projects)
            {
                projects.Add(p with
                {
                    Quality = DataQuality.LastKnown,
                    Summary = StripStaleFreshnessPrefixes(p.Summary),
                    IsActive = false
                });
            }
        }

        return new CockpitSnapshot(collectedAt, projects, issues,
            previous?.Routes ?? Array.Empty<RouteCoverage>(),
            quality == DataQuality.Fresh ? DataQuality.Unavailable : quality);
    }

    // --- supersede / consume indexing ---

    private static Dictionary<string, HashSet<string>> BuildSupersedeIndex(List<SourceFact> works)
    {
        // key = old line/direct job id or parent_work_id → set of successor entity ids
        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var w in works)
        {
            void Add(string? key)
            {
                if (string.IsNullOrWhiteSpace(key)) return;
                if (!map.TryGetValue(key, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    map[key] = set;
                }
                set.Add(w.EntityId);
            }

            if (w.Links.TryGetValue("supersedes", out var sup)) Add(sup);
            if (w.Links.TryGetValue("parent_work_id", out var parent))
            {
                var handling = Val(w, "lead_handling_state");
                if (IsConsumedOrHandled(handling) || HasSuccessorMarker(w))
                {
                    Add(parent);
                }
            }
        }

        return map;
    }

    private static HashSet<string> BuildConsumedKeySet(
        List<SourceFact> works,
        Dictionary<string, HashSet<string>> supersedeMap)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in supersedeMap.Keys) keys.Add(k);

        foreach (var w in works)
        {
            var handling = Val(w, "lead_handling_state");
            if (!IsConsumedHistorical(handling) && !IsHistoricalFlag(w)) continue;

            if (w.Links.TryGetValue("line_job_id", out var line)) keys.Add(line);
            if (w.Links.TryGetValue("direct_job_id", out var direct)) keys.Add(direct);
            keys.Add(w.EntityId);
            // Also strip common entity prefixes for matching.
            if (w.EntityId.StartsWith("work:line:", StringComparison.Ordinal))
                keys.Add(w.EntityId["work:line:".Length..]);
            if (w.EntityId.StartsWith("work:direct:", StringComparison.Ordinal))
                keys.Add(w.EntityId["work:direct:".Length..]);
        }

        return keys;
    }

    private static bool HasSuccessorMarker(SourceFact w)
    {
        var h = Val(w, "lead_handling_state");
        return h is not null && (
            h.Contains("successor", StringComparison.OrdinalIgnoreCase)
            || h.Contains("consumed_old", StringComparison.OrdinalIgnoreCase)
            || h.Equals("consumed|superseded|handled", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsConsumedOrHandled(string? handling) =>
        !string.IsNullOrWhiteSpace(handling)
        && (handling.Contains("consumed_old_terminal", StringComparison.OrdinalIgnoreCase)
            || handling.Contains("superseded", StringComparison.OrdinalIgnoreCase)
            || handling.Equals("handled", StringComparison.OrdinalIgnoreCase)
            || handling.Equals("consumed_old_terminal_current_successor", StringComparison.OrdinalIgnoreCase));

    private static bool IsConsumedHistorical(string? handling) =>
        !string.IsNullOrWhiteSpace(handling)
        && (handling.Contains("consumed_old_terminal", StringComparison.OrdinalIgnoreCase)
            || handling.Contains("superseded", StringComparison.OrdinalIgnoreCase)
            || handling.Equals("consumed", StringComparison.OrdinalIgnoreCase)
            || handling.Equals("handled", StringComparison.OrdinalIgnoreCase));

    private static bool IsHistoricalFlag(SourceFact w) =>
        string.Equals(Val(w, "historical"), "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Val(w, "current_active_fault"), "false", StringComparison.OrdinalIgnoreCase)
        || (Val(w, "lead_handling_state")?.Contains("cancelled_archived", StringComparison.OrdinalIgnoreCase) == true);

    private static bool IsWorkConsumed(
        SourceFact w,
        Dictionary<string, HashSet<string>> supersedeMap,
        HashSet<string> consumedKeys)
    {
        if (IsHistoricalFlag(w)) return true;
        var handling = Val(w, "lead_handling_state");
        if (IsConsumedHistorical(handling) && !handling!.Contains("successor", StringComparison.OrdinalIgnoreCase)
            && !handling.Contains("current_successor", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (w.Links.TryGetValue("line_job_id", out var line) && consumedKeys.Contains(line) && supersedeMap.ContainsKey(line))
            return true;
        if (w.Links.TryGetValue("direct_job_id", out var direct) && consumedKeys.Contains(direct) && supersedeMap.ContainsKey(direct))
            return true;

        // Entity id matched as superseded target.
        if (w.EntityId.StartsWith("work:line:", StringComparison.Ordinal))
        {
            var id = w.EntityId["work:line:".Length..];
            if (supersedeMap.ContainsKey(id)) return true;
        }
        if (w.EntityId.StartsWith("work:direct:", StringComparison.Ordinal))
        {
            var id = w.EntityId["work:direct:".Length..];
            if (supersedeMap.ContainsKey(id)) return true;
        }

        if (supersedeMap.ContainsKey(w.EntityId)) return true;

        return false;
    }

    private static void ClassifyHistorical(
        List<SourceFact> works,
        Dictionary<string, HashSet<string>> supersedeMap,
        HashSet<string> consumedKeys,
        out List<SourceFact> current,
        out List<SourceFact> historical)
    {
        current = new List<SourceFact>();
        historical = new List<SourceFact>();

        foreach (var w in works)
        {
            var handling = Val(w, "lead_handling_state");
            // Successor after consume stays CURRENT.
            if (handling is not null && handling.Contains("current_successor", StringComparison.OrdinalIgnoreCase))
            {
                current.Add(w);
                continue;
            }

            if (handling is not null
                && handling.Contains("cancelled_archived", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Val(w, "process_state"), "alive", StringComparison.OrdinalIgnoreCase))
            {
                historical.Add(w);
                continue;
            }

            if (IsWorkConsumed(w, supersedeMap, consumedKeys))
            {
                historical.Add(w);
                continue;
            }

            current.Add(w);
        }
    }

    /// <summary>
    /// Deduplicate line+direct that bind the same job pair — keep one merged fact.
    /// Independent concurrent works (different bindings) are retained.
    /// </summary>
    private static List<SourceFact> DeduplicateWorks(List<SourceFact> works)
    {
        var result = new List<SourceFact>();
        var byLine = new Dictionary<string, int>(StringComparer.Ordinal);
        var byDirect = new Dictionary<string, int>(StringComparer.Ordinal);
        var byAccept = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in works)
        {
            w.Links.TryGetValue("line_job_id", out var line);
            w.Links.TryGetValue("direct_job_id", out var direct);
            var accept = AcceptanceDir(w);
            var idx = -1;
            var found = (!string.IsNullOrWhiteSpace(line) && byLine.TryGetValue(line, out idx))
                        || (!string.IsNullOrWhiteSpace(direct) && byDirect.TryGetValue(direct!, out idx))
                        || (!string.IsNullOrWhiteSpace(accept) && byAccept.TryGetValue(accept!, out idx));
            if (found)
            {
                result[idx] = MergeFacts(result[idx], w);
            }
            else
            {
                idx = result.Count;
                result.Add(w);
            }

            if (!string.IsNullOrWhiteSpace(line)) byLine[line] = idx;
            if (!string.IsNullOrWhiteSpace(direct)) byDirect[direct!] = idx;
            if (!string.IsNullOrWhiteSpace(accept)) byAccept[accept!] = idx;
        }

        return result;
    }

    private static string? AcceptanceDir(SourceFact w)
    {
        foreach (var ev in w.Evidence)
        {
            var loc = (ev.Location ?? ev.SourceId ?? "").Replace('/', '\\');
            var idx = loc.LastIndexOf("\\acceptance\\", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            return loc[..(idx + "\\acceptance".Length)];
        }

        return null;
    }

    private static SourceFact MergeFacts(SourceFact a, SourceFact b)
    {
        var links = new Dictionary<string, string>(a.Links, StringComparer.Ordinal);
        foreach (var kv in b.Links)
        {
            if (!links.ContainsKey(kv.Key)) links[kv.Key] = kv.Value;
        }

        var values = CloneValues(a.Values);
        foreach (var key in AxisKeys.Concat(new[]
                 {
                     "summary", "role", "route", "stage", "name", "paused", "historical",
                     "current_active_fault", "mailbox_waiting", "mailbox_implicit",
                     "completed", "total", "progress_basis", "failure_code", "pid",
                     "start_time_utc_ticks", "attention_owner", "attention_reason",
                     "authorized_current", "contribution_item", "counts_as_adopted_outcome",
                     "contribution_id", "registry_pointer_lag", "next_step", "current_handler",
                     "execution_blocker", "remaining_repair_dispatched", "user_decision_required",
                     "actor_name", "task_name", "model", "effort", "blocker", "review_assigned",
                     "lead_source_kind", "source_adopted", "lead_run_id"
                 }))
        {
            var av = values[key]?.GetValue<string>();
            var bv = b.Values[key]?.GetValue<string>();
            if (IsMissing(av) && !IsMissing(bv))
            {
                values[key] = bv;
            }
            else if (!IsMissing(av) && !IsMissing(bv) && !string.Equals(av, bv, StringComparison.Ordinal))
            {
                // Prefer more specific non-unknown; prefer failed over unknown; prefer succeeded over returned.
                values[key] = PreferAxis(av!, bv!);
            }
        }

        var evidence = a.Evidence.Concat(b.Evidence).ToList();
        var projectId = a.ProjectId ?? b.ProjectId;
        // Prefer entity that looks like active/successor.
        var entityId = PreferEntity(a, b);
        return new SourceFact(a.Kind, entityId, projectId, links, values, evidence);
    }

    private static string PreferEntity(SourceFact a, SourceFact b)
    {
        var ha = Val(a, "lead_handling_state");
        var hb = Val(b, "lead_handling_state");
        if (ha is not null && ha.Contains("successor", StringComparison.OrdinalIgnoreCase)) return a.EntityId;
        if (hb is not null && hb.Contains("successor", StringComparison.OrdinalIgnoreCase)) return b.EntityId;
        if (IsHistoricalFlag(a) && !IsHistoricalFlag(b)) return b.EntityId;
        if (IsHistoricalFlag(b) && !IsHistoricalFlag(a)) return a.EntityId;
        return a.EntityId;
    }

    private static string PreferAxis(string a, string b)
    {
        if (a == "unknown") return b;
        if (b == "unknown") return a;
        if (IsLiveHandlerAxis(a) && !IsLiveHandlerAxis(b)) return a;
        if (IsLiveHandlerAxis(b) && !IsLiveHandlerAxis(a)) return b;
        if (IsHandledAxis(a) && !IsHandledAxis(b)) return a;
        if (IsHandledAxis(b) && !IsHandledAxis(a)) return b;
        if (a == "failed" || b == "failed") return "failed";
        if (a == "succeeded" || b == "succeeded") return a == "succeeded" ? a : b;
        if (a.Contains("consumed", StringComparison.OrdinalIgnoreCase)) return a;
        if (b.Contains("consumed", StringComparison.OrdinalIgnoreCase)) return b;
        return a;
    }

    private static bool IsLiveHandlerAxis(string s) =>
        s.Equals("blocked_after_acceptance", StringComparison.OrdinalIgnoreCase)
        || s.Equals("lead_accepting", StringComparison.OrdinalIgnoreCase)
        || s.Equals("repair_dispatched", StringComparison.OrdinalIgnoreCase)
        || s.Equals("acceptance_in_progress", StringComparison.OrdinalIgnoreCase);

    private static bool IsHandledAxis(string s) =>
        s.Contains("lead_handled", StringComparison.OrdinalIgnoreCase)
        || s.StartsWith("handled", StringComparison.OrdinalIgnoreCase)
        || s.Contains("fail_repair", StringComparison.OrdinalIgnoreCase)
        || s.Equals("content_not_accepted", StringComparison.OrdinalIgnoreCase);

    // --- derived attentions (truth case rules) ---

    private static void EmitDerivedAttentions(
        string projectId,
        List<SourceFact> currentWorks,
        List<SourceFact> historicalWorks,
        List<SourceFact> contrib,
        List<SourceFact> leads,
        List<AttentionItem> attentions,
        List<SourceIssue> issues,
        DateTimeOffset collectedAt)
    {
        var existingIds = new HashSet<string>(attentions.Select(a => a.Id), StringComparer.Ordinal);

        // Rule 1/2: failed terminal without handling/successor → Lead attention 关联/处理状态待核
        foreach (var w in currentWorks.Concat(historicalWorks))
        {
            if (!IsFailed(Val(w, "execution_state"))) continue;

            var handling = Val(w, "lead_handling_state");
            if (handling is not null && handling.Contains("cancelled_archived", StringComparison.OrdinalIgnoreCase))
                continue;
            var consumed = IsWorkClearlyHandled(w, historicalWorks, currentWorks);
            if (consumed) continue;

            // If already historical via consume, skip; otherwise if failed and handling missing → attention
            if (IsMissing(handling) || handling == "unknown")
            {
                // Only raise if NOT already classified as properly consumed historical with successor.
                var hasSuccessor = HasLinkedSuccessor(w, currentWorks);
                if (!hasSuccessor)
                {
                    var id = "attention:handling_unknown:" + w.EntityId;
                    if (existingIds.Add(id))
                    {
                        attentions.Add(new AttentionItem(
                            id,
                            AttentionOwner.Lead,
                            "关联/处理状态待核",
                            "failed_terminal_without_lead_handling_or_successor",
                            BuildWorkTargets(projectId, w),
                            w.Evidence));
                    }
                }
            }
        }

        // Rule 1: stale mailbox waiting=true alone must NOT raise current new fault when consumed.
        foreach (var w in currentWorks.ToList())
        {
            var waiting = string.Equals(Val(w, "mailbox_waiting"), "true", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(Val(w, "callback_state"), "waiting_mailbox", StringComparison.OrdinalIgnoreCase);
            if (!waiting) continue;

            w.Links.TryGetValue("line_job_id", out var line);
            w.Links.TryGetValue("direct_job_id", out var direct);
            var isOldConsumed = historicalWorks.Any(h =>
                (line is not null && h.Links.TryGetValue("line_job_id", out var hl) && hl == line)
                || (direct is not null && h.Links.TryGetValue("direct_job_id", out var hd) && hd == direct)
                || IsConsumedHistorical(Val(h, "lead_handling_state")));

            // Also: if this work itself is the old line that has a superseding successor in current.
            var supersededBySuccessor = currentWorks.Any(c =>
                c.Links.TryGetValue("supersedes", out var sup)
                && ((line is not null && (sup == line || c.Links.TryGetValue("line_job_id", out _) && sup == line))
                    || (direct is not null && sup == direct)));

            if (isOldConsumed || supersededBySuccessor || IsWorkClearlyHandled(w, historicalWorks, currentWorks))
            {
                // Demote: do not create fault attention for mailbox waiting alone.
                continue;
            }
        }

        // Rule 3: LeadHandling says handled/consumed but process_state still alive for exact identity → residual conflict
        foreach (var w in currentWorks.Concat(historicalWorks))
        {
            var handling = Val(w, "lead_handling_state");
            var process = Val(w, "process_state");
            var cancelledButLive = handling is not null
                && handling.Contains("cancelled_archived", StringComparison.OrdinalIgnoreCase)
                && string.Equals(process, "alive", StringComparison.OrdinalIgnoreCase);
            if (!cancelledButLive && !IsConsumedOrHandled(handling)) continue;
            if (!string.Equals(process, "alive", StringComparison.OrdinalIgnoreCase)) continue;

            var id = "attention:residual_process:" + w.EntityId;
            if (existingIds.Add(id))
            {
                attentions.Add(new AttentionItem(
                    id,
                    AttentionOwner.Lead,
                    "残留进程/处理冲突：已处理身份仍存活",
                    "consumed_identity_still_live",
                    BuildWorkTargets(projectId, w),
                    w.Evidence));
            }
        }

        // Also from issues
        foreach (var issue in issues.Where(i =>
                     i.Code.Equals("consumed_identity_still_live", StringComparison.OrdinalIgnoreCase)))
        {
            var id = "attention:issue:" + issue.Code + ":" + issue.SourceId;
            if (existingIds.Add(id))
            {
                attentions.Add(new AttentionItem(
                    id,
                    AttentionOwner.Lead,
                    "残留进程/处理冲突",
                    issue.Detail,
                    Array.Empty<NavigationTarget>(),
                    Array.Empty<EvidenceRef>()));
            }
        }

        // Rule 4: successful transport/receipt alone → Acceptance/Lead pending, not PASS.
        // Already-handled quota/content-not-accepted stays visible on historical works;
        // "未见原Lead验收" is current-only.
        var currentEntityIds = new HashSet<string>(currentWorks.Select(w => w.EntityId), StringComparer.Ordinal);
        var awCurrentHandler = currentWorks.Any(w =>
            w.Evidence.Any(e => e.Kind.Equals("active_work", StringComparison.OrdinalIgnoreCase))
            && (string.Equals(Val(w, "lead_handling_state"), "blocked_after_acceptance", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Val(w, "lead_handling_state"), "lead_accepting", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Val(w, "lead_handling_state"), "repair_dispatched", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Val(w, "acceptance_state"), "acceptance_in_progress", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Val(w, "execution_state"), "active", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Val(w, "execution_state"), "running", StringComparison.OrdinalIgnoreCase)));
        foreach (var w in currentWorks.Concat(historicalWorks))
        {
            var transport = Val(w, "transport_state");
            var acceptance = Val(w, "acceptance_state");
            var goal = Val(w, "goal_state");
            var handling = Val(w, "lead_handling_state");
            var liveHandler = string.Equals(handling, "blocked_after_acceptance", StringComparison.OrdinalIgnoreCase)
                || string.Equals(handling, "lead_accepting", StringComparison.OrdinalIgnoreCase)
                || string.Equals(handling, "repair_dispatched", StringComparison.OrdinalIgnoreCase)
                || string.Equals(acceptance, "acceptance_in_progress", StringComparison.OrdinalIgnoreCase);
            var alreadyHandled = !IsMissing(acceptance)
                && (acceptance!.StartsWith("handled", StringComparison.OrdinalIgnoreCase)
                    || acceptance.Contains("fail_repair", StringComparison.OrdinalIgnoreCase)
                    || acceptance.Contains("partial_adopted", StringComparison.OrdinalIgnoreCase)
                    || acceptance.Equals("content_not_accepted", StringComparison.OrdinalIgnoreCase));
            var handlingDone = !IsMissing(handling)
                && (handling!.Contains("lead_handled", StringComparison.OrdinalIgnoreCase)
                    || handling.Contains("quota", StringComparison.OrdinalIgnoreCase));
            var transportOrReturn =
                string.Equals(transport, "complete", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Val(w, "execution_state"), "succeeded", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Val(w, "execution_state"), "returned", StringComparison.OrdinalIgnoreCase);
            if (!alreadyHandled && !handlingDone && !transportOrReturn)
                continue;

            var acceptancePending = !alreadyHandled && !handlingDone
                && (IsMissing(acceptance) || acceptance is "unknown" or "pending");
            var noProductPass = IsMissing(goal) || goal is "unknown" or "not_complete";
            // Army/contribution AR adopt is not Lead acceptance of this execution receipt.
            var thisWorkAccepted = alreadyHandled || handlingDone || (!IsMissing(acceptance)
                && (acceptance!.Contains("accept", StringComparison.OrdinalIgnoreCase)
                    || acceptance.Contains("已验收", StringComparison.Ordinal)));
            var linkedLeadAcceptance = contrib.Any(c =>
                SameWorkBinding(c, w)
                && (string.Equals(Val(c, "acceptance_state"), "accepted_partial_or_full", StringComparison.OrdinalIgnoreCase)
                    || (Val(c, "acceptance_state")?.Contains("accept", StringComparison.OrdinalIgnoreCase) == true
                        && Val(c, "acceptance_state")?.Contains("pending", StringComparison.OrdinalIgnoreCase) != true)));

            if (alreadyHandled || handlingDone)
            {
                var failRepair = (acceptance ?? "").Contains("fail_repair", StringComparison.OrdinalIgnoreCase)
                                 || (handling ?? "").Contains("fail_repair", StringComparison.OrdinalIgnoreCase);
                var quota = (handling ?? "").Contains("quota", StringComparison.OrdinalIgnoreCase)
                            || (acceptance ?? "").Equals("content_not_accepted", StringComparison.OrdinalIgnoreCase);
                var resumed = (handling ?? "").Contains("resumed", StringComparison.OrdinalIgnoreCase);
                var id = "attention:lead_handled:" + w.EntityId;
                if (existingIds.Add(id))
                {
                    var text = failRepair
                        ? "已处理待修复：原Lead已给出失败/返修结论"
                        : quota && resumed
                            ? "已处理（额度失败，资源已另派）；内容未验收"
                            : quota
                                ? "已处理（额度失败）；内容未验收"
                                : "已处理：原Lead已给出部分采用或处理结论";
                    attentions.Add(new AttentionItem(
                        id,
                        AttentionOwner.Lead,
                        text,
                        failRepair ? "lead_handled_fail_repair"
                            : quota ? "lead_handled_quota" : "lead_handled_result",
                        BuildWorkTargets(projectId, w),
                        w.Evidence));
                }
            }
            else if (currentEntityIds.Contains(w.EntityId)
                     && !liveHandler
                     && string.Equals(Val(w, "registry_pointer_lag"), "true", StringComparison.OrdinalIgnoreCase) == false
                     && !(awCurrentHandler && !w.Evidence.Any(e => e.Kind.Equals("active_work", StringComparison.OrdinalIgnoreCase)))
                     && acceptancePending && noProductPass && !thisWorkAccepted && !linkedLeadAcceptance)
            {
                var id = "attention:pending_acceptance:" + w.EntityId;
                if (existingIds.Add(id))
                {
                    attentions.Add(new AttentionItem(
                        id,
                        AttentionOwner.Lead,
                        "待Lead处理/待验收：当前回执仅证明返回，未见原Lead验收",
                        "transport_or_receipt_without_lead_acceptance",
                        BuildWorkTargets(projectId, w),
                        w.Evidence));
                }
            }

            // Never invent goal PASS from transport.
            _ = handling;
        }

        // Map attention_owner from contribution facts that look like attention
        foreach (var c in contrib)
        {
            var ownerRaw = Val(c, "attention_owner");
            if (ownerRaw is null) continue;
            var id = "attention:from:" + c.EntityId;
            if (existingIds.Add(id))
            {
                attentions.Add(new AttentionItem(
                    id,
                    ParseOwner(ownerRaw),
                    Val(c, "summary") ?? Val(c, "attention_reason") ?? "attention",
                    Val(c, "attention_reason") ?? "from_fact",
                    BuildWorkTargets(projectId, c),
                    c.Evidence));
            }
        }
    }

    private static bool IsWorkClearlyHandled(
        SourceFact w,
        List<SourceFact> historical,
        List<SourceFact> current)
    {
        var handling = Val(w, "lead_handling_state");
        var acceptance = Val(w, "acceptance_state");
        if ((handling ?? string.Empty).Contains("lead_handled", StringComparison.OrdinalIgnoreCase)
            || (handling ?? string.Empty).Contains("quota", StringComparison.OrdinalIgnoreCase)
            || (acceptance ?? string.Empty).StartsWith("handled", StringComparison.OrdinalIgnoreCase)
            || string.Equals(acceptance, "content_not_accepted", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IsConsumedHistorical(handling) || IsHistoricalFlag(w))
        {
            // Handled historically only counts if there is successor OR explicit consumed_old_terminal
            if (IsConsumedHistorical(handling)) return true;
            if (HasLinkedSuccessor(w, current)) return true;
        }

        w.Links.TryGetValue("line_job_id", out var line);
        w.Links.TryGetValue("direct_job_id", out var direct);
        foreach (var c in current)
        {
            if (c.Links.TryGetValue("supersedes", out var sup))
            {
                if (line is not null && (sup == line || w.EntityId.EndsWith(sup, StringComparison.Ordinal))) return true;
                if (direct is not null && sup == direct) return true;
                if (sup == w.EntityId) return true;
            }
        }

        return historical.Any(h =>
            string.Equals(h.EntityId, w.EntityId, StringComparison.Ordinal)
            && IsConsumedHistorical(Val(h, "lead_handling_state")));
    }

    private static bool HasLinkedSuccessor(SourceFact w, List<SourceFact> current)
    {
        w.Links.TryGetValue("line_job_id", out var line);
        w.Links.TryGetValue("direct_job_id", out var direct);
        foreach (var c in current)
        {
            if (!c.Links.TryGetValue("supersedes", out var sup)) continue;
            if (line is not null && sup == line) return true;
            if (direct is not null && sup == direct) return true;
            if (sup == w.EntityId) return true;
            if (w.EntityId.EndsWith(":" + sup, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    // --- view builders ---

    private static WorkView ToWorkView(SourceFact f, DataQuality quality, bool historical)
    {
        var axes = new WorkAxes(
            AxisOrUnknown(Val(f, "process_state")),
            AxisOrUnknown(Val(f, "turn_state")),
            AxisOrUnknown(Val(f, "execution_state")),
            AxisOrUnknown(Val(f, "transport_state")),
            AxisOrUnknown(Val(f, "delivery_state")),
            AxisOrUnknown(Val(f, "callback_state")),
            AxisOrUnknown(Val(f, "lead_handling_state")),
            AxisOrUnknown(Val(f, "acceptance_state")),
            AxisOrUnknown(Val(f, "adoption_state")),
            AxisOrUnknown(Val(f, "goal_state")));

        var role = AxisOrUnknown(Val(f, "role"));
        var route = NullIfUnknown(Val(f, "route"));
        var summary = Val(f, "summary") ?? f.EntityId;
        if (historical && !summary.Contains("historical", StringComparison.OrdinalIgnoreCase))
        {
            summary = "[historical] " + summary;
        }

        var artifacts = BuildArtifacts(f);
        var targets = string.IsNullOrWhiteSpace(f.ProjectId)
            ? Array.Empty<NavigationTarget>()
            : BuildWorkTargets(f.ProjectId!, f);

        var q = historical ? DataQuality.LastKnown : quality;
        return new WorkView(
            f.EntityId,
            role,
            route,
            summary,
            axes,
            artifacts,
            targets,
            f.Evidence,
            q,
            NullIfUnknown(Val(f, "actor_name")),
            NullIfUnknown(Val(f, "task_name")),
            NullIfUnknown(Val(f, "model")),
            NullIfUnknown(Val(f, "effort")),
            NullIfUnknown(Val(f, "blocker") ?? Val(f, "execution_blocker")),
            ActorKindFromRole(role, historical),
            NullIfUnknown(Val(f, "review_assigned")));
    }

    private static bool IsLeadAcceptanceCard(SourceFact f)
    {
        var role = Val(f, "role") ?? string.Empty;
        if (role.Contains("lead_acceptance", StringComparison.OrdinalIgnoreCase)) return true;
        return f.Kind.Equals("acceptance", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Val(f, "contribution_item"), "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLiveLegion(SourceFact f)
    {
        var role = Val(f, "role") ?? string.Empty;
        if (!role.Contains("army", StringComparison.OrdinalIgnoreCase)
            && !role.Contains("contrib", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(Val(f, "counts_as_adopted_outcome"), "true", StringComparison.OrdinalIgnoreCase))
            return false;
        var exec = Val(f, "execution_state");
        return string.Equals(exec, "active", StringComparison.OrdinalIgnoreCase)
            || string.Equals(exec, "running", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Val(f, "adoption_state"), "in_progress", StringComparison.OrdinalIgnoreCase);
    }

    private static string ActorKindFromRole(string role, bool historical)
    {
        if (historical) return "history";
        if (role.Contains("generation", StringComparison.OrdinalIgnoreCase))
            return "progress";
        if (role.Contains("review", StringComparison.OrdinalIgnoreCase)
            || role.Contains("审核", StringComparison.Ordinal))
            return "reviewer";
        if (role.Contains("army", StringComparison.OrdinalIgnoreCase)
            || role.Contains("contrib", StringComparison.OrdinalIgnoreCase)
            || role.Contains("军团", StringComparison.Ordinal))
            return "legion";
        if (role.Equals("lead", StringComparison.OrdinalIgnoreCase)
            || role.Contains("负责人", StringComparison.Ordinal))
            return "lead";
        if (role.Contains("exec", StringComparison.OrdinalIgnoreCase))
            return "executor";
        return "unknown";
    }

    /// <summary>
    /// One current owner row. Never dump every collected lead session as extra owners.
    /// </summary>
    private static WorkView? BuildCurrentLeadView(
        string projectId,
        List<SourceFact> pLeads,
        List<SourceFact> currentWorks,
        List<SourceFact> contrib,
        List<WorkView> already)
    {
        if (already.Any(w => string.Equals(w.ActorKind, "lead", StringComparison.OrdinalIgnoreCase)
                             && w.Quality != DataQuality.LastKnown))
        {
            return null;
        }

        var currentSessions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var w in currentWorks)
        {
            if (w.Links.TryGetValue("lead_session_id", out var sid) && !string.IsNullOrWhiteSpace(sid))
                currentSessions.Add(sid);
        }

        var ranked = pLeads
            .Select(l => (Lead: l, Score: LeadIdentityScore(l, currentSessions)))
            .OrderByDescending(x => x.Score)
            .ToList();
        var chosen = ranked.FirstOrDefault(x => x.Score > 0).Lead
                     ?? ranked.FirstOrDefault().Lead;

        if (chosen is null)
        {
            var handlingWork = currentWorks.FirstOrDefault(w =>
                Val(w, "lead_handling_state") is "lead_accepting" or "repair_dispatched" or "blocked_after_acceptance" or "callback_wait"
                || Val(w, "acceptance_state") is "acceptance_in_progress" or "handled_fail_repair");
            if (handlingWork is not null)
            {
                var synthetic = CloneValues(handlingWork.Values);
                synthetic["role"] = "lead";
                synthetic["turn_state"] = "unknown";
                synthetic.Remove("model");
                synthetic.Remove("effort");
                synthetic.Remove("actor_name");
                chosen = new SourceFact(
                    "lead",
                    "lead:from-work:" + handlingWork.EntityId,
                    projectId,
                    handlingWork.Links,
                    synthetic,
                    handlingWork.Evidence);
            }
        }

        if (chosen is null)
            return null;

        var folded = CloneValues(chosen.Values);
        folded["role"] = "lead";
        chosen.Links.TryGetValue("lead_session_id", out var chosenSession);
        foreach (var lead in pLeads)
        {
            lead.Links.TryGetValue("lead_session_id", out var sid);
            if (!string.IsNullOrWhiteSpace(chosenSession)
                && !string.Equals(sid, chosenSession, StringComparison.Ordinal))
            {
                continue;
            }

            if (IsMissing(ValFrom(folded, "model")) && !IsMissing(Val(lead, "model")))
                folded["model"] = Val(lead, "model");
            if (IsMissing(ValFrom(folded, "effort")) && !IsMissing(Val(lead, "effort")))
                folded["effort"] = Val(lead, "effort");
            if (IsMissing(ValFrom(folded, "actor_name")) && !IsMissing(Val(lead, "actor_name")))
                folded["actor_name"] = Val(lead, "actor_name");
        }

        var authorized = currentWorks.Where(w =>
            string.Equals(Val(w, "authorized_current"), "true", StringComparison.OrdinalIgnoreCase)).ToList();
        var acceptedSources = authorized.Count > 0 ? authorized : currentWorks;
        var comparisonOpen = currentWorks.Any(w =>
            string.Equals(Val(w, "comparison_product_pass"), "false", StringComparison.OrdinalIgnoreCase));
        var currentInFlight = acceptedSources.Any(w =>
        {
            var exec = Val(w, "execution_state") ?? string.Empty;
            return exec.Equals("active", StringComparison.OrdinalIgnoreCase)
                   || exec.Equals("running", StringComparison.OrdinalIgnoreCase);
        });

        foreach (var w in currentWorks.OrderBy(w =>
                     string.Equals(Val(w, "role"), "lead", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
        {
            var handling = Val(w, "lead_handling_state");
            var acc = Val(w, "acceptance_state");
            if (!IsLiveHandlerFold(handling, acc)
                && !string.Equals(Val(w, "role"), "lead", StringComparison.OrdinalIgnoreCase))
                continue;
            if (IsMissing(ValFrom(folded, "lead_handling_state")) && !IsMissing(handling))
                folded["lead_handling_state"] = handling;
            if (IsMissing(ValFrom(folded, "acceptance_state")) && !IsFoldedAccepted(acc) && !IsMissing(acc))
                folded["acceptance_state"] = acc;
            if (string.Equals(Val(w, "role"), "lead", StringComparison.OrdinalIgnoreCase))
            {
                if (IsMissing(ValFrom(folded, "model")) && !IsMissing(Val(w, "model")))
                    folded["model"] = Val(w, "model");
                if (IsMissing(ValFrom(folded, "effort")) && !IsMissing(Val(w, "effort")))
                    folded["effort"] = Val(w, "effort");
            }
        }

        foreach (var w in acceptedSources)
        {
            var acc = Val(w, "acceptance_state");
            if ((currentInFlight || comparisonOpen) && IsFoldedAccepted(acc))
                continue;
            if (IsMissing(ValFrom(folded, "lead_handling_state")) && !IsMissing(Val(w, "lead_handling_state")))
                folded["lead_handling_state"] = Val(w, "lead_handling_state");
            if (IsMissing(ValFrom(folded, "acceptance_state")) && !IsMissing(acc))
                folded["acceptance_state"] = acc;
        }
        foreach (var c in contrib.Where(IsLeadAcceptanceCard))
        {
            if (string.Equals(Val(c, "contribution_item"), "true", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(Val(c, "counts_as_adopted_outcome"), "true", StringComparison.OrdinalIgnoreCase))
                continue;
            var cRole = Val(c, "role") ?? string.Empty;
            if (cRole.Contains("army", StringComparison.OrdinalIgnoreCase))
                continue;
            c.Links.TryGetValue("line_job_id", out var cLine);
            c.Links.TryGetValue("direct_job_id", out var cDirect);
            if (string.IsNullOrWhiteSpace(cLine) && string.IsNullOrWhiteSpace(cDirect))
                continue;
            if (!acceptedSources.Any(w => MatchesWorkId(w, cLine, cDirect)))
                continue;
            if ((currentInFlight || comparisonOpen) && IsFoldedAccepted(Val(c, "acceptance_state")))
                continue;
            if (IsMissing(ValFrom(folded, "acceptance_state")) && !IsMissing(Val(c, "acceptance_state")))
                folded["acceptance_state"] = Val(c, "acceptance_state");
            if (IsMissing(ValFrom(folded, "lead_handling_state")) && !IsMissing(Val(c, "lead_handling_state")))
                folded["lead_handling_state"] = Val(c, "lead_handling_state");
        }

        var fact = new SourceFact("lead", chosen.EntityId, projectId, chosen.Links, folded, chosen.Evidence);
        return ToWorkView(fact, DataQuality.Fresh, historical: false);
    }

    private static string? ValFrom(JsonObject values, string key) =>
        values[key]?.GetValue<string>();

    private static bool IsLiveHandlerFold(string? handling, string? acceptance)
    {
        var h = handling ?? string.Empty;
        var a = acceptance ?? string.Empty;
        return h.Equals("blocked_after_acceptance", StringComparison.OrdinalIgnoreCase)
               || h.Equals("lead_accepting", StringComparison.OrdinalIgnoreCase)
               || h.Equals("repair_dispatched", StringComparison.OrdinalIgnoreCase)
               || h.Equals("callback_wait", StringComparison.OrdinalIgnoreCase)
               || a.Equals("acceptance_in_progress", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFoldedAccepted(string? acceptance) =>
        !string.IsNullOrWhiteSpace(acceptance)
        && (acceptance.Equals("handled_accepted", StringComparison.OrdinalIgnoreCase)
            || acceptance.Equals("accepted", StringComparison.OrdinalIgnoreCase)
            || acceptance.Equals("accepted_partial_or_full", StringComparison.OrdinalIgnoreCase));

    private static bool MatchesWorkId(SourceFact w, string? line, string? direct)
    {
        if (!string.IsNullOrWhiteSpace(line)
            && (w.EntityId.Contains(line, StringComparison.OrdinalIgnoreCase)
                || (w.Links.TryGetValue("line_job_id", out var l) && string.Equals(l, line, StringComparison.OrdinalIgnoreCase))))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(direct)
            && (w.EntityId.Contains(direct, StringComparison.OrdinalIgnoreCase)
                || (w.Links.TryGetValue("direct_job_id", out var d) && string.Equals(d, direct, StringComparison.OrdinalIgnoreCase))))
        {
            return true;
        }

        return false;
    }

    private static int LeadIdentityScore(SourceFact lead, HashSet<string> currentSessions)
    {
        var score = 0;
        lead.Links.TryGetValue("lead_session_id", out var sid);
        if (!string.IsNullOrWhiteSpace(sid) && currentSessions.Contains(sid))
            score += 8;
        if (!IsMissing(Val(lead, "model")) || !IsMissing(Val(lead, "effort")))
            score += 4;
        var kind = Val(lead, "lead_source_kind");
        if (string.Equals(kind, "lead_run", StringComparison.OrdinalIgnoreCase)
            || lead.Evidence.Any(e => e.Kind.Equals("lead_run", StringComparison.OrdinalIgnoreCase)))
            score += 4;
        if (string.Equals(kind, "host_owner", StringComparison.OrdinalIgnoreCase)
            || lead.Evidence.Any(e => e.Kind.Equals("lead_owner", StringComparison.OrdinalIgnoreCase)))
            score -= 3;
        return score;
    }

    private static void RelocateAdoptedFolderWorks(
        List<SourceFact> current,
        List<SourceFact> historical,
        IEnumerable<SourceFact> markers)
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in markers)
        {
            if (!string.Equals(Val(m, "source_adopted"), "true", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Val(m, "counts_as_adopted_outcome"), "true", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Val(m, "adoption_state"), "adopted", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var dir = AcceptanceDir(m);
            if (!string.IsNullOrWhiteSpace(dir))
                dirs.Add(dir!);
        }

        if (dirs.Count == 0) return;
        for (var i = current.Count - 1; i >= 0; i--)
        {
            var dir = AcceptanceDir(current[i]);
            if (string.IsNullOrWhiteSpace(dir) || !dirs.Contains(dir))
                continue;
            historical.Add(current[i]);
            current.RemoveAt(i);
        }
    }

    private static IReadOnlyList<ArtifactView> BuildArtifacts(SourceFact f)
    {
        var list = new List<ArtifactView>();
        if (f.Values["artifacts"] is JsonArray arr)
        {
            var i = 0;
            foreach (var n in arr)
            {
                var label = n?.GetValue<string>() ?? ("artifact-" + i);
                list.Add(new ArtifactView(f.EntityId + "#a" + i, label, "present", null, f.Evidence));
                i++;
            }
        }

        return list;
    }

    private static AttentionItem ToAttentionItem(SourceFact f)
    {
        var owner = ParseOwner(Val(f, "attention_owner"));
        var desc = Val(f, "summary") ?? Val(f, "attention_reason") ?? "attention";
        var reason = Val(f, "attention_reason") ?? "attention_fact";
        var targets = string.IsNullOrWhiteSpace(f.ProjectId)
            ? Array.Empty<NavigationTarget>()
            : BuildWorkTargets(f.ProjectId!, f);
        return new AttentionItem(f.EntityId, owner, desc, reason, targets, f.Evidence);
    }

    private static AttentionOwner ParseOwner(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return AttentionOwner.None;
        if (raw.Equals("User", StringComparison.OrdinalIgnoreCase)) return AttentionOwner.User;
        if (raw.Equals("Secretary", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("秘书", StringComparison.OrdinalIgnoreCase)) return AttentionOwner.Secretary;
        if (raw.Equals("Lead", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("原Lead", StringComparison.OrdinalIgnoreCase)) return AttentionOwner.Lead;
        return AttentionOwner.None;
    }

    private static ProgressView? BuildProgress(
        SourceFact? project,
        List<SourceFact> contrib,
        List<SourceFact> works)
    {
        foreach (var src in contrib.Concat(works).Append(project).Where(x => x is not null).Cast<SourceFact>())
        {
            var completedRaw = Val(src, "completed");
            var totalRaw = Val(src, "total");
            var basis = Val(src, "progress_basis");
            if (IsMissing(completedRaw) || IsMissing(basis)) continue;
            if (IsDisallowedProgressBasis(basis)) continue;
            if (!int.TryParse(completedRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var completed)) continue;
            var total = 0;
            if (!IsMissing(totalRaw)
                && !int.TryParse(totalRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out total))
            {
                continue;
            }

            if (total <= 0 && !(basis!.Contains("世界", StringComparison.Ordinal) && completed > 0))
                continue;
            return new ProgressView(completed, total, basis!, src.Evidence);
        }

        return null;
    }

    private static bool IsDisallowedProgressBasis(string? basis) =>
        !string.IsNullOrWhiteSpace(basis)
        && (basis.Contains("test", StringComparison.OrdinalIgnoreCase)
            || basis.Contains("turn", StringComparison.OrdinalIgnoreCase)
            || basis.Equals("tests", StringComparison.OrdinalIgnoreCase)
            || basis.Equals("turns", StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<RouteCoverage> BuildRoutes(List<SourceFact> routeFacts)
    {
        var list = new List<RouteCoverage>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in routeFacts)
        {
            var routeId = Val(r, "route") ?? r.EntityId.Replace("route:", "", StringComparison.Ordinal);
            if (!seen.Add(routeId)) continue;
            list.Add(new RouteCoverage(
                routeId,
                AxisOrUnknown(Val(r, "coverage_level")),
                NullIfUnknown(Val(r, "coverage_gap")),
                r.Evidence));
        }

        return list;
    }

    private static IReadOnlyList<NavigationTarget> BuildTargets(string projectId, SourceFact? project, List<SourceFact> works)
    {
        var list = new List<NavigationTarget>();
        if (project is not null && project.Links.TryGetValue("worktree", out var wt) && !string.IsNullOrWhiteSpace(wt))
        {
            list.Add(new NavigationTarget("path", wt, "worktree", projectId, project.EntityId));
        }

        foreach (var w in works.Take(3))
        {
            if (w.Links.TryGetValue("workspace", out var ws) && !string.IsNullOrWhiteSpace(ws))
            {
                list.Add(new NavigationTarget("path", ws, "workspace", projectId, w.EntityId));
            }
        }

        return list;
    }

    private static IReadOnlyList<NavigationTarget> BuildWorkTargets(string projectId, SourceFact w)
    {
        var list = new List<NavigationTarget>();
        if (w.Links.TryGetValue("workspace", out var ws) && !string.IsNullOrWhiteSpace(ws))
        {
            list.Add(new NavigationTarget("path", ws, "workspace", projectId, w.EntityId));
        }
        else if (w.Links.TryGetValue("worktree", out var wt) && !string.IsNullOrWhiteSpace(wt))
        {
            list.Add(new NavigationTarget("path", wt, "worktree", projectId, w.EntityId));
        }

        if (w.Links.TryGetValue("lead_session_id", out var sid) && !string.IsNullOrWhiteSpace(sid))
        {
            list.Add(new NavigationTarget("lead_session", sid, "lead", projectId, w.EntityId));
        }

        return list;
    }

    private static bool IsWaitingRegistryStatus(string status)
    {
        if (string.IsNullOrWhiteSpace(status)) return false;
        var u = status.ToUpperInvariant();
        return u.Contains("WAITING", StringComparison.Ordinal)
            || u.Contains("PENDING", StringComparison.Ordinal)
            || u.Contains("USER", StringComparison.Ordinal);
    }

    private static bool IsTerminalRegistryStatus(string status)
    {
        if (string.IsNullOrWhiteSpace(status)) return false;
        var u = status.ToUpperInvariant();
        return (u.Contains("TERMINAL", StringComparison.Ordinal)
                || u.Contains("COMPLETED", StringComparison.Ordinal)
                || u.Contains("RETIRED", StringComparison.Ordinal)
                || u.Equals("CANCELLED", StringComparison.Ordinal)
                || u.Equals("CANCELED", StringComparison.Ordinal))
            && !IsWaitingRegistryStatus(status);
    }

    internal static string StripStaleFreshnessPrefixes(string? summary)
    {
        var s = summary ?? string.Empty;
        while (true)
        {
            var t = s.Trim();
            if (t.StartsWith("(last-known)", StringComparison.OrdinalIgnoreCase))
            {
                s = t["(last-known)".Length..].TrimStart();
                continue;
            }

            if (t.StartsWith("[historical]", StringComparison.OrdinalIgnoreCase))
            {
                s = t["[historical]".Length..].TrimStart();
                continue;
            }

            return t;
        }
    }

    private static bool LooksLikeTechnicalToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (value.Equals("unknown", StringComparison.OrdinalIgnoreCase)) return true;
        if (value.Contains("adoption_partial", StringComparison.OrdinalIgnoreCase)) return true;
        if (value.Contains("process_observation", StringComparison.OrdinalIgnoreCase)) return true;
        if (value.StartsWith("V23_", StringComparison.OrdinalIgnoreCase)) return true;
        if (value.Contains('_', StringComparison.Ordinal) && value.All(ch => char.IsAscii(ch) || ch is '_' or '-' or '.'))
            return true;
        return false;
    }

    private static string BuildProjectSummary(
        SourceFact? project,
        List<SourceFact> current,
        List<SourceFact> historical,
        List<AttentionItem> attentions,
        List<SourceFact> contrib)
    {
        if (Val(project, "summary") is { } s && !IsMissing(s) && !s.Contains("idle_or_registry", StringComparison.Ordinal)
            && !s.StartsWith("current_works=", StringComparison.Ordinal)
            && !LooksLikeTechnicalToken(s))
        {
            return StripStaleFreshnessPrefixes(s);
        }

        var parts = new List<string>();
        if (current.Count == 0 && contrib.Count == 0)
            parts.Add("本轮没有可读的当前执行或回执");
        else
            parts.Add("当前可读工作 " + current.Count + " 项");
        if (historical.Count > 0) parts.Add("可追溯历史 " + historical.Count + " 项");
        if (attentions.Count > 0) parts.Add("待处理 " + attentions.Count + " 项");
        var adoptedIds = DistinctAdoptedContributionIds(contrib);
        if (adoptedIds.Count > 0)
            parts.Add("已采用贡献 " + adoptedIds.Count + " 份（" + string.Join("、", adoptedIds) + "）");
        return string.Join("；", parts);
    }

    internal static List<string> DistinctAdoptedContributionIds(IEnumerable<SourceFact> contrib)
    {
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in contrib)
        {
            if (!IsCountableContributionItem(c)) continue;
            var adopted = string.Equals(Val(c, "counts_as_adopted_outcome"), "true", StringComparison.OrdinalIgnoreCase)
                          || (Val(c, "adoption_state")?.Contains("adopt", StringComparison.OrdinalIgnoreCase) == true
                              && !string.Equals(Val(c, "counts_as_adopted_outcome"), "false", StringComparison.OrdinalIgnoreCase));
            if (!adopted) continue;
            var id = StableContributionId(c);
            if (seen.Add(id)) ids.Add(id);
        }

        ids.Sort(StringComparer.Ordinal);
        return ids;
    }

    internal static bool IsCountableContributionItem(SourceFact c)
    {
        var role = Val(c, "role") ?? string.Empty;
        if (role.Contains("lead_acceptance", StringComparison.OrdinalIgnoreCase)) return false;
        if (c.Kind == "acceptance") return false;
        if (string.Equals(Val(c, "contribution_item"), "false", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(Val(c, "contribution_item"), "true", StringComparison.OrdinalIgnoreCase)) return true;
        return c.Kind == "contribution"
               || role.Contains("contrib", StringComparison.OrdinalIgnoreCase)
               || role.Contains("army_contribution", StringComparison.OrdinalIgnoreCase);
    }

    internal static string StableContributionId(SourceFact c) =>
        Val(c, "contribution_id") ?? Val(c, "name") ?? c.EntityId;

    private static bool IsHistoricalOnlyIssue(SourceIssue issue)
    {
        if (issue.Code.Equals("historical_source_missing", StringComparison.OrdinalIgnoreCase))
            return true;
        return issue.Detail.Contains("scope=historical", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveCurrentPhase(SourceFact? project, List<SourceFact> current, List<SourceFact> contrib)
    {
        var fromCurrent = FirstCurrentSourceStage(current);
        if (!IsMissing(fromCurrent))
            return fromCurrent!;

        var inferred = InferPhase(current, contrib);
        if (!IsMissing(inferred))
            return inferred!;

        if (current.Any(IsExecutionRole))
            return "进行中";

        var registryStage = Val(project, "stage");
        if (!IsMissing(registryStage) && !LooksLikeTechnicalToken(registryStage))
            return registryStage!;

        return "进行中";
    }

    private static string? FirstCurrentSourceStage(List<SourceFact> current)
    {
        foreach (var w in current.Where(IsExecutionRole))
        {
            if (string.Equals(Val(w, "authorized_current"), "true", StringComparison.OrdinalIgnoreCase)
                && !IsMissing(Val(w, "stage")))
            {
                return Val(w, "stage");
            }
        }

        foreach (var w in current.Where(IsExecutionRole))
        {
            if (!IsMissing(Val(w, "stage")) && Val(w, "stage") is not "unknown")
                return Val(w, "stage");
        }

        return null;
    }

    private static string? InferPhase(List<SourceFact> current, List<SourceFact> contrib)
    {
        _ = contrib;
        if (current.Any(w => string.Equals(Val(w, "lead_handling_state"), "blocked_after_acceptance", StringComparison.OrdinalIgnoreCase)))
            return "结果已交回，负责人已判退修；后续处理被平台限制中断，退修尚未派出";
        if (current.Any(w =>
                string.Equals(Val(w, "lead_handling_state"), "lead_accepting", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Val(w, "acceptance_state"), "acceptance_in_progress", StringComparison.OrdinalIgnoreCase)))
            return "结果已交回，负责人正在验收";
        if (current.Any(w => string.Equals(Val(w, "lead_handling_state"), "repair_dispatched", StringComparison.OrdinalIgnoreCase)))
            return "原执行者正在按退修继续做";
        if (current.Any(w =>
                IsExecutionRole(w)
                && (string.Equals(Val(w, "execution_state"), "active", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Val(w, "execution_state"), "running", StringComparison.OrdinalIgnoreCase))))
            return "执行中";
        if (current.Any(w =>
                (Val(w, "acceptance_state") ?? "").Contains("fail_repair", StringComparison.OrdinalIgnoreCase)
                || (Val(w, "lead_handling_state") ?? "").Contains("fail_repair", StringComparison.OrdinalIgnoreCase)))
        {
            return "结果已交回，负责人已判退修";
        }

        if (current.Any(w =>
                string.Equals(Val(w, "role"), "generation", StringComparison.OrdinalIgnoreCase)
                || !IsMissing(Val(w, "generation_pid"))
                || string.Equals(Val(w, "progress_basis"), "世界已生成天数", StringComparison.Ordinal)))
        {
            return "原执行者继续生成";
        }

        if (current.Any(w => string.Equals(Val(w, "execution_state"), "active", StringComparison.OrdinalIgnoreCase)
                             && IsExecutionRole(w)))
        {
            return "执行中";
        }

        var awaitingLead = current.Any(w =>
            (string.Equals(Val(w, "transport_state"), "complete", StringComparison.OrdinalIgnoreCase)
             || string.Equals(Val(w, "execution_state"), "succeeded", StringComparison.OrdinalIgnoreCase)
             || string.Equals(Val(w, "execution_state"), "returned", StringComparison.OrdinalIgnoreCase))
            && (IsMissing(Val(w, "acceptance_state")) || Val(w, "acceptance_state") is "unknown" or "pending")
            && IsExecutionRole(w));
        if (awaitingLead)
            return "结果已交回，待负责人验收";
        if (current.Any(w => IsFailed(Val(w, "execution_state"))))
            return "当前执行失败，待处理";
        return null;
    }

    private static bool IsExecutionRole(SourceFact w)
    {
        var role = Val(w, "role") ?? string.Empty;
        if (role.Contains("contrib", StringComparison.OrdinalIgnoreCase)
            || role.Contains("acceptance", StringComparison.OrdinalIgnoreCase)
            || role.Contains("army", StringComparison.OrdinalIgnoreCase)
            || role.Equals("cli", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return role is "execution" or "unknown" or ""
            || w.EntityId.StartsWith("work:line:", StringComparison.Ordinal)
            || w.EntityId.StartsWith("work:direct:", StringComparison.Ordinal);
    }

    private static bool SameWorkBinding(SourceFact a, SourceFact b)
    {
        if (a.Links.TryGetValue("line_job_id", out var al) && b.Links.TryGetValue("line_job_id", out var bl)
            && !string.IsNullOrWhiteSpace(al) && al == bl)
        {
            return true;
        }

        if (a.Links.TryGetValue("direct_job_id", out var ad) && b.Links.TryGetValue("direct_job_id", out var bd)
            && !string.IsNullOrWhiteSpace(ad) && ad == bd)
        {
            return true;
        }

        return false;
    }

    private static string? InferNextStep(
        List<AttentionItem> attentions,
        List<SourceFact> current,
        List<SourceFact> contrib)
    {
        var authorizedIds = current
            .Where(w => string.Equals(Val(w, "authorized_current"), "true", StringComparison.OrdinalIgnoreCase))
            .Select(w => w.EntityId)
            .ToList();
        var authorizedAtt = attentions.FirstOrDefault(a =>
            a.Owner == AttentionOwner.Lead
            && authorizedIds.Any(id =>
                a.Id.Contains(id, StringComparison.Ordinal)
                || a.Targets.Any(t => t.EntityId is not null && t.EntityId.Contains(id, StringComparison.Ordinal))));
        if (authorizedAtt is not null) return authorizedAtt.Description;
        if (authorizedIds.Count > 0)
        {
            var userOnCurrent = attentions.FirstOrDefault(a => a.Owner == AttentionOwner.User);
            if (userOnCurrent is not null) return userOnCurrent.Description;
            return null;
        }

        var leadAtt = attentions.FirstOrDefault(a => a.Owner == AttentionOwner.Lead);
        if (leadAtt is not null) return leadAtt.Description;
        var userAtt = attentions.FirstOrDefault(a => a.Owner == AttentionOwner.User);
        if (userAtt is not null) return userAtt.Description;
        if (contrib.Any(c => Val(c, "acceptance_state") == "pending"))
            return "原Lead待验收贡献";
        if (current.Any(w => string.Equals(Val(w, "transport_state"), "complete", StringComparison.OrdinalIgnoreCase)))
            return "待原Lead处理回执";
        return null;
    }

    // --- helpers ---

    private static string? Val(SourceFact? f, string key)
    {
        if (f is null) return null;
        if (!f.Values.TryGetPropertyValue(key, out var node) || node is null) return null;
        try { return node.GetValue<string>(); }
        catch { return node.ToJsonString().Trim('"'); }
    }

    private static bool IsMissing(string? s) =>
        string.IsNullOrWhiteSpace(s) || s == "unknown";

    private static string AxisOrUnknown(string? s) => IsMissing(s) ? "unknown" : s!;

    private static string? NullIfUnknown(string? s) => IsMissing(s) ? null : s;

    private static bool IsFailed(string? execution) =>
        string.Equals(execution, "failed", StringComparison.OrdinalIgnoreCase);

    private static bool IsTerminalGoal(string? goal) =>
        string.Equals(goal, "complete", StringComparison.OrdinalIgnoreCase);

    private static JsonObject CloneValues(JsonObject values) => (JsonObject)values.DeepClone()!;

    private static DateTimeOffset? MaxFactAt(IEnumerable<SourceFact> facts)
    {
        DateTimeOffset? max = null;
        foreach (var f in facts)
        {
            foreach (var e in f.Evidence)
            {
                var t = e.FactAt ?? e.ObservedAt;
                if (max is null || t > max) max = t;
            }
        }

        return max;
    }
}
