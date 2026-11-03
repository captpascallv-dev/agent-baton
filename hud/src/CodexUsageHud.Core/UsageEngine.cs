using System.Diagnostics;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace CodexUsageHud.Core;

public sealed class UsageEngine : IDisposable
{
    private const int MaxFilesPerPass = 24;
    private const int MaxHotFiles = 12;
    private static readonly TimeSpan PassTimeBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HotFileBudget = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RecentFileWindow = TimeSpan.FromMinutes(30);

    private readonly UsageDatabase _database;
    private readonly string _codexHome;
    private readonly RolloutIndexer _indexer;
    private readonly StateMetadataReader _stateReader = new();
    private readonly SessionIndexReader _indexReader = new();
    private readonly CodexExecutableDiscovery _executableDiscovery = new();
    // A normal primary quota read can approach five seconds. Leave enough time
    // for the authenticated read to finish before invalidating the account pair.
    private readonly AppServerClient _appServerClient = new(HudProduct.Version,
        requestTimeout: TimeSpan.FromSeconds(15));
    private readonly ProviderQuotaCoordinator _providers;
    private readonly QuotaStateMachine _quotaState;
    private readonly RefreshCadence _cadence = new();
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, long> _signaledPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _continuationPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly AppIoQueue _appIo;
    private readonly QuotaSnapshotWriter _quotaSnapshots;
    private IReadOnlyList<RolloutFile> _files = Array.Empty<RolloutFile>();
    private Dictionary<string, ThreadMetadataRow> _metadataByPath = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ThreadMetadataRow> _metadataRows = Array.Empty<ThreadMetadataRow>();
    private IdentityColumnPresence _identityColumns = IdentityColumnPresence.Missing;
    private QuotaObservation _quota;
    private ProviderQuotaBoard _providersBoard;
    private ProviderAccessSettings _providerSettings = ProviderAccessSettings.Default();
    private string? _primaryIdentityHash;
    private string? _primaryIdentityNamespace;
    private string? _ownerConfirmedIdentityStamp;
    private int _unverifiedHistoryCount;
    private int _foreignHistoryCount;
    private DateTimeOffset? _lastRolloutPassUtc;
    private int _fileCursor;
    private int _forceDiscovery;
    private long _signalSequence;
    private bool _indexing;
    private bool _disposed;

    public UsageEngine(string codexHome, string databasePath, string? logPath = null,
        Action<string>? appIoBeforeOperation = null, ProviderQuotaCoordinator? providers = null,
        QuotaSnapshotWriter? quotaSnapshots = null, string? productVersion = null)
    {
        _codexHome = CodexHomeResolver.Resolve(codexHome);
        _database = new UsageDatabase(databasePath);
        _indexer = new RolloutIndexer(_database);
        _appIo = new AppIoQueue(_database, _indexer.SetPinnedThread,
            string.IsNullOrWhiteSpace(logPath) ? null : new PrivacyLog(logPath),
            beforeOperation: appIoBeforeOperation);
        _quotaState = new QuotaStateMachine(_database);
        _quota = _quotaState.RestoreForDisplay();
        _providers = providers ?? new ProviderQuotaCoordinator();
        var dataDirectory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        _quotaSnapshots = quotaSnapshots ?? new QuotaSnapshotWriter(
            string.IsNullOrEmpty(dataDirectory) ? Path.GetFullPath(".") : dataDirectory,
            string.IsNullOrWhiteSpace(productVersion) ? HudReleaseVersion.FromEntryAssembly() : productVersion);
        _providers.SlotPublished += OnProviderSlotPublished;
        _providersBoard = _providers.CurrentBoard(DateTimeOffset.UtcNow, _providerSettings);
        InitializeWatchers();
    }

    public event Action? ProvidersUpdated;

    public UsageDatabase Database => _database;
    public string BoundCodexHome => _codexHome;
    public RefreshCadence Cadence => _cadence;
    public IsolatedQuotaCache ProviderCache => _providers.Cache;

    public bool IsIndexing
    {
        get { lock (_gate) return _indexing; }
    }

