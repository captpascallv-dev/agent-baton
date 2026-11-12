using System.Text.Json.Nodes;
using CliWatch.Contracts;
using CliWatch.Normalization;
using CliWatch.Projection;
using CliWatch.Views;

namespace AiMoeStudio.Bridge;

public static class LinkedIdentitySelfTest
{
    static readonly string SlotRoot = FixturePath(@"C:\studio-fixture\slot-mailbox");
    static readonly string NativeRoot = FixturePath(@"C:\studio-fixture\native-mailbox");
    static readonly string Workspace = FixturePath(@"F:\studio-fixture\work");
    static readonly string Outbox = FixturePath(@"F:\studio-fixture\work\outbox");
    const string Native192 = "32d81ec8-3691-4007-8d2d-a6b15821f6d8";
    const string Native194 = "74f67825-2f40-425e-8bcc-7ef61bc8695b";
    const string OtherNative = "be7280d8-1e51-41f3-ae10-7a6cdb717062";
    const string Slot192 = "todo-app-review-closeout-flow-r192";
    const string Slot194 = "notes-app-review-closeout-cost-r194";
    static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-22T12:00:00+08:00");
    static int _checks;
    static string FixturePath(string windowsPath) => OperatingSystem.IsMacOS()
        ? "/studio-fixture/" + windowsPath[3..].Replace('\\', '/')
        : windowsPath;

