namespace Nullbox.Core.Jobs;

/// <summary>A unit of background work that the scheduler runs on a cron schedule.</summary>
public interface IRecurringJob
{
    Task ExecuteAsync(CancellationToken cancellationToken);
}
