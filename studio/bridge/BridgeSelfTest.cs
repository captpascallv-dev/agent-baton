using System.Text.Json;
using CliWatch.App;
using CliWatch.Contracts;

namespace AiMoeStudio.Bridge;

// Controlled in-memory views exercise the export contract; no production files/processes.
public static class BridgeSelfTest
{
    public static async Task RunAsync()
    {
        var at = DateTimeOffset.Parse("2026-10-22T01:00:00Z");
        var snapshot = new CockpitSnapshot(at, new[]
        {
            Project("running", "running_active", true, Work("run", "alive", "active", "none", "unknown")),
            Project("waiting", "waiting", true, Work("wait", "alive", "waiting", "none", "unknown")),
            Project("delivered", "delivered", false, Work("done", "dead", "ended", "delivered", "unknown")),
            Project("unknown", "unknown", true, Work("unknown", "unknown", "unknown", "unknown", "unknown")),
            Project("paused", "running_quiet", true, Work("paused", "alive", "quiet", "none", "unknown")) with { IsPaused = true },
            Project("mixed", "running_active", true, Work("current", "alive", "active", "none", "unknown")) with
            {
                WorkItems = new[] { Work("current", "alive", "active", "none", "unknown"), Work("old", "dead", "ended", "delivered", "unknown") with { Quality = DataQuality.LastKnown } }
            }
        }, new[]
        {
            new SourceIssue("fixture", "optional_missing", "scope=historical; fixture", at),
            new SourceIssue("fixture", "historical_source_missing", "fixture", at),
            new SourceIssue("fixture", "scan_budget_exhausted", "scope=current; fixture", at)
        }, Array.Empty<RouteCoverage>(), DataQuality.Conflicting);
        var state = new PublishedState();
        Equal(state.Export().Source.Status, "starting");
        state.Success(snapshot);
        var first = state.Export();
        Equal(first.Source.Quality, "Conflicting");
        Equal(first.Projects[0].Quality, "Fresh");
        Equal(first.Projects[0].Phase, "running_active");
        Equal(first.Projects[1].Works[0].Axes.Turn, "waiting");
        Equal(first.Projects[2].IsCurrent, false);
        Equal(first.Projects[2].Works[0].Axes.Acceptance, "unknown");
        Equal(first.Projects[3].Works[0].Axes.Process, "unknown");
        Equal(first.Projects[4].IsCurrent, false);
        Equal(first.Projects[5].Works[1].IsHistorical, true);
        Equal(first.Issues.Count(issue => issue.Scope == "history"), 2);
        var json = JsonSerializer.Serialize(first, AtomicSnapshot.JsonOptions);
        Require(!json.Contains("DO_NOT_EXPORT") && !json.Contains("C:\\\\private") && !json.Contains("rawDocument"), "no private evidence or targets");
        Require(json.Contains("\"leadHandling\"") && json.Contains("\"isHistorical\""), "camelCase fields");
        state.Failure("fixture failure");
        var failed = state.Export();
        Equal(failed.Source.Status, "degraded");
        Equal(failed.Source.Stale, true);
        Equal(failed.Source.CollectedAt, first.Source.CollectedAt);
        Equal(failed.Source.PublishedAt, first.Source.PublishedAt);
        Equal(failed.Projects.Count, first.Projects.Count);
        var neverConnected = new PublishedState();
        neverConnected.Failure("fixture failure");
        Equal(neverConnected.Export().Source.Status, "offline");
        Equal(neverConnected.Export().Source.CollectedAt, null);

        // Use the installed RefreshService to confirm previous-frame reuse and failure retention.
        var collector = new ControlledCollector(at);
        var projector = new ControlledProjector(snapshot);
        using var refresh = new RefreshService(collector, new ControlledNormalizer(), projector);
        var settings = new CollectorSettings(Array.Empty<string>(), Array.Empty<string>());
        Require((await refresh.RefreshAsync(settings, CancellationToken.None)).Succeeded, "first refresh");
        Require((await refresh.RefreshAsync(settings, CancellationToken.None)).Succeeded, "second refresh");
        Require(projector.SawPrevious, "previous frame retained");
        collector.Fail = true;
        var failedRefresh = await refresh.RefreshAsync(settings, CancellationToken.None);
        Require(!failedRefresh.Succeeded && failedRefresh.KeptLastKnown, "failure retains real snapshot");
        Equal(failedRefresh.Snapshot?.CollectedAt, at);

        var privateTemp = Path.Combine(Path.GetTempPath(), "ai-moe-studio-config-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            // A bridge restart retains the last published display frame, never freshens it.
            await AtomicSnapshot.WriteAsync(privateTemp, first);
            var restarted = new PublishedState();
            restarted.RestoreLastPublished(privateTemp);
            restarted.Failure("fixture failure");
            Equal(restarted.Export().Source.CollectedAt, first.Source.CollectedAt);
            Equal(restarted.Export().Source.PublishedAt, first.Source.PublishedAt);
            Equal(restarted.Export().Source.Status, "degraded");
            foreach (var invalid in new[] { "{broken", "{}", "[]", "{\"mailbox_roots\":\"bad\"}", "{\"mailbox_roots\":[\"fixture\"],\"read_only\":false}", "{\"mailbox_roots\":[\"fixture\"],\"read_only\":\"true\"}" })
            {
                await File.WriteAllTextAsync(privateTemp, invalid);
                try { BridgeConfig.Load(privateTemp); throw new InvalidOperationException("invalid config accepted"); }
                catch (BridgeConfigException) { }
            }
            await File.WriteAllTextAsync(privateTemp, "{\"mailbox_roots\":[\"fixture\"],\"claude_mailbox_roots\":[\"fixture-claude\"],\"read_only\":true}");
            var loaded = BridgeConfig.Load(privateTemp);
            Require(loaded.ReadOnly && loaded.MailboxRoots.Single() == "fixture" && loaded.ClaudeMailboxRoots.Single() == "fixture-claude", "valid read-only config");
        }
        finally { if (File.Exists(privateTemp)) File.Delete(privateTemp); }
        using var parentGone = new CancellationTokenSource();
        using var parent = ParentLifetime.Attach(int.MaxValue, parentGone);
        Require(parentGone.IsCancellationRequested, "owned bridge exits when its server parent is absent");
        Console.WriteLine("bridge_self_test_ok: running, waiting, delivery-without-acceptance, unknown, current/history, private-fields, previous-frame, failed-refresh, invalid-config");
    }

