using System.Net.Http;

namespace CodexUsageHud.Core;

public class CodexQuotaProfile
{
    private readonly AppServerClient _client;
    private readonly CodexExecutableDiscovery _discovery;
    private readonly string? _executableOverride;

    public CodexQuotaProfile(AppServerClient? client = null, CodexExecutableDiscovery? discovery = null,
        string? executableOverride = null)
    {
        _client = client ?? new AppServerClient();
        _discovery = discovery ?? new CodexExecutableDiscovery();
        _executableOverride = executableOverride;
    }

    public virtual async Task<ProviderSlotSnapshot> ReadAsync(ProviderSlotSettings settings, string? isolatedHome,
        bool suppliesLocalAnalysis, CancellationToken cancellationToken, string? primaryHome = null)
    {
        _ = primaryHome;
        if (!settings.Enabled)
        {
            return ProviderQuotaPresentation.Placeholder(settings, suppliesLocalAnalysis,
                QuotaSlotStatus.Disabled, "未启用", "用户关闭了此槽", "slot_disabled");
        }

        if (!string.IsNullOrWhiteSpace(isolatedHome))
        {
            var comparedPrimary = suppliesLocalAnalysis ? null : primaryHome;
            var homeStatus = SecondaryCodexHome.Classify(isolatedHome, comparedPrimary);
            if (homeStatus == SecondaryHomeStatus.SameAsPrimary)
            {
                return ProviderQuotaPresentation.Placeholder(settings, false,
                    QuotaSlotStatus.SetupRequired, SecondaryCodexHome.SameStatusText,
                    SecondaryCodexHome.SameDetail, SecondaryCodexHome.SameCode);
            }

            if (homeStatus == SecondaryHomeStatus.MissingPath)
            {
                return ProviderQuotaPresentation.Placeholder(settings, suppliesLocalAnalysis,
                    QuotaSlotStatus.SetupRequired, SecondaryCodexHome.InvalidStatusText,
                    SecondaryCodexHome.InvalidDetail, SecondaryCodexHome.InvalidCode);
            }
        }

        var executable = _discovery.Find(_executableOverride);
        if (string.IsNullOrWhiteSpace(executable))
        {
            return ProviderQuotaPresentation.Placeholder(settings, suppliesLocalAnalysis,
                QuotaSlotStatus.Unavailable, "未找到 Codex 可执行文件", "codex.exe 不可用",
                "codex_executable_missing");
        }

        IReadOnlyDictionary<string, string>? environment = null;
        if (!string.IsNullOrWhiteSpace(isolatedHome))
        {
            environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["CODEX_HOME"] = Path.GetFullPath(isolatedHome),
            };
        }

        var combined = await _client.ReadQuotaAndIdentityAsync(executable, cancellationToken, environment)
            .ConfigureAwait(false);
        var result = combined.Quota;
        var identity = combined.Identity?.Hash;
        var flags = AppendPlanType(combined.IdentityShape?.Format() ?? combined.Identity?.PresenceFlags,
            result.Observation.PlanType ?? combined.PlanType);
        var now = DateTimeOffset.UtcNow;
        var windows = ProviderQuotaPresentation.FromCodex(result.Observation);
        if (windows.Count == 0)
        {
            var code = result.ErrorCode ?? result.Observation.ErrorCode ?? "quota_unavailable";
            var status = code is "codex_executable_missing" or "app_server_launch" or "app_server_initialize_exit"
                or "app_server_rate_limits_exit"
                ? QuotaSlotStatus.NotConnected
                : QuotaSlotStatus.Unavailable;
            var text = status == QuotaSlotStatus.NotConnected
                ? "未连接：App Server 无法读取该主目录登录态"
                : "官方额度不可用";
            var failedSource = suppliesLocalAnalysis
                ? "Codex App Server account/rateLimits/read"
                : SecondaryCodexHome.CliSourceDescription;
            return new ProviderSlotSnapshot(settings.SlotId, ProviderIds.Codex, settings.Label, true,
                suppliesLocalAnalysis, status, text, now, failedSource,
                Array.Empty<QuotaWindowObservation>(), identity, code, FieldPresenceFlags: flags);
        }

        var expired = windows.All(window => window.ResetsAtUtc.HasValue && window.ResetsAtUtc.Value <= now);
        var statusLive = expired || result.Observation.IsStale
            ? QuotaSlotStatus.Stale
            : ProviderQuotaPresentation.CodexStatus(result.Observation, now);
        var statusText = statusLive == QuotaSlotStatus.Stale ? "陈旧观测，不作为实时额度" : "官方 App Server";
        var source = suppliesLocalAnalysis
            ? "Codex App Server account/rateLimits/read · 子进程隔离 CODEX_HOME"
            : SecondaryCodexHome.CliSourceDescription;
        return new ProviderSlotSnapshot(settings.SlotId, ProviderIds.Codex, settings.Label, true,
            suppliesLocalAnalysis, statusLive, statusText, result.Observation.ObservedAtUtc,
            source, windows, identity, result.ErrorCode, identity, FieldPresenceFlags: flags);
    }

    public async Task<string?> ReadIdentityAsync(string? isolatedHome, CancellationToken cancellationToken)
    {
        try
        {
            var executable = _discovery.Find(_executableOverride);
            if (string.IsNullOrWhiteSpace(executable)) return null;
            IReadOnlyDictionary<string, string>? environment = null;
            if (!string.IsNullOrWhiteSpace(isolatedHome))
            {
                environment = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["CODEX_HOME"] = Path.GetFullPath(isolatedHome),
                };
            }

            return await _client.ReadAccountIdentityAsync(executable, cancellationToken, environment)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? AppendPlanType(string? flags, string? planType)
    {
        if (string.IsNullOrWhiteSpace(planType)) return flags;
        var marker = "plan_type=" + planType;
        return string.IsNullOrWhiteSpace(flags) ? marker : flags + " " + marker;
    }
}

