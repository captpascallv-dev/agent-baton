using System.Collections.Concurrent;

namespace CodexUsageHud.Core;

public sealed class IsolatedQuotaCache
{
    private readonly ConcurrentDictionary<string, QuotaObservation> _latest = new(StringComparer.Ordinal);

    public QuotaObservation Store(string slotId, QuotaObservation observation)
    {
        var copy = observation with { Additional = observation.Additional.ToArray() };
        _latest[slotId] = copy;
        return copy;
    }

    public QuotaObservation? Load(string slotId) =>
        _latest.TryGetValue(slotId, out var value) ? value : null;

    public bool Remove(string slotId) => _latest.TryRemove(slotId, out _);

    public bool SharesReference(string leftSlotId, string rightSlotId) =>
        _latest.TryGetValue(leftSlotId, out var left) &&
        _latest.TryGetValue(rightSlotId, out var right) &&
        ReferenceEquals(left, right);
}

public sealed class ProviderRefreshBackoff
{
    public static readonly TimeSpan DefaultFailureDelay = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan DefaultFailureCap = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan DefaultSuccessInterval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan RateLimitInitial = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan RateLimitCap = TimeSpan.FromMinutes(30);

    private DateTimeOffset _nextAttemptUtc = DateTimeOffset.MinValue;
    private TimeSpan _delay = DefaultFailureDelay;
    private TimeSpan _rateDelay = RateLimitInitial;
    private bool _rateLimitedHold;

    public bool IsDue(DateTimeOffset nowUtc) => nowUtc >= _nextAttemptUtc;

    public bool HoldsManual(DateTimeOffset nowUtc) => _rateLimitedHold && nowUtc < _nextAttemptUtc;

    public void Succeeded(DateTimeOffset nowUtc, TimeSpan? successInterval = null)
    {
        _delay = DefaultFailureDelay;
        _rateDelay = RateLimitInitial;
        _rateLimitedHold = false;
        _nextAttemptUtc = nowUtc + (successInterval ?? DefaultSuccessInterval);
    }

    public void Failed(DateTimeOffset nowUtc)
    {
        if (_rateLimitedHold && nowUtc < _nextAttemptUtc)
            return;
        _rateLimitedHold = false;
        _nextAttemptUtc = nowUtc + _delay;
        var nextMs = Math.Min(_delay.TotalMilliseconds * 2, DefaultFailureCap.TotalMilliseconds);
        _delay = TimeSpan.FromMilliseconds(nextMs);
    }

    public void FailedRateLimited(DateTimeOffset nowUtc)
    {
        _rateLimitedHold = true;
        _nextAttemptUtc = nowUtc + _rateDelay;
        var nextMs = Math.Min(_rateDelay.TotalMilliseconds * 2, RateLimitCap.TotalMilliseconds);
        _rateDelay = TimeSpan.FromMilliseconds(nextMs);
    }
}

public sealed class ProviderQuotaCoordinator
{
    public static readonly TimeSpan LiveFreshness = TimeSpan.FromSeconds(75);
    public static readonly TimeSpan ClaudeLiveFreshness = TimeSpan.FromMinutes(6);
    public static readonly TimeSpan ClaudeSuccessInterval = TimeSpan.FromMinutes(5);

    private readonly CodexQuotaProfile _codex;
    private readonly PiCodexQuotaAdapter _piCodex;
    private readonly CursorQuotaAdapter _cursor;
    private readonly GrokQuotaAdapter _grok;
    private readonly GrokBotQuotaAdapter _grokBot;
    private readonly ClaudeQuotaAdapter _claude;
    private readonly IPiCodexTokenSource _piTokens;
    private readonly IsolatedQuotaCache _cache = new();
    private readonly ConcurrentDictionary<string, ProviderRefreshBackoff> _backoff = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ProviderSlotSnapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _generation = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _generationFingerprint = new(StringComparer.Ordinal);
    private readonly object _generationLock = new();
    private readonly Func<DateTimeOffset> _clock;

