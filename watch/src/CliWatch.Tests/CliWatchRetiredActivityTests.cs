// SPDX-License-Identifier: MPL-2.0
using CliWatch.Collection;
using CliWatch.Contracts;
using CliWatch.Normalization;
using CliWatch.Projection;
using Xunit;

namespace CliWatch.Tests;

public sealed class CliWatchRetiredActivityTests
{
    [Fact]
    public async Task Explicit_retired_registration_goes_to_history()
    {
        using var dir = new TempDir();
        WriteJob(dir.Path, "alpha", "old", "1", "2026-09-20T20:53:00Z", state: "retired", extra:
            ",\"retired_at\":\"2026-09-20T20:56:20Z\",\"retirement_reason\":\"replaced by attempt 2\"");
        var projected = await ProjectAsync(dir.Path);
        var job = Assert.Single(projected.Projects, p => p.Id == "old--1");
        Assert.Equal(CliWatchStatus.Retired, job.Phase);
        Assert.False(job.IsActive);
        Assert.Contains("已退役", job.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("已登记/启动未证实", job.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("已交付", job.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("失败", job.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retired_attempt_does_not_hide_later_live_round()
    {
        using var dir = new TempDir();
        WriteJob(dir.Path, "alpha", "job", "1", "2026-09-20T20:56:30Z", state: "retired", extra:
            ",\"retired_at\":\"2026-09-20T20:56:30Z\",\"retirement_reason\":\"replaced by attempt 2\"");
        var adapter = Path.Combine(dir.Path, "adapter", "jobs", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        Directory.CreateDirectory(adapter);
        WriteJob(dir.Path, "alpha", "job", "2", "2026-09-20T20:56:20Z", extra:
            ",\"adapter_job_id\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"adapter_state_root\":"
            + Json(Path.Combine(dir.Path, "adapter")));
        File.WriteAllText(Path.Combine(adapter, "owner.json"),
            "{\"pid\":1001,\"start_time_utc_ticks\":111,\"started_at_utc\":\"2026-09-20T20:56:21Z\"}");
        File.WriteAllText(Path.Combine(adapter, "request.json"),
            "{\"job_id\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"model\":\"grok-4.6\"}");
        var wrapper = Proc(1001, 1, 111, "pwsh.exe", "pwsh", 10, 10, 10);
        var grok = Proc(2002, 1001, 222, "grok.exe", "grok", 20, 20, 20);
        var projected = await ProjectAsync(dir.Path, new FakeSnapshot(new[] { wrapper, grok }));
        var retired = Assert.Single(projected.Projects, p => p.Id == "job--1");
        var live = Assert.Single(projected.Projects, p => p.Id == "job--2");
        Assert.Equal(CliWatchStatus.Retired, retired.Phase);
        Assert.False(retired.IsActive);
        Assert.True(live.IsActive);
        Assert.True(CliWatchStatus.IsRunning(live.Phase) || live.Phase == CliWatchStatus.StartUnconfirmed);
    }

    [Fact]
    public async Task Retired_record_with_live_process_stays_visible()
    {
        using var dir = new TempDir();
        var adapter = Path.Combine(dir.Path, "adapter", "jobs", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        Directory.CreateDirectory(adapter);
        WriteJob(dir.Path, "alpha", "job", "1", "2026-09-20T20:53:00Z", state: "retired", extra:
            ",\"adapter_job_id\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"adapter_state_root\":"
            + Json(Path.Combine(dir.Path, "adapter")));
        File.WriteAllText(Path.Combine(adapter, "owner.json"),
            "{\"pid\":1001,\"start_time_utc_ticks\":111,\"started_at_utc\":\"2026-09-20T20:53:01Z\"}");
        File.WriteAllText(Path.Combine(adapter, "request.json"),
            "{\"job_id\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"model\":\"grok-4.6\"}");
        var wrapper = Proc(1001, 1, 111, "pwsh.exe", "pwsh", 10, 10, 10);
        var grok = Proc(2002, 1001, 222, "grok.exe", "grok", 20, 20, 20);
        var projected = await ProjectAsync(dir.Path, new FakeSnapshot(new[] { wrapper, grok }));
        var job = Assert.Single(projected.Projects, p => p.Id == "job--1");
        Assert.True(job.IsActive);
        Assert.NotEqual(CliWatchStatus.Retired, job.Phase);
        Assert.True(CliWatchStatus.IsRunning(job.Phase) || job.Phase == CliWatchStatus.StartUnconfirmed);
        Assert.Contains("退役", string.Join(" ", job.WorkItems.Select(w => w.Blocker)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_roots_do_not_zero_counter_activity_either_order()
    {
        using var liveRoot = new TempDir();
        using var emptyRoot = new TempDir();
        Directory.CreateDirectory(Path.Combine(emptyRoot.Path, "jobs"));
        SetupCursorJob(liveRoot.Path);
        var wrapper = Proc(1001, 1, 111, "pwsh.exe", "pwsh", 10, 10, 10);
        var cursor = Proc(2002, 1001, 222, "cursor-agent.exe", "cursor-agent --cwd C:\\work", 20, 20, 20);

        async Task<string> KindAfterIncrement(params string[] roots)
        {
            var store = new ProcessActivityStore();
            var snap = new FakeSnapshot(new[] { wrapper, cursor });
            var collector = new MailboxCollector(snap, activity: store);
            await collector.CollectAsync(Settings(roots), CancellationToken.None);
            snap.Live = new[]
            {
                wrapper,
                Proc(2002, 1001, 222, "cursor-agent.exe", "cursor-agent --cwd C:\\work", 80, 5000, 20)
            };
            var batch = await collector.CollectAsync(Settings(roots), CancellationToken.None);
            var bind = Assert.Single(batch.Documents, d => d.Kind == MailboxCollector.KindProcessBind);
            return bind.Data["activity_kind"]?.GetValue<string>() ?? "";
        }

        var forward = await KindAfterIncrement(liveRoot.Path, emptyRoot.Path);
        var reverse = await KindAfterIncrement(emptyRoot.Path, liveRoot.Path);
        Assert.Equal(forward, reverse);
        Assert.True(forward is "cli_compute" or "child_io", forward);
    }

    [Fact]
    public async Task Quiet_refresh_keeps_artifact_time_not_dispatch()
    {
        using var dir = new TempDir();
        var outbox = Path.Combine(dir.Path, "outbox");
        Directory.CreateDirectory(outbox);
        var adapter = Path.Combine(dir.Path, "adapter", "jobs", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        Directory.CreateDirectory(adapter);
        WriteJob(dir.Path, "alpha", "live", "1", "2026-09-20T20:50:00Z", extra:
            ",\"outbox\":" + Json(outbox)
            + ",\"adapter_job_id\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"adapter_state_root\":"
            + Json(Path.Combine(dir.Path, "adapter")));
        File.WriteAllText(Path.Combine(adapter, "owner.json"),
            "{\"pid\":1001,\"start_time_utc_ticks\":111,\"started_at_utc\":\"2026-09-20T20:58:00Z\"}");
        File.WriteAllText(Path.Combine(adapter, "request.json"),
            "{\"job_id\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"model\":\"grok-4.6\"}");
        File.WriteAllText(Path.Combine(outbox, "DISPATCH.txt"), "dispatch");
        File.WriteAllText(Path.Combine(outbox, "STARTED.json"), "{\"status\":\"running\"}");
        File.WriteAllText(Path.Combine(outbox, "check.log"), "log");
        var dispatchTime = DateTime.UtcNow.AddHours(-2);
        var startedTime = DateTime.UtcNow.AddMinutes(-3);
        var logTime = DateTime.UtcNow.AddMinutes(-1);
        File.SetLastWriteTimeUtc(Path.Combine(outbox, "DISPATCH.txt"), dispatchTime);
        File.SetLastWriteTimeUtc(Path.Combine(outbox, "STARTED.json"), startedTime);
        File.SetLastWriteTimeUtc(Path.Combine(outbox, "check.log"), logTime);

        var wrapper = Proc(1001, 1, 111, "pwsh.exe", "pwsh", 10, 10, 10);
        var grok = Proc(2002, 1001, 222, "grok.exe", "grok", 20, 20, 20);
        var store = new ProcessActivityStore();
        var snap = new FakeSnapshot(new[] { wrapper, grok });
        var collector = new MailboxCollector(snap, activity: store);
        await collector.CollectAsync(Settings(dir.Path), CancellationToken.None);
        var batch2 = await collector.CollectAsync(Settings(dir.Path), CancellationToken.None);
        Assert.DoesNotContain(batch2.Documents, d =>
            d.Kind == "outbox_file" && string.Equals(d.Data["name"]?.GetValue<string>(), "DISPATCH.txt", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(batch2.Documents, d =>
            d.Kind == "outbox_file" && string.Equals(d.Data["name"]?.GetValue<string>(), "STARTED.json", StringComparison.OrdinalIgnoreCase));
        var projected = new CliWatchProjector().Project(new IdentityNormalizer().Normalize(batch2));
        var job = Assert.Single(projected.Projects, p => p.Id == "live--1");
        Assert.DoesNotContain("小时", CliWatchStatus.FormatAgo(job.LastFactAt, DateTimeOffset.UtcNow, false), StringComparison.Ordinal);
        Assert.DoesNotContain("最后活动 2 小时", job.Summary, StringComparison.Ordinal);
        Assert.Contains("最后活动", job.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("最后活动未知", job.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_counters_are_not_fake_activity()
    {
        using var dir = new TempDir();
        var adapter = Path.Combine(dir.Path, "adapter", "jobs", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        Directory.CreateDirectory(adapter);
        WriteJob(dir.Path, "alpha", "live", "1", "2026-09-20T20:50:00Z", extra:
            ",\"adapter_job_id\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"adapter_state_root\":"
            + Json(Path.Combine(dir.Path, "adapter")));
        File.WriteAllText(Path.Combine(adapter, "owner.json"),
            "{\"pid\":1001,\"start_time_utc_ticks\":111,\"started_at_utc\":\"2026-09-20T20:50:01Z\"}");
        File.WriteAllText(Path.Combine(adapter, "request.json"),
            "{\"job_id\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"model\":\"grok-4.6\"}");
        var wrapper = new ObservedProcess(1001, 1, 111, DateTimeOffset.Parse("2026-09-20T20:50:01Z"), "pwsh.exe", null, "pwsh", null, null, null, false);
        var grok = new ObservedProcess(2002, 1001, 222, DateTimeOffset.Parse("2026-09-20T20:50:02Z"), "grok.exe", null, "grok", null, null, null, false);
        var projected = await ProjectAsync(dir.Path, new FakeSnapshot(new[] { wrapper, grok }));
        var job = Assert.Single(projected.Projects, p => p.Id == "live--1");
        Assert.NotEqual(CliWatchStatus.RunningActive, job.Phase);
        Assert.DoesNotContain("运行中有活动", job.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("子程序有计算/读写活动", job.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("CLI 进程有计算/读写活动", job.Summary, StringComparison.Ordinal);
    }

    static void SetupCursorJob(string root)
    {
        var adapter = Path.Combine(root, "adapter", "jobs", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        Directory.CreateDirectory(adapter);
        WriteJob(root, "alpha", "cursor-job", "1", "2026-09-20T20:50:00Z", extra:
            ",\"adapter_job_id\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"adapter_state_root\":"
            + Json(Path.Combine(root, "adapter")));
        File.WriteAllText(Path.Combine(adapter, "owner.json"),
            "{\"pid\":1001,\"start_time_utc_ticks\":111,\"started_at_utc\":\"2026-09-20T20:50:01Z\"}");
        File.WriteAllText(Path.Combine(adapter, "request.json"),
            "{\"job_id\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"model\":\"cursor-grok-4.6-xhigh\"}");
    }

    static Task<CockpitSnapshot> ProjectAsync(string root, IProcessSnapshot? snapshot = null) =>
        ProjectAsync(new[] { root }, snapshot);

    static async Task<CockpitSnapshot> ProjectAsync(IReadOnlyList<string> roots, IProcessSnapshot? snapshot = null)
    {
        var collector = new MailboxCollector(snapshot ?? new FakeSnapshot(Array.Empty<ObservedProcess>()));
        var batch = await collector.CollectAsync(Settings(roots.ToArray()), CancellationToken.None);
        return new CliWatchProjector().Project(new IdentityNormalizer().Normalize(batch));
    }

    static CollectorSettings Settings(params string[] roots) =>
        new(Array.Empty<string>(), Array.Empty<string>())
        {
            MailboxRoots = roots,
            QuietSeconds = 120,
            HistoryHours = 72
        };

    static void WriteJob(string root, string project, string jobId, string attempt, string? registeredAt, string extra = "", string state = "registered")
    {
        var key = jobId + "--" + attempt;
        var jobs = Path.Combine(root, "jobs");
        Directory.CreateDirectory(jobs);
        var registered = string.IsNullOrWhiteSpace(registeredAt)
            ? ""
            : ",\"registered_at\":\"" + registeredAt + "\"";
        var outboxJson = extra.Contains("\"outbox\"", StringComparison.Ordinal)
            ? ""
            : ",\"outbox\":" + Json(Path.Combine(root, "missing-" + key));
        File.WriteAllText(Path.Combine(jobs, key + ".json"),
            "{\"project\":\"" + project + "\",\"job_id\":\"" + jobId + "\",\"attempt_id\":\"" + attempt
            + "\",\"harness\":\"codex-cli\",\"key\":\"" + key + "\",\"state\":\"" + state + "\"" + registered
            + outboxJson + extra + "}");
    }

    static ObservedProcess Proc(int pid, int parent, long ticks, string image, string cmd, long cpu, long read, long write) =>
        new(pid, parent, ticks, DateTimeOffset.Parse("2026-09-20T00:00:00Z"), image, null, cmd, cpu, read, write, true);

    static string Json(string path) => "\"" + path.Replace("\\", "\\\\") + "\"";

    sealed class FakeSnapshot : IProcessSnapshot
    {
        public IReadOnlyList<ObservedProcess> Live { get; set; }
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
