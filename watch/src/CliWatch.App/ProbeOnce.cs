// SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using CliWatch.Collection;
using CliWatch.Contracts;
using CliWatch.Normalization;
using CliWatch.Projection;

namespace CliWatch.App;

/// <summary>
/// One read-only collect/project pass. No window, no mutex, no prompt-binding writes.
/// </summary>
static class ProbeOnce
{
    public static int Run(LaunchOptions launch)
    {
        var requested = launch.ProbeOutput;
        if (string.IsNullOrWhiteSpace(requested))
            return 2;

        string full;
        try
        {
            full = Path.GetFullPath(requested);
        }
        catch (Exception)
        {
            return 2;
        }

        var config = AppConfig.Load(launch.ConfigPath);
        if (IsForbidden(full, config))
            return 2;

        try
        {
            var snapshot = new WindowsProcessSnapshot();
            var probe = new WindowsProcessProbe();
            var collector = new MailboxCollector(snapshot, probe, promptBindingDirectory: null);
            var batch = collector.CollectAsync(config.ToCollectorSettings(), CancellationToken.None).GetAwaiter().GetResult();
            var facts = new IdentityNormalizer().Normalize(batch);
            var projected = new CliWatchProjector().Project(facts);
            var json = BuildReport(projected, facts);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
            var tmp = full + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(full))
                File.Delete(full);
            File.Move(tmp, full);
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                Console.Error.WriteLine(ex.GetType().Name);
            }
            catch (Exception)
            {
            }

