using Hangfire;
using Hangfire.Storage;
using Nullbox.Core.Gmail;
using Nullbox.Core.Jev;
using Nullbox.Web.Jobs;

namespace Nullbox.Web.Ui;

/// <summary>Everything the dashboard shows, read once per page load.</summary>
public sealed record DashboardSnapshot(
    GmailCredentialStatus? Gmail,
    JobRunStatus? InboxPoll,
    JobRunStatus? Heartbeat,
    QueueStatus Queues,
    JevBudgetStatus? JevBudget,
    DateTimeOffset ReadAtUtc);

/// <summary>The last run of a recurring job, as Hangfire recorded it.</summary>
public sealed record JobRunStatus(string JobId, DateTime? LastRunUtc, string? LastState, DateTime? NextRunUtc, string? Error);

public sealed record QueueStatus(long Enqueued, long Processing, long Failed, long Succeeded, IReadOnlyDictionary<string, long> ByQueue);

/// <summary>Reads the dashboard's status cards from the real services; each source is optional.</summary>
public sealed class DashboardStatusProvider(IServiceProvider services, TimeProvider time)
{
    /// <summary>Recurring job id of the inbox poll (M1-08); shown as soon as that job exists.</summary>
    public const string InboxPollJobId = "inbox-poll";

    public async Task<DashboardSnapshot> GetAsync(CancellationToken cancellationToken = default)
    {
        var gmail = services.GetService<IGmailCredentialStore>()?.GetStatus(GmailTokenKind.Primary);
        var budget = services.GetService<IJevBudget>() is { } jev ? await jev.GetStatusAsync(cancellationToken) : null;

        JobRunStatus? poll = null, heartbeat = null;
        var queues = new QueueStatus(0, 0, 0, 0, new Dictionary<string, long>());
        if (services.GetService<JobStorage>() is { } storage)
        {
            using (var connection = storage.GetConnection())
            {
                var recurring = connection.GetRecurringJobs();
                poll = Find(recurring, InboxPollJobId);
                heartbeat = Find(recurring, HeartbeatJob.JobId);
            }

            var monitoring = storage.GetMonitoringApi();
            var stats = monitoring.GetStatistics();
            queues = new QueueStatus(
                stats.Enqueued,
                stats.Processing,
                stats.Failed,
                stats.Succeeded,
                monitoring.Queues().ToDictionary(q => q.Name, q => q.Length, StringComparer.Ordinal));
        }

        return new DashboardSnapshot(gmail, poll, heartbeat, queues, budget, time.GetUtcNow());
    }

    private static JobRunStatus? Find(IEnumerable<RecurringJobDto> jobs, string id)
        => jobs.FirstOrDefault(j => j.Id == id) is { } job
            ? new JobRunStatus(job.Id, job.LastExecution, job.LastJobState, job.NextExecution, job.Error)
            : null;
}
