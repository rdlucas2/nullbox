using Hangfire;
using Nullbox.Core.Jobs;

namespace Nullbox.Web.Jobs;

/// <summary><see cref="IJobScheduler"/> backed by Hangfire's recurring job manager.</summary>
public sealed class HangfireJobScheduler(IRecurringJobManagerV2 manager) : IJobScheduler
{
    public void AddOrUpdateRecurring<TJob>(string jobId, string cron)
        where TJob : IRecurringJob
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(cron);

        // Hangfire swaps CancellationToken.None for its own shutdown-aware token at execution time.
        manager.AddOrUpdate<TJob>(
            jobId,
            job => job.ExecuteAsync(CancellationToken.None),
            cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }

    public void RemoveRecurring(string jobId) => manager.RemoveIfExists(jobId);

    public string? TriggerRecurring(string jobId) => manager.TriggerJob(jobId);
}
