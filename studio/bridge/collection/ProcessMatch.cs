// SPDX-License-Identifier: MPL-2.0
using System.Text;
using CliWatch.Contracts;

namespace CliWatch.Collection;

internal static class ProcessMatch
{
    // Keep this list aligned with Codex's documented top-level CLI options. Unknown
    // options and variadic image arguments are ambiguous; those commands fail open.
    static readonly HashSet<string> CodexValueOptions = new(StringComparer.Ordinal)
    {
        "-c", "--config", "--enable", "--disable", "--remote", "--remote-auth-token-env",
        "-m", "--model", "--local-provider", "-p", "--profile", "-s", "--sandbox",
        "-C", "--cd", "--add-dir", "-a", "--ask-for-approval"
    };

    static readonly HashSet<string> CodexBooleanOptions = new(StringComparer.Ordinal)
    {
        "--strict-config", "--oss", "--approve-for-me",
        "--dangerously-bypass-approvals-and-sandbox", "--dangerously-bypass-hook-trust",
        "--search", "--no-alt-screen"
    };

    public static bool Matches(ObservedProcess process, ProcessDiscoveryRule rule)
    {
        if (IsCodexServiceProcess(process)) return false;

        if (rule.ExcludeImages is { Count: > 0 })
        {
            foreach (var ex in rule.ExcludeImages)
            {
                if (ImageEquals(process.ImageName, ex) || ImageEquals(process.ImagePath, ex))
                    return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(rule.Image) && !ImageEquals(process.ImageName, rule.Image) && !ImageEquals(process.ImagePath, rule.Image))
            return false;

        var cursorNode = rule.Cli.Equals("Cursor", StringComparison.OrdinalIgnoreCase) && ImageEquals(process.ImageName, "node.exe");
        if (cursorNode && !IsCursorEntry(process)) return false;
        var entry = EntryIdentity(process);
        if (!cursorNode && rule.CommandContains is { Count: > 0 })
        {
            var any = false;
            foreach (var needle in rule.CommandContains)
            {
                if (!string.IsNullOrWhiteSpace(needle) && entry.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    any = true;
                    break;
                }
            }

            if (!any) return false;
        }

        if (rule.CommandExcludes is { Count: > 0 })
        {
            foreach (var needle in rule.CommandExcludes)
            {
                if (!string.IsNullOrWhiteSpace(needle) && entry.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
        }

        if (rule.ScriptPackages is { Count: > 0 }
            && !IsConfiguredScriptEntry(process, rule.ScriptPackages))
            return false;

        // Claude Desktop is also claude.exe. Only a claude-code path that is not
        // the AnthropicClaude app, and not an Electron role process, is Claude Code.
        if (rule.Cli.Equals("Claude", StringComparison.OrdinalIgnoreCase)
            && !IsClaudeCodeCli(process))
            return false;

        return true;
    }

    static bool IsCursorEntry(ObservedProcess process)
    {
        if (!TryFindNodeEntryScript(process.CommandLine, out var script, out _)) return false;
        var path = script.Replace('\\', '/');
        if (!FileNameOf(path).Equals("index.js", StringComparison.OrdinalIgnoreCase)) return false;
        if (path.EndsWith("/cursor-agent/index.js", StringComparison.OrdinalIgnoreCase)) return true;
        var marker = "/cursor-agent/versions/";
        var at = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at >= 0 && path[(at + marker.Length)..].Split('/').Length == 2) return true;
        return path.Contains("/@anysphere/cursor-agent/", StringComparison.OrdinalIgnoreCase);
    }

    public static string EntryIdentity(ObservedProcess process)
    {
        if (ImageEquals(process.ImageName, "node.exe"))
            return TryFindNodeEntryScript(process.CommandLine, out var script, out _) ? script : "";
        var args = string.IsNullOrWhiteSpace(process.CommandLine) ? new List<string>() : SplitWindowsArguments(process.CommandLine);
        if (ImageEquals(process.ImageName, "pwsh.exe") || ImageEquals(process.ImageName, "powershell.exe"))
        {
            for (var i = 1; i + 1 < args.Count; i++)
            {
                if (args[i] == "--") break;
                if (args[i].Equals("-File", StringComparison.OrdinalIgnoreCase)) return args[i + 1];
                if (args[i].Equals("-Command", StringComparison.OrdinalIgnoreCase)) break;
            }
        }
        return process.ImagePath ?? (args.Count > 0 ? args[0] : process.ImageName);
    }

    public static bool IsShellOrMcp(ObservedProcess process)
    {
        if (ImageEquals(process.ImageName, "grove.exe") || ImageEquals(process.ImageName, "Code.exe")) return true;
        if (IsCodexServiceProcess(process)) return true;
        var entry = EntryIdentity(process).Replace('\\', '/');
        if (entry.Contains("/mcp-server/", StringComparison.OrdinalIgnoreCase)
            || entry.Contains("/@modelcontextprotocol/", StringComparison.OrdinalIgnoreCase)
            || FileNameOf(entry).Equals("mcp-server.js", StringComparison.OrdinalIgnoreCase)
            || FileNameOf(entry).Equals("process_file_host.ps1", StringComparison.OrdinalIgnoreCase)
            || FileNameOf(entry).Equals("invoke_grok_build.ps1", StringComparison.OrdinalIgnoreCase)) return true;
        if (!ImageEquals(process.ImageName, "grok.exe") && !ImageEquals(process.ImageName, "agent.exe") && !ImageEquals(process.ImageName, "claude.exe")) return false;
        var args = string.IsNullOrWhiteSpace(process.CommandLine) ? new List<string>() : SplitWindowsArguments(process.CommandLine);
        for (var i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == "--") return false;
            var option = arg.Split('=')[0];
            if (NativeValueOptions.Contains(option) || ImageEquals(process.ImageName, "claude.exe") && TryClaudeValueOption(arg, out _))
            { if (!arg.Contains('=')) i++; continue; }
            if (arg.StartsWith('-')) continue;
            return arg is "mcp" or "mcp-server";
        }
        return false;
    }

    internal static readonly HashSet<string> NativeValueOptions = new(StringComparer.Ordinal)
    {
        "--prompt-file", "--cwd", "--session-id", "--resume", "--model", "--reasoning-effort",
        "--workspace", "--output-format", "--input-format", "--system-prompt", "--system-prompt-file",
        "--append-system-prompt", "--append-system-prompt-file", "--session-dir", "--session", "--thinking",
        "--provider", "--config", "--profile", "--mcp-config", "--add-dir", "--api-key", "--name",
        "--extension", "--skill", "--prompt-template", "--theme", "-m", "-c", "-C"
    };

    internal static int NativeOptionStart(IReadOnlyList<string> args) =>
        TryFindNodeEntryScript(args, out _, out var index) ? index + 1 : 1;

    public readonly record struct PiLaunchFacts(
        string? LaunchScript,
        string? Model,
        string? Effort,
        bool HasSessionDir,
        bool HasPromptFile,
        IReadOnlyList<string> FileArgs);

    public static bool IsConfiguredScriptEntry(ObservedProcess process, IReadOnlyList<string>? packages)
    {
        if (packages is not { Count: > 0 }) return false;
        if (!ImageEquals(process.ImageName, "node.exe") && !ImageEquals(process.ImagePath, "node.exe"))
            return false;
        if (!TryFindNodeEntryScript(process.CommandLine, out var script, out _))
            return false;
        if (!FileNameOf(script).Equals("cli.js", StringComparison.OrdinalIgnoreCase))
            return false;
        var norm = script.Replace('\\', '/');
        foreach (var package in packages)
        {
            if (string.IsNullOrWhiteSpace(package)) continue;
            var segment = "/" + package.Replace('\\', '/').Trim('/') + "/";
            if (norm.Contains(segment, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static bool TryReadPiLaunch(ObservedProcess process, IReadOnlyList<ProcessDiscoveryRule> rules, out PiLaunchFacts facts)
    {
        facts = default;
        if (!string.Equals(Family(process, rules), "PI", StringComparison.OrdinalIgnoreCase))
            return false;
        var args = string.IsNullOrWhiteSpace(process.CommandLine)
            ? new List<string>()
            : SplitWindowsArguments(process.CommandLine);
        if (!TryPiOptionStart(process, args, out var start))
            return false;
        string? script = null;
        if (!ImageEquals(process.ImageName, "pi.exe") && !ImageEquals(process.ImagePath, "pi.exe"))
            TryFindNodeEntryScript(args, out script, out _);

        // Installed PI parseArgs: `--` ends options. Known options are exact tokens.
        // A value option consumes the next argv even when that text looks like a flag.
        // `--name=value` is not a known option and does not set model or thinking.
        // `@path` is a file argument only when the parser does not consume it as a value.
        ReadPiOptions(args, start, out var model, out var effort, out var sessionDir, out var promptFile, out var fileArgs);

        facts = new PiLaunchFacts(script, model, effort, sessionDir, promptFile, fileArgs);
        return true;
    }

    public static bool TryReadPiFileArgs(ObservedProcess process, out IReadOnlyList<string> fileArgs)
    {
        fileArgs = Array.Empty<string>();
        var args = string.IsNullOrWhiteSpace(process.CommandLine)
            ? new List<string>()
            : SplitWindowsArguments(process.CommandLine);
        if (!TryPiOptionStart(process, args, out var start))
            return false;
        ReadPiOptions(args, start, out _, out _, out _, out _, out var found);
        fileArgs = found;
        return true;
    }

    static bool TryPiOptionStart(ObservedProcess process, IReadOnlyList<string> args, out int start)
    {
        start = 0;
        if (ImageEquals(process.ImageName, "pi.exe") || ImageEquals(process.ImagePath, "pi.exe"))
        {
            start = args.Count > 0 && IsPiExecutable(args[0]) ? 1 : 0;
            return true;
        }

        if (!TryFindNodeEntryScript(args, out var script, out var scriptIndex))
            return false;
        if (!FileNameOf(script).Equals("cli.js", StringComparison.OrdinalIgnoreCase))
            return false;
        var norm = script.Replace('\\', '/');
        if (!norm.Contains("/@earendil-works/pi-coding-agent/", StringComparison.OrdinalIgnoreCase)
            && !norm.Contains("/@mariozechner/pi-coding-agent/", StringComparison.OrdinalIgnoreCase))
            return false;
        start = scriptIndex + 1;
        return true;
    }

    public static string? Family(ObservedProcess process, IReadOnlyList<ProcessDiscoveryRule> rules)
    {
        foreach (var rule in rules)
        {
            if (Matches(process, rule))
                return rule.Cli;
        }

        return null;
    }

    public static bool IsDescendant(IReadOnlyDictionary<int, int> parents, int pid, int ancestorPid)
    {
        if (pid <= 0 || ancestorPid <= 0) return false;
        var guard = 0;
        var current = pid;
        var seen = new HashSet<int>();
        while (current > 0 && seen.Add(current) && guard++ < 64)
        {
            if (current == ancestorPid) return true;
            if (!parents.TryGetValue(current, out var parent) || parent <= 0 || parent == current)
                break;
            current = parent;
        }

        return false;
    }

    public static bool CommandContainsToken(string? commandLine, string? token)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || string.IsNullOrWhiteSpace(token))
            return false;
        return commandLine.Contains(token, StringComparison.OrdinalIgnoreCase);
    }

    public static bool CommandDeclaresMailbox(string? commandLine, string? project, string? jobId, string? attempt, string? outbox)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || string.IsNullOrWhiteSpace(outbox)
            || string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(jobId)
            || string.IsNullOrWhiteSpace(attempt) || !Path.IsPathFullyQualified(outbox)) return false;
        // Match the mechanical contract emitted by mailbox.py register, not an
        // incidental reference to another worker's output in the prompt.
        var identity = $"项目：{project}；job_id：{jobId}；attempt_id：{attempt}";
        var destination = "唯一交付目录：" + outbox.Replace('\\', '/').TrimEnd('/');
        var lines = commandLine.Replace("\r", "").Replace('\\', '/').Split('\n');
        for (var i = 0; i + 1 < lines.Length; i++)
        {
            if (lines[i].Trim() == identity
                && string.Equals(lines[i + 1].Trim().TrimEnd('/'), destination, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public static bool IsCodexImage(ObservedProcess process) =>
        ImageEquals(process.ImageName, "codex.exe") || ImageEquals(process.ImagePath, "codex.exe");

    public static bool IsCodexServiceProcess(ObservedProcess process)
    {
        if (!IsCodexImage(process) || string.IsNullOrWhiteSpace(process.CommandLine)) return false;
        var args = SplitWindowsArguments(process.CommandLine);
        if (args.Count == 0) return false;
        var commandIndex = IsCodexExecutable(args[0]) ? 1 : 0;
        return TryReadCodexSubcommand(args, commandIndex, out var command)
            && (command.Equals("app-server", StringComparison.OrdinalIgnoreCase)
                || command.Equals("mcp-server", StringComparison.OrdinalIgnoreCase)
                || command.Equals("exec-server", StringComparison.OrdinalIgnoreCase));
    }

    public static (string? Model, string? Effort) CodexArguments(ObservedProcess process)
    {
        if (!IsCodexImage(process) || IsCodexServiceProcess(process) || string.IsNullOrWhiteSpace(process.CommandLine)) return (null, null);
        var args = SplitWindowsArguments(process.CommandLine);
        string? model = null, effort = null;
        for (var i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == "--") break;
            var equal = arg.IndexOf('=');
            var name = equal > 0 ? arg[..equal] : arg;
            var needsValue = CodexValueOptions.Contains(name) || name is "-o" or "--output-last-message" or "--output-schema" or "--color";
            if (!needsValue) continue;
            var value = equal > 0 ? arg[(equal + 1)..] : i + 1 < args.Count ? args[++i] : null;
            if (value is null) break;
            if (name is "-m" or "--model") model = value;
            if (name is "-c" or "--config" && value.StartsWith("model_reasoning_effort=", StringComparison.Ordinal))
                effort = value["model_reasoning_effort=".Length..].Trim('"', '\'');
        }
        return (model, effort);
    }

    public static bool CodexInvocationMatches(ObservedProcess process, string? workspace, string? session, string? outbox)
    {
        if (!IsCodexImage(process) || IsCodexServiceProcess(process) || string.IsNullOrWhiteSpace(process.CommandLine)) return false;
        var args = SplitWindowsArguments(process.CommandLine);
        string? cwd = null, resume = null, output = null;
        for (var i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == "--") break;
            if (arg == "resume" && i + 1 < args.Count && Guid.TryParse(args[i + 1], out _)) { resume = args[++i]; continue; }
            var equal = arg.IndexOf('=');
            var name = equal > 0 ? arg[..equal] : arg;
            if (!(CodexValueOptions.Contains(name) || name is "-o" or "--output-last-message" or "--output-schema" or "--color")) continue;
            var value = equal > 0 ? arg[(equal + 1)..] : i + 1 < args.Count ? args[++i] : null;
            if (value is null) break;
            if (name is "-C" or "--cd") cwd = value;
            if (name is "-o" or "--output-last-message") output = value;
        }
        return PromptFileIdentity.SamePath(cwd, workspace)
            && (session is null || session.Equals(resume, StringComparison.Ordinal))
            && (outbox is null || output is not null && Path.IsPathFullyQualified(output)
                && PromptFileIdentity.SamePath(Path.GetDirectoryName(output), outbox));
    }

    static bool TryReadCodexSubcommand(IReadOnlyList<string> args, int index, out string command)
    {
        command = string.Empty;
        while (index < args.Count)
        {
            var argument = args[index];
            if (argument is "--" or "-h" or "--help" or "-V" or "--version")
                return false;

            // --image/ -i accepts FILE...; without Codex's full parser its boundary
            // with a following command is ambiguous, so do not suppress that process.
            if (argument is "-i" or "--image")
                return false;

            var equals = argument.IndexOf('=');
            var option = equals > 0 ? argument[..equals] : argument;
            if (CodexValueOptions.Contains(option))
            {
                if (equals >= 0)
                {
                    if (equals == argument.Length - 1) return false;
                    index++;
                }
                else
                {
                    if (index + 1 >= args.Count
                        || args[index + 1].StartsWith("-", StringComparison.Ordinal))
                        return false;
                    index += 2;
                }
                continue;
            }

            if (equals < 0 && CodexBooleanOptions.Contains(argument))
            {
                index++;
                continue;
            }

            if (argument.StartsWith("-", StringComparison.Ordinal))
                return false;

            command = argument;
            return true;
        }

        return false;
    }

    static List<string> SplitWindowsArguments(string commandLine)
    {
        var args = new List<string>();
        var i = 0;
        while (i < commandLine.Length)
        {
            while (i < commandLine.Length && char.IsWhiteSpace(commandLine[i])) i++;
            if (i >= commandLine.Length) break;

            var value = new StringBuilder();
            var quoted = false;
            while (i < commandLine.Length)
            {
                if (commandLine[i] == '\\')
                {
                    var slashes = 0;
                    while (i < commandLine.Length && commandLine[i] == '\\')
                    {
                        slashes++;
                        i++;
                    }

                    if (i < commandLine.Length && commandLine[i] == '\"')
                    {
                        value.Append('\\', slashes / 2);
                        if (slashes % 2 == 1)
                            value.Append('"');
                        else
                            quoted = !quoted;
                        i++;
                    }
                    else
                    {
                        value.Append('\\', slashes);
                    }

                    continue;
                }

                if (commandLine[i] == '\"')
                {
                    quoted = !quoted;
                    i++;
                    continue;
                }

                if (!quoted && char.IsWhiteSpace(commandLine[i])) break;
                value.Append(commandLine[i++]);
            }

            args.Add(value.ToString());
        }

        return args;
    }

    static readonly HashSet<string> NodeValueOptions = new(StringComparer.Ordinal)
    {
        "-e", "--eval", "-p", "--print",
        "-r", "--require", "--import",
        "--loader", "--experimental-loader",
        "--experimental-policy", "--policy",
        "--title", "--redirect-warnings",
        "--cpu-prof-dir", "--heap-prof-dir", "--diagnostic-dir",
        "--report-dir", "--trace-event-file-pattern",
        "--env-file", "--test-reporter", "--watch-path",
        "-C", "--conditions",
        "--experimental-specifier-resolution",
        "--max-http-header-size",
        "--secure-heap", "--secure-heap-min",
        "--v8-pool-size"
    };

    static bool TryFindNodeEntryScript(string? commandLine, out string script, out int scriptIndex)
    {
        script = string.Empty;
        scriptIndex = -1;
        if (string.IsNullOrWhiteSpace(commandLine)) return false;
        return TryFindNodeEntryScript(SplitWindowsArguments(commandLine), out script, out scriptIndex);
    }

    static bool TryFindNodeEntryScript(IReadOnlyList<string> args, out string script, out int scriptIndex)
    {
        script = string.Empty;
        scriptIndex = -1;
        var index = IndexOfExecutable(args, "node", "node.exe");
        var i = index >= 0 ? index + 1 : 0;
        while (i < args.Count)
        {
            var argument = args[i];
            if (argument == "--")
            {
                i++;
                break;
            }

            if (IsNodeEval(argument))
                return false;

            if (argument.StartsWith("-", StringComparison.Ordinal))
            {
                var equals = argument.IndexOf('=');
                var option = equals > 0 ? argument[..equals] : argument;
                if (equals < 0 && NodeValueOptions.Contains(option))
                {
                    i += 2;
                    continue;
                }

                i++;
                continue;
            }

            script = argument;
            scriptIndex = i;
            return !string.IsNullOrWhiteSpace(script);
        }

        if (i < args.Count && !args[i].StartsWith("-", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(args[i]))
        {
            script = args[i];
            scriptIndex = i;
            return true;
        }

        return false;
    }

    static bool IsNodeEval(string argument)
    {
        if (argument is "-e" or "--eval" or "-p" or "--print") return true;
        return argument.StartsWith("--eval=", StringComparison.Ordinal)
               || argument.StartsWith("--print=", StringComparison.Ordinal);
    }

    static readonly HashSet<string> PiBooleanOptions = new(StringComparer.Ordinal)
    {
        "--help", "-h", "--version", "-v",
        "--continue", "-c", "--resume", "-r",
        "--no-session",
        "--no-tools", "-nt", "--no-builtin-tools", "-nbt",
        "--no-extensions", "-ne",
        "--no-skills", "-ns", "--no-prompt-templates", "-np",
        "--no-themes",
        "--no-context-files", "-nc",
        "--verbose",
        "--approve", "-a", "--no-approve", "-na",
        "--offline"
    };

    static readonly HashSet<string> PiValueOptions = new(StringComparer.Ordinal)
    {
        "--provider", "--model", "--api-key",
        "--system-prompt", "--append-system-prompt",
        "--session", "--session-id", "--fork", "--session-dir", "--models",
        "--tools", "-t", "--exclude-tools", "-xt",
        "--thinking", "--export",
        "--extension", "-e", "--skill", "--prompt-template", "--theme",
        "--name", "-n"
    };

    static readonly HashSet<string> PiThinkingLevels = new(StringComparer.Ordinal)
    {
        "off", "minimal", "low", "medium", "high", "xhigh", "max"
    };

    static void ReadPiOptions(
        IReadOnlyList<string> args,
        int start,
        out string? model,
        out string? effort,
        out bool sessionDir,
        out bool promptFile,
        out List<string> fileArgs)
    {
        model = null;
        effort = null;
        sessionDir = false;
        promptFile = false;
        fileArgs = new List<string>();
        for (var i = start; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == "--")
            {
                for (var j = i + 1; j < args.Count; j++)
                    RememberPiFile(fileArgs, args[j]);
                break;
            }

            if (PiBooleanOptions.Contains(arg))
                continue;

            if (PiValueOptions.Contains(arg))
            {
                if (i + 1 < args.Count)
                {
                    var value = args[++i];
                    if (arg == "--model")
                        model = value;
                    else if (arg == "--thinking")
                    {
                        if (PiThinkingLevels.Contains(value))
                            effort = value;
                    }
                    else if (arg == "--session-dir")
                        sessionDir = true;
                }

                continue;
            }

            if (arg is "--mode" or "--use-theme")
            {
                if (i + 1 < args.Count && !args[i + 1].StartsWith('-'))
                    i++;
                continue;
            }

            if (arg is "--print" or "-p")
            {
                if (i + 1 < args.Count)
                {
                    var next = args[i + 1];
                    if (!next.StartsWith('@') && (!next.StartsWith('-') || next.StartsWith("---", StringComparison.Ordinal)))
                        i++;
                }

                continue;
            }

            if (arg == "--list-models")
            {
                if (i + 1 < args.Count && !args[i + 1].StartsWith('-') && !args[i + 1].StartsWith('@'))
                    i++;
                continue;
            }

            if (arg == "--tui-mode")
            {
                if (i + 1 < args.Count)
                {
                    var mode = args[i + 1];
                    if (mode is "regular" or "fullscreen" || !mode.StartsWith('-'))
                        i++;
                }

                continue;
            }

            if (arg.StartsWith('@'))
            {
                RememberPiFile(fileArgs, arg);
                continue;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                var eq = arg.IndexOf('=');
                if (eq >= 0)
                {
                    if (arg[2..eq] == "prompt-file")
                        promptFile = true;
                    continue;
                }

                var flagName = arg[2..];
                if (i + 1 < args.Count && !args[i + 1].StartsWith('-') && !args[i + 1].StartsWith('@'))
                    i++;
                if (flagName == "prompt-file")
                    promptFile = true;
                continue;
            }
        }
    }

    static void RememberPiFile(List<string> fileArgs, string arg)
    {
        if (arg.Length > 1 && arg[0] == '@')
            fileArgs.Add(arg[1..]);
    }

    static bool IsClaudeCodeCli(ObservedProcess process)
    {
        var args = string.IsNullOrWhiteSpace(process.CommandLine)
            ? new List<string>()
            : SplitWindowsArguments(process.CommandLine);
        var argv0 = args.Count > 0 ? args[0] : null;
        if (HasDirectorySegment(process.ImagePath, "AnthropicClaude")
            || HasDirectorySegment(process.ImageName, "AnthropicClaude")
            || HasDirectorySegment(argv0, "AnthropicClaude"))
            return false;
        var claudeCode = HasDirectorySegment(process.ImagePath, "claude-code")
            || HasDirectorySegment(process.ImageName, "claude-code")
            || HasDirectorySegment(argv0, "claude-code");
        if (!claudeCode) return false;
        return !HasClaudeDesktopRole(args);
    }

    static bool HasDirectorySegment(string? path, string segment)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(segment))
            return false;
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (part.Equals(segment, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    static readonly HashSet<string> ClaudeDesktopRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "renderer", "gpu-process", "utility", "crashpad-handler"
    };

    // Root command of claude.exe 2.1.284. <argument> consumes the next argv even when it
    // starts with '-'. [argument] consumes the next argv only when it does not.
    // -p/--print has no argument. '--' ends options; later tokens are the prompt.
    static readonly HashSet<string> ClaudeRequiredValueOptions = new(StringComparer.Ordinal)
    {
        "add-dir", "advisor", "agent", "agent-color", "agent-id", "agent-name", "agent-type",
        "agents", "allowed-tools", "allowedTools", "append-subagent-system-prompt",
        "append-subagent-system-prompt-file", "append-system-prompt", "append-system-prompt-file",
        "attach-serve", "autocompact", "betas", "channels", "client-data-url", "correlation-id",
        "dangerously-load-development-channels", "debug-file", "deep-link-cwd-b64",
        "deep-link-last-fetch", "deep-link-repo", "disallowed-tools", "disallowedTools",
        "effort", "environment", "fallback-model", "file", "forward-home-settings",
        "inherit-permission-mode", "input-format", "json-schema", "managed-settings",
        "max-budget-usd", "max-thinking-tokens", "max-turns", "mcp-config", "messaging-socket-path",
        "model", "name", "on-branch", "output-format", "parent-session-id", "permission-mode",
        "permission-prompt-tool", "permission-prompts", "plan-mode-instructions", "plugin-dir",
        "plugin-dir-no-mcp", "plugin-url", "pool", "prefill", "prefill-b64", "project-config-root",
        "ref", "remote-control-session-name-prefix", "resume-drops-turn", "resume-session-at",
        "rewind-files", "sdk-url", "session-id", "setting-sources", "settings", "system-prompt",
        "system-prompt-file", "system-prompt-snapshot", "task-budget", "team-name", "teammate-mode",
        "thinking", "thinking-display", "tools", "watch-artifact", "watch-artifact-no-autoreact",
        "workload"
    };

    static readonly HashSet<string> ClaudeOptionalValueOptions = new(StringComparer.Ordinal)
    {
        "cloud", "debug", "from-pr", "prompt-suggestions", "rc", "remote", "remote-control",
        "resume", "teleport", "worktree"
    };

    static bool HasClaudeDesktopRole(IReadOnlyList<string> args)
    {
        for (var i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == "--")
                break;
            if (IsClaudeRoleSwitch(arg))
                return true;
            if (arg.Equals("--type", StringComparison.Ordinal)
                && i + 1 < args.Count
                && ClaudeDesktopRoles.Contains(args[i + 1]))
                return true;
            if (TryClaudeValueOption(arg, out var required))
            {
                if (i + 1 < args.Count && (required || !args[i + 1].StartsWith('-')))
                    i++;
                continue;
            }

            if (IsCrashpadHandlerToken(arg))
                return true;
        }

        return false;
    }

    static bool IsClaudeRoleSwitch(string arg) =>
        arg.StartsWith("--type=", StringComparison.Ordinal)
        && ClaudeDesktopRoles.Contains(arg["--type=".Length..]);

    static bool TryClaudeValueOption(string arg, out bool required)
    {
        required = false;
        if (arg.Equals("-n", StringComparison.Ordinal))
        {
            required = true;
            return true;
        }

        if (arg is "-d" or "-r" or "-w")
            return true;
        if (!arg.StartsWith("--", StringComparison.Ordinal) || arg.Length < 3 || arg.Contains('='))
            return false;
        var name = arg[2..];
        if (ClaudeRequiredValueOptions.Contains(name))
        {
            required = true;
            return true;
        }

        return ClaudeOptionalValueOptions.Contains(name);
    }

    static bool IsCrashpadHandlerToken(string arg)
    {
        var name = FileNameOf(arg);
        if (name.Equals("crashpad-handler.exe", StringComparison.OrdinalIgnoreCase))
            return true;
        return name.Equals("crashpad-handler", StringComparison.OrdinalIgnoreCase)
            && arg.IndexOfAny(new[] { '/', '\\' }) >= 0;
    }

    static int IndexOfExecutable(IReadOnlyList<string> args, params string[] names)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var name = FileNameOf(args[i]);
            foreach (var expected in names)
            {
                if (name.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
        }

        return -1;
    }

    static bool IsPiExecutable(string value)
    {
        var name = FileNameOf(value);
        return name.Equals("pi", StringComparison.OrdinalIgnoreCase)
               || name.Equals("pi.exe", StringComparison.OrdinalIgnoreCase);
    }

    static string FileNameOf(string path)
    {
        var norm = path.Replace('\\', '/');
        var slash = norm.LastIndexOf('/');
        return slash >= 0 ? norm[(slash + 1)..] : norm;
    }

    static bool IsCodexExecutable(string value)
    {
        var name = value.Replace('\\', '/').Split('/').LastOrDefault() ?? value;
        return name.Equals("codex", StringComparison.OrdinalIgnoreCase)
            || name.Equals("codex.exe", StringComparison.OrdinalIgnoreCase);
    }

    static bool ImageEquals(string? actual, string? expected)
    {
        if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(expected))
            return false;
        var a = actual.Replace('\\', '/').Split('/').LastOrDefault() ?? actual;
        var e = expected.Replace('\\', '/').Split('/').LastOrDefault() ?? expected;
        return a.Equals(e, StringComparison.OrdinalIgnoreCase)
               || a.Equals(e + ".exe", StringComparison.OrdinalIgnoreCase)
               || (a + ".exe").Equals(e, StringComparison.OrdinalIgnoreCase);
    }
}
