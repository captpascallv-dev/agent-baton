// SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Nodes;
using CliWatch.Collection;
using CliWatch.Contracts;
using CliWatch.Normalization;
using CliWatch.Projection;
using Xunit;

namespace CliWatch.Tests;

public sealed class CliWatchOwnerHandoffTests
{
    const string Project = "demo-project";
    const string JobId = "demo-project-v23-12-continuous-20260921";
    const string OldOwner = "00000000-0000-0000-0000-000000000001";
    const string NewOwner = "00000000-0000-0000-0000-000000000002";
    const string Coordinator = "00000000-0000-0000-0000-000000000003";
    static readonly (string Attempt, string RegisteredAt)[] MigratedAttempts =
    {
        ("1", "2026-09-21T03:55:18.325691+00:00"),
        ("3", "2026-09-21T07:32:39.818901+00:00"),
        ("4", "2026-09-21T07:39:44.535009+00:00"),
        ("6", "2026-09-21T15:05:07.339633+00:00"),
        ("7", "2026-09-21T16:54:42.131640+00:00"),
        ("9", "2026-09-21T19:24:01.961260+00:00")
    };

    [Fact]
    public async Task Accepted_real_owner_migration_hides_only_unstarted_old_registrations_and_is_restart_stable()
    {
        using var root = new TempDir();
        var authority = WriteAuthority(root.Path);
        foreach (var (attempt, registeredAt) in MigratedAttempts)
            WriteJob(root.Path, Project, JobId, attempt, registeredAt, NewOwner, authority, handoff: true);
        WriteJob(root.Path, Project, JobId, "26", "2026-09-23T14:31:16.782934+00:00", OldOwner);
        WriteSentDelivery(root.Path, Project, JobId, "26", OldOwner,
            "2026-09-23T14:54:34.065265+00:00", "2026-09-23T14:54:37.473493+00:00");

        var firstBatch = await CollectAsync(root.Path);
        var registration = Assert.Single(firstBatch.Documents,
            d => d.Kind == MailboxCollector.KindMailboxJob && d.Data["attempt_id"]?.ToString() == "1");
        Assert.Equal(true, registration.Data["owner_handoff_verified"]?.GetValue<bool>());
        Assert.Equal(OldOwner, registration.Data["owner_handoff_evidence_old_thread_id"]?.GetValue<string>());
        Assert.Equal(NewOwner, registration.Data["owner_handoff_evidence_new_thread_id"]?.GetValue<string>());

        var normalized = new IdentityNormalizer().Normalize(firstBatch);
        var normalizedRegistration = Assert.Single(normalized.Facts,
            f => f.Kind == MailboxCollector.KindMailboxJob && f.Values["attempt_id"]?.ToString() == "1");
        Assert.Equal(true, normalizedRegistration.Values["owner_handoff_verified"]?.GetValue<bool>());
        var authorityFact = Assert.Single(normalized.Facts,
            f => f.Kind == MailboxCollector.KindOwnerHandoffAuthority
                && f.Values["job_key"]?.ToString() == JobId + "--1");
        Assert.Contains(authorityFact.Evidence, e => e.Location == authority);
        Assert.False(authorityFact.Values.ContainsKey("judgment"));
        Assert.False(authorityFact.Values.ContainsKey("user_authority"));

        var first = new CliWatchProjector().Project(normalized);
        Assert.Contains(first.Projects.Single(p => p.Name == $"{Project} / {JobId} #1").WorkItems
            .SelectMany(w => w.Evidence), e => e.Location == authority);
        AssertMigratedRows(first);
        var refreshed = new CliWatchProjector().Project(new IdentityNormalizer().Normalize(await CollectAsync(root.Path)));
        AssertMigratedRows(refreshed);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("rejected")]
    [InlineData("owner-mismatch")]
    [InlineData("unrelated-successor")]
    public async Task Missing_or_conflicting_handoff_does_not_hide_a_different_owner_registration(string evidenceCase)
    {
        using var root = new TempDir();
        var authority = Path.Combine(root.Path, "handoff", "HANDOFF_ACCEPTANCE.json");
        if (evidenceCase != "missing")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(authority)!);
            WriteAuthority(authority, accepted: evidenceCase != "rejected",
                authorityOldOwner: evidenceCase == "owner-mismatch" ? "unrelated-old-owner" : OldOwner);
        }

        WriteJob(root.Path, Project, JobId, "1", MigratedAttempts[0].RegisteredAt, NewOwner, authority, handoff: true);
        var successorOwner = evidenceCase == "unrelated-successor" ? "unrelated-successor-owner" : OldOwner;
        WriteJob(root.Path, Project, JobId, "26", "2026-09-23T14:31:16.782934+00:00", successorOwner);
        WriteSentDelivery(root.Path, Project, JobId, "26", successorOwner,
            "2026-09-23T14:54:34.065265+00:00", "2026-09-23T14:54:37.473493+00:00");

        var batch = await CollectAsync(root.Path);
        var fact = Assert.Single(new IdentityNormalizer().Normalize(batch).Facts,
            f => f.Kind == MailboxCollector.KindMailboxJob && f.Values["attempt_id"]?.ToString() == "1");
        if (evidenceCase == "unrelated-successor")
            Assert.Equal(true, fact.Values["owner_handoff_verified"]?.GetValue<bool>());
        else
            Assert.NotEqual(true, fact.Values["owner_handoff_verified"]?.GetValue<bool>());

        var projected = new CliWatchProjector().Project(new IdentityNormalizer().Normalize(batch));
        var old = Find(projected, root.Path, Project, JobId, "1");
        var successor = Find(projected, root.Path, Project, JobId, "26");
        Assert.Equal(CliWatchStatus.Registered, old.Phase);
        Assert.False(old.IsActive);
        Assert.Equal(CliWatchStatus.Delivered, successor.Phase);
        Assert.False(successor.IsActive);
    }

    [Fact]
    public async Task Accepted_transfer_is_scoped_to_mailbox_root_project_and_job()
    {
        using var firstRoot = new TempDir();
        using var secondRoot = new TempDir();
        var authority = WriteAuthority(firstRoot.Path);

        // Same root/project/job and the accepted owner pair: old unstarted rounds are historical.
        WriteJob(firstRoot.Path, Project, JobId, "1", MigratedAttempts[0].RegisteredAt, NewOwner, authority, handoff: true);
        WriteJob(firstRoot.Path, Project, JobId, "26", "2026-09-23T14:31:16.782934+00:00", OldOwner);
        WriteSentDelivery(firstRoot.Path, Project, JobId, "26", OldOwner,
            "2026-09-23T14:54:34.065265+00:00", "2026-09-23T14:54:37.473493+00:00");

        // Same root, different job or project: no handoff proof is present for those registrations.
        WriteJob(firstRoot.Path, Project, "other-job", "1", MigratedAttempts[0].RegisteredAt, NewOwner);
        WriteJob(firstRoot.Path, Project, "other-job", "26", "2026-09-22T14:31:16.782934+00:00", OldOwner);
        WriteJob(firstRoot.Path, "another-project", "other-project-job", "1", MigratedAttempts[0].RegisteredAt, NewOwner);
        WriteJob(firstRoot.Path, "another-project", "other-project-job", "26", "2026-09-23T14:31:16.782934+00:00", OldOwner);

        // Same project/job/attempt identities in another mailbox root cannot borrow the proof.
        WriteJob(secondRoot.Path, Project, JobId, "1", MigratedAttempts[0].RegisteredAt, NewOwner);
        WriteJob(secondRoot.Path, Project, JobId, "26", "2026-09-23T14:31:16.782934+00:00", OldOwner);

        var batch = await CollectAsync(firstRoot.Path, secondRoot.Path);
        var projected = new CliWatchProjector().Project(new IdentityNormalizer().Normalize(batch));
        Assert.False(Find(projected, firstRoot.Path, Project, JobId, "1").IsActive);
        Assert.False(Find(projected, firstRoot.Path, Project, JobId, "26").IsActive, "ended delivery remains in history");
        Assert.False(Find(projected, firstRoot.Path, Project, "other-job", "1").IsActive, "different job must not inherit the transfer");
        Assert.False(Find(projected, firstRoot.Path, "another-project", "other-project-job", "1").IsActive, "different project must not inherit the transfer");
        Assert.False(Find(projected, secondRoot.Path, Project, JobId, "1").IsActive, "different mailbox root must not inherit the transfer");
    }

    [Fact]
    public async Task Live_cli_remains_current_even_when_owner_handoff_would_supersede_registration()
    {
        using var root = new TempDir();
        var authority = WriteAuthority(root.Path);
        var adapterRoot = Path.Combine(root.Path, "adapter");
        var adapterJobId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        var adapterJob = Path.Combine(adapterRoot, "jobs", adapterJobId);
        Directory.CreateDirectory(adapterJob);
        WriteJob(root.Path, Project, JobId, "1", MigratedAttempts[0].RegisteredAt, NewOwner, authority,
            handoff: true, adapterRoot: adapterRoot, adapterJobId: adapterJobId);
        WriteJob(root.Path, Project, JobId, "26", "2026-09-23T14:31:16.782934+00:00", OldOwner);
        WriteSentDelivery(root.Path, Project, JobId, "26", OldOwner,
            "2026-09-23T14:54:34.065265+00:00", "2026-09-23T14:54:37.473493+00:00");
        File.WriteAllText(Path.Combine(adapterJob, "owner.json"),
            "{\"pid\":1001,\"start_time_utc_ticks\":111,\"started_at_utc\":\"2026-09-21T03:55:19Z\"}");
        File.WriteAllText(Path.Combine(adapterJob, "request.json"), "{\"model\":\"codex-test\"}");
        var live = new[]
        {
            Proc(1001, 1, 111, "pwsh.exe", "pwsh -File wrapper.ps1"),
            Proc(2002, 1001, 222, "cursor-agent.exe", "cursor-agent --cwd C:\\work")
        };

        var projected = new CliWatchProjector().Project(new IdentityNormalizer().Normalize(
            await CollectAsync(new FakeSnapshot(live), root.Path)));
        var active = Find(projected, root.Path, Project, JobId, "1");
        Assert.True(active.IsActive);
        Assert.True(CliWatchStatus.IsRunning(active.Phase) || active.Phase == CliWatchStatus.StartUnconfirmed);
        Assert.False(Find(projected, root.Path, Project, JobId, "26").IsActive);
    }

    [Fact]
    public async Task Live_wrapper_keeps_even_a_retired_registration_current_after_a_later_delivery()
    {
        using var root = new TempDir();
        var authority = WriteAuthority(root.Path);
        var adapterRoot = Path.Combine(root.Path, "adapter");
        var adapterJobId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
        var adapterJob = Path.Combine(adapterRoot, "jobs", adapterJobId);
        Directory.CreateDirectory(adapterJob);
        WriteJob(root.Path, Project, JobId, "1", MigratedAttempts[0].RegisteredAt, NewOwner, authority,
            handoff: true, adapterRoot: adapterRoot, adapterJobId: adapterJobId, state: "retired");
        WriteJob(root.Path, Project, JobId, "26", "2026-09-23T14:31:16.782934+00:00", OldOwner);
        WriteSentDelivery(root.Path, Project, JobId, "26", OldOwner,
            "2026-09-23T14:54:34.065265+00:00", "2026-09-23T14:54:37.473493+00:00");
        File.WriteAllText(Path.Combine(adapterJob, "owner.json"),
            "{\"pid\":1101,\"start_time_utc_ticks\":333,\"started_at_utc\":\"2026-09-21T03:55:19Z\"}");
        File.WriteAllText(Path.Combine(adapterJob, "request.json"), "{\"model\":\"cursor-test\"}");
        var wrapper = Proc(1101, 1, 333, "pwsh.exe", "pwsh -File wrapper.ps1");

        var projected = new CliWatchProjector().Project(new IdentityNormalizer().Normalize(
            await CollectAsync(new FakeSnapshot(new[] { wrapper }), root.Path)));
        var active = Find(projected, root.Path, Project, JobId, "1");
        Assert.Equal(CliWatchStatus.StartUnconfirmed, active.Phase);
        Assert.True(active.IsActive);
        Assert.Contains("退役", string.Join(" ", active.WorkItems.Select(w => w.Blocker)), StringComparison.Ordinal);
        Assert.Equal(CliWatchStatus.Delivered, Find(projected, root.Path, Project, JobId, "26").Phase);
    }

    [Fact]
    public async Task Codex_service_entry_parses_globals_and_fails_open_on_unknown_or_missing_options()
    {
        using var root = new TempDir();
        Directory.CreateDirectory(Path.Combine(root.Path, "jobs"));
        var processes = new[]
        {
            Proc(101, 1, 101, "codex.exe", "\"C:\\Program Files\\Codex\\codex.exe\" app-server --listen stdio"),
            Proc(102, 1, 102, "codex.exe", "\"C:\\Program Files\\Codex\\codex.exe\" mcp-server --transport stdio"),
            Proc(103, 1, 103, "codex.exe", "\"C:\\Program Files\\Codex\\codex.exe\" exec \"Explain app-server and mcp-server text\""),
            Proc(104, 1, 104, "codex.exe", "\"C:\\Program Files\\Codex\\codex.exe\""),
            Proc(105, 1, 105, "codex.exe", "\"C:\\Program Files\\Codex\\codex.exe\" -c model=app-server app-server --analytics-default-enabled -c model=mcp-server"),
            Proc(106, 1, 106, "codex.exe", "\"C:\\Program Files\\Codex\\codex.exe\" -C \"C:\\work tree\" -c model=mcp-server mcp-server --transport stdio"),
            Proc(107, 1, 107, "codex.exe", "\"C:\\Program Files\\Codex\\codex.exe\" -c model=app-server exec \"Explain mcp-server\""),
            Proc(108, 1, 108, "codex.exe", "\"C:\\Program Files\\Codex\\codex.exe\" -c model=mcp-server \"Explain app-server\""),
            Proc(109, 1, 109, "codex.exe", "\"C:\\Program Files\\Codex\\codex.exe\" --future-global-option app-server"),
            Proc(110, 1, 110, "codex.exe", "\"C:\\Program Files\\Codex\\codex.exe\" -c"),
            Proc(111, 1, 111, "codex.exe", "\"C:\\Program Files\\Codex\\codex.exe\" -c --help app-server"),
            Proc(112, 1, 112, "codex.exe", "\"C:\\Program Files\\Codex\\codex.exe\" -i picture.png app-server")
        };

        var batch = await CollectAsync(new FakeSnapshot(processes), root.Path);
        var unbound = batch.Documents.Where(d => d.Kind == MailboxCollector.KindUnboundProcess)
            .Select(d => d.Data["pid"]!.GetValue<int>()).OrderBy(pid => pid).ToArray();
        Assert.Equal(new[] { 103, 104, 107, 108, 109, 110, 111, 112 }, unbound);

        var projected = new CliWatchProjector().Project(new IdentityNormalizer().Normalize(batch));
        var unboundView = Assert.Single(projected.Projects, p => p.Id == "unbound");
        Assert.Equal(8, unboundView.WorkItems.Count);
        foreach (var pid in unbound)
            Assert.Contains(unboundView.WorkItems, w => w.Id.Contains($":{pid}:", StringComparison.Ordinal));
    }

    static void AssertMigratedRows(CockpitSnapshot projected)
    {
        foreach (var (attempt, _) in MigratedAttempts)
        {
            var row = Assert.Single(projected.Projects, p => p.Name == $"{Project} / {JobId} #{attempt}");
            Assert.Equal(CliWatchStatus.Registered, row.Phase);
            Assert.False(row.IsActive);
            Assert.Contains("不代表任务完成", row.NextStep, StringComparison.Ordinal);
            Assert.DoesNotContain("通过", row.Summary, StringComparison.Ordinal);
        }

        var latest = Assert.Single(projected.Projects, p => p.Name == $"{Project} / {JobId} #26");
        Assert.Equal(CliWatchStatus.Delivered, latest.Phase);
        Assert.False(latest.IsActive);
        Assert.Contains("已交付待 Lead", latest.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("PASS", latest.Summary, StringComparison.OrdinalIgnoreCase);
    }

    static ProjectView Find(CockpitSnapshot snapshot, string root, string project, string job, string attempt)
    {
        var key = job + "--" + attempt;
        return Assert.Single(snapshot.Projects, p => p.Name == $"{project} / {job} #{attempt}"
            && (p.Id.Equals(key, StringComparison.OrdinalIgnoreCase)
                || p.Id.StartsWith(root, StringComparison.OrdinalIgnoreCase)));
    }

    static string WriteAuthority(string root)
    {
        var path = Path.Combine(root, "handoff", "HANDOFF_ACCEPTANCE.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteAuthority(path, accepted: true, authorityOldOwner: OldOwner);
        return path;
    }

    static void WriteAuthority(string path, bool accepted, string authorityOldOwner)
    {
        var authority = new JsonObject
        {
            ["accepted"] = accepted,
            ["date"] = "2026-09-25",
            ["coordinator_thread_id"] = Coordinator,
            ["old_thread_id"] = authorityOldOwner,
            ["new_thread_id"] = NewOwner,
            ["judgment"] = "Authority text is not needed for the display projection.",
            ["user_authority"] = new JsonArray(JsonValue.Create("Authority text")),
            ["product_phase_acceptance"] = false,
            ["ownership_release_condition"] = "Routing transfer verified and explicit coordinator release message delivered to the new Lead."
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, authority.ToJsonString());
    }

    static void WriteJob(
        string root,
        string project,
        string jobId,
        string attempt,
        string registeredAt,
        string targetOwner,
        string? authority = null,
        bool handoff = false,
        string? adapterRoot = null,
        string? adapterJobId = null,
        string state = "registered")
    {
        var key = jobId + "--" + attempt;
        var job = new JsonObject
        {
            ["project"] = project,
            ["job_id"] = jobId,
            ["attempt_id"] = attempt,
            ["harness"] = "cursor-cli",
            ["key"] = key,
            ["outbox"] = Path.Combine(root, "outboxes", key),
            ["registered_at"] = registeredAt,
            ["target_thread_id"] = targetOwner,
            ["state"] = state
        };
        if (handoff)
        {
            job["previous_target_thread_id"] = OldOwner;
            job["owner_handoff_authority"] = authority;
            job["owner_handoff_at"] = "2026-09-25T11:04:49.246854+00:00";
        }
        if (adapterRoot is not null && adapterJobId is not null)
        {
            job["adapter_state_root"] = adapterRoot;
            job["adapter_job_id"] = adapterJobId;
        }

        var jobs = Path.Combine(root, "jobs");
        Directory.CreateDirectory(jobs);
        File.WriteAllText(Path.Combine(jobs, key + ".json"), job.ToJsonString());
    }

    static void WriteSentDelivery(string root, string project, string jobId, string attempt,
        string owner, string createdAt, string sentAt)
    {
        var key = jobId + "--" + attempt;
        var deliveries = Path.Combine(root, "deliveries");
        var snapshot = Path.Combine(root, "snapshots", key);
        Directory.CreateDirectory(deliveries);
        Directory.CreateDirectory(snapshot);
        File.WriteAllText(Path.Combine(deliveries, key + ".json"), new JsonObject
        {
            ["id"] = key,
            ["project"] = project,
            ["target_thread_id"] = owner,
            ["marker"] = "app-mailbox:" + key,
            ["state"] = "sent",
            ["created_at"] = createdAt,
            ["sent_at"] = sentAt
        }.ToJsonString());
        File.WriteAllText(Path.Combine(snapshot, "manifest.json"), new JsonObject
        {
            ["job"] = new JsonObject
            {
                ["project"] = project,
                ["job_id"] = jobId,
                ["attempt_id"] = attempt,
                ["key"] = key
            },
            ["files"] = new JsonArray(),
            ["marker"] = "app-mailbox:" + key
        }.ToJsonString());
        File.WriteAllText(Path.Combine(snapshot, "0-READY.json"), new JsonObject
        {
            ["job_id"] = jobId,
            ["attempt_id"] = attempt,
            ["status"] = "completed",
            ["report_file"] = "REPORT.md",
            ["files"] = new JsonArray()
        }.ToJsonString());
    }

    static Task<CollectionBatch> CollectAsync(params string[] roots) =>
        CollectAsync(new FakeSnapshot(Array.Empty<ObservedProcess>()), roots);

    static async Task<CollectionBatch> CollectAsync(IProcessSnapshot snapshot, params string[] roots)
    {
        var batch = await new MailboxCollector(snapshot).CollectAsync(new CollectorSettings(Array.Empty<string>(), Array.Empty<string>())
        {
            MailboxRoots = roots,
            QuietSeconds = 120,
            HistoryHours = 72
        }, CancellationToken.None);
        return batch with { CollectedAt = DateTimeOffset.Parse("2026-09-23T16:00:00Z") };
    }

    static ObservedProcess Proc(int pid, int parent, long ticks, string image, string command) =>
        new(pid, parent, ticks, DateTimeOffset.Parse("2026-09-25T11:00:00Z"), image, null, command, 10, 10, 10, true);

    sealed class FakeSnapshot : IProcessSnapshot
    {
        readonly IReadOnlyList<ObservedProcess> _processes;
        public FakeSnapshot(IReadOnlyList<ObservedProcess> processes) => _processes = processes;
        public IReadOnlyList<ObservedProcess> List(CancellationToken cancellationToken) => _processes;
        public ProcessIdentityObservation Observe(int pid, long expectedStartTicks)
        {
            var process = _processes.FirstOrDefault(p => p.Pid == pid);
            var state = process is not null && process.StartTicks == expectedStartTicks ? "alive" : "dead";
            return new ProcessIdentityObservation(pid, expectedStartTicks, state, DateTimeOffset.UtcNow);
        }
    }

    sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cli-watch-handoff-test-" + Guid.NewGuid().ToString("n"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (Exception) { }
        }
    }
}
