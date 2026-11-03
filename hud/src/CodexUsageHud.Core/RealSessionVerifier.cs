namespace CodexUsageHud.Core;

public sealed record RealThreadEvidence(
    string ThreadId,
    int ParsedTokenEvents,
    int DistinctTuples,
    int ExactReplayCount,
    int CumulativeInterleavings,
    CanonicalTokenUsage OracleTotal,
    CanonicalTokenUsage DatabaseTotal,
    long DatabaseSampleCount,
    long DatabaseFingerprintCount,
    bool ComponentsEqual,
    bool CountsEqual);

public sealed record RealSessionVerificationResult(
    IReadOnlyList<RealThreadEvidence> Threads,
    bool RestartUnchanged,
    bool SampleCountUnchanged,
    bool FingerprintCountUnchanged,
    bool SourceOffsetsUnchanged,
    bool DatabasePrivacyClean,
    bool DatabaseSentinelClean,
    bool DatabaseSchemaClean,
    bool DatabaseRelativePathsClean,
    string HudLogPrivacyStatus,
    string RenderedStringPrivacyStatus,
    string StatusCode,
    string? ErrorCode);

public sealed class RealSessionVerifier
{
    private readonly PrivacyJsonlReader _reader = new();

    public RealSessionVerificationResult Verify(string codexHome, string databasePath, string privacySentinel,
        CancellationToken cancellationToken = default)
    {
        if (File.Exists(databasePath))
        {
            return Failure("acceptance_database_not_fresh");
        }

        var home = CodexHomeResolver.Resolve(codexHome);
        var files = new RolloutDiscovery().Discover(home);
        var rows = new StateMetadataReader().Read(home);
        var byPath = rows.Where(row => !string.IsNullOrWhiteSpace(row.RolloutRelativePath))
            .GroupBy(row => row.RolloutRelativePath!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().ThreadId, StringComparer.OrdinalIgnoreCase);
        var selected = new List<SelectedLog>();
        foreach (var file in files.Where(item => item.Length is > 0 and <= 64L * 1024 * 1024)
                     .OrderByDescending(item => SafeWriteTime(item.FullPath)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = ReadComplete(file.FullPath, cancellationToken);
            var currentLength = SafeLength(file.FullPath);
            if (currentLength != file.Length || batch.LastCompleteOffset != file.Length || batch.HasMoreData ||
                batch.ErrorCodes.Contains("token_schema_degraded", StringComparer.Ordinal))
            {
                continue;
            }

            var thread = byPath.TryGetValue(file.RelativePath, out var metadataThread)
                ? metadataThread
                : FindThreadId(batch);
            if (string.IsNullOrWhiteSpace(thread) || selected.Any(item => item.ThreadId == thread))
            {
                continue;
            }

            var oracle = BuildOracle(thread, batch);
            if (oracle.Parsed == 0)
            {
                continue;
            }

            selected.Add(new SelectedLog(file, thread, oracle));
            if (selected.Count == 2) break;
        }

        if (selected.Count < 2)
        {
            return Failure("two_suitable_complete_threads_not_found", "INSUFFICIENT_REAL_THREADS");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var firstTotals = new Dictionary<string, CanonicalTokenUsage>(StringComparer.Ordinal);
        var firstSamples = new Dictionary<string, long>(StringComparer.Ordinal);
        var firstFingerprints = new Dictionary<string, long>(StringComparer.Ordinal);
        IReadOnlyDictionary<string, long> firstOffsets;
        using (var database = new UsageDatabase(databasePath))
        {
            var indexer = new RolloutIndexer(database);
            foreach (var item in selected)
            {
                ScanToEnd(indexer, item.File, item.ThreadId, cancellationToken);
                firstTotals[item.ThreadId] = database.GetSessionTotal(item.ThreadId);
                firstSamples[item.ThreadId] = database.GetSampleCount(item.ThreadId);
                firstFingerprints[item.ThreadId] = database.GetFingerprintCount(item.ThreadId);
            }
            firstOffsets = OffsetSnapshot(database);
        }

        var secondTotals = new Dictionary<string, CanonicalTokenUsage>(StringComparer.Ordinal);
        var secondSamples = new Dictionary<string, long>(StringComparer.Ordinal);
        var secondFingerprints = new Dictionary<string, long>(StringComparer.Ordinal);
        IReadOnlyDictionary<string, long> secondOffsets;
        bool databaseSentinelClean;
        bool databaseSchemaClean;
        bool databaseRelativePathsClean;
        using (var restarted = new UsageDatabase(databasePath))
        {
            var indexer = new RolloutIndexer(restarted);
            foreach (var item in selected)
            {
                ScanToEnd(indexer, item.File, item.ThreadId, cancellationToken);
                secondTotals[item.ThreadId] = restarted.GetSessionTotal(item.ThreadId);
                secondSamples[item.ThreadId] = restarted.GetSampleCount(item.ThreadId);
                secondFingerprints[item.ThreadId] = restarted.GetFingerprintCount(item.ThreadId);
            }
            secondOffsets = OffsetSnapshot(restarted);
            databaseSentinelClean = !restarted.ContainsPrivacySentinel(privacySentinel);
            var forbiddenColumns = new HashSet<string>(new[]
            {
                "first_user_message", "preview", "prompt", "response", "raw_json",
                "credential", "auth", "cookie", "token_text",
            }, StringComparer.OrdinalIgnoreCase);
            databaseSchemaClean = restarted.ReadSchemaColumnNames().Values.SelectMany(item => item)
                .All(column => !forbiddenColumns.Contains(column));
            databaseRelativePathsClean = restarted.LoadSourceStates().All(item =>
                !Path.IsPathRooted(item.RelativePath) &&
                !item.RelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Contains("..", StringComparer.Ordinal));
        }

        var evidence = selected.Select(item =>
        {
            var actual = firstTotals[item.ThreadId];
            var samples = firstSamples[item.ThreadId];
            var fingerprints = firstFingerprints[item.ThreadId];
            return new RealThreadEvidence(item.ThreadId, item.Oracle.Parsed, item.Oracle.Tuples.Count,
                item.Oracle.Parsed - item.Oracle.Tuples.Count, item.Oracle.Interleavings,
                item.Oracle.Total, actual, samples, fingerprints,
                item.Oracle.Total == actual,
                samples == item.Oracle.Tuples.Count && fingerprints == item.Oracle.Tuples.Count);
        }).ToArray();
        var totalsUnchanged = DictionariesEqual(firstTotals, secondTotals);
        var samplesUnchanged = DictionariesEqual(firstSamples, secondSamples);
        var fingerprintsUnchanged = DictionariesEqual(firstFingerprints, secondFingerprints);
        var offsetsUnchanged = DictionariesEqual(firstOffsets, secondOffsets);
        var okay = evidence.All(item => item.ComponentsEqual && item.CountsEqual) && totalsUnchanged &&
                   samplesUnchanged && fingerprintsUnchanged && offsetsUnchanged &&
                   databaseSentinelClean && databaseSchemaClean && databaseRelativePathsClean;
        return new RealSessionVerificationResult(evidence, totalsUnchanged, samplesUnchanged,
            fingerprintsUnchanged, offsetsUnchanged,
            databaseSentinelClean && databaseSchemaClean && databaseRelativePathsClean,
            databaseSentinelClean, databaseSchemaClean, databaseRelativePathsClean,
            "not_checked", "not_checked", okay ? "OK" : "MISMATCH", null);
    }

    private static void ScanToEnd(RolloutIndexer indexer, RolloutFile file, string threadId,
        CancellationToken cancellationToken)
    {
        for (var pass = 0; pass < 1024; pass++)
        {
            var result = indexer.ScanFile(file.FullPath, file.RelativePath, threadId, cancellationToken);
            if (!result.HasMoreData) return;
        }
        throw new InvalidOperationException("scan_batch_limit");
    }

    private ParseBatchResult ReadComplete(string path, CancellationToken cancellationToken)
    {
        var cursor = new SourceReadCursor(0);
        var events = new List<ParsedEvent>();
        var errors = new List<string>();
        var lines = 0;
        var skipped = 0;
        var bytes = 0L;
        var peak = 0;
        for (var pass = 0; pass < 4096; pass++)
        {
            var batch = _reader.Read(path, cursor, cancellationToken,
                PrivacyJsonlReader.DefaultBatchBytes, TimeSpan.FromSeconds(2));
            events.AddRange(batch.Events);
            errors.AddRange(batch.ErrorCodes);
            lines += batch.LinesRead;
            skipped += batch.LinesSkipped;
            bytes += batch.BytesRead;
            peak = Math.Max(peak, batch.PeakRetainedBytes);
            cursor = batch.Cursor;
            if (!batch.HasMoreData)
                return new ParseBatchResult(events, cursor, lines, skipped,
                    errors.Distinct(StringComparer.Ordinal).ToArray(), bytes, peak, false, false);
        }
        throw new InvalidOperationException("reader_batch_limit");
    }

    private static OracleEvidence BuildOracle(string threadId, ParseBatchResult batch)
    {
        var tuples = new HashSet<OracleTuple>();
        var total = default(CanonicalTokenUsage);
        var parsed = 0;
        var interleavings = 0;
        var maximumCumulative = -1L;
        foreach (var parsedEvent in batch.Events)
        {
            if (parsedEvent.TokenSnapshot is null) continue;
            parsed++;
            var tuple = OracleTuple.From(threadId, parsedEvent.TokenSnapshot);
            if (!tuples.Add(tuple)) continue;
            var cumulative = OracleCanonical(parsedEvent.TokenSnapshot.TotalUsage);
            if (maximumCumulative > cumulative.Total) interleavings++;
            maximumCumulative = Math.Max(maximumCumulative, cumulative.Total);
            total = OracleAdd(total, OracleCanonical(parsedEvent.TokenSnapshot.LastUsage));
        }
        return new OracleEvidence(parsed, tuples, total, interleavings);
    }

    private static CanonicalTokenUsage OracleCanonical(TokenComponents source)
    {
        var input = Math.Max(source.InputTokens ?? 0, 0);
        var cached = Math.Min(input, Math.Max(Math.Max(source.CachedInputTokens ?? 0, 0),
            Math.Max(source.CacheReadInputTokens ?? 0, 0)));
        var cacheWrite = Math.Max(source.CacheWriteInputTokens ?? 0, 0);
        var output = Math.Max(source.OutputTokens ?? 0, 0);
        var reasoning = Math.Min(output, Math.Max(source.ReasoningOutputTokens ?? 0, 0));
        return new CanonicalTokenUsage(input, Math.Max(input - cached, 0), cached, cacheWrite,
            output, reasoning, Saturating(input, output), Math.Max(source.TotalTokens ?? 0, 0));
    }

    private static CanonicalTokenUsage OracleAdd(CanonicalTokenUsage left, CanonicalTokenUsage right) => new(
        Saturating(left.Input, right.Input), Saturating(left.RawInput, right.RawInput),
        Saturating(left.CachedInput, right.CachedInput), Saturating(left.CacheWriteInput, right.CacheWriteInput),
        Saturating(left.Output, right.Output), Saturating(left.Reasoning, right.Reasoning),
        Saturating(left.Total, right.Total), Saturating(left.ReportedTotal, right.ReportedTotal));

    private static long Saturating(long left, long right) =>
        right > 0 && left > long.MaxValue - right ? long.MaxValue : left + right;

    private static string? FindThreadId(ParseBatchResult batch) =>
        batch.Events.FirstOrDefault(item => item.EventKind == "session_meta")?.ThreadId ??
        batch.Events.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.ThreadId))?.ThreadId;

