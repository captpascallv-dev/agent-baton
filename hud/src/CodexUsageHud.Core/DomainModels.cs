using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CodexUsageHud.Core;

public readonly record struct TokenComponents(
    long? InputTokens,
    long? CachedInputTokens,
    long? CacheReadInputTokens,
    long? CacheWriteInputTokens,
    long? OutputTokens,
    long? ReasoningOutputTokens,
    long? TotalTokens)
{
    public static TokenComponents Empty => new(null, null, null, null, null, null, null);

    public bool HasValue => InputTokens.HasValue || CachedInputTokens.HasValue ||
                            CacheReadInputTokens.HasValue || CacheWriteInputTokens.HasValue ||
                            OutputTokens.HasValue || ReasoningOutputTokens.HasValue || TotalTokens.HasValue;

    public CanonicalTokenUsage ToCanonical()
    {
        var input = NonNegative(InputTokens ?? 0);
        var cached = Math.Max(NonNegative(CachedInputTokens ?? 0), NonNegative(CacheReadInputTokens ?? 0));
        cached = Math.Min(cached, input);
        var cacheWrite = NonNegative(CacheWriteInputTokens ?? 0);
        var output = NonNegative(OutputTokens ?? 0);
        var reasoning = Math.Min(NonNegative(ReasoningOutputTokens ?? 0), output);
        return new CanonicalTokenUsage(input, Math.Max(input - cached, 0), cached, cacheWrite,
            output, reasoning, SaturatingAdd(input, output), NonNegative(TotalTokens ?? 0));
    }

    public string ToFingerprintPart() => string.Join(',',
        Normalize(InputTokens), Normalize(CachedInputTokens), Normalize(CacheReadInputTokens),
        Normalize(CacheWriteInputTokens), Normalize(OutputTokens), Normalize(ReasoningOutputTokens),
        Normalize(TotalTokens));

    private static string Normalize(long? value) => value.HasValue
        ? "v:" + value.Value.ToString(CultureInfo.InvariantCulture)
        : "m";

    internal static long NonNegative(long value) => value < 0 ? 0 : value;

    internal static long SaturatingAdd(long left, long right)
    {
        if (right > 0 && left > long.MaxValue - right) return long.MaxValue;
        if (right < 0 && left < long.MinValue - right) return long.MinValue;
        return left + right;
    }
}

public readonly record struct CanonicalTokenUsage(
    long Input,
    long RawInput,
    long CachedInput,
    long CacheWriteInput,
    long Output,
    long Reasoning,
    long Total,
    long ReportedTotal)
{
    public static CanonicalTokenUsage operator +(CanonicalTokenUsage left, CanonicalTokenUsage right) =>
        new(
            TokenComponents.SaturatingAdd(left.Input, right.Input),
            TokenComponents.SaturatingAdd(left.RawInput, right.RawInput),
            TokenComponents.SaturatingAdd(left.CachedInput, right.CachedInput),
            TokenComponents.SaturatingAdd(left.CacheWriteInput, right.CacheWriteInput),
            TokenComponents.SaturatingAdd(left.Output, right.Output),
            TokenComponents.SaturatingAdd(left.Reasoning, right.Reasoning),
            TokenComponents.SaturatingAdd(left.Total, right.Total),
            TokenComponents.SaturatingAdd(left.ReportedTotal, right.ReportedTotal));

    public CanonicalTokenUsage Negate() => new(
        0L - Input, 0L - RawInput, 0L - CachedInput, 0L - CacheWriteInput,
        0L - Output, 0L - Reasoning, 0L - Total, 0L - ReportedTotal);
}

public static class HudProduct
{
    public const string Version = "1.0.5";
}

