using System.Net;
using Hangfire;
using Microsoft.Extensions.Options;
using Nullbox.Core.Gmail;
using Nullbox.Core.Inbox;
using Nullbox.Core.Jev;
using Nullbox.Core.Jobs;
using Nullbox.Core.Options;
using Nullbox.Core.Pipeline;

namespace Nullbox.Web.Inbox;

/// <summary>
/// Recurring inbox poll. Never runs twice at once, and is not retried: the next scheduled run picks up where
/// this one stopped, because the history id only moves forward after messages are enqueued.
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 10 * 60)]
[AutomaticRetry(Attempts = 0, LogEvents = true, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
public sealed class InboxPollJob(InboxPoller poller) : IRecurringJob
{
    public const string JobId = "inbox-poll";

    public Task ExecuteAsync(CancellationToken cancellationToken) => poller.PollAsync(cancellationToken);
}

/// <summary>
/// Processes one message on the <see cref="QueueName"/> queue, whose worker count bounds parallelism.
/// <list type="bullet">
/// <item>A Jev budget, or a Gmail or Jev credential that needs fixing, defers the message (a new scheduled job) without
/// counting an attempt.</item>
/// <item>A request Jev or Gmail will never accept (too many tokens, invalid, message gone) is recorded as poison at once.</item>
/// <item>Anything else counts an attempt and is rethrown, so Hangfire retries it with its growing delay, until
/// <see cref="PipelineSettings.MaxAttempts"/>; then it is recorded as poison and not retried again.</item>
/// </list>
/// </summary>
[AutomaticRetry(Attempts = MaxHangfireRetries)]
public sealed class ProcessMessageJob(
    IMessageProcessor processor,
    IInboxStateStore store,
    IDecisionLog log,
    IMessageJobQueue queue,
    IOptions<NullboxOptions> options,
    TimeProvider time,
    ILogger<ProcessMessageJob> logger)
{
    public const string QueueName = "inbox";

    /// <summary>Upper bound only; <see cref="PipelineSettings.MaxAttempts"/> normally stops retries first.</summary>
    public const int MaxHangfireRetries = 10;

    public async Task ExecuteAsync(string messageId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        if (await store.IsProcessedAsync(messageId, cancellationToken))
        {
            logger.LogDebug("Message {MessageId} already processed", messageId);
            return;
        }

        try
        {
            await processor.ProcessAsync(messageId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (DeferUntil(ex) is { } at)
        {
            queue.ScheduleProcessing(messageId, at);
            logger.LogWarning("Message {MessageId} deferred until {At:u}: {Reason}", messageId, at, ex.Message);
            return;
        }
        catch (Exception ex) when (IsPermanent(ex))
        {
            await GiveUpAsync(messageId, ex, cancellationToken);
            return;
        }
        catch (Exception ex)
        {
            var attempts = await log.RecordFailureAsync(messageId, Describe(ex), time.GetUtcNow(), cancellationToken);
            if (attempts < Math.Max(1, options.Value.Pipeline.MaxAttempts))
            {
                logger.LogWarning(ex, "Message {MessageId} failed (attempt {Attempts}); retrying", messageId, attempts);
                throw;
            }

            await GiveUpAsync(messageId, ex, cancellationToken);
            return;
        }

        await log.ClearFailuresAsync(messageId, cancellationToken);
        await store.MarkProcessedAsync(messageId, time.GetUtcNow(), cancellationToken);
    }

    /// <summary>When to try again for failures that are about budget or credentials rather than the message.</summary>
    public DateTimeOffset? DeferUntil(Exception ex) => ex switch
    {
        JevBudgetExceededException budget => budget.RetryAfter,
        GmailReauthorizationRequiredException or GmailNotConfiguredException or JevUnauthorizedException or JevNotConfiguredException
            => time.GetUtcNow() + options.Value.Pipeline.AuthRetryDelay,
        _ => null,
    };

    /// <summary>Failures that retrying the same message can't fix.</summary>
    public static bool IsPermanent(Exception ex) => ex switch
    {
        JevMaxTokensExceededException or JevValidationException => true,
        Google.GoogleApiException { HttpStatusCode: HttpStatusCode.NotFound or HttpStatusCode.BadRequest } => true,
        _ => false,
    };

    private async Task GiveUpAsync(string messageId, Exception ex, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await log.MarkPoisonAsync(messageId, Describe(ex), now, cancellationToken);
        await store.MarkProcessedAsync(messageId, now, cancellationToken);
        logger.LogError(ex, "Message {MessageId} recorded as poison; it will not be retried", messageId);
    }

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";
}

/// <summary>Enqueues <see cref="ProcessMessageJob"/> on the inbox queue.</summary>
public sealed class HangfireMessageJobQueue(IBackgroundJobClient client) : IMessageJobQueue
{
    public void EnqueueProcessing(string messageId)
        => client.Enqueue<ProcessMessageJob>(ProcessMessageJob.QueueName, job => job.ExecuteAsync(messageId, CancellationToken.None));

    public void ScheduleProcessing(string messageId, DateTimeOffset at)
        => client.Schedule<ProcessMessageJob>(ProcessMessageJob.QueueName, job => job.ExecuteAsync(messageId, CancellationToken.None), at);
}
