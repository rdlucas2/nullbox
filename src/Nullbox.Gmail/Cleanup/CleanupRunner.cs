using Microsoft.Extensions.Logging;
using Nullbox.Core.Cleanup;
using Nullbox.Core.Gmail;

namespace Nullbox.Gmail.Cleanup;

/// <summary>
/// emailBlaster's <c>run_cleanup</c>: search every rule first (a message matched by an earlier rule is not counted
/// again), then, unless it is a dry run, mark read and archive each rule's matches with the config's label. Only
/// <see cref="IGmailReader.SearchAsync"/>, message metadata reads, <see cref="IGmailService.EnsureLabelAsync"/> and
/// <see cref="IGmailService.BatchModifyAsync"/> (which chunks at 1000 ids) are used: cleanup never trashes or deletes.
/// </summary>
public sealed class CleanupRunner(IGmailService gmail, TimeProvider time, ILogger<CleanupRunner> logger)
{
    /// <summary>How many senders a sample lists, as cleanup.py's <c>most_common(10)</c>.</summary>
    public const int TopSenderCount = 10;

    /// <summary>
    /// Runs every rule in <paramref name="config"/>. A dry run only searches (and samples senders); otherwise matches
    /// are changed. <paramref name="sampleSize"/> reads the From header of that many matches per rule to list the top
    /// senders (0: none).
    /// </summary>
    public async Task<CleanupResult> RunAsync(CleanupConfig config, bool dryRun, int sampleSize = 0, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        var label = config.Label ?? CleanupConfig.DefaultLabel(time.GetUtcNow());

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var plan = new List<(CleanupRule Rule, string Query, List<string> Ids, IReadOnlyList<SenderCount> Senders)>();
        foreach (var rule in config.Rules)
        {
            var query = CleanupQuery.Build(rule, config.Keep);
            var ids = (await gmail.SearchAsync(query, cancellationToken: cancellationToken)).Where(seen.Add).ToList();
            var senders = sampleSize > 0 && ids.Count > 0 ? await SampleSendersAsync(ids, sampleSize, cancellationToken) : [];
            plan.Add((rule, query, ids, senders));
            logger.LogInformation("Cleanup rule {Rule} ({Actions}) matched {Count} messages", rule.Name, rule.Describe(), ids.Count);
        }

        var matched = plan.Sum(p => p.Ids.Count);
        if (dryRun || matched == 0)
        {
            return new CleanupResult(label, dryRun, [.. plan.Select(p => Result(p.Rule, p.Query, p.Ids.Count, 0, p.Senders))]);
        }

        string? labelId = config.Rules.Any(r => r.Archive) ? await gmail.EnsureLabelAsync(label, cancellationToken) : null;
        var results = new List<CleanupRuleResult>(plan.Count);
        foreach (var (rule, query, ids, senders) in plan)
        {
            var changed = ids.Count > 0 ? await ApplyAsync(rule, ids, labelId, cancellationToken) : 0;
            results.Add(Result(rule, query, ids.Count, changed, senders));
        }

        var result = new CleanupResult(label, dryRun, results);
        logger.LogInformation("Cleanup changed {Changed} of {Matched} messages (label {Label})", result.Changed, result.Matched, label);
        return result;
    }

    private async Task<int> ApplyAsync(CleanupRule rule, List<string> ids, string? labelId, CancellationToken cancellationToken)
    {
        var remove = new List<string>(2);
        if (rule.MarkRead)
        {
            remove.Add(GmailLabels.Unread);
        }

        if (rule.Archive)
        {
            remove.Add(GmailLabels.Inbox);
        }

        string[]? add = rule.Archive && labelId is not null ? [labelId] : null;
        if (remove.Count == 0 && add is null)
        {
            return 0; // cleanup.py sends an empty modify here; there is nothing to change
        }

        await gmail.BatchModifyAsync(ids, add, remove, cancellationToken);
        return ids.Count;
    }

    private async Task<IReadOnlyList<SenderCount>> SampleSendersAsync(List<string> ids, int sampleSize, CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var id in ids.Take(sampleSize))
        {
            GmailMessage message;
            try
            {
                message = await gmail.GetMessageAsync(id, MessageFormat.Metadata, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The sample is only a preview; a message deleted since the search should not stop the run.
                logger.LogDebug("Skipped message {MessageId} in the sender sample: {Error}", id, ex.GetType().Name);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(message.From))
            {
                counts[message.From] = counts.GetValueOrDefault(message.From) + 1;
            }
        }

        return [.. counts
            .OrderByDescending(c => c.Value)
            .ThenBy(c => c.Key, StringComparer.Ordinal)
            .Take(TopSenderCount)
            .Select(c => new SenderCount(c.Key, c.Value))];
    }

    private static CleanupRuleResult Result(CleanupRule rule, string query, int matched, int changed, IReadOnlyList<SenderCount> senders)
        => new(rule.Name, query, rule.Describe(), matched, changed, senders);
}
