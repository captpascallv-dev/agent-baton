// SPDX-License-Identifier: MPL-2.0
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CliWatch.Collection;
using CliWatch.Contracts;
using CliWatch.Normalization;
using CliWatch.Projection;
using Xunit;

namespace CliWatch.Tests;

public sealed class CliWatchPromptFileTests
{
    const string Sentinel = "PROMPT_TEXT_SHOULD_NOT_PERSIST";
    const string CommandToken = "COMMAND_TOKEN_SHOULD_NOT_PERSIST";
    const string Session = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    const string OtherSession = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";

    [Fact]
    public async Task Prompt_file_without_adapter_fields_binds_and_shelves_older_attempts()
    {
        using var world = new World();
        world.Job("demo-project", "work", "6", world.Registered.AddHours(-2));
        world.Job("demo-project", "work", "7", world.Registered.AddHours(-1));
        world.Job("demo-project", "work", "9", world.Registered.AddHours(2));
        world.Job("demo-project", "work", "4", world.Registered.AddMinutes(-30), owner: "other-lead");
        world.Job("other", "side", "1", world.Registered.AddMinutes(-10), session: Session);
        world.Job("demo-project", "guard-g", "1", world.Registered.AddHours(3), harness: "grok-execution-guard");
        world.Job("demo-project", "guard-c", "1", world.Registered.AddHours(3), harness: "cursor-execution-guard");
        world.Job("demo-project", "guard-p", "1", world.Registered.AddHours(3), harness: "pi-execution-guard");
        world.Job("demo-project", "guard-local", "1", world.Registered.AddHours(3), harness: "local-guard");
        world.Job("demo-project", "guard-exit", "1", world.Registered.AddHours(3), harness: "cursor-exit-guard");
        world.Job("demo-project", "guard-looking-cli", "1", world.Registered.AddHours(3), harness: "local-guard-cli");
        world.Job("demo-project", "titled", "1", world.Registered.AddHours(3), summary: "execution-guard notice is not a role");
        world.Job("demo-project", "plain-guard-word", "1", world.Registered.AddHours(3), harness: "execution-guard");
        var launch = world.Matched("demo-project", "work", "8", world.Registered, model: "grok-4.8");
        world.WriteSession(launch.Workspace, Session);
        var registration = File.ReadAllText(launch.JobFile);
        Assert.DoesNotContain("adapter_job_id", registration, StringComparison.Ordinal);
        Assert.DoesNotContain("adapter_state_root", registration, StringComparison.Ordinal);

        var snap = await world.Project(launch.Wrapper, launch.Cli);
        var current = Assert.Single(snap.Projects, p => p.Id == "work--8");
        Assert.True(current.IsActive);
        Assert.True(CliWatchStatus.IsRunning(current.Phase));
        Assert.Contains("grok-4.8", current.Summary, StringComparison.Ordinal);
        Assert.Contains("xhigh", current.Summary, StringComparison.Ordinal);
        Assert.Contains("最近 CLI 日志", current.Summary, StringComparison.Ordinal);
        foreach (var id in new[] { "work--6", "work--7" })
        {
            var old = Assert.Single(snap.Projects, p => p.Id == id);
            Assert.False(old.IsActive);
            Assert.Equal(CliWatchStatus.Registered, old.Phase);
            Assert.Contains("不代表任务完成", old.NextStep, StringComparison.Ordinal);
        }

        Assert.False(snap.Projects.Single(p => p.Id == "work--9").IsActive);
        Assert.Equal(CliWatchStatus.Registered, snap.Projects.Single(p => p.Id == "work--9").Phase);
        Assert.False(snap.Projects.Single(p => p.Id == "work--4").IsActive);
        var side = snap.Projects.Single(p => p.Id == "side--1");
        Assert.False(side.IsActive);
        Assert.False(CliWatchStatus.IsRunning(side.Phase));
        Assert.False(snap.Projects.Single(p => p.Id == "titled--1").IsActive);
        Assert.False(snap.Projects.Single(p => p.Id == "plain-guard-word--1").IsActive);
        Assert.False(snap.Projects.Single(p => p.Id == "guard-looking-cli--1").IsActive);
        Assert.DoesNotContain(snap.Projects, p => p.Id is "guard-g--1" or "guard-c--1" or "guard-p--1" or "guard-local--1" or "guard-exit--1");

        var store = File.ReadAllText(world.StoreFile);
        Assert.Contains(launch.Cli.StartTicks.ToString(System.Globalization.CultureInfo.InvariantCulture), store, StringComparison.Ordinal);
        Assert.DoesNotContain(Sentinel, store, StringComparison.Ordinal);
        Assert.DoesNotContain(CommandToken, store, StringComparison.Ordinal);
        Assert.DoesNotContain("--prompt-file", store, StringComparison.Ordinal);
        foreach (var doc in world.LastBatch!.Documents)
        {
            var json = doc.Data.ToJsonString();
            Assert.DoesNotContain(Sentinel, json, StringComparison.Ordinal);
            Assert.DoesNotContain(CommandToken, json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Quoted_prompt_path_and_equals_form_still_bind()
    {
        using var quoted = new World();
        var spaced = quoted.Matched("demo-project", "work", "1", quoted.Registered, quote: true, workspace: Path.Combine(quoted.Root, "work space"), stateRoot: Path.Combine(quoted.Root, "job files"));
        var snap = await quoted.Project(spaced.Wrapper, spaced.Cli);
        Assert.True(CliWatchStatus.IsRunning(snap.Projects.Single(p => p.Id == "work--1").Phase));

        using var equals = new World();
        var plain = equals.Matched("demo-project", "work", "1", equals.Registered, commandStyle: "equals");
        var again = await equals.Project(plain.Wrapper, plain.Cli);
        Assert.True(CliWatchStatus.IsRunning(again.Projects.Single(p => p.Id == "work--1").Phase));
        Assert.Contains("grok-4.7", again.Projects.Single(p => p.Id == "work--1").Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("session")]
    [InlineData("time")]
    [InlineData("substring")]
    [InlineData("prefix-flag")]
    [InlineData("outside-dispatch")]
    [InlineData("duplicate")]
    [InlineData("reparse")]
    public async Task Broken_prompt_identity_does_not_bind(string broken)
    {
        using var world = new World();
        var launch = world.Matched("demo-project", "work", "8", world.Registered);
        if (broken == "hash")
        {
            var bytes = File.ReadAllBytes(launch.Dispatch);
            bytes[^1] ^= 0x01;
            File.WriteAllBytes(launch.Dispatch, bytes);
        }
        else if (broken == "session")
        {
            launch = launch with { Cli = launch.Cli with { CommandLine = world.Command(launch, SessionOverride: OtherSession) } };
        }
        else if (broken == "time")
        {
            launch = launch with { Cli = launch.Cli with { StartedAt = world.Registered.AddMinutes(-5) } };
        }
        else if (broken == "substring")
        {
            launch = launch with { Cli = launch.Cli with { CommandLine = "C:\\grok\\grok.exe --cwd \"" + launch.Workspace + "\" --model grok-4.7 --session-id " + Session + " \"note --prompt-file " + launch.Prompt + "\"" } };
        }
        else if (broken == "prefix-flag")
        {
            launch = launch with { Cli = launch.Cli with { CommandLine = "C:\\grok\\grok.exe --cwd \"" + launch.Workspace + "\" --model grok-4.7 --prompt-file-note \"" + launch.Prompt + "\" --session-id " + Session } };
        }
        else if (broken == "outside-dispatch")
        {
            var outside = Path.Combine(world.Root, "elsewhere", "DISPATCH.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
            File.Copy(launch.Dispatch, outside, overwrite: true);
            world.RewriteDispatchPrompt(launch.JobFile, outside);
        }
        else if (broken == "duplicate")
        {
            world.Job("demo-project", "work", "3", world.Registered.AddMinutes(-5), dispatchBytes: File.ReadAllBytes(launch.Dispatch));
        }
        else if (broken == "reparse")
        {
            var link = Path.Combine(world.Root, "link-state");
            world.Junction(link, Path.GetDirectoryName(Path.GetDirectoryName(launch.Prompt))!);
            var viaLink = Path.Combine(link, Path.GetFileName(Path.GetDirectoryName(launch.Prompt))!, "prompt.txt");
            launch = launch with { Cli = launch.Cli with { CommandLine = world.Command(launch, promptOverride: viaLink) } };
        }

        var snap = await world.Project(launch.Wrapper, launch.Cli);
        var row = Assert.Single(snap.Projects, p => p.Id == "work--8");
        Assert.Equal(CliWatchStatus.Registered, row.Phase);
        Assert.False(CliWatchStatus.IsRunning(row.Phase));
        Assert.DoesNotContain("运行 ", row.Summary, StringComparison.Ordinal);
        if (broken == "duplicate")
        {
            var other = Assert.Single(snap.Projects, p => p.Id == "work--3");
            Assert.Equal(CliWatchStatus.Registered, other.Phase);
            Assert.False(other.IsActive);
        }
    }

    [Fact]
    public async Task Live_prompt_file_is_not_hidden_by_a_guard_or_a_wrong_family()
    {
        using var world = new World();
        var launch = world.Matched("demo-project", "work", "2", world.Registered);
        world.Job("demo-project", "guard-g", "1", world.Registered.AddHours(1), harness: "grok-execution-guard");
        world.Job("demo-project", "cursor-job", "1", world.Registered, harness: "cursor-cli");
        var unrelated = new ObservedProcess(9090, 1, 9090, world.Registered.AddMinutes(5), "grok.exe", null, "grok.exe --cwd \"" + launch.Workspace + "\" --model grok-4.7");
        var snap = await world.Project(launch.Wrapper, launch.Cli, unrelated);
        var live = Assert.Single(snap.Projects, p => p.Id == "work--2");
        Assert.True(live.IsActive);
        Assert.True(CliWatchStatus.IsRunning(live.Phase));
        var cursor = Assert.Single(snap.Projects, p => p.Id == "cursor-job--1");
        Assert.False(cursor.IsActive);
        Assert.Equal(CliWatchStatus.Registered, cursor.Phase);
        Assert.DoesNotContain(snap.Projects, p => p.Id == "guard-g--1");
        Assert.Contains(snap.Projects, p => p.Id == "unbound");
    }

    [Fact]
    public async Task Restart_after_exit_keeps_terminal_state_and_history()
    {
        using var world = new World();
        world.Job("demo-project", "work", "6", world.Registered.AddHours(-2));
        world.Job("demo-project", "work", "7", world.Registered.AddHours(-1));
        var launch = world.Matched("demo-project", "work", "8", world.Registered, model: "grok-4.8");
        var running = await world.Project(launch.Wrapper, launch.Cli);
        Assert.True(CliWatchStatus.IsRunning(running.Projects.Single(p => p.Id == "work--8").Phase));
        Assert.False(running.Projects.Single(p => p.Id == "work--6").IsActive);

        File.WriteAllText(Path.Combine(launch.AdapterDir, "receipt.json"),
            "{\"protocol_version\":\"agent-direct-grok-receipt-v1\",\"job_id\":\"" + launch.AdapterJobId
            + "\",\"transport_complete\":true,\"grok_success\":true,\"completed_at_utc\":\"2026-09-21T18:00:00Z\"}");
        var exited = await world.Project();
        var current = Assert.Single(exited.Projects, p => p.Id == "work--8");
        Assert.Equal(CliWatchStatus.ExecutorFinished, current.Phase);
        Assert.False(current.IsActive);
        Assert.Contains("grok-4.8", current.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("运行 ", current.Summary, StringComparison.Ordinal);
        Assert.NotEqual(CliWatchStatus.Delivered, current.Phase);
        foreach (var id in new[] { "work--6", "work--7" })
        {
            var old = Assert.Single(exited.Projects, p => p.Id == id);
            Assert.False(old.IsActive);
            Assert.Equal(CliWatchStatus.Registered, old.Phase);
        }
    }

    [Fact]
    public async Task Restart_without_receipt_is_not_running_or_delivered()
    {
        using var world = new World();
        world.Job("demo-project", "work", "6", world.Registered.AddHours(-2));
        var launch = world.Matched("demo-project", "work", "8", world.Registered);
        await world.Project(launch.Wrapper, launch.Cli);
        var exited = await world.Project();
        var current = Assert.Single(exited.Projects, p => p.Id == "work--8");
        Assert.Equal(CliWatchStatus.ProcessGone, current.Phase);
        Assert.DoesNotContain("运行 ", current.Summary, StringComparison.Ordinal);
        Assert.NotEqual(CliWatchStatus.Delivered, current.Phase);
        Assert.False(exited.Projects.Single(p => p.Id == "work--6").IsActive);
        Assert.Equal(CliWatchStatus.Registered, exited.Projects.Single(p => p.Id == "work--6").Phase);
    }

    [Fact]
    public async Task Changed_prompt_identity_is_unknown_not_a_cached_run()
    {
        using var world = new World();
        world.Job("demo-project", "work", "6", world.Registered.AddHours(-2));
        var launch = world.Matched("demo-project", "work", "8", world.Registered);
        await world.Project(launch.Wrapper, launch.Cli);
        var bytes = File.ReadAllBytes(launch.Prompt);
        bytes[0] ^= 0x01;
        File.WriteAllBytes(launch.Prompt, bytes);
        var snap = await world.Project(launch.Wrapper, launch.Cli);
        var current = Assert.Single(snap.Projects, p => p.Id == "work--8");
        Assert.Equal(CliWatchStatus.Unknown, current.Phase);
        Assert.Contains("状态未知", current.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("运行 ", current.Summary, StringComparison.Ordinal);
        Assert.NotEqual(CliWatchStatus.Delivered, current.Phase);
        Assert.False(snap.Projects.Single(p => p.Id == "work--6").IsActive);
    }

    [Fact]
    public async Task Reused_pid_is_not_reported_running()
    {
        using var world = new World();
        world.Job("demo-project", "work", "6", world.Registered.AddHours(-2));
        var launch = world.Matched("demo-project", "work", "8", world.Registered);
        await world.Project(launch.Wrapper, launch.Cli);
        var reused = launch.Cli with
        {
            StartTicks = launch.Cli.StartTicks + 1,
            CommandLine = "grok.exe --cwd \"" + launch.Workspace + "\" --model grok-4.7"
        };
        var snap = await world.Project(reused);
        var current = Assert.Single(snap.Projects, p => p.Id == "work--8");
        Assert.False(CliWatchStatus.IsRunning(current.Phase));
        Assert.DoesNotContain("运行 ", current.Summary, StringComparison.Ordinal);
        Assert.NotEqual(CliWatchStatus.Delivered, current.Phase);
        Assert.False(snap.Projects.Single(p => p.Id == "work--6").IsActive);
    }

    [Fact]
    public async Task Restart_while_process_still_alive_stays_running()
    {
        using var world = new World();
        var launch = world.Matched("demo-project", "work", "8", world.Registered, model: "grok-4.8");
        await world.Project(launch.Wrapper, launch.Cli);
        var again = await world.Project(launch.Wrapper, launch.Cli);
        var current = Assert.Single(again.Projects, p => p.Id == "work--8");
        Assert.True(current.IsActive);
        Assert.True(CliWatchStatus.IsRunning(current.Phase));
        Assert.Contains("grok-4.8", current.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Native_dispatch_path_binds_actual_resume_and_remembers_exit_without_claiming_success()
    {
        using var world = new World();
        world.Job("demo-project", "work", "9", world.Registered.AddHours(-1));
        var launch = world.Matched("demo-project", "work", "10", world.Registered, quote: true);
        var cli = launch.Cli with { CommandLine = world.Command(launch, promptOverride: launch.Dispatch).Replace("--session-id", "--resume") };
        world.WriteSession(launch.Workspace, Session);
        var snap = await world.Project(cli);
        var current = snap.Projects.Single(p => p.Id == "work--10");
        Assert.Equal(CliWatchStatus.RunningActive, current.Phase);
        Assert.Contains("grok-4.7", current.Summary, StringComparison.Ordinal);
        Assert.Contains("xhigh", current.Summary, StringComparison.Ordinal);
        Assert.False(snap.Projects.Single(p => p.Id == "work--9").IsActive);
        Assert.DoesNotContain(snap.Projects, p => p.Id == "unbound");
        // Recreate the collector to exercise the on-disk record, not in-memory state.
        var restarted = await world.Project(cli);
        Assert.True(CliWatchStatus.IsRunning(restarted.Projects.Single(p => p.Id == "work--10").Phase));
        var exited = await world.Project();
        Assert.Equal(CliWatchStatus.ProcessGone, exited.Projects.Single(p => p.Id == "work--10").Phase);
        Assert.False(exited.Projects.Single(p => p.Id == "work--9").IsActive);
        var reused = await world.Project(cli with { StartTicks = cli.StartTicks + 1, CommandLine = "grok.exe --help" });
        Assert.False(CliWatchStatus.IsRunning(reused.Projects.Single(p => p.Id == "work--10").Phase));
        File.AppendAllText(launch.Dispatch, "changed");
        Assert.Equal(CliWatchStatus.Unknown, (await world.Project()).Projects.Single(p => p.Id == "work--10").Phase);
        Assert.DoesNotContain(Sentinel, File.ReadAllText(world.StoreFile), StringComparison.Ordinal);
        Assert.DoesNotContain(CommandToken, File.ReadAllText(world.StoreFile), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("wrong-cwd")]
    [InlineData("wrong-declared-path")]
    [InlineData("duplicate-registry")]
    [InlineData("wrong-registered-session")]
    [InlineData("older-process")]
    [InlineData("text-not-switch")]
    public async Task Native_dispatch_path_requires_unique_exact_contract(string reason)
    {
        using var world = new World();
        var launch = world.Matched("demo-project", "work", "10", world.Registered);
        var command = world.Command(launch, promptOverride: launch.Dispatch);
        var cli = launch.Cli with { CommandLine = command };
        if (reason == "wrong-cwd") cli = cli with { CommandLine = command.Replace(launch.Workspace, world.Root) };
        if (reason == "wrong-declared-path") world.RewriteDispatchPrompt(launch.JobFile, launch.Prompt);
        if (reason == "duplicate-registry")
        {
            var duplicate = JsonNode.Parse(File.ReadAllText(launch.JobFile))!.AsObject();
            duplicate["key"] = "other--1"; duplicate["job_id"] = "other"; duplicate["attempt_id"] = "1";
            File.WriteAllText(Path.Combine(world.Root, "jobs", "other--1.json"), duplicate.ToJsonString());
        }
        if (reason == "wrong-registered-session")
        {
            var registered = JsonNode.Parse(File.ReadAllText(launch.JobFile))!.AsObject();
            registered["resume_native_session_id"] = OtherSession;
            File.WriteAllText(launch.JobFile, registered.ToJsonString());
        }
        if (reason == "older-process") cli = cli with { StartedAt = world.Registered.AddMinutes(-1), StartTicks = world.Registered.AddMinutes(-1).UtcTicks };
        if (reason == "text-not-switch") cli = cli with { CommandLine = "grok.exe \"" + command.Replace("\"", "") + "\"" };
        var snap = await world.Project(cli);
        Assert.False(CliWatchStatus.IsRunning(snap.Projects.Single(p => p.Id == "work--10").Phase));
        Assert.Contains(snap.Projects, p => p.Id == "unbound");
    }

    [Fact]
    public async Task Unavailable_process_observation_is_not_reported_as_exit()
    {
        using var world = new World();
        var launch = world.Matched("demo-project", "work", "10", world.Registered);
        var cli = launch.Cli with { CommandLine = world.Command(launch, promptOverride: launch.Dispatch) };
        await world.Project(cli);
        world.UnknownMissingProcess = true;
        var missing = await world.Project();
        Assert.Equal(CliWatchStatus.Unknown, missing.Projects.Single(p => p.Id == "work--10").Phase);
    }

    sealed class World : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "cli-watch-pf-" + Guid.NewGuid().ToString("N"));
        public string Store { get; }
        public string StoreFile => Path.Combine(Store, "prompt-file-bindings.json");
        public DateTimeOffset Registered { get; } = DateTimeOffset.Parse("2026-09-21T17:00:00Z");
        public CollectionBatch? LastBatch { get; private set; }
        public bool UnknownMissingProcess { get; set; }
        readonly List<string> _links = new();
        public World()
        {
            Store = Path.Combine(Root, "store");
            Directory.CreateDirectory(Store);
            Directory.CreateDirectory(Path.Combine(Root, "grokhome"));
            Directory.CreateDirectory(Path.Combine(Root, "jobs"));
        }

        public void Job(
            string project,
            string jobId,
            string attempt,
            DateTimeOffset registered,
            string harness = "grok-cli",
            string owner = "lead-1",
            string? summary = null,
            string? session = null,
            string? workspace = null,
            byte[]? dispatchBytes = null,
            bool includeDispatchPrompt = true)
        {
            var outbox = Outbox(project, jobId, attempt);
            Directory.CreateDirectory(outbox);
            var dispatch = Path.Combine(outbox, "DISPATCH.txt");
            if (dispatchBytes is not null)
                File.WriteAllBytes(dispatch, dispatchBytes);
            var ws = workspace ?? Path.Combine(Root, "workspace");
            Directory.CreateDirectory(ws);
            var obj = new JsonObject
            {
                ["project"] = project,
                ["job_id"] = jobId,
                ["attempt_id"] = attempt,
                ["key"] = jobId + "--" + attempt,
                ["harness"] = harness,
                ["target_thread_id"] = owner,
                ["outbox"] = Path.GetFullPath(outbox),
                ["workspace"] = Path.GetFullPath(ws),
                ["registered_at"] = registered.ToString("o"),
                ["state"] = "registered"
            };
            if (summary is not null) obj["task_summary"] = summary;
            if (session is not null) obj["resume_native_session_id"] = session;
            if (includeDispatchPrompt && dispatchBytes is not null)
                obj["dispatch_prompt"] = Path.GetFullPath(dispatch);
            else if (includeDispatchPrompt && File.Exists(dispatch))
                obj["dispatch_prompt"] = Path.GetFullPath(dispatch);
            File.WriteAllText(Path.Combine(Root, "jobs", jobId + "--" + attempt + ".json"), obj.ToJsonString());
        }

        public Launch Matched(
            string project,
            string jobId,
            string attempt,
            DateTimeOffset registered,
            string model = "grok-4.7",
            bool quote = false,
            string? workspace = null,
            string? stateRoot = null,
            string commandStyle = "separate")
        {
            var ws = Path.GetFullPath(workspace ?? Path.Combine(Root, "workspace"));
            Directory.CreateDirectory(ws);
            var guid = Guid.NewGuid().ToString("D");
            var root = stateRoot ?? Path.Combine(Root, "state");
            var adapter = Path.Combine(root, "jobs", guid);
            Directory.CreateDirectory(adapter);
            var prompt = Path.GetFullPath(Path.Combine(adapter, "prompt.txt"));
            var bytes = Encoding.UTF8.GetBytes(Sentinel + "\n" + guid);
            File.WriteAllBytes(prompt, bytes);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var request = new JsonObject
            {
                ["protocol_version"] = "agent-direct-grok-request-v1",
                ["job_id"] = guid,
                ["workspace"] = ws,
                ["prompt"] = new JsonObject { ["path"] = prompt, ["bytes"] = bytes.LongLength, ["sha256"] = hash },
                ["model"] = model,
                ["reasoning_effort"] = "xhigh",
                ["session_id"] = Session,
                ["resume"] = false
            };
            File.WriteAllText(Path.Combine(adapter, "request.json"), request.ToJsonString());
            var outbox = Outbox(project, jobId, attempt);
            Directory.CreateDirectory(outbox);
            var dispatch = Path.GetFullPath(Path.Combine(outbox, "DISPATCH.txt"));
            File.WriteAllBytes(dispatch, bytes);
            var started = registered.AddMinutes(10);
            var wrapperStarted = registered.AddMinutes(9);
            Job(project, jobId, attempt, registered, workspace: ws, dispatchBytes: bytes);
            var jobFile = Path.Combine(Root, "jobs", jobId + "--" + attempt + ".json");
            var launch = new Launch(ws, prompt, dispatch, jobFile, adapter, guid, null!, null!, commandStyle, quote, model);
            var command = Command(launch);
            var cliTicks = started.UtcDateTime.Ticks;
            var wrapperTicks = wrapperStarted.UtcDateTime.Ticks;
            File.WriteAllText(Path.Combine(adapter, "owner.json"),
                "{\"protocol_version\":\"agent-direct-grok-owner-v1\",\"pid\":1001,\"start_time_utc_ticks\":" + wrapperTicks.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"started_at_utc\":\"" + wrapperStarted.ToString("o") + "\"}");
            var wrapper = new ObservedProcess(1001, 1, wrapperTicks, wrapperStarted, "pwsh.exe", null, "pwsh.exe -File invoke_grok_build.ps1", 10, 10, 10, true);
            var cli = new ObservedProcess(2002, 1001, cliTicks, started, "grok.exe", @"C:\grok\grok.exe", command, 20, 20, 20, true);
            return launch with { Wrapper = wrapper, Cli = cli };
        }

        public string Command(Launch launch, string? SessionOverride = null, string? promptOverride = null)
        {
            var prompt = promptOverride ?? launch.Prompt;
            var session = SessionOverride ?? Session;
            string Show(string path) => launch.Quote || path.Contains(' ') ? "\"" + path + "\"" : path;
            if (launch.CommandStyle == "equals")
                return "C:\\grok\\grok.exe --cwd " + Show(launch.Workspace) + " --model " + launch.Model
                    + " --reasoning-effort xhigh --prompt-file=" + prompt + " --session-id " + session + " " + CommandToken;
            return "C:\\grok\\grok.exe --cwd " + Show(launch.Workspace) + " --model " + launch.Model
                + " --reasoning-effort xhigh --prompt-file " + Show(prompt) + " --session-id " + session + " " + CommandToken;
        }

        public void RewriteDispatchPrompt(string jobFile, string dispatchPrompt)
        {
            var obj = JsonNode.Parse(File.ReadAllText(jobFile))!.AsObject();
            obj["dispatch_prompt"] = Path.GetFullPath(dispatchPrompt);
            File.WriteAllText(jobFile, obj.ToJsonString());
        }

        public void WriteSession(string workspace, string sessionId)
        {
            var enc = Uri.EscapeDataString(Path.GetFullPath(workspace).Trim().TrimEnd('\\', '/'));
            var dir = Path.Combine(Root, "grokhome", "sessions", enc, sessionId);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "summary.json"),
                "{\"current_model_id\":\"grok-4.8\",\"reasoning_effort\":\"xhigh\",\"last_active_at\":\"" + DateTimeOffset.UtcNow.ToString("o") + "\"}");
        }

        public void Junction(string link, string target)
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c mklink /J \"" + link + "\" \"" + target + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = System.Diagnostics.Process.Start(psi)!;
            process.WaitForExit();
            if (process.ExitCode != 0 || !Directory.Exists(link))
                throw new InvalidOperationException("junction was not created: " + process.ExitCode);
            _links.Add(link);
        }

        public async Task<CockpitSnapshot> Project(params ObservedProcess[] processes)
        {
            var collector = new MailboxCollector(new Snap(processes, UnknownMissingProcess), promptBindingDirectory: Store);
            LastBatch = await collector.CollectAsync(new CollectorSettings(Array.Empty<string>(), Array.Empty<string>())
            {
                MailboxRoots = new[] { Root },
                QuietSeconds = 120,
                HistoryHours = 72,
                GrokHome = Path.Combine(Root, "grokhome")
            }, CancellationToken.None);
            return new CliWatchProjector().Project(new IdentityNormalizer().Normalize(LastBatch)
                with { CollectedAt = Registered.AddHours(1) });
        }

        string Outbox(string project, string jobId, string attempt) =>
            Path.Combine(Root, "out", project, jobId, attempt);

        public void Dispose()
        {
            foreach (var link in _links)
            {
                try { if (Directory.Exists(link)) Directory.Delete(link, recursive: false); } catch { }
            }
            try { Directory.Delete(Root, true); } catch { }
        }
    }

    sealed record Launch(
        string Workspace,
        string Prompt,
        string Dispatch,
        string JobFile,
        string AdapterDir,
        string AdapterJobId,
        ObservedProcess Wrapper,
        ObservedProcess Cli,
        string CommandStyle,
        bool Quote,
        string Model);

    sealed class Snap(ObservedProcess[] processes, bool unknownMissing = false) : IProcessSnapshot
    {
        public IReadOnlyList<ObservedProcess> List(CancellationToken cancellationToken) => processes;
        public ProcessIdentityObservation Observe(int pid, long expectedStartTicks)
        {
            var match = processes.FirstOrDefault(p => p.Pid == pid);
            if (match is null)
                return new ProcessIdentityObservation(pid, expectedStartTicks, unknownMissing ? "unknown" : "dead", DateTimeOffset.UtcNow);
            var state = match.StartTicks == expectedStartTicks ? "alive" : "dead";
            return new ProcessIdentityObservation(pid, expectedStartTicks, state, DateTimeOffset.UtcNow);
        }
    }
}
