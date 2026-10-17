// SPDX-License-Identifier: MPL-2.0
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CliWatch.Collection;
using CliWatch.Contracts;
using CliWatch.Normalization;
using CliWatch.Projection;
using CliWatch.Views;
using Xunit;

namespace CliWatch.Tests;

public sealed class NativeCurrentTruthTests
{
    [Fact]
    public async Task Declared_native_root_binds_only_the_exact_dispatch_attempt()
    {
        using var f = new Fixture();
        var current = f.NativeAdapter("4");
        f.Job("work", "1", f.Path + "/old-out", current.Control, current.State);
        var s = await f.Project(current.Processes);
        Assert.True(CliWatchStatus.IsRunning(s.Projects.Single(p => p.Id == "work--4").Phase), f.Diagnostic);
        Assert.False(CliWatchStatus.IsRunning(s.Projects.Single(p => p.Id == "work--1").Phase));
        Assert.DoesNotContain(s.Projects, p => p.Phase == CliWatchStatus.Unbound);
    }

    [Theory]
    [InlineData("prompt")]
    [InlineData("receipt-path")]
    [InlineData("request-session")]
    [InlineData("pid-reuse")]
    [InlineData("argv-workspace")]
    public async Task Native_identity_mutations_do_not_claim_an_executing_task(string mutation)
    {
        using var f = new Fixture(); var n = f.NativeAdapter("4");
        if (mutation == "prompt") File.AppendAllText(n.Out + "/DISPATCH.txt", "changed");
        if (mutation == "receipt-path") f.Edit(n.Control + "/DISPATCH_RECEIPT.json", "registered_dispatch", f.Path + "/foreign/DISPATCH.txt");
        if (mutation == "request-session") f.Edit(n.State + "/jobs/" + Fixture.AdapterId + "/request.json", "session_id", Guid.NewGuid().ToString());
        if (mutation == "pid-reuse") n.Processes[0] = n.Processes[0] with { StartTicks = n.Processes[0].StartTicks + 10_000_000 };
        if (mutation == "argv-workspace") n.Processes[1] = n.Processes[1] with { CommandLine = "codex.exe exec --cd C:\\foreign resume " + Fixture.Session };
        var s = await f.Project(n.Processes);
        Assert.False(CliWatchStatus.IsRunning(s.Projects.Single(p => p.Id == "work--4").Phase));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Supervisor_birth_uses_exact_psutil_epoch_and_rejects_reused_pid(bool reused)
    {
        using var f = new Fixture(); var n = f.Supervisor();
        if (reused) n.Processes[0] = n.Processes[0] with { StartTicks = n.Processes[0].StartTicks + 10_000_000 };
        var s = await f.Project(n.Processes); var p = s.Projects.Single(p => p.Id == "work--1");
        Assert.True(reused ? !CliWatchStatus.IsRunning(p.Phase) : CliWatchStatus.IsRunning(p.Phase), f.Diagnostic);
        if (!reused) Assert.Equal("gpt-6-astra", Assert.Single(p.WorkItems).Model);
    }

    [Fact]
    public async Task Explicit_parallel_current_scope_keeps_failure_and_delivery_not_just_latest_project()
    {
        using var f = new Fixture();
        var a = f.Job("branch-a", "1"); var b = f.Job("branch-b", "1"); var old = f.Job("old", "1");
        f.Ready(a, "failed"); f.Ready(b, "completed"); f.Ready(old, "completed");
        f.Scope(new[] { ("branch-a", a), ("branch-b", b) });
        var s = await f.Project();
        Assert.False(s.Projects.Single(p => p.Id == "branch-a--1").IsActive, f.Diagnostic);
        Assert.False(s.Projects.Single(p => p.Id == "branch-b--1").IsActive);
        Assert.False(s.Projects.Single(p => p.Id == "old--1").IsActive);
        Assert.Equal(CliWatchStatus.ReportedFailure, s.Projects.Single(p => p.Id == "branch-a--1").Phase);
    }

    [Theory]
    [InlineData("native_job_id")]
    [InlineData("native_adapter_job_id")]
    public async Task Explicit_native_registration_binds_without_a_fixed_receipt_filename(string field)
    {
        using var f = new Fixture(); var n = f.NativeAdapter("4");
        f.Edit(f.Path + "/mailbox/jobs/work--4.json", field, Fixture.AdapterId);
        File.Delete(n.Control + "/DISPATCH_RECEIPT.json");
        var s = await f.Project(n.Processes);
        Assert.True(CliWatchStatus.IsRunning(s.Projects.Single(p => p.Id == "work--4").Phase), f.Diagnostic);
        Assert.DoesNotContain(s.Projects, p => p.Phase == CliWatchStatus.Unbound);
    }

    [Theory]
    [InlineData("prompt")]
    [InlineData("conflicting-id")]
    [InlineData("session")]
    [InlineData("pid-reuse")]
    public async Task Direct_native_identity_is_not_trusted_without_exact_evidence(string mutation)
    {
        using var f = new Fixture(); var n = f.NativeAdapter("4");
        var job = f.Path + "/mailbox/jobs/work--4.json";
        f.Edit(job, "native_job_id", Fixture.AdapterId);
        File.Delete(n.Control + "/DISPATCH_RECEIPT.json");
        if (mutation == "prompt") File.AppendAllText(n.Out + "/DISPATCH.txt", "changed");
        if (mutation == "conflicting-id") f.Edit(job, "native_adapter_job_id", Guid.NewGuid().ToString());
        if (mutation == "session") f.Edit(job, "native_session_id", Guid.NewGuid().ToString());
        if (mutation == "pid-reuse") n.Processes[0] = n.Processes[0] with { StartTicks = n.Processes[0].StartTicks + 10_000_000 };
        Assert.False(CliWatchStatus.IsRunning((await f.Project(n.Processes)).Projects.Single(p => p.Id == "work--4").Phase));
    }

    [Fact]
    public async Task Delivery_after_scope_publication_cannot_be_hidden_by_old_membership()
    {
        using var f = new Fixture(); var o = f.Job("work", "1");
        f.Scope(Array.Empty<(string, string)>());
        f.Edit(f.ScopeState!, "at", DateTimeOffset.UtcNow.AddMinutes(-2).ToString("o"));
        f.Ready(o, "completed");
        var p = Assert.Single((await f.Project()).Projects);
        Assert.False(p.IsActive, f.Diagnostic);
        Assert.Equal(CliWatchStatus.Delivered, p.Phase);
    }

    [Theory]
    [InlineData(0, "reported_failure")]
    [InlineData(1, "failed")]
    public async Task Returned_business_problem_is_not_a_process_crash(int code, string expected)
    {
        using var f = new Fixture(); var n = f.Supervisor();
        var born = new DateTimeOffset(639264667737943450, TimeSpan.Zero);
        f.Edit(n.Control + "/NATIVE_START.json", "at", born.ToString("o"));
        f.Json(n.Control + "/STARTUP_RECEIPT.json", new JsonObject { ["native_pid"] = 201, ["native_created"] = 1790869973.794345, ["native_session_id"] = Fixture.Session });
        f.Json(n.Control + "/NATIVE_TERMINAL.json", new JsonObject { ["at"] = born.AddMinutes(1).ToString("o"), ["native_exit_code"] = code,
            ["actual_binding_verified"] = true, ["full_dispatch_received"] = true, ["session_id"] = Fixture.Session });
        f.Ready(n.Out, "failed");
        var p = Assert.Single((await f.Project()).Projects);
        Assert.Equal(expected, p.Phase);
        Assert.NotEqual("passed", Assert.Single(p.WorkItems).Axes.Acceptance);
        if (code == 0) Assert.DoesNotContain("异常退出", CliWatchStatus.LabelZh(p.Phase));
    }

    [Theory]
    [InlineData(0, false, "executor_finished")]
    [InlineData(1, false, "failed")]
    [InlineData(0, true, "process_gone")]
    public async Task Supervisor_end_without_READY_requires_the_same_start_and_session(int code, bool foreign, string phase)
    {
        using var f = new Fixture(); var n = f.Supervisor();
        var born = new DateTimeOffset(639264667737943450, TimeSpan.Zero);
        f.Edit(n.Control + "/NATIVE_START.json", "at", born.ToString("o"));
        f.Json(n.Control + "/STARTUP_RECEIPT.json", new JsonObject { ["native_pid"] = 201, ["native_created"] = 1790869973.794345, ["native_session_id"] = Fixture.Session });
        f.Json(n.Control + "/NATIVE_TERMINAL.json", new JsonObject { ["at"] = born.AddMinutes(1).ToString("o"), ["native_exit_code"] = code,
            ["actual_binding_verified"] = true, ["full_dispatch_received"] = true, ["session_id"] = foreign ? Guid.NewGuid().ToString() : Fixture.Session });
        var s = await f.Project(); Assert.Equal(phase, Assert.Single(s.Projects).Phase);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("older-than-registration")]
    public async Task Unverified_or_old_scope_does_not_prune_current_work(string mode)
    {
        using var f = new Fixture(); f.Job("work", "1"); f.Scope(Array.Empty<(string, string)>());
        if (mode == "missing") File.Delete(f.ScopeState!);
        if (mode == "malformed") f.Edit(f.ScopeState!, "current_active_jobs", "wrong-type");
        if (mode == "older-than-registration") f.Edit(f.ScopeState!, "at", DateTimeOffset.UtcNow.AddDays(-2).ToString("o"));
        var row = Assert.Single((await f.Project()).Projects);
        Assert.False(row.IsActive);
        Assert.False(CliWatchStatus.IsTerminal(row.Phase));
    }

    [Fact]
    public async Task Current_scope_is_read_before_historical_json_exhausts_the_budget()
    {
        using var f = new Fixture();
        for (var i = 0; i < 12; i++)
        {
            var id = "old-" + i.ToString("00");
            f.Job(id, "1");
            f.Edit(f.Path + "/mailbox/jobs/" + id + "--1.json", "adapter_receipt", f.Path + "/missing-receipt.json");
        }
        var current = f.Job("zzz-current", "1");
        f.Ready(current, "failed");
        f.Scope(new[] { ("zzz-current", current) });
        var s = await f.Project(maxFiles: 5);
        Assert.True(f.Attempted <= 5, f.Diagnostic);
        Assert.DoesNotContain(s.Issues, i => i.Code == "current_scope_unavailable");
        Assert.Contains(s.Issues, i => i.Code == "scan_budget_exhausted");
        var currentRow = s.Projects.Single(p => p.Id == "zzz-current--1");
        Assert.False(currentRow.IsActive, f.Diagnostic);
        Assert.Equal(CliWatchStatus.ReportedFailure, currentRow.Phase);
        Assert.Contains(s.Projects, p => p.Id.StartsWith("old-") && !p.IsActive);
        Assert.DoesNotContain(s.Projects, p => p.Id.StartsWith("old-") && p.IsActive);
        Assert.Contains(s.Issues, i => i.Code == "optional_missing" && CurrentList.IsHistoricalIssue(i));
        Assert.DoesNotContain(s.Issues, i => i.Code == "optional_missing" && !CurrentList.IsHistoricalIssue(i));
    }

    [Fact]
    public async Task Tight_budget_stays_strict_and_bad_scope_does_not_hide_a_read_job()
    {
        using var stale = new Fixture();
        for (var i = 0; i < 8; i++) stale.Job("old-" + i.ToString("00"), "1");
        stale.Scope(Array.Empty<(string, string)>());
        stale.Edit(stale.ScopeState!, "at", DateTimeOffset.UtcNow.AddDays(-2).ToString("o"));
        var staleView = await stale.Project(maxFiles: 3);
        Assert.True(stale.Attempted <= 3, stale.Diagnostic);
        Assert.Contains(staleView.Issues, i => i.Code == "scan_budget_exhausted");
        Assert.DoesNotContain(staleView.Issues, i => i.Code == "current_scope_unavailable");
        Assert.False(Assert.Single(staleView.Projects).IsActive, stale.Diagnostic);

        using var broken = new Fixture();
        for (var i = 0; i < 8; i++) broken.Job("old-" + i.ToString("00"), "1");
        broken.Scope(Array.Empty<(string, string)>());
        broken.Edit(broken.ScopeState!, "current_active_jobs", "wrong-type");
        var brokenView = await broken.Project(maxFiles: 3);
        Assert.True(broken.Attempted <= 3, broken.Diagnostic);
        Assert.Contains(brokenView.Issues, i => i.Code == "scan_budget_exhausted");
        Assert.Contains(brokenView.Issues, i => i.Code == "current_scope_unavailable");
        Assert.False(Assert.Single(brokenView.Projects).IsActive, broken.Diagnostic);
    }

    [Fact]
    public async Task Live_contract_stays_current_when_history_would_exhaust_the_budget()
    {
        using var f = new Fixture();
        for (var i = 0; i < 10; i++) f.Job("old-" + i.ToString("00"), "1");
        var liveOut = f.Job("live-job", "1");
        f.Scope(Array.Empty<(string, string)>());
        var started = DateTimeOffset.UtcNow.AddMinutes(-1);
        var command = "codex.exe exec\n项目：alpha；job_id：live-job；attempt_id：1\n唯一交付目录："
            + liveOut.Replace('\\', '/') + "\n";
        var process = new ObservedProcess(77, 0, started.UtcTicks, started, "codex.exe", null, command);
        var s = await f.Project(new[] { process }, 3);
        Assert.True(f.Attempted <= 3, f.Diagnostic);
        Assert.Contains(s.Issues, i => i.Code == "scan_budget_exhausted");
        var row = s.Projects.Single(p => p.Id == "live-job--1");
        Assert.True(row.IsActive, f.Diagnostic);
        Assert.True(CliWatchStatus.IsRunning(row.Phase), f.Diagnostic);
        Assert.DoesNotContain(s.Projects, p => p.Id.StartsWith("old-"));
    }

    [Fact]
    public async Task Live_native_execution_overrides_nonmember_scope_and_pinned_display_closeout()
    {
        using var f = new Fixture(); var n = f.NativeAdapter("4"); f.Ready(n.Out, "completed"); f.Closeout("work--4", n.Out);
        f.Scope(Array.Empty<(string, string)>());
        var s = await f.Project(n.Processes);
        Assert.True(s.Projects.Single(p => p.Id == "work--4").IsActive);
        Assert.True(CliWatchStatus.IsRunning(s.Projects.Single(p => p.Id == "work--4").Phase));
    }

    [Theory]
    [InlineData("unchanged", false)]
    [InlineData("registration", true)]
    [InlineData("ready", true)]
    [InlineData("evidence", true)]
    public async Task Display_closeout_archives_only_pinned_ended_observation(string change, bool current)
    {
        using var f = new Fixture(); var o = f.Job("work", "1"); f.Ready(o, "completed"); f.Closeout("work--1", o);
        if (change == "registration") f.Edit(f.Path + "/mailbox/jobs/work--1.json", "task_summary", "new task");
        if (change == "ready") f.Edit(o + "/READY.json", "revision", 2);
        if (change == "evidence") File.AppendAllText(f.Path + "/owner-closeout.md", "changed");
        var p = Assert.Single((await f.Project()).Projects);
        Assert.False(p.IsActive, f.Diagnostic); Assert.Equal(CliWatchStatus.Delivered, p.Phase);
        if (!current) Assert.Contains("收尾证据", p.NextStep, StringComparison.Ordinal);
        else Assert.DoesNotContain("收尾证据", p.NextStep, StringComparison.Ordinal);
        Assert.NotEqual("passed", Assert.Single(p.WorkItems).Axes.Acceptance);
    }

    [Fact]
    public async Task Oversized_historical_receipt_does_not_stale_healthy_live_job()
    {
        using var f = new Fixture(); var n = f.NativeAdapter("4");
        f.Job("old", "1"); f.Edit(f.Path + "/mailbox/jobs/old--1.json", "state", "retired");
        var receipt = f.Path + "/huge/receipt.json"; Directory.CreateDirectory(f.Path + "/huge");
        File.WriteAllText(receipt, new string(' ', 1_048_577));
        f.Edit(f.Path + "/mailbox/jobs/old--1.json", "adapter_receipt", receipt);
        var s = await f.Project(n.Processes);
        Assert.Equal(DataQuality.Fresh, s.Quality);
        var p = s.Projects.Single(p => p.Id == "work--4");
        Assert.Equal(DataQuality.Fresh, p.Quality); Assert.True(CliWatchStatus.IsRunning(p.Phase));
        Assert.Contains(s.Issues, i => i.Code == "too_large" && CurrentList.IsHistoricalIssue(i));
        Assert.DoesNotContain("上次已知", HudPresentation.Build(s, p.Id).BannerMessage ?? "");
    }

    [Fact]
    public async Task Unread_active_receipt_is_partial_current_refresh_not_a_reused_snapshot()
    {
        using var f = new Fixture(); f.Job("work", "1");
        var receipt = f.Path + "/huge/receipt.json"; Directory.CreateDirectory(f.Path + "/huge"); File.WriteAllText(receipt, new string(' ', 1_048_577));
        f.Edit(f.Path + "/mailbox/jobs/work--1.json", "adapter_receipt", receipt);
        var s = await f.Project();
        var row = Assert.Single(s.Projects);
        Assert.False(row.IsActive);
        Assert.Equal(DataQuality.Fresh, s.Quality);
        Assert.Contains(s.Issues, i => i.Code == "too_large" && CurrentList.IsHistoricalIssue(i));
        Assert.Empty(CurrentList.DefaultItems(s));
        var hud = HudPresentation.Build(s, null);
        Assert.Contains("当前没有进行中的 CLI 任务", hud.BannerMessage);
        Assert.DoesNotContain("上次已知", hud.BannerMessage ?? "");
        var issues = StatusLanguage.IssuesSummary(s.Issues, s);
        Assert.Contains("不影响当前执行", issues, StringComparison.Ordinal);
        Assert.DoesNotContain("状态待核实", issues, StringComparison.Ordinal);
        var retained = s with { Quality = DataQuality.LastKnown };
        Assert.Contains("上次已知", HudPresentation.Build(retained, null).BannerMessage);
        Assert.Contains("last known", HudPresentation.Build(retained, null, false, 96, UiLang.En).BannerMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Actual_exec_server_is_not_a_task_but_prompt_words_and_cli_model_are_preserved()
    {
        using var f = new Fixture(); var t = DateTimeOffset.UtcNow.AddMinutes(-1);
        var service = new ObservedProcess(11, 0, t.UtcTicks, t, "codex.exe", null, "codex.exe exec-server --remote pipe --environment-id id");
        var cli = service with { Pid = 12, CommandLine = "codex.exe exec -m gpt-6-astra -c model_reasoning_effort=xhigh \"Explain exec-server\"" };
        var s = await f.Project(new[] { service, cli }); var p = Assert.Single(s.Projects);
        Assert.Equal(CliWatchStatus.Unbound, p.Phase); var w = Assert.Single(p.WorkItems);
        Assert.StartsWith("gpt-6-astra", w.Model); Assert.StartsWith("xhigh", w.Effort);
    }

    [Theory]
    [InlineData("files", "valid")]
    [InlineData("safe_files", "valid")]
    [InlineData("files", "wrong-lead")]
    [InlineData("files", "changed-report")]
    [InlineData("files", "wrong-outbox")]
    [InlineData("files", "new-ready")]
    [InlineData("files", "live")]
    public async Task Lead_handling_requires_original_owner_and_exact_current_report(string shape, string mutation)
    {
        using var f = new Fixture();
        var native = mutation == "live" ? f.NativeAdapter("1") : null;
        var o = native?.Out ?? f.Job("work", "1");
        f.Scope(new[] { ("work", o) });
        var report = o + "/REPORT.md"; File.WriteAllText(report, "Actual bounded delivery, no business acceptance.");
        f.Json(o + "/READY.json", new JsonObject { ["job_id"] = "work", ["attempt_id"] = "1", ["status"] = "completed", ["report_file"] = report });
        var reviews = f.Path + "/lead/review1"; Directory.CreateDirectory(reviews);
        var verification = reviews + "/VERIFIED.json";
        f.Json(verification, new JsonObject { ["at"] = DateTimeOffset.UtcNow.ToString("o"),
            [shape] = new JsonArray(new JsonObject { ["path"] = mutation == "wrong-outbox" ? f.Path + "/other/REPORT.md" : report,
                ["bytes"] = new FileInfo(report).Length, ["sha256"] = Fixture.Hash(report) }) });
        var decided = DateTimeOffset.UtcNow;
        f.Json(reviews + "/LEAD_DISPOSITION.json", new JsonObject { ["at"] = decided.ToString("o"),
            ["accepted_by"] = mutation == "wrong-lead" ? "foreign" : "owner", ["decision"] = "handled, bounded outcome only", ["evidence"] = verification });
        var cfg = JsonNode.Parse(File.ReadAllText(f.ScopeFile!))!.AsObject();
        cfg["sources"]![0]!["lead_review_root"] = f.Path + "/lead"; f.Json(f.ScopeFile!, cfg);
        if (mutation == "changed-report") File.AppendAllText(report, "new result");
        if (mutation == "new-ready") File.SetLastWriteTimeUtc(o + "/READY.json", decided.AddSeconds(2).UtcDateTime);
        var p = Assert.Single((await f.Project(native?.Processes)).Projects);
        Assert.True((mutation == "live") == p.IsActive, f.Diagnostic);
        if (native is null) Assert.Equal(CliWatchStatus.Delivered, p.Phase);
        else Assert.True(CliWatchStatus.IsRunning(p.Phase));
        Assert.NotEqual("passed", Assert.Single(p.WorkItems).Axes.Acceptance);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Verified_launch_without_current_process_or_receipt_is_not_unstarted(bool supervisor)
    {
        using var f = new Fixture();
        _ = supervisor ? f.Supervisor() : f.NativeAdapter("4");
        var p = Assert.Single((await f.Project()).Projects);
        Assert.Equal(CliWatchStatus.ProcessGone, p.Phase);
        Assert.False(p.IsActive);
        Assert.DoesNotContain("失败", CliWatchStatus.LabelZh(p.Phase));
    }

    [Fact]
    public async Task Explicit_adapter_owner_exit_without_receipt_is_visible_as_missing_handback()
    {
        using var f = new Fixture(); var n = f.NativeAdapter("4");
        f.Edit(f.Path + "/mailbox/jobs/work--4.json", "adapter_job_id", Fixture.AdapterId);
        f.Edit(f.Path + "/mailbox/jobs/work--4.json", "adapter_state_root", n.State);
        var p = Assert.Single((await f.Project()).Projects);
        Assert.Equal(CliWatchStatus.ProcessGone, p.Phase);
        Assert.False(p.IsActive);
    }

    [Theory]
    [InlineData("valid", "transport_failed")]
    [InlineData("before-start", "unknown")]
    [InlineData("future", "unknown")]
    [InlineData("delivered", "delivered")]
    public async Task Adapter_transport_failure_is_distinct_from_business_result(string mode, string expected)
    {
        using var f = new Fixture(); var n = f.NativeAdapter("4");
        var at = mode == "before-start" ? DateTimeOffset.UtcNow.AddHours(-1)
            : mode == "future" ? DateTimeOffset.UtcNow.AddHours(1) : DateTimeOffset.UtcNow;
        f.Json(n.State + "/jobs/" + Fixture.AdapterId + "/receipt.json", new JsonObject {
            ["job_id"] = Fixture.AdapterId, ["transport_complete"] = false,
            ["transport_error"] = "Adapter transport failed.", ["completed_at_utc"] = at.ToString("o") });
        if (mode == "delivered") f.Ready(n.Out, "completed");
        var p = Assert.Single((await f.Project()).Projects);
        Assert.Equal(expected, p.Phase);
        Assert.NotEqual("passed", Assert.Single(p.WorkItems).Axes.Acceptance);
        if (mode == "valid") Assert.Equal("failed", Assert.Single(p.WorkItems).Axes.Transport);
    }

    sealed class Fixture : IDisposable
    {
        public const string AdapterId = "99d2db0c-b867-4e68-b2d7-98cf0bad6212", Session = "00000000-0000-0000-0000-000000000004";
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cli-watch-current-truth-test-" + Guid.NewGuid().ToString("N"));
        public string? ScopeFile, ScopeState, CloseoutFile;
        public int Attempted;
        public string Diagnostic = "";
        public Fixture() { Directory.CreateDirectory(Path + "/mailbox/jobs"); }
        public void Json(string p, JsonObject o) { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!); File.WriteAllText(p, o.ToJsonString()); }
        public void Edit(string p, string k, JsonNode? v) { var o = JsonNode.Parse(File.ReadAllText(p))!.AsObject(); o[k] = v; Json(p,o); }
        public static string Hash(string p) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)));
        public string Job(string job, string attempt, string? outbox = null, string? control = null, string? state = null)
        {
            outbox ??= Path + "/out-" + job + "-" + attempt; Directory.CreateDirectory(outbox); File.WriteAllText(outbox + "/DISPATCH.txt", "isolated canonical input");
            Json(Path + "/mailbox/jobs/" + job + "--" + attempt + ".json", new JsonObject { ["project"]="alpha",["job_id"]=job,["attempt_id"]=attempt,
                ["key"]=job+"--"+attempt,["state"]="registered",["harness"]="codex-cli",["target_thread_id"]="owner",["outbox"]=outbox,
                ["workspace"]=Path,["registered_at"]=DateTimeOffset.UtcNow.AddMinutes(-5).ToString("o"),
                ["execution_authority"]=control is null ? null : control+"/AUTHORITY.json",["native_state_root"]=state });
            return outbox;
        }
        public void Ready(string o,string status) => Json(o+"/READY.json",new JsonObject{["status"]=status});
        public sealed record Native(string Out,string Control,string State,ObservedProcess[] Processes);
        public Native NativeAdapter(string attempt)
        {
            var c=Path+"/control-"+attempt;Directory.CreateDirectory(c); var st=Path+"/native";var o=Job("work",attempt,null,c,st);
            var t=DateTimeOffset.UtcNow.AddMinutes(-1);var owner=new ObservedProcess(101,0,t.UtcTicks,t,"pwsh.exe",null,"pwsh.exe launch.ps1");
            var child=new ObservedProcess(102,101,t.AddSeconds(1).UtcTicks,t.AddSeconds(1),"codex.exe",null,"codex.exe exec --cd \""+Path+"\" -m gpt-6.1-sol -c model_reasoning_effort=xhigh resume "+Session);
            var p=o+"/DISPATCH.txt"; var req=new JsonObject{["protocol_version"]="telephone-line-direct-codex-request-v1",["job_id"]=AdapterId,["workspace"]=Path,
                ["model"]="gpt-6.1-sol",["reasoning_effort"]="xhigh",["session_id"]=Session,["prompt"]=new JsonObject{["path"]=p,["bytes"]=new FileInfo(p).Length,["sha256"]=Hash(p)}};
            Json(st+"/jobs/"+AdapterId+"/request.json",req);Json(st+"/jobs/"+AdapterId+"/owner.json",new JsonObject{["pid"]=101,["start_time_utc_ticks"]=t.UtcTicks});
            Json(c+"/DISPATCH_RECEIPT.json",new JsonObject{["native_job_id"]=AdapterId,["native_session_id"]=Session,["registered_dispatch"]=p,["dispatch_bytes"]=new FileInfo(p).Length,["dispatch_sha256"]=Hash(p)});
            return new Native(o,c,st,new[]{owner,child});
        }
        public Native Supervisor()
        {
            var c=Path+"/control";Directory.CreateDirectory(c);var o=Job("work","1",null,c);const long ticks=639264667737943450;
            var t=new DateTimeOffset(ticks,TimeSpan.Zero);var owner=new ObservedProcess(201,0,ticks,t,"node.exe",null,"node.exe codex.js exec");
            var child=new ObservedProcess(202,201,ticks+100000,t.AddMilliseconds(10),"codex.exe",null,"codex.exe exec -C \""+Path+"\" -m gpt-6-astra -c model_reasoning_effort=xhigh -o \""+o+"/FINAL_RESPONSE.md\" -");
            Json(c+"/WORKSPACE.json",new JsonObject{["job_id"]="work",["runtime"]=Path,["outbox"]=o});
            Json(c+"/NATIVE_START.json",new JsonObject{["harness"]="codex-cli",["pid"]=201,["created"]=1790869973.794345,["model"]="gpt-6-astra",["effort"]="xhigh",["prompt_sha256"]=Hash(o+"/DISPATCH.txt")});
            return new Native(o,c,"",new[]{owner,child});
        }
        public void Scope((string Job,string Out)[] members)
        {
            ScopeFile=Path+"/scope-config.json";ScopeState=Path+"/scope-state.json";var a=new JsonArray();foreach(var (j,o) in members)a.Add(new JsonObject{["job_id"]=j,["outbox"]=o});
            Json(ScopeState,new JsonObject{["at"]=DateTimeOffset.UtcNow.ToString("o"),["current_active_jobs"]=a});
            Json(ScopeFile,new JsonObject{["sources"]=new JsonArray(new JsonObject{["project"]="alpha",["mailbox_root"]=Path+"/mailbox",["target_thread_id"]="owner",["path"]=ScopeState})});
        }
        public void Closeout(string key,string o)
        {
            CloseoutFile=Path+"/closeout.json";var evidence=Path+"/owner-closeout.md";File.WriteAllText(evidence,"original owner closeout evidence");
            Json(CloseoutFile,new JsonObject{["records"]=new JsonArray(new JsonObject{["key"]=key,["mailbox_root"]=Path+"/mailbox",
                ["registration_sha256"]=Hash(Path+"/mailbox/jobs/"+key+".json"),["ready_sha256"]=Hash(o+"/READY.json"),["evidence_path"]=evidence,["evidence_sha256"]=Hash(evidence),["recorded_at"]=DateTimeOffset.UtcNow.ToString("o")})});
        }
        public async Task<CockpitSnapshot> Project(ObservedProcess[]? live=null, int? maxFiles=null)
        {
            var batch=await new MailboxCollector(new Snapshot(live??Array.Empty<ObservedProcess>())).CollectAsync(new CollectorSettings(Array.Empty<string>(),Array.Empty<string>(), MaxFilesPerRefresh: maxFiles ?? 512)
                {MailboxRoots=new[]{Path+"/mailbox"},CurrentScopeSourcesFile=ScopeFile,DisplayCloseoutsFile=CloseoutFile},CancellationToken.None);
            Attempted = batch.Documents.First(d => d.Kind == "watch_config").Data["json_files_attempted"]!.GetValue<int>();
            var result = new CliWatchProjector().Project(new IdentityNormalizer().Normalize(batch));
            Diagnostic = string.Join("; ", result.Projects.Select(p => p.Id+":"+p.Phase+":"+p.IsActive))
                + " attempted=" + Attempted
                + " issues=" + string.Join(",", result.Issues.Select(i => i.Code + ":" + i.Detail))
                + " flags=" + string.Join(";", batch.Documents.Where(d=>d.Kind=="mailbox_job"||d.Kind=="process_bind"||d.Kind=="outbox_ready"||d.Kind.StartsWith("observer_lead")).Select(d=>d.Kind+":"+d.Data.ToJsonString()));
            return result;
        }
        public void Dispose() { if(System.IO.Path.GetFullPath(Path).StartsWith(System.IO.Path.GetTempPath(),StringComparison.OrdinalIgnoreCase)) Directory.Delete(Path,true); }
    }
    sealed class Snapshot(ObservedProcess[] live) : IProcessSnapshot
    {
        public IReadOnlyList<ObservedProcess> List(CancellationToken ct)=>live;
        public ProcessIdentityObservation Observe(int pid,long ticks)=>new(pid,ticks,live.Any(p=>p.Pid==pid&&p.StartTicks==ticks)?"alive":"dead",DateTimeOffset.UtcNow);
    }
}
