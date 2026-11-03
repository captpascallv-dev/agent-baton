namespace CodexUsageHud.Core;

public enum AccountAnalysisState
{
    Verified,
    OwnerConfirmedDirectory,
    IdentityMismatch,
    UnverifiedLocalHistory,
    IdentityUnavailable,
    SchemaUnavailable,
}

public sealed record IdentityColumnPresence(
    bool DatabasePresent,
    bool ThreadsTablePresent,
    bool HasAccountId,
    bool HasChatgptAccountId,
    bool HasUserId)
{
    public static IdentityColumnPresence Missing { get; } = new(false, false, false, false, false);

    public bool AnyIdentityColumn => HasAccountId || HasChatgptAccountId || HasUserId;

    public static IdentityColumnPresence FromColumns(bool databasePresent, IReadOnlySet<string> columns)
    {
        if (!databasePresent) return Missing;
        return new IdentityColumnPresence(true, columns.Contains("id"),
            columns.Contains("account_id"),
            columns.Contains("chatgpt_account_id"),
            columns.Contains("user_id"));
    }
}

public sealed record AttributionPartition(
    AccountAnalysisState State,
    IReadOnlyList<SessionAggregate> Analysis,
    IReadOnlyList<SessionAggregate> MachineBound,
    IReadOnlyList<SessionAggregate> Unverified,
    IReadOnlyList<SessionAggregate> Foreign,
    string Note)
{
    public IReadOnlyList<SessionAggregate> Verified => Analysis;
}

public static class PrimaryAccountAttribution
{
    public static AttributionPartition Partition(
        IReadOnlyList<SessionAggregate> sessions,
        IReadOnlyList<ThreadMetadataRow> metadataRows,
        string? boundIdentityHash,
        IdentityColumnPresence columns,
        string? boundNamespace = BoundAccountIdentity.Account,
        string? ownerConfirmedStampHash = null)
    {
        sessions ??= Array.Empty<SessionAggregate>();
        metadataRows ??= Array.Empty<ThreadMetadataRow>();
        var canClassify = columns.DatabasePresent && columns.ThreadsTablePresent && columns.AnyIdentityColumn &&
                          !string.IsNullOrWhiteSpace(boundIdentityHash);
        var stamp = string.IsNullOrWhiteSpace(ownerConfirmedStampHash) ? null : ownerConfirmedStampHash.Trim();
        var mismatch = stamp is not null && !string.IsNullOrWhiteSpace(boundIdentityHash) &&
                       !string.Equals(stamp, boundIdentityHash, StringComparison.Ordinal);

        if (mismatch)
            return PartitionMismatch(sessions, metadataRows, boundIdentityHash!, boundNamespace, canClassify);

        if (!canClassify)
            return OwnerConfirmed(sessions, "所有者已确认当前 Codex 主目录就是这个 App 账户。" +
                "这是目录级绑定，不是逐行 account_id 机器核验。" +
                ClassifyUnavailableReason(columns, boundIdentityHash) +
                " 不能把他户以外的记录从分析里拿掉；确认他户需要可用的身份列和当前身份。" +
                " 第二账户与其他服务不进入此列表。");

        return PartitionClassified(sessions, metadataRows, boundIdentityHash!, boundNamespace, mismatch: false);
    }

    public static AttributionClassifier.Kind ClassifyRow(ThreadMetadataRow row, string boundIdentityHash,
        string? boundNamespace)
    {
        var ns = string.IsNullOrWhiteSpace(boundNamespace) ? BoundAccountIdentity.Account : boundNamespace;
        if (string.Equals(ns, BoundAccountIdentity.Account, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(row.AccountId)) return AttributionClassifier.Kind.Unverified;
            var hashed = BoundIdentity.HashAccount(row.AccountId);
            return string.Equals(hashed, boundIdentityHash, StringComparison.Ordinal)
                ? AttributionClassifier.Kind.Bound
                : AttributionClassifier.Kind.Foreign;
        }

        if (string.Equals(ns, BoundAccountIdentity.User, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(row.UserId)) return AttributionClassifier.Kind.Unverified;
            var hashed = BoundIdentity.HashUser(row.UserId);
            return string.Equals(hashed, boundIdentityHash, StringComparison.Ordinal)
                ? AttributionClassifier.Kind.Bound
                : AttributionClassifier.Kind.Foreign;
        }

        return AttributionClassifier.Kind.Unverified;
    }

