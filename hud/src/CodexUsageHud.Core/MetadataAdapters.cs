using Microsoft.Data.Sqlite;
using System.Buffers;
using System.Globalization;
using System.Text.Json;

namespace CodexUsageHud.Core;

public sealed record ThreadMetadataRow(
    string ThreadId,
    string? RolloutRelativePath,
    DateTimeOffset? CreatedAtUtc,
    DateTimeOffset? UpdatedAtUtc,
    string? Cwd,
    string? Model,
    string? ReasoningEffort,
    string? Source,
    string? AgentNickname,
    string? AgentRole,
    string? Name,
    string? AccountId = null,
    string? UserId = null);

public sealed record SessionSourceClassification(
    SessionKind Kind,
    SessionSurface Surface,
    string? ParentThreadId = null,
    int? AgentDepth = null);

public static class MetadataQuery
{
    public static readonly IReadOnlyList<string> AllowedColumns = new[]
    {
        "id", "rollout_path", "created_at", "updated_at", "cwd", "model",
        "reasoning_effort", "source", "agent_nickname", "agent_role", "name",
        "account_id", "chatgpt_account_id", "user_id",
    };

    public static string Build(IReadOnlySet<string> availableColumns)
    {
        var selected = Selected(availableColumns);
        return selected.Contains("id", StringComparer.Ordinal)
            ? $"SELECT {string.Join(", ", selected.Select(QuoteIdentifier))} FROM threads;"
            : string.Empty;
    }

    public static IReadOnlyList<string> Selected(IReadOnlySet<string> availableColumns) =>
        AllowedColumns.Where(availableColumns.Contains).ToArray();

    private static string QuoteIdentifier(string value) => "\"" + value + "\"";
}

public static class RolloutPathNormalizer
{
    public static bool TryNormalize(string codexHome, string? suppliedPath, out string relativePath)
    {
        relativePath = string.Empty;
        if (string.IsNullOrWhiteSpace(suppliedPath))
        {
            return false;
        }

        try
        {
            var home = Path.GetFullPath(CodexHomeResolver.Resolve(codexHome)).TrimEnd(Path.DirectorySeparatorChar);
            var candidate = RemoveExtendedPrefix(suppliedPath.Trim());
            var full = Path.GetFullPath(Path.IsPathFullyQualified(candidate)
                ? candidate
                : Path.Combine(home, candidate.Replace('/', Path.DirectorySeparatorChar)));
            var sessions = Path.Combine(home, "sessions");
            var archived = Path.Combine(home, "archived_sessions");
            if (!IsBelow(full, sessions) && !IsBelow(full, archived))
            {
                return false;
            }

            relativePath = Path.GetRelativePath(home, full).Replace(Path.DirectorySeparatorChar, '/');
            return relativePath.StartsWith("sessions/", StringComparison.OrdinalIgnoreCase) ||
                   relativePath.StartsWith("archived_sessions/", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsBelow(string path, string root)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string RemoveExtendedPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[8..];
        }

        return path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) ? path[4..] : path;
    }
}

public sealed class StateMetadataReader
{
    public IdentityColumnPresence LastIdentityColumns { get; private set; } = IdentityColumnPresence.Missing;

