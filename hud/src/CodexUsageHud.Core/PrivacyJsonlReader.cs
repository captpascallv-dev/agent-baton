using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace CodexUsageHud.Core;

public sealed class PrivacyJsonlReader
{
    public const int CandidatePrefixLimit = 64 * 1024;
    public const int AllowlistedRecordLimit = 256 * 1024;
    public const long DefaultBatchBytes = 16L * 1024 * 1024;
    private const int ReadBlockSize = 16 * 1024;
    private static readonly TimeSpan DefaultBatchTime = TimeSpan.FromMilliseconds(250);

    public ParseBatchResult Read(string path, long startOffset,
        CancellationToken cancellationToken = default, long maxBytes = DefaultBatchBytes,
        TimeSpan? maxElapsed = null) =>
        Read(path, new SourceReadCursor(startOffset), cancellationToken, maxBytes, maxElapsed);

    public ParseBatchResult Read(string path, SourceReadCursor sourceCursor,
        CancellationToken cancellationToken = default, long maxBytes = DefaultBatchBytes,
        TimeSpan? maxElapsed = null)
    {
        var events = new List<ParsedEvent>();
        var errors = new List<string>();
        var cursor = sourceCursor.Normalize();
        var linesRead = 0;
        var linesSkipped = 0;
        var bytesConsumed = 0L;
        var peakRetained = 0;
        var hasMoreData = false;
        var stoppedByBudget = false;
        var stopwatch = Stopwatch.StartNew();
        var timeBudget = maxElapsed ?? DefaultBatchTime;
        maxBytes = Math.Max(1, maxBytes);

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, ReadBlockSize, FileOptions.SequentialScan);
            if (cursor.CompleteOffset > stream.Length || (cursor.DrainMode && cursor.DrainOffset > stream.Length))
                cursor = new SourceReadCursor(0);

            var originalCompleteOffset = cursor.CompleteOffset;
            var position = cursor.DrainMode ? cursor.DrainOffset : cursor.CompleteOffset;
            var lineStart = cursor.DrainMode ? cursor.DrainLineStartOffset : cursor.CompleteOffset;
            stream.Position = position;
            var processedPosition = position;
            var block = ArrayPool<byte>.Shared.Rent(ReadBlockSize);
            var candidate = ArrayPool<byte>.Shared.Rent(CandidatePrefixLimit);
            byte[]? allowlisted = null;
            var candidateCount = 0;
            var allowlistedCount = 0;
            var state = cursor.DrainMode ? LineState.Drain : LineState.Candidate;
            var stop = false;