public sealed record TokenUsageSnapshot(
    TokenComponents TotalUsage,
    TokenComponents LastUsage,
    long? ContextWindow)
{
    public const string LineageSemanticPrefix = "lineage-semantic-v2";
    public const string LineageSemanticLegacyPrefix = "lineage-semantic-v1";

    public string Fingerprint(string threadId)
    {
        var canonical = string.Join('|', "schema-1", threadId, TotalUsage.ToFingerprintPart(),
            LastUsage.ToFingerprintPart(), FormatPresence(ContextWindow));
        return HashText(canonical);
    }

    public string SemanticMaterial() => FormatLiveSemanticMaterial(TotalUsage, LastUsage, ContextWindow);

    public string SemanticIdentity() => HashText(SemanticMaterial());

    public string LineageSemanticIdentity() => SemanticIdentity();

    public string LegacySemanticMaterial()
    {
        var last = LastUsage.ToCanonical();
        var total = TotalUsage.ToCanonical();
        return FormatLegacySemanticMaterial(last, total.Input, total.Output, total.Total, ContextWindow);
    }

    public string LegacySemanticIdentity() => HashText(LegacySemanticMaterial());

    public static string FormatLiveSemanticMaterial(TokenComponents total, TokenComponents last,
        long? contextWindow) =>
        string.Join('|', LineageSemanticPrefix, total.ToFingerprintPart(), last.ToFingerprintPart(),
            FormatPresence(contextWindow));

    public static string FormatLineageSemanticMaterial(CanonicalTokenUsage last,
        long cumulativeInput, long cumulativeOutput, long cumulativeTotal, long? contextWindow) =>
        FormatLegacySemanticMaterial(last, cumulativeInput, cumulativeOutput, cumulativeTotal, contextWindow);

    public static string FormatLegacySemanticMaterial(CanonicalTokenUsage last,
        long cumulativeInput, long cumulativeOutput, long cumulativeTotal, long? contextWindow)
    {
        static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
        var lastPart = string.Join(',', Number(last.Input), Number(last.RawInput), Number(last.CachedInput),
            Number(last.CacheWriteInput), Number(last.Output), Number(last.Reasoning), Number(last.Total),
            Number(last.ReportedTotal));
        var totalPart = string.Join(',', Number(cumulativeInput), Number(cumulativeOutput),
            Number(cumulativeTotal));
        var context = contextWindow is > 0 ? "v:" + Number(contextWindow.Value) : "m";
        return string.Join('|', LineageSemanticLegacyPrefix, lastPart, totalPart, context);
    }

    public static string LineageSemanticMaterialFromStored(CanonicalTokenUsage last,
        long cumulativeInput, long cumulativeOutput, long cumulativeTotal, long? contextWindow) =>
        FormatLegacySemanticMaterial(last, cumulativeInput, cumulativeOutput, cumulativeTotal, contextWindow);

    public static string LineageSemanticIdentityFromStored(CanonicalTokenUsage last,
        long cumulativeInput, long cumulativeOutput, long cumulativeTotal, long? contextWindow) =>
        HashText(LineageSemanticMaterialFromStored(last, cumulativeInput, cumulativeOutput, cumulativeTotal,
            contextWindow));

    public static bool IsLiveSemanticMaterial(string? material) =>
        material is not null && material.StartsWith(LineageSemanticPrefix + "|", StringComparison.Ordinal);

    public static bool IsLegacySemanticMaterial(string? material) =>
        material is not null && material.StartsWith(LineageSemanticLegacyPrefix + "|", StringComparison.Ordinal);

    public static string ReplaceLiveContextWindow(string material, long? contextWindow)
    {
        if (!IsLiveSemanticMaterial(material)) return material;
        var separator = material.LastIndexOf('|');
        if (separator <= 0) return material;
        return material[..separator] + "|" + FormatPresence(contextWindow);
    }

    public static string FormatPresence(long? value) => value.HasValue
        ? "v:" + value.Value.ToString(CultureInfo.InvariantCulture)
        : "m";

    public static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public enum SessionKind
{
    Unknown,
    Primary,
    InternalTask,
}

public enum SessionSurface
{
    Unknown,
    App,
    Cli,
    InternalTask,
}

public sealed record SessionMetadata(
    string ThreadId,
    string? DisplayName,
    string? Role,
    string? Nickname,
    string? ProjectTag,
    string? Model,
    string? ReasoningEffort,
    string? ServiceTier,
    DateTimeOffset? LastActivityUtc,
    string? RolloutRelativePath,
    bool Pinned = false,
    SessionStatus StoredStatus = SessionStatus.Idle,
    string? CurrentTurnKey = null,
    long TurnSequence = 0,
    bool TurnOpen = false,
    string ServiceTierSource = ServiceTierProvenance.Unavailable,
    SessionKind Kind = SessionKind.Unknown,
    SessionSurface Surface = SessionSurface.Unknown,
    string? ParentThreadId = null,
    int? AgentDepth = null)
{
    public string SafeName => string.IsNullOrWhiteSpace(DisplayName) ? ShortId(ThreadId) : DisplayName.Trim();
    public string ShortThreadId => ShortId(ThreadId);
    private static string ShortId(string value) => value.Length <= 12 ? value : value[..12];
}

public enum SessionStatus
{
    Idle,
    Running,
    Unknown,
}

public enum RecentUsageKind
{
    LatestTurn,
    LatestEventDegraded,
    Unavailable,
}

public sealed record SessionContextMetrics(
    long? PostCompactionInputTokens,
    long? PostCompactionWindowTokens,
    int PostCompactionSampleCount,
    double? BaselineTrendPercentagePoints,
    double? TurnRunwayChangePercent,
    double? TokenRunwayChangePercent,
    bool UsesExplicitCompactionBoundaries)
{
    public bool HasReliablePostCompactionBaseline =>
        PostCompactionSampleCount >= 3 && PostCompactionInputTokens is > 0 &&
        PostCompactionWindowTokens is > 0;

    public double? PostCompactionPercent => HasReliablePostCompactionBaseline
        ? Math.Clamp(PostCompactionInputTokens!.Value * 100d / PostCompactionWindowTokens!.Value, 0d, 100d)
        : null;

    public double? MaximumRunwayShrinkPercent
    {
        get
        {
            var shrink = new[] { TurnRunwayChangePercent, TokenRunwayChangePercent }
                .Where(value => value.HasValue)
                .Select(value => Math.Max(0d, -value!.Value))
                .DefaultIfEmpty()
                .Max();
            return TurnRunwayChangePercent.HasValue || TokenRunwayChangePercent.HasValue ? shrink : null;
        }
    }
}

public enum DriftAssessmentLevel
{
    Unassessed = 0,
    Occasional = 1,
    Repeated = 2,
}

public sealed record SessionDriftAssessment(
    DriftAssessmentLevel Level,
    DateTimeOffset? ObservedAtUtc = null)
{
    public static SessionDriftAssessment Unassessed { get; } =
        new(DriftAssessmentLevel.Unassessed);
}

public sealed record SessionLifecycleMetrics(
    long AggregatedTurnCount,
    long Recent48HourTokens,
    long Recent48HourTurnCount,
    int ComparableSessionCount,
    int LifetimeTokenRank,
    int LifetimeTurnRank,
    int RecentTokenRank,
    int RecentTurnRank,
    double? LifetimeTokenMedianMultiple,
    double? LifetimeTurnMedianMultiple,
    double? RecentTokenMedianMultiple,
    double? RecentTurnMedianMultiple);

public sealed record SessionAggregate(
    SessionMetadata Metadata,
    SessionStatus Status,
    CanonicalTokenUsage SessionTotal,
    CanonicalTokenUsage LatestTurnTotal,
    DateTimeOffset? LastActivityUtc,
    string? CurrentTurnKey,
    bool IsInferredCurrent = false,
    RecentUsageKind RecentKind = RecentUsageKind.Unavailable,
    CanonicalTokenUsage RecentUsage = default,
    string RecentUsageConfidence = "不可用",
    SessionContextMetrics? ContextMetrics = null,
    SessionLifecycleMetrics? LifecycleMetrics = null,
    SessionDriftAssessment? DriftAssessment = null)
{
    public string RecentUsageLabel => RecentKind switch
    {
        RecentUsageKind.LatestTurn => "最近一轮",
        RecentUsageKind.LatestEventDegraded => "最近事件（降级）",
        _ => "不可用",
    };
}

public sealed record QuotaBucket(
    string Id,
    string Name,
    double UsedPercent,
    int WindowDurationMinutes,
    DateTimeOffset ResetsAtUtc)
{
    public bool HasValidWindow
    {
        get
        {
            if (WindowDurationMinutes <= 0) return false;
            try
            {
                _ = ResetsAtUtc.AddMinutes(-WindowDurationMinutes);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }
    }
    public double RemainingPercent => Math.Clamp(100d - UsedPercent, 0d, 100d);
    public DateTimeOffset CycleStartUtc => ResetsAtUtc.AddMinutes(-WindowDurationMinutes);
}

public enum QuotaSource
{
    OfficialAppServer,
    RolloutFallback,
    Unavailable,
}

public sealed record QuotaObservation(
    QuotaBucket? Primary,
    IReadOnlyList<QuotaBucket> Additional,
    QuotaSource Source,
    DateTimeOffset ObservedAtUtc,
    bool IsStale,
    string? ErrorCode = null,
    string? PlanType = null);

public sealed record ResetSignal(
    DateTimeOffset ObservedAtUtc,
    double PreviousRemainingPercent,
    double CurrentRemainingPercent,
    string ReasonCode);

public sealed record SourceIdentity(
    string ThreadId,
    string VolumeSerial,
    string FileId,
    string FallbackDigest,
    bool IsDegraded)
{
    public string StableKey => IsDegraded
        ? "fallback-v2:" + TokenUsageSnapshot.HashText("source-v2|" + ThreadId + "|" + FallbackDigest)
        : "win-v2:" + TokenUsageSnapshot.HashText(
            string.Join('|', "source-v2", ThreadId, VolumeSerial, FileId));

    // Kept as a source-compatibility alias; it is always a digest in schema v6.
    public string FallbackKey => FallbackDigest;
}

public readonly record struct SourceReadCursor(
    long CompleteOffset,
    bool DrainMode = false,
    long DrainLineStartOffset = 0,
    long DrainOffset = 0)
{
    public SourceReadCursor Normalize()
    {
        var complete = Math.Max(0, CompleteOffset);
        if (!DrainMode) return new SourceReadCursor(complete, false, 0, 0);
        var lineStart = Math.Max(complete, DrainLineStartOffset);
        var drain = Math.Max(lineStart, DrainOffset);
        return new SourceReadCursor(complete, true, lineStart, drain);
    }
}

public sealed record SourceFileState(
    SourceIdentity Identity,
    string RelativePath,
    int Generation,
    SourceReadCursor Cursor,
    long LastObservedLength,
    long LastWriteTimeUtcTicks,
    string? CursorTurnKey,
    string HealthCode,
    long StateRevision)
{
    public long CompleteOffset => Cursor.CompleteOffset;
}

public sealed record SourceStateExpectation(
    string SourceKey,
    bool Exists,
    int Generation,
    SourceReadCursor Cursor,
    long StateRevision);

public sealed record ParsedEvent(
    string EventKind,
    string? ThreadId,
    string? Model,
    string? ReasoningEffort,
    string? ServiceTier,
    string? TurnKey,
    bool? TaskStarted,
    TokenUsageSnapshot? TokenSnapshot,
    DateTimeOffset? EventTimeUtc,
    string? ErrorCode = null,
    long? SourceOffset = null);

public sealed record ParseBatchResult(
    IReadOnlyList<ParsedEvent> Events,
    SourceReadCursor Cursor,
    int LinesRead,
    int LinesSkipped,
    IReadOnlyList<string> ErrorCodes,
    long BytesRead = 0,
    int PeakRetainedBytes = 0,
    bool HasMoreData = false,
    bool StoppedByBudget = false)
{
    public long LastCompleteOffset => Cursor.CompleteOffset;
}

public sealed record TokenSampleCandidate(
    string ThreadId,
    TokenUsageSnapshot Snapshot,
    DateTimeOffset? EventTimeUtc,
    DateTimeOffset ObservedAtUtc,
    string? TurnKey,
    string? Model,
    string? ServiceTier,
    string? SourceKey = null,
    int SourceGeneration = 0,
    long? SourceOffset = null,
    string TurnConfidence = "unavailable",
    string EventOrderConfidence = "unavailable");

public sealed record StructuralCursor(
    string EventIdentity,
    string SourceIdentityHash,
    int SourceGeneration,
    long SourceOffset,
    DateTimeOffset? EventTimeUtc);

public sealed record StructuralEventRecord(
    string EventIdentity,
    string BaseIdentity,
    string ThreadId,
    string EventKind,
    string? AssociationKey,
    DateTimeOffset? EventTimeUtc,
    string SourceIdentityHash,
    int SourceGeneration,
    long SourceOffset,
    int Occurrence);

public sealed record ParserErrorRecord(
    string RelativePath,
    long Offset,
    string ErrorCode,
    DateTimeOffset ObservedAtUtc);

public sealed record ScanTransactionRequest(
    SourceStateExpectation ExpectedSource,
    SourceFileState ProposedSource,
    IReadOnlyList<ParsedEvent> OrderedSanitizedEvents,
    IReadOnlyList<ParserErrorRecord> Errors,
    DateTimeOffset ObservedAtUtc);

public enum TokenDisposition
{
    Accepted,
    OrphanRecovered,
    Duplicate,
}

public sealed record TokenDispositionRecord(
    string Fingerprint,
    TokenDisposition Disposition,
    long? SampleId,
    string? ThreadId,
    bool MetadataRecovered = false);

public sealed record StructuralDispositionRecord(
    string EventIdentity,
    bool Canonical,
    string? AssociationKey,
    bool AdvancedCurrentState);

public sealed record ScanCommitResult(
    bool Committed,
    SourceFileState? CommittedSource,
    int AcceptedSamples,
    int DuplicateSamples,
    int TokenWrites,
    int SessionWrites,
    int SourceWrites,
    IReadOnlyList<string> DiagnosticCodes,
    IReadOnlyList<SessionMetadata>? CommittedSessions = null,
    IReadOnlyList<TokenDispositionRecord>? TokenDispositions = null,
    IReadOnlyList<StructuralDispositionRecord>? StructuralDispositions = null);

public sealed record AppServerReadResult(
    QuotaObservation Observation,
    string RequestArguments,
    bool TierOverrideRequested,
    string? ErrorCode = null);

public sealed record AppServerQuotaIdentityResult(
    AppServerReadResult Quota,
    BoundAccountIdentity? Identity,
    AccountIdentityShape? IdentityShape = null,
    string? PlanType = null);

public sealed record HudSnapshot(
    QuotaObservation Quota,
    IReadOnlyList<SessionAggregate> Sessions,
    CanonicalTokenUsage? CycleTotal,
    DateTimeOffset GeneratedAtUtc,
    bool IsIndexing,
    string? FreshnessMessage,
    IReadOnlyList<string> RecentErrorCodes,
    IReadOnlyList<HudEvent>? RecentEvents = null,
    bool AggregateMigrationPending = false,
    CanonicalTokenUsage? RunningCycleTotal = null,
    ProviderQuotaBoard? Providers = null,
    string? PrimaryBindingNote = null,
    int? MachineLocalSessionCount = null,
    int? MachineLocalRunningCount = null,
    int VerifiedAnalysisCount = 0,
    int UnverifiedHistoryCount = 0,
    int ForeignHistoryCount = 0,
    AccountAnalysisState AccountAnalysisState = AccountAnalysisState.SchemaUnavailable,
    IReadOnlyList<SessionAggregate>? UnverifiedSessions = null);

public sealed record HudFrame(
    HudSnapshot Snapshot,
    string CollapsedText,
    string OverviewText,
    string FreshnessText,
    string EventsText,
    string ThresholdText,
    IReadOnlyList<SessionDisplayRow> Rows,
    int BuildThreadId,
    long BuildElapsedMilliseconds);

public sealed record HudEvent(
    DateTimeOffset ObservedAtUtc,
    string Kind,
    string Code,
    string Summary);

public sealed record DatabasePerformanceMetrics(
    long RecurringTokenHistoryRowsRead,
    long RecurringTurnRowsRead,
    long CycleAggregateRowsRead,
    long CycleBucketRowsRead,
    long CycleBoundarySampleRowsRead,
    int LastRebuildBatchRows,
    long LastRebuildBatchMilliseconds);

public static class ServiceTierProvenance
{
    public const string Unavailable = "unavailable";
    public const string CatalogDefault = "catalog-default";
    public const string LegacyPreserved = "legacy-preserved";
    public const string RolloutExplicit = "rollout-explicit";

    public static bool IsValid(string? value) => value is Unavailable or CatalogDefault or
        LegacyPreserved or RolloutExplicit;

    public static int Priority(string? value) => value switch
    {
        RolloutExplicit => 3,
        LegacyPreserved => 2,
        CatalogDefault => 1,
        _ => 0,
    };
}

public static class TokenFormat
{
    public static string Compact(long value)
    {
        if (value >= 1_000_000_000)
            return (value / 1_000_000_000d).ToString("0.0", CultureInfo.InvariantCulture) + "B";
        if (value >= 1_000_000)
            return (value / 1_000_000d).ToString("0.0", CultureInfo.InvariantCulture) + "M";
        if (value >= 1_000)
            return (value / 1_000d).ToString("0.0", CultureInfo.InvariantCulture) + "K";
        return value.ToString(CultureInfo.InvariantCulture);
    }
}