internal static class CursorSandAccess
{
    public static AllowlistedHttpRequest Request(string cookie) => new("POST",
        ProviderHttpAllowlist.CursorSandUsage,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Cookie"] = "WorkosCursorSessionToken=" + cookie,
            ["Origin"] = "https://cursor.com",
            ["Accept"] = "application/json",
        }, "{}");

    public static ProviderSlotSnapshot SharedSnapshot(ProviderSlotSettings settings, CursorSandParse sand,
        DateTimeOffset now, string? identity)
    {
        if (sand.Windows.Count == 0)
        {
            var elapsed = string.Equals(sand.ErrorCode, "shared_weekly_period_elapsed", StringComparison.Ordinal);
            var excluded = string.Equals(sand.ErrorCode, "shared_weekly_not_included", StringComparison.Ordinal);
            var status = elapsed
                ? QuotaSlotStatus.Stale
                : excluded ? QuotaSlotStatus.NoAllowance : QuotaSlotStatus.Unavailable;
            var text = elapsed
                ? "已结束的周期，不显示为当前额度"
                : excluded
                    ? "当前计划未包含共享周额度，不显示 100% 可用"
                    : "共享周额度无法确认，不显示月度 100%";
            return ProviderQuotaPresentation.Placeholder(settings, false, status, text, SharedWeeklyQuota.SourceUrl,
                sand.ErrorCode);
        }

        var stale = sand.Windows.Any(window => window.ResetsAtUtc.HasValue && window.ResetsAtUtc.Value <= now);
        return new ProviderSlotSnapshot(settings.SlotId, ProviderQuotaPresentation.ProviderFor(settings.SlotId),
            settings.Label, true, false, stale ? QuotaSlotStatus.Stale : QuotaSlotStatus.Live,
            stale ? "陈旧观测" : SharedWeeklyQuota.StatusText, now, SharedWeeklyQuota.SourceUrl, sand.Windows,
            identity, sand.ErrorCode, identity, FieldPresenceFlags: "isUnified=1");
    }
}

public sealed class CursorQuotaAdapter
{
    private readonly IAllowlistedHttpSender _http;
    private readonly ICursorTokenSource _tokens;

    public CursorQuotaAdapter(IAllowlistedHttpSender http, ICursorTokenSource tokens)
    {
        _http = http;
        _tokens = tokens;
    }

    public async Task<ProviderSlotSnapshot> ReadAsync(ProviderSlotSettings settings,
        CancellationToken cancellationToken)
    {
        if (!settings.Enabled)
            return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.Disabled, "未启用",
                "用户关闭了 Cursor 槽", "slot_disabled");

