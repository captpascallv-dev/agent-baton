// SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using CliWatch.Collection;
using CliWatch.Contracts;
using CliWatch.Normalization;
using CliWatch.Projection;
using Xunit;

namespace CliWatch.Tests;

public sealed class CliWatchNativeLaunchTests
{
    static readonly DateTimeOffset Registered = DateTimeOffset.Parse("2026-09-21T13:25:00Z");

    [Fact]
    public async Task Native_cli_with_exact_outbox_is_current_and_old_unverified_attempts_are_history()
    {
        using var f = new Fixture();
        f.Job("work", "1", Registered.AddHours(-9));
        f.Job("work", "3", Registered.AddHours(-5));
        var current = f.Job("work", "5", Registered);
        f.Job("independent", "1", Registered.AddHours(-4));
        f.Job("work", "other-owner", Registered.AddHours(-4), "another-lead");
        var reference = f.Job("reference", "1", Registered.AddHours(-3));
        var cli = f.Cursor(1234, current.Replace('\\', '/'));
        cli = cli with { CommandLine = cli.CommandLine + "\nConsume " + reference + "/REPORT.md" };
        var snapshot = await f.Project(cli);
        var row = Assert.Single(snapshot.Projects, p => p.Id == "work--5");
        Assert.True(row.IsActive);
        Assert.True(CliWatchStatus.IsRunning(row.Phase));
        Assert.Equal("Cursor", Assert.Single(row.WorkItems).ActorName);
        foreach (var id in new[] { "work--1", "work--3" })
        {
            var old = Assert.Single(snapshot.Projects, p => p.Id == id);
            Assert.False(old.IsActive);
            Assert.Equal(CliWatchStatus.Registered, old.Phase); // Never relabel as completed.
        }
        Assert.False(snapshot.Projects.Single(p => p.Id == "independent--1").IsActive);
        Assert.False(snapshot.Projects.Single(p => p.Id == "work--other-owner").IsActive);
    }

    [Theory]
    [InlineData("prefix")]
    [InlineData("workspace-only")]
    [InlineData("predates-registration")]
    [InlineData("two-processes")]
    [InlineData("other-cli")]
    [InlineData("shared-outbox")]
    [InlineData("reference-only")]
    [InlineData("two-contracts")]
    public async Task Ambiguous_or_unrelated_process_does_not_prove_launch(string scenario)
    {
        using var f = new Fixture();
        var outbox = f.Job("work", "5", Registered);
        var commandPath = scenario == "prefix" ? outbox + "0/REPORT.md"
            : scenario == "workspace-only" ? f.Root : outbox;
        var cli = f.Cursor(1234, commandPath);
        if (scenario == "predates-registration") cli = cli with { StartedAt = Registered.AddMinutes(-1) };
        if (scenario == "other-cli") cli = cli with { ImageName = "grok.exe", ImagePath = "grok.exe", CommandLine = "grok.exe " + outbox };
        if (scenario == "shared-outbox") f.Job("unrelated", "1", Registered, outboxOverride: outbox);
        if (scenario == "reference-only") cli = cli with { CommandLine = "node C:/cursor-agent/index.js -p \"Consume " + outbox + "/REPORT.md\"" };
        if (scenario == "two-contracts")
        {
            var other = f.Job("other", "1", Registered);
            cli = cli with { CommandLine = cli.CommandLine + "\n" + f.Cursor(1, other).CommandLine };
        }
        var processes = scenario == "two-processes" ? new[] { cli, f.Cursor(5678, outbox) } : new[] { cli };
        var snapshot = await f.Project(processes);
        Assert.Equal(CliWatchStatus.Registered, snapshot.Projects.Single(p => p.Id == "work--5").Phase);
    }

    [Fact]
    public async Task Two_live_attempts_stay_visible_and_new_registration_alone_is_not_supersession()
    {
        using var f = new Fixture();
        var old = f.Job("work", "1", Registered.AddMinutes(-2));
        var newer = f.Job("work", "2", Registered);
        var pending = await f.Project();
        Assert.All(pending.Projects, p => Assert.False(p.IsActive));
        var live = await f.Project(f.Cursor(1234, old), f.Cursor(5678, newer));
        Assert.All(live.Projects, p => Assert.True(p.IsActive));
        Assert.All(live.Projects, p => Assert.True(CliWatchStatus.IsRunning(p.Phase)));
    }

    [Fact]
    public async Task Dead_adapter_owner_is_not_overridden_by_outbox_text()
    {
        using var f = new Fixture();
        var outbox = f.Job("work", "1", Registered);
        var file = Path.Combine(f.Root, "jobs", "work--1.json");
        var job = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        var adapter = Path.Combine(f.Root, "adapter", "jobs", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        Directory.CreateDirectory(adapter);
        job["adapter_state_root"] = Path.Combine(f.Root, "adapter");
        job["adapter_job_id"] = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        File.WriteAllText(file, job.ToJsonString());
        File.WriteAllText(Path.Combine(adapter, "owner.json"), "{\"pid\":9999,\"start_time_utc_ticks\":123}");
        var snapshot = await f.Project(f.Cursor(1234, outbox));
        Assert.False(CliWatchStatus.IsRunning(snapshot.Projects.Single(p => p.Id == "work--1").Phase));
    }

    sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "cli-watch-native-" + Guid.NewGuid().ToString("N"));
        public string Job(string id, string attempt, DateTimeOffset registered, string owner = "lead", string? outboxOverride = null)
        {
            var outbox = outboxOverride ?? Path.Combine(Root, "outbox", id, attempt);
            Directory.CreateDirectory(outbox);
            Directory.CreateDirectory(Path.Combine(Root, "jobs"));
            File.WriteAllText(Path.Combine(Root, "jobs", id + "--" + attempt + ".json"), JsonSerializer.Serialize(new
            {
                project = "demo", job_id = id, attempt_id = attempt, key = id + "--" + attempt,
                harness = "cursor-cli", target_thread_id = owner, outbox, registered_at = registered.ToString("o")
            }));
            return outbox;
        }
        public ObservedProcess Cursor(int pid, string path) => new(pid, 1, pid, Registered.AddSeconds(30),
            "node.exe", @"C:\cursor-agent\node.exe", "node C:/cursor-agent/index.js -p \"Deliver\n"
            + $"项目：demo；job_id：{Path.GetFileName(Path.GetDirectoryName(path))}；attempt_id：{Path.GetFileName(path)}\r\r\n"
            + "唯一交付目录：" + path + "\nEnd\"");
        public async Task<CockpitSnapshot> Project(params ObservedProcess[] processes)
        {
            var collector = new MailboxCollector(new Snapshot(processes));
            var batch = await collector.CollectAsync(new CollectorSettings(Array.Empty<string>(), Array.Empty<string>())
                { MailboxRoots = new[] { Root } }, CancellationToken.None);
            return new CliWatchProjector().Project(new IdentityNormalizer().Normalize(batch)
                with { CollectedAt = Registered.AddHours(1) });
        }
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }
    sealed class Snapshot(ObservedProcess[] processes) : IProcessSnapshot
    {
        public IReadOnlyList<ObservedProcess> List(CancellationToken token) => processes;
        public ProcessIdentityObservation Observe(int pid, long ticks) => new(pid, ticks,
            processes.Any(p => p.Pid == pid && p.StartTicks == ticks) ? "alive" : "dead", DateTimeOffset.UtcNow);
    }
}
