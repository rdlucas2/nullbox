using Microsoft.Extensions.Logging;
using Nullbox.Core.Filters;

namespace Nullbox.Gmail.Filters;

/// <summary>
/// create_filters.py: one Gmail filter per <see cref="FilterDefinition"/> that labels matching mail and skips the inbox.
/// Missing labels are created; a filter with the same query and label changes is skipped, so it is safe to run again.
/// A dry run creates nothing.
/// </summary>
public sealed class GmailFilterSync(IGmailFilterApi api, ILogger<GmailFilterSync> logger)
{
    public async Task<FilterSyncResult> SyncAsync(IReadOnlyList<FilterDefinition> definitions, bool dryRun, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var labels = await api.ListLabelsAsync(cancellationToken);
        var existing = (await api.ListFiltersAsync(cancellationToken)).Select(Key).ToHashSet();
        var labelsCreated = new List<string>();
        var items = new List<FilterSyncItem>(definitions.Count);

        foreach (var definition in definitions)
        {
            if (!labels.TryGetValue(definition.Label, out var labelId))
            {
                labelId = dryRun ? $"<new:{definition.Label}>" : await api.CreateLabelAsync(definition.Label, cancellationToken);
                labels[definition.Label] = labelId;
                labelsCreated.Add(definition.Label);
            }

            var filter = new GmailFilterSpec(definition.Query, [labelId], definition.RemoveLabelIds);
            FilterSyncStatus status;
            if (existing.Contains(Key(filter)))
            {
                status = FilterSyncStatus.Exists;
            }
            else if (dryRun)
            {
                status = FilterSyncStatus.WouldCreate;
            }
            else
            {
                await api.CreateFilterAsync(filter, cancellationToken);
                existing.Add(Key(filter));
                status = FilterSyncStatus.Created;
            }

            items.Add(new FilterSyncItem(definition.Label, definition.Query, definition.MarkRead, status));
        }

        var result = new FilterSyncResult(dryRun, labelsCreated, items);
        logger.LogInformation(
            "{Verb} {Created} filters, {Existing} already existed ({Labels} new labels)",
            dryRun ? "Would create" : "Created",
            result.Created,
            result.Existing,
            labelsCreated.Count);
        return result;
    }

    /// <summary>create_filters.py's <c>filter_key</c>: the query plus the label sets, ignoring order.</summary>
    private static string Key(GmailFilterSpec filter)
        => string.Join(
            '\u001f',
            filter.Query,
            string.Join(',', filter.AddLabelIds.Distinct().Order(StringComparer.Ordinal)),
            string.Join(',', filter.RemoveLabelIds.Distinct().Order(StringComparer.Ordinal)));
}
