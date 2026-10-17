// SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Text.Json.Nodes;
using CliWatch.Contracts;

namespace CliWatch.Projection;

/// <summary>
/// Project mailbox + process + session facts into one HUD row per job.
/// </summary>
public sealed class CliWatchProjector : IProjector
{
    readonly Dictionary<string, DateTimeOffset> _sourcedActivity = new(StringComparer.Ordinal);
    readonly Dictionary<string, ProjectView> _executionPins = new(StringComparer.Ordinal);

    public CockpitSnapshot Project(FactBatch batch, CockpitSnapshot? previous = null)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var issues = new List<SourceIssue>(batch.Issues ?? Array.Empty<SourceIssue>());
        var facts = batch.Facts ?? Array.Empty<SourceFact>();
        var now = batch.CollectedAt;

        var config = facts.FirstOrDefault(f => f.Kind == "watch_config");
        var quietSeconds = IntVal(config, "quiet_seconds") ?? 120;
        if (quietSeconds < 15) quietSeconds = 15;
        var historyHours = IntVal(config, "history_hours") ?? 72;
        var sample = Str(config, "sample_banner") == "true" || BoolVal(config, "sample_banner") == true;

        var roots = facts.Where(f => f.Kind == "mailbox_root").ToList();
        var jobs = facts.Where(f => f.Kind == "mailbox_job"
            && !MailboxRoles.IsNotificationOnly(Str(f, "harness"))).ToList();
        var readableRoots = roots.Count(r => Str(r, "readable") == "true" || BoolVal(r, "readable") == true);
        var rootFailures = issues.Any(i =>
            i.Code is "mailbox_root_missing" or "mailbox_jobs_unreadable" or "mailbox_roots_empty" or "mailbox_jobs_missing");

        var byKey = new Dictionary<string, List<SourceFact>>(StringComparer.OrdinalIgnoreCase);
        void Index(SourceFact f)
        {
            var key = KeyOf(f);
            if (string.IsNullOrWhiteSpace(key)) return;
            var index = IdentityToken(MailboxRootOf(f), key);
            if (!byKey.TryGetValue(index, out var list))
            {
                list = new List<SourceFact>();
                byKey[index] = list;
            }

            list.Add(f);
        }

        foreach (var f in facts)
            Index(f);

