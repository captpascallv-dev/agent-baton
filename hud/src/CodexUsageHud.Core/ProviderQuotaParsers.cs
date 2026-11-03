using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodexUsageHud.Core;

public static class JsonQuotaFields
{
    public static string? GetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }

        return null;
    }

    public static bool? GetBoolean(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind is JsonValueKind.True) return true;
            if (value.ValueKind is JsonValueKind.False) return false;
        }

        return null;
    }

    public static double? GetDouble(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
                return number;
            if (value.ValueKind == JsonValueKind.String &&
                double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    public static DateTimeOffset? GetDate(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            var parsed = ReadDate(value);
            if (parsed.HasValue) return parsed;
        }

        return null;
    }

    public static DateTimeOffset? ReadDate(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt64(out var integer))
                return FromUnixFlexible(integer);
            if (value.TryGetDouble(out var number))
                return FromUnixFlexible((long)number);
        }

        if (value.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return parsed.ToUniversalTime();
        }

        return null;
    }

    public static DateTimeOffset? FromUnixFlexible(long value)
    {
        try
        {
            if (value > 9_999_999_999) return DateTimeOffset.FromUnixTimeMilliseconds(value);
            return DateTimeOffset.FromUnixTimeSeconds(value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public static bool HasObject(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object)
                return true;
        }

        return false;
    }

    public static bool HasNumber(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number) return true;
            if (value.ValueKind == JsonValueKind.String &&
                double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                return true;
            }
        }

        return false;
    }
}

public sealed record CursorReplyShape(
    bool IndividualUsage,
    bool IndividualPlan,
    bool TeamUsage,
    bool TeamPooled,
    bool AutoPercentUsed,
    bool ApiPercentUsed,
    bool BillingCycleEnd)
{
    public string Format() =>
        "individualUsage=" + Flag(IndividualUsage) +
        " plan=" + Flag(IndividualPlan) +
        " teamUsage=" + Flag(TeamUsage) +
        " pooled=" + Flag(TeamPooled) +
        " autoPercentUsed=" + Flag(AutoPercentUsed) +
        " apiPercentUsed=" + Flag(ApiPercentUsed) +
        " billingCycleEnd=" + Flag(BillingCycleEnd);

    private static string Flag(bool value) => value ? "1" : "0";
}

public static class CursorQuotaParser
{
    public static IReadOnlyList<QuotaWindowObservation> Parse(string json, DateTimeOffset nowUtc,
        out string? errorCode) =>
        Parse(json, nowUtc, out errorCode, out _);

    public static IReadOnlyList<QuotaWindowObservation> Parse(string json, DateTimeOffset nowUtc,
        out string? errorCode, out CursorReplyShape shape)
    {
        errorCode = null;
        shape = new CursorReplyShape(false, false, false, false, false, false, false);
        _ = nowUtc;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var root = Unwrap(document.RootElement);
            var individual = Nested(root, "individualUsage", "individual_usage");
            var individualPlan = individual.HasValue ? Nested(individual.Value, "plan") : Nested(root, "plan");
            var team = Nested(root, "teamUsage", "team_usage");
            var teamPooled = team.HasValue ? Nested(team.Value, "pooled") : Nested(root, "pooled");
            var plan = SelectPlan(individualPlan, teamPooled, root);
            shape = InspectShape(root, individual, individualPlan, team, teamPooled, plan);

            var windows = new List<QuotaWindowObservation>();
            var auto = ReadPool(plan, "autoPercentUsed", "auto_percent_used", "Cursor 自有模型");
            var api = ReadPool(plan, "apiPercentUsed", "api_percent_used", "其他模型");
            if (auto is not null) windows.Add(auto);
            if (api is not null) windows.Add(api);
            if (windows.Count == 0 && teamPooled.HasValue)
                windows.AddRange(ReadNamedPool(teamPooled.Value, "团队共用"));

            if (windows.Count == 0)
            {
                errorCode = "cursor_pools_missing";
                return Array.Empty<QuotaWindowObservation>();
            }

            var reset = JsonQuotaFields.GetDate(root, "billingCycleEnd", "billing_cycle_end",
                "nextResetAt", "next_reset_at", "resetsAt", "resets_at");
            return windows.Select(window => window with
            {
                ResetsAtUtc = window.ResetsAtUtc ?? reset,
                WindowDurationMinutes = window.WindowDurationMinutes,
                MissingResetText = window.ResetsAtUtc is null && reset is null ? "重置时间未提供" : window.MissingResetText,
            }).ToArray();
        }
        catch (JsonException)
        {
            errorCode = "cursor_json_invalid";
            return Array.Empty<QuotaWindowObservation>();
        }
    }

    public static CursorReplyShape InspectShape(string json)
    {
        Parse(json, DateTimeOffset.UtcNow, out _, out var shape);
        return shape;
    }

    private static JsonElement SelectPlan(JsonElement? individualPlan, JsonElement? teamPooled, JsonElement root)
    {
        if (individualPlan.HasValue && HasPoolPercents(individualPlan.Value)) return individualPlan.Value;
        if (teamPooled.HasValue && HasPoolPercents(teamPooled.Value)) return teamPooled.Value;
        return root;
    }

    private static bool HasPoolPercents(JsonElement element) =>
        JsonQuotaFields.HasNumber(element, "autoPercentUsed", "auto_percent_used") ||
        JsonQuotaFields.HasNumber(element, "apiPercentUsed", "api_percent_used");

    private static CursorReplyShape InspectShape(JsonElement root, JsonElement? individual, JsonElement? plan,
        JsonElement? team, JsonElement? pooled, JsonElement selected)
    {
        var autoSource = HasPoolPercents(selected) ? selected : root;
        return new CursorReplyShape(
            individual.HasValue,
            plan.HasValue,
            team.HasValue,
            pooled.HasValue,
            JsonQuotaFields.HasNumber(autoSource, "autoPercentUsed", "auto_percent_used"),
            JsonQuotaFields.HasNumber(autoSource, "apiPercentUsed", "api_percent_used"),
            JsonQuotaFields.GetDate(root, "billingCycleEnd", "billing_cycle_end", "nextResetAt",
                "next_reset_at", "resetsAt", "resets_at").HasValue);
    }

    private static QuotaWindowObservation? ReadPool(JsonElement root, string camel, string snake, string name)
    {
        var used = JsonQuotaFields.GetDouble(root, camel, snake);
        if (!used.HasValue) return null;
        if (used.Value < 0 || used.Value > 100) return null;
        return new QuotaWindowObservation(camel, name, used, null, null, true, "重置时间未提供");
    }

    private static IEnumerable<QuotaWindowObservation> ReadNamedPool(JsonElement element, string name)
    {
        var used = JsonQuotaFields.GetDouble(element, "percentUsed", "percent_used", "usedPercent", "used_percent");
        if (!used.HasValue || used.Value < 0 || used.Value > 100) yield break;
        yield return new QuotaWindowObservation(name, name, used, null, null, true, "重置时间未提供");
    }

    private static JsonElement Unwrap(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("result", out var result) &&
            result.ValueKind == JsonValueKind.Object)
        {
            return result;
        }

        return root;
    }

    private static JsonElement? Nested(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object)
                return value;
        }

        return null;
    }
}

