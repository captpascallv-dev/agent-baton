// SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CliWatch.Contracts;

namespace CliWatch.Collection;

public sealed class FileCollector : ICollector
{
    public const string KindProjectRegistry = "project_registry";
    public const string KindActiveWork = "active_work";
    public const string KindLeadRun = "lead_run";
    public const string KindLeadOwner = "lead_owner";
    public const string KindCliChild = "cli_child";
    public const string KindCliLifecycle = "cli_lifecycle";
    public const string KindProcessObservation = "process_observation";
    public const string KindLineDispatch = "line_dispatch";
    public const string KindLineReceipt = "line_receipt";
    public const string KindLineDelivery = "line_delivery";
    public const string KindWakeIntent = "wake_intent";
    public const string KindWakeAttempt = "wake_attempt";
    public const string KindDirectRequest = "direct_request";
    public const string KindDirectReceipt = "direct_receipt";
    public const string KindRouteStatus = "route_status";
    public const string KindBotRegistry = "bot_registry";
    public const string KindBotResultPointer = "bot_result_pointer";
    public const string KindBotAcceptance = "bot_acceptance";
    public const string KindGenerationStatus = "generation_status";
    public const string KindCurrentResult = "current_result";

    public static readonly HashSet<string> KnownKinds = new(StringComparer.Ordinal)
    {
        KindProjectRegistry, KindActiveWork, KindLeadRun, KindLeadOwner, KindCliChild,
        KindCliLifecycle, KindProcessObservation, KindLineDispatch, KindLineReceipt,
        KindLineDelivery, KindWakeIntent, KindWakeAttempt, KindDirectRequest,
        KindDirectReceipt, KindRouteStatus, KindBotRegistry, KindBotResultPointer,
        KindBotAcceptance, KindGenerationStatus, KindCurrentResult
    };

    private static readonly HashSet<string> DeniedNameFragments = new(StringComparer.OrdinalIgnoreCase)
    {
        ".env", "cookies", "cookie", "auth.json", "credentials", "credential",
        "api_key", "apikey", "secret", "id_rsa", ".pem", ".pfx", "messages.jsonl",
        "prompt", "response.json", "transcript", "chat.jsonl"
    };