    private static AttributionPartition PartitionMismatch(
        IReadOnlyList<SessionAggregate> sessions,
        IReadOnlyList<ThreadMetadataRow> metadataRows,
        string boundIdentityHash,
        string? boundNamespace,
        bool canClassify)
    {
        if (!canClassify)
        {
            return new AttributionPartition(AccountAnalysisState.IdentityMismatch, Array.Empty<SessionAggregate>(),
                Array.Empty<SessionAggregate>(), sessions, Array.Empty<SessionAggregate>(),
                "当前 App 身份已变化。没有可用的行级身份列，因此不把原主目录历史改绑到新身份。" +
                "本机记录仍保留；分析列表为空。这不是删除，也不是机器核验通过。");
        }

        var classified = Classify(sessions, metadataRows, boundIdentityHash, boundNamespace);
        return new AttributionPartition(AccountAnalysisState.IdentityMismatch, classified.Bound, classified.Bound,
            classified.Unverified, classified.Foreign,
            "当前 App 身份已变化。只显示能机器匹配到新身份的会话，不把无身份列或旧身份记录改绑为新账户。" +
            $" 新身份已匹配 {classified.Bound.Count}，未改绑 {classified.Unverified.Count}，确认他户/旧身份 {classified.Foreign.Count}。" +
            " 第二账户与其他服务不进入此列表。");
    }

    private static AttributionPartition PartitionClassified(
        IReadOnlyList<SessionAggregate> sessions,
        IReadOnlyList<ThreadMetadataRow> metadataRows,
        string boundIdentityHash,
        string? boundNamespace,
        bool mismatch)
    {
        var classified = Classify(sessions, metadataRows, boundIdentityHash, boundNamespace);
        var analysis = classified.Bound.Concat(classified.Unverified).ToArray();
        var allBound = classified.Unverified.Count == 0;
        var state = allBound ? AccountAnalysisState.Verified : AccountAnalysisState.OwnerConfirmedDirectory;
        var note = allBound
            ? "分析列表中的会话均已用同一身份命名空间的 allowlisted account_id/user_id 与当前 App 身份机器匹配。" +
              $" 确认他户 {classified.Foreign.Count} 已排除。第二账户与其他服务不进入此列表。"
            : "所有者已确认当前 Codex 主目录就是这个 App 账户。分析默认包含该主目录会话，并排除确认他户。" +
              "这不是逐行 account_id 机器核验；未匹配行不得报成机器归属。" +
              $" 机器匹配 {classified.Bound.Count}，行级未匹配 {classified.Unverified.Count}，确认他户 {classified.Foreign.Count}。" +
              " 第二账户与其他服务不进入此列表。";
        _ = mismatch;
        return new AttributionPartition(state, analysis, classified.Bound, classified.Unverified, classified.Foreign,
            note);
    }

    private static AttributionPartition OwnerConfirmed(IReadOnlyList<SessionAggregate> sessions, string note) =>
        new(AccountAnalysisState.OwnerConfirmedDirectory, sessions, Array.Empty<SessionAggregate>(), sessions,
            Array.Empty<SessionAggregate>(), note);

    private static string ClassifyUnavailableReason(IdentityColumnPresence columns, string? boundIdentityHash)
    {
        if (!columns.DatabasePresent || !columns.ThreadsTablePresent)
            return " 本地 threads 元数据不可用。";
        if (!columns.AnyIdentityColumn)
            return " threads 表没有 account_id/chatgpt_account_id/user_id。";
        if (string.IsNullOrWhiteSpace(boundIdentityHash))
            return " 当前 App 身份哈希不可用。";
        return string.Empty;
    }

    private static ClassifiedSessions Classify(
        IReadOnlyList<SessionAggregate> sessions,
        IReadOnlyList<ThreadMetadataRow> metadataRows,
        string boundIdentityHash,
        string? boundNamespace)
    {
        var byThread = new Dictionary<string, ThreadMetadataRow>(StringComparer.Ordinal);
        foreach (var row in metadataRows)
        {
            if (string.IsNullOrWhiteSpace(row.ThreadId) || byThread.ContainsKey(row.ThreadId)) continue;
            byThread[row.ThreadId] = row;
        }

        var bound = new List<SessionAggregate>();
        var unverified = new List<SessionAggregate>();
        var foreign = new List<SessionAggregate>();
        foreach (var session in sessions)
        {
            if (!byThread.TryGetValue(session.Metadata.ThreadId, out var row))
            {
                unverified.Add(session);
                continue;
            }

            var classified = ClassifyRow(row, boundIdentityHash, boundNamespace);
            if (classified == AttributionClassifier.Kind.Bound) bound.Add(session);
            else if (classified == AttributionClassifier.Kind.Foreign) foreign.Add(session);
            else unverified.Add(session);
        }

        return new ClassifiedSessions(bound, unverified, foreign);
    }

    private sealed record ClassifiedSessions(
        IReadOnlyList<SessionAggregate> Bound,
        IReadOnlyList<SessionAggregate> Unverified,
        IReadOnlyList<SessionAggregate> Foreign);
}