public static class GrokQuotaParser
{
    public static IReadOnlyList<QuotaWindowObservation> Parse(string json, DateTimeOffset nowUtc,
        out string? errorCode)
    {
        errorCode = null;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("result", out var wrapped) &&
                wrapped.ValueKind == JsonValueKind.Object)
            {
                root = wrapped;
            }

            var usageRoot = root;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("config", out var config) &&
                config.ValueKind == JsonValueKind.Object)
            {
                usageRoot = config;
            }

            _ = Period(usageRoot, nowUtc, out var periodLive, out var periodStart, out var periodEnd);
            if (!periodLive && !periodStart.HasValue && !periodEnd.HasValue)
                _ = Period(root, nowUtc, out periodLive, out periodStart, out periodEnd);
            if (!periodLive && periodEnd.HasValue && periodEnd.Value <= nowUtc)
            {
                errorCode = "grok_period_elapsed";
                return Array.Empty<QuotaWindowObservation>();
            }

            var used = JsonQuotaFields.GetDouble(usageRoot, "creditUsagePercent", "credit_usage_percent",
                "usagePercent", "usage_percent");
            if (!used.HasValue)
                used = JsonQuotaFields.GetDouble(root, "creditUsagePercent", "credit_usage_percent",
                    "usagePercent", "usage_percent");
            if (!used.HasValue) used = FirstProductPercent(usageRoot) ?? FirstProductPercent(root);
            if (!used.HasValue)
            {
                if (periodLive)
                    used = 0d;
                else
                {
                    errorCode = "grok_usage_missing";
                    return Array.Empty<QuotaWindowObservation>();
                }
            }

            if (used.Value < 0 || used.Value > 100)
            {
                errorCode = "grok_usage_invalid";
                return Array.Empty<QuotaWindowObservation>();
            }

            int? duration = null;
            if (periodStart.HasValue && periodEnd.HasValue)
            {
                var minutes = (int)Math.Round((periodEnd.Value - periodStart.Value).TotalMinutes);
                if (minutes > 0) duration = minutes;
            }

            return new[]
            {
                new QuotaWindowObservation("weekly", "每周额度", used, periodEnd, duration, true,
                    periodEnd.HasValue ? null : "重置时间未提供"),
            };
        }
        catch (JsonException)
        {
            errorCode = "grok_json_invalid";
            return Array.Empty<QuotaWindowObservation>();
        }
    }

    public static string? ParsePlanName(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            var name = JsonQuotaFields.GetString(root, "subscription_tier_display",
                "subscriptionTierDisplay", "planName", "plan_name");
            if (!string.IsNullOrWhiteSpace(name)) return name;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("config", out var config) &&
                config.ValueKind == JsonValueKind.Object)
            {
                return JsonQuotaFields.GetString(config, "subscription_tier_display",
                    "subscriptionTierDisplay", "planName", "plan_name");
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Period(JsonElement root, DateTimeOffset nowUtc, out bool live,
        out DateTimeOffset? start, out DateTimeOffset? end)
    {
        live = false;
        start = null;
        end = null;
        JsonElement period = default;
        var found = root.TryGetProperty("currentPeriod", out period) ||
                    root.TryGetProperty("current_period", out period);
        if (found && period.ValueKind == JsonValueKind.Object)
        {
            start = JsonQuotaFields.GetDate(period, "start", "startsAt", "starts_at", "startTime", "start_time");
            end = JsonQuotaFields.GetDate(period, "end", "endsAt", "ends_at", "endTime", "end_time");
        }
        else
        {
            start = JsonQuotaFields.GetDate(root, "periodStart", "period_start", "currentPeriodStart");
            end = JsonQuotaFields.GetDate(root, "periodEnd", "period_end", "currentPeriodEnd");
        }

        if (start.HasValue && end.HasValue)
        {
            live = start.Value <= nowUtc && nowUtc < end.Value;
            return true;
        }

        live = true;
        return false;
    }

    private static double? FirstProductPercent(JsonElement root)
    {
        if (!root.TryGetProperty("productUsage", out var products) &&
            !root.TryGetProperty("product_usage", out products))
        {
            return null;
        }

        if (products.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in products.EnumerateArray())
        {
            var used = JsonQuotaFields.GetDouble(item, "usagePercent", "usage_percent");
            if (used.HasValue) return used;
        }

        return null;
    }
}

public enum CursorAllowanceMode
{
    Unconfirmed,
    Legacy,
    Unified,
}

public sealed record CursorSandParse(
    CursorAllowanceMode Mode,
    IReadOnlyList<QuotaWindowObservation> Windows,
    string? ErrorCode);

public static class GrokBotQuotaParser
{
    public static IReadOnlyList<QuotaWindowObservation> Parse(string json, DateTimeOffset nowUtc,
        out string? errorCode)
    {
        var read = Read(json, nowUtc);
        errorCode = read.ErrorCode;
        return read.Windows;
    }

