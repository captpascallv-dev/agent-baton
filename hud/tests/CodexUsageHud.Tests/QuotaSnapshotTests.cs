using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexUsageHud.App;
using CodexUsageHud.Core;

namespace CodexUsageHud.Tests;

internal static class QuotaSnapshotTests
{
    private const string Secret = "SNAPSHOT_SECRET_9C3E_label@example.test";

    public static int Run(string[] args)
    {
        if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            Console.Error.WriteLine("usage: --quota-snapshot <output-directory>");
            return 2;
        }

        var output = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(output);
        var log = new StringBuilder();
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "hud-quota-snapshot-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                SchemaPrivacyAndStates(root, log);
                TogglePendingAtomicAndFailure(root, log);
                EngineSettingRoundTrip(root, log);
                AssemblyVersion(log);
                ParserCodexWindows(root, output, log);
                BoundedOwnedTemp(root, log);
            }
            finally
            {
                try { Directory.Delete(root, true); }
                catch (Exception ex) { log.AppendLine("cleanup=" + Scrub(ex.Message)); }
            }

            log.AppendLine("result=pass");
            File.WriteAllText(Path.Combine(output, "quota-snapshot-checks.txt"), log.ToString(), Encoding.UTF8);
            Console.WriteLine("quota-snapshot pass");
            return 0;
        }
        catch (Exception ex)
        {
            log.AppendLine("result=fail");
            log.AppendLine(Scrub(ex.ToString()));
            File.WriteAllText(Path.Combine(output, "quota-snapshot-checks.txt"), log.ToString(), Encoding.UTF8);
            Console.Error.WriteLine("quota-snapshot fail");
            return 1;
        }
    }

    private static void SchemaPrivacyAndStates(string root, StringBuilder log)
    {
        var observed = DateTimeOffset.Parse("2026-10-04T11:58:00.0000000Z", CultureInfo.InvariantCulture);
        var retained = DateTimeOffset.Parse("2026-10-04T09:00:00.0000000Z", CultureInfo.InvariantCulture);
        var resetPrimary = DateTimeOffset.Parse("2026-10-04T16:58:00.0000000Z", CultureInfo.InvariantCulture);
        var resetWeekly = DateTimeOffset.Parse("2026-10-11T11:58:00.0000000Z", CultureInfo.InvariantCulture);
        var written = DateTimeOffset.Parse("2026-10-04T12:00:00.0000000Z", CultureInfo.InvariantCulture);
        var board = new ProviderQuotaBoard(new[]
        {
            Slot(ProviderSlotIds.CodexPrimary, ProviderIds.Codex, QuotaSlotStatus.Live, observed, new[]
            {
                Window("primary", 20, 300, resetPrimary),
                Window("weekly", 58, 10080, resetWeekly),
                Window("free-text-plan-" + Secret, 1, 60, resetPrimary),
            }),
            Slot(ProviderSlotIds.CodexSecondary, ProviderIds.Codex, QuotaSlotStatus.Stale, retained, new[]
            {
                Window("secondary_window", 10, 300, resetPrimary),
            }),
            Slot(ProviderSlotIds.Cursor, ProviderIds.Cursor, QuotaSlotStatus.Live, observed, new[]
            {
                Window("autoPercentUsed", 12, 43200, resetWeekly),
                Window("apiPercentUsed", null, null, null),
            }),
            Slot(ProviderSlotIds.Grok, ProviderIds.Grok, QuotaSlotStatus.Unavailable, observed, new[]
            {
                Window("weekly", 5, 10080, resetWeekly),
            }),
            Slot(ProviderSlotIds.GrokBot, ProviderIds.GrokBot, QuotaSlotStatus.Disabled, observed, new[]
            {
                Window("sand", 8, 10080, resetWeekly),
            }, enabled: false),
            Slot(ProviderSlotIds.Claude, ProviderIds.Claude, QuotaSlotStatus.Live, retained, new[]
            {
                Window("five_hour", 25, 300, resetPrimary),
                Window("seven_day", 40, 10080, resetWeekly),
                Window("seven_day_sonnet", 15, 10080, resetWeekly),
            }, delayed: true),
        }, written);

        var directory = Path.Combine(root, "schema");
        var writer = new QuotaSnapshotWriter(directory, "1.1.3", clock: () => written);
        writer.SetEnabled(true);
        writer.Publish(board);
        writer.WhenIdleAsync().GetAwaiter().GetResult();
        var json = File.ReadAllText(writer.FinalPath, Encoding.UTF8);
        using var document = JsonDocument.Parse(json);
        var rootElement = document.RootElement;
        Expect(rootElement.GetProperty("schema").GetInt32() == 1, "schema");
        Expect(rootElement.GetProperty("written_at_utc").GetString() == QuotaSnapshotJson.FormatUtc(written), "written");
        Expect(rootElement.GetProperty("hud_version").GetString() == "1.1.3", "version");
        var slots = rootElement.GetProperty("slots").EnumerateArray().ToArray();
        Expect(slots.Length == 6, "six");
        string[] ids = { "codex_primary", "codex_secondary", "cursor", "grok", "grok_bot", "claude" };
        string[] labels = { "Codex主", "Codex备", "Cursor", "Grok", "Bot", "Claude" };
        string[] states = { "ok", "stale", "ok", "unavailable", "disabled", "stale" };
        for (var index = 0; index < 6; index++)
        {
            Expect(slots[index].GetProperty("slot").GetString() == ids[index], "slot-id");
            Expect(slots[index].GetProperty("label").GetString() == labels[index], "label");
            Expect(slots[index].GetProperty("state").GetString() == states[index], "state-" + ids[index]);
            AssertAllowlisted(slots[index], true);
        }

        var primaryWindows = slots[0].GetProperty("windows").EnumerateArray().ToArray();
        Expect(primaryWindows.Length == 2, "codex-windows");
        Expect(primaryWindows[0].GetProperty("pool").GetString() == "codex_primary_window", "pool-primary");
        Expect(primaryWindows[0].GetProperty("window_minutes").GetInt32() == 300, "minutes");
        Expect(primaryWindows[0].GetProperty("remaining_percent").GetDouble() == 80d, "remaining");
        Expect(primaryWindows[0].GetProperty("observed_at_utc").GetString() == QuotaSnapshotJson.FormatUtc(observed), "observed");
        Expect(primaryWindows[1].GetProperty("pool").GetString() == "codex_weekly", "pool-weekly");
        Expect(primaryWindows[1].GetProperty("remaining_percent").GetDouble() == 42d, "weekly-remaining");
        Expect(slots[1].GetProperty("windows")[0].GetProperty("pool").GetString() == "codex_secondary_window", "secondary-pool");
        Expect(slots[1].GetProperty("windows")[0].GetProperty("observed_at_utc").GetString() ==
               QuotaSnapshotJson.FormatUtc(retained), "retained-observed");
        var cursorWindows = slots[2].GetProperty("windows").EnumerateArray().ToArray();
        Expect(cursorWindows.Length == 2, "cursor-windows");
        Expect(cursorWindows[0].GetProperty("pool").GetString() == "cursor_auto", "cursor-auto");
        Expect(cursorWindows[1].GetProperty("pool").GetString() == "cursor_api", "cursor-api");
        Expect(cursorWindows[1].GetProperty("window_minutes").ValueKind == JsonValueKind.Null, "null-minutes");
        Expect(cursorWindows[1].GetProperty("remaining_percent").ValueKind == JsonValueKind.Null, "null-percent");
        Expect(cursorWindows[1].GetProperty("resets_at_utc").ValueKind == JsonValueKind.Null, "null-reset");
        Expect(slots[3].GetProperty("windows").GetArrayLength() == 0, "failed-slot-empty");
        Expect(slots[4].GetProperty("windows").GetArrayLength() == 0, "disabled-empty");
        var claudeWindows = slots[5].GetProperty("windows").EnumerateArray().Select(item => item.GetProperty("pool").GetString()).ToArray();
        Expect(claudeWindows.SequenceEqual(new[] { "claude_five_hour", "claude_seven_day", "claude_seven_day_sonnet" }), "claude-pools");
        Expect(!json.Contains(Secret, StringComparison.Ordinal), "secret");
        Expect(!json.Contains("AccountKey", StringComparison.Ordinal), "account-key");
        Expect(!json.Contains("OpaqueIdentity", StringComparison.Ordinal), "hash");
        Expect(!json.Contains(directory, StringComparison.OrdinalIgnoreCase), "directory");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
            Expect(!json.Contains(profile, StringComparison.OrdinalIgnoreCase), "profile");
        log.AppendLine("schema_privacy_states=pass");
    }

    private static void TogglePendingAtomicAndFailure(string root, StringBuilder log)
    {
        var directory = Path.Combine(root, "writer");
        var off = new QuotaSnapshotWriter(directory, "1.1.3");
        off.Publish(OneBoard(10));
        off.WhenIdleAsync().GetAwaiter().GetResult();
        Expect(!File.Exists(off.FinalPath), "default-off");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commits = 0;
        var coalesced = new QuotaSnapshotWriter(directory, "1.1.3", () =>
        {
            var count = Interlocked.Increment(ref commits);
            if (count == 1)
            {
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            }
        });
        coalesced.SetEnabled(true);
        coalesced.Publish(OneBoard(1));
        Expect(entered.Task.Wait(TimeSpan.FromSeconds(5)), "commit-entered");
        for (var index = 2; index <= 20; index++)
            coalesced.Publish(OneBoard(index));
        release.TrySetResult();
        coalesced.WhenIdleAsync().GetAwaiter().GetResult();
        Expect(commits is >= 1 and <= 3, "coalesced-commits");
        using (var saved = JsonDocument.Parse(File.ReadAllText(coalesced.FinalPath)))
        {
            var remaining = saved.RootElement.GetProperty("slots")[0].GetProperty("windows")[0]
                .GetProperty("remaining_percent").GetDouble();
            Expect(remaining == 80d, "last-board");
        }

        var blockedEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockedRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockedDir = Path.Combine(root, "blocked");
        var prior = File.ReadAllBytes(coalesced.FinalPath);
        var blocked = new QuotaSnapshotWriter(blockedDir, "1.1.3", () =>
        {
            blockedEntered.TrySetResult();
            blockedRelease.Task.GetAwaiter().GetResult();
        });
        blocked.SetEnabled(true);
        blocked.Publish(OneBoard(3));
        Expect(blockedEntered.Task.Wait(TimeSpan.FromSeconds(5)), "blocked-entered");
        blocked.SetEnabled(false);
        blocked.Publish(OneBoard(4));
        blockedRelease.TrySetResult();
        blocked.WhenIdleAsync().GetAwaiter().GetResult();
        Expect(!File.Exists(blocked.FinalPath), "off-drops-pending");
        Expect(File.ReadAllBytes(coalesced.FinalPath).AsSpan().SequenceEqual(prior), "old-file-kept");

        var readersStopped = 0;
        var readerAttempts = 0;
        var goodReads = 0;
        var badReads = 0;
        var readDirectory = Path.Combine(root, "atomic");
        var atomic = new QuotaSnapshotWriter(readDirectory, "1.1.3");
        atomic.SetEnabled(true);
        var reader = Task.Run(() =>
        {
            var path = atomic.FinalPath;
            while (Volatile.Read(ref readersStopped) == 0)
            {
                Interlocked.Increment(ref readerAttempts);
                try
                {
                    byte[] bytes;
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        using var memory = new MemoryStream();
                        stream.CopyTo(memory);
                        bytes = memory.ToArray();
                    }

                    if (bytes.Length == 0)
                    {
                        Interlocked.Increment(ref badReads);
                        continue;
                    }

                    using var parsed = JsonDocument.Parse(bytes);
                    if (parsed.RootElement.GetProperty("schema").GetInt32() == 1 &&
                        parsed.RootElement.GetProperty("slots").GetArrayLength() == 6)
                        Interlocked.Increment(ref goodReads);
                    else
                        Interlocked.Increment(ref badReads);
                }
                catch (FileNotFoundException)
                {
                }
                catch (IOException)
                {
                }
                catch (JsonException)
                {
                    Interlocked.Increment(ref badReads);
                }
            }
        });
        Expect(SpinWait.SpinUntil(() => Volatile.Read(ref readerAttempts) > 0, TimeSpan.FromSeconds(2)), "reader-started");
        for (var index = 0; index < 25; index++)
            atomic.Publish(OneBoard(index));
        atomic.WhenIdleAsync().GetAwaiter().GetResult();
        Expect(SpinWait.SpinUntil(() => Volatile.Read(ref goodReads) > 0, TimeSpan.FromSeconds(2)), "atomic-saw-file");
        Volatile.Write(ref readersStopped, 1);
        reader.Wait(TimeSpan.FromSeconds(5));
        Expect(badReads == 0, "atomic-reader");
        Expect(goodReads > 0, "atomic-saw-file");

        var blocker = Path.Combine(root, "not-a-directory");
        File.WriteAllText(blocker, "blocked");
        var failing = new QuotaSnapshotWriter(blocker, "1.1.3");
        failing.SetEnabled(true);
        failing.Publish(OneBoard(9));
        failing.WhenIdleAsync().GetAwaiter().GetResult();
        Expect(!File.Exists(Path.Combine(blocker, QuotaSnapshotWriter.FileName)), "io-contained");
        log.AppendLine("toggle_atomic_failure=pass commits=" + commits.ToString(CultureInfo.InvariantCulture) +
                       " good_reads=" + goodReads.ToString(CultureInfo.InvariantCulture));
    }

    private static void EngineSettingRoundTrip(string root, StringBuilder log)
    {
        var directory = Path.Combine(root, "engine");
        Directory.CreateDirectory(Path.Combine(directory, "sessions"));
        var database = Path.Combine(directory, "usage.db");
        using (var engine = new UsageEngine(directory, database, Path.Combine(directory, "hud.log"),
                   productVersion: "1.1.3"))
        {
            var missing = engine.LoadSettingsAsync(QuotaSnapshotWriter.SettingKey).GetAwaiter().GetResult();
            Expect(!QuotaSnapshotWriter.IsEnabledValue(missing.GetValueOrDefault(QuotaSnapshotWriter.SettingKey)), "missing-off");
            engine.ExportCurrentQuotaSnapshot();
            engine.FlushQuotaSnapshotAsync().GetAwaiter().GetResult();
            var snapshot = Path.Combine(directory, QuotaSnapshotWriter.FileName);
            Expect(!File.Exists(snapshot), "engine-default-no-file");
            engine.SaveSettingsAsync(new Dictionary<string, string>
            {
                [QuotaSnapshotWriter.SettingKey] = "1",
            }).GetAwaiter().GetResult();
            engine.SetQuotaSnapshotExportEnabled(true);
            engine.ExportCurrentQuotaSnapshot();
            engine.FlushQuotaSnapshotAsync().GetAwaiter().GetResult();
            Expect(File.Exists(snapshot), "engine-writes-beside-db");
            var json = File.ReadAllText(snapshot, Encoding.UTF8);
            using var document = JsonDocument.Parse(json);
            Expect(document.RootElement.GetProperty("hud_version").GetString() == "1.1.3", "engine-version");
            Expect(document.RootElement.GetProperty("slots").GetArrayLength() == 6, "engine-six");
            Expect(!json.Contains(directory, StringComparison.OrdinalIgnoreCase), "engine-path");
            var before = File.ReadAllBytes(snapshot);
            engine.SetQuotaSnapshotExportEnabled(false);
            engine.ExportCurrentQuotaSnapshot();
            engine.FlushQuotaSnapshotAsync().GetAwaiter().GetResult();
            Expect(File.ReadAllBytes(snapshot).AsSpan().SequenceEqual(before), "engine-off-keeps");
            engine.SaveSettingsAsync(new Dictionary<string, string>
            {
                [QuotaSnapshotWriter.SettingKey] = "0",
            }).GetAwaiter().GetResult();
        }

        using (var restarted = new UsageEngine(directory, database, Path.Combine(directory, "hud.log"),
                   productVersion: "1.1.3"))
        {
            var savedOff = restarted.LoadSettingsAsync(QuotaSnapshotWriter.SettingKey).GetAwaiter().GetResult();
            Expect(savedOff[QuotaSnapshotWriter.SettingKey] == "0", "persisted-off");
            restarted.SaveSettingsAsync(new Dictionary<string, string>
            {
                [QuotaSnapshotWriter.SettingKey] = "1",
            }).GetAwaiter().GetResult();
        }

        using (var enabledAgain = new UsageEngine(directory, database, Path.Combine(directory, "hud.log"),
                   productVersion: "1.1.3"))
        {
            var savedOn = enabledAgain.LoadSettingsAsync(QuotaSnapshotWriter.SettingKey).GetAwaiter().GetResult();
            Expect(savedOn[QuotaSnapshotWriter.SettingKey] == "1", "persisted-on");
        }
        Expect(!QuotaSnapshotWriter.IsEnabledValue("true"), "only-one-enables");
        log.AppendLine("engine_setting=pass");
    }

    private static void AssemblyVersion(StringBuilder log)
    {
        var version = typeof(MainWindow).Assembly.GetName().Version;
        Expect(version is not null && version.Major == 1 && version.Minor == 1 && version.Build == 3, "assembly");
        Expect(HudReleaseVersion.FromAssembly(typeof(MainWindow).Assembly) == "1.1.3", "product-text");
        log.AppendLine("assembly_version=1.1.3");
    }

    private static void RenderSettings(string root, string output, StringBuilder log)
    {
        if (Application.Current is null)
        {
            var application = new CodexUsageHud.App.App();
            application.InitializeComponent();
        }

        var directory = Path.Combine(root, "ui");
        Directory.CreateDirectory(Path.Combine(directory, "sessions"));
        using var engine = new UsageEngine(directory, Path.Combine(directory, "usage.db"),
            Path.Combine(directory, "hud.log"), productVersion: "1.1.3");
        using var window = new MainWindow(engine, () => Task.CompletedTask, false)
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            Opacity = 0,
            Left = -12000,
            Top = -12000,
        };
        window.OverrideWorkAreaForTests(new Rect(-12000, -12000, 1600, 1000));
        var trayFieldEarly = typeof(MainWindow).GetField("_tray",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (trayFieldEarly?.GetValue(window) is System.Windows.Forms.NotifyIcon earlyTray)
            earlyTray.Visible = false;
        window.Show();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Invoke(window, "OnSettings", window, new RoutedEventArgs());
        window.UpdateLayout();
        var checkbox = (System.Windows.Controls.CheckBox)(window.FindName("QuotaSnapshotCheckBox")
            ?? throw new InvalidOperationException("checkbox_missing"));
        var block = (FrameworkElement)(window.FindName("QuotaSnapshotSettingBlock")
            ?? throw new InvalidOperationException("block_missing"));
        Expect(checkbox.IsChecked != true, "ui-default-off");
        var readyField = typeof(MainWindow).GetField("_settingsReady",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Expect(readyField?.GetValue(window) is true, "settings-ready");
        checkbox.IsChecked = true;
        Invoke(window, "OnQuotaSnapshotSettingClick", checkbox, new RoutedEventArgs());
        WaitForSetting(engine, "1", TimeSpan.FromSeconds(5));
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        engine.FlushQuotaSnapshotAsync().GetAwaiter().GetResult();
        var snapshot = Path.Combine(directory, QuotaSnapshotWriter.FileName);
        Expect(File.Exists(snapshot), "ui-on-writes");
        window.UpdateLayout();
        SaveElementPng(block, Path.Combine(output, "settings-quota-snapshot.png"));
        var onBytes = File.ReadAllBytes(snapshot);
        checkbox.IsChecked = false;
        Invoke(window, "OnQuotaSnapshotSettingClick", checkbox, new RoutedEventArgs());
        WaitForSetting(engine, "0", TimeSpan.FromSeconds(5));
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        engine.ExportCurrentQuotaSnapshot();
        engine.FlushQuotaSnapshotAsync().GetAwaiter().GetResult();
        Expect(File.ReadAllBytes(snapshot).AsSpan().SequenceEqual(onBytes), "ui-off-no-new-write");
        Expect(checkbox.IsChecked != true, "ui-ends-off");
        var trayField = typeof(MainWindow).GetField("_tray",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (trayField?.GetValue(window) is System.Windows.Forms.NotifyIcon tray)
        {
            tray.Visible = false;
            tray.Dispose();
        }

        window.Hide();
        window.DisposeResources();
        log.AppendLine("settings_ui=rendered default_off=true persisted_on_then_off=true");
    }

    private static void ParserCodexWindows(string root, string output, StringBuilder log)
    {
        var observed = DateTimeOffset.Parse("2026-10-04T11:58:00.0000000Z", CultureInfo.InvariantCulture);
        var written = DateTimeOffset.Parse("2026-10-04T12:00:00.0000000Z", CultureInfo.InvariantCulture);
        var primaryReset = DateTimeOffset.Parse("2026-10-04T16:58:00.0000000Z", CultureInfo.InvariantCulture);
        var secondaryReset = DateTimeOffset.Parse("2026-10-11T11:58:00.0000000Z", CultureInfo.InvariantCulture);
        var parsed = QuotaJsonParser.Parse(CodexLimitJson("codex", Secret, 20, 300, primaryReset, 58, 10080, secondaryReset), observed);
        Expect(parsed.Primary?.Id == "codex", "parser-primary-id");
        Expect(parsed.Additional.Any(bucket => bucket.Id == "codex/secondary"), "parser-secondary-id");
        var windows = ProviderQuotaPresentation.FromCodex(parsed);
        Expect(windows.Any(window => window.WindowId == "codex"), "factory-primary");
        Expect(windows.Any(window => window.WindowId == "codex/secondary"), "factory-secondary");
        Expect(windows.Any(window => window.WindowId == Secret), "factory-keeps-unknown");

        var directory = Path.Combine(root, "parser");
        var writer = new QuotaSnapshotWriter(directory, "1.1.3", clock: () => written);
        writer.SetEnabled(true);
        writer.Publish(new ProviderQuotaBoard(new[]
        {
            Slot(ProviderSlotIds.CodexPrimary, ProviderIds.Codex, QuotaSlotStatus.Live, observed, windows),
        }, written));
        writer.WhenIdleAsync().GetAwaiter().GetResult();
        var json = File.ReadAllText(writer.FinalPath, Encoding.UTF8);
        File.WriteAllText(Path.Combine(output, "SYNTHETIC_EXAMPLE.json"), json, new UTF8Encoding(false));
        using var document = JsonDocument.Parse(json);
        var slot = document.RootElement.GetProperty("slots").EnumerateArray().Single(item => item.GetProperty("slot").GetString() == "codex_primary");
        Expect(slot.GetProperty("state").GetString() == "ok", "parser-state");
        var exported = slot.GetProperty("windows").EnumerateArray().ToArray();
        Expect(exported.Length == 2, "parser-both-windows");
        Expect(exported[0].GetProperty("pool").GetString() == "codex", "parser-pool-primary");
        Expect(exported[0].GetProperty("window_minutes").GetInt32() == 300, "parser-primary-minutes");
        Expect(exported[0].GetProperty("remaining_percent").GetDouble() == 80d, "parser-primary-remaining");
        Expect(exported[0].GetProperty("resets_at_utc").GetString() == QuotaSnapshotJson.FormatUtc(primaryReset), "parser-primary-reset");
        Expect(exported[0].GetProperty("observed_at_utc").GetString() == QuotaSnapshotJson.FormatUtc(observed), "parser-primary-observed");
        Expect(exported[1].GetProperty("pool").GetString() == "codex_secondary_window", "parser-pool-secondary");
        Expect(exported[1].GetProperty("window_minutes").GetInt32() == 10080, "parser-secondary-minutes");
        Expect(exported[1].GetProperty("remaining_percent").GetDouble() == 42d, "parser-secondary-remaining");
        Expect(exported[1].GetProperty("resets_at_utc").GetString() == QuotaSnapshotJson.FormatUtc(secondaryReset), "parser-secondary-reset");
        Expect(exported[1].GetProperty("observed_at_utc").GetString() == QuotaSnapshotJson.FormatUtc(observed), "parser-secondary-observed");
        Expect(document.RootElement.GetProperty("written_at_utc").GetString() == QuotaSnapshotJson.FormatUtc(written), "parser-written");
        Expect(!json.Contains(Secret, StringComparison.Ordinal), "parser-secret");

        var alias = QuotaJsonParser.Parse(CodexLimitJson("codex-main", "alias", 10, 300, primaryReset, 30, 10080, secondaryReset), observed);
        var aliasWindows = ProviderQuotaPresentation.FromCodex(alias);
        Expect(aliasWindows.Any(window => window.WindowId == "codex-main"), "alias-main");
        Expect(aliasWindows.Any(window => window.WindowId == "codex-main/secondary"), "alias-secondary");
        Expect(QuotaSnapshotMapper.TryMapPool(ProviderSlotIds.CodexPrimary, "codex-main", out var aliasPrimary) && aliasPrimary == "codex", "alias-pool");
        Expect(QuotaSnapshotMapper.TryMapPool(ProviderSlotIds.CodexPrimary, "codex-main/secondary", out var aliasSecondary) &&
               aliasSecondary == "codex_secondary_window", "alias-secondary-pool");
        Expect(QuotaSnapshotMapper.TryMapPool(ProviderSlotIds.CodexSecondary, "codex_main", out var underscore) && underscore == "codex", "alias-underscore");
        Expect(QuotaSnapshotMapper.TryMapPool(ProviderSlotIds.CodexSecondary, "codex_main/secondary", out var underscoreSecondary) &&
               underscoreSecondary == "codex_secondary_window", "alias-underscore-secondary");
        Expect(!QuotaSnapshotMapper.TryMapPool(ProviderSlotIds.CodexPrimary, Secret + "/secondary", out _), "unknown-secondary");
        Expect(!QuotaSnapshotMapper.TryMapPool(ProviderSlotIds.CodexPrimary, "codex/secondary/extra", out _), "unknown-suffix");
        log.AppendLine("parser_codex_windows=pass");
    }

    private static string CodexLimitJson(string limitId, string limitName, int primaryUsed, int primaryMinutes,
        DateTimeOffset primaryReset, int secondaryUsed, int secondaryMinutes, DateTimeOffset secondaryReset)
    {
        var primaryUnix = primaryReset.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var secondaryUnix = secondaryReset.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return "{\"result\":{\"rateLimitsByLimitId\":{\"" + limitId + "\":{\"limitId\":\"" + limitId +
               "\",\"limitName\":\"" + limitName + "\",\"primary\":{\"usedPercent\":" +
               primaryUsed.ToString(CultureInfo.InvariantCulture) + ",\"windowDurationMins\":" +
               primaryMinutes.ToString(CultureInfo.InvariantCulture) + ",\"resetsAt\":" + primaryUnix +
               "},\"secondary\":{\"usedPercent\":" + secondaryUsed.ToString(CultureInfo.InvariantCulture) +
               ",\"windowDurationMins\":" + secondaryMinutes.ToString(CultureInfo.InvariantCulture) +
               ",\"resetsAt\":" + secondaryUnix + "}},\"" + Secret + "\":{\"limitId\":\"" + Secret +
               "\",\"limitName\":\"" + Secret + "\",\"primary\":{\"usedPercent\":1,\"windowDurationMins\":60,\"resetsAt\":" +
               primaryUnix + "},\"secondary\":{\"usedPercent\":2,\"windowDurationMins\":120,\"resetsAt\":" +
               secondaryUnix + "}}}}}";
    }

    private static void BoundedOwnedTemp(string root, StringBuilder log)
    {
        var successDir = Path.Combine(root, "temp-success");
        Directory.CreateDirectory(successDir);
        File.WriteAllText(Path.Combine(successDir, "keep-me.txt"), "keep");
        var success = new QuotaSnapshotWriter(successDir, "1.1.3");
        success.SetEnabled(true);
        for (var index = 0; index < 8; index++)
            success.Publish(OneBoard(index));
        success.WhenIdleAsync().GetAwaiter().GetResult();
        Expect(File.ReadAllText(Path.Combine(successDir, "keep-me.txt")) == "keep", "success-keeps-unrelated");
        Expect(SnapshotTempNames(successDir).Length == 0, "success-no-temp");
        using (var saved = JsonDocument.Parse(File.ReadAllText(success.FinalPath)))
        {
            Expect(saved.RootElement.GetProperty("schema").GetInt32() == 1, "success-schema");
            Expect(saved.RootElement.GetProperty("slots").GetArrayLength() == 6, "success-slots");
        }

        var recoverDir = Path.Combine(root, "temp-recover");
        Directory.CreateDirectory(recoverDir);
        File.WriteAllText(Path.Combine(recoverDir, "keep-me.txt"), "keep");
        File.WriteAllText(Path.Combine(recoverDir, QuotaSnapshotWriter.TempFileName), "INTERRUPTED-NOT-JSON");
        var recover = new QuotaSnapshotWriter(recoverDir, "1.1.3");
        recover.SetEnabled(true);
        recover.Publish(OneBoard(20));
        recover.WhenIdleAsync().GetAwaiter().GetResult();
        var recovered = File.ReadAllText(recover.FinalPath, Encoding.UTF8);
        Expect(!recovered.Contains("INTERRUPTED", StringComparison.Ordinal), "recovered-not-temp");
        using (var saved = JsonDocument.Parse(recovered))
        {
            Expect(saved.RootElement.GetProperty("slots")[0].GetProperty("windows")[0]
                .GetProperty("remaining_percent").GetDouble() == 80d, "recovered-last");
        }
        Expect(File.ReadAllText(Path.Combine(recoverDir, "keep-me.txt")) == "keep", "recover-keeps-unrelated");
        Expect(SnapshotTempNames(recoverDir).Length == 0, "recover-no-orphan");

        var failDir = Path.Combine(root, "temp-fail");
        Directory.CreateDirectory(failDir);
        Directory.CreateDirectory(Path.Combine(failDir, QuotaSnapshotWriter.FileName));
        File.WriteAllText(Path.Combine(failDir, "keep-me.txt"), "keep");
        var failing = new QuotaSnapshotWriter(failDir, "1.1.3");
        failing.SetEnabled(true);
        for (var index = 0; index < 6; index++)
            failing.Publish(OneBoard(index));
        failing.WhenIdleAsync().GetAwaiter().GetResult();
        Expect(Directory.Exists(Path.Combine(failDir, QuotaSnapshotWriter.FileName)), "fail-leaves-blocker");
        Expect(File.ReadAllText(Path.Combine(failDir, "keep-me.txt")) == "keep", "fail-keeps-unrelated");
        var temps = SnapshotTempNames(failDir);
        Expect(temps.Length <= 1, "fail-temp-bound");
        Expect(temps.All(name => name == QuotaSnapshotWriter.TempFileName), "fail-only-owned-temp");
        log.AppendLine("bounded_temp=pass temps=" + temps.Length.ToString(CultureInfo.InvariantCulture));
    }

    private static string[] SnapshotTempNames(string directory) =>
        Directory.EnumerateFiles(directory)
            .Select(Path.GetFileName)
            .Where(name => name is not null &&
                           name.StartsWith(QuotaSnapshotWriter.FileName, StringComparison.Ordinal) &&
                           name.EndsWith(".tmp", StringComparison.Ordinal))
            .Select(name => name!)
            .ToArray();

    private static ProviderQuotaBoard OneBoard(int usedPercent) => new(new[]
    {
        Slot(ProviderSlotIds.CodexPrimary, ProviderIds.Codex, QuotaSlotStatus.Live, DateTimeOffset.Parse("2026-10-04T11:00:00Z", CultureInfo.InvariantCulture),
            new[] { Window("primary", usedPercent, 300, DateTimeOffset.Parse("2026-10-04T16:00:00Z", CultureInfo.InvariantCulture)) }),
        Slot(ProviderSlotIds.CodexSecondary, ProviderIds.Codex, QuotaSlotStatus.Disabled, DateTimeOffset.UnixEpoch, Array.Empty<QuotaWindowObservation>(), false),
        Slot(ProviderSlotIds.Cursor, ProviderIds.Cursor, QuotaSlotStatus.Disabled, DateTimeOffset.UnixEpoch, Array.Empty<QuotaWindowObservation>(), false),
        Slot(ProviderSlotIds.Grok, ProviderIds.Grok, QuotaSlotStatus.Disabled, DateTimeOffset.UnixEpoch, Array.Empty<QuotaWindowObservation>(), false),
        Slot(ProviderSlotIds.GrokBot, ProviderIds.GrokBot, QuotaSlotStatus.Disabled, DateTimeOffset.UnixEpoch, Array.Empty<QuotaWindowObservation>(), false),
        Slot(ProviderSlotIds.Claude, ProviderIds.Claude, QuotaSlotStatus.Disabled, DateTimeOffset.UnixEpoch, Array.Empty<QuotaWindowObservation>(), false),
    }, DateTimeOffset.Parse("2026-10-04T12:00:00Z", CultureInfo.InvariantCulture));

    private static ProviderSlotSnapshot Slot(string slotId, string providerId, QuotaSlotStatus status,
        DateTimeOffset observed, IReadOnlyList<QuotaWindowObservation> windows, bool enabled = true, bool delayed = false) =>
        new(slotId, providerId, Secret, enabled, false, status, Secret, observed, Secret, windows,
            OpaqueIdentityHash: Secret, ErrorCode: Secret, AccountKey: Secret, ConfigFingerprint: Secret,
            FieldPresenceFlags: Secret, UpdateDelayed: delayed, DelayText: Secret);

    private static QuotaWindowObservation Window(string id, double? used, int? minutes, DateTimeOffset? reset) =>
        new(id, Secret, used, reset, minutes, used.HasValue, Secret);

    private static void AssertAllowlisted(JsonElement slot, bool windowLevel)
    {
        foreach (var property in slot.EnumerateObject())
        {
            if (property.Name is not ("slot" or "label" or "state" or "windows"))
                throw new InvalidOperationException("unexpected_slot_field");
        }

        if (!windowLevel) return;
        foreach (var window in slot.GetProperty("windows").EnumerateArray())
        {
            foreach (var property in window.EnumerateObject())
            {
                if (property.Name is not ("pool" or "window_minutes" or "remaining_percent" or "resets_at_utc" or "observed_at_utc"))
                    throw new InvalidOperationException("unexpected_window_field");
            }
        }
    }

    private static void SaveElementPng(FrameworkElement element, string outputPath)
    {
        element.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        var width = element.ActualWidth;
        var height = element.ActualHeight;
        if (width < 1 || height < 1)
        {
            element.Measure(new Size(358, double.PositiveInfinity));
            element.Arrange(new Rect(0, 0, 358, element.DesiredSize.Height));
            element.UpdateLayout();
            width = Math.Max(element.ActualWidth, element.DesiredSize.Width);
            height = Math.Max(element.ActualHeight, element.DesiredSize.Height);
        }

        var pixelWidth = Math.Max(1, (int)Math.Ceiling(width));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(height));
        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }

    private static void WaitForSetting(UsageEngine engine, string expected, TimeSpan timeout)
    {
        Task<IReadOnlyDictionary<string, string?>>? pending = null;
        var observed = "<none>";
        PumpUntil(() =>
        {
            pending ??= engine.LoadSettingsAsync(QuotaSnapshotWriter.SettingKey);
            if (!pending.IsCompleted) return false;
            observed = pending.GetAwaiter().GetResult().GetValueOrDefault(QuotaSnapshotWriter.SettingKey) ?? "<null>";
            if (observed == expected) return true;
            pending = engine.LoadSettingsAsync(QuotaSnapshotWriter.SettingKey);
            return false;
        }, timeout, () => "observed=" + observed);
    }

    private static void PumpUntil(Func<bool> done, TimeSpan timeout, Func<string>? detail)
    {
        if (done()) return;
        var frame = new DispatcherFrame();
        var started = DateTime.UtcNow;
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(20),
        };
        timer.Tick += (_, _) =>
        {
            if (done() || DateTime.UtcNow - started > timeout)
            {
                timer.Stop();
                frame.Continue = false;
            }
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        if (!done()) throw new TimeoutException("ui_pump_timeout " + (detail?.Invoke() ?? string.Empty));
    }

    private static void Invoke(MainWindow window, string name, params object?[] arguments)
    {
        var method = typeof(MainWindow).GetMethod(name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("method_missing:" + name);
        method.Invoke(window, arguments);
    }

    private static void Expect(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("assertion_failed:" + name);
    }

    private static string Scrub(string text)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
            text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        return text;
    }
}