    public Task<HudFrame> RefreshAsync(bool manualQuota = false,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.Run(async () =>
        {
            var errors = new List<string>();
            try
            {
                var aggregatesReady = _database.IsAggregateRebuildComplete ||
                                      _database.RunAggregateRebuildBatch();
                if (aggregatesReady)
                {
                    RefreshMetadataAndRollouts(errors, cancellationToken);
                }
                else
                {
                    lock (_gate) _indexing = true;
                }

                var now = DateTimeOffset.UtcNow;
                if (_cadence.IsQuotaDue(now, manualQuota))
                {
                    _cadence.MarkQuotaAttempt(now);
                    await RefreshQuotaAsync(errors, cancellationToken, manualQuota).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                errors.Add("engine_refresh");
            }

            return HudPresentation.BuildFrame(BuildSnapshot(errors));
        }, cancellationToken);
    }

    public HudSnapshot GetSnapshot() => BuildSnapshot(Array.Empty<string>());
    public HudFrame GetFrame() => HudPresentation.BuildFrame(GetSnapshot());
    public Task<IReadOnlyDictionary<string, string?>> LoadSettingsAsync(params string[] keys) =>
        _appIo.LoadSettingsAsync(keys);
    public Task SaveSettingsAsync(IReadOnlyDictionary<string, string> values) =>
        _appIo.SaveSettingsAsync(values);
    public Task SetPinnedThreadAsync(string? threadId) => _appIo.SetPinnedThreadAsync(threadId);
    public Task SetSessionDriftAssessmentAsync(string threadId, DriftAssessmentLevel level) =>
        _appIo.SetSessionDriftAssessmentAsync(threadId, level, DateTimeOffset.UtcNow);
    public Task WriteLogCodeAsync(string code, string? relativePath = null, long? offset = null) =>
        _appIo.WriteLogCodeAsync(code, relativePath, offset);
    public void SetQuotaSnapshotExportEnabled(bool enabled) => _quotaSnapshots.SetEnabled(enabled);

    public void ExportCurrentQuotaSnapshot() => TryExportQuotaSnapshot(_providersBoard);

    public Task FlushQuotaSnapshotAsync() => _quotaSnapshots.WhenIdleAsync();

    public Task StopAcceptingAppIoAsync() => _appIo.StopAcceptingAsync();
    public Task CompleteAppIoAsync(IReadOnlyDictionary<string, string> finalSettings) =>
        _appIo.CompleteAsync(finalSettings);

    private void RefreshMetadataAndRollouts(List<string> errors, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var forceDiscovery = Interlocked.Exchange(ref _forceDiscovery, 0) != 0;
        if (forceDiscovery || _cadence.IsMetadataDue(now))
        {
            var names = _indexReader.Read(_codexHome);
            var rows = _stateReader.Read(_codexHome);
            var metadataByPath = new Dictionary<string, ThreadMetadataRow>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _indexer.MergeMetadata(SessionMetadataMapper.ToSession(row, names));
                if (!string.IsNullOrWhiteSpace(row.RolloutRelativePath))
                {
                    metadataByPath[row.RolloutRelativePath!] = row;
                }
            }

            _metadataByPath = metadataByPath;
            _metadataRows = rows;
            _identityColumns = _stateReader.LastIdentityColumns;
            _files = new RolloutDiscovery().Discover(_codexHome);
            _fileCursor = _files.Count == 0 ? 0 : _fileCursor % _files.Count;
            var discoveredPaths = _files.Select(item => item.RelativePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var discoveredFullPaths = _files.Select(item => item.FullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _pendingPaths.RemoveWhere(path => !discoveredPaths.Contains(path));
            _continuationPaths.RemoveWhere(path => !discoveredPaths.Contains(path));
            foreach (var signaled in _signaledPaths.Keys.Where(path => !discoveredFullPaths.Contains(path)).ToArray())
            {
                _signaledPaths.TryRemove(signaled, out _);
            }
            foreach (var file in _files)
            {
                var knownThread = KnownThread(file);
                if (_indexer.IsSourceCurrent(file, knownThread))
                {
                    _pendingPaths.Remove(file.RelativePath);
                }
                else
                {
                    _pendingPaths.Add(file.RelativePath);
                }
            }
            _cadence.MarkMetadataRefresh(now);
        }

        if (_files.Count == 0)
        {
            lock (_gate) _indexing = false;
            _lastRolloutPassUtc = now;
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hot = PriorityFiles(now).ToArray();
        foreach (var file in hot)
        {
            if (stopwatch.Elapsed >= HotFileBudget) break;
            ScanOne(file, errors, visited, cancellationToken);
        }

        var hotContinuations = hot.Where(file => _continuationPaths.Contains(file.RelativePath)).ToArray();
        var continuationCursor = 0;
        while (hotContinuations.Length > 0 && stopwatch.Elapsed < HotFileBudget)
        {
            var file = hotContinuations[continuationCursor % hotContinuations.Length];
            continuationCursor++;
            ScanOne(file, errors, visited: null, cancellationToken);
            hotContinuations = hotContinuations.Where(item => _continuationPaths.Contains(item.RelativePath)).ToArray();
        }

        var scannedCold = 0;
        var examined = 0;
        while (scannedCold < Math.Min(MaxFilesPerPass, _files.Count) && examined < _files.Count &&
               stopwatch.Elapsed < PassTimeBudget)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = _files[_fileCursor];
            _fileCursor = (_fileCursor + 1) % _files.Count;
            examined++;
            if (visited.Contains(file.FullPath))
            {
                continue;
            }
            ScanOne(file, errors, visited, cancellationToken);
            scannedCold++;
        }

        lock (_gate)
        {
            _indexing = _pendingPaths.Count > 0 || _continuationPaths.Count > 0;
        }
        _lastRolloutPassUtc = DateTimeOffset.UtcNow;
    }

    private void ScanOne(RolloutFile file, List<string> errors, HashSet<string>? visited,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        visited?.Add(file.FullPath);
        _signaledPaths.TryGetValue(file.FullPath, out var signalVersion);
        try
        {
            var scan = _indexer.ScanFile(file.FullPath, file.RelativePath, KnownThread(file), cancellationToken);
            errors.AddRange(scan.ErrorCodes);
            if (scan.HasMoreData)
            {
                _continuationPaths.Add(file.RelativePath);
                _pendingPaths.Add(file.RelativePath);
            }
            else
            {
                _continuationPaths.Remove(file.RelativePath);
                _pendingPaths.Remove(file.RelativePath);
            }

            if (signalVersion != 0 && _signaledPaths.TryGetValue(file.FullPath, out var currentVersion) &&
                currentVersion == signalVersion)
            {
                _signaledPaths.TryRemove(file.FullPath, out _);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException)
        {
            errors.Add("source_io");
            _pendingPaths.Add(file.RelativePath);
        }
        catch (UnauthorizedAccessException)
        {
            errors.Add("source_access_denied");
            _pendingPaths.Add(file.RelativePath);
        }
    }

    private IEnumerable<RolloutFile> PriorityFiles(DateTimeOffset now)
    {
        var signaled = _signaledPaths.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _files.Where(file => signaled.Contains(file.FullPath))
            .Concat(_files.Where(file => _continuationPaths.Contains(file.RelativePath)))
            .Concat(_files.Where(file => now - file.LastWriteTimeUtc <= RecentFileWindow))
            .Concat(_files.Take(1))
            .DistinctBy(file => file.FullPath, StringComparer.OrdinalIgnoreCase)
            .Take(MaxHotFiles);
    }

    private string KnownThread(RolloutFile file) =>
        _metadataByPath.TryGetValue(file.RelativePath, out var row) ? row.ThreadId : "unknown-thread";

    private async Task RefreshQuotaAsync(List<string> errors, CancellationToken cancellationToken,
        bool manualQuota)
    {
        var executable = _executableDiscovery.Find();
        if (string.IsNullOrWhiteSpace(executable))
        {
            errors.Add("codex_executable_missing");
            _quotaState.InvalidateAssociation();
            _quota = _quotaState.Observe(new QuotaObservation(null, Array.Empty<QuotaBucket>(),
                QuotaSource.Unavailable, DateTimeOffset.UtcNow, false, "codex_executable_missing"), null);
            _primaryIdentityHash = null;
            _primaryIdentityNamespace = null;
            await RememberOwnerConfirmedStampAsync(null).ConfigureAwait(false);
            await RefreshProviderBoardAsync(manualQuota, cancellationToken).ConfigureAwait(false);
            return;
        }

        var isolatedHome = IsolatedCodexHome();
        var combined = await _appServerClient.ReadQuotaAndIdentityAsync(executable, cancellationToken, isolatedHome)
            .ConfigureAwait(false);
        var primaryIdentity = combined.Identity?.Hash;
        if (!string.Equals(primaryIdentity, _primaryIdentityHash, StringComparison.Ordinal))
            _quotaState.InvalidateAssociation();
        _quota = _quotaState.Observe(combined.Quota.Observation, primaryIdentity);
        _primaryIdentityHash = string.IsNullOrWhiteSpace(primaryIdentity) ? null : primaryIdentity;
        _primaryIdentityNamespace = combined.Identity?.Namespace;
        await RememberOwnerConfirmedStampAsync(_primaryIdentityHash).ConfigureAwait(false);
        if (combined.Quota.Observation.Primary is not null)
        {
            var catalog = await _appServerClient.ReadModelCatalogAsync(executable, cancellationToken, isolatedHome)
                .ConfigureAwait(false);
            if (catalog is not null)
            {
                foreach (var session in _database.LoadSessions())
                {
                    var resolved = ServiceTierResolver.Resolve(null, session.Model, catalog);
                    _database.ApplyCatalogTier(session.ThreadId, resolved);
                }
            }
        }

        if (combined.Quota.ErrorCode is not null)
        {
            errors.Add(combined.Quota.ErrorCode);
        }

        await RefreshProviderBoardAsync(manualQuota, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProviderQuotaBoard> RefreshProviderBoardAsync(bool manual,
        CancellationToken cancellationToken)
    {
        try
        {
            var keys = new List<string>
            {
                ProviderSettingKeys.CompactLayout, ProviderSettingKeys.AccessNotice,
            };
            foreach (var slotId in ProviderSlotIds.All)
            {
                keys.Add(ProviderSettingKeys.Label(slotId));
                keys.Add(ProviderSettingKeys.Enabled(slotId));
                keys.Add(ProviderSettingKeys.CodexHome(slotId));
            }

            var stored = await LoadSettingsAsync(keys.ToArray()).ConfigureAwait(false);
            _providerSettings = ProviderSettingKeys.FromStored(stored);
            await _providers.RefreshAsync(_providerSettings, _quota, cancellationToken, manual,
                _primaryIdentityHash, _codexHome)
                .ConfigureAwait(false);
            _providersBoard = _providers.CurrentBoard(DateTimeOffset.UtcNow, _providerSettings);
            TryExportQuotaSnapshot(_providersBoard);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            _providersBoard = _providers.CurrentBoard(DateTimeOffset.UtcNow, _providerSettings);
            TryExportQuotaSnapshot(_providersBoard);
        }

        return _providersBoard;
    }

    public ProviderAccessSettings ProviderSettings => _providerSettings;

    public ProviderQuotaBoard GetProviderBoard()
    {
        var board = _providers.CurrentBoard(DateTimeOffset.UtcNow, _providerSettings);
        _providersBoard = board;
        return AnnotatePrimaryHistory(board, _unverifiedHistoryCount, _foreignHistoryCount);
    }

    private void OnProviderSlotPublished(ProviderSlotSnapshot _)
    {
        _providersBoard = _providers.CurrentBoard(DateTimeOffset.UtcNow, _providerSettings);
        TryExportQuotaSnapshot(_providersBoard);
        ProvidersUpdated?.Invoke();
    }

    private void TryExportQuotaSnapshot(ProviderQuotaBoard board)
    {
        try
        {
            _quotaSnapshots.Publish(board);
        }
        catch (Exception)
        {
            // Export failures stay off the quota refresh path.
        }
    }

    private HudSnapshot BuildSnapshot(IReadOnlyList<string> refreshErrors)
    {
        var now = DateTimeOffset.UtcNow;
        var aggregateMigrationPending = !_database.IsAggregateRebuildComplete;
        var localSessions = aggregateMigrationPending
            ? Array.Empty<SessionAggregate>()
            : _indexer.LoadAggregates(now);
        var partition = PrimaryAccountAttribution.Partition(localSessions, _metadataRows, _primaryIdentityHash,
            _identityColumns, _primaryIdentityNamespace, _ownerConfirmedIdentityStamp);
        _unverifiedHistoryCount = partition.Unverified.Count;
        _foreignHistoryCount = partition.Foreign.Count;
        var sessions = partition.Analysis;
        var primaryQuota = _quota.Primary is { HasValidWindow: true } bucket ? bucket : null;
        var hasCurrentWindow = primaryQuota is not null && primaryQuota.CycleStartUtc <= now &&
                               primaryQuota.ResetsAtUtc > now && !aggregateMigrationPending;
        CanonicalTokenUsage? cycle = !hasCurrentWindow
            ? null
            : _database.GetCycleTotal(primaryQuota!.CycleStartUtc, primaryQuota.ResetsAtUtc);
        var runningThreadIds = localSessions.Where(item => item.Status == SessionStatus.Running)
            .Select(item => item.Metadata.ThreadId).ToArray();
        CanonicalTokenUsage? runningCycle = !hasCurrentWindow
            ? null
            : _database.GetCycleTotalForThreads(primaryQuota!.CycleStartUtc, primaryQuota.ResetsAtUtc,
                runningThreadIds);
        var errors = refreshErrors.Concat(new[] { _quota.ErrorCode ?? string.Empty })
            .Where(error => !string.IsNullOrWhiteSpace(error)).Distinct(StringComparer.Ordinal).ToArray();
        var sourceAge = _lastRolloutPassUtc.HasValue ? now - _lastRolloutPassUtc.Value : (TimeSpan?)null;
        var freshness = aggregateMigrationPending
            ? "索引中（统计迁移）"
            : primaryQuota is null
            ? "额度不可用；本周期不可用"
            : _quota.IsStale ? "本机最后观测/陈旧"
                : sourceAge.HasValue ? $"官方额度 · 本地索引 {Math.Max(0, sourceAge.Value.TotalSeconds):0} 秒前" : "官方 App Server 观测";
        var providers = AnnotatePrimaryHistory(
            _providers.CurrentBoard(now, _providerSettings), partition.Unverified.Count, partition.Foreign.Count);
        return new HudSnapshot(_quota, sessions, cycle, now, IsIndexing, freshness, errors,
            _database.LoadRecentEvents(), aggregateMigrationPending, runningCycle, providers, partition.Note,
            localSessions.Count, runningThreadIds.Length, partition.MachineBound.Count, partition.Unverified.Count,
            partition.Foreign.Count, partition.State, partition.Unverified);
    }

    private static ProviderQuotaBoard AnnotatePrimaryHistory(ProviderQuotaBoard board, int unverified, int foreign)
    {
        if (unverified == 0 && foreign == 0) return board;
        var slots = board.Slots.Select(slot =>
            slot.SlotId == ProviderSlotIds.CodexPrimary
                ? slot with { UnverifiedHistoryCount = unverified, ForeignHistoryCount = foreign }
                : slot).ToArray();
        return board with { Slots = slots };
    }

    private async Task RememberOwnerConfirmedStampAsync(string? currentHash)
    {
        if (_ownerConfirmedIdentityStamp is null)
        {
            var stored = await LoadSettingsAsync(ProviderSettingKeys.OwnerConfirmedPrimaryStamp)
                .ConfigureAwait(false);
            _ownerConfirmedIdentityStamp = stored.GetValueOrDefault(ProviderSettingKeys.OwnerConfirmedPrimaryStamp) ??
                                           string.Empty;
        }

        if (string.IsNullOrWhiteSpace(_ownerConfirmedIdentityStamp) && !string.IsNullOrWhiteSpace(currentHash))
        {
            _ownerConfirmedIdentityStamp = currentHash;
            await SaveSettingsAsync(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ProviderSettingKeys.OwnerConfirmedPrimaryStamp] = currentHash,
            }).ConfigureAwait(false);
        }
    }

    private IReadOnlyDictionary<string, string> IsolatedCodexHome() =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CODEX_HOME"] = Path.GetFullPath(_codexHome),
        };

    private void InitializeWatchers()
    {
        foreach (var folder in new[] { "sessions", "archived_sessions" })
        {
            var directory = Path.Combine(_codexHome, folder);
            if (!Directory.Exists(directory)) continue;
            try
            {
                var watcher = new FileSystemWatcher(directory, "*.jsonl")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size |
                                   NotifyFilters.CreationTime,
                    InternalBufferSize = 16 * 1024,
                };
                watcher.Changed += (_, args) => SignalPath(args.FullPath, false);
                watcher.Created += (_, args) => SignalPath(args.FullPath, true);
                watcher.Deleted += (_, _) => Interlocked.Exchange(ref _forceDiscovery, 1);
                watcher.Renamed += (_, args) =>
                {
                    SignalPath(args.FullPath, true);
                    Interlocked.Exchange(ref _forceDiscovery, 1);
                };
                watcher.Error += (_, _) => Interlocked.Exchange(ref _forceDiscovery, 1);
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (ArgumentException)
            {
                // Periodic discovery remains the fallback.
            }
            catch (IOException)
            {
                // Periodic discovery remains the fallback.
            }
            catch (UnauthorizedAccessException)
            {
                // Periodic discovery remains the fallback.
            }
        }
    }

    private void SignalPath(string fullPath, bool forceDiscovery)
    {
        if (!string.Equals(Path.GetExtension(fullPath), ".jsonl", StringComparison.OrdinalIgnoreCase)) return;
        _signaledPaths[Path.GetFullPath(fullPath)] = Interlocked.Increment(ref _signalSequence);
        if (forceDiscovery) Interlocked.Exchange(ref _forceDiscovery, 1);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _providers.SlotPublished -= OnProviderSlotPublished;
        _appIo.DisposeAsync().AsTask().GetAwaiter().GetResult();
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();
        _database.Dispose();
    }
}

public sealed class AppIoQueue : IAsyncDisposable
{
    public const int DefaultCapacity = 64;

    private readonly UsageDatabase _database;
    private readonly Action<string?> _setPinnedThread;
    private readonly PrivacyLog? _log;
    private readonly Action<string>? _beforeOperation;
    private readonly Channel<WorkItem> _channel;
    private readonly SemaphoreSlim _admission = new(1, 1);
    private readonly Task _worker;
    private bool _accepting = true;
    private bool _completionStarted;
    private int _disposeStarted;
    private int _resourcesDisposed;
    private readonly TaskCompletionSource<bool> _disposeCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AppIoQueue(UsageDatabase database, Action<string?> setPinnedThread, PrivacyLog? log = null,
        int capacity = DefaultCapacity, Action<string>? beforeOperation = null)
    {
        _database = database;
        _setPinnedThread = setPinnedThread;
        _log = log;
        _beforeOperation = beforeOperation;
        Capacity = Math.Clamp(capacity, 1, 1024);
        _channel = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        _worker = Task.Run(ProcessAsync);
    }

    public int Capacity { get; }

    public Task<IReadOnlyDictionary<string, string?>> LoadSettingsAsync(IReadOnlyList<string> keys) =>
        EnqueueAsync("load-settings", () => (IReadOnlyDictionary<string, string?>)keys
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(key => key, key => _database.LoadSetting(key), StringComparer.Ordinal));

    public Task SaveSettingsAsync(IReadOnlyDictionary<string, string> values) =>
        EnqueueAsync("save-settings", () =>
        {
            foreach (var pair in values) _database.SaveSetting(pair.Key, pair.Value);
            return true;
        });

    public Task SetPinnedThreadAsync(string? threadId) => EnqueueAsync("set-pin", () =>
    {
        _setPinnedThread(threadId);
        return true;
    });

    public Task SetSessionDriftAssessmentAsync(string threadId, DriftAssessmentLevel level,
        DateTimeOffset observedAtUtc) => EnqueueAsync("set-drift", () =>
    {
        _database.SetSessionDriftAssessment(threadId, level, observedAtUtc);
        return true;
    });

    public Task WriteLogCodeAsync(string code, string? relativePath = null, long? offset = null) =>
        EnqueueAsync("write-log", () =>
        {
            _log?.WriteCode(code, relativePath, offset);
            return true;
        });

    public async Task StopAcceptingAsync()
    {
        await _admission.WaitAsync().ConfigureAwait(false);
        try
        {
            _accepting = false;
        }
        finally
        {
            _admission.Release();
        }
    }

    public async Task CompleteAsync(IReadOnlyDictionary<string, string> finalSettings)
    {
        Task? finalWrite = null;
        await _admission.WaitAsync().ConfigureAwait(false);
        try
        {
            _accepting = false;
            if (!_completionStarted)
            {
                _completionStarted = true;
                if (finalSettings.Count > 0)
                {
                    var item = WorkItem.Create("final-settings", () =>
                    {
                        foreach (var pair in finalSettings) _database.SaveSetting(pair.Key, pair.Value);
                        return true;
                    });
                    await _channel.Writer.WriteAsync(item).ConfigureAwait(false);
                    finalWrite = item.Completion.Task;
                }
                _channel.Writer.TryComplete();
            }
        }
        finally
        {
            _admission.Release();
        }

        Exception? failure = null;
        try
        {
            if (finalWrite is not null) await finalWrite.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        await _worker.ConfigureAwait(false);
        if (failure is not null) throw failure;
    }

    private async Task<T> EnqueueAsync<T>(string operation, Func<T> action)
    {
        await _admission.WaitAsync().ConfigureAwait(false);
        WorkItem item;
        try
        {
            if (!_accepting) throw new InvalidOperationException("app_io_closed");
            item = WorkItem.Create(operation, () => action());
            await _channel.Writer.WriteAsync(item).ConfigureAwait(false);
        }
        finally
        {
            _admission.Release();
        }
        return (T)(await item.Completion.Task.ConfigureAwait(false))!;
    }

    private async Task ProcessAsync()
    {
        await foreach (var item in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                _beforeOperation?.Invoke(item.Operation);
                item.Completion.TrySetResult(item.Action());
            }
            catch (Exception exception)
            {
                item.Completion.TrySetException(exception);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            await _disposeCompletion.Task.ConfigureAwait(false);
            return;
        }
        try
        {
            await CompleteAsync(new Dictionary<string, string>()).ConfigureAwait(false);
        }
        finally
        {
            if (Interlocked.Exchange(ref _resourcesDisposed, 1) == 0) _log?.Dispose();
            _admission.Dispose();
            _disposeCompletion.TrySetResult(true);
        }
    }

    private sealed record WorkItem(string Operation, Func<object?> Action,
        TaskCompletionSource<object?> Completion)
    {
        public static WorkItem Create<T>(string operation, Func<T> action) => new(operation,
            () => action(), new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously));
    }
}

public sealed class PrivacyLog : IDisposable
{
    private readonly StreamWriter _writer;

    public PrivacyLog(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read),
            new System.Text.UTF8Encoding(false)) { AutoFlush = true };
    }

    public void WriteCode(string code, string? relativePath = null, long? offset = null)
    {
        var safePath = string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)
            ? string.Empty
            : relativePath.Replace('\\', '/');
        _writer.WriteLine(string.Join('|', DateTimeOffset.UtcNow.ToString("O"), code,
            safePath, offset?.ToString() ?? string.Empty));
    }

    public void Dispose() => _writer.Dispose();
}