        string? token;
        try { token = _tokens.ReadAccessToken(); }
        catch (Exception)
        {
            return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.NotConnected,
                "无法读取本机 Cursor 登录", "仅读取 cursorAuth/accessToken", "cursor_token_unavailable");
        }

        if (string.IsNullOrWhiteSpace(token) ||
            !CursorSessionCookie.TryBuild(token, out var cookie, out var identity) ||
            identity is null)
        {
            return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.NotConnected,
                "未连接：本机没有可用的 Cursor 登录", "需要编辑器已登录", "cursor_sign_in_required");
        }
        try
        {
            var sandResponse = await _http.SendAsync(CursorSandAccess.Request(cookie), cancellationToken)
                .ConfigureAwait(false);
            if (sandResponse.StatusCode is 401 or 403)
            {
                return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.NotConnected,
                    "未连接：Cursor 登录已失效", "get-sand-usage-status 拒绝", "cursor_sand_unauthorized");
            }

            if (sandResponse.StatusCode < 200 || sandResponse.StatusCode >= 300)
            {
                return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.Unavailable,
                    "Cursor 额度模式无法确认，不沿用月度 100%", "get-sand-usage-status HTTP " + sandResponse.StatusCode,
                    "cursor_sand_http_" + sandResponse.StatusCode);
            }

            var now = DateTimeOffset.UtcNow;
            var sand = GrokBotQuotaParser.Read(sandResponse.Body, now);
            if (sand.Mode == CursorAllowanceMode.Unified)
                return CursorSandAccess.SharedSnapshot(settings, sand, now, identity);
            if (sand.Mode != CursorAllowanceMode.Legacy)
            {
                var code = sand.ErrorCode is "cursor_sand_mode_invalid" or "grok_bot_json_invalid"
                    ? sand.ErrorCode
                    : "cursor_mode_unconfirmed";
                return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.Unavailable,
                    "无法确认 Cursor 额度模式，不沿用月度 100%", "get-sand-usage-status 没有明确的 isUnified", code);
            }

            return await ReadLegacySummaryAsync(settings, cookie, identity, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.Unavailable,
                "Cursor 请求超时", "独立超时", "cursor_timeout");
        }
        catch (InvalidOperationException exception)
        {
            var code = ProviderHttpErrors.Sanitize(exception);
            return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.Unavailable,
                "Cursor 请求被拒绝", code, code);
        }
        catch (HttpRequestException)
        {
            return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.Unavailable,
                "Cursor 网络不可用", "独立失败", "cursor_network");
        }
    }

    private async Task<ProviderSlotSnapshot> ReadLegacySummaryAsync(ProviderSlotSettings settings, string cookie,
        string identity, CancellationToken cancellationToken)
    {
        var response = await _http.SendAsync(new AllowlistedHttpRequest("GET",
            ProviderHttpAllowlist.CursorUsageSummary,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Cookie"] = "WorkosCursorSessionToken=" + cookie,
                ["Accept"] = "application/json",
            }), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is 401 or 403)
        {
            return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.NotConnected,
                "未连接：Cursor 登录已失效", "usage-summary 拒绝", "cursor_unauthorized");
        }

        if (response.StatusCode < 200 || response.StatusCode >= 300)
        {
            return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.Unavailable,
                "Cursor 额度接口不可用", "usage-summary HTTP " + response.StatusCode,
                "cursor_http_" + response.StatusCode);
        }

        var windows = CursorQuotaParser.Parse(response.Body, DateTimeOffset.UtcNow, out var error, out var shape);
        if (windows.Count == 0)
        {
            return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.Unavailable,
                "Cursor 未返回可用的模型池", "两个池不能合并成一个限额", error ?? "cursor_pools_missing") with
            {
                FieldPresenceFlags = shape.Format(),
            };
        }

        var now = DateTimeOffset.UtcNow;
        var stale = windows.Any(window => window.ResetsAtUtc.HasValue && window.ResetsAtUtc.Value <= now);
        return new ProviderSlotSnapshot(settings.SlotId, ProviderIds.Cursor, settings.Label, true, false,
            stale ? QuotaSlotStatus.Stale : QuotaSlotStatus.Live,
            stale ? "陈旧观测" : "Cursor usage-summary", now,
            "https://cursor.com/api/usage-summary", windows, identity, error, identity,
            FieldPresenceFlags: shape.Format());
    }
}

public sealed class GrokQuotaAdapter
{
    private readonly IAllowlistedHttpSender _http;
    private readonly IGrokTokenSource _tokens;

    public GrokQuotaAdapter(IAllowlistedHttpSender http, IGrokTokenSource tokens)
    {
        _http = http;
        _tokens = tokens;
    }

    public async Task<ProviderSlotSnapshot> ReadAsync(ProviderSlotSettings settings,
        CancellationToken cancellationToken)
    {
        if (!settings.Enabled)
            return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.Disabled, "未启用",
                "用户关闭了 Grok 槽", "slot_disabled");

