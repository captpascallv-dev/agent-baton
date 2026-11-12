using CodexUsageHud.Core;
using System.Text.Json;

static string? Option(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    if (i < 0) return null;
    if (i + 1 >= args.Length || args[i + 1].StartsWith("--")) throw new ArgumentException("Missing option value");
    return args[i + 1];
}
static void ValidateArguments(string[] args)
{
    var flags = new HashSet<string> { "--self-test", "--demo", "--live", "--watch", "--once" };
    var options = new HashSet<string> { "--fixture", "--output-dir", "--codex" };
    var seen = new HashSet<string>();
    for (var i = 0; i < args.Length; i++)
    {
        var name = args[i];
        if (!flags.Contains(name) && !options.Contains(name)) throw new ArgumentException("Unknown option");
        if (!seen.Add(name)) throw new ArgumentException("Duplicate option");
        if (options.Contains(name))
        {
            if (++i >= args.Length || args[i].StartsWith("--") || string.IsNullOrWhiteSpace(args[i]))
                throw new ArgumentException("Missing option value");
        }
    }
    if (seen.Contains("--self-test"))
    {
        if (seen.Count != 1) throw new ArgumentException("--self-test must be used alone");
        return;
    }
    if (seen.Contains("--watch") && seen.Contains("--once")) throw new ArgumentException("--watch and --once are mutually exclusive");
    if (new[] { "--demo", "--live", "--fixture" }.Count(seen.Contains) != 1)
        throw new ArgumentException("Select exactly one of --demo, --fixture FILE or explicitly authorized --live");
    if (seen.Contains("--codex") && !seen.Contains("--live")) throw new ArgumentException("--codex requires --live");
    if (!seen.Contains("--live") && !seen.Contains("--output-dir"))
        throw new ArgumentException("Demo and fixtures require --output-dir to avoid overwriting real quota");
}
static ProviderQuotaBoard Board(IReadOnlyList<QuotaWindowObservation> windows, DateTimeOffset now, bool stale = false)
{
    stale = stale || windows.Any(window => window.ResetsAtUtc is { } reset && reset <= now);
    var slots = ProviderSlotIds.All.Select(id => new ProviderSlotSnapshot(id,
        id == ProviderSlotIds.CodexPrimary ? ProviderIds.Codex : id, id,
        id == ProviderSlotIds.CodexPrimary, false,
        id != ProviderSlotIds.CodexPrimary ? QuotaSlotStatus.Disabled : windows.Count == 0 ? QuotaSlotStatus.Unavailable : stale ? QuotaSlotStatus.Stale : QuotaSlotStatus.Live,
        "", now, "", id == ProviderSlotIds.CodexPrimary ? windows : Array.Empty<QuotaWindowObservation>())).ToArray();
    return new ProviderQuotaBoard(slots, now);
}
static byte[] Snapshot(ProviderQuotaBoard board, bool demo) => QuotaSnapshotJson.Serialize(
    QuotaSnapshotMapper.MapSlots(board), board.GeneratedAtUtc, HudProduct.Version + (demo ? "-demo" : ""));