            try
            {
                while (!stop)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (bytesConsumed >= maxBytes || stopwatch.Elapsed >= timeBudget)
                    {
                        stoppedByBudget = processedPosition < stream.Length;
                        break;
                    }

                    var read = stream.Read(block, 0, ReadBlockSize);
                    if (read == 0) break;
                    var blockStart = stream.Position - read;
                    for (var index = 0; index < read; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (bytesConsumed >= maxBytes || stopwatch.Elapsed >= timeBudget)
                        {
                            stoppedByBudget = blockStart + index < stream.Length;
                            stop = true;
                            break;
                        }

                        var value = block[index];
                        var absolute = blockStart + index;
                        var nextOffset = absolute + 1;
                        bytesConsumed++;
                        processedPosition = nextOffset;

                        if (value == (byte)'\n')
                        {
                            linesRead++;
                            if (state == LineState.Drain)
                            {
                                linesSkipped++;
                            }
                            else
                            {
                                FinalizeLine(state == LineState.PotentialAllowed
                                        ? allowlisted.AsSpan(0, allowlistedCount)
                                        : candidate.AsSpan(0, candidateCount),
                                    lineStart, events, errors, ref linesSkipped);
                            }

                            cursor = new SourceReadCursor(nextOffset);
                            lineStart = nextOffset;
                            candidate.AsSpan(0, candidateCount).Clear();
                            candidateCount = 0;
                            if (allowlisted is not null)
                            {
                                ArrayPool<byte>.Shared.Return(allowlisted, clearArray: true);
                                allowlisted = null;
                            }
                            allowlistedCount = 0;
                            state = LineState.Candidate;
                            continue;
                        }

                        if (state == LineState.Drain)
                        {
                            cursor = new SourceReadCursor(cursor.CompleteOffset, true, lineStart, nextOffset);
                            continue;
                        }

                        if (state == LineState.PotentialAllowed)
                        {
                            if (allowlistedCount >= AllowlistedRecordLimit)
                            {
                                errors.Add("record_over_limit");
                                var retained = allowlisted!;
                                retained.AsSpan(0, allowlistedCount).Clear();
                                ArrayPool<byte>.Shared.Return(retained, clearArray: true);
                                allowlisted = null;
                                allowlistedCount = 0;
                                state = LineState.Drain;
                                cursor = new SourceReadCursor(cursor.CompleteOffset, true, lineStart, nextOffset);
                            }
                            else
                            {
                                allowlisted![allowlistedCount++] = value;
                                peakRetained = Math.Max(peakRetained, allowlistedCount);
                            }
                            continue;
                        }

                        if (candidateCount >= CandidatePrefixLimit)
                        {
                            errors.Add("record_over_limit");
                            candidate.AsSpan(0, candidateCount).Clear();
                            candidateCount = 0;
                            state = LineState.Drain;
                            cursor = new SourceReadCursor(cursor.CompleteOffset, true, lineStart, nextOffset);
                            continue;
                        }

                        candidate[candidateCount++] = value;
                        peakRetained = Math.Max(peakRetained, candidateCount);
                        if (!ShouldProbe(value, candidateCount)) continue;
                        var probe = Probe(candidate.AsSpan(0, candidateCount));
                        if (probe == ProbeDecision.PotentialAllowed)
                        {
                            allowlisted = ArrayPool<byte>.Shared.Rent(AllowlistedRecordLimit);
                            candidate.AsSpan(0, candidateCount).CopyTo(allowlisted);
                            allowlistedCount = candidateCount;
                            candidate.AsSpan(0, candidateCount).Clear();
                            candidateCount = 0;
                            state = LineState.PotentialAllowed;
                        }
                        else if (probe is ProbeDecision.Unknown or ProbeDecision.Malformed)
                        {
                            if (probe == ProbeDecision.Malformed) errors.Add("record_structural_error");
                            candidate.AsSpan(0, candidateCount).Clear();
                            candidateCount = 0;
                            state = LineState.Drain;
                            cursor = new SourceReadCursor(cursor.CompleteOffset, true, lineStart, nextOffset);
                        }
                    }
                }

