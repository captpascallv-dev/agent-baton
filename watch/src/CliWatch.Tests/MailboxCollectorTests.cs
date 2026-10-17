// SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Nodes;
using CliWatch.Collection;
using CliWatch.Contracts;
using CliWatch.Normalization;
using CliWatch.Projection;
using Xunit;

namespace CliWatch.Tests;

public sealed class MailboxCollectorTests
{
    [Fact]
    public async Task Binds_job_to_project_and_not_wrapper_as_cli()
    {
        using var dir = new TempDir();
        var jobs = Path.Combine(dir.Path, "jobs");
        Directory.CreateDirectory(jobs);
        var adapter = Path.Combine(dir.Path, "adapter", "jobs", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        Directory.CreateDirectory(adapter);
        File.WriteAllText(Path.Combine(jobs, "demo-job--1.json"), """
            {"project":"demo","job_id":"demo-job","attempt_id":"1","harness":"grok-cli","key":"demo-job--1","outbox":"X:\\nope","adapter_job_id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","adapter_state_root":"STATE","workspace":"C:\\work\\demo"}
            """.Replace("STATE", adapter.Replace("\\jobs\\aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "").Replace("\\", "\\\\")));
        // rewrite with real paths
        var stateRoot = Path.Combine(dir.Path, "adapter");
        File.WriteAllText(Path.Combine(jobs, "demo-job--1.json"),
            "{" +
            "\"project\":\"demo\",\"job_id\":\"demo-job\",\"attempt_id\":\"1\",\"harness\":\"grok-cli\"," +
            "\"key\":\"demo-job--1\",\"workspace\":\"C:\\\\work\\\\demo\"," +
            "\"adapter_job_id\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\"," +
            "\"adapter_state_root\":" + Json(stateRoot) + "}");
        File.WriteAllText(Path.Combine(adapter, "owner.json"),
            "{\"protocol_version\":\"agent-direct-grok-owner-v1\",\"pid\":1001,\"start_time_utc_ticks\":111,\"started_at_utc\":\"2026-09-20T00:00:00Z\"}");
        File.WriteAllText(Path.Combine(adapter, "request.json"),
            "{\"protocol_version\":\"agent-direct-grok-request-v1\",\"job_id\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"workspace\":\"C:\\\\work\\\\demo\",\"model\":\"grok-4.6\",\"reasoning_effort\":\"xhigh\",\"session_id\":\"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb\",\"resume\":false,\"created_at_utc\":\"2026-09-20T00:00:00Z\"}");

        var wrapper = new ObservedProcess(1001, 1, 111, DateTimeOffset.Parse("2026-09-20T00:00:00Z"), "pwsh.exe", null,
            "pwsh -File process_file_host.ps1");
        var grok = new ObservedProcess(2002, 1001, 222, DateTimeOffset.Parse("2026-09-20T00:00:01Z"), "grok.exe",
            @"C:\Users\x\.grok\bin\grok.exe",
            @"grok.exe --cwd C:\work\demo --resume bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var snap = new FakeSnapshot(new[] { wrapper, grok });
        var collector = new MailboxCollector(snap);
        var batch = await collector.CollectAsync(Settings(dir.Path), CancellationToken.None);
        var facts = new IdentityNormalizer().Normalize(batch);
        var snapShot = new CliWatchProjector().Project(facts);

        var job = Assert.Single(snapShot.Projects, p => p.Id == "demo-job--1");
        Assert.Contains("demo", job.Name, StringComparison.Ordinal);
        Assert.True(CliWatchStatus.IsRunning(job.Phase) || job.Phase == CliWatchStatus.RunningUnknown);
        Assert.NotEqual(CliWatchStatus.RunningActive, job.Phase);
        var work = Assert.Single(job.WorkItems);
        Assert.Equal("Grok", work.ActorName);
        Assert.Equal("grok-4.6", work.Model);
        Assert.Equal("xhigh", work.Effort);
        Assert.NotEqual("pwsh.exe", work.ActorName);
    }

    [Fact]
    public async Task Pid_reuse_does_not_bind()
    {
        using var dir = new TempDir();
        var jobs = Path.Combine(dir.Path, "jobs");
        Directory.CreateDirectory(jobs);
        var adapter = Path.Combine(dir.Path, "adapter", "jobs", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        Directory.CreateDirectory(adapter);
        var stateRoot = Path.Combine(dir.Path, "adapter");
        File.WriteAllText(Path.Combine(jobs, "reuse--1.json"),
            "{" +
            "\"project\":\"demo\",\"job_id\":\"reuse\",\"attempt_id\":\"1\",\"harness\":\"grok-cli\"," +
            "\"key\":\"reuse--1\",\"adapter_job_id\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\"," +
            "\"adapter_state_root\":" + Json(stateRoot) + "}");
        File.WriteAllText(Path.Combine(adapter, "owner.json"),
            "{\"pid\":1001,\"start_time_utc_ticks\":111,\"started_at_utc\":\"2026-09-20T00:00:00Z\"}");

        var reused = new ObservedProcess(1001, 1, 999999, DateTimeOffset.UtcNow, "pwsh.exe", null, "pwsh");
        var snap = new FakeSnapshot(new[] { reused });
        var collector = new MailboxCollector(snap);
        var batch = await collector.CollectAsync(Settings(dir.Path), CancellationToken.None);
        var facts = new IdentityNormalizer().Normalize(batch);
        var projected = new CliWatchProjector().Project(facts);
        var job = Assert.Single(projected.Projects, p => p.Id == "reuse--1");
        Assert.Equal(CliWatchStatus.Unknown, job.Phase);
        Assert.DoesNotContain("运行中", job.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_root_is_unavailable_not_empty_success()
    {
        var collector = new MailboxCollector(new FakeSnapshot(Array.Empty<ObservedProcess>()));
        var settings = new CollectorSettings(Array.Empty<string>(), Array.Empty<string>())
        {
            MailboxRoots = new[] { Path.Combine(Path.GetTempPath(), "cli-watch-missing-" + Guid.NewGuid().ToString("n")) }
        };
        var batch = await collector.CollectAsync(settings, CancellationToken.None);
        var facts = new IdentityNormalizer().Normalize(batch);
        var projected = new CliWatchProjector().Project(facts);
        Assert.Equal(DataQuality.Unavailable, projected.Quality);
        Assert.Empty(projected.Projects);
        Assert.Contains(projected.Issues, i => i.Code == "mailbox_root_missing");
    }

    [Fact]
    public async Task Corrupt_job_stays_unknown()
    {
        using var dir = new TempDir();
        var jobs = Path.Combine(dir.Path, "jobs");
        Directory.CreateDirectory(jobs);
        File.WriteAllText(Path.Combine(jobs, "bad--1.json"), "{not-json");
        var collector = new MailboxCollector(new FakeSnapshot(Array.Empty<ObservedProcess>()));
        var batch = await collector.CollectAsync(Settings(dir.Path), CancellationToken.None);
        Assert.Contains(batch.Issues, i => i.Code == "parse_error");
        var facts = new IdentityNormalizer().Normalize(batch);
        var projected = new CliWatchProjector().Project(facts);
        var job = Assert.Single(projected.Projects);
        Assert.Equal(CliWatchStatus.Unknown, job.Phase);
    }

    [Fact]
    public async Task Ready_and_receipt_are_distinct()
    {
        using var dir = new TempDir();
        var jobs = Path.Combine(dir.Path, "jobs");
        var outbox = Path.Combine(dir.Path, "outbox");
        var adapter = Path.Combine(dir.Path, "adapter", "jobs", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        Directory.CreateDirectory(jobs);
        Directory.CreateDirectory(outbox);
        Directory.CreateDirectory(adapter);
        var stateRoot = Path.Combine(dir.Path, "adapter");
        File.WriteAllText(Path.Combine(jobs, "done--1.json"),
            "{" +
            "\"project\":\"demo\",\"job_id\":\"done\",\"attempt_id\":\"1\",\"harness\":\"grok-cli\"," +
            "\"key\":\"done--1\",\"outbox\":" + Json(outbox) + "," +
            "\"adapter_job_id\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\"," +
            "\"adapter_state_root\":" + Json(stateRoot) + "}");
        File.WriteAllText(Path.Combine(adapter, "owner.json"),
            "{\"pid\":9,\"start_time_utc_ticks\":1,\"started_at_utc\":\"2026-09-20T00:00:00Z\"}");
        File.WriteAllText(Path.Combine(adapter, "receipt.json"),
            "{\"protocol_version\":\"agent-direct-grok-receipt-v1\",\"job_id\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"transport_complete\":true,\"grok_success\":true,\"completed_at_utc\":\"2026-09-20T01:00:00Z\"}");

        var collector = new MailboxCollector(new FakeSnapshot(Array.Empty<ObservedProcess>()));
        var batch = await collector.CollectAsync(Settings(dir.Path), CancellationToken.None);
        var projected = new CliWatchProjector().Project(new IdentityNormalizer().Normalize(batch));
        Assert.Equal(CliWatchStatus.ExecutorFinished, projected.Projects.Single().Phase);

        File.WriteAllText(Path.Combine(outbox, "READY.json"),
            "{\"job_id\":\"done\",\"attempt_id\":\"1\",\"status\":\"completed\",\"report_file\":\"REPORT.md\",\"files\":[]}");
        batch = await collector.CollectAsync(Settings(dir.Path), CancellationToken.None);
        projected = new CliWatchProjector().Project(new IdentityNormalizer().Normalize(batch));
        var delivered = projected.Projects.Single();
        Assert.Equal(CliWatchStatus.Delivered, delivered.Phase);
        Assert.False(delivered.IsActive);
    }

    [Fact]
    public async Task Same_session_old_delivered_does_not_inherit_new_attempt_process()
    {
        using var dir = new TempDir();
        var jobs = Path.Combine(dir.Path, "jobs");
        var out1 = Path.Combine(dir.Path, "out1");
        var out2 = Path.Combine(dir.Path, "out2");
        var ad1 = Path.Combine(dir.Path, "a1", "jobs", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var ad2 = Path.Combine(dir.Path, "a2", "jobs", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        Directory.CreateDirectory(jobs);
        Directory.CreateDirectory(out1);
        Directory.CreateDirectory(out2);
        Directory.CreateDirectory(ad1);
        Directory.CreateDirectory(ad2);
        const string session = "cccccccc-cccc-cccc-cccc-cccccccccccc";
        File.WriteAllText(Path.Combine(jobs, "job--1.json"),
            "{" +
            "\"project\":\"demo\",\"job_id\":\"job\",\"attempt_id\":\"1\",\"harness\":\"grok-cli\"," +
            "\"key\":\"job--1\",\"outbox\":" + Json(out1) + "," +
            "\"adapter_job_id\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\"," +
            "\"adapter_state_root\":" + Json(Path.Combine(dir.Path, "a1")) + "," +
            "\"resume_native_session_id\":\"" + session + "\"}");
        File.WriteAllText(Path.Combine(jobs, "job--2.json"),
            "{" +
            "\"project\":\"demo\",\"job_id\":\"job\",\"attempt_id\":\"2\",\"harness\":\"grok-cli\"," +
            "\"key\":\"job--2\",\"outbox\":" + Json(out2) + "," +
            "\"adapter_job_id\":\"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb\"," +
            "\"adapter_state_root\":" + Json(Path.Combine(dir.Path, "a2")) + "}");
        File.WriteAllText(Path.Combine(ad1, "owner.json"),
            "{\"pid\":1001,\"start_time_utc_ticks\":111,\"started_at_utc\":\"2026-09-20T00:00:00Z\"}");
        File.WriteAllText(Path.Combine(ad1, "request.json"),
            "{\"job_id\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"session_id\":\"" + session + "\",\"model\":\"grok-4.6\",\"reasoning_effort\":\"xhigh\"}");
        File.WriteAllText(Path.Combine(ad2, "owner.json"),
            "{\"pid\":3003,\"start_time_utc_ticks\":333,\"started_at_utc\":\"2026-09-20T01:00:00Z\"}");
        File.WriteAllText(Path.Combine(ad2, "request.json"),
            "{\"job_id\":\"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb\",\"session_id\":\"" + session + "\",\"model\":\"grok-4.6\",\"reasoning_effort\":\"xhigh\"}");
        File.WriteAllText(Path.Combine(out1, "READY.json"),
            "{\"job_id\":\"job\",\"attempt_id\":\"1\",\"status\":\"completed\",\"report_file\":\"REPORT.md\",\"files\":[]}");

        var wrapper2 = new ObservedProcess(3003, 1, 333, DateTimeOffset.Parse("2026-09-20T01:00:00Z"), "pwsh.exe", null, "pwsh -File process_file_host.ps1");
        var grok2 = new ObservedProcess(4004, 3003, 444, DateTimeOffset.Parse("2026-09-20T01:00:01Z"), "grok.exe", null,
            "grok.exe --cwd C:\\work --resume " + session);
        var collector = new MailboxCollector(new FakeSnapshot(new[] { wrapper2, grok2 }));
        var projected = new CliWatchProjector().Project(new IdentityNormalizer().Normalize(
            await collector.CollectAsync(Settings(dir.Path), CancellationToken.None)));

        var old = Assert.Single(projected.Projects, p => p.Id == "job--1");
        var cur = Assert.Single(projected.Projects, p => p.Id == "job--2");
        Assert.Equal(CliWatchStatus.Delivered, old.Phase);
        Assert.False(old.IsActive);
        Assert.True(CliWatchStatus.IsRunning(cur.Phase));
        Assert.True(cur.IsActive);
        Assert.DoesNotContain("运行中", old.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Child_counter_increase_is_activity_unrelated_process_is_not()
    {
        using var dir = new TempDir();
        var jobs = Path.Combine(dir.Path, "jobs");
        var adapter = Path.Combine(dir.Path, "adapter", "jobs", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        Directory.CreateDirectory(jobs);
        Directory.CreateDirectory(adapter);
        File.WriteAllText(Path.Combine(jobs, "live--1.json"),
            "{" +
            "\"project\":\"demo\",\"job_id\":\"live\",\"attempt_id\":\"1\",\"harness\":\"grok-cli\"," +
            "\"key\":\"live--1\",\"adapter_job_id\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\"," +
            "\"adapter_state_root\":" + Json(Path.Combine(dir.Path, "adapter")) + "}");
        File.WriteAllText(Path.Combine(adapter, "owner.json"),
            "{\"pid\":1001,\"start_time_utc_ticks\":111,\"started_at_utc\":\"2026-09-20T00:00:00Z\"}");
        File.WriteAllText(Path.Combine(adapter, "request.json"),
            "{\"job_id\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"model\":\"grok-4.6\"}");

        var store = new ProcessActivityStore();
        var snap = new FakeSnapshot(Array.Empty<ObservedProcess>());
        var collector = new MailboxCollector(snap, activity: store);
        var wrapper = Proc(1001, 1, 111, "pwsh.exe", "pwsh", 10, 10, 10);
        var grok = Proc(2002, 1001, 222, "grok.exe", "grok", 20, 20, 20);
        var child = Proc(3003, 2002, 333, "node.exe", "node worker", 30, 30, 30);
        var unrelated = Proc(9999, 1, 999, "node.exe", "node other", 40, 40, 40);
        snap.Live = new[] { wrapper, grok, child, unrelated };
        await collector.CollectAsync(Settings(dir.Path), CancellationToken.None);

        snap.Live = new[]
        {
            wrapper,
            grok,
            Proc(3003, 2002, 333, "node.exe", "node worker", 30, 5000, 30),
            Proc(9999, 1, 999, "node.exe", "node other", 40, 90000, 40)
        };
        var batch2 = await collector.CollectAsync(Settings(dir.Path), CancellationToken.None);
        var bind = Assert.Single(batch2.Documents, d => d.Kind == MailboxCollector.KindProcessBind);
        Assert.Equal("child_io", bind.Data["activity_kind"]?.GetValue<string>());
        var projected = new CliWatchProjector().Project(new IdentityNormalizer().Normalize(batch2));
        var job = Assert.Single(projected.Projects, p => p.Id == "live--1");
        Assert.Equal(CliWatchStatus.RunningActive, job.Phase);
        Assert.Contains("子程序有计算/读写活动", job.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("卡死", job.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Quiet_counters_are_not_failure()
    {
        using var dir = new TempDir();
        var jobs = Path.Combine(dir.Path, "jobs");
        var adapter = Path.Combine(dir.Path, "adapter", "jobs", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        Directory.CreateDirectory(jobs);
        Directory.CreateDirectory(adapter);
        File.WriteAllText(Path.Combine(jobs, "q--1.json"),
            "{" +
            "\"project\":\"demo\",\"job_id\":\"q\",\"attempt_id\":\"1\",\"harness\":\"grok-cli\"," +
            "\"key\":\"q--1\",\"adapter_job_id\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\"," +
            "\"adapter_state_root\":" + Json(Path.Combine(dir.Path, "adapter")) + "}");
        File.WriteAllText(Path.Combine(adapter, "owner.json"),
            "{\"pid\":1001,\"start_time_utc_ticks\":111,\"started_at_utc\":\"2026-09-20T00:00:00Z\"}");
        File.WriteAllText(Path.Combine(adapter, "request.json"),
            "{\"job_id\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"model\":\"grok-4.6\"}");
        var grok = Proc(2002, 1001, 222, "grok.exe", "grok", 20, 20, 20);
        var wrapper = Proc(1001, 1, 111, "pwsh.exe", "pwsh", 10, 10, 10);
        var snap = new FakeSnapshot(new[] { wrapper, grok });
        var collector = new MailboxCollector(snap);
        await collector.CollectAsync(Settings(dir.Path), CancellationToken.None);
        var batch2 = await collector.CollectAsync(Settings(dir.Path), CancellationToken.None);
        var projected = new CliWatchProjector().Project(new IdentityNormalizer().Normalize(batch2));
        var job = Assert.Single(projected.Projects, p => p.Id == "q--1");
        Assert.Equal(CliWatchStatus.RunningQuiet, job.Phase);
        Assert.NotEqual(CliWatchStatus.Failed, job.Phase);
    }

    static ObservedProcess Proc(int pid, int parent, long ticks, string image, string cmd, long cpu, long read, long write) =>
        new(pid, parent, ticks, DateTimeOffset.Parse("2026-09-20T00:00:00Z"), image, null, cmd, cpu, read, write, true);

    [Fact]
    public async Task Unbound_cli_is_not_guessed()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "jobs"));
        var grok = new ObservedProcess(3003, 1, 333, DateTimeOffset.UtcNow, "grok.exe", null, "grok.exe --cwd C:\\other");
        var collector = new MailboxCollector(new FakeSnapshot(new[] { grok }));
        var batch = await collector.CollectAsync(Settings(dir.Path), CancellationToken.None);
        var projected = new CliWatchProjector().Project(new IdentityNormalizer().Normalize(batch));
        var unbound = Assert.Single(projected.Projects);
        Assert.Equal("unbound", unbound.Id);
        Assert.Equal("未关联任务", unbound.Name);
        Assert.DoesNotContain("demo", unbound.Name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Snapshot_ready_after_outbox_gone_restores_delivery_on_latest_round()
    {
        using var dir = new TempDir();
        var jobs = Path.Combine(dir.Path, "jobs");
        var snap = Path.Combine(dir.Path, "snapshots", "old--1");
        Directory.CreateDirectory(jobs);
        Directory.CreateDirectory(Path.Combine(dir.Path, "deliveries"));
        Directory.CreateDirectory(snap);
        File.WriteAllText(Path.Combine(jobs, "old--1.json"),
            "{" +
            "\"project\":\"demo-project\",\"job_id\":\"old\",\"attempt_id\":\"1\",\"harness\":\"codex-cli\"," +
            "\"key\":\"old--1\",\"state\":\"registered\"," +
            "\"registered_at\":\"2026-09-20T00:00:00Z\"," +
            "\"outbox\":" + Json(Path.Combine(dir.Path, "missing-outbox")) + "}");
        File.WriteAllText(Path.Combine(dir.Path, "deliveries", "old--1.json"),
            "{\"id\":\"old--1\",\"project\":\"demo-project\",\"state\":\"sent\",\"sent_at\":\"2026-09-20T01:20:06Z\",\"marker\":\"app-mailbox:old--1\"}");
        File.WriteAllText(Path.Combine(snap, "manifest.json"),
            "{\"job\":{\"project\":\"demo-project\",\"job_id\":\"old\",\"attempt_id\":\"1\",\"key\":\"old--1\",\"harness\":\"codex-cli\"},\"files\":[],\"marker\":\"app-mailbox:old--1\"}");
        File.WriteAllText(Path.Combine(snap, "0-READY.json"),
            "{\"job_id\":\"old\",\"attempt_id\":\"1\",\"status\":\"completed\",\"report_file\":\"REPORT.md\",\"files\":[]}");

        var projected = new CliWatchProjector().Project(new IdentityNormalizer().Normalize(
            await new MailboxCollector(new FakeSnapshot(Array.Empty<ObservedProcess>()))
                .CollectAsync(Settings(dir.Path), CancellationToken.None)));
        var job = Assert.Single(projected.Projects, p => p.Id == "old--1");
        Assert.Equal(CliWatchStatus.Delivered, job.Phase);
        Assert.False(job.IsActive);
        Assert.DoesNotContain("运行 ", job.Summary, StringComparison.Ordinal);
        Assert.Contains("已交付待 Lead", job.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wrapper_only_does_not_show_cli_running_duration()
    {
        using var dir = new TempDir();
        var jobs = Path.Combine(dir.Path, "jobs");
        var adapter = Path.Combine(dir.Path, "adapter", "jobs", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        Directory.CreateDirectory(jobs);
        Directory.CreateDirectory(adapter);
        File.WriteAllText(Path.Combine(jobs, "wrap--1.json"),
            "{" +
            "\"project\":\"demo\",\"job_id\":\"wrap\",\"attempt_id\":\"1\",\"harness\":\"grok-cli\"," +
            "\"key\":\"wrap--1\",\"adapter_job_id\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\"," +
            "\"adapter_state_root\":" + Json(Path.Combine(dir.Path, "adapter")) + "}");
        File.WriteAllText(Path.Combine(adapter, "owner.json"),
            "{\"pid\":1001,\"start_time_utc_ticks\":111,\"started_at_utc\":\"2026-09-20T00:00:00Z\"}");
        File.WriteAllText(Path.Combine(adapter, "request.json"),
            "{\"job_id\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"model\":\"grok-4.6\"}");

        var wrapper = Proc(1001, 1, 111, "pwsh.exe", "pwsh -File process_file_host.ps1", 10, 10, 10);
        var collector = new MailboxCollector(new FakeSnapshot(new[] { wrapper }));
        var projected = new CliWatchProjector().Project(new IdentityNormalizer().Normalize(
            await collector.CollectAsync(Settings(dir.Path), CancellationToken.None)));
        var job = Assert.Single(projected.Projects, p => p.Id == "wrap--1");
        Assert.Equal(CliWatchStatus.StartUnconfirmed, job.Phase);
        Assert.DoesNotContain("运行 ", job.Summary, StringComparison.Ordinal);
        Assert.Contains("启动等待已过", job.Summary, StringComparison.Ordinal);

        var grok = Proc(2002, 1001, 222, "grok.exe", "grok --cwd C:\\work", 20, 20, 20);
        projected = new CliWatchProjector().Project(new IdentityNormalizer().Normalize(
            await new MailboxCollector(new FakeSnapshot(new[] { wrapper, grok }))
                .CollectAsync(Settings(dir.Path), CancellationToken.None)));
        job = Assert.Single(projected.Projects, p => p.Id == "wrap--1");
        Assert.True(CliWatchStatus.IsRunning(job.Phase));
        Assert.Contains("运行 ", job.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("启动等待已过", job.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Registered_only_does_not_show_running_duration()
    {
        using var dir = new TempDir();
        var jobs = Path.Combine(dir.Path, "jobs");
        Directory.CreateDirectory(jobs);
        File.WriteAllText(Path.Combine(jobs, "new--1.json"),
            "{" +
            "\"project\":\"demo\",\"job_id\":\"new\",\"attempt_id\":\"1\",\"harness\":\"codex-cli\"," +
            "\"key\":\"new--1\",\"state\":\"registered\"," +
            "\"registered_at\":\"2026-09-19T00:00:00Z\"}");
        var projected = new CliWatchProjector().Project(new IdentityNormalizer().Normalize(
            await new MailboxCollector(new FakeSnapshot(Array.Empty<ObservedProcess>()))
                .CollectAsync(Settings(dir.Path), CancellationToken.None))
            with { CollectedAt = DateTimeOffset.Parse("2026-09-20T00:00:00Z") });
        var job = Assert.Single(projected.Projects, p => p.Id == "new--1");
        Assert.Equal(CliWatchStatus.Registered, job.Phase);
        Assert.False(job.IsActive);
        Assert.DoesNotContain("运行 ", job.Summary, StringComparison.Ordinal);
        Assert.Contains("登记已过", job.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wrong_snapshot_identity_is_not_completion()
    {
        using var dir = new TempDir();
        var jobs = Path.Combine(dir.Path, "jobs");
        var snap = Path.Combine(dir.Path, "snapshots", "job--1");
        Directory.CreateDirectory(jobs);
        Directory.CreateDirectory(snap);
        File.WriteAllText(Path.Combine(jobs, "job--1.json"),
            "{\"project\":\"demo\",\"job_id\":\"job\",\"attempt_id\":\"1\",\"harness\":\"codex-cli\",\"key\":\"job--1\",\"state\":\"registered\"}");
        File.WriteAllText(Path.Combine(snap, "manifest.json"),
            "{\"job\":{\"project\":\"demo\",\"job_id\":\"job\",\"attempt_id\":\"9\",\"key\":\"job--1\"},\"files\":[],\"marker\":\"x\"}");
        File.WriteAllText(Path.Combine(snap, "0-READY.json"),
            "{\"job_id\":\"job\",\"attempt_id\":\"9\",\"status\":\"completed\"}");
        var projected = new CliWatchProjector().Project(new IdentityNormalizer().Normalize(
            await new MailboxCollector(new FakeSnapshot(Array.Empty<ObservedProcess>()))
                .CollectAsync(Settings(dir.Path), CancellationToken.None)));
        var job = Assert.Single(projected.Projects, p => p.Id == "job--1");
        Assert.NotEqual(CliWatchStatus.Delivered, job.Phase);
        Assert.Equal(CliWatchStatus.Unknown, job.Phase);
    }

    [Fact]
    public async Task Snapshot_failed_ready_stays_a_reported_problem()
    {
        using var dir = new TempDir();
        var jobs = Path.Combine(dir.Path, "jobs");
        var snap = Path.Combine(dir.Path, "snapshots", "bad--1");
        Directory.CreateDirectory(jobs);
        Directory.CreateDirectory(snap);
        File.WriteAllText(Path.Combine(jobs, "bad--1.json"),
            "{\"project\":\"demo\",\"job_id\":\"bad\",\"attempt_id\":\"1\",\"harness\":\"codex-cli\",\"key\":\"bad--1\"}");
        File.WriteAllText(Path.Combine(snap, "manifest.json"),
            "{\"job\":{\"project\":\"demo\",\"job_id\":\"bad\",\"attempt_id\":\"1\",\"key\":\"bad--1\"},\"files\":[]}");
        File.WriteAllText(Path.Combine(snap, "0-READY.json"),
            "{\"job_id\":\"bad\",\"attempt_id\":\"1\",\"status\":\"failed\"}");
        var projected = new CliWatchProjector().Project(new IdentityNormalizer().Normalize(
            await new MailboxCollector(new FakeSnapshot(Array.Empty<ObservedProcess>()))
                .CollectAsync(Settings(dir.Path), CancellationToken.None)));
        var job = Assert.Single(projected.Projects, p => p.Id == "bad--1");
        Assert.Equal(CliWatchStatus.ReportedFailure, job.Phase);
        Assert.False(job.IsActive);
    }

    [Fact]
    public async Task Live_cli_not_hidden_by_snapshot_ready()
    {
        using var dir = new TempDir();
        var jobs = Path.Combine(dir.Path, "jobs");
        var adapter = Path.Combine(dir.Path, "adapter", "jobs", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var snap = Path.Combine(dir.Path, "snapshots", "live--1");
        Directory.CreateDirectory(jobs);
        Directory.CreateDirectory(adapter);
        Directory.CreateDirectory(snap);
        File.WriteAllText(Path.Combine(jobs, "live--1.json"),
            "{" +
            "\"project\":\"demo\",\"job_id\":\"live\",\"attempt_id\":\"1\",\"harness\":\"grok-cli\"," +
            "\"key\":\"live--1\",\"adapter_job_id\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\"," +
            "\"adapter_state_root\":" + Json(Path.Combine(dir.Path, "adapter")) + "}");
        File.WriteAllText(Path.Combine(adapter, "owner.json"),
            "{\"pid\":1001,\"start_time_utc_ticks\":111,\"started_at_utc\":\"2026-09-20T00:00:00Z\"}");
        File.WriteAllText(Path.Combine(adapter, "request.json"),
            "{\"job_id\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"model\":\"grok-4.6\"}");
        File.WriteAllText(Path.Combine(snap, "manifest.json"),
            "{\"job\":{\"project\":\"demo\",\"job_id\":\"live\",\"attempt_id\":\"1\",\"key\":\"live--1\"},\"files\":[]}");
        File.WriteAllText(Path.Combine(snap, "0-READY.json"),
            "{\"job_id\":\"live\",\"attempt_id\":\"1\",\"status\":\"completed\"}");
        var wrapper = Proc(1001, 1, 111, "pwsh.exe", "pwsh", 10, 10, 10);
        var grok = Proc(2002, 1001, 222, "grok.exe", "grok", 20, 20, 20);
        var projected = new CliWatchProjector().Project(new IdentityNormalizer().Normalize(
            await new MailboxCollector(new FakeSnapshot(new[] { wrapper, grok }))
                .CollectAsync(Settings(dir.Path), CancellationToken.None)));
        var job = Assert.Single(projected.Projects, p => p.Id == "live--1");
        Assert.True(CliWatchStatus.IsRunning(job.Phase) || job.Phase == CliWatchStatus.StartUnconfirmed);
        Assert.True(job.IsActive);
        Assert.NotEqual(CliWatchStatus.Delivered, job.Phase);
    }


    [Theory]
    [InlineData("foreign-key")]
    [InlineData("numeric-key")]
    [InlineData("foreign-root")]
    [InlineData("attempt")]
    [InlineData("attempt-aliases")]
    [InlineData("status-type")]
    public async Task Rejected_ready_does_not_reassign_or_erase_healthy_job(string problem)
    {
        using var dir = new TempDir();
        var a = FixJob(dir.Path, "a");
        FixJob(dir.Path, "b");
        var ready = new JsonObject { ["status"] = "completed", ["job_id"] = "a", ["attempt_id"] = "1" };
        if (problem == "foreign-key") ready["job_key"] = "b--1";
        if (problem == "numeric-key") ready["job_key"] = 123;
        if (problem == "foreign-root") ready["mailbox_root"] = Path.Combine(dir.Path, "other");
        if (problem == "attempt") { ready.Remove("attempt_id"); ready["attempt"] = 2; }
        if (problem == "attempt-aliases") ready["attempt"] = 2;
        if (problem == "status-type") ready["status"] = 3;
        WriteFix(Path.Combine(a, "READY.json"), ready);
        var batch = await new MailboxCollector(new FakeSnapshot([])).CollectAsync(FixSettings(dir.Path), CancellationToken.None);
        var projected = new CliWatchProjector().Project(new IdentityNormalizer().Normalize(batch));
        Assert.Equal(CliWatchStatus.Registered, projected.Projects.Single(p => p.Id == "b--1").Phase);
        Assert.NotEqual(CliWatchStatus.Delivered, projected.Projects.Single(p => p.Id == "a--1").Phase);
        Assert.Contains(batch.Issues, i => i.Code.Contains("conflict") || i.Code == "schema_error");
    }

    [Theory]
    [InlineData("1", "executor_finished", "adapter-model（已观察）")]
    [InlineData("2", "unknown", "registered-model（登记指定）")]
    public async Task Adapter_attempt_applies_to_model_and_receipt(string attempt, string phase, string model)
    {
        using var dir = new TempDir();
        FixJob(dir.Path, "job", state: true);
        var adapter = Path.Combine(dir.Path, "state", "jobs", "job");
        WriteFix(Path.Combine(adapter, "request.json"), new JsonObject { ["job_id"] = "job", ["attempt_id"] = attempt, ["model"] = "adapter-model" });
        WriteFix(Path.Combine(adapter, "receipt.json"), new JsonObject { ["job_id"] = "job", ["attempt"] = attempt, ["transport_complete"] = true, ["grok_success"] = true });
        var batch = await new MailboxCollector(new FakeSnapshot([])).CollectAsync(FixSettings(dir.Path), CancellationToken.None);
        var view = new CliWatchProjector().Project(new IdentityNormalizer().Normalize(batch)).Projects.Single();
        Assert.Equal(phase, view.Phase);
        Assert.Equal(model, view.WorkItems.Single().Model);
    }

    [Theory]
    [InlineData("correct-zero", "executor_finished")]
    [InlineData("correct-nonzero", "failed")]
    [InlineData("nonzero-ready", "failed")]
    [InlineData("wrong-pid", "unknown")]
    [InlineData("wrong-ticks", "unknown")]
    [InlineData("wrong-attempt", "unknown")]
    [InlineData("wrong-job", "unknown")]
    [InlineData("string-exit", "unknown")]
    [InlineData("missing-owner", "unknown")]
    public async Task Terminal_requires_registered_round_and_exact_owner(string problem, string phase)
    {
        using var dir = new TempDir();
        FixJob(dir.Path, "job", state: true);
        var adapter = Path.Combine(dir.Path, "state", "jobs", "job");
        var ticks = DateTimeOffset.UtcNow.AddMinutes(-1).UtcTicks;
        var owner = new JsonObject { ["job_id"] = "job", ["attempt"] = 1, ["pid"] = 500, ["start_time_utc_ticks"] = ticks };
        if (problem != "missing-owner") WriteFix(Path.Combine(adapter, "owner.json"), owner);
        var terminal = (JsonObject)owner.DeepClone();
        terminal["exit_code"] = problem is "correct-nonzero" or "nonzero-ready" ? 17 : 0;
        if (problem == "nonzero-ready")
            WriteFix(Path.Combine(dir.Path, "outbox", "job-1", "READY.json"), new JsonObject { ["status"] = "completed", ["job_id"] = "job", ["attempt_id"] = "1" });
        terminal["finished_at_utc"] = DateTimeOffset.UtcNow.ToString("o");
        if (problem == "wrong-pid") terminal["pid"] = 501;
        if (problem == "wrong-ticks") terminal["start_time_utc_ticks"] = ticks + 1;
        if (problem == "wrong-attempt") terminal["attempt"] = 2;
        if (problem == "wrong-job") terminal["job_id"] = "other";
        if (problem == "string-exit") terminal["exit_code"] = "0";
        WriteFix(Path.Combine(adapter, "terminal.json"), terminal);
        var batch = await new MailboxCollector(new FakeSnapshot([])).CollectAsync(FixSettings(dir.Path), CancellationToken.None);
        var view = new CliWatchProjector().Project(new IdentityNormalizer().Normalize(batch)).Projects.Single();
        Assert.Equal(phase, view.Phase);
        Assert.NotEqual("complete", view.WorkItems.Single().Axes.Delivery);
        Assert.NotEqual("accepted", view.WorkItems.Single().Axes.Acceptance);
    }

    [Theory]
    [InlineData("delivered", true, "delivered")]
    [InlineData("unknown", true, "unknown")]
    [InlineData("delivered", false, "unknown")]
    public async Task Historical_delivered_alias_is_exact_claude_only(string status, bool claude, string phase)
    {
        using var dir = new TempDir();
        var outbox = FixJob(dir.Path, "job", claude: claude);
        WriteFix(Path.Combine(outbox, "READY.json"), new JsonObject { ["job_id"] = "job", ["attempt_id"] = "1", ["status"] = status });
        var settings = claude ? FixSettings(dir.Path) : Settings(dir.Path);
        var batch = await new MailboxCollector(new FakeSnapshot([])).CollectAsync(settings, CancellationToken.None);
        var view = new CliWatchProjector().Project(new IdentityNormalizer().Normalize(batch)).Projects.Single();
        Assert.Equal(phase, view.Phase);
    }

    [Fact]
    public async Task Live_prompt_adapter_registration_precedes_historical_evidence()
    {
        using var dir = new TempDir();
        var jobs = Path.Combine(dir.Path, "jobs");
        Directory.CreateDirectory(jobs);
        var liveId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        var oldId = "dddddddd-dddd-dddd-dddd-dddddddddddd";
        var stateRoot = Path.Combine(dir.Path, "adapter");
        WriteAdapterOwner(stateRoot, liveId, 1001, 111);
        File.WriteAllText(Path.Combine(stateRoot, "jobs", liveId, "request.json"),
            "{\"job_id\":\"" + liveId + "\",\"workspace\":\"C:\\\\work\\\\demo\",\"model\":\"grok-4.7\",\"reasoning_effort\":\"xhigh\",\"session_id\":\"cccccccc-cccc-cccc-cccc-cccccccccccc\",\"resume\":true}");
        for (var i = 0; i < 12; i++)
        {
            var name = "a-hist-" + i.ToString("00");
            var histId = "bbbbbbbb-bbbb-bbbb-bbbb-" + i.ToString("000000000000");
            WriteAdapterOwner(stateRoot, histId, 9, 9);
            WriteRegistration(jobs, name, histId, stateRoot, null);
        }

        WriteAdapterOwner(stateRoot, oldId, 8, 8);
        var mentionResult = Path.Combine(dir.Path, "old-name", "native", "jobs", liveId, "grok-result.json");
        WriteRegistration(jobs, "a-mention", oldId, stateRoot,
            ",\"task_summary\":\"see " + liveId + "\",\"adapter_result\":" + Json(mentionResult));
        WriteRegistration(jobs, "a-none", null, null, ",\"task_summary\":\"see " + liveId + "\"");
        WriteRegistration(jobs, "a-old-name", oldId, stateRoot, null);
        WriteRegistration(jobs, "z-live", liveId, stateRoot, null);
        var names = Directory.EnumerateFiles(jobs, "*.json").Select(Path.GetFileName).ToList();
        Assert.Equal("z-live--1.json", names[^1]);
        var prompt = Path.Combine(dir.Path, "old-name", "native", "jobs", liveId, "prompt.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(prompt)!);
        File.WriteAllText(prompt, "fixture");
        var (wrapper, grok) = LivePromptProcesses(prompt);
        var budget = names.Count + 2;
        var collector = new MailboxCollector(new FakeSnapshot(new[] { wrapper, grok }));
        var batch = await collector.CollectAsync(Settings(dir.Path) with { MaxFilesPerRefresh = budget, GrokHome = Path.Combine(dir.Path, "no-grok-home") }, CancellationToken.None);
        var reads = collector.ContentReads;
        var attempted = batch.Documents.First(d => d.Kind == "watch_config").Data["json_files_attempted"]!.GetValue<int>();
        Assert.Equal(budget, attempted);
        Assert.Equal(budget, reads.Count);
        Assert.EndsWith(Path.Combine(liveId, "owner.json"), reads[^2]);
        Assert.EndsWith(Path.Combine(liveId, "request.json"), reads[^1]);
        Assert.DoesNotContain(reads.Take(reads.Count - 2), path => path.EndsWith("owner.json", StringComparison.OrdinalIgnoreCase) || path.EndsWith("request.json", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(reads, path => path.Contains("a-hist-", StringComparison.OrdinalIgnoreCase) && path.EndsWith("owner.json", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(reads, path => path.Contains(oldId, StringComparison.OrdinalIgnoreCase));
        var bind = Assert.Single(batch.Documents, d => d.Kind == "process_bind" && ObservationIdentity.Text(d.Data, "job_key") == "z-live--1");
        Assert.Equal(2002, bind.Data["cli_pid"]?.GetValue<int>());
        Assert.DoesNotContain(batch.Documents, d => d.Kind == "process_bind" && d.Data["cli_pid"]?.GetValue<int>() == 2002 && ObservationIdentity.Text(d.Data, "job_key") != "z-live--1");
        Assert.DoesNotContain(batch.Documents, d => d.Kind == "unbound_process" && d.Data["pid"]?.GetValue<int>() == 2002);
        Assert.Contains(batch.Issues, issue => issue.Code == "scan_budget_exhausted");
        Assert.False(File.Exists(Path.Combine(dir.Path, "prompt-file-bindings.json")));
        var projected = new CliWatchProjector().Project(new IdentityNormalizer().Normalize(batch));
        Assert.True(Assert.Single(projected.Projects, p => p.Id == "z-live--1").IsActive);
        Assert.DoesNotContain(projected.Projects, p => (p.Id == "a-mention--1" || p.Id == "a-none--1" || p.Id == "a-old-name--1") && p.IsActive);
    }

    [Fact]
    public async Task Tiny_budget_does_not_prefix_read_registrations()
    {
        using var dir = new TempDir();
        var jobs = Path.Combine(dir.Path, "jobs");
        var liveId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        var stateRoot = Path.Combine(dir.Path, "adapter");
        for (var i = 0; i < 12; i++)
            WriteRegistration(jobs, "a-hist-" + i.ToString("00"), null, null, ",\"task_summary\":\"plain\"");
        WriteAdapterOwner(stateRoot, liveId, 1001, 111);
        WriteRegistration(jobs, "z-live", liveId, stateRoot, null);
        var prompt = Path.Combine(dir.Path, "native", "jobs", liveId, "prompt.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(prompt)!);
        File.WriteAllText(prompt, "fixture");
        var (wrapper, grok) = LivePromptProcesses(prompt);
        var collector = new MailboxCollector(new FakeSnapshot(new[] { wrapper, grok }));
        var batch = await collector.CollectAsync(Settings(dir.Path) with { MaxFilesPerRefresh = 0, GrokHome = Path.Combine(dir.Path, "no-grok-home") }, CancellationToken.None);
        var attempted = batch.Documents.First(d => d.Kind == "watch_config").Data["json_files_attempted"]!.GetValue<int>();
        Assert.Equal(1, attempted);
        Assert.Single(collector.ContentReads);
        Assert.DoesNotContain(collector.ContentReads, path => path.Contains("z-live", StringComparison.OrdinalIgnoreCase) || path.Contains(liveId, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(batch.Documents, d => d.Kind == "unbound_process" && d.Data["pid"]?.GetValue<int>() == 2002);
        Assert.DoesNotContain(batch.Documents, d => d.Kind == "mailbox_job" && ObservationIdentity.Text(d.Data, "job_id") == "z-live");
    }

    [Fact]
    public async Task Oversized_registration_is_not_read_to_select_a_live_adapter()
    {
        using var dir = new TempDir();
        var jobs = Path.Combine(dir.Path, "jobs");
        var liveId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        var stateRoot = Path.Combine(dir.Path, "adapter");
        var over = Path.Combine(jobs, "0-over--1.json");
        Directory.CreateDirectory(jobs);
        var head = "{\"project\":\"fixture\",\"job_id\":\"over\",\"attempt_id\":\"1\",\"harness\":\"grok-cli\",\"key\":\"over--1\",\"adapter_job_id\":\"" + liveId + "\",\"pad\":\"";
        File.WriteAllText(over, head + new string('x', 6000) + "\"}");
        WriteRegistration(jobs, "a-hist-00", null, null, null);
        WriteAdapterOwner(stateRoot, liveId, 1001, 111);
        WriteRegistration(jobs, "z-live", liveId, stateRoot, null);
        var prompt = Path.Combine(dir.Path, "native", "jobs", liveId, "prompt.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(prompt)!);
        File.WriteAllText(prompt, "fixture");
        var (wrapper, grok) = LivePromptProcesses(prompt);
        var collector = new MailboxCollector(new FakeSnapshot(new[] { wrapper, grok }));
        var batch = await collector.CollectAsync(Settings(dir.Path) with { MaxFilesPerRefresh = 2, MaxFileBytes = 512, GrokHome = Path.Combine(dir.Path, "no-grok-home") }, CancellationToken.None);
        var attempted = batch.Documents.First(d => d.Kind == "watch_config").Data["json_files_attempted"]!.GetValue<int>();
        Assert.Equal(2, attempted);
        Assert.Single(collector.ContentReads);
        Assert.DoesNotContain(collector.ContentReads, path => path.EndsWith("0-over--1.json", StringComparison.OrdinalIgnoreCase) || path.Contains("z-live", StringComparison.OrdinalIgnoreCase) || path.Contains(liveId, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(batch.Issues, issue => issue.Code == "too_large" && issue.SourceId.EndsWith("0-over--1.json", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(batch.Documents, d => d.Kind == "mailbox_job" && ObservationIdentity.Text(d.Data, "job_id") == "z-live");
        Assert.Contains(batch.Documents, d => d.Kind == "unbound_process" && d.Data["pid"]?.GetValue<int>() == 2002);
    }

    [Fact]
    public async Task Finished_adapter_registration_stays_inactive_without_a_live_process()
    {
        using var dir = new TempDir();
        var jobs = Path.Combine(dir.Path, "jobs");
        Directory.CreateDirectory(jobs);
        var adapterId = "dddddddd-dddd-dddd-dddd-dddddddddddd";
        var adapter = Path.Combine(dir.Path, "adapter", "jobs", adapterId);
        Directory.CreateDirectory(adapter);
        File.WriteAllText(Path.Combine(adapter, "owner.json"),
            "{\"pid\":1001,\"start_time_utc_ticks\":111,\"started_at_utc\":\"2026-09-20T00:00:00Z\"}");
        File.WriteAllText(Path.Combine(adapter, "receipt.json"),
            "{\"job_id\":\"" + adapterId + "\",\"attempt_id\":\"1\",\"transport_complete\":true,\"grok_success\":true}");
        File.WriteAllText(Path.Combine(jobs, "ended--1.json"),
            "{\"project\":\"fixture\",\"job_id\":\"ended\",\"attempt_id\":\"1\",\"harness\":\"grok-cli\",\"key\":\"ended--1\",\"adapter_job_id\":\"" + adapterId + "\",\"adapter_state_root\":" + Json(Path.Combine(dir.Path, "adapter")) + "}");
        var settings = Settings(dir.Path) with { GrokHome = Path.Combine(dir.Path, "no-grok-home") };
        var projected = new CliWatchProjector().Project(new IdentityNormalizer().Normalize(
            await new MailboxCollector(new FakeSnapshot([])).CollectAsync(settings, CancellationToken.None)));
        var job = Assert.Single(projected.Projects, p => p.Id == "ended--1");
        Assert.False(job.IsActive);
    }

    [Fact]
    public async Task Tiny_budget_rotates_roots_and_keeps_unscanned_registration_history_stale()
    {
        using var dir = new TempDir();
        var a = Path.Combine(dir.Path, "a");
        var b = Path.Combine(dir.Path, "b");
        FixJob(a, "a1"); FixJob(a, "a2"); FixJob(b, "b1");
        var settings = new CollectorSettings([], [], MaxFilesPerRefresh: 1) { ClaudeMailboxRoots = [a, b] };
        var collector = new MailboxCollector(new FakeSnapshot([]));
        var projector = new CliWatchProjector();
        CockpitSnapshot? previous = null;
        var found = new HashSet<string>();
        for (var i = 0; i < 6; i++)
        {
            var batch = await collector.CollectAsync(settings, CancellationToken.None);
            Assert.Single(batch.Documents, d => d.Kind == "mailbox_job");
            Assert.Contains(batch.Issues, issue => issue.Code == "scan_budget_exhausted");
            previous = projector.Project(new IdentityNormalizer().Normalize(batch), previous);
            Assert.Equal(DataQuality.Partial, previous.Quality);
            foreach (var item in previous.Projects)
            {
                found.Add(item.Id);
                Assert.Contains(item.Phase, new[] { CliWatchStatus.Unknown, CliWatchStatus.Registered });
                if (item.Quality == DataQuality.LastKnown) Assert.False(item.IsActive);
            }
        }
        Assert.Equal(3, found.Count);
    }

    [Fact]
    public async Task Partial_scan_does_not_duplicate_same_key_on_different_roots()
    {
        using var dir = new TempDir();
        var a = Path.Combine(dir.Path, "a"); var b = Path.Combine(dir.Path, "b");
        FixJob(a, "same"); FixJob(b, "same");
        var settings = new CollectorSettings([], []) { ClaudeMailboxRoots = [a, b] };
        var collector = new MailboxCollector(new FakeSnapshot([])); var projector = new CliWatchProjector();
        var full = projector.Project(new IdentityNormalizer().Normalize(await collector.CollectAsync(settings, CancellationToken.None)));
        var partial = projector.Project(new IdentityNormalizer().Normalize(await collector.CollectAsync(settings with { MaxFilesPerRefresh = 1 }, CancellationToken.None)), full);
        Assert.Equal(2, partial.Projects.Count);
        Assert.Equal(full.Projects.Select(p => p.Id).OrderBy(id => id), partial.Projects.Select(p => p.Id).OrderBy(id => id));
        Assert.Single(partial.Projects, p => p.Quality == DataQuality.LastKnown && p.Phase == CliWatchStatus.Registered && !p.IsActive);
    }

    [Theory]
    [InlineData(2, 1048576, false, "scan_budget_exhausted")]
    [InlineData(512, 1024, false, "too_large")]
    [InlineData(512, 1048576, true, null)]
    public async Task Grok_associated_metadata_obeys_budget_without_copying_body(int files, int bytes, bool observed, string? issue)
    {
        using var dir = new TempDir();
        FixJob(dir.Path, "job", state: true);
        var jobFile = Path.Combine(dir.Path, "jobs", "job", "1", "job.json");
        var job = JsonNode.Parse(File.ReadAllText(jobFile))!.AsObject(); job["executor"] = "grok"; WriteFix(jobFile, job);
        const string session = "11111111-1111-4111-8111-111111111111";
        WriteFix(Path.Combine(dir.Path, "state", "jobs", "job", "request.json"), new JsonObject { ["job_id"] = "job", ["attempt"] = 1, ["session_id"] = session, ["workspace"] = dir.Path });
        var grokHome = Path.Combine(dir.Path, "grok");
        var summary = Path.Combine(grokHome, "sessions", Uri.EscapeDataString(dir.Path), session, "summary.json");
        const string body = "FIXTURE_BODY_MUST_NOT_ENTER_FACTS";
        WriteFix(summary, new JsonObject { ["current_model_id"] = "session-model", ["last_active_at"] = DateTimeOffset.UtcNow.ToString("o"), ["unrecognized_body"] = body + new string('x', 2048) });
        var settings = FixSettings(dir.Path) with { MaxFilesPerRefresh = files, MaxFileBytes = bytes, GrokHome = grokHome };
        var batch = await new MailboxCollector(new FakeSnapshot([])).CollectAsync(settings, CancellationToken.None);
        Assert.True(batch.Documents.First(d => d.Kind == "watch_config").Data["json_files_attempted"]!.GetValue<int>() <= files);
        Assert.Equal(observed, batch.Documents.Any(d => d.Kind == "grok_session_activity" && ObservationIdentity.Text(d.Data, "current_model_id") == "session-model"));
        Assert.DoesNotContain(body, string.Join(" ", batch.Documents.Select(d => d.Data.ToJsonString())), StringComparison.Ordinal);
        if (issue is not null) Assert.Contains(batch.Issues, i => i.Code == issue);
    }

    [Fact]
    public async Task Byte_budget_rejects_routes_and_oversized_job_but_keeps_small_job()
    {
        using var dir = new TempDir();
        FixJob(dir.Path, "large", claude: false); FixJob(dir.Path, "small", claude: false);
        var path = Path.Combine(dir.Path, "jobs", "large--1.json");
        var job = JsonNode.Parse(File.ReadAllText(path))!.AsObject(); job["padding"] = new string('x', 4096); WriteFix(path, job);
        WriteFix(Path.Combine(dir.Path, "routes.json"), new JsonObject { ["padding"] = new string('x', 4096) });
        var batch = await new MailboxCollector(new FakeSnapshot([])).CollectAsync(Settings(dir.Path) with { MaxFileBytes = 1024 }, CancellationToken.None);
        Assert.Contains(batch.Documents, d => d.Kind == "mailbox_job" && ObservationIdentity.Text(d.Data, "job_id") == "small");
        Assert.Equal(2, batch.Issues.Count(i => i.Code == "too_large"));
    }

    static string FixJob(string root, string id, bool state = false, bool claude = true)
    {
        var outbox = Path.Combine(root, "outbox", id + "-1"); Directory.CreateDirectory(outbox);
        var job = new JsonObject { ["job_id"] = id, ["attempt"] = 1, ["project"] = "fixture", ["executor"] = "codex-cli",
            ["model"] = "registered-model", ["effort"] = "xhigh", ["outbox"] = outbox,
            ["dispatched_at"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("o") };
        if (state) { job["state_root"] = Path.Combine(root, "state"); Directory.CreateDirectory(Path.Combine(root, "state", "jobs", id)); }
        if (!claude) { job["key"] = id + "--1"; job["harness"] = "codex-cli"; }
        WriteFix(claude ? Path.Combine(root, "jobs", id, "1", "job.json") : Path.Combine(root, "jobs", id + "--1.json"), job);
        return outbox;
    }
    static void WriteFix(string path, JsonObject obj) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, obj.ToJsonString()); }
    static CollectorSettings FixSettings(string root) => new([], []) { ClaudeMailboxRoots = [root] };

    static CollectorSettings Settings(string root) =>
        new(Array.Empty<string>(), Array.Empty<string>())
        {
            MailboxRoots = new[] { root },
            QuietSeconds = 120,
            HistoryHours = 72
        };

    static string Json(string path) => "\"" + path.Replace("\\", "\\\\") + "\"";

    static void WriteAdapterOwner(string stateRoot, string adapterId, int pid, long ticks)
    {
        var dir = Path.Combine(stateRoot, "jobs", adapterId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "owner.json"),
            "{\"pid\":" + pid + ",\"start_time_utc_ticks\":" + ticks + ",\"started_at_utc\":\"2026-09-20T00:00:00Z\"}");
    }

    static void WriteRegistration(string jobs, string name, string? adapterId, string? stateRoot, string? extra)
    {
        Directory.CreateDirectory(jobs);
        var body = "{\"project\":\"fixture\",\"job_id\":\"" + name + "\",\"attempt_id\":\"1\",\"harness\":\"grok-cli\",\"key\":\"" + name + "--1\"";
        if (adapterId is not null)
            body += ",\"adapter_job_id\":\"" + adapterId + "\"";
        if (stateRoot is not null)
            body += ",\"adapter_state_root\":" + Json(stateRoot);
        body += extra + "}";
        File.WriteAllText(Path.Combine(jobs, name + "--1.json"), body);
    }

    static (ObservedProcess Wrapper, ObservedProcess Grok) LivePromptProcesses(string prompt)
    {
        var command = "grok.exe --cwd C:\\work\\demo --prompt-file " + prompt + " --resume cccccccc-cccc-cccc-cccc-cccccccccccc";
        var wrapper = new ObservedProcess(1001, 1, 111, DateTimeOffset.Parse("2026-09-20T00:00:00Z"), "pwsh.exe", null, "pwsh");
        var grok = new ObservedProcess(2002, 1001, 222, DateTimeOffset.Parse("2026-09-20T00:00:01Z"), "grok.exe",
            @"C:\Users\x\.grok\bin\grok.exe", command);
        return (wrapper, grok);
    }

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