    public event Action<ProviderSlotSnapshot>? SlotPublished;

    public ProviderQuotaCoordinator(CodexQuotaProfile? codex = null, CursorQuotaAdapter? cursor = null,
        GrokQuotaAdapter? grok = null, GrokBotQuotaAdapter? grokBot = null,
        IAllowlistedHttpSender? http = null, ICursorTokenSource? cursorTokens = null,
        IGrokTokenSource? grokTokens = null, PiCodexQuotaAdapter? piCodex = null,
        IPiCodexTokenSource? piTokens = null, ClaudeQuotaAdapter? claude = null,
        IClaudeTokenSource? claudeTokens = null, Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        http ??= new AllowlistedHttpsSender(TimeSpan.FromSeconds(8));
        cursorTokens ??= DefaultTokenSources.Cursor();
        grokTokens ??= DefaultTokenSources.Grok();
        _piTokens = piTokens ?? DefaultTokenSources.PiCodex();
        _codex = codex ?? new CodexQuotaProfile();
        _piCodex = piCodex ?? new PiCodexQuotaAdapter(http, _piTokens);
        _cursor = cursor ?? new CursorQuotaAdapter(http, cursorTokens);
        _grok = grok ?? new GrokQuotaAdapter(http, grokTokens);
        _grokBot = grokBot ?? new GrokBotQuotaAdapter(http, cursorTokens);
        _claude = claude ?? new ClaudeQuotaAdapter(http, claudeTokens ?? DefaultTokenSources.Claude(), _clock);
    }

    public IsolatedQuotaCache Cache => _cache;
    public TimeSpan PublishBudget { get; set; } = TimeSpan.FromMilliseconds(500);

    public Func<string>? GrokLoginStampOverride { get; set; }

    public Func<string>? ClaudeLoginStampOverride { get; set; }

    public Func<string, string>? CodexCliAuthStampOverride { get; set; }

    public static string FormatLoginStamp(LoginPresence presence)
    {
        if (!presence.Present) return "0";
        var ticks = presence.LastWriteUtc?.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0";
        var length = presence.Length?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0";
        return ticks + ":" + length;
    }

    public ProviderQuotaBoard CurrentBoard(DateTimeOffset nowUtc, ProviderAccessSettings settings)
    {
        var slots = settings.Slots.Select(slot => PresentSlot(slot, nowUtc)).ToArray();
        return new ProviderQuotaBoard(ApplyIdentityCollisions(slots), nowUtc);
    }

    public async Task<ProviderQuotaBoard> RefreshAsync(ProviderAccessSettings settings,
        QuotaObservation? primaryCodexObservation, CancellationToken cancellationToken, bool manual = false,
        string? primaryIdentityHash = null, string? primaryCodexHome = null)
    {
        var tasks = settings.Slots.Select(slot => RefreshSlotAsync(slot, primaryCodexObservation,
            cancellationToken, manual, primaryIdentityHash, primaryCodexHome)).ToArray();
        var all = Task.WhenAll(tasks);
        var winner = await Task.WhenAny(all, Task.Delay(PublishBudget, cancellationToken)).ConfigureAwait(false);
        if (winner != all)
            return CurrentBoard(_clock(), settings);

        await all.ConfigureAwait(false);
        return CurrentBoard(_clock(), settings);
    }

