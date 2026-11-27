namespace Nullbox.Core.Inbox;

/// <summary>Where incremental inbox sync stands. Persisted so a restart resumes from the same history id.</summary>
public sealed record InboxSyncState(
    ulong? HistoryId,
    bool Paused,
    DateTimeOffset? LastPollUtc,
    string? LastPollResult);

/// <summary>Sync position, pause flag and the set of message ids already handed to processing.</summary>
public interface IInboxStateStore
{
    Task<InboxSyncState> GetAsync(CancellationToken cancellationToken = default);

    Task SetHistoryIdAsync(ulong historyId, CancellationToken cancellationToken = default);

    Task SetPausedAsync(bool paused, CancellationToken cancellationToken = default);

    Task RecordPollAsync(DateTimeOffset at, string result, CancellationToken cancellationToken = default);

    /// <summary>The ids from <paramref name="messageIds"/> that were never enqueued, in the given order.</summary>
    Task<IReadOnlyList<string>> FilterUnseenAsync(IReadOnlyList<string> messageIds, CancellationToken cancellationToken = default);

    Task MarkEnqueuedAsync(string messageId, DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>Records that processing finished; returns false if it had already been recorded.</summary>
    Task<bool> MarkProcessedAsync(string messageId, DateTimeOffset at, CancellationToken cancellationToken = default);

    Task<bool> IsProcessedAsync(string messageId, CancellationToken cancellationToken = default);
}

/// <summary>Hands one message to background processing.</summary>
public interface IMessageJobQueue
{
    void EnqueueProcessing(string messageId);
}

/// <summary>
/// What happens to one new inbox message. A stub for now; the rule pipeline (M2-12) replaces it.
/// Must be safe to call twice for the same id.
/// </summary>
public interface IMessageProcessor
{
    Task ProcessAsync(string messageId, CancellationToken cancellationToken);
}
