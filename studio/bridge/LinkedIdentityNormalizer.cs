using System.Globalization;
using System.Text.Json.Nodes;
using CliWatch.Contracts;

namespace AiMoeStudio.Bridge;

/// <summary>
/// Join an explicit slot native_mailbox_job_id to its exact native attempt.
/// State classification remains entirely inside the installed CliWatchProjector.
/// This layer uses collected facts only and never opens an additional source.
/// </summary>
public sealed class LinkedIdentityNormalizer(INormalizer inner) : INormalizer
{
    static StringComparer PathComparer => OperatingSystem.IsMacOS() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
    static StringComparison PathComparison => OperatingSystem.IsMacOS() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
    readonly Dictionary<string, Registration> _registrations = new(PathComparer);
    readonly Dictionary<string, string> _acceptedTargets = new(PathComparer);
    readonly HashSet<string> _changedLinks = new(PathComparer);

    public FactBatch Normalize(CollectionBatch batch) => Reconcile(inner.Normalize(batch));

    public FactBatch Reconcile(FactBatch batch)
    {
        var facts = batch.Facts;
        var jobs = facts.Where(f => f.Kind == "mailbox_job").Select(Registration.Read)
            .Where(job => job is not null).Cast<Registration>().ToList();
        var duplicates = jobs.GroupBy(job => job.Identity, PathComparer)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(PathComparer);
        foreach (var job in jobs.Where(job => !duplicates.Contains(job.Identity)))
        {
            if (_registrations.TryGetValue(job.Identity, out var old)
                && Text(old.Fact, "native_mailbox_job_id") is { } oldLink
                && !oldLink.Equals(Text(job.Fact, "native_mailbox_job_id"), StringComparison.OrdinalIgnoreCase))
                _changedLinks.Add(job.Identity);
            _registrations[job.Identity] = job;
        }
        foreach (var duplicate in duplicates) _registrations.Remove(duplicate);

        // Partial collection rotates historical registration reads. Keep only identity
        // templates, never old process/READY facts; prune roots removed from config.
        var roots = facts.Where(f => f.Kind == "mailbox_root").Select(f => Root(f) ?? f.Links.GetValueOrDefault("location"))
            .Where(root => !string.IsNullOrWhiteSpace(root)).Select(root => CanonicalPath(root!))
            .Where(root => root is not null).ToHashSet(PathComparer);
        if (roots.Count > 0)
            foreach (var key in _registrations.Where(pair => !roots.Contains(pair.Value.Root)).Select(pair => pair.Key).ToArray())
                _registrations.Remove(key);

        var observed = jobs.Select(job => job.Identity).ToHashSet(PathComparer);
        var proposed = new List<Link>();
        var conflicts = new HashSet<string>(PathComparer);
        foreach (var source in _registrations.Values)
        {
            var nativeId = Text(source.Fact, "native_mailbox_job_id");
            if (!Guid.TryParseExact(nativeId, "D", out _) || nativeId.Equals(source.JobId, StringComparison.OrdinalIgnoreCase))
                continue;
            var targets = _registrations.Values.Where(target => target.JobId.Equals(nativeId, StringComparison.OrdinalIgnoreCase)
                && target.Attempt == source.Attempt && target.Identity != source.Identity).ToArray();
            if (targets.Length != 1)
            {
                if (targets.Length > 1) conflicts.Add(source.Identity);
                continue;
            }
            var target = targets[0];
            if (!observed.Contains(source.Identity) && !observed.Contains(target.Identity)) continue;
            if (!Compatible(source, target) || _changedLinks.Contains(source.Identity)
                || _acceptedTargets.TryGetValue(source.Identity, out var accepted) && accepted != target.Identity
                || !ProcessClaimsAgree(facts, source, target))
            {
                conflicts.Add(source.Identity);
                continue;
            }
            proposed.Add(new Link(source, target));
        }
        foreach (var group in proposed.GroupBy(link => link.Target.Identity, PathComparer).Where(group => group.Count() > 1))
            foreach (var link in group) conflicts.Add(link.Source.Identity);
        var links = proposed.Where(link => !conflicts.Contains(link.Source.Identity)).ToArray();
        if (links.Length == 0) return AddConflicts(batch, conflicts);

        var byMember = new Dictionary<string, Link>(PathComparer);
        foreach (var link in links)
        {
            byMember[link.Source.Identity] = link;
            byMember[link.Target.Identity] = link;
            _acceptedTargets[link.Source.Identity] = link.Target.Identity;
        }
        var output = new List<SourceFact>();
        var resolvedReadyLocations = new HashSet<string>(PathComparer);
        foreach (var fact in facts)
        {
            var identity = Identity(fact);
            if (identity is null || !byMember.TryGetValue(identity, out var link)) { output.Add(fact); continue; }
            if (fact.Kind is "mailbox_job" or "process_bind") continue;
            output.Add(Rebase(fact, link, resolvedReadyLocations));
        }
        foreach (var link in links)
        {
            // Original registration locations and IDs remain evidence of this round.
            output.Add(CombineRegistration(link, observed));
            var bindings = facts.Where(f => f.Kind == "process_bind" && IsMember(f, link)).ToArray();
            var chosen = bindings.OrderByDescending(IsLiveBinding)
                .ThenByDescending(f => Identity(f) == link.Target.Identity).FirstOrDefault();
            foreach (var binding in bindings)
            {
                var rebased = Rebase(binding, link, resolvedReadyLocations);
                output.Add(binding == chosen ? rebased : rebased with { Kind = "studio_original_process_bind" });
            }
        }
        var issues = batch.Issues
            .Where(issue => !(issue.Code == "ready_identity_conflict" && resolvedReadyLocations.Contains(issue.SourceId)))
            .Select(issue =>
            {
                var identity = Identity(issue.MailboxRoot, issue.JobKey);
                return identity is not null && byMember.TryGetValue(identity, out var link)
                    ? issue with { MailboxRoot = link.Source.Root, JobKey = link.Source.Key } : issue;
            }).ToArray();
        return AddConflicts(batch with { Facts = output, Issues = issues }, conflicts);
    }