    static ProjectView Project(string id, string phase, bool active, WorkView work) =>
        new(id, "fixture / " + id, "fixture", phase, "fixture", null, active, false, null,
            new[] { work }, Array.Empty<AttentionItem>(), Array.Empty<NavigationTarget>(), DataQuality.Fresh, null);
    static WorkView Work(string id, string process, string turn, string delivery, string acceptance) =>
        new(id, "executor", "watch:fixture", "fixture", new WorkAxes(process, turn, "unknown", "unknown",
            delivery, "unknown", "unknown", acceptance, "unknown", "unknown"),
            new[] { new ArtifactView("fixture", "交付 C:\\private\\result.txt", "present",
                new NavigationTarget("file", "DO_NOT_EXPORT", "fixture", "fixture"),
                new[] { new EvidenceRef("fixture", "DO_NOT_EXPORT", "rawDocument", DateTimeOffset.UtcNow, null, DataQuality.Fresh) }) },
            Array.Empty<NavigationTarget>(), Array.Empty<EvidenceRef>(), DataQuality.Fresh, "CLI", "fixture");
    static void Equal<T>(T actual, T expected) => Require(EqualityComparer<T>.Default.Equals(actual, expected), "unexpected contract value");
    static void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
    sealed class ControlledCollector(DateTimeOffset at) : ICollector
    {
        public bool Fail { get; set; }
        public Task<CollectionBatch> CollectAsync(CollectorSettings _, CancellationToken __) =>
            Fail ? throw new IOException("DO_NOT_EXPORT") : Task.FromResult(new CollectionBatch(at, Array.Empty<RawDocument>(), Array.Empty<SourceIssue>()));
    }
    sealed class ControlledNormalizer : INormalizer
    {
        public FactBatch Normalize(CollectionBatch batch) => new(batch.CollectedAt, Array.Empty<SourceFact>(), batch.Issues);
    }
    sealed class ControlledProjector(CockpitSnapshot snapshot) : IProjector
    {
        public bool SawPrevious { get; private set; }
        public CockpitSnapshot Project(FactBatch _, CockpitSnapshot? previous = null) { SawPrevious |= previous is not null; return snapshot; }
    }
}
