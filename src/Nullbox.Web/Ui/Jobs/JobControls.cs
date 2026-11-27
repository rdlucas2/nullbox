using Microsoft.Extensions.Options;
using Nullbox.Core.Inbox;
using Nullbox.Core.Jobs;
using Nullbox.Core.Options;
using Nullbox.Core.Pipeline;
using Nullbox.Core.Settings;
using Nullbox.Web.Inbox;

namespace Nullbox.Web.Ui.Jobs;

/// <summary>Everything the Jobs page shows about the inbox poll and the pipeline's kill switch.</summary>
public sealed record JobsState(
    string PollCron,
    bool PollCronFromSettings,
    string ConfiguredPollCron,
    bool PollPaused,
    DateTimeOffset? LastPollUtc,
    string? LastPollResult,
    bool KillSwitch,
    bool KillSwitchFromConfig);

/// <summary>
/// The Jobs page's controls: the inbox poll schedule (kept in <c>app_settings</c>, so it survives restarts and
/// overrides <c>Nullbox:Jobs:InboxPollCron</c>), pausing the poll, the pipeline kill switch, and "run now".
/// </summary>
public sealed class JobControls(
    IJobScheduler scheduler,
    IInboxStateStore inbox,
    IPipelineSettingsStore pipeline,
    IAppSettingsStore settings,
    IOptions<NullboxOptions> options)
{
    public async Task<JobsState> GetAsync(CancellationToken cancellationToken = default)
    {
        var stored = await settings.GetAsync(AppSettingKeys.InboxPollCron, cancellationToken);
        var state = await inbox.GetAsync(cancellationToken);
        var configured = options.Value.Jobs.InboxPollCron;
        return new JobsState(
            stored ?? configured,
            stored is not null,
            configured,
            state.Paused,
            state.LastPollUtc,
            state.LastPollResult,
            options.Value.Pipeline.KillSwitch || await pipeline.GetKillSwitchAsync(cancellationToken),
            options.Value.Pipeline.KillSwitch);
    }

    /// <summary>
    /// Reschedules the poll. The scheduler parses the cron first, so a bad expression changes nothing.
    /// Null or blank goes back to the configured schedule.
    /// </summary>
    /// <returns>Null on success, otherwise why the expression was refused.</returns>
    public async Task<string?> SetPollCronAsync(string? cron, CancellationToken cancellationToken = default)
    {
        var value = string.IsNullOrWhiteSpace(cron) ? null : cron.Trim();
        try
        {
            scheduler.AddOrUpdateRecurring<InboxPollJob>(InboxPollJob.JobId, value ?? options.Value.Jobs.InboxPollCron);
        }
        catch (ArgumentException ex)
        {
            return $"That is not a valid cron expression ({ex.Message.Split('\n')[0]}).";
        }

        await settings.SetAsync(AppSettingKeys.InboxPollCron, value, cancellationToken);
        return null;
    }

    public Task SetPollPausedAsync(bool paused, CancellationToken cancellationToken = default) => inbox.SetPausedAsync(paused, cancellationToken);

    public Task SetKillSwitchAsync(bool on, CancellationToken cancellationToken = default) => pipeline.SetKillSwitchAsync(on, cancellationToken);

    /// <summary>Runs the inbox poll now, outside its schedule. Returns the Hangfire job id, if one was created.</summary>
    public string? RunPollNow() => scheduler.TriggerRecurring(InboxPollJob.JobId);
}