    public IdentityColumnPresence InspectIdentityColumns(string? sqlitePath)
    {
        if (string.IsNullOrWhiteSpace(sqlitePath) || !File.Exists(sqlitePath))
        {
            LastIdentityColumns = IdentityColumnPresence.Missing;
            return LastIdentityColumns;
        }

        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = sqlitePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                DefaultTimeout = 2,
            };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            LastIdentityColumns = IdentityColumnPresence.FromColumns(true, ReadColumns(connection));
            return LastIdentityColumns;
        }
        catch (SqliteException)
        {
            LastIdentityColumns = IdentityColumnPresence.Missing;
            return LastIdentityColumns;
        }
        catch (IOException)
        {
            LastIdentityColumns = IdentityColumnPresence.Missing;
            return LastIdentityColumns;
        }
        catch (UnauthorizedAccessException)
        {
            LastIdentityColumns = IdentityColumnPresence.Missing;
            return LastIdentityColumns;
        }
    }

    public IReadOnlyList<ThreadMetadataRow> Read(string codexHome)
    {
        var resolvedHome = CodexHomeResolver.Resolve(codexHome);
        var path = Path.Combine(resolvedHome, "state_5.sqlite");
        if (!File.Exists(path))
        {
            LastIdentityColumns = IdentityColumnPresence.Missing;
            return Array.Empty<ThreadMetadataRow>();
        }

        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                DefaultTimeout = 2,
            };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            var available = ReadColumns(connection);
            LastIdentityColumns = IdentityColumnPresence.FromColumns(true, available);
            var selected = MetadataQuery.Selected(available);
            var sql = MetadataQuery.Build(available);
            if (string.IsNullOrEmpty(sql))
            {
                return Array.Empty<ThreadMetadataRow>();
            }

            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            var rows = new List<ThreadMetadataRow>();
            while (reader.Read())
            {
                try
                {
                    var values = new Dictionary<string, object?>(StringComparer.Ordinal);
                    for (var index = 0; index < reader.FieldCount; index++)
                    {
                        values[selected[index]] = reader.IsDBNull(index) ? null : reader.GetValue(index);
                    }

                    var threadId = AsString(values, "id");
                    if (string.IsNullOrWhiteSpace(threadId))
                    {
                        continue;
                    }

                    var suppliedRollout = AsString(values, "rollout_path");
                    var rollout = RolloutPathNormalizer.TryNormalize(resolvedHome, suppliedRollout, out var normalized)
                        ? normalized
                        : null;
                    rows.Add(new ThreadMetadataRow(threadId, rollout,
                        AsDate(values, "created_at"), AsDate(values, "updated_at"), AsString(values, "cwd"),
                        AsString(values, "model"), AsString(values, "reasoning_effort"), AsString(values, "source"),
                        AsString(values, "agent_nickname"), AsString(values, "agent_role"), AsString(values, "name"),
                        AsString(values, "account_id") ?? AsString(values, "chatgpt_account_id"),
                        AsString(values, "user_id")));
                }
                catch (InvalidCastException)
                {
                    // One malformed metadata row is isolated; later rows remain available.
                }
                catch (FormatException)
                {
                    // One malformed metadata row is isolated; later rows remain available.
                }
                catch (OverflowException)
                {
                    // One malformed metadata row is isolated; later rows remain available.
                }
            }

            return rows;
        }
        catch (SqliteException)
        {
            LastIdentityColumns = IdentityColumnPresence.Missing;
            return Array.Empty<ThreadMetadataRow>();
        }
        catch (IOException)
        {
            LastIdentityColumns = IdentityColumnPresence.Missing;
            return Array.Empty<ThreadMetadataRow>();
        }
        catch (UnauthorizedAccessException)
        {
            LastIdentityColumns = IdentityColumnPresence.Missing;
            return Array.Empty<ThreadMetadataRow>();
        }
    }

    private static HashSet<string> ReadColumns(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(threads);";
        using var reader = command.ExecuteReader();
        var columns = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            if (!reader.IsDBNull(1))
            {
                columns.Add(reader.GetString(1));
            }
        }

        return columns;
    }

    private static string? AsString(IReadOnlyDictionary<string, object?> values, string key) =>
        values.TryGetValue(key, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;

    internal static DateTimeOffset? AsDate(IReadOnlyDictionary<string, object?> values, string key)
    {
        if (!values.TryGetValue(key, out var raw) || raw is null)
        {
            return null;
        }

        if (TryInteger(raw, out var unix))
        {
            try
            {
                return Math.Abs(unix) >= 10_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(unix)
                    : DateTimeOffset.FromUnixTimeSeconds(unix);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        var text = Convert.ToString(raw, CultureInfo.InvariantCulture);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value)
            ? value.ToUniversalTime()
            : null;
    }

    private static bool TryInteger(object value, out long number)
    {
        switch (value)
        {
            case long item: number = item; return true;
            case int item: number = item; return true;
            case short item: number = item; return true;
            case byte item: number = item; return true;
            case string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                number = parsed;
                return true;
            default:
                number = 0;
                return false;
        }
    }
}

public sealed class SessionIndexReader
{
    private const int BlockSize = 16 * 1024;

    public IReadOnlyDictionary<string, string> Read(string codexHome)
    {
        var path = Path.Combine(CodexHomeResolver.Resolve(codexHome), "session_index.jsonl");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return result;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, BlockSize, FileOptions.SequentialScan);
            var block = ArrayPool<byte>.Shared.Rent(BlockSize);
            var line = ArrayPool<byte>.Shared.Rent(PrivacyJsonlReader.AllowlistedRecordLimit);
            var count = 0;
            var overflow = false;
            try
            {
                int read;
                while ((read = stream.Read(block, 0, BlockSize)) > 0)
                {
                    for (var index = 0; index < read; index++)
                    {
                        var value = block[index];
                        if (value == (byte)'\n')
                        {
                            if (!overflow)
                            {
                                var length = count > 0 && line[count - 1] == (byte)'\r' ? count - 1 : count;
                                TryReadApproved(line.AsSpan(0, length), result);
                            }

                            count = 0;
                            overflow = false;
                            continue;
                        }

                        if (count < PrivacyJsonlReader.AllowlistedRecordLimit)
                        {
                            line[count++] = value;
                        }
                        else
                        {
                            overflow = true;
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(line, clearArray: true);
                ArrayPool<byte>.Shared.Return(block, clearArray: true);
            }
        }
        catch (IOException)
        {
            // Optional metadata failure does not block rollout indexing.
        }
        catch (UnauthorizedAccessException)
        {
            // Optional metadata failure does not block rollout indexing.
        }

        return result;
    }

    private static void TryReadApproved(ReadOnlySpan<byte> bytes, IDictionary<string, string> result)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        try
        {
            var reader = new Utf8JsonReader(bytes, isFinalBlock: true, state: default);
            ApprovedField field = ApprovedField.None;
            string? id = null;
            string? name = null;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1)
                {
                    field = reader.ValueTextEquals("thread_id"u8) || reader.ValueTextEquals("id"u8)
                        ? ApprovedField.Id
                        : reader.ValueTextEquals("thread_name"u8) || reader.ValueTextEquals("name"u8)
                            ? ApprovedField.Name
                            : ApprovedField.None;
                    continue;
                }

                if (reader.TokenType == JsonTokenType.String)
                {
                    if (field == ApprovedField.Id)
                    {
                        id ??= reader.GetString();
                    }
                    else if (field == ApprovedField.Name)
                    {
                        name ??= reader.GetString();
                    }
                }

                field = ApprovedField.None;
            }

            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
            {
                result[id] = name;
            }
        }
        catch (JsonException)
        {
            // The malformed line is isolated and parsing continues at the next newline.
        }
    }

    private enum ApprovedField { None, Id, Name }
}

