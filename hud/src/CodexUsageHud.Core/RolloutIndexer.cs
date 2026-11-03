using Microsoft.Data.Sqlite;
using System.Diagnostics;

namespace CodexUsageHud.Core;

public sealed record ScanResult(
    string RelativePath,
    string ThreadId,
    long PreviousOffset,
    long NewOffset,
    int Generation,
    int AcceptedSamples,
    int DuplicateSamples,
    IReadOnlyList<string> ErrorCodes,
    long BytesRead = 0,
    int TokenWrites = 0,
    int SessionWrites = 0,
    int SourceWrites = 0,
    bool WasSkipped = false,
    bool HasMoreData = false,
    long ElapsedMilliseconds = 0,
    int PeakRetainedBytes = 0,
    long StateRevision = 0);

public sealed class RolloutIndexer
{
    private static readonly string[] ContinuationHealthCodes =
    {
        "more_data", "token_oracle_rescan", "turn_key_rescan", "sample_source_rescan",
        "private_identity_rescan", "file_identity_rescan", "context_window_rescan",
        "context_boundary_rescan",
    };

    private readonly UsageDatabase _database;
    private readonly PrivacyJsonlReader _reader;
    private readonly FileIdentityProvider _identityProvider;
    private readonly Dictionary<string, SourceFileState> _sourceStates;

    public RolloutIndexer(UsageDatabase database, PrivacyJsonlReader? reader = null,
        FileIdentityProvider? identityProvider = null)
    {
        _database = database;
        _reader = reader ?? new PrivacyJsonlReader();
        _identityProvider = identityProvider ?? new FileIdentityProvider();
        _sourceStates = database.LoadSourceStates().ToDictionary(item => item.Identity.StableKey,
            StringComparer.Ordinal);
    }