        GrokLoginInspection inspection;
        try { inspection = _tokens.InspectLogin(); }
        catch (Exception)
        {
            return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.NotConnected,
                "无法读取本机 Grok 登录", "仅检查登录文件形状，不输出密钥", "grok_token_unavailable");
        }

        if (!inspection.Usable)
        {
            if (!inspection.FilePresent)
            {
                return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.NotConnected,
                    "未连接：本机没有可用的 Grok 登录", "需要 Grok CLI 已登录", "grok_sign_in_required") with
                {
                    FieldPresenceFlags = inspection.Format(),
                };
            }

            if (!inspection.RecognizedFormat)
            {
                return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.Unavailable,
                    "本机 Grok 登录格式当前解析器无法识别，不提示重新登录",
                    "公开 CLI 形态：issuer 条目的 key 与到期字段，或根令牌字段",
                    "grok_login_unsupported") with
                {
                    FieldPresenceFlags = inspection.Format(),
                };
            }

            if (inspection.HasRefreshToken)
            {
                GrokRenewalOutcome renewal;
                try { renewal = await _tokens.EnsureFreshAsync(false, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return RenewalPlaceholder(settings, inspection, new GrokRenewalOutcome(GrokRenewalKind.Timeout, true));
                }

                inspection = _tokens.InspectLogin();
                if (!inspection.Usable)
                    return RenewalPlaceholder(settings, inspection, renewal);
            }
            else if (inspection.Expired)
            {
                return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.NotConnected,
                    "未连接：Grok 访问令牌已过期，且没有可续期凭据", "本地 expires_at 已过期", "grok_login_expired") with
                {
                    FieldPresenceFlags = inspection.Format(),
                };
            }
            else
            {
                return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.NotConnected,
                    "未连接：本机没有可用的 Grok 登录", "需要 Grok CLI 已登录", "grok_sign_in_required") with
                {
                    FieldPresenceFlags = inspection.Format(),
                };
            }
        }

        return await ReadBillingAsync(settings, inspection, retried: false, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProviderSlotSnapshot> ReadBillingAsync(ProviderSlotSettings settings,
        GrokLoginInspection inspection, bool retried, CancellationToken cancellationToken)
    {
        string? token;
        try { token = _tokens.ReadAccessToken(); }
        catch (Exception)
        {
            return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.NotConnected,
                "无法读取本机 Grok 登录", "仅读取已识别的 token 字段", "grok_token_unavailable") with
            {
                FieldPresenceFlags = inspection.Format(),
            };
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.NotConnected,
                "未连接：本机没有可用的 Grok 登录", "需要 Grok CLI 已登录", "grok_sign_in_required") with
            {
                FieldPresenceFlags = inspection.Format(),
            };
        }

        var identity = StableSubjectIdentity.Hash("grok|", token);
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Authorization"] = "Bearer " + token,
            ["x-xai-token-auth"] = "xai-grok-cli",
            ["Accept"] = "application/json",
        };
        try
        {
            var response = await _http.SendAsync(new AllowlistedHttpRequest("GET",
                ProviderHttpAllowlist.GrokBilling, headers), cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is 401 or 403)
            {
                if (!retried && inspection.HasRefreshToken)
                {
                    GrokRenewalOutcome renewal;
                    try { renewal = await _tokens.EnsureFreshAsync(true, cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        return RenewalPlaceholder(settings, inspection,
                            new GrokRenewalOutcome(GrokRenewalKind.Timeout, true));
                    }

                    var after = _tokens.InspectLogin();
                    if (after.Usable)
                        return await ReadBillingAsync(settings, after, true, cancellationToken).ConfigureAwait(false);
                    return RenewalPlaceholder(settings, after, renewal);
                }

                var revoked = inspection.HasRefreshToken || retried;
                return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.NotConnected,
                    revoked ? "未连接：Grok 登录续期后仍被额度接口拒绝" : "未连接：Grok 访问令牌已过期，且没有可续期凭据",
                    "billing 拒绝",
                    revoked ? "grok_login_revoked" : "grok_login_expired") with
                {
                    FieldPresenceFlags = inspection.Format(),
                };
            }

            if (response.StatusCode < 200 || response.StatusCode >= 300)
            {
                return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.Unavailable,
                    "Grok 额度接口不可用", "billing HTTP " + response.StatusCode,
                    "grok_http_" + response.StatusCode);
            }

            var now = DateTimeOffset.UtcNow;
            var windows = GrokQuotaParser.Parse(response.Body, now, out var error);
            if (windows.Count == 0)
            {
                var elapsed = string.Equals(error, "grok_period_elapsed", StringComparison.Ordinal);
                return ProviderQuotaPresentation.Placeholder(settings, false,
                    elapsed ? QuotaSlotStatus.Stale : QuotaSlotStatus.Unavailable,
                    elapsed ? "已结束的周期，不显示为当前额度" : "Grok 未返回可用周额度",
                    "个人账单形态，缺少字段且不在进行中的周期时不按 0 处理", error);
            }

            try
            {
                var settingsResponse = await _http.SendAsync(new AllowlistedHttpRequest("GET",
                    ProviderHttpAllowlist.GrokSettings, headers), cancellationToken).ConfigureAwait(false);
                if (settingsResponse.StatusCode is >= 200 and < 300)
                    _ = GrokQuotaParser.ParsePlanName(settingsResponse.Body);
            }
            catch (Exception)
            {
                // Plan name is optional; usage already succeeded.
            }

            var stale = windows.Any(window => window.ResetsAtUtc.HasValue && window.ResetsAtUtc.Value <= now);
            return new ProviderSlotSnapshot(settings.SlotId, ProviderIds.Grok, settings.Label, true, false,
                stale ? QuotaSlotStatus.Stale : QuotaSlotStatus.Live,
                stale ? "陈旧观测" : "Grok 个人周额度", now,
                "https://cli-chat-proxy.grok.com/v1/billing?format=credits", windows, identity, error,
                identity, FieldPresenceFlags: inspection.Format());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.Unavailable,
                "Grok 请求超时", "独立超时", "grok_timeout");
        }
        catch (InvalidOperationException exception)
        {
            var code = ProviderHttpErrors.Sanitize(exception);
            return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.Unavailable,
                "Grok 请求被拒绝", code, code);
        }
        catch (HttpRequestException)
        {
            return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.Unavailable,
                "Grok 网络不可用", "独立失败", "grok_network");
        }
    }

    private static ProviderSlotSnapshot RenewalPlaceholder(ProviderSlotSettings settings,
        GrokLoginInspection inspection, GrokRenewalOutcome outcome)
    {
        switch (outcome.Kind)
        {
            case GrokRenewalKind.Revoked:
                return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.NotConnected,
                    "未连接：Grok 登录续期被拒绝，需要重新登录", "refresh grant 已失效",
                    "grok_login_revoked") with
                {
                    FieldPresenceFlags = inspection.Format(),
                };
            case GrokRenewalKind.NotRenewable:
                return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.NotConnected,
                    "未连接：Grok 访问令牌已过期，且没有可续期凭据", "本地 expires_at 已过期",
                    "grok_login_expired") with
                {
                    FieldPresenceFlags = inspection.Format(),
                };
            case GrokRenewalKind.Timeout:
                return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.Unavailable,
                    "Grok 登录续期超时，稍后再试", "官方 grok models 超时",
                    "grok_renewal_unavailable") with
                {
                    FieldPresenceFlags = inspection.Format(),
                };
            case GrokRenewalKind.BinaryMissing:
                return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.Unavailable,
                    "Grok 登录续期暂时不可用：本机没有 Grok CLI", "缺少 grok.exe",
                    "grok_renewal_unavailable") with
                {
                    FieldPresenceFlags = inspection.Format(),
                };
            default:
                return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.Unavailable,
                    "Grok 登录续期暂时不可用", "官方 grok models 未写出可用令牌",
                    "grok_renewal_unavailable") with
                {
                    FieldPresenceFlags = inspection.Format(),
                };
        }
    }
}