    public static void Run()
    {
        _checks = 0;
        var returned = Returned();
        var normalizer = NewNormalizer();
        var joined = normalizer.Normalize(Batch(returned));
        Require(joined.Facts.Count(f => f.Kind == "mailbox_job") == 1, "exact explicit link joins registrations");
        var registration = joined.Facts.Single(f => f.Kind == "mailbox_job");
        Require(Text(registration, "job_id") == Slot192, "slot identity remains canonical");
        Require(registration.Evidence.Count(e => e.Kind == "mailbox_job") == 2, "both original registration identities retained as evidence");
        Require(registration.Values["studio_identity_aliases"] is JsonArray aliases && aliases.Count == 2, "original keys retained");
        var projected = new CliWatchProjector().Project(joined);
        var row = projected.Projects.Single();
        Require(row.Id == Slot192 + "--1" && row.Phase == CliWatchStatus.ReportedFailure && !row.IsActive, "R192 returned issue is one historical job");
        var work = row.WorkItems.Single();
        Require(work.Axes.Delivery == "complete" && work.Axes.Execution == "returned"
            && work.Axes.Acceptance == "pending", "delivery and pending acceptance preserved");
        Require(work.Model?.Contains("gpt-6.1-sol", StringComparison.Ordinal) == true, "native model retained");
        Require(work.Artifacts.Any(a => a.Label == "READY.json") && work.Artifacts.Any(a => a.Label == "native-artifact.json")
            && work.Artifacts.Any(a => a.Label == "slot-artifact.json"), "artifacts from both identities retained");

        var active = Active();
        var activeRow = Project(active).Projects.Single();
        Require(activeRow.Phase == CliWatchStatus.RunningActive && CurrentList.IsDefaultCurrent(activeRow), "R194 actual running state wins over unknown alias");
        Require(activeRow.WorkItems.Count == 1 && !StatusLanguage.IsHistoricalWork(activeRow.WorkItems[0]), "one current work");

        if (OperatingSystem.IsMacOS())
            AssertSeparate(ChangeJob(Returned(), Native192, values => values["workspace"] = Workspace.ToUpperInvariant()), 2, "Mac path case must not merge distinct identities");
        AssertSeparate(ChangeJob(Returned(), Slot192, values => values.Remove("native_mailbox_job_id")), 2, "missing link");
        AssertSeparate(ChangeJob(Returned(), Slot192, values => values["native_mailbox_job_id"] = OtherNative), 2, "wrong UUID");
        AssertSeparate(ChangeJob(Returned(), Native192, values => values["outbox"] = FixturePath(@"F:\studio-fixture\other\outbox")), 2, "wrong outbox");
        AssertSeparate(ChangeJob(Returned(), Native192, values => values["workspace"] = FixturePath(@"F:\studio-fixture\other")), 2, "wrong workspace");
        AssertSeparate(ChangeJob(Returned(), Slot192, values => values["native_job_id"] = OtherNative), 2, "conflicting explicit UUID");
        AssertSeparate(ChangeJob(Returned(), Native192, values => values["directory_identity_conflict"] = true), 2, "native directory conflict");
        AssertSeparate(ChangeJob(Returned(), Native192, values => values["attempt_id"] = "2", rekey: true), 2, "different attempt");
        var differentRounds = Returned().Append(Job(Native192, NativeRoot, "2")).ToArray();
        AssertSeparate(differentRounds, 2, "round 2 is not absorbed into round 1");
        var duplicateTarget = Returned().Append(Root(FixturePath(@"C:\studio-fixture\another-native")))
            .Append(Job(Native192, FixturePath(@"C:\studio-fixture\another-native"), "1")).ToArray();
        AssertSeparate(duplicateTarget, 3, "ambiguous native registration");
        var duplicateAlias = Returned().Append(Job("different-slot", SlotRoot, "1", Native192)).ToArray();
        AssertSeparate(duplicateAlias, 3, "two aliases claiming one native round");

        var noLinkSameName = ChangeJob(Returned(), Slot192, values => { values.Remove("native_mailbox_job_id"); values["task_summary"] = "same task name"; });
        noLinkSameName = ChangeJob(noLinkSameName, Native192, values => values["task_summary"] = "same task name");
        AssertSeparate(noLinkSameName, 2, "same name never links jobs");
        var conflictingProcess = Active().Append(Bind(Slot194, SlotRoot, true, 43, At.AddMinutes(-5).UtcTicks)).ToArray();
        AssertSeparate(conflictingProcess, 2, "different live process claims");
        var reusedPid = Active().Append(Bind(Slot194, SlotRoot, true, 42, At.AddMinutes(-4).UtcTicks)).ToArray();
        AssertSeparate(reusedPid, 2, "same PID with different birth identity");

        // A plain READY using the other explicitly linked job ID may be reconciled.
        var nativeReadyOnly = Returned().Where(doc => doc.Kind != "outbox_ready").Append(Ready(Native192, NativeRoot, false, Slot192)).ToArray();
        var fixedReady = NewNormalizer().Normalize(Batch(nativeReadyOnly));
        Require(fixedReady.Facts.Single(f => f.Kind == "outbox_ready").Values["identity_ok"]?.GetValue<bool>() == true, "linked READY identity reconciled");
        Require(new CliWatchProjector().Project(fixedReady).Projects.Single().Phase == CliWatchStatus.ReportedFailure, "linked READY retains reported failure");
        var foreignReady = Returned().Where(doc => doc.Kind != "outbox_ready").Append(Ready(Native192, NativeRoot, false, OtherNative)).ToArray();
        var unverified = Project(foreignReady).Projects.Single();
        Require(unverified.Phase == CliWatchStatus.ExecutorFinished && unverified.WorkItems[0].Axes.Acceptance == "unknown", "foreign READY artifact cannot invent delivery or acceptance");

        // The collector rotates historical reads. Keep a verified identity template,
        // while using only this frame's process/READY facts.
        var rotating = NewNormalizer();
        var projector = new CliWatchProjector();
        var previous = projector.Project(rotating.Normalize(Batch(Active())));
        var nativeOnly = Active().Where(doc => Text(doc.Data, "mailbox_root") != SlotRoot || doc.Kind == "mailbox_root").ToArray();
        var second = projector.Project(rotating.Normalize(Batch(nativeOnly)), previous);
        Require(second.Projects.Count == 1 && second.Projects[0].IsActive && second.Projects[0].WorkItems.Count == 1, "partial reads do not resurrect the duplicate identity");
        var slotOnly = Active().Where(doc => Text(doc.Data, "mailbox_root") != NativeRoot || doc.Kind == "mailbox_root").ToArray();
        var third = projector.Project(rotating.Normalize(Batch(slotOnly)), second);
        Require(third.Projects.Count == 1 && !third.Projects[0].IsActive, "cached identity never reuses an old live process");
        var changedLink = ChangeJob(Active(), Slot194, values => values["native_mailbox_job_id"] = OtherNative);
        changedLink = changedLink.Append(Job(OtherNative, NativeRoot, "1")).ToArray();
        Require(rotating.Normalize(Batch(changedLink)).Facts.Count(f => f.Kind == "mailbox_job") == 3, "changed explicit link is a conflict");

        Console.WriteLine("identity_self_test_ok: " + _checks + " assertions; R192 return/delivery, R194 running, exact links, wrong/missing/ambiguous links, rounds, process conflicts, READY identity, partial refresh");
    }