        var built = new List<BuiltJob>();
        var seenActivityIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var job in jobs)
        {
            var key = KeyOf(job) ?? job.EntityId;
            var index = IdentityToken(MailboxRootOf(job), key);
            byKey.TryGetValue(index, out var related);
            related ??= new List<SourceFact> { job };
            built.Add(BuildJob(job, related, now, quietSeconds, historyHours, sample, issues, previous, _sourcedActivity, seenActivityIds));
        }

        foreach (var cached in _sourcedActivity.Keys.ToList())
        {
            if (!seenActivityIds.Contains(cached))
                _sourcedActivity.Remove(cached);
        }

        var keyCounts = built
            .GroupBy(b => b.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var projects = new List<ProjectView>(built.Count);
        var freshPins = new Dictionary<string, ProjectView>(StringComparer.Ordinal);
        foreach (var item in built)
        {
            var supersededRegistration = item.View.Phase == CliWatchStatus.Registered
                && built.Any(next => next.Group == item.Group && next.JobId == item.JobId
                    && next.AttemptId != item.AttemptId
                    && item.RegisteredAt is not null && next.RegisteredAt > item.RegisteredAt
                    && (next.OwnerThread == item.OwnerThread
                        || built.Any(handoff => handoff.Group == item.Group && handoff.JobId == item.JobId
                            && HandoffConnects(handoff, item.OwnerThread, next.OwnerThread)))
                    && (CliWatchStatus.IsRunning(next.View.Phase)
                        || next.View.Phase == CliWatchStatus.StartUnconfirmed
                        || CliWatchStatus.IsTerminal(next.View.Phase) && !next.RetiredIdle));
            // Current window membership is this round's confirmed execution or confirmed launcher.
            // Scope, READY, acceptance, registration age, and the previous IsActive do not qualify a row.
            var inWindow = item.CliAlive || item.LauncherAlive;
            var view = PlaceOnList(item.View, historical: !inWindow);
            if (!inWindow)
                view = view with { NextStep = "本轮没有该任务的执行或启动证据，保留在历史。原状态保留，不代表完成或失败。" };
            if (item.DisplayCloseoutIdle)
                view = view with { NextStep = "已核对原 Lead 收尾证据，此精确交卷归入历史。业务状态和原始交付记录未改写。" };
            else if (item.LeadRecordedIdle)
                view = view with { NextStep = "执行已结束，已有原 Lead 验收记录，归入历史。验收结论未核实，不代表本窗判定通过。" };
            else if (supersededRegistration && !inWindow)
                view = view with { NextStep = "同一任务已有后续执行轮次；此登记保留在历史中，不代表任务完成。" };
            else if (item.UnconfirmedExpired && !inWindow)
                view = view with { NextStep = "超过登记的启动时限或历史时效，仍无当前执行证据，归入历史；原状态保留，不代表完成或失败。" };
            if (keyCounts.TryGetValue(item.Key, out var n) && n > 1)
                view = view with { Id = item.Identity };
            projects.Add(view);
            RememberPin(freshPins, item, view);
        }

        projects = projects
            .OrderBy(p => StatusSort(p.Phase))
            .ThenBy(p => p.LastFactAt ?? DateTimeOffset.MinValue)
            .ToList();

        var unbound = facts.Where(f => f.Kind == "unbound_process").ToList();
        var keptUnbound = new List<SourceFact>();
        var revived = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var proc in unbound)
        {
            var pin = ProcessPin(proc);
            if (pin is not null && _executionPins.TryGetValue(pin, out var cached))
            {
                var index = projects.FindIndex(p => p.Id.Equals(cached.Id, StringComparison.OrdinalIgnoreCase));
                var sameInstance = index >= 0
                    && freshPins.TryGetValue(pin, out var already)
                    && already.Id.Equals(projects[index].Id, StringComparison.OrdinalIgnoreCase);
                if (sameInstance)
                    continue;

                if (index >= 0 && projects[index].IsActive)
                {
                    keptUnbound.Add(proc);
                    continue;
                }

                if (revived.Add(cached.Id))
                {
                    var basis = index >= 0 ? projects[index] : cached;
                    var continued = ContinueInstance(basis, cached, pin, now, registrationRead: index >= 0);
                    if (index >= 0) projects[index] = continued;
                    else projects.Add(continued);
                    freshPins[pin] = continued;
                }

                continue;
            }

            keptUnbound.Add(proc);
        }

        if (keptUnbound.Count > 0)
            projects.Add(BuildUnbound(keptUnbound, now, sample));

        // A missed registration is a collection gap. Keep the last record in history.
        // Continue it only when this round still has the same process. Never reuse the old IsActive.
        var partialRoots = roots.Where(r => BoolVal(r, "scan_complete") == false).Select(MailboxRootOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (previous is not null && partialRoots.Count > 0)
            foreach (var old in previous.Projects.Where(p => p.Id != "unbound"))
            {
                var registration = old.WorkItems.SelectMany(w => w.Evidence).FirstOrDefault(e => e.Kind == "mailbox_job")?.Location;
                if (registration is not null)
                {
                    var currentIndex = projects.FindIndex(p => p.WorkItems.SelectMany(w => w.Evidence)
                        .Any(e => e.Kind == "mailbox_job" && e.Location.Equals(registration, StringComparison.OrdinalIgnoreCase)));
                    if (currentIndex >= 0)
                    {
                        projects[currentIndex] = projects[currentIndex] with { Id = old.Id };
                        continue;
                    }
                }

                if (projects.Any(p => p.Id.Equals(old.Id, StringComparison.OrdinalIgnoreCase))) continue;
                var oldRoot = old.WorkItems.SelectMany(w => w.Evidence).Select(e => e.Location)
                    .FirstOrDefault(path => path is not null && partialRoots.Any(root => path.Replace('\\', '/').StartsWith(root.Replace('\\', '/').TrimEnd('/') + "/jobs/", StringComparison.OrdinalIgnoreCase)));
                if (oldRoot is null) continue;
                projects.Add(HoldUnread(old));
            }

        _executionPins.Clear();
        foreach (var pin in freshPins)
            _executionPins[pin.Key] = pin.Value;

        var inactive = built.Where(j => !projects.Any(p => p.IsActive && p.ActivityIdentity == j.View.ActivityIdentity))
            .Select(j => j.Identity).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < issues.Count; i++)
            if (issues[i].MailboxRoot is { } issueRoot && issues[i].JobKey is { } issueKey
                && inactive.Contains(IdentityToken(issueRoot, issueKey)))
                issues[i] = issues[i] with { Detail = "scope=historical; " + issues[i].Detail };

        DataQuality quality;
        if (jobs.Count == 0 && unbound.Count == 0 && (roots.Count == 0 || readableRoots == 0 || rootFailures))
            quality = DataQuality.Unavailable;
        else if (issues.Any(i => i.Code.Contains("conflict", StringComparison.OrdinalIgnoreCase)))
            quality = DataQuality.Conflicting;
        else if (BoolVal(config, "root_collection_partial") == true
            || projects.Any(p => p.IsActive && p.Quality is DataQuality.Partial or DataQuality.LastKnown))
            quality = DataQuality.Partial;
        else
            quality = DataQuality.Fresh;

        if (sample)
        {
            issues.Insert(0, new SourceIssue(
                "sample",
                "sample_banner",
                "样例（模拟状态，非本机实况）",
                now));
        }

        return new CockpitSnapshot(now, projects, issues, Array.Empty<RouteCoverage>(), quality);
    }

    static BuiltJob BuildJob(
        SourceFact job,
        List<SourceFact> related,
        DateTimeOffset now,
        int quietSeconds,
        int historyHours,
        bool sample,
        List<SourceIssue> issues,
        CockpitSnapshot? previous,
        Dictionary<string, DateTimeOffset> sourcedActivity,
        HashSet<string> seenActivityIds)
    {
        var key = KeyOf(job) ?? job.EntityId;
        var projectId = Str(job, "project") ?? "unspecified";
        var jobId = Str(job, "job_id") ?? key;
        var attempt = Str(job, "attempt_id");
        var harness = Str(job, "harness");
        var summary = Str(job, "task_summary");
        var registered = ParseTime(Str(job, "registered_at"));
        var corrupt = BoolVal(job, "corrupt") == true;
        var directoryConflict = BoolVal(job, "directory_identity_conflict") == true;

        var request = related.FirstOrDefault(f => f.Kind == "adapter_request" && BoolVal(f, "identity_ok") != false);
        var owner = related.FirstOrDefault(f => f.Kind == "adapter_owner" && BoolVal(f, "identity_ok") != false);
        var receipt = related.FirstOrDefault(f => f.Kind == "adapter_receipt" && BoolVal(f, "identity_ok") != false);
        var outboxReady = related.FirstOrDefault(f => f.Kind == "outbox_ready" && BoolVal(f, "identity_ok") != false && ReadyMatches(f, job));
        var snapshotReady = related.FirstOrDefault(f =>
            f.Kind == "snapshot_ready"
            && BoolVal(f, "identity_ok") != false
            && BoolVal(f, "corrupt") != true
            && ReadyMatches(f, job));
        var ready = outboxReady ?? snapshotReady;
        var terminal = related.FirstOrDefault(f => f.Kind == "adapter_terminal" && BoolVal(f, "identity_ok") == true);
        var nativeTerminal = related.FirstOrDefault(f => f.Kind == "native_supervisor_terminal" && BoolVal(f, "identity_ok") == true);
        if (ready is not null && Str(job, "source_kind") == "claude_lead_mailbox" && Str(ready, "status") == "delivered")
        {
            var values = (JsonObject)ready.Values.DeepClone();
            values["status"] = "completed";
            ready = ready with { Values = values };
        }
        var delivery = related.FirstOrDefault(f => f.Kind == "mailbox_delivery");
        var deliveryOk = delivery is not null && BoolVal(delivery, "identity_ok") != false;
        var deliveryState = deliveryOk ? Str(delivery, "state") : null;
        var snapshotMismatch = related.Any(f =>
            (f.Kind is "snapshot_ready" or "mailbox_snapshot" or "mailbox_delivery")
            && BoolVal(f, "identity_ok") == false);
        var result = related.FirstOrDefault(f => f.Kind == "adapter_result_stat" && BoolVal(f, "identity_ok") != false);
        var activity = related.FirstOrDefault(f => f.Kind == "grok_session_activity" && BoolVal(f, "identity_ok") != false);
        var bind = related.FirstOrDefault(f => f.Kind == "process_bind");
        var outboxFiles = related.Where(f => f.Kind == "outbox_file").ToList();

        var claude = string.Equals(Str(job, "source_kind"), "claude_lead_mailbox", StringComparison.Ordinal);
        var observedModel = FirstNonEmpty(Str(request, "model"), Str(activity, "current_model_id"), Str(activity, "primaryModelId"));
        var observedEffort = FirstNonEmpty(Str(request, "reasoning_effort"), Str(activity, "reasoning_effort"));
        if (BoolVal(bind, "cli_alive") == true && string.Equals(Str(bind, "cli_family"), "Codex", StringComparison.OrdinalIgnoreCase))
        {
            observedModel = FirstNonEmpty(Str(bind, "observed_model"), observedModel);
            observedEffort = FirstNonEmpty(Str(bind, "observed_effort"), observedEffort);
        }
        var argvModel = Str(bind, "observed_model");
        var argvEffort = Str(bind, "observed_effort");
        var registeredModel = FirstNonEmpty(Str(job, "execution_model"), Str(job, "registered_model"));
        var registeredEffort = FirstNonEmpty(Str(job, "reasoning_effort"), Str(job, "registered_effort"));
        var (model, modelNote) = Provenance(observedModel, argvModel, registeredModel, claude);
        var (effort, effortNote) = Provenance(observedEffort, argvEffort, registeredEffort, claude);
        var cli = FirstNonEmpty(Str(bind, "cli_family"), HumanHarness(harness));
        if (claude)
            cli = "Claude Lead发起 · " + (string.IsNullOrWhiteSpace(cli) ? "CLI" : cli);
        var cliAlive = BoolVal(bind, "cli_alive") == true;
        var wrapperAlive = BoolVal(bind, "wrapper_alive") == true;
        var wrapperState = Str(bind, "wrapper_state") ?? "absent";
        var cliPid = IntVal(bind, "cli_pid") ?? 0;
        var wrapperPid = IntVal(bind, "wrapper_pid") ?? 0;
        var activityKind = Str(bind, "activity_kind");
        var processBusy = activityKind is "child_io" or "cli_compute";

        var sessionTimes = new List<DateTimeOffset?>
        {
            ParseTime(Str(activity, "last_active_at")),
            ParseTime(Str(activity, "updated_at")),
            ParseTime(Str(activity, "events.jsonl_mtime")),
            ParseTime(Str(activity, "updates.jsonl_mtime")),
            ParseTime(Str(activity, "summary.json_mtime")),
            ParseTime(Str(activity, "signals.json_mtime"))
        };
        var sessionActivity = cliAlive ? MaxTime(sessionTimes.ToArray()) : null;
        var times = new List<DateTimeOffset?> { sessionActivity };
        foreach (var file in outboxFiles)
        {
            if (BoolVal(file, "activity") == false) continue;
            var fileName = Str(file, "name");
            if (IsRegistrationOutboxName(fileName)) continue;
            times.Add(ParseTime(Str(file, "mtime")));
        }

        var resultBytes = LongVal(result, "bytes") ?? 0;
        if (resultBytes > 0)
            times.Add(ParseTime(Str(result, "mtime")));
        if (processBusy)
            times.Add(now);
        var frameSourced = MaxTime(times.ToArray());
        // Registration/DISPATCH and delivery transport times are not CLI activity.
        // Empty grok-result.json is not activity. Shared-session timestamps require a live CLI.

        var waiting = LooksWaiting(activity);
        var activityKnown = BoolVal(activity, "activity_known") == true
                            || BoolVal(bind, "counters_available") == true;
        DateTimeOffset? runStarted = null;
        var startTicks = LongVal(bind, "cli_start_ticks") ?? LongVal(owner, "start_time_utc_ticks") ?? 0;
        if (cliAlive)
        {
            if (startTicks > 0)
                runStarted = new DateTimeOffset(startTicks, TimeSpan.Zero);
            runStarted ??= ParseTime(Str(owner, "started_at_utc"));
        }

        DateTimeOffset? wrapperWaitStarted = null;
        if (!cliAlive && wrapperAlive)
            wrapperWaitStarted = ParseTime(Str(owner, "started_at_utc"));

        var started = runStarted ?? wrapperWaitStarted;
        if (frameSourced is { } observed && started is { } startAt && observed < startAt)
            frameSourced = null;

        var activityId = IdentityToken(MailboxRootOf(job), key) + "\n" + startTicks.ToString(CultureInfo.InvariantCulture);
        DateTimeOffset? remembered = null;
        if (sourcedActivity.TryGetValue(activityId, out var cached))
            remembered = cached;
        var previousObserved = PreviousObservedActivity(previous, activityId);
        remembered = MaxTime(remembered, previousObserved);

        var lastActivity = MaxTime(frameSourced, remembered);
        if (cliAlive || wrapperAlive)
        {
            seenActivityIds.Add(activityId);
            if (lastActivity is { } keep)
                sourcedActivity[activityId] = keep;
        }
        else
        {
            sourcedActivity.Remove(activityId);
        }

        var sessionRecent = sessionActivity is { } sat && (now - sat).TotalSeconds <= quietSeconds;
        var displayKind = !cliAlive
            ? "none"
            : sessionRecent
                ? "cli_log"
                : processBusy
                    ? activityKind
                    : string.IsNullOrWhiteSpace(activityKind) ? "quiet" : activityKind;

        var thisFrameActive = processBusy || sessionRecent;
        var status = Classify(
            corrupt,
            ready,
            receipt,
            result,
            cliAlive,
            wrapperAlive,
            wrapperState,
            owner is not null,
            waiting,
            lastActivity,
            activityKnown,
            now,
            quietSeconds,
            thisFrameActive,
            deliveryState,
            displayKind);

        if (!cliAlive && terminal is not null && !corrupt)
        {
            if (IntVal(terminal, "exit_code") != 0) status = CliWatchStatus.Failed;
            else if (ready is null) status = CliWatchStatus.ExecutorFinished;
        }
        if (!cliAlive && nativeTerminal is not null && !corrupt)
        {
            if (IntVal(nativeTerminal, "exit_code") != 0) status = CliWatchStatus.Failed;
            else if (ready is null) status = CliWatchStatus.ExecutorFinished;
        }
        if (!cliAlive && ready is null && terminal is null && BoolVal(job, "collection_partial") == true)
            status = CliWatchStatus.Unknown;

        if (!cliAlive && !wrapperAlive && snapshotMismatch && ready is null
            && status == CliWatchStatus.Registered)
        {
            status = CliWatchStatus.Unknown;
        }

        if (!cliAlive && status == CliWatchStatus.Registered && related.Any(f =>
            BoolVal(f, "identity_ok") == false || BoolVal(f, "corrupt") == true))
            status = CliWatchStatus.Unknown;

        // A saved prompt-file link that no longer matches the files is not a running
        // or completed attempt. Real READY/receipt evidence above still stands.
        if (string.Equals(Str(bind, "bind_how"), "prompt_file_degraded", StringComparison.Ordinal)
            && !cliAlive
            && !wrapperAlive
            && status == CliWatchStatus.Registered)
        {
            status = CliWatchStatus.Unknown;
        }

        if (BoolVal(bind, "observed_cli_unavailable") == true && status == CliWatchStatus.Registered)
            status = CliWatchStatus.Unknown;

        if (BoolVal(bind, "observed_cli_exited") == true
            && status is CliWatchStatus.Registered or CliWatchStatus.Unknown
            && !corrupt && !snapshotMismatch && BoolVal(job, "collection_partial") != true
            && ready is null && receipt is null && deliveryState != "sent")
            status = CliWatchStatus.ProcessGone;

        if (!cliAlive && !wrapperAlive && ready is null && receipt is null && terminal is null && nativeTerminal is null
            && !corrupt && !snapshotMismatch && BoolVal(job, "collection_partial") != true
            && (BoolVal(job, "native_supervisor_absent") == true
                || request is not null && owner is not null && wrapperState == "dead")
            && !issues.Any(i => i.Code == "process_snapshot_failed"))
            status = CliWatchStatus.ProcessGone;

        if (!cliAlive && !wrapperAlive && ready is null && receipt is not null && !corrupt && !snapshotMismatch
            && BoolVal(receipt, "transport_complete") == false && !string.IsNullOrWhiteSpace(Str(receipt, "transport_error"))
            && ParseTime(Str(receipt, "completed_at_utc")) is { } receiptEnded && receiptEnded <= now
            && LongVal(owner, "start_time_utc_ticks") is > 0 and var ownerBorn && receiptEnded.UtcTicks >= ownerBorn)
            status = CliWatchStatus.TransportFailed;

        var jobState = Str(job, "state");
        var retiredRecord = LooksRetired(jobState);
        var retiredIdle = retiredRecord && !cliAlive && !wrapperAlive;
        if (retiredIdle)
            status = CliWatchStatus.Retired;
        if (directoryConflict)
            status = CliWatchStatus.Unknown;

        var endedWithoutProcess = !cliAlive && !wrapperAlive
                                  && (CliWatchStatus.IsTerminal(status)
                                      || retiredIdle
                                      || (status == CliWatchStatus.Unknown && deliveryState == "sent"));
        var startIdentity = ParseTime(Str(owner, "started_at_utc"));
        if (startIdentity is null)
        {
            var ownerTicks = LongVal(owner, "start_time_utc_ticks");
            if (ownerTicks is > 0)
                startIdentity = new DateTimeOffset(ownerTicks.Value, TimeSpan.Zero);
        }

        var durationLine = TimingLine(status, runStarted, wrapperWaitStarted, registered, now);
        var label = CliWatchStatus.LabelZh(status);
        var basis = CliWatchStatus.BasisZh(status);
        if (!activityKnown && CliWatchStatus.IsRunning(status))
            basis += " 过程活动依据不足时显示为未知，不编造进度。";
        if (deliveryState is "sent" or "pending" or "sending")
            basis += " 通知状态 " + deliveryState + " 只表示运输，不是 Lead 验收。";

        var name = projectId + " / " + jobId;
        if (!string.IsNullOrWhiteSpace(attempt))
            name += " #" + attempt;
        if (claude)
            name = "Claude Lead发起 · " + name;
        var oneLiner = string.Join(" · ", new[]
        {
            cli,
            string.IsNullOrWhiteSpace(model) ? null : model,
            string.IsNullOrWhiteSpace(effort) ? null : effort,
            label,
            durationLine,
            CliWatchStatus.FormatAgo(lastActivity, now, english: false),
            CliWatchStatus.ActivityKindZh(displayKind)
        }.Where(s => !string.IsNullOrWhiteSpace(s)));

        var acceptanceRecord = related.FirstOrDefault(f => f.Kind == "acceptance_record");
        var leadRecordedIdle = endedWithoutProcess && (LongVal(acceptanceRecord, "bytes") is > 0 || BoolVal(job, "lead_disposition_verified") == true);
        // Age and an unhandled delivery are not current-window evidence.
        // A live CLI or the exact launcher still wins inside Classify.
        var pendingAt = MaxTime(lastActivity, registered, startIdentity,
            ParseTime(Str(terminal, "finished_at_utc")), ParseTime(Str(receipt, "completed_at_utc")));
        var timeoutMinutes = IntVal(job, "timeout_minutes") ?? 0;
        var unconfirmedExpired = !cliAlive && !wrapperAlive
            && status is CliWatchStatus.Registered or CliWatchStatus.Unknown or CliWatchStatus.ExecutorFinished or CliWatchStatus.ProcessGone
            && pendingAt is { } pendingSince
            && ((now - pendingSince).TotalHours >= Math.Max(1, historyHours)
                || timeoutMinutes > 0 && (now - pendingSince).TotalMinutes >= timeoutMinutes);
        var axes = AxesFor(status, cliAlive, wrapperAlive, waiting);
        if (acceptanceRecord is not null || BoolVal(job, "lead_disposition_verified") == true)
            axes = axes with { Acceptance = "unknown" };
        var artifacts = BuildArtifacts(job, related, key, projectId).ToList();
        var launchScript = Str(bind, "launch_script");
        if (!string.IsNullOrWhiteSpace(launchScript) && LooksLikePath(launchScript))
        {
            artifacts.Add(new ArtifactView(
                "launch:" + key,
                "启动入口",
                "present",
                new NavigationTarget("file", launchScript, "启动入口", projectId, key),
                bind?.Evidence ?? Array.Empty<EvidenceRef>()));
        }

        var jobLocation = job.Links.TryGetValue("location", out var linkedLocation) ? linkedLocation : null;
        if (claude && !string.IsNullOrWhiteSpace(jobLocation) && LooksLikePath(jobLocation))
        {
            artifacts.Add(new ArtifactView(
                "claude-job:" + key,
                "Claude Lead登记",
                "present",
                new NavigationTarget("file", jobLocation, "Claude Lead登记", projectId, key),
                job.Evidence));
        }
        var targets = artifacts.Select(a => a.Target).Where(t => t is not null).Cast<NavigationTarget>().ToList();
        var evidence = related.SelectMany(r => r.Evidence).Concat(job.Evidence).ToList();

        string? note = CliWatchStatus.ActivityNoteZh(displayKind);
        if (ready is not null && CliWatchStatus.IsRunning(status))
            note = "已见到交付文件，但当前绑定的 CLI 仍在。交付不是 Lead 验收。";
        if (retiredRecord && (cliAlive || wrapperAlive))
            note = "登记已标记退役，但当前仍有存活进程。以进程为准，不隐去真实运行。退役不是验收。";
        if (!string.IsNullOrWhiteSpace(modelNote))
            note = string.IsNullOrWhiteSpace(note) ? modelNote : note + " " + modelNote;
        if (!string.IsNullOrWhiteSpace(effortNote))
            note = string.IsNullOrWhiteSpace(note) ? effortNote : note + " " + effortNote;
        if (claude && !string.IsNullOrWhiteSpace(jobLocation))
            note = string.IsNullOrWhiteSpace(note) ? "来源 Claude Lead发起 " + jobLocation : note + " 来源 Claude Lead发起 " + jobLocation;
        if (acceptanceRecord is not null || BoolVal(job, "lead_disposition_verified") == true)
        {
            note = string.IsNullOrWhiteSpace(note)
                ? CliWatchStatus.AcceptanceUnverifiedZh
                : CliWatchStatus.AcceptanceUnverifiedZh + " " + note;
            oneLiner = oneLiner + " · " + CliWatchStatus.AcceptanceUnverifiedZh;
        }

        if (directoryConflict)
        {
            const string conflictNote = "目录身份与 job.json 声明不一致。不采用其声明的 adapter、交付目录或验收记录。";
            note = string.IsNullOrWhiteSpace(note) ? conflictNote : conflictNote + " " + note;
        }

        if (sample)
            oneLiner = "【样例】" + oneLiner;

        var rowQuality = BoolVal(job, "collection_partial") == true ? DataQuality.Partial : corrupt
            ? DataQuality.Unavailable
            : directoryConflict
                ? DataQuality.Conflicting
                : DataQuality.Fresh;
        var work = new WorkView(
            "work:" + key,
            "executor",
            "watch:" + status,
            oneLiner,
            axes,
            artifacts,
            targets,
            evidence,
            rowQuality,
            cli,
            summary ?? jobId,
            model,
            effort,
            note ?? QuietNote(status),
            "executor",
            null);

        var attentions = new List<AttentionItem>();
        if (status is CliWatchStatus.Failed or CliWatchStatus.TransportFailed)
        {
            attentions.Add(new AttentionItem(
                "attention:failed:" + key,
                AttentionOwner.Lead,
                status == CliWatchStatus.TransportFailed ? "执行交付链异常，待原 Lead 核实业务结果" : "明确异常退出/失败，待原 Lead 处理",
                "cli_watch_failed",
                targets,
                evidence));
        }
        else if (status is CliWatchStatus.Delivered or CliWatchStatus.ReportedFailure)
        {
            attentions.Add(new AttentionItem(
                "attention:delivered:" + key,
                AttentionOwner.Lead,
                acceptanceRecord is not null
                    ? CliWatchStatus.AcceptanceUnverifiedZh
                    : "已交付，待原 Lead 处理。本窗不验收。",
                "cli_watch_delivered",
                targets,
                evidence));
        }

        var view = new ProjectView(
            key,
            name,
            oneLiner,
            status,
            summary,
            NextStep(status),
            true,
            false,
            null,
            new[] { work },
            attentions,
            targets,
            rowQuality,
            lastActivity ?? started ?? registered,
            lastActivity,
            activityId);
        return new BuiltJob(
            view,
            IdentityToken(MailboxRootOf(job), key),
            GroupToken(MailboxRootOf(job), projectId),
            key,
            jobId,
            Str(job, "target_thread_id") ?? string.Empty,
            attempt ?? string.Empty,
            registered,
            startIdentity,
            endedWithoutProcess,
            retiredIdle,
            leadRecordedIdle,
            unconfirmedExpired,
            cliAlive || wrapperAlive,
            cliAlive,
            wrapperAlive && !cliAlive && !corrupt && !ExactRoundEnded(ready, receipt, terminal, nativeTerminal),
            cliAlive ? cliPid : 0,
            cliAlive ? LongVal(bind, "cli_start_ticks") ?? 0 : 0,
            wrapperAlive ? wrapperPid : 0,
            wrapperAlive ? LongVal(bind, "wrapper_start_ticks") ?? 0 : 0,
            BoolVal(job, "current_scope_member") == false
                && ParseTime(Str(job, "current_scope_at")) is { } scopeAt && pendingAt > scopeAt
                    ? true : BoolVal(job, "current_scope_member"),
            !cliAlive && !wrapperAlive && BoolVal(job, "display_closeout_verified") == true,
            BoolVal(job, "owner_handoff_verified") == true,
            Str(job, "owner_handoff_evidence_old_thread_id") ?? string.Empty,
            Str(job, "owner_handoff_evidence_new_thread_id") ?? string.Empty);
    }

    sealed record BuiltJob(
        ProjectView View,
        string Identity,
        string Group,
        string Key,
        string JobId,
        string OwnerThread,
        string AttemptId,
        DateTimeOffset? RegisteredAt,
        DateTimeOffset? StartedAt,
        bool EndedWithoutProcess,
        bool RetiredIdle,
        bool LeadRecordedIdle,
        bool UnconfirmedExpired,
        bool ProcessAlive,
        bool CliAlive,
        bool LauncherAlive,
        int CliPid,
        long CliTicks,
        int WrapperPid,
        long WrapperTicks,
        bool? CurrentScopeMember,
        bool DisplayCloseoutIdle,
        bool OwnerHandoffVerified,
        string OwnerHandoffFrom,
        string OwnerHandoffTo);

    static bool HandoffConnects(BuiltJob handoff, string ownerA, string ownerB)
    {
        if (!handoff.OwnerHandoffVerified
            || string.IsNullOrWhiteSpace(ownerA)
            || string.IsNullOrWhiteSpace(ownerB)
            || ownerA.Equals(ownerB, StringComparison.Ordinal))
            return false;

        return handoff.OwnerHandoffFrom.Equals(ownerA, StringComparison.Ordinal)
                && handoff.OwnerHandoffTo.Equals(ownerB, StringComparison.Ordinal)
            || handoff.OwnerHandoffFrom.Equals(ownerB, StringComparison.Ordinal)
                && handoff.OwnerHandoffTo.Equals(ownerA, StringComparison.Ordinal);
    }

    static void RememberPin(Dictionary<string, ProjectView> pins, BuiltJob item, ProjectView view)
    {
        if (item.CliAlive && item.CliPid > 0 && item.CliTicks > 0)
            pins[item.CliPid + "\n" + item.CliTicks] = view;
        if (item.LauncherAlive && item.WrapperPid > 0 && item.WrapperTicks > 0)
            pins[item.WrapperPid + "\n" + item.WrapperTicks] = view;
    }

    static bool ExactRoundEnded(SourceFact? ready, SourceFact? receipt, SourceFact? terminal, SourceFact? nativeTerminal)
    {
        if (ready is not null && BoolVal(ready, "corrupt") != true
            && Str(ready, "status") is "completed" or "failed" or "blocked")
            return true;
        if (terminal is not null && BoolVal(terminal, "corrupt") != true)
            return true;
        if (nativeTerminal is not null && BoolVal(nativeTerminal, "corrupt") != true)
            return true;
        return receipt is not null
            && BoolVal(receipt, "corrupt") != true
            && BoolVal(receipt, "transport_complete") == true;
    }

    static ProjectView ContinueInstance(ProjectView basis, ProjectView cached, string pin, DateTimeOffset now, bool registrationRead)
    {
        var observed = basis.ObservedActivityAt ?? cached.ObservedActivityAt;
        var split = pin.IndexOf('\n');
        var ticks = split >= 0 && long.TryParse(pin[(split + 1)..], out var parsed) ? parsed : 0;
        var started = ticks > 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : (DateTimeOffset?)null;
        var launching = cached.Phase == CliWatchStatus.StartUnconfirmed;
        var phase = launching ? CliWatchStatus.StartUnconfirmed : CliWatchStatus.RunningUnknown;
        var duration = started is { } start
            ? (launching ? "启动等待已过 " : "运行 ") + CliWatchStatus.FormatDuration(now - start, english: false)
            : null;
        var ago = launching ? null : CliWatchStatus.FormatAgo(observed, now, english: false);
        var source = cached.WorkItems.FirstOrDefault() ?? basis.WorkItems.FirstOrDefault();
        var head = string.Join(" · ", new[]
        {
            source?.ActorName,
            source?.Model,
            source?.Effort,
            CliWatchStatus.LabelZh(phase),
            duration,
            ago
        }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var axes = AxesFor(phase, cliAlive: !launching, wrapperAlive: launching, waiting: false);
        var blocker = QuietNote(phase);
        var next = launching
            ? registrationRead
                ? "登记已读到；启动器仍在。本轮没有新的 CLI 活动证据。"
                : "本轮未读到登记；同一启动进程仍在。没有新的 CLI 活动证据。"
            : registrationRead
                ? "登记已读到，绑定记录本轮未读到；同一执行进程仍在。本轮没有新的活动或等待证据。"
                : "本轮未读到登记；同一执行进程仍在。本轮没有新的活动或等待证据。";
        var works = (basis.WorkItems.Count > 0 ? basis.WorkItems : cached.WorkItems).Select(w => w with
        {
            Summary = head,
            Axes = axes,
            Quality = DataQuality.Partial,
            Blocker = blocker,
            Route = "watch:" + phase
        }).ToList();
        return basis with
        {
            IsActive = true,
            Phase = phase,
            Quality = DataQuality.Partial,
            Summary = head,
            NextStep = next,
            WorkItems = works,
            ObservedActivityAt = observed,
            Attention = Array.Empty<AttentionItem>()
        };
    }

    static string? ProcessPin(SourceFact proc)
    {
        var pid = IntVal(proc, "pid") ?? 0;
        var ticks = LongVal(proc, "start_time_utc_ticks") ?? 0;
        if (pid <= 0 || ticks <= 0) return null;
        return pid + "\n" + ticks;
    }

    static ProjectView HoldUnread(ProjectView old)
    {
        var summary = old.Summary.StartsWith("上次记录（本轮未扫描）：", StringComparison.Ordinal)
            ? old.Summary
            : "上次记录（本轮未扫描）：" + old.Summary;
        return old with
        {
            IsActive = false,
            Quality = DataQuality.LastKnown,
            Summary = summary,
            NextStep = "本轮未读到登记，也没有当前执行证据。保留上次记录在历史，不代表执行仍在或已结束。",
            WorkItems = old.WorkItems.Select(w => w with
            {
                Quality = DataQuality.LastKnown,
                Summary = w.Summary.StartsWith("上次记录（本轮未扫描）：", StringComparison.Ordinal)
                    ? w.Summary
                    : "上次记录（本轮未扫描）：" + w.Summary
            }).ToList()
        };
    }

    static ProjectView PlaceOnList(ProjectView view, bool historical)
    {
        if (!historical)
        {
            var works = view.WorkItems.Select(w => w with
            {
                Quality = view.Quality is DataQuality.Unavailable or DataQuality.Conflicting
                    ? view.Quality
                    : DataQuality.Fresh
            }).ToList();
            return view with { IsActive = true, WorkItems = works };
        }

        var histWorks = view.WorkItems.Select(w => w with
        {
            Quality = view.Quality == DataQuality.Conflicting ? DataQuality.Conflicting : DataQuality.LastKnown
        }).ToList();
        return view with { IsActive = false, WorkItems = histWorks };
    }

    static string MailboxRootOf(SourceFact job)
    {
        if (job.Links.TryGetValue("mailbox_root", out var linked) && !string.IsNullOrWhiteSpace(linked))
            return linked;
        var root = Str(job, "mailbox_root");
        if (!string.IsNullOrWhiteSpace(root)) return root;
        if (job.Links.TryGetValue("location", out var loc) && !string.IsNullOrWhiteSpace(loc))
        {
            try
            {
                var full = Path.GetFullPath(loc);
                var jobsDir = Path.GetDirectoryName(full);
                if (!string.IsNullOrWhiteSpace(jobsDir)
                    && string.Equals(Path.GetFileName(jobsDir), "jobs", StringComparison.OrdinalIgnoreCase))
                {
                    var parent = Path.GetDirectoryName(jobsDir);
                    if (!string.IsNullOrWhiteSpace(parent)) return parent;
                }
            }
            catch (Exception)
            {
            }
        }

        return string.Empty;
    }

    static string IdentityToken(string root, string key) => root + "\n" + key;

    static string GroupToken(string root, string projectId) => root + "\n" + projectId;

    static ProjectView BuildUnbound(List<SourceFact> unbound, DateTimeOffset now, bool sample)
    {
        var works = new List<WorkView>();
        var targets = new List<NavigationTarget>();
        foreach (var p in unbound)
        {
            var pid = IntVal(p, "pid") ?? 0;
            var cli = Str(p, "cli") ?? "CLI";
            var started = ParseTime(Str(p, "started_at_utc"));
            var duration = started is { } st ? now - st : (TimeSpan?)null;
            var observedModel = Str(p, "observed_model");
            var observedEffort = Str(p, "observed_effort");
            var script = Str(p, "launch_script");
            var model = string.IsNullOrWhiteSpace(observedModel) ? null : observedModel + "（已观察）";
            var effort = string.IsNullOrWhiteSpace(observedEffort) ? null : observedEffort + "（已观察）";
            var artifacts = new List<ArtifactView>();
            if (!string.IsNullOrWhiteSpace(script) && LooksLikePath(script))
            {
                artifacts.Add(new ArtifactView(
                    "launch:" + p.EntityId,
                    "启动入口",
                    "present",
                    new NavigationTarget("file", script, "启动入口", "unbound", p.EntityId),
                    p.Evidence));
            }

            var summary = cli + " · PID " + pid + " · 未关联任务 · 运行 "
                          + CliWatchStatus.FormatDuration(duration, false);
            works.Add(new WorkView(
                p.EntityId,
                "executor",
                "watch:" + CliWatchStatus.Unbound,
                summary,
                AxesFor(CliWatchStatus.Unbound, cliAlive: true, wrapperAlive: false, waiting: false),
                artifacts,
                artifacts.Select(a => a.Target).Where(t => t is not null).Cast<NavigationTarget>().ToList(),
                p.Evidence,
                DataQuality.Fresh,
                cli,
                "未关联任务",
                model,
                effort,
                string.IsNullOrWhiteSpace(script) ? "不猜测所属项目" : "启动入口 " + script + "。不猜测所属项目。",
                "executor",
                null));
        }

        return new ProjectView(
            "unbound",
            "未关联任务",
            sample ? "【样例】本机可识别 CLI，未绑定任务登记" : "本机可识别 CLI，未绑定任务登记",
            CliWatchStatus.Unbound,
            null,
            "只观察，不猜测项目。",
            true,
            false,
            null,
            works,
            Array.Empty<AttentionItem>(),
            targets,
            DataQuality.Fresh,
            now);
    }

    public static string Classify(
        bool corrupt,
        SourceFact? ready,
        SourceFact? receipt,
        SourceFact? result,
        bool cliAlive,
        bool wrapperAlive,
        string wrapperState,
        bool ownerPresent,
        bool waiting,
        DateTimeOffset? lastActivity,
        bool activityKnown,
        DateTimeOffset now,
        int quietSeconds,
        bool processBusy = false,
        string? deliveryState = null,
        string? activityKind = null)
    {
        if (corrupt) return CliWatchStatus.Unknown;

        if (cliAlive)
        {
            if (waiting) return CliWatchStatus.Waiting;
            if (processBusy) return CliWatchStatus.RunningActive;
            var kind = activityKind ?? "";
            if (kind is "unavailable" or "baseline" or "none")
                return CliWatchStatus.RunningUnknown;
            if (lastActivity is not null)
                return CliWatchStatus.RunningQuiet;
            return activityKnown ? CliWatchStatus.RunningQuiet : CliWatchStatus.RunningUnknown;
        }

        var readyStatus = Str(ready, "status");
        if (ready is not null && BoolVal(ready, "corrupt") != true)
        {
            if (readyStatus is "failed" or "blocked")
                return CliWatchStatus.ReportedFailure;
            if (readyStatus is "completed")
                return CliWatchStatus.Delivered;
            return CliWatchStatus.Unknown;
        }

        if (!wrapperAlive
            && string.Equals(deliveryState, "sent", StringComparison.OrdinalIgnoreCase)
            && ready is null)
        {
            return CliWatchStatus.Unknown;
        }

        var transport = BoolVal(receipt, "transport_complete");
        var grokSuccess = BoolVal(receipt, "grok_success");
        if (receipt is not null && BoolVal(receipt, "corrupt") != true && transport == true)
        {
            if (grokSuccess == false)
                return CliWatchStatus.Failed;
            return CliWatchStatus.ExecutorFinished;
        }

        var resultBytes = LongVal(result, "bytes") ?? 0;
        if (!cliAlive && !wrapperAlive && resultBytes > 0)
            return CliWatchStatus.ExecutorFinished;

        if (wrapperAlive)
            return CliWatchStatus.StartUnconfirmed;

        if (ownerPresent && wrapperState == "dead")
            return CliWatchStatus.Unknown;

        if (ownerPresent && wrapperState == "unknown")
            return CliWatchStatus.Unknown;

        return CliWatchStatus.Registered;
    }

    static WorkAxes AxesFor(string status, bool cliAlive, bool wrapperAlive, bool waiting)
    {
        var process = cliAlive ? "alive" : wrapperAlive ? "wrapper_only" : "dead";
        if (status is CliWatchStatus.Registered) process = "unknown";
        if (status is CliWatchStatus.Unknown) process = "unknown";
        var exec = status switch
        {
            CliWatchStatus.RunningActive or CliWatchStatus.RunningQuiet or CliWatchStatus.RunningUnknown or CliWatchStatus.Waiting => "running",
            CliWatchStatus.Delivered or CliWatchStatus.ReportedFailure or CliWatchStatus.ExecutorFinished => "returned",
            CliWatchStatus.Failed => "failed",
            CliWatchStatus.Retired => "unknown",
            CliWatchStatus.Registered or CliWatchStatus.StartUnconfirmed => "registered",
            _ => "unknown"
        };
        var delivery = status is CliWatchStatus.Delivered or CliWatchStatus.ReportedFailure ? "complete" : "unknown";
        var transport = status is CliWatchStatus.ExecutorFinished or CliWatchStatus.Delivered or CliWatchStatus.ReportedFailure or CliWatchStatus.Failed
            ? "complete"
            : "unknown";
        if (status == CliWatchStatus.TransportFailed) transport = "failed";
        var turn = waiting ? "waiting" : status is CliWatchStatus.RunningQuiet ? "quiet" : status is CliWatchStatus.RunningActive ? "active" : "unknown";
        var lead = status is CliWatchStatus.Delivered or CliWatchStatus.ReportedFailure or CliWatchStatus.TransportFailed ? "awaiting_lead" : "unknown";
        var acceptance = status is CliWatchStatus.Delivered or CliWatchStatus.ReportedFailure ? "pending" : "unknown";
        return new WorkAxes(process, turn, exec, transport, delivery, "unknown", lead, acceptance, "unknown", "unknown");
    }

    static IReadOnlyList<ArtifactView> BuildArtifacts(SourceFact job, List<SourceFact> related, string key, string projectId)
    {
        var list = new List<ArtifactView>();
        void Add(SourceFact? f, string label)
        {
            if (f is null) return;
            var path = f.Links.TryGetValue("location", out var loc) ? loc : f.EntityId;
            if (string.IsNullOrWhiteSpace(path) || path.StartsWith("bind:", StringComparison.Ordinal) || path.StartsWith("process:", StringComparison.Ordinal))
                return;
            if (!LooksLikePath(path)) return;
            var target = new NavigationTarget("file", path, label, projectId, key);
            list.Add(new ArtifactView(f.EntityId, label, "present", target, f.Evidence));
        }

        Add(related.FirstOrDefault(f => f.Kind == "adapter_owner"), "owner.json");
        Add(related.FirstOrDefault(f => f.Kind == "adapter_request"), "request.json");
        Add(related.FirstOrDefault(f => f.Kind == "adapter_receipt"), "receipt.json");
        Add(related.FirstOrDefault(f => f.Kind == "adapter_terminal"), "terminal.json");
        Add(related.FirstOrDefault(f => f.Kind == "outbox_ready"), "READY.json");
        Add(related.FirstOrDefault(f => f.Kind == "snapshot_ready"), "归档 READY");
        Add(related.FirstOrDefault(f => f.Kind == "mailbox_delivery"), "投递记录");
        Add(related.FirstOrDefault(f => f.Kind == "mailbox_snapshot"), "交付快照清单");
        Add(related.FirstOrDefault(f => f.Kind == "lead_disposition_record"), "原 Lead 处置记录");
        Add(related.FirstOrDefault(f => f.Kind == "grok_session_activity"), "Grok session summary");
        foreach (var file in related.Where(f => f.Kind == "outbox_file"))
            Add(file, Str(file, "name") ?? "outbox");
        var outbox = Str(job, "outbox");
        if (!string.IsNullOrWhiteSpace(outbox) && LooksLikePath(outbox))
        {
            list.Add(new ArtifactView(
                "outbox-dir:" + key,
                "交付目录",
                "present",
                new NavigationTarget("folder", outbox, "交付目录", projectId, key),
                job.Evidence));
        }

        return list;
    }

    static string? TimingLine(
        string status,
        DateTimeOffset? runStarted,
        DateTimeOffset? wrapperWaitStarted,
        DateTimeOffset? registered,
        DateTimeOffset now)
    {
        if (CliWatchStatus.IsRunning(status) && runStarted is { } start)
            return "运行 " + CliWatchStatus.FormatDuration(now - start, english: false);

        if (status == CliWatchStatus.StartUnconfirmed && wrapperWaitStarted is { } wait)
            return "启动等待已过 " + CliWatchStatus.FormatDuration(now - wait, english: false);

        if (status == CliWatchStatus.Registered && registered is { } reg)
            return "登记已过 " + CliWatchStatus.FormatDuration(now - reg, english: false);

        return null;
    }

    static bool ReadyMatches(SourceFact ready, SourceFact job)
    {
        return ObservationIdentity.Matches(ready.Values, job.Values);
    }

    static int StatusSort(string? phase) => phase switch
    {
        CliWatchStatus.RunningActive => 0,
        CliWatchStatus.Waiting => 1,
        CliWatchStatus.RunningQuiet => 2,
        CliWatchStatus.RunningUnknown => 3,
        CliWatchStatus.StartUnconfirmed => 4,
        CliWatchStatus.Registered => 5,
        CliWatchStatus.Delivered => 6,
        CliWatchStatus.ReportedFailure => 6,
        CliWatchStatus.ProcessGone => 7,
        CliWatchStatus.ExecutorFinished => 7,
        CliWatchStatus.Failed => 8,
        CliWatchStatus.TransportFailed => 8,
        CliWatchStatus.Retired => 9,
        CliWatchStatus.Unbound => 10,
        _ => 11
    };

    static DateTimeOffset? PreviousObservedActivity(CockpitSnapshot? previous, string activityId)
    {
        if (previous is null || string.IsNullOrWhiteSpace(activityId)) return null;
        foreach (var p in previous.Projects)
        {
            if (p.ActivityIdentity == activityId && p.ObservedActivityAt is { } at)
                return at;
        }

        return null;
    }

    static bool LooksRetired(string? state) =>
        !string.IsNullOrWhiteSpace(state)
        && (state.Equals("retired", StringComparison.OrdinalIgnoreCase)
            || state.Equals("superseded", StringComparison.OrdinalIgnoreCase));

    static bool IsRegistrationOutboxName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && (name.Equals("DISPATCH.txt", StringComparison.OrdinalIgnoreCase)
            || name.Equals("ACCESS.json", StringComparison.OrdinalIgnoreCase));

    static bool LooksWaiting(SourceFact? activity)
    {
        if (activity is null) return false;
        var tool = Str(activity, "last_tool_name");
        if (string.IsNullOrWhiteSpace(tool)) return false;
        return tool.Contains("ask_user", StringComparison.OrdinalIgnoreCase)
               || tool.Contains("AskUserQuestion", StringComparison.OrdinalIgnoreCase)
               || tool.Equals("wait", StringComparison.OrdinalIgnoreCase)
               || tool.Contains("permission", StringComparison.OrdinalIgnoreCase);
    }

    static string? QuietNote(string status) => status switch
    {
        CliWatchStatus.RunningQuiet => "进程仍在，但暂时没有新的过程记录。不能据此认定卡死。",
        CliWatchStatus.RunningUnknown => "进程仍在，活动观察未知或基线未建立。不能据此认定卡死。",
        CliWatchStatus.StartUnconfirmed => "包装器存活不等于实际 CLI 存活。",
        CliWatchStatus.ExecutorFinished => "执行器结束/exit 0 不是业务验收。",
        CliWatchStatus.Delivered => "已交回，待 Lead。本窗不验收。",
        CliWatchStatus.ReportedFailure => "问题已交回，待 Lead；不等于进程异常退出。",
        CliWatchStatus.ProcessGone => "未见原进程或匹配交卷，退出原因待核实。",
        CliWatchStatus.TransportFailed => "原适配器已报告执行交付链异常，业务结果待核实。",
        CliWatchStatus.Retired => "登记已退役或已被替代。不是完成、失败或验收。",
        _ => null
    };

    static string NextStep(string status) => status switch
    {
        CliWatchStatus.Delivered => "原 Lead 阅读交付并决定。本窗只观察。",
        CliWatchStatus.ReportedFailure => "原 Lead 阅读失败或阻碍报告并决定。本窗不重试。",
        CliWatchStatus.ProcessGone => "原 Lead 核对终态和保留成果；本窗不自动重跑。",
        CliWatchStatus.TransportFailed => "原 Lead 核对原始退出和交付证据后决定接续。本窗不自动重跑。",
        CliWatchStatus.Retired => "登记已退役或已被替代。本窗不验收、不重试。",
        CliWatchStatus.Failed => "原 Lead 处理失败。本窗不重试、不杀进程。",
        CliWatchStatus.ExecutorFinished => "等待交付文件，或由原 Lead 查看执行器回执。",
        CliWatchStatus.Waiting => "CLI 侧在等待；有原生依据时才这样显示。",
        CliWatchStatus.RunningQuiet => "需要时打开依据查看；不要把静默当成卡死。",
        CliWatchStatus.RunningUnknown => "继续观察。活动依据不足时按未知处理，不编造有活动。",
        CliWatchStatus.Unbound => "不猜测项目；有任务登记后才会归属。",
        _ => "继续观察。本窗不派单、不干预。"
    };

    static string HumanHarness(string? harness)
    {
        if (string.IsNullOrWhiteSpace(harness)) return "CLI";
        if (harness.Contains("grok", StringComparison.OrdinalIgnoreCase)) return "Grok";
        if (harness.Contains("cursor", StringComparison.OrdinalIgnoreCase)) return "Cursor";
        if (harness.Contains("claude", StringComparison.OrdinalIgnoreCase)) return "Claude";
        if (harness.Contains("codex", StringComparison.OrdinalIgnoreCase)) return "Codex";
        if (harness.Contains("pi", StringComparison.OrdinalIgnoreCase)) return "PI";
        if (harness.Contains("dsh", StringComparison.OrdinalIgnoreCase) || harness.Contains("deep", StringComparison.OrdinalIgnoreCase))
            return "DSH";
        return harness;
    }

    static bool LooksLikePath(string path)
    {
        var t = path.Trim();
        return t.Length >= 3 && char.IsLetter(t[0]) && t[1] == ':' && (t[2] == '\\' || t[2] == '/');
    }

    static string? KeyOf(SourceFact f)
    {
        if (f.Links.TryGetValue("job_key", out var k) && !string.IsNullOrWhiteSpace(k))
            return k;
        return FirstNonEmpty(Str(f, "job_key"), Str(f, "key"));
    }

    static (string? Value, string? Note) Provenance(string? adapterValue, string? argvValue, string? registeredValue, bool claude)
    {
        string? observed = null;
        var fromAdapter = false;
        if (!string.IsNullOrWhiteSpace(adapterValue))
        {
            observed = adapterValue;
            fromAdapter = true;
        }
        else if (!string.IsNullOrWhiteSpace(argvValue))
        {
            observed = argvValue;
        }

        string? value = null;
        if (!string.IsNullOrWhiteSpace(observed))
            value = fromAdapter && !claude ? observed : observed + "（已观察）";
        else if (!string.IsNullOrWhiteSpace(registeredValue))
            value = registeredValue + "（登记指定）";

        string? note = null;
        if (!string.IsNullOrWhiteSpace(registeredValue)
            && !string.IsNullOrWhiteSpace(observed)
            && !observed.Equals(registeredValue, StringComparison.OrdinalIgnoreCase))
        {
            note = "登记指定 " + registeredValue + "，已观察 " + observed + "。不互相顶替。";
        }
        else if (!string.IsNullOrWhiteSpace(adapterValue)
                 && !string.IsNullOrWhiteSpace(argvValue)
                 && !adapterValue.Equals(argvValue, StringComparison.OrdinalIgnoreCase))
        {
            note = "适配器记录 " + adapterValue + "，启动参数 " + argvValue + "。不互相顶替。";
        }

        return (value, note);
    }

    static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }

        return null;
    }

    static string? Str(SourceFact? f, string? key)
    {
        if (f is null || string.IsNullOrWhiteSpace(key)) return null;
        if (f.Values[key] is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)) return s;
            if (v.TryGetValue<bool>(out var b)) return b ? "true" : "false";
            if (v.TryGetValue<long>(out var l)) return l.ToString(CultureInfo.InvariantCulture);
        }

        return null;
    }

    static bool? BoolVal(SourceFact? f, string key)
    {
        if (f is null) return null;
        if (f.Values[key] is JsonValue v)
        {
            if (v.TryGetValue<bool>(out var b)) return b;
            if (v.TryGetValue<string>(out var s) && bool.TryParse(s, out var p)) return p;
        }

        return null;
    }

    static int? IntVal(SourceFact? f, string key)
    {
        if (f is null) return null;
        if (f.Values[key] is JsonValue v)
        {
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<long>(out var l)) return (int)l;
            if (v.TryGetValue<string>(out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p))
                return p;
        }

        return null;
    }

    static long? LongVal(SourceFact? f, string key)
    {
        if (f is null) return null;
        if (f.Values[key] is JsonValue v)
        {
            if (v.TryGetValue<long>(out var l)) return l;
            if (v.TryGetValue<int>(out var i)) return i;
        }

        return null;
    }

    static DateTimeOffset? ParseTime(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            return parsed.ToUniversalTime();
        return null;
    }

    static DateTimeOffset? MaxTime(params DateTimeOffset?[] times)
    {
        DateTimeOffset? max = null;
        foreach (var t in times)
        {
            if (t is null) continue;
            if (max is null || t > max) max = t;
        }

        return max;
    }
}
