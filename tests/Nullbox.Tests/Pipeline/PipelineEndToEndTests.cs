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
        Assert.Equal(["label:nullbox/promo", "label:nullbox/review/promo-cleanup/archive-confident-promos", "mark_read", "archive"], dryRun.Single(r => r.RuleSetKey == "promo-cleanup@v1").Actions);
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
        const string review = "nullbox/review/promo-cleanup/archive-confident-promos";
        Assert.Equal(
            ["label:nullbox/promo", $"label:{review}", $"modify:m2:+Label_nullbox/promo,Label_{review}:-UNREAD,INBOX"],
            _factory.Gmail.Changes);
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
        Assert.Equal("""{"items":[],"next":null}""", await decisions.Content.ReadAsStringAsync());
        Assert.Equal("[]", await poison.Content.ReadAsStringAsync());

        using (var badOutcome = await client.GetAsync("/pipeline/decisions?outcome=maybe"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, badOutcome.StatusCode);
        }

        using (var replayNoToken = await client.PostAsync("/pipeline/replay", JsonBody("{}")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, replayNoToken.StatusCode);
            Assert.Contains("antiforgery", await replayNoToken.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        using var off = await client.PostAsync("/pipeline/kill-switch/off", new FormUrlEncodedContent([token]));
        Assert.False(JsonDocument.Parse(await off.Content.ReadAsStringAsync()).RootElement.GetProperty("killSwitch").GetBoolean());
    }

    [Fact]
    public async Task Decisions_can_be_filtered_and_a_draft_replayed_over_them_without_touching_Gmail()
    {
        _ = _factory.Server;
        _factory.Gmail.Add("m1");
        _factory.Gmail.InboxSearchResults.Add("m1");
        await PollAndWaitAsync("m1", ruleSets: 2);

        using var client = _factory.CreateNonRedirectingClient();
        using var login = await LoginHelper.LoginAsync(client, NullboxAppFactory.AdminPassword);

        using var filtered = await client.GetAsync("/pipeline/decisions?ruleSet=promo-cleanup&version=1&action=archive&mode=dry_run&outcome=dry_run&minConfidence=0.8");
        var page = JsonDocument.Parse(await filtered.Content.ReadAsStringAsync()).RootElement;
        var item = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal("promo-cleanup@v1", item.GetProperty("ruleSetKey").GetString());
        Assert.Equal("dry_run", item.GetProperty("outcome").GetString());
        Assert.Equal(64, item.GetProperty("inputsHash").GetString()!.Length);

        using var none = await client.GetAsync("/pipeline/decisions?ruleSet=promo-cleanup&maxConfidence=0.5");
        Assert.Empty(JsonDocument.Parse(await none.Content.ReadAsStringAsync()).RootElement.GetProperty("items").EnumerateArray());

        var token = await LoginHelper.GetAntiforgeryFieldAsync(client);
        using (var invalid = await PostReplayAsync(client, token.Value, """{ "rule_set": { "name": "Bad Name" } }"""))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Contains("errors", await invalid.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        var draft = RuleJson.Serialize(RuleSetSeeds.Load(RuleSetSeeds.PromoCleanup) with
        {
            Decisions = [new DecisionRule { Name = "Only label", When = "category.choice == \"promo\"", Actions = ["label:nullbox/promo"] }],
        });
        var callsBefore = _factory.Jev.Calls.Count;
        using var replay = await PostReplayAsync(client, token.Value, $$"""{ "rule_set": {{draft}}, "sample": { "limit": 10 } }""");

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var result = JsonDocument.Parse(await replay.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, result.GetProperty("changed").GetInt32());
        Assert.Equal(0, result.GetProperty("jevCalls").GetInt32());
        var changed = Assert.Single(result.GetProperty("items").EnumerateArray());
        Assert.Equal("changed", changed.GetProperty("status").GetString());
        Assert.Equal(["label:nullbox/promo"], changed.GetProperty("after").GetProperty("actions").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal(callsBefore, _factory.Jev.Calls.Count);
        Assert.Empty(_factory.Gmail.Changes);
    }

    private static StringContent JsonBody(string json) => new(json, System.Text.Encoding.UTF8, "application/json");

    private static async Task<HttpResponseMessage> PostReplayAsync(HttpClient client, string token, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/pipeline/replay") { Content = JsonBody(json) };
        request.Headers.Add("RequestVerificationToken", token);
        return await client.SendAsync(request);
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