    private ProviderSlotSnapshot PresentSlot(ProviderSlotSettings slot, DateTimeOffset nowUtc)
    {
        var fingerprint = ConfigurationKey(slot);
        if (!slot.Enabled)
        {
            return ProviderQuotaPresentation.Placeholder(slot, slot.SlotId == ProviderSlotIds.CodexPrimary,
                QuotaSlotStatus.Disabled, "未启用", "用户关闭了此槽", "slot_disabled") with
            {
                ConfigFingerprint = fingerprint,
            };
        }

        if (!_snapshots.TryGetValue(slot.SlotId, out var snapshot))
        {
            return ProviderQuotaPresentation.Placeholder(slot, slot.SlotId == ProviderSlotIds.CodexPrimary,
                QuotaSlotStatus.Unavailable, "尚未刷新", "等待独立刷新") with
            {
                ConfigFingerprint = fingerprint,
            };
        }

        if (!string.Equals(snapshot.ConfigFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return ProviderQuotaPresentation.Placeholder(slot, slot.SlotId == ProviderSlotIds.CodexPrimary,
                QuotaSlotStatus.Unavailable, "配置已更换，等待刷新", "不沿用上一账户额度",
                "config_changed") with
            {
                ConfigFingerprint = fingerprint,
            };
        }

        return AgeIfNeeded(snapshot with { Label = slot.Label, Enabled = true }, nowUtc);
    }

    private async Task<ProviderSlotSnapshot> RefreshSlotAsync(ProviderSlotSettings slot,
        QuotaObservation? primaryCodexObservation,
        CancellationToken cancellationToken, bool manual, string? primaryIdentityHash, string? primaryCodexHome)
    {
        var fingerprint = ConfigurationKey(slot);
        var started = BeginGeneration(slot.SlotId, fingerprint);
        if (!slot.Enabled)
        {
            DropCachedQuota(slot.SlotId);
            var disabled = ProviderQuotaPresentation.Placeholder(slot,
                slot.SlotId == ProviderSlotIds.CodexPrimary, QuotaSlotStatus.Disabled, "未启用",
                "用户关闭了此槽", "slot_disabled") with { ConfigFingerprint = fingerprint };
            Publish(slot.SlotId, disabled);
            return disabled;
        }

        var gate = _gates.GetOrAdd(slot.SlotId, _ => new SemaphoreSlim(1, 1));
        var acquiredImmediate = await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false);
        if (!acquiredImmediate)
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!IsCurrent(slot.SlotId, started))
                return PresentSlot(slot, _clock());

            if (!acquiredImmediate &&
                _snapshots.TryGetValue(slot.SlotId, out var published) &&
                string.Equals(published.ConfigFingerprint, fingerprint, StringComparison.Ordinal))
            {
                return AgeIfNeeded(published, _clock());
            }

            var backoff = _backoff.GetOrAdd(slot.SlotId, _ => new ProviderRefreshBackoff());
            var now = _clock();
            var skipBackoff = slot.SlotId == ProviderSlotIds.CodexPrimary && primaryCodexObservation is not null;
            var manualHeld = manual && backoff.HoldsManual(now);
            if ((!manual || manualHeld) && !skipBackoff && !backoff.IsDue(now) &&
                _snapshots.TryGetValue(slot.SlotId, out var cached) &&
                string.Equals(cached.ConfigFingerprint, fingerprint, StringComparison.Ordinal))
            {
                return AgeIfNeeded(cached, now);
            }

