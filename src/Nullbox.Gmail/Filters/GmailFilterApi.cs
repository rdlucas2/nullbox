using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;

namespace Nullbox.Gmail.Filters;

/// <summary>A Gmail filter as the sync compares them: its query and the labels it adds and removes.</summary>
public sealed record GmailFilterSpec(string Query, IReadOnlyList<string> AddLabelIds, IReadOnlyList<string> RemoveLabelIds);

/// <summary>
/// The label and filter calls create_filters.py makes. Implemented over the <b>filters</b> token
/// (<c>gmail.settings.basic</c> + <c>gmail.labels</c>), which the mail token does not include.
/// </summary>
public interface IGmailFilterApi
{
    /// <summary>Label name to id, for every label in the mailbox.</summary>
    Task<Dictionary<string, string>> ListLabelsAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates a label and returns its id.</summary>
    Task<string> CreateLabelAsync(string name, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GmailFilterSpec>> ListFiltersAsync(CancellationToken cancellationToken = default);

    Task CreateFilterAsync(GmailFilterSpec filter, CancellationToken cancellationToken = default);
}

public sealed class GmailFilterApi(GmailService gmail, GmailRetry retry) : IGmailFilterApi
{
    private const string Me = "me";

    public async Task<Dictionary<string, string>> ListLabelsAsync(CancellationToken cancellationToken = default)
    {
        var response = await retry.ExecuteAsync(ct => gmail.Users.Labels.List(Me).ExecuteAsync(ct), cancellationToken);
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var label in response.Labels ?? [])
        {
            labels.TryAdd(label.Name, label.Id);
        }

        return labels;
    }

    public async Task<string> CreateLabelAsync(string name, CancellationToken cancellationToken = default)
    {
        var created = await retry.ExecuteAsync(ct => gmail.Users.Labels.Create(new Label { Name = name }, Me).ExecuteAsync(ct), cancellationToken);
        return created.Id;
    }

    public async Task<IReadOnlyList<GmailFilterSpec>> ListFiltersAsync(CancellationToken cancellationToken = default)
    {
        var response = await retry.ExecuteAsync(ct => gmail.Users.Settings.Filters.List(Me).ExecuteAsync(ct), cancellationToken);
        return [.. (response.Filter ?? []).Select(f => new GmailFilterSpec(
            f.Criteria?.Query ?? string.Empty,
            [.. f.Action?.AddLabelIds ?? []],
            [.. f.Action?.RemoveLabelIds ?? []]))];
    }

    public Task CreateFilterAsync(GmailFilterSpec filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var body = new Filter
        {
            Criteria = new FilterCriteria { Query = filter.Query },
            Action = new FilterAction { AddLabelIds = [.. filter.AddLabelIds], RemoveLabelIds = [.. filter.RemoveLabelIds] },
        };
        return retry.ExecuteAsync(ct => gmail.Users.Settings.Filters.Create(body, Me).ExecuteAsync(ct), cancellationToken);
    }
}