    public bool IsSourceCurrent(RolloutFile file, string knownThreadId)
    {
        try
        {
            var identity = _identityProvider.Get(file.FullPath, knownThreadId);
            if (!_sourceStates.TryGetValue(identity.StableKey, out var existing) ||
                ContinuationHealthCodes.Contains(existing.HealthCode, StringComparer.Ordinal) ||
                !string.Equals(existing.RelativePath, file.RelativePath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var info = new FileInfo(file.FullPath);
            return info.Length == existing.LastObservedLength && existing.LastWriteTimeUtcTicks > 0 &&
                   info.LastWriteTimeUtc.Ticks == existing.LastWriteTimeUtcTicks;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public ScanResult ScanFile(string path, string relativePath, string knownThreadId = "unknown-thread",
        CancellationToken cancellationToken = default, long maxBytes = PrivacyJsonlReader.DefaultBatchBytes)
    {
        var stopwatch = Stopwatch.StartNew();
        var safeRelative = NormalizeRelative(relativePath);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = ScanAttempt(path, safeRelative, knownThreadId, cancellationToken, maxBytes,
                    stopwatch);
                if (!result.ErrorCodes.Contains("source_revision_conflict", StringComparer.Ordinal)) return result;

                var identity = _identityProvider.Get(Path.GetFullPath(path), knownThreadId);
                var committed = _database.LoadSourceState(identity.StableKey);
                if (committed is null) _sourceStates.Remove(identity.StableKey);
                else _sourceStates[identity.StableKey] = committed;
                if (attempt == 0) continue;

                _database.AddParserError(safeRelative, committed?.Cursor.CompleteOffset ?? 0,
                    "source_revision_conflict", DateTimeOffset.UtcNow);
                return result with { HasMoreData = false };
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
            {
                return new ScanResult(safeRelative, knownThreadId, 0, 0, 0, 0, 0,
                    new[] { "database_busy" }, 0, 0, 0, 0, false, false,
                    stopwatch.ElapsedMilliseconds, 0);
            }
        }

        throw new InvalidOperationException("unreachable_scan_attempt");
    }

    private ScanResult ScanAttempt(string path, string relativePath, string knownThreadId,
        CancellationToken cancellationToken, long maxBytes, Stopwatch stopwatch)
    {
        var fullPath = Path.GetFullPath(path);
        var fileInfo = new FileInfo(fullPath);
        var fileLength = fileInfo.Length;
        var writeTicks = fileInfo.LastWriteTimeUtc.Ticks;
        var identity = _identityProvider.Get(fullPath, knownThreadId);
        _sourceStates.TryGetValue(identity.StableKey, out var existing);
        var generation = existing?.Generation ?? 0;
        var cursor = existing?.Cursor ?? new SourceReadCursor(0);
        var previousOffset = cursor.CompleteOffset;
        var cursorTurnKey = existing?.CursorTurnKey;
        var effectiveOffset = cursor.DrainMode ? cursor.DrainOffset : cursor.CompleteOffset;

        if (existing is not null && fileLength < effectiveOffset)
        {
            generation++;
            cursor = new SourceReadCursor(0);
            cursorTurnKey = null;
        }
        var sameLengthRewrite = existing is not null && existing.LastWriteTimeUtcTicks > 0 &&
                                fileLength == existing.LastObservedLength &&
                                writeTicks != existing.LastWriteTimeUtcTicks;
        if (sameLengthRewrite)
        {
            generation++;
            cursor = new SourceReadCursor(0);
            cursorTurnKey = null;
        }

        var stableLength = existing is not null && fileLength == existing.LastObservedLength;
        var stableWrite = existing is not null &&
                          (existing.LastWriteTimeUtcTicks == 0 || writeTicks == existing.LastWriteTimeUtcTicks);
        var samePath = existing is not null &&
                       string.Equals(existing.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase);
        var requiresContinuation = existing is not null &&
                                   ContinuationHealthCodes.Contains(existing.HealthCode, StringComparer.Ordinal);
        if (stableLength && stableWrite && samePath && !requiresContinuation)
        {
            if (existing!.LastWriteTimeUtcTicks == 0)
            {
                return CommitMetadataOnly(existing with
                {
                    LastWriteTimeUtcTicks = writeTicks,
                    LastObservedLength = fileLength,
                }, existing, relativePath, knownThreadId, previousOffset, stopwatch);
            }
            return new ScanResult(relativePath, knownThreadId, previousOffset, previousOffset, generation,
                0, 0, Array.Empty<string>(), 0, 0, 0, 0, true, false,
                stopwatch.ElapsedMilliseconds, 0, existing.StateRevision);
        }

        if (stableLength && stableWrite && !samePath && !requiresContinuation)
        {
            return CommitMetadataOnly(existing! with
            {
                RelativePath = relativePath,
                LastWriteTimeUtcTicks = writeTicks,
            }, existing, relativePath, knownThreadId, previousOffset, stopwatch);
        }

        var batch = _reader.Read(fullPath, cursor, cancellationToken, maxBytes);
        var errors = batch.ErrorCodes.Distinct(StringComparer.Ordinal).ToList();
        var health = batch.StoppedByBudget && batch.HasMoreData
            ? "more_data"
            : batch.Cursor.DrainMode && !batch.HasMoreData
                ? "unknown_drain_waiting"
                : errors.Count > 0
                    ? errors[0]
                    : batch.HasMoreData && batch.Cursor.CompleteOffset < fileLength
                        ? "partial_tail"
                        : "ok";
        var proposed = new SourceFileState(identity, relativePath, generation, batch.Cursor,
            fileLength, writeTicks, cursorTurnKey, health, (existing?.StateRevision ?? 0) + 1);
        var expectation = new SourceStateExpectation(identity.StableKey, existing is not null,
            existing?.Generation ?? 0, existing?.Cursor ?? new SourceReadCursor(0),
            existing?.StateRevision ?? 0);
        var observedAt = DateTimeOffset.UtcNow;
        var parserErrors = errors.Select(code => new ParserErrorRecord(relativePath,
            batch.Cursor.CompleteOffset, code, observedAt)).ToArray();
        var commit = _database.CommitScan(new ScanTransactionRequest(expectation, proposed,
            batch.Events, parserErrors, observedAt));
        if (!commit.Committed)
        {
            return new ScanResult(relativePath, knownThreadId, previousOffset, previousOffset, generation,
                0, 0, commit.DiagnosticCodes, batch.BytesRead, 0, 0, 0, false,
                commit.DiagnosticCodes.Contains("aggregate_rebuild_pending", StringComparer.Ordinal),
                stopwatch.ElapsedMilliseconds, batch.PeakRetainedBytes, existing?.StateRevision ?? 0);
        }

        _sourceStates[identity.StableKey] = commit.CommittedSource!;
        errors.AddRange(commit.DiagnosticCodes);
        var finalThread = batch.Events.LastOrDefault(item => !string.IsNullOrWhiteSpace(item.ThreadId))?.ThreadId
                          ?? knownThreadId;
        return new ScanResult(relativePath, finalThread, previousOffset,
            commit.CommittedSource!.Cursor.CompleteOffset, generation, commit.AcceptedSamples,
            commit.DuplicateSamples, errors.Distinct(StringComparer.Ordinal).ToArray(), batch.BytesRead,
            commit.TokenWrites, commit.SessionWrites, commit.SourceWrites, false,
            batch.StoppedByBudget && batch.HasMoreData, stopwatch.ElapsedMilliseconds,
            batch.PeakRetainedBytes, commit.CommittedSource.StateRevision);
    }

    private ScanResult CommitMetadataOnly(SourceFileState proposed, SourceFileState existing,
        string relativePath, string knownThreadId, long previousOffset, Stopwatch stopwatch)
    {
        var expectation = new SourceStateExpectation(existing.Identity.StableKey, true,
            existing.Generation, existing.Cursor, existing.StateRevision);
        var commit = _database.CommitScan(new ScanTransactionRequest(expectation, proposed,
            Array.Empty<ParsedEvent>(), Array.Empty<ParserErrorRecord>(), DateTimeOffset.UtcNow));
        if (!commit.Committed)
        {
            return new ScanResult(relativePath, knownThreadId, previousOffset, previousOffset,
                existing.Generation, 0, 0, commit.DiagnosticCodes, 0, 0, 0, 0,
                false, false, stopwatch.ElapsedMilliseconds, 0, existing.StateRevision);
        }
        _sourceStates[existing.Identity.StableKey] = commit.CommittedSource!;
        return new ScanResult(relativePath, knownThreadId, previousOffset, previousOffset,
            existing.Generation, 0, 0, Array.Empty<string>(), 0, 0, 0, commit.SourceWrites,
            true, false, stopwatch.ElapsedMilliseconds, 0, commit.CommittedSource!.StateRevision);
    }

    public IReadOnlyList<ScanResult> ScanDiscovered(string codexHome,
        CancellationToken cancellationToken = default)
    {
        var discovery = new RolloutDiscovery(_identityProvider);
        var results = new List<ScanResult>();
        foreach (var file in discovery.Discover(codexHome))
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(ScanFile(file.FullPath, file.RelativePath, "unknown-thread", cancellationToken));
        }
        return results;
    }

    public bool MergeMetadata(SessionMetadata metadata) => _database.MergeSessionMetadata(metadata);

    public void SetPinnedThread(string? threadId) => _database.SetPinnedThread(threadId);

    public IReadOnlyList<SessionAggregate> LoadAggregates(DateTimeOffset nowUtc) =>
        _database.LoadSessionAggregates(nowUtc);

    private static string NormalizeRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
            throw new ArgumentException("relative_path_required", nameof(path));
        var normalized = path.Replace('\\', '/');
        if (normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(part => part == ".."))
            throw new ArgumentException("relative_path_required", nameof(path));
        return normalized;
    }
}
