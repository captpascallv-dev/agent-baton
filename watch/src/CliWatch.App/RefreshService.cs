// SPDX-License-Identifier: MPL-2.0
using CliWatch.Contracts;

namespace CliWatch.App;

public sealed class RefreshOutcome
{
    public bool Succeeded { get; init; }
    public bool Canceled { get; init; }
    public bool KeptLastKnown { get; init; }
    public CockpitSnapshot? Snapshot { get; init; }
    public CockpitSnapshot? LastSuccessful { get; init; }
    public SourceIssue? Issue { get; init; }

    public static RefreshOutcome Ok(CockpitSnapshot snapshot) => new()
    {
        Succeeded = true,
        Snapshot = snapshot,
        LastSuccessful = snapshot
    };

    public static RefreshOutcome Cancel(CockpitSnapshot? last) => new()
    {
        Canceled = true,
        Snapshot = last,
        LastSuccessful = last,
        KeptLastKnown = last is not null
    };

    public static RefreshOutcome Fail(CockpitSnapshot? displayed, CockpitSnapshot? last, SourceIssue issue) => new()
    {
        Succeeded = false,
        Snapshot = displayed,
        LastSuccessful = last,
        KeptLastKnown = last is not null,
        Issue = issue
    };
}

/// <summary>
/// Collect → Normalize → Project off the UI thread. Never re-judges PASS.
/// Failure keeps last-known snapshot and surfaces a SourceIssue.
/// </summary>
public sealed class RefreshService : IDisposable
{
    readonly ICollector _collector;
    readonly INormalizer _normalizer;
    readonly IProjector _projector;
    readonly object _gate = new();
    bool _disposed;

    public RefreshService(ICollector collector, INormalizer normalizer, IProjector projector)
    {
        _collector = collector ?? throw new ArgumentNullException(nameof(collector));
        _normalizer = normalizer ?? throw new ArgumentNullException(nameof(normalizer));
        _projector = projector ?? throw new ArgumentNullException(nameof(projector));
    }

    public CockpitSnapshot? LastSuccessfulSnapshot { get; private set; }

    public async Task<RefreshOutcome> RefreshAsync(CollectorSettings settings, CancellationToken cancellationToken)
    {
        if (_disposed)
            return RefreshOutcome.Cancel(LastSuccessfulSnapshot);

        try
        {
            if (cancellationToken.IsCancellationRequested)
                return RefreshOutcome.Cancel(LastSuccessfulSnapshot);

            CollectionBatch batch;
            try
            {
                batch = await _collector.CollectAsync(settings, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return RefreshOutcome.Cancel(LastSuccessfulSnapshot);
            }
            catch (ObjectDisposedException)
            {
                return RefreshOutcome.Cancel(LastSuccessfulSnapshot);
            }
            catch (Exception ex)
            {
                return Fail(ex);
            }

            if (_disposed || cancellationToken.IsCancellationRequested)
                return RefreshOutcome.Cancel(LastSuccessfulSnapshot);

            FactBatch facts;
            try
            {
                facts = _normalizer.Normalize(batch);
            }
            catch (OperationCanceledException)
            {
                return RefreshOutcome.Cancel(LastSuccessfulSnapshot);
            }
            catch (Exception ex)
            {
                return Fail(ex);
            }

            if (_disposed || cancellationToken.IsCancellationRequested)
                return RefreshOutcome.Cancel(LastSuccessfulSnapshot);

            try
            {
                var snapshot = _projector.Project(facts, LastSuccessfulSnapshot);
                lock (_gate)
                {
                    LastSuccessfulSnapshot = snapshot;
                }
                return RefreshOutcome.Ok(snapshot);
            }
            catch (OperationCanceledException)
            {
                return RefreshOutcome.Cancel(LastSuccessfulSnapshot);
            }
            catch (Exception ex)
            {
                return Fail(ex);
            }
        }
        catch (OperationCanceledException)
        {
            return RefreshOutcome.Cancel(LastSuccessfulSnapshot);
        }
        catch (ObjectDisposedException)
        {
            return RefreshOutcome.Cancel(LastSuccessfulSnapshot);
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    RefreshOutcome Fail(Exception ex)
    {
        var issue = new SourceIssue(
            "refresh",
            "refresh_failed",
            ex.GetType().Name + ": " + ex.Message,
            DateTimeOffset.UtcNow);

        var last = LastSuccessfulSnapshot;
        if (last is null)
        {
            var empty = new CockpitSnapshot(
                DateTimeOffset.UtcNow,
                Array.Empty<ProjectView>(),
                new[] { issue },
                Array.Empty<RouteCoverage>(),
                DataQuality.Unavailable);
            return RefreshOutcome.Fail(empty, null, issue);
        }

        var displayed = MarkLastKnown(last, issue);
        return RefreshOutcome.Fail(displayed, last, issue);
    }

    public static CockpitSnapshot MarkLastKnown(CockpitSnapshot previous, SourceIssue issue)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(issue);
        var issues = new List<SourceIssue>(previous.Issues.Count + 1);
        issues.AddRange(previous.Issues);
        issues.Add(issue);
        var quality = previous.Quality == DataQuality.Unavailable
            ? DataQuality.Unavailable
            : DataQuality.LastKnown;
        return previous with
        {
            Issues = issues,
            Quality = quality
        };
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
