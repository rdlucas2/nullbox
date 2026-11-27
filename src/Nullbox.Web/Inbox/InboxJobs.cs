using Hangfire;
using Nullbox.Core.Inbox;
using Nullbox.Core.Jobs;

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

/// <summary>Processes one message on the <see cref="QueueName"/> queue, whose worker count bounds parallelism.</summary>
[AutomaticRetry(Attempts = 3)]
public sealed class ProcessMessageJob(
    IMessageProcessor processor,
    IInboxStateStore store,
    TimeProvider time,
    ILogger<ProcessMessageJob> logger)
{
    public const string QueueName = "inbox";

    public async Task ExecuteAsync(string messageId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        if (await store.IsProcessedAsync(messageId, cancellationToken))
        {
            logger.LogDebug("Message {MessageId} already processed", messageId);
            return;
        }

        await processor.ProcessAsync(messageId, cancellationToken);
        await store.MarkProcessedAsync(messageId, time.GetUtcNow(), cancellationToken);
    }
}

/// <summary>Enqueues <see cref="ProcessMessageJob"/> on the inbox queue.</summary>
public sealed class HangfireMessageJobQueue(IBackgroundJobClient client) : IMessageJobQueue
{
    public void EnqueueProcessing(string messageId)
        => client.Enqueue<ProcessMessageJob>(ProcessMessageJob.QueueName, job => job.ExecuteAsync(messageId, CancellationToken.None));
}

/// <summary>Placeholder until the rule pipeline exists (M2-12): logs and does nothing else.</summary>
public sealed class NoopMessageProcessor(ILogger<NoopMessageProcessor> logger) : IMessageProcessor
{
    public Task ProcessAsync(string messageId, CancellationToken cancellationToken)
    {
        logger.LogInformation("Message {MessageId} received; no processing pipeline yet", messageId);
        return Task.CompletedTask;
    }
}
