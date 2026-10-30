using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodexUsageHud.Core;

public static class AntigravityQuota
{
    public const string SourceDescription = "agy --print /usage · 订阅模型额度";
    public const string ShareNote =
        "同一组模型共用该组的每周额度和 5 小时额度。不同组和不同窗口都不能相加。这里只显示订阅额度，不把 G1 付费 credits 换成百分比。";
    public const int PrintTimeoutSeconds = 20;
    public const int OutputKeepChars = 262144;

    public static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(PrintTimeoutSeconds + 8);
}

public enum AntigravityUsageKind
{
    Json,
    NotFound,
    TimedOut,
    Failed,
    SignInRequired,
    LoginExpired,
    RegionUnavailable,
    RateLimited,
}

public sealed record AntigravityUsageRead(AntigravityUsageKind Kind, string? Json);

public interface IAntigravityUsageClient
{
    Task<AntigravityUsageRead> ReadUsageAsync(CancellationToken cancellationToken);
}

public sealed class AntigravityCliClient : IAntigravityUsageClient
{
    public async Task<AntigravityUsageRead> ReadUsageAsync(CancellationToken cancellationToken)
    {
        var executable = ResolveExecutable();
        if (executable is null)
            return new AntigravityUsageRead(AntigravityUsageKind.NotFound, null);

        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // /usage is answered by the CLI itself. Do not add a model prompt.
        start.ArgumentList.Add("-p");
        start.ArgumentList.Add("/usage");
        start.ArgumentList.Add("--output-format");
        start.ArgumentList.Add("json");
        start.ArgumentList.Add("--print-timeout");
        start.ArgumentList.Add(AntigravityQuota.PrintTimeoutSeconds.ToString(CultureInfo.InvariantCulture) + "s");

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
                return new AntigravityUsageRead(AntigravityUsageKind.Failed, null);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new AntigravityUsageRead(AntigravityUsageKind.NotFound, null);
        }

        using var killOnCancel = cancellationToken.Register(() => Kill(process));
        var stdoutTask = ReadBoundedAsync(process.StandardOutput, AntigravityQuota.OutputKeepChars, cancellationToken);
        var stderrTask = ReadBoundedAsync(process.StandardError, 8192, cancellationToken);
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync(cancellationToken))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Kill(process);
            try
            {
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return Interpret(process.ExitCode, stdout, stderr);
    }

    internal static AntigravityUsageRead Interpret(int exitCode, string stdout, string stderr)
    {
        if (exitCode == 0 && LooksLikeJson(stdout))
            return new AntigravityUsageRead(AntigravityUsageKind.Json, stdout.Trim());

        return new AntigravityUsageRead(ClassifyFailure(stdout + "\n" + stderr, exitCode), null);
    }

    internal static string? ResolveExecutable()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(entry.Trim(), "agy.exe");
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static AntigravityUsageKind ClassifyFailure(string text, int exitCode)
    {
        if (ContainsAny(text, "rate limit", "too many requests", "429"))
            return AntigravityUsageKind.RateLimited;
        if (ContainsAny(text, "sign in", "sign-in", "not logged", "not signed", "login required",
                "unauthenticated", "please log in"))
            return AntigravityUsageKind.SignInRequired;
        if (ContainsAny(text, "token expired", "session expired", "login expired", "登录已过期", "已过期"))
            return AntigravityUsageKind.LoginExpired;
        if (ContainsAny(text, "not available in your country", "region", "location is not", "地区"))
            return AntigravityUsageKind.RegionUnavailable;
        if (ContainsAny(text, "timed out", "timeout", "deadline"))
            return AntigravityUsageKind.TimedOut;
        _ = exitCode;
        return AntigravityUsageKind.Failed;
    }

    private static bool ContainsAny(string text, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool LooksLikeJson(string text)
    {
        var trimmed = text.Trim();
        return trimmed.StartsWith('{') && trimmed.EndsWith('}');
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int keep,
        CancellationToken cancellationToken)
    {
        var kept = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (kept.Length < keep)
            {
                var take = Math.Min(read, keep - kept.Length);
                kept.Append(buffer, 0, take);
            }
        }

        return kept.ToString();
    }
}

public static class AntigravityQuotaParser
{
    public static IReadOnlyList<QuotaWindowObservation> Parse(string? json, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "antigravity_parse_failed";
            return Array.Empty<QuotaWindowObservation>();
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            error = "antigravity_parse_failed";
            return Array.Empty<QuotaWindowObservation>();
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "antigravity_parse_failed";
                return Array.Empty<QuotaWindowObservation>();
            }

            if (!root.TryGetProperty("status", out var statusNode) ||
                statusNode.ValueKind != JsonValueKind.String ||
                !string.Equals(statusNode.GetString(), "SUCCESS", StringComparison.Ordinal))
            {
                error = ClassifyStatus(root);
                return Array.Empty<QuotaWindowObservation>();
            }