public sealed class GrokBotQuotaAdapter
{
    private readonly IAllowlistedHttpSender _http;
    private readonly ICursorTokenSource _tokens;

    public GrokBotQuotaAdapter(IAllowlistedHttpSender http, ICursorTokenSource tokens)
    {
        _http = http;
        _tokens = tokens;
    }

    public async Task<ProviderSlotSnapshot> ReadAsync(ProviderSlotSettings settings,
        CancellationToken cancellationToken)
    {
        if (!settings.Enabled)
            return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.Disabled, "未启用",
                "用户关闭了 Grok Bot 槽", "slot_disabled");

        string? token;
        try { token = _tokens.ReadAccessToken(); }
        catch (Exception)
        {
            return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.NotConnected,
                "无法读取 Cursor 登录（Grok Bot 走 Cursor 账户）", "仅读取 cursorAuth/accessToken",
                "grok_bot_token_unavailable");
        }

        if (string.IsNullOrWhiteSpace(token) ||
            !CursorSessionCookie.TryBuild(token, out var cookie, out var identity) || identity is null)
        {
            return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.NotConnected,
                "未连接：Grok Bot 需要 Cursor 登录", "不使用 ~/.grok", "grok_bot_sign_in_required");
        }
        try
        {
            var response = await _http.SendAsync(CursorSandAccess.Request(cookie), cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode is 401 or 403)
            {
                return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.NotConnected,
                    "未连接：Cursor 登录无法读取 Grok Bot", "get-sand-usage-status 拒绝",
                    "grok_bot_unauthorized");
            }

            if (response.StatusCode < 200 || response.StatusCode >= 300)
            {
                return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.Unavailable,
                    "Grok Bot 额度接口不可用", "HTTP " + response.StatusCode,
                    "grok_bot_http_" + response.StatusCode);
            }

            var now = DateTimeOffset.UtcNow;
            var sand = GrokBotQuotaParser.Read(response.Body, now);
            if (sand.Mode == CursorAllowanceMode.Unified)
                return CursorSandAccess.SharedSnapshot(settings, sand, now, identity);

            var windows = sand.Windows;
            var error = sand.ErrorCode;
            if (windows.Count == 0)
            {
                var excluded = string.Equals(error, "grok_bot_not_included", StringComparison.Ordinal);
                return ProviderQuotaPresentation.Placeholder(settings, false,
                    excluded ? QuotaSlotStatus.NoAllowance : QuotaSlotStatus.Unavailable,
                    excluded ? "当前计划未包含 Grok Bot，不显示 100% 可用" : "Grok Bot 回复无法解读",
                    "Sand 额度与 Cursor 月度池、Grok 周池分离", error);
            }

            var stale = windows.Any(window => window.ResetsAtUtc.HasValue && window.ResetsAtUtc.Value <= now);
            return new ProviderSlotSnapshot(settings.SlotId, ProviderIds.GrokBot, settings.Label, true, false,
                stale ? QuotaSlotStatus.Stale : QuotaSlotStatus.Live,
                stale ? "陈旧观测" : "Cursor dashboard Sand", now,
                SharedWeeklyQuota.SourceUrl, windows, identity, error, identity);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.Unavailable,
                "Grok Bot 请求超时", "独立超时", "grok_bot_timeout");
        }
        catch (InvalidOperationException exception)
        {
            var code = ProviderHttpErrors.Sanitize(exception);
            return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.Unavailable,
                "Grok Bot 请求被拒绝", code, code);
        }
        catch (HttpRequestException)
        {
            return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.Unavailable,
                "Grok Bot 网络不可用", "独立失败", "grok_bot_network");
        }
    }
}

public class PiCodexQuotaAdapter
{
    private readonly IAllowlistedHttpSender _http;
    private readonly IPiCodexTokenSource _tokens;
    private readonly IPiCodexSessionRenewer _renewer;
    private readonly object _renewalGate = new();
    private Task<PiCodexRenewalResult>? _inFlight;
    private DateTimeOffset _retryAfterUtc;
    private string? _failedStamp;

    public PiCodexQuotaAdapter(IAllowlistedHttpSender http, IPiCodexTokenSource tokens,
        IPiCodexSessionRenewer? renewer = null)
    {
        _http = http;
        _tokens = tokens;
        _renewer = renewer ?? new PiCodexNativeRenewer();
    }

    public virtual async Task<ProviderSlotSnapshot> ReadAsync(ProviderSlotSettings settings,
        CancellationToken cancellationToken)
    {
        if (!settings.Enabled)
            return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.Disabled, "未启用",
                "用户关闭了第二 Codex 槽", "slot_disabled");