    public static CursorSandParse Read(string json, DateTimeOffset nowUtc)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("result", out var wrapped) &&
                wrapped.ValueKind == JsonValueKind.Object)
            {
                root = wrapped;
            }

            switch (ProbeMode(root))
            {
                case SandModeProbe.Unified:
                    return ReadUnified(root, nowUtc);
                case SandModeProbe.NotBoolean:
                    return new CursorSandParse(CursorAllowanceMode.Unconfirmed,
                        Array.Empty<QuotaWindowObservation>(), "cursor_sand_mode_invalid");
                case SandModeProbe.Legacy:
                    return new CursorSandParse(CursorAllowanceMode.Legacy, ReadLegacy(root, out var legacyError),
                        legacyError);
                default:
                    return new CursorSandParse(CursorAllowanceMode.Unconfirmed, ReadLegacy(root, out var absentError),
                        absentError);
            }
        }
        catch (JsonException)
        {
            return new CursorSandParse(CursorAllowanceMode.Unconfirmed, Array.Empty<QuotaWindowObservation>(),
                "grok_bot_json_invalid");
        }
    }

    private enum SandModeProbe
    {
        Absent,
        Legacy,
        Unified,
        NotBoolean,
    }

    private static SandModeProbe ProbeMode(JsonElement root)
    {
        if (!root.TryGetProperty("isUnified", out var value) &&
            !root.TryGetProperty("is_unified", out value))
        {
            return SandModeProbe.Absent;
        }

        if (value.ValueKind == JsonValueKind.True) return SandModeProbe.Unified;
        if (value.ValueKind == JsonValueKind.False) return SandModeProbe.Legacy;
        return SandModeProbe.NotBoolean;
    }

    private static CursorSandParse ReadUnified(JsonElement root, DateTimeOffset nowUtc)
    {
        var includedZero = JsonQuotaFields.GetBoolean(root, "includedLimitZero", "included_limit_zero") == true;
        var hasIncluded = JsonQuotaFields.GetBoolean(root, "hasNonZeroIncludedLimit", "has_non_zero_included_limit");
        if (includedZero || hasIncluded == false)
        {
            return new CursorSandParse(CursorAllowanceMode.Unified, Array.Empty<QuotaWindowObservation>(),
                "shared_weekly_not_included");
        }

        var usedPresent = root.TryGetProperty("usagePercent", out _) || root.TryGetProperty("usage_percent", out _);
        var used = usedPresent ? JsonQuotaFields.GetDouble(root, "usagePercent", "usage_percent") : null;
        if (!usedPresent || !used.HasValue)
        {
            return new CursorSandParse(CursorAllowanceMode.Unified, Array.Empty<QuotaWindowObservation>(),
                "shared_weekly_usage_missing");
        }

        if (!double.IsFinite(used.Value) || used.Value < 0 || used.Value > 100)
        {
            return new CursorSandParse(CursorAllowanceMode.Unified, Array.Empty<QuotaWindowObservation>(),
                "shared_weekly_usage_invalid");
        }

        var start = JsonQuotaFields.GetDate(root, "currentPeriodStart", "current_period_start");
        var end = JsonQuotaFields.GetDate(root, "nextResetTimestampUtc", "next_reset_timestamp_utc",
            "nextResetAt", "next_reset_at");
        if (end.HasValue && end.Value <= nowUtc)
        {
            return new CursorSandParse(CursorAllowanceMode.Unified, Array.Empty<QuotaWindowObservation>(),
                "shared_weekly_period_elapsed");
        }

        if (start.HasValue && start.Value > nowUtc)
        {
            return new CursorSandParse(CursorAllowanceMode.Unified, Array.Empty<QuotaWindowObservation>(),
                "shared_weekly_period_not_current");
        }

        int? duration = null;
        if (start.HasValue && end.HasValue)
        {
            var minutes = (int)Math.Round((end.Value - start.Value).TotalMinutes, MidpointRounding.AwayFromZero);
            if (minutes <= 0)
            {
                return new CursorSandParse(CursorAllowanceMode.Unified, Array.Empty<QuotaWindowObservation>(),
                    "shared_weekly_period_elapsed");
            }

            duration = minutes;
        }

        return new CursorSandParse(CursorAllowanceMode.Unified, new[]
        {
            new QuotaWindowObservation(SharedWeeklyQuota.WindowId, SharedWeeklyQuota.DisplayName, used, end, duration,
                true, end.HasValue ? null : "重置时间未提供，不按 7 天推算"),
        }, null);
    }

    private static IReadOnlyList<QuotaWindowObservation> ReadLegacy(JsonElement root, out string? errorCode)
    {
        errorCode = null;
        var pooled = JsonQuotaFields.GetBoolean(root, "usesPooledEnterpriseAllowance",
            "uses_pooled_enterprise_allowance") == true;
        var includedZero = JsonQuotaFields.GetBoolean(root, "includedLimitZero", "included_limit_zero") == true;
        var hasIncluded = JsonQuotaFields.GetBoolean(root, "hasNonZeroIncludedLimit",
            "has_non_zero_included_limit");
        var usedPresent = root.TryGetProperty("usagePercent", out _) || root.TryGetProperty("usage_percent", out _);
        var used = usedPresent ? JsonQuotaFields.GetDouble(root, "usagePercent", "usage_percent") : null;
        var flagsPresent = root.TryGetProperty("usesPooledEnterpriseAllowance", out _) ||
                           root.TryGetProperty("uses_pooled_enterprise_allowance", out _) ||
                           root.TryGetProperty("includedLimitZero", out _) ||
                           root.TryGetProperty("included_limit_zero", out _) ||
                           root.TryGetProperty("hasNonZeroIncludedLimit", out _) ||
                           root.TryGetProperty("has_non_zero_included_limit", out _);
        if (!flagsPresent && !usedPresent)
        {
            errorCode = "grok_bot_unreadable";
            return Array.Empty<QuotaWindowObservation>();
        }

        if (pooled || includedZero || hasIncluded == false || !usedPresent || !used.HasValue)
        {
            errorCode = "grok_bot_not_included";
            return Array.Empty<QuotaWindowObservation>();
        }

        if (used.Value < 0 || used.Value > 100)
        {
            errorCode = "grok_bot_usage_invalid";
            return Array.Empty<QuotaWindowObservation>();
        }

        var reset = JsonQuotaFields.GetDate(root, "nextResetTimestampUtc", "next_reset_timestamp_utc",
            "nextResetAt", "next_reset_at");
        return new[]
        {
            new QuotaWindowObservation("sand", "Grok Bot 周额度", used, reset, null, true,
                reset.HasValue ? null : "重置时间未提供，不按 7 天推算"),
        };
    }
}

public static class CursorSessionCookie
{
    public static bool TryBuild(string accessToken, out string cookie, out string? opaqueIdentity)
    {
        cookie = string.Empty;
        opaqueIdentity = null;
        if (string.IsNullOrWhiteSpace(accessToken)) return false;
        if (!TryReadSubject(accessToken, out var subject) || string.IsNullOrWhiteSpace(subject))
            return false;
        var separator = subject.LastIndexOf('|');
        var accountId = separator >= 0 && separator < subject.Length - 1 ? subject[(separator + 1)..] : subject;
        if (string.IsNullOrWhiteSpace(accountId)) return false;
        opaqueIdentity = OpaqueIdentity.Hash("cursor|" + accountId);
        cookie = Uri.EscapeDataString(accountId + "::" + accessToken);
        return true;
    }

    public static bool TryReadSubject(string jwt, out string subject)
    {
        subject = string.Empty;
        var parts = jwt.Split('.');
        if (parts.Length < 2) return false;
        try
        {
            var json = Encoding.UTF8.GetString(FromBase64Url(parts[1]));
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("sub", out var sub) ||
                sub.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            subject = sub.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(subject);
        }
        catch (FormatException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }

        return Convert.FromBase64String(padded);
    }
}

public static class StableSubjectIdentity
{
    public static string? Hash(string providerPrefix, string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || !CursorSessionCookie.TryReadSubject(token, out var subject) ||
            string.IsNullOrWhiteSpace(subject))
        {
            return null;
        }

