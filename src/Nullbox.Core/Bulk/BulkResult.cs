namespace Nullbox.Core.Bulk;

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<BulkAction>))]
public enum BulkAction
{
    /// <summary>Remove INBOX and add a label (main.py <c>--archive-all-mail</c>, batch_archive.py <c>--search</c>).</summary>
    Archive,

    /// <summary>Remove UNREAD from every unread message (main.py <c>--mark-read</c>).</summary>
    MarkRead,

    /// <summary>Permanently delete up to a cap (one_thousand_batch.py, batch_archive.py <c>--delete-label</c>).</summary>
    Delete,
}

/// <summary>
/// The outcome of one bulk job. A dry run only counts (<see cref="Changed"/> is 0); <see cref="BlockedReason"/> is set
/// when the job was refused before touching Gmail.
/// </summary>
public sealed record BulkResult(BulkAction Action, string Query, bool DryRun, int Matched, int Changed, string? Label, string? BlockedReason = null)
{
    public static BulkResult Blocked(BulkAction action, string query, bool dryRun, string reason) => new(action, query, dryRun, 0, 0, null, reason);
}
