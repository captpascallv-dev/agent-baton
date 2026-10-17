// SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Nodes;
using CliWatch.Collection;
using CliWatch.Contracts;
using CliWatch.Normalization;
using CliWatch.Projection;
using CliWatch.Views;
using Xunit;

namespace CliWatch.Tests;

public sealed class CliWatchNativePiClaudeTests
{
    const string Sentinel = "BODY_SENTINEL_claude_do_not_store";
    const string Earendil = @"C:\pkg\node_modules\@earendil-works\pi-coding-agent\dist\bundle\cli.js";
    const string Mario = @"C:\pkg\node_modules\@mariozechner\pi-coding-agent\dist\bundle\cli.js";
    const string Mixed = @"C:\Users\Example\AppData\Roaming\npm/node_modules/@earendil-works/pi-coding-agent/dist/bundle/cli.js";

    [Fact]
    public async Task Node_pi_entry_is_visible_and_prompt_node_shell_are_not()
    {
        using var dir = new TempDir();
        var codex = Path.Combine(dir.Path, "codex");
        Directory.CreateDirectory(Path.Combine(codex, "jobs"));
        var ear = Proc(46636, 46636, "node.exe", "\"C:\\Program Files\\nodejs\\node.exe\" \"" + Mixed + "\"");
        var old = Proc(46637, 46637, "node.exe", Node(Mario));
        var prompt = Proc(46638, 46638, "node.exe", Node(@"C:\work\worker.js", "please inspect @earendil-works/pi-coding-agent/dist/bundle/cli.js"));
        var eval = Proc(46639, 46639, "node.exe", "node.exe -e \"console.log('@earendil-works/pi-coding-agent/dist/bundle/cli.js')\"");
        var shell = Proc(6800, 6800, "pwsh.exe", "pwsh.exe -File helper.ps1 @" + Earendil + " @" + Mario + " --model gpt-6.1-sol --thinking xhigh --session-dir C:\\s");
        var snapshot = await Project(new FakeSnapshot(new[] { ear, old, prompt, eval, shell }), Watch(codex, null, dir.Path));

        var unbound = Assert.Single(snapshot.Projects, p => p.Phase == CliWatchStatus.Unbound);
        Assert.Equal("未关联任务", unbound.Name);
        Assert.Equal(2, unbound.WorkItems.Count);
        Assert.Contains(unbound.WorkItems, w => w.Summary.Contains("46636", StringComparison.Ordinal));
        Assert.Contains(unbound.WorkItems, w => w.Summary.Contains("46637", StringComparison.Ordinal));
        Assert.DoesNotContain(unbound.WorkItems, w => w.Summary.Contains("46638", StringComparison.Ordinal));
        Assert.DoesNotContain(unbound.WorkItems, w => w.Summary.Contains("6800", StringComparison.Ordinal));
        Assert.All(unbound.WorkItems, w => Assert.Contains("启动入口", string.Join(" ", w.Artifacts.Select(a => a.Label)), StringComparison.Ordinal));

        var hud = HudPresentation.Build(snapshot, null);
        var row = Assert.Single(hud.Projects, p => p.ProjectId == unbound.Id);
        Assert.Contains("46636", string.Join(" ", unbound.WorkItems.Select(w => w.Summary)), StringComparison.Ordinal);
        Assert.Contains("|", row.MetaLine, StringComparison.Ordinal);
        var details = DetailsPresentation.Build(snapshot, unbound.Id, UiLang.Zh);
        Assert.Contains("cli.js", details.DiagnosticsText, StringComparison.Ordinal);
        Assert.Contains("未知/限制", details.DiagnosticsText, StringComparison.Ordinal);
        Assert.DoesNotContain(Sentinel, details.DiagnosticsText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exact_pi_contract_binds_and_registration_does_not_borrow_another_model()
    {
        using var dir = new TempDir();
        var codex = Path.Combine(dir.Path, "codex");
        var jobs = Path.Combine(codex, "jobs");
        var outbox = Path.Combine(dir.Path, "out-pi");
        Directory.CreateDirectory(jobs);
        Directory.CreateDirectory(outbox);
        File.WriteAllText(Path.Combine(jobs, "demo-project-pi--1.json"),
            "{" +
            "\"project\":\"Demo Project\",\"job_id\":\"demo-project-pi\",\"attempt_id\":\"1\",\"key\":\"demo-project-pi--1\"," +
            "\"harness\":\"pi\",\"task_summary\":\"原生绑定\"," +
            "\"registered_at\":\"2026-09-30T00:00:00Z\"," +
            "\"execution_model\":\"gpt-6.1-sol\",\"reasoning_effort\":\"xhigh\"," +
            "\"outbox\":" + Q(outbox) + "}");
        File.WriteAllText(Path.Combine(jobs, "other-grok--1.json"),
            "{" +
            "\"project\":\"other\",\"job_id\":\"other-grok\",\"attempt_id\":\"1\",\"key\":\"other-grok--1\"," +
            "\"harness\":\"grok-cli\",\"task_summary\":\"别人的默认\"," +
            "\"registered_at\":\"2026-09-30T00:00:00Z\"," +
            "\"outbox\":" + Q(Path.Combine(dir.Path, "out-grok")) + "," +
            "\"adapter_job_id\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\"," +
            "\"adapter_state_root\":" + Q(Path.Combine(dir.Path, "adapter")) + "}");
        var adapter = Path.Combine(dir.Path, "adapter", "jobs", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        Directory.CreateDirectory(adapter);
        File.WriteAllText(Path.Combine(adapter, "request.json"),
            "{\"job_id\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"model\":\"grok-4.6\",\"reasoning_effort\":\"xhigh\"}");
        File.WriteAllText(Path.Combine(jobs, "registered-only--1.json"),
            "{" +
            "\"project\":\"Demo Project\",\"job_id\":\"registered-only\",\"attempt_id\":\"1\",\"key\":\"registered-only--1\"," +
            "\"harness\":\"pi\",\"task_summary\":\"只登记\"," +
            "\"registered_at\":\"2026-09-30T00:00:00Z\"," +
            "\"execution_model\":\"gpt-6.1-sol\",\"reasoning_effort\":\"xhigh\"," +
            "\"outbox\":" + Q(Path.Combine(dir.Path, "out-reg")) + "}");

        var contract = "\n项目：Demo Project；job_id：demo-project-pi；attempt_id：1\n唯一交付目录：" + outbox.Replace('\\', '/') + "\n";
        var pi = Proc(47001, 47001, "node.exe", Node(Earendil, "--model gpt-6.1-sol --thinking xhigh --session-dir C:\\sessions\\pi") + contract);
        var worker = Proc(47002, 47002, "node.exe", Node(@"C:\work\worker.js", "项目：Demo Project") );
        var snapshot = await Project(new FakeSnapshot(new[] { pi, worker }), Watch(codex, null, dir.Path));

        var bound = Assert.Single(snapshot.Projects, p => p.Id == "demo-project-pi--1");
        Assert.True(CliWatchStatus.IsRunning(bound.Phase));
        var work = Assert.Single(bound.WorkItems);
        Assert.Equal("gpt-6.1-sol（已观察）", work.Model);
        Assert.Equal("xhigh（已观察）", work.Effort);
        Assert.DoesNotContain("grok-4.6", work.Model, StringComparison.Ordinal);
        Assert.Contains("cli.js", string.Join(" ", work.Artifacts.Select(a => a.Target?.Target)), StringComparison.Ordinal);
        Assert.DoesNotContain(snapshot.Projects, p => p.Phase == CliWatchStatus.Unbound && p.WorkItems.Any(w => w.Summary.Contains("47001", StringComparison.Ordinal)));

        var registered = Assert.Single(snapshot.Projects, p => p.Id == "registered-only--1");
        Assert.Equal(CliWatchStatus.Registered, registered.Phase);
        Assert.Equal("gpt-6.1-sol（登记指定）", Assert.Single(registered.WorkItems).Model);
        Assert.Equal("xhigh（登记指定）", Assert.Single(registered.WorkItems).Effort);
        Assert.DoesNotContain("grok-4.6", Assert.Single(registered.WorkItems).Model ?? "", StringComparison.Ordinal);

        var grok = Assert.Single(snapshot.Projects, p => p.Id == "other-grok--1");
        Assert.Equal("grok-4.6", Assert.Single(grok.WorkItems).Model);
        Assert.Equal("xhigh", Assert.Single(grok.WorkItems).Effort);
        var hud = HudPresentation.Build(snapshot, null, showHistory: true);
        var regRow = Assert.Single(hud.Projects, p => p.ProjectId == registered.Id);
        Assert.Contains("登记指定", regRow.MetaLine, StringComparison.Ordinal);
        var grokRow = Assert.Single(hud.Projects, p => p.ProjectId == grok.Id);
        Assert.DoesNotContain("登记指定", grokRow.MetaLine, StringComparison.Ordinal);
        Assert.DoesNotContain("已观察", grokRow.MetaLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reused_pi_pid_stays_unbound()
    {
        using var dir = new TempDir();
        var codex = Path.Combine(dir.Path, "codex");
        var jobs = Path.Combine(codex, "jobs");
        var adapter = Path.Combine(dir.Path, "adapter", "jobs", "pi-old");
        Directory.CreateDirectory(jobs);
        Directory.CreateDirectory(adapter);
        File.WriteAllText(Path.Combine(jobs, "old-pi--1.json"),
            "{" +
            "\"project\":\"Demo Project\",\"job_id\":\"old-pi\",\"attempt_id\":\"1\",\"key\":\"old-pi--1\"," +
            "\"harness\":\"pi\",\"registered_at\":\"2026-09-29T00:00:00Z\"," +
            "\"execution_model\":\"gpt-6.1-sol\",\"reasoning_effort\":\"xhigh\"," +
            "\"adapter_state_root\":" + Q(Path.Combine(dir.Path, "adapter")) + "," +
            "\"adapter_job_id\":\"pi-old\"," +
            "\"outbox\":" + Q(Path.Combine(dir.Path, "out-old")) + "}");
        File.WriteAllText(Path.Combine(adapter, "owner.json"),
            "{\"pid\":46636,\"start_time_utc_ticks\":111,\"started_at_utc\":\"2026-09-29T00:00:00Z\"}");
        var reused = Proc(46636, 222, "node.exe", Node(Earendil));
        var snapshot = await Project(new FakeSnapshot(new[] { reused }), Watch(codex, null, dir.Path));

        var job = Assert.Single(snapshot.Projects, p => p.Id == "old-pi--1");
        Assert.False(CliWatchStatus.IsRunning(job.Phase));
        Assert.Equal("gpt-6.1-sol（登记指定）", Assert.Single(job.WorkItems).Model);
        var unbound = Assert.Single(snapshot.Projects, p => p.Phase == CliWatchStatus.Unbound);
        Assert.Contains(unbound.WorkItems, w => w.Summary.Contains("46636", StringComparison.Ordinal));
        Assert.Null(Assert.Single(unbound.WorkItems).Model);
    }

    [Fact]
    public async Task Claude_reads_grok_pi_cursor_without_collapsing_executor_or_source()
    {
        using var dir = new TempDir();
        var (codex, claude) = Roots(dir.Path);
        var state = Path.Combine(dir.Path, "state");
        WriteClaude(claude, "live-cursor", "1", ClaudeJob(
            "live-cursor", "示例项目", "隔离游标链", "cursor", "composer-2.5", "high",
            state, Path.Combine(dir.Path, "out-live"), 7000, "2026-09-30T03:00:00+08:00"));
        var liveAdapter = Path.Combine(state, "jobs", "live-cursor");
        Directory.CreateDirectory(liveAdapter);
        File.WriteAllText(Path.Combine(liveAdapter, "owner.json"),
            "{\"pid\":7000,\"start_time_utc_ticks\":7000,\"started_at_utc\":\"2026-09-30T03:00:00Z\"}");
        File.WriteAllText(Path.Combine(liveAdapter, "request.json"),
            "{\"job_id\":\"live-cursor\",\"model\":\"composer-2.5\",\"reasoning_effort\":\"high\"}");

        WriteClaude(claude, "wrap-cursor", "1", ClaudeJob(
            "wrap-cursor", "示例项目", "包装未证实", "cursor", "composer-2.5", "high",
            state, Path.Combine(dir.Path, "out-wrap"), 7100, "2026-09-30T02:00:00+08:00"));
        Directory.CreateDirectory(Path.Combine(state, "jobs", "wrap-cursor"));
        File.WriteAllText(Path.Combine(state, "jobs", "wrap-cursor", "owner.json"),
            "{\"pid\":7100,\"start_time_utc_ticks\":7100,\"started_at_utc\":\"2026-09-30T02:00:00Z\"}");

        WriteClaude(claude, "exit-cursor", "1", ClaudeJob(
            "exit-cursor", "示例项目", "退出无交卷", "cursor", "composer-2.5", "high",
            state, Path.Combine(dir.Path, "out-exit"), 0, "2026-09-30T01:00:00+08:00"));
        Directory.CreateDirectory(Path.Combine(state, "jobs", "exit-cursor"));
        File.WriteAllText(Path.Combine(state, "jobs", "exit-cursor", "receipt.json"),
            "{\"job_id\":\"exit-cursor\",\"transport_complete\":true}");

        var readyOut = Path.Combine(dir.Path, "out-ready");
        Directory.CreateDirectory(readyOut);
        WriteClaude(claude, "ready-cursor", "1", ClaudeJob(
            "ready-cursor", "示例项目", "交付待Lead", "cursor", "composer-2.5", "high",
            state, readyOut, 0, "2026-09-30T05:00:00+08:00"));
        File.WriteAllText(Path.Combine(readyOut, "READY.json"),
            "{\"status\":\"completed\",\"deliverables\":[\"REPORT.md\"],\"notes\":\"" + Sentinel + "\"}");
        File.WriteAllText(Path.Combine(readyOut, "REPORT.md"), Sentinel);
        File.WriteAllText(Path.Combine(readyOut, "DISPATCH.txt"), Sentinel);

        var acceptOut = Path.Combine(dir.Path, "out-accept");
        Directory.CreateDirectory(acceptOut);
        WriteClaude(claude, "accept-cursor", "1", ClaudeJob(
            "accept-cursor", "示例项目", "有验收记录", "cursor", "composer-2.5", "high",
            state, acceptOut, 0, "2026-09-30T00:30:00+08:00"));
        File.WriteAllText(Path.Combine(acceptOut, "READY.json"), "{\"status\":\"completed\",\"deliverables\":[\"REPORT.md\"]}");
        File.WriteAllText(Path.Combine(acceptOut, "ACCEPTANCE.md"), Sentinel);

        foreach (var (id, task, status, when) in new[]
                 {
                     ("blocked-cursor", "阻塞", "blocked", "2026-09-30T00:40:00+08:00"),
                     ("failed-cursor", "失败", "failed", "2026-09-30T00:50:00+08:00")
                 })
        {
            var outbox = Path.Combine(dir.Path, "out-" + id);
            Directory.CreateDirectory(outbox);
            WriteClaude(claude, id, "1", ClaudeJob(id, "示例项目", task, "cursor", "composer-2.5", "high", state, outbox, 0, when));
            File.WriteAllText(Path.Combine(outbox, "READY.json"), "{\"status\":\"" + status + "\",\"deliverables\":[]}");
        }

        WriteClaude(claude, "grok-job", "1", ClaudeJob(
            "grok-job", "示例项目", "采集与去重脚本 v1", "grok", "grok-4.7", "xhigh",
            state, Path.Combine(dir.Path, "out-grok"), 39076, "2026-09-30T04:00:00+08:00"));
        WriteClaude(claude, "pi-job", "1", ClaudeJob(
            "pi-job", "示例项目", "同行档案 r1", "pi", "gpt-6-sol", "xhigh",
            state, Path.Combine(dir.Path, "out-pi"), 44240, "2026-09-30T03:30:00+08:00", "openai-codex"));
        WriteClaude(claude, "custom-job", "1", ClaudeJob(
            "custom-job", "示例项目", "未知执行器", "custom-runner", "some-model", "medium",
            state, Path.Combine(dir.Path, "out-custom"), 0, "2026-09-30T03:20:00+08:00"));

        var reuseOut = Path.Combine(dir.Path, "out-reuse");
        Directory.CreateDirectory(reuseOut);
        WriteClaude(claude, "reuse-cursor", "1", ClaudeJob(
            "reuse-cursor", "示例项目", "重用PID", "cursor", "composer-2.5", "high",
            state, reuseOut, 8000, "2026-09-29T01:00:00+08:00"));
        Directory.CreateDirectory(Path.Combine(state, "jobs", "reuse-cursor"));
        File.WriteAllText(Path.Combine(state, "jobs", "reuse-cursor", "owner.json"),
            "{\"pid\":8000,\"start_time_utc_ticks\":111,\"started_at_utc\":\"2026-09-29T01:00:00Z\"}");

        File.WriteAllText(Path.Combine(codex, "jobs", "shared-cursor--1.json"),
            "{" +
            "\"project\":\"示例项目\",\"job_id\":\"live-cursor\",\"attempt_id\":\"1\",\"key\":\"live-cursor--1\"," +
            "\"harness\":\"cursor-cli\",\"task_summary\":\"另一来源\"," +
            "\"registered_at\":\"2026-09-30T03:00:00Z\"," +
            "\"outbox\":" + Q(Path.Combine(dir.Path, "out-codex")) + "}");

        File.WriteAllText(Path.Combine(claude, "jobs", "flat.json"), "{\"task\":\"" + Sentinel + "\",\"job_id\":\"flat\"}");
        Directory.CreateDirectory(Path.Combine(claude, "jobs", "deep-job", "1", "extra"));
        File.WriteAllText(Path.Combine(claude, "jobs", "deep-job", "1", "extra", "job.json"), "{\"task\":\"" + Sentinel + "\"}");
        File.WriteAllText(Path.Combine(claude, "jobs", "live-cursor", "1", "prompt.txt"), Sentinel);

        var wrapper = Proc(7000, 7000, "pwsh.exe", "pwsh.exe -File C:\\launch\\process_file_host.ps1");
        var child = Proc(7001, 7001, "node.exe", "node.exe C:\\tools\\cursor-agent\\index.js", 7000);
        var wrapOnly = Proc(7100, 7100, "pwsh.exe", "pwsh.exe -File C:\\launch\\process_file_host.ps1");
        var reused = Proc(8000, 222, "cursor-agent.exe", "cursor-agent.exe");
        var (batch, snapshot) = await Collect(new FakeSnapshot(new[] { wrapper, child, wrapOnly, reused }), Watch(codex, claude, dir.Path));
        var packed = batch.Documents.Aggregate(string.Empty, (acc, doc) => acc + doc.Data.ToJsonString());
        Assert.DoesNotContain(Sentinel, packed, StringComparison.Ordinal);
        Assert.DoesNotContain(snapshot.Issues, i => (i.Detail ?? string.Empty).Contains(Sentinel, StringComparison.Ordinal));

        var live = Find(snapshot, "隔离游标链");
        Assert.True(CliWatchStatus.IsRunning(live.Phase));
        Assert.Contains("Claude Lead发起", live.Name, StringComparison.Ordinal);
        Assert.Contains("示例项目", live.Name, StringComparison.Ordinal);
        var liveWork = Assert.Single(live.WorkItems);
        Assert.Equal("Claude Lead发起 · Cursor", liveWork.ActorName);
        Assert.Equal("composer-2.5（已观察）", liveWork.Model);
        Assert.Equal("high（已观察）", liveWork.Effort);
        Assert.Equal("隔离游标链", live.Goal);

        var wrapped = Find(snapshot, "包装未证实");
        Assert.Equal(CliWatchStatus.StartUnconfirmed, wrapped.Phase);
        Assert.Contains("包装器在，CLI 未证实", wrapped.Summary, StringComparison.Ordinal);

        var exited = Find(snapshot, "退出无交卷");
        Assert.Equal(CliWatchStatus.ExecutorFinished, exited.Phase);
        Assert.Contains("未见交付", exited.Summary, StringComparison.Ordinal);

        var ready = Find(snapshot, "交付待Lead");
        Assert.Equal(CliWatchStatus.Delivered, ready.Phase);
        Assert.False(ready.IsActive);
        var readyDetails = DetailsPresentation.Build(snapshot, ready.Id, UiLang.Zh);
        Assert.Contains("已交回", readyDetails.WhoDoingWhat, StringComparison.Ordinal);
        Assert.DoesNotContain("未验收", readyDetails.WhoDoingWhat, StringComparison.Ordinal);
        Assert.DoesNotContain("PASS", ready.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("待", ready.Summary, StringComparison.Ordinal);

        var accepted = Find(snapshot, "有验收记录");
        Assert.Equal(CliWatchStatus.Delivered, accepted.Phase);
        Assert.False(accepted.IsActive);
        Assert.Contains(CliWatchStatus.AcceptanceUnverifiedZh, accepted.Summary, StringComparison.Ordinal);
        var acceptedDetails = DetailsPresentation.Build(snapshot, accepted.Id, UiLang.Zh);
        Assert.Contains(CliWatchStatus.AcceptanceUnverifiedZh, acceptedDetails.WhoDoingWhat, StringComparison.Ordinal);
        Assert.Contains(CliWatchStatus.AcceptanceUnverifiedZh, acceptedDetails.DiagnosticsText, StringComparison.Ordinal);
        Assert.DoesNotContain("PASS", acceptedDetails.OverallJudgment, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("unknown", Assert.Single(accepted.WorkItems).Axes.Acceptance);

        Assert.Equal(CliWatchStatus.ReportedFailure, Find(snapshot, "阻塞").Phase);
        Assert.Equal(CliWatchStatus.ReportedFailure, Find(snapshot, "失败").Phase);

        var grok = Find(snapshot, "采集与去重脚本 v1");
        Assert.Equal("Claude Lead发起 · Grok", Assert.Single(grok.WorkItems).ActorName);
        Assert.Equal("grok-4.7（登记指定）", Assert.Single(grok.WorkItems).Model);
        Assert.Equal("xhigh（登记指定）", Assert.Single(grok.WorkItems).Effort);

        var pi = Find(snapshot, "同行档案 r1");
        Assert.Equal("Claude Lead发起 · PI", Assert.Single(pi.WorkItems).ActorName);
        Assert.DoesNotContain("Codex", Assert.Single(pi.WorkItems).ActorName ?? "", StringComparison.Ordinal);
        Assert.Equal("gpt-6-sol（登记指定）", Assert.Single(pi.WorkItems).Model);

        var custom = Find(snapshot, "未知执行器");
        Assert.Contains("custom-runner", Assert.Single(custom.WorkItems).ActorName, StringComparison.Ordinal);
        Assert.DoesNotContain("Grok", Assert.Single(custom.WorkItems).ActorName ?? "", StringComparison.Ordinal);

        var reuse = Find(snapshot, "重用PID");
        Assert.False(CliWatchStatus.IsRunning(reuse.Phase));
        var unbound = Assert.Single(snapshot.Projects, p => p.Phase == CliWatchStatus.Unbound);
        Assert.Contains(unbound.WorkItems, w => w.Summary.Contains("8000", StringComparison.Ordinal) && w.ActorName == "Cursor");

        var codexRow = Assert.Single(snapshot.Projects, p => p.Goal == "另一来源");
        Assert.DoesNotContain("Claude Lead发起", codexRow.Name, StringComparison.Ordinal);
        Assert.Contains("示例项目", codexRow.Name, StringComparison.Ordinal);
        Assert.NotEqual(live.Id, codexRow.Id);
        Assert.Equal("Cursor", Assert.Single(codexRow.WorkItems).ActorName);
        Assert.Equal(CliWatchStatus.Registered, codexRow.Phase);

        var hud = HudPresentation.Build(snapshot, live.Id);
        var liveHud = Assert.Single(hud.Projects, p => p.ProjectId == live.Id);
        Assert.Contains("Claude Lead发起", liveHud.MetaLine, StringComparison.Ordinal);
        Assert.Contains("示例项目", liveHud.ProjectLabel, StringComparison.Ordinal);
        Assert.Contains("job.json", liveHud.MetaLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pi_option_boundary_keeps_real_model_and_drops_prompt_text()
    {
        using var dir = new TempDir();
        var codex = Path.Combine(dir.Path, "codex");
        Directory.CreateDirectory(Path.Combine(codex, "jobs"));
        var real = Proc(48001, 48001, "node.exe", Node(Earendil, "--model gpt-6.1-sol --thinking xhigh --session-dir C:\\sessions\\pi"));
        var afterTerminator = Proc(48002, 48002, "node.exe", Node(Earendil, "-- \"--model=gpt-6-astra\" \"--thinking=max\""));
        var promptValue = Proc(48003, 48003, "node.exe", Node(Earendil, "--append-system-prompt \"--model=gpt-6-astra\""));
        var providerDialect = Proc(48004, 48004, "node.exe", Node(Earendil, "--model openai/gpt-6.1-sol --thinking xhigh"));
        var colonDialect = Proc(48005, 48005, "node.exe", Node(Earendil, "--model sonnet:high"));
        var printThenReal = Proc(48006, 48006, "node.exe", Node(Earendil, "-p \"please --model gpt-6-astra\" --model gpt-6.1-sol --thinking xhigh"));
        var extensionValue = Proc(48007, 48007, "node.exe", Node(Earendil, "-e \"--model=gpt-6-astra\" --thinking xhigh"));
        var systemPrompt = Proc(48008, 48008, "node.exe", Node(Earendil, "--system-prompt \"--thinking=max\" --model gpt-6.1-sol"));
        var equalsForm = Proc(48009, 48009, "node.exe", Node(Earendil, "--model=gpt-6-astra --thinking=max"));
        var promptFile = Proc(48010, 48010, "node.exe", Node(Earendil, "--prompt-file C:\\prompts\\task.txt --model gpt-6.1-sol --thinking xhigh"));
        var lastWins = Proc(48011, 48011, "node.exe", Node(Earendil, "--model first-model --thinking low --model gpt-6.1-sol --thinking xhigh"));
        var ordinary = Proc(48012, 48012, "node.exe", Node(@"C:\work\worker.js", "please --model gpt-6-astra --thinking max"));
        var (batch, snapshot) = await Collect(new FakeSnapshot(new[]
        {
            real, afterTerminator, promptValue, providerDialect, colonDialect, printThenReal,
            extensionValue, systemPrompt, equalsForm, promptFile, lastWins, ordinary
        }), Watch(codex, null, dir.Path));

        var realWork = UnboundWork(snapshot, 48001);
        Assert.Equal("gpt-6.1-sol（已观察）", realWork.Model);
        Assert.Equal("xhigh（已观察）", realWork.Effort);
        Assert.Equal(true, UnboundData(batch, 48001)["has_session_dir"]?.GetValue<bool>());

        var terminatorWork = UnboundWork(snapshot, 48002);
        Assert.Null(terminatorWork.Model);
        Assert.Null(terminatorWork.Effort);
        Assert.DoesNotContain("astra", terminatorWork.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cli.js", string.Join(" ", terminatorWork.Artifacts.Select(a => a.Target?.Target)), StringComparison.Ordinal);

        var promptWork = UnboundWork(snapshot, 48003);
        Assert.Null(promptWork.Model);
        Assert.Null(promptWork.Effort);

        Assert.Equal("openai/gpt-6.1-sol（已观察）", UnboundWork(snapshot, 48004).Model);
        Assert.Equal("xhigh（已观察）", UnboundWork(snapshot, 48004).Effort);
        Assert.Equal("sonnet:high（已观察）", UnboundWork(snapshot, 48005).Model);
        Assert.Null(UnboundWork(snapshot, 48005).Effort);
        Assert.Equal("gpt-6.1-sol（已观察）", UnboundWork(snapshot, 48006).Model);
        Assert.Equal("xhigh（已观察）", UnboundWork(snapshot, 48006).Effort);
        Assert.Null(UnboundWork(snapshot, 48007).Model);
        Assert.Equal("xhigh（已观察）", UnboundWork(snapshot, 48007).Effort);
        Assert.Equal("gpt-6.1-sol（已观察）", UnboundWork(snapshot, 48008).Model);
        Assert.Null(UnboundWork(snapshot, 48008).Effort);
        Assert.Null(UnboundWork(snapshot, 48009).Model);
        Assert.Null(UnboundWork(snapshot, 48009).Effort);

        var promptFileWork = UnboundWork(snapshot, 48010);
        Assert.Equal("gpt-6.1-sol（已观察）", promptFileWork.Model);
        Assert.Equal("xhigh（已观察）", promptFileWork.Effort);
        Assert.Equal(true, UnboundData(batch, 48010)["has_prompt_file"]?.GetValue<bool>());
        Assert.Equal("gpt-6.1-sol（已观察）", UnboundWork(snapshot, 48011).Model);
        Assert.Equal("xhigh（已观察）", UnboundWork(snapshot, 48011).Effort);
        Assert.DoesNotContain(snapshot.Projects.SelectMany(p => p.WorkItems), w => w.Summary.Contains("48012", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Native_pi_canonical_attachment_binds_without_borrowing_registration()
    {
        using var dir = new TempDir();
        var notes = Path.Combine(dir.Path, "notes.txt");
        File.WriteAllText(notes, "notes-not-dispatch");
        var canon = WriteAttachmentJob(dir.Path, "job-canon", "b1", "registered-default-model");
        var printed = WriteAttachmentJob(dir.Path, "job-print", "b2", "print-registered", "omit");
        var dashed = WriteAttachmentJob(dir.Path, "job-dash", "b3", "dash-registered");
        WriteAttachmentJob(dir.Path, "job-sibling", "b4", "sibling-only-model");
        var early = WriteAttachmentJob(dir.Path, "job-early", "b5", "early-model");
        var mismatch = WriteAttachmentJob(dir.Path, "job-mismatch", "b6", "mismatch-model", notes);
        var registered = DateTimeOffset.Parse("2026-09-30T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
        var canonOut = Path.GetDirectoryName(canon)!;
        var real = Proc(49301, 49301, "node.exe", Node(Earendil,
            "--mode json --print --provider openai-codex --model gpt-6.1-sol --thinking xhigh --tools read,bash,write,grep,find,ls --session-dir C:\\sessions\\not-the-workspace --approve " + At(notes) + " " + At(canon)));
        var printAt = Proc(49302, 49302, "node.exe", Node(Earendil, "--print " + At(printed) + " --model gpt-6.1-sol --thinking xhigh"));
        var afterDash = Proc(49303, 49303, "node.exe", Node(Earendil, "--model gpt-6.1-sol --thinking xhigh -- \"--model=gpt-6-astra\" " + At(dashed)));
        var ordinary = Proc(49304, 49304, "node.exe", Node(@"C:\work\worker.js", At(canon)));
        var systemValue = Proc(49305, 49305, "node.exe", Node(Earendil, "--model gpt-6.1-sol --thinking xhigh --system-prompt " + At(canon)));
        var modeValue = Proc(49306, 49306, "node.exe", Node(Earendil, "--mode " + At(canon) + " --model gpt-6.1-sol --thinking xhigh"));
        var positional = Proc(49307, 49307, "node.exe", Node(Earendil, "-- \"" + canon + "\""));
        var outboxDir = Proc(49308, 49308, "node.exe", Node(Earendil, At(canonOut)));
        var wrongFile = Proc(49309, 49309, "node.exe", Node(Earendil, At(notes)));
        var relative = Proc(49310, 49310, "node.exe", Node(Earendil, "--session-dir \"" + canonOut + "\" @DISPATCH.txt"));
        var tooEarly = Named(49311, registered.UtcTicks, "node.exe", "node.exe", Node(Earendil, "--model gpt-6.1-sol --thinking xhigh " + At(early)), registered);
        var wrongDeclaration = Proc(49312, 49312, "node.exe", Node(Earendil, "--model other-observed --thinking low " + At(mismatch)));
        var store = Path.Combine(dir.Path, "store");
        var (batch, snapshot) = await Collect(new FakeSnapshot(new[]
        {
            real, printAt, afterDash, ordinary, systemValue, modeValue, positional, outboxDir, wrongFile, relative, tooEarly, wrongDeclaration
        }), Watch(Path.Combine(dir.Path, "codex"), null, dir.Path), store);

        var canonRow = Assert.Single(snapshot.Projects, p => p.Id == "job-canon--1");
        Assert.True(CliWatchStatus.IsRunning(canonRow.Phase));
        var canonWork = Assert.Single(canonRow.WorkItems);
        Assert.Equal("gpt-6.1-sol（已观察）", canonWork.Model);
        Assert.Equal("xhigh（已观察）", canonWork.Effort);
        Assert.Contains("registered-default-model", canonWork.Blocker, StringComparison.Ordinal);
        Assert.Contains("不互相顶替", canonWork.Blocker, StringComparison.Ordinal);
        var canonBind = Assert.Single(batch.Documents, d => d.Kind == MailboxCollector.KindProcessBind && JobKey(d) == "job-canon--1");
        Assert.Equal("prompt_file_request_identity", canonBind.Data["bind_how"]?.GetValue<string>());
        Assert.Equal(49301, canonBind.Data["cli_pid"]?.GetValue<int>());
        Assert.Equal(false, canonBind.Data["command_mentions_registered_job_id"]?.GetValue<bool>());
        Assert.Equal(false, canonBind.Data["has_prompt_file"]?.GetValue<bool>());
        Assert.Equal(true, canonBind.Data["has_session_dir"]?.GetValue<bool>());
        Assert.DoesNotContain(batch.Documents, d => d.Kind == MailboxCollector.KindAdapterRequest && JobKey(d) == "job-canon--1");

        var printRow = Assert.Single(snapshot.Projects, p => p.Id == "job-print--1");
        Assert.True(CliWatchStatus.IsRunning(printRow.Phase));
        Assert.Equal("gpt-6.1-sol（已观察）", Assert.Single(printRow.WorkItems).Model);
        var dashWork = Assert.Single(Assert.Single(snapshot.Projects, p => p.Id == "job-dash--1").WorkItems);
        Assert.True(CliWatchStatus.IsRunning(Assert.Single(snapshot.Projects, p => p.Id == "job-dash--1").Phase));
        Assert.Equal("gpt-6.1-sol（已观察）", dashWork.Model);
        Assert.DoesNotContain("astra", dashWork.Model ?? "", StringComparison.OrdinalIgnoreCase);

        var sibling = Assert.Single(snapshot.Projects, p => p.Id == "job-sibling--1");
        Assert.Equal(CliWatchStatus.Registered, sibling.Phase);
        Assert.Equal("sibling-only-model（登记指定）", Assert.Single(sibling.WorkItems).Model);
        Assert.DoesNotContain("gpt-6.1-sol", Assert.Single(sibling.WorkItems).Model ?? "", StringComparison.Ordinal);
        Assert.Equal(CliWatchStatus.Registered, Assert.Single(snapshot.Projects, p => p.Id == "job-early--1").Phase);
        Assert.Equal("early-model（登记指定）", Assert.Single(Assert.Single(snapshot.Projects, p => p.Id == "job-early--1").WorkItems).Model);
        var mismatchRow = Assert.Single(snapshot.Projects, p => p.Id == "job-mismatch--1");
        Assert.Equal(CliWatchStatus.Registered, mismatchRow.Phase);
        Assert.Equal("mismatch-model（登记指定）", Assert.Single(mismatchRow.WorkItems).Model);
        Assert.DoesNotContain("other-observed", Assert.Single(mismatchRow.WorkItems).Model ?? "", StringComparison.Ordinal);

        foreach (var pid in new[] { 49305, 49306, 49307, 49308, 49309, 49310, 49311, 49312 })
            UnboundWork(snapshot, pid);
        Assert.Null(UnboundWork(snapshot, 49310).Model);
        foreach (var pid in new[] { 49301, 49302, 49303, 49304 })
            Assert.DoesNotContain(snapshot.Projects.SelectMany(p => p.WorkItems), w => w.Summary.Contains("PID " + pid + " ", StringComparison.Ordinal));

        var storeText = File.ReadAllText(Path.Combine(store, "prompt-file-bindings.json"));
        Assert.Contains("\"pi_file_attachment\":true", storeText, StringComparison.Ordinal);
        Assert.Contains(Sha(canon), storeText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PI_DISPATCH_BODY_SENTINEL", storeText, StringComparison.Ordinal);
        Assert.DoesNotContain("not-the-workspace", storeText, StringComparison.Ordinal);
        Assert.DoesNotContain("--approve", storeText, StringComparison.Ordinal);
        foreach (var doc in batch.Documents)
            Assert.DoesNotContain("PI_DISPATCH_BODY_SENTINEL", doc.Data.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pi_attachment_ambiguity_stays_unbound()
    {
        using var dir = new TempDir();
        var a = WriteAttachmentJob(dir.Path, "job-a", "ba", "ambiguity-model");
        var b = WriteAttachmentJob(dir.Path, "job-b", "bb", "ambiguity-model");
        var c = WriteAttachmentJob(dir.Path, "job-c", "bc", "ambiguity-model");
        var both = Proc(49411, 49411, "node.exe", Node(Earendil, "--model gpt-6-astra --thinking max " + At(a) + " " + At(b)));
        var one = Proc(49412, 49412, "node.exe", Node(Earendil, "--model gpt-6-astra --thinking max " + At(c)));
        var two = Proc(49413, 49413, "node.exe", Node(Earendil, "--model gpt-6-astra --thinking max " + At(c)));
        var snapshot = await Project(new FakeSnapshot(new[] { both, one, two }), Watch(Path.Combine(dir.Path, "codex"), null, dir.Path));
        foreach (var id in new[] { "job-a--1", "job-b--1", "job-c--1" })
        {
            var row = Assert.Single(snapshot.Projects, p => p.Id == id);
            Assert.Equal(CliWatchStatus.Registered, row.Phase);
            Assert.Equal("ambiguity-model（登记指定）", Assert.Single(row.WorkItems).Model);
            Assert.DoesNotContain("gpt-6-astra", Assert.Single(row.WorkItems).Model ?? "", StringComparison.Ordinal);
        }

        foreach (var pid in new[] { 49411, 49412, 49413 })
        {
            var work = UnboundWork(snapshot, pid);
            Assert.Equal("gpt-6-astra（已观察）", work.Model);
            Assert.Equal("PI", work.ActorName);
        }
    }

    [Fact]
    public async Task Saved_pi_attachment_change_reuse_exit_and_unavailable()
    {
        using var changedDir = new TempDir();
        var changedStore = Path.Combine(changedDir.Path, "store");
        var changedDispatch = WriteAttachmentJob(changedDir.Path, "job-changed", "bx", "registered-default-model");
        var changedPi = Proc(49421, 49421, "node.exe", Node(Earendil, "--model gpt-6.1-sol --thinking xhigh " + At(changedDispatch)));
        var changedSettings = Watch(Path.Combine(changedDir.Path, "codex"), null, changedDir.Path);
        var changedFirst = await Collect(new FakeSnapshot(new[] { changedPi }), changedSettings, changedStore);
        Assert.True(CliWatchStatus.IsRunning(changedFirst.Snapshot.Projects.Single(p => p.Id == "job-changed--1").Phase));
        var originalHash = Sha(changedDispatch);
        var bytes = File.ReadAllBytes(changedDispatch);
        bytes[0] ^= 0x01;
        File.WriteAllBytes(changedDispatch, bytes);
        var changedNext = await Collect(new FakeSnapshot(new[] { changedPi }), changedSettings, changedStore);
        var changedRow = changedNext.Snapshot.Projects.Single(p => p.Id == "job-changed--1");
        Assert.Equal(CliWatchStatus.Unknown, changedRow.Phase);
        Assert.False(CliWatchStatus.IsRunning(changedRow.Phase));
        UnboundWork(changedNext.Snapshot, 49421);
        var changedStoreText = File.ReadAllText(Path.Combine(changedStore, "prompt-file-bindings.json"));
        Assert.Contains(originalHash, changedStoreText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Sha(changedDispatch), changedStoreText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PI_DISPATCH_BODY_SENTINEL", changedStoreText, StringComparison.Ordinal);

        using var reuseDir = new TempDir();
        var reuseStore = Path.Combine(reuseDir.Path, "store");
        var reuseDispatch = WriteAttachmentJob(reuseDir.Path, "job-reused", "br", "registered-default-model");
        var reusePi = Proc(49422, 49422, "node.exe", Node(Earendil, "--model gpt-6.1-sol --thinking xhigh " + At(reuseDispatch)));
        var reuseSettings = Watch(Path.Combine(reuseDir.Path, "codex"), null, reuseDir.Path);
        await Collect(new FakeSnapshot(new[] { reusePi }), reuseSettings, reuseStore);
        var reused = reusePi with
        {
            StartTicks = reusePi.StartTicks + 1,
            CommandLine = Node(Earendil, "--model gpt-6-astra --thinking max " + At(reuseDispatch))
        };
        var reuseSnap = await Collect(new FakeSnapshot(new[] { reused }), reuseSettings, reuseStore);
        var reuseRow = reuseSnap.Snapshot.Projects.Single(p => p.Id == "job-reused--1");
        Assert.Equal(CliWatchStatus.ProcessGone, reuseRow.Phase);
        Assert.False(CliWatchStatus.IsRunning(reuseRow.Phase));
        Assert.Equal("registered-default-model（登记指定）", Assert.Single(reuseRow.WorkItems).Model);
        Assert.DoesNotContain("gpt-6-astra", Assert.Single(reuseRow.WorkItems).Model ?? "", StringComparison.Ordinal);
        Assert.Equal("gpt-6-astra（已观察）", UnboundWork(reuseSnap.Snapshot, 49422).Model);

        using var exitDir = new TempDir();
        var exitStore = Path.Combine(exitDir.Path, "store");
        var exitDispatch = WriteAttachmentJob(exitDir.Path, "job-exit", "be", "registered-default-model");
        var exitPi = Proc(49423, 49423, "node.exe", Node(Earendil, "--model gpt-6.1-sol --thinking xhigh " + At(exitDispatch)));
        var exitSettings = Watch(Path.Combine(exitDir.Path, "codex"), null, exitDir.Path);
        await Collect(new FakeSnapshot(new[] { exitPi }), exitSettings, exitStore);
        var exited = await Collect(new FakeSnapshot(Array.Empty<ObservedProcess>()), exitSettings, exitStore);
        var exitRow = exited.Snapshot.Projects.Single(p => p.Id == "job-exit--1");
        Assert.Equal(CliWatchStatus.ProcessGone, exitRow.Phase);
        Assert.Equal("registered-default-model（登记指定）", Assert.Single(exitRow.WorkItems).Model);
        Assert.DoesNotContain(exited.Snapshot.Projects, p => p.Phase == CliWatchStatus.Unbound);

        using var missingDir = new TempDir();
        var missingStore = Path.Combine(missingDir.Path, "store");
        var missingDispatch = WriteAttachmentJob(missingDir.Path, "job-missing", "bm", "registered-default-model");
        var missingPi = Proc(49424, 49424, "node.exe", Node(Earendil, "--model gpt-6.1-sol --thinking xhigh " + At(missingDispatch)));
        var missingSettings = Watch(Path.Combine(missingDir.Path, "codex"), null, missingDir.Path);
        await Collect(new FakeSnapshot(new[] { missingPi }), missingSettings, missingStore);
        var missing = await Collect(new FakeSnapshot(Array.Empty<ObservedProcess>(), unknownMissing: true), missingSettings, missingStore);
        var missingRow = missing.Snapshot.Projects.Single(p => p.Id == "job-missing--1");
        Assert.Equal(CliWatchStatus.Unknown, missingRow.Phase);
        Assert.NotEqual(CliWatchStatus.ExecutorFinished, missingRow.Phase);
        Assert.False(CliWatchStatus.IsRunning(missingRow.Phase));
    }

    [Fact]
    public async Task Claude_code_stays_visible_and_desktop_processes_do_not()
    {
        using var dir = new TempDir();
        var codex = Path.Combine(dir.Path, "codex");
        Directory.CreateDirectory(Path.Combine(codex, "jobs"));
        const string code = @"C:\Users\Example\AppData\Roaming\Claude\claude-code\2.1.284\claude.exe";
        const string desktop = @"C:\Users\Example\AppData\Local\AnthropicClaude\app-2.16120.0\claude.exe";
        const string unknown = @"C:\Tools\claude.exe";
        var real = Named(49501, 49501, "claude.exe", code, QuoteExe(code));
        var quoted = Named(49508, 49508, "claude.exe", code, QuoteExe(code, "--print \"AnthropicClaude --type=renderer gpu-process\""));
        var quotedWord = Named(49509, 49509, "claude.exe", code, QuoteExe(code, "--print \"crashpad-handler\""));
        var ordinaryPrint = Named(49514, 49514, "claude.exe", code, QuoteExe(code, "--print \"ordinary task\""));
        var afterTerminator = Named(49515, 49515, "claude.exe", code, QuoteExe(code, "--print -- --type=renderer"));
        var systemPrompt = Named(49516, 49516, "claude.exe", code, QuoteExe(code, "--append-system-prompt \"--type=renderer\" --print \"ordinary task\""));
        var realRole = Named(49517, 49517, "claude.exe", code, QuoteExe(code, "--type=renderer"));
        var main = Named(49502, 49502, "claude.exe", desktop, QuoteExe(desktop));
        var renderer = Named(49503, 49503, "claude.exe", desktop, QuoteExe(desktop, "--type=renderer"));
        var gpu = Named(49504, 49504, "claude.exe", desktop, QuoteExe(desktop, "--type gpu-process"));
        var utility = Named(49505, 49505, "claude.exe", desktop, QuoteExe(desktop, "--type=utility"));
        var crashType = Named(49506, 49506, "claude.exe", desktop, QuoteExe(desktop, "--type=crashpad-handler"));
        var crashFile = Named(49507, 49507, "claude.exe", code, QuoteExe(code, "\"" + @"C:\Users\Example\AppData\Local\AnthropicClaude\app-2.16120.0\crashpad-handler.exe" + "\""));
        var unknownProc = Named(49510, 49510, "claude.exe", unknown, QuoteExe(unknown));
        var desktopMention = Named(49511, 49511, "claude.exe", desktop, QuoteExe(desktop, "\"" + code + "\""));
        var unknownMention = Named(49512, 49512, "claude.exe", unknown, QuoteExe(unknown, "\"" + code + "\""));
        var mcp = Named(49513, 49513, "claude.exe", code, QuoteExe(code, "mcp-server"));
        var snapshot = await Project(new FakeSnapshot(new[]
        {
            real, quoted, quotedWord, ordinaryPrint, afterTerminator, systemPrompt, realRole, main, renderer, gpu, utility, crashType, crashFile, unknownProc, desktopMention, unknownMention, mcp
        }), Watch(codex, null, dir.Path));

        var unbound = Assert.Single(snapshot.Projects, p => p.Phase == CliWatchStatus.Unbound);
        foreach (var pid in new[] { 49501, 49508, 49509, 49514, 49515, 49516 })
        {
            var work = Assert.Single(unbound.WorkItems, w => w.Summary.Contains("PID " + pid + " ", StringComparison.Ordinal));
            Assert.Equal("Claude", work.ActorName);
        }

        Assert.Equal(6, unbound.WorkItems.Count);
        foreach (var pid in new[] { 49502, 49503, 49504, 49505, 49506, 49507, 49510, 49511, 49512, 49513, 49517 })
            Assert.DoesNotContain(unbound.WorkItems, w => w.Summary.Contains("PID " + pid + " ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Directory_and_attempt_conflicts_keep_registration_and_do_not_borrow_delivery()
    {
        using var dir = new TempDir();
        var (codex, claude) = Roots(dir.Path);
        var state = Path.Combine(dir.Path, "state");
        var stolenJob = Path.Combine(dir.Path, "stolen-outbox-b");
        var stolenAttempt = Path.Combine(dir.Path, "stolen-outbox-attempt");
        Directory.CreateDirectory(stolenJob);
        Directory.CreateDirectory(stolenAttempt);
        File.WriteAllText(Path.Combine(stolenJob, "READY.json"),
            "{\"status\":\"completed\",\"deliverables\":[\"STOLEN_DELIVERY_EVIDENCE_B\"],\"notes\":\"STOLEN_DELIVERY_EVIDENCE_B\"}");
        File.WriteAllText(Path.Combine(stolenJob, "ACCEPTANCE.md"), "STOLEN_DELIVERY_EVIDENCE_B");
        File.WriteAllText(Path.Combine(stolenAttempt, "READY.json"),
            "{\"status\":\"completed\",\"deliverables\":[\"STOLEN_ATTEMPT_EVIDENCE\"],\"notes\":\"STOLEN_ATTEMPT_EVIDENCE\"}");
        File.WriteAllText(Path.Combine(stolenAttempt, "ACCEPTANCE.md"), "STOLEN_ATTEMPT_EVIDENCE");
        foreach (var adapterId in new[] { "directory-job-A", "declared-job-B" })
        {
            var adapter = Path.Combine(state, "jobs", adapterId);
            Directory.CreateDirectory(adapter);
            File.WriteAllText(Path.Combine(adapter, "request.json"),
                "{\"job_id\":" + Q(adapterId) + ",\"model\":\"observed-b-model\",\"reasoning_effort\":\"max\"}");
            File.WriteAllText(Path.Combine(adapter, "owner.json"),
                "{\"pid\":4242,\"start_time_utc_ticks\":4242,\"job_id\":" + Q(adapterId) + "}");
        }

        var attemptAdapter = Path.Combine(state, "jobs", "attempt-dir-job");
        Directory.CreateDirectory(attemptAdapter);
        File.WriteAllText(Path.Combine(attemptAdapter, "request.json"),
            "{\"job_id\":\"attempt-dir-job\",\"model\":\"observed-attempt-model\",\"reasoning_effort\":\"max\"}");

        WriteClaude(claude, "directory-job-A", "1",
            "{" +
            "\"job_id\":\"declared-job-B\",\"attempt\":1,\"project\":\"示例项目\",\"task\":\"different-job-B\"," +
            "\"executor\":\"grok\",\"model\":\"registered-a-model\",\"effort\":\"xhigh\"," +
            "\"state_root\":" + Q(state) + ",\"outbox\":" + Q(stolenJob) + "," +
            "\"dispatched_at\":\"2026-09-30T01:00:00+08:00\",\"prompt\":" + Q(Sentinel) + "}");
        WriteClaude(claude, "attempt-dir-job", "1",
            "{" +
            "\"job_id\":\"attempt-dir-job\",\"attempt\":9,\"project\":\"示例项目\",\"task\":\"attempt目录冲突\"," +
            "\"executor\":\"pi\",\"model\":\"registered-attempt-model\",\"effort\":\"xhigh\"," +
            "\"state_root\":" + Q(state) + ",\"outbox\":" + Q(stolenAttempt) + "," +
            "\"dispatched_at\":\"2026-09-30T02:00:00+08:00\",\"prompt\":" + Q(Sentinel) + "}");

        WriteLegal(dir.Path, claude, state, "legal-grok", "grok", "grok-4.7", "合法Grok", "2026-09-30T06:00:00+08:00");
        WriteLegal(dir.Path, claude, state, "legal-pi", "pi", "gpt-6.1-sol", "合法PI", "2026-09-30T04:00:00+08:00");
        WriteLegal(dir.Path, claude, state, "legal-cursor", "cursor", "composer-2.5", "合法Cursor", "2026-09-30T03:00:00+08:00");

        var (batch, snapshot) = await Collect(new FakeSnapshot(Array.Empty<ObservedProcess>()), Watch(codex, claude, dir.Path));
        var packed = batch.Documents.Aggregate(string.Empty, (acc, doc) => acc + doc.Data.ToJsonString());
        Assert.DoesNotContain("STOLEN_DELIVERY_EVIDENCE_B", packed, StringComparison.Ordinal);
        Assert.DoesNotContain("STOLEN_ATTEMPT_EVIDENCE", packed, StringComparison.Ordinal);
        Assert.DoesNotContain("observed-b-model", packed, StringComparison.Ordinal);
        Assert.DoesNotContain("observed-attempt-model", packed, StringComparison.Ordinal);
        Assert.False(Borrowed(batch, "directory-job-A--1"));
        Assert.False(Borrowed(batch, "attempt-dir-job--1"));
        Assert.Contains(batch.Documents, d => d.Kind == MailboxCollector.KindOutboxReady && JobKey(d) == "legal-grok--1");

        var conflict = Assert.Single(snapshot.Projects, p => p.Id == "directory-job-A--1");
        Assert.Equal("different-job-B", conflict.Goal);
        Assert.Equal(CliWatchStatus.Unknown, conflict.Phase);
        Assert.Equal(DataQuality.Conflicting, conflict.Quality);
        Assert.False(conflict.IsActive);
        Assert.DoesNotContain("已交付", conflict.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(CliWatchStatus.AcceptanceUnverifiedZh, conflict.Summary, StringComparison.Ordinal);
        var conflictWork = Assert.Single(conflict.WorkItems);
        Assert.Equal(DataQuality.Conflicting, conflictWork.Quality);
        Assert.Equal("registered-a-model（登记指定）", conflictWork.Model);
        Assert.Equal("xhigh（登记指定）", conflictWork.Effort);
        Assert.DoesNotContain("已观察", conflictWork.Model ?? "", StringComparison.Ordinal);
        Assert.Contains("目录身份与 job.json 声明不一致", conflictWork.Blocker, StringComparison.Ordinal);
        Assert.Contains("不采用", conflictWork.Blocker, StringComparison.Ordinal);
        Assert.DoesNotContain(conflictWork.Artifacts, a => (a.Target?.Target ?? string.Empty).Contains("stolen-outbox-b", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(conflictWork.Artifacts, a => string.Equals(a.Label, "READY.json", StringComparison.Ordinal));
        Assert.DoesNotContain(conflictWork.Artifacts, a => string.Equals(a.Label, "ACCEPTANCE.md", StringComparison.OrdinalIgnoreCase) || (a.Label ?? string.Empty).Contains("验收", StringComparison.Ordinal));

        var attempt = Assert.Single(snapshot.Projects, p => p.Id == "attempt-dir-job--1");
        Assert.Equal("attempt目录冲突", attempt.Goal);
        Assert.Equal(CliWatchStatus.Unknown, attempt.Phase);
        Assert.Equal(DataQuality.Conflicting, attempt.Quality);
        Assert.False(attempt.IsActive);
        Assert.DoesNotContain("已交付", attempt.Summary, StringComparison.Ordinal);
        var attemptWork = Assert.Single(attempt.WorkItems);
        Assert.Equal(DataQuality.Conflicting, attemptWork.Quality);
        Assert.Equal("registered-attempt-model（登记指定）", attemptWork.Model);
        Assert.Equal("Claude Lead发起 · PI", attemptWork.ActorName);
        Assert.DoesNotContain("observed-attempt-model", attemptWork.Model ?? "", StringComparison.Ordinal);
        Assert.Contains("不采用", attemptWork.Blocker, StringComparison.Ordinal);
        Assert.DoesNotContain(snapshot.Projects, p => p.Id == "attempt-dir-job--9");

        var grok = Find(snapshot, "合法Grok");
        Assert.Equal(CliWatchStatus.Delivered, grok.Phase);
        Assert.Equal(DataQuality.Fresh, grok.Quality);
        Assert.False(grok.IsActive);
        Assert.Equal("Claude Lead发起 · Grok", Assert.Single(grok.WorkItems).ActorName);
        Assert.Equal("grok-4.7（登记指定）", Assert.Single(grok.WorkItems).Model);

        var pi = Find(snapshot, "合法PI");
        Assert.Equal(CliWatchStatus.Delivered, pi.Phase);
        Assert.Equal("Claude Lead发起 · PI", Assert.Single(pi.WorkItems).ActorName);
        Assert.Equal("gpt-6.1-sol（登记指定）", Assert.Single(pi.WorkItems).Model);

        var cursor = Find(snapshot, "合法Cursor");
        Assert.Equal(CliWatchStatus.Delivered, cursor.Phase);
        Assert.Equal("Claude Lead发起 · Cursor", Assert.Single(cursor.WorkItems).ActorName);
        Assert.Equal("composer-2.5（登记指定）", Assert.Single(cursor.WorkItems).Model);

        Assert.Equal(DataQuality.Conflicting, snapshot.Quality);
        var issues = snapshot.Issues.Where(i => i.Code == "claude_directory_identity_conflict").ToList();
        Assert.Contains(issues, i => i.Detail.Contains("job_id", StringComparison.Ordinal) && i.Detail.Contains("不采用", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Detail.Contains("attempt", StringComparison.Ordinal) && i.Detail.Contains("不采用", StringComparison.Ordinal));
        Assert.DoesNotContain(issues, i => (i.Detail ?? string.Empty).Contains("仍按目录身份采集", StringComparison.Ordinal));
        Assert.Equal(5, snapshot.Projects.Count);
    }

    [Fact]
    public async Task Claude_missing_root_bad_json_and_host_pid_stay_limited()
    {
        using var dir = new TempDir();
        var codex = Path.Combine(dir.Path, "codex");
        Directory.CreateDirectory(Path.Combine(codex, "jobs"));
        var missing = Path.Combine(dir.Path, "missing-claude");
        var missingSnapshot = await Project(new FakeSnapshot(Array.Empty<ObservedProcess>()), Watch(codex, missing, dir.Path));
        Assert.Contains(missingSnapshot.Issues, i => i.Code == "mailbox_root_missing");

        var claude = Path.Combine(dir.Path, "claude");
        Directory.CreateDirectory(Path.Combine(claude, "jobs", "bad-id", "1"));
        File.WriteAllText(Path.Combine(claude, "jobs", "bad-id", "1", "job.json"), "{");
        var state = Path.Combine(dir.Path, "state");
        WriteClaude(claude, "host-only", "1", ClaudeJob(
            "host-only", "示例项目", "只有host", "cursor", "composer-2.5", "high",
            state, Path.Combine(dir.Path, "out-host"), 39076, "2026-09-30T01:00:00+08:00"));
        WriteClaude(claude, "conflict-job", "1", ClaudeJob(
            "conflict-job", "示例项目", "身份冲突", "grok", "grok-4.7", "xhigh",
            state, Path.Combine(dir.Path, "out-conflict"), 0, "2026-09-30T02:00:00+08:00"));
        Directory.CreateDirectory(Path.Combine(state, "jobs", "conflict-job"));
        File.WriteAllText(Path.Combine(state, "jobs", "conflict-job", "request.json"),
            "{\"job_id\":\"other-job\",\"model\":\"stolen-model\",\"reasoning_effort\":\"low\"}");

        var snapshot = await Project(new FakeSnapshot(Array.Empty<ObservedProcess>()), Watch(codex, claude, dir.Path));
        Assert.Contains(snapshot.Issues, i => i.Code == "parse_error");
        var host = Find(snapshot, "只有host");
        Assert.Equal(CliWatchStatus.Registered, host.Phase);
        var hostIssue = Assert.Single(snapshot.Issues, i => i.Code == "host_pid_not_cli_identity");
        Assert.DoesNotContain("conflict", hostIssue.Code, StringComparison.OrdinalIgnoreCase);
        var conflict = Find(snapshot, "身份冲突");
        Assert.DoesNotContain("stolen-model", Assert.Single(conflict.WorkItems).Model ?? "", StringComparison.Ordinal);
        Assert.Equal("grok-4.7（登记指定）", Assert.Single(conflict.WorkItems).Model);
        Assert.Equal(DataQuality.Conflicting, snapshot.Quality);
        Assert.Contains(snapshot.Issues, i => i.Code.Contains("conflict", StringComparison.OrdinalIgnoreCase));
    }

    static ProjectView Find(CockpitSnapshot snapshot, string goal) =>
        Assert.Single(snapshot.Projects, p => p.Goal == goal);

    static WorkView UnboundWork(CockpitSnapshot snapshot, int pid)
    {
        var row = Assert.Single(snapshot.Projects, p => p.Phase == CliWatchStatus.Unbound);
        return Assert.Single(row.WorkItems, w => w.Summary.Contains("PID " + pid + " ", StringComparison.Ordinal));
    }

    static JsonObject UnboundData(CollectionBatch batch, int pid)
    {
        var doc = Assert.Single(batch.Documents, d =>
            d.Kind == MailboxCollector.KindUnboundProcess
            && d.Data["pid"] is JsonValue value
            && value.TryGetValue<int>(out var found)
            && found == pid);
        return doc.Data;
    }

    static string? JobKey(RawDocument doc)
    {
        if (doc.Data["job_key"] is JsonValue value && value.TryGetValue<string>(out var key))
            return key;
        return null;
    }

    static bool Borrowed(CollectionBatch batch, string key) =>
        batch.Documents.Any(d =>
            JobKey(d) == key
            && d.Kind is MailboxCollector.KindOutboxReady
                or MailboxCollector.KindAcceptanceRecord
                or MailboxCollector.KindAdapterRequest
                or MailboxCollector.KindAdapterOwner
                or MailboxCollector.KindAdapterReceipt
                or "outbox_file");

    static void WriteLegal(string root, string claude, string state, string id, string executor, string model, string task, string when)
    {
        var outbox = Path.Combine(root, "out-" + id);
        Directory.CreateDirectory(outbox);
        WriteClaude(claude, id, "1", ClaudeJob(id, "示例项目", task, executor, model, "xhigh", state, outbox, 0, when));
        File.WriteAllText(Path.Combine(outbox, "READY.json"), "{\"status\":\"completed\",\"deliverables\":[\"REPORT.md\"]}");
    }

    static async Task<CockpitSnapshot> Project(IProcessSnapshot snapshot, CollectorSettings settings)
    {
        var (_, projected) = await Collect(snapshot, settings);
        return projected;
    }

    static async Task<(CollectionBatch Batch, CockpitSnapshot Snapshot)> Collect(IProcessSnapshot snapshot, CollectorSettings settings, string? store = null)
    {
        var collector = new MailboxCollector(snapshot, promptBindingDirectory: store);
        var batch = await collector.CollectAsync(settings, CancellationToken.None);
        var projected = new CliWatchProjector().Project(new IdentityNormalizer().Normalize(batch));
        return (batch, projected);
    }

    static CollectorSettings Watch(string codex, string? claude, string home) =>
        new(Array.Empty<string>(), Array.Empty<string>())
        {
            MailboxRoots = new[] { codex },
            ClaudeMailboxRoots = string.IsNullOrWhiteSpace(claude) ? Array.Empty<string>() : new[] { claude },
            GrokHome = Path.Combine(home, "empty-grok"),
            QuietSeconds = 120,
            HistoryHours = 72
        };

    static (string Codex, string Claude) Roots(string root)
    {
        var codex = Path.Combine(root, "codex");
        var claude = Path.Combine(root, "claude");
        Directory.CreateDirectory(Path.Combine(codex, "jobs"));
        Directory.CreateDirectory(Path.Combine(claude, "jobs"));
        return (codex, claude);
    }

    static void WriteClaude(string root, string id, string attempt, string json)
    {
        var dir = Path.Combine(root, "jobs", id, attempt);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "job.json"), json);
    }

    static string ClaudeJob(
        string id, string project, string task, string executor, string model, string effort,
        string stateRoot, string outbox, int hostPid, string dispatched, string? provider = null)
    {
        var providerJson = provider is null ? string.Empty : ",\"provider\":" + Q(provider);
        var hostJson = hostPid > 0 ? ",\"host_pid\":" + hostPid.ToString() : string.Empty;
        return "{" +
               "\"job_id\":" + Q(id) + ",\"attempt\":1,\"project\":" + Q(project) + ",\"task\":" + Q(task) + "," +
               "\"executor\":" + Q(executor) + ",\"model\":" + Q(model) + ",\"effort\":" + Q(effort) + "," +
               "\"launcher\":\"fixture\",\"state_root\":" + Q(stateRoot) + ",\"workspace\":" + Q(stateRoot) + "," +
               "\"outbox\":" + Q(outbox) + hostJson + ",\"dispatched_at\":" + Q(dispatched) + "," +
               "\"prompt\":" + Q(Sentinel) + providerJson + "}";
    }

    static string Node(string script, string rest = "")
    {
        var cmd = "\"C:\\Program Files\\nodejs\\node.exe\" \"" + script + "\"";
        return string.IsNullOrWhiteSpace(rest) ? cmd : cmd + " " + rest;
    }

    static string At(string path) => "\"@" + path + "\"";

    static string QuoteExe(string path, string rest = "")
    {
        var cmd = "\"" + path + "\"";
        return string.IsNullOrWhiteSpace(rest) ? cmd : cmd + " " + rest;
    }

    static ObservedProcess Named(int pid, long ticks, string image, string? imagePath, string command, DateTimeOffset? started = null) =>
        new(pid, 1, ticks, started ?? DateTimeOffset.Parse("2026-09-30T06:00:00Z"), image, imagePath, command);

    static string WriteAttachmentJob(string root, string id, string box, string model, string? declared = "dispatch", string registeredAt = "2026-09-30T00:00:00Z")
    {
        var jobs = Path.Combine(root, "codex", "jobs");
        var outbox = Path.Combine(root, "boxes", box);
        var workspace = Path.Combine(root, "ws-" + box);
        var dispatch = Path.Combine(outbox, "DISPATCH.txt");
        Directory.CreateDirectory(jobs);
        Directory.CreateDirectory(outbox);
        Directory.CreateDirectory(workspace);
        File.WriteAllText(dispatch, "PI_DISPATCH_BODY_SENTINEL\n" + id);
        var prompt = declared switch
        {
            "dispatch" => ",\"dispatch_prompt\":" + Q(dispatch),
            "omit" => "",
            _ => ",\"dispatch_prompt\":" + Q(declared ?? "")
        };
        File.WriteAllText(Path.Combine(jobs, id + "--1.json"),
            "{" +
            "\"project\":\"Demo Project\",\"job_id\":" + Q(id) + ",\"attempt_id\":\"1\",\"key\":" + Q(id + "--1") + "," +
            "\"harness\":\"pi-cli\",\"task_summary\":\"attachment\"," +
            "\"registered_at\":" + Q(registeredAt) + "," +
            "\"execution_model\":" + Q(model) + ",\"reasoning_effort\":\"xhigh\"," +
            "\"workspace\":" + Q(workspace) + "," +
            "\"outbox\":" + Q(outbox) + prompt + "}");
        return dispatch;
    }

    static string Sha(string path) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    static string Q(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    static ObservedProcess Proc(int pid, long ticks, string image, string command, int parent = 1) =>
        new(pid, parent, ticks, DateTimeOffset.Parse("2026-09-30T06:00:00Z"), image, image, command);

    sealed class FakeSnapshot : IProcessSnapshot
    {
        readonly IReadOnlyList<ObservedProcess> _live;
        readonly bool _unknownMissing;
        public FakeSnapshot(IReadOnlyList<ObservedProcess> live, bool unknownMissing = false)
        {
            _live = live;
            _unknownMissing = unknownMissing;
        }
        public IReadOnlyList<ObservedProcess> List(CancellationToken cancellationToken) => _live;
        public ProcessIdentityObservation Observe(int pid, long expectedStartTicks)
        {
            var match = _live.FirstOrDefault(p => p.Pid == pid);
            if (match is null)
                return new ProcessIdentityObservation(pid, expectedStartTicks, _unknownMissing ? "unknown" : "dead", DateTimeOffset.UtcNow);
            var state = match.StartTicks == expectedStartTicks ? "alive" : "dead";
            return new ProcessIdentityObservation(pid, expectedStartTicks, state, DateTimeOffset.UtcNow);
        }
    }

    sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cli-watch-pi-qq-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch (Exception) { }
        }
    }
}