    private static IReadOnlyDictionary<string, long> OffsetSnapshot(UsageDatabase database) =>
        database.LoadSourceStates().ToDictionary(item => item.Identity.StableKey,
            item => item.CompleteOffset, StringComparer.Ordinal);

    private static bool DictionariesEqual<T>(IReadOnlyDictionary<string, T> left,
        IReadOnlyDictionary<string, T> right) where T : notnull =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) &&
            EqualityComparer<T>.Default.Equals(pair.Value, value));

    private static DateTime SafeWriteTime(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch (IOException) { return DateTime.MinValue; }
        catch (UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (IOException) { return -1; }
        catch (UnauthorizedAccessException) { return -1; }
    }

    private static RealSessionVerificationResult Failure(string error,
        string status = "ERROR") => new(Array.Empty<RealThreadEvidence>(), false, false,
        false, false, false, false, false, false, "not_checked", "not_checked", status, error);

    private sealed record SelectedLog(RolloutFile File, string ThreadId, OracleEvidence Oracle);
    private sealed record OracleEvidence(int Parsed, HashSet<OracleTuple> Tuples,
        CanonicalTokenUsage Total, int Interleavings);

    private readonly record struct OracleTuple(
        string ThreadId,
        long? TotalInput, long? TotalCached, long? TotalCacheRead, long? TotalCacheWrite,
        long? TotalOutput, long? TotalReasoning, long? TotalReported,
        long? LastInput, long? LastCached, long? LastCacheRead, long? LastCacheWrite,
        long? LastOutput, long? LastReasoning, long? LastReported,
        long? ContextWindow)
    {
        public static OracleTuple From(string threadId, TokenUsageSnapshot snapshot) => new(
            threadId,
            snapshot.TotalUsage.InputTokens, snapshot.TotalUsage.CachedInputTokens,
            snapshot.TotalUsage.CacheReadInputTokens, snapshot.TotalUsage.CacheWriteInputTokens,
            snapshot.TotalUsage.OutputTokens, snapshot.TotalUsage.ReasoningOutputTokens,
            snapshot.TotalUsage.TotalTokens,
            snapshot.LastUsage.InputTokens, snapshot.LastUsage.CachedInputTokens,
            snapshot.LastUsage.CacheReadInputTokens, snapshot.LastUsage.CacheWriteInputTokens,
            snapshot.LastUsage.OutputTokens, snapshot.LastUsage.ReasoningOutputTokens,
            snapshot.LastUsage.TotalTokens, snapshot.ContextWindow);
    }
}
