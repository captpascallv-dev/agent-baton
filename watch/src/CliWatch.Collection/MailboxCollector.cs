// SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using CliWatch.Contracts;

[assembly: InternalsVisibleTo("CliWatch.Tests")]

namespace CliWatch.Collection;

/// <summary>
/// Read mailbox job registries and bind them to adapter files + real processes.
/// Does not guess task from process name. Does not treat wrappers as the CLI.
/// </summary>
public sealed partial class MailboxCollector : ICollector
{
    public const string KindMailboxJob = "mailbox_job";
    public const string KindMailboxRoutes = "mailbox_routes";
    public const string KindAdapterOwner = "adapter_owner";
    public const string KindAdapterRequest = "adapter_request";
    public const string KindAdapterReceipt = "adapter_receipt";
    public const string KindAdapterTerminal = "adapter_terminal";
    public const string KindAdapterResultStat = "adapter_result_stat";
    public const string KindOutboxReady = "outbox_ready";
    public const string KindMailboxDelivery = "mailbox_delivery";
    public const string KindMailboxSnapshot = "mailbox_snapshot";
    public const string KindSnapshotReady = "snapshot_ready";
    public const string KindGrokActivity = "grok_session_activity";
    public const string KindProcessBind = "process_bind";
    public const string KindUnboundProcess = "unbound_process";
    public const string KindMailboxRoot = "mailbox_root";
    public const string KindOwnerHandoffAuthority = "owner_handoff_authority";
    public const string KindAcceptanceRecord = "acceptance_record";