            if (!root.TryGetProperty("command", out var command) || command.ValueKind != JsonValueKind.Object ||
                !command.TryGetProperty("name", out var nameNode) || nameNode.ValueKind != JsonValueKind.String ||
                !string.Equals(nameNode.GetString(), "usage", StringComparison.Ordinal) ||
                !command.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
            {
                error = "antigravity_parse_failed";
                return Array.Empty<QuotaWindowObservation>();
            }

            var windows = new List<QuotaWindowObservation>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in groups.EnumerateArray())
            {
                if (group.ValueKind != JsonValueKind.Object) continue;
                var groupName = ReadString(group, "name");
                if (IsCredit(groupName, null)) continue;
                if (!group.TryGetProperty("buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var bucket in buckets.EnumerateArray())
                {
                    if (bucket.ValueKind != JsonValueKind.Object) continue;
                    var bucketName = ReadString(bucket, "name");
                    var bucketId = ReadString(bucket, "id");
                    if (IsCredit(groupName, bucketId) || IsCredit(bucketName, bucketId)) continue;
                    if (!TryRemaining(bucket, out var used)) continue;
                    var id = SanitizeId(bucketId);
                    if (id.Length == 0 || !seen.Add(id)) continue;
                    int? minutes = WindowMinutes(ReadString(bucket, "window"));
                    DateTimeOffset? reset = TryReset(bucket);
                    windows.Add(new QuotaWindowObservation(id, DisplayName(groupName, bucket),
                        used, reset, minutes, true));
                }
            }

            if (windows.Count == 0)
                error = "antigravity_schema_unsupported";
            return windows;
        }
    }

    private static string ClassifyStatus(JsonElement root)
    {
        var text = string.Empty;
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
            text = error.GetString() ?? string.Empty;
        if (text.Contains("429", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("rate", StringComparison.OrdinalIgnoreCase))
            return "antigravity_rate_limited";
        if (text.Contains("region", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("country", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("地区", StringComparison.Ordinal))
            return "antigravity_region_unavailable";
        if (text.Contains("expired", StringComparison.OrdinalIgnoreCase) || text.Contains("过期", StringComparison.Ordinal))
            return "antigravity_login_expired";
        if (text.Contains("sign", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("login", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("auth", StringComparison.OrdinalIgnoreCase))
            return "antigravity_sign_in_required";
        return "antigravity_cli_failed";
    }

    private static bool TryRemaining(JsonElement bucket, out double used)
    {
        used = 0;
        if (!bucket.TryGetProperty("remaining_fraction", out var node) || node.ValueKind != JsonValueKind.Number ||
            !node.TryGetDouble(out var fraction) || !double.IsFinite(fraction) || fraction < 0d || fraction > 1d)
            return false;
        used = 100d - (fraction * 100d);
        return true;
    }

    private static DateTimeOffset? TryReset(JsonElement bucket)
    {
        var text = ReadString(bucket, "reset_time");
        if (text.Length == 0) return null;
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            return null;
        return parsed.ToUniversalTime();
    }

    private static int? WindowMinutes(string window)
    {
        if (window.Equals("weekly", StringComparison.OrdinalIgnoreCase) ||
            window.Equals("week", StringComparison.OrdinalIgnoreCase))
            return 7 * 24 * 60;
        if (window.Equals("5h", StringComparison.OrdinalIgnoreCase) ||
            window.Equals("5-hour", StringComparison.OrdinalIgnoreCase) ||
            window.Equals("five_hour", StringComparison.OrdinalIgnoreCase))
            return 5 * 60;
        return null;
    }

    private static string DisplayName(string groupName, JsonElement bucket)
    {
        var pool = GroupLabel(groupName);
        var window = ReadString(bucket, "window");
        var period = window.ToLowerInvariant() switch
        {
            "weekly" or "week" => "每周",
            "5h" or "5-hour" or "five_hour" => "5 小时",
            _ => string.Empty,
        };
        if (period.Length == 0)
        {
            var bucketName = ReadString(bucket, "name");
            period = bucketName.Length == 0 ? "窗口" : TrimDisplay(bucketName);
        }

        return pool + " · " + period;
    }

    private static string GroupLabel(string name)
    {
        if (name.Contains("argon", StringComparison.OrdinalIgnoreCase)) return "Gemini 4 Argon";
        if (name.Contains("claude", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("gpt", StringComparison.OrdinalIgnoreCase))
            return "Claude 与 GPT";
        if (name.Contains("gemini", StringComparison.OrdinalIgnoreCase)) return "Gemini 模型";
        return name.Length == 0 ? "额度组" : TrimDisplay(name);
    }

    private static string TrimDisplay(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character is '\r' or '\n' or '\t')
            {
                if (builder.Length > 0 && builder[^1] != ' ') builder.Append(' ');
                continue;
            }

            builder.Append(character);
            if (builder.Length >= 40) break;
        }

        return builder.ToString().Trim();
    }

    private static bool IsCredit(string? name, string? id)
    {
        var text = ((name ?? string.Empty) + " " + (id ?? string.Empty)).ToLowerInvariant();
        return text.Contains("credit", StringComparison.Ordinal) ||
               text.Contains("g1-", StringComparison.Ordinal) ||
               text.Contains("g1_", StringComparison.Ordinal) ||
               text.Contains(" g1", StringComparison.Ordinal);
    }

    private static string ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var node) || node.ValueKind != JsonValueKind.String)
            return string.Empty;
        return (node.GetString() ?? string.Empty).Trim();
    }

    private static string SanitizeId(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character is '_' or '-' || char.IsAsciiLetterOrDigit(character))
                builder.Append(character);
            if (builder.Length >= 64) break;
        }

