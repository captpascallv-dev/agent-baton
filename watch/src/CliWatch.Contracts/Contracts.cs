// SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Nodes;
namespace CliWatch.Contracts;
public enum DataQuality { Fresh, LastKnown, Unavailable, Conflicting, Partial }
public enum AttentionOwner { None, Lead, Secretary, User }
public sealed record SourceIssue(string SourceId, string Code, string Detail, DateTimeOffset ObservedAt, string? MailboxRoot = null, string? JobKey = null);
public sealed record EvidenceRef(string SourceId, string Location, string Kind, DateTimeOffset ObservedAt, DateTimeOffset? FactAt, DataQuality Quality, string? Reason = null);
public sealed record RawDocument(string Id, string Kind, string Location, JsonObject Data, DateTimeOffset ObservedAt, DateTimeOffset? FactAt = null, string? ProjectHint = null, string? Scope = null);
/// <summary>Type-safe observation identities; attempt and attempt_id are aliases, never competing authorities.</summary>
public static class ObservationIdentity
{
    public static string? Text(JsonObject? obj, string key) =>
        obj?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    public static bool TryAttempt(JsonObject? obj, out string? attempt)
    {
        attempt = null;
        if (obj is null) return true;
        foreach (var key in new[] { "attempt_id", "attempt" })
        {
            if (obj[key] is null) continue;
            if (obj[key] is not JsonValue value) return false;
            string? current;
            if (value.TryGetValue<string>(out var text)) current = text.Trim();
            else if (value.TryGetValue<long>(out var number)) current = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            else return false;
            if (string.IsNullOrWhiteSpace(current)) return false;
            if (long.TryParse(current, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var numeric))
                current = numeric.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (attempt is not null && !attempt.Equals(current, StringComparison.Ordinal)) return false;
            attempt = current;
        }
        return true;
    }

    public static bool Matches(JsonObject evidence, JsonObject job, bool requireIdentity = false, bool adapterIdentity = false)
    {
        if (!TryAttempt(evidence, out var actual) || !TryAttempt(job, out var expected)) return false;
        var id = Text(evidence, "job_id");
        var expectedId = (adapterIdentity ? Text(job, "adapter_job_id") : null) ?? Text(job, "job_id");
        if (evidence["job_id"] is not null && id is null) return false;
        if (requireIdentity && (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(actual))) return false;
        return (string.IsNullOrWhiteSpace(id) || id.Equals(expectedId, StringComparison.Ordinal))
            && (actual is null || actual.Equals(expected, StringComparison.Ordinal));
    }
}
public sealed record CollectionBatch(DateTimeOffset CollectedAt, IReadOnlyList<RawDocument> Documents, IReadOnlyList<SourceIssue> Issues);
public sealed record CollectorSettings(IReadOnlyList<string> RegistryPaths, IReadOnlyList<string> AdditionalSourcePaths, int MaxFileBytes = 1048576, int MaxFilesPerRefresh = 512)
{
    public IReadOnlyList<string> MailboxRoots { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ClaudeMailboxRoots { get; init; } = Array.Empty<string>();
    public IReadOnlyList<ProcessDiscoveryRule> DiscoveryRules { get; init; } = Array.Empty<ProcessDiscoveryRule>();
    public string? GrokHome { get; init; }
    public string? CurrentScopeSourcesFile { get; init; }
    public string? DisplayCloseoutsFile { get; init; }
    public int QuietSeconds { get; init; } = 120;
    public int HistoryHours { get; init; } = 72;
    public bool SampleBanner { get; init; }
}

public sealed record ProcessDiscoveryRule(
    string Cli,
    string? Image = null,
    IReadOnlyList<string>? CommandContains = null,
    IReadOnlyList<string>? CommandExcludes = null,
    IReadOnlyList<string>? ExcludeImages = null,
    IReadOnlyList<string>? ScriptPackages = null);

public sealed record ObservedProcess(
    int Pid,
    int ParentPid,
    long StartTicks,
    DateTimeOffset StartedAt,
    string ImageName,
    string? ImagePath,
    string? CommandLine,
    long? CpuTimeTicks = null,
    long? ReadBytes = null,
    long? WriteBytes = null,
    bool CountersAvailable = false);

public sealed record ProcessIdentityObservation(int Pid, long ExpectedStartTicks, string State, DateTimeOffset ObservedAt);
public interface IProcessProbe { ValueTask<ProcessIdentityObservation> ObserveAsync(int pid, long expectedStartTicks, CancellationToken cancellationToken); }
public interface IProcessSnapshot
{
    IReadOnlyList<ObservedProcess> List(CancellationToken cancellationToken);
    ProcessIdentityObservation Observe(int pid, long expectedStartTicks);
}
public interface ICollector { Task<CollectionBatch> CollectAsync(CollectorSettings settings, CancellationToken cancellationToken); }
public sealed record SourceFact(string Kind, string EntityId, string? ProjectId, IReadOnlyDictionary<string,string> Links, JsonObject Values, IReadOnlyList<EvidenceRef> Evidence);
public sealed record FactBatch(DateTimeOffset CollectedAt, IReadOnlyList<SourceFact> Facts, IReadOnlyList<SourceIssue> Issues);
public interface INormalizer { FactBatch Normalize(CollectionBatch batch); }
public sealed record WorkAxes(string Process, string Turn, string Execution, string Transport, string Delivery, string Callback, string LeadHandling, string Acceptance, string Adoption, string Goal);
public sealed record NavigationTarget(string Kind, string Target, string Label, string ProjectId, string? EntityId = null);
public sealed record ArtifactView(string Id, string Label, string State, NavigationTarget? Target, IReadOnlyList<EvidenceRef> Evidence);
public sealed record ProgressView(int Completed, int Total, string Basis, IReadOnlyList<EvidenceRef> Evidence);
public sealed record AttentionItem(string Id, AttentionOwner Owner, string Description, string Reason, IReadOnlyList<NavigationTarget> Targets, IReadOnlyList<EvidenceRef> Evidence);
public sealed record WorkView(
    string Id,
    string Role,
    string? Route,
    string Summary,
    WorkAxes Axes,
    IReadOnlyList<ArtifactView> Artifacts,
    IReadOnlyList<NavigationTarget> Targets,
    IReadOnlyList<EvidenceRef> Evidence,
    DataQuality Quality,
    string? ActorName = null,
    string? TaskName = null,
    string? Model = null,
    string? Effort = null,
    string? Blocker = null,
    string? ActorKind = null,
    string? ReviewAssigned = null);
public sealed record ProjectView(string Id, string Name, string Summary, string Phase, string? Goal, string? NextStep, bool IsActive, bool IsPaused, ProgressView? Progress, IReadOnlyList<WorkView> WorkItems, IReadOnlyList<AttentionItem> Attention, IReadOnlyList<NavigationTarget> Targets, DataQuality Quality, DateTimeOffset? LastFactAt, DateTimeOffset? ObservedActivityAt = null, string? ActivityIdentity = null);
public sealed record RouteCoverage(string RouteId, string Level, string? Gap, IReadOnlyList<EvidenceRef> Evidence);
public sealed record CockpitSnapshot(DateTimeOffset CollectedAt, IReadOnlyList<ProjectView> Projects, IReadOnlyList<SourceIssue> Issues, IReadOnlyList<RouteCoverage> Routes, DataQuality Quality);
public interface IProjector { CockpitSnapshot Project(FactBatch batch, CockpitSnapshot? previous = null); }
public sealed class NavigationRequestEventArgs : EventArgs {
    public NavigationTarget Target { get; }
    public NavigationRequestEventArgs(NavigationTarget target) { Target = target; }
}