public static class SessionMetadataMapper
{
    public static SessionMetadata ToSession(ThreadMetadataRow row, IReadOnlyDictionary<string, string> indexNames)
    {
        var classification = ClassifySourceDetails(row.Source);
        var name = !string.IsNullOrWhiteSpace(row.Name)
            ? row.Name
            : indexNames.TryGetValue(row.ThreadId, out var indexed) ? indexed : null;
        if (string.IsNullOrWhiteSpace(name) && classification.Kind == SessionKind.InternalTask &&
            !string.IsNullOrWhiteSpace(row.AgentNickname))
        {
            name = row.AgentNickname;
        }
        var project = GetProjectTag(row.Cwd);
        return new SessionMetadata(row.ThreadId, name, row.AgentRole, row.AgentNickname, project,
            row.Model, row.ReasoningEffort, null, row.UpdatedAtUtc ?? row.CreatedAtUtc, row.RolloutRelativePath,
            Kind: classification.Kind, Surface: classification.Surface,
            ParentThreadId: classification.ParentThreadId, AgentDepth: classification.AgentDepth);
    }

    public static SessionKind ClassifySource(string? source) => ClassifySourceDetails(source).Kind;

    public static SessionSourceClassification ClassifySourceDetails(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return new SessionSourceClassification(SessionKind.Unknown, SessionSurface.Unknown);
        var trimmed = source.Trim();
        if (trimmed.Length > 16 * 1024)
            return new SessionSourceClassification(SessionKind.Unknown, SessionSurface.Unknown);
        if (trimmed.Equals("subagent", StringComparison.OrdinalIgnoreCase))
            return new SessionSourceClassification(SessionKind.InternalTask, SessionSurface.InternalTask);

        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("subagent", out var subagent))
                {
                    string? parentThreadId = null;
                    int? depth = null;
                    if (subagent.ValueKind == JsonValueKind.Object &&
                        subagent.TryGetProperty("thread_spawn", out var spawn) &&
                        spawn.ValueKind == JsonValueKind.Object)
                    {
                        if (spawn.TryGetProperty("parent_thread_id", out var parent) &&
                            parent.ValueKind == JsonValueKind.String)
                        {
                            var candidate = parent.GetString();
                            if (Guid.TryParse(candidate, out _)) parentThreadId = candidate;
                        }
                        if (spawn.TryGetProperty("depth", out var depthElement) &&
                            depthElement.TryGetInt32(out var candidateDepth) &&
                            candidateDepth is >= 0 and <= 32)
                        {
                            depth = candidateDepth;
                        }
                    }
                    return new SessionSourceClassification(SessionKind.InternalTask,
                        SessionSurface.InternalTask, parentThreadId, depth);
                }
            }
            catch (JsonException)
            {
                return new SessionSourceClassification(SessionKind.Unknown, SessionSurface.Unknown);
            }
        }

        if (trimmed.Equals("cli", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("exec", StringComparison.OrdinalIgnoreCase))
        {
            return new SessionSourceClassification(SessionKind.Primary, SessionSurface.Cli);
        }
        if (trimmed.Equals("vscode", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("app_server", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("appServer", StringComparison.OrdinalIgnoreCase))
        {
            return new SessionSourceClassification(SessionKind.Primary, SessionSurface.App);
        }
        return new SessionSourceClassification(SessionKind.Unknown, SessionSurface.Unknown);
    }

    private static string? GetProjectTag(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd))
        {
            return null;
        }

        var trimmed = cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}

public static class ServiceTierResolver
{
    public static string Resolve(string? explicitTier, string? model,
        IReadOnlyDictionary<string, string?>? catalogDefaults = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitTier))
        {
            if (explicitTier.Equals("priority", StringComparison.OrdinalIgnoreCase) ||
                explicitTier.Equals("fast", StringComparison.OrdinalIgnoreCase))
            {
                return "Fast";
            }

            if (explicitTier.Equals("default", StringComparison.OrdinalIgnoreCase) ||
                explicitTier.Equals("standard", StringComparison.OrdinalIgnoreCase) ||
                explicitTier.Equals("Standard（默认）", StringComparison.Ordinal))
            {
                return "Standard（默认）";
            }

            return explicitTier;
        }

        if (!string.IsNullOrWhiteSpace(model) && catalogDefaults is not null &&
            catalogDefaults.TryGetValue(model, out var modelDefault) &&
            (string.IsNullOrWhiteSpace(modelDefault) || modelDefault.Equals("default", StringComparison.OrdinalIgnoreCase)))
        {
            return "Standard（默认）";
        }

        return "不可用";
    }
}
