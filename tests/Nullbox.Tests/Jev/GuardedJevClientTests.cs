using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Nullbox.Core.Jev;
using Nullbox.Jev;
using Nullbox.Jev.Testing;

namespace Nullbox.Tests.Jev;

public sealed class GuardedJevClientTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 27, 10, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nullbox-jev-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task Same_message_and_rule_set_is_answered_from_cache()
    {
        var fake = new FakeJevClient(_ => JevTestSupport.Result());
        var guard = Guard(fake, new InMemoryJevStore());
        var context = new JevCallContext("msg-1", "rules-v1");

        var first = await guard.EvaluateAsync(JevTestSupport.SimpleRequest(), context);
        var second = await guard.EvaluateAsync(JevTestSupport.SimpleRequest(), context);

        Assert.Single(fake.Calls);
        Assert.False(first.FromCache);
        Assert.True(second.FromCache);
        Assert.Equal(0.02, second.Answer<JevNoulAnswer>("is_phishing")!.Noul);
    }

    [Fact]
    public async Task New_rule_set_version_or_no_context_misses_the_cache()
    {
        var fake = new FakeJevClient(_ => JevTestSupport.Result());
        var guard = Guard(fake, new InMemoryJevStore());

        await guard.EvaluateAsync(JevTestSupport.SimpleRequest(), new JevCallContext("msg-1", "rules-v1"));
        await guard.EvaluateAsync(JevTestSupport.SimpleRequest(), new JevCallContext("msg-1", "rules-v2"));
        await guard.EvaluateAsync(JevTestSupport.SimpleRequest());

        Assert.Equal(3, fake.Calls.Count);
    }

    [Fact]
    public async Task Cache_can_be_turned_off()
    {
        var fake = new FakeJevClient(_ => JevTestSupport.Result());
        var guard = Guard(fake, new InMemoryJevStore(), o => o.CacheEnabled = false);
        var context = new JevCallContext("msg-1", "rules-v1");

        await guard.EvaluateAsync(JevTestSupport.SimpleRequest(), context);
        await guard.EvaluateAsync(JevTestSupport.SimpleRequest(), context);

        Assert.Equal(2, fake.Calls.Count);
    }

    [Fact]
    public async Task Daily_token_budget_blocks_calls_until_utc_midnight()
    {
        var fake = new FakeJevClient(_ => JevTestSupport.Result(inputTokens: 600));
        var time = new FakeTimeProvider(Start);
        var guard = Guard(fake, new InMemoryJevStore(), o => o.MaxInputTokensPerDay = 1000, time);

        await guard.EvaluateAsync(JevTestSupport.SimpleRequest());
        await guard.EvaluateAsync(JevTestSupport.SimpleRequest());
        var ex = await Assert.ThrowsAsync<JevBudgetExceededException>(() => guard.EvaluateAsync(JevTestSupport.SimpleRequest()));

        Assert.Equal(new DateTimeOffset(2026, 11, 28, 0, 0, 0, TimeSpan.Zero), ex.RetryAfter);
        Assert.Equal(2, fake.Calls.Count);
        Assert.True((await guard.GetStatusAsync()).OverBudget);

        time.SetUtcNow(new DateTimeOffset(2026, 11, 28, 0, 0, 1, TimeSpan.Zero));
        await guard.EvaluateAsync(JevTestSupport.SimpleRequest());
        Assert.Equal(3, fake.Calls.Count);
    }

    [Fact]
    public async Task Hourly_request_cap_blocks_until_the_oldest_call_ages_out()
    {
        var fake = new FakeJevClient(_ => JevTestSupport.Result(inputTokens: 1));
        var time = new FakeTimeProvider(Start);
        var guard = Guard(fake, new InMemoryJevStore(), o => o.MaxRequestsPerHour = 2, time);

        await guard.EvaluateAsync(JevTestSupport.SimpleRequest());
        time.Advance(TimeSpan.FromMinutes(10));
        await guard.EvaluateAsync(JevTestSupport.SimpleRequest());
        var ex = await Assert.ThrowsAsync<JevBudgetExceededException>(() => guard.EvaluateAsync(JevTestSupport.SimpleRequest()));

        Assert.Equal(Start.AddHours(1), ex.RetryAfter);
        var status = await guard.GetStatusAsync();
        Assert.Equal(2, status.RequestsLastHour);
        Assert.Equal(2, status.InputTokensToday);
    }

    [Fact]
    public async Task Cached_answers_cost_nothing_even_over_budget()
    {
        var fake = new FakeJevClient(_ => JevTestSupport.Result(inputTokens: 5000));
        var guard = Guard(fake, new InMemoryJevStore(), o => o.MaxInputTokensPerDay = 1000);
        var context = new JevCallContext("msg-1", "rules-v1");

        await guard.EvaluateAsync(JevTestSupport.SimpleRequest(), context);
        var again = await guard.EvaluateAsync(JevTestSupport.SimpleRequest(), context);

        Assert.True(again.FromCache);
        Assert.Single(fake.Calls);
    }

    [Fact]
    public async Task Sqlite_store_keeps_cache_and_usage_across_instances()
    {
        var path = Path.Combine(_dir, "jev.db");
        var time = new FakeTimeProvider(Start);
        var fake = new FakeJevClient(_ => JevTestSupport.Result(inputTokens: 250));
        var context = new JevCallContext("msg-1", "rules-v1");

        await Guard(fake, new SqliteJevStore(path), time: time).EvaluateAsync(JevTestSupport.SimpleRequest(), context);

        var reopened = Guard(fake, new SqliteJevStore(path), time: time);
        var cached = await reopened.EvaluateAsync(JevTestSupport.SimpleRequest(), context);
        var status = await reopened.GetStatusAsync();

        Assert.True(cached.FromCache);
        Assert.Single(fake.Calls);
        Assert.Equal(250, status.InputTokensToday);
        Assert.Equal(1, status.RequestsLastHour);
    }

    [Fact]
    public async Task Fake_client_answers_every_question_and_records_calls()
    {
        var fake = new FakeJevClient();
        var request = System.Text.Json.JsonSerializer.Deserialize<JevRequest>(JevTestSupport.Fixture("request.json"), JevJson.Options)!;

        var result = await fake.EvaluateAsync(request, new JevCallContext("m", "v"));

        Assert.Equal("receipt", result.Answer<JevChoiceAnswer>("category")!.Choice);
        Assert.NotNull(result.Answer<JevScoreAnswer>("urgency"));
        Assert.Equal(0, result.Answer<JevNoulAnswer>("is_phishing")!.Noul);
        Assert.True(result.Usage.InputTokens > 0);
        Assert.Equal("m", Assert.Single(fake.Calls).Context!.MessageId);
    }

    [Fact]
    public void Di_wires_the_guarded_client_and_reads_the_env_key()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TYPESAFE_API_KEY"] = "from-env" })
            .Build();
        var services = new ServiceCollection().AddLogging().AddNullboxJev(config, _dir);
        using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<IJevClient>();
        var options = provider.GetRequiredService<IOptions<JevOptions>>().Value;

        Assert.IsType<GuardedJevClient>(client);
        Assert.Same(client, provider.GetRequiredService<IJevBudget>());
        Assert.Equal("from-env", options.ApiKey);
        Assert.Equal(Path.Combine(_dir, "jev.db"), options.DatabasePath);
    }

    [Fact]
    public void Di_prefers_the_configured_key_then_jev_api_key()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["JEV_API_KEY"] = "jev", ["TYPESAFE_API_KEY"] = "typesafe" })
            .Build();
        using var provider = new ServiceCollection().AddLogging().AddNullboxJev(config, _dir).BuildServiceProvider();

        Assert.Equal("jev", provider.GetRequiredService<IOptions<JevOptions>>().Value.ApiKey);
    }

    private static GuardedJevClient Guard(IJevClient inner, IJevStore store, Action<JevOptions>? configure = null, TimeProvider? time = null)
        => new(inner, store, Options.Create(JevTestSupport.Options(configure)), NullLogger<GuardedJevClient>.Instance, time ?? new FakeTimeProvider(Start));
}