    static LinkedIdentityNormalizer NewNormalizer() => new(new IdentityNormalizer());
    static CockpitSnapshot Project(RawDocument[] docs) => new CliWatchProjector().Project(NewNormalizer().Normalize(Batch(docs)));
    static CollectionBatch Batch(IEnumerable<RawDocument> docs) => new(At, docs.ToArray(), Array.Empty<SourceIssue>());
    static void AssertSeparate(RawDocument[] docs, int expected, string reason) =>
        Require(Project(docs).Projects.Count == expected, reason);
    static RawDocument[] Returned() => Roots().Concat(new[]
    {
        Job(Slot192, SlotRoot, "1", Native192), Job(Native192, NativeRoot),
        Bind(Slot192, SlotRoot, false), Bind(Native192, NativeRoot, false),
        Fact("adapter_owner", Native192, NativeRoot, new JsonObject { ["identity_ok"] = true, ["pid"] = 42, ["start_time_utc_ticks"] = At.AddMinutes(-5).UtcTicks }),
        Fact("adapter_request", Native192, NativeRoot, new JsonObject { ["identity_ok"] = true, ["model"] = "gpt-6.1-sol", ["reasoning_effort"] = "xhigh" }),
        Fact("adapter_terminal", Native192, NativeRoot, new JsonObject { ["identity_ok"] = true, ["exit_code"] = 0, ["finished_at_utc"] = At.ToString("o") }),
        Ready(Slot192, SlotRoot, true, Slot192),
        Fact("outbox_file", Slot192, SlotRoot, new JsonObject { ["name"] = "slot-artifact.json", ["activity"] = false }),
        Fact("outbox_file", Native192, NativeRoot, new JsonObject { ["name"] = "native-artifact.json", ["activity"] = false })
    }).ToArray();
    static RawDocument[] Active() => Roots().Concat(new[]
    {
        Job(Slot194, SlotRoot, "1", Native194), Job(Native194, NativeRoot),
        Bind(Slot194, SlotRoot, false), Bind(Native194, NativeRoot, true),
        Fact("adapter_owner", Native194, NativeRoot, new JsonObject { ["identity_ok"] = true, ["pid"] = 42, ["start_time_utc_ticks"] = At.AddMinutes(-5).UtcTicks }),
        Fact("adapter_request", Native194, NativeRoot, new JsonObject { ["identity_ok"] = true, ["model"] = "gpt-6.1-sol", ["reasoning_effort"] = "xhigh" })
    }).ToArray();
    static IEnumerable<RawDocument> Roots()
    {
        foreach (var root in new[] { SlotRoot, NativeRoot })
            yield return Root(root);
    }
    static RawDocument Root(string root) => new("root:" + root, "mailbox_root", root, new JsonObject {
        ["root"] = root, ["readable"] = true, ["scan_complete"] = false, ["mailbox_root"] = root }, At, At);
    static RawDocument Job(string id, string root, string attempt = "1", string? native = null)
    {
        var values = new JsonObject { ["project"] = "fixture", ["harness"] = "codex-cli", ["workspace"] = Workspace,
            ["outbox"] = Outbox, ["task_summary"] = "fixture", ["registered_at"] = At.AddMinutes(-10).ToString("o") };
        if (native is not null) { values["native_mailbox_job_id"] = native; values["collection_partial"] = true; }
        if (root == NativeRoot)
        {
            values["source_kind"] = "claude_lead_mailbox";
            values["adapter_job_id"] = id;
            values["registered_model"] = "gpt-6.1-sol";
            values["registered_effort"] = "xhigh";
        }
        return Fact("mailbox_job", id, root, values, attempt);
    }
    static RawDocument Bind(string id, string root, bool live, int pid = 42, long? ticks = null) =>
        Fact("process_bind", id, root, new JsonObject { ["cli_alive"] = live, ["wrapper_alive"] = false,
            ["wrapper_state"] = live ? "alive" : "dead", ["cli_pid"] = live ? pid : 0,
            ["cli_start_ticks"] = ticks ?? At.AddMinutes(-5).UtcTicks, ["activity_kind"] = live ? "cli_compute" : "none",
            ["counters_available"] = live, ["observed_model"] = live ? "gpt-6.1-sol" : null });
    static RawDocument Ready(string id, string root, bool identity, string declaredJob) =>
        Fact("outbox_ready", id, root, new JsonObject { ["job_id"] = declaredJob, ["identity_ok"] = identity, ["status"] = "failed" });
    static RawDocument Fact(string kind, string id, string root, JsonObject values, string attempt = "1")
    {
        var key = id + "--" + attempt;
        if (values["job_id"] is null) values["job_id"] = id;
        values["attempt_id"] = attempt; values["key"] = key; values["job_key"] = key; values["mailbox_root"] = root;
        var location = kind == "outbox_ready" ? Path.Combine(Outbox, "READY.json")
            : kind == "outbox_file" ? Path.Combine(Outbox, Text(values, "name")!)
            : Path.Combine(root, "jobs", key, kind + ".json");
        return new RawDocument(kind + ":" + root + ":" + key + ":" + location, kind, location, values, At, At, "fixture");
    }
    static RawDocument[] ChangeJob(RawDocument[] docs, string id, Action<JsonObject> change, bool rekey = false) =>
        docs.Select(doc =>
        {
            if (doc.Kind != "mailbox_job" || Text(doc.Data, "job_id") != id) return doc;
            var values = (JsonObject)doc.Data.DeepClone(); change(values);
            if (rekey) { values["key"] = id + "--" + Text(values, "attempt_id"); values["job_key"] = values["key"]!.DeepClone(); }
            return doc with { Data = values };
        }).ToArray();
    static string? Text(SourceFact fact, string key) => Text(fact.Values, key);
    static string? Text(JsonObject values, string key) => ObservationIdentity.Text(values, key);
    static void Require(bool ok, string reason) { _checks++; if (!ok) throw new InvalidOperationException("identity assertion: " + reason); }
}