            return 1;
        }
    }

    static bool IsForbidden(string full, AppConfig config)
    {
        var norm = full.Replace('\\', '/');
        if (norm.Contains("/.codex/integrations/app-mailbox", StringComparison.OrdinalIgnoreCase)
            || norm.Contains("/.codex/integrations/cli-watch-relay", StringComparison.OrdinalIgnoreCase)
            || norm.Contains("/.claude/lead-mailbox", StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var root in config.MailboxRoots.Concat(config.ClaudeMailboxRoots))
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            string expanded;
            try
            {
                expanded = Path.GetFullPath(Environment.ExpandEnvironmentVariables(root.Trim()));
            }
            catch (Exception)
            {
                continue;
            }

            if (IsUnder(full, expanded))
                return true;
        }

        return false;
    }

    static bool IsUnder(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var baseRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (full.Equals(baseRoot, StringComparison.OrdinalIgnoreCase)) return true;
        return full.StartsWith(baseRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    static string BuildReport(CockpitSnapshot projected, FactBatch facts)
    {
        var projects = new JsonArray();
        foreach (var project in projected.Projects)
        {
            var works = new JsonArray();
            foreach (var work in project.WorkItems)
            {
                var artifacts = new JsonArray();
                foreach (var artifact in work.Artifacts)
                {
                    artifacts.Add(new JsonObject
                    {
                        ["label"] = artifact.Label,
                        ["path"] = artifact.Target?.Target
                    });
                }

                works.Add(new JsonObject
                {
                    ["actor"] = work.ActorName,
                    ["task"] = work.TaskName,
                    ["model"] = work.Model,
                    ["effort"] = work.Effort,
                    ["blocker"] = work.Blocker,
                    ["artifacts"] = artifacts
                });
            }

            projects.Add(new JsonObject
            {
                ["id"] = project.Id,
                ["name"] = project.Name,
                ["phase"] = project.Phase,
                ["active"] = project.IsActive,
                ["goal"] = project.Goal,
                ["works"] = works
            });
        }

        var processes = new JsonArray();
        foreach (var fact in facts.Facts)
        {
            if (fact.Kind is not ("unbound_process" or "process_bind")) continue;
            processes.Add(new JsonObject
            {
                ["kind"] = fact.Kind,
                ["pid"] = IntOf(fact, "pid") ?? IntOf(fact, "cli_pid"),
                ["cli"] = First(Str(fact, "cli"), Str(fact, "cli_family"), Str(fact, "cli_image")),
                ["launch_script"] = Str(fact, "launch_script"),
                ["observed_model"] = Str(fact, "observed_model"),
                ["observed_effort"] = Str(fact, "observed_effort"),
                ["has_session_dir"] = BoolOf(fact, "has_session_dir"),
                ["has_prompt_file"] = BoolOf(fact, "has_prompt_file"),
                ["command_mentions_registered_job_id"] = BoolOf(fact, "command_mentions_registered_job_id"),
                ["job_key"] = fact.Links.TryGetValue("job_key", out var key) ? key : Str(fact, "job_key"),
                ["cli_alive"] = BoolOf(fact, "cli_alive"),
                ["wrapper_state"] = Str(fact, "wrapper_state"),
                ["binding_method"] = Str(fact, "bind_how"),
                ["wrapper_alive"] = BoolOf(fact, "wrapper_alive")
            });
        }

        var issues = new JsonArray();
        foreach (var issue in projected.Issues)
        {
            issues.Add(new JsonObject
            {
                ["code"] = issue.Code,
                ["source"] = issue.SourceId,
                ["detail"] = issue.Detail
            });
        }

        var registrations = new JsonArray();
        var claudeCursor = 0;
        foreach (var fact in facts.Facts.Where(f => f.Kind == "mailbox_job"
                     && string.Equals(Str(f, "source_kind"), "claude_lead_mailbox", StringComparison.Ordinal)))
        {
            var executor = Str(fact, "registered_executor") ?? string.Empty;
            if (executor.StartsWith("cursor", StringComparison.OrdinalIgnoreCase))
                claudeCursor++;
            registrations.Add(new JsonObject
            {
                ["job_id"] = Str(fact, "job_id"),
                ["attempt_id"] = Str(fact, "attempt_id"),
                ["project"] = Str(fact, "project"),
                ["task"] = First(Str(fact, "task_summary"), Str(fact, "task")),
                ["executor"] = Str(fact, "registered_executor"),
                ["registered_model"] = Str(fact, "registered_model"),
                ["registered_effort"] = Str(fact, "registered_effort"),
                ["location"] = fact.Links.TryGetValue("location", out var location) ? location : null
            });
        }

        var bindingEvidence = new JsonArray();
        foreach (var fact in facts.Facts.Where(f => f.Kind is "mailbox_job" or "adapter_request" or "adapter_owner" or "adapter_receipt" or "adapter_terminal" or "native_supervisor_terminal"))
        {
            var item = new JsonObject { ["kind"] = fact.Kind };
            foreach (var field in new[] { "job_key", "identity_ok", "corrupt", "collection_partial", "directory_identity_conflict",
                "native_adapter_verified", "native_supervisor_verified", "native_supervisor_absent", "current_scope_member", "lead_disposition_verified" })
                if (fact.Values[field] is JsonValue value) item[field] = value.DeepClone();
            bindingEvidence.Add(item);
        }

        var root = new JsonObject
        {
            ["probe"] = "cli-watch-readonly",
            ["wrote_sources"] = false,
            ["prompt_binding_directory"] = null,
            ["collected_at"] = projected.CollectedAt.ToString("o", CultureInfo.InvariantCulture),
            ["quality"] = projected.Quality.ToString(),
            ["claude_jobs_read"] = registrations.Count,
            ["claude_rows"] = projects.Count(item =>
                item is JsonObject obj
                && (obj["name"]?.GetValue<string>() ?? string.Empty).Contains("Claude Lead发起", StringComparison.Ordinal)),
            ["claude_cursor_jobs"] = claudeCursor,
            ["limitation"] = claudeCursor == 0
                ? "Claude Lead信箱当前没有 Cursor 执行登记。隔离夹具不是生产执行 PASS。"
                : null,
            ["projects"] = projects,
            ["claude_registrations"] = registrations,
            ["processes"] = processes,
            ["binding_evidence"] = bindingEvidence,
            ["issues"] = issues
        };
        return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    static string? Str(SourceFact fact, string key)
    {
        if (fact.Values[key] is not JsonValue value) return null;
        if (value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
            return text;
        return null;
    }

    static int? IntOf(SourceFact fact, string key)
    {
        if (fact.Values[key] is not JsonValue value) return null;
        if (value.TryGetValue<int>(out var number)) return number;
        if (value.TryGetValue<long>(out var wide)) return (int)wide;
        return null;
    }

    static bool BoolOf(SourceFact fact, string key)
    {
        if (fact.Values[key] is not JsonValue value) return false;
        return value.TryGetValue<bool>(out var flag) && flag;
    }

    static string? First(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        return null;
    }
}
