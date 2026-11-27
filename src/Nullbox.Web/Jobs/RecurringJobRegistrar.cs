using Microsoft.Extensions.Options;
using Nullbox.Core.Jobs;
using Nullbox.Core.Options;
using Nullbox.Web.Inbox;

namespace Nullbox.Web.Jobs;

/// <summary>Registers the app's recurring jobs with the scheduler when the host starts.</summary>
public sealed class RecurringJobRegistrar(IJobScheduler scheduler, IOptions<NullboxOptions> options) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        scheduler.AddOrUpdateRecurring<HeartbeatJob>(HeartbeatJob.JobId, options.Value.Jobs.HeartbeatCron);
        scheduler.AddOrUpdateRecurring<InboxPollJob>(InboxPollJob.JobId, options.Value.Jobs.InboxPollCron);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