            if (_snapshots.TryGetValue(slot.SlotId, out var previous) &&
                !string.Equals(previous.ConfigFingerprint, fingerprint, StringComparison.Ordinal))
            {
                DropCachedQuota(slot.SlotId);
                _backoff[slot.SlotId] = new ProviderRefreshBackoff();
                backoff = _backoff[slot.SlotId];
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(SlotReadTimeout(slot.SlotId));
            ProviderSlotSnapshot snapshot;
            try
            {
                snapshot = await ReadSlotAsync(slot, primaryCodexObservation, timeout.Token, primaryIdentityHash,
                    primaryCodexHome).ConfigureAwait(false);
                // PI renewal rotates its login during the read. A CLI app-server read can also
                // touch the selected home's auth-file metadata. Bind this completed observation
                // to the post-read stamp so that rotation does not drop the windows just read.
                if (slot.SlotId == ProviderSlotIds.CodexSecondary && IsCurrent(slot.SlotId, started))
                {
                    var refreshedKey = ConfigurationKey(slot);
                    if (!string.Equals(fingerprint, refreshedKey, StringComparison.Ordinal))
                    {
                        fingerprint = refreshedKey;
                        started = BeginGeneration(slot.SlotId, fingerprint);
                    }
                }
                snapshot = snapshot with { ConfigFingerprint = fingerprint };
                var sampledAt = _clock();
                if (snapshot.Status is QuotaSlotStatus.Live or QuotaSlotStatus.Stale or
                    QuotaSlotStatus.Disabled or QuotaSlotStatus.SetupRequired or QuotaSlotStatus.NotConnected
                    or QuotaSlotStatus.NoAllowance)
                {
                    var interval = slot.SlotId == ProviderSlotIds.Claude &&
                        snapshot.Status is QuotaSlotStatus.Live or QuotaSlotStatus.Stale
                        ? ClaudeSuccessInterval
                        : ProviderRefreshBackoff.DefaultSuccessInterval;
                    backoff.Succeeded(sampledAt, interval);
                }
                else if (slot.SlotId == ProviderSlotIds.Claude &&
                         string.Equals(snapshot.ErrorCode, "claude_rate_limited", StringComparison.Ordinal))
                {
                    backoff.FailedRateLimited(sampledAt);
                }
                else backoff.Failed(sampledAt);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                backoff.Failed(_clock());
                snapshot = ProviderQuotaPresentation.Placeholder(slot,
                    slot.SlotId == ProviderSlotIds.CodexPrimary, QuotaSlotStatus.Unavailable,
                    "此来源超时，不影响其他槽", "独立超时", "slot_timeout") with
                {
                    ConfigFingerprint = fingerprint,
                };
            }
            catch (Exception)
            {
                backoff.Failed(_clock());
                snapshot = ProviderQuotaPresentation.Placeholder(slot,
                    slot.SlotId == ProviderSlotIds.CodexPrimary, QuotaSlotStatus.Unavailable,
                    "此来源失败，不影响其他槽", "独立失败", "slot_failed") with
                {
                    ConfigFingerprint = fingerprint,
                };
            }

            if (!IsCurrent(slot.SlotId, started))
                return PresentSlot(slot, _clock());

            Publish(slot.SlotId, snapshot);
            return snapshot;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ProviderSlotSnapshot> ReadSlotAsync(ProviderSlotSettings slot,
        QuotaObservation? primaryCodexObservation, CancellationToken cancellationToken,
        string? primaryIdentityHash, string? primaryCodexHome)
    {
        switch (slot.SlotId)
        {
            case ProviderSlotIds.CodexPrimary:
                if (primaryCodexObservation is not null)
                {
                    var windows = ProviderQuotaPresentation.FromCodex(primaryCodexObservation);
                    var now = _clock();
                    var status = windows.Count == 0
                        ? QuotaSlotStatus.Unavailable
                        : ProviderQuotaPresentation.CodexStatus(primaryCodexObservation, now);
                    if (windows.Any(window => window.ResetsAtUtc.HasValue && window.ResetsAtUtc.Value <= now))
                        status = QuotaSlotStatus.Stale;
                    var snapshot = new ProviderSlotSnapshot(slot.SlotId, ProviderIds.Codex, slot.Label,
                        slot.Enabled, true, slot.Enabled ? status : QuotaSlotStatus.Disabled,
                        slot.Enabled ? ProviderQuotaPresentation.StatusCaption(status) : "未启用",
                        primaryCodexObservation.ObservedAtUtc, "当前绑定的 Codex App 主目录",
                        windows, primaryIdentityHash, primaryCodexObservation.ErrorCode, primaryIdentityHash);
                    _cache.Store(slot.SlotId, primaryCodexObservation);
                    return snapshot;
                }

                var primary = await _codex.ReadAsync(slot, primaryCodexHome, true, cancellationToken)
                    .ConfigureAwait(false);
                _cache.Store(slot.SlotId, ObservationFrom(primary));
                return primary;
            case ProviderSlotIds.CodexSecondary:
                if (!SecondaryCodexHome.UsesCli(slot.CodexHome))
                {
                    var secondary = await _piCodex.ReadAsync(slot, cancellationToken).ConfigureAwait(false);
                    _cache.Store(slot.SlotId, ObservationFrom(secondary));
                    return secondary;
                }

                var cliHome = SecondaryCodexHome.Normalize(slot.CodexHome);
                var homeStatus = SecondaryCodexHome.Classify(cliHome, primaryCodexHome);
                if (homeStatus == SecondaryHomeStatus.SameAsPrimary)
                {
                    var sameHome = ProviderQuotaPresentation.Placeholder(slot, false,
                        QuotaSlotStatus.SetupRequired, SecondaryCodexHome.SameStatusText,
                        SecondaryCodexHome.SameDetail, SecondaryCodexHome.SameCode);
                    _cache.Store(slot.SlotId, ObservationFrom(sameHome));
                    return sameHome;
                }

                if (homeStatus != SecondaryHomeStatus.Ready)
                {
                    var invalidHome = ProviderQuotaPresentation.Placeholder(slot, false,
                        QuotaSlotStatus.SetupRequired, SecondaryCodexHome.InvalidStatusText,
                        SecondaryCodexHome.InvalidDetail, SecondaryCodexHome.InvalidCode);
                    _cache.Store(slot.SlotId, ObservationFrom(invalidHome));
                    return invalidHome;
                }

                var cli = await _codex.ReadAsync(slot, cliHome, false, cancellationToken, primaryCodexHome)
                    .ConfigureAwait(false);
                _cache.Store(slot.SlotId, ObservationFrom(cli));
                return cli;
            case ProviderSlotIds.Cursor:
                return await _cursor.ReadAsync(slot, cancellationToken).ConfigureAwait(false);
            case ProviderSlotIds.Grok:
                return await _grok.ReadAsync(slot, cancellationToken).ConfigureAwait(false);
            case ProviderSlotIds.GrokBot:
                return await _grokBot.ReadAsync(slot, cancellationToken).ConfigureAwait(false);
            case ProviderSlotIds.Claude:
                return await _claude.ReadAsync(slot, cancellationToken).ConfigureAwait(false);
            default:
                return ProviderQuotaPresentation.Placeholder(slot, false, QuotaSlotStatus.Unavailable,
                    "未知槽", "slot_unknown", "slot_unknown");
        }
    }

    public static IReadOnlyList<ProviderSlotSnapshot> ApplyIdentityCollisions(
        IReadOnlyList<ProviderSlotSnapshot> slots)
    {
        var result = slots.ToArray();
        for (var i = 0; i < result.Length; i++)
        {
            var current = result[i];
            if (string.IsNullOrWhiteSpace(current.OpaqueIdentityHash) ||
                current.Status is QuotaSlotStatus.Disabled or QuotaSlotStatus.NotConnected or
                    QuotaSlotStatus.SetupRequired)
            {
                continue;
            }

            for (var j = 0; j < i; j++)
            {
                var previous = result[j];
                if (string.IsNullOrWhiteSpace(previous.OpaqueIdentityHash)) continue;
                if (!string.Equals(previous.ProviderId, current.ProviderId, StringComparison.Ordinal)) continue;
                if (!string.Equals(previous.OpaqueIdentityHash, current.OpaqueIdentityHash,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                result[i] = current with
                {
                    Status = QuotaSlotStatus.IdentityCollision,
                    StatusText = "与「" + previous.Label + "」为同一账户，不作为独立额度",
                    Windows = Array.Empty<QuotaWindowObservation>(),
                    ErrorCode = "identity_collision",
                };
                break;
            }
        }

        return result;
    }

    public static ProviderSlotSnapshot AgeIfNeeded(ProviderSlotSnapshot snapshot, DateTimeOffset nowUtc)
    {
        var windows = snapshot.Windows.Select(window =>
            window.ResetsAtUtc.HasValue && window.ResetsAtUtc.Value <= nowUtc
                ? window with { HasAllowance = false, MissingResetText = "已到期（陈旧）" }
                : window).ToArray();
        var hasCurrent = windows.Any(window => window.HasUsablePercent);
        var status = snapshot.Status;
        var text = snapshot.StatusText;
        string? delayText = null;
        if (snapshot.UpdateDelayed)
        {
            status = QuotaSlotStatus.Stale;
            text = "更新延迟 · " + ProviderQuotaPresentation.FormatAge(snapshot.ObservationAge(nowUtc));
            delayText = text;
        }
        else if (snapshot.Status == QuotaSlotStatus.Live)
        {
            var freshness = snapshot.SlotId == ProviderSlotIds.Claude ? ClaudeLiveFreshness : LiveFreshness;
            if (snapshot.Windows.Count > 0 && !hasCurrent)
            {
                status = QuotaSlotStatus.Stale;
                text = "窗口已到期，不作为当前额度";
            }
            else if (snapshot.ObservationAge(nowUtc) > freshness)
            {
                status = QuotaSlotStatus.Stale;
                text = "观测已超过新鲜窗口，不作为实时额度";
            }
        }

        return snapshot with { Windows = windows, Status = status, StatusText = text, DelayText = delayText };
    }

    public string ConfigurationKey(ProviderSlotSettings slot)
    {
        var presence = slot.SlotId switch
        {
            ProviderSlotIds.Cursor or ProviderSlotIds.GrokBot =>
                WindowsLoginPresence.Cursor().LastWriteUtc?.UtcTicks.ToString() ?? "0",
            ProviderSlotIds.Grok =>
                GrokLoginStampOverride?.Invoke() ?? FormatLoginStamp(WindowsLoginPresence.GrokAuthFile()),
            ProviderSlotIds.Claude =>
                ClaudeLoginStampOverride?.Invoke() ??
                FormatLoginStamp(WindowsLoginPresence.ClaudeCredentialsFile()) + ":" +
                FormatLoginStamp(WindowsLoginPresence.ClaudeDesktopProfile()),
            ProviderSlotIds.CodexSecondary => SecondaryFingerprint(slot),
            _ => slot.CodexHome ?? string.Empty,
        };
        return string.Join('|', slot.SlotId, slot.Enabled ? "1" : "0", slot.CodexHome ?? string.Empty, presence);
    }

    private string SecondaryFingerprint(ProviderSlotSettings slot)
    {
        if (!SecondaryCodexHome.UsesCli(slot.CodexHome))
            return _piTokens.ConfigurationFingerprint() + "|" + (slot.CodexHome ?? string.Empty);

        var home = SecondaryCodexHome.Normalize(slot.CodexHome) ?? slot.CodexHome ?? string.Empty;
        var stamp = CodexCliAuthStampOverride?.Invoke(home) ??
                    FormatLoginStamp(WindowsLoginPresence.CodexCliAuthFile(home));
        return "cli|" + home + "|" + stamp;
    }

    private int BeginGeneration(string slotId, string fingerprint)
    {
        lock (_generationLock)
        {
            if (_generationFingerprint.TryGetValue(slotId, out var current) &&
                string.Equals(current, fingerprint, StringComparison.Ordinal))
            {
                return _generation.GetOrAdd(slotId, 1);
            }

            _generationFingerprint[slotId] = fingerprint;
            return _generation.AddOrUpdate(slotId, 1, (_, value) => value + 1);
        }
    }

    private bool IsCurrent(string slotId, int started) =>
        _generation.TryGetValue(slotId, out var current) && current == started;

    private void Publish(string slotId, ProviderSlotSnapshot snapshot)
    {
        if (slotId == ProviderSlotIds.Claude)
            snapshot = ResolveClaudePublication(snapshot);
        _snapshots[slotId] = snapshot;
        SlotPublished?.Invoke(snapshot);
    }

    private ProviderSlotSnapshot ResolveClaudePublication(ProviderSlotSnapshot incoming)
    {
        if (!IsClaudeTransientFailure(incoming))
            return incoming with { UpdateDelayed = false, DelayText = null };

        if (!_snapshots.TryGetValue(ProviderSlotIds.Claude, out var previous) ||
            !CanRetainClaude(previous, incoming))
        {
            return incoming with { UpdateDelayed = false, DelayText = null };
        }

        return previous with
        {
            Label = incoming.Label,
            Enabled = true,
            Status = QuotaSlotStatus.Stale,
            StatusText = "更新延迟",
            ErrorCode = incoming.ErrorCode,
            UpdateDelayed = true,
            DelayText = null,
            FieldPresenceFlags = incoming.FieldPresenceFlags ?? previous.FieldPresenceFlags,
        };
    }

    private static bool CanRetainClaude(ProviderSlotSnapshot previous, ProviderSlotSnapshot incoming)
    {
        if (!previous.Enabled || previous.Windows.Count == 0) return false;
        if (previous.Status is not (QuotaSlotStatus.Live or QuotaSlotStatus.Stale)) return false;
        if (string.IsNullOrWhiteSpace(previous.OpaqueIdentityHash)) return false;
        if (!string.Equals(previous.ConfigFingerprint, incoming.ConfigFingerprint, StringComparison.Ordinal))
            return false;
        if (!string.IsNullOrWhiteSpace(incoming.OpaqueIdentityHash) &&
            !string.Equals(previous.OpaqueIdentityHash, incoming.OpaqueIdentityHash, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static bool IsClaudeTransientFailure(ProviderSlotSnapshot snapshot)
    {
        if (snapshot.SlotId != ProviderSlotIds.Claude || snapshot.Status != QuotaSlotStatus.Unavailable)
            return false;
        var code = snapshot.ErrorCode ?? string.Empty;
        if (code is "claude_rate_limited" or "claude_timeout" or "claude_network" or "slot_timeout" or "slot_failed")
            return true;
        const string httpPrefix = "claude_http_";
        return code.StartsWith(httpPrefix, StringComparison.Ordinal) &&
               int.TryParse(code.AsSpan(httpPrefix.Length), out var status) &&
               status is >= 500 and <= 599;
    }

    private void DropCachedQuota(string slotId)
    {
        _cache.Remove(slotId);
        _backoff.TryRemove(slotId, out _);
    }

    public static TimeSpan SlotReadTimeout(string slotId)
    {
        if (string.Equals(slotId, ProviderSlotIds.Grok, StringComparison.Ordinal))
            return TimeSpan.FromSeconds(35);
        if (string.Equals(slotId, ProviderSlotIds.CodexSecondary, StringComparison.Ordinal))
            return TimeSpan.FromSeconds(24);
        if (slotId.StartsWith("codex", StringComparison.Ordinal))
            return TimeSpan.FromSeconds(12);
        return TimeSpan.FromSeconds(8);
    }

    private static QuotaObservation ObservationFrom(ProviderSlotSnapshot snapshot)
    {
        var buckets = snapshot.Windows
            .Where(window => window.HasUsablePercent && window.ResetsAtUtc.HasValue &&
                             window.WindowDurationMinutes is > 0)
            .Select(window => new QuotaBucket(window.WindowId, window.DisplayName, window.UsedPercent!.Value,
                window.WindowDurationMinutes!.Value, window.ResetsAtUtc!.Value))
            .ToArray();
        return new QuotaObservation(buckets.FirstOrDefault(), buckets.Skip(1).ToArray(),
            QuotaSource.OfficialAppServer, snapshot.ObservedAtUtc,
            snapshot.Status == QuotaSlotStatus.Stale, snapshot.ErrorCode);
    }

    public AdapterDiagnostic Diagnostic(ProviderSlotSnapshot slot, string? otherIdentity) =>
        new(slot.SlotId, slot.Status.ToString(), slot.ObservedAtUtc,
            !string.IsNullOrWhiteSpace(slot.OpaqueIdentityHash) &&
            string.Equals(slot.OpaqueIdentityHash, otherIdentity, StringComparison.Ordinal),
            slot.OpaqueIdentityHash is null ? null : OpaqueIdentity.Short(slot.OpaqueIdentityHash),
            slot.ErrorCode);
}
