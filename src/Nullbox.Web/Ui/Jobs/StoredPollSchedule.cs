using Nullbox.Core.Jobs;
using Nullbox.Core.Settings;
using Nullbox.Web.Inbox;

namespace Nullbox.Web.Ui.Jobs;

/// <summary>
/// At startup, puts the inbox poll back on the schedule set on the Jobs page (<see cref="AppSettingKeys.InboxPollCron"/>),
/// which overrides <c>Nullbox:Jobs:InboxPollCron</c>. Registered after the data layer, so migrations have run.
/// </summary>
public sealed class StoredPollSchedule(IServiceScopeFactory scopes, IJobScheduler scheduler, ILogger<StoredPollSchedule> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        if (scope.ServiceProvider.GetService<IAppSettingsStore>() is not { } settings
            || await settings.GetAsync(AppSettingKeys.InboxPollCron, cancellationToken) is not { } cron)
        {
            return;
        }

        try
        {
            scheduler.AddOrUpdateRecurring<InboxPollJob>(InboxPollJob.JobId, cron);
            logger.LogInformation("Inbox poll uses the schedule set on the Jobs page: {Cron}", cron);
        }
        catch (ArgumentException)
        {
            logger.LogWarning("Ignoring the stored inbox poll schedule {Cron}: it is not a valid cron expression", cron);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
