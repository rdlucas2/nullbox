using Nullbox.Core.Jobs;

namespace Nullbox.Web.Jobs;

/// <summary>Tracks heartbeat runs so the scheduler's liveness is observable (dashboard, tests).</summary>
public sealed class HeartbeatState
{
    private long _runCount;
    private long _lastRunTicks;

    public long RunCount => Interlocked.Read(ref _runCount);

    public DateTimeOffset? LastRunUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastRunTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public void Record(DateTimeOffset nowUtc)
    {
        Interlocked.Exchange(ref _lastRunTicks, nowUtc.UtcTicks);
        Interlocked.Increment(ref _runCount);
    }
}

/// <summary>No-op recurring job proving that scheduling and execution work end to end.</summary>
public sealed class HeartbeatJob(HeartbeatState state, TimeProvider time, ILogger<HeartbeatJob> logger) : IRecurringJob
{
    public const string JobId = "heartbeat";

    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        state.Record(time.GetUtcNow());
        logger.LogInformation("Heartbeat {RunCount}", state.RunCount);
        return Task.CompletedTask;
    }
}
