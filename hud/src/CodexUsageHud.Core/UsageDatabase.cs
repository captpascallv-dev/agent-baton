using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace CodexUsageHud.Core;

public sealed class UsageDatabase : IDisposable
{
    private const string TupleOracleMigrationVersion = "2";
    private const string AggregateSchemaVersion = "6";
    private const string ContextCaptureMigrationVersion = "2";
    private const string SchemaVersion = "10";
    private const string LineageSemanticVersion = "2";
    private const string PrivateIdentityLogicalComplete = "logical_complete";
    private const string PrivateScrubPending = "pending";
    private const string PrivateScrubComplete = "complete";
    private const int MaximumRebuildBatch = 2000;
    private const long MinuteTicks = TimeSpan.TicksPerMinute;
    private const double HighContextRatio = 0.84d;
    private const double MaximumPostCompactionRatio = 0.60d;
    private const double MaximumRetainedInputRatio = 0.70d;
    private const int RetainedContextBaselines = 8;
    private static readonly long MaximumCompactionGapTicks = TimeSpan.FromMinutes(10).Ticks;

    private static readonly string[] OwnedTables =
    {
        "schema_info", "source_files", "event_fingerprints", "token_samples", "sessions",
        "structural_events", "structural_observations", "session_token_aggregates",
        "turn_token_aggregates", "latest_token_events", "cumulative_frontiers",
        "token_time_buckets", "active_cycle_aggregate", "aggregate_rebuild_state",
        "session_context_state", "context_baseline_observations",
        "session_drift_assessments", "quota_observations", "reset_signals", "parser_errors", "ui_settings",
    };

    private readonly string _databasePath;
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();
    private long _recurringTurnRowsRead;
    private long _cycleAggregateRowsRead;
    private long _cycleBucketRowsRead;
    private long _cycleBoundarySampleRowsRead;
    private int _lastRebuildBatchRows;
    private long _lastRebuildBatchMilliseconds;
    private DateTimeOffset _lifecycleInputsValidUntilUtc = DateTimeOffset.MinValue;
    private Dictionary<string, long> _cachedLifecycleTurnCounts = new(StringComparer.Ordinal);
    private Dictionary<string, RecentSessionActivity> _cachedRecentSessionActivity = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, string?> _canonicalParentMap =
        new Dictionary<string, string?>(StringComparer.Ordinal);
    private bool _disposed;

