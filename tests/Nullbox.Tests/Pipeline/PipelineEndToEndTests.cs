using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Nullbox.Core.Gmail;
using Nullbox.Core.Jev;
using Nullbox.Core.Jobs;
using Nullbox.Core.Pipeline;
using Nullbox.Core.Rules;
using Nullbox.Jev.Testing;
using Nullbox.Tests.Inbox;
using Nullbox.Tests.Infrastructure;
using Nullbox.Web.Inbox;

namespace Nullbox.Tests.Pipeline;

/// <summary>The real app and pipeline, with a scripted Gmail and Jev. The example rule sets are seeded as dry runs.</summary>
public sealed class PipelineAppFactory : NullboxAppFactory
{
    public FakeInboxGmail Gmail { get; } = new();

    /// <summary>A confident promo for promo-cleanup; neutral answers for anything else.</summary>
    public FakeJevClient Jev { get; } = new(request => request.Questions.ContainsKey("category")
        ? Answers.ConfidentPromo
        : FakeJevClient.DefaultResponder(request));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IGmailService>(Gmail);
            services.AddSingleton<IJevClient>(Jev);
        });
    }
}

[Collection(AppCollection.Name)]
public sealed class PipelineEndToEndTests : IDisposable
{
    private readonly PipelineAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<IReadOnlyList<DecisionRecord>> DecisionsAsync(string messageId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDecisionLog>().ListAsync(messageId);
    }

    private async Task PollAndWaitAsync(string messageId, int ruleSets)
    {
        _factory.Services.GetRequiredService<IJobScheduler>().TriggerRecurring(InboxPollJob.JobId);
        var store = _factory.Services.GetRequiredService<Nullbox.Core.Inbox.IInboxStateStore>();
        await WaitForAsync(async () => (await DecisionsAsync(messageId)).Count >= ruleSets && await store.IsProcessedAsync(messageId));
    }

    [Fact]
    public async Task Dry_run_records_decisions_without_touching_Gmail_and_live_applies_them()
    {
        _ = _factory.Server;
        _factory.Gmail.Add("m1");
        _factory.Gmail.InboxSearchResults.Add("m1");

        await PollAndWaitAsync("m1", ruleSets: 2);

        var dryRun = await DecisionsAsync("m1");
        Assert.All(dryRun, r => Assert.Equal(DecisionOutcome.DryRun, r.Outcome));
        Assert.Equal(["label:nullbox/promo", "mark_read", "archive"], dryRun.Single(r => r.RuleSetKey == "promo-cleanup@v1").Actions);
        Assert.Empty(_factory.Gmail.Changes);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var ruleSets = scope.ServiceProvider.GetRequiredService<IRuleSetStore>();
            var active = await ruleSets.GetVersionAsync(RuleSetSeeds.PromoCleanup);
            var live = await ruleSets.SaveAsync(active!.Document with { Mode = RuleSetMode.Live });
            await ruleSets.ActivateAsync(live.Name, live.Version);
        }

        _factory.Gmail.Add("m2");
        _factory.Gmail.History = start => new GmailHistoryChanges(["m2"], start + 1);
        await PollAndWaitAsync("m2", ruleSets: 2);

        var applied = (await DecisionsAsync("m2")).Single(r => r.RuleSetKey == "promo-cleanup@v2");
        Assert.Equal((RuleSetMode.Live, DecisionOutcome.Applied), (applied.Mode, applied.Outcome));
        Assert.Equal(["label:nullbox/promo", "modify:m2:+Label_nullbox/promo:-UNREAD,INBOX"], _factory.Gmail.Changes);
        Assert.DoesNotContain(_factory.Gmail.Changes, c => c.Contains("m1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pipeline_endpoints_need_the_admin_login_and_the_kill_switch_needs_a_token()
    {
        using var client = _factory.CreateNonRedirectingClient();
        using (var anonymous = await client.GetAsync("/pipeline/status"))
        {
            Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
        }

        using var login = await LoginHelper.LoginAsync(client, NullboxAppFactory.AdminPassword);
        using (var noToken = await client.PostAsync("/pipeline/kill-switch/on", new FormUrlEncodedContent([])))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        }

        var token = await LoginHelper.GetAntiforgeryFieldAsync(client);
        using var on = await client.PostAsync("/pipeline/kill-switch/on", new FormUrlEncodedContent([token]));
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);

        using var status = await client.GetAsync("/pipeline/status");
        var json = JsonDocument.Parse(await status.Content.ReadAsStringAsync()).RootElement;
        Assert.True(json.GetProperty("killSwitch").GetBoolean());
        Assert.False(json.GetProperty("killSwitchFromConfig").GetBoolean());
        Assert.Equal(100, json.GetProperty("maxMessagesPerHour").GetInt32());

        using var decisions = await client.GetAsync("/pipeline/decisions?limit=5");
        using var poison = await client.GetAsync("/pipeline/poison");
        Assert.Equal("[]", await decisions.Content.ReadAsStringAsync());
        Assert.Equal("[]", await poison.Content.ReadAsStringAsync());

        using var off = await client.PostAsync("/pipeline/kill-switch/off", new FormUrlEncodedContent([token]));
        Assert.False(JsonDocument.Parse(await off.Content.ReadAsStringAsync()).RootElement.GetProperty("killSwitch").GetBoolean());
    }

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
