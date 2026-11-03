using System.Globalization;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace CodexUsageHud.Core;

public static class HudReleaseVersion
{
    public static string FromEntryAssembly() =>
        FromAssembly(Assembly.GetEntryAssembly() ?? typeof(HudReleaseVersion).Assembly);

    public static string FromAssembly(Assembly assembly)
    {
        var version = assembly.GetName().Version;
        if (version is null) return "0.0.0";
        var build = version.Build < 0 ? 0 : version.Build;
        return string.Create(CultureInfo.InvariantCulture, $"{version.Major}.{version.Minor}.{build}");
    }
}

public sealed record QuotaSnapshotWindow(
    string Pool,
    int? WindowMinutes,
    double? RemainingPercent,
    DateTimeOffset? ResetsAtUtc,
    DateTimeOffset? ObservedAtUtc);

public sealed record QuotaSnapshotSlot(
    string Slot,
    string Label,
    string State,
    IReadOnlyList<QuotaSnapshotWindow> Windows);

public static class QuotaSnapshotMapper
{
    public const string StateOk = "ok";
    public const string StateStale = "stale";
    public const string StateUnavailable = "unavailable";
    public const string StateDisabled = "disabled";

    private static readonly (string SlotId, string PublicId, string Label)[] FixedSlots =
    {
        (ProviderSlotIds.CodexPrimary, "codex_primary", "Codex主"),
        (ProviderSlotIds.CodexSecondary, "codex_secondary", "Codex备"),
        (ProviderSlotIds.Cursor, "cursor", "Cursor"),
        (ProviderSlotIds.Grok, "grok", "Grok"),
        (ProviderSlotIds.GrokBot, "grok_bot", "Bot"),
        (ProviderSlotIds.Claude, "claude", "Claude"),
    };

    public static IReadOnlyList<QuotaSnapshotSlot> MapSlots(ProviderQuotaBoard board)
    {
        var byId = new Dictionary<string, ProviderSlotSnapshot>(StringComparer.Ordinal);
        foreach (var slot in board.Slots)
        {
            if (!byId.ContainsKey(slot.SlotId))
                byId[slot.SlotId] = slot;
        }

        var mapped = new QuotaSnapshotSlot[FixedSlots.Length];
        for (var index = 0; index < FixedSlots.Length; index++)
        {
            var (slotId, publicId, label) = FixedSlots[index];
            mapped[index] = byId.TryGetValue(slotId, out var slot)
                ? MapSlot(slot, publicId, label)
                : new QuotaSnapshotSlot(publicId, label, StateUnavailable, Array.Empty<QuotaSnapshotWindow>());
        }

        return mapped;
    }

    private static QuotaSnapshotSlot MapSlot(ProviderSlotSnapshot slot, string publicId, string label)
    {
        var state = StateOf(slot);
        if (state is not (StateOk or StateStale))
            return new QuotaSnapshotSlot(publicId, label, state, Array.Empty<QuotaSnapshotWindow>());

        var observedAt = SuccessfulObservedAt(slot.ObservedAtUtc);
        var windows = new List<QuotaSnapshotWindow>(slot.Windows.Count);
        foreach (var window in slot.Windows)
        {
            if (!TryMapPool(slot.SlotId, window.WindowId, out var pool))
                continue;
            double? remaining = window.HasUsablePercent && window.RemainingPercent is { } percent &&
                                double.IsFinite(percent)
                ? percent
                : null;
            int? minutes = window.WindowDurationMinutes is > 0 ? window.WindowDurationMinutes : null;
            windows.Add(new QuotaSnapshotWindow(pool, minutes, remaining, window.ResetsAtUtc, observedAt));
        }

        return new QuotaSnapshotSlot(publicId, label, state, windows);
    }

    private static string StateOf(ProviderSlotSnapshot slot)
    {
        if (!slot.Enabled || slot.Status == QuotaSlotStatus.Disabled)
            return StateDisabled;
        // A retained delayed observation is the previous reading, never a new ok sample.
        if (slot.UpdateDelayed || slot.Status == QuotaSlotStatus.Stale)
            return StateStale;
        if (slot.Status == QuotaSlotStatus.Live)
            return StateOk;
        return StateUnavailable;
    }

    private static DateTimeOffset? SuccessfulObservedAt(DateTimeOffset observedAt)
    {
        if (observedAt == default || observedAt.Year < 2000) return null;
        return observedAt.ToUniversalTime();
    }