                if (state == LineState.Drain)
                {
                    cursor = new SourceReadCursor(cursor.CompleteOffset, true, lineStart, processedPosition).Normalize();
                    hasMoreData = processedPosition < stream.Length;
                }
                else
                {
                    hasMoreData = stream.Length > cursor.CompleteOffset;
                }
                if (cursor.CompleteOffset == originalCompleteOffset && stream.Length == originalCompleteOffset)
                    hasMoreData = false;
            }
            finally
            {
                if (allowlisted is not null) ArrayPool<byte>.Shared.Return(allowlisted, clearArray: true);
                ArrayPool<byte>.Shared.Return(candidate, clearArray: true);
                ArrayPool<byte>.Shared.Return(block, clearArray: true);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            errors.Add("source_missing");
        }
        catch (UnauthorizedAccessException)
        {
            errors.Add("source_access_denied");
        }
        catch (IOException)
        {
            errors.Add("source_io");
        }

        return new ParseBatchResult(events, cursor.Normalize(), linesRead, linesSkipped,
            errors.Distinct(StringComparer.Ordinal).ToArray(), bytesConsumed, peakRetained,
            hasMoreData, stoppedByBudget);
    }

    private static void FinalizeLine(ReadOnlySpan<byte> line, long sourceOffset,
        ICollection<ParsedEvent> events, ICollection<string> errors, ref int linesSkipped)
    {
        if (line.Length > 0 && line[^1] == (byte)'\r') line = line[..^1];
        if (line.IsEmpty) return;
        var envelope = EnvelopeValidator.Validate(line);
        if (envelope.Decision == EnvelopeDecision.Unknown)
        {
            linesSkipped++;
            return;
        }
        if (envelope.Decision != EnvelopeDecision.Allowed)
        {
            linesSkipped++;
            errors.Add(envelope.ErrorCode ?? "record_structural_error");
            return;
        }

        var parsed = ApprovedFieldExtractor.Extract(line, envelope.Kind);
        if (parsed is null)
        {
            linesSkipped++;
            errors.Add("record_structural_error");
            return;
        }
        if (parsed.ErrorCode is not null)
        {
            linesSkipped++;
            errors.Add(parsed.ErrorCode);
            return;
        }
        parsed = parsed with { SourceOffset = sourceOffset };
        events.Add(parsed);
    }

    private static bool ShouldProbe(byte value, int count) => count <= 4096
        ? value is (byte)'"' or (byte)',' or (byte)'}'
        : count % 256 == 0 || count == CandidatePrefixLimit;

    private static ProbeDecision Probe(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var reader = new Utf8JsonReader(bytes, isFinalBlock: false, default);
            var rootKind = RootKind.Unset;
            var inPayload = false;
            var pendingRootType = false;
            var pendingPayload = false;
            var pendingPayloadType = false;
            var payloadTypeSeen = false;
            var payloadTypeAllowed = false;
            var objectDepth = 0;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    objectDepth++;
                    if (pendingPayload && objectDepth == 2) inPayload = true;
                    pendingPayload = false;
                    pendingRootType = false;
                    pendingPayloadType = false;
                    continue;
                }
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    if (inPayload && objectDepth == 2) inPayload = false;
                    objectDepth--;
                    continue;
                }
                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    pendingRootType = objectDepth == 1 && reader.ValueTextEquals("type"u8);
                    pendingPayload = objectDepth == 1 && reader.ValueTextEquals("payload"u8);
                    pendingPayloadType = inPayload && objectDepth == 2 && reader.ValueTextEquals("type"u8);
                    continue;
                }
                if (reader.TokenType == JsonTokenType.String && pendingRootType)
                {
                    rootKind = reader.ValueTextEquals("session_meta"u8) ? RootKind.SessionMeta
                        : reader.ValueTextEquals("turn_context"u8) ? RootKind.TurnContext
                        : reader.ValueTextEquals("event_msg"u8) ? RootKind.EventMessage
                        : RootKind.Unknown;
                    if (rootKind is RootKind.SessionMeta or RootKind.TurnContext)
                        return ProbeDecision.PotentialAllowed;
                    if (rootKind == RootKind.Unknown) return ProbeDecision.Unknown;
                    if (payloadTypeSeen)
                        return payloadTypeAllowed ? ProbeDecision.PotentialAllowed : ProbeDecision.Unknown;
                }
                else if (reader.TokenType == JsonTokenType.String && pendingPayloadType)
                {
                    payloadTypeSeen = true;
                    payloadTypeAllowed = IsAllowedPayloadType(ref reader);
                    if (rootKind == RootKind.EventMessage)
                        return payloadTypeAllowed ? ProbeDecision.PotentialAllowed : ProbeDecision.Unknown;
                }
                pendingRootType = false;
                pendingPayload = false;
                pendingPayloadType = false;
            }
            return ProbeDecision.NeedMore;
        }
        catch (JsonException)
        {
            return ProbeDecision.Malformed;
        }
    }

    private static bool IsAllowedPayloadType(ref Utf8JsonReader reader) =>
        reader.ValueTextEquals("token_count"u8) || reader.ValueTextEquals("task_started"u8) ||
        reader.ValueTextEquals("task_complete"u8) || reader.ValueTextEquals("thread_settings_applied"u8) ||
        reader.ValueTextEquals("context_compacted"u8);

    private static class EnvelopeValidator
    {
        private const int PropertyLimit = 512;

        public static EnvelopeValidation Validate(ReadOnlySpan<byte> bytes)
        {
            try
            {
                var reader = new Utf8JsonReader(bytes, true, new JsonReaderState(new JsonReaderOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32,
                }));
                var frames = new Stack<ValidationFrame>();
                var pending = PropertyKind.None;
                var rootStarted = false;
                var rootEnded = false;
                var rootTypeCount = 0;
                var rootPayloadCount = 0;
                var payloadTypeCount = 0;
                var rootKind = RootKind.Unset;
                var payloadKind = string.Empty;
                var propertyCount = 0;

                while (reader.Read())
                {
                    if (rootEnded) return Invalid("record_structural_error");
                    if (reader.TokenType == JsonTokenType.StartObject)
                    {
                        if (!rootStarted)
                        {
                            rootStarted = true;
                            frames.Push(new ValidationFrame(ValidationContext.Root));
                        }
                        else
                        {
                            var context = ContextForObject(frames.Count == 0
                                ? ValidationContext.Other : frames.Peek().Context, pending);
                            if (pending == PropertyKind.RootPayload && context != ValidationContext.Payload)
                                return Invalid("record_structural_error");
                            frames.Push(new ValidationFrame(context));
                        }
                        pending = PropertyKind.None;
                        continue;
                    }
                    if (reader.TokenType == JsonTokenType.StartArray)
                    {
                        if (pending == PropertyKind.RootPayload)
                            return Invalid("record_structural_error");
                        frames.Push(new ValidationFrame(ValidationContext.Other));
                        pending = PropertyKind.None;
                        continue;
                    }
                    if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray)
                    {
                        if (frames.Count == 0) return Invalid("record_structural_error");
                        frames.Pop();
                        if (frames.Count == 0) rootEnded = true;
                        pending = PropertyKind.None;
                        continue;
                    }
                    if (reader.TokenType == JsonTokenType.PropertyName)
                    {
                        if (frames.Count == 0) return Invalid("record_structural_error");
                        propertyCount++;
                        if (propertyCount > PropertyLimit) return Invalid("envelope_property_limit");
                        var frame = frames.Peek();
                        var digest = DigestProperty(ref reader);
                        if (frame.Context == ValidationContext.Root && !frame.AllNames.Add(digest))
                            return Invalid("duplicate_root_property");
                        if (frame.Context == ValidationContext.Payload && !frame.AllNames.Add(digest))
                            return Invalid("duplicate_payload_property");
                        pending = MapProperty(frame.Context, ref reader);
                        var approvedId = (int)pending;
                        if (approvedId != 0 && !frame.Approved.Add(approvedId))
                            return Invalid("duplicate_approved_property");
                        if (pending == PropertyKind.RootType) rootTypeCount++;
                        if (pending == PropertyKind.RootPayload) rootPayloadCount++;
                        if (pending == PropertyKind.PayloadType) payloadTypeCount++;
                        continue;
                    }

                    if (pending == PropertyKind.RootType)
                    {
                        if (reader.TokenType != JsonTokenType.String) return Invalid("record_structural_error");
                        rootKind = reader.ValueTextEquals("session_meta"u8) ? RootKind.SessionMeta
                            : reader.ValueTextEquals("turn_context"u8) ? RootKind.TurnContext
                            : reader.ValueTextEquals("event_msg"u8) ? RootKind.EventMessage
                            : RootKind.Unknown;
                    }
                    else if (pending == PropertyKind.PayloadType)
                    {
                        if (reader.TokenType != JsonTokenType.String) return Invalid("record_structural_error");
                        payloadKind = reader.ValueTextEquals("token_count"u8) ? "event_msg:token_count"
                            : reader.ValueTextEquals("task_started"u8) ? "event_msg:task_started"
                            : reader.ValueTextEquals("task_complete"u8) ? "event_msg:task_complete"
                            : reader.ValueTextEquals("thread_settings_applied"u8)
                                ? "event_msg:thread_settings_applied"
                                : reader.ValueTextEquals("context_compacted"u8)
                                    ? "event_msg:context_compacted" : string.Empty;
                    }
                    else if (pending == PropertyKind.RootPayload)
                    {
                        return Invalid("record_structural_error");
                    }
                    pending = PropertyKind.None;
                }

                if (!rootStarted || !rootEnded || frames.Count != 0) return Invalid("record_structural_error");
                if (rootKind == RootKind.Unknown) return new EnvelopeValidation(EnvelopeDecision.Unknown, string.Empty, null);
                if (rootTypeCount != 1 || rootPayloadCount != 1) return Invalid("record_structural_error");
                if (rootKind == RootKind.EventMessage)
                {
                    if (payloadTypeCount != 1) return Invalid("record_structural_error");
                    return string.IsNullOrEmpty(payloadKind)
                        ? new EnvelopeValidation(EnvelopeDecision.Unknown, string.Empty, null)
                        : new EnvelopeValidation(EnvelopeDecision.Allowed, payloadKind, null);
                }
                return rootKind switch
                {
                    RootKind.SessionMeta => new EnvelopeValidation(EnvelopeDecision.Allowed, "session_meta", null),
                    RootKind.TurnContext => new EnvelopeValidation(EnvelopeDecision.Allowed, "turn_context", null),
                    _ => Invalid("record_structural_error"),
                };
            }
            catch (JsonException)
            {
                return Invalid("record_structural_error");
            }
        }

        private static EnvelopeValidation Invalid(string code) =>
            new(EnvelopeDecision.Invalid, string.Empty, code);

        private static ulong DigestProperty(ref Utf8JsonReader reader)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            var hash = offset;
            if (reader.HasValueSequence)
            {
                foreach (var segment in reader.ValueSequence)
                    foreach (var value in segment.Span) hash = (hash ^ value) * prime;
            }
            else
            {
                foreach (var value in reader.ValueSpan) hash = (hash ^ value) * prime;
            }
            return hash;
        }

        private static ValidationContext ContextForObject(ValidationContext parent, PropertyKind property) =>
            (parent, property) switch
            {
                (ValidationContext.Root, PropertyKind.RootPayload) => ValidationContext.Payload,
                (ValidationContext.Payload, PropertyKind.PayloadInfo) => ValidationContext.Info,
                (ValidationContext.Payload, PropertyKind.PayloadThreadSettings) => ValidationContext.ThreadSettings,
                (ValidationContext.Info, PropertyKind.InfoTotal) => ValidationContext.TotalUsage,
                (ValidationContext.Info, PropertyKind.InfoLast) => ValidationContext.LastUsage,
                _ => ValidationContext.Other,
            };

        internal static PropertyKind MapProperty(ValidationContext context, ref Utf8JsonReader reader)
        {
            return context switch
            {
                ValidationContext.Root when reader.ValueTextEquals("type"u8) => PropertyKind.RootType,
                ValidationContext.Root when reader.ValueTextEquals("payload"u8) => PropertyKind.RootPayload,
                ValidationContext.Root when reader.ValueTextEquals("timestamp"u8) => PropertyKind.Timestamp,
                ValidationContext.Root when reader.ValueTextEquals("event_time"u8) => PropertyKind.Timestamp,
                ValidationContext.Root when reader.ValueTextEquals("id"u8) => PropertyKind.Id,
                ValidationContext.Payload when reader.ValueTextEquals("type"u8) => PropertyKind.PayloadType,
                ValidationContext.Payload when reader.ValueTextEquals("info"u8) => PropertyKind.PayloadInfo,
                ValidationContext.Payload when reader.ValueTextEquals("thread_settings"u8) => PropertyKind.PayloadThreadSettings,
                ValidationContext.Payload when reader.ValueTextEquals("thread_id"u8) => PropertyKind.ThreadId,
                ValidationContext.Payload when reader.ValueTextEquals("id"u8) => PropertyKind.ThreadId,
                ValidationContext.Payload when reader.ValueTextEquals("model"u8) => PropertyKind.Model,
                ValidationContext.Payload when reader.ValueTextEquals("reasoning_effort"u8) => PropertyKind.ReasoningEffort,
                ValidationContext.Payload when reader.ValueTextEquals("service_tier"u8) || reader.ValueTextEquals("serviceTier"u8) => PropertyKind.ServiceTier,
                ValidationContext.Payload when reader.ValueTextEquals("turn_id"u8) || reader.ValueTextEquals("turn_key"u8) => PropertyKind.TurnKey,
                ValidationContext.Payload when reader.ValueTextEquals("timestamp"u8) => PropertyKind.Timestamp,
                ValidationContext.Payload when reader.ValueTextEquals("event_time"u8) => PropertyKind.Timestamp,
                ValidationContext.Payload when IsContextWindow(ref reader) => PropertyKind.ContextWindow,
                ValidationContext.Info when reader.ValueTextEquals("total_token_usage"u8) => PropertyKind.InfoTotal,
                ValidationContext.Info when reader.ValueTextEquals("last_token_usage"u8) => PropertyKind.InfoLast,
                ValidationContext.Info when IsContextWindow(ref reader) => PropertyKind.ContextWindow,
                ValidationContext.ThreadSettings when reader.ValueTextEquals("service_tier"u8) || reader.ValueTextEquals("serviceTier"u8) => PropertyKind.ServiceTier,
                ValidationContext.TotalUsage or ValidationContext.LastUsage when IsTokenComponent(ref reader) => ComponentKind(ref reader),
                _ => PropertyKind.None,
            };
        }

        private static bool IsContextWindow(ref Utf8JsonReader reader) =>
            reader.ValueTextEquals("context_window"u8) || reader.ValueTextEquals("context_window_size"u8) ||
            reader.ValueTextEquals("model_context_window"u8) || reader.ValueTextEquals("model_context_window_size"u8);

        private static bool IsTokenComponent(ref Utf8JsonReader reader) =>
            reader.ValueTextEquals("input_tokens"u8) || reader.ValueTextEquals("cached_input_tokens"u8) ||
            reader.ValueTextEquals("cache_read_input_tokens"u8) || reader.ValueTextEquals("cache_write_input_tokens"u8) ||
            reader.ValueTextEquals("output_tokens"u8) || reader.ValueTextEquals("reasoning_output_tokens"u8) ||
            reader.ValueTextEquals("total_tokens"u8);

        private static PropertyKind ComponentKind(ref Utf8JsonReader reader) =>
            reader.ValueTextEquals("input_tokens"u8) ? PropertyKind.InputTokens
                : reader.ValueTextEquals("cached_input_tokens"u8) ? PropertyKind.CachedInputTokens
                : reader.ValueTextEquals("cache_read_input_tokens"u8) ? PropertyKind.CacheReadInputTokens
                : reader.ValueTextEquals("cache_write_input_tokens"u8) ? PropertyKind.CacheWriteInputTokens
                : reader.ValueTextEquals("output_tokens"u8) ? PropertyKind.OutputTokens
                : reader.ValueTextEquals("reasoning_output_tokens"u8) ? PropertyKind.ReasoningTokens
                : PropertyKind.TotalTokens;

        private sealed class ValidationFrame
        {
            public ValidationFrame(ValidationContext context) { Context = context; }
            public ValidationContext Context { get; }
            public HashSet<ulong> AllNames { get; } = new();
            public HashSet<int> Approved { get; } = new();
        }
    }

    private static class ApprovedFieldExtractor
    {
        public static ParsedEvent? Extract(ReadOnlySpan<byte> bytes, string kind)
        {
            try
            {
                var reader = new Utf8JsonReader(bytes, true, new JsonReaderState(new JsonReaderOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32,
                }));
                var contexts = new Stack<ValidationContext>();
                var pending = PropertyKind.None;
                string? rootId = null;
                string? threadId = null;
                string? model = null;
                string? reasoning = null;
                string? directTier = null;
                string? nestedTier = null;
                string? turnKey = null;
                long? rootTimestamp = null;
                long? payloadTimestamp = null;
                string? rootTimestampText = null;
                string? payloadTimestampText = null;
                long? contextWindow = null;
                var total = new MutableComponents();
                var last = new MutableComponents();
                var totalSeen = false;
                var lastSeen = false;
                var totalHasValue = false;
                var lastHasValue = false;
                var tokenSchemaDegraded = false;

                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.StartObject)
                    {
                        if (pending == PropertyKind.ContextWindow ||
                            pending is >= PropertyKind.InputTokens and <= PropertyKind.TotalTokens)
                            tokenSchemaDegraded = true;
                        if (contexts.Count == 0) contexts.Push(ValidationContext.Root);
                        else
                        {
                            var parent = contexts.Peek();
                            var next = (parent, pending) switch
                            {
                                (ValidationContext.Root, PropertyKind.RootPayload) => ValidationContext.Payload,
                                (ValidationContext.Payload, PropertyKind.PayloadInfo) => ValidationContext.Info,
                                (ValidationContext.Payload, PropertyKind.PayloadThreadSettings) => ValidationContext.ThreadSettings,
                                (ValidationContext.Info, PropertyKind.InfoTotal) => ValidationContext.TotalUsage,
                                (ValidationContext.Info, PropertyKind.InfoLast) => ValidationContext.LastUsage,
                                _ => ValidationContext.Other,
                            };
                            if (next == ValidationContext.TotalUsage) totalSeen = true;
                            if (next == ValidationContext.LastUsage) lastSeen = true;
                            contexts.Push(next);
                        }
                        pending = PropertyKind.None;
                        continue;
                    }
                    if (reader.TokenType == JsonTokenType.StartArray)
                    {
                        if (pending == PropertyKind.ContextWindow ||
                            pending is >= PropertyKind.InputTokens and <= PropertyKind.TotalTokens)
                            tokenSchemaDegraded = true;
                        contexts.Push(ValidationContext.Other);
                        pending = PropertyKind.None;
                        continue;
                    }
                    if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray)
                    {
                        contexts.Pop();
                        pending = PropertyKind.None;
                        continue;
                    }
                    if (reader.TokenType == JsonTokenType.PropertyName)
                    {
                        pending = EnvelopeValidator.MapProperty(contexts.Peek(), ref reader);
                        continue;
                    }

                    var context = contexts.Count == 0 ? ValidationContext.Other : contexts.Peek();
                    switch (pending)
                    {
                        case PropertyKind.Id when reader.TokenType == JsonTokenType.String:
                            if (context == ValidationContext.Root) rootId = reader.GetString();
                            else if (context == ValidationContext.Payload) threadId = reader.GetString();
                            break;
                        case PropertyKind.ThreadId when reader.TokenType == JsonTokenType.String:
                            threadId = reader.GetString();
                            break;
                        case PropertyKind.Model when reader.TokenType == JsonTokenType.String:
                            model = reader.GetString();
                            break;
                        case PropertyKind.ReasoningEffort when reader.TokenType == JsonTokenType.String:
                            reasoning = reader.GetString();
                            break;
                        case PropertyKind.ServiceTier when reader.TokenType == JsonTokenType.String:
                            if (context == ValidationContext.ThreadSettings) nestedTier = reader.GetString();
                            else directTier = reader.GetString();
                            break;
                        case PropertyKind.TurnKey when reader.TokenType == JsonTokenType.String:
                            turnKey = reader.GetString();
                            break;
                        case PropertyKind.Timestamp:
                        case PropertyKind.EventTime:
                            ReadTimestampValue(ref reader, context == ValidationContext.Root,
                                ref rootTimestamp, ref payloadTimestamp, ref rootTimestampText,
                                ref payloadTimestampText);
                            break;
                        case PropertyKind.ContextWindow:
                            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var window))
                                contextWindow = window;
                            else tokenSchemaDegraded = true;
                            break;
                        case >= PropertyKind.InputTokens and <= PropertyKind.TotalTokens:
                            if (context is not (ValidationContext.TotalUsage or ValidationContext.LastUsage))
                                break;
                            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var number))
                            {
                                if (context == ValidationContext.TotalUsage)
                                {
                                    total.Set(pending, number);
                                    totalHasValue = true;
                                }
                                else
                                {
                                    last.Set(pending, number);
                                    lastHasValue = true;
                                }
                            }
                            else tokenSchemaDegraded = true;
                            break;
                    }
                    pending = PropertyKind.None;
                }

                var time = ParseTimestamp(payloadTimestamp ?? rootTimestamp,
                    payloadTimestampText ?? rootTimestampText);
                var tier = string.IsNullOrWhiteSpace(directTier) ? nestedTier : directTier;
                return kind switch
                {
                    "session_meta" => string.IsNullOrWhiteSpace(threadId ?? rootId)
                        ? new ParsedEvent(kind, null, null, null, null, null, null, null, time,
                            "session_id_missing")
                        : new ParsedEvent(kind, threadId ?? rootId, null, null, null, null, null, null, time),
                    "turn_context" => new ParsedEvent(kind, threadId, model, reasoning, tier, turnKey,
                        null, null, time),
                    "event_msg:token_count" when tokenSchemaDegraded || !totalSeen || !lastSeen ||
                        !totalHasValue || !lastHasValue =>
                        new ParsedEvent(kind, threadId, model, null, tier, null, null, null, time,
                            "token_schema_degraded"),
                    "event_msg:token_count" => new ParsedEvent(kind, threadId, model, null, tier,
                        null, null, new TokenUsageSnapshot(total.ToImmutable(), last.ToImmutable(), contextWindow),
                        time),
                    "event_msg:task_started" => new ParsedEvent(kind, threadId, model, null, tier,
                        turnKey, true, null, time),
                    "event_msg:task_complete" => new ParsedEvent(kind, threadId, model, null, tier,
                        turnKey, false, null, time),
                    "event_msg:thread_settings_applied" => new ParsedEvent(kind, threadId, model,
                        reasoning, tier, null, null, null, time),
                    "event_msg:context_compacted" => new ParsedEvent(kind, threadId, null,
                        null, null, null, null, null, time),
                    _ => null,
                };
            }
            catch (JsonException)
            {
                return null;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        private static void ReadTimestampValue(ref Utf8JsonReader reader, bool root,
            ref long? rootNumeric, ref long? payloadNumeric, ref string? rootText,
            ref string? payloadText)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var number))
            {
                if (root) rootNumeric = number; else payloadNumeric = number;
            }
            else if (reader.TokenType == JsonTokenType.String)
            {
                if (root) rootText = reader.GetString(); else payloadText = reader.GetString();
            }
        }

        private static DateTimeOffset? ParseTimestamp(long? numeric, string? text)
        {
            if (numeric is > 0)
            {
                try
                {
                    return numeric > 10_000_000_000
                        ? DateTimeOffset.FromUnixTimeMilliseconds(numeric.Value)
                        : DateTimeOffset.FromUnixTimeSeconds(numeric.Value);
                }
                catch (ArgumentOutOfRangeException)
                {
                    return null;
                }
            }
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed.ToUniversalTime() : null;
        }

        private sealed class MutableComponents
        {
            private long? _input;
            private long? _cached;
            private long? _cacheRead;
            private long? _cacheWrite;
            private long? _output;
            private long? _reasoning;
            private long? _total;

            public void Set(PropertyKind kind, long value)
            {
                switch (kind)
                {
                    case PropertyKind.InputTokens: _input = value; break;
                    case PropertyKind.CachedInputTokens: _cached = value; break;
                    case PropertyKind.CacheReadInputTokens: _cacheRead = value; break;
                    case PropertyKind.CacheWriteInputTokens: _cacheWrite = value; break;
                    case PropertyKind.OutputTokens: _output = value; break;
                    case PropertyKind.ReasoningTokens: _reasoning = value; break;
                    case PropertyKind.TotalTokens: _total = value; break;
                }
            }

            public TokenComponents ToImmutable() => new(_input, _cached, _cacheRead, _cacheWrite,
                _output, _reasoning, _total);
        }
    }

    private enum LineState { Candidate, PotentialAllowed, Drain }
    private enum ProbeDecision { NeedMore, PotentialAllowed, Unknown, Malformed }
    private enum EnvelopeDecision { Allowed, Unknown, Invalid }
    private enum RootKind { Unset, SessionMeta, TurnContext, EventMessage, Unknown }
    private enum ValidationContext { Root, Payload, Info, TotalUsage, LastUsage, ThreadSettings, Other }

    private enum PropertyKind
    {
        None = 0,
        RootType = 1,
        RootPayload = 2,
        PayloadType = 3,
        PayloadInfo = 4,
        PayloadThreadSettings = 5,
        ThreadId = 6,
        Id = 7,
        Model = 8,
        ReasoningEffort = 9,
        ServiceTier = 10,
        TurnKey = 11,
        Timestamp = 12,
        EventTime = 13,
        ContextWindow = 14,
        InfoTotal = 15,
        InfoLast = 16,
        InputTokens = 20,
        CachedInputTokens = 21,
        CacheReadInputTokens = 22,
        CacheWriteInputTokens = 23,
        OutputTokens = 24,
        ReasoningTokens = 25,
        TotalTokens = 26,
    }

    private readonly record struct EnvelopeValidation(EnvelopeDecision Decision, string Kind,
        string? ErrorCode);
}
