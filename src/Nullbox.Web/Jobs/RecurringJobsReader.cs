using Hangfire;
using Hangfire.Storage;

namespace Nullbox.Web.Jobs;

/// <summary>A recurring job as the Jobs page shows it.</summary>
public sealed record RecurringJobView(string Id, string Cron, DateTime? LastRunUtc, string? LastState, DateTime? NextRunUtc, string? Error);

/// <summary>Reads the scheduler's recurring jobs.</summary>
public interface IRecurringJobsReader
{
    IReadOnlyList<RecurringJobView> List();
}

/// <summary><see cref="IRecurringJobsReader"/> over Hangfire's storage.</summary>
public sealed class HangfireRecurringJobsReader(JobStorage storage) : IRecurringJobsReader
{
    public IReadOnlyList<RecurringJobView> List()
    {
        using var connection = storage.GetConnection();
        return [.. connection.GetRecurringJobs()
            .OrderBy(j => j.Id, StringComparer.Ordinal)
            .Select(j => new RecurringJobView(j.Id, j.Cron, j.LastExecution, j.LastJobState, j.NextExecution, j.Error))];
    }
}
