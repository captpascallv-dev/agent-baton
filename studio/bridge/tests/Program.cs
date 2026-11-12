using System.Text;
using System.Text.Json.Nodes;
using CliWatch.Collection;
using CliWatch.Contracts;

// Synthetic inputs only. Caller supplies a dedicated fixture directory; no real sources.
var fixtureRoot = Path.GetFullPath(args.Single());
Directory.CreateDirectory(fixtureRoot);
if (OperatingSystem.IsMacOS()) StudioReadBoundary.AllowedRoot = fixtureRoot;
await new BoundaryTests(fixtureRoot).Run();

sealed class BoundaryTests(string root)
{
    static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
    int passed;
    public async Task Run()
    {
        await Check("new-change-delete-corrupt-lock-recovery", FreshFiles);
        await Check("same-key-cross-root-and-owner-conflicts", RootIdentity);
        await Check("fresh-process-activity-exit-pid-reuse-and-unknown", ProcessIdentity);
        await Check("exact-native-contract-and-session-classification", Classification);
        await Check("grok-directory-add-delete-and-summary-replace", GrokDirectories);
        await Check("byte-budget-exact-over-and-recovery", ByteBudget);
        Console.WriteLine($"collector_boundary_tests_ok: {passed}/6 groups");
    }
    async Task Check(string name, Func<Task> test)
    {
        await test(); passed++; Console.WriteLine("pass: " + name);
    }

