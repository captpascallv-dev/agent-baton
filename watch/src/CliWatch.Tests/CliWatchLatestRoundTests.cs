// SPDX-License-Identifier: MPL-2.0
using CliWatch.Collection;
using CliWatch.Contracts;
using CliWatch.Normalization;
using CliWatch.Projection;
using Xunit;

namespace CliWatch.Tests;

public sealed class CliWatchLatestRoundTests
{
    // Registration fixtures below belong to this date. Wall-clock time must not
    // turn identity/latest-round tests into accidental expiry tests.
    static readonly DateTimeOffset FixtureNow = DateTimeOffset.Parse("2026-09-20T23:59:00Z");
    [Theory]
    [InlineData("cursor-execution-guard", null)]
    [InlineData("cursor-execution-guard", "failed")]
    [InlineData("local-guard", null)]
    [InlineData("local-guard", "failed")]
    [InlineData("cursor-exit-guard", null)]
    [InlineData("cursor-exit-guard", "failed")]
    public async Task Notification_mailbox_is_not_an_execution_or_a_new_project_round(string harness, string? notificationStatus)
    {
        using var dir = new TempDir();
        WriteSnapshotReady(dir.Path, "alpha", "real-delivery", "1", "2026-09-20T12:00:00Z");
        WriteJob(dir.Path, "beta", "pending-execution", "1", "2026-09-20T12:00:00Z");
        if (notificationStatus is null)
            WriteJob(dir.Path, "alpha", "guard-notice", "1", "2026-09-20T13:00:00Z");
        else
            WriteSnapshotReady(dir.Path, "alpha", "guard-notice", "1", "2026-09-20T13:00:00Z", readyStatus: notificationStatus);
        var file = Path.Combine(dir.Path, "jobs", "guard-notice--1.json");
        var job = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        job["harness"] = harness;
        File.WriteAllText(file, job.ToJsonString());
        var before = File.ReadAllText(file);

        var batch = await new MailboxCollector(new FakeSnapshot(Array.Empty<ObservedProcess>()))
            .CollectAsync(new CollectorSettings(Array.Empty<string>(), Array.Empty<string>())
            { MailboxRoots = new[] { dir.Path } }, CancellationToken.None);
        Assert.DoesNotContain(batch.Documents, d => d.Kind == MailboxCollector.KindProcessBind
            && d.Data["job_key"]?.ToString() == "guard-notice--1");
        var projected = new CliWatchProjector().Project(new IdentityNormalizer().Normalize(batch) with { CollectedAt = FixtureNow });
        Assert.DoesNotContain(projected.Projects, p => p.Id == "guard-notice--1");
        var delivery = projected.Projects.Single(p => p.Id == "real-delivery--1");
        Assert.False(delivery.IsActive);
        Assert.Equal(CliWatchStatus.Delivered, delivery.Phase);
        var pending = projected.Projects.Single(p => p.Id == "pending-execution--1");
        Assert.False(pending.IsActive);
        Assert.Equal(CliWatchStatus.Registered, pending.Phase);
        Assert.Equal(before, File.ReadAllText(file));
    }