        return builder.ToString();
    }
}

public sealed class AntigravityQuotaAdapter
{
    private readonly IAntigravityUsageClient _client;
    private readonly Func<DateTimeOffset> _clock;

    public AntigravityQuotaAdapter(IAntigravityUsageClient? client = null, Func<DateTimeOffset>? clock = null)
    {
        _client = client ?? new AntigravityCliClient();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<ProviderSlotSnapshot> ReadAsync(ProviderSlotSettings settings,
        CancellationToken cancellationToken)
    {
        if (!settings.Enabled)
            return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.Disabled, "未启用",
                "用户关闭了 Antigravity 槽", "slot_disabled");

        AntigravityUsageRead read;
        try
        {
            read = await _client.ReadUsageAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return Unavailable(settings, "Antigravity 额度读取失败，不影响其他槽", "独立失败", "antigravity_cli_failed");
        }

        switch (read.Kind)
        {
            case AntigravityUsageKind.NotFound:
                return Unavailable(settings, "未找到 Antigravity CLI（agy）", "PATH 中没有 agy.exe",
                    "antigravity_cli_missing");
            case AntigravityUsageKind.TimedOut:
                return Unavailable(settings, "Antigravity 额度读取超时", "独立超时", "antigravity_timeout");
            case AntigravityUsageKind.SignInRequired:
                return Disconnected(settings, "未连接：本机 agy 未登录", "需要官方 CLI 已登录",
                    "antigravity_sign_in_required");
            case AntigravityUsageKind.LoginExpired:
                return Disconnected(settings, "未连接：Antigravity 登录已过期", "不刷新、不改写凭据",
                    "antigravity_login_expired");
            case AntigravityUsageKind.RegionUnavailable:
                return Unavailable(settings, "Antigravity 当前地区不可用", "官方 CLI 拒绝读取额度",
                    "antigravity_region_unavailable");
            case AntigravityUsageKind.RateLimited:
                return Unavailable(settings, "Antigravity 额度读取被限流，不显示猜测百分比", "独立限流",
                    "antigravity_rate_limited");
            case AntigravityUsageKind.Failed:
                return Unavailable(settings, "Antigravity 额度读取失败，不影响其他槽", "独立失败",
                    "antigravity_cli_failed");
        }

        var windows = AntigravityQuotaParser.Parse(read.Json, out var error);
        if (windows.Count == 0)
        {
            return error switch
            {
                "antigravity_rate_limited" => Unavailable(settings, "Antigravity 额度读取被限流，不显示猜测百分比",
                    "独立限流", error),
                "antigravity_region_unavailable" => Unavailable(settings, "Antigravity 当前地区不可用",
                    "官方 CLI 拒绝读取额度", error),
                "antigravity_login_expired" => Disconnected(settings, "未连接：Antigravity 登录已过期",
                    "不刷新、不改写凭据", error),
                "antigravity_sign_in_required" => Disconnected(settings, "未连接：本机 agy 未登录",
                    "需要官方 CLI 已登录", error),
                "antigravity_cli_failed" => Unavailable(settings, "Antigravity 额度读取失败，不影响其他槽",
                    "官方 CLI 没有返回额度", error),
                _ => Unavailable(settings, "Antigravity 额度格式无法识别，不显示猜测百分比",
                    "缺少可用的订阅额度窗口", error ?? "antigravity_parse_failed"),
            };
        }

        var now = _clock();
        var current = windows.Any(window => !window.ResetsAtUtc.HasValue || window.ResetsAtUtc.Value > now);
        return new ProviderSlotSnapshot(settings.SlotId, ProviderIds.Antigravity, settings.Label, true, false,
            current ? QuotaSlotStatus.Live : QuotaSlotStatus.Stale,
            current ? "Antigravity 订阅额度" : "窗口已到期，不作为当前额度",
            now, AntigravityQuota.SourceDescription, windows, ErrorCode: error);
    }

    private static ProviderSlotSnapshot Disconnected(ProviderSlotSettings settings, string status, string source,
        string code) =>
        ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.NotConnected, status, source, code);

    private static ProviderSlotSnapshot Unavailable(ProviderSlotSettings settings, string status, string source,
        string code) =>
        ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.Unavailable, status, source, code);
}
