namespace CodexUsageHud.Core;

public static class ProviderLiveDiagnostic
{
    public sealed record LiveSlotReport(
        string SlotId,
        string Status,
        int WindowCount,
        string? GlanceWindow,
        double? RemainingPercent,
        bool ResetPresent,
        int AgeSeconds,
        bool IdentityPresent,
        bool IdentityEqual,
        string? ErrorCode,
        string? Dependency,
        string? FieldFlags = null,
        string? WindowDetail = null);

    public sealed record LiveRunResult(
        IReadOnlyList<LiveSlotReport> Slots,
        IdentityColumnPresence Schema,
        bool PiCodexLoginPresent,
        string Output);

    public static async Task<LiveRunResult> RunAsync(CancellationToken cancellationToken, string? slotFilter = null)
    {
        var primaryHome = CodexHomeResolver.Resolve();
        var piPresence = WindowsLoginPresence.PiCodexAuthFile();
        var schema = new StateMetadataReader().InspectIdentityColumns(
            Path.Combine(primaryHome, "state_5.sqlite"));

        using var http = new AllowlistedHttpsSender(TimeSpan.FromSeconds(8));
        var cursorTokens = DefaultTokenSources.Cursor();
        var grokTokens = DefaultTokenSources.Grok();
        var piTokens = DefaultTokenSources.PiCodex();
        var codex = new CodexQuotaProfile();
        var piCodex = new PiCodexQuotaAdapter(http, piTokens);
        var cursor = new CursorQuotaAdapter(http, cursorTokens);
        var grok = new GrokQuotaAdapter(http, grokTokens);
        var grokBot = new GrokBotQuotaAdapter(http, cursorTokens);
        var claude = new ClaudeQuotaAdapter(http, DefaultTokenSources.Claude());
        var defaults = ProviderAccessSettings.Default();
        var enabled = new ProviderAccessSettings(defaults.Slots.Select(slot => slot with { Enabled = true }).ToArray(),
            CompactLayoutModes.Rail, true);

        string? cursorIdentity = null;
        string? grokBotIdentity = null;
        string? primaryIdentity = null;
        string? secondaryIdentity = null;
        var reports = new List<LiveSlotReport>(ProviderSlotIds.All.Count);
        foreach (var slot in enabled.Slots)
        {
            if (!string.IsNullOrWhiteSpace(slotFilter) &&
                !string.Equals(slot.SlotId, slotFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            cancellationToken.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(slot.SlotId == ProviderSlotIds.Grok
                ? TimeSpan.FromSeconds(40)
                : slot.SlotId == ProviderSlotIds.CodexSecondary
                    ? TimeSpan.FromSeconds(28)
                    : TimeSpan.FromSeconds(10));
            ProviderSlotSnapshot snapshot;
            string? dependency = null;
            try
            {
                snapshot = slot.SlotId switch
                {
                    ProviderSlotIds.CodexPrimary =>
                        await codex.ReadAsync(slot, primaryHome, true, timeout.Token).ConfigureAwait(false),
                    ProviderSlotIds.CodexSecondary =>
                        await piCodex.ReadAsync(slot, timeout.Token).ConfigureAwait(false),
                    ProviderSlotIds.Cursor =>
                        await cursor.ReadAsync(slot, timeout.Token).ConfigureAwait(false),
                    ProviderSlotIds.Grok =>
                        await grok.ReadAsync(slot, timeout.Token).ConfigureAwait(false),
                    ProviderSlotIds.GrokBot =>
                        await grokBot.ReadAsync(slot, timeout.Token).ConfigureAwait(false),
                    ProviderSlotIds.Claude =>
                        await claude.ReadAsync(slot, timeout.Token).ConfigureAwait(false),
                    _ => ProviderQuotaPresentation.Placeholder(slot, false, QuotaSlotStatus.Unavailable,
                        "未知槽", "slot_unknown", "slot_unknown"),
                };
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                snapshot = ProviderQuotaPresentation.Placeholder(slot, slot.SlotId == ProviderSlotIds.CodexPrimary,
                    QuotaSlotStatus.Unavailable, "此来源超时，不影响其他槽", "独立超时", "slot_timeout");
                dependency = "slot_timeout";
            }
            catch (Exception exception)
            {
                var code = ProviderHttpErrors.Sanitize(exception);
                snapshot = ProviderQuotaPresentation.Placeholder(slot, slot.SlotId == ProviderSlotIds.CodexPrimary,
                    QuotaSlotStatus.Unavailable, "此来源失败，不影响其他槽", code, code);
                dependency = code;
            }

            if (slot.SlotId == ProviderSlotIds.CodexSecondary)
                dependency ??= piPresence.Present ? null : "pi_codex_auth_file_absent";
            if (slot.SlotId == ProviderSlotIds.Grok)
            {
                var grokFile = WindowsLoginPresence.GrokAuthFile();
                dependency ??= grokFile.Present ? null : "grok_auth_file_absent";
            }
            if (slot.SlotId == ProviderSlotIds.Claude && dependency is null)
            {
                var claudeFile = WindowsLoginPresence.ClaudeCredentialsFile();
                var desktop = WindowsLoginPresence.ClaudeDesktopProfile();
                var failed = snapshot.Status is QuotaSlotStatus.NotConnected or QuotaSlotStatus.Unavailable;
                if (!claudeFile.Present && !desktop.Present)
                    dependency = "claude_credentials_absent";
                else if (!claudeFile.Present && desktop.Present && failed)
                    dependency = "claude_desktop_profile";
            }
            if (slot.SlotId == ProviderSlotIds.Cursor) cursorIdentity = snapshot.OpaqueIdentityHash;
            if (slot.SlotId == ProviderSlotIds.GrokBot) grokBotIdentity = snapshot.OpaqueIdentityHash;
            if (slot.SlotId == ProviderSlotIds.CodexPrimary) primaryIdentity = snapshot.OpaqueIdentityHash;
            if (slot.SlotId == ProviderSlotIds.CodexSecondary) secondaryIdentity = snapshot.OpaqueIdentityHash;

            reports.Add(ToReport(snapshot, dependency));
        }

        for (var index = 0; index < reports.Count; index++)
        {
            var report = reports[index];
            if (report.SlotId == ProviderSlotIds.GrokBot)
            {
                reports[index] = report with
                {
                    IdentityEqual = report.IdentityPresent &&
                                    !string.IsNullOrWhiteSpace(cursorIdentity) &&
                                    string.Equals(cursorIdentity, grokBotIdentity, StringComparison.Ordinal),
                };
            }

            if (report.SlotId == ProviderSlotIds.CodexSecondary)
            {
                reports[index] = report with
                {
                    IdentityEqual = report.IdentityPresent &&
                                    !string.IsNullOrWhiteSpace(primaryIdentity) &&
                                    string.Equals(primaryIdentity, secondaryIdentity, StringComparison.Ordinal),
                };
            }
        }

        var output = Format(reports, schema, piPresence.Present);
        return new LiveRunResult(reports, schema, piPresence.Present, output);
    }

    public static string Format(IReadOnlyList<LiveSlotReport> reports, IdentityColumnPresence schema,
        bool piCodexLoginPresent)
    {
        var lines = new List<string>
        {
            "provider_live_diagnostic isolated=1 settings_untouched=1",
            "schema database_present=" + Flag(schema.DatabasePresent) +
            " threads=" + Flag(schema.ThreadsTablePresent) +
            " account_id=" + Flag(schema.HasAccountId) +
            " chatgpt_account_id=" + Flag(schema.HasChatgptAccountId) +
            " user_id=" + Flag(schema.HasUserId),
            "pi_codex_login_present=" + Flag(piCodexLoginPresent),
        };
        foreach (var slot in reports)
        {
            lines.Add("slot=" + slot.SlotId +
                      " status=" + slot.Status +
                      " windows=" + slot.WindowCount +
                      " glance_window=" + SanitizeToken(slot.GlanceWindow) +
                      " remaining=" + (slot.RemainingPercent.HasValue
                          ? slot.RemainingPercent.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                          : "none") +
                      " reset_present=" + Flag(slot.ResetPresent) +
                      " age_s=" + slot.AgeSeconds +
                      " identity_present=" + Flag(slot.IdentityPresent) +
                      " identity_equal=" + Flag(slot.IdentityEqual) +
                      " error=" + SanitizeToken(slot.ErrorCode) +
                      " dependency=" + SanitizeToken(slot.Dependency) +
                      " fields=" + SanitizeToken(slot.FieldFlags) +
                      " windows_detail=" + SanitizeDetail(slot.WindowDetail));
        }

        return string.Join('\n', lines);
    }

    public static async Task<string> RunGrokRenewalProofAsync(CancellationToken cancellationToken)
    {
        var tokens = DefaultTokenSources.Grok();
        var binary = GrokCliModelsRenewer.ResolveExecutable();
        var before = tokens.InspectLogin();
        GrokRenewalOutcome renewal;
        try
        {
            renewal = await tokens.EnsureFreshAsync(true, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            renewal = new GrokRenewalOutcome(GrokRenewalKind.Timeout, true);
        }

        var after = tokens.InspectLogin();
        using var http = new AllowlistedHttpsSender(TimeSpan.FromSeconds(8));
        var adapter = new GrokQuotaAdapter(http, tokens);
        var settings = new ProviderSlotSettings(ProviderSlotIds.Grok, "Grok", true);
        ProviderSlotSnapshot snapshot;
        try
        {
            snapshot = await adapter.ReadAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            snapshot = ProviderQuotaPresentation.Placeholder(settings, false, QuotaSlotStatus.Unavailable,
                "Grok 请求超时", "独立超时", "grok_timeout");
        }

        var remaining = snapshot.GlanceRemainingPercent;
        var lines = new[]
        {
            "grok_renewal_proof isolated=0 settings_untouched=1 secrets_omitted=1",
            "binary_present=" + Flag(binary is not null),
            "before_file_present=" + Flag(before.FilePresent) +
            " before_recognized=" + Flag(before.RecognizedFormat) +
            " before_format_kind=" + SanitizeToken(before.FormatKind) +
            " before_has_expiry=" + Flag(before.HasExpiryField) +
            " before_expired=" + Flag(before.Expired) +
            " before_usable=" + Flag(before.Usable) +
            " before_has_refresh=" + Flag(before.HasRefreshToken) +
            " before_renewable=" + Flag(before.Renewable),
            "renewal_kind=" + renewal.Kind +
            " renewal_attempted=" + Flag(renewal.Attempted),
            "after_file_present=" + Flag(after.FilePresent) +
            " after_recognized=" + Flag(after.RecognizedFormat) +
            " after_format_kind=" + SanitizeToken(after.FormatKind) +
            " after_has_expiry=" + Flag(after.HasExpiryField) +
            " after_expired=" + Flag(after.Expired) +
            " after_usable=" + Flag(after.Usable) +
            " after_has_refresh=" + Flag(after.HasRefreshToken),
            "quota_status=" + snapshot.Status +
            " windows=" + snapshot.Windows.Count +
            " remaining=" + (remaining.HasValue
                ? remaining.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                : "none") +
            " reset_present=" + Flag(snapshot.Windows.Any(window => window.ResetsAtUtc.HasValue)) +
            " error=" + SanitizeToken(snapshot.ErrorCode) +
            " fields=" + SanitizeToken(snapshot.FieldPresenceFlags),
        };
        return string.Join('\n', lines);
    }

    public static IReadOnlyList<string> SecretHits(string text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
        var hits = new List<string>();
        foreach (var marker in new[]
                 {
                     "eyJ", "Bearer", "access_token", "accessToken", "refreshToken", "refresh_token",
                     "claudeAiOauth", "sk-ant", "WorkosCursorSessionToken",
                     "auth.json", ".credentials.json", "Exception:", "   at ",
                 })
        {
            if (text.Contains(marker, StringComparison.Ordinal))
                hits.Add(marker);
        }

        return hits;
    }

    private static LiveSlotReport ToReport(ProviderSlotSnapshot snapshot, string? dependency)
    {
        var error = snapshot.ErrorCode;
        if (!string.IsNullOrWhiteSpace(error) && !ProviderHttpErrors.IsStableCode(error))
            error = "http_failed";
        var age = Math.Max(0, (int)snapshot.ObservationAge(DateTimeOffset.UtcNow).TotalSeconds);
        return new LiveSlotReport(snapshot.SlotId, snapshot.Status.ToString(), snapshot.Windows.Count,
            snapshot.GlanceWindow?.DisplayName, snapshot.GlanceRemainingPercent,
            snapshot.Windows.Any(window => window.ResetsAtUtc.HasValue), age,
            !string.IsNullOrWhiteSpace(snapshot.OpaqueIdentityHash), false, error, dependency,
            snapshot.FieldPresenceFlags, SummarizeWindows(snapshot));
    }

    public static string SummarizeWindows(ProviderSlotSnapshot snapshot)
    {
        if (snapshot.Windows.Count == 0) return "none";
        return string.Join("; ", snapshot.Windows.Select(window =>
        {
            var used = window.UsedPercent.HasValue
                ? window.UsedPercent.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                : "none";
            var remaining = window.RemainingPercent.HasValue
                ? window.RemainingPercent.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                : "none";
            var reset = window.ResetsAtUtc.HasValue
                ? window.ResetsAtUtc.Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ",
                    System.Globalization.CultureInfo.InvariantCulture)
                : "none";
            return window.WindowId + " name=" + window.DisplayName + " used=" + used +
                   " remaining=" + remaining + " reset=" + reset;
        }));
    }

    private static string Flag(bool value) => value ? "1" : "0";

    private static string SanitizeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "none";
        return ProviderHttpErrors.IsStableCode(value) || value.All(character =>
            character is '_' or '-' or ' ' or '=' or ',' or ':' || char.IsLetterOrDigit(character) || character > 127)
            ? value.Replace('\n', ' ').Replace('\r', ' ')
            : "redacted";
    }

    private static string SanitizeDetail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "none";
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value.Replace('\n', ' ').Replace('\r', ' '))
        {
            if (character is '_' or '-' or ' ' or '=' or ':' or '.' or ';' or '%' or ',')
                builder.Append(character);
            else if (char.IsLetterOrDigit(character) || character > 127)
                builder.Append(character);
            else
                builder.Append('_');
        }

