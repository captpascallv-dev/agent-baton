// SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CliWatch.Collection;

/// <summary>
/// Bind a recognized CLI from its real --prompt-file argument, or a native PI @file
/// that is exactly the canonical outbox DISPATCH.txt. Reads file size and SHA-256 only.
/// Does not keep prompt text or the command line.
/// </summary>
internal static class PromptFileIdentity
{
    public const string RequestProtocol = "agent-direct-grok-request-v1";
    public const string StoreFileName = "prompt-file-bindings.json";
    const long MaxIdentityBytes = 8L * 1024L * 1024L;
    const long MaxRequestBytes = 64L * 1024L;
    const int StoreVersion = 1;
    static readonly AsyncLocal<(Func<string, string?> Read, int MaxBytes)?> MetadataPolicy = new();

    internal static IDisposable UseMetadataReader(Func<string, string?> read, int maxBytes)
    {
        var previous = MetadataPolicy.Value;
        MetadataPolicy.Value = (read, maxBytes);
        return new MetadataScope(previous);
    }

    sealed class MetadataScope((Func<string, string?> Read, int MaxBytes)? previous) : IDisposable
    {
        public void Dispose() => MetadataPolicy.Value = previous;
    }

    internal static string? ReadMetadata(string path) => MetadataPolicy.Value is { } policy
        ? policy.Read(path) : File.ReadAllText(path);

    internal readonly record struct CliInvocation(
        string PromptPath,
        string Workspace,
        string SessionId,
        bool Resume,
        string? Model,
        string? ReasoningEffort);

    internal readonly record struct PromptFileProof(
        string AdapterDirectory,
        string PromptPath,
        long PromptBytes,
        string PromptSha256,
        string RequestPath,
        long RequestBytes,
        string RequestSha256,
        string DispatchPath,
        long DispatchBytes,
        string DispatchSha256,
        string RegistrationPath,
        long RegistrationBytes,
        string RegistrationSha256,
        string AdapterJobId,
        string SessionId,
        bool Resume,
        string Workspace)
    {
        public bool DirectDispatch { get; init; }
        public bool PiFileAttachment { get; init; }
        public string? Model { get; init; }
        public string? ReasoningEffort { get; init; }
    }

    public static bool HasPromptFileSwitch(string? commandLine)
    {
        if (!TrySplit(commandLine, out var args)) return false;
        return Take(args, "--prompt-file", out _) != FlagTake.Missing;
    }

    public static bool TryInvocation(string? commandLine, out CliInvocation invocation)
    {
        invocation = default;
        if (!TrySplit(commandLine, out var args)) return false;
        var prompt = Take(args, "--prompt-file", out var promptPath);
        var cwd = Take(args, "--cwd", out var cwdPath);
        var session = Take(args, "--session-id", out var sessionId);
        var resume = Take(args, "--resume", out var resumeId);
        var model = Take(args, "--model", out var modelId);
        var effort = Take(args, "--reasoning-effort", out var effortId);
        if (prompt != FlagTake.Value || cwd != FlagTake.Value) return false;
        if (session == FlagTake.Bad || resume == FlagTake.Bad || model == FlagTake.Bad || effort == FlagTake.Bad)
            return false;
        if (session == FlagTake.Value == (resume == FlagTake.Value)) return false;
        var sid = session == FlagTake.Value ? sessionId : resumeId;
        if (!IsGuid(sid)) return false;
        invocation = new CliInvocation(
            promptPath,
            cwdPath,
            sid,
            resume == FlagTake.Value,
            model == FlagTake.Value ? modelId : null,
            effort == FlagTake.Value ? effortId : null);
        return true;
    }

    public static bool TryMatch(
        JsonObject job,
        string jobFilePath,
        CliInvocation invocation,
        DateTimeOffset processStarted,
        out PromptFileProof proof)
    {
        proof = default;
        var project = Str(job, "project");
        var jobId = Str(job, "job_id");
        var attempt = Str(job, "attempt_id");
        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(jobId) || string.IsNullOrWhiteSpace(attempt))
            return false;
        var registered = ParseTime(Str(job, "registered_at"));
        if (registered is null || processStarted <= registered.Value) return false;

