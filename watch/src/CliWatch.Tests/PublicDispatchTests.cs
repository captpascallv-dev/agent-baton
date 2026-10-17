// SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Nodes;
using CliWatch.Collection;
using CliWatch.Contracts;
using CliWatch.Normalization;
using CliWatch.Projection;
using Xunit;

namespace CliWatch.Tests;

public sealed class PublicDispatchTests
{
    [Fact]
    public async Task Flat_public_dispatch_binds_the_real_cli_child_and_excludes_task_bodies()
    {
        using var f = new Fixture();
        var wrapper = f.Process(4101, 0, "pwsh.exe", "pwsh.exe -File Run-Agent.ps1", f.Start);
        var child = f.Process(4102, 4101, "grok.exe", "grok.exe --model demo-model --reasoning-effort high", f.Start.AddSeconds(1));
        f.Job("public-job"); f.Owner(wrapper);
        File.WriteAllText(Path.Combine(f.JobDirectory, "TASK_CARD.md"), "PRIVATE_BODY_SENTINEL");
        File.WriteAllText(Path.Combine(f.JobDirectory, "agent.log"), "PRIVATE_BODY_SENTINEL");
        var (batch, view) = await f.Collect(wrapper, child);
        var job = Assert.Single(batch.Documents, d => d.Kind == MailboxCollector.KindMailboxJob);
        Assert.Equal("public-job", job.Data["job_id"]!.GetValue<string>());
        Assert.Equal("1", job.Data["attempt_id"]!.GetValue<string>());
        var binding = Assert.Single(batch.Documents, d => d.Kind == MailboxCollector.KindProcessBind);
        Assert.True(binding.Data["cli_alive"]!.GetValue<bool>());
        Assert.Equal(child.Pid, binding.Data["cli_pid"]!.GetValue<int>());
        Assert.Contains(view.Projects, p => p.WorkItems.Any(w => w.TaskName == "Public build task"));
        Assert.DoesNotContain("PRIVATE_BODY_SENTINEL", string.Join("\n", batch.Documents.Select(d => d.Data.ToJsonString())));
    }

    [Fact]
    public async Task A_reused_wrapper_pid_is_not_the_original_execution()
    {
        using var f = new Fixture(); f.Job("public-job");
        var original = f.Process(4101, 0, "pwsh.exe", "pwsh.exe -File Run-Agent.ps1", f.Start);
        f.Owner(original);
        var reused = original with { StartTicks = original.StartTicks + 1000, StartedAt = original.StartedAt.AddTicks(1000) };
        var child = f.Process(4102, 4101, "grok.exe", "grok.exe --model demo-model", f.Start.AddSeconds(1));
        var (batch, _) = await f.Collect(reused, child);
        var binding = Assert.Single(batch.Documents, d => d.Kind == MailboxCollector.KindProcessBind);
        Assert.False(binding.Data["cli_alive"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("wrong-job", "1")]
    [InlineData("public-job", "2")]
    public async Task Directory_identity_conflict_cannot_redirect_delivery(string declaredJob, string attempt)
    {
        using var f = new Fixture(); f.Job(declaredJob, attempt);
        Directory.CreateDirectory(f.Outbox);
        File.WriteAllText(Path.Combine(f.Outbox, "READY.json"), "{\"status\":\"completed\"}");
        var (batch, _) = await f.Collect();
        Assert.Contains(batch.Issues, i => i.Code == "claude_directory_identity_conflict");
        Assert.DoesNotContain(batch.Documents, d => d.Kind == MailboxCollector.KindOutboxReady);
    }

    [Fact]
    public async Task Public_terminal_exit_without_READY_is_not_a_delivery()
    {
        using var f = new Fixture(); f.Job("public-job");
        var wrapper = f.Process(4101, 0, "pwsh.exe", "pwsh.exe -File Run-Agent.ps1", f.Start);
        f.Owner(wrapper);
        f.Write("terminal.json", new JsonObject {
            ["job_id"] = "public-job", ["attempt_id"] = "1", ["pid"] = wrapper.Pid,
            ["start_time_utc_ticks"] = wrapper.StartTicks, ["exit_code"] = 0,
            ["finished_at_utc"] = f.Start.AddSeconds(5).ToString("O")
        });
        var (batch, view) = await f.Collect();
        Assert.Contains(batch.Documents, d => d.Kind == MailboxCollector.KindAdapterTerminal);
        Assert.DoesNotContain(view.Projects, p => p.Phase == CliWatchStatus.Delivered);
    }

    sealed class Fixture : IDisposable
    {
        readonly string _root = Path.Combine(Path.GetTempPath(), "agent-baton-watch-test-" + Guid.NewGuid().ToString("N"));
        public DateTimeOffset Start { get; } = DateTimeOffset.UtcNow.AddMinutes(-1);
        public string JobDirectory => Path.Combine(_root, "claude", "jobs", "public-job");
        public string Outbox => Path.Combine(_root, "outbox");
        public void Job(string jobId, string attempt = "1")
        {
            Directory.CreateDirectory(JobDirectory);
            Write("job.json", new JsonObject {
                ["job_id"] = jobId, ["attempt_id"] = attempt, ["project"] = "demo",
                ["task"] = "Public build task", ["executor"] = "grok", ["outbox"] = Outbox,
                ["workspace"] = _root, ["host_pid"] = 4101, ["dispatched_at"] = Start.ToString("O"),
                ["prompt"] = "PRIVATE_BODY_SENTINEL"
            });
        }
        public void Owner(ObservedProcess process) => Write("owner.json", new JsonObject {
            ["protocol_version"] = "agent-baton-runner-owner-v1", ["job_id"] = "public-job",
            ["attempt_id"] = "1", ["pid"] = process.Pid, ["start_time_utc_ticks"] = process.StartTicks
        });
        public void Write(string file, JsonObject value) => File.WriteAllText(Path.Combine(JobDirectory, file), value.ToJsonString());
        public ObservedProcess Process(int pid, int parent, string image, string command, DateTimeOffset at) =>
            new(pid, parent, at.UtcTicks, at, image, "C:\\tools\\" + image, command);
        public async Task<(CollectionBatch, CockpitSnapshot)> Collect(params ObservedProcess[] processes)
        {
            var collector = new MailboxCollector(new Snapshot(processes));
            var settings = new CollectorSettings(Array.Empty<string>(), Array.Empty<string>()) {
                ClaudeMailboxRoots = new[] { Path.Combine(_root, "claude") },
                GrokHome = Path.Combine(_root, "empty-grok")
            };
            var batch = await collector.CollectAsync(settings, CancellationToken.None);
            return (batch, new CliWatchProjector().Project(new IdentityNormalizer().Normalize(batch)));
        }
        public void Dispose()
        {
            if (!Directory.Exists(_root)) return;
            if (Path.GetDirectoryName(Path.GetFullPath(_root)) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                || !Path.GetFileName(_root).StartsWith("agent-baton-watch-test-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected fixture cleanup path.");
            Directory.Delete(_root, true);
        }
    }
    sealed class Snapshot(IReadOnlyList<ObservedProcess> processes) : IProcessSnapshot
    {
        public IReadOnlyList<ObservedProcess> List(CancellationToken cancellationToken) => processes;
        public ProcessIdentityObservation Observe(int pid, long expectedStartTicks)
        {
            var process = processes.FirstOrDefault(p => p.Pid == pid && p.StartTicks == expectedStartTicks);
            return new(pid, expectedStartTicks, process is null ? "dead" : "alive", DateTimeOffset.UtcNow);
        }
    }
}