static async Task WriteSnapshotAsync(string directory, byte[] bytes, CancellationToken cancellationToken = default)
{
    var temp = Path.Combine(directory, ".quota-" + Guid.NewGuid().ToString("N") + ".tmp");
    try
    {
        await File.WriteAllBytesAsync(temp, bytes, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        File.Move(temp, Path.Combine(directory, "quota-snapshot.json"), true);
    }
    finally { if (File.Exists(temp)) File.Delete(temp); }
}
static async Task SelfTest()
{
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("Self-test assertion: " + name);
    }
    foreach (var valid in new[] {
        new[] { "--self-test" }, new[] { "--demo", "--output-dir", "scratch", "--once" },
        new[] { "--fixture", "fixture.json", "--output-dir", "scratch", "--watch" },
        new[] { "--live", "--codex", "/trusted/codex" } }) ValidateArguments(valid);
    foreach (var invalid in new[] {
        Array.Empty<string>(), new[] { "--unknown" }, new[] { "--live", "--live" },
        new[] { "--live", "--once", "--watch" }, new[] { "--self-test", "--live" },
        new[] { "--live", "--demo" }, new[] { "--fixture" },
        new[] { "--demo", "--output-dir", "scratch", "--codex", "anything" },
        new[] { "--demo" } })
    {
        bool rejected = false;
        try { ValidateArguments(invalid); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "invalid arguments rejected before collection");
    }
    var now = new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);
    const string sentinel = "PRIVACY_SENTINEL_7d8f";
    var input = JsonSerializer.Serialize(new
    {
        account = sentinel, email = sentinel + "@example.invalid", token = sentinel, path = "/" + sentinel,
        rateLimits = new { primary = new { usedPercent = 25, windowDurationMins = 300, resetsAt = 2000000000 } },
        unknown_pool = new { name = sentinel }
    });
    var observation = QuotaJsonParser.Parse(input, now);
    var windows = ProviderQuotaPresentation.FromCodex(observation);
    Check(windows.Count > 0 && windows[0].RemainingPercent == 75, "parser");
    // Inject private fields at the mapping boundary as well as the parser boundary.
    var board = Board(windows, now);
    var contaminated = board.Slots.Select(s => s with
    {
        Label = sentinel, StatusText = sentinel, SourceDescription = sentinel,
        OpaqueIdentityHash = sentinel, AccountKey = sentinel, ConfigFingerprint = sentinel,
        ErrorCode = sentinel, FieldPresenceFlags = sentinel, DelayText = sentinel,
        Windows = s.Windows.Concat(new[] { new QuotaWindowObservation(sentinel, sentinel, 12, now, 123, true) }).ToArray()
    }).ToArray();
    var bytes = Snapshot(new ProviderQuotaBoard(contaminated, now), true);
    using var doc = JsonDocument.Parse(bytes);
    var root = doc.RootElement;
    Check(root.GetProperty("schema").GetInt32() == 1, "schema1");
    Check(root.GetProperty("written_at_utc").GetDateTimeOffset() == now, "timestamp");
    var slots = root.GetProperty("slots").EnumerateArray().ToArray();
    Check(slots.Length == 6 && slots.Skip(1).All(s => s.GetProperty("state").GetString() == "disabled" && s.GetProperty("windows").GetArrayLength() == 0), "disabled slots");
    Check(slots[0].GetProperty("windows").GetArrayLength() == windows.Count, "unknown pool excluded");
    Check(slots[0].GetProperty("windows")[0].GetProperty("remaining_percent").GetDouble() == 75, "mapper");
    Check(!root.GetRawText().Contains(sentinel, StringComparison.Ordinal), "private sentinel excluded");
    Check(root.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] { "hud_version", "schema", "slots", "written_at_utc" }), "root allowlist");
    foreach (var reset in new[] { now.AddSeconds(-1), now })
    {
        var expired = new[] { new QuotaWindowObservation("primary", "5-hour", 25, reset, 300, true) };
        using var expiredDoc = JsonDocument.Parse(Snapshot(Board(expired, now), true));
        Check(expiredDoc.RootElement.GetProperty("slots")[0].GetProperty("state").GetString() == "stale", "expired reset is stale");
    }
    Check(slots[0].GetProperty("state").GetString() == "ok", "future reset stays ok");
    var nullable = new[] { new QuotaWindowObservation("primary", "5-hour", null, null, 300, true) };
    using var nullDoc = JsonDocument.Parse(Snapshot(Board(nullable, now), true));
    var nullWindow = nullDoc.RootElement.GetProperty("slots")[0].GetProperty("windows")[0];
    Check(nullWindow.GetProperty("remaining_percent").ValueKind == JsonValueKind.Null && nullWindow.GetProperty("resets_at_utc").ValueKind == JsonValueKind.Null, "missing values are null not zero");
    foreach (var missingInput in new[] { "{}", "{invalid-json" })
    {
        var missingWindows = ProviderQuotaPresentation.FromCodex(QuotaJsonParser.Parse(missingInput, now));
        Check(missingWindows.Count == 0, "invalid/missing input has no windows");
        using var missing = JsonDocument.Parse(Snapshot(Board(missingWindows, now), false));
        var slot = missing.RootElement.GetProperty("slots")[0];
        Check(slot.GetProperty("state").GetString() == "unavailable" && slot.GetProperty("windows").GetArrayLength() == 0, "unavailable");
    }
    // Exercise the production replacement helper only inside a newly owned temporary directory.
    var directory = Path.Combine(Path.GetTempPath(), "hud-self-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        await WriteSnapshotAsync(directory, bytes);
        var replacement = Snapshot(Board(nullable, now.AddMinutes(1)), true);
        await WriteSnapshotAsync(directory, replacement);
        var path = Path.Combine(directory, "quota-snapshot.json");
        Check((await File.ReadAllBytesAsync(path)).SequenceEqual(replacement), "atomic replacement bytes");
        using var written = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
        Check(written.RootElement.GetProperty("schema").GetInt32() == 1, "replacement schema compatibility");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await WriteSnapshotAsync(directory, bytes, cancelled.Token); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { }
        Check((await File.ReadAllBytesAsync(path)).SequenceEqual(replacement), "cancel leaves previous snapshot intact");
        Check(Directory.GetFiles(directory).Length == 1, "temporary files cleaned");
    }
    finally { Directory.Delete(directory, true); }
    Console.WriteLine("PASS arguments, expired reset, parser, mapper, schema1, disabled slots, privacy sentinel, unknown pool, null values, invalid JSON, unavailable, atomic replacement/cancellation");
}

