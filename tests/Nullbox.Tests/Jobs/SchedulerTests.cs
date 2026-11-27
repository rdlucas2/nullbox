using Hangfire;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nullbox.Core.Jobs;
using Nullbox.Tests.Infrastructure;
using Nullbox.Web.Jobs;

namespace Nullbox.Tests.Jobs;

[Collection(AppCollection.Name)]
public sealed class SchedulerTests(NullboxAppFactory factory)
{
    [Fact]
    public void Heartbeat_is_registered_as_a_recurring_job_with_the_configured_cron()
    {
        _ = factory.Server; // start the host so the registrar has run

        using var connection = factory.Services.GetRequiredService<JobStorage>().GetConnection();
        var heartbeat = connection.GetRecurringJobs().SingleOrDefault(j => j.Id == HeartbeatJob.JobId);

        Assert.NotNull(heartbeat);
        Assert.Equal("0 0 1 1 *", heartbeat.Cron);
        Assert.Equal(typeof(HeartbeatJob), heartbeat.Job.Type);
    }

    [Fact]
    public async Task Triggered_heartbeat_executes_on_the_hangfire_server()
    {
        _ = factory.Server;
        var state = factory.Services.GetRequiredService<HeartbeatState>();
        var before = state.RunCount;

        var jobId = factory.Services.GetRequiredService<IJobScheduler>().TriggerRecurring(HeartbeatJob.JobId);

        Assert.False(string.IsNullOrEmpty(jobId));
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (state.RunCount == before && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        Assert.True(state.RunCount > before, "heartbeat should have run on the background server");
        Assert.NotNull(state.LastRunUtc);
    }

    [Fact]
    public void Scheduler_adds_updates_and_removes_recurring_jobs()
    {
        _ = factory.Server;
        var scheduler = factory.Services.GetRequiredService<IJobScheduler>();
        using var connection = factory.Services.GetRequiredService<JobStorage>().GetConnection();

        scheduler.AddOrUpdateRecurring<HeartbeatJob>("scheduler-test", "*/10 * * * *");
        scheduler.AddOrUpdateRecurring<HeartbeatJob>("scheduler-test", "*/15 * * * *");
        var updated = connection.GetRecurringJobs().Single(j => j.Id == "scheduler-test");
        scheduler.RemoveRecurring("scheduler-test");

        Assert.Equal("*/15 * * * *", updated.Cron);
        Assert.DoesNotContain(connection.GetRecurringJobs(), j => j.Id == "scheduler-test");
    }

    [Fact]
    public void Scheduler_rejects_blank_ids_and_crons()
    {
        var scheduler = factory.Services.GetRequiredService<IJobScheduler>();

        Assert.Throws<ArgumentException>(() => scheduler.AddOrUpdateRecurring<HeartbeatJob>(" ", "* * * * *"));
        Assert.Throws<ArgumentException>(() => scheduler.AddOrUpdateRecurring<HeartbeatJob>("id", ""));
    }
}

public sealed class HeartbeatJobTests
{
    [Fact]
    public async Task ExecuteAsync_records_the_run_time()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 27, 5, 0, 0, TimeSpan.Zero));
        var state = new HeartbeatState();
        var job = new HeartbeatJob(state, time, NullLogger<HeartbeatJob>.Instance);

        await job.ExecuteAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(5));
        await job.ExecuteAsync(CancellationToken.None);

        Assert.Equal(2, state.RunCount);
        Assert.Equal(new DateTimeOffset(2026, 11, 27, 5, 5, 0, TimeSpan.Zero), state.LastRunUtc);
    }

    [Fact]
    public async Task ExecuteAsync_honours_cancellation()
    {
        var job = new HeartbeatJob(new HeartbeatState(), TimeProvider.System, NullLogger<HeartbeatJob>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(() => job.ExecuteAsync(new CancellationToken(canceled: true)));
    }
}