        var separator = subject.LastIndexOf('|');
        var accountId = separator >= 0 && separator < subject.Length - 1 ? subject[(separator + 1)..] : subject;
        return string.IsNullOrWhiteSpace(accountId) ? null : OpaqueIdentity.Hash(providerPrefix + accountId);
    }
}

public sealed record GrokAuthInspection(
    bool RecognizedRootToken,
    bool RecognizedIssuerEntries,
    bool HasExpiryField,
    bool Expired,
    bool Usable,
    bool HasRefreshToken,
    string FormatKind)
{
    public static GrokAuthInspection Unsupported { get; } =
        new(false, false, false, false, false, false, "unsupported");
    public static GrokAuthInspection Absent { get; } =
        new(false, false, false, false, false, false, "absent");

    public bool Renewable => HasRefreshToken && (Expired || !Usable);

    public string Format() =>
        "recognized_format=" + (Usable || RecognizedRootToken || RecognizedIssuerEntries ? "1" : "0") +
        " format_kind=" + FormatKind +
        " has_expiry_field=" + (HasExpiryField ? "1" : "0") +
        " expired=" + (Expired ? "1" : "0") +
        " usable=" + (Usable ? "1" : "0") +
        " has_refresh_token=" + (HasRefreshToken ? "1" : "0");
}

public sealed record GrokLoginInspection(
    bool FilePresent,
    bool RecognizedFormat,
    bool HasExpiryField,
    bool Expired,
    bool Usable,
    bool HasRefreshToken,
    string FormatKind)
{
    public static GrokLoginInspection Missing { get; } =
        new(false, false, false, false, false, false, "absent");

    public static GrokLoginInspection Injected(bool usable) =>
        new(true, usable, false, false, usable, false, usable ? "injected" : "absent");

    public bool Renewable => HasRefreshToken && (Expired || !Usable);

    public string Format() =>
        "file_present=" + (FilePresent ? "1" : "0") + " " +
        "recognized_format=" + (RecognizedFormat ? "1" : "0") +
        " format_kind=" + FormatKind +
        " has_expiry_field=" + (HasExpiryField ? "1" : "0") +
        " expired=" + (Expired ? "1" : "0") +
        " usable=" + (Usable ? "1" : "0") +
        " has_refresh_token=" + (HasRefreshToken ? "1" : "0");
}

public static class GrokAuthTokenParser
{
    public static string? Extract(string json) => Collect(json, DateTimeOffset.UtcNow).Token;

    public static string? Extract(string json, DateTimeOffset nowUtc) => Collect(json, nowUtc).Token;

    public static GrokAuthInspection Inspect(string json) => Collect(json, DateTimeOffset.UtcNow).Inspection;

    public static GrokAuthInspection Inspect(string json, DateTimeOffset nowUtc) => Collect(json, nowUtc).Inspection;

    private readonly record struct AuthCandidate(string Token, DateTimeOffset? ExpiresAt, bool IssuerEntry, bool Root);

    private sealed class WalkState
    {
        public List<AuthCandidate> Candidates { get; } = new();
        public bool HasRefreshToken { get; set; }
    }

    private readonly record struct CollectResult(string? Token, GrokAuthInspection Inspection);

    private static CollectResult Collect(string json, DateTimeOffset nowUtc)
    {
        var inspection = GrokAuthInspection.Unsupported;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return new CollectResult(null, inspection);

            var state = new WalkState();
            Walk(document.RootElement, state, 0, true);
            if (state.Candidates.Count == 0)
                return new CollectResult(null, inspection);

            var root = state.Candidates.Any(item => item.Root);
            var issuer = state.Candidates.Any(item => item.IssuerEntry);
            var hasExpiry = state.Candidates.Any(item => item.ExpiresAt.HasValue);
            var unexpired = state.Candidates.Where(item => !item.ExpiresAt.HasValue || item.ExpiresAt.Value > nowUtc)
                .ToArray();
            var dated = state.Candidates.Where(item => item.ExpiresAt.HasValue).ToArray();
            var expired = dated.Length > 0 && unexpired.Length == 0;
            var usable = unexpired.Length > 0;
            var kind = issuer ? "issuer_entries" : root ? "root_token" : "unsupported";
            inspection = new GrokAuthInspection(root, issuer, hasExpiry, expired && !usable, usable,
                state.HasRefreshToken, kind);
            var pick = unexpired
                .OrderByDescending(item => item.ExpiresAt ?? DateTimeOffset.MaxValue)
                .Select(item => item.Token)
                .FirstOrDefault();
            return new CollectResult(usable ? pick : null, inspection);
        }
        catch (JsonException)
        {
            inspection = GrokAuthInspection.Unsupported;
            return new CollectResult(null, inspection);
        }
    }

    private static void Walk(JsonElement element, WalkState state, int depth, bool isRoot)
    {
        if (element.ValueKind != JsonValueKind.Object || depth > 6) return;
        string? token = null;
        var issuerEntry = false;
        foreach (var name in new[] { "access_token", "accessToken", "token", "key" })
        {
            if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) continue;
            var text = value.GetString();
            if (string.IsNullOrWhiteSpace(text)) continue;
            token = text;
            issuerEntry = name == "key";
            break;
        }

        foreach (var refreshName in new[] { "refresh_token", "refreshToken" })
        {
            if (!element.TryGetProperty(refreshName, out var refresh) ||
                refresh.ValueKind != JsonValueKind.String) continue;
            if (!string.IsNullOrWhiteSpace(refresh.GetString()))
                state.HasRefreshToken = true;
        }

        DateTimeOffset? expires = JsonQuotaFields.GetDate(element, "expires_at", "expiresAt", "expiry");
        if (token is not null && !expires.HasValue)
            expires = JwtExpiry(token);

        if (token is not null)
            state.Candidates.Add(new AuthCandidate(token, expires, issuerEntry, isRoot && !issuerEntry));

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object)
                Walk(property.Value, state, depth + 1, false);
        }
    }

    private static DateTimeOffset? JwtExpiry(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var json = Encoding.UTF8.GetString(CursorSessionCookieBase64.FromBase64Url(parts[1]));
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("exp", out var exp)) return null;
            if (exp.ValueKind == JsonValueKind.Number && exp.TryGetInt64(out var seconds))
                return JsonQuotaFields.FromUnixFlexible(seconds);
        }
        catch (FormatException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }
}

internal static class CursorSessionCookieBase64
{
    public static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }

        return Convert.FromBase64String(padded);
    }
}

public sealed record BoundAccountIdentity(string Hash, string Namespace, string? PresenceFlags = null)
{
    public const string Account = "account";
    public const string User = "user";
    public const string Email = "email";
}