    public static bool TryMapPool(string slotId, string windowId, out string pool)
    {
        pool = string.Empty;
        if (slotId is ProviderSlotIds.CodexPrimary or ProviderSlotIds.CodexSecondary)
            return TryCodexPool(windowId, out pool);
        if (slotId == ProviderSlotIds.Cursor)
            return TryCursorPool(windowId, out pool);
        if (slotId == ProviderSlotIds.Grok && windowId == "weekly")
            return Assign("grok_weekly", out pool);
        if (slotId == ProviderSlotIds.GrokBot && windowId == SharedWeeklyQuota.WindowId)
            return Assign(SharedWeeklyQuota.PoolId, out pool);
        if (slotId == ProviderSlotIds.GrokBot && windowId == "sand")
            return Assign("grok_bot", out pool);
        if (slotId == ProviderSlotIds.Claude)
            return TryClaudePool(windowId, out pool);
        return false;
    }

    private static bool TryCodexPool(string windowId, out string pool)
    {
        pool = windowId switch
        {
            "primary" or "primary_window" => "codex_primary_window",
            "secondary" or "secondary_window" => "codex_secondary_window",
            "weekly" => "codex_weekly",
            "codex" or "codex-main" or "codex_main" => "codex",
            "codex/secondary" or "codex-main/secondary" or "codex_main/secondary" => "codex_secondary_window",
            _ => string.Empty,
        };
        return pool.Length > 0;
    }

    private static bool TryCursorPool(string windowId, out string pool)
    {
        pool = windowId switch
        {
            "autoPercentUsed" => "cursor_auto",
            "apiPercentUsed" => "cursor_api",
            "团队共用" => "cursor_team",
            SharedWeeklyQuota.WindowId => SharedWeeklyQuota.PoolId,
            _ => string.Empty,
        };
        return pool.Length > 0;
    }

    private static bool TryClaudePool(string windowId, out string pool)
    {
        pool = string.Empty;
        if (windowId is "five_hour" or "seven_day")
            return Assign("claude_" + windowId, out pool);
        const string prefix = "seven_day_";
        if (!windowId.StartsWith(prefix, StringComparison.Ordinal) || windowId.Length is < 11 or > 40)
            return false;
        for (var index = prefix.Length; index < windowId.Length; index++)
        {
            var character = windowId[index];
            if (character is not '_' && !char.IsAsciiLetterOrDigit(character))
                return false;
        }

        return Assign("claude_" + windowId, out pool);
    }

    private static bool Assign(string value, out string pool)
    {
        pool = value;
        return true;
    }
}

