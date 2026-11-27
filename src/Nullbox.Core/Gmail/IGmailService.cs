namespace Nullbox.Core.Gmail;

/// <summary>
/// Gmail operations used by nullbox (port of emailBlaster's <c>main.py</c>). Every bulk method de-duplicates ids,
/// does nothing for an empty list and chunks requests at Gmail's 1000-id limit.
/// </summary>
public interface IGmailService
{
    /// <summary>
    /// Returns the ids of messages matching a Gmail search query, following every result page.
    /// <c>maxResults</c> stops after that many ids; <c>null</c> means all of them.
    /// </summary>
    Task<IReadOnlyList<string>> SearchAsync(string query, int? maxResults = null, CancellationToken cancellationToken = default);

    /// <summary>Fetches one message. <see cref="MessageFormat.Metadata"/> skips the body to save quota.</summary>
    Task<GmailMessage> GetMessageAsync(string messageId, MessageFormat format = MessageFormat.Metadata, CancellationToken cancellationToken = default);

    /// <summary>Returns the id of the user label with this name, creating it if it does not exist.</summary>
    Task<string> EnsureLabelAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Adds and removes label ids on many messages.</summary>
    Task BatchModifyAsync(
        IEnumerable<string> messageIds,
        IEnumerable<string>? addLabelIds,
        IEnumerable<string>? removeLabelIds,
        CancellationToken cancellationToken = default);

    /// <summary>Removes the UNREAD label.</summary>
    Task MarkReadAsync(IEnumerable<string> messageIds, CancellationToken cancellationToken = default);

    /// <summary>Removes INBOX and, when <paramref name="label"/> is given, applies that label (created if needed).</summary>
    Task ArchiveAsync(IEnumerable<string> messageIds, string? label, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves messages to Trash (recoverable for 30 days). Throws <see cref="DeleteLockedException"/> while the
    /// app-wide delete lock is on (<see cref="GmailOptions.AllowDelete"/> off, the default).
    /// </summary>
    Task TrashAsync(IEnumerable<string> messageIds, CancellationToken cancellationToken = default);

    /// <summary>Takes messages back out of Trash (used to undo a delete). Not affected by the delete lock.</summary>
    Task UntrashAsync(IEnumerable<string> messageIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Permanently deletes messages. Throws <see cref="DeleteLockedException"/> while the delete lock is on, and
    /// <see cref="PermanentDeleteDisabledException"/> unless <see cref="GmailOptions.AllowPermanentDelete"/> is also on.
    /// </summary>
    Task BatchDeleteAsync(IEnumerable<string> messageIds, CancellationToken cancellationToken = default);

    /// <summary>The mailbox's current history id (<c>users.getProfile</c>), the starting point for incremental sync.</summary>
    Task<ulong> GetCurrentHistoryIdAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Ids of messages added to the inbox since <paramref name="startHistoryId"/> (<c>users.history.list</c>, all pages),
    /// plus the history id to resume from next time. Throws <see cref="GmailHistoryExpiredException"/> when Gmail no
    /// longer has history that far back (it answers 404 after about a week).
    /// </summary>
    Task<GmailHistoryChanges> GetInboxChangesAsync(ulong startHistoryId, CancellationToken cancellationToken = default);
}

/// <summary>New inbox message ids (oldest first, de-duplicated) and the history id to resume from.</summary>
public sealed record GmailHistoryChanges(IReadOnlyList<string> AddedMessageIds, ulong HistoryId);

/// <summary>The start history id is too old for Gmail; resync with a query instead.</summary>
public sealed class GmailHistoryExpiredException(ulong startHistoryId, Exception? inner = null)
    : InvalidOperationException($"Gmail history from id {startHistoryId} is no longer available.", inner)
{
    public ulong StartHistoryId { get; } = startHistoryId;
}

public enum MessageFormat
{
    /// <summary>Headers, labels and snippet only.</summary>
    Metadata,

    /// <summary>Also the plain-text body, truncated to <see cref="GmailOptions.BodyMaxChars"/>.</summary>
    Full,
}

/// <summary>The parts of a Gmail message nullbox works with.</summary>
public sealed record GmailMessage(
    string Id,
    string? ThreadId,
    string? From,
    string? Subject,
    string? Date,
    string? ListUnsubscribe,
    IReadOnlyList<string> LabelIds,
    string? Snippet,
    DateTimeOffset? InternalDate,
    string? Body)
{
    public bool HasUnsubscribe => !string.IsNullOrWhiteSpace(ListUnsubscribe);
}

/// <summary>Well-known Gmail system label ids.</summary>
public static class GmailLabels
{
    public const string Inbox = "INBOX";
    public const string Unread = "UNREAD";
    public const string Starred = "STARRED";
    public const string Important = "IMPORTANT";
    public const string Trash = "TRASH";
}

public sealed class GmailOptions
{
    public const string SectionName = "Nullbox:Gmail";

    /// <summary>
    /// The app-wide delete lock: off by default, so nothing is moved to Trash or deleted, whichever job or rule asks.
    /// Callers that can fall back (rules archive instead) check this first; <see cref="IGmailService"/> refuses anyway.
    /// </summary>
    public bool AllowDelete { get; set; }

    /// <summary>Permanent delete is off by default and also needs <see cref="AllowDelete"/>; trash is the recoverable alternative.</summary>
    public bool AllowPermanentDelete { get; set; }

    /// <summary>
    /// Lets the filter sync create labels and Gmail filters (they change how new mail is filed). Off by default; dry
    /// runs work without it. Also needs the filters token, <c>token_filters.json</c>.
    /// </summary>
    public bool AllowFilterSync { get; set; }

    /// <summary>Body text is cut to this many characters (bodies are never logged).</summary>
    public int BodyMaxChars { get; set; } = 4000;

    /// <summary>Retries after the first attempt on 429, 5xx and Gmail rate-limit 403s.</summary>
    public int MaxRetries { get; set; } = 5;

    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromSeconds(32);
}

/// <summary>The delete lock is on (<see cref="GmailOptions.AllowDelete"/> is off); nothing was sent to Gmail.</summary>
public sealed class DeleteLockedException()
    : InvalidOperationException("Deleting mail is locked. Set Nullbox:Gmail:AllowDelete=true to allow moving mail to Trash.");

public sealed class PermanentDeleteDisabledException()
    : InvalidOperationException("Permanent delete is disabled. Set Nullbox:Gmail:AllowPermanentDelete=true to allow it, or use trash.");
