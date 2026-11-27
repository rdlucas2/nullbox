namespace Nullbox.Core.Jobs;

/// <summary>Scheduler abstraction so the rest of the app never references Hangfire directly.</summary>
public interface IJobScheduler
{
    /// <summary>Creates or updates a recurring job. <paramref name="cron"/> is a standard 5-field cron expression (UTC).</summary>
    void AddOrUpdateRecurring<TJob>(string jobId, string cron)
        where TJob : IRecurringJob;

    void RemoveRecurring(string jobId);

    /// <summary>Runs a recurring job now, outside its schedule. Returns the background job id, if one was created.</summary>
    string? TriggerRecurring(string jobId);
}
