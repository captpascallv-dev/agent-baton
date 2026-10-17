// SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Nodes;
using CliWatch.Contracts;

namespace CliWatch.Collection;

public sealed partial class MailboxCollector
{
    // Explicit observer configuration identifies the original Lead's control
    // root. Executor outboxes cannot nominate their own acceptance document.
    async Task ReadLeadDispositionsAsync(JsonObject source, List<RawDocument> jobs,
        List<RawDocument> docs, List<SourceIssue> issues, HashSet<string> seen,
        DateTimeOffset now, CancellationToken ct)
    {
        var root = Str(source, "lead_review_root");
        var target = Str(source, "target_thread_id");
        if (!SafeObservationPath(root) || !Directory.Exists(root) || string.IsNullOrWhiteSpace(target)) return;
        var relevant = jobs.Where(j => Str(j.Data, "project") == Str(source, "project")
            && Str(j.Data, "target_thread_id") == target
            && PromptFileIdentity.SamePath(Str(j.Data, "mailbox_root"), Str(source, "mailbox_root"))).ToList();
        if (relevant.Count == 0) return;
        string[] directories;
        try { directories = Directory.GetDirectories(root!, "review*", SearchOption.TopDirectoryOnly).OrderBy(p => p, StringComparer.Ordinal).Take(257).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            issues.Add(new SourceIssue(root!, "lead_disposition_unavailable", "原 Lead 处置记录不可读，不据此归档。", now));
            return;
        }
        if (directories.Length > 256)
        {
            issues.Add(new SourceIssue(root!, "lead_disposition_limit", "原 Lead 处置目录超过本轮256项上限，不据此归档。", now));
            return;
        }
        foreach (var directory in directories)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(directory, "LEAD_DISPOSITION.json");
            if (!File.Exists(path)) continue;
            var disposition = await ObservationJson(path, "observer_lead_disposition", Str(source, "project"), null, docs, issues, seen, now, ct).ConfigureAwait(false);
            if (disposition is null || Flag(disposition.Data, "corrupt") || Str(disposition.Data, "accepted_by") != target
                || string.IsNullOrWhiteSpace(Str(disposition.Data, "decision"))) continue;
            var at = ParseTime(Str(disposition.Data, "at"));
            if (at is null || at > now) continue;
            var proofPath = Str(disposition.Data, "original_snapshot_verification") ?? Str(disposition.Data, "evidence");
            if (!SafeObservationPath(proofPath) || !PromptFileIdentity.SamePath(Path.GetDirectoryName(proofPath), directory)) continue;
            var proof = await ObservationJson(proofPath, "observer_lead_verification", Str(source, "project"), null, docs, issues, seen, now, ct).ConfigureAwait(false);
            var proofAt = proof is null ? null : ParseTime(Str(proof.Data, "at"));
            if (proof is null || Flag(proof.Data, "corrupt") || proofAt is null || proofAt > at) continue;
            var files = (proof.Data["files"] as JsonArray) ?? (proof.Data["safe_files"] as JsonArray);
            if (files is null) continue;
            foreach (var job in relevant)
            {
                var registered = ParseTime(Str(job.Data, "registered_at"));
                var outbox = Str(job.Data, "outbox");
                if (registered is null || registered > at || !SafeObservationPath(outbox)
                    || Path.GetFullPath(directory).StartsWith(Path.GetFullPath(outbox!) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                var readyPath = FindReadyPath(outbox);
                var ready = docs.FirstOrDefault(d => d.Kind == KindOutboxReady
                    && PromptFileIdentity.SamePath(d.Location, readyPath)
                    && Str(d.Data, "job_key") == Str(job.Data, "job_key")
                    && PromptFileIdentity.SamePath(Str(d.Data, "mailbox_root"), Str(job.Data, "mailbox_root")));
                if (ready is null || Flag(ready.Data, "corrupt") || Flag(job.Data, "directory_identity_conflict")
                    || ready.Data["identity_ok"] is JsonValue valid && valid.TryGetValue<bool>(out var identityOk) && !identityOk
                    || Str(ready.Data, "job_id") != Str(job.Data, "job_id")
                    || !ObservationIdentity.TryAttempt(ready.Data, out var readyAttempt)
                    || !ObservationIdentity.TryAttempt(job.Data, out var jobAttempt) || readyAttempt != jobAttempt
                    || Str(ready.Data, "status") is not ("completed" or "failed" or "blocked")
                    || ready.FactAt is null || ready.FactAt > at) continue;
                var report = Str(ready.Data, "report_file");
                if (!SafeObservationPath(report) || !PromptFileIdentity.SamePath(Path.GetDirectoryName(report), outbox)) continue;
                var entry = files.OfType<JsonObject>().FirstOrDefault(f => PromptFileIdentity.SamePath(Str(f, "source") ?? Str(f, "path"), report));
                if (entry is null || LongVal(entry, "bytes") is not > 0
                    || !FingerprintMatches(report, Str(entry, "sha256"), LongVal(entry, "bytes"), issues, now)) continue;
                // Record handling, never infer a passed business outcome from
                // file presence, a model claim or the word 'accepted'.
                job.Data["lead_disposition_verified"] = true;
                docs.Add(new RawDocument("lead-handled:" + Str(job.Data, "job_key"), "lead_disposition_record", path,
                    new JsonObject { ["job_key"] = Str(job.Data, "job_key"), ["mailbox_root"] = Str(job.Data, "mailbox_root"),
                        ["identity_ok"] = true, ["handled_at"] = at.Value.ToString("o") }, now, at, Str(source, "project"), "current"));
            }
        }
    }
}