public sealed record AccountIdentityShape(
    bool HasError,
    bool HasResult,
    bool HasAccountObject,
    bool AccountIsNull,
    bool RequiresOpenaiAuth,
    bool HasAccountId,
    bool HasUserId,
    bool HasEmail,
    string AccountType)
{
    public static AccountIdentityShape Empty { get; } = new(false, false, false, false, false, false, false, false, "none");

    public string Format() =>
        "error=" + Flag(HasError) +
        " result=" + Flag(HasResult) +
        " account=" + Flag(HasAccountObject) +
        " account_null=" + Flag(AccountIsNull) +
        " requires_openai_auth=" + Flag(RequiresOpenaiAuth) +
        " account_id=" + Flag(HasAccountId) +
        " user_id=" + Flag(HasUserId) +
        " email=" + Flag(HasEmail) +
        " type=" + AccountTypeFlag(AccountType);

    private static string Flag(bool value) => value ? "1" : "0";

    public static string AccountTypeFlag(string? value) => value switch
    {
        "chatgpt" => "chatgpt",
        "apiKey" => "apiKey",
        "amazonBedrock" => "amazonBedrock",
        _ => string.IsNullOrWhiteSpace(value) ? "none" : "unknown",
    };
}

public static class BoundIdentity
{
    public static string HashAccount(string id) => OpaqueIdentity.Hash("codex|account|" + id.Trim());
    public static string HashUser(string id) => OpaqueIdentity.Hash("codex|user|" + id.Trim());
    public static string HashEmail(string email) =>
        OpaqueIdentity.Hash("codex|email|" + email.Trim().ToLowerInvariant());
}

public static class AccountIdentityParser
{
    public static string? ParseOpaqueHash(string? json) => Parse(json)?.Hash;

    public static string? ReadPlanType(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.TryGetProperty("error", out _)) return null;
            var result = root.TryGetProperty("result", out var resultElement) &&
                         resultElement.ValueKind == JsonValueKind.Object
                ? resultElement
                : root;
            var plan = JsonQuotaFields.GetString(result, "planType", "plan_type");
            if (result.TryGetProperty("account", out var account) && account.ValueKind == JsonValueKind.Object)
                plan ??= JsonQuotaFields.GetString(account, "planType", "plan_type");
            return QuotaJsonParser.SanitizePlanType(plan);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static BoundAccountIdentity? Parse(string? json) => Parse(json, out _);

