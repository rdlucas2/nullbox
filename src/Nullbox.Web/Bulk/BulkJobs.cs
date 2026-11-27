using Hangfire;
using Microsoft.Extensions.Options;
using Nullbox.Core.Bulk;
using Nullbox.Core.Filters;
using Nullbox.Core.Gmail;
using Nullbox.Core.Options;
using Nullbox.Core.Pipeline;
using Nullbox.Gmail.Bulk;
using Nullbox.Gmail.Filters;

namespace Nullbox.Web.Bulk;

/// <summary>emailBlaster's <c>filters.json</c> at <c>{data}/filters.json</c>, read on every sync.</summary>
public sealed class FilterDefinitionsFile(string path)
{
    public const string FileName = "filters.json";

    public string Path { get; } = path;

    public bool Exists => File.Exists(Path);

    /// <exception cref="FileNotFoundException">There is no filters.json.</exception>
    /// <exception cref="FilterDefinitionsException">The file is not valid.</exception>
    public IReadOnlyList<FilterDefinition> Load()
        => Exists
            ? FilterDefinitions.Parse(File.ReadAllText(Path))
            : throw new FileNotFoundException($"There is no {FileName} in the data directory; copy emailBlaster's {FileName} there.");
}

/// <summary>
/// Bulk Gmail jobs and the filter sync (M1-07). Each returns its result, which Hangfire keeps on the job's page. Not
/// retried and never run twice at once. Applying is refused while the pipeline kill switch is on; dry runs always run.
/// </summary>
[AutomaticRetry(Attempts = 0, LogEvents = true, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
[DisableConcurrentExecution(timeoutInSeconds: 30 * 60)]
public sealed class BulkJobs(
    BulkActions bulk,
    GmailFilterSync filters,
    FilterDefinitionsFile definitions,
    IPipelineSettingsStore settings,
    IOptions<NullboxOptions> options,
    IOptions<GmailOptions> gmailOptions)
{
    public const string KillSwitchReason = "The kill switch is on, so only dry runs are allowed.";

    public const string FilterSyncDisabledReason = "Filter sync is off; set Nullbox:Gmail:AllowFilterSync=true to create labels and filters.";

    public async Task<BulkResult> ArchiveAsync(string query, string? label, bool dryRun, CancellationToken cancellationToken)
        => !dryRun && await KillSwitchOnAsync(cancellationToken)
            ? BulkResult.Blocked(BulkAction.Archive, query, dryRun, KillSwitchReason)
            : await bulk.ArchiveAsync(query, label, dryRun, cancellationToken);

    public async Task<BulkResult> MarkAllReadAsync(bool dryRun, CancellationToken cancellationToken)
        => !dryRun && await KillSwitchOnAsync(cancellationToken)
            ? BulkResult.Blocked(BulkAction.MarkRead, BulkActions.UnreadQuery, dryRun, KillSwitchReason)
            : await bulk.MarkAllReadAsync(dryRun, cancellationToken);

    public async Task<BulkResult> DeleteAsync(string query, int cap, bool dryRun, CancellationToken cancellationToken)
        => !dryRun && await KillSwitchOnAsync(cancellationToken)
            ? BulkResult.Blocked(BulkAction.Delete, query, dryRun, KillSwitchReason)
            : await bulk.DeleteAsync(query, cap, dryRun, cancellationToken);

    public async Task<FilterSyncResult> SyncFiltersAsync(bool dryRun, CancellationToken cancellationToken)
    {
        if (!dryRun && !gmailOptions.Value.AllowFilterSync)
        {
            return FilterSyncResult.Blocked(dryRun, FilterSyncDisabledReason);
        }

        if (!dryRun && await KillSwitchOnAsync(cancellationToken))
        {
            return FilterSyncResult.Blocked(dryRun, KillSwitchReason);
        }

        return await filters.SyncAsync(definitions.Load(), dryRun, cancellationToken);
    }

    private async Task<bool> KillSwitchOnAsync(CancellationToken cancellationToken)
        => options.Value.Pipeline.KillSwitch || await settings.GetKillSwitchAsync(cancellationToken);
}
