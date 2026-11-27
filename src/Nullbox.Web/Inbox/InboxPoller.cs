using Nullbox.Core.Gmail;
using Nullbox.Core.Inbox;

namespace Nullbox.Web.Inbox;

/// <summary>What one poll did.</summary>
public sealed record InboxPollOutcome(InboxPollStatus Status, int Found, int Enqueued, ulong? HistoryId)
{
    public static InboxPollOutcome Skipped(InboxPollStatus status) => new(status, 0, 0, null);

    public string Describe() => Status switch
    {
        InboxPollStatus.Paused => "paused",
        InboxPollStatus.NotConfigured => "Gmail not configured",
        InboxPollStatus.ReauthorizationRequired => "Gmail needs re-authorizing",
        InboxPollStatus.Resynced => $"resynced by query: {Found} found, {Enqueued} new",
        _ => $"{Found} found, {Enqueued} new",
    };
}

public enum InboxPollStatus
{
    /// <summary>Incremental sync from the stored history id.</summary>
    Synced,

    /// <summary>First run, or history expired: listed the inbox by <c>newer_than:</c> query instead.</summary>
    Resynced,

    Paused,

    /// <summary>No token yet (see /settings/gmail).</summary>
    NotConfigured,

    /// <summary>Google rejected the stored token; a new one has to be uploaded.</summary>
    ReauthorizationRequired,
}

/// <summary>
/// One inbox poll: reads Gmail history since the stored id (or falls back to an <c>in:inbox newer_than:</c> query on
/// first run and when history has expired), and hands each message not seen before to processing exactly once.
/// </summary>
public sealed class InboxPoller(
    IGmailService? gmail,
    IInboxStateStore store,
    IMessageJobQueue queue,
    TimeProvider time,
    string resyncWindow,
    ILogger<InboxPoller> logger)
{
    public async Task<InboxPollOutcome> PollAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await PollCoreAsync(cancellationToken);
        }
        catch (Exception ex) when (Find<GmailNotConfiguredException>(ex) is not null)
        {
            // Not a failure worth retrying every few minutes: wait for the token to be added.
            logger.LogWarning("Inbox poll skipped: {Reason}", Find<GmailNotConfiguredException>(ex)!.Message);
            return await RecordAsync(InboxPollOutcome.Skipped(InboxPollStatus.NotConfigured), CancellationToken.None);
        }
        catch (Exception ex) when (Find<GmailReauthorizationRequiredException>(ex) is not null)
        {
            logger.LogWarning("Inbox poll skipped: {Reason}", Find<GmailReauthorizationRequiredException>(ex)!.Message);
            return await RecordAsync(InboxPollOutcome.Skipped(InboxPollStatus.ReauthorizationRequired), CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Shown on the status endpoint; the job itself still fails so the dashboard shows it.
            await store.RecordPollAsync(time.GetUtcNow(), $"failed: {ex.GetType().Name}", CancellationToken.None);
            throw;
        }
    }

    private async Task<InboxPollOutcome> PollCoreAsync(CancellationToken cancellationToken)
    {
        var state = await store.GetAsync(cancellationToken);
        if (state.Paused)
        {
            logger.LogInformation("Inbox poll skipped: paused");
            return await RecordAsync(InboxPollOutcome.Skipped(InboxPollStatus.Paused), cancellationToken);
        }

        if (gmail is null)
        {
            logger.LogWarning("Inbox poll skipped: Gmail is not configured");
            return await RecordAsync(InboxPollOutcome.Skipped(InboxPollStatus.NotConfigured), cancellationToken);
        }

        IReadOnlyList<string> found;
        ulong historyId;
        var status = InboxPollStatus.Synced;
        if (state.HistoryId is not { } start)
        {
            (found, historyId) = await ResyncAsync(gmail, cancellationToken);
            status = InboxPollStatus.Resynced;
        }
        else
        {
            try
            {
                var changes = await gmail.GetInboxChangesAsync(start, cancellationToken);
                (found, historyId) = (changes.AddedMessageIds, changes.HistoryId);
            }
            catch (GmailHistoryExpiredException)
            {
                logger.LogWarning("Gmail history from {HistoryId} has expired; resyncing with newer_than:{Window}", start, resyncWindow);
                (found, historyId) = await ResyncAsync(gmail, cancellationToken);
                status = InboxPollStatus.Resynced;
            }
        }

        var fresh = await store.FilterUnseenAsync(found, cancellationToken);
        foreach (var id in fresh)
        {
            // Enqueue before marking: a crash in between re-enqueues next time, and processing skips done ids.
            queue.EnqueueProcessing(id);
            await store.MarkEnqueuedAsync(id, time.GetUtcNow(), cancellationToken);
        }

        await store.SetHistoryIdAsync(historyId, cancellationToken);
        var outcome = new InboxPollOutcome(status, found.Count, fresh.Count, historyId);
        logger.LogInformation("Inbox poll: {Result}, history id now {HistoryId}", outcome.Describe(), historyId);
        return await RecordAsync(outcome, cancellationToken);
    }

    private async Task<(IReadOnlyList<string> Ids, ulong HistoryId)> ResyncAsync(IGmailService service, CancellationToken cancellationToken)
    {
        // Take the history id first, so anything arriving during the search shows up in the next incremental poll.
        var historyId = await service.GetCurrentHistoryIdAsync(cancellationToken);
        var ids = await service.SearchAsync($"in:inbox newer_than:{resyncWindow}", cancellationToken: cancellationToken);
        return ([.. ids.Reverse()], historyId); // search is newest first; process oldest first
    }

    private static T? Find<T>(Exception? ex)
        where T : Exception
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is T match)
            {
                return match;
            }
        }

        return null;
    }

    private async Task<InboxPollOutcome> RecordAsync(InboxPollOutcome outcome, CancellationToken cancellationToken)
    {
        await store.RecordPollAsync(time.GetUtcNow(), outcome.Describe(), cancellationToken);
        return outcome;
    }
}
