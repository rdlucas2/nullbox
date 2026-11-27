using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nullbox.Core.Bulk;
using Nullbox.Core.Gmail;

namespace Nullbox.Gmail.Bulk;

/// <summary>
/// emailBlaster's bulk commands: archive by search (default: the whole inbox) with a timestamped label, mark all unread
/// mail read, and a capped permanent delete. Every one can be a dry run that only counts. The delete needs the delete
/// lock lifted <b>and</b> <see cref="GmailOptions.AllowPermanentDelete"/>; otherwise it is refused before any Gmail call.
/// </summary>
public sealed class BulkActions(IGmailService gmail, IOptions<GmailOptions> options, TimeProvider time, ILogger<BulkActions> logger)
{
    public const string InboxQuery = "in:inbox";
    public const string UnreadQuery = "is:unread";
    public const int DefaultDeleteCap = 1500;
    public const int MaxDeleteCap = 10_000;

    public const string DeleteLockedReason = "Permanent delete needs Nullbox:Gmail:AllowDelete and Nullbox:Gmail:AllowPermanentDelete; both default to off.";

    /// <summary><c>archive_yyyyMMdd_HHmmss</c> in UTC, as main.py's <c>--archive-all-mail</c> label.</summary>
    public static string DefaultArchiveLabel(DateTimeOffset now)
        => "archive_" + now.UtcDateTime.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);

    /// <summary>Removes INBOX from every match and adds <paramref name="label"/> (default <see cref="DefaultArchiveLabel"/>).</summary>
    public async Task<BulkResult> ArchiveAsync(string query, string? label, bool dryRun, CancellationToken cancellationToken = default)
    {
        query = string.IsNullOrWhiteSpace(query) ? InboxQuery : query.Trim();
        label = string.IsNullOrWhiteSpace(label) ? DefaultArchiveLabel(time.GetUtcNow()) : label.Trim();
        var ids = await gmail.SearchAsync(query, cancellationToken: cancellationToken);
        if (dryRun || ids.Count == 0)
        {
            return Done(new BulkResult(BulkAction.Archive, query, dryRun, ids.Count, 0, label));
        }

        var labelId = await gmail.EnsureLabelAsync(label, cancellationToken);
        await gmail.BatchModifyAsync(ids, [labelId], [GmailLabels.Inbox], cancellationToken);
        return Done(new BulkResult(BulkAction.Archive, query, dryRun, ids.Count, ids.Count, label));
    }

    /// <summary>Removes UNREAD from every unread message (all mail, not only the inbox, as main.py does).</summary>
    public async Task<BulkResult> MarkAllReadAsync(bool dryRun, CancellationToken cancellationToken = default)
    {
        var ids = await gmail.SearchAsync(UnreadQuery, cancellationToken: cancellationToken);
        if (!dryRun && ids.Count > 0)
        {
            await gmail.BatchModifyAsync(ids, null, [GmailLabels.Unread], cancellationToken);
        }

        return Done(new BulkResult(BulkAction.MarkRead, UnreadQuery, dryRun, ids.Count, dryRun ? 0 : ids.Count, null));
    }

    /// <summary>
    /// Permanently deletes at most <paramref name="cap"/> matches of <paramref name="query"/>. A dry run counts them
    /// (also while deletes are locked); applying is refused unless both delete settings are on.
    /// </summary>
    public async Task<BulkResult> DeleteAsync(string query, int cap, bool dryRun, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("A delete needs a search; an empty one would match every message.", nameof(query));
        }

        query = query.Trim();
        cap = Math.Clamp(cap, 1, MaxDeleteCap);
        var settings = options.Value;
        if (!dryRun && !(settings.AllowDelete && settings.AllowPermanentDelete))
        {
            logger.LogWarning("Bulk delete refused: deletes are locked");
            return BulkResult.Blocked(BulkAction.Delete, query, dryRun, DeleteLockedReason);
        }

        var ids = await gmail.SearchAsync(query, cap, cancellationToken);
        if (!dryRun && ids.Count > 0)
        {
            await gmail.BatchDeleteAsync(ids, cancellationToken);
        }

        return Done(new BulkResult(BulkAction.Delete, query, dryRun, ids.Count, dryRun ? 0 : ids.Count, null));
    }

    private BulkResult Done(BulkResult result)
    {
        logger.LogInformation(
            "Bulk {Action}{DryRun}: {Matched} matched, {Changed} changed",
            result.Action,
            result.DryRun ? " (dry run)" : string.Empty,
            result.Matched,
            result.Changed);
        return result;
    }
}
