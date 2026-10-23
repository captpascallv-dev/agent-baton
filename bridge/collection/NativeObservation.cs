// SPDX-License-Identifier: MPL-2.0
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CliWatch.Contracts;

namespace CliWatch.Collection;

public sealed partial class MailboxCollector
{
    readonly Dictionary<string, PromptFileIdentity.FileIdentity?> _identityFiles = new(StringComparer.OrdinalIgnoreCase);
    static string ObservationKey(string? root, string? key) => (root ?? "") + "\n" + (key ?? "");

    void MarkPartialRead(string kind, string path, string? key)
    {
        if (_currentRoot is null) return;
        key ??= kind == KindMailboxJob ? FileKey(path) : null;
        if (key is not null) _partialJobs.Add(ObservationKey(_currentRoot, key));
        else _partialRoots.Add(_currentRoot);
    }

    static bool SafeObservationPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
        try
        {
            for (var p = Path.GetFullPath(path); !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
                if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) return false;
            return true;
        }
        catch (Exception) { return false; }
    }

    bool FingerprintMatches(string? path, string? hash, long? bytes, List<SourceIssue> issues, DateTimeOffset now)
    {
        if (!SafeObservationPath(path) || string.IsNullOrWhiteSpace(hash) || hash.Length != 64) return false;
        var full = Path.GetFullPath(path!);
        if (IsDeniedName(full)) return false;
        if (!_identityFiles.TryGetValue(full, out var identity))
        {
            if (!_jsonAttempts.Contains(full))
            {
                if (!CanScan(_currentRoot ?? "observer", full, issues, now)) return false;
                _jsonAttempts.Add(full); _budgetRemaining--; _rootBudget--;
            }
            identity = PromptFileIdentity.TryFileIdentity(full, out var found) && found.Bytes <= _maxJsonBytes ? found : null;
            _identityFiles[full] = identity;
        }
        return identity is { } id && (bytes is null || bytes == id.Bytes) && id.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase);
    }

    async Task<RawDocument?> ObservationJson(string? path, string kind, string? project, string? key,
        List<RawDocument> docs, List<SourceIssue> issues, HashSet<string> seen, DateTimeOffset now, CancellationToken ct)
    {
        if (!SafeObservationPath(path)) return null;
        return await TryReadJsonAsync(path, kind, project, docs, issues, seen, now, ct, key, issueIfMissing: false).ConfigureAwait(false);
    }

    static string? ControlDirectory(JsonObject job)
    {
        var authority = Str(job, "execution_authority");
        return SafeObservationPath(authority) ? Path.GetDirectoryName(authority) : null;
    }

    async Task ResolveNativeAdapterAsync(RawDocument job, string? project, string key,
        List<RawDocument> docs, List<SourceIssue> issues, HashSet<string> seen, IReadOnlyList<ObservedProcess> live, DateTimeOffset now, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(Str(job.Data, "adapter_job_id"))) return;
        var state = Str(job.Data, "native_state_root");
        if (!SafeObservationPath(state)) return;
        var declaredId = Str(job.Data, "native_adapter_job_id");
        var directId = Str(job.Data, "native_job_id");
        if (declaredId is not null && directId is not null && declaredId != directId) return;
        declaredId ??= directId;
        RawDocument? receipt = null;
        // An exact ID in the original registration is enough to locate a
        // request. Identity still requires workspace + full DISPATCH hash and
        // owner birth/CLI argv below; receipt filenames are not an identity.
        if (declaredId is null)
        {
            var directory = ControlDirectory(job.Data);
            if (directory is null) return;
            receipt = await ObservationJson(Path.Combine(directory, "DISPATCH_RECEIPT.json"), "native_dispatch_receipt", project, key, docs, issues, seen, now, ct).ConfigureAwait(false);
            if (receipt is null || Flag(receipt.Data, "corrupt")) return;
        }
        var id = declaredId ?? Str(receipt!.Data, "native_job_id");
        var canonical = Path.Combine(Str(job.Data, "outbox") ?? "", "DISPATCH.txt");
        if (!Guid.TryParseExact(id, "D", out _)) return;
        if (receipt is not null && (!PromptFileIdentity.SamePath(Str(receipt.Data, "registered_dispatch"), canonical)
            || !FingerprintMatches(canonical, Str(receipt.Data, "dispatch_sha256"), LongVal(receipt.Data, "dispatch_bytes"), issues, now))) return;
        var requestPath = Path.Combine(state!, "jobs", id!, "request.json");
        var request = await ObservationJson(requestPath, "native_adapter_identity", project, key, docs, issues, seen, now, ct).ConfigureAwait(false);
        if (request is null || Flag(request.Data, "corrupt") || Str(request.Data, "job_id") != id
            || Str(request.Data, "protocol_version") != "telephone-line-direct-codex-request-v1"
            || !PromptFileIdentity.SamePath(Str(request.Data, "workspace"), Str(job.Data, "workspace"))
            || !PromptFileIdentity.SamePath(Str(request.Data, "path"), canonical)
            || receipt is not null && Str(request.Data, "session_id") != Str(receipt.Data, "native_session_id")
            || Str(job.Data, "native_session_id") is { Length: > 0 } session && Str(request.Data, "session_id") != session
            || !FingerprintMatches(canonical, Str(request.Data, "sha256"), LongVal(request.Data, "bytes"), issues, now)) return;
        job.Data["adapter_job_id"] = id;
        job.Data["adapter_state_root"] = state;
        job.Data["native_adapter_verified"] = true;
    }

    // psutil subtracts the integer Windows/Unix epoch offset before converting
    // to double seconds. Dividing first loses additional precision at this age.
    // Equality checks the same representable value, with no arbitrary tolerance.
    internal static double WindowsCreatedEpoch(long utcTicks) =>
        (utcTicks - DateTime.UnixEpoch.Ticks) / 10_000_000.0;

    async Task<(RawDocument? Owner, RawDocument? Request)> ReadNativeSupervisorAsync(RawDocument job, string? project, string key,
        List<RawDocument> docs, List<SourceIssue> issues, HashSet<string> seen, IReadOnlyList<ObservedProcess> live, DateTimeOffset now, CancellationToken ct)
    {
        if (!string.Equals(Str(job.Data, "harness"), "codex-cli", StringComparison.Ordinal)) return (null, null);
        var dir = ControlDirectory(job.Data);
        if (dir is null) return (null, null);
        var workspace = await ObservationJson(Path.Combine(dir, "WORKSPACE.json"), "native_supervisor_workspace", project, key, docs, issues, seen, now, ct).ConfigureAwait(false);
        if (workspace is null || Flag(workspace.Data, "corrupt")
            || Str(workspace.Data, "job_id") != Str(job.Data, "job_id")
            || !PromptFileIdentity.SamePath(Str(workspace.Data, "outbox"), Str(job.Data, "outbox"))
            || !PromptFileIdentity.SamePath(Str(workspace.Data, "runtime"), Str(job.Data, "workspace"))) return (null, null);
        var start = await ObservationJson(Path.Combine(dir, "NATIVE_START.json"), "native_supervisor_start", project, key, docs, issues, seen, now, ct).ConfigureAwait(false);
        if (start is null || Flag(start.Data, "corrupt") || Str(start.Data, "harness") != "codex-cli"
            || !FingerprintMatches(Path.Combine(Str(job.Data, "outbox")!, "DISPATCH.txt"), Str(start.Data, "prompt_sha256"), null, issues, now)) return (null, null);
        var pid = IntVal(start.Data, "pid");
        if (pid is not > 0 || start.Data["created"] is not JsonValue createdNode || !createdNode.TryGetValue<double>(out var created) || !double.IsFinite(created)) return (null, null);
        job.Data["native_supervisor_verified"] = true;
        var matching = live.Where(p => p.Pid == pid && p.StartTicks > 0 && WindowsCreatedEpoch(p.StartTicks) == created).ToList();
        job.Data["native_supervisor_absent"] = !issues.Any(i => i.Code == "process_snapshot_failed")
            && _snapshot is not null && live.All(p => p.Pid != pid);
        // When the original process has ended, delivery/terminal facts may still
        // stand; no approximate birth timestamp is promoted to a live binding.
        RawDocument? owner = null;
        if (matching.Count == 1)
        {
            var data = new JsonObject { ["job_id"] = Str(job.Data, "job_id"), ["attempt_id"] = Str(job.Data, "attempt_id"),
                ["job_key"] = key, ["mailbox_root"] = Str(job.Data, "mailbox_root"),
                ["pid"] = matching[0].Pid, ["start_time_utc_ticks"] = matching[0].StartTicks,
                ["started_at_utc"] = matching[0].StartedAt.ToString("o"), ["identity_ok"] = true };
            owner = new RawDocument("native-owner:" + key, KindAdapterOwner, start.Location, data, now, matching[0].StartedAt, project, "current");
            docs.Add(owner);
        }
        var requestData = new JsonObject { ["job_id"] = Str(job.Data, "job_id"), ["attempt_id"] = Str(job.Data, "attempt_id"),
            ["job_key"] = key, ["mailbox_root"] = Str(job.Data, "mailbox_root"),
            ["workspace"] = Str(job.Data, "workspace"), ["model"] = Str(start.Data, "model"), ["reasoning_effort"] = Str(start.Data, "effort"), ["identity_ok"] = true };
        var request = new RawDocument("native-request:" + key, KindAdapterRequest, start.Location, requestData, now, start.FactAt, project, "current");
        docs.Add(request);
        var terminal = await ObservationJson(Path.Combine(dir, "NATIVE_TERMINAL.json"), "native_supervisor_terminal_input", project, key, docs, issues, seen, now, ct).ConfigureAwait(false);
        var startup = terminal is null ? null : await ObservationJson(Path.Combine(dir, "STARTUP_RECEIPT.json"), "native_supervisor_startup", project, key, docs, issues, seen, now, ct).ConfigureAwait(false);
        var ended = terminal is null ? null : ParseTime(Str(terminal.Data, "at"));
        var began = ParseTime(Str(start.Data, "at"));
        if (terminal is not null && startup is not null && !Flag(terminal.Data, "corrupt") && !Flag(startup.Data, "corrupt")
            && terminal.Data["native_exit_code"] is JsonValue exitNode && exitNode.TryGetValue<int>(out var exit)
            && ended is not null && began is not null && ended >= began && ended <= now
            && Flag(terminal.Data, "actual_binding_verified") && Flag(terminal.Data, "full_dispatch_received")
            && IntVal(startup.Data, "native_pid") == pid
            && startup.Data["native_created"] is JsonValue birth && birth.TryGetValue<double>(out var startupCreated) && startupCreated == created
            && Guid.TryParse(Str(terminal.Data, "session_id"), out _)
            && Str(terminal.Data, "session_id") == Str(startup.Data, "native_session_id"))
            docs.Add(new RawDocument("native-terminal:" + key, "native_supervisor_terminal", terminal.Location,
                new JsonObject { ["job_key"] = key, ["mailbox_root"] = Str(job.Data, "mailbox_root"),
                    ["identity_ok"] = true, ["exit_code"] = exit, ["finished_at_utc"] = ended.Value.ToString("o") }, now, ended, project, "current"));
        return (owner, request);
    }

    async Task ReadObserverControlFirstAsync(CollectorSettings settings, List<RawDocument> docs, List<SourceIssue> issues,
        HashSet<string> seen, DateTimeOffset now, CancellationToken ct)
    {
        _scopeMembers.Clear();
        _closeoutPins.Clear();
        var scopes = await ObservationJson(settings.CurrentScopeSourcesFile, "observer_scope_config", null, null, docs, issues, seen, now, ct).ConfigureAwait(false);
        if (scopes?.Data["sources"] is JsonArray sources)
        foreach (var source in sources.OfType<JsonObject>())
        {
            var path = Str(source, "path");
            var scope = await ObservationJson(path, "observer_owner_scope", Str(source, "project"), null, docs, issues, seen, now, ct).ConfigureAwait(false);
            if (!TryReadVerifiedScope(scope, now, out _, out var members)) continue;
            var root = Str(source, "mailbox_root");
            if (string.IsNullOrWhiteSpace(root)) continue;
            foreach (var member in members.OfType<JsonObject>())
            {
                var id = Str(member, "job_id");
                if (!string.IsNullOrWhiteSpace(id))
                    _scopeMembers.Add(new ScopeMember(root, id));
            }
        }
        var closeouts = await ObservationJson(settings.DisplayCloseoutsFile, "observer_closeout_config", null, null, docs, issues, seen, now, ct).ConfigureAwait(false);
        if (closeouts?.Data["records"] is not JsonArray records) return;
        foreach (var record in records.OfType<JsonObject>())
        {
            var key = Str(record, "key");
            var root = Str(record, "mailbox_root");
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(root))
                _closeoutPins.Add(new CloseoutPin(root, key));
            FingerprintMatches(Str(record, "evidence_path"), Str(record, "evidence_sha256"), null, issues, now);
        }
    }

    static bool TryReadVerifiedScope(RawDocument? scope, DateTimeOffset now, out DateTimeOffset stamp, out JsonArray members)
    {
        stamp = default;
        members = new JsonArray();
        if (scope is null || Flag(scope.Data, "corrupt") || scope.Data["current_active_jobs"] is not JsonArray array)
            return false;
        var parsed = ParseTime(Str(scope.Data, "at"));
        if (parsed is null || parsed > now) return false;
        if (array.Any(m => m is not JsonObject o || string.IsNullOrWhiteSpace(Str(o, "job_id")) || !SafeObservationPath(Str(o, "outbox"))))
            return false;
        stamp = parsed.Value;
        members = array;
        return true;
    }

    async Task ReadObserverDecisionsAsync(CollectorSettings settings, List<RawDocument> docs, List<SourceIssue> issues,
        HashSet<string> seen, DateTimeOffset now, CancellationToken ct)
    {
        var jobs = docs.Where(d => d.Kind == KindMailboxJob).ToList();
        var scopes = await ObservationJson(settings.CurrentScopeSourcesFile, "observer_scope_config", null, null, docs, issues, seen, now, ct).ConfigureAwait(false);
        if (settings.CurrentScopeSourcesFile is not null && (scopes is null || Flag(scopes.Data, "corrupt") || scopes.Data["sources"] is not JsonArray))
            issues.Add(new SourceIssue(settings.CurrentScopeSourcesFile, "current_scope_unavailable", "当前清单配置未能读取；不据此隐藏任务。", now));
        if (scopes?.Data["sources"] is JsonArray sources)
        foreach (var source in sources.OfType<JsonObject>())
        {
            await ReadLeadDispositionsAsync(source, jobs, docs, issues, seen, now, ct).ConfigureAwait(false);
            var path = Str(source, "path");
            var scope = await ObservationJson(path, "observer_owner_scope", Str(source, "project"), null, docs, issues, seen, now, ct).ConfigureAwait(false);
            if (!TryReadVerifiedScope(scope, now, out var stamp, out var members))
            {
                issues.Add(new SourceIssue(path ?? "observer_scope", "current_scope_unavailable", "原 Lead 当前清单未能核实；不据此隐藏任务。", now));
                continue;
            }
            foreach (var job in jobs.Where(j => Str(j.Data, "project") == Str(source, "project")
                && Str(j.Data, "target_thread_id") == Str(source, "target_thread_id")
                && PromptFileIdentity.SamePath(Str(j.Data, "mailbox_root"), Str(source, "mailbox_root"))))
            {
                var registered = ParseTime(Str(job.Data, "registered_at"));
                if (registered is null || registered > stamp) continue;
                job.Data["current_scope_at"] = stamp.ToString("o");
                job.Data["current_scope_member"] = members.OfType<JsonObject>().Any(m => Str(m, "job_id") == Str(job.Data, "job_id")
                    && PromptFileIdentity.SamePath(Str(m, "outbox"), Str(job.Data, "outbox")));
            }
        }
        var closeouts = await ObservationJson(settings.DisplayCloseoutsFile, "observer_closeout_config", null, null, docs, issues, seen, now, ct).ConfigureAwait(false);
        if (closeouts?.Data["records"] is not JsonArray records) return;
        foreach (var record in records.OfType<JsonObject>())
        foreach (var job in jobs.Where(j => Str(j.Data, "job_key") == Str(record, "key")
            && PromptFileIdentity.SamePath(Str(j.Data, "mailbox_root"), Str(record, "mailbox_root"))))
        {
            var at = ParseTime(Str(record, "recorded_at"));
            if (at is null || at > now
                || !FingerprintMatches(job.Location, Str(record, "registration_sha256"), null, issues, now)
                || !FingerprintMatches(FindReadyPath(Str(job.Data, "outbox")), Str(record, "ready_sha256"), null, issues, now)
                || !FingerprintMatches(Str(record, "evidence_path"), Str(record, "evidence_sha256"), null, issues, now)) continue;
            job.Data["display_closeout_verified"] = true;
        }
    }
}
