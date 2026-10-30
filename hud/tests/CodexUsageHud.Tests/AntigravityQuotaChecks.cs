using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexUsageHud.App;
using CodexUsageHud.Core;

namespace CodexUsageHud.Tests;

internal static class AntigravityQuotaChecks
{
    public static int Run(string[] args)
    {
        if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            Console.Error.WriteLine("usage: --antigravity-quota <output-directory>");
            return 2;
        }

        var output = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(output);
        var log = new StringBuilder();
        try
        {
            ParserLocksFractionsAndIgnoresCredits(log);
            MissingFieldsDoNotInventPercent(log);
            ExpiredMissingResetAndUnknownWindow(log);
            AdapterStatuses(log);
            SettingsStayOffUntilEnabled(log);
            CoordinatorCacheTimeoutAndRetention(log).GetAwaiter().GetResult();
            SyntheticSevenSlotLayout(output, log);
            var logPath = Path.Combine(output, "antigravity-checks.log");
            File.WriteAllText(logPath, log.ToString());
            Console.WriteLine("antigravity-quota ok");
            Console.WriteLine(logPath);
            return 0;
        }
        catch (Exception exception)
        {
            log.AppendLine(exception.ToString());
            var logPath = Path.Combine(output, "antigravity-checks.log");
            File.WriteAllText(logPath, log.ToString());
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void ParserLocksFractionsAndIgnoresCredits(StringBuilder log)
    {
        const double geminiWeekly = 0.978570818901062;
        const double geminiFiveHour = 0.966951310634613;
        const double argonWeekly = 0.9128440022468567;
        var windows = AntigravityQuotaParser.Parse(FixtureJson(geminiWeekly, geminiFiveHour, argonWeekly), out var error);
        Equal<string?>(null, error);
        Equal(5, windows.Count);
        var weekly = One(windows, "gemini-weekly");
        var fiveHour = One(windows, "gemini-5h");
        var thirdWeekly = One(windows, "3p-weekly");
        var thirdFiveHour = One(windows, "3p-5h");
        var argon = One(windows, "gemini-argon-wtu-weekly");
        Equal("Gemini 模型 · 每周", weekly.DisplayName);
        Equal("Gemini 模型 · 5 小时", fiveHour.DisplayName);
        Equal("Claude 与 GPT · 每周", thirdWeekly.DisplayName);
        Equal("Claude 与 GPT · 5 小时", thirdFiveHour.DisplayName);
        Equal("Gemini 4 Argon · 每周", argon.DisplayName);
        Equal("97.86", ProviderQuotaPresentation.FormatFinePercent(weekly.RemainingPercent!.Value));
        Equal("96.7", ProviderQuotaPresentation.FormatFinePercent(fiveHour.RemainingPercent!.Value));
        Equal("100", ProviderQuotaPresentation.FormatFinePercent(thirdWeekly.RemainingPercent!.Value));
        Equal("100", ProviderQuotaPresentation.FormatFinePercent(thirdFiveHour.RemainingPercent!.Value));
        Equal("91.28", ProviderQuotaPresentation.FormatFinePercent(argon.RemainingPercent!.Value));
        Near(100d - geminiWeekly * 100d, weekly.UsedPercent!.Value);
        Equal(7 * 24 * 60, weekly.WindowDurationMinutes);
        Equal(5 * 60, fiveHour.WindowDurationMinutes);
        Equal(7 * 24 * 60, argon.WindowDurationMinutes);
        True(thirdFiveHour.ResetsAtUtc is null, "missing_reset_invented");
        Equal(ParseUtc("2026-11-06T18:22:57Z"), weekly.ResetsAtUtc);
        Equal(ParseUtc("2026-11-03T16:15:08Z"), argon.ResetsAtUtc);
        True(windows.All(window => window.WindowId is not "g1-weekly" and not "gemini-missing" and not "pro-g1-addon"),
            "credit_or_incomplete_bucket_kept");
        var slot = new ProviderSlotSnapshot(ProviderSlotIds.Antigravity, ProviderIds.Antigravity, "Antigravity",
            true, false, QuotaSlotStatus.Live, "Antigravity 订阅额度", ParseUtc("2026-10-30T18:00:00Z"),
            AntigravityQuota.SourceDescription, windows);
        Equal("91.28", ProviderQuotaPresentation.FormatFinePercent(slot.GlanceRemainingPercent!.Value));
        Equal("Gemini 4 Argon · 每周", slot.GlanceWindowCaption);
        var summed = windows.Sum(window => window.RemainingPercent!.Value);
        True(Math.Abs(summed - slot.GlanceRemainingPercent.Value) > 1, "glance_is_sum");
        log.AppendLine("parser fractions=97.86/96.7/100/100/91.28 glance=91.28 windows=5 credits_ignored=1");
    }

    private static void MissingFieldsDoNotInventPercent(StringBuilder log)
    {
        var empty = AntigravityQuotaParser.Parse("{}", out var emptyError);
        Equal(0, empty.Count);
        Equal("antigravity_cli_failed", emptyError);
        var broken = AntigravityQuotaParser.Parse("{", out var brokenError);
        Equal(0, broken.Count);
        Equal("antigravity_parse_failed", brokenError);
        var creditsOnly = AntigravityQuotaParser.Parse(
            """
            {"status":"SUCCESS","command":{"name":"usage","data":{"groups":[{"name":"G1 Credits","buckets":[{"id":"g1-balance","name":"credits","window":"weekly","remaining_fraction":0.2,"reset_time":"2026-11-01T00:00:00Z"}]},{"name":"Gemini Models","buckets":[{"id":"gemini-weekly","name":"Weekly","window":"weekly"},{"id":"gemini-high","name":"too high","window":"weekly","remaining_fraction":1.2},{"id":"pro-g1-addon","name":"Pro G1 add-on","window":"weekly","remaining_fraction":0.4,"reset_time":"2026-11-01T00:00:00Z"}]}]}}}
            """,
            out var creditError);
        Equal(0, creditsOnly.Count);
        Equal("antigravity_schema_unsupported", creditError);
        var region = AntigravityQuotaParser.Parse(
            """{"status":"ERROR","error":"not available in your country"}""",
            out var regionError);
        Equal(0, region.Count);
        Equal("antigravity_region_unavailable", regionError);
        var expiredLogin = AntigravityQuotaParser.Parse(
            """{"status":"ERROR","error":"login expired"}""",
            out var expiredError);
        Equal(0, expiredLogin.Count);
        Equal("antigravity_login_expired", expiredError);
        var limited = AntigravityQuotaParser.Parse(
            """{"status":"ERROR","error":"429 too many requests"}""",
            out var limitedError);
        Equal(0, limited.Count);
        Equal("antigravity_rate_limited", limitedError);
        log.AppendLine("parser missing_fraction_and_credit_and_status_codes_drop_percent");
    }

    private static void ExpiredMissingResetAndUnknownWindow(StringBuilder log)
    {
        var unknown = AntigravityQuotaParser.Parse(
            """
            {"status":"SUCCESS","command":{"name":"usage","data":{"groups":[{"name":"Gemini 4 Argon","buckets":[{"id":"argon-burst","name":"Burst window","window":"burst","remaining_fraction":0.5}]}]}}}
            """,
            out var unknownError);
        Equal<string?>(null, unknownError);
        Equal(1, unknown.Count);
        Equal("Gemini 4 Argon · Burst window", unknown[0].DisplayName);
        True(unknown[0].WindowDurationMinutes is null, "unknown_window_minutes_invented");
        True(unknown[0].ResetsAtUtc is null, "unknown_window_reset_invented");
        Equal("50", ProviderQuotaPresentation.FormatFinePercent(unknown[0].RemainingPercent!.Value));

        var expiredJson =
            """
            {"status":"SUCCESS","command":{"name":"usage","data":{"groups":[{"name":"Gemini Models","buckets":[{"id":"gemini-weekly","name":"Weekly","window":"weekly","remaining_fraction":0.8,"reset_time":"2020-01-01T00:00:00Z"}]}]}}}
            """;
        var client = new ScriptedClient(_ => Json(expiredJson));
        var clock = new MutableClock(ParseUtc("2026-10-30T18:00:00Z"));
        var adapter = new AntigravityQuotaAdapter(client, clock.Now);
        var settings = AntigravitySlot(true);
        var read = adapter.ReadAsync(settings, CancellationToken.None).GetAwaiter().GetResult();
        Equal(QuotaSlotStatus.Stale, read.Status);
        Equal("窗口已到期，不作为当前额度", read.StatusText);
        var aged = ProviderQuotaCoordinator.AgeIfNeeded(read, clock.Utc);
        True(aged.GlanceRemainingPercent is null, "expired_percent_kept");
        True(aged.StatusText.Contains("窗口已到期", StringComparison.Ordinal), "expired_caption");
        log.AppendLine("parser unknown_window_minutes_null expired_aged_clears_percent");
    }

    private static void AdapterStatuses(StringBuilder log)
    {
        var settings = AntigravitySlot(true);
        var disabled = new AntigravityQuotaAdapter(new ScriptedClient(_ => Json("{}")), () => DateTimeOffset.UtcNow)
            .ReadAsync(AntigravitySlot(false), CancellationToken.None).GetAwaiter().GetResult();
        Equal(QuotaSlotStatus.Disabled, disabled.Status);
        Equal("slot_disabled", disabled.ErrorCode);

        ExpectKind(AntigravityUsageKind.NotFound, "antigravity_cli_missing", QuotaSlotStatus.Unavailable);
        ExpectKind(AntigravityUsageKind.TimedOut, "antigravity_timeout", QuotaSlotStatus.Unavailable);
        ExpectKind(AntigravityUsageKind.SignInRequired, "antigravity_sign_in_required", QuotaSlotStatus.NotConnected);
        ExpectKind(AntigravityUsageKind.LoginExpired, "antigravity_login_expired", QuotaSlotStatus.NotConnected);
        ExpectKind(AntigravityUsageKind.RegionUnavailable, "antigravity_region_unavailable", QuotaSlotStatus.Unavailable);
        ExpectKind(AntigravityUsageKind.RateLimited, "antigravity_rate_limited", QuotaSlotStatus.Unavailable);
        ExpectKind(AntigravityUsageKind.Failed, "antigravity_cli_failed", QuotaSlotStatus.Unavailable);

        var unrecognized = new AntigravityQuotaAdapter(new ScriptedClient(_ => Json("{")), () => DateTimeOffset.UtcNow)
            .ReadAsync(settings, CancellationToken.None).GetAwaiter().GetResult();
        Equal("antigravity_parse_failed", unrecognized.ErrorCode);
        Equal(0, unrecognized.Windows.Count);
        True(unrecognized.GlanceRemainingPercent is null, "parse_failure_invented_percent");
        True(unrecognized.StatusText.Contains("不显示猜测百分比", StringComparison.Ordinal), "parse_caption");
        log.AppendLine("adapter status_codes_cover_missing_timeout_signin_expired_region_limit_parse");
    }

    private static void SettingsStayOffUntilEnabled(StringBuilder log)
    {
        var defaults = ProviderAccessSettings.Default();
        Equal(7, defaults.Slots.Count);
        True(!defaults.Slot(ProviderSlotIds.Antigravity).Enabled, "default_on");
        Equal("Antigravity", defaults.Slot(ProviderSlotIds.Antigravity).Label);
        Equal("Agy", defaults.Slots.Last().ShortAlias());
        var preserved = ProviderSettingKeys.FromStored(new Dictionary<string, string?>
        {
            ["slot_claude_enabled"] = "1",
            ["slot_cursor_label"] = "Cursor Custom",
        });
        True(!preserved.Slot(ProviderSlotIds.Antigravity).Enabled, "old_settings_enabled_antigravity");
        Equal("Antigravity", preserved.Slot(ProviderSlotIds.Antigravity).Label);
        True(preserved.Slot(ProviderSlotIds.Claude).Enabled, "claude_opt_in_lost");
        True(preserved.Slot(ProviderSlotIds.CodexPrimary).Enabled, "primary_default_lost");
        var opted = ProviderSettingKeys.FromStored(new Dictionary<string, string?>
        {
            ["slot_antigravity_enabled"] = "1",
            ["slot_antigravity_label"] = "反重力",
        });
        True(opted.Slot(ProviderSlotIds.Antigravity).Enabled, "explicit_opt_in_ignored");
        Equal("反重力", opted.Slot(ProviderSlotIds.Antigravity).Label);
        var stored = ProviderSettingKeys.ToStored(opted);
        Equal("1", stored[ProviderSettingKeys.Enabled(ProviderSlotIds.Antigravity)]);
        Equal("反重力", stored[ProviderSettingKeys.Label(ProviderSlotIds.Antigravity)]);
        var roundTrip = ProviderSettingKeys.FromStored(stored.ToDictionary(pair => pair.Key, pair => (string?)pair.Value));
        True(roundTrip.Slot(ProviderSlotIds.Antigravity).Enabled, "round_trip_lost_enabled");
        Equal("反重力", roundTrip.Slot(ProviderSlotIds.Antigravity).Label);
        True(!ProviderSettingKeys.ToStored(defaults).ContainsKey("antigravity_quota") , "snapshot_key_added");
        var windowSource = File.ReadAllText(Path.Combine(ProjectRoot(), "src", "CodexUsageHud.App", "MainWindow.xaml.cs"));
        True(windowSource.Contains("ProviderSettingKeys.Enabled(ProviderSlotIds.Antigravity)", StringComparison.Ordinal),
            "restore_omits_antigravity_enabled");
        True(windowSource.Contains("ProviderSettingKeys.Label(ProviderSlotIds.Antigravity)", StringComparison.Ordinal),
            "restore_omits_antigravity_label");
        True(!QuotaSnapshotMapper.TryMapPool(ProviderSlotIds.Antigravity, "gemini-weekly", out _),
            "snapshot_maps_antigravity_pool");
        log.AppendLine("settings default_off old_file_stays_off explicit_label_round_trip snapshot_pool_unmapped");
    }

    private static async Task CoordinatorCacheTimeoutAndRetention(StringBuilder log)
    {
        await DisabledDoesNotCall(log).ConfigureAwait(false);
        await TimeoutCancelsWithoutBlockingOtherSlots(log).ConfigureAwait(false);
        await RateLimitHoldsForFiveMinutes(log).ConfigureAwait(false);
        await SuccessCacheRetainsTransientAndDropsSignIn(log).ConfigureAwait(false);
    }

    private static async Task DisabledDoesNotCall(StringBuilder log)
    {
        var client = new ScriptedClient(_ => Json(LiveFixture()));
        var coordinator = Coordinator(client, new MutableClock(ParseUtc("2026-10-30T18:00:00Z")));
        var board = await coordinator.RefreshAsync(BoardSettings(antigravity: false),
            PrimaryObservation(ParseUtc("2026-10-30T18:00:00Z")), CancellationToken.None).ConfigureAwait(false);
        Equal(0, client.Calls);
        Equal(QuotaSlotStatus.Disabled, board.Find(ProviderSlotIds.Antigravity)!.Status);
        Equal(7, board.Slots.Count);
        log.AppendLine("coordinator disabled_calls=0 slots=7");
    }

    private static async Task TimeoutCancelsWithoutBlockingOtherSlots(StringBuilder log)
    {
        var client = new ScriptedClient(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return new AntigravityUsageRead(AntigravityUsageKind.Failed, null);
        });
        var now = ParseUtc("2026-10-30T18:00:00Z");
        var coordinator = Coordinator(client, new MutableClock(now));
        coordinator.ReadTimeoutOverride = slotId => slotId == ProviderSlotIds.Antigravity
            ? TimeSpan.FromMilliseconds(200)
            : TimeSpan.FromMilliseconds(50);
        var watch = Stopwatch.StartNew();
        var board = await coordinator.RefreshAsync(BoardSettings(antigravity: true),
            PrimaryObservation(now), CancellationToken.None).ConfigureAwait(false);
        watch.Stop();
        var slot = board.Find(ProviderSlotIds.Antigravity)!;
        Equal("slot_timeout", slot.ErrorCode);
        Equal(QuotaSlotStatus.Unavailable, slot.Status);
        True(slot.GlanceRemainingPercent is null, "timeout_invented_percent");
        Equal(1, client.Calls);
        Equal(80d, board.PrimaryCodex!.GlanceRemainingPercent);
        True(watch.Elapsed < TimeSpan.FromSeconds(3), "timeout_blocked:" + watch.Elapsed);
        log.AppendLine("coordinator timeout_ms=" + watch.Elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) +
                       " primary_remaining=80");
    }

    private static async Task RateLimitHoldsForFiveMinutes(StringBuilder log)
    {
        var client = new ScriptedClient(_ => Json("""{"status":"ERROR","error":"429 too many requests"}"""));
        var clock = new MutableClock(ParseUtc("2026-10-30T18:00:00Z"));
        var coordinator = Coordinator(client, clock);
        var settings = BoardSettings(antigravity: true);
        var first = await coordinator.RefreshAsync(settings, PrimaryObservation(clock.Utc), CancellationToken.None, true)
            .ConfigureAwait(false);
        Equal("antigravity_rate_limited", first.Find(ProviderSlotIds.Antigravity)!.ErrorCode);
        True(first.Find(ProviderSlotIds.Antigravity)!.GlanceRemainingPercent is null, "rate_limit_percent");
        clock.Utc = clock.Utc.AddMinutes(4);
        var held = await coordinator.RefreshAsync(settings, PrimaryObservation(clock.Utc), CancellationToken.None, true)
            .ConfigureAwait(false);
        Equal(1, client.Calls);
        Equal("antigravity_rate_limited", held.Find(ProviderSlotIds.Antigravity)!.ErrorCode);
        clock.Utc = clock.Utc.AddMinutes(1);
        client.Next = _ => Json(LiveFixture());
        var due = await coordinator.RefreshAsync(settings, PrimaryObservation(clock.Utc), CancellationToken.None, true)
            .ConfigureAwait(false);
        Equal(2, client.Calls);
        Equal("91.28", ProviderQuotaPresentation.FormatFinePercent(
            due.Find(ProviderSlotIds.Antigravity)!.GlanceRemainingPercent!.Value));
        log.AppendLine("coordinator rate_limit_held_4m then_refreshed");
    }

    private static async Task SuccessCacheRetainsTransientAndDropsSignIn(StringBuilder log)
    {
        var client = new ScriptedClient(_ => Json(LiveFixture()));
        var clock = new MutableClock(ParseUtc("2026-10-30T18:00:00Z"));
        var coordinator = Coordinator(client, clock);
        var settings = BoardSettings(antigravity: true);
        var live = await coordinator.RefreshAsync(settings, PrimaryObservation(clock.Utc), CancellationToken.None)
            .ConfigureAwait(false);
        var slot = live.Find(ProviderSlotIds.Antigravity)!;
        Equal(QuotaSlotStatus.Live, slot.Status);
        Equal(5, slot.Windows.Count);
        Equal("91.28", ProviderQuotaPresentation.FormatFinePercent(slot.GlanceRemainingPercent!.Value));
        True(!slot.SuppliesLocalAnalysis, "antigravity_supplies_analysis");
        True(slot.OpaqueIdentityHash is null, "antigravity_identity_hash");
        clock.Utc = clock.Utc.AddMinutes(1);
        var cached = await coordinator.RefreshAsync(settings, PrimaryObservation(clock.Utc), CancellationToken.None)
            .ConfigureAwait(false);
        Equal(1, client.Calls);
        Equal(QuotaSlotStatus.Live, cached.Find(ProviderSlotIds.Antigravity)!.Status);
        clock.Utc = clock.Utc.AddMinutes(4);
        client.Next = _ => Task.FromResult(new AntigravityUsageRead(AntigravityUsageKind.TimedOut, null));
        var delayed = await coordinator.RefreshAsync(settings, PrimaryObservation(clock.Utc), CancellationToken.None)
            .ConfigureAwait(false);
        var retained = delayed.Find(ProviderSlotIds.Antigravity)!;
        Equal(2, client.Calls);
        True(retained.UpdateDelayed, "transient_not_retained");
        Equal(QuotaSlotStatus.Stale, retained.Status);
        True(retained.StatusText.Contains("更新延迟", StringComparison.Ordinal), "delay_caption:" + retained.StatusText);
        Equal(5, retained.Windows.Count);
        Equal("91.28", ProviderQuotaPresentation.FormatFinePercent(retained.GlanceRemainingPercent!.Value));
        Equal(80d, delayed.PrimaryCodex!.GlanceRemainingPercent);

        var signInClient = new ScriptedClient(_ => Json(LiveFixture()));
        var signInClock = new MutableClock(ParseUtc("2026-10-30T18:00:00Z"));
        var signInCoordinator = Coordinator(signInClient, signInClock);
        await signInCoordinator.RefreshAsync(settings, PrimaryObservation(signInClock.Utc), CancellationToken.None)
            .ConfigureAwait(false);
        signInClock.Utc = signInClock.Utc.AddMinutes(5);
        signInClient.Next = _ => Task.FromResult(new AntigravityUsageRead(AntigravityUsageKind.SignInRequired, null));
        var signedOut = await signInCoordinator.RefreshAsync(settings, PrimaryObservation(signInClock.Utc),
            CancellationToken.None).ConfigureAwait(false);
        var disconnected = signedOut.Find(ProviderSlotIds.Antigravity)!;
        Equal(QuotaSlotStatus.NotConnected, disconnected.Status);
        Equal(0, disconnected.Windows.Count);
        True(!disconnected.UpdateDelayed, "sign_in_retained_old_windows");
        log.AppendLine("coordinator cache_1m_no_call retain_timeout drop_sign_in");
    }

    private static void SyntheticSevenSlotLayout(string output, StringBuilder log)
    {
        EnsureWpf();
        var data = Path.Combine(Path.GetTempPath(), "hud-antigravity-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(data, "codex-home", "sessions"));
        using var engine = new UsageEngine(Path.Combine(data, "codex-home"), Path.Combine(data, "usage.db"),
            Path.Combine(data, "hud.log"));
        using var window = new MainWindow(engine, () => Task.CompletedTask, false);
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Left = -14000;
        window.Top = -14000;
        SetField(window, "_autoHide", false);
        window.Show();
        var viewModel = (MainViewModel)GetField(window, "_viewModel")!;
        var now = ParseUtc("2026-10-30T18:30:00Z");
        viewModel.Apply(SyntheticSnapshot(now));
        window.OverrideWorkAreaForTests(new Rect(-14000, -14000, 1920, 1080));
        Invoke(window, "ApplyExpansionState", false);
        window.UpdateLayout();
        Idle(window);
        Equal(252d, window.Width);
        Equal(7, viewModel.Slots.Count);
        var list = (System.Windows.Controls.ItemsControl)window.FindName("RailSlotList")!;
        list.UpdateLayout();
        Equal(7, list.Items.Count);
        var scroller = (System.Windows.Controls.ScrollViewer)window.FindName("RailSlotScroller")!;
        True(scroller.ScrollableHeight < 8, "rail_scrolls_on_1080:" + scroller.ScrollableHeight.ToString("0.##", CultureInfo.InvariantCulture));
        AssertAliasInside(window, list, 0, "Codex主");
        AssertAliasInside(window, list, 5, "Claude");
        AssertAliasInside(window, list, 6, "Agy");
        var agyCaption = FindVisualChildren<System.Windows.Controls.TextBlock>(RailButton(list, 6))
            .First(block => block.Text != null && block.Text.Contains("Gemini", StringComparison.Ordinal));
        Equal("Gemini 模型 · 5 小时", agyCaption.Text);
        AssertTextInside(window, agyCaption, "agy-caption");
        Equal("64.5%", viewModel.Slots.Last().GlancePercentText);
        Equal("Gemini 模型 · 5 小时", viewModel.Slots.Last().GlanceWindowCaption);
        Equal("82%", viewModel.Slots[0].GlancePercentText);
        Equal("40%", viewModel.Slots[5].GlancePercentText);
        RenderWindow(window, Path.Combine(output, "synthetic-rail-252.png"));

        SetField(window, "_dockSide", Enum.Parse(GetField(window, "_dockSide")!.GetType(), "Top"));
        window.OverrideWorkAreaForTests(new Rect(-14000, -14000, 1920, 1080));
        Invoke(window, "ApplyExpansionState", false);
        window.UpdateLayout();
        Idle(window);
        True(Math.Abs(window.Width - 1100d) < 1.5, "top_width:" + window.Width.ToString("0.##", CultureInfo.InvariantCulture));
        True(!viewModel.TopBarNarrowLayout, "top_1100_marked_narrow");
        var topList = (System.Windows.Controls.ItemsControl)window.FindName("RailTopSlotList")!;
        topList.UpdateLayout();
        Equal(7, topList.Items.Count);
        for (var index = 0; index < 7; index++)
            AssertSlotInside(window, RailButton(topList, index), "top-" + index);
        RenderWindow(window, Path.Combine(output, "synthetic-rail-top-1100.png"));

        window.OverrideWorkAreaForTests(new Rect(-14000, -14000, 664, 900));
        Invoke(window, "ApplyExpansionState", false);
        window.UpdateLayout();
        Idle(window);
        True(Math.Abs(window.Width - 640d) < 1.5, "narrow_width:" + window.Width.ToString("0.##", CultureInfo.InvariantCulture));
        True(viewModel.TopBarNarrowLayout, "narrow_layout_off");
        topList.UpdateLayout();
        var narrowAgy = RailButton(topList, 6);
        AssertSlotInside(window, narrowAgy, "narrow-agy");
        var narrowPercent = FindVisualChildren<System.Windows.Controls.TextBlock>(narrowAgy)
            .First(block => block.Text == "64.5%");
        AssertTextInside(narrowAgy, narrowPercent, "narrow-percent");
        RenderWindow(window, Path.Combine(output, "synthetic-rail-top-640.png"));

        SetField(window, "_dockSide", Enum.Parse(GetField(window, "_dockSide")!.GetType(), "Right"));
        window.OverrideWorkAreaForTests(new Rect(-14000, -14000, 1920, 1080));
        Invoke(window, "ApplyExpansionState", false);
        window.UpdateLayout();
        viewModel.SelectSlot(ProviderSlotIds.Antigravity, true);
        Invoke(window, "PlaceSlotDetailPopup");
        window.UpdateLayout();
        Idle(window);
        Equal("64.5%", viewModel.SlotDetailPercent);
        Equal(5, viewModel.SlotDetailWindows.Count);
        True(viewModel.SlotDetailShareNote.Contains("不能相加", StringComparison.Ordinal), "share_note");
        True(viewModel.SlotDetailShareNote.Contains("G1", StringComparison.Ordinal), "g1_note");
        True(viewModel.SlotDetailBody.Contains("重置时间不可用", StringComparison.Ordinal), "missing_reset_hidden");
        True(!viewModel.SlotDetailBody.Contains("435", StringComparison.Ordinal), "detail_shows_sum");
        True(viewModel.SlotDetailWindows.Any(row => row.RemainingText == "100%"), "integer_percent");
        True(viewModel.SlotDetailWindows.Any(row => row.RemainingText == "91.25%"), "two_decimal_percent");
        var detail = (FrameworkElement)window.FindName("SlotDetailHost")!;
        RenderElement(detail, Path.Combine(output, "synthetic-slot-detail.png"));

        viewModel.IsExpanded = true;
        Invoke(window, "ApplyExpansionState", false);
        var settingsPanel = (FrameworkElement)window.FindName("SettingsPanel")!;
        settingsPanel.Visibility = Visibility.Visible;
        var checkBox = (System.Windows.Controls.CheckBox)window.FindName("SlotAntigravityEnabled")!;
        var label = (System.Windows.Controls.TextBox)window.FindName("SlotAntigravityLabel")!;
        True(checkBox.IsChecked != true, "settings_checkbox_default_on");
        label.Text = "反重力";
        checkBox.IsChecked = true;
        SetField(window, "_settingsReady", true);
        var captured = (ProviderAccessSettings)Invoke(window, "CaptureProviderSettings")!;
        True(captured.Slot(ProviderSlotIds.Antigravity).Enabled, "checkbox_capture");
        Equal("反重力", captured.Slot(ProviderSlotIds.Antigravity).Label);
        checkBox.IsChecked = false;
        label.Text = "Antigravity";
        window.UpdateLayout();
        Idle(window);
        checkBox.BringIntoView();
        window.UpdateLayout();
        Idle(window);
        var form = VisualParent<System.Windows.Controls.StackPanel>(checkBox)
            ?? throw new InvalidOperationException("settings_form_missing");
        form.Background = (System.Windows.Media.Brush)window.FindResource("HudPanel");
        RenderElement(form, Path.Combine(output, "synthetic-settings.png"));
        var settingsScroller = VisualParent<System.Windows.Controls.ScrollViewer>(checkBox);
        settingsScroller?.ScrollToEnd();
        window.UpdateLayout();
        Idle(window);
        RenderWindow(window, Path.Combine(output, "synthetic-settings-window.png"));
        var captionVisible = VisualText(settingsPanel).Any(text => text.Contains("启用 Antigravity 订阅额度", StringComparison.Ordinal));
        True(captionVisible, "settings_caption_missing");
        window.Hide();
        log.AppendLine("ui rail_252_no_scroll top_1100 narrow_640 detail=64.5 settings_checkbox_off data=" + data);
        log.AppendLine("SYNTHETIC screenshots are not live quota.");
    }

    private static string FixtureJson(double geminiWeekly, double geminiFiveHour, double argonWeekly)
    {
        var weekly = geminiWeekly.ToString("G17", CultureInfo.InvariantCulture);
        var fiveHour = geminiFiveHour.ToString("G17", CultureInfo.InvariantCulture);
        var argon = argonWeekly.ToString("G17", CultureInfo.InvariantCulture);
        return """
            {"status":"SUCCESS","response":"rounded text is not the quota","command":{"name":"usage","data":{"groups":[
            {"name":"Gemini Models","buckets":[
            {"id":"gemini-weekly","name":"Weekly Limit Remaining","window":"weekly","remaining_fraction":
            """ + weekly + """
            ,"reset_time":"2026-11-06T18:22:57Z"},
            {"id":"gemini-5h","name":"Five Hour Limit Remaining","window":"5h","remaining_fraction":
            """ + fiveHour + """
            ,"reset_time":"2026-10-30T23:22:57Z"},
            {"id":"gemini-missing","name":"no fraction","window":"weekly"},
            {"id":"gemini-weekly","name":"duplicate","window":"weekly","remaining_fraction":0.1,"reset_time":"2026-11-06T18:22:57Z"}]},
            {"name":"Claude and GPT models","buckets":[
            {"id":"3p-weekly","name":"Weekly","window":"weekly","remaining_fraction":1,"reset_time":"2026-11-06T18:28:19Z"},
            {"id":"3p-5h","name":"Five Hour","window":"5h","remaining_fraction":1}]},
            {"name":"Gemini 4 Argon","buckets":[
            {"id":"gemini-argon-wtu-weekly","name":"Weekly","window":"weekly","remaining_fraction":
            """ + argon + """
            ,"reset_time":"2026-11-03T16:15:08Z"}]},
            {"name":"G1 Credits","buckets":[
            {"id":"g1-weekly","name":"credits","window":"weekly","remaining_fraction":0.1,"reset_time":"2026-11-06T18:22:57Z"}]},
            {"name":"Gemini Models","buckets":[
            {"id":"pro-g1-addon","name":"Pro G1 add-on","window":"weekly","remaining_fraction":0.4,"reset_time":"2026-11-06T18:22:57Z"}]}]}}}
            """;
    }

    private static string LiveFixture() => FixtureJson(0.978570818901062, 0.966951310634613, 0.9128440022468567);

    private static HudSnapshot SyntheticSnapshot(DateTimeOffset now)
    {
        var quota = new QuotaObservation(
            new QuotaBucket("primary", "5 小时", 18, 300, now.AddHours(4)),
            Array.Empty<QuotaBucket>(), QuotaSource.OfficialAppServer, now, false);
        var slots = new[]
        {
            SyntheticSlot(ProviderSlotIds.CodexPrimary, ProviderIds.Codex, "Codex 当前", "primary", "5 小时", 18, 300, now, now.AddHours(4), true),
            SyntheticSlot(ProviderSlotIds.CodexSecondary, ProviderIds.Codex, "Codex 第二账户", "primary", "5 小时", 29, 300, now, now.AddHours(3), false),
            SyntheticSlot(ProviderSlotIds.Cursor, ProviderIds.Cursor, "Cursor", "shared_weekly", "共享周额度", 37, 10080, now, now.AddDays(6), false),
            SyntheticSlot(ProviderSlotIds.Grok, ProviderIds.Grok, "Grok", "weekly", "每周", 45, 10080, now, now.AddDays(5), false),
            SyntheticSlot(ProviderSlotIds.GrokBot, ProviderIds.GrokBot, "Grok Bot", "shared_weekly", "共享周额度", 52, 10080, now, now.AddDays(6), false),
            SyntheticSlot(ProviderSlotIds.Claude, ProviderIds.Claude, "Claude 订阅", "seven_day", "每周", 60, 10080, now, now.AddDays(4), false),
            new ProviderSlotSnapshot(ProviderSlotIds.Antigravity, ProviderIds.Antigravity, "Antigravity", true, false,
                QuotaSlotStatus.Live, "Antigravity 订阅额度", now, AntigravityQuota.SourceDescription,
                new[]
                {
                    new QuotaWindowObservation("gemini-weekly", "Gemini 模型 · 每周", 20, now.AddDays(20), 10080, true),
                    new QuotaWindowObservation("gemini-5h", "Gemini 模型 · 5 小时", 35.5, now.AddHours(5), 300, true),
                    new QuotaWindowObservation("3p-weekly", "Claude 与 GPT · 每周", 0, now.AddDays(20), 10080, true),
                    new QuotaWindowObservation("3p-5h", "Claude 与 GPT · 5 小时", 0, null, 300, true),
                    new QuotaWindowObservation("gemini-argon-wtu-weekly", "Gemini 4 Argon · 每周", 8.75, now.AddDays(8), 10080, true),
                }),
        };
        return new HudSnapshot(quota, Array.Empty<SessionAggregate>(), null, now, false, "合成额度",
            Array.Empty<string>(), Providers: new ProviderQuotaBoard(slots, now));
    }

    private static ProviderSlotSnapshot SyntheticSlot(string slotId, string providerId, string label, string windowId,
        string windowName, double used, int minutes, DateTimeOffset now, DateTimeOffset reset, bool analysis) =>
        new(slotId, providerId, label, true, analysis, QuotaSlotStatus.Live, "合成额度", now, "合成",
            new[] { new QuotaWindowObservation(windowId, windowName, used, reset, minutes, true) });

    private static ProviderQuotaCoordinator Coordinator(ScriptedClient client, MutableClock clock)
    {
        var tokens = new InjectedTokenSource(null);
        var coordinator = new ProviderQuotaCoordinator(
            http: new ScriptedHttp(),
            cursorTokens: tokens,
            grokTokens: tokens,
            piTokens: new InjectedPiCodexTokenSource(null),
            claudeTokens: new InjectedClaudeTokenSource(null),
            clock: clock.Now,
            antigravity: new AntigravityQuotaAdapter(client, clock.Now));
        coordinator.PublishBudget = TimeSpan.FromSeconds(8);
        coordinator.GrokLoginStampOverride = () => "0";
        coordinator.ClaudeLoginStampOverride = () => "0";
        coordinator.CodexCliAuthStampOverride = _ => "0";
        return coordinator;
    }

    private static ProviderAccessSettings BoardSettings(bool antigravity)
    {
        var slots = ProviderAccessSettings.Default().Slots.Select(slot => slot.SlotId switch
        {
            ProviderSlotIds.CodexPrimary => slot with { Enabled = true },
            ProviderSlotIds.Antigravity => slot with { Enabled = antigravity },
            _ => slot with { Enabled = false },
        }).ToArray();
        return new ProviderAccessSettings(slots, CompactLayoutModes.Rail, true);
    }

    private static ProviderSlotSettings AntigravitySlot(bool enabled) =>
        new(ProviderSlotIds.Antigravity, "Antigravity", enabled);

    private static QuotaObservation PrimaryObservation(DateTimeOffset now) =>
        new(new QuotaBucket("primary", "Codex", 20, 300, now.AddHours(3)), Array.Empty<QuotaBucket>(),
            QuotaSource.OfficialAppServer, now, false);

    private static Task<AntigravityUsageRead> Json(string json) =>
        Task.FromResult(new AntigravityUsageRead(AntigravityUsageKind.Json, json));

    private static void ExpectKind(AntigravityUsageKind kind, string code, QuotaSlotStatus status)
    {
        var read = new AntigravityQuotaAdapter(new ScriptedClient(_ => Task.FromResult(new AntigravityUsageRead(kind, null))),
                () => DateTimeOffset.UtcNow)
            .ReadAsync(AntigravitySlot(true), CancellationToken.None).GetAwaiter().GetResult();
        Equal(code, read.ErrorCode);
        Equal(status, read.Status);
        True(read.GlanceRemainingPercent is null, code + "_percent");
    }

    private static QuotaWindowObservation One(IReadOnlyList<QuotaWindowObservation> windows, string id)
    {
        var matches = windows.Where(window => window.WindowId == id).ToArray();
        Equal(1, matches.Length);
        return matches[0];
    }

    private static DateTimeOffset ParseUtc(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string ProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CodexUsageHud.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("project_root_missing");
    }

    private static void EnsureWpf()
    {
        if (System.Windows.Application.Current is null)
        {
            var application = new CodexUsageHud.App.App();
            application.InitializeComponent();
        }

        if (System.Windows.Application.Current is not CodexUsageHud.App.App app)
            throw new InvalidOperationException("unexpected_wpf_application");
        var startup = typeof(CodexUsageHud.App.App).GetMethod("OnStartup",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.DeclaredOnly);
        if (startup is not null)
        {
            app.Startup -= (System.Windows.StartupEventHandler)Delegate.CreateDelegate(
                typeof(System.Windows.StartupEventHandler), app, startup);
        }

        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    private static void RenderWindow(MainWindow window, string outputPath)
    {
        var visual = (FrameworkElement)window.Content;
        visual.Measure(new Size(window.Width, window.Height));
        visual.Arrange(new Rect(0, 0, window.Width, window.Height));
        visual.UpdateLayout();
        var pixelWidth = Math.Max(1, (int)Math.Ceiling(window.Width));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(window.Height));
        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        SavePng(bitmap, outputPath);
        File.WriteAllText(outputPath + ".txt",
            "SYNTHETIC fixture. Not live Antigravity quota.\r\n" +
            "logical=" + window.Width.ToString("0.###", CultureInfo.InvariantCulture) + "x" +
            window.Height.ToString("0.###", CultureInfo.InvariantCulture) + "\r\n");
    }

    private static void RenderElement(FrameworkElement element, string outputPath)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        element.Arrange(new Rect(element.DesiredSize));
        element.UpdateLayout();
        var pixelWidth = Math.Max(1, (int)Math.Ceiling(element.ActualWidth));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(element.ActualHeight));
        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        SavePng(bitmap, outputPath);
        File.WriteAllText(outputPath + ".txt",
            "SYNTHETIC fixture. Not live Antigravity quota.\r\n" +
            "logical=" + element.ActualWidth.ToString("0.###", CultureInfo.InvariantCulture) + "x" +
            element.ActualHeight.ToString("0.###", CultureInfo.InvariantCulture) + "\r\n");
    }

    private static void SavePng(RenderTargetBitmap bitmap, string outputPath)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }

    private static System.Windows.Controls.Button RailButton(System.Windows.Controls.ItemsControl list, int index)
    {
        list.UpdateLayout();
        var container = list.ItemContainerGenerator.ContainerFromIndex(index)
            ?? throw new InvalidOperationException("rail_container_missing:" + index);
        return FindVisualChild<System.Windows.Controls.Button>(container)
            ?? throw new InvalidOperationException("rail_button_missing:" + index);
    }

    private static void AssertAliasInside(MainWindow window, System.Windows.Controls.ItemsControl list, int index, string alias)
    {
        var button = RailButton(list, index);
        AssertSlotInside(window, button, alias);
        var text = FindVisualChildren<System.Windows.Controls.TextBlock>(button)
            .FirstOrDefault(block => block.Text == alias);
        True(text is not null && text.ActualHeight > 8, alias + "_alias_missing");
    }

    private static void AssertTextInside(FrameworkElement bounds, FrameworkElement text, string name)
    {
        var topLeft = text.TranslatePoint(new Point(0, 0), bounds);
        var right = topLeft.X + text.ActualWidth;
        var bottom = topLeft.Y + text.ActualHeight;
        True(topLeft.X >= -1, name + "_left");
        True(topLeft.Y >= -1, name + "_top");
        True(right <= bounds.ActualWidth + 1.5, name + "_right:" + right.ToString("0.##", CultureInfo.InvariantCulture));
        True(bottom <= bounds.ActualHeight + 1.5, name + "_bottom:" + bottom.ToString("0.##", CultureInfo.InvariantCulture));
    }

    private static void AssertSlotInside(MainWindow window, FrameworkElement element, string name)
    {
        var topLeft = element.TranslatePoint(new Point(0, 0), window);
        var bottom = topLeft.Y + element.ActualHeight;
        var right = topLeft.X + element.ActualWidth;
        True(topLeft.Y >= -1, name + "_above:" + topLeft.Y.ToString("0.##", CultureInfo.InvariantCulture));
        True(bottom <= window.Height + 1.5, name + "_clipped_bottom:" + bottom.ToString("0.##", CultureInfo.InvariantCulture));
        True(topLeft.X >= -1, name + "_left:" + topLeft.X.ToString("0.##", CultureInfo.InvariantCulture));
        True(right <= window.Width + 1.5, name + "_clipped_right:" + right.ToString("0.##", CultureInfo.InvariantCulture));
    }

    private static IEnumerable<string> VisualText(DependencyObject root)
    {
        if (root is System.Windows.Controls.TextBlock block && !string.IsNullOrWhiteSpace(block.Text))
            yield return block.Text;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            foreach (var text in VisualText(VisualTreeHelper.GetChild(root, index)))
                yield return text;
        }
    }

    private static T? VisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        var current = VisualTreeHelper.GetParent(child);
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            var nested = FindVisualChild<T>(child);
            if (nested is not null) return nested;
        }

        return null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var nested in FindVisualChildren<T>(child))
                yield return nested;
        }
    }

    private static object? Invoke(MainWindow window, string name, params object[] arguments)
    {
        var method = typeof(MainWindow).GetMethod(name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("window_method_missing:" + name);
        return method.Invoke(window, arguments);
    }

    private static object? GetField(MainWindow window, string name) =>
        typeof(MainWindow).GetField(name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(window);

    private static void SetField(MainWindow window, string name, object? value) =>
        typeof(MainWindow).GetField(name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(window, value);

    private static void Idle(MainWindow window) =>
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static string ShortAlias(this ProviderSlotSettings slot) => slot.SlotId switch
    {
        ProviderSlotIds.Antigravity => "Agy",
        _ => slot.Label,
    };

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException("expected [" + expected + "] actual [" + actual + "]");
    }

    private static void Near(double expected, double actual)
    {
        if (Math.Abs(expected - actual) > 0.000001)
            throw new InvalidOperationException("expected " + expected.ToString("G17", CultureInfo.InvariantCulture) +
                                                " actual " + actual.ToString("G17", CultureInfo.InvariantCulture));
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class MutableClock
    {
        public MutableClock(DateTimeOffset utc) => Utc = utc;
        public DateTimeOffset Utc { get; set; }
        public DateTimeOffset Now() => Utc;
    }

    private sealed class ScriptedClient : IAntigravityUsageClient
    {
        public int Calls;
        public Func<CancellationToken, Task<AntigravityUsageRead>> Next;
        public ScriptedClient(Func<CancellationToken, Task<AntigravityUsageRead>> next) => Next = next;
        public Task<AntigravityUsageRead> ReadUsageAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Next(cancellationToken);
        }
    }

    private sealed class ScriptedHttp : IAllowlistedHttpSender
    {
        public Task<AllowlistedHttpResponse> SendAsync(AllowlistedHttpRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new AllowlistedHttpResponse(404, "{}", request.Url));
    }
}