        var text = builder.ToString();
        return SecretHits(text).Count == 0 ? text : "redacted";
    }

    public static async Task<string> RunSecondaryCliQuotaAsync(string cliHome, bool comparePrimary,
        CancellationToken cancellationToken)
    {
        var home = SecondaryCodexHome.Normalize(cliHome);
        var primaryHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        var profile = new CodexQuotaProfile();
        var secondarySettings = new ProviderSlotSettings(ProviderSlotIds.CodexSecondary, "Codex 第二账户", true, home);
        ProviderSlotSnapshot secondary;
        string? dependency = null;
        try
        {
            secondary = await profile.ReadAsync(secondarySettings, home, false, cancellationToken, primaryHome)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            secondary = ProviderQuotaPresentation.Placeholder(secondarySettings, false, QuotaSlotStatus.Unavailable,
                "此来源超时，不影响其他槽", "独立超时", "slot_timeout");
            dependency = "slot_timeout";
        }
        catch (Exception exception)
        {
            dependency = exception.GetType().Name;
            secondary = ProviderQuotaPresentation.Placeholder(secondarySettings, false, QuotaSlotStatus.Unavailable,
                "此来源失败，不影响其他槽", dependency, dependency);
        }

        var auth = WindowsLoginPresence.CodexCliAuthFile(home);
        if (secondary.Status is QuotaSlotStatus.NotConnected or QuotaSlotStatus.Unavailable && !auth.Present)
            dependency ??= "codex_cli_auth_file_absent";
        dependency ??= secondary.ErrorCode;

        bool? identityEqual = null;
        bool primaryIdentityPresent = false;
        if (comparePrimary)
        {
            try
            {
                var primary = await profile.ReadAsync(
                    new ProviderSlotSettings(ProviderSlotIds.CodexPrimary, "Codex 当前", true),
                    primaryHome, true, cancellationToken).ConfigureAwait(false);
                primaryIdentityPresent = !string.IsNullOrWhiteSpace(primary.OpaqueIdentityHash);
                identityEqual = primaryIdentityPresent &&
                    !string.IsNullOrWhiteSpace(secondary.OpaqueIdentityHash) &&
                    string.Equals(primary.OpaqueIdentityHash, secondary.OpaqueIdentityHash, StringComparison.Ordinal);
            }
            catch (OperationCanceledException)
            {
                dependency ??= "primary_compare_timeout";
            }
            catch (Exception exception)
            {
                dependency ??= "primary_compare_" + exception.GetType().Name;
            }
        }

        var plan = PlanTypeFromFlags(secondary.FieldPresenceFlags);
        var lines = new List<string>
        {
            "secondary_cli_quota isolated=1 settings_untouched=1 secrets_omitted=1",
            "source=codex_cli_app_server",
            "local_analysis=" + Flag(secondary.SuppliesLocalAnalysis),
            "status=" + secondary.Status,
            "observed_at=" + secondary.ObservedAtUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ",
                System.Globalization.CultureInfo.InvariantCulture),
            "plan_type=" + SanitizeToken(plan),
            "identity_present=" + Flag(!string.IsNullOrWhiteSpace(secondary.OpaqueIdentityHash)),
            "primary_identity_present=" + Flag(primaryIdentityPresent),
            "identity_equal=" + Flag(identityEqual == true),
            "auth_file_present=" + Flag(auth.Present),
            "error=" + SanitizeToken(secondary.ErrorCode),
            "dependency=" + SanitizeToken(dependency),
            "windows=" + secondary.Windows.Count,
        };
        if (secondary.Windows.Count == 0)
            lines.Add("window=none");
        foreach (var window in secondary.Windows)
        {
            var used = window.UsedPercent.HasValue
                ? window.UsedPercent.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                : "none";
            var remaining = window.RemainingPercent.HasValue
                ? window.RemainingPercent.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                : "none";
            var duration = window.WindowDurationMinutes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none";
            var reset = window.ResetsAtUtc.HasValue
                ? window.ResetsAtUtc.Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ",
                    System.Globalization.CultureInfo.InvariantCulture)
                : "none";
            lines.Add("window id=" + SanitizeToken(window.WindowId) +
                      " name=" + SanitizeToken(window.DisplayName) +
                      " used=" + used +
                      " remaining=" + remaining +
                      " duration_mins=" + duration +
                      " reset=" + reset);
        }

        var output = string.Join('\n', lines);
        return SecretHits(output).Count == 0 ? output : "secondary_cli_quota redacted=1";
    }

    private static string? PlanTypeFromFlags(string? flags)
    {
        if (string.IsNullOrWhiteSpace(flags)) return null;
        const string marker = "plan_type=";
        var index = flags.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) return null;
        var value = flags[(index + marker.Length)..];
        var end = value.IndexOf(' ');
        return end < 0 ? value : value[..end];
    }
}