try
{
    ValidateArguments(args);
    if (args.Contains("--self-test")) { await SelfTest(); return 0; }
    var demo = args.Contains("--demo"); var live = args.Contains("--live"); var fixture = Option(args, "--fixture");
    if ((demo ? 1 : 0) + (live ? 1 : 0) + (fixture is null ? 0 : 1) != 1)
        throw new ArgumentException("Select exactly one of --demo, --fixture FILE or explicitly authorized --live");
    var directory = Option(args, "--output-dir") ?? (OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "CodexUsageHUD")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageHUD"));
    if (!live && Option(args, "--output-dir") is null) throw new ArgumentException("Demo and fixtures require --output-dir to avoid overwriting real quota");
    var executable = Option(args, "--codex");
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    Directory.CreateDirectory(directory);
    do
    {
        var now = DateTimeOffset.UtcNow;
        IReadOnlyList<QuotaWindowObservation> windows;
        bool stale = false;
        if (demo) windows = new[] {new QuotaWindowObservation("primary", "5-hour", 27, now.AddHours(3), 300, true), new QuotaWindowObservation("secondary", "Weekly", 42, now.AddDays(4), 10080, true)};
        else
        {
            QuotaObservation observation;
            if (fixture is not null) observation = QuotaJsonParser.Parse(await File.ReadAllTextAsync(fixture, stop.Token), now);
            else
            {
                var exe = new CodexExecutableDiscovery().Find(executable);
                observation = exe is null ? QuotaJsonParser.Parse("{}", now) : (await new AppServerClient().ReadRateLimitsAsync(exe, stop.Token)).Observation;
            }
            windows = ProviderQuotaPresentation.FromCodex(observation); stale = observation.IsStale;
        }
        var bytes = Snapshot(Board(windows, now, stale), !live);
        await WriteSnapshotAsync(directory, bytes, stop.Token);
        using var json = JsonDocument.Parse(bytes);
        Console.WriteLine(JsonSerializer.Serialize(json.RootElement));
        if (!args.Contains("--watch")) break;
        await Task.Delay(TimeSpan.FromSeconds(60), stop.Token);
    } while (!stop.IsCancellationRequested);
    return 0;
}
catch (OperationCanceledException) { return 0; }
catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 2; }
catch { Console.Error.WriteLine("HUD read/write failed; no credentials or private paths emitted."); return 1; }