    public static BoundAccountIdentity? Parse(string? json, out AccountIdentityShape shape)
    {
        shape = AccountIdentityShape.Empty;
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            var hasError = root.TryGetProperty("error", out _);
            var hasResult = root.TryGetProperty("result", out var resultElement) &&
                            resultElement.ValueKind == JsonValueKind.Object;
            var result = hasResult ? resultElement : root;
            if (hasError)
            {
                shape = new AccountIdentityShape(true, hasResult, false, false, false, false, false, false, "none");
                return null;
            }

            var accountNull = false;
            JsonElement? account = null;
            if (result.TryGetProperty("account", out var accountElement))
            {
                if (accountElement.ValueKind == JsonValueKind.Null) accountNull = true;
                else if (accountElement.ValueKind == JsonValueKind.Object) account = accountElement;
            }

            var requiresAuth = JsonQuotaFields.GetBoolean(result, "requiresOpenaiAuth", "requires_openai_auth") == true;
            var accountType = account is { } typed
                ? AccountIdentityShape.AccountTypeFlag(JsonQuotaFields.GetString(typed, "type"))
                : "none";
            var accountId = FirstAccountId(result) ?? NestedAccountId(result, "account") ??
                            NestedAccountId(result, "profile");
            var userId = FirstUserId(result) ?? NestedUserId(result, "account") ?? NestedUserId(result, "user");
            var email = JsonQuotaFields.GetString(result, "email") ??
                        NestedEmail(result, "account") ?? NestedEmail(result, "user") ??
                        NestedEmail(result, "profile");
            shape = new AccountIdentityShape(false, hasResult, account is not null, accountNull, requiresAuth,
                !string.IsNullOrWhiteSpace(accountId), !string.IsNullOrWhiteSpace(userId),
                !string.IsNullOrWhiteSpace(email), accountType);

            if (!string.IsNullOrWhiteSpace(accountId))
                return new BoundAccountIdentity(BoundIdentity.HashAccount(accountId), BoundAccountIdentity.Account,
                    shape.Format());
            if (!string.IsNullOrWhiteSpace(userId))
                return new BoundAccountIdentity(BoundIdentity.HashUser(userId), BoundAccountIdentity.User,
                    shape.Format());
            if (!string.IsNullOrWhiteSpace(email))
                return new BoundAccountIdentity(BoundIdentity.HashEmail(email), BoundAccountIdentity.Email,
                    shape.Format());
            return null;
        }
        catch (JsonException)
        {
            shape = AccountIdentityShape.Empty;
            return null;
        }
    }

    private static string? NestedAccountId(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var nested) && nested.ValueKind == JsonValueKind.Object
            ? FirstAccountId(nested)
            : null;

    private static string? NestedUserId(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var nested) && nested.ValueKind == JsonValueKind.Object
            ? FirstUserId(nested)
            : null;

    private static string? NestedEmail(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var nested) && nested.ValueKind == JsonValueKind.Object
            ? JsonQuotaFields.GetString(nested, "email")
            : null;

    private static string? FirstAccountId(JsonElement element)
    {
        foreach (var name in new[] { "accountId", "account_id", "chatgptAccountId", "chatgpt_account_id" })
        {
            var value = JsonQuotaFields.GetString(element, name);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        return null;
    }

    private static string? FirstUserId(JsonElement element)
    {
        foreach (var name in new[] { "userId", "user_id" })
        {
            var value = JsonQuotaFields.GetString(element, name);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        return null;
    }
}

public static class AttributionClassifier
{
    public enum Kind
    {
        Bound,
        Unverified,
        Foreign,
    }

    public static Kind Classify(string? sessionAccountId, string? boundAccountId)
    {
        if (string.IsNullOrWhiteSpace(boundAccountId) || string.IsNullOrWhiteSpace(sessionAccountId))
            return Kind.Unverified;
        return string.Equals(sessionAccountId.Trim(), boundAccountId.Trim(), StringComparison.Ordinal)
            ? Kind.Bound
            : Kind.Foreign;
    }
}

public sealed record PiCodexCredential(string AccessToken, string AccountId);

public sealed record PiCodexAuthInspection(
    bool HasOpenAiCodexKey,
    bool TypeOauth,
    bool HasAccess,
    bool HasAccountId,
    bool HasExpiryField,
    bool Expired,
    bool Usable,
    string FormatKind)
{
    public static PiCodexAuthInspection Missing { get; } =
        new(false, false, false, false, false, false, false, "absent");
}

public sealed record PiCodexLoginInspection(
    bool FilePresent,
    bool HasOpenAiCodexKey,
    bool TypeOauth,
    bool HasAccess,
    bool HasAccountId,
    bool HasExpiryField,
    bool Expired,
    bool Usable,
    string FormatKind)
{
    public static PiCodexLoginInspection Missing { get; } =
        new(false, false, false, false, false, false, false, false, "absent");

    public static PiCodexLoginInspection Injected(bool usable) =>
        new(true, usable, usable, usable, usable, false, false, usable, usable ? "injected" : "absent");

    public bool RecognizedFormat => TypeOauth && HasOpenAiCodexKey;

    public string Format() =>
        "file_present=" + (FilePresent ? "1" : "0") +
        " openai_codex_key=" + (HasOpenAiCodexKey ? "1" : "0") +
        " type_oauth=" + (TypeOauth ? "1" : "0") +
        " has_access=" + (HasAccess ? "1" : "0") +
        " has_account_id=" + (HasAccountId ? "1" : "0") +
        " has_expiry_field=" + (HasExpiryField ? "1" : "0") +
        " expired=" + (Expired ? "1" : "0") +
        " usable=" + (Usable ? "1" : "0") +
        " format_kind=" + FormatKind;
}

public static class PiCodexAuthParser
{
    public const string ProviderKey = "openai-codex";
    private const string ChatgptAuthClaim = "https://api.openai.com/auth";

    public static PiCodexCredential? Extract(string json) => Collect(json, DateTimeOffset.UtcNow).Credential;

    public static PiCodexAuthInspection Inspect(string json) => Collect(json, DateTimeOffset.UtcNow).Inspection;

    private readonly record struct CollectResult(PiCodexCredential? Credential, PiCodexAuthInspection Inspection);

    private static CollectResult Collect(string json, DateTimeOffset nowUtc)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return new CollectResult(null, PiCodexAuthInspection.Missing);

            if (!document.RootElement.TryGetProperty(ProviderKey, out var entry) ||
                entry.ValueKind != JsonValueKind.Object)
            {
                return new CollectResult(null, new PiCodexAuthInspection(false, false, false, false, false, false,
                    false, "missing_key"));
            }

            var type = JsonQuotaFields.GetString(entry, "type");
            var typeOauth = string.Equals(type, "oauth", StringComparison.Ordinal);
            var access = JsonQuotaFields.GetString(entry, "access");
            var hasAccess = !string.IsNullOrWhiteSpace(access);
            var accountId = JsonQuotaFields.GetString(entry, "accountId", "account_id");
            if (string.IsNullOrWhiteSpace(accountId) && hasAccess)
                accountId = AccountIdFromAccess(access!);
            var hasAccountId = !string.IsNullOrWhiteSpace(accountId);
            var expires = JsonQuotaFields.GetDate(entry, "expires", "expires_at", "expiresAt");
            if (!expires.HasValue && hasAccess)
                expires = AccessExpiry(access!);
            var expired = expires.HasValue && expires.Value <= nowUtc;
            var usable = typeOauth && hasAccess && hasAccountId && !expired;
            var kind = !typeOauth ? "not_oauth" : expired ? "oauth_expired" : usable ? "oauth" : "oauth_incomplete";
            var inspection = new PiCodexAuthInspection(true, typeOauth, hasAccess, hasAccountId, expires.HasValue,
                expired, usable, kind);
            if (!usable) return new CollectResult(null, inspection);
            return new CollectResult(new PiCodexCredential(access!, accountId!), inspection);
        }
        catch (JsonException)
        {
            return new CollectResult(null, new PiCodexAuthInspection(false, false, false, false, false, false, false,
                "unreadable"));
        }
    }

    private static string? AccountIdFromAccess(string accessToken)
    {
        var payload = DecodeJwtPayload(accessToken);
        if (payload is null) return null;
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty(ChatgptAuthClaim, out var auth) ||
                auth.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return JsonQuotaFields.GetString(auth, "chatgpt_account_id");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DateTimeOffset? AccessExpiry(string accessToken)
    {
        var payload = DecodeJwtPayload(accessToken);
        if (payload is null) return null;
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("exp", out var exp)) return null;
            if (exp.ValueKind == JsonValueKind.Number && exp.TryGetInt64(out var seconds))
                return JsonQuotaFields.FromUnixFlexible(seconds);
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static string? DecodeJwtPayload(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            return Encoding.UTF8.GetString(CursorSessionCookieBase64.FromBase64Url(parts[1]));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

public static class PiCodexQuotaParser
{
    public static IReadOnlyList<QuotaWindowObservation> Parse(string json, DateTimeOffset nowUtc, out string? error,
        out string flags)
    {
        error = null;
        flags = "rate_limit=0 primary_window=0 secondary_window=0 used_percent=0 reset_after_seconds=0";
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 12 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "pi_codex_usage_shape";
                return Array.Empty<QuotaWindowObservation>();
            }

            if (!document.RootElement.TryGetProperty("rate_limit", out var rateLimit) ||
                rateLimit.ValueKind != JsonValueKind.Object)
            {
                error = "pi_codex_rate_limit_missing";
                return Array.Empty<QuotaWindowObservation>();
            }

            var windows = new List<QuotaWindowObservation>(2);
            var primary = ReadWindow(rateLimit, "primary_window", "主用量窗口", nowUtc, out var primaryUsed,
                out var primaryReset);
            var secondary = ReadWindow(rateLimit, "secondary_window", "辅用量窗口", nowUtc, out var secondaryUsed,
                out var secondaryReset);
            if (primary is not null) windows.Add(primary);
            if (secondary is not null) windows.Add(secondary);
            flags = "rate_limit=1" +
                    " primary_window=" + (primary is null ? "0" : "1") +
                    " secondary_window=" + (secondary is null ? "0" : "1") +
                    " used_percent=" + (primaryUsed || secondaryUsed ? "1" : "0") +
                    " reset_after_seconds=" + (primaryReset || secondaryReset ? "1" : "0");
            if (windows.Count == 0)
            {
                error = "pi_codex_windows_missing";
                return Array.Empty<QuotaWindowObservation>();
            }

            return windows;
        }
        catch (JsonException)
        {
            error = "pi_codex_usage_shape";
            return Array.Empty<QuotaWindowObservation>();
        }
    }

    private static QuotaWindowObservation? ReadWindow(JsonElement rateLimit, string name, string displayName,
        DateTimeOffset nowUtc, out bool hasUsed, out bool hasReset)
    {
        hasUsed = false;
        hasReset = false;
        if (!rateLimit.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object)
            return null;
        var used = JsonQuotaFields.GetDouble(window, "used_percent", "usedPercent");
        hasUsed = used.HasValue;
        var resetAfter = JsonQuotaFields.GetDouble(window, "reset_after_seconds", "resetAfterSeconds");
        hasReset = resetAfter.HasValue;
        DateTimeOffset? resetsAt = null;
        string? missingReset = null;
        if (resetAfter.HasValue && double.IsFinite(resetAfter.Value))
            resetsAt = nowUtc.AddSeconds(Math.Max(0, resetAfter.Value));
        else
            missingReset = "重置时间未提供";
        return new QuotaWindowObservation(name, displayName, used, resetsAt, null, used.HasValue, missingReset);
    }
}