    [Fact]
    public async Task Ended_delivery_stays_in_history_with_its_delivery_status()
    {
        using var dir = new TempDir();
        WriteSnapshotReady(dir.Path, "alpha", "only", "1", "2026-09-20T12:00:00Z", sentAt: "2026-09-20T12:30:00Z");
        var projected = await ProjectAsync(dir.Path);
        var job = Assert.Single(projected.Projects, p => p.Id == "only--1");
        Assert.Equal(CliWatchStatus.Delivered, job.Phase);
        Assert.False(job.IsActive);
        Assert.Contains("已交付待 Lead", job.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("运行 ", job.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("项目完成", job.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task All_ended_deliveries_keep_their_status_in_history()
    {
        using var dir = new TempDir();
        WriteSnapshotReady(dir.Path, "alpha", "old-a", "1", "2026-09-20T10:00:00Z", sentAt: "2026-09-20T10:20:00Z");
        WriteSnapshotReady(dir.Path, "alpha", "old-b", "1", "2026-09-20T11:00:00Z", sentAt: "2026-09-20T11:20:00Z");
        WriteSnapshotReady(dir.Path, "alpha", "old-c", "1", "2026-09-20T12:00:00Z", sentAt: "2026-09-20T12:20:00Z");
        WriteSnapshotReady(dir.Path, "alpha", "new-round", "1", "2026-09-20T13:00:00Z", sentAt: "2026-09-20T13:20:00Z");

        var projected = await ProjectAsync(dir.Path);
        var current = projected.Projects.Where(p => p.IsActive).Select(p => p.Id).ToList();
        var history = projected.Projects.Where(p => !p.IsActive).Select(p => p.Id).ToList();
        Assert.Empty(current);
        Assert.Contains("new-round--1", history);
        Assert.Contains("old-a--1", history);
        Assert.Contains("old-b--1", history);
        Assert.Contains("old-c--1", history);
        Assert.Equal(4, history.Count);
        Assert.All(history, id => Assert.Equal(CliWatchStatus.Delivered, projected.Projects.Single(p => p.Id == id).Phase));
        Assert.Equal(CliWatchStatus.Delivered, projected.Projects.Single(p => p.Id == "new-round--1").Phase);
    }

    [Fact]
    public async Task Late_old_report_does_not_steal_latest_round()
    {
        using var dir = new TempDir();
        WriteSnapshotReady(dir.Path, "alpha", "old-round", "1", "2026-09-20T10:00:00Z", sentAt: "2026-09-20T23:50:00Z");
        WriteJob(dir.Path, "alpha", "new-round", "1", "2026-09-20T12:00:00Z");

        var projected = await ProjectAsync(dir.Path);
        var old = Assert.Single(projected.Projects, p => p.Id == "old-round--1");
        var newer = Assert.Single(projected.Projects, p => p.Id == "new-round--1");
        Assert.Equal(CliWatchStatus.Delivered, old.Phase);
        Assert.False(old.IsActive);
        Assert.Equal(CliWatchStatus.Registered, newer.Phase);
        Assert.False(newer.IsActive);
    }

    [Fact]
    public async Task Parallel_live_same_project_is_not_dropped_by_newer_registration()
    {
        using var dir = new TempDir();
        var adapter = Path.Combine(dir.Path, "adapter", "jobs", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        Directory.CreateDirectory(adapter);
        WriteJob(dir.Path, "alpha", "live-old", "1", "2026-09-20T10:00:00Z", extra:
            ",\"adapter_job_id\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"adapter_state_root\":"
            + Json(Path.Combine(dir.Path, "adapter")));
        File.WriteAllText(Path.Combine(adapter, "owner.json"),
            "{\"pid\":1001,\"start_time_utc_ticks\":111,\"started_at_utc\":\"2026-09-20T10:00:01Z\"}");
        File.WriteAllText(Path.Combine(adapter, "request.json"),
            "{\"job_id\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"model\":\"grok-4.6\"}");
        WriteSnapshotReady(dir.Path, "alpha", "newer-reg", "1", "2026-09-20T12:00:00Z", sentAt: "2026-09-20T12:10:00Z");

        var wrapper = Proc(1001, 1, 111, "pwsh.exe", "pwsh", 10, 10, 10);
        var grok = Proc(2002, 1001, 222, "grok.exe", "grok", 20, 20, 20);
        var projected = await ProjectAsync(dir.Path, new FakeSnapshot(new[] { wrapper, grok }));
        var live = Assert.Single(projected.Projects, p => p.Id == "live-old--1");
        var newer = Assert.Single(projected.Projects, p => p.Id == "newer-reg--1");
        Assert.True(CliWatchStatus.IsRunning(live.Phase) || live.Phase == CliWatchStatus.StartUnconfirmed);
        Assert.True(live.IsActive);
        Assert.Equal(CliWatchStatus.Delivered, newer.Phase);
        Assert.False(newer.IsActive);
    }

    [Fact]
    public async Task Failed_and_finished_rows_stay_in_history()
    {
        using var dir = new TempDir();
        WriteSnapshotReady(dir.Path, "alpha", "failed-job", "1", "2026-09-20T12:00:00Z", readyStatus: "failed");
        var failed = await ProjectAsync(dir.Path);
        var failRow = Assert.Single(failed.Projects, p => p.Id == "failed-job--1");
        Assert.Equal(CliWatchStatus.ReportedFailure, failRow.Phase);
        Assert.False(failRow.IsActive);

        using var dir2 = new TempDir();
        var adapter = Path.Combine(dir2.Path, "adapter", "jobs", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        Directory.CreateDirectory(adapter);
        WriteJob(dir2.Path, "alpha", "finished-job", "1", "2026-09-20T12:00:00Z", extra:
            ",\"adapter_job_id\":\"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb\",\"adapter_state_root\":"
            + Json(Path.Combine(dir2.Path, "adapter")));
        File.WriteAllText(Path.Combine(adapter, "owner.json"),
            "{\"pid\":9,\"start_time_utc_ticks\":1,\"started_at_utc\":\"2026-09-20T12:00:01Z\"}");
        File.WriteAllText(Path.Combine(adapter, "receipt.json"),
            "{\"protocol_version\":\"agent-direct-grok-receipt-v1\",\"job_id\":\"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb\",\"transport_complete\":true,\"grok_success\":true,\"completed_at_utc\":\"2026-09-20T12:10:00Z\"}");
        var finished = await ProjectAsync(dir2.Path);
        var row = Assert.Single(finished.Projects, p => p.Id == "finished-job--1");
        Assert.Equal(CliWatchStatus.ExecutorFinished, row.Phase);
        Assert.False(row.IsActive);
        Assert.Contains("未见交付", row.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unbound_row_stays_current_beside_latest_delivery()
    {
        using var dir = new TempDir();
        WriteSnapshotReady(dir.Path, "alpha", "done", "1", "2026-09-20T12:00:00Z");
        var grok = new ObservedProcess(3003, 1, 333, DateTimeOffset.UtcNow, "grok.exe", null, "grok.exe --cwd C:\\other");
        var projected = await ProjectAsync(dir.Path, new FakeSnapshot(new[] { grok }));
        var delivered = Assert.Single(projected.Projects, p => p.Id == "done--1");
        var unbound = Assert.Single(projected.Projects, p => p.Id == "unbound");
        Assert.False(delivered.IsActive);
        Assert.True(unbound.IsActive);
        Assert.Equal("未关联任务", unbound.Name);
    }

    [Fact]
    public async Task Mailbox_roots_isolate_same_project_field()
    {
        using var a = new TempDir();
        using var b = new TempDir();
        WriteSnapshotReady(a.Path, "alpha", "root-a", "1", "2026-09-20T10:00:00Z");
        WriteSnapshotReady(b.Path, "alpha", "root-b", "1", "2026-09-20T11:00:00Z");
        var collector = new MailboxCollector(new FakeSnapshot(Array.Empty<ObservedProcess>()));
        var batch = await collector.CollectAsync(new CollectorSettings(Array.Empty<string>(), Array.Empty<string>())
        {
            MailboxRoots = new[] { a.Path, b.Path },
            QuietSeconds = 120,
            HistoryHours = 72
        }, CancellationToken.None);
        var projected = new CliWatchProjector().Project(new IdentityNormalizer().Normalize(batch) with { CollectedAt = FixtureNow });
        var left = Assert.Single(projected.Projects, p => p.Id == "root-a--1");
        var right = Assert.Single(projected.Projects, p => p.Id == "root-b--1");
        Assert.False(left.IsActive);
        Assert.False(right.IsActive);
        Assert.Equal(CliWatchStatus.Delivered, left.Phase);
        Assert.Equal(CliWatchStatus.Delivered, right.Phase);
    }

    [Fact]
    public async Task Project_field_not_guessed_from_title()
    {
        using var dir = new TempDir();
        WriteSnapshotReady(dir.Path, "alpha", "looks-like-beta", "1", "2026-09-20T10:00:00Z");
        WriteSnapshotReady(dir.Path, "beta", "other", "1", "2026-09-20T09:00:00Z");
        var projected = await ProjectAsync(dir.Path);
        var alpha = Assert.Single(projected.Projects, p => p.Id == "looks-like-beta--1");
        var beta = Assert.Single(projected.Projects, p => p.Id == "other--1");
        Assert.False(alpha.IsActive);
        Assert.False(beta.IsActive);
    }

    [Fact]
    public async Task Same_key_on_two_roots_rejects_foreign_declared_root()
    {
        using var a = new TempDir();
        using var b = new TempDir();
        WriteSnapshotReady(a.Path, "alpha", "shared", "1", "2026-09-20T10:00:00Z", readyStatus: "completed");
        WriteSnapshotReady(b.Path, "alpha", "shared", "1", "2026-09-20T10:00:00Z", readyStatus: "failed");
        File.WriteAllText(Path.Combine(b.Path, "snapshots", "shared--1", "0-READY.json"),
            "{\"job_id\":\"shared\",\"attempt_id\":\"1\",\"status\":\"failed\",\"mailbox_root\":"
            + Json(a.Path) + ",\"report_file\":\"REPORT.md\",\"files\":[]}");

        var projected = await ProjectAsync(new[] { a.Path, b.Path });
        var rows = projected.Projects.Where(p => p.Name.StartsWith("alpha / shared", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, p => p.Phase == CliWatchStatus.Delivered && !p.IsActive);
        Assert.Contains(rows, p => p.Phase == CliWatchStatus.Unknown && !p.IsActive);
        Assert.Contains(projected.Issues, i => i.Code == "context_identity_conflict");
        Assert.Equal(2, rows.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var delivered = Assert.Single(rows, p => p.Phase == CliWatchStatus.Delivered);
        var failed = Assert.Single(rows, p => p.Phase == CliWatchStatus.Unknown);
        Assert.Contains(a.Path, string.Join(" ", delivered.WorkItems.SelectMany(w => w.Evidence.Select(e => e.Location))), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(b.Path, string.Join(" ", failed.WorkItems.SelectMany(w => w.Evidence.Select(e => e.Location))), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(b.Path, string.Join(" ", delivered.WorkItems.SelectMany(w => w.Evidence.Select(e => e.Location))), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(a.Path, string.Join(" ", failed.WorkItems.SelectMany(w => w.Evidence.Select(e => e.Location))), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Live_process_on_one_root_does_not_animate_the_other()
    {
        using var a = new TempDir();
        using var b = new TempDir();
        var adapter = Path.Combine(a.Path, "adapter", "jobs", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        Directory.CreateDirectory(adapter);
        WriteJob(a.Path, "alpha", "shared", "1", "2026-09-20T10:00:00Z", extra:
            ",\"adapter_job_id\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"adapter_state_root\":"
            + Json(Path.Combine(a.Path, "adapter")));
        File.WriteAllText(Path.Combine(adapter, "owner.json"),
            "{\"pid\":1001,\"start_time_utc_ticks\":111,\"started_at_utc\":\"2026-09-20T10:00:01Z\"}");
        File.WriteAllText(Path.Combine(adapter, "request.json"),
            "{\"job_id\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"model\":\"grok-4.6\"}");
        WriteSnapshotReady(b.Path, "alpha", "shared", "1", "2026-09-20T10:00:00Z", readyStatus: "completed");

        var wrapper = Proc(1001, 1, 111, "pwsh.exe", "pwsh", 10, 10, 10);
        var grok = Proc(2002, 1001, 222, "grok.exe", "grok", 20, 20, 20);
        var projected = await ProjectAsync(new[] { a.Path, b.Path }, new FakeSnapshot(new[] { wrapper, grok }));
        var rows = projected.Projects.Where(p => p.Name.StartsWith("alpha / shared", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, rows.Count);
        var live = Assert.Single(rows, p => CliWatchStatus.IsRunning(p.Phase) || p.Phase == CliWatchStatus.StartUnconfirmed);
        var delivered = Assert.Single(rows, p => p.Phase == CliWatchStatus.Delivered);
        Assert.True(live.IsActive);
        Assert.False(delivered.IsActive);
        Assert.False(CliWatchStatus.IsRunning(delivered.Phase));
        Assert.NotEqual(CliWatchStatus.StartUnconfirmed, delivered.Phase);
    }

    [Fact]
    public async Task Missing_times_do_not_make_an_ended_delivery_current()
    {
        using var dir = new TempDir();
        WriteSnapshotReady(dir.Path, "alpha", "job-a", "1", registeredAt: null);
        WriteSnapshotReady(dir.Path, "alpha", "job-b", "1", registeredAt: null);
        var first = await ProjectAsync(dir.Path);
        var second = await ProjectAsync(dir.Path);
        var firstCurrent = first.Projects.Where(p => p.IsActive).Select(p => p.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var secondCurrent = second.Projects.Where(p => p.IsActive).Select(p => p.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Empty(firstCurrent);
        Assert.Equal(firstCurrent, secondCurrent);
        Assert.Equal(2, first.Projects.Count(p => !p.IsActive));
    }

    static Task<CockpitSnapshot> ProjectAsync(string root, IProcessSnapshot? snapshot = null) =>
        ProjectAsync(new[] { root }, snapshot);

    static async Task<CockpitSnapshot> ProjectAsync(IReadOnlyList<string> roots, IProcessSnapshot? snapshot = null)
    {
        var collector = new MailboxCollector(snapshot ?? new FakeSnapshot(Array.Empty<ObservedProcess>()));
        var batch = await collector.CollectAsync(new CollectorSettings(Array.Empty<string>(), Array.Empty<string>())
        {
            MailboxRoots = roots,
            QuietSeconds = 120,
            HistoryHours = 72
        }, CancellationToken.None);
        return new CliWatchProjector().Project(new IdentityNormalizer().Normalize(batch) with { CollectedAt = FixtureNow });
    }

    static void WriteSnapshotReady(
        string root,
        string project,
        string jobId,
        string attempt,
        string? registeredAt,
        string? sentAt = null,
        string readyStatus = "completed")
    {
        var key = jobId + "--" + attempt;
        WriteJob(root, project, jobId, attempt, registeredAt);
        var snap = Path.Combine(root, "snapshots", key);
        Directory.CreateDirectory(Path.Combine(root, "deliveries"));
        Directory.CreateDirectory(snap);
        File.WriteAllText(Path.Combine(root, "deliveries", key + ".json"),
            "{\"id\":\"" + key + "\",\"project\":\"" + project + "\",\"state\":\"sent\",\"sent_at\":\""
            + (sentAt ?? "2026-09-20T23:00:00Z") + "\",\"marker\":\"app-mailbox:" + key + "\"}");
        File.WriteAllText(Path.Combine(snap, "manifest.json"),
            "{\"job\":{\"project\":\"" + project + "\",\"job_id\":\"" + jobId + "\",\"attempt_id\":\"" + attempt
            + "\",\"key\":\"" + key + "\"},\"files\":[],\"marker\":\"app-mailbox:" + key + "\"}");
        File.WriteAllText(Path.Combine(snap, "0-READY.json"),
            "{\"job_id\":\"" + jobId + "\",\"attempt_id\":\"" + attempt + "\",\"status\":\"" + readyStatus
            + "\",\"report_file\":\"REPORT.md\",\"files\":[]}");
    }

    static void WriteJob(string root, string project, string jobId, string attempt, string? registeredAt, string extra = "")
    {
        var key = jobId + "--" + attempt;
        var jobs = Path.Combine(root, "jobs");
        Directory.CreateDirectory(jobs);
        var registered = string.IsNullOrWhiteSpace(registeredAt)
            ? ""
            : ",\"registered_at\":\"" + registeredAt + "\"";
        File.WriteAllText(Path.Combine(jobs, key + ".json"),
            "{\"project\":\"" + project + "\",\"job_id\":\"" + jobId + "\",\"attempt_id\":\"" + attempt
            + "\",\"harness\":\"codex-cli\",\"key\":\"" + key + "\",\"state\":\"registered\"" + registered
            + ",\"outbox\":" + Json(Path.Combine(root, "missing-" + key)) + extra + "}");
    }

    static ObservedProcess Proc(int pid, int parent, long ticks, string image, string cmd, long cpu, long read, long write) =>
        new(pid, parent, ticks, DateTimeOffset.Parse("2026-09-20T00:00:00Z"), image, null, cmd, cpu, read, write, true);

    static string Json(string path) => "\"" + path.Replace("\\", "\\\\") + "\"";

    sealed class FakeSnapshot : IProcessSnapshot
    {
        public IReadOnlyList<ObservedProcess> Live { get; }
        public FakeSnapshot(IReadOnlyList<ObservedProcess> live) => Live = live;
        public IReadOnlyList<ObservedProcess> List(CancellationToken cancellationToken) => Live;
        public ProcessIdentityObservation Observe(int pid, long expectedStartTicks)
        {
            var match = Live.FirstOrDefault(p => p.Pid == pid);
            if (match is null)
                return new ProcessIdentityObservation(pid, expectedStartTicks, "dead", DateTimeOffset.UtcNow);
            var state = match.StartTicks == expectedStartTicks ? "alive" : "dead";
            return new ProcessIdentityObservation(pid, expectedStartTicks, state, DateTimeOffset.UtcNow);
        }
    }

    sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cli-watch-test-" + Guid.NewGuid().ToString("n"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (Exception) { }
        }
    }
}