    /// <summary>
    /// Known producer payloads that are not cockpit metadata. Exclude before reading.
    /// </summary>
    private static readonly HashSet<string> NonContractPayloadNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "grok-result.json", "grok_result.json", "response.json", "prompt.json",
        "raw_prompt.json", "messages.jsonl", "transcript.json", "chat.jsonl",
        "diagnostics.json", "hidden_reasoning.json", "model-output.json"
    };

    private static readonly HashSet<string> DeniedJsonKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "token", "access_token", "refresh_token", "password", "cookie", "cookies",
        "authorization", "api_key", "apikey", "secret", "client_secret",
        "prompt", "response", "messages", "transcript", "raw_prompt", "hidden_reasoning",
        "diagnostic", "diagnostics", "reasoning", "thinking", "grok_result", "grok-result"
    };

    private static readonly string[] FactTimeKeys =
    {
        "updated_at_utc", "updated_at", "recorded_at_utc", "fact_at", "completed_at_utc",
        "created_at_utc", "started_at_utc", "received_at", "captured_at"
    };

    private static readonly string[] CurrentPointerKeys =
    {
        "dashboard_active_work_path", "current_run_root", "current_line_job_root",
        "current_direct_job_root", "current_army_acceptance", "army_repair_request",
        "binding", "lead_binding", "lead_run_root"
    };

    private static readonly HashSet<string> ShortEffortEnums = new(StringComparer.OrdinalIgnoreCase)
    {
        "xhigh", "high", "medium", "low", "xlow", "max", "minimal", "min", "default"
    };

    private readonly IProcessProbe? _processProbe;

    public FileCollector(IProcessProbe? processProbe = null)
    {
        _processProbe = processProbe;
    }

    public async Task<CollectionBatch> CollectAsync(CollectorSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var observedAt = DateTimeOffset.UtcNow;
        var documents = new List<RawDocument>();
        var issues = new List<SourceIssue>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var budget = new FileBudget(settings.MaxFilesPerRefresh, settings.MaxFileBytes);

        foreach (var registryPath in settings.RegistryPaths ?? Array.Empty<string>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await CollectRegistryAsync(registryPath, settings, documents, issues, seen, budget, observedAt, cancellationToken).ConfigureAwait(false);
        }

        foreach (var extra in settings.AdditionalSourcePaths ?? Array.Empty<string>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await CollectExplicitAsync(extra, projectHint: null, context: "additional", documents, issues, seen, budget, observedAt, cancellationToken, scope: "current").ConfigureAwait(false);
        }

        return new CollectionBatch(observedAt, documents, issues);
    }

    private async Task CollectRegistryAsync(
        string registryPath,
        CollectorSettings settings,
        List<RawDocument> documents,
        List<SourceIssue> issues,
        HashSet<string> seen,
        FileBudget budget,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizePath(registryPath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            issues.Add(Issue("registry", "empty_path", "Registry path is empty.", observedAt));
            return;
        }

        var doc = await TryReadFileAsync(normalized, KindProjectRegistry, projectHint: null, documents, issues, seen, budget, observedAt, cancellationToken).ConfigureAwait(false);
        if (doc is null)
        {
            return;
        }

        if (doc.Data["projects"] is not JsonArray projects)
        {
            issues.Add(Issue(normalized, "registry_projects_missing", "Registry has no projects array.", observedAt));
            return;
        }

        foreach (var node in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node is not JsonObject project)
            {
                issues.Add(Issue(normalized, "registry_project_invalid", "A project entry is not an object.", observedAt));
                continue;
            }

            var hint = project["project_id"]?.GetValue<string>() ?? project["id"]?.GetValue<string>();
            var status = project["status"]?.GetValue<string>() ?? project["registry_status"]?.GetValue<string>();
            var paused = project["paused"]?.GetValue<bool>() == true
                         || project["paused_by_user"]?.GetValue<bool>() == true
                         || string.Equals(status, "PAUSED", StringComparison.OrdinalIgnoreCase);
            var scope = paused || IsHistoricalRegistryStatus(status) ? "historical" : "current";
            var currentLineId = Str(project, "current_line_job_id", "line_job_id");
            var currentDirectId = Str(project, "current_direct_job_id", "direct_job_id");
            foreach (var kv in project)
            {
                if (!CurrentPointerKeys.Contains(kv.Key) && !IsAuthorizedMetadataPointerKey(kv.Key))
                    continue;
                if (kv.Value is JsonValue v && v.TryGetValue<string>(out var path) && !string.IsNullOrWhiteSpace(path)
                    && LooksLikeJsonMetadata(path))
                {
                    var pointerScope = scope;
                    if (string.Equals(scope, "current", StringComparison.OrdinalIgnoreCase)
                        && JobRootLagsCurrentId(kv.Key, path, currentLineId, currentDirectId))
                    {
                        pointerScope = "historical";
                    }

                    await CollectExplicitAsync(path, hint, kv.Key, documents, issues, seen, budget, observedAt, cancellationToken, pointerScope).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task CollectExplicitAsync(
        string path,
        string? projectHint,
        string context,
        List<RawDocument> documents,
        List<SourceIssue> issues,
        HashSet<string> seen,
        FileBudget budget,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken,
        string scope = "current")
    {
        var normalized = NormalizePath(path);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        if (Directory.Exists(normalized))
        {
            // Current pointer only: one directory level, no historical sibling walk.
            // Read only registered metadata filenames; skip known non-contract payloads before ingest.
            string[] files;
            try
            {
                files = Directory.GetFiles(normalized);
            }
            catch (Exception ex)
            {
                issues.Add(Issue(normalized, "dir_unreadable",
                    ScopeDetail(scope, projectHint, context, ex.GetType().Name + ": " + ex.Message), observedAt));
                return;
            }

            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    || file.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (IsNonContractPayload(file))
                    continue;

                var kind = Classify(file, context);
                if (kind is null || !KnownKinds.Contains(kind))
                    continue;

                await TryReadFileAsync(file, kind, projectHint, documents, issues, seen, budget, observedAt, cancellationToken, scope, context).ConfigureAwait(false);
            }

            return;
        }

        if (IsNonContractPayload(normalized))
            return;

        var fileKind = Classify(normalized, context);
        await TryReadFileAsync(normalized, fileKind, projectHint, documents, issues, seen, budget, observedAt, cancellationToken, scope, context).ConfigureAwait(false);
    }

    private async Task<RawDocument?> TryReadFileAsync(
        string path,
        string? kind,
        string? projectHint,
        List<RawDocument> documents,
        List<SourceIssue> issues,
        HashSet<string> seen,
        FileBudget budget,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken,
        string scope = "current",
        string? context = null)
    {
        var normalized = NormalizePath(path);
        if (!seen.Add(normalized))
        {
            return null;
        }

        if (IsNonContractPayload(normalized))
            return null;

        if (IsDeniedPath(normalized))
        {
            issues.Add(Issue(normalized, "denied_path",
                ScopeDetail(scope, projectHint, context, "Path matches credential/message deny list; skipped."), observedAt));
            return null;
        }

        if (!budget.TryConsumeSlot(out var overflow))
        {
            if (overflow)
            {
                issues.Add(Issue(normalized, "max_files", "MaxFilesPerRefresh reached; remaining current files not read.", observedAt));
            }
            return null;
        }

        if (!File.Exists(normalized))
        {
            var missingCode = string.Equals(scope, "historical", StringComparison.OrdinalIgnoreCase)
                ? "historical_source_missing"
                : "missing";
            issues.Add(Issue(normalized, missingCode,
                ScopeDetail(scope, projectHint, context, "Referenced source is missing."), observedAt));
            return null;
        }

        FileInfo info;
        try
        {
            info = new FileInfo(normalized);
        }
        catch (Exception ex)
        {
            issues.Add(Issue(normalized, "stat_failed",
                ScopeDetail(scope, projectHint, context, ex.GetType().Name + ": " + ex.Message), observedAt));
            return null;
        }

        if (info.Length > budget.MaxFileBytes)
        {
            issues.Add(Issue(normalized, "too_large",
                ScopeDetail(scope, projectHint, context, $"File is {info.Length} bytes; MaxFileBytes={budget.MaxFileBytes}."), observedAt));
            return null;
        }

        string text;
        try
        {
            text = await File.ReadAllTextAsync(normalized, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            issues.Add(Issue(normalized, "read_failed",
                ScopeDetail(scope, projectHint, context, ex.GetType().Name + ": " + ex.Message), observedAt));
            return null;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            issues.Add(Issue(normalized, "empty_file",
                ScopeDetail(scope, projectHint, context, "File exists but is empty (possible half-write)."), observedAt));
            return null;
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(text);
        }
        catch (JsonException ex)
        {
            issues.Add(Issue(normalized, "parse_error",
                ScopeDetail(scope, projectHint, context, "Half-write or invalid JSON: " + ex.Message), observedAt));
            return null;
        }

        if (parsed is not JsonObject obj)
        {
            issues.Add(Issue(normalized, "not_object",
                ScopeDetail(scope, projectHint, context, "JSON root is not an object."), observedAt));
            return null;
        }

        var sanitized = Allowlist(obj);
        var resolvedKind = kind ?? Classify(normalized, context);
        if (resolvedKind is null && LooksExecutorStatusResult(sanitized))
            resolvedKind = KindCurrentResult;
        if (context is "expected_result" && resolvedKind is null && !LooksExecutorStatusResult(sanitized))
            return null;
        if (resolvedKind is null || !KnownKinds.Contains(resolvedKind))
        {
            issues.Add(Issue(normalized, "unknown_kind",
                ScopeDetail(scope, projectHint, context, "File exists but this format is not a contract kind; cannot read those states from it."), observedAt));
            return null;
        }

        var factAt = ExtractFactAt(sanitized) ?? ToOffset(info.LastWriteTimeUtc);
        var id = resolvedKind + ":" + normalized;
        var document = new RawDocument(id, resolvedKind, normalized, sanitized, observedAt, factAt, projectHint, scope);
        documents.Add(document);
        await MaybeObserveProcessAsync(document, documents, issues, seen, budget, observedAt, cancellationToken).ConfigureAwait(false);
        await FollowNestedPointersAsync(document, documents, issues, seen, budget, observedAt, cancellationToken).ConfigureAwait(false);
        if (document.Kind == KindActiveWork || document.Kind == KindGenerationStatus)
        {
            await FollowGenerationAssociatesAsync(document, documents, issues, seen, budget, observedAt, cancellationToken).ConfigureAwait(false);
        }
        return document;
    }

    private async Task FollowNestedPointersAsync(
        RawDocument document,
        List<RawDocument> documents,
        List<SourceIssue> issues,
        HashSet<string> seen,
        FileBudget budget,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        if (document.Kind is KindLineReceipt or KindLineDispatch)
        {
            await FollowStarterJobRootAsync(document, documents, issues, seen, budget, observedAt, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (document.Kind is not (KindActiveWork or KindProjectRegistry or KindBotAcceptance or KindBotResultPointer))
        {
            return;
        }

        await FollowPointerMapAsync(document.Data, document, documents, issues, seen, budget, observedAt, cancellationToken).ConfigureAwait(false);
        if (document.Kind == KindActiveWork && document.Data["lanes"] is JsonObject lanes)
        {
            foreach (var lane in lanes)
            {
                if (lane.Value is JsonObject laneObj)
                    await FollowPointerMapAsync(laneObj, document, documents, issues, seen, budget, observedAt, cancellationToken, "lane:" + lane.Key).ConfigureAwait(false);
            }
        }

        if (document.Kind == KindActiveWork)
        {
            await FollowAuthorizedJobRootsAsync(document, documents, issues, seen, budget, observedAt, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task FollowPointerMapAsync(
        JsonObject data,
        RawDocument document,
        List<RawDocument> documents,
        List<SourceIssue> issues,
        HashSet<string> seen,
        FileBudget budget,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken,
        string? contextPrefix = null)
    {
        foreach (var kv in data)
        {
            if (!IsAuthorizedMetadataPointerKey(kv.Key))
                continue;
            string? path = null;
            if (kv.Value is JsonValue v && v.TryGetValue<string>(out var s))
                path = s;
            else if (kv.Value is JsonObject obj && obj["path"] is JsonValue pv && pv.TryGetValue<string>(out var nested))
                path = nested;
            if (string.IsNullOrWhiteSpace(path) || !LooksLikePath(path) || !LooksLikeJsonMetadata(path))
                continue;
            var nestedScope = LooksHistoricalPointerKey(kv.Key)
                || (document.Kind == KindActiveWork
                    && string.IsNullOrWhiteSpace(contextPrefix)
                    && IsStalePackageAcceptancePointer(document, kv.Key, path))
                ? "historical"
                : "current";
            var context = string.IsNullOrWhiteSpace(contextPrefix) ? kv.Key : contextPrefix + ":" + kv.Key;
            await CollectExplicitAsync(path, document.ProjectHint, context, documents, issues, seen, budget, observedAt, cancellationToken, scope: nestedScope).ConfigureAwait(false);
        }
    }

    private async Task FollowStarterJobRootAsync(
        RawDocument document,
        List<RawDocument> documents,
        List<SourceIssue> issues,
        HashSet<string> seen,
        FileBudget budget,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        var loc = document.Location ?? string.Empty;
        var dispatched = document.Data["dispatched"] is JsonValue dv && dv.TryGetValue<bool>(out var db) && db;
        if (!loc.Contains("DISPATCH_RECEIPT", StringComparison.OrdinalIgnoreCase) && !dispatched)
            return;

        async Task Follow(string? path, string context)
        {
            if (string.IsNullOrWhiteSpace(path) || !LooksLikePath(path)) return;
            var followScope = string.Equals(document.Scope, "historical", StringComparison.OrdinalIgnoreCase)
                ? "historical"
                : "current";
            await CollectExplicitAsync(path, document.ProjectHint, context, documents, issues, seen, budget, observedAt, cancellationToken, scope: followScope).ConfigureAwait(false);
        }

        if (document.Data["job_root"] is JsonValue jv && jv.TryGetValue<string>(out var jobRoot))
            await Follow(jobRoot, "job_root").ConfigureAwait(false);
        if (document.Data["dispatch"] is JsonObject disp
            && disp["path"] is JsonValue pv && pv.TryGetValue<string>(out var dispatchPath))
        {
            await Follow(dispatchPath, "dispatch").ConfigureAwait(false);
        }
    }

    private async Task FollowAuthorizedJobRootsAsync(
        RawDocument document,
        List<RawDocument> documents,
        List<SourceIssue> issues,
        HashSet<string> seen,
        FileBudget budget,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        var data = document.Data;
        var hint = document.ProjectHint;
        string? Str(params string[] keys)
        {
            foreach (var k in keys)
            {
                if (data[k] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                    return s;
            }
            return null;
        }

        var lineId = Str("line_job_id", "current_line_job_id");
        var directId = Str("direct_job_id", "current_direct_job_id");
        var telRoot = Str("telephone_state_root", "line_state_root");
        var dirRoot = Str("direct_state_root", "route_state_root");

        if (!string.IsNullOrWhiteSpace(telRoot) && !string.IsNullOrWhiteSpace(lineId))
        {
            var lineDir = Path.Combine(telRoot, "jobs", lineId);
            await CollectExplicitAsync(lineDir, hint, "active_work_line_job_root", documents, issues, seen, budget, observedAt, cancellationToken, scope: "current").ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(dirRoot) && !string.IsNullOrWhiteSpace(directId))
        {
            var directDir = Path.Combine(dirRoot, "jobs", directId);
            await CollectExplicitAsync(directDir, hint, "active_work_direct_job_root", documents, issues, seen, budget, observedAt, cancellationToken, scope: "current").ConfigureAwait(false);
        }

        foreach (var receiptKey in new[] { "line_receipt", "telephone_receipt", "route_receipt", "direct_receipt", "actual_dispatch_receipt", "native_identity_source", "last_transport_receipt", "last_direct_receipt" })
        {
            var receipt = Str(receiptKey);
            if (string.IsNullOrWhiteSpace(receipt)) continue;
            var parent = Path.GetDirectoryName(NormalizePath(receipt));
            if (!string.IsNullOrWhiteSpace(parent))
            {
                var jobScope = LooksHistoricalPointerKey(receiptKey) ? "historical" : "current";
                await CollectExplicitAsync(parent, hint, receiptKey + "_job_dir", documents, issues, seen, budget, observedAt, cancellationToken, scope: jobScope).ConfigureAwait(false);
            }
        }
    }

    private async Task FollowGenerationAssociatesAsync(
        RawDocument document,
        List<RawDocument> documents,
        List<SourceIssue> issues,
        HashSet<string> seen,
        FileBudget budget,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        string? Str(params string[] keys)
        {
            foreach (var k in keys)
            {
                if (document.Data[k] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                    return s;
            }
            return null;
        }

        var hint = document.ProjectHint;
        var configPath = Str("prepared_next_config", "current_config");
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            await CollectExplicitAsync(configPath, hint, "prepared_next_config", documents, issues, seen, budget, observedAt, cancellationToken, scope: "current").ConfigureAwait(false);
        }

        var resultPath = Str("current_result", "result");
        var packageRoot = Str("package_root");
        var anchors = new List<string>();
        if (!string.IsNullOrWhiteSpace(resultPath))
        {
            var resultDir = Path.GetDirectoryName(NormalizePath(resultPath));
            if (!string.IsNullOrWhiteSpace(resultDir)) anchors.Add(resultDir);
        }
        if (!string.IsNullOrWhiteSpace(packageRoot))
            anchors.Add(NormalizePath(packageRoot));
        if (!string.IsNullOrWhiteSpace(document.Location))
        {
            var locDir = Path.GetDirectoryName(NormalizePath(document.Location));
            if (!string.IsNullOrWhiteSpace(locDir)) anchors.Add(locDir);
        }

        foreach (var anchor in anchors.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var runPid = Path.Combine(anchor, "run-pid.json");
            if (File.Exists(runPid))
            {
                await CollectExplicitAsync(runPid, hint, "generation_run_pid", documents, issues, seen, budget, observedAt, cancellationToken, scope: "current").ConfigureAwait(false);
            }

            var parent = Path.GetDirectoryName(anchor);
            foreach (var dir in new[] { anchor, parent }.Where(d => !string.IsNullOrWhiteSpace(d)))
            {
                var heartbeat = Path.Combine(dir!, "heartbeat.json");
                if (File.Exists(heartbeat))
                {
                    await CollectExplicitAsync(heartbeat, hint, "generation_heartbeat", documents, issues, seen, budget, observedAt, cancellationToken, scope: "current").ConfigureAwait(false);
                }
            }
        }
    }

    internal static bool IsStalePackageAcceptancePointer(RawDocument activeWork, string key, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var k = key.ToLowerInvariant();
        if (k is "last_native_acceptance") return false;
        if (!k.Contains("acceptance", StringComparison.Ordinal)) return false;
        if (k.StartsWith("current_", StringComparison.Ordinal) && k.Contains("native", StringComparison.Ordinal))
            return false;

        var n = path.Replace('\\', '/');
        string? Root(params string[] keys)
        {
            foreach (var name in keys)
            {
                if (activeWork.Data[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                    return s.Replace('\\', '/');
            }
            return null;
        }

        var packageRoot = Root("package_root");
        if (!string.IsNullOrWhiteSpace(packageRoot)
            && n.StartsWith(packageRoot.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var currentResult = Root("current_result", "result");
        if (!string.IsNullOrWhiteSpace(currentResult))
        {
            var dir = Path.GetDirectoryName(currentResult)?.Replace('\\', '/');
            if (!string.IsNullOrWhiteSpace(dir)
                && n.StartsWith(dir.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        var currentCard = Root("current_card", "current_prompt", "current_acceptance");
        if (!string.IsNullOrWhiteSpace(currentCard))
        {
            var dir = Path.GetDirectoryName(currentCard)?.Replace('\\', '/');
            if (!string.IsNullOrWhiteSpace(dir)
                && n.StartsWith(dir.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        var packageId = Root("package_id", "current_package_id");
        if (!string.IsNullOrWhiteSpace(packageId)
            && n.Contains(packageId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return k is "latest_acceptance" or "acceptance"
            || k.Contains("g6", StringComparison.Ordinal)
            || k.Contains("g7", StringComparison.Ordinal);
    }

    private async Task MaybeObserveProcessAsync(
        RawDocument document,
        List<RawDocument> documents,
        List<SourceIssue> issues,
        HashSet<string> seen,
        FileBudget budget,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        if (!TryReadPidTicks(document.Data, out var pid, out var ticks))
        {
            return;
        }

        var obsId = KindProcessObservation + ":" + document.Location + ":" + pid + ":" + ticks;
        if (!seen.Add(obsId))
        {
            return;
        }

        if (!budget.TryConsumeSlot(out _))
        {
            return;
        }

        string state;
        if (_processProbe is null)
        {
            state = "unknown";
        }
        else
        {
            try
            {
                var observation = await _processProbe.ObserveAsync(pid, ticks, cancellationToken).ConfigureAwait(false);
                state = string.IsNullOrWhiteSpace(observation.State) ? "unknown" : observation.State;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                issues.Add(Issue(document.Location, "probe_failed",
                    ScopeDetail("current", document.ProjectHint, "process_probe",
                        "进程探针失败：" + ex.GetType().Name + "。已有回执/验收不受影响。"),
                    observedAt));
                state = "unknown";
            }
        }

        var data = new JsonObject
        {
            ["pid"] = pid,
            ["expected_start_ticks"] = ticks,
            ["state"] = state,
            ["source_location"] = document.Location,
            ["probe_present"] = _processProbe is not null
        };
        documents.Add(new RawDocument(
            obsId,
            KindProcessObservation,
            document.Location + "#process",
            data,
            observedAt,
            document.FactAt,
            document.ProjectHint,
            "current"));
    }

    private static bool TryReadPidTicks(JsonObject data, out int pid, out long ticks)
    {
        pid = 0;
        ticks = 0;
        if (!TryGetInt(data, "pid", out pid))
        {
            return false;
        }

        return TryGetLong(data, "start_time_utc_ticks", out ticks) || TryGetLong(data, "start_ticks", out ticks);
    }

    private static bool TryGetInt(JsonObject data, string key, out int value)
    {
        value = 0;
        var node = data[key];
        if (node is JsonValue v && v.TryGetValue<int>(out value))
        {
            return true;
        }

        if (node is JsonValue s && s.TryGetValue<string>(out var text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        return false;
    }

    private static bool TryGetLong(JsonObject data, string key, out long value)
    {
        value = 0;
        var node = data[key];
        if (node is JsonValue v && v.TryGetValue<long>(out value))
        {
            return true;
        }

        if (node is JsonValue s && s.TryGetValue<string>(out var text) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        return false;
    }

    internal static string? Classify(string path, string? context)
    {
        var name = Path.GetFileName(path);
        var lower = path.Replace('\\', '/').ToLowerInvariant();
        var file = name.ToLowerInvariant();

        if (file.Contains("bot_work_registry", StringComparison.Ordinal)
            || file.Contains("bot_registry", StringComparison.Ordinal)
            || (file.Contains("bot", StringComparison.Ordinal) && file.Contains("registry", StringComparison.Ordinal)))
        {
            return KindBotRegistry;
        }

        if (file.Contains("registry", StringComparison.Ordinal) || context is "registry")
        {
            return KindProjectRegistry;
        }

        if (file is "active_work.json" || file is "active-work.json" || file == "active_work.json")
        {
            return KindActiveWork;
        }

        if (context is "current_result")
        {
            return KindCurrentResult;
        }

        if (file is "heartbeat.json" || file is "run-pid.json" || file is "run_pid.json"
            || file is "config.json" || context is "prepared_next_config" or "generation_heartbeat" or "generation_run_pid")
        {
            return KindGenerationStatus;
        }

        if (file.Contains("acceptance", StringComparison.Ordinal)
            || file.Contains("adopt", StringComparison.Ordinal)
            || file.Contains("assessment", StringComparison.Ordinal)
            || file.Contains("resume_result", StringComparison.Ordinal)
            || file is "resume_result.json")
        {
            return KindBotAcceptance;
        }

        if (file.Contains("contribution_return", StringComparison.Ordinal) || file.Contains("result_pointer", StringComparison.Ordinal) || file.Contains("repair_return", StringComparison.Ordinal))
        {
            return KindBotResultPointer;
        }

        if (file is "run.json" || file is "lead-run.json" || file.StartsWith("lead_run", StringComparison.Ordinal))
        {
            return KindLeadRun;
        }

        if (file is "child.json"
            || file.Contains("cli_child", StringComparison.Ordinal)
            || file.Contains("cli-child", StringComparison.Ordinal)
            || file.Contains("command-child", StringComparison.Ordinal))
        {
            return KindCliChild;
        }

        if (file is "cli_lifecycle.json" || file.Contains("cli_lifecycle", StringComparison.Ordinal) || file.Contains("cli-lifecycle", StringComparison.Ordinal))
        {
            return KindCliLifecycle;
        }

        if (file is "lifecycle-status.json" || file.Contains("lifecycle-status", StringComparison.Ordinal))
        {
            // Telephone line lifecycle is not a CLI turn document.
            return KindLineDispatch;
        }

        if (file.Contains("lifecycle", StringComparison.Ordinal))
        {
            return KindCliLifecycle;
        }

        if (file.Contains("wake-intent", StringComparison.Ordinal) || file.Contains("wake_intent", StringComparison.Ordinal))
        {
            return KindWakeIntent;
        }

        if (file.Contains("wake-attempt", StringComparison.Ordinal) || file.Contains("wake_attempt", StringComparison.Ordinal))
        {
            return KindWakeAttempt;
        }

        if (file is "dispatch.json"
            || (file.Contains("dispatch", StringComparison.Ordinal)
                && (context is "active_work_line_job_root" or "active_work_direct_job_root"
                    or "line_receipt_job_dir" or "telephone_receipt_job_dir"
                    or "route_receipt_job_dir" or "direct_receipt_job_dir"
                    or "actual_dispatch_receipt_job_dir" or "native_identity_source_job_dir")))
        {
            return KindLineDispatch;
        }

        if (file.Contains("delivery", StringComparison.Ordinal))
        {
            return KindLineDelivery;
        }

        if (file.Contains("route-status", StringComparison.Ordinal) || file.Contains("route_status", StringComparison.Ordinal))
        {
            return KindRouteStatus;
        }

        if (file is "request.json")
        {
            return KindDirectRequest;
        }

        if (file is "owner.json"
            || file.Contains("command-owner", StringComparison.Ordinal)
            || file.Contains("relay-owner", StringComparison.Ordinal)
            || file.EndsWith("-owner.json", StringComparison.Ordinal))
        {
            return KindLeadOwner;
        }

        if (file is "receipt.json"
            || file.Contains("receipt", StringComparison.Ordinal)
            || context is "line_receipt" or "route_receipt" or "telephone_receipt" or "direct_receipt" or "actual_dispatch_receipt")
        {
            if (file.Contains("direct", StringComparison.Ordinal)
                || lower.Contains("/direct-", StringComparison.Ordinal)
                || lower.Contains("/direct/", StringComparison.Ordinal)
                || context is "current_direct_job_root" or "direct_receipt" or "route_receipt")
            {
                return KindDirectReceipt;
            }

            return KindLineReceipt;
        }

        if (file.Contains("binding", StringComparison.Ordinal) || context is "binding" or "lead_binding")
        {
            return KindLeadRun;
        }

        if (file.Contains("repair_request", StringComparison.Ordinal) || context is "army_repair_request")
        {
            return KindBotResultPointer;
        }

        if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return null;
    }

    private static JsonObject Allowlist(JsonObject source)
    {
        var copy = new JsonObject();
        foreach (var kv in source)
        {
            if (DeniedJsonKeys.Contains(kv.Key))
            {
                if (IsAllowedShortEffortScalar(kv.Key, kv.Value))
                {
                    copy[kv.Key] = kv.Value is null ? null : StripPayloadNode(kv.Value);
                    continue;
                }

                if (kv.Value is JsonObject deniedObj)
                    LiftRouteShellScalars(copy, deniedObj);
                continue;
            }

            copy[kv.Key] = kv.Value is null ? null : StripPayloadNode(kv.Value);
        }

        return copy;
    }

    /// <summary>
    /// Denied payload objects may still carry route-shell identity. Copy only
    /// job/session/transport scalars already missing on the parent. Never copy
    /// result/response/thought bodies.
    /// </summary>
    private static void LiftRouteShellScalars(JsonObject dest, JsonObject src)
    {
        foreach (var key in new[] { "job_id", "session_id", "native_session_id", "transport_complete" })
        {
            if (dest[key] is not null) continue;
            if (src[key] is JsonValue v)
                dest[key] = v.DeepClone();
        }
    }

    internal static string? JobIdFromPath(string? path)
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

    private static bool JobRootLagsCurrentId(string key, string path, string? currentLineId, string? currentDirectId)
    {
        var k = key.ToLowerInvariant();
        var rootId = JobIdFromPath(path);
        if (string.IsNullOrWhiteSpace(rootId)) return false;
        if (k is "current_line_job_root" or "current_line_job_dir")
        {
            return !string.IsNullOrWhiteSpace(currentLineId)
                   && !string.Equals(rootId, currentLineId, StringComparison.OrdinalIgnoreCase);
        }

        if (k is "current_direct_job_root" or "current_direct_job_dir")
        {
            return !string.IsNullOrWhiteSpace(currentDirectId)
                   && !string.Equals(rootId, currentDirectId, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static string? Str(JsonObject obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (obj[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                return s.Trim();
        }

        return null;
    }

    private static JsonNode? StripPayloadNode(JsonNode node)
    {
        if (node is JsonObject obj)
            return Allowlist(obj);
        if (node is JsonArray arr)
        {
            var copy = new JsonArray();
            foreach (var item in arr)
            {
                copy.Add(item is null ? null : StripPayloadNode(item));
            }

            return copy;
        }

        return node.DeepClone();
    }

    internal static bool IsAuthorizedMetadataPointerKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        var k = key.ToLowerInvariant();
        if (k.Contains("prompt", StringComparison.Ordinal)
            || k.Contains("response", StringComparison.Ordinal)
            || k.Contains("grok_result", StringComparison.Ordinal)
            || k.Contains("grok-result", StringComparison.Ordinal)
            || k.Contains("transcript", StringComparison.Ordinal)
            || k.Contains("stdin", StringComparison.Ordinal))
        {
            return false;
        }

        if (k.Contains("acceptance", StringComparison.Ordinal)) return true;
        if (k.Contains("assessment", StringComparison.Ordinal)) return true;
        if (k.Contains("resume_result", StringComparison.Ordinal)) return true;
        if (k is "line_receipt" or "route_receipt" or "telephone_receipt" or "direct_receipt"
            or "actual_dispatch_receipt" or "native_identity_source" or "last_transport_receipt"
            or "last_direct_receipt" or "prepared_next_config" or "current_result"
            or "current_config" or "expected_result" or "lead_run_root" or "current_run_root"
            or "job_root" or "start_receipt_path" or "start_receipt")
        {
            return true;
        }

        return k is "binding" or "lead_binding" or "dashboard_active_work_path";
    }

    private static bool IsAllowedShortEffortScalar(string key, JsonNode? value)
    {
        if (key is not "reasoning" and not "reasoning_effort" and not "effort")
            return false;
        if (value is not JsonValue v || !v.TryGetValue<string>(out var s) || string.IsNullOrWhiteSpace(s))
            return false;
        s = s.Trim();
        return s.Length <= 16 && ShortEffortEnums.Contains(s);
    }

    internal static bool LooksHistoricalPointerKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        var k = key.ToLowerInvariant();
        if (k is "last_native_acceptance") return false;
        if (k.StartsWith("last_failed", StringComparison.Ordinal)) return true;
        if (k is "last_transport_receipt" or "last_direct_receipt" or "last_receipt_assessment")
            return true;
        if (k.StartsWith("previous_", StringComparison.Ordinal) || k.Contains("previous_", StringComparison.Ordinal))
            return true;
        return false;
    }

    internal static bool LooksLikeJsonMetadata(string path)
    {
        if (IsNonContractPayload(path)) return false;
        var ext = Path.GetExtension(path);
        if (string.Equals(ext, ".md", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".txt", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".ps1", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return LooksLikePath(path);
    }

    private static DateTimeOffset? ExtractFactAt(JsonObject data)
    {
        foreach (var key in FactTimeKeys)
        {
            if (data[key] is JsonValue v && v.TryGetValue<string>(out var text) && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            {
                return parsed.ToUniversalTime();
            }
        }

        return null;
    }

    private static DateTimeOffset ToOffset(DateTime utc)
    {
        return new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    private static bool IsDeniedPath(string path)
    {
        var leaf = Path.GetFileName(path);
        foreach (var fragment in DeniedNameFragments)
        {
            if (leaf.Contains(fragment, StringComparison.OrdinalIgnoreCase) || path.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                // allow protocol field names inside json; deny only file names that look like secrets
                if (fragment is "prompt" && !leaf.Contains("prompt", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (leaf.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal static bool LooksLikePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var t = value.Trim();
        // Ordinary English/Chinese prose (including "actual-delta/named-evidence") is not a file pointer.
        if (t.Contains(' ') && t.IndexOfAny(new[] { ':', '\\' }) < 0)
            return false;
        if (t.Length >= 3 && char.IsLetter(t[0]) && t[1] == ':' && (t[2] == '\\' || t[2] == '/'))
            return true;
        if (t.StartsWith(@"\\", StringComparison.Ordinal))
            return true;
        if (t.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && t.Contains('\\'))
            return true;
        return false;
    }

    internal static bool LooksExecutorStatusResult(JsonObject obj)
    {
        var hasLine = obj["line_job_id"] is JsonValue lv && lv.TryGetValue<string>(out var line) && !string.IsNullOrWhiteSpace(line);
        var hasSelf = obj["self_accepted"] is JsonValue;
        var hasStatus = obj["current_status"] is JsonValue sv && sv.TryGetValue<string>(out var st) && !string.IsNullOrWhiteSpace(st);
        var schema = obj["schema"] is JsonValue sc && sc.TryGetValue<string>(out var s) ? s : null;
        var cockpitSchema = schema is not null && schema.Contains("user-cockpit", StringComparison.OrdinalIgnoreCase)
                            && schema.Contains("result", StringComparison.OrdinalIgnoreCase);
        return hasLine && (hasSelf || hasStatus || cockpitSchema);
    }

    internal static bool IsNonContractPayload(string path)
    {
        var leaf = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(leaf)) return false;
        if (NonContractPayloadNames.Contains(leaf)) return true;
        var lower = leaf.ToLowerInvariant();
        return lower.Contains("grok-result", StringComparison.Ordinal)
               || lower.Contains("grok_result", StringComparison.Ordinal)
               || lower is "response.json" or "prompt.json";
    }

    internal static bool IsHistoricalRegistryStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return false;
        var u = status.ToUpperInvariant();
        if (u.Contains("WAITING", StringComparison.Ordinal) || u.Contains("USER", StringComparison.Ordinal)
            || u.Contains("PENDING", StringComparison.Ordinal) || u.Equals("ACTIVE", StringComparison.Ordinal))
        {
            return false;
        }

        return u.Contains("PAUSE", StringComparison.Ordinal)
               || u.Contains("TERMINAL", StringComparison.Ordinal)
               || u.Contains("HISTOR", StringComparison.Ordinal)
               || u.Contains("RETIRED", StringComparison.Ordinal)
               || u.Contains("COMPLETED", StringComparison.Ordinal)
               || u.Equals("CANCELLED", StringComparison.Ordinal)
               || u.Equals("CANCELED", StringComparison.Ordinal);
    }

    private static string ScopeDetail(string scope, string? projectHint, string? context, string message)
    {
        return "scope=" + scope
               + ";project=" + (string.IsNullOrWhiteSpace(projectHint) ? "unspecified" : projectHint)
               + ";axis=" + (string.IsNullOrWhiteSpace(context) ? "source" : context)
               + "; " + message;
    }

    internal static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var trimmed = path.Trim();
        try
        {
            if (Path.IsPathRooted(trimmed) && (File.Exists(trimmed) || Directory.Exists(trimmed)))
            {
                return Path.GetFullPath(trimmed);
            }
        }
        catch (Exception)
        {
            // keep trimmed form for missing-path issues
        }

        return trimmed.Replace('\\', Path.DirectorySeparatorChar);
    }

    private static SourceIssue Issue(string sourceId, string code, string detail, DateTimeOffset observedAt)
    {
        return new SourceIssue(sourceId, code, detail, observedAt);
    }

    private sealed class FileBudget
    {
        private int _remaining;
        private bool _overflowIssued;

        public FileBudget(int maxFiles, int maxFileBytes)
        {
            _remaining = maxFiles <= 0 ? 512 : maxFiles;
            MaxFileBytes = maxFileBytes <= 0 ? 1_048_576 : maxFileBytes;
        }

        public int MaxFileBytes { get; }

        public bool TryConsumeSlot(out bool firstOverflow)
        {
            firstOverflow = false;
            if (_remaining <= 0)
            {
                if (!_overflowIssued)
                {
                    _overflowIssued = true;
                    firstOverflow = true;
                }

                return false;
            }

            _remaining--;
            return true;
        }
    }
}