        PiCodexLoginInspection inspection;
        try { inspection = _tokens.InspectLogin(); }
        catch (Exception)
        {
            return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.NotConnected,
                "无法读取本机 PI openai-codex 登录", "仅检查 openai-codex 条目形状，不输出密钥",
                "pi_codex_token_unavailable");
        }

        if (!inspection.Usable)
        {
            if (!inspection.FilePresent)
            {
                return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.NotConnected,
                    PiCodexSubscription.MissingStatusText, PiCodexSubscription.MissingDetail,
                    PiCodexSubscription.MissingCode) with
                {
                    FieldPresenceFlags = inspection.Format(),
                };
            }

            if (!inspection.HasOpenAiCodexKey)
            {
                return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.NotConnected,
                    PiCodexSubscription.MissingStatusText, "PI 登录文件存在但没有 openai-codex 条目",
                    PiCodexSubscription.MissingCode) with
                {
                    FieldPresenceFlags = inspection.Format(),
                };
            }

            if (!inspection.RecognizedFormat)
            {
                return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.Unavailable,
                    PiCodexSubscription.UnsupportedStatusText, PiCodexSubscription.UnsupportedDetail,
                    PiCodexSubscription.UnsupportedCode) with
                {
                    FieldPresenceFlags = inspection.Format(),
                };
            }

            if (inspection.Expired)
            {
                var renewal = await RenewAsync(_tokens.ConfigurationFingerprint(), false, cancellationToken)
                    .ConfigureAwait(false);
                inspection = InspectSafely();
                if (!inspection.Usable)
                    return RenewalPlaceholder(settings, inspection, renewal);
            }
            else
            {
                return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.NotConnected,
                    PiCodexSubscription.MissingStatusText, PiCodexSubscription.MissingDetail,
                    PiCodexSubscription.MissingCode) with
                {
                    FieldPresenceFlags = inspection.Format(),
                };
            }
        }

        PiCodexCredential? credential;
        try { credential = _tokens.ReadCredential(); }
        catch (Exception)
        {
            return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.NotConnected,
                "无法读取本机 PI openai-codex 登录", "仅读取 openai-codex 的 access 与 accountId",
                "pi_codex_token_unavailable") with
            {
                FieldPresenceFlags = inspection.Format(),
            };
        }

        if (credential is null)
        {
            return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.NotConnected,
                PiCodexSubscription.MissingStatusText, PiCodexSubscription.MissingDetail,
                PiCodexSubscription.MissingCode) with
            {
                FieldPresenceFlags = inspection.Format(),
            };
        }

        return await ReadUsageAsync(settings, inspection, credential, false, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProviderSlotSnapshot> ReadUsageAsync(ProviderSlotSettings settings,
        PiCodexLoginInspection inspection, PiCodexCredential credential, bool retried,
        CancellationToken cancellationToken)
    {
        var identity = BoundIdentity.HashAccount(credential.AccountId);
        var stamp = _tokens.ConfigurationFingerprint();
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Authorization"] = "Bearer " + credential.AccessToken,
            ["chatgpt-account-id"] = credential.AccountId,
            ["User-Agent"] = "codex-cli",
            ["Accept"] = "application/json",
        };
        try
        {
            var response = await _http.SendAsync(new AllowlistedHttpRequest("GET",
                ProviderHttpAllowlist.PiCodexUsage, headers), cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is 401 or 403)
            {
                if (!retried)
                {
                    var renewal = await RenewAsync(stamp, true, cancellationToken).ConfigureAwait(false);
                    var after = InspectSafely();
                    if (after.Usable && renewal == PiCodexRenewalResult.Ready)
                    {
                        PiCodexCredential? fresh;
                        try { fresh = _tokens.ReadCredential(); }
                        catch (Exception) { fresh = null; }
                        if (fresh is not null)
                            return await ReadUsageAsync(settings, after, fresh, true, cancellationToken)
                                .ConfigureAwait(false);
                    }
                    if (!after.Usable && (!after.FilePresent || !after.HasOpenAiCodexKey))
                        return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.NotConnected,
                            PiCodexSubscription.MissingStatusText, PiCodexSubscription.MissingDetail,
                            PiCodexSubscription.MissingCode);
                    return RenewalPlaceholder(settings, after, renewal);
                }

                MarkRenewalFailure(_tokens.ConfigurationFingerprint());
                return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.NotConnected,
                    "未连接：PI 续期后额度接口仍拒绝登录", "wham/usage 拒绝",
                    "pi_codex_unauthorized") with
                {
                    OpaqueIdentityHash = identity,
                    AccountKey = identity,
                    FieldPresenceFlags = inspection.Format(),
                };
            }

            if (response.StatusCode < 200 || response.StatusCode >= 300)
            {
                return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.Unavailable,
                    "PI Codex 额度接口不可用", "wham/usage HTTP " + response.StatusCode,
                    "pi_codex_http_" + response.StatusCode) with
                {
                    FieldPresenceFlags = inspection.Format(),
                };
            }

            if (!string.Equals(stamp, _tokens.ConfigurationFingerprint(), StringComparison.Ordinal))
                return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.Unavailable,
                    "PI 登录读取期间已变化，等待重新读取", "不沿用旧账户额度", "pi_codex_login_changed");
            lock (_renewalGate) { _retryAfterUtc = default; _failedStamp = null; }
            var now = DateTimeOffset.UtcNow;
            var windows = PiCodexQuotaParser.Parse(response.Body, now, out var error, out var shape);
            if (windows.Count == 0)
            {
                return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.Unavailable,
                    "PI 未返回可用的用量窗口", "不把缺失窗口显示成 100% 可用",
                    error ?? "pi_codex_windows_missing") with
                {
                    OpaqueIdentityHash = identity,
                    AccountKey = identity,
                    FieldPresenceFlags = shape,
                };
            }

            var stale = windows.Any(window => window.ResetsAtUtc.HasValue && window.ResetsAtUtc.Value <= now);
            return new ProviderSlotSnapshot(settings.SlotId, ProviderIds.Codex, settings.Label, true, false,
                stale ? QuotaSlotStatus.Stale : QuotaSlotStatus.Live,
                stale ? "陈旧观测" : "PI ChatGPT/Codex 订阅", now, PiCodexSubscription.SourceDescription, windows,
                identity, error, identity, FieldPresenceFlags: shape);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.Unavailable,
                "PI Codex 请求超时", "独立超时", "pi_codex_timeout");
        }
        catch (InvalidOperationException exception)
        {
            var code = ProviderHttpErrors.Sanitize(exception);
            return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.Unavailable,
                "PI Codex 请求被拒绝", code, code);
        }
        catch (HttpRequestException)
        {
            return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.Unavailable,
                "PI Codex 网络不可用", "独立失败", "pi_codex_network");
        }
    }

    private PiCodexLoginInspection InspectSafely()
    {
        try { return _tokens.InspectLogin(); }
        catch (Exception) { return PiCodexLoginInspection.Missing; }
    }

    private async Task<PiCodexRenewalResult> RenewAsync(string stamp, bool force,
        CancellationToken cancellationToken)
    {
        Task<PiCodexRenewalResult> pending;
        lock (_renewalGate)
        {
            if (_inFlight is { IsCompleted: false }) pending = _inFlight;
            else if (string.Equals(_failedStamp, stamp, StringComparison.Ordinal) &&
                     DateTimeOffset.UtcNow < _retryAfterUtc)
                return PiCodexRenewalResult.Failed;
            else
            {
                _inFlight = pending = CheckAndRenewAsync(stamp, force);
            }
        }
        return await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<PiCodexRenewalResult> CheckAndRenewAsync(string stamp, bool force)
    {
        var current = InspectSafely();
        if (current.Usable && (!force || !string.Equals(_tokens.ConfigurationFingerprint(), stamp,
                StringComparison.Ordinal))) return PiCodexRenewalResult.Ready;

        PiCodexRenewalResult result;
        try { result = await _renewer.CheckAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { result = PiCodexRenewalResult.Failed; }
        // Exit 0 is not proof of a refreshed access token; inspect the PI store again.
        if (result == PiCodexRenewalResult.Ready && !InspectSafely().Usable)
            result = PiCodexRenewalResult.Failed;
        if (result != PiCodexRenewalResult.Ready)
            MarkRenewalFailure(_tokens.ConfigurationFingerprint());
        return result;
    }

    private void MarkRenewalFailure(string stamp)
    {
        lock (_renewalGate)
        {
            _failedStamp = stamp;
            _retryAfterUtc = DateTimeOffset.UtcNow.AddSeconds(30);
        }
    }

    private static ProviderSlotSnapshot RenewalPlaceholder(ProviderSlotSettings settings,
        PiCodexLoginInspection inspection, PiCodexRenewalResult result)
    {
        var missing = !inspection.FilePresent || !inspection.HasOpenAiCodexKey;
        return ProviderQuotaPresentation.Placeholder(settings, false,
            missing ? QuotaSlotStatus.NotConnected : QuotaSlotStatus.Unavailable,
            missing ? PiCodexSubscription.MissingStatusText : PiCodexSubscription.RenewalStatusText,
            result == PiCodexRenewalResult.Timeout ? "PI auth check 超时" : "PI auth check 未提供可用登录",
            missing ? PiCodexSubscription.MissingCode : PiCodexSubscription.RenewalCode) with
        {
            FieldPresenceFlags = inspection.Format(),
        };
    }
}

public sealed class ClaudeQuotaAdapter
{
    public const string SourceDescription = "https://api.anthropic.com/api/oauth/usage";
    public const string OauthBeta = "oauth-2025-04-20";

    private readonly IAllowlistedHttpSender _http;
    private readonly IClaudeTokenSource _tokens;
    private readonly Func<DateTimeOffset> _clock;

    public ClaudeQuotaAdapter(IAllowlistedHttpSender http, IClaudeTokenSource tokens,
        Func<DateTimeOffset>? clock = null)
    {
        _http = http;
        _tokens = tokens;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<ProviderSlotSnapshot> ReadAsync(ProviderSlotSettings settings,
        CancellationToken cancellationToken)
    {
        if (!settings.Enabled)
            return ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.Disabled, "未启用",
                "用户关闭了 Claude 槽", "slot_disabled");

        ClaudeLoginInspection inspection;
        try { inspection = _tokens.InspectLogin(); }
        catch (Exception)
        {
            return Disconnected(settings, "无法读取本机 Claude Code 登录", "仅检查 claudeAiOauth.accessToken",
                "claude_token_unavailable", null);
        }

        if (!inspection.Usable)
        {
            var desktop = string.Equals(inspection.SourceKind, "claude_desktop", StringComparison.Ordinal);
            if (inspection.AccountAmbiguous)
            {
                return Unavailable(settings, "Claude Desktop 有多个账户缓存，无法证明当前账户",
                    "不选用任意缓存账户", "claude_desktop_account_ambiguous", inspection);
            }

            if (inspection.DecryptFailed)
            {
                return Unavailable(settings, "Claude Desktop 登录无法在本地解密",
                    "不改写 Desktop 配置", "claude_desktop_decrypt_failed", inspection);
            }

            if (inspection.Expired)
            {
                return Disconnected(settings,
                    desktop ? "未连接：Claude Desktop 登录已过期" : "未连接：Claude Code 登录已过期",
                    "不刷新、不重写凭据", "claude_login_expired", inspection);
            }

            if (!inspection.FilePresent)
            {
                return Disconnected(settings, "未连接：本机没有可用的 Claude Code 登录",
                    "需要 Claude Code 已登录", "claude_sign_in_required", inspection);
            }

            if (desktop && inspection.Recognized && !inspection.HasAccess)
            {
                return Disconnected(settings, "未连接：Claude Desktop 没有可用的访问令牌",
                    "需要当前账户的未过期访问令牌", "claude_sign_in_required", inspection);
            }

            return Disconnected(settings,
                desktop ? "未连接：Claude Desktop 登录当前无法用于订阅额度" : "未连接：本机 Claude Code 登录当前无法用于订阅额度",
                desktop ? "仅接受当前账户未过期的访问令牌" : "仅接受 claudeAiOauth.accessToken",
                "claude_login_unsupported", inspection);
        }

        string? token;
        try { token = _tokens.ReadAccessToken(); }
        catch (Exception)
        {
            return Disconnected(settings, "无法读取本机 Claude Code 登录", "仅读取 claudeAiOauth.accessToken",
                "claude_token_unavailable", inspection);
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return Disconnected(settings, "未连接：本机没有可用的 Claude Code 登录",
                "需要 Claude Code 已登录", "claude_sign_in_required", inspection);
        }

        var identity = OpaqueIdentity.Hash("claude-oauth|" + token);
        try
        {
            var response = await _http.SendAsync(new AllowlistedHttpRequest("GET",
                ProviderHttpAllowlist.ClaudeOAuthUsage,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Authorization"] = "Bearer " + token,
                    ["Accept"] = "application/json",
                    ["anthropic-beta"] = OauthBeta,
                }), cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is 401 or 403)
            {
                return Disconnected(settings, "未连接：Claude 订阅登录被拒绝",
                    "oauth/usage 拒绝", "claude_unauthorized", inspection);
            }

            if (response.StatusCode == 429)
            {
                return Unavailable(settings, "Claude 订阅额度接口限流，不显示猜测百分比",
                    "oauth/usage HTTP 429", "claude_rate_limited", inspection, identity: identity);
            }

            if (response.StatusCode is >= 500 and <= 599)
            {
                return Unavailable(settings, "Claude 订阅额度接口不可用",
                    "oauth/usage HTTP " + response.StatusCode, "claude_http_" + response.StatusCode, inspection,
                    identity: identity);
            }

            if (response.StatusCode < 200 || response.StatusCode >= 300)
            {
                return Unavailable(settings, "Claude 订阅额度接口不可用",
                    "oauth/usage HTTP " + response.StatusCode, "claude_http_" + response.StatusCode, inspection);
            }

            var windows = ClaudeQuotaParser.Parse(response.Body, out var error, out var flags);
            if (windows.Count == 0)
            {
                return Unavailable(settings, "Claude 订阅额度格式暂不支持，不显示 0% 或 100%",
                    "缺少可用的 utilization 窗口", error ?? "claude_schema_unsupported", inspection, flags);
            }

            var now = _clock();
            var current = windows.Any(window => !window.ResetsAtUtc.HasValue || window.ResetsAtUtc.Value > now);
            return new ProviderSlotSnapshot(settings.SlotId, ProviderIds.Claude, settings.Label, true, false,
                current ? QuotaSlotStatus.Live : QuotaSlotStatus.Stale,
                current ? "Claude 订阅额度" : "窗口已到期，不作为当前额度", now, SourceDescription, windows,
                identity, error, FieldPresenceFlags: inspection.Format() + " " + flags);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable(settings, "Claude 订阅额度请求超时", "独立超时", "claude_timeout", inspection,
                identity: identity);
        }
        catch (InvalidOperationException exception)
        {
            var code = ProviderHttpErrors.Sanitize(exception);
            return Unavailable(settings, "Claude 订阅额度请求被拒绝", code, code, inspection);
        }
        catch (HttpRequestException)
        {
            return Unavailable(settings, "Claude 订阅额度网络不可用", "独立失败", "claude_network", inspection,
                identity: identity);
        }
    }

    private static ProviderSlotSnapshot Disconnected(ProviderSlotSettings settings, string statusText,
        string source, string code, ClaudeLoginInspection? inspection) =>
        ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.NotConnected, statusText, source,
            code) with
        {
            FieldPresenceFlags = inspection?.Format(),
        };

    private static ProviderSlotSnapshot Unavailable(ProviderSlotSettings settings, string statusText, string source,
        string code, ClaudeLoginInspection inspection, string? flags = null, string? identity = null) =>
        ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.Unavailable, statusText, source,
            code) with
        {
            OpaqueIdentityHash = identity,
            FieldPresenceFlags = flags is null ? inspection.Format() : inspection.Format() + " " + flags,
        };
}