public sealed record ClaudeOAuthInspection(
    bool Recognized,
    bool HasAccess,
    bool HasExpiryField,
    bool Expired,
    bool Usable,
    string FormatKind)
{
    public static ClaudeOAuthInspection Unsupported { get; } = new(false, false, false, false, false, "unsupported");
    public static ClaudeOAuthInspection Unreadable { get; } = new(false, false, false, false, false, "unreadable");

    public string Format() =>
        "recognized=" + (Recognized ? "1" : "0") +
        " has_access=" + (HasAccess ? "1" : "0") +
        " has_expiry=" + (HasExpiryField ? "1" : "0") +
        " expired=" + (Expired ? "1" : "0") +
        " usable=" + (Usable ? "1" : "0") +
        " format_kind=" + FormatKind;
}

public static class ClaudeOAuthParser
{
    public const int MaxAccessTokenChars = 16384;

    public static ClaudeOAuthInspection Inspect(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!TryGetOAuth(document.RootElement, out var oauth))
                return ClaudeOAuthInspection.Unsupported;
            var token = ReadAccessToken(oauth);
            var hasAccess = token is not null;
            var hasExpiry = oauth.TryGetProperty("expiresAt", out var expiry) &&
                            expiry.ValueKind is JsonValueKind.Number or JsonValueKind.String;
            var expired = false;
            if (hasExpiry)
            {
                var expiresAt = JsonQuotaFields.ReadDate(expiry);
                expired = expiresAt.HasValue && expiresAt.Value <= DateTimeOffset.UtcNow;
            }

            return new ClaudeOAuthInspection(true, hasAccess, hasExpiry, expired, hasAccess && !expired,
                "claude_ai_oauth");
        }
        catch (JsonException)
        {
            return ClaudeOAuthInspection.Unreadable;
        }
    }

    public static string? ExtractAccessToken(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!TryGetOAuth(document.RootElement, out var oauth)) return null;
            return ReadAccessToken(oauth);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetOAuth(JsonElement root, out JsonElement oauth)
    {
        oauth = default;
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (!root.TryGetProperty("claudeAiOauth", out oauth) || oauth.ValueKind != JsonValueKind.Object)
            return false;
        return true;
    }

    private static string? ReadAccessToken(JsonElement oauth)
    {
        if (!oauth.TryGetProperty("accessToken", out var token) || token.ValueKind != JsonValueKind.String)
            return null;
        var value = token.GetString();
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxAccessTokenChars) return null;
        return value;
    }
}

public static class ClaudeQuotaParser
{
    private static readonly (string Id, string DisplayName, int Minutes)[] KnownWindows =
    {
        ("five_hour", "5 小时", 300),
        ("seven_day", "每周", 10080),
        ("seven_day_sonnet", "每周 Sonnet", 10080),
        ("seven_day_opus", "每周 Opus", 10080),
    };

    public static IReadOnlyList<QuotaWindowObservation> Parse(string json, out string? error, out string flags)
    {
        error = null;
        flags = "parse=0";
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "claude_schema_unsupported";
                flags = "parse=1 root=not_object keys=none";
                return Array.Empty<QuotaWindowObservation>();
            }

            var windows = new List<QuotaWindowObservation>(4);
            var parts = new List<string> { "parse=1" };
            if (TryConsumeLimits(root, windows, parts))
            {
                parts.Add("legacy=0");
                parts.Add("keys=" + SafeKeyNames(root));
                flags = string.Join(" ", parts);
                return windows;
            }