        if (!TryCanonicalFile(invocation.PromptPath, out var promptFull)) return false;
        // A native CLI may read the registered contract itself, without an adapter.
        // Exact canonical path identity is stronger than matching a copied prompt.
        if (TryCanonicalDirectory(Str(job, "outbox"), out var directOutbox)
            && SamePath(promptFull, Path.Combine(directOutbox, "DISPATCH.txt")))
        {
            var declaredDirect = Str(job, "dispatch_prompt");
            if (!string.IsNullOrWhiteSpace(declaredDirect)
                && (!TryCanonicalFile(declaredDirect, out var declaredPath) || !SamePath(declaredPath, promptFull))) return false;
            if (!TryCanonicalDirectory(invocation.Workspace, out var directWorkspace)
                || !TryCanonicalDirectory(Str(job, "workspace"), out var registeredWorkspace)
                || !SamePath(directWorkspace, registeredWorkspace)) return false;
            var registeredSession = Str(job, "resume_native_session_id");
            if (registeredSession is not null && !registeredSession.Equals(invocation.SessionId, StringComparison.OrdinalIgnoreCase)) return false;
            if (invocation.Model is not null && !IsToken(invocation.Model, 80)) return false;
            if (invocation.ReasoningEffort is not null && !IsToken(invocation.ReasoningEffort, 16)) return false;
            if (!TryFileIdentity(promptFull, out var directId) || !TryFileIdentity(jobFilePath, out var directRegistration, metadata: true)) return false;
            proof = new PromptFileProof("", directId.FullPath, directId.Bytes, directId.Sha256,
                "", 0, "", directId.FullPath, directId.Bytes, directId.Sha256,
                directRegistration.FullPath, directRegistration.Bytes, directRegistration.Sha256,
                "", invocation.SessionId, invocation.Resume, directWorkspace)
            {
                DirectDispatch = true, Model = invocation.Model, ReasoningEffort = invocation.ReasoningEffort
            };
            return true;
        }
        if (!promptFull.EndsWith(Path.DirectorySeparatorChar + "prompt.txt", StringComparison.OrdinalIgnoreCase)
            && !Path.GetFileName(promptFull).Equals("prompt.txt", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!TryFileIdentity(promptFull, out var promptId)) return false;
        var adapterDir = Path.GetDirectoryName(promptId.FullPath);
        if (string.IsNullOrWhiteSpace(adapterDir)) return false;
        var adapterJobId = Path.GetFileName(adapterDir);
        if (!IsGuid(adapterJobId)) return false;

        var requestPath = Path.Combine(adapterDir, "request.json");
        if (!TryFileIdentity(requestPath, out var requestId, metadata: true) || requestId.Bytes > MaxRequestBytes) return false;
        if (!TryReadRequest(requestId, out var request)) return false;
        if (!adapterJobId.Equals(request.JobId, StringComparison.OrdinalIgnoreCase)) return false;
        if (!SamePath(request.PromptPath, promptId.FullPath)) return false;
        if (request.PromptBytes != promptId.Bytes || !request.PromptSha256.Equals(promptId.Sha256, StringComparison.OrdinalIgnoreCase))
            return false;
        if (request.Resume != invocation.Resume) return false;
        if (!request.SessionId.Equals(invocation.SessionId, StringComparison.OrdinalIgnoreCase)) return false;
        if (!TryCanonicalDirectory(invocation.Workspace, out var cwd)) return false;
        if (!TryCanonicalDirectory(request.Workspace, out var requestWorkspace) || !SamePath(cwd, requestWorkspace))
            return false;
        if (invocation.Model is not null && !invocation.Model.Equals(request.Model, StringComparison.Ordinal))
            return false;
        if (invocation.ReasoningEffort is not null || request.ReasoningEffort is not null)
        {
            if (invocation.ReasoningEffort is null || request.ReasoningEffort is null
                || !invocation.ReasoningEffort.Equals(request.ReasoningEffort, StringComparison.Ordinal))
                return false;
        }

        var jobWorkspace = Str(job, "workspace");
        if (!string.IsNullOrWhiteSpace(jobWorkspace))
        {
            if (!TryCanonicalDirectory(jobWorkspace, out var registeredWorkspace) || !SamePath(registeredWorkspace, requestWorkspace))
                return false;
        }

        if (!TryCanonicalDirectory(Str(job, "outbox"), out var outbox)) return false;
        var dispatchPath = Path.Combine(outbox, "DISPATCH.txt");
        if (!TryFileIdentity(dispatchPath, out var dispatchId)) return false;
        if (!SamePath(dispatchId.FullPath, dispatchPath)) return false;
        var declared = Str(job, "dispatch_prompt");
        if (!string.IsNullOrWhiteSpace(declared))
        {
            if (!TryCanonicalFile(declared, out var declaredFull) || !SamePath(declaredFull, dispatchId.FullPath))
                return false;
        }
        if (dispatchId.Bytes != promptId.Bytes || !dispatchId.Sha256.Equals(promptId.Sha256, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!TryFileIdentity(jobFilePath, out var registrationId, metadata: true)) return false;
        if (!OwnerAgrees(adapterDir, invocation)) return false;

        proof = new PromptFileProof(
            adapterDir,
            promptId.FullPath,
            promptId.Bytes,
            promptId.Sha256,
            requestId.FullPath,
            requestId.Bytes,
            requestId.Sha256,
            dispatchId.FullPath,
            dispatchId.Bytes,
            dispatchId.Sha256,
            registrationId.FullPath,
            registrationId.Bytes,
            registrationId.Sha256,
            request.JobId,
            request.SessionId,
            request.Resume,
            requestWorkspace);
        return true;
    }

    public static bool TryMatchPiAttachment(
        JsonObject job,
        string jobFilePath,
        IReadOnlyList<string> fileArgs,
        DateTimeOffset processStarted,
        out PromptFileProof proof)
    {
        proof = default;
        if (fileArgs is not { Count: > 0 }) return false;
        var project = Str(job, "project");
        var jobId = Str(job, "job_id");
        var attempt = Str(job, "attempt_id");
        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(jobId) || string.IsNullOrWhiteSpace(attempt))
            return false;
        var registered = ParseTime(Str(job, "registered_at"));
        if (registered is null || processStarted <= registered.Value) return false;
        if (!TryCanonicalDirectory(Str(job, "outbox"), out var outbox)) return false;
        var canonicalDispatch = Path.Combine(outbox, "DISPATCH.txt");
        if (!TryCanonicalFile(canonicalDispatch, out var dispatchFull) || !SamePath(dispatchFull, canonicalDispatch))
            return false;
        var declared = Str(job, "dispatch_prompt");
        if (!string.IsNullOrWhiteSpace(declared)
            && (!TryCanonicalFile(declared, out var declaredPath) || !SamePath(declaredPath, dispatchFull)))
            return false;

        var attached = false;
        foreach (var file in fileArgs)
        {
            if (!TryCanonicalFile(file, out var full) || !SamePath(full, dispatchFull)) continue;
            attached = true;
            break;
        }

        if (!attached) return false;
        if (!TryFileIdentity(dispatchFull, out var dispatchId) || !TryFileIdentity(jobFilePath, out var registration, metadata: true))
            return false;
        proof = new PromptFileProof("", dispatchId.FullPath, dispatchId.Bytes, dispatchId.Sha256,
            "", 0, "", dispatchId.FullPath, dispatchId.Bytes, dispatchId.Sha256,
            registration.FullPath, registration.Bytes, registration.Sha256,
            "", "", false, "")
        {
            PiFileAttachment = true
        };
        return true;
    }