public static class QuotaSnapshotJson
{
    public static byte[] Serialize(IReadOnlyList<QuotaSnapshotSlot> slots, DateTimeOffset writtenAtUtc, string hudVersion)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", QuotaSnapshotWriter.SchemaVersion);
            writer.WriteString("written_at_utc", FormatUtc(writtenAtUtc));
            writer.WriteString("hud_version", hudVersion);
            writer.WritePropertyName("slots");
            writer.WriteStartArray();
            foreach (var slot in slots)
            {
                writer.WriteStartObject();
                writer.WriteString("slot", slot.Slot);
                writer.WriteString("label", slot.Label);
                writer.WriteString("state", slot.State);
                writer.WritePropertyName("windows");
                writer.WriteStartArray();
                foreach (var window in slot.Windows)
                {
                    writer.WriteStartObject();
                    writer.WriteString("pool", window.Pool);
                    writer.WritePropertyName("window_minutes");
                    if (window.WindowMinutes is { } minutes) writer.WriteNumberValue(minutes);
                    else writer.WriteNullValue();
                    writer.WritePropertyName("remaining_percent");
                    if (window.RemainingPercent is { } remaining) writer.WriteNumberValue(remaining);
                    else writer.WriteNullValue();
                    writer.WritePropertyName("resets_at_utc");
                    if (window.ResetsAtUtc is { } resets) writer.WriteStringValue(FormatUtc(resets));
                    else writer.WriteNullValue();
                    writer.WritePropertyName("observed_at_utc");
                    if (window.ObservedAtUtc is { } observed) writer.WriteStringValue(FormatUtc(observed));
                    else writer.WriteNullValue();
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    public static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
}

public sealed class QuotaSnapshotWriter
{
    public const int SchemaVersion = 1;
    public const string FileName = "quota-snapshot.json";
    public const string TempFileName = FileName + ".tmp";
    public const string SettingKey = "quota_snapshot_export";

    private readonly string _directory;
    private readonly string _hudVersion;
    private readonly Action? _beforeCommit;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private readonly object _commitLock = new();
    private PendingSnapshot? _pending;
    private Task _tail = Task.CompletedTask;
    private int _epoch;
    private int _enabled;
    private bool _running;

    public QuotaSnapshotWriter(string directory, string hudVersion, Action? beforeCommit = null,
        Func<DateTimeOffset>? clock = null)
    {
        _directory = directory;
        _hudVersion = SanitizeVersion(hudVersion);
        _beforeCommit = beforeCommit;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public string FinalPath => Path.Combine(_directory, FileName);

    public static bool IsEnabledValue(string? value) =>
        string.Equals(value, "1", StringComparison.Ordinal);

    public void SetEnabled(bool enabled)
    {
        lock (_commitLock)
        {
            if (enabled)
            {
                _enabled = 1;
                return;
            }

            _enabled = 0;
            _epoch++;
        }

        lock (_gate)
            _pending = null;
    }

    public void Publish(ProviderQuotaBoard board)
    {
        if (Volatile.Read(ref _enabled) == 0) return;
        int epoch;
        lock (_commitLock)
        {
            if (_enabled == 0) return;
            epoch = _epoch;
        }

        IReadOnlyList<QuotaSnapshotSlot> slots;
        try
        {
            slots = QuotaSnapshotMapper.MapSlots(board);
        }
        catch (Exception)
        {
            return;
        }

        lock (_gate)
        {
            if (Volatile.Read(ref _enabled) == 0) return;
            _pending = new PendingSnapshot(slots, epoch);
            if (_running) return;
            _running = true;
            _tail = Task.Run(Drain);
        }
    }

    public async Task WhenIdleAsync()
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            Task tail;
            lock (_gate)
                tail = _tail;
            await tail.ConfigureAwait(false);
            lock (_gate)
            {
                if (!_running && _pending is null)
                    return;
            }

            await Task.Delay(2).ConfigureAwait(false);
        }
    }

    private void Drain()
    {
        while (true)
        {
            PendingSnapshot? pending;
            lock (_gate)
            {
                pending = _pending;
                _pending = null;
                if (pending is null || Volatile.Read(ref _enabled) == 0)
                {
                    _running = false;
                    return;
                }
            }

            try
            {
                WriteAtomically(pending);
            }
            catch (Exception)
            {
                // One failed snapshot must not stop the HUD or later snapshots.
            }
        }
    }

    private void WriteAtomically(PendingSnapshot pending)
    {
        string? tempPath = null;
        try
        {
            if (string.IsNullOrWhiteSpace(_directory)) return;
            Directory.CreateDirectory(_directory);
            var bytes = QuotaSnapshotJson.Serialize(pending.Slots, _clock(), _hudVersion);
            // One owned temp per directory. An interrupted write is overwritten here, not renamed aside.
            tempPath = Path.Combine(_directory, TempFileName);
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }

            if (Volatile.Read(ref _enabled) == 0) return;
            _beforeCommit?.Invoke();
            // OFF waits on this lock, so a replace cannot finish after OFF has returned.
            lock (_commitLock)
            {
                if (_enabled == 0 || _epoch != pending.Epoch)
                    return;
                var finalPath = Path.Combine(_directory, FileName);
                if (File.Exists(finalPath))
                    File.Replace(tempPath, finalPath, null);
                else
                    File.Move(tempPath, finalPath);
                tempPath = null;
            }
        }
        finally
        {
            if (tempPath is not null)
                TryDelete(tempPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // A leftover temp is not the snapshot readers open.
        }
    }

    private static string SanitizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "0.0.0";
        var trimmed = version.Trim();
        if (trimmed.Length is 0 or > 32) return "0.0.0";
        foreach (var character in trimmed)
        {
            if (character is not ('.' or '+' or '-') && !char.IsAsciiLetterOrDigit(character))
                return "0.0.0";
        }

        return trimmed;
    }

    private sealed record PendingSnapshot(IReadOnlyList<QuotaSnapshotSlot> Slots, int Epoch);
}
