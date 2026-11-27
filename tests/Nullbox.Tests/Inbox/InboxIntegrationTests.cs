using System.Net;
using System.Text.Json;
using Hangfire;
using Hangfire.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Nullbox.Core.Gmail;
using Nullbox.Core.Inbox;
using Nullbox.Core.Jobs;
using Nullbox.Tests.Infrastructure;
using Nullbox.Web.Inbox;

namespace Nullbox.Tests.Inbox;

/// <summary>The real app with a scripted Gmail and a recording processor in place of the stub.</summary>
public sealed class InboxAppFactory : NullboxAppFactory
{
    public FakeInboxGmail Gmail { get; } = new();

    public RecordingProcessor Processor { get; } = new() { Delay = TimeSpan.FromMilliseconds(150) };

    protected override IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>(base.Settings)
    {
        ["Nullbox:Jobs:MessageWorkers"] = "2",
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IGmailService>(Gmail);
            services.AddSingleton<IMessageProcessor>(Processor);
        });
    }
}

[Collection(AppCollection.Name)]
public sealed class InboxIntegrationTests : IDisposable
{
    private readonly InboxAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public void Inbox_poll_is_registered_with_the_configured_cron()
    {
        _ = _factory.Server;

        using var connection = _factory.Services.GetRequiredService<JobStorage>().GetConnection();
        var poll = connection.GetRecurringJobs().Single(j => j.Id == InboxPollJob.JobId);

        Assert.Equal("0 0 1 1 *", poll.Cron);
        Assert.Equal(typeof(InboxPollJob), poll.Job.Type);
    }

    [Fact]
    public async Task Triggered_poll_processes_each_new_message_once_on_the_inbox_queue_with_bounded_workers()
    {
        _ = _factory.Server;
        _factory.Gmail.InboxSearchResults.AddRange(["m5", "m4", "m3", "m2", "m1"]);
        var scheduler = _factory.Services.GetRequiredService<IJobScheduler>();

        scheduler.TriggerRecurring(InboxPollJob.JobId);
        await WaitForAsync(() => _factory.Processor.Processed.Count >= 5);

        // A second poll that sees the same messages again (history replays them) enqueues nothing new.
        _factory.Gmail.History = start => new GmailHistoryChanges(["m1", "m3"], start + 1);
        scheduler.TriggerRecurring(InboxPollJob.JobId);
        var store = _factory.Services.GetRequiredService<IInboxStateStore>();
        await WaitForAsync(async () => (await store.GetAsync()).HistoryId == 101);
        await Task.Delay(500);

        Assert.Equal(["m1", "m2", "m3", "m4", "m5"], _factory.Processor.Processed.Order());
        Assert.InRange(_factory.Processor.MaxConcurrent, 1, 2);
    }

    [Fact]
    public async Task Status_and_pause_need_the_admin_login()
    {
        using var client = _factory.CreateNonRedirectingClient();

        using var status = await client.GetAsync("/inbox/status");
        using var pause = await client.PostAsync("/inbox/pause", new FormUrlEncodedContent([]));

        Assert.Equal(HttpStatusCode.Redirect, status.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, pause.StatusCode);
        Assert.False((await _factory.Services.GetRequiredService<IInboxStateStore>().GetAsync()).Paused);
    }

    [Fact]
    public async Task Pause_requires_an_antiforgery_token()
    {
        using var client = _factory.CreateNonRedirectingClient();
        using var login = await LoginHelper.LoginAsync(client, NullboxAppFactory.AdminPassword);

        using var pause = await client.PostAsync("/inbox/pause", new FormUrlEncodedContent([]));

        Assert.Equal(HttpStatusCode.BadRequest, pause.StatusCode);
        Assert.False((await _factory.Services.GetRequiredService<IInboxStateStore>().GetAsync()).Paused);
    }

    [Fact]
    public async Task Admin_can_pause_and_resume_and_a_paused_poll_skips_gmail()
    {
        using var client = _factory.CreateNonRedirectingClient();
        using var login = await LoginHelper.LoginAsync(client, NullboxAppFactory.AdminPassword);
        var token = await LoginHelper.GetAntiforgeryFieldAsync(client);

        using var pause = await client.PostAsync("/inbox/pause", new FormUrlEncodedContent([token]));
        Assert.Equal(HttpStatusCode.OK, pause.StatusCode);
        Assert.True(JsonDocument.Parse(await pause.Content.ReadAsStringAsync()).RootElement.GetProperty("paused").GetBoolean());

        await _factory.Services.GetRequiredService<InboxPollJob>().ExecuteAsync(default);
        Assert.Empty(_factory.Gmail.Calls);

        using var resume = await client.PostAsync("/inbox/resume", new FormUrlEncodedContent([token]));
        using var status = await client.GetAsync("/inbox/status");
        var json = JsonDocument.Parse(await status.Content.ReadAsStringAsync()).RootElement;
        Assert.False(json.GetProperty("paused").GetBoolean());
        Assert.Equal("paused", json.GetProperty("lastPollResult").GetString());
    }

    private static Task WaitForAsync(Func<bool> condition) => WaitForAsync(() => Task.FromResult(condition()));

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for background jobs");
            await Task.Delay(100);
        }
    }
}