    public static bool StillVerified(PromptFileBindingRecord record, JsonObject job, string jobFilePath, string key, string mailboxRoot)
    {
        if (!string.Equals(record.Project, Str(job, "project"), StringComparison.Ordinal)) return false;
        if (!string.Equals(record.JobId, Str(job, "job_id"), StringComparison.Ordinal)) return false;
        if (!string.Equals(record.AttemptId, Str(job, "attempt_id"), StringComparison.Ordinal)) return false;
        if (!string.Equals(record.Owner, Str(job, "target_thread_id") ?? string.Empty, StringComparison.Ordinal)) return false;
        if (!string.Equals(record.Key, key, StringComparison.Ordinal)) return false;
        if (!SamePath(record.MailboxRoot, mailboxRoot)) return false;
        if (!SamePath(record.RegistrationPath, jobFilePath)) return false;
        if (!IdentityMatches(record.RegistrationPath, record.RegistrationBytes, record.RegistrationSha256, metadata: true)) return false;
        if (!IdentityMatches(record.DispatchPath, record.DispatchBytes, record.DispatchSha256)) return false;
        if (!IdentityMatches(record.PromptPath, record.PromptBytes, record.PromptSha256)) return false;
        if (!record.DirectDispatch && !record.PiFileAttachment
            && !IdentityMatches(record.RequestPath, record.RequestBytes, record.RequestSha256, metadata: true)) return false;
        if (!TryCanonicalDirectory(Str(job, "outbox"), out var outbox)) return false;
        var canonicalDispatch = Path.Combine(outbox, "DISPATCH.txt");
        if (!SamePath(canonicalDispatch, record.DispatchPath)) return false;
        var declared = Str(job, "dispatch_prompt");
        if (!string.IsNullOrWhiteSpace(declared))
        {
            if (!TryCanonicalFile(declared, out var declaredFull) || !SamePath(declaredFull, record.DispatchPath))
                return false;
        }
        if (record.PiFileAttachment)
            return !record.DirectDispatch && SamePath(record.PromptPath, record.DispatchPath);
        if (record.DirectDispatch)
            return SamePath(record.PromptPath, record.DispatchPath)
                && TryCanonicalDirectory(Str(job, "workspace"), out var directWorkspace)
                && SamePath(directWorkspace, record.Workspace);
        if (!TryFileIdentity(record.RequestPath, out var requestId, metadata: true) || requestId.Bytes > MaxRequestBytes) return false;
        if (!TryReadRequest(requestId, out var request)) return false;
        if (!request.JobId.Equals(record.AdapterJobId, StringComparison.OrdinalIgnoreCase)) return false;
        if (!request.SessionId.Equals(record.SessionId, StringComparison.OrdinalIgnoreCase)) return false;
        if (request.Resume != record.Resume) return false;
        if (!SamePath(request.PromptPath, record.PromptPath)) return false;
        if (request.PromptBytes != record.PromptBytes || !request.PromptSha256.Equals(record.PromptSha256, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!TryCanonicalDirectory(request.Workspace, out var workspace) || !SamePath(workspace, record.Workspace))
            return false;
        var dir = Path.GetDirectoryName(record.PromptPath);
        if (string.IsNullOrWhiteSpace(dir) || !SamePath(dir, Path.GetDirectoryName(record.RequestPath) ?? string.Empty))
            return false;
        if (!Path.GetFileName(dir).Equals(record.AdapterJobId, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    public static bool IsSameLiveProcess(CliWatch.Contracts.ObservedProcess process, PromptFileBindingRecord record)
    {
        if (process.Pid != record.CliPid || process.StartTicks != record.CliStartTicks) return false;
        if (string.IsNullOrWhiteSpace(process.CommandLine)) return true;
        if (record.PiFileAttachment)
        {
            if (record.DirectDispatch) return false;
            if (!ProcessMatch.TryReadPiFileArgs(process, out var fileArgs)) return false;
            foreach (var file in fileArgs)
            {
                if (TryCanonicalFile(file, out var full) && SamePath(full, record.PromptPath))
                    return true;
            }

            return false;
        }

        if (!TryInvocation(process.CommandLine, out var invocation)) return false;
        if (!TryCanonicalFile(invocation.PromptPath, out var prompt) || !SamePath(prompt, record.PromptPath)) return false;
        if (!TryCanonicalDirectory(invocation.Workspace, out var cwd) || !SamePath(cwd, record.Workspace)) return false;
        if (invocation.Resume != record.Resume) return false;
        if (!invocation.SessionId.Equals(record.SessionId, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    public static bool OwnerAllows(string adapterDirectory, CliWatch.Contracts.ObservedProcess cli, IReadOnlyList<CliWatch.Contracts.ObservedProcess> live, IReadOnlyDictionary<int, int> parents)
    {
        var path = Path.Combine(adapterDirectory, "owner.json");
        if (!File.Exists(path)) return true;
        if (!TryReadOwner(path, out var pid, out var ticks)) return false;
        CliWatch.Contracts.ObservedProcess? owner = null;
        foreach (var process in live)
        {
            if (process.Pid == pid && process.StartTicks == ticks)
            {
                owner = process;
                break;
            }
        }

        if (owner is null) return true;
        if (owner.Pid == cli.Pid) return true;
        return ProcessMatch.IsDescendant(parents, cli.Pid, owner.Pid);
    }

    static bool OwnerAgrees(string adapterDirectory, CliInvocation invocation)
    {
        _ = invocation;
        var path = Path.Combine(adapterDirectory, "owner.json");
        if (!File.Exists(path)) return true;
        return TryReadOwner(path, out _, out _);
    }

    static bool TryReadOwner(string path, out int pid, out long ticks)
    {
        pid = 0;
        ticks = 0;
        if (!TryFileIdentity(path, out var id, metadata: true) || id.Bytes > MaxRequestBytes) return false;
        try
        {
            var text = ReadMetadata(id.FullPath);
            if (text is null) return false;
            if (JsonNode.Parse(text) is not JsonObject obj) return false;
            pid = IntOf(obj, "pid") ?? 0;
            ticks = LongOf(obj, "start_time_utc_ticks") ?? 0;
            return pid > 0 && ticks > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    readonly record struct RequestInfo(
        string JobId,
        string Workspace,
        string SessionId,
        bool Resume,
        string Model,
        string? ReasoningEffort,
        string PromptPath,
        long PromptBytes,
        string PromptSha256);

    static bool TryReadRequest(FileIdentity identity, out RequestInfo info)
    {
        info = default;
        try
        {
            var text = ReadMetadata(identity.FullPath);
            if (text is null) return false;
            if (JsonNode.Parse(text) is not JsonObject obj) return false;
            if (!string.Equals(Str(obj, "protocol_version"), RequestProtocol, StringComparison.Ordinal)) return false;
            var jobId = Str(obj, "job_id");
            var workspace = Str(obj, "workspace");
            var sessionId = Str(obj, "session_id");
            var model = Str(obj, "model");
            if (!IsGuid(jobId) || string.IsNullOrWhiteSpace(workspace) || !IsGuid(sessionId) || !IsToken(model, 80))
                return false;
            if (obj["resume"] is not JsonValue resumeValue || !resumeValue.TryGetValue<bool>(out var resume))
                return false;
            string? effort = null;
            if (obj["reasoning_effort"] is not null)
            {
                effort = Str(obj, "reasoning_effort");
                if (!IsToken(effort, 16)) return false;
            }
            if (obj["prompt"] is not JsonObject prompt) return false;
            foreach (var kv in prompt)
            {
                if (kv.Key is not ("path" or "bytes" or "sha256")) return false;
            }
            var promptPath = Str(prompt, "path");
            var promptSha = Str(prompt, "sha256");
            var promptBytes = LongOf(prompt, "bytes");
            if (!TryCanonicalFile(promptPath, out var canonicalPrompt)) return false;
            if (promptBytes is null || promptBytes <= 0 || string.IsNullOrWhiteSpace(promptSha) || !IsSha256(promptSha))
                return false;
            info = new RequestInfo(jobId!, workspace!, sessionId!, resume, model!, effort, canonicalPrompt, promptBytes.Value, promptSha!.ToLowerInvariant());
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal readonly record struct FileIdentity(string FullPath, long Bytes, string Sha256);

    static bool IdentityMatches(string path, long bytes, string sha256, bool metadata = false)
    {
        if (!TryFileIdentity(path, out var id, metadata)) return false;
        return id.Bytes == bytes && id.Sha256.Equals(sha256, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool TryFileIdentity(string? rawPath, out FileIdentity identity, bool metadata = false)
    {
        identity = default;
        if (!TryCanonicalFile(rawPath, out var full)) return false;
        try
        {
            var limit = Math.Min(MaxIdentityBytes, MetadataPolicy.Value?.MaxBytes ?? MaxIdentityBytes);
            if (metadata && MetadataPolicy.Value is { } policy)
            {
                if (policy.Read(full) is null) return false;
                limit = Math.Min(limit, policy.MaxBytes);
            }
            var info = new FileInfo(full);
            if (!info.Exists || info.Length <= 0 || info.Length > limit) return false;
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            int read;
            long total = 0;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > limit) return false;
                hash.AppendData(buffer.AsSpan(0, read));
            }
            if (total != info.Length) return false;
            identity = new FileIdentity(full, total, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    static bool TryCanonicalFile(string? raw, out string full)
    {
        full = "";
        if (!TryFullPath(raw, out full)) return false;
        try
        {
            if (!File.Exists(full) || ContainsReparsePoint(full)) return false;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    static bool TryCanonicalDirectory(string? raw, out string full)
    {
        full = "";
        if (!TryFullPath(raw, out full)) return false;
        full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        try
        {
            if (string.IsNullOrWhiteSpace(full) || !Directory.Exists(full) || ContainsReparsePoint(full))
                return false;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    static bool TryFullPath(string? raw, out string full)
    {
        full = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var trimmed = raw.Trim();
        if (!Path.IsPathFullyQualified(trimmed)) return false;
        try
        {
            full = Path.GetFullPath(trimmed);
            return !string.IsNullOrWhiteSpace(full);
        }
        catch (Exception)
        {
            return false;
        }
    }

    static bool ContainsReparsePoint(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root)) return true;
        if (HasReparse(root)) return true;
        var current = root;
        var rest = fullPath.Substring(root.Length);
        foreach (var part in rest.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (HasReparse(current)) return true;
        }

        return false;
    }

    static bool HasReparse(string path)
    {
        var attrs = File.GetAttributes(path);
        return (attrs & FileAttributes.ReparsePoint) != 0;
    }

    public static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        var a = left.Trim().Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
        var b = right.Trim().Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
        return a.Equals(b, StringComparison.OrdinalIgnoreCase);
    }

    enum FlagTake { Missing, Value, Bad }

    static FlagTake Take(IReadOnlyList<string> args, string name, out string value)
    {
        value = "";
        var seen = false;
        for (var i = ProcessMatch.NativeOptionStart(args); i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == "--") break;
            string? found = null;
            if (arg.Equals(name, StringComparison.Ordinal))
            {
                if (i + 1 >= args.Count || IsSwitch(args[i + 1])) return FlagTake.Bad;
                found = args[++i];
            }
            else if (arg.StartsWith(name + "=", StringComparison.Ordinal))
            {
                found = arg[(name.Length + 1)..];
                if (found.Length == 0) return FlagTake.Bad;
            }
            else
            {
                if (ProcessMatch.NativeValueOptions.Contains(arg) && i + 1 < args.Count) i++;
                continue;
            }

            if (seen) return FlagTake.Bad;
            seen = true;
            value = found;
        }

        return seen ? FlagTake.Value : FlagTake.Missing;
    }

    static bool IsSwitch(string arg) => arg.Length > 1 && arg[0] == '-';

    static bool TrySplit(string? commandLine, out List<string> args)
    {
        args = new List<string>();
        if (string.IsNullOrEmpty(commandLine)) return false;
        var i = 0;
        var n = commandLine.Length;
        while (i < n)
        {
            while (i < n && (commandLine[i] == ' ' || commandLine[i] == '\t')) i++;
            if (i >= n) break;
            var sb = new StringBuilder();
            var inQuotes = false;
            while (i < n)
            {
                var slashes = 0;
                while (i < n && commandLine[i] == '\\')
                {
                    slashes++;
                    i++;
                }

                if (i < n && commandLine[i] == '"')
                {
                    sb.Append('\\', slashes / 2);
                    if ((slashes % 2) == 1)
                    {
                        sb.Append('"');
                        i++;
                        continue;
                    }

                    if (inQuotes && i + 1 < n && commandLine[i + 1] == '"')
                    {
                        sb.Append('"');
                        i += 2;
                        continue;
                    }

                    inQuotes = !inQuotes;
                    i++;
                    continue;
                }

                if (slashes > 0) sb.Append('\\', slashes);
                if (i >= n) break;
                if (!inQuotes && (commandLine[i] == ' ' || commandLine[i] == '\t')) break;
                sb.Append(commandLine[i]);
                i++;
            }

            args.Add(sb.ToString());
        }

        return args.Count > 0;
    }

    static bool IsGuid(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && Guid.TryParseExact(value, "D", out _);

    static bool IsSha256(string value) =>
        value.Length == 64 && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'));

    static bool IsToken(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        if (text.Length == 0 || text.Length > max) return false;
        foreach (var ch in text)
        {
            if (char.IsControl(ch) || char.IsWhiteSpace(ch)) return false;
        }

        return true;
    }

    static string? Str(JsonObject? obj, params string[] keys)
    {
        if (obj is null) return null;
        foreach (var key in keys)
        {
            if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
                return text;
        }

        return null;
    }

    static int? IntOf(JsonObject obj, string key)
    {
        var number = LongOf(obj, key);
        if (number is null || number < int.MinValue || number > int.MaxValue) return null;
        return (int)number.Value;
    }

    static long? LongOf(JsonObject obj, string key)
    {
        if (obj[key] is not JsonValue value) return null;
        if (value.TryGetValue<long>(out var whole)) return whole;
        if (value.TryGetValue<int>(out var small)) return small;
        if (value.TryGetValue<string>(out var text) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return null;
    }

    static DateTimeOffset? ParseTime(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            return parsed.ToUniversalTime();
        return null;
    }
}

internal sealed class PromptFileBindingRecord
{
    public bool DirectDispatch { get; init; }
    public bool PiFileAttachment { get; init; }
    public string? Model { get; init; }
    public string? ReasoningEffort { get; init; }
    public required string MailboxRoot { get; init; }
    public required string Project { get; init; }
    public required string JobId { get; init; }
    public required string AttemptId { get; init; }
    public required string Owner { get; init; }
    public required string Key { get; init; }
    public required string RegistrationPath { get; init; }
    public required long RegistrationBytes { get; init; }
    public required string RegistrationSha256 { get; init; }
    public required string DispatchPath { get; init; }
    public required long DispatchBytes { get; init; }
    public required string DispatchSha256 { get; init; }
    public required string PromptPath { get; init; }
    public required long PromptBytes { get; init; }
    public required string PromptSha256 { get; init; }
    public required string RequestPath { get; init; }
    public required long RequestBytes { get; init; }
    public required string RequestSha256 { get; init; }
    public required string AdapterJobId { get; init; }
    public required string SessionId { get; init; }
    public required bool Resume { get; init; }
    public required string Workspace { get; init; }
    public required int CliPid { get; init; }
    public required long CliStartTicks { get; init; }
    public required string CliStartedAt { get; init; }
    public string MapKey => MailboxRoot + "\n" + Key;

    public static PromptFileBindingRecord FromProof(
        string mailboxRoot,
        string project,
        string jobId,
        string attemptId,
        string owner,
        string key,
        PromptFileIdentity.PromptFileProof proof,
        int cliPid,
        long cliStartTicks,
        DateTimeOffset cliStartedAt) => new()
        {
            DirectDispatch = proof.DirectDispatch,
            PiFileAttachment = proof.PiFileAttachment,
            Model = proof.Model,
            ReasoningEffort = proof.ReasoningEffort,
            MailboxRoot = mailboxRoot,
            Project = project,
            JobId = jobId,
            AttemptId = attemptId,
            Owner = owner,
            Key = key,
            RegistrationPath = proof.RegistrationPath,
            RegistrationBytes = proof.RegistrationBytes,
            RegistrationSha256 = proof.RegistrationSha256,
            DispatchPath = proof.DispatchPath,
            DispatchBytes = proof.DispatchBytes,
            DispatchSha256 = proof.DispatchSha256,
            PromptPath = proof.PromptPath,
            PromptBytes = proof.PromptBytes,
            PromptSha256 = proof.PromptSha256,
            RequestPath = proof.RequestPath,
            RequestBytes = proof.RequestBytes,
            RequestSha256 = proof.RequestSha256,
            AdapterJobId = proof.AdapterJobId,
            SessionId = proof.SessionId,
            Resume = proof.Resume,
            Workspace = proof.Workspace,
            CliPid = cliPid,
            CliStartTicks = cliStartTicks,
            CliStartedAt = cliStartedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)
        };

    public JsonObject ToJson() => new()
    {
        ["direct_dispatch"] = DirectDispatch,
        ["pi_file_attachment"] = PiFileAttachment,
        ["model"] = Model,
        ["reasoning_effort"] = ReasoningEffort,
        ["mailbox_root"] = MailboxRoot,
        ["project"] = Project,
        ["job_id"] = JobId,
        ["attempt_id"] = AttemptId,
        ["owner"] = Owner,
        ["key"] = Key,
        ["registration_path"] = RegistrationPath,
        ["registration_bytes"] = RegistrationBytes,
        ["registration_sha256"] = RegistrationSha256,
        ["dispatch_path"] = DispatchPath,
        ["dispatch_bytes"] = DispatchBytes,
        ["dispatch_sha256"] = DispatchSha256,
        ["prompt_path"] = PromptPath,
        ["prompt_bytes"] = PromptBytes,
        ["prompt_sha256"] = PromptSha256,
        ["request_path"] = RequestPath,
        ["request_bytes"] = RequestBytes,
        ["request_sha256"] = RequestSha256,
        ["adapter_job_id"] = AdapterJobId,
        ["session_id"] = SessionId,
        ["resume"] = Resume,
        ["workspace"] = Workspace,
        ["cli_pid"] = CliPid,
        ["cli_start_ticks"] = CliStartTicks.ToString(CultureInfo.InvariantCulture),
        ["cli_started_at"] = CliStartedAt
    };

    public static bool TryRead(JsonObject obj, out PromptFileBindingRecord? record)
    {
        record = null;
        var mailbox = Text(obj, "mailbox_root");
        var project = Text(obj, "project");
        var jobId = Text(obj, "job_id");
        var attempt = Text(obj, "attempt_id");
        var key = Text(obj, "key");
        var owner = obj["owner"] is JsonValue ownerValue && ownerValue.TryGetValue<string>(out var ownerText) ? ownerText : null;
        if (mailbox is null || project is null || jobId is null || attempt is null || key is null || owner is null)
            return false;
        if (!TryLong(obj, "registration_bytes", out var registrationBytes)
            || !TryLong(obj, "dispatch_bytes", out var dispatchBytes)
            || !TryLong(obj, "prompt_bytes", out var promptBytes)
            || !TryLong(obj, "request_bytes", out var requestBytes)
            || !TryInt(obj, "cli_pid", out var pid)
            || !TryTick(obj, out var ticks))
            return false;
        if (obj["resume"] is not JsonValue resumeValue || !resumeValue.TryGetValue<bool>(out var resume))
            return false;
        var registrationPath = Text(obj, "registration_path");
        var registrationSha = Text(obj, "registration_sha256");
        var dispatchPath = Text(obj, "dispatch_path");
        var dispatchSha = Text(obj, "dispatch_sha256");
        var promptPath = Text(obj, "prompt_path");
        var promptSha = Text(obj, "prompt_sha256");
        var direct = obj["direct_dispatch"] is JsonValue directValue && directValue.TryGetValue<bool>(out var isDirect) && isDirect;
        var piAttach = obj["pi_file_attachment"] is JsonValue piValue && piValue.TryGetValue<bool>(out var isPi) && isPi;
        if (direct && piAttach) return false;
        var bare = direct || piAttach;
        var requestPath = bare ? "" : Text(obj, "request_path");
        var requestSha = bare ? "" : Text(obj, "request_sha256");
        var adapterJob = bare ? "" : Text(obj, "adapter_job_id");
        var session = piAttach ? TextAllowEmpty(obj, "session_id") : Text(obj, "session_id");
        var workspace = piAttach ? TextAllowEmpty(obj, "workspace") : Text(obj, "workspace");
        var started = Text(obj, "cli_started_at");
        if (registrationPath is null || registrationSha is null || dispatchPath is null || dispatchSha is null
            || promptPath is null || promptSha is null || requestPath is null || requestSha is null
            || adapterJob is null || session is null || workspace is null || started is null || pid <= 0 || ticks <= 0)
            return false;
        record = new PromptFileBindingRecord
        {
            DirectDispatch = direct,
            PiFileAttachment = piAttach,
            Model = Text(obj, "model"),
            ReasoningEffort = Text(obj, "reasoning_effort"),
            MailboxRoot = mailbox,
            Project = project,
            JobId = jobId,
            AttemptId = attempt,
            Owner = owner,
            Key = key,
            RegistrationPath = registrationPath,
            RegistrationBytes = registrationBytes,
            RegistrationSha256 = registrationSha,
            DispatchPath = dispatchPath,
            DispatchBytes = dispatchBytes,
            DispatchSha256 = dispatchSha,
            PromptPath = promptPath,
            PromptBytes = promptBytes,
            PromptSha256 = promptSha,
            RequestPath = requestPath,
            RequestBytes = requestBytes,
            RequestSha256 = requestSha,
            AdapterJobId = adapterJob,
            SessionId = session,
            Resume = resume,
            Workspace = workspace,
            CliPid = pid,
            CliStartTicks = ticks,
            CliStartedAt = started
        };
        return true;
    }

    static string? Text(JsonObject obj, string key) =>
        obj[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    static string? TextAllowEmpty(JsonObject obj, string key) =>
        obj[key] is JsonValue value && value.TryGetValue<string>(out var text) && text is not null ? text : null;

    static bool TryLong(JsonObject obj, string key, out long number)
    {
        number = 0;
        if (obj[key] is not JsonValue value) return false;
        if (value.TryGetValue<long>(out number)) return true;
        if (value.TryGetValue<int>(out var small))
        {
            number = small;
            return true;
        }
        return false;
    }

    static bool TryInt(JsonObject obj, string key, out int number)
    {
        number = 0;
        if (!TryLong(obj, key, out var whole) || whole is < int.MinValue or > int.MaxValue) return false;
        number = (int)whole;
        return true;
    }

    static bool TryTick(JsonObject obj, out long ticks)
    {
        ticks = 0;
        if (obj["cli_start_ticks"] is not JsonValue value) return false;
        if (value.TryGetValue<string>(out var text))
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks) && ticks > 0;
        if (value.TryGetValue<long>(out ticks)) return ticks > 0;
        return false;
    }
}

internal static class PromptFileBindingStore
{
    const int MaxStoreBytes = 1_048_576;
    const int MaxRecords = 256;

    public static bool TryLoad(string directory, out Dictionary<string, PromptFileBindingRecord> records, out string error)
    {
        records = new Dictionary<string, PromptFileBindingRecord>(StringComparer.OrdinalIgnoreCase);
        error = "";
        var path = Path.Combine(directory, PromptFileIdentity.StoreFileName);
        if (!File.Exists(path)) return true;
        try
        {
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > MaxStoreBytes)
            {
                error = "unreadable";
                return false;
            }

            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root)
            {
                error = "unreadable";
                return false;
            }

            var version = root["version"] is JsonValue versionValue && versionValue.TryGetValue<int>(out var parsed) ? parsed : -1;
            if (version != 1)
            {
                error = "version";
                return false;
            }

            if (root["bindings"] is not JsonArray bindings)
            {
                error = "unreadable";
                return false;
            }

            if (bindings.Count > MaxRecords)
            {
                error = "unreadable";
                return false;
            }

            foreach (var item in bindings)
            {
                if (item is not JsonObject obj || !PromptFileBindingRecord.TryRead(obj, out var record) || record is null)
                {
                    error = "unreadable";
                    return false;
                }

                records[record.MapKey] = record;
            }

            return true;
        }
        catch (Exception)
        {
            error = "unreadable";
            return false;
        }
    }

    public static bool TrySave(string directory, Dictionary<string, PromptFileBindingRecord> records)
    {
        try
        {
            if (records.Count > MaxRecords) return false;
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, PromptFileIdentity.StoreFileName);
            var arr = new JsonArray();
            foreach (var record in records.Values.OrderBy(r => r.MapKey, StringComparer.OrdinalIgnoreCase))
                arr.Add(record.ToJson());
            var root = new JsonObject { ["version"] = 1, ["bindings"] = arr };
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString());
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
