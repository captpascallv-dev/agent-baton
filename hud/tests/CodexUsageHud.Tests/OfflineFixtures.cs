using CodexUsageHud.Core;

namespace CodexUsageHud.Tests;

internal static partial class Program
{
    private static readonly Lazy<string> TemporaryFixtures = new(() => NewRunRoot("temporary-fixtures"));

    private static string TestTempRoot()
    {
        return TemporaryFixtures.Value;
    }

    private static void RestoreTestWindow(CodexUsageHud.App.MainWindow window)
    {
        var restore = typeof(CodexUsageHud.App.MainWindow).GetMethod("RestoreWindowStateAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        AwaitWithDispatcher((Task)restore.Invoke(window, null)!, window.Dispatcher, TimeSpan.FromSeconds(5));
    }

    private static IAllowlistedHttpSender OfflineHttp() => new ScriptedHttpSender((_, _) =>
        throw new InvalidOperationException("unexpected_http_request_in_offline_test"));

    private static CodexQuotaProfile OfflineCodex() => new ScriptedCodexProfile((slot, _, _) =>
        Task.FromResult(ProviderQuotaPresentation.Placeholder(slot,
            slot.SlotId == ProviderSlotIds.CodexPrimary, QuotaSlotStatus.NotConnected,
            "Offline fixture", "No app-server process", "offline_fixture")));

    private static ProviderQuotaCoordinator OfflineCoordinator(CodexQuotaProfile? codex = null,
        IAllowlistedHttpSender? http = null, ICursorTokenSource? cursorTokens = null,
        IGrokTokenSource? grokTokens = null, PiCodexQuotaAdapter? piCodex = null,
        IPiCodexTokenSource? piTokens = null, IClaudeTokenSource? claudeTokens = null,
        Func<DateTimeOffset>? clock = null, TimeSpan? publishBudget = null,
        Func<string>? grokLoginStamp = null, Func<string>? claudeLoginStamp = null,
        Func<string, string>? codexCliAuthStamp = null) => new(
        codex: codex ?? OfflineCodex(), http: http ?? OfflineHttp(),
        cursorTokens: cursorTokens ?? new InjectedTokenSource(null),
        grokTokens: grokTokens ?? new InjectedTokenSource(null), piCodex: piCodex,
        piTokens: piTokens ?? new InjectedPiCodexTokenSource(null),
        claudeTokens: claudeTokens ?? new InjectedClaudeTokenSource(null), clock: clock)
    {
        PublishBudget = publishBudget ?? TimeSpan.FromMilliseconds(500),
        GrokLoginStampOverride = grokLoginStamp ?? (() => "offline-grok"),
        ClaudeLoginStampOverride = claudeLoginStamp ?? (() => "offline-claude"),
        CodexCliAuthStampOverride = codexCliAuthStamp,
    };

    private static void RunTest(Action run)
    {
        var context = SynchronizationContext.Current;
        try
        {
            run();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(context);
        }
    }

    private static void RunnerRestoresSynchronizationContext()
    {
        var context = SynchronizationContext.Current;
        RunTest(() => SynchronizationContext.SetSynchronizationContext(
            new System.Windows.Threading.DispatcherSynchronizationContext()));
        Assert.True(ReferenceEquals(context, SynchronizationContext.Current), "context_leaked_after_success");
        try
        {
            RunTest(() =>
            {
                SynchronizationContext.SetSynchronizationContext(
                    new System.Windows.Threading.DispatcherSynchronizationContext());
                throw new InvalidOperationException("synthetic_failure");
            });
        }
        catch (InvalidOperationException exception) when (exception.Message == "synthetic_failure") { }
        Assert.True(ReferenceEquals(context, SynchronizationContext.Current), "context_leaked_after_failure");
    }
}
