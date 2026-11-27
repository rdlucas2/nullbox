using Hangfire;
using Microsoft.Extensions.Options;
using Nullbox.Core.Cleanup;
using Nullbox.Core.Options;
using Nullbox.Core.Pipeline;
using Nullbox.Gmail.Cleanup;

namespace Nullbox.Web.Cleanup;

/// <summary>
/// Runs one queued cleanup (<see cref="ICleanupRunLog"/>). Not retried: a failed run is recorded and can be started
/// again. Applying is refused while the pipeline kill switch is on; dry runs always go ahead, since they change nothing.
/// </summary>
[AutomaticRetry(Attempts = 0, LogEvents = true, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
[DisableConcurrentExecution(timeoutInSeconds: 30 * 60)]
public sealed class RunCleanupJob(
    ICleanupConfigStore configs,
    ICleanupRunLog runs,
    IPipelineSettingsStore settings,
    IOptions<NullboxOptions> options,
    CleanupRunner runner,
    ILogger<RunCleanupJob> logger)
{
    public const string KillSwitchReason = "The kill switch is on, so only dry runs are allowed.";

    public async Task ExecuteAsync(long runId, CancellationToken cancellationToken)
    {
        var run = await runs.GetAsync(runId, cancellationToken) ?? throw new KeyNotFoundException($"No cleanup run {runId}.");
        await runs.MarkRunningAsync(runId, cancellationToken);

        var entry = configs.Get(run.ConfigId);
        if (entry?.Config is not { } config)
        {
            await runs.FinishWithoutResultAsync(runId, CleanupRunStatus.Failed, entry?.Error ?? $"There is no cleanup file named {run.ConfigId}.", CancellationToken.None);
            return;
        }

        if (!run.DryRun && (options.Value.Pipeline.KillSwitch || await settings.GetKillSwitchAsync(cancellationToken)))
        {
            logger.LogWarning("Cleanup run {RunId} ({Config}) blocked: kill switch is on", runId, run.ConfigId);
            await runs.FinishWithoutResultAsync(runId, CleanupRunStatus.Blocked, KillSwitchReason, CancellationToken.None);
            return;
        }

        try
        {
            var result = await runner.RunAsync(config, run.DryRun, run.SampleSize, cancellationToken);
            await runs.CompleteAsync(runId, result, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await runs.FinishWithoutResultAsync(runId, CleanupRunStatus.Failed, $"{ex.GetType().Name}: {ex.Message}", CancellationToken.None);
            throw; // also shows as failed on the Hangfire dashboard
        }
    }
}

/// <summary>
/// Queues a cleanup run and hands it to Hangfire. The Hangfire client is resolved only when a run starts, so a page
/// that merely shows the launcher's buttons never builds one.
/// </summary>
public sealed class CleanupLauncher(ICleanupRunLog runs, IServiceProvider services)
{
    /// <summary>Sender samples read one message each, so they are capped.</summary>
    public const int MaxSampleSize = 500;

    public async Task<CleanupRun> StartAsync(string configId, bool dryRun, int sampleSize, CancellationToken cancellationToken)
    {
        var run = await runs.QueueAsync(configId, dryRun, Math.Clamp(sampleSize, 0, MaxSampleSize), cancellationToken);
        services.GetRequiredService<IBackgroundJobClient>().Enqueue<RunCleanupJob>(job => job.ExecuteAsync(run.Id, CancellationToken.None));
        return run;
    }
}