    static bool Compatible(Registration source, Registration target)
    {
        if (new[] { source.Fact, target.Fact }.Any(f => Flag(f, "corrupt") || Flag(f, "directory_identity_conflict")
            || Flag(f, "adapter_identity_conflict"))) return false;
        if (!string.IsNullOrWhiteSpace(Text(target.Fact, "native_mailbox_job_id"))) return false; // No link chains.
        if (!SamePath(Text(source.Fact, "outbox"), Text(target.Fact, "outbox"))
            || !SamePath(Text(source.Fact, "workspace"), Text(target.Fact, "workspace"))) return false;
        foreach (var key in new[] { "project", "harness" })
            if (Text(source.Fact, key) is { Length: > 0 } left && Text(target.Fact, key) is { Length: > 0 } right
                && !left.Equals(right, StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var key in new[] { "native_job_id", "native_adapter_job_id", "adapter_job_id" })
            if (Text(source.Fact, key) is { } value && Guid.TryParseExact(value, "D", out _)
                && !value.Equals(target.JobId, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    static bool ProcessClaimsAgree(IReadOnlyList<SourceFact> facts, Registration source, Registration target)
    {
        var live = facts.Where(f => f.Kind == "process_bind" && IsLiveBinding(f)
            && (SameIdentity(Identity(f), source.Identity) || SameIdentity(Identity(f), target.Identity))).ToArray();
        if (live.Length < 2) return true;
        if (live.Any(f => !Positive(f, Flag(f, "cli_alive") ? "cli_pid" : "wrapper_pid")
            || !Positive(f, Flag(f, "cli_alive") ? "cli_start_ticks" : "wrapper_start_ticks"))) return false;
        var claims = live.Select(f => Flag(f, "cli_alive")
            ? "cli:" + Scalar(f.Values, "cli_pid") + ":" + Scalar(f.Values, "cli_start_ticks")
            : "wrapper:" + Scalar(f.Values, "wrapper_pid") + ":" + Scalar(f.Values, "wrapper_start_ticks")).Distinct().ToArray();
        return claims.Length == 1 && !claims[0].EndsWith(":", StringComparison.Ordinal);
    }

    static SourceFact CombineRegistration(Link link, HashSet<string> observed)
    {
        var source = link.Source;
        var values = (JsonObject)source.Fact.Values.DeepClone();
        foreach (var key in new[] { "task_summary", "registered_model", "registered_effort", "execution_model",
            "reasoning_effort", "adapter_state_root", "adapter_job_id", "source_kind", "source_label",
            "native_supervisor_verified", "native_supervisor_absent" })
            if (values[key] is null && link.Target.Fact.Values[key] is { } value) values[key] = value.DeepClone();
        if (!observed.Contains(source.Identity)) values["collection_partial"] = true;
        values["studio_identity_aliases"] = new JsonArray(source.Key, link.Target.Key);
        return source.Fact with { Values = values, Evidence = source.Fact.Evidence.Concat(link.Target.Fact.Evidence).Distinct().ToArray() };
    }

    static SourceFact Rebase(SourceFact fact, Link link, HashSet<string> resolvedReadyLocations)
    {
        var values = (JsonObject)fact.Values.DeepClone();
        var links = new Dictionary<string, string>(fact.Links, StringComparer.Ordinal)
        {
            ["job_key"] = link.Source.Key, ["mailbox_root"] = link.Source.Root
        };
        values["job_key"] = link.Source.Key;
        values["mailbox_root"] = link.Source.Root;
        values["studio_original_job_key"] = Key(fact);
        if (Text(fact, "job_id")?.Equals(link.Target.JobId, StringComparison.OrdinalIgnoreCase) == true)
            values["job_id"] = link.Source.JobId;
        // Plain READY identity was rejected only because its explicit ID used the
        // other linked registration. The same attempt/shared outbox now establishes
        // that identity; this does not change its status or invent acceptance.
        if (fact.Kind == "outbox_ready" && !Flag(fact, "corrupt")
            && ObservationIdentity.TryAttempt(fact.Values, out var attempt) && attempt == link.Source.Attempt
            && Text(fact, "job_id") is { } readyId
            && (readyId.Equals(link.Source.JobId, StringComparison.OrdinalIgnoreCase)
                || readyId.Equals(link.Target.JobId, StringComparison.OrdinalIgnoreCase))
            && SamePath(fact.Links.GetValueOrDefault("location"), Path.Combine(Text(link.Source.Fact, "outbox")!, "READY.json")))
        {
            values["identity_ok"] = true;
            if (fact.Links.GetValueOrDefault("location") is { } location) resolvedReadyLocations.Add(location);
        }
        return fact with { Values = values, Links = links };
    }

    FactBatch AddConflicts(FactBatch batch, HashSet<string> conflicts)
    {
        if (conflicts.Count == 0) return batch;
        var issues = batch.Issues.ToList();
        foreach (var conflict in conflicts)
            if (_registrations.TryGetValue(conflict, out var source))
                issues.Add(new SourceIssue("studio_identity", "studio_identity_conflict",
                    "明确任务链接与轮次、来源或进程身份有冲突，保留独立记录。", batch.CollectedAt, source.Root, source.Key));
        return batch with { Issues = issues };
    }

    static bool SameIdentity(string? left, string right) => string.Equals(left, right, PathComparison);
    static bool IsMember(SourceFact fact, Link link) => SameIdentity(Identity(fact), link.Source.Identity) || SameIdentity(Identity(fact), link.Target.Identity);
    static bool Positive(SourceFact fact, string key) => long.TryParse(Scalar(fact.Values, key), out var value) && value > 0;
    static bool IsLiveBinding(SourceFact fact) => Flag(fact, "cli_alive") || Flag(fact, "wrapper_alive");
    static bool SamePath(string? left, string? right) => CanonicalPath(left) is { } full && full.Equals(CanonicalPath(right), PathComparison);
    static string? CanonicalPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) return null;
        try { return Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch (Exception) { return null; }
    }
    static string? Text(SourceFact fact, string key) => ObservationIdentity.Text(fact.Values, key);
    static bool Flag(SourceFact fact, string key) => fact.Values[key] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
    static string? Root(SourceFact fact) => fact.Links.GetValueOrDefault("mailbox_root") ?? Text(fact, "mailbox_root");
    static string? Key(SourceFact fact) => fact.Links.GetValueOrDefault("job_key") ?? Text(fact, "job_key") ?? Text(fact, "key");
    static string? Identity(SourceFact fact) => Identity(Root(fact), Key(fact));
    static string? Identity(string? root, string? key) => CanonicalPath(root) is { } full && !string.IsNullOrWhiteSpace(key) ? full + "\n" + key : null;
    static string? Scalar(JsonObject values, string key)
    {
        if (values[key] is not JsonValue value) return null;
        if (value.TryGetValue<string>(out var text)) return text;
        return value.TryGetValue<long>(out var number) ? number.ToString(CultureInfo.InvariantCulture) : null;
    }
    sealed record Registration(SourceFact Fact, string Root, string Key, string JobId, string Attempt, string Identity)
    {
        public static Registration? Read(SourceFact fact)
        {
            var root = CanonicalPath(LinkedIdentityNormalizer.Root(fact));
            var key = LinkedIdentityNormalizer.Key(fact);
            var job = Text(fact, "job_id");
            if (root is null || job is null || !ObservationIdentity.TryAttempt(fact.Values, out var attempt)
                || attempt is null || key != job + "--" + attempt) return null;
            return new Registration(fact, root, key, job, attempt, root + "\n" + key);
        }
    }
    sealed record Link(Registration Source, Registration Target);
}