    async Task FreshFiles()
    {
        var home = Path.Combine(root, "fresh");
        var a = Job(home, "a");
        Write(a.Request, Request(a, "alpha"));
        Write(a.Ready, new JsonObject { ["job_id"] = "a", ["attempt_id"] = "1", ["status"] = "completed" });
        var collector = new MailboxCollector(new Snapshot());
        var settings = Settings(home);
        var first = await Collect(collector, settings);
        Require(Text(Doc(first, "adapter_request", "a--1").Data, "model") == "alpha", "first request");
        Require(!InvalidIdentity(Doc(first, "outbox_ready", "a--1").Data), "first READY identity");
        var originalTime = File.GetLastWriteTimeUtc(a.Request);
        var originalBytes = new FileInfo(a.Request).Length;
        Write(a.Request, Request(a, "bravo"));
        File.SetLastWriteTimeUtc(a.Request, originalTime);
        Require(new FileInfo(a.Request).Length == originalBytes, "same-size replacement fixture");
        var b = Job(home, "b");
        var changed = await Collect(collector, settings);
        Require(Text(Doc(changed, "adapter_request", "a--1").Data, "model") == "bravo", "same-size and same-mtime change read fresh");
        Require(changed.Documents.Any(d => d.Kind == "mailbox_job" && Text(d.Data, "job_id") == "b"), "new registration visible next refresh");
        File.Delete(a.Ready); File.Delete(b.Registration);
        var deleted = await Collect(collector, settings);
        Require(!deleted.Documents.Any(d => d.Kind == "outbox_ready" && Text(d.Data, "job_key") == "a--1"), "deleted READY removed");
        Require(!deleted.Documents.Any(d => d.Kind == "mailbox_job" && Text(d.Data, "job_id") == "b"), "deleted registration removed");
        File.WriteAllText(a.Request, "{half-written");
        var corrupt = await Collect(collector, settings);
        Require(Flag(Doc(corrupt, "adapter_request", "a--1").Data, "corrupt"), "half-written metadata isolated");
        Write(a.Request, Request(a, "fresh"));
        using (new FileStream(a.Request, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var locked = await Collect(collector, settings);
            Require(locked.Issues.Any(i => i.Code == "read_failed"), "read failure reported");
            Require(!locked.Documents.Any(d => d.Kind == "adapter_request"), "failed read does not reuse old request");
        }
        var recovered = await Collect(collector, settings);
        Require(Text(Doc(recovered, "adapter_request", "a--1").Data, "model") == "fresh", "read failure and corrupt recovery");
    }

    async Task RootIdentity()
    {
        var left = Path.Combine(root, "roots", "left"); var right = Path.Combine(root, "roots", "right");
        var a = Job(left, "shared"); var b = Job(right, "shared");
        Write(a.Ready, new JsonObject { ["job_id"] = "shared", ["attempt_id"] = "1" });
        Write(b.Ready, new JsonObject { ["job_id"] = "other", ["attempt_id"] = "1" });
        var batch = await Collect(new MailboxCollector(new Snapshot()), Settings(left, right));
        var ready = batch.Documents.Where(d => d.Kind == "outbox_ready").ToArray();
        Require(ready.Length == 2, "same key preserved in both roots");
        Require(!InvalidIdentity(ready.Single(d => SamePath(Text(d.Data, "mailbox_root"), left)).Data), "left root identity isolated");
        Require(InvalidIdentity(ready.Single(d => SamePath(Text(d.Data, "mailbox_root"), right)).Data), "right root conflict retained");
        Write(a.Owner, Owner(a, 1001, 111)); Write(b.Owner, Owner(b, 1001, 111));
        Write(a.Request, Request(a, "alpha")); Write(b.Request, Request(b, "bravo"));
        batch = await Collect(new MailboxCollector(new Snapshot()), Settings(left, right));
        Require(batch.Issues.Count(i => i.Code == "owner_claim_conflict") == 2, "two roots cannot claim one launch");
        Require(batch.Documents.Where(d => d.Kind == "adapter_request").All(d => !Flag(d.Data, "identity_ok")), "owner conflicts invalidate both requests");
    }

    async Task ProcessIdentity()
    {
        var home = Path.Combine(root, "process"); var job = Job(home, "process");
        Write(job.Owner, Owner(job, 1001, 111));
        Write(job.Request, Request(job, "alpha"));
        var wrapper = Process(1001, 1, 111, "pwsh.exe", "pwsh -File process_file_host.ps1", 1);
        var child = Process(2002, 1001, 222, "grok.exe", "grok.exe --model alpha", 10);
        var source = new Snapshot { Live = [wrapper, child] };
        var collector = new MailboxCollector(source); var settings = Settings(home);
        var first = Doc(await Collect(collector, settings), "process_bind", "process--1");
        Require(Flag(first.Data, "cli_alive") && Number(first.Data, "cli_start_ticks") == 222, "new process binds exact identity");
        Require(Text(first.Data, "activity_kind") == "baseline", "first counter frame is baseline");
        source.Live = [wrapper, child with { CpuTimeTicks = 30 }];
        var active = Doc(await Collect(collector, settings), "process_bind", "process--1");
        Require(Text(active.Data, "activity_kind") == "cli_compute", "current CPU change visible");
        var quiet = Doc(await Collect(collector, settings), "process_bind", "process--1");
        Require(Text(quiet.Data, "activity_kind") == "quiet", "unchanged current counters quiet");
        source.Live = [];
        var exited = Doc(await Collect(collector, settings), "process_bind", "process--1");
        Require(!Flag(exited.Data, "cli_alive") && Text(exited.Data, "wrapper_state") == "dead", "exit removes binding");
        source.Live = [wrapper with { StartTicks = 999 }, child with { StartTicks = 333 }];
        var reused = Doc(await Collect(collector, settings), "process_bind", "process--1");
        Require(!Flag(reused.Data, "cli_alive"), "reused owner PID cannot consume old identity");
        source.Live = []; source.Unknown = true;
        var unknown = Doc(await Collect(collector, settings), "process_bind", "process--1");
        Require(Text(unknown.Data, "wrapper_state") == "unknown", "failed identity read stays unknown");
        source.Unknown = false; source.Live = [wrapper, child with { StartTicks = 333, CpuTimeTicks = 50 }];
        var restored = Doc(await Collect(collector, settings), "process_bind", "process--1");
        Require(Flag(restored.Data, "cli_alive") && Number(restored.Data, "cli_start_ticks") == 333, "current new child identity accepted");
        Require(Text(restored.Data, "activity_kind") == "baseline", "PID reuse has no old activity baseline");
    }

    async Task Classification()
    {
        var home = Path.Combine(root, "classification"); var job = Job(home, "native");
        var registration = JsonNode.Parse(File.ReadAllText(job.Registration))!.AsObject();
        registration.Remove("adapter_job_id"); registration.Remove("adapter_state_root");
        registration["resume_native_session_id"] = "session-fixture"; Write(job.Registration, registration);
        var contract = $"grok.exe\n项目：fixture；job_id：native；attempt_id：1\n唯一交付目录：{job.Outbox.Replace('\\', '/')}";
        var source = new Snapshot { Live = [Process(42, 1, 42, "grok.exe", contract, 0)] };
        var collector = new MailboxCollector(source); var settings = Settings(home);
        var native = Doc(await Collect(collector, settings), "process_bind", "native--1");
        Require(Text(native.Data, "bind_how") == "exact_registered_delivery_contract", "native contract preserved");
        source.Live = [Process(43, 1, 43, "grok.exe", "grok.exe --resume session-fixture", 0)];
        var session = Doc(await Collect(collector, settings), "process_bind", "native--1");
        Require(Text(session.Data, "bind_how") == "unclaimed_session" && Number(session.Data, "cli_pid") == 43, "unclaimed session match preserved");
        source.Live = [Process(44, 1, 44, "grok.exe", "grok.exe mcp --resume session-fixture", 0)];
        Require(!Flag(Doc(await Collect(collector, settings), "process_bind", "native--1").Data, "cli_alive"), "MCP role excluded");
        source.Live = [Process(45, 1, 45, "grok.exe", "grok.exe --prompt-file missing.txt --resume session-fixture", 0)];
        Require(!Flag(Doc(await Collect(collector, settings), "process_bind", "native--1").Data, "cli_alive"), "prompt identity cannot use session fallback");
    }

    async Task GrokDirectories()
    {
        var home = Path.Combine(root, "grok-source"); var grok = Path.Combine(root, "grok-home");
        var a = Job(home, "first"); Write(a.Request, Request(a, "alpha", "session-first"));
        var summary = Path.Combine(grok, "sessions", "other-workspace", "session-first", "summary.json");
        Write(summary, new JsonObject { ["current_model_id"] = "alpha", ["last_active_at"] = At.ToString("o") });
        var collector = new MailboxCollector(new Snapshot()); var settings = Settings(home) with { GrokHome = grok };
        Require(Text(Doc(await Collect(collector, settings), "grok_session_activity", "first--1").Data, "current_model_id") == "alpha", "fallback group found");
        var b = Job(home, "second"); Write(b.Request, Request(b, "bravo", "session-second"));
        Write(Path.Combine(grok, "sessions", "new-group", "session-second", "summary.json"), new JsonObject { ["current_model_id"] = "bravo" });
        var originalTime = File.GetLastWriteTimeUtc(summary);
        Write(summary, new JsonObject { ["current_model_id"] = "fresh", ["last_active_at"] = At.ToString("o") });
        File.SetLastWriteTimeUtc(summary, originalTime);
        var changed = await Collect(collector, settings);
        Require(Text(Doc(changed, "grok_session_activity", "first--1").Data, "current_model_id") == "fresh", "summary read every refresh");
        Require(Text(Doc(changed, "grok_session_activity", "second--1").Data, "current_model_id") == "bravo", "new group and session indexed next refresh");
        File.Delete(summary); Directory.Delete(Path.GetDirectoryName(summary)!);
        var removed = Doc(await Collect(collector, settings), "grok_session_activity", "first--1");
        Require(Text(removed.Data, "current_model_id") is null && !Flag(removed.Data, "activity_known"), "deleted session has no cached model or activity");
        Write(summary, new JsonObject { ["current_model_id"] = "again" });
        Require(Text(Doc(await Collect(collector, settings), "grok_session_activity", "first--1").Data, "current_model_id") == "again", "session restored next refresh");
    }

    async Task ByteBudget()
    {
        var home = Path.Combine(root, "budget"); var a = Job(home, "budget");
        var request = Request(a, "alpha"); request["padding"] = "";
        var text = request.ToJsonString();
        request["padding"] = new string('x', 4096 - Encoding.UTF8.GetByteCount(text));
        Write(a.Request, request);
        Require(new FileInfo(a.Request).Length == 4096, "exact limit fixture");
        var collector = new MailboxCollector(new Snapshot()); var settings = Settings(home) with { MaxFileBytes = 4096 };
        Require(Text(Doc(await Collect(collector, settings), "adapter_request", "budget--1").Data, "model") == "alpha", "exact byte limit accepted");
        request["padding"] = Text(request, "padding") + "x"; Write(a.Request, request);
        var over = await Collect(collector, settings);
        Require(over.Issues.Any(i => i.Code == "too_large") && !over.Documents.Any(d => d.Kind == "adapter_request"), "limit plus one rejected");
        Write(a.Request, Request(a, "fresh"));
        Require(Text(Doc(await Collect(collector, settings), "adapter_request", "budget--1").Data, "model") == "fresh", "oversize does not poison next refresh");
    }

    sealed record Fixture(string Id, string Registration, string Outbox, string State, string Owner, string Request, string Ready);
    Fixture Job(string home, string id)
    {
        var state = Path.Combine(home, "state"); var adapter = Path.Combine(state, "jobs", id);
        var outbox = Path.Combine(home, "outbox", id); Directory.CreateDirectory(outbox); Directory.CreateDirectory(adapter);
        var registration = Path.Combine(home, "jobs", id + "--1.json");
        Write(registration, new JsonObject { ["project"] = "fixture", ["job_id"] = id, ["attempt_id"] = "1", ["key"] = id + "--1",
            ["harness"] = "grok-cli", ["outbox"] = outbox, ["workspace"] = home, ["adapter_job_id"] = id,
            ["adapter_state_root"] = state, ["registered_at"] = At.ToString("o") });
        return new(id, registration, outbox, state, Path.Combine(adapter, "owner.json"), Path.Combine(adapter, "request.json"), Path.Combine(outbox, "READY.json"));
    }
    static JsonObject Request(Fixture job, string model, string? session = null) => new() { ["job_id"] = job.Id, ["attempt_id"] = "1", ["model"] = model,
        ["session_id"] = session, ["workspace"] = Path.GetDirectoryName(Path.GetDirectoryName(job.Registration)) };
    static JsonObject Owner(Fixture job, int pid, long ticks) => new() { ["job_id"] = job.Id, ["attempt_id"] = "1", ["pid"] = pid, ["start_time_utc_ticks"] = ticks };
    static CollectorSettings Settings(params string[] roots) => new([], [], MaxFilesPerRefresh: 512)
        { MailboxRoots = roots, GrokHome = Path.Combine(roots[0], "unavailable-fixture-grok-home") };
    static Task<CollectionBatch> Collect(MailboxCollector collector, CollectorSettings settings) => collector.CollectAsync(settings, CancellationToken.None);
    static RawDocument Doc(CollectionBatch batch, string kind, string key) => batch.Documents.Single(d => d.Kind == kind && Text(d.Data, "job_key") == key);
    static string? Text(JsonObject obj, string key) => ObservationIdentity.Text(obj, key);
    static bool Flag(JsonObject obj, string key) => obj[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    static bool InvalidIdentity(JsonObject obj) => obj["identity_ok"] is JsonValue v && v.TryGetValue<bool>(out var b) && !b;
    static long Number(JsonObject obj, string key) => obj[key] is JsonValue v ? v.TryGetValue<long>(out var n) ? n : v.TryGetValue<int>(out var i) ? i : 0 : 0;
    static bool SamePath(string? a, string b) => string.Equals(a?.Replace('\\', '/').TrimEnd('/'), b.Replace('\\', '/').TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    static void Write(string path, JsonObject obj) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, obj.ToJsonString()); }
    static void Require(bool value, string description) { if (!value) throw new InvalidOperationException(description); }
    static ObservedProcess Process(int pid, int parent, long ticks, string image, string command, long cpu) => new(pid, parent, ticks, At.AddSeconds(1), image, null, command, cpu, 0, 0, true);
    sealed class Snapshot : IProcessSnapshot
    {
        public IReadOnlyList<ObservedProcess> Live = [];
        public bool Unknown;
        public IReadOnlyList<ObservedProcess> List(CancellationToken token) => Live;
        public ProcessIdentityObservation Observe(int pid, long ticks) => new(pid, ticks, Unknown ? "unknown" : Live.Any(p => p.Pid == pid && p.StartTicks == ticks) ? "alive" : "dead", DateTimeOffset.UtcNow);
    }
}