    static readonly HashSet<string> DeniedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".env", "cookies", "cookie", "auth.json", "credentials", "credential",
        "api_key", "apikey", "secret", "id_rsa", ".pem", ".pfx",
        "messages.jsonl", "chat_history.jsonl", "prompt.txt", "system_prompt.txt"
    };

    readonly IProcessSnapshot? _snapshot;
    readonly IProcessProbe? _probe;
    readonly ProcessActivityStore _activity;
    readonly string? _bindingDirectory;
    Dictionary<string, PromptFileBindingRecord>? _bindings;
    bool _saveBindings;
    readonly List<JobBindInput> _pendingInputs = new();
    readonly Dictionary<string, string> _jsonCache = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _jsonAttempts = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> _contentReads = new();
    internal IReadOnlyList<string> ContentReads => _contentReads;
    readonly Dictionary<string, int> _scanOffsets = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _partialRoots = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _partialJobs = new(StringComparer.OrdinalIgnoreCase);
    readonly List<ScopeMember> _scopeMembers = new();
    readonly List<CloseoutPin> _closeoutPins = new();
    int _budgetRemaining, _rootBudget, _maxJsonBytes, _rootRotation;
    string? _currentRoot;

    sealed record ScopeMember(string MailboxRoot, string JobId);
    sealed record CloseoutPin(string MailboxRoot, string Key);

    public MailboxCollector(
        IProcessSnapshot? snapshot = null,
        IProcessProbe? probe = null,
        ProcessActivityStore? activity = null,
        string? promptBindingDirectory = null)
    {
        _snapshot = snapshot;
        _probe = probe;
        _activity = activity ?? new ProcessActivityStore();
        _bindingDirectory = string.IsNullOrWhiteSpace(promptBindingDirectory) ? null : promptBindingDirectory;
    }

    public async Task<CollectionBatch> CollectAsync(CollectorSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var observedAt = DateTimeOffset.UtcNow;
        var documents = new List<RawDocument>();
        var issues = new List<SourceIssue>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var boundPids = new HashSet<int>();

        documents.Add(new RawDocument(
            "watch_config",
            "watch_config",
            "config",
            new JsonObject
            {
                ["quiet_seconds"] = settings.QuietSeconds,
                ["history_hours"] = settings.HistoryHours,
                ["sample_banner"] = settings.SampleBanner,
                ["mailbox_root_count"] = settings.MailboxRoots.Count
            },
            observedAt,
            observedAt,
            null,
            "current"));

        _pendingInputs.Clear();
        _jsonCache.Clear();
        _jsonAttempts.Clear();
        _contentReads.Clear();
        _partialRoots.Clear();
        _partialJobs.Clear();
        _identityFiles.Clear();
        _scopeMembers.Clear();
        _closeoutPins.Clear();
        _budgetRemaining = Math.Max(1, settings.MaxFilesPerRefresh);
        _maxJsonBytes = Math.Min(1_048_576, Math.Max(1, settings.MaxFileBytes));
        using var metadataScope = PromptFileIdentity.UseMetadataReader(path =>
        {
            var kind = Path.GetFileName(path) is "summary.json" or "signals.json" ? "grok_metadata" : "identity_metadata";
            var doc = TryReadJsonAsync(path, kind, null, documents, issues, seen, observedAt, cancellationToken,
                issueIfMissing: false).GetAwaiter().GetResult();
            return doc is not null && !Flag(doc.Data, "corrupt") ? _jsonCache.GetValueOrDefault(ExpandPath(path)) : null;
        }, _maxJsonBytes);
        _saveBindings = false;
        _bindings = null;
        if (_bindingDirectory is not null)
        {
            if (PromptFileBindingStore.TryLoad(_bindingDirectory, out var loaded, out _))
            {
                _bindings = loaded;
                _saveBindings = true;
            }
            else
            {
                issues.Add(new SourceIssue(_bindingDirectory, "prompt_binding_store_unreadable",
                    "已保存的 prompt-file 绑定无法读取，本轮不沿用也不覆盖。", observedAt));
            }
        }

        var roots = ExpandRoots(settings.MailboxRoots);
        if (roots.Count == 0)
        {
            issues.Add(new SourceIssue("mailbox", "mailbox_roots_empty",
                "没有配置任务登记根，不能把空列表当成没有任务。", observedAt));
        }

        IReadOnlyList<ObservedProcess> live = Array.Empty<ObservedProcess>();
        Dictionary<int, int> parents = new();
        try
        {
            live = _snapshot?.List(cancellationToken) ?? Array.Empty<ObservedProcess>();
            foreach (var p in live)
            {
                if (p.Pid > 0)
                    parents[p.Pid] = p.ParentPid;
            }
        }
        catch (Exception ex)
        {
            issues.Add(new SourceIssue("process_snapshot", "process_snapshot_failed",
                "进程快照失败，进程状态按未知处理：" + ex.GetType().Name + ": " + ex.Message, observedAt));
        }

        var rules = settings.DiscoveryRules.Count > 0
            ? settings.DiscoveryRules
            : DefaultDiscoveryRules();

        var sources = ExpandRoots(settings.ClaudeMailboxRoots).Select(r => (Root: r, Claude: true))
            .Concat(roots.Select(r => (Root: r, Claude: false)))
            .Concat(ExpandRoots(settings.AdditionalSourcePaths).Where(r => Directory.Exists(Path.Combine(r, "jobs")))
                .Select(r => (Root: r, Claude: false)))
            .DistinctBy(r => r.Root, StringComparer.OrdinalIgnoreCase).ToList();
        if (sources.Count > 0)
        {
            var rotation = _rootRotation++ % sources.Count;
            sources = sources.Skip(rotation).Concat(sources.Take(rotation)).ToList();
        }
        // Current control, its task list, and closeout evidence share this budget
        // and are read before historical registrations can exhaust it.
        _currentRoot = null;
        _rootBudget = _budgetRemaining;
        await ReadObserverControlFirstAsync(settings, documents, issues, seen, observedAt, cancellationToken).ConfigureAwait(false);
        _currentRoot = null;
        for (var index = 0; index < sources.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = sources[index];
            _currentRoot = source.Root;
            var left = sources.Count - index;
            // Reserve one content-read for each remaining root when possible.
            // With fewer slots than roots, rotate the first root across refreshes.
            _rootBudget = _budgetRemaining >= left ? _budgetRemaining - left + 1 : Math.Min(1, _budgetRemaining);
            try
            {
                if (source.Claude)
                    await CollectClaudeRootAsync(source.Root, settings, documents, issues, seen, live, parents, rules,
                        boundPids, observedAt, cancellationToken).ConfigureAwait(false);
                else
                    await CollectRootAsync(source.Root, settings, documents, issues, seen, live, parents, rules,
                        boundPids, observedAt, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _partialRoots.Add(source.Root);
                issues.Add(new SourceIssue(source.Root, "mailbox_jobs_unreadable", "本根扫描失败；独立根继续采集：" + ex.GetType().Name, observedAt));
            }
        }
        _currentRoot = null;
        _rootBudget = _budgetRemaining;

        ValidateAdapterInputs(_pendingInputs, documents, issues, observedAt);
        foreach (var group in _pendingInputs.GroupBy(i => i.MailboxRoot, StringComparer.OrdinalIgnoreCase))
        {
            _currentRoot = group.Key;
            await BindJobs(group.ToList(), live, parents, rules, boundPids, documents, issues, seen, settings, observedAt, cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var doc in documents)
        {
            if (doc.Kind == KindMailboxRoot && _partialRoots.Contains(doc.Location)) doc.Data["scan_complete"] = false;
            if (doc.Kind == KindMailboxJob && (_partialRoots.Contains(Str(doc.Data, "mailbox_root") ?? "")
                || _partialJobs.Contains(ObservationKey(Str(doc.Data, "mailbox_root"), Str(doc.Data, "job_key"))))) doc.Data["collection_partial"] = true;
        }
        documents[0].Data["json_files_attempted"] = _jsonAttempts.Count;
        _currentRoot = null;
        _rootBudget = _budgetRemaining;
        await ReadObserverDecisionsAsync(settings, documents, issues, seen, observedAt, cancellationToken).ConfigureAwait(false);
        documents[0].Data["collection_partial"] = _partialRoots.Count > 0 || _partialJobs.Count > 0;
        documents[0].Data["root_collection_partial"] = _partialRoots.Count > 0;
        AnnotateActivity(documents, live, parents, observedAt);
        _activity.Commit(live);
        if (_saveBindings && _bindings is not null && _bindingDirectory is not null
            && !PromptFileBindingStore.TrySave(_bindingDirectory, _bindings))
        {
            issues.Add(new SourceIssue(_bindingDirectory, "prompt_binding_store_unwritable",
                "已验证的 prompt-file 绑定未能写入 CLI Watch 数据目录。本轮结果仍来自当前读取。", observedAt));
        }

        foreach (var process in live)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (boundPids.Contains(process.Pid)) continue;
            var family = ProcessMatch.Family(process, rules);
            if (family is null) continue;
            if (LooksLikeShellOrMcp(process)) continue;
            var id = KindUnboundProcess + ":" + process.Pid + ":" + process.StartTicks;
            if (!seen.Add(id)) continue;
            var unboundData = new JsonObject
            {
                ["pid"] = process.Pid,
                ["parent_pid"] = process.ParentPid,
                ["start_time_utc_ticks"] = process.StartTicks,
                ["started_at_utc"] = process.StartedAt.ToString("o"),
                ["image"] = process.ImageName,
                ["cli"] = family,
                ["command_present"] = !string.IsNullOrWhiteSpace(process.CommandLine)
            };
            StampObservedLaunch(unboundData, process, rules, CommandMentionsAnyJob(process.CommandLine, documents));
            documents.Add(new RawDocument(
                id,
                KindUnboundProcess,
                "process:" + process.Pid,
                unboundData,
                observedAt,
                process.StartedAt,
                null,
                "current"));
        }

        return new CollectionBatch(observedAt, documents, issues);
    }

    bool CanScan(string root, string path, List<SourceIssue> issues, DateTimeOffset observedAt)
    {
        if (_jsonCache.ContainsKey(ExpandPath(path))) return true;
        if (_budgetRemaining > 0 && _rootBudget > 0) return true;
        if (_partialRoots.Add(root))
            issues.Add(new SourceIssue(root, "scan_budget_exhausted", "JSON 内容读取预算已用尽；本根为部分快照，未扫描不代表结束或完成。", observedAt));
        return false;
    }

    void PartitionJobFiles(string root, IEnumerable<string> files, IReadOnlyList<ObservedProcess> live,
        List<string> currentFirst, List<string> closeoutNext, List<string> history)
    {
        foreach (var file in files)
        {
            if (MatchesMemberFile(root, file) || MatchesLiveContract(file, live))
                currentFirst.Add(file);
            else if (MatchesCloseoutFile(root, file)) closeoutNext.Add(file);
            else history.Add(file);
        }
    }

    IEnumerable<string> EnumeratePriorityThenHistory(string root, IReadOnlyList<string> currentFirst,
        IReadOnlyList<string> closeoutNext, IReadOnlyList<string> history)
    {
        foreach (var file in currentFirst) yield return file;
        foreach (var file in closeoutNext) yield return file;
        foreach (var file in ScanFiles(root, history)) yield return file;
    }

    bool MatchesMemberFile(string root, string file)
    {
        foreach (var member in _scopeMembers)
        {
            if (!PromptFileIdentity.SamePath(member.MailboxRoot, root)) continue;
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.Equals(member.JobId, StringComparison.Ordinal)
                || name.StartsWith(member.JobId + "--", StringComparison.Ordinal))
                return true;
            if (Path.GetFileName(file).Equals("job.json", StringComparison.OrdinalIgnoreCase))
            {
                if (TryStructuredAttempt(file, out var jobId, out _)
                    && jobId.Equals(member.JobId, StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }

    bool MatchesCloseoutFile(string root, string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var structured = TryStructuredAttempt(file, out var jobId, out var attempt);
        foreach (var pin in _closeoutPins)
        {
            if (!PromptFileIdentity.SamePath(pin.MailboxRoot, root)) continue;
            if (name.Equals(pin.Key, StringComparison.Ordinal)) return true;
            if (structured && pin.Key.Equals(jobId + "--" + attempt, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    static bool MatchesLiveContract(string file, IReadOnlyList<ObservedProcess> live)
    {
        if (!TryStructuredAttempt(file, out var jobId, out var attempt)) return false;
        foreach (var process in live)
        {
            var command = process.CommandLine;
            if (string.IsNullOrWhiteSpace(command)) continue;
            if (HasBoundedToken(command, "job_id：" + jobId) && HasBoundedToken(command, "attempt_id：" + attempt))
                return true;
        }
        return false;
    }

    static List<string> LivePromptAdapterIds(IReadOnlyList<ObservedProcess> live)
    {
        var ids = new List<string>();
        foreach (var process in live)
        {
            if (!PromptFileIdentity.TryInvocation(process.CommandLine, out var invocation)) continue;
            var directory = Path.GetDirectoryName(invocation.PromptPath);
            var name = string.IsNullOrWhiteSpace(directory) ? "" : Path.GetFileName(directory);
            if (!Guid.TryParse(name, out var id)) continue;
            var text = id.ToString();
            if (ids.Exists(existing => existing.Equals(text, StringComparison.OrdinalIgnoreCase))) continue;
            ids.Add(text);
        }

        return ids;
    }

    static string? LivePromptAdapterId(RawDocument job)
    {
        var id = Str(job.Data, "adapter_job_id");
        return !string.IsNullOrWhiteSpace(id) && Guid.TryParse(id, out var parsed) ? parsed.ToString() : null;
    }

    static bool TryStructuredAttempt(string file, out string jobId, out string attempt)
    {
        jobId = "";
        attempt = "";
        if (Path.GetFileName(file).Equals("job.json", StringComparison.OrdinalIgnoreCase))
        {
            var attemptDir = Path.GetDirectoryName(file);
            var jobDir = attemptDir is null ? null : Path.GetDirectoryName(attemptDir);
            if (jobDir is not null)
            {
                if (Path.GetFileName(jobDir).Equals("jobs", StringComparison.OrdinalIgnoreCase))
                {
                    jobId = Path.GetFileName(attemptDir) ?? "";
                    attempt = "1";
                    return jobId.Length > 0;
                }
                jobId = Path.GetFileName(jobDir);
                attempt = Path.GetFileName(attemptDir) ?? "";
                return jobId.Length > 0 && attempt.Length > 0;
            }
        }
        var name = Path.GetFileNameWithoutExtension(file);
        var split = name.LastIndexOf("--", StringComparison.Ordinal);
        if (split <= 0 || split + 2 >= name.Length) return false;
        jobId = name[..split];
        attempt = name[(split + 2)..];
        return jobId.Length > 0 && attempt.Length > 0;
    }

    static bool HasBoundedToken(string command, string token)
    {
        var index = 0;
        while ((index = command.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            var after = index + token.Length;
            if (after >= command.Length || !IsIdentityChar(command[after])) return true;
            index = after;
        }
        return false;
    }

    static bool IsIdentityChar(char value) => char.IsLetterOrDigit(value) || value is '-' or '_';

    IEnumerable<string> ScanFiles(string root, IEnumerable<string> files)
    {
        var offset = _scanOffsets.GetValueOrDefault(root);
        var read = 0;
        foreach (var file in files.Skip(offset).Concat(files.Take(offset)))
        {
            yield return file;
            read++;
            _scanOffsets[root] = offset + read;
        }
        _scanOffsets[root] = 0;
    }

    static IEnumerable<string> ClaudeJobFiles(string jobsDir, List<SourceIssue> issues, DateTimeOffset observedAt)
    {
        foreach (var jobDir in Directory.EnumerateDirectories(jobsDir))
        {
            if (IsReparse(jobDir))
            {
                issues.Add(new SourceIssue(jobDir, "claude_reparse_skipped", "Claude Lead任务目录是重解析点，未进入。", observedAt));
                continue;
            }
            var publicJob = Path.Combine(jobDir, "job.json");
            if (File.Exists(publicJob))
            {
                yield return publicJob;
                continue;
            }
            foreach (var attemptDir in Directory.EnumerateDirectories(jobDir))
            {
                if (IsReparse(attemptDir))
                {
                    issues.Add(new SourceIssue(attemptDir, "claude_reparse_skipped", "Claude Lead attempt 目录是重解析点，未进入。", observedAt));
                    continue;
                }
                var file = Path.Combine(attemptDir, "job.json");
                if (File.Exists(file)) yield return file;
            }
        }
    }

    async Task CollectRootAsync(
        string root,
        CollectorSettings settings,
        List<RawDocument> documents,
        List<SourceIssue> issues,
        HashSet<string> seen,
        IReadOnlyList<ObservedProcess> live,
        Dictionary<int, int> parents,
        IReadOnlyList<ProcessDiscoveryRule> rules,
        HashSet<int> boundPids,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        var stampedFrom = documents.Count;
        try
        {
        documents.Add(new RawDocument(
            KindMailboxRoot + ":" + root,
            KindMailboxRoot,
            root,
            new JsonObject
            {
                ["root"] = root,
                ["readable"] = Directory.Exists(root)
            },
            observedAt,
            observedAt,
            null,
            Directory.Exists(root) ? "current" : "unavailable"));

        if (!Directory.Exists(root))
        {
            issues.Add(new SourceIssue(root, "mailbox_root_missing",
                "登记根不存在或不可读，不能把空列表当成没有任务。", observedAt));
            return;
        }

        var routesPath = Path.Combine(root, "routes.json");
        RawDocument? routes = null;

        var jobsDir = Path.Combine(root, "jobs");
        if (!Directory.Exists(jobsDir))
        {
            issues.Add(new SourceIssue(jobsDir, "mailbox_jobs_missing",
                "jobs 目录不存在。若本根应有任务，当前不能当作空闲。", observedAt));
            return;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(jobsDir, "*.json");
        }
        catch (Exception ex)
        {
            issues.Add(new SourceIssue(jobsDir, "mailbox_jobs_unreadable",
                "无法读取 jobs 目录：" + ex.GetType().Name + ": " + ex.Message, observedAt));
            return;
        }

        var bindInputs = new List<JobBindInput>();
        var registrationFiles = files.ToList();
        var currentFirst = new List<string>();
        var closeoutNext = new List<string>();
        var history = new List<string>();
        PartitionJobFiles(root, registrationFiles, live, currentFirst, closeoutNext, history);
        var pendingAdapters = new HashSet<string>(LivePromptAdapterIds(live), StringComparer.OrdinalIgnoreCase);
        var deferredEvidence = new List<RawDocument>();
        var stop = false;

        async Task<(bool Stop, RawDocument? Job)> ReadRegistrationAsync(string file)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsDeniedName(file)) return (false, null);
            if (!CanScan(root, file, issues, observedAt)) return (true, null);
            var read = await TryReadJsonAsync(file, KindMailboxJob, null, documents, issues, seen, observedAt, cancellationToken).ConfigureAwait(false);
            if (read is null) return (false, null);

            var readProject = Str(read.Data, "project");
            var readKey = Str(read.Data, "key") ?? FileKey(file);
            read.Data["key"] = readKey;
            read.Data["job_key"] = readKey;
            if (ObservationIdentity.TryAttempt(read.Data, out var normalizedAttempt) && normalizedAttempt is not null)
                read.Data["attempt_id"] = normalizedAttempt;
            read.Data["mailbox_root"] = root;
            read = read with { ProjectHint = readProject ?? read.ProjectHint };
            ReplaceDoc(documents, read);
            if (MailboxRoles.IsNotificationOnly(Str(read.Data, "harness"))) return (false, null);
            return (false, read);
        }

        async Task FinishRegistrationAsync(RawDocument jobDoc)
        {
            var project = Str(jobDoc.Data, "project");
            var jobId = Str(jobDoc.Data, "job_id");
            var attempt = Str(jobDoc.Data, "attempt_id");
            var key = Str(jobDoc.Data, "key") ?? "";
            AttachRouteTitle(jobDoc, routes);

            var authorityPath = Str(jobDoc.Data, "owner_handoff_authority");
            var previousOwner = Str(jobDoc.Data, "previous_target_thread_id");
            var currentOwner = Str(jobDoc.Data, "target_thread_id");
            if (!string.IsNullOrWhiteSpace(authorityPath)
                && !string.IsNullOrWhiteSpace(previousOwner)
                && !string.IsNullOrWhiteSpace(currentOwner)
                && !previousOwner.Equals(currentOwner, StringComparison.Ordinal))
            {
                var expandedAuthorityPath = ExpandPath(authorityPath);
                if (Path.IsPathFullyQualified(expandedAuthorityPath))
                {
                    var authority = await TryReadJsonAsync(
                        expandedAuthorityPath, KindOwnerHandoffAuthority, project, documents, issues, seen,
                        observedAt, cancellationToken, key, issueIfMissing: false).ConfigureAwait(false);
                    if (authority is not null)
                    {
                        var verified = VerifyOwnerHandoff(jobDoc.Data, authority.Data);
                        jobDoc.Data["owner_handoff_verified"] = verified;
                        jobDoc.Data["owner_handoff_evidence_old_thread_id"] = Str(authority.Data, "old_thread_id");
                        jobDoc.Data["owner_handoff_evidence_new_thread_id"] = Str(authority.Data, "new_thread_id");
                        jobDoc.Data["owner_handoff_evidence_location"] = authority.Location;
                        ReplaceDoc(documents, jobDoc);
                    }
                }
            }

            var outbox = Str(jobDoc.Data, "outbox");
            await ResolveNativeAdapterAsync(jobDoc, project, key, documents, issues, seen, live, observedAt, cancellationToken).ConfigureAwait(false);
            var adapterState = Str(jobDoc.Data, "adapter_state_root");
            var adapterJob = Str(jobDoc.Data, "adapter_job_id");
            var receiptPath = Str(jobDoc.Data, "adapter_receipt");
            var resultPath = Str(jobDoc.Data, "adapter_result");

            var jobRoot = InferAdapterJobRoot(receiptPath, resultPath, adapterState, adapterJob);
            var ownerDoc = await TryReadJsonAsync(
                jobRoot is null ? null : Path.Combine(jobRoot, "owner.json"),
                KindAdapterOwner, project, documents, issues, seen, observedAt, cancellationToken, key).ConfigureAwait(false);
            var requestDoc = await TryReadJsonAsync(
                jobRoot is null ? null : Path.Combine(jobRoot, "request.json"),
                KindAdapterRequest, project, documents, issues, seen, observedAt, cancellationToken, key).ConfigureAwait(false);
            if (jobRoot is null)
                (ownerDoc, requestDoc) = await ReadNativeSupervisorAsync(jobDoc, project, key, documents, issues, seen, live, observedAt, cancellationToken).ConfigureAwait(false);
            await TryReadJsonAsync(
                receiptPath ?? (jobRoot is null ? null : Path.Combine(jobRoot, "receipt.json")),
                KindAdapterReceipt, project, documents, issues, seen, observedAt, cancellationToken, key).ConfigureAwait(false);
            await TryReadJsonAsync(
                jobRoot is null ? null : Path.Combine(jobRoot, "terminal.json"), KindAdapterTerminal, project,
                documents, issues, seen, observedAt, cancellationToken, key, issueIfMissing: false).ConfigureAwait(false);
            AddResultStat(resultPath ?? (jobRoot is null ? null : Path.Combine(jobRoot, "grok-result.json")),
                project, key, root, documents, issues, seen, observedAt);

            var readyPath = FindReadyPath(outbox);
            await TryReadJsonAsync(readyPath, KindOutboxReady, project, documents, issues, seen, observedAt, cancellationToken, key).ConfigureAwait(false);
            AddOutboxStat(outbox, project, key, documents, seen, observedAt);
            await ReadPersistedDeliveryAsync(
                root, key, jobId, attempt, project, documents, issues, seen, observedAt, cancellationToken).ConfigureAwait(false);

            NoteGrokActivity(settings, root, key, project, jobDoc, requestDoc, documents, issues, seen, observedAt);

            bindInputs.Add(new JobBindInput(jobDoc, ownerDoc, requestDoc, key, project, root));
        }

        async Task TakeFileAsync(string file, bool canDeferEvidence)
        {
            if (stop) return;
            var (stopped, jobDoc) = await ReadRegistrationAsync(file).ConfigureAwait(false);
            if (stopped)
            {
                stop = true;
                return;
            }

            if (jobDoc is null) return;
            var adapterId = LivePromptAdapterId(jobDoc);
            // Registration content is already budgeted. While a live prompt adapter is still
            // unmatched, keep historical extra files behind that registration's identity reads.
            if (canDeferEvidence && pendingAdapters.Count > 0 && (adapterId is null || !pendingAdapters.Contains(adapterId)))
            {
                deferredEvidence.Add(jobDoc);
                return;
            }

            await FinishRegistrationAsync(jobDoc).ConfigureAwait(false);
            if (adapterId is not null)
                pendingAdapters.Remove(adapterId);
        }

        foreach (var file in currentFirst)
        {
            await TakeFileAsync(file, canDeferEvidence: false).ConfigureAwait(false);
            if (stop) break;
        }

        if (!stop)
        {
            foreach (var file in closeoutNext)
            {
                await TakeFileAsync(file, canDeferEvidence: false).ConfigureAwait(false);
                if (stop) break;
            }
        }

        if (!stop)
        {
            foreach (var file in ScanFiles(root, history))
            {
                await TakeFileAsync(file, canDeferEvidence: true).ConfigureAwait(false);
                if (stop) break;
            }
        }

        foreach (var jobDoc in deferredEvidence)
            await FinishRegistrationAsync(jobDoc).ConfigureAwait(false);

        routes = await TryReadJsonAsync(routesPath, KindMailboxRoutes, null, documents, issues, seen, observedAt, cancellationToken).ConfigureAwait(false);
        foreach (var item in bindInputs) AttachRouteTitle(item.Job, routes);
        _pendingInputs.AddRange(bindInputs);
        }
        finally
        {
            StampMailboxRoot(documents, stampedFrom, root, issues, observedAt);
        }
    }

    async Task CollectClaudeRootAsync(
        string root,
        CollectorSettings settings,
        List<RawDocument> documents,
        List<SourceIssue> issues,
        HashSet<string> seen,
        IReadOnlyList<ObservedProcess> live,
        Dictionary<int, int> parents,
        IReadOnlyList<ProcessDiscoveryRule> rules,
        HashSet<int> boundPids,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        var stampedFrom = documents.Count;
        try
        {
            documents.Add(new RawDocument(
                KindMailboxRoot + ":claude:" + root,
                KindMailboxRoot,
                root,
                new JsonObject
                {
                    ["root"] = root,
                    ["readable"] = Directory.Exists(root),
                    ["source_kind"] = "claude_lead_mailbox",
                    ["source_label"] = "Claude Lead发起"
                },
                observedAt,
                observedAt,
                null,
                Directory.Exists(root) ? "current" : "unavailable"));

            if (!Directory.Exists(root))
            {
                issues.Add(new SourceIssue(root, "mailbox_root_missing",
                    "Claude Lead信箱根不存在或不可读，不能把空列表当成没有任务。", observedAt));
                return;
            }

            var jobsDir = Path.Combine(root, "jobs");
            if (!Directory.Exists(jobsDir))
            {
                issues.Add(new SourceIssue(jobsDir, "mailbox_jobs_missing",
                    "Claude Lead jobs 目录不存在。若本根应有任务，当前不能当作空闲。", observedAt));
                return;
            }

            var bindInputs = new List<JobBindInput>();
            var registrationFiles = ClaudeJobFiles(jobsDir, issues, observedAt).ToList();
            var currentFirst = new List<string>();
            var closeoutNext = new List<string>();
            var history = new List<string>();
            PartitionJobFiles(root, registrationFiles, live, currentFirst, closeoutNext, history);
            foreach (var jobFile in EnumeratePriorityThenHistory(root, currentFirst, closeoutNext, history))
            {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!CanScan(root, jobFile, issues, observedAt)) break;
                    var attemptDir = Path.GetDirectoryName(jobFile)!;
                    var publicLayout = Path.GetDirectoryName(attemptDir)!.Equals(jobsDir, StringComparison.OrdinalIgnoreCase);
                    var jobDir = publicLayout ? attemptDir : Path.GetDirectoryName(attemptDir)!;
                    if (!File.Exists(jobFile)) continue;
                    var dirJobId = Path.GetFileName(jobDir);
                    var dirAttempt = publicLayout ? "1" : Path.GetFileName(attemptDir);
                    var provisionalKey = dirJobId + "--" + dirAttempt;
                    var jobDoc = await TryReadJsonAsync(
                        jobFile, KindMailboxJob, null, documents, issues, seen, observedAt, cancellationToken, provisionalKey)
                        .ConfigureAwait(false);
                    if (jobDoc is null) continue;
                    if (Flag(jobDoc.Data, "corrupt"))
                    {
                        jobDoc.Data["source_kind"] = "claude_lead_mailbox";
                        jobDoc.Data["source_label"] = "Claude Lead发起";
                        jobDoc.Data["job_key"] = provisionalKey;
                        jobDoc.Data["key"] = provisionalKey;
                        jobDoc.Data["mailbox_root"] = root;
                        continue;
                    }

                    var project = Str(jobDoc.Data, "project");
                    var jsonJobId = Str(jobDoc.Data, "job_id");
                    var attempt = ScalarString(jobDoc.Data, "attempt_id") ?? ScalarString(jobDoc.Data, "attempt") ?? dirAttempt;
                    var jobId = string.IsNullOrWhiteSpace(jsonJobId) ? dirJobId : jsonJobId;
                    var identityConflict = false;
                    if (!string.IsNullOrWhiteSpace(jsonJobId)
                        && !jsonJobId.Equals(dirJobId, StringComparison.Ordinal))
                    {
                        issues.Add(new SourceIssue(jobFile, "claude_directory_identity_conflict",
                            "job.json 的 job_id 与目录名不一致。保留目录身份，不采用其声明的 adapter、交付目录或验收记录。", observedAt));
                        jobId = dirJobId;
                        identityConflict = true;
                    }

                    var jsonAttempt = ScalarString(jobDoc.Data, "attempt_id") ?? ScalarString(jobDoc.Data, "attempt");
                    if (!string.IsNullOrWhiteSpace(jsonAttempt)
                        && !jsonAttempt.Equals(dirAttempt, StringComparison.Ordinal))
                    {
                        issues.Add(new SourceIssue(jobFile, "claude_directory_identity_conflict",
                            "job.json 的 attempt 与目录名不一致。保留目录身份，不采用其声明的 adapter、交付目录或验收记录。", observedAt));
                        attempt = dirAttempt;
                        identityConflict = true;
                    }

                    var key = jobId + "--" + attempt;
                    var executor = Str(jobDoc.Data, "executor") ?? string.Empty;
                    jobDoc.Data["source_kind"] = "claude_lead_mailbox";
                    jobDoc.Data["source_label"] = "Claude Lead发起";
                    jobDoc.Data["registered_executor"] = executor;
                    jobDoc.Data["harness"] = MapClaudeHarness(executor);
                    jobDoc.Data["job_id"] = jobId;
                    jobDoc.Data["attempt_id"] = attempt;
                    jobDoc.Data["key"] = key;
                    jobDoc.Data["job_key"] = key;
                    jobDoc.Data["mailbox_root"] = root;
                    jobDoc.Data.Remove("notes");
                    var task = Str(jobDoc.Data, "task");
                    if (!string.IsNullOrWhiteSpace(task) && string.IsNullOrWhiteSpace(Str(jobDoc.Data, "task_summary")))
                        jobDoc.Data["task_summary"] = task;
                    var registeredModel = Str(jobDoc.Data, "model");
                    var registeredEffort = Str(jobDoc.Data, "effort");
                    if (!string.IsNullOrWhiteSpace(registeredModel))
                        jobDoc.Data["registered_model"] = registeredModel;
                    if (!string.IsNullOrWhiteSpace(registeredEffort))
                        jobDoc.Data["registered_effort"] = registeredEffort;
                    if (string.IsNullOrWhiteSpace(Str(jobDoc.Data, "registered_at")))
                    {
                        var dispatched = Str(jobDoc.Data, "dispatched_at");
                        if (!string.IsNullOrWhiteSpace(dispatched))
                        {
                            jobDoc.Data["registered_at"] = dispatched;
                            jobDoc.Data["registered_at_source"] = "dispatched_at";
                        }
                    }

                    if (identityConflict)
                    {
                        jobDoc.Data["directory_identity_conflict"] = true;
                        jobDoc.Data.Remove("outbox");
                        jobDoc.Data.Remove("state_root");
                        jobDoc.Data.Remove("adapter_state_root");
                        jobDoc.Data.Remove("adapter_job_id");
                        jobDoc = jobDoc with { ProjectHint = project ?? jobDoc.ProjectHint };
                        ReplaceDoc(documents, jobDoc);
                        continue;
                    }

                    var stateRoot = Str(jobDoc.Data, "state_root");
                    if (!string.IsNullOrWhiteSpace(stateRoot))
                        jobDoc.Data["adapter_state_root"] = stateRoot;
                    if (!string.IsNullOrWhiteSpace(stateRoot) && !string.IsNullOrWhiteSpace(jobId))
                        jobDoc.Data["adapter_job_id"] = jobId;

                    jobDoc = jobDoc with { ProjectHint = project ?? jobDoc.ProjectHint };
                    ReplaceDoc(documents, jobDoc);

                    if (MailboxRoles.IsNotificationOnly(Str(jobDoc.Data, "harness"))) continue;

                    var outbox = Str(jobDoc.Data, "outbox");
                    string? adapterDir = publicLayout ? jobDir : null;
                    if (!publicLayout && !string.IsNullOrWhiteSpace(stateRoot) && !string.IsNullOrWhiteSpace(jobId))
                    {
                        var candidate = Path.Combine(ExpandPath(stateRoot), "jobs", jobId);
                        if (Directory.Exists(candidate))
                            adapterDir = candidate;
                    }

                    var ownerDoc = await TryReadJsonAsync(
                        adapterDir is null ? null : Path.Combine(adapterDir, "owner.json"),
                        KindAdapterOwner, project, documents, issues, seen, observedAt, cancellationToken, key)
                        .ConfigureAwait(false);
                    var requestDoc = await TryReadJsonAsync(
                        adapterDir is null ? null : Path.Combine(adapterDir, "request.json"),
                        KindAdapterRequest, project, documents, issues, seen, observedAt, cancellationToken, key)
                        .ConfigureAwait(false);
                    var receiptDoc = await TryReadJsonAsync(
                        adapterDir is null ? null : Path.Combine(adapterDir, "receipt.json"),
                        KindAdapterReceipt, project, documents, issues, seen, observedAt, cancellationToken, key)
                        .ConfigureAwait(false);
                    await TryReadJsonAsync(
                        adapterDir is null ? null : Path.Combine(adapterDir, "terminal.json"), KindAdapterTerminal, project,
                        documents, issues, seen, observedAt, cancellationToken, key, issueIfMissing: false).ConfigureAwait(false);
                    var ownerOk = AdapterIdentityOk(ownerDoc, jobDoc.Data, "owner.json", issues, observedAt);
                    var requestOk = AdapterIdentityOk(requestDoc, jobDoc.Data, "request.json", issues, observedAt);
                    AdapterIdentityOk(receiptDoc, jobDoc.Data, "receipt.json", issues, observedAt);

                    var hostPid = Num(jobDoc.Data, "host_pid");
                    var ownerPid = ownerOk ? Num(ownerDoc?.Data, "pid") : null;
                    if (hostPid is > 0 && ownerDoc is null)
                    {
                        issues.Add(new SourceIssue(jobFile, "host_pid_not_cli_identity",
                            "host_pid 只是登记时的包装进程号，不能单独当成 CLI。", observedAt));
                    }
                    else if (hostPid is > 0 && ownerPid is > 0 && hostPid.Value != ownerPid.Value)
                    {
                        issues.Add(new SourceIssue(jobFile, "owner_host_pid_conflict",
                            "host_pid 与 owner.pid 不一致，绑定仍只用 owner 的 pid 和启动刻度。", observedAt));
                    }

                    var readyPath = FindReadyPath(outbox);
                    await TryReadJsonAsync(readyPath, KindOutboxReady, project, documents, issues, seen, observedAt, cancellationToken, key)
                        .ConfigureAwait(false);
                    AddAcceptanceStat(outbox, project, key, documents, seen, observedAt);
                    AddOutboxStat(outbox, project, key, documents, seen, observedAt);

                    if (executor.StartsWith("grok", StringComparison.OrdinalIgnoreCase) && requestOk)
                        NoteGrokActivity(settings, root, key, project, jobDoc, requestDoc, documents, issues, seen, observedAt);

                    bindInputs.Add(new JobBindInput(
                        jobDoc,
                        ownerDoc,
                        requestOk ? requestDoc : null,
                        key,
                        project,
                        root));
            }

            _pendingInputs.AddRange(bindInputs);
        }
        finally
        {
            StampMailboxRoot(documents, stampedFrom, root, issues, observedAt);
        }
    }

    sealed class JobBindInput
    {
        public JobBindInput(RawDocument job, RawDocument? owner, RawDocument? request, string key, string? project, string mailboxRoot)
        {
            Job = job;
            Owner = owner;
            Request = request;
            Key = key;
            Project = project;
            MailboxRoot = mailboxRoot;
        }

        public RawDocument Job { get; }
        public RawDocument? Owner { get; set; }
        public RawDocument? Request { get; set; }
        public string Key { get; }
        public string? Project { get; }
        public string MailboxRoot { get; }
    }

    async Task BindJobs(
        List<JobBindInput> inputs,
        IReadOnlyList<ObservedProcess> live,
        Dictionary<int, int> parents,
        IReadOnlyList<ProcessDiscoveryRule> rules,
        HashSet<int> boundPids,
        List<RawDocument> documents,
        List<SourceIssue> issues,
        HashSet<string> seen,
        CollectorSettings settings,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        var claimed = new HashSet<int>();
        var bound = new Dictionary<string, RawDocument>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in inputs)
        {
            var ownerState = OwnerState(item.Owner, live);
            if (ownerState.State != "alive" || ownerState.Live is null)
                continue;
            var doc = BindFromOwnerTree(item, ownerState.Live, live, parents, rules, observedAt, ownerState.State);
            bound[item.Key] = doc;
            ClaimTree(claimed, live, parents, doc);
        }

        // Native launches may not use an adapter owner file. An exact, unique
        // registered delivery contract in the real CLI invocation identifies the attempt;
        // project names, shared workspaces and a "started" note do not.
        foreach (var item in inputs)
        {
            if (bound.ContainsKey(item.Key) || OwnerState(item.Owner, live).State != "absent") continue;
            var registered = ParseTime(Str(item.Job.Data, "registered_at"));
            if (registered is null) continue;
            var outbox = Str(item.Job.Data, "outbox");
            var family = GuessFamily("", Str(item.Job.Data, "harness") ?? "");
            var matches = live.Where(p => !claimed.Contains(p.Pid) && !boundPids.Contains(p.Pid)
                && p.StartTicks > 0 && p.StartedAt >= registered.Value
                && !LooksLikeShellOrMcp(p)
                && string.Equals(ProcessMatch.Family(p, rules), family, StringComparison.OrdinalIgnoreCase)
                && DeclaresMailbox(p, item)
                && inputs.Count(other => DeclaresMailbox(p, other)) == 1
                && inputs.Count(other => string.Equals(
                    Str(other.Job.Data, "outbox")?.Replace('\\', '/').TrimEnd('/'),
                    outbox?.Replace('\\', '/').TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) == 1)
                .ToList();
            if (matches.Count != 1) continue;
            var doc = BindDoc(item, matches[0], "exact_registered_delivery_contract", "absent", 0, 0, rules, observedAt);
            bound[item.Key] = doc;
            ClaimTree(claimed, live, parents, doc);
        }

        await ApplyPromptFileBindingsAsync(
            inputs, live, parents, rules, claimed, boundPids, bound, documents, issues, seen, settings, observedAt, cancellationToken)
            .ConfigureAwait(false);

        foreach (var item in inputs)
        {
            if (bound.ContainsKey(item.Key)) continue;
            var ownerState = OwnerState(item.Owner, live);
            RawDocument doc;
            if (ownerState.State == "dead")
            {
                doc = BindNone(item, ownerState.State, observedAt, "owner_dead_no_session_fallback");
            }
            else if (ownerState.State == "alive")
            {
                doc = BindNone(item, ownerState.State, observedAt, "owner_alive_no_cli_child");
            }
            else if (ownerState.State == "absent")
            {
                doc = BindUnclaimedSession(item, live, claimed, rules, observedAt);
            }
            else
            {
                doc = BindNone(item, ownerState.State, observedAt, "owner_unknown");
            }

            bound[item.Key] = doc;
            ClaimTree(claimed, live, parents, doc);
        }

        foreach (var item in inputs)
        {
            if (!bound.TryGetValue(item.Key, out var doc)) continue;
            var bindId = KindProcessBind + ":" + item.MailboxRoot + ":" + item.Key;
            if (!seen.Add(bindId)) continue;
            documents.Add(doc with { Id = bindId, Location = "bind:" + item.MailboxRoot + ":" + item.Key });
            var cliPid = doc.Data["cli_pid"]?.GetValue<int>() ?? 0;
            var wrapperPid = doc.Data["wrapper_pid"]?.GetValue<int>() ?? 0;
            var cliAlive = Flag(doc.Data, "cli_alive");
            var wrapperAlive = Flag(doc.Data, "wrapper_alive");
            if (cliAlive && cliPid > 0) boundPids.Add(cliPid);
            if (wrapperAlive && wrapperPid > 0) boundPids.Add(wrapperPid);
            foreach (var p in live)
            {
                if (cliAlive && cliPid > 0 && ProcessMatch.IsDescendant(parents, p.Pid, cliPid))
                    boundPids.Add(p.Pid);
            }
        }
    }

    static bool DeclaresMailbox(ObservedProcess process, JobBindInput item) =>
        ProcessMatch.CommandDeclaresMailbox(process.CommandLine, Str(item.Job.Data, "project"),
            Str(item.Job.Data, "job_id"), Str(item.Job.Data, "attempt_id"), Str(item.Job.Data, "outbox"));

    void AnnotateActivity(
        List<RawDocument> documents,
        IReadOnlyList<ObservedProcess> live,
        Dictionary<int, int> parents,
        DateTimeOffset observedAt)
    {
        foreach (var doc in documents)
        {
            if (doc.Kind != KindProcessBind) continue;
            var cliPid = doc.Data["cli_pid"]?.GetValue<int>() ?? 0;
            if (cliPid <= 0)
            {
                doc.Data["activity_kind"] = "none";
                doc.Data["counters_available"] = false;
                continue;
            }

            var cliTicks = LongVal(doc.Data, "cli_start_ticks") ?? 0;
            ObservedProcess? cli = null;
            foreach (var p in live)
            {
                if (p.Pid == cliPid && (cliTicks == 0 || p.StartTicks == cliTicks))
                {
                    cli = p;
                    break;
                }
            }

            if (cli is null)
            {
                doc.Data["activity_kind"] = "none";
                doc.Data["counters_available"] = false;
                continue;
            }

            var tree = new List<ObservedProcess> { cli };
            foreach (var p in live)
            {
                if (p.Pid == cli.Pid) continue;
                if (ProcessMatch.IsDescendant(parents, p.Pid, cli.Pid))
                    tree.Add(p);
            }

            var anyAvailable = false;
            var anyBaseline = false;
            var selfBusy = false;
            var childBusy = false;
            long cpu = 0, read = 0, write = 0;
            foreach (var p in tree)
            {
                var delta = _activity.Diff(p);
                if (delta.CountersAvailable) anyAvailable = true;
                if (delta.HadBaseline) anyBaseline = true;
                if (!delta.AnyIncrease) continue;
                cpu += delta.CpuDelta;
                read += delta.ReadDelta;
                write += delta.WriteDelta;
                if (p.Pid == cli.Pid) selfBusy = true;
                else childBusy = true;
            }

            string kind;
            if (childBusy)
                kind = "child_io";
            else if (selfBusy)
                kind = "cli_compute";
            else if (!anyAvailable)
                kind = "unavailable";
            else if (!anyBaseline)
                kind = "baseline";
            else
                kind = "quiet";

            doc.Data["activity_kind"] = kind;
            doc.Data["counters_available"] = anyAvailable;
            doc.Data["had_counter_baseline"] = anyBaseline;
            doc.Data["cpu_delta_ticks"] = cpu;
            doc.Data["read_delta_bytes"] = read;
            doc.Data["write_delta_bytes"] = write;
            doc.Data["tree_size"] = tree.Count;
            doc.Data["activity_at"] = observedAt.ToString("o");
        }
    }

    (string State, ObservedProcess? Live, int Pid, long Ticks) OwnerState(RawDocument? owner, IReadOnlyList<ObservedProcess> live)
    {
        if (owner is null)
            return ("absent", null, 0, 0);
        if (Flag(owner.Data, "corrupt") || owner.Data["identity_ok"] is JsonValue identity && identity.TryGetValue<bool>(out var ok) && !ok)
            return ("unknown", null, 0, 0);
        var ownerPid = IntVal(owner.Data, "pid") ?? 0;
        var ownerTicks = LongVal(owner.Data, "start_time_utc_ticks") ?? 0;
        if (ownerPid <= 0)
            return ("absent", null, 0, 0);
        if (ownerTicks <= 0)
            return ("unknown", null, ownerPid, 0);

        var obs = _snapshot?.Observe(ownerPid, ownerTicks)
                  ?? _probe?.ObserveAsync(ownerPid, ownerTicks, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        var state = obs?.State ?? "unknown";
        ObservedProcess? liveOwner = null;
        if (state == "alive")
        {
            foreach (var p in live)
            {
                if (p.Pid == ownerPid && p.StartTicks == ownerTicks)
                {
                    liveOwner = p;
                    break;
                }
            }
        }

        return (state, liveOwner, ownerPid, ownerTicks);
    }

    async Task ApplyPromptFileBindingsAsync(
        List<JobBindInput> inputs,
        IReadOnlyList<ObservedProcess> live,
        Dictionary<int, int> parents,
        IReadOnlyList<ProcessDiscoveryRule> rules,
        HashSet<int> claimed,
        HashSet<int> boundPids,
        Dictionary<string, RawDocument> bound,
        List<RawDocument> documents,
        List<SourceIssue> issues,
        HashSet<string> seen,
        CollectorSettings settings,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        var hits = new List<(JobBindInput Item, ObservedProcess Process, PromptFileIdentity.PromptFileProof Proof)>();
        foreach (var process in live)
        {
            if (process.Pid <= 0 || process.StartTicks <= 0) continue;
            if (claimed.Contains(process.Pid) || boundPids.Contains(process.Pid)) continue;
            if (LooksLikeShellOrMcp(process)) continue;
            var family = ProcessMatch.Family(process, rules);
            if (family is null) continue;
            if (!PromptFileIdentity.TryInvocation(process.CommandLine, out var invocation)) continue;
            foreach (var item in inputs)
            {
                if (bound.ContainsKey(item.Key) || HasRegisteredAdapter(item.Job.Data)) continue;
                if (OwnerState(item.Owner, live).State != "absent") continue;
                var harness = Str(item.Job.Data, "harness") ?? "";
                if (!string.Equals(GuessFamily("", harness), family, StringComparison.OrdinalIgnoreCase)) continue;
                if (!PromptFileIdentity.TryMatch(item.Job.Data, item.Job.Location, invocation, process.StartedAt, out var proof))
                    continue;
                if (!proof.DirectDispatch && !proof.PiFileAttachment && !PromptFileIdentity.OwnerAllows(proof.AdapterDirectory, process, live, parents)) continue;
                hits.Add((item, process, proof));
            }
        }

        // Native PI @file is parsed by PI's option rules. session-dir is not the process cwd.
        foreach (var process in live)
        {
            if (process.Pid <= 0 || process.StartTicks <= 0) continue;
            if (claimed.Contains(process.Pid) || boundPids.Contains(process.Pid)) continue;
            if (LooksLikeShellOrMcp(process)) continue;
            if (!string.Equals(ProcessMatch.Family(process, rules), "PI", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!ProcessMatch.TryReadPiFileArgs(process, out var fileArgs) || fileArgs.Count == 0)
                continue;
            foreach (var item in inputs)
            {
                if (bound.ContainsKey(item.Key) || HasRegisteredAdapter(item.Job.Data)) continue;
                if (OwnerState(item.Owner, live).State != "absent") continue;
                var harness = Str(item.Job.Data, "harness") ?? "";
                if (!string.Equals(GuessFamily("", harness), "PI", StringComparison.OrdinalIgnoreCase)) continue;
                if (SavedPiAttachmentBlocks(item, process)) continue;
                if (!PromptFileIdentity.TryMatchPiAttachment(
                        item.Job.Data, item.Job.Location, fileArgs, process.StartedAt, out var proof))
                    continue;
                hits.Add((item, process, proof));
            }
        }

        foreach (var hit in UniquePromptHits(hits))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!hit.Proof.PiFileAttachment && hit.Proof.DirectDispatch)
                AttachDirectInvocation(hit.Item, hit.Proof.Workspace, hit.Proof.SessionId, hit.Proof.Model,
                    hit.Proof.ReasoningEffort, documents, issues, seen, settings, observedAt);
            else if (!hit.Proof.PiFileAttachment && !await AttachPromptAdapterAsync(
                hit.Item, hit.Proof.AdapterDirectory, documents, issues, seen, settings, observedAt, cancellationToken)
                .ConfigureAwait(false))
                continue;
            var ownerState = OwnerState(hit.Item.Owner, live);
            var doc = BindDoc(
                hit.Item, hit.Process, "prompt_file_request_identity", ownerState.State,
                ownerState.Pid, ownerState.Ticks, rules, observedAt);
            bound[hit.Item.Key] = doc;
            ClaimTree(claimed, live, parents, doc);
            RememberPromptBinding(hit.Item, hit.Proof, hit.Process, issues, observedAt);
        }

        if (_bindings is null) return;
        foreach (var item in inputs)
        {
            if (bound.ContainsKey(item.Key) || HasRegisteredAdapter(item.Job.Data)) continue;
            if (OwnerState(item.Owner, live).State != "absent") continue;
            if (!_bindings.TryGetValue(item.MailboxRoot + "\n" + item.Key, out var record)) continue;
            if (!PromptFileIdentity.StillVerified(record, item.Job.Data, item.Job.Location, item.Key, item.MailboxRoot))
            {
                bound[item.Key] = BindDoc(item, null, "prompt_file_degraded", "absent", 0, 0, rules, observedAt);
                continue;
            }

            var adapterDir = Path.GetDirectoryName(record.RequestPath);
            if (!record.PiFileAttachment && record.DirectDispatch)
                AttachDirectInvocation(item, record.Workspace, record.SessionId, record.Model,
                    record.ReasoningEffort, documents, issues, seen, settings, observedAt);
            else if (!record.PiFileAttachment && (string.IsNullOrWhiteSpace(adapterDir)
                || !await AttachPromptAdapterAsync(
                    item, adapterDir!, documents, issues, seen, settings, observedAt, cancellationToken).ConfigureAwait(false)))
            {
                bound[item.Key] = BindDoc(item, null, "prompt_file_degraded", "absent", 0, 0, rules, observedAt);
                continue;
            }

            ObservedProcess? cli = null;
            foreach (var process in live)
            {
                if (claimed.Contains(process.Pid) || boundPids.Contains(process.Pid)) continue;
                if (!PromptFileIdentity.IsSameLiveProcess(process, record)) continue;
                if (!record.DirectDispatch && !record.PiFileAttachment && !PromptFileIdentity.OwnerAllows(adapterDir!, process, live, parents)) continue;
                cli = process;
                break;
            }

            var restoredOwner = OwnerState(item.Owner, live);
            var restored = BindDoc(
                item, cli, "prompt_file_request_identity", restoredOwner.State,
                restoredOwner.Pid, restoredOwner.Ticks, rules, observedAt);
            // A missing snapshot row is not proof of exit (access may be unavailable).
            // Only an exact PID/start-time observation can confirm the old CLI ended.
            if (cli is null)
            {
                var previousProcess = _snapshot?.Observe(record.CliPid, record.CliStartTicks)
                    ?? (_probe is null ? null : await _probe.ObserveAsync(record.CliPid, record.CliStartTicks, cancellationToken).ConfigureAwait(false));
                restored.Data["observed_cli_exited"] = previousProcess?.State == "dead";
                restored.Data["observed_cli_unavailable"] = previousProcess?.State != "dead";
            }
            bound[item.Key] = restored;
            ClaimTree(claimed, live, parents, restored);
        }
    }

    static List<(JobBindInput Item, ObservedProcess Process, PromptFileIdentity.PromptFileProof Proof)> UniquePromptHits(
        List<(JobBindInput Item, ObservedProcess Process, PromptFileIdentity.PromptFileProof Proof)> hits)
    {
        var pidCount = new Dictionary<int, int>();
        var jobCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var hit in hits)
        {
            pidCount[hit.Process.Pid] = pidCount.TryGetValue(hit.Process.Pid, out var processes) ? processes + 1 : 1;
            jobCount[hit.Item.Key] = jobCount.TryGetValue(hit.Item.Key, out var jobs) ? jobs + 1 : 1;
        }

        var chosen = new List<(JobBindInput Item, ObservedProcess Process, PromptFileIdentity.PromptFileProof Proof)>();
        foreach (var hit in hits)
        {
            if (pidCount[hit.Process.Pid] != 1 || jobCount[hit.Item.Key] != 1) continue;
            chosen.Add(hit);
        }

        return chosen;
    }

    bool SavedPiAttachmentBlocks(JobBindInput item, ObservedProcess process)
    {
        if (_bindings is null || !_bindings.TryGetValue(item.MailboxRoot + "\n" + item.Key, out var saved))
            return false;
        if (!saved.PiFileAttachment) return false;
        if (saved.CliPid == process.Pid && saved.CliStartTicks != process.StartTicks)
            return true;
        return !PromptFileIdentity.StillVerified(saved, item.Job.Data, item.Job.Location, item.Key, item.MailboxRoot);
    }

    void RememberPromptBinding(
        JobBindInput item,
        PromptFileIdentity.PromptFileProof proof,
        ObservedProcess process,
        List<SourceIssue> issues,
        DateTimeOffset observedAt)
    {
        if (_bindings is null) return;
        var record = PromptFileBindingRecord.FromProof(
            item.MailboxRoot,
            Str(item.Job.Data, "project") ?? "",
            Str(item.Job.Data, "job_id") ?? "",
            Str(item.Job.Data, "attempt_id") ?? "",
            Str(item.Job.Data, "target_thread_id") ?? "",
            item.Key,
            proof,
            process.Pid,
            process.StartTicks,
            process.StartedAt);
        if (_bindings.Count >= 256 && !_bindings.ContainsKey(record.MapKey))
        {
            issues.Add(new SourceIssue(item.MailboxRoot, "prompt_binding_store_full",
                "已验证绑定超过保存上限，本轮不新增缓存。", observedAt));
            return;
        }

        _bindings[record.MapKey] = record;
    }

    async Task<bool> AttachPromptAdapterAsync(
        JobBindInput item,
        string adapterDir,
        List<RawDocument> documents,
        List<SourceIssue> issues,
        HashSet<string> seen,
        CollectorSettings settings,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        var stampedFrom = documents.Count;
        var owner = await TryReadJsonAsync(
            Path.Combine(adapterDir, "owner.json"),
            KindAdapterOwner, item.Project, documents, issues, seen, observedAt, cancellationToken, item.Key,
            issueIfMissing: false).ConfigureAwait(false);
        var request = await TryReadJsonAsync(
            Path.Combine(adapterDir, "request.json"),
            KindAdapterRequest, item.Project, documents, issues, seen, observedAt, cancellationToken, item.Key,
            issueIfMissing: false).ConfigureAwait(false);
        var receiptPath = Path.Combine(adapterDir, "receipt.json");
        if (File.Exists(receiptPath))
        {
            await TryReadJsonAsync(
                receiptPath, KindAdapterReceipt, item.Project, documents, issues, seen, observedAt, cancellationToken, item.Key,
                issueIfMissing: false).ConfigureAwait(false);
        }

        var resultPath = Path.Combine(adapterDir, "grok-result.json");
        if (File.Exists(resultPath))
            AddResultStat(resultPath, item.Project, item.Key, item.MailboxRoot, documents, issues, seen, observedAt);
        StampMailboxRoot(documents, stampedFrom, item.MailboxRoot, issues, observedAt);
        var adapterJob = (JsonObject)item.Job.Data.DeepClone();
        adapterJob["adapter_job_id"] = Path.GetFileName(adapterDir);
        if (!AdapterIdentityOk(owner, adapterJob, "owner.json", issues, observedAt)) return false;
        if (request is null || !AdapterIdentityOk(request, adapterJob, "request.json", issues, observedAt)) return false;
        foreach (var doc in documents.Skip(stampedFrom).Where(d => d.Kind == KindAdapterReceipt))
            AdapterIdentityOk(doc, adapterJob, "receipt.json", issues, observedAt);
        item.Request = request;
        item.Owner = owner;
        NoteGrokActivity(settings, item.MailboxRoot, item.Key, item.Project, item.Job, request, documents, issues, seen, observedAt);
        return true;
    }

    static bool HasRegisteredAdapter(JsonObject job) =>
        !string.IsNullOrWhiteSpace(Str(job, "adapter_state_root"))
        || !string.IsNullOrWhiteSpace(Str(job, "adapter_job_id"))
        || !string.IsNullOrWhiteSpace(Str(job, "adapter_receipt"))
        || !string.IsNullOrWhiteSpace(Str(job, "adapter_result"));

    void AttachDirectInvocation(
        JobBindInput item, string workspace, string session, string? model, string? effort,
        List<RawDocument> documents, List<SourceIssue> issues, HashSet<string> seen,
        CollectorSettings settings, DateTimeOffset observedAt)
    {
        var id = KindAdapterRequest + ":native:" + item.MailboxRoot + ":" + item.Key;
        var data = new JsonObject
        {
            ["job_key"] = item.Key, ["mailbox_root"] = item.MailboxRoot,
            ["workspace"] = workspace, ["session_id"] = session,
            ["model"] = model, ["reasoning_effort"] = effort,
            ["source"] = "observed_native_invocation"
        };
        var request = new RawDocument(id, KindAdapterRequest, "native-invocation:" + item.Key,
            data, observedAt, observedAt, item.Project, "current");
        item.Request = request;
        if (seen.Add(id)) documents.Add(request);
        NoteGrokActivity(settings, item.MailboxRoot, item.Key, item.Project, item.Job,
            request, documents, issues, seen, observedAt);
    }

    void NoteGrokActivity(
        CollectorSettings settings,
        string root,
        string key,
        string? project,
        RawDocument job,
        RawDocument? request,
        List<RawDocument> documents,
        List<SourceIssue> issues,
        HashSet<string> seen,
        DateTimeOffset observedAt)
    {
        var sessionId = Str(request?.Data, "session_id") ?? Str(job.Data, "resume_native_session_id");
        var workspace = Str(request?.Data, "workspace") ?? Str(job.Data, "workspace");
        var grokActivity = GrokSessionReader.TryRead(settings.GrokHome, workspace, sessionId, observedAt, issues);
        if (grokActivity is null) return;
        var actId = KindGrokActivity + ":" + root + ":" + key;
        if (!seen.Add(actId)) return;
        grokActivity["job_key"] = key;
        grokActivity["mailbox_root"] = root;
        documents.Add(new RawDocument(
            actId, KindGrokActivity,
            grokActivity["session_dir"]?.GetValue<string>() ?? "grok-session:" + sessionId,
            grokActivity, observedAt, ParseTime(Str(grokActivity, "last_active_at")) ?? observedAt,
            project, "current"));
    }

    RawDocument BindFromOwnerTree(
        JobBindInput item,
        ObservedProcess ownerLive,
        IReadOnlyList<ObservedProcess> live,
        Dictionary<int, int> parents,
        IReadOnlyList<ProcessDiscoveryRule> rules,
        DateTimeOffset observedAt,
        string ownerState)
    {
        var harness = Str(item.Job.Data, "harness") ?? "";
        var sessionId = Str(item.Request?.Data, "session_id") ?? Str(item.Job.Data, "resume_native_session_id");
        ObservedProcess? sessionHit = null;
        ObservedProcess? anyCli = null;
        var nativeVerified = Flag(item.Job.Data, "native_adapter_verified") || Flag(item.Job.Data, "native_supervisor_verified");
        var candidates = live.Where(p => p.Pid == ownerLive.Pid || ProcessMatch.IsDescendant(parents, p.Pid, ownerLive.Pid))
            .Where(p => !nativeVerified || ProcessMatch.CodexInvocationMatches(p,
                Str(item.Job.Data, "workspace"), Flag(item.Job.Data, "native_adapter_verified") ? sessionId : null,
                Flag(item.Job.Data, "native_supervisor_verified") ? Str(item.Job.Data, "outbox") : null)).ToList();
        if (nativeVerified && candidates.Count != 1)
            return BindDoc(item, null, "native_identity_ambiguous_or_unavailable", ownerState, ownerLive.Pid, ownerLive.StartTicks, rules, observedAt);
        foreach (var p in candidates)
        {
            if (!ProcessMatch.IsDescendant(parents, p.Pid, ownerLive.Pid) && p.Pid != ownerLive.Pid)
                continue;
            if (LooksLikeShellOrMcp(p)) continue;
            if (ProcessMatch.Family(p, rules) is null)
                continue;
            anyCli ??= p;
            if (!string.IsNullOrWhiteSpace(sessionId) && ProcessMatch.CommandContainsToken(p.CommandLine, sessionId))
            {
                sessionHit = p;
                break;
            }
        }

        var cli = sessionHit ?? anyCli;
        var how = sessionHit is not null ? "owner_descendant_session" : "owner_descendant";
        return BindDoc(item, cli, how, ownerState, ownerLive.Pid, ownerLive.StartTicks, rules, observedAt);
    }

    RawDocument BindUnclaimedSession(
        JobBindInput item,
        IReadOnlyList<ObservedProcess> live,
        HashSet<int> claimed,
        IReadOnlyList<ProcessDiscoveryRule> rules,
        DateTimeOffset observedAt)
    {
        var sessionId = Str(item.Request?.Data, "session_id") ?? Str(item.Job.Data, "resume_native_session_id");
        if (string.IsNullOrWhiteSpace(sessionId))
            return BindNone(item, "absent", observedAt, "no_owner_no_session");

        foreach (var p in live)
        {
            if (claimed.Contains(p.Pid)) continue;
            if (ProcessMatch.Family(p, rules) is null) continue;
            if (LooksLikeShellOrMcp(p)) continue;
            if (PromptFileIdentity.HasPromptFileSwitch(p.CommandLine)) continue;
            if (!ProcessMatch.CommandContainsToken(p.CommandLine, sessionId))
                continue;
            return BindDoc(item, p, "unclaimed_session", "absent", 0, 0, rules, observedAt);
        }

        return BindNone(item, "absent", observedAt, "no_owner_session_unclaimed");
    }

    RawDocument BindNone(JobBindInput item, string ownerState, DateTimeOffset observedAt, string how)
    {
        var ownerPid = item.Owner is null ? 0 : IntVal(item.Owner.Data, "pid") ?? 0;
        var ownerTicks = item.Owner is null ? 0 : LongVal(item.Owner.Data, "start_time_utc_ticks") ?? 0;
        return BindDoc(item, null, how, ownerState, ownerPid, ownerTicks, Array.Empty<ProcessDiscoveryRule>(), observedAt);
    }

    static RawDocument BindDoc(
        JobBindInput item,
        ObservedProcess? cli,
        string bindHow,
        string ownerState,
        int ownerPid,
        long ownerTicks,
        IReadOnlyList<ProcessDiscoveryRule> rules,
        DateTimeOffset observedAt)
    {
        var harness = Str(item.Job.Data, "harness") ?? "";
        var data = new JsonObject
        {
            ["job_key"] = item.Key,
            ["mailbox_root"] = item.MailboxRoot,
            ["wrapper_pid"] = ownerPid,
            ["wrapper_start_ticks"] = ownerTicks,
            ["wrapper_state"] = ownerState,
            ["bind_how"] = bindHow,
            ["cli_pid"] = cli?.Pid ?? 0,
            ["cli_start_ticks"] = cli?.StartTicks ?? 0,
            ["cli_image"] = cli?.ImageName,
            ["cli_family"] = cli is null ? null : (ProcessMatch.Family(cli, rules) ?? GuessFamily(cli.ImageName, harness)),
            ["cli_alive"] = cli is not null,
            ["wrapper_alive"] = ownerState == "alive",
            ["command_mentions_registered_job_id"] = cli is not null && CommandMentions(cli.CommandLine, Str(item.Job.Data, "job_id"))
        };
        if (cli is not null)
            StampObservedLaunch(data, cli, rules, Flag(data, "command_mentions_registered_job_id"));
        return new RawDocument(
            KindProcessBind + ":" + item.MailboxRoot + ":" + item.Key,
            KindProcessBind,
            "bind:" + item.MailboxRoot + ":" + item.Key,
            data,
            observedAt,
            cli?.StartedAt ?? ParseTime(Str(item.Owner?.Data, "started_at_utc")) ?? observedAt,
            item.Project,
            "current");
    }

    static void ClaimTree(HashSet<int> claimed, IReadOnlyList<ObservedProcess> live, Dictionary<int, int> parents, RawDocument bind)
    {
        var cliPid = bind.Data["cli_pid"]?.GetValue<int>() ?? 0;
        var wrapperPid = bind.Data["wrapper_pid"]?.GetValue<int>() ?? 0;
        if (cliPid > 0) claimed.Add(cliPid);
        if (wrapperPid > 0) claimed.Add(wrapperPid);
        foreach (var p in live)
        {
            if (cliPid > 0 && ProcessMatch.IsDescendant(parents, p.Pid, cliPid))
                claimed.Add(p.Pid);
            if (wrapperPid > 0 && ProcessMatch.IsDescendant(parents, p.Pid, wrapperPid))
                claimed.Add(p.Pid);
        }
    }

    async Task ReadPersistedDeliveryAsync(
        string mailboxRoot,
        string key,
        string? jobId,
        string? attempt,
        string? project,
        List<RawDocument> documents,
        List<SourceIssue> issues,
        HashSet<string> seen,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        var deliveryPath = Path.Combine(mailboxRoot, "deliveries", key + ".json");
        var delivery = await TryReadJsonAsync(
            deliveryPath, KindMailboxDelivery, project, documents, issues, seen, observedAt, cancellationToken, key,
            issueIfMissing: false).ConfigureAwait(false);
        if (delivery is not null)
        {
            var deliveryId = Str(delivery.Data, "id") ?? Path.GetFileNameWithoutExtension(deliveryPath);
            var identityOk = string.Equals(deliveryId, key, StringComparison.Ordinal);
            delivery.Data["identity_ok"] = identityOk;
            delivery.Data["job_key"] = key;
            if (!identityOk)
            {
                issues.Add(new SourceIssue(deliveryPath, "delivery_identity_mismatch",
                    "交付记录 id 与本轮 key 不一致，未当作本轮终态。", observedAt));
            }
        }

        var snapDir = Path.Combine(mailboxRoot, "snapshots", key);
        if (!Directory.Exists(snapDir))
            return;

        var manifestPath = Path.Combine(snapDir, "manifest.json");
        var manifest = await TryReadJsonAsync(
            manifestPath, KindMailboxSnapshot, project, documents, issues, seen, observedAt, cancellationToken, key,
            issueIfMissing: false).ConfigureAwait(false);
        var snapOk = true;
        if (manifest is not null)
        {
            var nested = manifest.Data["job"] as JsonObject;
            var snapKey = Str(nested, "key") ?? Str(manifest.Data, "key");
            var snapJob = Str(nested, "job_id") ?? Str(manifest.Data, "job_id");
            var snapAttempt = Str(nested, "attempt_id") ?? Str(manifest.Data, "attempt_id");
            snapOk = (string.IsNullOrWhiteSpace(snapKey) || snapKey.Equals(key, StringComparison.Ordinal))
                     && (string.IsNullOrWhiteSpace(jobId) || string.IsNullOrWhiteSpace(snapJob) || snapJob.Equals(jobId, StringComparison.Ordinal))
                     && (string.IsNullOrWhiteSpace(attempt) || string.IsNullOrWhiteSpace(snapAttempt) || snapAttempt.Equals(attempt, StringComparison.Ordinal));
            manifest.Data["identity_ok"] = snapOk;
            manifest.Data["job_key"] = key;
            if (!snapOk)
            {
                issues.Add(new SourceIssue(manifestPath, "snapshot_identity_mismatch",
                    "快照身份与本轮 job/attempt 不一致，未恢复终态。", observedAt));
            }
        }

        if (!snapOk)
            return;

        foreach (var readyFile in EnumerateSnapshotReadyFiles(snapDir))
        {
            var snapReady = await TryReadJsonAsync(
                readyFile, KindSnapshotReady, project, documents, issues, seen, observedAt, cancellationToken, key,
                issueIfMissing: false).ConfigureAwait(false);
            if (snapReady is null) continue;
            var readyJob = Str(snapReady.Data, "job_id");
            var readyAttempt = Str(snapReady.Data, "attempt_id");
            var readyOk = (string.IsNullOrWhiteSpace(jobId) || string.IsNullOrWhiteSpace(readyJob) || readyJob.Equals(jobId, StringComparison.Ordinal))
                          && (string.IsNullOrWhiteSpace(attempt) || string.IsNullOrWhiteSpace(readyAttempt) || readyAttempt.Equals(attempt, StringComparison.Ordinal));
            snapReady.Data["identity_ok"] = readyOk;
            snapReady.Data["job_key"] = key;
            snapReady.Data["from_snapshot"] = true;
            if (!readyOk)
            {
                issues.Add(new SourceIssue(readyFile, "snapshot_ready_identity_mismatch",
                    "快照 READY 的 job/attempt 与本轮不一致，未恢复终态。", observedAt));
            }

            break;
        }
    }

    static IEnumerable<string> EnumerateSnapshotReadyFiles(string snapDir)
    {
        string[] files;
        try { files = Directory.GetFiles(snapDir, "*.json"); }
        catch (Exception) { yield break; }

        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (name.Contains("READY", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("tmp", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                yield return file;
            }
        }
    }

    async Task<RawDocument?> TryReadJsonAsync(
        string? path,
        string kind,
        string? projectHint,
        List<RawDocument> documents,
        List<SourceIssue> issues,
        HashSet<string> seen,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken,
        string? jobKey = null,
        bool issueIfMissing = true)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var normalized = ExpandPath(path);
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        var documentId = kind + ":" + normalized;
        if (!string.IsNullOrWhiteSpace(jobKey))
            documentId += ":" + _currentRoot + ":" + jobKey;
        if (!seen.Add(documentId))
            return documents.FirstOrDefault(d => d.Id == documentId);

        if (IsDeniedName(normalized))
        {
            NoteJobIssue(issues, normalized, "denied_path", "路径在拒绝名单中，已跳过。", observedAt, jobKey, kind);
            return null;
        }

        if (!File.Exists(normalized))
        {
            if (issueIfMissing && kind is KindAdapterOwner or KindAdapterRequest or KindAdapterReceipt or KindOutboxReady)
            {
                NoteJobIssue(issues, normalized, "optional_missing",
                    "scope=current;project=" + (projectHint ?? "unspecified") + ";axis=" + kind + "; 引用文件当前不存在。",
                    observedAt, jobKey, kind);
            }

            return null;
        }

        FileInfo info;
        try { info = new FileInfo(normalized); }
        catch (Exception ex)
        {
            NoteJobIssue(issues, normalized, "stat_failed", ex.GetType().Name + ": " + ex.Message, observedAt, jobKey, kind);
            return null;
        }

        if (info.Length == 0)
        {
            NoteJobIssue(issues, normalized, "empty_file",
                kind + " 文件存在但是空的（运行中结果文件经常如此，不能据此判停滞）。", observedAt, jobKey, kind);
            return null;
        }

        if (!_jsonCache.ContainsKey(normalized) && !_jsonAttempts.Contains(normalized))
        {
            if (!CanScan(_currentRoot ?? "mailbox", normalized, issues, observedAt)) return null;
            _jsonAttempts.Add(normalized);
            _budgetRemaining--;
            _rootBudget--;
        }
        var maxBytes = Math.Min(_maxJsonBytes, kind == KindOwnerHandoffAuthority ? 65_536 : 1_048_576);
        if (info.Length > maxBytes)
        {
            MarkPartialRead(kind, normalized, jobKey);
            NoteJobIssue(issues, normalized, "too_large", "文件过大，未整读。", observedAt, jobKey, kind);
            return null;
        }

        string text;
        try
        {
            if (!_jsonCache.TryGetValue(normalized, out text!))
            {
                // Stat is only an early rejection. Bound the read itself against growth/replace races.
                _contentReads.Add(normalized);
                await using var stream = new FileStream(normalized, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, true);
                var bytes = new byte[maxBytes + 1];
                var length = 0;
                while (length < bytes.Length)
                {
                    var read = await stream.ReadAsync(bytes.AsMemory(length), cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    length += read;
                }
                if (length > maxBytes)
                {
                    MarkPartialRead(kind, normalized, jobKey);
                    NoteJobIssue(issues, normalized, "too_large", "文件在读取时超过配置字节预算，未解析。", observedAt, jobKey, kind);
                    return null;
                }
                text = System.Text.Encoding.UTF8.GetString(bytes, 0, length).TrimStart('\uFEFF');
                _jsonCache[normalized] = text;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            NoteJobIssue(issues, normalized, "read_failed", ex.GetType().Name + ": " + ex.Message, observedAt, jobKey, kind);
            return null;
        }

        JsonNode? parsed;
        try { parsed = JsonNode.Parse(text); }
        catch (JsonException ex)
        {
            NoteJobIssue(issues, normalized, "parse_error",
                "JSON 损坏或半写：" + ex.Message, observedAt, jobKey, kind);
            var fallbackKey = jobKey ?? Path.GetFileNameWithoutExtension(normalized);
            var unknown = new RawDocument(
                documentId, kind, normalized,
                new JsonObject
                {
                    ["corrupt"] = true,
                    ["job_key"] = fallbackKey,
                    ["job_id"] = fallbackKey,
                    ["project"] = projectHint ?? "unspecified"
                },
                observedAt, ToOffset(info.LastWriteTimeUtc), projectHint, "current");
            documents.Add(unknown);
            return unknown;
        }

        if (parsed is not JsonObject obj)
        {
            NoteJobIssue(issues, normalized, "not_object", "JSON 根不是对象。", observedAt, jobKey, kind);
            return null;
        }

        var data = kind == KindOwnerHandoffAuthority
            ? JsonAllowlist.CopyOwnerHandoffAuthority(obj)
            : kind == "grok_metadata" ? JsonAllowlist.CopySessionMetadata(obj)
            : JsonAllowlist.Copy(obj);
        if (kind == KindOutboxReady)
            data.Remove("notes");
        var badField = kind == "grok_metadata" ? null : JsonAllowlist.InvalidMetadataField(obj);
        if (badField is not null)
        {
            NoteJobIssue(issues, normalized, "schema_error", "受控元数据字段类型或别名冲突：" + badField + "；本文件隔离。", observedAt, jobKey, kind);
            data = new JsonObject { ["corrupt"] = true, ["identity_ok"] = false };
        }
        if (!string.IsNullOrWhiteSpace(jobKey))
        {
            var declaredKey = Str(data, "job_key");
            var declaredIndex = Str(data, "key");
            if (declaredKey is not null && declaredKey != jobKey || declaredIndex is not null && declaredIndex != jobKey)
            {
                data["identity_ok"] = false;
                data["corrupt"] = true;
                NoteJobIssue(issues, normalized, "context_identity_conflict", "文档声明 key 与采集上下文不一致；不采用其状态。", observedAt, jobKey, kind);
            }
            data["job_key"] = jobKey;
        }
        if (ObservationIdentity.TryAttempt(data, out var normalizedAttempt) && normalizedAttempt is not null)
            data["attempt_id"] = normalizedAttempt;
        var factAt = ParseTime(Str(data, "registered_at", "created_at_utc", "completed_at_utc", "updated_at", "started_at_utc"))
                     ?? ToOffset(info.LastWriteTimeUtc);
        var doc = new RawDocument(documentId, kind, normalized, data, observedAt, factAt, projectHint, "current");
        documents.Add(doc);
        return doc;
    }

    void AddResultStat(
        string? path,
        string? project,
        string key,
        string mailboxRoot,
        List<RawDocument> documents,
        List<SourceIssue> issues,
        HashSet<string> seen,
        DateTimeOffset observedAt)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var normalized = ExpandPath(path);
        var id = KindAdapterResultStat + ":" + normalized;
        if (!seen.Add(id)) return;
        if (!File.Exists(normalized))
        {
            issues.Add(new SourceIssue(normalized, "optional_missing",
                "scope=current;project=" + (project ?? "unspecified") + ";axis=adapter_result; 结果文件当前不存在。",
                observedAt, mailboxRoot, key));
            return;
        }

        try
        {
            var info = new FileInfo(normalized);
            documents.Add(new RawDocument(
                id, KindAdapterResultStat, normalized,
                new JsonObject
                {
                    ["job_key"] = key,
                    ["bytes"] = info.Length,
                    ["mtime"] = ToOffset(info.LastWriteTimeUtc).ToString("o"),
                    ["empty"] = info.Length == 0
                },
                observedAt, ToOffset(info.LastWriteTimeUtc), project, "current"));
        }
        catch (Exception ex)
        {
            issues.Add(new SourceIssue(normalized, "stat_failed", ex.GetType().Name + ": " + ex.Message, observedAt, mailboxRoot, key));
        }
    }

    void NoteJobIssue(List<SourceIssue> issues, string source, string code, string detail, DateTimeOffset observedAt, string? jobKey, string kind)
    {
        var key = jobKey ?? (kind == KindMailboxJob ? FileKey(source) : null);
        var root = string.IsNullOrWhiteSpace(key) ? null : _currentRoot;
        if (string.IsNullOrWhiteSpace(root)) key = null;
        issues.Add(new SourceIssue(source, code, detail, observedAt, root, key));
    }

    static readonly HashSet<string> RegistrationOutboxNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "DISPATCH.txt", "ACCESS.json"
    };

    const int MaxOutboxStatFiles = 32;

    static void AddOutboxStat(
        string? outbox,
        string? project,
        string key,
        List<RawDocument> documents,
        HashSet<string> seen,
        DateTimeOffset observedAt)
    {
        if (string.IsNullOrWhiteSpace(outbox)) return;
        var dir = ExpandPath(outbox);
        if (!Directory.Exists(dir)) return;

        var added = 0;
        void Consider(string path)
        {
            if (added >= MaxOutboxStatFiles) return;
            if (IsDeniedName(path)) return;
            var name = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(name) || RegistrationOutboxNames.Contains(name))
                return;
            if (name.Equals("ACCEPTANCE.md", StringComparison.OrdinalIgnoreCase))
                return;
            var id = "outbox_file:" + path;
            if (!seen.Add(id)) return;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return;
                documents.Add(new RawDocument(
                    id, "outbox_file", path,
                    new JsonObject
                    {
                        ["job_key"] = key,
                        ["name"] = name,
                        ["bytes"] = info.Length,
                        ["mtime"] = ToOffset(info.LastWriteTimeUtc).ToString("o"),
                        ["activity"] = true
                    },
                    observedAt, ToOffset(info.LastWriteTimeUtc), project, "current"));
                added++;
            }
            catch (Exception)
            {
            }
        }

        try
        {
            foreach (var file in Directory.GetFiles(dir))
                Consider(file);
            if (added < MaxOutboxStatFiles)
            {
                foreach (var sub in Directory.GetDirectories(dir))
                {
                    if (added >= MaxOutboxStatFiles) break;
                    var leaf = Path.GetFileName(sub);
                    if (string.IsNullOrWhiteSpace(leaf) || leaf.StartsWith(".", StringComparison.Ordinal))
                        continue;
                    try
                    {
                        foreach (var file in Directory.GetFiles(sub))
                        {
                            Consider(file);
                            if (added >= MaxOutboxStatFiles) break;
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }
        catch (Exception)
        {
        }
    }

    static string? FindReadyPath(string? outbox)
    {
        if (string.IsNullOrWhiteSpace(outbox)) return null;
        var dir = ExpandPath(outbox);
        var ready = Path.Combine(dir, "READY.json");
        if (File.Exists(ready)) return ready;
        return null;
    }

    static string? InferAdapterJobRoot(string? receipt, string? result, string? stateRoot, string? adapterJobId)
    {
        foreach (var path in new[] { receipt, result })
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            var dir = Path.GetDirectoryName(ExpandPath(path));
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                return dir;
        }

        if (!string.IsNullOrWhiteSpace(stateRoot) && !string.IsNullOrWhiteSpace(adapterJobId))
        {
            var dir = Path.Combine(ExpandPath(stateRoot), "jobs", adapterJobId);
            if (Directory.Exists(dir)) return dir;
        }

        return null;
    }

    static void AttachRouteTitle(RawDocument job, RawDocument? routes)
    {
        if (routes?.Data["projects"] is not JsonObject projects) return;
        var project = Str(job.Data, "project");
        if (string.IsNullOrWhiteSpace(project)) return;
        if (projects[project] is JsonObject node)
        {
            var title = Str(node, "title");
            if (!string.IsNullOrWhiteSpace(title))
                job.Data["route_title"] = title;
            var status = Str(node, "status");
            if (!string.IsNullOrWhiteSpace(status))
                job.Data["route_status"] = status;
        }
    }

    static void StampMailboxRoot(List<RawDocument> documents, int fromIndex, string root, List<SourceIssue> issues, DateTimeOffset observedAt)
    {
        for (var i = fromIndex; i < documents.Count; i++)
        {
            var data = documents[i].Data;
            foreach (var field in new[] { "mailbox_root", "root" })
                if (Str(data, field) is { } declared && !PromptFileIdentity.SamePath(declared, root))
                {
                    data["identity_ok"] = false;
                    data["corrupt"] = true;
                    issues.Add(new SourceIssue(documents[i].Location, "context_identity_conflict", "文档声明根与采集根不一致；不采用其状态。", observedAt));
                }
            data["mailbox_root"] = root;
        }
    }

    static void ReplaceDoc(List<RawDocument> documents, RawDocument job)
    {
        for (var i = 0; i < documents.Count; i++)
        {
            if (documents[i].Id == job.Id)
            {
                documents[i] = job;
                return;
            }
        }
    }

    static bool IsLikelyCliImage(string image, string harness)
    {
        var name = Path.GetFileNameWithoutExtension(image);
        if (name.Equals("grok", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("claude", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("codex", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Contains("cursor-agent", StringComparison.OrdinalIgnoreCase)) return true;
        if (harness.Contains("grok", StringComparison.OrdinalIgnoreCase) && name.Equals("grok", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    static void StampObservedLaunch(
        JsonObject data,
        ObservedProcess process,
        IReadOnlyList<ProcessDiscoveryRule> rules,
        bool mentionsRegisteredJob)
    {
        data["command_mentions_registered_job_id"] = mentionsRegisteredJob;
        var codex = ProcessMatch.CodexArguments(process);
        if (!string.IsNullOrWhiteSpace(codex.Model)) data["observed_model"] = codex.Model;
        if (!string.IsNullOrWhiteSpace(codex.Effort)) data["observed_effort"] = codex.Effort;
        if (!ProcessMatch.TryReadPiLaunch(process, rules, out var pi))
            return;
        if (!string.IsNullOrWhiteSpace(pi.LaunchScript))
            data["launch_script"] = pi.LaunchScript;
        if (!string.IsNullOrWhiteSpace(pi.Model))
            data["observed_model"] = pi.Model;
        if (!string.IsNullOrWhiteSpace(pi.Effort))
            data["observed_effort"] = pi.Effort;
        data["has_session_dir"] = pi.HasSessionDir;
        data["has_prompt_file"] = pi.HasPromptFile;
    }

    static bool CommandMentions(string? command, string? jobId) =>
        !string.IsNullOrWhiteSpace(command)
        && !string.IsNullOrWhiteSpace(jobId)
        && command.Contains(jobId, StringComparison.Ordinal);

    static bool CommandMentionsAnyJob(string? command, List<RawDocument> documents)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        foreach (var doc in documents)
        {
            if (doc.Kind != KindMailboxJob) continue;
            if (CommandMentions(command, Str(doc.Data, "job_id")))
                return true;
        }

        return false;
    }

    static bool Flag(JsonObject data, string key) =>
        data[key] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    static bool AdapterIdentityOk(
        RawDocument? doc, JsonObject job, string label, List<SourceIssue> issues, DateTimeOffset observedAt)
    {
        if (doc is null) return true;
        var ok = !Flag(doc.Data, "corrupt") && ObservationIdentity.Matches(doc.Data, job, adapterIdentity: true);
        doc.Data["identity_ok"] = ok;
        if (!ok)
            issues.Add(new SourceIssue(doc.Location, "adapter_identity_conflict",
                label + " 的 job/attempt 与登记不一致；不用其进程、模型、会话或终态。", observedAt));
        return ok;
    }

    static void ValidateAdapterInputs(List<JobBindInput> inputs, List<RawDocument> documents, List<SourceIssue> issues, DateTimeOffset observedAt)
    {
        foreach (var item in inputs)
        {
            var related = documents.Where(d => Str(d.Data, "job_key") == item.Key
                && PromptFileIdentity.SamePath(Str(d.Data, "mailbox_root"), item.MailboxRoot)).ToList();
            foreach (var doc in related.Where(d => d.Kind is KindAdapterOwner or KindAdapterRequest or KindAdapterReceipt))
                AdapterIdentityOk(doc, item.Job.Data, doc.Kind, issues, observedAt);
            foreach (var terminal in related.Where(d => d.Kind == KindAdapterTerminal))
            {
                var owner = item.Owner;
                var pid = Num(terminal.Data, "pid");
                var ticks = Num(terminal.Data, "start_time_utc_ticks");
                var finished = ParseTime(Str(terminal.Data, "finished_at_utc"));
                var ok = !Flag(terminal.Data, "corrupt") && owner is not null && Flag(owner.Data, "identity_ok")
                    && ObservationIdentity.Matches(terminal.Data, item.Job.Data, requireIdentity: true, adapterIdentity: true)
                    && ObservationIdentity.Matches(owner.Data, item.Job.Data, requireIdentity: true, adapterIdentity: true)
                    && pid is > 0 and <= int.MaxValue && ticks is > 0
                    && pid == Num(owner.Data, "pid") && ticks == Num(owner.Data, "start_time_utc_ticks")
                    && terminal.Data["pid"] is JsonValue pv && pv.TryGetValue<int>(out _)
                    && terminal.Data["start_time_utc_ticks"] is JsonValue tv && tv.TryGetValue<long>(out _)
                    && terminal.Data["exit_code"] is JsonValue ev && ev.TryGetValue<int>(out _)
                    && finished is not null && finished.Value <= observedAt
                    && finished.Value.UtcTicks >= ticks.Value;
                terminal.Data["identity_ok"] = ok;
                if (!ok)
                    issues.Add(new SourceIssue(terminal.Location, "terminal_identity_conflict", "terminal.json 缺少或冲突 job/attempt、owner 启动身份、退出码或结束时间；不采用终态。", observedAt));
            }
            if (item.Request is not null && !Flag(item.Request.Data, "identity_ok"))
            {
                item.Request = null;
                foreach (var doc in related.Where(d => d.Kind == KindGrokActivity)) doc.Data["identity_ok"] = false;
            }
            if (related.Any(d => d.Kind is KindAdapterOwner or KindAdapterRequest or KindAdapterReceipt && !Flag(d.Data, "identity_ok")))
                foreach (var doc in related.Where(d => d.Kind == KindAdapterResultStat)) doc.Data["identity_ok"] = false;
            foreach (var doc in related.Where(d => d.Kind is KindOutboxReady or KindSnapshotReady))
            {
                if (Flag(doc.Data, "corrupt") || !ObservationIdentity.Matches(doc.Data, item.Job.Data))
                {
                    doc.Data["identity_ok"] = false;
                    issues.Add(new SourceIssue(doc.Location, "ready_identity_conflict", "READY 的 job/attempt 与采集登记不一致；未采用交回状态。", observedAt));
                }
            }
        }

        // A legacy owner with no attempt is usable only for an independent adapter
        // contract. Conflicting registrations cannot both claim one launch identity.
        var owners = inputs.Where(i => i.Owner is not null && Flag(i.Owner.Data, "identity_ok"));
        foreach (var group in owners.GroupBy(i => (Num(i.Owner!.Data, "pid"), Num(i.Owner.Data, "start_time_utc_ticks"))))
        {
            if (group.Key.Item1 is not > 0 || group.Key.Item2 is not > 0 || group.Count() < 2) continue;
            foreach (var item in group)
            {
                item.Owner!.Data["identity_ok"] = false;
                item.Job.Data["adapter_identity_conflict"] = true;
                item.Request = null;
                foreach (var doc in documents.Where(d => Str(d.Data, "job_key") == item.Key
                    && PromptFileIdentity.SamePath(Str(d.Data, "mailbox_root"), item.MailboxRoot)
                    && d.Kind is KindAdapterRequest or KindAdapterReceipt or KindAdapterTerminal or KindAdapterResultStat or KindGrokActivity))
                    doc.Data["identity_ok"] = false;
                issues.Add(new SourceIssue(item.Job.Location, "owner_claim_conflict", "多个登记竞争同一 PID/启动刻度；不猜所属轮次。", observedAt));
            }
        }
        foreach (var group in inputs.Where(i => HasRegisteredAdapter(i.Job.Data))
            .GroupBy(i => InferAdapterJobRoot(Str(i.Job.Data, "adapter_receipt"), Str(i.Job.Data, "adapter_result"),
                Str(i.Job.Data, "adapter_state_root"), Str(i.Job.Data, "adapter_job_id")), StringComparer.OrdinalIgnoreCase))
        {
            if (group.Key is null || group.Count() < 2) continue;
            foreach (var item in group)
                foreach (var doc in documents.Where(d => Str(d.Data, "job_key") == item.Key
                    && PromptFileIdentity.SamePath(Str(d.Data, "mailbox_root"), item.MailboxRoot)
                    && d.Kind is KindAdapterOwner or KindAdapterRequest or KindAdapterReceipt or KindAdapterTerminal or KindAdapterResultStat or KindGrokActivity))
                    if (!ObservationIdentity.TryAttempt(doc.Data, out var attempt) || attempt is null)
                    {
                        doc.Data["identity_ok"] = false;
                        item.Job.Data["adapter_identity_conflict"] = true;
                        if (doc == item.Request) item.Request = null;
                        issues.Add(new SourceIssue(doc.Location, "adapter_attempt_conflict", "共享 adapter 缺少独立轮次身份；不借用旧轮次证据。", observedAt));
                    }
        }
    }

    static void AddAcceptanceStat(
        string? outbox,
        string? project,
        string key,
        List<RawDocument> documents,
        HashSet<string> seen,
        DateTimeOffset observedAt)
    {
        if (string.IsNullOrWhiteSpace(outbox)) return;
        var path = Path.Combine(ExpandPath(outbox), "ACCEPTANCE.md");
        var id = KindAcceptanceRecord + ":" + path;
        if (!seen.Add(id) || !File.Exists(path)) return;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return;
            documents.Add(new RawDocument(
                id,
                KindAcceptanceRecord,
                path,
                new JsonObject
                {
                    ["job_key"] = key,
                    ["name"] = "ACCEPTANCE.md",
                    ["body_read"] = false,
                    ["bytes"] = info.Length,
                    ["mtime"] = ToOffset(info.LastWriteTimeUtc).ToString("o")
                },
                observedAt,
                ToOffset(info.LastWriteTimeUtc),
                project,
                "current"));
        }
        catch (Exception)
        {
        }
    }

    static string MapClaudeHarness(string executor)
    {
        var value = executor.Trim();
        if (value.StartsWith("grok", StringComparison.OrdinalIgnoreCase)) return "grok-cli";
        if (value.Equals("pi", StringComparison.OrdinalIgnoreCase) || value.StartsWith("pi-", StringComparison.OrdinalIgnoreCase))
            return "pi-cli";
        if (value.StartsWith("cursor", StringComparison.OrdinalIgnoreCase)) return "cursor-cli";
        return string.IsNullOrWhiteSpace(value) ? "cli" : value;
    }

    static bool IsReparse(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            return info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception)
        {
            return false;
        }
    }

    static string? ScalarString(JsonObject? obj, params string[] keys)
    {
        if (obj is null) return null;
        foreach (var key in keys)
        {
            if (obj[key] is not JsonValue value) continue;
            if (value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
                return text.Trim();
            if (value.TryGetValue<int>(out var number))
                return number.ToString(CultureInfo.InvariantCulture);
            if (value.TryGetValue<long>(out var wide))
                return wide.ToString(CultureInfo.InvariantCulture);
        }

        return null;
    }

    static long? Num(JsonObject? obj, string key)
    {
        if (obj?[key] is not JsonValue value) return null;
        if (value.TryGetValue<long>(out var wide)) return wide;
        if (value.TryGetValue<int>(out var number)) return number;
        if (value.TryGetValue<string>(out var text)
            && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return null;
    }

    static string GuessFamily(string image, string harness)
    {
        if (image.Contains("grok", StringComparison.OrdinalIgnoreCase) || harness.Contains("grok", StringComparison.OrdinalIgnoreCase))
            return "Grok";
        if (image.Contains("cursor", StringComparison.OrdinalIgnoreCase) || harness.Contains("cursor", StringComparison.OrdinalIgnoreCase))
            return "Cursor";
        if (image.Contains("claude", StringComparison.OrdinalIgnoreCase) || harness.Contains("claude", StringComparison.OrdinalIgnoreCase))
            return "Claude";
        if (image.Contains("codex", StringComparison.OrdinalIgnoreCase) || harness.Contains("codex", StringComparison.OrdinalIgnoreCase))
            return "Codex";
        if (harness.Contains("pi", StringComparison.OrdinalIgnoreCase)) return "PI";
        if (harness.Contains("dsh", StringComparison.OrdinalIgnoreCase) || harness.Contains("deep", StringComparison.OrdinalIgnoreCase))
            return "DSH";
        return string.IsNullOrWhiteSpace(harness) ? image : harness;
    }

    static bool LooksLikeShellOrMcp(ObservedProcess process) => ProcessMatch.IsShellOrMcp(process);

    internal static IReadOnlyList<ProcessDiscoveryRule> DefaultDiscoveryRules() =>
        new ProcessDiscoveryRule[]
        {
            new("Grok", "grok.exe", null, null, new[] { "grove.exe", "agent.exe" }),
            new("Cursor", "cursor-agent.exe", null, null, null),
            new("Cursor", "node.exe", null, null, null),
            new("Claude", "claude.exe", null, null, null),
            new("Codex", "codex.exe", null, null, null),
            new("PI", "pi.exe", null, null, null),
            new("PI", "node.exe", null, null, null, new[]
            {
                "@earendil-works/pi-coding-agent",
                "@mariozechner/pi-coding-agent"
            }),
            new("DSH", "pwsh.exe", new[] { "Invoke-DeepSea", "deepsea-v4" }, null, null),
            new("DSH", "powershell.exe", new[] { "Invoke-DeepSea", "deepsea-v4" }, null, null)
        };

    static List<string> ExpandRoots(IReadOnlyList<string> roots)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots ?? Array.Empty<string>())
        {
            var expanded = ExpandPath(root);
            if (string.IsNullOrWhiteSpace(expanded)) continue;
            if (seen.Add(expanded))
                list.Add(expanded);
        }

        return list;
    }

    internal static string ExpandPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
        }
        catch (Exception)
        {
            return path.Trim();
        }
    }

    static bool IsDeniedName(string path)
    {
        var leaf = Path.GetFileName(path);
        foreach (var fragment in DeniedNames)
        {
            if (leaf.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    static string FileKey(string path) => Path.GetFileNameWithoutExtension(path);

    static string? Str(JsonObject? obj, params string[] keys)
    {
        if (obj is null) return null;
        foreach (var key in keys)
        {
            if (obj[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                return s;
        }

        return null;
    }

    static bool VerifyOwnerHandoff(JsonObject registration, JsonObject authority)
    {
        var previousOwner = Str(registration, "previous_target_thread_id");
        var currentOwner = Str(registration, "target_thread_id");
        var authorityOldOwner = Str(authority, "old_thread_id");
        var authorityNewOwner = Str(authority, "new_thread_id");
        var accepted = authority["accepted"] is JsonValue acceptedValue
            && acceptedValue.TryGetValue<bool>(out var acceptedFlag) && acceptedFlag;
        var handoffAt = ParseTime(Str(registration, "owner_handoff_at"));
        var authorityDateText = Str(authority, "date");
        var authorityDateValid = DateOnly.TryParseExact(
            authorityDateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var authorityDate);
        var handoffDateMatches = handoffAt is { } time
            && authorityDateValid
            && DateOnly.FromDateTime(time.UtcDateTime) == authorityDate;

        return accepted
            && !string.IsNullOrWhiteSpace(previousOwner)
            && !string.IsNullOrWhiteSpace(currentOwner)
            && !previousOwner.Equals(currentOwner, StringComparison.Ordinal)
            && string.Equals(previousOwner, authorityOldOwner, StringComparison.Ordinal)
            && string.Equals(currentOwner, authorityNewOwner, StringComparison.Ordinal)
            && handoffDateMatches;
    }

    static int? IntVal(JsonObject obj, string key)
    {
        if (obj[key] is JsonValue v)
        {
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<long>(out var l)) return (int)l;
            if (v.TryGetValue<string>(out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p))
                return p;
        }

        return null;
    }

    static long? LongVal(JsonObject obj, string key)
    {
        if (obj[key] is JsonValue v)
        {
            if (v.TryGetValue<long>(out var l)) return l;
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<string>(out var s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p))
                return p;
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

    static DateTimeOffset ToOffset(DateTime utc) =>
        new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
}