    public UsageDatabase(string databasePath)
    {
        _databasePath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
            DefaultTimeout = 2,
        };
        _connection = new SqliteConnection(builder.ToString());
        _connection.Open();
        using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=2000;";
            pragma.ExecuteNonQuery();
        }

        Initialize();
    }

    public string DatabasePath => _databasePath;

    public DatabasePerformanceMetrics PerformanceMetrics
    {
        get
        {
            lock (_gate)
            {
                return new DatabasePerformanceMetrics(0, _recurringTurnRowsRead, _cycleAggregateRowsRead,
                    _cycleBucketRowsRead, _cycleBoundarySampleRowsRead, _lastRebuildBatchRows,
                    _lastRebuildBatchMilliseconds);
            }
        }
    }

    public bool IsAggregateRebuildComplete
    {
        get
        {
            lock (_gate)
            {
                using var command = _connection.CreateCommand();
                command.CommandText = "SELECT status FROM aggregate_rebuild_state WHERE singleton = 1;";
                return string.Equals(command.ExecuteScalar() as string, "ready", StringComparison.Ordinal);
            }
        }
    }

    public void Initialize()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            CreateBaseSchema();
            EnsureV5Columns();
            EnsureContextV2Columns();
            EnsureSessionHierarchyColumns();
            EnsureLineageColumns();
            var scrubPrivateSourceBytes = ApplyPrivateSourceIdentityMigration();
            if (scrubPrivateSourceBytes) CompletePrivateSourceScrub();
            CreateV5Indexes();
            ApplyFileIdentityMigration();
            ApplyTupleOracleMigration();
            ApplyTurnKeyMigration();
            ApplySampleSourceMigration();
            ApplyContextCaptureMigration();
            ApplyLineageSemanticMigration();
            PrepareAggregateMigration();
            using (var transaction = BeginImmediate())
            {
                WriteSchemaValue("version", SchemaVersion, transaction);
                transaction.Commit();
            }

        }
    }

    private void CreateBaseSchema()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_info (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            INSERT OR IGNORE INTO schema_info(key, value) VALUES ('version', '6');

            CREATE TABLE IF NOT EXISTS source_files (
                source_key TEXT PRIMARY KEY,
                thread_id TEXT NOT NULL,
                volume_serial TEXT NOT NULL,
                file_id TEXT NOT NULL,
                fallback_digest TEXT NOT NULL,
                is_degraded INTEGER NOT NULL,
                relative_path TEXT NOT NULL,
                generation INTEGER NOT NULL,
                complete_offset INTEGER NOT NULL,
                drain_mode INTEGER NOT NULL DEFAULT 0,
                drain_line_start_offset INTEGER NOT NULL DEFAULT 0,
                drain_offset INTEGER NOT NULL DEFAULT 0,
                last_length INTEGER NOT NULL,
                last_write_utc_ticks INTEGER NOT NULL DEFAULT 0,
                cursor_turn_key TEXT,
                health_code TEXT NOT NULL,
                state_revision INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS event_fingerprints (
                fingerprint TEXT PRIMARY KEY,
                thread_id TEXT NOT NULL,
                first_seen_utc TEXT NOT NULL,
                last_seen_utc TEXT NOT NULL,
                duplicate_count INTEGER NOT NULL DEFAULT 0,
                disposition TEXT NOT NULL DEFAULT 'accepted',
                diagnostic_code TEXT
            );

            CREATE TABLE IF NOT EXISTS token_samples (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                fingerprint TEXT NOT NULL UNIQUE,
                thread_id TEXT NOT NULL,
                input_tokens INTEGER NOT NULL,
                raw_input_tokens INTEGER NOT NULL,
                cached_input_tokens INTEGER NOT NULL,
                cache_write_input_tokens INTEGER NOT NULL,
                output_tokens INTEGER NOT NULL,
                reasoning_output_tokens INTEGER NOT NULL,
                cumulative_input_tokens INTEGER NOT NULL,
                cumulative_output_tokens INTEGER NOT NULL,
                cumulative_total_tokens INTEGER NOT NULL,
                canonical_total_tokens INTEGER NOT NULL,
                reported_total_tokens INTEGER NOT NULL,
                event_time_utc TEXT,
                event_time_ticks INTEGER,
                observed_at_utc TEXT NOT NULL,
                source_key TEXT,
                source_generation INTEGER NOT NULL DEFAULT 0,
                source_offset INTEGER,
                turn_key TEXT,
                model TEXT,
                service_tier TEXT,
                confidence TEXT NOT NULL,
                turn_confidence TEXT NOT NULL DEFAULT 'unavailable',
                event_order_confidence TEXT NOT NULL DEFAULT 'unavailable',
                context_window INTEGER,
                semantic_identity TEXT NOT NULL DEFAULT '',
                semantic_material TEXT,
                legacy_semantic_identity TEXT NOT NULL DEFAULT '',
                lineage_root_thread_id TEXT NOT NULL DEFAULT '',
                is_lineage_canonical INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS sessions (
                thread_id TEXT PRIMARY KEY,
                display_name TEXT,
                role TEXT,
                nickname TEXT,
                project_tag TEXT,
                model TEXT,
                reasoning_effort TEXT,
                service_tier TEXT,
                service_tier_source TEXT NOT NULL DEFAULT 'unavailable',
                last_activity_utc TEXT,
                status TEXT NOT NULL,
                current_turn_key TEXT,
                current_turn_reliable INTEGER NOT NULL DEFAULT 0,
                turn_sequence INTEGER NOT NULL DEFAULT 0,
                turn_open INTEGER NOT NULL DEFAULT 0,
                pinned INTEGER NOT NULL DEFAULT 0,
                current_structural_event_identity TEXT,
                current_structural_source_hash TEXT,
                current_structural_generation INTEGER NOT NULL DEFAULT 0,
                current_structural_offset INTEGER NOT NULL DEFAULT -1,
                current_structural_time_utc TEXT,
                state_event_time_ticks INTEGER,
                state_source_key TEXT,
                state_source_generation INTEGER NOT NULL DEFAULT 0,
                state_source_offset INTEGER NOT NULL DEFAULT -1,
                state_event_kind TEXT,
                session_kind TEXT NOT NULL DEFAULT 'Unknown',
                session_surface TEXT NOT NULL DEFAULT 'Unknown',
                parent_thread_id TEXT,
                agent_depth INTEGER
            );

            CREATE TABLE IF NOT EXISTS structural_events (
                event_identity TEXT PRIMARY KEY,
                base_identity TEXT NOT NULL,
                thread_id TEXT NOT NULL,
                event_kind TEXT NOT NULL,
                association_key TEXT,
                event_time_utc TEXT,
                source_identity_hash TEXT NOT NULL,
                source_generation INTEGER NOT NULL,
                source_offset INTEGER NOT NULL,
                occurrence INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS structural_observations (
                observation_key TEXT PRIMARY KEY,
                source_key TEXT NOT NULL,
                source_generation INTEGER NOT NULL,
                source_offset INTEGER NOT NULL,
                base_identity TEXT NOT NULL,
                event_identity TEXT NOT NULL,
                association_key TEXT,
                canonical INTEGER NOT NULL,
                disposition TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS session_token_aggregates (
                thread_id TEXT PRIMARY KEY,
                input_tokens INTEGER NOT NULL,
                raw_input_tokens INTEGER NOT NULL,
                cached_input_tokens INTEGER NOT NULL,
                cache_write_input_tokens INTEGER NOT NULL,
                output_tokens INTEGER NOT NULL,
                reasoning_output_tokens INTEGER NOT NULL,
                canonical_total_tokens INTEGER NOT NULL,
                reported_total_tokens INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS turn_token_aggregates (
                thread_id TEXT NOT NULL,
                turn_key TEXT NOT NULL,
                input_tokens INTEGER NOT NULL,
                raw_input_tokens INTEGER NOT NULL,
                cached_input_tokens INTEGER NOT NULL,
                cache_write_input_tokens INTEGER NOT NULL,
                output_tokens INTEGER NOT NULL,
                reasoning_output_tokens INTEGER NOT NULL,
                canonical_total_tokens INTEGER NOT NULL,
                reported_total_tokens INTEGER NOT NULL,
                PRIMARY KEY(thread_id, turn_key)
            );

            CREATE TABLE IF NOT EXISTS latest_token_events (
                thread_id TEXT PRIMARY KEY,
                sample_id INTEGER NOT NULL,
                turn_key TEXT,
                event_time_ticks INTEGER,
                source_key TEXT,
                source_generation INTEGER NOT NULL,
                source_offset INTEGER,
                turn_confidence TEXT NOT NULL,
                event_order_confidence TEXT NOT NULL,
                input_tokens INTEGER NOT NULL,
                raw_input_tokens INTEGER NOT NULL,
                cached_input_tokens INTEGER NOT NULL,
                cache_write_input_tokens INTEGER NOT NULL,
                output_tokens INTEGER NOT NULL,
                reasoning_output_tokens INTEGER NOT NULL,
                canonical_total_tokens INTEGER NOT NULL,
                reported_total_tokens INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS cumulative_frontiers (
                thread_id TEXT PRIMARY KEY,
                maximum_cumulative_total INTEGER NOT NULL,
                sample_id INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS token_time_buckets (
                bucket_start_ticks INTEGER PRIMARY KEY,
                input_tokens INTEGER NOT NULL,
                raw_input_tokens INTEGER NOT NULL,
                cached_input_tokens INTEGER NOT NULL,
                cache_write_input_tokens INTEGER NOT NULL,
                output_tokens INTEGER NOT NULL,
                reasoning_output_tokens INTEGER NOT NULL,
                canonical_total_tokens INTEGER NOT NULL,
                reported_total_tokens INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS active_cycle_aggregate (
                singleton INTEGER PRIMARY KEY CHECK(singleton = 1),
                cycle_start_ticks INTEGER NOT NULL,
                cycle_end_ticks INTEGER NOT NULL,
                input_tokens INTEGER NOT NULL,
                raw_input_tokens INTEGER NOT NULL,
                cached_input_tokens INTEGER NOT NULL,
                cache_write_input_tokens INTEGER NOT NULL,
                output_tokens INTEGER NOT NULL,
                reasoning_output_tokens INTEGER NOT NULL,
                canonical_total_tokens INTEGER NOT NULL,
                reported_total_tokens INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS session_context_state (
                thread_id TEXT PRIMARY KEY,
                latest_event_ticks INTEGER NOT NULL,
                latest_source_key TEXT,
                latest_source_generation INTEGER NOT NULL DEFAULT 0,
                latest_source_offset INTEGER,
                latest_sample_id INTEGER NOT NULL,
                current_input_tokens INTEGER,
                current_context_window INTEGER,
                current_model TEXT,
                high_input_tokens INTEGER,
                high_context_window INTEGER,
                high_cumulative_total INTEGER,
                high_event_ticks INTEGER,
                high_turn_key TEXT,
                awaiting_post INTEGER NOT NULL DEFAULT 0,
                marker_event_ticks INTEGER,
                marker_turn_key TEXT,
                explicit_marker_ticks INTEGER,
                explicit_marker_source_key TEXT,
                explicit_marker_source_generation INTEGER NOT NULL DEFAULT 0,
                explicit_marker_source_offset INTEGER,
                explicit_marker_identity TEXT,
                explicit_marker_turn_key TEXT
            );

            CREATE TABLE IF NOT EXISTS context_baseline_observations (
                thread_id TEXT NOT NULL,
                post_sample_id INTEGER NOT NULL,
                post_input_tokens INTEGER NOT NULL,
                context_window INTEGER NOT NULL,
                event_time_ticks INTEGER NOT NULL,
                model_key TEXT NOT NULL DEFAULT '',
                detection_source TEXT NOT NULL DEFAULT 'heuristic',
                boundary_event_identity TEXT,
                turn_key TEXT,
                runway_turns INTEGER,
                runway_tokens INTEGER,
                PRIMARY KEY(thread_id, post_sample_id)
            );

            CREATE TABLE IF NOT EXISTS session_drift_assessments (
                thread_id TEXT PRIMARY KEY,
                assessment_level INTEGER NOT NULL CHECK(assessment_level IN (1, 2)),
                observed_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS aggregate_rebuild_state (
                singleton INTEGER PRIMARY KEY CHECK(singleton = 1),
                status TEXT NOT NULL,
                cursor_sample_id INTEGER NOT NULL,
                total_samples INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS quota_observations (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                bucket_id TEXT NOT NULL,
                bucket_name TEXT NOT NULL,
                used_percent REAL NOT NULL,
                window_duration_minutes INTEGER NOT NULL,
                resets_at_utc TEXT NOT NULL,
                observed_at_utc TEXT NOT NULL,
                source TEXT NOT NULL,
                is_stale INTEGER NOT NULL DEFAULT 0,
                is_primary INTEGER NOT NULL DEFAULT 1
            );

            CREATE TABLE IF NOT EXISTS reset_signals (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                observed_at_utc TEXT NOT NULL,
                previous_remaining_percent REAL NOT NULL,
                current_remaining_percent REAL NOT NULL,
                reason_code TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS parser_errors (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                relative_path TEXT NOT NULL,
                offset INTEGER NOT NULL,
                error_code TEXT NOT NULL,
                observed_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS ui_settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private void EnsureV5Columns()
    {
        EnsureColumn("event_fingerprints", "disposition", "TEXT NOT NULL DEFAULT 'accepted'");
        EnsureColumn("event_fingerprints", "diagnostic_code", "TEXT");
        EnsureColumn("token_samples", "cumulative_input_tokens", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("token_samples", "cumulative_output_tokens", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("token_samples", "cumulative_total_tokens", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("token_samples", "source_key", "TEXT");
        EnsureColumn("token_samples", "source_generation", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("token_samples", "source_offset", "INTEGER");
        EnsureColumn("token_samples", "event_time_ticks", "INTEGER");
        EnsureColumn("token_samples", "turn_confidence", "TEXT NOT NULL DEFAULT 'unavailable'");
        EnsureColumn("token_samples", "event_order_confidence", "TEXT NOT NULL DEFAULT 'unavailable'");
        EnsureColumn("token_samples", "context_window", "INTEGER");
        EnsureColumn("sessions", "turn_sequence", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("sessions", "turn_open", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("sessions", "current_turn_reliable", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("sessions", "current_structural_event_identity", "TEXT");
        EnsureColumn("sessions", "current_structural_source_hash", "TEXT");
        EnsureColumn("sessions", "current_structural_generation", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("sessions", "current_structural_offset", "INTEGER NOT NULL DEFAULT -1");
        EnsureColumn("sessions", "current_structural_time_utc", "TEXT");
        EnsureColumn("sessions", "state_event_time_ticks", "INTEGER");
        EnsureColumn("sessions", "state_source_key", "TEXT");
        EnsureColumn("sessions", "state_source_generation", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("sessions", "state_source_offset", "INTEGER NOT NULL DEFAULT -1");
        EnsureColumn("sessions", "state_event_kind", "TEXT");
        EnsureColumn("sessions", "service_tier_source", "TEXT NOT NULL DEFAULT 'unavailable'");
        EnsureColumn("sessions", "session_kind", "TEXT NOT NULL DEFAULT 'Unknown'");
        EnsureColumn("quota_observations", "is_primary", "INTEGER NOT NULL DEFAULT 1");
        ApplyServiceTierProvenanceMigration();
    }

    private void EnsureContextV2Columns()
    {
        EnsureColumn("session_context_state", "current_model", "TEXT");
        EnsureColumn("session_context_state", "explicit_marker_ticks", "INTEGER");
        EnsureColumn("session_context_state", "explicit_marker_source_key", "TEXT");
        EnsureColumn("session_context_state", "explicit_marker_source_generation",
            "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("session_context_state", "explicit_marker_source_offset", "INTEGER");
        EnsureColumn("session_context_state", "explicit_marker_identity", "TEXT");
        EnsureColumn("session_context_state", "explicit_marker_turn_key", "TEXT");
        EnsureColumn("context_baseline_observations", "model_key", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn("context_baseline_observations", "detection_source",
            "TEXT NOT NULL DEFAULT 'heuristic'");
        EnsureColumn("context_baseline_observations", "boundary_event_identity", "TEXT");
        EnsureColumn("context_baseline_observations", "turn_key", "TEXT");
        EnsureColumn("context_baseline_observations", "runway_turns", "INTEGER");
        EnsureColumn("context_baseline_observations", "runway_tokens", "INTEGER");
    }

    private void EnsureSessionHierarchyColumns()
    {
        EnsureColumn("sessions", "session_surface", "TEXT NOT NULL DEFAULT 'Unknown'");
        EnsureColumn("sessions", "parent_thread_id", "TEXT");
        EnsureColumn("sessions", "agent_depth", "INTEGER");
    }

    private void EnsureLineageColumns()
    {
        EnsureColumn("token_samples", "semantic_identity", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn("token_samples", "semantic_material", "TEXT");
        EnsureColumn("token_samples", "legacy_semantic_identity", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn("token_samples", "lineage_root_thread_id", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn("token_samples", "is_lineage_canonical", "INTEGER NOT NULL DEFAULT 0");
    }

    private void ApplyServiceTierProvenanceMigration()
    {
        if (!string.Equals(ReadSchemaValueCore("service_tier_provenance_v1"), "1", StringComparison.Ordinal))
        {
            using var transaction = BeginImmediate();
            using (var migrate = _connection.CreateCommand())
            {
                migrate.Transaction = transaction;
                migrate.CommandText = """
                    UPDATE sessions SET service_tier_source = CASE
                        WHEN service_tier IS NOT NULL AND trim(service_tier) <> '' AND service_tier <> '不可用'
                            THEN 'legacy-preserved'
                        ELSE 'unavailable'
                    END;
                    """;
                migrate.ExecuteNonQuery();
            }
            WriteSchemaValue("service_tier_provenance_v1", "1", transaction);
            transaction.Commit();
        }

        using var validate = _connection.CreateCommand();
        validate.CommandText = """
            SELECT COUNT(*) FROM sessions
            WHERE service_tier_source NOT IN
                ('rollout-explicit', 'legacy-preserved', 'catalog-default', 'unavailable');
            """;
        if (Convert.ToInt64(validate.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
            throw new InvalidOperationException("SERVICE_TIER_SOURCE_INVALID");
    }

    private void CreateV5Indexes()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            CREATE INDEX IF NOT EXISTS ix_source_thread ON source_files(thread_id);
            CREATE INDEX IF NOT EXISTS ix_source_relative_path ON source_files(relative_path);
            CREATE INDEX IF NOT EXISTS ix_token_thread_id ON token_samples(thread_id, id);
            CREATE INDEX IF NOT EXISTS ix_token_thread_turn_id ON token_samples(thread_id, turn_key, id);
            CREATE INDEX IF NOT EXISTS ix_token_thread_event_ticks ON token_samples(thread_id, event_time_ticks, id);
            CREATE INDEX IF NOT EXISTS ix_token_event_ticks_id ON token_samples(event_time_ticks, id);
            CREATE INDEX IF NOT EXISTS ix_token_source_order ON token_samples(source_key, source_generation, source_offset);
            CREATE INDEX IF NOT EXISTS ix_context_baseline_thread_time
                ON context_baseline_observations(thread_id, event_time_ticks DESC, post_sample_id DESC);
            CREATE INDEX IF NOT EXISTS ix_structural_thread_order
                ON structural_events(thread_id, event_time_utc, source_identity_hash, source_generation, source_offset);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_structural_observation_lookup
                ON structural_observations(source_key, source_generation, source_offset, base_identity);
            CREATE INDEX IF NOT EXISTS ix_structural_observation_order
                ON structural_observations(source_key, source_generation, base_identity, source_offset);
            CREATE INDEX IF NOT EXISTS ix_quota_time ON quota_observations(observed_at_utc);
            CREATE INDEX IF NOT EXISTS ix_token_lineage_lookup
                ON token_samples(lineage_root_thread_id, semantic_identity, is_lineage_canonical);
            CREATE INDEX IF NOT EXISTS ix_token_legacy_lookup
                ON token_samples(lineage_root_thread_id, legacy_semantic_identity, is_lineage_canonical);
            CREATE INDEX IF NOT EXISTS ix_token_canonical_event_ticks
                ON token_samples(event_time_ticks, id) WHERE is_lineage_canonical = 1;
            CREATE UNIQUE INDEX IF NOT EXISTS ux_token_lineage_canonical
                ON token_samples(lineage_root_thread_id, semantic_identity) WHERE is_lineage_canonical = 1;
            """;
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<SourceFileState> LoadSourceStates()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT source_key, thread_id, volume_serial, file_id, fallback_digest,
                       is_degraded, relative_path, generation, complete_offset, drain_mode,
                       drain_line_start_offset, drain_offset, last_length, last_write_utc_ticks,
                       cursor_turn_key, health_code, state_revision
                FROM source_files ORDER BY source_key;
                """;
            using var reader = command.ExecuteReader();
            var result = new List<SourceFileState>();
            while (reader.Read()) result.Add(ReadSourceState(reader));
            return result;
        }
    }

    public SourceFileState? LoadSourceState(string sourceKey)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT source_key, thread_id, volume_serial, file_id, fallback_digest,
                       is_degraded, relative_path, generation, complete_offset, drain_mode,
                       drain_line_start_offset, drain_offset, last_length, last_write_utc_ticks,
                       cursor_turn_key, health_code, state_revision
                FROM source_files WHERE source_key = $source_key;
                """;
            command.Parameters.AddWithValue("$source_key", sourceKey);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadSourceState(reader) : null;
        }
    }

    public IReadOnlyList<StructuralEventRecord> LoadStructuralEvents()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT event_identity, base_identity, thread_id, event_kind, association_key,
                       event_time_utc, source_identity_hash, source_generation, source_offset, occurrence
                FROM structural_events ORDER BY rowid;
                """;
            using var reader = command.ExecuteReader();
            var result = new List<StructuralEventRecord>();
            while (reader.Read())
            {
                result.Add(new StructuralEventRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), OptionalString(reader, 4), OptionalDate(reader, 5), reader.GetString(6),
                    reader.GetInt32(7), reader.GetInt64(8), reader.GetInt32(9)));
            }
            return result;
        }
    }

    public IReadOnlyDictionary<string, StructuralCursor> LoadStructuralCursors()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT thread_id, current_structural_event_identity, current_structural_source_hash,
                       current_structural_generation, current_structural_offset, current_structural_time_utc
                FROM sessions WHERE current_structural_event_identity IS NOT NULL;
                """;
            using var reader = command.ExecuteReader();
            var result = new Dictionary<string, StructuralCursor>(StringComparer.Ordinal);
            while (reader.Read())
            {
                result[reader.GetString(0)] = new StructuralCursor(reader.GetString(1),
                    OptionalString(reader, 2) ?? string.Empty, reader.GetInt32(3), reader.GetInt64(4),
                    OptionalDate(reader, 5));
            }
            return result;
        }
    }

    public ScanCommitResult CommitScan(ScanTransactionRequest request)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!IsAggregateReadyCore()) return NotCommitted("aggregate_rebuild_pending");
            using var transaction = BeginImmediate();
            if (!SourceExpectationMatches(request.ExpectedSource, transaction))
                return NotCommitted("source_revision_conflict");

            var diagnostics = new HashSet<string>(StringComparer.Ordinal);
            var tokenDispositions = new List<TokenDispositionRecord>();
            var structuralDispositions = new List<StructuralDispositionRecord>();
            var sessions = new Dictionary<string, SessionWork>(StringComparer.Ordinal);
            var scanTurns = new Dictionary<string, ScanTurn>(StringComparer.Ordinal);
            var aggregates = new AggregateAccumulator();
            var accepted = 0;
            var duplicates = 0;
            var tokenWrites = 0;
            var currentThreadId = request.ProposedSource.Identity.ThreadId;
            var parentMap = LoadParentMap(transaction);
            var contextReplayThreads = new HashSet<string>(StringComparer.Ordinal);
            var lineageOrigins = new Dictionary<string, (bool WasInsert, string? PreviousParent)>(
                StringComparer.Ordinal);

            foreach (var parsed in request.OrderedSanitizedEvents.OrderBy(item => item.SourceOffset ?? long.MaxValue))
            {
                if (parsed.ErrorCode is not null) diagnostics.Add(parsed.ErrorCode);
                if (!string.IsNullOrWhiteSpace(parsed.ThreadId)) currentThreadId = parsed.ThreadId!;
                if (string.IsNullOrWhiteSpace(currentThreadId) || currentThreadId == "unknown-thread")
                {
                    diagnostics.Add("thread_id_missing");
                    continue;
                }

                var session = GetSessionWork(currentThreadId, sessions, transaction);
                if (!lineageOrigins.ContainsKey(currentThreadId))
                    lineageOrigins[currentThreadId] = (!session.Exists, session.Metadata.ParentThreadId);
                var scanTurn = GetScanTurn(currentThreadId, scanTurns, session,
                    request.ExpectedSource.Exists ? request.ProposedSource.CursorTurnKey : null);
                if (parsed.TokenSnapshot is null)
                {
                    var structural = ResolveStructural(parsed, currentThreadId, request.ProposedSource,
                        scanTurn, session, transaction);
                    structuralDispositions.Add(structural.Disposition);
                    if (structural.DiagnosticCode is not null) diagnostics.Add(structural.DiagnosticCode);
                    if (parsed.EventKind == "event_msg:context_compacted")
                        ObserveExplicitCompactionBoundary(parsed, currentThreadId, structural,
                            request.ProposedSource, scanTurn, transaction);
                    ApplyStructuralState(parsed, structural, session, scanTurn, request.ProposedSource,
                        request.ObservedAtUtc);
                    continue;
                }

                var candidate = new TokenSampleCandidate(currentThreadId, parsed.TokenSnapshot,
                    parsed.EventTimeUtc, request.ObservedAtUtc, scanTurn.Key ?? session.Metadata.CurrentTurnKey,
                    parsed.Model ?? session.Metadata.Model,
                    parsed.ServiceTier ?? session.Metadata.ServiceTier,
                    request.ProposedSource.Identity.StableKey,
                    request.ProposedSource.Generation, parsed.SourceOffset,
                    scanTurn.Reliable || session.CurrentTurnReliable ? "reliable" : "unavailable",
                    parsed.EventTimeUtc.HasValue ? "reliable" : "degraded");
                var token = InsertTokenCandidate(candidate, transaction);
                tokenDispositions.Add(new TokenDispositionRecord(token.Fingerprint, token.Disposition,
                    token.SampleId, currentThreadId, token.MetadataRecovered));
                tokenWrites += token.Writes;
                if (token.Disposition == TokenDisposition.Duplicate)
                {
                    if (token.SampleId.HasValue && token.IsLineageCanonical)
                        ObserveContextCandidate(candidate, token.SampleId.Value, transaction);
                    duplicates++;
                    if (token.MetadataRecovered) diagnostics.Add("duplicate_metadata_recovered");
                    continue;
                }

                accepted++;
                var lineage = AssignLineageAfterInsert(candidate, token, parentMap, transaction);
                ApplyLineageAggregateTransitions(lineage, aggregates, contextReplayThreads);
                if (lineage.Retired.Count > 0) contextReplayThreads.Add(candidate.ThreadId);
                if (lineage.IsCanonical)
                {
                    aggregates.Register(candidate, token.SampleId!.Value, token.Usage, token.CumulativeTotal,
                        diagnostics, transaction, this);
                    ObserveContextCandidate(candidate, token.SampleId.Value, transaction);
                }
                if (parsed.EventTimeUtc.HasValue && CanAdvanceState(session, parsed.EventTimeUtc.Value.UtcTicks,
                        request.ProposedSource.Identity.StableKey, request.ProposedSource.Generation,
                        parsed.SourceOffset ?? 0, reliableWithoutTime: false))
                {
                    AdvanceOrderedMetadata(session, parsed, request.ProposedSource, parsed.SourceOffset ?? 0,
                        parsed.EventTimeUtc.Value.UtcTicks, updateActivity: true);
                }
            }

            ApplyAggregateDeltas(aggregates, transaction);
            var committedSessions = new List<SessionMetadata>();
            foreach (var session in sessions.Values)
            {
                if (!session.Dirty) continue;
                session.Metadata = session.Metadata with
                {
                    StoredStatus = StatusFor(session.TurnOpen, session.Metadata.LastActivityUtc,
                        request.ObservedAtUtc),
                    CurrentTurnKey = session.CurrentTurnKey,
                    TurnSequence = session.TurnSequence,
                    TurnOpen = session.TurnOpen,
                };
                UpsertSessionCore(session, transaction);
                committedSessions.Add(session.Metadata);
            }

            var lineageInvalidated = false;
            foreach (var origin in lineageOrigins)
            {
                if (!sessions.TryGetValue(origin.Key, out var work)) continue;
                if (!RequiresLineageGraphInvalidation(work, origin.Value.WasInsert, origin.Value.PreviousParent,
                        transaction))
                    continue;
                InvalidateLineageForGraphChange(transaction);
                lineageInvalidated = true;
                break;
            }

            if (!lineageInvalidated)
            {
                foreach (var replayThread in contextReplayThreads)
                {
                    RecomputeLatestEvent(replayThread, transaction);
                    RecomputeFrontier(replayThread, transaction);
                    ReplayContextForThread(replayThread, transaction);
                }
            }

            var finalTurn = scanTurns.TryGetValue(currentThreadId, out var finalScanTurn)
                ? finalScanTurn.Key
                : request.ProposedSource.CursorTurnKey;
            var committedSource = request.ProposedSource with
            {
                CursorTurnKey = finalTurn,
                StateRevision = request.ExpectedSource.StateRevision + 1,
                RelativePath = SafeRelativePath(request.ProposedSource.RelativePath),
            };
            foreach (var error in request.Errors.Concat(diagnostics.Select(code =>
                         new ParserErrorRecord(committedSource.RelativePath, committedSource.Cursor.CompleteOffset,
                             code, request.ObservedAtUtc)))
                     .GroupBy(item => (item.RelativePath, item.Offset, item.ErrorCode))
                     .Select(group => group.First()))
            {
                InsertParserError(error with { RelativePath = SafeRelativePath(error.RelativePath) }, transaction);
            }

            SaveSourceStateCore(committedSource, transaction);
            transaction.Commit();
            return new ScanCommitResult(true, committedSource, accepted, duplicates, tokenWrites,
                committedSessions.Count, 1, diagnostics.OrderBy(item => item, StringComparer.Ordinal).ToArray(),
                committedSessions, tokenDispositions, structuralDispositions);
        }
    }

    public bool AcceptTokenSample(string threadId, TokenUsageSnapshot snapshot,
        DateTimeOffset? eventTimeUtc, DateTimeOffset observedAtUtc, string? turnKey,
        string? model, string? serviceTier)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!IsAggregateReadyCore()) return false;
            using var transaction = BeginImmediate();
            var candidate = new TokenSampleCandidate(threadId, snapshot, eventTimeUtc, observedAtUtc,
                turnKey, model, serviceTier, null, 0, null, turnKey is null ? "unavailable" : "reliable",
                eventTimeUtc.HasValue ? "reliable" : "degraded");
            var result = InsertTokenCandidate(candidate, transaction);
            if (result.Disposition == TokenDisposition.Duplicate)
            {
                if (result.SampleId.HasValue && result.IsLineageCanonical)
                    ObserveContextCandidate(candidate, result.SampleId.Value, transaction);
                transaction.Commit();
                return false;
            }

            var diagnostics = new HashSet<string>(StringComparer.Ordinal);
            var aggregates = new AggregateAccumulator();
            var parentMap = LoadParentMap(transaction);
            var lineage = AssignLineageAfterInsert(candidate, result, parentMap, transaction);
            var replayThreads = new HashSet<string>(StringComparer.Ordinal);
            ApplyLineageAggregateTransitions(lineage, aggregates, replayThreads);
            if (lineage.Retired.Count > 0) replayThreads.Add(candidate.ThreadId);
            if (lineage.IsCanonical)
            {
                aggregates.Register(candidate, result.SampleId!.Value, result.Usage, result.CumulativeTotal,
                    diagnostics, transaction, this);
                ObserveContextCandidate(candidate, result.SampleId.Value, transaction);
            }
            ApplyAggregateDeltas(aggregates, transaction);
            foreach (var replayThread in replayThreads)
            {
                RecomputeLatestEvent(replayThread, transaction);
                RecomputeFrontier(replayThread, transaction);
                ReplayContextForThread(replayThread, transaction);
            }
            transaction.Commit();
            return true;
        }
    }

    public bool RunAggregateRebuildBatch(int maximumRows = MaximumRebuildBatch)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            maximumRows = Math.Clamp(maximumRows, 1, MaximumRebuildBatch);
            if (IsAggregateReadyCore())
            {
                _lastRebuildBatchRows = 0;
                _lastRebuildBatchMilliseconds = 0;
                return true;
            }

            var stopwatch = Stopwatch.StartNew();
            using var transaction = BeginImmediate();
            long cursor;
            using (var state = _connection.CreateCommand())
            {
                state.Transaction = transaction;
                state.CommandText = "SELECT cursor_sample_id FROM aggregate_rebuild_state WHERE singleton = 1;";
                cursor = Convert.ToInt64(state.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            var rows = new List<RebuildSample>();
            using (var command = _connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"""
                    SELECT id, thread_id, turn_key, event_time_utc, event_time_ticks,
                           source_key, source_generation, source_offset,
                           input_tokens, raw_input_tokens, cached_input_tokens, cache_write_input_tokens,
                           output_tokens, reasoning_output_tokens, canonical_total_tokens, reported_total_tokens,
                           cumulative_total_tokens, turn_confidence, event_order_confidence,
                           context_window, is_lineage_canonical, cumulative_input_tokens, cumulative_output_tokens,
                           model
                    FROM token_samples WHERE id > $cursor ORDER BY id LIMIT {maximumRows};
                    """;
                command.Parameters.AddWithValue("$cursor", cursor);
                using var reader = command.ExecuteReader();
                while (reader.Read()) rows.Add(ReadRebuildSample(reader));
            }

            var accumulator = new AggregateAccumulator();
            var diagnostics = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                var ticks = row.EventTimeTicks ?? row.EventTimeUtc?.UtcTicks;
                var turnConfidence = row.TurnKey is not null && row.TurnConfidence == "unavailable"
                    ? "reliable" : row.TurnConfidence;
                var orderConfidence = ticks.HasValue ? "reliable" : "degraded";
                if (row.EventTimeTicks != ticks || row.TurnConfidence != turnConfidence ||
                    row.EventOrderConfidence != orderConfidence)
                {
                    using var update = _connection.CreateCommand();
                    update.Transaction = transaction;
                    update.CommandText = """
                        UPDATE token_samples SET event_time_ticks = $ticks,
                            turn_confidence = $turn_confidence,
                            event_order_confidence = $order_confidence
                        WHERE id = $id;
                        """;
                    update.Parameters.AddWithValue("$ticks", ticks.HasValue ? ticks.Value : DBNull.Value);
                    update.Parameters.AddWithValue("$turn_confidence", turnConfidence);
                    update.Parameters.AddWithValue("$order_confidence", orderConfidence);
                    update.Parameters.AddWithValue("$id", row.Id);
                    update.ExecuteNonQuery();
                }

                var candidate = new TokenSampleCandidate(row.ThreadId, RebuildSnapshot(row), ticks.HasValue
                        ? new DateTimeOffset(ticks.Value, TimeSpan.Zero) : null,
                    DateTimeOffset.MinValue, row.TurnKey, row.Model, null, row.SourceKey,
                    row.SourceGeneration, row.SourceOffset, turnConfidence, orderConfidence);
                if (!row.IsLineageCanonical) continue;
                accumulator.Register(candidate, row.Id, row.Usage, row.CumulativeTotal,
                    diagnostics, transaction, this);
            }
            ApplyAggregateDeltas(accumulator, transaction);

            var newCursor = rows.Count == 0 ? cursor : rows[^1].Id;
            var status = rows.Count < maximumRows ? "ready" : "rebuilding";
            using (var updateState = _connection.CreateCommand())
            {
                updateState.Transaction = transaction;
                updateState.CommandText = """
                    UPDATE aggregate_rebuild_state
                    SET cursor_sample_id = $cursor, status = $status WHERE singleton = 1;
                    """;
                updateState.Parameters.AddWithValue("$cursor", newCursor);
                updateState.Parameters.AddWithValue("$status", status);
                updateState.ExecuteNonQuery();
            }
            transaction.Commit();
            stopwatch.Stop();
            _lastRebuildBatchRows = rows.Count;
            _lastRebuildBatchMilliseconds = stopwatch.ElapsedMilliseconds;
            return status == "ready";
        }
    }

    public void UpsertSession(SessionMetadata metadata, SessionStatus status = SessionStatus.Idle,
        string? currentTurnKey = null, long turnSequence = 0, bool turnOpen = false,
        StructuralCursor? structuralCursor = null)
    {
        lock (_gate)
        {
            using var transaction = BeginImmediate();
            var work = GetSessionWork(metadata.ThreadId, new Dictionary<string, SessionWork>(StringComparer.Ordinal),
                transaction);
            var wasInsert = !work.Exists;
            var previousParent = work.Metadata.ParentThreadId;
            var tier = SelectServiceTier(work.Metadata.ServiceTier, work.Metadata.ServiceTierSource,
                metadata.ServiceTier, metadata.ServiceTierSource, preferIncomingOnEqual: true);
            work.Metadata = work.Metadata with
            {
                DisplayName = metadata.DisplayName ?? work.Metadata.DisplayName,
                Role = metadata.Role ?? work.Metadata.Role,
                Nickname = metadata.Nickname ?? work.Metadata.Nickname,
                ProjectTag = metadata.ProjectTag ?? work.Metadata.ProjectTag,
                Model = metadata.Model ?? work.Metadata.Model,
                ReasoningEffort = metadata.ReasoningEffort ?? work.Metadata.ReasoningEffort,
                ServiceTier = tier.Tier,
                ServiceTierSource = tier.Source,
                LastActivityUtc = metadata.LastActivityUtc ?? work.Metadata.LastActivityUtc,
                Pinned = metadata.Pinned || work.Metadata.Pinned,
                StoredStatus = status,
                Kind = metadata.Kind == SessionKind.Unknown ? work.Metadata.Kind : metadata.Kind,
                Surface = metadata.Surface == SessionSurface.Unknown ? work.Metadata.Surface : metadata.Surface,
                ParentThreadId = metadata.ParentThreadId ?? work.Metadata.ParentThreadId,
                AgentDepth = metadata.AgentDepth ?? work.Metadata.AgentDepth,
            };
            work.CurrentTurnKey = currentTurnKey;
            work.CurrentTurnReliable = currentTurnKey is not null;
            work.TurnSequence = Math.Max(0, turnSequence);
            work.TurnOpen = turnOpen;
            work.Dirty = true;
            if (structuralCursor is not null)
            {
                work.StructuralCursor = structuralCursor;
                work.StateEventTimeTicks = structuralCursor.EventTimeUtc?.UtcTicks;
                work.StateSourceKey = structuralCursor.SourceIdentityHash;
                work.StateSourceGeneration = structuralCursor.SourceGeneration;
                work.StateSourceOffset = structuralCursor.SourceOffset;
                work.StateEventKind = "structural";
            }
            UpsertSessionCore(work, transaction);
            MaybeInvalidateLineageGraph(work, previousParent, wasInsert, transaction);
            transaction.Commit();
        }
    }

    public bool MergeSessionMetadata(SessionMetadata metadata)
    {
        lock (_gate)
        {
            using var transaction = BeginImmediate();
            var map = new Dictionary<string, SessionWork>(StringComparer.Ordinal);
            var work = GetSessionWork(metadata.ThreadId, map, transaction);
            var before = work.Metadata;
            var wasInsert = !work.Exists;
            var previousParent = before.ParentThreadId;
            var orderedRolloutExists = work.StateSourceKey is not null;
            var tier = SelectServiceTier(before.ServiceTier, before.ServiceTierSource,
                metadata.ServiceTier, metadata.ServiceTierSource, preferIncomingOnEqual: false);
            work.Metadata = before with
            {
                DisplayName = metadata.DisplayName ?? before.DisplayName,
                Role = metadata.Role ?? before.Role,
                Nickname = metadata.Nickname ?? before.Nickname,
                ProjectTag = metadata.ProjectTag ?? before.ProjectTag,
                Model = orderedRolloutExists ? before.Model ?? metadata.Model : metadata.Model ?? before.Model,
                ReasoningEffort = orderedRolloutExists
                    ? before.ReasoningEffort ?? metadata.ReasoningEffort
                    : metadata.ReasoningEffort ?? before.ReasoningEffort,
                ServiceTier = tier.Tier,
                ServiceTierSource = tier.Source,
                LastActivityUtc = before.LastActivityUtc ?? metadata.LastActivityUtc,
                Kind = metadata.Kind == SessionKind.Unknown ? before.Kind : metadata.Kind,
                Surface = metadata.Surface == SessionSurface.Unknown ? before.Surface : metadata.Surface,
                ParentThreadId = metadata.ParentThreadId ?? before.ParentThreadId,
                AgentDepth = metadata.AgentDepth ?? before.AgentDepth,
            };
            if (work.Exists && work.Metadata == before)
            {
                transaction.Rollback();
                return false;
            }
            UpsertSessionCore(work, transaction);
            MaybeInvalidateLineageGraph(work, previousParent, wasInsert, transaction);
            transaction.Commit();
            return true;
        }
    }

    public bool ClearSessionParent(string threadId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var transaction = BeginImmediate();
            var work = GetSessionWork(threadId, new Dictionary<string, SessionWork>(StringComparer.Ordinal),
                transaction);
            if (!work.Exists || work.Metadata.ParentThreadId is null)
            {
                transaction.Rollback();
                return false;
            }

            var previousParent = work.Metadata.ParentThreadId;
            work.Metadata = work.Metadata with { ParentThreadId = null };
            work.Dirty = true;
            UpsertSessionCore(work, transaction);
            MaybeInvalidateLineageGraph(work, previousParent, wasInsert: false, transaction);
            transaction.Commit();
            return true;
        }
    }

    public void RebuildLineageCanonical()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var transaction = BeginImmediate();
            ReassignLineageCanonical(transaction);
            ReplayAllDerivedContext(transaction);
            InvalidateAggregateRebuild(transaction);
            _lifecycleInputsValidUntilUtc = DateTimeOffset.MinValue;
            transaction.Commit();
        }
    }

    public bool RecoverPersistedContextWindow(string fingerprint, long contextWindow)
    {
        if (string.IsNullOrWhiteSpace(fingerprint)) return false;
        lock (_gate)
        {
            ThrowIfDisposed();
            using var transaction = BeginImmediate();
            using var recover = _connection.CreateCommand();
            recover.Transaction = transaction;
            recover.CommandText = """
                UPDATE token_samples SET context_window = $context_window
                WHERE fingerprint = $fingerprint AND context_window IS NULL;
                """;
            recover.Parameters.AddWithValue("$context_window", contextWindow);
            recover.Parameters.AddWithValue("$fingerprint", fingerprint);
            if (recover.ExecuteNonQuery() <= 0)
            {
                transaction.Rollback();
                return false;
            }

            var persisted = LoadPersistedTokenSample(fingerprint, transaction);
            RefreshSemanticFromStored(persisted.SampleId, transaction);
            transaction.Commit();
            return true;
        }
    }

    public bool ApplyCatalogTier(string threadId, string serviceTier)
    {
        lock (_gate)
        {
            if (IsUnavailableTier(serviceTier)) return false;
            using var transaction = BeginImmediate();
            var work = GetSessionWork(threadId, new Dictionary<string, SessionWork>(StringComparer.Ordinal),
                transaction);
            if (ServiceTierProvenance.Priority(work.Metadata.ServiceTierSource) >
                ServiceTierProvenance.Priority(ServiceTierProvenance.CatalogDefault))
            {
                transaction.Rollback();
                return false;
            }
            if (string.Equals(work.Metadata.ServiceTier, serviceTier, StringComparison.Ordinal) &&
                string.Equals(work.Metadata.ServiceTierSource, ServiceTierProvenance.CatalogDefault,
                    StringComparison.Ordinal))
            {
                transaction.Rollback();
                return false;
            }
            work.Metadata = work.Metadata with
            {
                ServiceTier = serviceTier,
                ServiceTierSource = ServiceTierProvenance.CatalogDefault,
            };
            work.Dirty = true;
            UpsertSessionCore(work, transaction);
            transaction.Commit();
            return true;
        }
    }

    public void SetPinnedThread(string? threadId)
    {
        lock (_gate)
        {
            using var transaction = BeginImmediate();
            using (var clear = _connection.CreateCommand())
            {
                clear.Transaction = transaction;
                clear.CommandText = "UPDATE sessions SET pinned = 0 WHERE pinned <> 0;";
                clear.ExecuteNonQuery();
            }
            if (!string.IsNullOrWhiteSpace(threadId))
            {
                using var pin = _connection.CreateCommand();
                pin.Transaction = transaction;
                pin.CommandText = "UPDATE sessions SET pinned = 1 WHERE thread_id = $thread_id;";
                pin.Parameters.AddWithValue("$thread_id", threadId);
                pin.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    public void SetSessionDriftAssessment(string threadId, DriftAssessmentLevel level,
        DateTimeOffset observedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(threadId)) throw new ArgumentException("thread_id_required", nameof(threadId));
        if (!Enum.IsDefined(level)) throw new ArgumentOutOfRangeException(nameof(level));
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            if (level == DriftAssessmentLevel.Unassessed)
            {
                command.CommandText = "DELETE FROM session_drift_assessments WHERE thread_id = $thread_id;";
                command.Parameters.AddWithValue("$thread_id", threadId);
                command.ExecuteNonQuery();
                return;
            }

            command.CommandText = """
                INSERT INTO session_drift_assessments(thread_id, assessment_level, observed_at_utc)
                VALUES ($thread_id, $assessment_level, $observed_at_utc)
                ON CONFLICT(thread_id) DO UPDATE SET
                    assessment_level = excluded.assessment_level,
                    observed_at_utc = excluded.observed_at_utc;
                """;
            command.Parameters.AddWithValue("$thread_id", threadId);
            command.Parameters.AddWithValue("$assessment_level", (int)level);
            command.Parameters.AddWithValue("$observed_at_utc", observedAtUtc.UtcDateTime.ToString("O"));
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyDictionary<string, SessionDriftAssessment> LoadSessionDriftAssessments()
    {
        lock (_gate) return LoadSessionDriftAssessmentsCore();
    }

    public IReadOnlyList<SessionMetadata> LoadSessions()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT thread_id, display_name, role, nickname, project_tag, model,
                       reasoning_effort, service_tier, service_tier_source, last_activity_utc, status,
                       current_turn_key, turn_sequence, turn_open, pinned, session_kind,
                       session_surface, parent_thread_id, agent_depth
                FROM sessions ORDER BY last_activity_utc DESC;
                """;
            using var reader = command.ExecuteReader();
            var list = new List<SessionMetadata>();
            while (reader.Read()) list.Add(ReadSessionMetadata(reader));
            return list;
        }
    }

    public IReadOnlyList<SessionAggregate> LoadSessionAggregates(DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            _recurringTurnRowsRead = 0;
            var sessions = LoadSessionWorks();
            var totals = LoadSessionTotalsCore();
            var latest = LoadLatestEvents();
            var turns = LoadRecurringTurnTotalsCore();
            var contextMetrics = LoadContextMetricsCore();
            var lifecycleMetrics = LoadLifecycleMetricsCore(sessions, totals, nowUtc);
            var driftAssessments = LoadSessionDriftAssessmentsCore();
            var result = new List<SessionAggregate>();
            foreach (var work in sessions)
            {
                totals.TryGetValue(work.Metadata.ThreadId, out var sessionTotal);
                var latestTurn = work.CurrentTurnKey is not null &&
                                 turns.TryGetValue((work.Metadata.ThreadId, work.CurrentTurnKey), out var currentTotal)
                    ? currentTotal : default;
                var recentKind = RecentUsageKind.Unavailable;
                var recent = default(CanonicalTokenUsage);
                var confidence = "unavailable";
                if (latest.TryGetValue(work.Metadata.ThreadId, out var latestEvent))
                {
                    if (latestEvent.TurnKey is not null && latestEvent.TurnConfidence == "reliable" &&
                        turns.TryGetValue((work.Metadata.ThreadId, latestEvent.TurnKey), out var turnTotal))
                    {
                        recentKind = RecentUsageKind.LatestTurn;
                        recent = turnTotal;
                        confidence = "reliable-turn";
                        latestTurn = turnTotal;
                    }
                    else
                    {
                        recentKind = RecentUsageKind.LatestEventDegraded;
                        recent = latestEvent.Usage;
                        confidence = latestEvent.EventOrderConfidence;
                    }
                }

                var status = StatusFor(work.TurnOpen, work.Metadata.LastActivityUtc, nowUtc);
                contextMetrics.TryGetValue(work.Metadata.ThreadId, out var context);
                lifecycleMetrics.TryGetValue(work.Metadata.ThreadId, out var lifecycle);
                driftAssessments.TryGetValue(work.Metadata.ThreadId, out var drift);
                result.Add(new SessionAggregate(work.Metadata with
                    {
                        StoredStatus = status,
                        CurrentTurnKey = work.CurrentTurnKey,
                        TurnSequence = work.TurnSequence,
                        TurnOpen = work.TurnOpen,
                    }, status, sessionTotal, latestTurn, work.Metadata.LastActivityUtc,
                    work.CurrentTurnKey, false, recentKind, recent, confidence, context, lifecycle,
                    drift ?? SessionDriftAssessment.Unassessed));
            }
            return result.OrderByDescending(item => item.LastActivityUtc ?? DateTimeOffset.MinValue).ToArray();
        }
    }

    public IReadOnlyDictionary<string, CanonicalTokenUsage> LoadSessionTotals()
    {
        lock (_gate) return LoadSessionTotalsCore();
    }

    public IReadOnlyDictionary<(string ThreadId, string TurnKey), CanonicalTokenUsage> LoadTurnTotals()
    {
        lock (_gate) return LoadTurnTotalsCore();
    }

    public IReadOnlyDictionary<string, SessionContextMetrics> LoadSessionContextMetrics()
    {
        lock (_gate) return LoadContextMetricsCore();
    }

    public IReadOnlyList<string> GetRecurringTurnQueryPlan()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "EXPLAIN QUERY PLAN " + RecurringTurnTotalsSql;
            using var reader = command.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read()) rows.Add(reader.GetString(3));
            return rows;
        }
    }

    public IReadOnlyDictionary<string, IReadOnlySet<string>> LoadKnownTurnKeys()
    {
        lock (_gate)
        {
            var mutable = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT thread_id, turn_key FROM turn_token_aggregates ORDER BY thread_id, turn_key;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!mutable.TryGetValue(reader.GetString(0), out var keys))
                {
                    keys = new HashSet<string>(StringComparer.Ordinal);
                    mutable[reader.GetString(0)] = keys;
                }
                keys.Add(reader.GetString(1));
            }
            return mutable.ToDictionary(pair => pair.Key, pair => (IReadOnlySet<string>)pair.Value,
                StringComparer.Ordinal);
        }
    }

    public CanonicalTokenUsage GetSessionTotal(string threadId, string? turnKey = null)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = turnKey is null
                ? $"SELECT {AggregateColumns} FROM session_token_aggregates WHERE thread_id = $thread_id;"
                : $"SELECT {AggregateColumns} FROM turn_token_aggregates WHERE thread_id = $thread_id AND turn_key = $turn_key;";
            command.Parameters.AddWithValue("$thread_id", threadId);
            if (turnKey is not null) command.Parameters.AddWithValue("$turn_key", turnKey);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadAggregate(reader) : default;
        }
    }

    public CanonicalTokenUsage GetCycleTotal(DateTimeOffset cycleStartUtc, DateTimeOffset cycleEndUtc)
    {
        lock (_gate)
        {
            if (!IsAggregateReadyCore() || cycleEndUtc <= cycleStartUtc) return default;
            var start = cycleStartUtc.UtcTicks;
            var end = cycleEndUtc.UtcTicks;
            using (var existing = _connection.CreateCommand())
            {
                existing.CommandText = $"""
                    SELECT {AggregateColumns} FROM active_cycle_aggregate
                    WHERE singleton = 1 AND cycle_start_ticks = $start AND cycle_end_ticks = $end;
                    """;
                existing.Parameters.AddWithValue("$start", start);
                existing.Parameters.AddWithValue("$end", end);
                using var reader = existing.ExecuteReader();
                if (reader.Read())
                {
                    _cycleAggregateRowsRead = 1;
                    _cycleBucketRowsRead = 0;
                    _cycleBoundarySampleRowsRead = 0;
                    return ReadAggregate(reader);
                }
            }

            var total = default(CanonicalTokenUsage);
            var bucketRows = 0L;
            var boundaryRows = 0L;
            using var transaction = BeginImmediate();
            var firstFullMinute = CeilingMinute(start);
            var endFullMinute = FloorMinute(end);
            if (firstFullMinute < endFullMinute)
            {
                using var buckets = _connection.CreateCommand();
                buckets.Transaction = transaction;
                buckets.CommandText = $"""
                    SELECT {AggregateColumns} FROM token_time_buckets
                    WHERE bucket_start_ticks >= $start AND bucket_start_ticks < $end
                    ORDER BY bucket_start_ticks;
                    """;
                buckets.Parameters.AddWithValue("$start", firstFullMinute);
                buckets.Parameters.AddWithValue("$end", endFullMinute);
                using var reader = buckets.ExecuteReader();
                while (reader.Read())
                {
                    total += ReadAggregate(reader);
                    bucketRows++;
                }
            }

            var ranges = BoundaryRanges(start, end, firstFullMinute, endFullMinute);
            foreach (var range in ranges)
            {
                using var samples = _connection.CreateCommand();
                samples.Transaction = transaction;
                samples.CommandText = $"""
                    SELECT {AggregateColumns} FROM token_samples
                    WHERE event_time_ticks >= $start AND event_time_ticks < $end
                      AND is_lineage_canonical = 1
                    ORDER BY event_time_ticks, id;
                    """;
                samples.Parameters.AddWithValue("$start", range.Start);
                samples.Parameters.AddWithValue("$end", range.End);
                using var reader = samples.ExecuteReader();
                while (reader.Read())
                {
                    total += ReadAggregate(reader);
                    boundaryRows++;
                }
            }

            UpsertActiveCycle(start, end, total, transaction);
            transaction.Commit();
            _cycleAggregateRowsRead = 0;
            _cycleBucketRowsRead = bucketRows;
            _cycleBoundarySampleRowsRead = boundaryRows;
            return total;
        }
    }

    public CanonicalTokenUsage GetCycleTotalForThreads(DateTimeOffset cycleStartUtc,
        DateTimeOffset cycleEndUtc, IReadOnlyCollection<string> threadIds)
    {
        lock (_gate)
        {
            if (!IsAggregateReadyCore() || cycleEndUtc <= cycleStartUtc) return default;
            var distinctThreadIds = threadIds.Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal).ToArray();
            if (distinctThreadIds.Length == 0) return default;

            var total = default(CanonicalTokenUsage);
            const int maximumThreadBatch = 400;
            for (var startIndex = 0; startIndex < distinctThreadIds.Length; startIndex += maximumThreadBatch)
            {
                var batch = distinctThreadIds.Skip(startIndex).Take(maximumThreadBatch).ToArray();
                using var command = _connection.CreateCommand();
                var parameters = new string[batch.Length];
                for (var index = 0; index < batch.Length; index++)
                {
                    parameters[index] = $"$thread_{index}";
                    command.Parameters.AddWithValue(parameters[index], batch[index]);
                }

                command.CommandText = $"""
                    SELECT {AggregateColumns} FROM token_samples
                    WHERE event_time_ticks >= $start AND event_time_ticks < $end
                      AND is_lineage_canonical = 1
                      AND thread_id IN ({string.Join(", ", parameters)})
                    ORDER BY thread_id, event_time_ticks, id;
                    """;
                command.Parameters.AddWithValue("$start", cycleStartUtc.UtcTicks);
                command.Parameters.AddWithValue("$end", cycleEndUtc.UtcTicks);
                using var reader = command.ExecuteReader();
                while (reader.Read()) total += ReadAggregate(reader);
            }

            return total;
        }
    }

    public long GetFingerprintCount(string? threadId = null) => ExecuteScalarLong(threadId is null
        ? "SELECT COUNT(*) FROM event_fingerprints;"
        : "SELECT COUNT(*) FROM event_fingerprints WHERE thread_id = $thread_id;", threadId);

    public long GetSampleCount(string? threadId = null) => ExecuteScalarLong(threadId is null
        ? "SELECT COUNT(*) FROM token_samples;"
        : "SELECT COUNT(*) FROM token_samples WHERE thread_id = $thread_id;", threadId);

    public long GetStructuralEventCount() => ExecuteScalarLong("SELECT COUNT(*) FROM structural_events;");

    public long GetDuplicateObservationCount()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT duplicate_count FROM event_fingerprints;";
            using var reader = command.ExecuteReader();
            var total = 0L;
            while (reader.Read()) total = TokenComponents.SaturatingAdd(total, ReadLong(reader, 0));
            return total;
        }
    }

    public long GetResetSignalCount() => ExecuteScalarLong("SELECT COUNT(*) FROM reset_signals;");

    public void AddParserError(string relativePath, long offset, string errorCode, DateTimeOffset observedAtUtc)
    {
        lock (_gate)
        {
            using var transaction = BeginImmediate();
            InsertParserError(new ParserErrorRecord(SafeRelativePath(relativePath), offset, errorCode,
                observedAtUtc), transaction);
            transaction.Commit();
        }
    }

    public void SaveQuota(QuotaObservation observation)
    {
        if (observation.Primary is null || !observation.Primary.HasValidWindow) return;
        lock (_gate)
        {
            using var transaction = BeginImmediate();
            InsertQuotaBucket(observation.Primary, true, observation, transaction);
            foreach (var bucket in observation.Additional.Where(bucket => bucket.HasValidWindow))
                InsertQuotaBucket(bucket, false, observation, transaction);
            transaction.Commit();
        }
    }

    public QuotaObservation? LoadLatestQuota(bool markStale)
    {
        lock (_gate)
        {
            using var primary = _connection.CreateCommand();
            primary.CommandText = """
                SELECT bucket_id, bucket_name, used_percent, window_duration_minutes,
                       resets_at_utc, observed_at_utc, source
                FROM quota_observations WHERE is_primary = 1 ORDER BY id DESC LIMIT 1;
                """;
            using var reader = primary.ExecuteReader();
            if (!reader.Read()) return null;
            var bucket = ReadQuotaBucket(reader);
            var observed = OptionalDate(reader, 5) ?? DateTimeOffset.MinValue;
            var source = Enum.TryParse<QuotaSource>(reader.GetString(6), out var parsed)
                ? parsed : QuotaSource.OfficialAppServer;
            reader.Close();
            if (!bucket.HasValidWindow)
            {
                return new QuotaObservation(null, Array.Empty<QuotaBucket>(), QuotaSource.Unavailable,
                    observed, false, "quota_window_invalid");
            }
            using var additional = _connection.CreateCommand();
            additional.CommandText = """
                SELECT bucket_id, bucket_name, used_percent, window_duration_minutes, resets_at_utc
                FROM quota_observations WHERE is_primary = 0 AND observed_at_utc = $observed ORDER BY id;
                """;
            additional.Parameters.AddWithValue("$observed", Iso(observed));
            using var additionalReader = additional.ExecuteReader();
            var additionalBuckets = new List<QuotaBucket>();
            while (additionalReader.Read())
            {
                var additionalBucket = ReadQuotaBucket(additionalReader);
                if (additionalBucket.HasValidWindow) additionalBuckets.Add(additionalBucket);
            }
            return new QuotaObservation(bucket, additionalBuckets, source, observed, markStale,
                markStale ? "quota_last_observation" : null);
        }
    }

    public void SaveResetSignal(ResetSignal signal)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO reset_signals(observed_at_utc, previous_remaining_percent,
                    current_remaining_percent, reason_code)
                VALUES ($observed, $previous, $current, $reason);
                """;
            command.Parameters.AddWithValue("$observed", Iso(signal.ObservedAtUtc));
            command.Parameters.AddWithValue("$previous", signal.PreviousRemainingPercent);
            command.Parameters.AddWithValue("$current", signal.CurrentRemainingPercent);
            command.Parameters.AddWithValue("$reason", signal.ReasonCode);
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<HudEvent> LoadRecentEvents(int limit = 12)
    {
        lock (_gate)
        {
            var events = new List<HudEvent>();
            using (var quota = _connection.CreateCommand())
            {
                quota.CommandText = """
                    SELECT observed_at_utc, used_percent, is_stale
                    FROM quota_observations WHERE is_primary = 1 ORDER BY id DESC LIMIT $limit;
                    """;
                quota.Parameters.AddWithValue("$limit", limit);
                using var reader = quota.ExecuteReader();
                while (reader.Read())
                {
                    var observed = OptionalDate(reader, 0) ?? DateTimeOffset.MinValue;
                    var stale = reader.GetInt64(2) != 0;
                    events.Add(new HudEvent(observed, "quota", stale ? "quota_stale" : "quota_observed",
                        $"官方额度观测 · 已用 {reader.GetDouble(1):0.#}%"));
                }
            }
            using (var reset = _connection.CreateCommand())
            {
                reset.CommandText = "SELECT observed_at_utc, reason_code FROM reset_signals ORDER BY id DESC LIMIT $limit;";
                reset.Parameters.AddWithValue("$limit", limit);
                using var reader = reset.ExecuteReader();
                while (reader.Read()) events.Add(new HudEvent(OptionalDate(reader, 0) ?? DateTimeOffset.MinValue,
                    "reset", reader.GetString(1), "疑似 reset 信号"));
            }
            using (var errors = _connection.CreateCommand())
            {
                errors.CommandText = "SELECT observed_at_utc, error_code FROM parser_errors ORDER BY id DESC LIMIT $limit;";
                errors.Parameters.AddWithValue("$limit", limit);
                using var reader = errors.ExecuteReader();
                while (reader.Read()) events.Add(new HudEvent(OptionalDate(reader, 0) ?? DateTimeOffset.MinValue,
                    "source", reader.GetString(1), $"来源状态：{reader.GetString(1)}"));
            }
            return events.OrderByDescending(item => item.ObservedAtUtc).Take(limit).ToArray();
        }
    }

    public void SaveSetting(string key, string value)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ui_settings(key, value) VALUES ($key, $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        }
    }

    public string? LoadSetting(string key)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT value FROM ui_settings WHERE key = $key;";
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteScalar() as string;
        }
    }

    public IReadOnlyList<string> ReadPrivacyTextValues()
    {
        lock (_gate)
        {
            var values = new List<string>();
            foreach (var table in OwnedTables)
            {
                var columns = ReadColumns(table)
                    .Where(item => item.Type.Equals("TEXT", StringComparison.OrdinalIgnoreCase) ||
                                   item.Type.Equals("BLOB", StringComparison.OrdinalIgnoreCase))
                    .Select(item => item.Name).ToArray();
                if (columns.Length == 0) continue;
                using var command = _connection.CreateCommand();
                command.CommandText = $"SELECT {string.Join(", ", columns.Select(QuoteIdentifier))} FROM {QuoteIdentifier(table)};";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    for (var index = 0; index < reader.FieldCount; index++)
                    {
                        if (reader.IsDBNull(index)) continue;
                        values.Add(reader.GetValue(index) is byte[] bytes
                            ? Encoding.UTF8.GetString(bytes)
                            : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture) ?? string.Empty);
                    }
                }
            }
            return values;
        }
    }

    public bool ContainsPrivacySentinel(string sentinel) =>
        ReadPrivacyTextValues().Any(value => value.Contains(sentinel, StringComparison.Ordinal));

    public IReadOnlyDictionary<string, IReadOnlyList<string>> ReadSchemaColumnNames()
    {
        lock (_gate)
        {
            var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var table in OwnedTables) result[table] = ReadColumns(table).Select(item => item.Name).ToArray();
            return result;
        }
    }

    public string? ReadSchemaValue(string key)
    {
        lock (_gate) return ReadSchemaValueCore(key);
    }

    private TokenInsertResult InsertTokenCandidate(TokenSampleCandidate candidate, SqliteTransaction transaction)
    {
        var fingerprint = candidate.Snapshot.Fingerprint(candidate.ThreadId);
        var observed = Iso(candidate.ObservedAtUtc);
        using var fingerprintCommand = _connection.CreateCommand();
        fingerprintCommand.Transaction = transaction;
        fingerprintCommand.CommandText = """
            INSERT OR IGNORE INTO event_fingerprints(
                fingerprint, thread_id, first_seen_utc, last_seen_utc, duplicate_count, disposition)
            VALUES ($fingerprint, $thread_id, $observed, $observed, 0, 'accepted');
            """;
        fingerprintCommand.Parameters.AddWithValue("$fingerprint", fingerprint);
        fingerprintCommand.Parameters.AddWithValue("$thread_id", candidate.ThreadId);
        fingerprintCommand.Parameters.AddWithValue("$observed", observed);
        var inserted = fingerprintCommand.ExecuteNonQuery() == 1;
        if (!inserted && TokenSampleExists(fingerprint, transaction))
        {
            using var duplicate = _connection.CreateCommand();
            duplicate.Transaction = transaction;
            duplicate.CommandText = """
                UPDATE event_fingerprints
                SET last_seen_utc = $observed, duplicate_count = duplicate_count + 1
                WHERE fingerprint = $fingerprint;
                """;
            duplicate.Parameters.AddWithValue("$observed", observed);
            duplicate.Parameters.AddWithValue("$fingerprint", fingerprint);
            duplicate.ExecuteNonQuery();

            var persisted = LoadPersistedTokenSample(fingerprint, transaction);
            var turnRows = 0;
            if (!string.IsNullOrWhiteSpace(candidate.TurnKey))
            {
                using var recoverTurn = _connection.CreateCommand();
                recoverTurn.Transaction = transaction;
                recoverTurn.CommandText = """
                    UPDATE token_samples SET turn_key = $turn_key, turn_confidence = $turn_confidence
                    WHERE fingerprint = $fingerprint AND turn_key IS NULL;
                    """;
                recoverTurn.Parameters.AddWithValue("$turn_key", candidate.TurnKey);
                recoverTurn.Parameters.AddWithValue("$turn_confidence", candidate.TurnConfidence);
                recoverTurn.Parameters.AddWithValue("$fingerprint", fingerprint);
                turnRows = recoverTurn.ExecuteNonQuery();
            }

            var sourceRows = 0;
            if (!string.IsNullOrWhiteSpace(candidate.SourceKey))
            {
                using var recoverSource = _connection.CreateCommand();
                recoverSource.Transaction = transaction;
                recoverSource.CommandText = """
                    UPDATE token_samples SET source_key = $source_key,
                        source_generation = $source_generation, source_offset = $source_offset,
                        event_order_confidence = $event_order_confidence
                    WHERE fingerprint = $fingerprint AND source_key IS NULL;
                    """;
                recoverSource.Parameters.AddWithValue("$source_key", candidate.SourceKey);
                recoverSource.Parameters.AddWithValue("$source_generation", candidate.SourceGeneration);
                recoverSource.Parameters.AddWithValue("$source_offset", candidate.SourceOffset.HasValue
                    ? candidate.SourceOffset.Value : DBNull.Value);
                recoverSource.Parameters.AddWithValue("$event_order_confidence", candidate.EventOrderConfidence);
                recoverSource.Parameters.AddWithValue("$fingerprint", fingerprint);
                sourceRows = recoverSource.ExecuteNonQuery();
            }

            var contextRows = 0;
            if (candidate.Snapshot.ContextWindow.HasValue)
            {
                using var recoverContext = _connection.CreateCommand();
                recoverContext.Transaction = transaction;
                recoverContext.CommandText = """
                    UPDATE token_samples SET context_window = $context_window
                    WHERE fingerprint = $fingerprint AND context_window IS NULL;
                    """;
                recoverContext.Parameters.AddWithValue("$context_window", candidate.Snapshot.ContextWindow.Value);
                recoverContext.Parameters.AddWithValue("$fingerprint", fingerprint);
                contextRows = recoverContext.ExecuteNonQuery();
            }

            var modelRows = 0;
            if (!string.IsNullOrWhiteSpace(candidate.Model))
            {
                using var recoverModel = _connection.CreateCommand();
                recoverModel.Transaction = transaction;
                recoverModel.CommandText = """
                    UPDATE token_samples SET model = $model
                    WHERE fingerprint = $fingerprint AND model IS NULL;
                    """;
                recoverModel.Parameters.AddWithValue("$model", candidate.Model);
                recoverModel.Parameters.AddWithValue("$fingerprint", fingerprint);
                modelRows = recoverModel.ExecuteNonQuery();
            }

            if (turnRows > 0 && persisted.IsLineageCanonical)
                UpsertTurnAggregate(persisted.ThreadId, candidate.TurnKey!, persisted.Usage, transaction);
            var stillCanonical = persisted.IsLineageCanonical;
            if (contextRows > 0)
                stillCanonical = RefreshSemanticFromStored(persisted.SampleId, transaction);
            var metadataRecovered = turnRows + sourceRows + contextRows + modelRows > 0;
            if (turnRows + sourceRows > 0) RecomputeLatestEvent(persisted.ThreadId, transaction);
            if (contextRows > 0) _lifecycleInputsValidUntilUtc = DateTimeOffset.MinValue;
            return new TokenInsertResult(fingerprint, TokenDisposition.Duplicate, persisted.SampleId,
                1 + turnRows + sourceRows + contextRows + modelRows, persisted.Usage, persisted.CumulativeTotal,
                metadataRecovered, persisted.SemanticIdentity, stillCanonical);
        }

        var disposition = inserted ? TokenDisposition.Accepted : TokenDisposition.OrphanRecovered;
        if (!inserted)
        {
            using var recover = _connection.CreateCommand();
            recover.Transaction = transaction;
            recover.CommandText = """
                UPDATE event_fingerprints
                SET last_seen_utc = $observed, disposition = 'accepted', diagnostic_code = NULL
                WHERE fingerprint = $fingerprint;
                """;
            recover.Parameters.AddWithValue("$observed", observed);
            recover.Parameters.AddWithValue("$fingerprint", fingerprint);
            recover.ExecuteNonQuery();
        }

        var usage = candidate.Snapshot.LastUsage.ToCanonical();
        var cumulative = candidate.Snapshot.TotalUsage.ToCanonical();
        var semanticMaterial = candidate.Snapshot.SemanticMaterial();
        var semanticIdentity = candidate.Snapshot.SemanticIdentity();
        var legacyIdentity = candidate.Snapshot.LegacySemanticIdentity();
        using var sample = _connection.CreateCommand();
        sample.Transaction = transaction;
        sample.CommandText = """
            INSERT INTO token_samples(
                fingerprint, thread_id, input_tokens, raw_input_tokens, cached_input_tokens,
                cache_write_input_tokens, output_tokens, reasoning_output_tokens,
                cumulative_input_tokens, cumulative_output_tokens, cumulative_total_tokens,
                canonical_total_tokens, reported_total_tokens, event_time_utc, event_time_ticks,
                observed_at_utc, source_key, source_generation, source_offset, turn_key, model,
                service_tier, confidence, turn_confidence, event_order_confidence, context_window,
                semantic_identity, semantic_material, legacy_semantic_identity, lineage_root_thread_id,
                is_lineage_canonical)
            VALUES ($fingerprint, $thread_id, $input, $raw_input, $cached, $cache_write,
                $output, $reasoning, $cumulative_input, $cumulative_output, $cumulative_total,
                $canonical_total, $reported_total, $event_time, $event_ticks, $observed,
                $source_key, $source_generation, $source_offset, $turn_key, $model, $service_tier,
                'trusted', $turn_confidence, $event_order_confidence, $context_window,
                $semantic_identity, $semantic_material, $legacy_identity, $lineage_root, 0);
            """;
        AddUsageParameters(sample, usage);
        sample.Parameters.AddWithValue("$fingerprint", fingerprint);
        sample.Parameters.AddWithValue("$thread_id", candidate.ThreadId);
        sample.Parameters.AddWithValue("$cumulative_input", cumulative.Input);
        sample.Parameters.AddWithValue("$cumulative_output", cumulative.Output);
        sample.Parameters.AddWithValue("$cumulative_total", cumulative.Total);
        sample.Parameters.AddWithValue("$event_time", candidate.EventTimeUtc.HasValue
            ? Iso(candidate.EventTimeUtc.Value) : DBNull.Value);
        sample.Parameters.AddWithValue("$event_ticks", candidate.EventTimeUtc.HasValue
            ? candidate.EventTimeUtc.Value.UtcTicks : DBNull.Value);
        sample.Parameters.AddWithValue("$observed", observed);
        sample.Parameters.AddWithValue("$source_key", (object?)candidate.SourceKey ?? DBNull.Value);
        sample.Parameters.AddWithValue("$source_generation", candidate.SourceGeneration);
        sample.Parameters.AddWithValue("$source_offset", candidate.SourceOffset.HasValue
            ? candidate.SourceOffset.Value : DBNull.Value);
        sample.Parameters.AddWithValue("$turn_key", (object?)candidate.TurnKey ?? DBNull.Value);
        sample.Parameters.AddWithValue("$model", (object?)candidate.Model ?? DBNull.Value);
        sample.Parameters.AddWithValue("$service_tier", (object?)candidate.ServiceTier ?? DBNull.Value);
        sample.Parameters.AddWithValue("$turn_confidence", candidate.TurnConfidence);
        sample.Parameters.AddWithValue("$event_order_confidence", candidate.EventOrderConfidence);
        sample.Parameters.AddWithValue("$context_window", candidate.Snapshot.ContextWindow.HasValue
            ? candidate.Snapshot.ContextWindow.Value : DBNull.Value);
        sample.Parameters.AddWithValue("$semantic_identity", semanticIdentity);
        sample.Parameters.AddWithValue("$semantic_material", semanticMaterial);
        sample.Parameters.AddWithValue("$legacy_identity", legacyIdentity);
        sample.Parameters.AddWithValue("$lineage_root", candidate.ThreadId);
        sample.ExecuteNonQuery();
        using var identity = _connection.CreateCommand();
        identity.Transaction = transaction;
        identity.CommandText = "SELECT last_insert_rowid();";
        var sampleId = Convert.ToInt64(identity.ExecuteScalar(), CultureInfo.InvariantCulture);
        return new TokenInsertResult(fingerprint, disposition, sampleId, 2, usage, cumulative.Total, false,
            semanticIdentity, false);
    }

    private void ObserveExplicitCompactionBoundary(ParsedEvent parsed, string threadId,
        StructuralResolution resolution, SourceFileState source, ScanTurn scanTurn,
        SqliteTransaction transaction)
    {
        if (!parsed.EventTimeUtc.HasValue || string.IsNullOrWhiteSpace(resolution.Disposition.EventIdentity))
            return;

        ApplyExplicitCompactionMarker(threadId, parsed.EventTimeUtc.Value.UtcTicks, source.Identity.StableKey,
            source.Generation, parsed.SourceOffset ?? 0, resolution.Disposition.EventIdentity,
            resolution.AssociationKey ?? scanTurn.Key, transaction);
    }

    private void ApplyExplicitCompactionMarker(string threadId, long eventTicks, string sourceKey,
        int sourceGeneration, long sourceOffset, string eventIdentity, string? turnKey,
        SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO session_context_state(
                thread_id, latest_event_ticks, latest_source_key, latest_source_generation,
                latest_source_offset, latest_sample_id, explicit_marker_ticks,
                explicit_marker_source_key, explicit_marker_source_generation,
                explicit_marker_source_offset, explicit_marker_identity, explicit_marker_turn_key)
            VALUES ($thread_id, $ticks, $source_key, $generation, $offset, 0, $ticks,
                $source_key, $generation, $offset, $identity, $turn_key)
            ON CONFLICT(thread_id) DO UPDATE SET
                explicit_marker_ticks = excluded.explicit_marker_ticks,
                explicit_marker_source_key = excluded.explicit_marker_source_key,
                explicit_marker_source_generation = excluded.explicit_marker_source_generation,
                explicit_marker_source_offset = excluded.explicit_marker_source_offset,
                explicit_marker_identity = excluded.explicit_marker_identity,
                explicit_marker_turn_key = excluded.explicit_marker_turn_key
            WHERE excluded.explicit_marker_ticks >= session_context_state.latest_event_ticks;
            """;
        command.Parameters.AddWithValue("$thread_id", threadId);
        command.Parameters.AddWithValue("$ticks", eventTicks);
        command.Parameters.AddWithValue("$source_key", sourceKey);
        command.Parameters.AddWithValue("$generation", sourceGeneration);
        command.Parameters.AddWithValue("$offset", sourceOffset);
        command.Parameters.AddWithValue("$identity", eventIdentity);
        command.Parameters.AddWithValue("$turn_key", (object?)turnKey ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private void ObserveContextCandidate(TokenSampleCandidate candidate, long sampleId,
        SqliteTransaction transaction)
    {
        if (candidate.Snapshot.ContextWindow is not > 0 || !candidate.EventTimeUtc.HasValue ||
            candidate.EventOrderConfidence != "reliable") return;

        var contextWindow = candidate.Snapshot.ContextWindow.Value;
        var eventTicks = candidate.EventTimeUtc.Value.UtcTicks;
        var inputTokens = candidate.Snapshot.LastUsage.ToCanonical().Input;
        var cumulativeTotal = candidate.Snapshot.TotalUsage.ToCanonical().Total;
        var state = LoadContextState(candidate.ThreadId, transaction);
        if (state is not null && !IsLaterContextObservation(state, candidate, sampleId, eventTicks)) return;

        var currentInput = state?.CurrentInputTokens;
        var currentWindow = state?.CurrentContextWindow;
        var currentModel = state?.CurrentModel;
        var highInput = state?.HighInputTokens;
        var highWindow = state?.HighContextWindow;
        var highCumulative = state?.HighCumulativeTotal;
        var highTicks = state?.HighEventTicks;
        var highTurn = state?.HighTurnKey;
        var awaitingPost = state?.AwaitingPost ?? false;
        var markerTicks = state?.MarkerEventTicks;
        var markerTurn = state?.MarkerTurnKey;
        var explicitTicks = state?.ExplicitMarkerTicks;
        var explicitSourceKey = state?.ExplicitMarkerSourceKey;
        var explicitGeneration = state?.ExplicitMarkerSourceGeneration ?? 0;
        var explicitOffset = state?.ExplicitMarkerSourceOffset;
        var explicitIdentity = state?.ExplicitMarkerIdentity;
        var explicitTurn = state?.ExplicitMarkerTurnKey;

        if (inputTokens > 0)
        {
            var explicitBaseline = explicitTicks.HasValue && explicitOffset.HasValue &&
                                   !string.IsNullOrWhiteSpace(explicitIdentity) &&
                                   !string.IsNullOrWhiteSpace(candidate.Model) &&
                                   candidate.TurnConfidence == "reliable" &&
                                   !string.IsNullOrWhiteSpace(candidate.TurnKey) &&
                                   candidate.EventTimeUtc.Value.UtcTicks >= explicitTicks.Value &&
                                   string.Equals(candidate.SourceKey, explicitSourceKey, StringComparison.Ordinal) &&
                                   candidate.SourceGeneration == explicitGeneration &&
                                   candidate.SourceOffset.HasValue &&
                                   candidate.SourceOffset.Value > explicitOffset.Value &&
                                   !HasNonCanonicalSampleBetween(candidate.ThreadId, explicitTicks.Value,
                                       eventTicks, transaction);
            if (explicitBaseline)
            {
                InsertContextBaseline(candidate.ThreadId, sampleId, inputTokens, contextWindow,
                    eventTicks, candidate.Model!, candidate.TurnKey!, "explicit", explicitIdentity,
                    transaction);
                explicitTicks = null;
                explicitSourceKey = null;
                explicitGeneration = 0;
                explicitOffset = null;
                explicitIdentity = null;
                explicitTurn = null;
                awaitingPost = false;
                markerTicks = null;
                markerTurn = null;
            }
            else if (awaitingPost && markerTicks.HasValue && highInput is > 0 && highWindow == contextWindow &&
                candidate.TurnConfidence == "reliable" && !string.IsNullOrWhiteSpace(candidate.TurnKey) &&
                string.Equals(candidate.TurnKey, markerTurn, StringComparison.Ordinal) &&
                eventTicks >= markerTicks.Value && eventTicks - markerTicks.Value <= MaximumCompactionGapTicks &&
                inputTokens <= contextWindow * MaximumPostCompactionRatio &&
                inputTokens <= highInput.Value * MaximumRetainedInputRatio &&
                !string.IsNullOrWhiteSpace(candidate.Model))
            {
                InsertContextBaseline(candidate.ThreadId, sampleId, inputTokens, contextWindow,
                    eventTicks, candidate.Model!, candidate.TurnKey!, "heuristic", null, transaction);
            }

            currentInput = inputTokens;
            currentWindow = contextWindow;
            if (!string.IsNullOrWhiteSpace(candidate.Model)) currentModel = candidate.Model;
            awaitingPost = false;
            markerTicks = null;
            markerTurn = null;

            if (inputTokens >= contextWindow * HighContextRatio && candidate.TurnConfidence == "reliable" &&
                !string.IsNullOrWhiteSpace(candidate.TurnKey))
            {
                highInput = inputTokens;
                highWindow = contextWindow;
                highCumulative = cumulativeTotal;
                highTicks = eventTicks;
                highTurn = candidate.TurnKey;
            }
            else
            {
                highInput = null;
                highWindow = null;
                highCumulative = null;
                highTicks = null;
                highTurn = null;
            }
        }
        else
        {
            var sameReliableTurn = candidate.TurnConfidence == "reliable" &&
                                   !string.IsNullOrWhiteSpace(candidate.TurnKey) &&
                                   string.Equals(candidate.TurnKey, highTurn, StringComparison.Ordinal);
            var qualifiesAsMarker = highInput is > 0 && highWindow == contextWindow &&
                                    highCumulative == cumulativeTotal && highTicks.HasValue &&
                                    eventTicks >= highTicks.Value &&
                                    eventTicks - highTicks.Value <= MaximumCompactionGapTicks &&
                                    sameReliableTurn;
            if (qualifiesAsMarker)
            {
                awaitingPost = true;
                markerTicks = eventTicks;
                markerTurn = candidate.TurnKey;
            }
            else if (!(awaitingPost && markerTicks.HasValue && eventTicks >= markerTicks.Value &&
                       eventTicks - markerTicks.Value <= MaximumCompactionGapTicks &&
                       string.Equals(candidate.TurnKey, markerTurn, StringComparison.Ordinal)))
            {
                awaitingPost = false;
                markerTicks = null;
                markerTurn = null;
            }
        }

        UpsertContextState(new ContextState(candidate.ThreadId, eventTicks, candidate.SourceKey,
            candidate.SourceGeneration, candidate.SourceOffset, sampleId, currentInput, currentWindow,
            currentModel, highInput, highWindow, highCumulative, highTicks, highTurn, awaitingPost,
            markerTicks, markerTurn, explicitTicks, explicitSourceKey, explicitGeneration,
            explicitOffset, explicitIdentity, explicitTurn), transaction);
    }

    private ContextState? LoadContextState(string threadId, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT thread_id, latest_event_ticks, latest_source_key, latest_source_generation,
                latest_source_offset, latest_sample_id, current_input_tokens,
                current_context_window, current_model, high_input_tokens, high_context_window,
                high_cumulative_total, high_event_ticks, high_turn_key, awaiting_post,
                marker_event_ticks, marker_turn_key, explicit_marker_ticks,
                explicit_marker_source_key, explicit_marker_source_generation,
                explicit_marker_source_offset, explicit_marker_identity, explicit_marker_turn_key
            FROM session_context_state WHERE thread_id = $thread_id;
            """;
        command.Parameters.AddWithValue("$thread_id", threadId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new ContextState(reader.GetString(0), reader.GetInt64(1),
            OptionalString(reader, 2), reader.GetInt32(3), OptionalLong(reader, 4), reader.GetInt64(5),
            OptionalLong(reader, 6), OptionalLong(reader, 7), OptionalString(reader, 8),
            OptionalLong(reader, 9), OptionalLong(reader, 10), OptionalLong(reader, 11),
            OptionalLong(reader, 12), OptionalString(reader, 13), reader.GetInt64(14) != 0,
            OptionalLong(reader, 15), OptionalString(reader, 16), OptionalLong(reader, 17),
            OptionalString(reader, 18), reader.GetInt32(19), OptionalLong(reader, 20),
            OptionalString(reader, 21), OptionalString(reader, 22)) : null;
    }

    private static bool IsLaterContextObservation(ContextState state, TokenSampleCandidate candidate,
        long sampleId, long eventTicks)
    {
        var timeComparison = eventTicks.CompareTo(state.LatestEventTicks);
        if (timeComparison != 0) return timeComparison > 0;
        if (!string.IsNullOrWhiteSpace(candidate.SourceKey) &&
            string.Equals(candidate.SourceKey, state.LatestSourceKey, StringComparison.Ordinal) &&
            candidate.SourceGeneration == state.LatestSourceGeneration && candidate.SourceOffset.HasValue &&
            state.LatestSourceOffset.HasValue)
        {
            return candidate.SourceOffset.Value > state.LatestSourceOffset.Value;
        }
        return sampleId > state.LatestSampleId;
    }

    private void UpsertContextState(ContextState state, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO session_context_state(
                thread_id, latest_event_ticks, latest_source_key, latest_source_generation,
                latest_source_offset, latest_sample_id, current_input_tokens, current_context_window,
                current_model, high_input_tokens, high_context_window, high_cumulative_total,
                high_event_ticks, high_turn_key, awaiting_post, marker_event_ticks, marker_turn_key,
                explicit_marker_ticks, explicit_marker_source_key, explicit_marker_source_generation,
                explicit_marker_source_offset, explicit_marker_identity, explicit_marker_turn_key)
            VALUES ($thread_id, $latest_ticks, $source_key, $source_generation, $source_offset,
                $sample_id, $current_input, $current_window, $current_model, $high_input, $high_window,
                $high_cumulative, $high_ticks, $high_turn, $awaiting_post, $marker_ticks, $marker_turn,
                $explicit_ticks, $explicit_source_key, $explicit_generation, $explicit_offset,
                $explicit_identity, $explicit_turn)
            ON CONFLICT(thread_id) DO UPDATE SET
                latest_event_ticks = excluded.latest_event_ticks,
                latest_source_key = excluded.latest_source_key,
                latest_source_generation = excluded.latest_source_generation,
                latest_source_offset = excluded.latest_source_offset,
                latest_sample_id = excluded.latest_sample_id,
                current_input_tokens = excluded.current_input_tokens,
                current_context_window = excluded.current_context_window,
                current_model = excluded.current_model,
                high_input_tokens = excluded.high_input_tokens,
                high_context_window = excluded.high_context_window,
                high_cumulative_total = excluded.high_cumulative_total,
                high_event_ticks = excluded.high_event_ticks,
                high_turn_key = excluded.high_turn_key,
                awaiting_post = excluded.awaiting_post,
                marker_event_ticks = excluded.marker_event_ticks,
                marker_turn_key = excluded.marker_turn_key,
                explicit_marker_ticks = excluded.explicit_marker_ticks,
                explicit_marker_source_key = excluded.explicit_marker_source_key,
                explicit_marker_source_generation = excluded.explicit_marker_source_generation,
                explicit_marker_source_offset = excluded.explicit_marker_source_offset,
                explicit_marker_identity = excluded.explicit_marker_identity,
                explicit_marker_turn_key = excluded.explicit_marker_turn_key;
            """;
        command.Parameters.AddWithValue("$thread_id", state.ThreadId);
        command.Parameters.AddWithValue("$latest_ticks", state.LatestEventTicks);
        command.Parameters.AddWithValue("$source_key", (object?)state.LatestSourceKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$source_generation", state.LatestSourceGeneration);
        command.Parameters.AddWithValue("$source_offset", state.LatestSourceOffset.HasValue
            ? state.LatestSourceOffset.Value : DBNull.Value);
        command.Parameters.AddWithValue("$sample_id", state.LatestSampleId);
        command.Parameters.AddWithValue("$current_input", state.CurrentInputTokens.HasValue
            ? state.CurrentInputTokens.Value : DBNull.Value);
        command.Parameters.AddWithValue("$current_window", state.CurrentContextWindow.HasValue
            ? state.CurrentContextWindow.Value : DBNull.Value);
        command.Parameters.AddWithValue("$current_model", (object?)state.CurrentModel ?? DBNull.Value);
        command.Parameters.AddWithValue("$high_input", state.HighInputTokens.HasValue
            ? state.HighInputTokens.Value : DBNull.Value);
        command.Parameters.AddWithValue("$high_window", state.HighContextWindow.HasValue
            ? state.HighContextWindow.Value : DBNull.Value);
        command.Parameters.AddWithValue("$high_cumulative", state.HighCumulativeTotal.HasValue
            ? state.HighCumulativeTotal.Value : DBNull.Value);
        command.Parameters.AddWithValue("$high_ticks", state.HighEventTicks.HasValue
            ? state.HighEventTicks.Value : DBNull.Value);
        command.Parameters.AddWithValue("$high_turn", (object?)state.HighTurnKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$awaiting_post", state.AwaitingPost ? 1 : 0);
        command.Parameters.AddWithValue("$marker_ticks", state.MarkerEventTicks.HasValue
            ? state.MarkerEventTicks.Value : DBNull.Value);
        command.Parameters.AddWithValue("$marker_turn", (object?)state.MarkerTurnKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$explicit_ticks", state.ExplicitMarkerTicks.HasValue
            ? state.ExplicitMarkerTicks.Value : DBNull.Value);
        command.Parameters.AddWithValue("$explicit_source_key",
            (object?)state.ExplicitMarkerSourceKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$explicit_generation", state.ExplicitMarkerSourceGeneration);
        command.Parameters.AddWithValue("$explicit_offset", state.ExplicitMarkerSourceOffset.HasValue
            ? state.ExplicitMarkerSourceOffset.Value : DBNull.Value);
        command.Parameters.AddWithValue("$explicit_identity",
            (object?)state.ExplicitMarkerIdentity ?? DBNull.Value);
        command.Parameters.AddWithValue("$explicit_turn", (object?)state.ExplicitMarkerTurnKey ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private void InsertContextBaseline(string threadId, long sampleId, long inputTokens,
        long contextWindow, long eventTicks, string model, string turnKey, string detectionSource,
        string? boundaryEventIdentity, SqliteTransaction transaction)
    {
        var modelKey = model.Trim();
        long? runwayTurns = null;
        long? runwayTokens = null;
        long? previousTicks = null;
        using (var previous = _connection.CreateCommand())
        {
            previous.Transaction = transaction;
            previous.CommandText = """
                SELECT event_time_ticks
                FROM context_baseline_observations
                WHERE thread_id = $thread_id AND context_window = $window
                  AND model_key = $model AND detection_source = $source
                  AND event_time_ticks < $ticks
                ORDER BY event_time_ticks DESC, post_sample_id DESC LIMIT 1;
                """;
            previous.Parameters.AddWithValue("$thread_id", threadId);
            previous.Parameters.AddWithValue("$window", contextWindow);
            previous.Parameters.AddWithValue("$model", modelKey);
            previous.Parameters.AddWithValue("$source", detectionSource);
            previous.Parameters.AddWithValue("$ticks", eventTicks);
            var value = previous.ExecuteScalar();
            if (value is not null and not DBNull)
                previousTicks = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        if (previousTicks.HasValue)
        {
            var turns = new HashSet<string>(StringComparer.Ordinal);
            var tokens = 0L;
            using var interval = _connection.CreateCommand();
            interval.Transaction = transaction;
            interval.CommandText = """
                SELECT turn_key, turn_confidence, canonical_total_tokens
                FROM token_samples
                WHERE thread_id = $thread_id AND context_window = $window
                  AND COALESCE(model, '') = $model
                  AND event_time_ticks >= $start_ticks AND event_time_ticks < $end_ticks
                  AND event_order_confidence = 'reliable'
                  AND is_lineage_canonical = 1
                ORDER BY event_time_ticks, id;
                """;
            interval.Parameters.AddWithValue("$thread_id", threadId);
            interval.Parameters.AddWithValue("$window", contextWindow);
            interval.Parameters.AddWithValue("$model", modelKey);
            interval.Parameters.AddWithValue("$start_ticks", previousTicks.Value);
            interval.Parameters.AddWithValue("$end_ticks", eventTicks);
            using var reader = interval.ExecuteReader();
            while (reader.Read())
            {
                tokens = TokenComponents.SaturatingAdd(tokens, ReadLong(reader, 2));
                if (!reader.IsDBNull(0) && string.Equals(reader.GetString(1), "reliable",
                        StringComparison.Ordinal))
                    turns.Add(reader.GetString(0));
            }
            if (turns.Count > 0 && tokens > 0)
            {
                runwayTurns = turns.Count;
                runwayTokens = tokens;
            }
        }

        using (var insert = _connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO context_baseline_observations(
                    thread_id, post_sample_id, post_input_tokens, context_window, event_time_ticks,
                    model_key, detection_source, boundary_event_identity, turn_key,
                    runway_turns, runway_tokens)
                VALUES ($thread_id, $sample_id, $input, $window, $ticks, $model, $source,
                    $boundary, $turn_key, $runway_turns, $runway_tokens);
                """;
            insert.Parameters.AddWithValue("$thread_id", threadId);
            insert.Parameters.AddWithValue("$sample_id", sampleId);
            insert.Parameters.AddWithValue("$input", inputTokens);
            insert.Parameters.AddWithValue("$window", contextWindow);
            insert.Parameters.AddWithValue("$ticks", eventTicks);
            insert.Parameters.AddWithValue("$model", modelKey);
            insert.Parameters.AddWithValue("$source", detectionSource);
            insert.Parameters.AddWithValue("$boundary", (object?)boundaryEventIdentity ?? DBNull.Value);
            insert.Parameters.AddWithValue("$turn_key", turnKey);
            insert.Parameters.AddWithValue("$runway_turns", runwayTurns.HasValue
                ? runwayTurns.Value : DBNull.Value);
            insert.Parameters.AddWithValue("$runway_tokens", runwayTokens.HasValue
                ? runwayTokens.Value : DBNull.Value);
            insert.ExecuteNonQuery();
        }
        using var trim = _connection.CreateCommand();
        trim.Transaction = transaction;
        trim.CommandText = """
            DELETE FROM context_baseline_observations
            WHERE thread_id = $thread_id AND post_sample_id NOT IN (
                SELECT post_sample_id FROM context_baseline_observations
                WHERE thread_id = $thread_id
                ORDER BY event_time_ticks DESC, post_sample_id DESC LIMIT $limit
            );
            """;
        trim.Parameters.AddWithValue("$thread_id", threadId);
        trim.Parameters.AddWithValue("$limit", RetainedContextBaselines);
        trim.ExecuteNonQuery();
    }

    private PersistedTokenSample LoadPersistedTokenSample(string fingerprint, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT id, thread_id, {AggregateColumns}, cumulative_total_tokens, semantic_identity,
                   is_lineage_canonical
            FROM token_samples WHERE fingerprint = $fingerprint;
            """;
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("DUPLICATE_SAMPLE_MISSING");
        return new PersistedTokenSample(reader.GetInt64(0), reader.GetString(1), ReadAggregate(reader, 2),
            reader.GetInt64(10), reader.IsDBNull(11) ? string.Empty : reader.GetString(11),
            reader.GetInt64(12) != 0);
    }

    private void RecomputeLatestEvent(string threadId, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT thread_id, id, turn_key, event_time_ticks, source_key, source_generation,
                   source_offset, turn_confidence, event_order_confidence, {AggregateColumns}
            FROM token_samples
            WHERE thread_id = $thread_id AND is_lineage_canonical = 1
            ORDER BY id;
            """;
        command.Parameters.AddWithValue("$thread_id", threadId);
        using var reader = command.ExecuteReader();
        LatestEventState? latest = null;
        while (reader.Read())
        {
            var candidate = new LatestEventState(reader.GetString(0), reader.GetInt64(1),
                OptionalString(reader, 2), OptionalLong(reader, 3), OptionalString(reader, 4),
                reader.GetInt32(5), OptionalLong(reader, 6), reader.GetString(7), reader.GetString(8),
                ReadAggregate(reader, 9));
            if (ShouldReplaceLatest(latest, candidate)) latest = candidate;
        }
        reader.Close();
        if (latest is not null) UpsertLatestEvent(latest, transaction);
        else
        {
            using var delete = _connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM latest_token_events WHERE thread_id = $thread_id;";
            delete.Parameters.AddWithValue("$thread_id", threadId);
            delete.ExecuteNonQuery();
        }
    }

    private void RecomputeFrontier(string threadId, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT cumulative_total_tokens, id
            FROM token_samples
            WHERE thread_id = $thread_id AND is_lineage_canonical = 1
            ORDER BY cumulative_total_tokens DESC, id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$thread_id", threadId);
        using var reader = command.ExecuteReader();
        if (reader.Read())
        {
            var frontier = new FrontierState(reader.GetInt64(0), reader.GetInt64(1));
            reader.Close();
            UpsertFrontier(threadId, frontier, transaction);
            return;
        }
        reader.Close();
        using var delete = _connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM cumulative_frontiers WHERE thread_id = $thread_id;";
        delete.Parameters.AddWithValue("$thread_id", threadId);
        delete.ExecuteNonQuery();
    }

    private bool RefreshSemanticFromStored(long sampleId, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT thread_id, {AggregateColumns}, cumulative_input_tokens, cumulative_output_tokens,
                   cumulative_total_tokens, context_window, semantic_identity, semantic_material,
                   legacy_semantic_identity, lineage_root_thread_id, is_lineage_canonical, turn_key,
                   event_time_ticks, source_key, source_generation, source_offset, turn_confidence,
                   event_order_confidence
            FROM token_samples WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", sampleId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return false;
        var threadId = reader.GetString(0);
        var last = ReadAggregate(reader, 1);
        var cumulativeInput = ReadLong(reader, 9);
        var cumulativeOutput = ReadLong(reader, 10);
        var cumulativeTotal = ReadLong(reader, 11);
        var storedContext = OptionalLong(reader, 12);
        var existingIdentity = OptionalString(reader, 13) ?? string.Empty;
        var existingMaterial = OptionalString(reader, 14) ?? string.Empty;
        var existingLegacy = OptionalString(reader, 15) ?? string.Empty;
        var root = OptionalString(reader, 16) ?? string.Empty;
        var canonical = reader.GetInt64(17) != 0;
        var turnKey = OptionalString(reader, 18);
        var eventTicks = OptionalLong(reader, 19);
        var sourceKey = OptionalString(reader, 20);
        var sourceGeneration = reader.GetInt32(21);
        var sourceOffset = OptionalLong(reader, 22);
        var turnConfidence = reader.GetString(23);
        var eventOrderConfidence = reader.GetString(24);
        reader.Close();

        var material = TokenUsageSnapshot.IsLiveSemanticMaterial(existingMaterial)
            ? TokenUsageSnapshot.ReplaceLiveContextWindow(existingMaterial, storedContext)
            : TokenUsageSnapshot.LineageSemanticMaterialFromStored(last, cumulativeInput, cumulativeOutput,
                cumulativeTotal, storedContext);
        var identity = TokenUsageSnapshot.HashText(material);
        var legacyIdentity = TokenUsageSnapshot.LineageSemanticIdentityFromStored(last, cumulativeInput,
            cumulativeOutput, cumulativeTotal, storedContext);
        if (string.Equals(existingIdentity, identity, StringComparison.Ordinal) &&
            string.Equals(existingMaterial, material, StringComparison.Ordinal) &&
            string.Equals(existingLegacy, legacyIdentity, StringComparison.Ordinal))
        {
            return canonical;
        }

        var self = new CanonicalSample(sampleId, threadId, last, turnKey, eventTicks, sourceKey,
            sourceGeneration, sourceOffset, turnConfidence, eventOrderConfidence, cumulativeTotal);
        var aggregates = new AggregateAccumulator();
        var replayThreads = new HashSet<string>(StringComparer.Ordinal) { threadId };
        _canonicalParentMap = LoadParentMap(transaction);
        if (canonical) aggregates.SubtractConsumption(self);

        using (var write = _connection.CreateCommand())
        {
            write.Transaction = transaction;
            write.CommandText = """
                UPDATE token_samples
                SET semantic_identity = $identity, semantic_material = $material,
                    legacy_semantic_identity = $legacy, is_lineage_canonical = 0
                WHERE id = $id;
                """;
            write.Parameters.AddWithValue("$identity", identity);
            write.Parameters.AddWithValue("$material", material);
            write.Parameters.AddWithValue("$legacy", legacyIdentity);
            write.Parameters.AddWithValue("$id", sampleId);
            write.ExecuteNonQuery();
        }

        var oldExact = ApplyExactGroupElection(root, existingIdentity, transaction);
        ApplyLineageAggregateTransitions(oldExact, aggregates, replayThreads);
        var newExact = ApplyExactGroupElection(root, identity, transaction);
        ApplyLineageAggregateTransitions(newExact, aggregates, replayThreads);
        if (!string.IsNullOrWhiteSpace(existingLegacy))
            ApplyLineageAggregateTransitions(ApplyLegacyCompatibility(root, existingLegacy, transaction),
                aggregates, replayThreads);
        if (!string.IsNullOrWhiteSpace(legacyIdentity) &&
            !string.Equals(existingLegacy, legacyIdentity, StringComparison.Ordinal))
        {
            ApplyLineageAggregateTransitions(ApplyLegacyCompatibility(root, legacyIdentity, transaction),
                aggregates, replayThreads);
        }

        ApplyAggregateDeltas(aggregates, transaction);
        foreach (var replayThread in replayThreads)
        {
            RecomputeLatestEvent(replayThread, transaction);
            RecomputeFrontier(replayThread, transaction);
            ReplayContextForThread(replayThread, transaction);
        }
        _lifecycleInputsValidUntilUtc = DateTimeOffset.MinValue;
        return IsSampleLineageCanonical(sampleId, transaction);
    }

    private StructuralResolution ResolveStructural(ParsedEvent parsed, string threadId,
        SourceFileState source, ScanTurn scanTurn, SessionWork session, SqliteTransaction transaction)
    {
        var sourceKey = source.Identity.StableKey;
        var offset = parsed.SourceOffset ?? 0;
        var explicitTurn = string.IsNullOrWhiteSpace(parsed.TurnKey) ? null : parsed.TurnKey;
        var baseMaterial = explicitTurn is not null
            ? string.Join('|', "structural-base-v3", threadId, parsed.EventKind, "explicit", explicitTurn,
                parsed.EventTimeUtc?.UtcTicks.ToString(CultureInfo.InvariantCulture) ?? "missing-time")
            : parsed.EventTimeUtc.HasValue
                ? string.Join('|', "structural-base-v3", threadId, parsed.EventKind, "timestamp",
                    parsed.EventTimeUtc.Value.UtcTicks.ToString(CultureInfo.InvariantCulture))
                : string.Join('|', "structural-base-v3", threadId, parsed.EventKind, "implicit",
                    sourceKey, source.Generation.ToString(CultureInfo.InvariantCulture),
                    offset.ToString(CultureInfo.InvariantCulture));
        var baseIdentity = TokenUsageSnapshot.HashText(baseMaterial);
        var observationKey = TokenUsageSnapshot.HashText(string.Join('|', "structural-observation-v3",
            sourceKey, source.Generation.ToString(CultureInfo.InvariantCulture),
            offset.ToString(CultureInfo.InvariantCulture), baseIdentity));

        using (var existingObservation = _connection.CreateCommand())
        {
            existingObservation.Transaction = transaction;
            existingObservation.CommandText = """
                SELECT event_identity, association_key, canonical
                FROM structural_observations WHERE observation_key = $key;
                """;
            existingObservation.Parameters.AddWithValue("$key", observationKey);
            using var reader = existingObservation.ExecuteReader();
            if (reader.Read())
            {
                var existingAssociation = OptionalString(reader, 1);
                UpdateScanTurn(parsed, scanTurn, existingAssociation,
                    explicitTurn is not null || parsed.EventTimeUtc.HasValue);
                return new StructuralResolution(
                    new StructuralDispositionRecord(reader.GetString(0), false, existingAssociation, false),
                    existingAssociation, false, false, "structural_replay");
            }
        }

        var occurrence = 0;
        using (var count = _connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = """
                SELECT COUNT(*) FROM structural_observations
                WHERE source_key = $source_key AND source_generation = $generation
                  AND base_identity = $base_identity AND source_offset < $offset;
                """;
            count.Parameters.AddWithValue("$source_key", sourceKey);
            count.Parameters.AddWithValue("$generation", source.Generation);
            count.Parameters.AddWithValue("$base_identity", baseIdentity);
            count.Parameters.AddWithValue("$offset", offset);
            occurrence = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        var eventIdentity = baseIdentity + ":" + occurrence.ToString(CultureInfo.InvariantCulture);
        string? association = explicitTurn;
        var canonical = false;
        using (var existingCanonical = _connection.CreateCommand())
        {
            existingCanonical.Transaction = transaction;
            existingCanonical.CommandText = "SELECT association_key FROM structural_events WHERE event_identity = $id;";
            existingCanonical.Parameters.AddWithValue("$id", eventIdentity);
            using var reader = existingCanonical.ExecuteReader();
            if (reader.Read())
            {
                association = OptionalString(reader, 0);
            }
            else
            {
                reader.Close();
                association = DetermineAssociation(parsed, threadId, source, scanTurn, explicitTurn, offset);
                using var insert = _connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO structural_events(event_identity, base_identity, thread_id, event_kind,
                        association_key, event_time_utc, source_identity_hash, source_generation,
                        source_offset, occurrence)
                    VALUES ($event_identity, $base_identity, $thread_id, $event_kind, $association,
                        $event_time, $source_key, $generation, $offset, $occurrence);
                    """;
                insert.Parameters.AddWithValue("$event_identity", eventIdentity);
                insert.Parameters.AddWithValue("$base_identity", baseIdentity);
                insert.Parameters.AddWithValue("$thread_id", threadId);
                insert.Parameters.AddWithValue("$event_kind", parsed.EventKind);
                insert.Parameters.AddWithValue("$association", (object?)association ?? DBNull.Value);
                insert.Parameters.AddWithValue("$event_time", parsed.EventTimeUtc.HasValue
                    ? Iso(parsed.EventTimeUtc.Value) : DBNull.Value);
                insert.Parameters.AddWithValue("$source_key", sourceKey);
                insert.Parameters.AddWithValue("$generation", source.Generation);
                insert.Parameters.AddWithValue("$offset", offset);
                insert.Parameters.AddWithValue("$occurrence", occurrence);
                insert.ExecuteNonQuery();
                canonical = true;
            }
        }

        using (var observation = _connection.CreateCommand())
        {
            observation.Transaction = transaction;
            observation.CommandText = """
                INSERT INTO structural_observations(observation_key, source_key, source_generation,
                    source_offset, base_identity, event_identity, association_key, canonical, disposition)
                VALUES ($key, $source_key, $generation, $offset, $base_identity, $event_identity,
                    $association, $canonical, $disposition);
                """;
            observation.Parameters.AddWithValue("$key", observationKey);
            observation.Parameters.AddWithValue("$source_key", sourceKey);
            observation.Parameters.AddWithValue("$generation", source.Generation);
            observation.Parameters.AddWithValue("$offset", offset);
            observation.Parameters.AddWithValue("$base_identity", baseIdentity);
            observation.Parameters.AddWithValue("$event_identity", eventIdentity);
            observation.Parameters.AddWithValue("$association", (object?)association ?? DBNull.Value);
            observation.Parameters.AddWithValue("$canonical", canonical ? 1 : 0);
            observation.Parameters.AddWithValue("$disposition", canonical ? "canonical" : "replay");
            observation.ExecuteNonQuery();
        }

        var reliable = explicitTurn is not null || parsed.EventTimeUtc.HasValue;
        UpdateScanTurn(parsed, scanTurn, association, reliable);
        var advances = canonical && reliable && CanAdvanceState(session, parsed.EventTimeUtc?.UtcTicks,
            sourceKey, source.Generation, offset, explicitTurn is not null);
        return new StructuralResolution(new StructuralDispositionRecord(eventIdentity, canonical,
            association, advances), association, canonical, advances,
            reliable ? null : "structural_order_ambiguous");
    }

    private static string? DetermineAssociation(ParsedEvent parsed, string threadId, SourceFileState source,
        ScanTurn scanTurn, string? explicitTurn, long offset)
    {
        if (explicitTurn is not null) return explicitTurn;
        if (parsed.TaskStarted == true)
        {
            var digest = TokenUsageSnapshot.HashText(string.Join('|', "implicit-turn-v3", threadId,
                source.Identity.StableKey, source.Generation.ToString(CultureInfo.InvariantCulture),
                offset.ToString(CultureInfo.InvariantCulture)));
            return "implicit-" + digest[..20];
        }
        return scanTurn.Key;
    }

    private static void UpdateScanTurn(ParsedEvent parsed, ScanTurn scanTurn, string? association, bool reliable)
    {
        if (parsed.TaskStarted == true || parsed.EventKind == "turn_context")
        {
            if (association is not null) scanTurn.Key = association;
            scanTurn.Reliable = reliable;
        }
        else if (parsed.TaskStarted == false && association is not null)
        {
            scanTurn.Key = association;
            scanTurn.Reliable = reliable;
        }
    }

    private static void ApplyStructuralState(ParsedEvent parsed, StructuralResolution resolution,
        SessionWork session, ScanTurn scanTurn, SourceFileState source, DateTimeOffset observedAt)
    {
        if (!resolution.Advanced) return;
        var association = resolution.AssociationKey;
        if (parsed.TaskStarted == true && association is not null)
        {
            if (!string.Equals(session.CurrentTurnKey, association, StringComparison.Ordinal)) session.TurnSequence++;
            session.CurrentTurnKey = association;
            session.CurrentTurnReliable = true;
            session.TurnOpen = true;
        }
        else if (parsed.TaskStarted == false)
        {
            if (association is not null) session.CurrentTurnKey = association;
            session.CurrentTurnReliable = true;
            session.TurnOpen = false;
        }
        else if (parsed.EventKind == "turn_context" && association is not null)
        {
            session.CurrentTurnKey = association;
            session.CurrentTurnReliable = true;
        }

        var ticks = parsed.EventTimeUtc?.UtcTicks;
        AdvanceOrderedMetadata(session, parsed, source, parsed.SourceOffset ?? 0, ticks,
            updateActivity: ticks.HasValue);
        session.StructuralCursor = new StructuralCursor(resolution.Disposition.EventIdentity,
            source.Identity.StableKey, source.Generation, parsed.SourceOffset ?? 0, parsed.EventTimeUtc);
        session.Metadata = session.Metadata with
        {
            StoredStatus = StatusFor(session.TurnOpen, session.Metadata.LastActivityUtc, observedAt),
        };
        session.Dirty = true;
        scanTurn.Key = association ?? scanTurn.Key;
    }

    private static void AdvanceOrderedMetadata(SessionWork session, ParsedEvent parsed, SourceFileState source,
        long offset, long? eventTicks, bool updateActivity)
    {
        var normalizedTier = parsed.ServiceTier is null
            ? null : ServiceTierResolver.Resolve(parsed.ServiceTier, parsed.Model ?? session.Metadata.Model);
        session.Metadata = session.Metadata with
        {
            Model = parsed.Model ?? session.Metadata.Model,
            ReasoningEffort = parsed.ReasoningEffort ?? session.Metadata.ReasoningEffort,
            ServiceTier = normalizedTier ?? session.Metadata.ServiceTier,
            ServiceTierSource = normalizedTier is null
                ? session.Metadata.ServiceTierSource
                : ServiceTierProvenance.RolloutExplicit,
            LastActivityUtc = updateActivity && eventTicks.HasValue
                ? Max(session.Metadata.LastActivityUtc, new DateTimeOffset(eventTicks.Value, TimeSpan.Zero))
                : session.Metadata.LastActivityUtc,
        };
        session.StateEventTimeTicks = eventTicks;
        session.StateSourceKey = source.Identity.StableKey;
        session.StateSourceGeneration = source.Generation;
        session.StateSourceOffset = offset;
        session.StateEventKind = parsed.EventKind;
        session.Dirty = true;
    }

    private static bool CanAdvanceState(SessionWork session, long? eventTicks, string sourceKey,
        int generation, long offset, bool reliableWithoutTime)
    {
        if (session.StateSourceKey is null) return eventTicks.HasValue || reliableWithoutTime;
        if (eventTicks.HasValue && session.StateEventTimeTicks.HasValue)
        {
            var comparison = eventTicks.Value.CompareTo(session.StateEventTimeTicks.Value);
            if (comparison > 0) return true;
            if (comparison < 0) return false;
            return string.Equals(session.StateSourceKey, sourceKey, StringComparison.Ordinal) &&
                   session.StateSourceGeneration == generation && offset > session.StateSourceOffset;
        }
        if (eventTicks.HasValue) return true;
        if (session.StateEventTimeTicks.HasValue || !reliableWithoutTime) return false;
        return string.Equals(session.StateSourceKey, sourceKey, StringComparison.Ordinal) &&
               session.StateSourceGeneration == generation && offset > session.StateSourceOffset;
    }

    private void ApplyAggregateDeltas(AggregateAccumulator accumulator, SqliteTransaction transaction)
    {
        foreach (var pair in accumulator.SessionDeltas) UpsertSessionAggregate(pair.Key, pair.Value, transaction);
        foreach (var pair in accumulator.TurnDeltas) UpsertTurnAggregate(pair.Key.ThreadId, pair.Key.TurnKey,
            pair.Value, transaction);
        foreach (var pair in accumulator.BucketDeltas) UpsertBucketAggregate(pair.Key, pair.Value, transaction);
        foreach (var pair in accumulator.Frontiers) UpsertFrontier(pair.Key, pair.Value, transaction);
        foreach (var pair in accumulator.Latest) UpsertLatestEvent(pair.Value, transaction);

        using var cycle = _connection.CreateCommand();
        cycle.Transaction = transaction;
        cycle.CommandText = $"SELECT cycle_start_ticks, cycle_end_ticks, {AggregateColumns} FROM active_cycle_aggregate WHERE singleton = 1;";
        using var reader = cycle.ExecuteReader();
        if (!reader.Read()) return;
        var start = reader.GetInt64(0);
        var end = reader.GetInt64(1);
        var current = ReadAggregate(reader, 2);
        reader.Close();
        var delta = accumulator.TimedDeltas.Where(item => item.EventTimeTicks >= start && item.EventTimeTicks < end)
            .Aggregate(default(CanonicalTokenUsage), (total, item) => total + item.Usage);
        if (delta != default) UpsertActiveCycle(start, end, current + delta, transaction);
    }

    private void UpsertSessionAggregate(string threadId, CanonicalTokenUsage delta,
        SqliteTransaction transaction)
    {
        var current = ReadAggregateRow("session_token_aggregates", "thread_id = $thread_id", transaction,
            ("$thread_id", threadId));
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO session_token_aggregates(thread_id, {AggregateColumns})
            VALUES ($thread_id, {AggregateParameterColumns})
            ON CONFLICT(thread_id) DO UPDATE SET {AggregateUpdateColumns};
            """;
        command.Parameters.AddWithValue("$thread_id", threadId);
        AddUsageParameters(command, current + delta);
        command.ExecuteNonQuery();
    }

    private void UpsertTurnAggregate(string threadId, string turnKey, CanonicalTokenUsage delta,
        SqliteTransaction transaction)
    {
        var current = ReadAggregateRow("turn_token_aggregates",
            "thread_id = $thread_id AND turn_key = $turn_key", transaction,
            ("$thread_id", threadId), ("$turn_key", turnKey));
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO turn_token_aggregates(thread_id, turn_key, {AggregateColumns})
            VALUES ($thread_id, $turn_key, {AggregateParameterColumns})
            ON CONFLICT(thread_id, turn_key) DO UPDATE SET {AggregateUpdateColumns};
            """;
        command.Parameters.AddWithValue("$thread_id", threadId);
        command.Parameters.AddWithValue("$turn_key", turnKey);
        AddUsageParameters(command, current + delta);
        command.ExecuteNonQuery();
    }

    private void UpsertBucketAggregate(long bucketStart, CanonicalTokenUsage delta,
        SqliteTransaction transaction)
    {
        var current = ReadAggregateRow("token_time_buckets", "bucket_start_ticks = $bucket", transaction,
            ("$bucket", bucketStart));
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO token_time_buckets(bucket_start_ticks, {AggregateColumns})
            VALUES ($bucket, {AggregateParameterColumns})
            ON CONFLICT(bucket_start_ticks) DO UPDATE SET {AggregateUpdateColumns};
            """;
        command.Parameters.AddWithValue("$bucket", bucketStart);
        AddUsageParameters(command, current + delta);
        command.ExecuteNonQuery();
    }

    private void UpsertFrontier(string threadId, FrontierState frontier, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO cumulative_frontiers(thread_id, maximum_cumulative_total, sample_id)
            VALUES ($thread_id, $maximum, $sample_id)
            ON CONFLICT(thread_id) DO UPDATE SET
                maximum_cumulative_total = excluded.maximum_cumulative_total,
                sample_id = excluded.sample_id;
            """;
        command.Parameters.AddWithValue("$thread_id", threadId);
        command.Parameters.AddWithValue("$maximum", frontier.Maximum);
        command.Parameters.AddWithValue("$sample_id", frontier.SampleId);
        command.ExecuteNonQuery();
    }

    private void UpsertLatestEvent(LatestEventState latest, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO latest_token_events(thread_id, sample_id, turn_key, event_time_ticks,
                source_key, source_generation, source_offset, turn_confidence, event_order_confidence,
                {AggregateColumns})
            VALUES ($thread_id, $sample_id, $turn_key, $event_time_ticks, $source_key,
                $source_generation, $source_offset, $turn_confidence, $event_order_confidence,
                {AggregateParameterColumns})
            ON CONFLICT(thread_id) DO UPDATE SET
                sample_id = excluded.sample_id,
                turn_key = excluded.turn_key,
                event_time_ticks = excluded.event_time_ticks,
                source_key = excluded.source_key,
                source_generation = excluded.source_generation,
                source_offset = excluded.source_offset,
                turn_confidence = excluded.turn_confidence,
                event_order_confidence = excluded.event_order_confidence,
                {AggregateUpdateColumns};
            """;
        command.Parameters.AddWithValue("$thread_id", latest.ThreadId);
        command.Parameters.AddWithValue("$sample_id", latest.SampleId);
        command.Parameters.AddWithValue("$turn_key", (object?)latest.TurnKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$event_time_ticks", latest.EventTimeTicks.HasValue
            ? latest.EventTimeTicks.Value : DBNull.Value);
        command.Parameters.AddWithValue("$source_key", (object?)latest.SourceKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$source_generation", latest.SourceGeneration);
        command.Parameters.AddWithValue("$source_offset", latest.SourceOffset.HasValue
            ? latest.SourceOffset.Value : DBNull.Value);
        command.Parameters.AddWithValue("$turn_confidence", latest.TurnConfidence);
        command.Parameters.AddWithValue("$event_order_confidence", latest.EventOrderConfidence);
        AddUsageParameters(command, latest.Usage);
        command.ExecuteNonQuery();
    }

    private void UpsertActiveCycle(long start, long end, CanonicalTokenUsage total,
        SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO active_cycle_aggregate(singleton, cycle_start_ticks, cycle_end_ticks, {AggregateColumns})
            VALUES (1, $start, $end, {AggregateParameterColumns})
            ON CONFLICT(singleton) DO UPDATE SET cycle_start_ticks = excluded.cycle_start_ticks,
                cycle_end_ticks = excluded.cycle_end_ticks, {AggregateUpdateColumns};
            """;
        command.Parameters.AddWithValue("$start", start);
        command.Parameters.AddWithValue("$end", end);
        AddUsageParameters(command, total);
        command.ExecuteNonQuery();
    }

    private CanonicalTokenUsage ReadAggregateRow(string table, string predicate,
        SqliteTransaction transaction, params (string Name, object Value)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {AggregateColumns} FROM {QuoteIdentifier(table)} WHERE {predicate};";
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadAggregate(reader) : default;
    }

    private Dictionary<string, CanonicalTokenUsage> LoadSessionTotalsCore()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT thread_id, {AggregateColumns} FROM session_token_aggregates ORDER BY thread_id;";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, CanonicalTokenUsage>(StringComparer.Ordinal);
        while (reader.Read()) result[reader.GetString(0)] = ReadAggregate(reader, 1);
        return result;
    }

    private Dictionary<(string ThreadId, string TurnKey), CanonicalTokenUsage> LoadTurnTotalsCore()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT thread_id, turn_key, {AggregateColumns} FROM turn_token_aggregates ORDER BY thread_id, turn_key;";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<(string, string), CanonicalTokenUsage>();
        while (reader.Read()) result[(reader.GetString(0), reader.GetString(1))] = ReadAggregate(reader, 2);
        return result;
    }

    private Dictionary<(string ThreadId, string TurnKey), CanonicalTokenUsage> LoadRecurringTurnTotalsCore()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = RecurringTurnTotalsSql;
        using var reader = command.ExecuteReader();
        var result = new Dictionary<(string, string), CanonicalTokenUsage>();
        while (reader.Read())
        {
            result[(reader.GetString(0), reader.GetString(1))] = ReadAggregate(reader, 2);
            _recurringTurnRowsRead++;
        }
        return result;
    }

    private Dictionary<string, LatestEventState> LoadLatestEvents()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"""
            SELECT thread_id, sample_id, turn_key, event_time_ticks, source_key, source_generation,
                   source_offset, turn_confidence, event_order_confidence, {AggregateColumns}
            FROM latest_token_events ORDER BY thread_id;
            """;
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, LatestEventState>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var latest = new LatestEventState(reader.GetString(0), reader.GetInt64(1), OptionalString(reader, 2),
                OptionalLong(reader, 3), OptionalString(reader, 4), reader.GetInt32(5), OptionalLong(reader, 6),
                reader.GetString(7), reader.GetString(8), ReadAggregate(reader, 9));
            result[latest.ThreadId] = latest;
        }
        return result;
    }

    private Dictionary<string, SessionLifecycleMetrics> LoadLifecycleMetricsCore(
        IReadOnlyList<SessionWork> sessions,
        IReadOnlyDictionary<string, CanonicalTokenUsage> totals,
        DateTimeOffset nowUtc)
    {
        if (nowUtc >= _lifecycleInputsValidUntilUtc)
        {
            var refreshedTurnCounts = new Dictionary<string, long>(StringComparer.Ordinal);
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT thread_id, COUNT(*)
                    FROM turn_token_aggregates
                    GROUP BY thread_id ORDER BY thread_id;
                    """;
                using var reader = command.ExecuteReader();
                while (reader.Read()) refreshedTurnCounts[reader.GetString(0)] = reader.GetInt64(1);
            }

            var refreshedRecent = new Dictionary<string, RecentSessionActivity>(StringComparer.Ordinal);
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT thread_id, TOTAL(canonical_total_tokens),
                           COUNT(DISTINCT CASE WHEN turn_confidence = 'reliable' THEN turn_key END)
                    FROM token_samples INDEXED BY ix_token_event_ticks_id
                    WHERE event_time_ticks >= $cutoff AND event_time_ticks <= $now
                      AND is_lineage_canonical = 1
                    GROUP BY thread_id ORDER BY thread_id;
                    """;
                command.Parameters.AddWithValue("$cutoff", nowUtc.Subtract(TimeSpan.FromHours(48)).UtcTicks);
                command.Parameters.AddWithValue("$now", nowUtc.UtcTicks);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    refreshedRecent[reader.GetString(0)] = new RecentSessionActivity(
                        ClampDoubleToLong(reader.GetDouble(1)), reader.GetInt64(2));
            }

            _cachedLifecycleTurnCounts = refreshedTurnCounts;
            _cachedRecentSessionActivity = refreshedRecent;
            _lifecycleInputsValidUntilUtc = nowUtc.AddMinutes(1);
        }
        var turnCounts = _cachedLifecycleTurnCounts;
        var recent = _cachedRecentSessionActivity;

        var result = new Dictionary<string, SessionLifecycleMetrics>(StringComparer.Ordinal);
        var comparableGroups = sessions
            .Where(item => item.Metadata.Kind == SessionKind.Primary &&
                           !string.IsNullOrWhiteSpace(item.Metadata.Model) &&
                           totals.GetValueOrDefault(item.Metadata.ThreadId).Total > 0)
            .GroupBy(item => item.Metadata.Model!, StringComparer.Ordinal);
        foreach (var group in comparableGroups)
        {
            var candidates = group.ToArray();
            var lifetimeTokens = candidates.Select(item =>
                totals.GetValueOrDefault(item.Metadata.ThreadId).Total).ToArray();
            var lifetimeTurns = candidates.Select(item =>
                turnCounts.GetValueOrDefault(item.Metadata.ThreadId)).ToArray();
            var recentTokens = candidates.Select(item =>
                recent.GetValueOrDefault(item.Metadata.ThreadId).TotalTokens).ToArray();
            var recentTurns = candidates.Select(item =>
                recent.GetValueOrDefault(item.Metadata.ThreadId).TurnCount).ToArray();
            var lifetimeTokenMedian = PositiveMedian(lifetimeTokens);
            var lifetimeTurnMedian = PositiveMedian(lifetimeTurns);
            var recentTokenMedian = PositiveMedian(recentTokens);
            var recentTurnMedian = PositiveMedian(recentTurns);

            foreach (var item in candidates)
            {
                var threadId = item.Metadata.ThreadId;
                var total = totals.GetValueOrDefault(threadId).Total;
                var turns = turnCounts.GetValueOrDefault(threadId);
                var recentActivity = recent.GetValueOrDefault(threadId);
                result[threadId] = new SessionLifecycleMetrics(turns, recentActivity.TotalTokens,
                    recentActivity.TurnCount, candidates.Length,
                    DescendingRank(lifetimeTokens, total), DescendingRank(lifetimeTurns, turns),
                    DescendingRank(recentTokens, recentActivity.TotalTokens),
                    DescendingRank(recentTurns, recentActivity.TurnCount),
                    MultipleOfMedian(total, lifetimeTokenMedian), MultipleOfMedian(turns, lifetimeTurnMedian),
                    MultipleOfMedian(recentActivity.TotalTokens, recentTokenMedian),
                    MultipleOfMedian(recentActivity.TurnCount, recentTurnMedian));
            }
        }
        return result;
    }

    private Dictionary<string, SessionDriftAssessment> LoadSessionDriftAssessmentsCore()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT thread_id, assessment_level, observed_at_utc
            FROM session_drift_assessments ORDER BY thread_id;
            """;
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, SessionDriftAssessment>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var level = (DriftAssessmentLevel)reader.GetInt32(1);
            if (level is not (DriftAssessmentLevel.Occasional or DriftAssessmentLevel.Repeated)) continue;
            result[reader.GetString(0)] = new SessionDriftAssessment(level, OptionalDate(reader, 2));
        }
        return result;
    }

    private static int DescendingRank(IEnumerable<long> values, long value) =>
        values.Count(candidate => candidate > value) + 1;

    private static double? PositiveMedian(IEnumerable<long> source)
    {
        var values = source.Where(value => value > 0).Order().ToArray();
        if (values.Length == 0) return null;
        return values.Length % 2 == 1 ? values[values.Length / 2]
            : values[values.Length / 2 - 1] / 2d + values[values.Length / 2] / 2d;
    }

    private static double? MultipleOfMedian(long value, double? median) =>
        median is > 0d ? value / median.Value : null;

    private static long ClampDoubleToLong(double value) => value switch
    {
        >= long.MaxValue => long.MaxValue,
        <= 0d => 0L,
        _ => (long)Math.Round(value, MidpointRounding.AwayFromZero),
    };

    private Dictionary<string, SessionContextMetrics> LoadContextMetricsCore()
    {
        var states = new Dictionary<string, (long? Window, string? Model)>(StringComparer.Ordinal);
        using (var stateCommand = _connection.CreateCommand())
        {
            stateCommand.CommandText = """
                SELECT thread_id, current_context_window, current_model
                FROM session_context_state ORDER BY thread_id;
                """;
            using var stateReader = stateCommand.ExecuteReader();
            while (stateReader.Read())
                states[stateReader.GetString(0)] = (OptionalLong(stateReader, 1), OptionalString(stateReader, 2));
        }

        var baselines = new Dictionary<string, List<ContextBaseline>>(StringComparer.Ordinal);
        using (var baselineCommand = _connection.CreateCommand())
        {
            baselineCommand.CommandText = """
                SELECT thread_id, post_sample_id, post_input_tokens, context_window,
                       event_time_ticks, model_key, detection_source, runway_turns, runway_tokens
                FROM context_baseline_observations
                ORDER BY thread_id, event_time_ticks, post_sample_id;
                """;
            using var reader = baselineCommand.ExecuteReader();
            while (reader.Read())
            {
                var threadId = reader.GetString(0);
                if (!baselines.TryGetValue(threadId, out var list))
                {
                    list = new List<ContextBaseline>(RetainedContextBaselines);
                    baselines[threadId] = list;
                }
                list.Add(new ContextBaseline(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
                    reader.GetInt64(4), reader.GetString(5), reader.GetString(6),
                    OptionalLong(reader, 7), OptionalLong(reader, 8)));
            }
        }

        var result = new Dictionary<string, SessionContextMetrics>(StringComparer.Ordinal);
        foreach (var pair in states)
        {
            baselines.TryGetValue(pair.Key, out var observations);
            observations ??= new List<ContextBaseline>();
            var segment = pair.Value.Window is > 0 && !string.IsNullOrWhiteSpace(pair.Value.Model)
                ? observations.Where(item => item.ContextWindow == pair.Value.Window.Value &&
                                             string.Equals(item.ModelKey, pair.Value.Model,
                                                 StringComparison.Ordinal)).ToList()
                : new List<ContextBaseline>();
            var explicitGroup = segment.Where(item => item.DetectionSource == "explicit").ToList();
            var heuristicGroup = segment.Where(item => item.DetectionSource == "heuristic").ToList();
            var selected = explicitGroup.Count >= 3 ? explicitGroup
                : heuristicGroup.Count >= 3 ? heuristicGroup
                : explicitGroup.Count >= heuristicGroup.Count ? explicitGroup : heuristicGroup;
            var latestThree = selected.TakeLast(3).ToArray();
            ContextBaseline? representative = null;
            double? baselineTrend = null;
            double? turnRunwayChange = null;
            double? tokenRunwayChange = null;
            if (latestThree.Length == 3)
            {
                representative = latestThree.OrderBy(item => item.Ratio).ElementAt(1);
                baselineTrend = latestThree[^1].Ratio - latestThree[0].Ratio;
                turnRunwayChange = CalculateRunwayChange(latestThree, item => item.RunwayTurns);
                tokenRunwayChange = CalculateRunwayChange(latestThree, item => item.RunwayTokens);
            }
            result[pair.Key] = new SessionContextMetrics(representative?.InputTokens,
                representative?.ContextWindow, selected.Count, baselineTrend, turnRunwayChange,
                tokenRunwayChange, selected.Count > 0 && selected.All(item =>
                    item.DetectionSource == "explicit"));
        }
        return result;
    }

    private static double? CalculateRunwayChange(IReadOnlyList<ContextBaseline> observations,
        Func<ContextBaseline, long?> selector)
    {
        if (observations.Count < 3 || selector(observations[^1]) is not > 0) return null;
        var previous = observations.Take(observations.Count - 1).Select(selector)
            .Where(value => value is > 0).Select(value => (double)value!.Value).Order().ToArray();
        if (previous.Length == 0) return null;
        var median = previous.Length % 2 == 1 ? previous[previous.Length / 2]
            : (previous[previous.Length / 2 - 1] + previous[previous.Length / 2]) / 2d;
        return median <= 0d ? null : (selector(observations[^1])!.Value / median - 1d) * 100d;
    }

    private List<SessionWork> LoadSessionWorks()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = SessionWorkSelect + " ORDER BY thread_id;";
        using var reader = command.ExecuteReader();
        var result = new List<SessionWork>();
        while (reader.Read()) result.Add(ReadSessionWork(reader));
        return result;
    }

    private SessionWork GetSessionWork(string threadId, IDictionary<string, SessionWork> cache,
        SqliteTransaction transaction)
    {
        if (cache.TryGetValue(threadId, out var cached)) return cached;
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SessionWorkSelect + " WHERE thread_id = $thread_id;";
        command.Parameters.AddWithValue("$thread_id", threadId);
        using var reader = command.ExecuteReader();
        var work = reader.Read() ? ReadSessionWork(reader) : SessionWork.Create(threadId);
        cache[threadId] = work;
        return work;
    }

    private static ScanTurn GetScanTurn(string threadId, IDictionary<string, ScanTurn> cache,
        SessionWork session, string? sourceCursorTurn)
    {
        if (cache.TryGetValue(threadId, out var turn)) return turn;
        turn = new ScanTurn(sourceCursorTurn ?? session.CurrentTurnKey,
            sourceCursorTurn is not null || session.CurrentTurnReliable);
        cache[threadId] = turn;
        return turn;
    }

    private void UpsertSessionCore(SessionWork work, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO sessions(thread_id, display_name, role, nickname, project_tag, model,
                reasoning_effort, service_tier, service_tier_source, last_activity_utc, status, current_turn_key,
                current_turn_reliable, turn_sequence, turn_open, pinned,
                current_structural_event_identity, current_structural_source_hash,
                current_structural_generation, current_structural_offset, current_structural_time_utc,
                state_event_time_ticks, state_source_key, state_source_generation,
                state_source_offset, state_event_kind, session_kind, session_surface,
                parent_thread_id, agent_depth)
            VALUES ($thread_id, $display_name, $role, $nickname, $project_tag, $model,
                $reasoning_effort, $service_tier, $service_tier_source, $last_activity, $status, $turn_key,
                $turn_reliable, $turn_sequence, $turn_open, $pinned, $structural_event_identity,
                $structural_source_hash, $structural_generation, $structural_offset,
                $structural_time_utc, $state_time_ticks, $state_source_key, $state_generation,
                $state_offset, $state_event_kind, $session_kind, $session_surface,
                $parent_thread_id, $agent_depth)
            ON CONFLICT(thread_id) DO UPDATE SET
                display_name = excluded.display_name, role = excluded.role, nickname = excluded.nickname,
                project_tag = excluded.project_tag, model = excluded.model,
                reasoning_effort = excluded.reasoning_effort, service_tier = excluded.service_tier,
                service_tier_source = excluded.service_tier_source,
                last_activity_utc = excluded.last_activity_utc, status = excluded.status,
                current_turn_key = excluded.current_turn_key,
                current_turn_reliable = excluded.current_turn_reliable,
                turn_sequence = excluded.turn_sequence, turn_open = excluded.turn_open,
                pinned = excluded.pinned,
                current_structural_event_identity = excluded.current_structural_event_identity,
                current_structural_source_hash = excluded.current_structural_source_hash,
                current_structural_generation = excluded.current_structural_generation,
                current_structural_offset = excluded.current_structural_offset,
                current_structural_time_utc = excluded.current_structural_time_utc,
                state_event_time_ticks = excluded.state_event_time_ticks,
                state_source_key = excluded.state_source_key,
                state_source_generation = excluded.state_source_generation,
                state_source_offset = excluded.state_source_offset,
                state_event_kind = excluded.state_event_kind,
                session_kind = excluded.session_kind,
                session_surface = excluded.session_surface,
                parent_thread_id = excluded.parent_thread_id,
                agent_depth = excluded.agent_depth;
            """;
        command.Parameters.AddWithValue("$thread_id", work.Metadata.ThreadId);
        command.Parameters.AddWithValue("$display_name", (object?)work.Metadata.DisplayName ?? DBNull.Value);
        command.Parameters.AddWithValue("$role", (object?)work.Metadata.Role ?? DBNull.Value);
        command.Parameters.AddWithValue("$nickname", (object?)work.Metadata.Nickname ?? DBNull.Value);
        command.Parameters.AddWithValue("$project_tag", (object?)work.Metadata.ProjectTag ?? DBNull.Value);
        command.Parameters.AddWithValue("$model", (object?)work.Metadata.Model ?? DBNull.Value);
        command.Parameters.AddWithValue("$reasoning_effort", (object?)work.Metadata.ReasoningEffort ?? DBNull.Value);
        command.Parameters.AddWithValue("$service_tier", (object?)work.Metadata.ServiceTier ?? DBNull.Value);
        command.Parameters.AddWithValue("$service_tier_source", work.Metadata.ServiceTierSource);
        command.Parameters.AddWithValue("$last_activity", work.Metadata.LastActivityUtc.HasValue
            ? Iso(work.Metadata.LastActivityUtc.Value) : DBNull.Value);
        command.Parameters.AddWithValue("$status", work.Metadata.StoredStatus.ToString());
        command.Parameters.AddWithValue("$turn_key", (object?)work.CurrentTurnKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$turn_reliable", work.CurrentTurnReliable ? 1 : 0);
        command.Parameters.AddWithValue("$turn_sequence", Math.Max(0, work.TurnSequence));
        command.Parameters.AddWithValue("$turn_open", work.TurnOpen ? 1 : 0);
        command.Parameters.AddWithValue("$pinned", work.Metadata.Pinned ? 1 : 0);
        command.Parameters.AddWithValue("$structural_event_identity",
            (object?)work.StructuralCursor?.EventIdentity ?? DBNull.Value);
        command.Parameters.AddWithValue("$structural_source_hash",
            (object?)work.StructuralCursor?.SourceIdentityHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$structural_generation", work.StructuralCursor?.SourceGeneration ?? 0);
        command.Parameters.AddWithValue("$structural_offset", work.StructuralCursor?.SourceOffset ?? -1);
        command.Parameters.AddWithValue("$structural_time_utc", work.StructuralCursor?.EventTimeUtc is { } time
            ? Iso(time) : DBNull.Value);
        command.Parameters.AddWithValue("$state_time_ticks", work.StateEventTimeTicks.HasValue
            ? work.StateEventTimeTicks.Value : DBNull.Value);
        command.Parameters.AddWithValue("$state_source_key", (object?)work.StateSourceKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$state_generation", work.StateSourceGeneration);
        command.Parameters.AddWithValue("$state_offset", work.StateSourceOffset);
        command.Parameters.AddWithValue("$state_event_kind", (object?)work.StateEventKind ?? DBNull.Value);
        command.Parameters.AddWithValue("$session_kind", work.Metadata.Kind.ToString());
        command.Parameters.AddWithValue("$session_surface", work.Metadata.Surface.ToString());
        command.Parameters.AddWithValue("$parent_thread_id", (object?)work.Metadata.ParentThreadId ?? DBNull.Value);
        command.Parameters.AddWithValue("$agent_depth", work.Metadata.AgentDepth.HasValue
            ? work.Metadata.AgentDepth.Value : DBNull.Value);
        command.ExecuteNonQuery();
        work.Exists = true;
    }

    private void SaveSourceStateCore(SourceFileState state, SqliteTransaction transaction)
    {
        var cursor = state.Cursor.Normalize();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO source_files(source_key, thread_id, volume_serial, file_id, fallback_digest,
                is_degraded, relative_path, generation, complete_offset, drain_mode,
                drain_line_start_offset, drain_offset, last_length, last_write_utc_ticks,
                cursor_turn_key, health_code, state_revision)
            VALUES ($source_key, $thread_id, $volume_serial, $file_id, $fallback_digest,
                $is_degraded, $relative_path, $generation, $complete_offset, $drain_mode,
                $drain_line_start, $drain_offset, $last_length, $last_write_ticks,
                $cursor_turn_key, $health_code, $state_revision)
            ON CONFLICT(source_key) DO UPDATE SET
                relative_path = excluded.relative_path, generation = excluded.generation,
                complete_offset = excluded.complete_offset, drain_mode = excluded.drain_mode,
                drain_line_start_offset = excluded.drain_line_start_offset,
                drain_offset = excluded.drain_offset, last_length = excluded.last_length,
                last_write_utc_ticks = excluded.last_write_utc_ticks,
                cursor_turn_key = excluded.cursor_turn_key, health_code = excluded.health_code,
                state_revision = excluded.state_revision;
            """;
        command.Parameters.AddWithValue("$source_key", state.Identity.StableKey);
        command.Parameters.AddWithValue("$thread_id", state.Identity.ThreadId);
        command.Parameters.AddWithValue("$volume_serial", state.Identity.VolumeSerial);
        command.Parameters.AddWithValue("$file_id", state.Identity.FileId);
        command.Parameters.AddWithValue("$fallback_digest", state.Identity.FallbackDigest);
        command.Parameters.AddWithValue("$is_degraded", state.Identity.IsDegraded ? 1 : 0);
        command.Parameters.AddWithValue("$relative_path", SafeRelativePath(state.RelativePath));
        command.Parameters.AddWithValue("$generation", state.Generation);
        command.Parameters.AddWithValue("$complete_offset", cursor.CompleteOffset);
        command.Parameters.AddWithValue("$drain_mode", cursor.DrainMode ? 1 : 0);
        command.Parameters.AddWithValue("$drain_line_start", cursor.DrainLineStartOffset);
        command.Parameters.AddWithValue("$drain_offset", cursor.DrainOffset);
        command.Parameters.AddWithValue("$last_length", state.LastObservedLength);
        command.Parameters.AddWithValue("$last_write_ticks", state.LastWriteTimeUtcTicks);
        command.Parameters.AddWithValue("$cursor_turn_key", (object?)state.CursorTurnKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$health_code", state.HealthCode);
        command.Parameters.AddWithValue("$state_revision", state.StateRevision);
        command.ExecuteNonQuery();
    }

    private bool SourceExpectationMatches(SourceStateExpectation expected, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT generation, complete_offset, drain_mode, drain_line_start_offset, drain_offset,
                   state_revision FROM source_files WHERE source_key = $source_key;
            """;
        command.Parameters.AddWithValue("$source_key", expected.SourceKey);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return !expected.Exists;
        if (!expected.Exists) return false;
        var cursor = new SourceReadCursor(reader.GetInt64(1), reader.GetInt64(2) != 0,
            reader.GetInt64(3), reader.GetInt64(4)).Normalize();
        return reader.GetInt32(0) == expected.Generation && cursor == expected.Cursor.Normalize() &&
               reader.GetInt64(5) == expected.StateRevision;
    }

    private static ScanCommitResult NotCommitted(string code) => new(false, null, 0, 0, 0, 0, 0,
        new[] { code }, Array.Empty<SessionMetadata>(), Array.Empty<TokenDispositionRecord>(),
        Array.Empty<StructuralDispositionRecord>());

    private bool TokenSampleExists(string fingerprint, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM token_samples WHERE fingerprint = $fingerprint LIMIT 1;";
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        return command.ExecuteScalar() is not null;
    }

    private void InsertParserError(ParserErrorRecord error, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO parser_errors(relative_path, offset, error_code, observed_at_utc)
            VALUES ($path, $offset, $error, $observed);
            """;
        command.Parameters.AddWithValue("$path", SafeRelativePath(error.RelativePath));
        command.Parameters.AddWithValue("$offset", Math.Max(0, error.Offset));
        command.Parameters.AddWithValue("$error", error.ErrorCode);
        command.Parameters.AddWithValue("$observed", Iso(error.ObservedAtUtc));
        command.ExecuteNonQuery();
    }

    private void InsertQuotaBucket(QuotaBucket bucket, bool isPrimary, QuotaObservation observation,
        SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO quota_observations(bucket_id, bucket_name, used_percent,
                window_duration_minutes, resets_at_utc, observed_at_utc, source, is_stale, is_primary)
            VALUES ($id, $name, $used, $duration, $reset, $observed, $source, $stale, $primary);
            """;
        command.Parameters.AddWithValue("$id", bucket.Id);
        command.Parameters.AddWithValue("$name", bucket.Name);
        command.Parameters.AddWithValue("$used", bucket.UsedPercent);
        command.Parameters.AddWithValue("$duration", bucket.WindowDurationMinutes);
        command.Parameters.AddWithValue("$reset", Iso(bucket.ResetsAtUtc));
        command.Parameters.AddWithValue("$observed", Iso(observation.ObservedAtUtc));
        command.Parameters.AddWithValue("$source", observation.Source.ToString());
        command.Parameters.AddWithValue("$stale", observation.IsStale ? 1 : 0);
        command.Parameters.AddWithValue("$primary", isPrimary ? 1 : 0);
        command.ExecuteNonQuery();
    }

    private void PrepareAggregateMigration()
    {
        var marker = ReadSchemaValueCore("aggregate_schema_version");
        using var existing = _connection.CreateCommand();
        existing.CommandText = "SELECT status FROM aggregate_rebuild_state WHERE singleton = 1;";
        var state = existing.ExecuteScalar() as string;
        if (string.Equals(marker, AggregateSchemaVersion, StringComparison.Ordinal) && state is not null) return;

        using var transaction = BeginImmediate();
        foreach (var table in new[] { "session_token_aggregates", "turn_token_aggregates",
                     "latest_token_events", "cumulative_frontiers", "token_time_buckets",
                     "active_cycle_aggregate" })
        {
            using var clear = _connection.CreateCommand();
            clear.Transaction = transaction;
            clear.CommandText = $"DELETE FROM {QuoteIdentifier(table)};";
            clear.ExecuteNonQuery();
        }
        long count;
        using (var samples = _connection.CreateCommand())
        {
            samples.Transaction = transaction;
            samples.CommandText = "SELECT COUNT(*) FROM token_samples;";
            count = Convert.ToInt64(samples.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        using (var rebuild = _connection.CreateCommand())
        {
            rebuild.Transaction = transaction;
            rebuild.CommandText = """
                INSERT INTO aggregate_rebuild_state(singleton, status, cursor_sample_id, total_samples)
                VALUES (1, $status, 0, $total)
                ON CONFLICT(singleton) DO UPDATE SET status = excluded.status,
                    cursor_sample_id = 0, total_samples = excluded.total_samples;
                """;
            rebuild.Parameters.AddWithValue("$status", count == 0 ? "ready" : "rebuilding");
            rebuild.Parameters.AddWithValue("$total", count);
            rebuild.ExecuteNonQuery();
        }
        WriteSchemaValue("aggregate_schema_version", AggregateSchemaVersion, transaction);
        transaction.Commit();
    }

    private bool ApplyPrivateSourceIdentityMigration()
    {
        var identityMarker = ReadSchemaValueCore("private_source_identity_v2");
        var scrubMarker = ReadSchemaValueCore("private_source_scrub_v2");
        if (identityMarker is not null and not "1" and not PrivateIdentityLogicalComplete ||
            scrubMarker is not null and not PrivateScrubPending and not PrivateScrubComplete)
        {
            throw new InvalidOperationException("PRIVATE_SOURCE_SCRUB_STATE_INVALID");
        }

        var columns = ReadColumns("source_files").Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        var hasLegacyPrivateColumn = columns.Contains("fallback_key");
        if (hasLegacyPrivateColumn &&
            (string.Equals(identityMarker, PrivateIdentityLogicalComplete, StringComparison.Ordinal) ||
             string.Equals(scrubMarker, PrivateScrubComplete, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("PRIVATE_SOURCE_SCRUB_STATE_INVALID");
        }

        if (!hasLegacyPrivateColumn)
        {
            EnsureColumn("source_files", "drain_mode", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn("source_files", "drain_line_start_offset", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn("source_files", "drain_offset", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn("source_files", "state_revision", "INTEGER NOT NULL DEFAULT 0");
            if (!columns.Contains("fallback_digest"))
                EnsureColumn("source_files", "fallback_digest", "TEXT NOT NULL DEFAULT ''");
            using var markerTransaction = BeginImmediate();
            WriteSchemaValue("private_source_identity_v2", PrivateIdentityLogicalComplete, markerTransaction);
            if (!string.Equals(scrubMarker, PrivateScrubComplete, StringComparison.Ordinal))
                WriteSchemaValue("private_source_scrub_v2", PrivateScrubPending, markerTransaction);
            markerTransaction.Commit();
            if (!string.Equals(scrubMarker, PrivateScrubComplete, StringComparison.Ordinal))
                CrashForMigrationTest("after-logical-pending-commit");
            return !string.Equals(scrubMarker, PrivateScrubComplete, StringComparison.Ordinal);
        }

        var lastWrite = columns.Contains("last_write_utc_ticks") ? "last_write_utc_ticks" : "0";
        var cursorTurn = columns.Contains("cursor_turn_key") ? "cursor_turn_key" : "NULL";
        var legacyRows = new List<LegacySource>();
        using (var read = _connection.CreateCommand())
        {
            read.CommandText = $"""
                SELECT source_key, thread_id, volume_serial, file_id, fallback_key, is_degraded,
                       relative_path, generation, complete_offset, last_length, {lastWrite},
                       {cursorTurn}, health_code FROM source_files ORDER BY source_key;
                """;
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                legacyRows.Add(new LegacySource(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), reader.GetInt64(5) != 0, reader.GetString(6),
                    reader.GetInt32(7), reader.GetInt64(8), reader.GetInt64(9), reader.GetInt64(10),
                    OptionalString(reader, 11), reader.GetString(12)));
            }
        }

        var converted = legacyRows.Select(ConvertLegacySource).ToArray();
        using var transaction = BeginImmediate();
        using (var create = _connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = """
                DROP TABLE IF EXISTS source_files_v5;
                CREATE TABLE source_files_v5 (
                    source_key TEXT PRIMARY KEY, thread_id TEXT NOT NULL, volume_serial TEXT NOT NULL,
                    file_id TEXT NOT NULL, fallback_digest TEXT NOT NULL, is_degraded INTEGER NOT NULL,
                    relative_path TEXT NOT NULL, generation INTEGER NOT NULL, complete_offset INTEGER NOT NULL,
                    drain_mode INTEGER NOT NULL DEFAULT 0, drain_line_start_offset INTEGER NOT NULL DEFAULT 0,
                    drain_offset INTEGER NOT NULL DEFAULT 0, last_length INTEGER NOT NULL,
                    last_write_utc_ticks INTEGER NOT NULL DEFAULT 0, cursor_turn_key TEXT,
                    health_code TEXT NOT NULL, state_revision INTEGER NOT NULL DEFAULT 0
                );
                """;
            create.ExecuteNonQuery();
        }

        foreach (var group in converted.GroupBy(item => item.Identity.StableKey, StringComparer.Ordinal))
        {
            var items = group.ToArray();
            var reset = items.Length > 1 || items.Any(item => item.RequiresReset);
            var chosen = items.OrderByDescending(item => item.Legacy.Generation)
                .ThenByDescending(item => item.Legacy.CompleteOffset).First();
            var state = new SourceFileState(chosen.Identity,
                items.Select(item => SafeRelativePath(item.Legacy.RelativePath))
                    .OrderBy(item => item, StringComparer.Ordinal).First(),
                reset ? items.Max(item => item.Legacy.Generation) + 1 : chosen.Legacy.Generation,
                reset ? new SourceReadCursor(0) : new SourceReadCursor(chosen.Legacy.CompleteOffset),
                reset ? 0 : chosen.Legacy.LastLength,
                reset ? 0 : chosen.Legacy.LastWriteTicks,
                reset ? null : chosen.Legacy.CursorTurnKey,
                reset ? "private_identity_rescan" : chosen.Legacy.HealthCode,
                1);
            InsertMigratedSource(state, transaction);
        }

        foreach (var item in converted)
        {
            using var update = _connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE token_samples SET source_key = $new_key WHERE source_key = $old_key;";
            update.Parameters.AddWithValue("$new_key", item.Identity.StableKey);
            update.Parameters.AddWithValue("$old_key", item.Legacy.SourceKey);
            update.ExecuteNonQuery();
        }
        using (var releaseOrphans = _connection.CreateCommand())
        {
            releaseOrphans.Transaction = transaction;
            releaseOrphans.CommandText = """
                DELETE FROM event_fingerprints WHERE NOT EXISTS (
                    SELECT 1 FROM token_samples WHERE token_samples.fingerprint = event_fingerprints.fingerprint);
                """;
            releaseOrphans.ExecuteNonQuery();
        }
        using (var replace = _connection.CreateCommand())
        {
            replace.Transaction = transaction;
            replace.CommandText = "DROP TABLE source_files; ALTER TABLE source_files_v5 RENAME TO source_files;";
            replace.ExecuteNonQuery();
        }
        WriteSchemaValue("private_source_identity_v2", PrivateIdentityLogicalComplete, transaction);
        WriteSchemaValue("private_source_scrub_v2", PrivateScrubPending, transaction);
        transaction.Commit();
        CrashForMigrationTest("after-logical-pending-commit");
        return true;
    }

    private void CompletePrivateSourceScrub()
    {
        CheckpointWalOrThrow();
        using (var vacuum = _connection.CreateCommand())
        {
            vacuum.CommandText = "VACUUM;";
            vacuum.ExecuteNonQuery();
        }
        CheckpointWalOrThrow();
        CrashForMigrationTest("after-vacuum-before-complete-marker");

        var identity = ReadSchemaValueCore("private_source_identity_v2");
        var scrub = ReadSchemaValueCore("private_source_scrub_v2");
        if (!string.Equals(identity, PrivateIdentityLogicalComplete, StringComparison.Ordinal) ||
            !string.Equals(scrub, PrivateScrubPending, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("PRIVATE_SOURCE_SCRUB_STATE_INVALID");
        }
        using (var transaction = BeginImmediate())
        {
            WriteSchemaValue("private_source_scrub_v2", PrivateScrubComplete, transaction);
            transaction.Commit();
        }
        CrashForMigrationTest("after-complete-marker-commit");
    }

    private void CheckpointWalOrThrow()
    {
        using var checkpoint = _connection.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        using var reader = checkpoint.ExecuteReader();
        if (!reader.Read() || reader.GetInt32(0) != 0)
            throw new InvalidOperationException("PRIVATE_SOURCE_SCRUB_CHECKPOINT_BUSY");
    }

    private static void CrashForMigrationTest(string point)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CODEX_USAGE_HUD_TEST_MIGRATION_CRASH"),
                point, StringComparison.Ordinal)) return;
        using var process = Process.GetCurrentProcess();
        process.Kill(entireProcessTree: false);
        Environment.Exit(197);
    }

    private static ConvertedLegacySource ConvertLegacySource(LegacySource legacy)
    {
        if (!legacy.IsDegraded && legacy.FileId.Length == 32)
            return new ConvertedLegacySource(legacy,
                new SourceIdentity(legacy.ThreadId, legacy.VolumeSerial, legacy.FileId, string.Empty, false), false);

        var requiresReset = !legacy.IsDegraded;
        string digest;
        var separator = legacy.FallbackKey.LastIndexOf('|');
        if (legacy.IsDegraded && separator > 0 && separator < legacy.FallbackKey.Length - 1 &&
            long.TryParse(legacy.FallbackKey[(separator + 1)..], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var creationTicks))
        {
            try
            {
                digest = FileIdentityProvider.BuildFallbackDigest(legacy.ThreadId,
                    legacy.FallbackKey[..separator], creationTicks);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
            {
                requiresReset = true;
                digest = TokenUsageSnapshot.HashText("legacy-fallback-v2\0" + legacy.ThreadId + "\0" +
                                                     legacy.FallbackKey);
            }
        }
        else
        {
            requiresReset = true;
            digest = TokenUsageSnapshot.HashText("legacy-fallback-v2\0" + legacy.ThreadId + "\0" +
                                                 legacy.FallbackKey + "\0" + legacy.SourceKey);
        }
        return new ConvertedLegacySource(legacy,
            new SourceIdentity(legacy.ThreadId, string.Empty, string.Empty, digest, true), requiresReset);
    }

    private void InsertMigratedSource(SourceFileState state, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO source_files_v5(source_key, thread_id, volume_serial, file_id, fallback_digest,
                is_degraded, relative_path, generation, complete_offset, drain_mode,
                drain_line_start_offset, drain_offset, last_length, last_write_utc_ticks,
                cursor_turn_key, health_code, state_revision)
            VALUES ($source_key, $thread_id, $volume, $file_id, $fallback_digest, $degraded,
                $relative, $generation, $complete, 0, 0, 0, $length, $write_ticks,
                $cursor_turn, $health, $revision);
            """;
        command.Parameters.AddWithValue("$source_key", state.Identity.StableKey);
        command.Parameters.AddWithValue("$thread_id", state.Identity.ThreadId);
        command.Parameters.AddWithValue("$volume", state.Identity.VolumeSerial);
        command.Parameters.AddWithValue("$file_id", state.Identity.FileId);
        command.Parameters.AddWithValue("$fallback_digest", state.Identity.FallbackDigest);
        command.Parameters.AddWithValue("$degraded", state.Identity.IsDegraded ? 1 : 0);
        command.Parameters.AddWithValue("$relative", SafeRelativePath(state.RelativePath));
        command.Parameters.AddWithValue("$generation", state.Generation);
        command.Parameters.AddWithValue("$complete", state.Cursor.CompleteOffset);
        command.Parameters.AddWithValue("$length", state.LastObservedLength);
        command.Parameters.AddWithValue("$write_ticks", state.LastWriteTimeUtcTicks);
        command.Parameters.AddWithValue("$cursor_turn", (object?)state.CursorTurnKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$health", state.HealthCode);
        command.Parameters.AddWithValue("$revision", state.StateRevision);
        command.ExecuteNonQuery();
    }

    private void ApplyTupleOracleMigration()
    {
        var oldGuard = ReadSchemaValueCore("token_cumulative_guard");
        var migrated = ReadSchemaValueCore("token_tuple_oracle_migration");
        using var transaction = BeginImmediate();
        long orphanCount;
        using (var orphanQuery = _connection.CreateCommand())
        {
            orphanQuery.Transaction = transaction;
            orphanQuery.CommandText = """
                SELECT COUNT(*) FROM event_fingerprints AS fingerprints
                WHERE NOT EXISTS (SELECT 1 FROM token_samples AS samples
                                  WHERE samples.fingerprint = fingerprints.fingerprint);
                """;
            orphanCount = Convert.ToInt64(orphanQuery.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        var legacyVersion = migrated is not null &&
                            !string.Equals(migrated, TupleOracleMigrationVersion, StringComparison.Ordinal);
        if (legacyVersion || string.Equals(oldGuard, "1", StringComparison.Ordinal) || orphanCount > 0)
        {
            using (var release = _connection.CreateCommand())
            {
                release.Transaction = transaction;
                release.CommandText = """
                    DELETE FROM event_fingerprints WHERE NOT EXISTS (
                        SELECT 1 FROM token_samples WHERE token_samples.fingerprint = event_fingerprints.fingerprint);
                    """;
                release.ExecuteNonQuery();
            }
            ResetSources("token_oracle_rescan", transaction);
        }
        WriteSchemaValue("token_tuple_oracle_migration", TupleOracleMigrationVersion, transaction);
        WriteSchemaValue("token_tuple_dedup", "1", transaction);
        WriteSchemaValue("token_cumulative_guard", "0", transaction);
        transaction.Commit();
    }

    private void ApplyFileIdentityMigration()
    {
        if (string.Equals(ReadSchemaValueCore("windows_file_id_128"), "1", StringComparison.Ordinal)) return;
        using var transaction = BeginImmediate();
        using (var reset = _connection.CreateCommand())
        {
            reset.Transaction = transaction;
            reset.CommandText = """
                UPDATE source_files SET generation = generation + 1, complete_offset = 0,
                    drain_mode = 0, drain_line_start_offset = 0, drain_offset = 0,
                    last_length = 0, cursor_turn_key = NULL, health_code = 'file_identity_rescan',
                    state_revision = state_revision + 1
                WHERE is_degraded = 0 AND length(file_id) <> 32;
                """;
            reset.ExecuteNonQuery();
        }
        WriteSchemaValue("windows_file_id_128", "1", transaction);
        transaction.Commit();
    }

    private void ApplyTurnKeyMigration()
    {
        if (string.Equals(ReadSchemaValueCore("deterministic_turn_keys"), "1", StringComparison.Ordinal)) return;
        using var transaction = BeginImmediate();
        using (var samples = _connection.CreateCommand())
        {
            samples.Transaction = transaction;
            samples.CommandText = "UPDATE token_samples SET turn_key = NULL, turn_confidence = 'unavailable';";
            samples.ExecuteNonQuery();
        }
        using (var sessions = _connection.CreateCommand())
        {
            sessions.Transaction = transaction;
            sessions.CommandText = """
                UPDATE sessions SET current_turn_key = NULL, current_turn_reliable = 0,
                    turn_sequence = 0, turn_open = 0, status = 'Idle';
                """;
            sessions.ExecuteNonQuery();
        }
        InvalidateAggregateRebuild(transaction);
        ResetSources("turn_key_rescan", transaction);
        WriteSchemaValue("deterministic_turn_keys", "1", transaction);
        transaction.Commit();
    }

    private void ApplySampleSourceMigration()
    {
        if (string.Equals(ReadSchemaValueCore("sample_source_offsets"), "1", StringComparison.Ordinal)) return;
        using var transaction = BeginImmediate();
        InvalidateAggregateRebuild(transaction);
        ResetSources("sample_source_rescan", transaction);
        WriteSchemaValue("sample_source_offsets", "1", transaction);
        transaction.Commit();
    }

    private void ApplyContextCaptureMigration()
    {
        if (string.Equals(ReadSchemaValueCore("context_capture_version"),
                ContextCaptureMigrationVersion, StringComparison.Ordinal)) return;

        using var transaction = BeginImmediate();
        using (var clearState = _connection.CreateCommand())
        {
            clearState.Transaction = transaction;
            clearState.CommandText = "DELETE FROM session_context_state; DELETE FROM context_baseline_observations;";
            clearState.ExecuteNonQuery();
        }
        ResetSources("context_boundary_rescan", transaction);
        WriteSchemaValue("context_capture_version", ContextCaptureMigrationVersion, transaction);
        transaction.Commit();
    }

    private void ApplyLineageSemanticMigration()
    {
        var already = string.Equals(ReadSchemaValueCore("lineage_semantic_version"), LineageSemanticVersion,
            StringComparison.Ordinal);
        using var transaction = BeginImmediate();
        var backfilled = BackfillLineageIdentities(transaction);
        if (already && backfilled == 0)
        {
            transaction.Commit();
            return;
        }

        ReassignLineageCanonical(transaction);
        ReplayAllDerivedContext(transaction);
        ResetSources("lineage_semantic_rescan", transaction);
        InvalidateAggregateRebuild(transaction);
        WriteSchemaValue("lineage_semantic_version", LineageSemanticVersion, transaction);
        transaction.Commit();
    }

    private void MaybeInvalidateLineageGraph(SessionWork work, string? previousParent, bool wasInsert,
        SqliteTransaction transaction)
    {
        if (!RequiresLineageGraphInvalidation(work, wasInsert, previousParent, transaction)) return;
        InvalidateLineageForGraphChange(transaction);
    }

    private bool RequiresLineageGraphInvalidation(SessionWork work, bool wasInsert, string? previousParent,
        SqliteTransaction transaction)
    {
        var parentChanged = !string.Equals(previousParent, work.Metadata.ParentThreadId, StringComparison.Ordinal);
        if (!parentChanged && !wasInsert) return false;
        if (!parentChanged && wasInsert &&
            work.Metadata.ParentThreadId is null && !IsTargetedAsParent(work.Metadata.ThreadId, transaction))
        {
            return false;
        }

        if (!ThreadHasTokenSamples(work.Metadata.ThreadId, transaction) &&
            !IsTargetedAsParent(work.Metadata.ThreadId, transaction) &&
            (work.Metadata.ParentThreadId is null ||
             !ThreadHasTokenSamples(work.Metadata.ParentThreadId, transaction)))
        {
            return false;
        }

        return true;
    }

    private void InvalidateLineageForGraphChange(SqliteTransaction transaction)
    {
        ReassignLineageCanonical(transaction);
        ReplayAllDerivedContext(transaction);
        InvalidateAggregateRebuild(transaction);
        _lifecycleInputsValidUntilUtc = DateTimeOffset.MinValue;
    }

    private bool IsTargetedAsParent(string threadId, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1 FROM sessions WHERE parent_thread_id = $thread_id LIMIT 1;
            """;
        command.Parameters.AddWithValue("$thread_id", threadId);
        return command.ExecuteScalar() is not null;
    }

    private bool ThreadHasTokenSamples(string threadId, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM token_samples WHERE thread_id = $thread_id LIMIT 1;";
        command.Parameters.AddWithValue("$thread_id", threadId);
        return command.ExecuteScalar() is not null;
    }

    private int BackfillLineageIdentities(SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT id, {AggregateColumns}, cumulative_input_tokens, cumulative_output_tokens,
                   cumulative_total_tokens, context_window, semantic_identity, semantic_material,
                   legacy_semantic_identity
            FROM token_samples
            WHERE semantic_identity = '' OR semantic_identity IS NULL OR semantic_material IS NULL
               OR legacy_semantic_identity = '' OR legacy_semantic_identity IS NULL
               OR (
                    semantic_material NOT LIKE '{TokenUsageSnapshot.LineageSemanticLegacyPrefix}|%'
                    AND semantic_material NOT LIKE '{TokenUsageSnapshot.LineageSemanticPrefix}|%'
                  );
            """;
        using var reader = command.ExecuteReader();
        var updates = new List<(long Id, string Identity, string Material, string Legacy)>();
        while (reader.Read())
        {
            var last = ReadAggregate(reader, 1);
            var cumulativeInput = ReadLong(reader, 9);
            var cumulativeOutput = ReadLong(reader, 10);
            var cumulativeTotal = ReadLong(reader, 11);
            var contextWindow = OptionalLong(reader, 12);
            var existingIdentity = OptionalString(reader, 13);
            var existingMaterial = OptionalString(reader, 14);
            var existingLegacy = OptionalString(reader, 15);
            string material;
            string identity;
            if (TokenUsageSnapshot.IsLiveSemanticMaterial(existingMaterial))
            {
                material = existingMaterial!;
                identity = string.IsNullOrWhiteSpace(existingIdentity)
                    ? TokenUsageSnapshot.HashText(material)
                    : existingIdentity;
            }
            else
            {
                material = TokenUsageSnapshot.LineageSemanticMaterialFromStored(last, cumulativeInput,
                    cumulativeOutput, cumulativeTotal, contextWindow);
                identity = TokenUsageSnapshot.HashText(material);
            }
            var legacy = TokenUsageSnapshot.LineageSemanticIdentityFromStored(last, cumulativeInput,
                cumulativeOutput, cumulativeTotal, contextWindow);
            if (string.Equals(existingIdentity, identity, StringComparison.Ordinal) &&
                string.Equals(existingMaterial, material, StringComparison.Ordinal) &&
                string.Equals(existingLegacy, legacy, StringComparison.Ordinal))
            {
                continue;
            }
            updates.Add((reader.GetInt64(0), identity, material, legacy));
        }
        reader.Close();
        foreach (var update in updates)
        {
            using var write = _connection.CreateCommand();
            write.Transaction = transaction;
            write.CommandText = """
                UPDATE token_samples
                SET semantic_identity = $identity, semantic_material = $material,
                    legacy_semantic_identity = $legacy
                WHERE id = $id;
                """;
            write.Parameters.AddWithValue("$identity", update.Identity);
            write.Parameters.AddWithValue("$material", update.Material);
            write.Parameters.AddWithValue("$legacy", update.Legacy);
            write.Parameters.AddWithValue("$id", update.Id);
            write.ExecuteNonQuery();
        }
        return updates.Count;
    }

    private Dictionary<string, string?> LoadParentMap(SqliteTransaction? transaction = null)
    {
        using var command = _connection.CreateCommand();
        if (transaction is not null) command.Transaction = transaction;
        command.CommandText = "SELECT thread_id, parent_thread_id FROM sessions;";
        using var reader = command.ExecuteReader();
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        while (reader.Read())
            map[reader.GetString(0)] = OptionalString(reader, 1);
        return map;
    }

    private static string ResolveLineageRoot(string threadId, IReadOnlyDictionary<string, string?> parentMap)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = threadId;
        while (true)
        {
            if (!parentMap.TryGetValue(current, out var parent) || string.IsNullOrWhiteSpace(parent))
                return current;
            if (!parentMap.ContainsKey(parent))
                return current;
            if (!seen.Add(current) || seen.Contains(parent) ||
                string.Equals(parent, current, StringComparison.Ordinal))
            {
                return threadId;
            }
            current = parent;
        }
    }

    private static int ResolvedLineageDepth(string threadId, IReadOnlyDictionary<string, string?> parentMap)
    {
        var root = ResolveLineageRoot(threadId, parentMap);
        if (string.Equals(threadId, root, StringComparison.Ordinal)) return 0;
        var depth = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal) { threadId };
        var current = threadId;
        while (true)
        {
            if (!parentMap.TryGetValue(current, out var parent) || string.IsNullOrWhiteSpace(parent))
                return depth;
            if (!parentMap.ContainsKey(parent)) return depth;
            if (!seen.Add(parent) || string.Equals(parent, current, StringComparison.Ordinal))
                return depth;
            depth++;
            if (string.Equals(parent, root, StringComparison.Ordinal)) return depth;
            current = parent;
        }
    }

    private void ReassignLineageCanonical(SqliteTransaction transaction)
    {
        var parentMap = LoadParentMap(transaction);
        _canonicalParentMap = parentMap;
        using (var clear = _connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "UPDATE token_samples SET is_lineage_canonical = 0;";
            clear.ExecuteNonQuery();
        }

        var sampleThreads = new HashSet<string>(StringComparer.Ordinal);
        using (var threads = _connection.CreateCommand())
        {
            threads.Transaction = transaction;
            threads.CommandText = "SELECT DISTINCT thread_id FROM token_samples;";
            using var reader = threads.ExecuteReader();
            while (reader.Read()) sampleThreads.Add(reader.GetString(0));
        }

        foreach (var threadId in sampleThreads)
        {
            var root = ResolveLineageRoot(threadId, parentMap);
            using var updateRoot = _connection.CreateCommand();
            updateRoot.Transaction = transaction;
            updateRoot.CommandText = """
                UPDATE token_samples SET lineage_root_thread_id = $root WHERE thread_id = $thread_id;
                """;
            updateRoot.Parameters.AddWithValue("$root", root);
            updateRoot.Parameters.AddWithValue("$thread_id", threadId);
            updateRoot.ExecuteNonQuery();
        }

        var groups = new List<(string Root, string Identity)>();
        using (var distinct = _connection.CreateCommand())
        {
            distinct.Transaction = transaction;
            distinct.CommandText = """
                SELECT DISTINCT lineage_root_thread_id, semantic_identity
                FROM token_samples
                WHERE semantic_identity <> '';
                """;
            using var reader = distinct.ExecuteReader();
            while (reader.Read()) groups.Add((reader.GetString(0), reader.GetString(1)));
        }

        foreach (var (root, identity) in groups)
            ApplyExactGroupElection(root, identity, transaction);

        ApplyAllLegacyCompatibility(transaction);
    }

    private void ApplyAllLegacyCompatibility(SqliteTransaction transaction)
    {
        _canonicalParentMap = LoadParentMap(transaction);
        if (!AnyLiveSample(transaction)) return;
        var aliases = new HashSet<(string Root, string Legacy)>();
        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT DISTINCT lineage_root_thread_id, legacy_semantic_identity
                FROM token_samples
                WHERE legacy_semantic_identity <> '';
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
                aliases.Add((reader.GetString(0), reader.GetString(1)));
        }

        foreach (var (root, legacy) in aliases)
            ApplyLegacyCompatibility(root, legacy, transaction);
    }

    private bool AnyLiveSample(SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT 1 FROM token_samples
            WHERE semantic_material LIKE '{TokenUsageSnapshot.LineageSemanticPrefix}|%'
            LIMIT 1;
            """;
        return command.ExecuteScalar() is not null;
    }

    private LineageAssignment AssignLineageAfterInsert(TokenSampleCandidate candidate, TokenInsertResult inserted,
        IReadOnlyDictionary<string, string?> parentMap, SqliteTransaction transaction)
    {
        var sampleId = inserted.SampleId ?? throw new InvalidOperationException("LINEAGE_SAMPLE_MISSING");
        var semanticIdentity = string.IsNullOrWhiteSpace(inserted.SemanticIdentity)
            ? candidate.Snapshot.SemanticIdentity()
            : inserted.SemanticIdentity;
        var legacyIdentity = candidate.Snapshot.LegacySemanticIdentity();
        _canonicalParentMap = parentMap;
        var root = ResolveLineageRoot(candidate.ThreadId, parentMap);
        using (var rootUpdate = _connection.CreateCommand())
        {
            rootUpdate.Transaction = transaction;
            rootUpdate.CommandText = """
                UPDATE token_samples
                SET lineage_root_thread_id = $root, semantic_identity = $identity,
                    legacy_semantic_identity = $legacy
                WHERE id = $id;
                """;
            rootUpdate.Parameters.AddWithValue("$root", root);
            rootUpdate.Parameters.AddWithValue("$identity", semanticIdentity);
            rootUpdate.Parameters.AddWithValue("$legacy", legacyIdentity);
            rootUpdate.Parameters.AddWithValue("$id", sampleId);
            rootUpdate.ExecuteNonQuery();
        }

        var retired = new List<CanonicalSample>();
        var promoted = new List<CanonicalSample>();
        var exact = ApplyExactGroupElection(root, semanticIdentity, transaction);
        AppendUniqueSamples(retired, exact.Retired);
        AppendUniqueSamples(promoted, exact.Promoted);
        var compat = ApplyLegacyCompatibility(root, legacyIdentity, transaction);
        AppendUniqueSamples(retired, compat.Retired);
        AppendUniqueSamples(promoted, compat.Promoted);
        retired.RemoveAll(sample => sample.Id == sampleId);
        promoted.RemoveAll(sample => sample.Id == sampleId);
        NetLineageTransitions(retired, promoted);
        var isCanonical = IsSampleLineageCanonical(sampleId, transaction);
        return new LineageAssignment(isCanonical, retired, promoted);
    }

    private void ApplyLineageAggregateTransitions(LineageAssignment assignment, AggregateAccumulator aggregates,
        ISet<string> replayThreads)
    {
        var retiredIds = new HashSet<long>();
        var uniqueRetired = new List<CanonicalSample>();
        foreach (var retired in assignment.Retired)
        {
            if (!retiredIds.Add(retired.Id)) continue;
            uniqueRetired.Add(retired);
        }

        var promotedIds = new HashSet<long>();
        var uniquePromoted = new List<CanonicalSample>();
        foreach (var promoted in assignment.Promoted)
        {
            if (!promotedIds.Add(promoted.Id)) continue;
            uniquePromoted.Add(promoted);
        }

        var cancelled = new HashSet<long>(retiredIds);
        cancelled.IntersectWith(promotedIds);
        foreach (var retired in uniqueRetired)
        {
            if (cancelled.Contains(retired.Id)) continue;
            aggregates.SubtractConsumption(retired);
            replayThreads.Add(retired.ThreadId);
        }

        foreach (var promoted in uniquePromoted)
        {
            if (cancelled.Contains(promoted.Id)) continue;
            aggregates.AddConsumption(promoted);
            replayThreads.Add(promoted.ThreadId);
        }

        if (uniqueRetired.Exists(sample => !cancelled.Contains(sample.Id)) ||
            uniquePromoted.Exists(sample => !cancelled.Contains(sample.Id)))
            _lifecycleInputsValidUntilUtc = DateTimeOffset.MinValue;
    }

    private LineageAssignment ApplyExactGroupElection(string root, string identity, SqliteTransaction transaction)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(identity))
            return LineageAssignment.Empty;
        return ElectLineageGroup(LoadExactLineageGroup(root, identity, transaction), transaction);
    }

    private LineageAssignment ApplyLegacyCompatibility(string root, string legacyIdentity,
        SqliteTransaction transaction)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(legacyIdentity))
            return LineageAssignment.Empty;
        var legacyWinner = LoadLegacyExactWinner(root, legacyIdentity, transaction);
        var liveRepresentatives = LoadLiveExactRepresentativesByAlias(root, legacyIdentity, transaction);
        if (legacyWinner is null || liveRepresentatives.Count == 0) return LineageAssignment.Empty;

        var pairedLive = liveRepresentatives[0];
        var legacyEarlier = CompareCanonicalOrder(ToCanonicalSample(legacyWinner),
            ToCanonicalSample(pairedLive)) < 0;
        var overlay = new List<LineageMember>(liveRepresentatives.Count + 1) { legacyWinner };
        overlay.AddRange(liveRepresentatives);
        var desired = new Dictionary<long, bool>();
        desired[legacyWinner.Id] = legacyEarlier;
        foreach (var live in liveRepresentatives)
            desired[live.Id] = !(legacyEarlier && live.Id == pairedLive.Id);

        var retired = new List<CanonicalSample>();
        var promoted = new List<CanonicalSample>();
        foreach (var member in overlay)
        {
            if (!desired.TryGetValue(member.Id, out var want) || want || !member.Canonical) continue;
            SetLineageCanonical(member.Id, false, transaction);
            retired.Add(ToCanonicalSample(member));
        }

        foreach (var member in overlay)
        {
            if (!desired.TryGetValue(member.Id, out var want) || !want || member.Canonical) continue;
            SetLineageCanonical(member.Id, true, transaction);
            promoted.Add(ToCanonicalSample(member));
        }

        return new LineageAssignment(false, retired, promoted);
    }

    private LineageAssignment ElectLineageGroup(IReadOnlyList<LineageMember> group, SqliteTransaction transaction)
    {
        if (group.Count == 0) return LineageAssignment.Empty;
        var winner = EarliestLineageMember(group);
        var retired = new List<CanonicalSample>();
        var promoted = new List<CanonicalSample>();
        foreach (var member in group)
        {
            if (!member.Canonical || member.Id == winner.Id) continue;
            SetLineageCanonical(member.Id, false, transaction);
            retired.Add(ToCanonicalSample(member));
        }

        foreach (var member in group)
        {
            if (member.Id != winner.Id || member.Canonical) continue;
            SetLineageCanonical(member.Id, true, transaction);
            promoted.Add(ToCanonicalSample(member));
        }

        return new LineageAssignment(false, retired, promoted);
    }

    private List<LineageMember> LoadExactLineageGroup(string root, string identity, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT id, thread_id, {AggregateColumns}, turn_key, event_time_ticks, source_key,
                   source_generation, source_offset, turn_confidence, event_order_confidence,
                   cumulative_total_tokens, is_lineage_canonical, semantic_identity,
                   semantic_material, legacy_semantic_identity
            FROM token_samples
            WHERE lineage_root_thread_id = $root AND semantic_identity = $identity;
            """;
        command.Parameters.AddWithValue("$root", root);
        command.Parameters.AddWithValue("$identity", identity);
        return ReadLineageMembers(command);
    }

    private LineageMember? LoadLegacyExactWinner(string root, string legacyIdentity,
        SqliteTransaction transaction)
    {
        LineageMember? winner = null;
        foreach (var member in LoadExactLineageGroup(root, legacyIdentity, transaction))
        {
            if (!TokenUsageSnapshot.IsLegacySemanticMaterial(member.Material)) continue;
            if (winner is null ||
                CompareCanonicalOrder(ToCanonicalSample(member), ToCanonicalSample(winner)) < 0)
            {
                winner = member;
            }
        }

        return winner;
    }

    private List<LineageMember> LoadLiveExactRepresentativesByAlias(string root, string legacyIdentity,
        SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT id, thread_id, {AggregateColumns}, turn_key, event_time_ticks, source_key,
                   source_generation, source_offset, turn_confidence, event_order_confidence,
                   cumulative_total_tokens, is_lineage_canonical, semantic_identity,
                   semantic_material, legacy_semantic_identity
            FROM token_samples
            WHERE lineage_root_thread_id = $root AND legacy_semantic_identity = $legacy
              AND semantic_material LIKE '{TokenUsageSnapshot.LineageSemanticPrefix}|%';
            """;
        command.Parameters.AddWithValue("$root", root);
        command.Parameters.AddWithValue("$legacy", legacyIdentity);
        var representatives = new Dictionary<string, LineageMember>(StringComparer.Ordinal);
        foreach (var row in ReadLineageMembers(command))
        {
            var key = string.IsNullOrWhiteSpace(row.Identity) ? row.Material : row.Identity;
            if (!representatives.TryGetValue(key, out var current) ||
                CompareCanonicalOrder(ToCanonicalSample(row), ToCanonicalSample(current)) < 0)
            {
                representatives[key] = row;
            }
        }

        var ordered = representatives.Values.ToList();
        ordered.Sort((left, right) =>
            CompareCanonicalOrder(ToCanonicalSample(left), ToCanonicalSample(right)));
        return ordered;
    }

    private List<LineageMember> ReadLineageMembers(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var rows = new List<LineageMember>();
        while (reader.Read())
        {
            rows.Add(new LineageMember(reader.GetInt64(0), reader.GetString(1), ReadAggregate(reader, 2),
                OptionalString(reader, 10), OptionalLong(reader, 11), OptionalString(reader, 12),
                reader.GetInt32(13), OptionalLong(reader, 14), reader.GetString(15), reader.GetString(16),
                reader.GetInt64(17), reader.GetInt64(18) != 0, OptionalString(reader, 19) ?? string.Empty,
                OptionalString(reader, 20) ?? string.Empty, OptionalString(reader, 21) ?? string.Empty));
        }
        return rows;
    }

    private LineageMember EarliestLineageMember(IReadOnlyList<LineageMember> group)
    {
        var winner = group[0];
        foreach (var member in group)
        {
            if (CompareCanonicalOrder(ToCanonicalSample(member), ToCanonicalSample(winner)) < 0)
                winner = member;
        }

        return winner;
    }

    private static void AppendUniqueSamples(List<CanonicalSample> target, IReadOnlyList<CanonicalSample> extra)
    {
        foreach (var sample in extra)
        {
            if (target.Exists(existing => existing.Id == sample.Id)) continue;
            target.Add(sample);
        }
    }

    private static void NetLineageTransitions(List<CanonicalSample> retired, List<CanonicalSample> promoted)
    {
        var retiredIds = new HashSet<long>();
        var uniqueRetired = new List<CanonicalSample>();
        foreach (var sample in retired)
        {
            if (!retiredIds.Add(sample.Id)) continue;
            uniqueRetired.Add(sample);
        }

        var promotedIds = new HashSet<long>();
        var uniquePromoted = new List<CanonicalSample>();
        foreach (var sample in promoted)
        {
            if (!promotedIds.Add(sample.Id)) continue;
            uniquePromoted.Add(sample);
        }

        var cancelled = new HashSet<long>(retiredIds);
        cancelled.IntersectWith(promotedIds);
        retired.Clear();
        promoted.Clear();
        foreach (var sample in uniqueRetired)
        {
            if (cancelled.Contains(sample.Id)) continue;
            retired.Add(sample);
        }

        foreach (var sample in uniquePromoted)
        {
            if (cancelled.Contains(sample.Id)) continue;
            promoted.Add(sample);
        }
    }

    private static CanonicalSample ToCanonicalSample(LineageMember member) => new(member.Id, member.ThreadId,
        member.Usage, member.TurnKey, member.EventTimeTicks, member.SourceKey, member.SourceGeneration,
        member.SourceOffset, member.TurnConfidence, member.EventOrderConfidence, member.CumulativeTotal);

    private void SetLineageCanonical(long sampleId, bool canonical, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE token_samples SET is_lineage_canonical = $flag WHERE id = $id;";
        command.Parameters.AddWithValue("$flag", canonical ? 1 : 0);
        command.Parameters.AddWithValue("$id", sampleId);
        command.ExecuteNonQuery();
    }

    private bool IsSampleLineageCanonical(long sampleId, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT is_lineage_canonical FROM token_samples WHERE id = $id;";
        command.Parameters.AddWithValue("$id", sampleId);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    private int CompareCanonicalOrder(CanonicalSample left, CanonicalSample right)
    {
        var leftReliable = left.EventTimeTicks.HasValue;
        var rightReliable = right.EventTimeTicks.HasValue;
        if (leftReliable != rightReliable) return leftReliable ? -1 : 1;
        if (leftReliable)
        {
            var time = left.EventTimeTicks!.Value.CompareTo(right.EventTimeTicks!.Value);
            if (time != 0) return time;
            var depth = ResolvedLineageDepth(left.ThreadId, _canonicalParentMap)
                .CompareTo(ResolvedLineageDepth(right.ThreadId, _canonicalParentMap));
            if (depth != 0) return depth;
        }

        var source = string.Compare(left.SourceKey ?? string.Empty, right.SourceKey ?? string.Empty,
            StringComparison.Ordinal);
        if ((left.SourceKey is null) != (right.SourceKey is null))
            return left.SourceKey is null ? 1 : -1;
        if (source != 0) return source;
        var generation = left.SourceGeneration.CompareTo(right.SourceGeneration);
        if (generation != 0) return generation;
        if ((left.SourceOffset is null) != (right.SourceOffset is null))
            return left.SourceOffset is null ? 1 : -1;
        if (left.SourceOffset.HasValue)
        {
            var offset = left.SourceOffset.Value.CompareTo(right.SourceOffset!.Value);
            if (offset != 0) return offset;
        }
        return left.Id.CompareTo(right.Id);
    }

    private bool HasNonCanonicalSampleBetween(string threadId, long explicitTicks, long candidateTicks,
        SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1 FROM token_samples
            WHERE thread_id = $thread_id AND is_lineage_canonical = 0
              AND event_time_ticks IS NOT NULL
              AND event_time_ticks >= $explicit_ticks
              AND event_time_ticks < $candidate_ticks
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$thread_id", threadId);
        command.Parameters.AddWithValue("$explicit_ticks", explicitTicks);
        command.Parameters.AddWithValue("$candidate_ticks", candidateTicks);
        return command.ExecuteScalar() is not null;
    }

    private void ReplayAllDerivedContext(SqliteTransaction transaction)
    {
        var threads = new HashSet<string>(StringComparer.Ordinal);
        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT DISTINCT thread_id FROM token_samples
                UNION
                SELECT DISTINCT thread_id FROM structural_events
                WHERE event_kind = 'event_msg:context_compacted';
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read()) threads.Add(reader.GetString(0));
        }

        foreach (var threadId in threads) ReplayContextForThread(threadId, transaction);
    }

    private void ReplayContextForThread(string threadId, SqliteTransaction transaction)
    {
        using (var clear = _connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = """
                DELETE FROM session_context_state WHERE thread_id = $thread_id;
                DELETE FROM context_baseline_observations WHERE thread_id = $thread_id;
                """;
            clear.Parameters.AddWithValue("$thread_id", threadId);
            clear.ExecuteNonQuery();
        }

        var compactEvents = new List<CompactReplayEvent>();
        using (var compact = _connection.CreateCommand())
        {
            compact.Transaction = transaction;
            compact.CommandText = """
                SELECT event_identity, association_key, event_time_utc, source_identity_hash,
                       source_generation, source_offset
                FROM structural_events
                WHERE thread_id = $thread_id AND event_kind = 'event_msg:context_compacted'
                ORDER BY source_offset, event_identity;
                """;
            compact.Parameters.AddWithValue("$thread_id", threadId);
            using var compactReader = compact.ExecuteReader();
            while (compactReader.Read())
            {
                var eventTime = OptionalDate(compactReader, 2);
                if (eventTime is null || string.IsNullOrWhiteSpace(compactReader.GetString(0))) continue;
                compactEvents.Add(new CompactReplayEvent(eventTime.Value.UtcTicks, compactReader.GetInt64(5),
                    compactReader.GetString(3), compactReader.GetInt32(4), compactReader.GetString(0),
                    OptionalString(compactReader, 1)));
            }
        }

        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT id, thread_id, turn_key, event_time_utc, event_time_ticks, source_key,
                   source_generation, source_offset, {AggregateColumns}, cumulative_total_tokens,
                   turn_confidence, event_order_confidence, context_window,
                   cumulative_input_tokens, cumulative_output_tokens, model
            FROM token_samples
            WHERE thread_id = $thread_id AND is_lineage_canonical = 1
            ORDER BY CASE WHEN event_time_ticks IS NULL THEN 1 ELSE 0 END, event_time_ticks, id;
            """;
        command.Parameters.AddWithValue("$thread_id", threadId);
        using var reader = command.ExecuteReader();
        var rows = new List<RebuildSample>();
        while (reader.Read())
        {
            rows.Add(new RebuildSample(reader.GetInt64(0), reader.GetString(1), OptionalString(reader, 2),
                OptionalDate(reader, 3), OptionalLong(reader, 4), OptionalString(reader, 5), reader.GetInt32(6),
                OptionalLong(reader, 7), ReadAggregate(reader, 8), reader.GetInt64(16), reader.GetString(17),
                reader.GetString(18), OptionalLong(reader, 19), true, ReadLong(reader, 20), ReadLong(reader, 21),
                OptionalString(reader, 22)));
        }
        reader.Close();

        var steps = new List<(long Ticks, long Offset, int Kind, int Index)>(compactEvents.Count + rows.Count);
        for (var index = 0; index < compactEvents.Count; index++)
        {
            var item = compactEvents[index];
            steps.Add((item.Ticks, item.Offset, 0, index));
        }
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            steps.Add((row.EventTimeTicks ?? long.MaxValue, row.SourceOffset ?? long.MaxValue, 1, index));
        }
        steps.Sort((left, right) =>
        {
            var time = left.Ticks.CompareTo(right.Ticks);
            if (time != 0) return time;
            var offset = left.Offset.CompareTo(right.Offset);
            if (offset != 0) return offset;
            var kind = left.Kind.CompareTo(right.Kind);
            return kind != 0 ? kind : left.Index.CompareTo(right.Index);
        });

        foreach (var step in steps)
        {
            if (step.Kind == 0)
            {
                var compact = compactEvents[step.Index];
                ApplyExplicitCompactionMarker(threadId, compact.Ticks, compact.SourceKey, compact.Generation,
                    compact.Offset, compact.EventIdentity, compact.AssociationKey, transaction);
                continue;
            }

            var row = rows[step.Index];
            var candidate = new TokenSampleCandidate(row.ThreadId, RebuildSnapshot(row),
                row.EventTimeTicks.HasValue ? new DateTimeOffset(row.EventTimeTicks.Value, TimeSpan.Zero) : null,
                DateTimeOffset.MinValue, row.TurnKey, row.Model, null, row.SourceKey, row.SourceGeneration,
                row.SourceOffset, row.TurnConfidence, row.EventOrderConfidence);
            ObserveContextCandidate(candidate, row.Id, transaction);
        }
    }

    private static TokenUsageSnapshot RebuildSnapshot(RebuildSample row) => new(
        new TokenComponents(row.CumulativeInput, null, null, null, row.CumulativeOutput, null, row.CumulativeTotal),
        new TokenComponents(row.Usage.Input, row.Usage.CachedInput, null, row.Usage.CacheWriteInput,
            row.Usage.Output, row.Usage.Reasoning, row.Usage.ReportedTotal),
        row.ContextWindow);

    private void InvalidateAggregateRebuild(SqliteTransaction transaction)
    {
        foreach (var table in new[] { "session_token_aggregates", "turn_token_aggregates",
                     "latest_token_events", "cumulative_frontiers", "token_time_buckets",
                     "active_cycle_aggregate" })
        {
            using var clear = _connection.CreateCommand();
            clear.Transaction = transaction;
            clear.CommandText = $"DELETE FROM {QuoteIdentifier(table)};";
            clear.ExecuteNonQuery();
        }
        long count;
        using (var samples = _connection.CreateCommand())
        {
            samples.Transaction = transaction;
            samples.CommandText = "SELECT COUNT(*) FROM token_samples;";
            count = Convert.ToInt64(samples.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        using (var rebuild = _connection.CreateCommand())
        {
            rebuild.Transaction = transaction;
            rebuild.CommandText = """
                INSERT INTO aggregate_rebuild_state(singleton, status, cursor_sample_id, total_samples)
                VALUES (1, $status, 0, $total)
                ON CONFLICT(singleton) DO UPDATE SET status = excluded.status,
                    cursor_sample_id = 0, total_samples = excluded.total_samples;
                """;
            rebuild.Parameters.AddWithValue("$status", count == 0 ? "ready" : "rebuilding");
            rebuild.Parameters.AddWithValue("$total", count);
            rebuild.ExecuteNonQuery();
        }
        WriteSchemaValue("aggregate_schema_version", AggregateSchemaVersion, transaction);
    }

    private void ResetSources(string health, SqliteTransaction transaction)
    {
        using var reset = _connection.CreateCommand();
        reset.Transaction = transaction;
        reset.CommandText = """
            UPDATE source_files SET complete_offset = 0, drain_mode = 0,
                drain_line_start_offset = 0, drain_offset = 0, last_length = 0,
                cursor_turn_key = NULL, health_code = $health, state_revision = state_revision + 1;
            """;
        reset.Parameters.AddWithValue("$health", health);
        reset.ExecuteNonQuery();
    }

    private bool IsAggregateReadyCore()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT status FROM aggregate_rebuild_state WHERE singleton = 1;";
        return string.Equals(command.ExecuteScalar() as string, "ready", StringComparison.Ordinal);
    }

    private FrontierState LoadFrontier(string threadId, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT maximum_cumulative_total, sample_id FROM cumulative_frontiers WHERE thread_id = $thread_id;";
        command.Parameters.AddWithValue("$thread_id", threadId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new FrontierState(reader.GetInt64(0), reader.GetInt64(1)) : new FrontierState(-1, 0);
    }

    private LatestEventState? LoadLatestEvent(string threadId, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT thread_id, sample_id, turn_key, event_time_ticks, source_key, source_generation,
                   source_offset, turn_confidence, event_order_confidence, {AggregateColumns}
            FROM latest_token_events WHERE thread_id = $thread_id;
            """;
        command.Parameters.AddWithValue("$thread_id", threadId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new LatestEventState(reader.GetString(0), reader.GetInt64(1),
            OptionalString(reader, 2), OptionalLong(reader, 3), OptionalString(reader, 4), reader.GetInt32(5),
            OptionalLong(reader, 6), reader.GetString(7), reader.GetString(8), ReadAggregate(reader, 9)) : null;
    }

    private static bool ShouldReplaceLatest(LatestEventState? current, LatestEventState candidate)
    {
        if (current is null) return true;
        if (!candidate.EventTimeTicks.HasValue) return candidate.SampleId > current.SampleId;
        if (!current.EventTimeTicks.HasValue) return true;
        var comparison = candidate.EventTimeTicks.Value.CompareTo(current.EventTimeTicks.Value);
        if (comparison > 0) return true;
        if (comparison < 0) return false;
        return string.Equals(candidate.SourceKey, current.SourceKey, StringComparison.Ordinal) &&
               candidate.SourceGeneration == current.SourceGeneration &&
               candidate.SourceOffset.GetValueOrDefault(-1) > current.SourceOffset.GetValueOrDefault(-1);
    }

    private static IReadOnlyList<(long Start, long End)> BoundaryRanges(long start, long end,
        long firstFullMinute, long endFullMinute)
    {
        var ranges = new List<(long, long)>();
        var firstEnd = Math.Min(firstFullMinute, end);
        if (start < firstEnd) ranges.Add((start, firstEnd));
        var lastStart = Math.Max(endFullMinute, start);
        if (lastStart < end && ranges.All(item => item.Item2 <= lastStart)) ranges.Add((lastStart, end));
        return ranges;
    }

    private static long FloorMinute(long ticks) => ticks - ticks % MinuteTicks;
    private static long CeilingMinute(long ticks) => ticks % MinuteTicks == 0 ? ticks : FloorMinute(ticks) + MinuteTicks;

    private void EnsureColumn(string table, string column, string definition)
    {
        if (ReadColumns(table).Any(item => item.Name.Equals(column, StringComparison.Ordinal))) return;
        using var alter = _connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {QuoteIdentifier(table)} ADD COLUMN {QuoteIdentifier(column)} {definition};";
        alter.ExecuteNonQuery();
    }

    private IReadOnlyList<(string Name, string Type)> ReadColumns(string table)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({QuoteIdentifier(table)});";
        using var reader = command.ExecuteReader();
        var result = new List<(string, string)>();
        while (reader.Read()) result.Add((reader.GetString(1), reader.IsDBNull(2) ? string.Empty : reader.GetString(2)));
        return result;
    }

    private string? ReadSchemaValueCore(string key)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT value FROM schema_info WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    private void WriteSchemaValue(string key, string value, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO schema_info(key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private long ExecuteScalarLong(string sql, string? threadId = null)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            if (threadId is not null) command.Parameters.AddWithValue("$thread_id", threadId);
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    private static SourceFileState ReadSourceState(SqliteDataReader reader)
    {
        var identity = new SourceIdentity(reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetInt64(5) != 0);
        var cursor = new SourceReadCursor(reader.GetInt64(8), reader.GetInt64(9) != 0,
            reader.GetInt64(10), reader.GetInt64(11)).Normalize();
        return new SourceFileState(identity, reader.GetString(6), reader.GetInt32(7), cursor,
            reader.GetInt64(12), reader.GetInt64(13), OptionalString(reader, 14), reader.GetString(15),
            reader.GetInt64(16));
    }

    private static SessionMetadata ReadSessionMetadata(SqliteDataReader reader, int sessionKindIndex = 15,
        int sessionSurfaceIndex = 16, int parentThreadIndex = 17, int agentDepthIndex = 18)
    {
        var status = Enum.TryParse<SessionStatus>(reader.GetString(10), out var parsed)
            ? parsed : SessionStatus.Idle;
        var kind = !reader.IsDBNull(sessionKindIndex) &&
                   Enum.TryParse<SessionKind>(reader.GetString(sessionKindIndex), true, out var parsedKind)
            ? parsedKind : SessionKind.Unknown;
        var surface = !reader.IsDBNull(sessionSurfaceIndex) &&
                      Enum.TryParse<SessionSurface>(reader.GetString(sessionSurfaceIndex), true,
                          out var parsedSurface)
            ? parsedSurface : SessionSurface.Unknown;
        return new SessionMetadata(reader.GetString(0), OptionalString(reader, 1), OptionalString(reader, 2),
            OptionalString(reader, 3), OptionalString(reader, 4), OptionalString(reader, 5),
            OptionalString(reader, 6), OptionalString(reader, 7), OptionalDate(reader, 9), null,
            reader.GetInt64(14) != 0, status, OptionalString(reader, 11), reader.GetInt64(12),
            reader.GetInt64(13) != 0, reader.GetString(8), kind, surface,
            OptionalString(reader, parentThreadIndex), OptionalInt(reader, agentDepthIndex));
    }

    private static SessionWork ReadSessionWork(SqliteDataReader reader)
    {
        var metadata = ReadSessionMetadata(reader, 26, 27, 28, 29);
        var structural = reader.IsDBNull(16) ? null : new StructuralCursor(reader.GetString(16),
            OptionalString(reader, 17) ?? string.Empty, reader.GetInt32(18), reader.GetInt64(19),
            OptionalDate(reader, 20));
        return new SessionWork(metadata, true, OptionalString(reader, 11), reader.GetInt64(15) != 0,
            reader.GetInt64(12), reader.GetInt64(13) != 0, structural, OptionalLong(reader, 21),
            OptionalString(reader, 22), reader.GetInt32(23), reader.GetInt64(24), OptionalString(reader, 25));
    }

    private static RebuildSample ReadRebuildSample(SqliteDataReader reader) => new(
        reader.GetInt64(0), reader.GetString(1), OptionalString(reader, 2), OptionalDate(reader, 3),
        OptionalLong(reader, 4), OptionalString(reader, 5), reader.GetInt32(6), OptionalLong(reader, 7),
        ReadAggregate(reader, 8), reader.GetInt64(16), reader.GetString(17), reader.GetString(18),
        OptionalLong(reader, 19), reader.GetInt64(20) != 0, ReadLong(reader, 21), ReadLong(reader, 22),
        OptionalString(reader, 23));

    private static CanonicalTokenUsage ReadAggregate(SqliteDataReader reader, int start = 0) => new(
        ReadLong(reader, start), ReadLong(reader, start + 1), ReadLong(reader, start + 2),
        ReadLong(reader, start + 3), ReadLong(reader, start + 4), ReadLong(reader, start + 5),
        ReadLong(reader, start + 6), ReadLong(reader, start + 7));

    private static QuotaBucket ReadQuotaBucket(SqliteDataReader reader) => new(reader.GetString(0),
        reader.GetString(1), reader.GetDouble(2), reader.GetInt32(3),
        OptionalDate(reader, 4) ?? DateTimeOffset.MinValue);

    private static void AddUsageParameters(SqliteCommand command, CanonicalTokenUsage usage)
    {
        command.Parameters.AddWithValue("$input", usage.Input);
        command.Parameters.AddWithValue("$raw_input", usage.RawInput);
        command.Parameters.AddWithValue("$cached", usage.CachedInput);
        command.Parameters.AddWithValue("$cache_write", usage.CacheWriteInput);
        command.Parameters.AddWithValue("$output", usage.Output);
        command.Parameters.AddWithValue("$reasoning", usage.Reasoning);
        command.Parameters.AddWithValue("$canonical_total", usage.Total);
        command.Parameters.AddWithValue("$reported_total", usage.ReportedTotal);
    }

    private static SessionStatus StatusFor(bool turnOpen, DateTimeOffset? activity, DateTimeOffset nowUtc)
    {
        if (!turnOpen) return SessionStatus.Idle;
        return activity.HasValue && nowUtc - activity.Value <= TimeSpan.FromSeconds(120)
            ? SessionStatus.Running : SessionStatus.Unknown;
    }

    private static DateTimeOffset? Max(DateTimeOffset? left, DateTimeOffset? right)
    {
        if (!left.HasValue) return right;
        if (!right.HasValue) return left;
        return left.Value >= right.Value ? left : right;
    }

    private static (string? Tier, string Source) SelectServiceTier(string? existingTier,
        string? existingSource, string? incomingTier, string? incomingSource,
        bool preferIncomingOnEqual)
    {
        var normalizedExistingSource = NormalizeStoredTierSource(existingTier, existingSource);
        var normalizedIncomingSource = NormalizeIncomingTierSource(incomingTier, incomingSource);
        if (IsUnavailableTier(incomingTier))
            return (existingTier, normalizedExistingSource);

        var existingPriority = ServiceTierProvenance.Priority(normalizedExistingSource);
        var incomingPriority = ServiceTierProvenance.Priority(normalizedIncomingSource);
        return incomingPriority > existingPriority || preferIncomingOnEqual && incomingPriority == existingPriority
            ? (incomingTier, normalizedIncomingSource)
            : (existingTier, normalizedExistingSource);
    }

    private static string NormalizeStoredTierSource(string? tier, string? source)
    {
        if (IsUnavailableTier(tier)) return ServiceTierProvenance.Unavailable;
        if (!ServiceTierProvenance.IsValid(source))
            throw new InvalidOperationException("SERVICE_TIER_SOURCE_INVALID");
        return source == ServiceTierProvenance.Unavailable
            ? ServiceTierProvenance.LegacyPreserved
            : source!;
    }

    private static string NormalizeIncomingTierSource(string? tier, string? source)
    {
        if (IsUnavailableTier(tier)) return ServiceTierProvenance.Unavailable;
        if (!ServiceTierProvenance.IsValid(source))
            throw new InvalidOperationException("SERVICE_TIER_SOURCE_INVALID");
        return source == ServiceTierProvenance.Unavailable
            ? ServiceTierProvenance.LegacyPreserved
            : source!;
    }

    private static bool IsUnavailableTier(string? tier) => string.IsNullOrWhiteSpace(tier) ||
        string.Equals(tier, "不可用", StringComparison.Ordinal);

    private static string SafeRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value))
            return "redacted/" + TokenUsageSnapshot.HashText(value ?? string.Empty)[..16] + ".jsonl";
        var normalized = value.Replace('\\', '/');
        if (normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(part => part == ".."))
            return "redacted/" + TokenUsageSnapshot.HashText(normalized)[..16] + ".jsonl";
        return normalized;
    }

    private SqliteTransaction BeginImmediate() => _connection.BeginTransaction(deferred: false);
    private static long ReadLong(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? 0 : reader.GetInt64(index);
    private static long? OptionalLong(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetInt64(index);
    private static int? OptionalInt(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetInt32(index);
    private static string? OptionalString(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);

    private static DateTimeOffset? OptionalDate(SqliteDataReader reader, int index)
    {
        var text = OptionalString(reader, index);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value)
            ? value.ToUniversalTime() : null;
    }

    private static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    private static string QuoteIdentifier(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _connection.Dispose();
        }
    }

    private const string AggregateColumns = """
        input_tokens, raw_input_tokens, cached_input_tokens, cache_write_input_tokens,
        output_tokens, reasoning_output_tokens, canonical_total_tokens, reported_total_tokens
        """;

    private const string AggregateParameterColumns = """
        $input, $raw_input, $cached, $cache_write, $output, $reasoning, $canonical_total, $reported_total
        """;

    private const string AggregateUpdateColumns = """
        input_tokens = excluded.input_tokens,
        raw_input_tokens = excluded.raw_input_tokens,
        cached_input_tokens = excluded.cached_input_tokens,
        cache_write_input_tokens = excluded.cache_write_input_tokens,
        output_tokens = excluded.output_tokens,
        reasoning_output_tokens = excluded.reasoning_output_tokens,
        canonical_total_tokens = excluded.canonical_total_tokens,
        reported_total_tokens = excluded.reported_total_tokens
        """;

    private const string RecurringTurnTotalsSql = """
        WITH requested(thread_id, turn_key) AS MATERIALIZED (
            SELECT thread_id, current_turn_key FROM sessions WHERE current_turn_key IS NOT NULL
            UNION
            SELECT thread_id, turn_key FROM latest_token_events
            WHERE turn_key IS NOT NULL AND turn_confidence = 'reliable'
        )
        SELECT aggregates.thread_id, aggregates.turn_key,
               aggregates.input_tokens, aggregates.raw_input_tokens,
               aggregates.cached_input_tokens, aggregates.cache_write_input_tokens,
               aggregates.output_tokens, aggregates.reasoning_output_tokens,
               aggregates.canonical_total_tokens, aggregates.reported_total_tokens
        FROM requested
        JOIN turn_token_aggregates AS aggregates INDEXED BY sqlite_autoindex_turn_token_aggregates_1
          ON aggregates.thread_id = requested.thread_id AND aggregates.turn_key = requested.turn_key
        ORDER BY aggregates.thread_id, aggregates.turn_key;
        """;

    private const string SessionWorkSelect = """
        SELECT thread_id, display_name, role, nickname, project_tag, model,
               reasoning_effort, service_tier, service_tier_source, last_activity_utc, status,
               current_turn_key, turn_sequence, turn_open, pinned, current_turn_reliable,
               current_structural_event_identity, current_structural_source_hash,
               current_structural_generation, current_structural_offset, current_structural_time_utc,
               state_event_time_ticks, state_source_key, state_source_generation,
               state_source_offset, state_event_kind, session_kind, session_surface,
               parent_thread_id, agent_depth
        FROM sessions
        """;

    private sealed class AggregateAccumulator
    {
        public Dictionary<string, CanonicalTokenUsage> SessionDeltas { get; } = new(StringComparer.Ordinal);
        public Dictionary<(string ThreadId, string TurnKey), CanonicalTokenUsage> TurnDeltas { get; } = new();
        public Dictionary<long, CanonicalTokenUsage> BucketDeltas { get; } = new();
        public Dictionary<string, FrontierState> Frontiers { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, LatestEventState> Latest { get; } = new(StringComparer.Ordinal);
        public List<TimedDelta> TimedDeltas { get; } = new();

        public void Register(TokenSampleCandidate candidate, long sampleId, CanonicalTokenUsage usage,
            long cumulativeTotal, ISet<string> diagnostics, SqliteTransaction transaction,
            UsageDatabase database)
        {
            SessionDeltas[candidate.ThreadId] = SessionDeltas.GetValueOrDefault(candidate.ThreadId) + usage;
            if (candidate.TurnKey is not null)
            {
                var key = (candidate.ThreadId, candidate.TurnKey);
                TurnDeltas[key] = TurnDeltas.GetValueOrDefault(key) + usage;
            }
            if (candidate.EventTimeUtc.HasValue)
            {
                var ticks = candidate.EventTimeUtc.Value.UtcTicks;
                var bucket = FloorMinute(ticks);
                BucketDeltas[bucket] = BucketDeltas.GetValueOrDefault(bucket) + usage;
                TimedDeltas.Add(new TimedDelta(ticks, usage));
            }

            if (!Frontiers.TryGetValue(candidate.ThreadId, out var frontier))
                frontier = database.LoadFrontier(candidate.ThreadId, transaction);
            if (frontier.Maximum > cumulativeTotal) diagnostics.Add("cumulative_interleaving_observed");
            Frontiers[candidate.ThreadId] = new FrontierState(Math.Max(frontier.Maximum, cumulativeTotal), sampleId);

            if (!Latest.TryGetValue(candidate.ThreadId, out var current))
                current = database.LoadLatestEvent(candidate.ThreadId, transaction);
            var next = new LatestEventState(candidate.ThreadId, sampleId, candidate.TurnKey,
                candidate.EventTimeUtc?.UtcTicks, candidate.SourceKey, candidate.SourceGeneration,
                candidate.SourceOffset, candidate.TurnConfidence, candidate.EventOrderConfidence, usage);
            if (ShouldReplaceLatest(current, next)) Latest[candidate.ThreadId] = next;
            else if (current is not null) Latest[candidate.ThreadId] = current;
        }

        public void SubtractConsumption(CanonicalSample retired)
        {
            SessionDeltas[retired.ThreadId] = SessionDeltas.GetValueOrDefault(retired.ThreadId) +
                                              retired.Usage.Negate();
            if (retired.TurnKey is not null)
            {
                var key = (retired.ThreadId, retired.TurnKey);
                TurnDeltas[key] = TurnDeltas.GetValueOrDefault(key) + retired.Usage.Negate();
            }
            if (retired.EventTimeTicks.HasValue)
            {
                var ticks = retired.EventTimeTicks.Value;
                var bucket = FloorMinute(ticks);
                BucketDeltas[bucket] = BucketDeltas.GetValueOrDefault(bucket) + retired.Usage.Negate();
                TimedDeltas.Add(new TimedDelta(ticks, retired.Usage.Negate()));
            }
        }

        public void AddConsumption(CanonicalSample promoted)
        {
            SessionDeltas[promoted.ThreadId] = SessionDeltas.GetValueOrDefault(promoted.ThreadId) + promoted.Usage;
            if (promoted.TurnKey is not null)
            {
                var key = (promoted.ThreadId, promoted.TurnKey);
                TurnDeltas[key] = TurnDeltas.GetValueOrDefault(key) + promoted.Usage;
            }
            if (promoted.EventTimeTicks.HasValue)
            {
                var ticks = promoted.EventTimeTicks.Value;
                var bucket = FloorMinute(ticks);
                BucketDeltas[bucket] = BucketDeltas.GetValueOrDefault(bucket) + promoted.Usage;
                TimedDeltas.Add(new TimedDelta(ticks, promoted.Usage));
            }
        }
    }

    private sealed class SessionWork
    {
        public SessionWork(SessionMetadata metadata, bool exists, string? currentTurnKey,
            bool currentTurnReliable, long turnSequence, bool turnOpen, StructuralCursor? structuralCursor,
            long? stateEventTimeTicks, string? stateSourceKey, int stateSourceGeneration,
            long stateSourceOffset, string? stateEventKind)
        {
            Metadata = metadata;
            Exists = exists;
            CurrentTurnKey = currentTurnKey;
            CurrentTurnReliable = currentTurnReliable;
            TurnSequence = turnSequence;
            TurnOpen = turnOpen;
            StructuralCursor = structuralCursor;
            StateEventTimeTicks = stateEventTimeTicks;
            StateSourceKey = stateSourceKey;
            StateSourceGeneration = stateSourceGeneration;
            StateSourceOffset = stateSourceOffset;
            StateEventKind = stateEventKind;
            Dirty = !exists;
        }

        public SessionMetadata Metadata { get; set; }
        public bool Exists { get; set; }
        public string? CurrentTurnKey { get; set; }
        public bool CurrentTurnReliable { get; set; }
        public long TurnSequence { get; set; }
        public bool TurnOpen { get; set; }
        public StructuralCursor? StructuralCursor { get; set; }
        public long? StateEventTimeTicks { get; set; }
        public string? StateSourceKey { get; set; }
        public int StateSourceGeneration { get; set; }
        public long StateSourceOffset { get; set; }
        public string? StateEventKind { get; set; }
        public bool Dirty { get; set; }

        public static SessionWork Create(string threadId) => new(
            new SessionMetadata(threadId, null, null, null, null, null, null, null, null, null),
            false, null, false, 0, false, null, null, null, 0, -1, null);
    }

    private sealed class ScanTurn
    {
        public ScanTurn(string? key, bool reliable) { Key = key; Reliable = reliable; }
        public string? Key { get; set; }
        public bool Reliable { get; set; }
    }

    private readonly record struct TokenInsertResult(string Fingerprint, TokenDisposition Disposition,
        long? SampleId, int Writes, CanonicalTokenUsage Usage, long CumulativeTotal,
        bool MetadataRecovered, string SemanticIdentity = "", bool IsLineageCanonical = false);

    private readonly record struct PersistedTokenSample(long SampleId, string ThreadId,
        CanonicalTokenUsage Usage, long CumulativeTotal, string SemanticIdentity = "",
        bool IsLineageCanonical = false);

    private readonly record struct StructuralResolution(StructuralDispositionRecord Disposition,
        string? AssociationKey, bool Canonical, bool Advanced, string? DiagnosticCode);

    private readonly record struct FrontierState(long Maximum, long SampleId);
    private readonly record struct TimedDelta(long EventTimeTicks, CanonicalTokenUsage Usage);
    private sealed record LatestEventState(string ThreadId, long SampleId, string? TurnKey,
        long? EventTimeTicks, string? SourceKey, int SourceGeneration, long? SourceOffset,
        string TurnConfidence, string EventOrderConfidence, CanonicalTokenUsage Usage);
    private sealed record ContextState(string ThreadId, long LatestEventTicks, string? LatestSourceKey,
        int LatestSourceGeneration, long? LatestSourceOffset, long LatestSampleId,
        long? CurrentInputTokens, long? CurrentContextWindow, string? CurrentModel,
        long? HighInputTokens, long? HighContextWindow, long? HighCumulativeTotal,
        long? HighEventTicks, string? HighTurnKey, bool AwaitingPost, long? MarkerEventTicks,
        string? MarkerTurnKey, long? ExplicitMarkerTicks, string? ExplicitMarkerSourceKey,
        int ExplicitMarkerSourceGeneration, long? ExplicitMarkerSourceOffset,
        string? ExplicitMarkerIdentity, string? ExplicitMarkerTurnKey);
    private readonly record struct ContextBaseline(long SampleId, long InputTokens, long ContextWindow,
        long EventTimeTicks, string ModelKey, string DetectionSource, long? RunwayTurns,
        long? RunwayTokens)
    {
        public double Ratio => InputTokens * 100d / ContextWindow;
    }
    private readonly record struct RecentSessionActivity(long TotalTokens, long TurnCount);
    private sealed record RebuildSample(long Id, string ThreadId, string? TurnKey,
        DateTimeOffset? EventTimeUtc, long? EventTimeTicks, string? SourceKey, int SourceGeneration,
        long? SourceOffset, CanonicalTokenUsage Usage, long CumulativeTotal,
        string TurnConfidence, string EventOrderConfidence, long? ContextWindow = null,
        bool IsLineageCanonical = true, long CumulativeInput = 0, long CumulativeOutput = 0,
        string? Model = null);
    private readonly record struct CompactReplayEvent(long Ticks, long Offset, string SourceKey, int Generation,
        string EventIdentity, string? AssociationKey);
    private readonly record struct LineageAssignment(bool IsCanonical, IReadOnlyList<CanonicalSample> Retired,
        IReadOnlyList<CanonicalSample> Promoted)
    {
        public static LineageAssignment Empty { get; } = new(false, Array.Empty<CanonicalSample>(),
            Array.Empty<CanonicalSample>());
    }
    private sealed record LineageMember(long Id, string ThreadId, CanonicalTokenUsage Usage,
        string? TurnKey, long? EventTimeTicks, string? SourceKey, int SourceGeneration,
        long? SourceOffset, string TurnConfidence, string EventOrderConfidence, long CumulativeTotal,
        bool Canonical, string Identity, string Material, string LegacyIdentity);
    private sealed record CanonicalSample(long Id, string ThreadId, CanonicalTokenUsage Usage,
        string? TurnKey, long? EventTimeTicks, string? SourceKey, int SourceGeneration,
        long? SourceOffset, string TurnConfidence, string EventOrderConfidence, long CumulativeTotal);
    private sealed record LegacySource(string SourceKey, string ThreadId, string VolumeSerial,
        string FileId, string FallbackKey, bool IsDegraded, string RelativePath, int Generation,
        long CompleteOffset, long LastLength, long LastWriteTicks, string? CursorTurnKey,
        string HealthCode);
    private sealed record ConvertedLegacySource(LegacySource Legacy, SourceIdentity Identity,
        bool RequiresReset);
}