            parts.Add("legacy=1");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var known in KnownWindows)
            {
                seen.Add(known.Id);
                if (TryReadWindow(root, known.Id, known.DisplayName, known.Minutes, out var window, out var presence))
                    windows.Add(window!);
                parts.Add(presence);
            }

            foreach (var property in root.EnumerateObject())
            {
                if (seen.Contains(property.Name)) continue;
                if (!IsAdditionalModelWindow(property.Name)) continue;
                var suffix = property.Name["seven_day_".Length..];
                if (TryReadWindow(root, property.Name, "每周 " + suffix, 10080, out var window, out var presence))
                    windows.Add(window!);
                parts.Add(presence);
            }

            parts.Add("keys=" + SafeKeyNames(root));
            flags = string.Join(" ", parts);
            if (windows.Count == 0)
            {
                error = "claude_schema_unsupported";
                return Array.Empty<QuotaWindowObservation>();
            }

            return windows;
        }
        catch (JsonException)
        {
            error = "claude_schema_unsupported";
            flags = "parse=0";
            return Array.Empty<QuotaWindowObservation>();
        }
    }

    private static bool TryConsumeLimits(JsonElement root, List<QuotaWindowObservation> windows, List<string> parts)
    {
        if (!root.TryGetProperty("limits", out var limits))
        {
            parts.Add("limits=absent");
            return false;
        }

        if (limits.ValueKind != JsonValueKind.Array)
        {
            parts.Add("limits=unsupported");
            return false;
        }

        var accepted = new Dictionary<string, QuotaWindowObservation>(StringComparer.Ordinal);
        var rejected = new HashSet<string>(StringComparer.Ordinal);
        var kept = 0;
        var examined = 0;
        var inactive = 0;
        var unknown = 0;
        var range = 0;
        var unnamed = 0;
        foreach (var item in limits.EnumerateArray())
        {
            examined++;
            if (examined > 16)
                break;
            if (!TryReadLimit(item, out var window, out var skip) || window is null)
            {
                if (skip == "inactive") inactive++;
                else if (skip == "unknown") unknown++;
                else if (skip == "range") range++;
                else if (skip == "scope") unnamed++;
                continue;
            }
            if (rejected.Contains(window.WindowId))
                continue;
            if (accepted.TryGetValue(window.WindowId, out var existing))
            {
                if (existing.UsedPercent != window.UsedPercent || existing.ResetsAtUtc != window.ResetsAtUtc)
                {
                    accepted.Remove(window.WindowId);
                    rejected.Add(window.WindowId);
                }

                continue;
            }

            accepted[window.WindowId] = window;
            kept++;
        }

        parts.Add("limits=" + examined.ToString(CultureInfo.InvariantCulture));
        parts.Add("limits_kept=" + accepted.Count.ToString(CultureInfo.InvariantCulture));
        parts.Add("limits_inactive=" + inactive.ToString(CultureInfo.InvariantCulture));
        parts.Add("limits_unknown=" + unknown.ToString(CultureInfo.InvariantCulture));
        parts.Add("limits_range=" + range.ToString(CultureInfo.InvariantCulture));
        parts.Add("limits_scope=" + unnamed.ToString(CultureInfo.InvariantCulture));
        if (accepted.Count == 0)
            return false;

        foreach (var id in new[] { "five_hour", "seven_day" })
        {
            if (accepted.Remove(id, out var known))
                windows.Add(known);
        }

        foreach (var extra in accepted.Values.OrderBy(window => window.WindowId, StringComparer.Ordinal))
            windows.Add(extra);
        return kept > 0 || windows.Count > 0;
    }

    private static bool TryReadLimit(JsonElement item, out QuotaWindowObservation? window, out string skip)
    {
        window = null;
        skip = "shape";
        if (item.ValueKind != JsonValueKind.Object)
            return false;

        // is_active false marks the window that is not currently binding the
        // request. A valid session, weekly, or named model window still counts.
        var kind = JsonQuotaFields.GetString(item, "kind");
        if (!TryMapLimit(item, kind, out var id, out var displayName, out var minutes))
        {
            skip = kind == "weekly_scoped" ? "scope" : "unknown";
            return false;
        }

        if (!TryReadUsedPercent(item, out var used))
        {
            skip = "range";
            return false;
        }

        skip = "";
        var reset = JsonQuotaFields.GetDate(item, "resets_at", "resetsAt");
        window = new QuotaWindowObservation(id, displayName, used, reset, minutes, true,
            reset.HasValue ? null : "重置时间未提供，不推算");
        return true;
    }

    private static bool TryMapLimit(JsonElement item, string? kind, out string id, out string displayName, out int minutes)
    {
        id = "";
        displayName = "";
        minutes = 0;
        if (kind is "session" or "five_hour")
        {
            id = "five_hour";
            displayName = "5 小时";
            minutes = 300;
            return true;
        }

        if (kind is "weekly_all" or "weekly" or "seven_day")
        {
            id = "seven_day";
            displayName = "每周";
            minutes = 10080;
            return true;
        }

        if (kind != "weekly_scoped")
            return false;
        if (!TryModelLabel(item, out var model))
            return false;
        var slug = ModelWindowId(model);
        if (slug is null)
            return false;
        id = slug;
        displayName = "每周 " + model;
        minutes = 10080;
        return true;
    }

    private static bool TryReadUsedPercent(JsonElement item, out double used)
    {
        used = 0;
        var hasPercent = TryFiniteNumber(item, "percent", out var percent);
        var hasUtilization = TryFiniteNumber(item, "utilization", out var utilization);
        if (item.TryGetProperty("percent", out var percentValue) &&
            percentValue.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) &&
            !hasPercent)
        {
            return false;
        }

        if (item.TryGetProperty("utilization", out var utilizationValue) &&
            utilizationValue.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) &&
            !hasUtilization)
        {
            return false;
        }

        if (hasPercent && hasUtilization && Math.Abs(percent - utilization) > 0.001d)
            return false;
        if (!hasPercent && !hasUtilization)
            return false;
        used = hasPercent ? percent : utilization;
        return used >= 0d && used <= 100d;
    }

    private static bool TryFiniteNumber(JsonElement item, string name, out double number)
    {
        number = 0;
        var value = JsonQuotaFields.GetDouble(item, name);
        if (!value.HasValue || !double.IsFinite(value.Value))
            return false;
        number = value.Value;
        return true;
    }

    private static bool TryModelLabel(JsonElement item, out string model)
    {
        model = "";
        if (!item.TryGetProperty("scope", out var scope) || scope.ValueKind != JsonValueKind.Object)
            return false;
        if (!scope.TryGetProperty("model", out var modelElement) || modelElement.ValueKind != JsonValueKind.Object)
            return false;
        var raw = JsonQuotaFields.GetString(modelElement, "display_name", "displayName");
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        var trimmed = raw.Trim();
        if (trimmed.Length is < 1 or > 24)
            return false;
        foreach (var character in trimmed)
        {
            if (character is not (' ' or '+' or '.' or '-' or '_') && !char.IsAsciiLetterOrDigit(character))
                return false;
        }

        if (ProviderLiveDiagnostic.SecretHits(trimmed).Count > 0)
            return false;
        model = trimmed;
        return true;
    }

    private static string? ModelWindowId(string model)
    {
        var builder = new StringBuilder("seven_day_");
        var pendingBreak = false;
        var wrote = false;
        foreach (var character in model)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                if (pendingBreak && wrote)
                    builder.Append('_');
                pendingBreak = false;
                builder.Append(char.ToLowerInvariant(character));
                wrote = true;
            }
            else
            {
                pendingBreak = wrote;
            }
        }

        if (!wrote || builder.Length is < 11 or > 40)
            return null;
        var id = builder.ToString();
        return id == "seven_day" ? null : id;
    }

    private static bool TryReadWindow(JsonElement root, string id, string displayName, int minutes,
        out QuotaWindowObservation? window, out string presence)
    {
        window = null;
        if (!root.TryGetProperty(id, out var element) ||
            element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            presence = id + ":absent";
            return false;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            presence = id + ":unsupported";
            return false;
        }

        var used = JsonQuotaFields.GetDouble(element, "utilization");
        var hasUsed = used.HasValue && double.IsFinite(used.Value) && used.Value >= 0d && used.Value <= 100d;
        var reset = JsonQuotaFields.GetDate(element, "resets_at");
        presence = id + ":used=" + (hasUsed ? "1" : "0") + ",reset=" + (reset.HasValue ? "1" : "0");
        if (!hasUsed) return false;
        window = new QuotaWindowObservation(id, displayName, used, reset, minutes, true,
            reset.HasValue ? null : "重置时间未提供，不推算");
        return true;
    }

    private static bool IsAdditionalModelWindow(string name)
    {
        const string prefix = "seven_day_";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.Length <= prefix.Length || name.Length > 40)
            return false;
        for (var index = prefix.Length; index < name.Length; index++)
        {
            var character = name[index];
            if (character is not '_' && !char.IsAsciiLetterOrDigit(character)) return false;
        }

        return true;
    }

    private static string SafeKeyNames(JsonElement root)
    {
        var names = new List<string>();
        foreach (var property in root.EnumerateObject())
        {
            if (names.Count >= 8) break;
            var name = property.Name;
            if (name.Length is 0 or > 40) continue;
            if (!name.All(character => character is '_' or '-' || char.IsAsciiLetterOrDigit(character))) continue;
            if (name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("sk-", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("eyJ", StringComparison.Ordinal))
            {
                continue;
            }

            names.Add(name);
        }

        return names.Count == 0 ? "none" : string.Join(",", names);
    }
}
