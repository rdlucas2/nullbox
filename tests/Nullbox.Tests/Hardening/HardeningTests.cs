using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nullbox.Core.Gmail;
using Nullbox.Core.Inbox;
using Nullbox.Core.Jobs;
using Nullbox.Core.Pipeline;
using Nullbox.Gmail.Auth;
using Nullbox.Tests.Infrastructure;
using Nullbox.Web.Inbox;

namespace Nullbox.Tests.Hardening;

[Collection(AppCollection.Name)]
public sealed class HealthAndMetricsTests(NullboxAppFactory factory)
{
    [Fact]
    public async Task Liveness_only_needs_the_database_so_a_missing_gmail_token_is_not_unhealthy()
    {
        using var client = factory.CreateNonRedirectingClient();

        using var response = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Readiness_reports_each_component_to_admins_only()
    {
        using var client = factory.CreateNonRedirectingClient();
        using (var anonymous = await client.GetAsync("/healthz/ready"))
        {
            Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
        }

        using var login = await LoginHelper.LoginAsync(client, NullboxAppFactory.AdminPassword);
        using var response = await client.GetAsync("/healthz/ready");
        var checks = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("checks");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // degraded still answers 200
        Assert.Equal("Healthy", checks.GetProperty("database").GetProperty("status").GetString());
        Assert.Equal(("Degraded", "not connected"), (checks.GetProperty("gmail").GetProperty("status").GetString(), checks.GetProperty("gmail").GetProperty("description").GetString()));
        Assert.Equal("Degraded", checks.GetProperty("jev").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Metrics_count_decisions_jobs_and_jev_for_admins_only()
    {
        using var client = factory.CreateNonRedirectingClient();
        using (var anonymous = await client.GetAsync("/api/metrics"))
        {
            Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDecisionLog>().RecordAsync(new DecisionRecord(
                "metrics-1", "promo-cleanup@v1", Nullbox.Core.Rules.RuleSetMode.DryRun, DecisionOutcome.DryRun, [], ["archive"], [], "jev-fake", "{}", DateTimeOffset.UtcNow));
        }

        using var login = await LoginHelper.LoginAsync(client, NullboxAppFactory.AdminPassword);
        var metrics = JsonDocument.Parse(await client.GetStringAsync("/api/metrics")).RootElement;

        Assert.True(metrics.GetProperty("decisions").GetProperty("last24Hours").GetProperty("DryRun").GetInt32() >= 1);
        Assert.Equal(JsonValueKind.Number, metrics.GetProperty("jobs").GetProperty("succeeded").ValueKind);
        Assert.Equal(JsonValueKind.Object, metrics.GetProperty("cleanupRuns").ValueKind);
    }
}

public sealed class LoginRateLimitTests : IDisposable
{
    private readonly LimitedFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Sign_in_attempts_beyond_the_limit_are_refused_with_429()
    {
        using var client = _factory.CreateNonRedirectingClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            using var response = await LoginHelper.LoginAsync(client, "wrong-password");
            statuses.Add(response.StatusCode);
        }

        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, statuses.Take(3));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[3]);
    }

    private sealed class LimitedFactory : NullboxAppFactory
    {
        protected override IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>(base.Settings)
        {
            ["Nullbox:Login:AttemptsPerWindow"] = "3",
        };
    }
}

public sealed class GmailOverrideTests
{
    [Fact]
    public void A_test_base_uri_redirects_the_gmail_client()
    {
        var files = new GoogleCredentialFiles(Path.Combine(Path.GetTempPath(), "nullbox-override", Guid.NewGuid().ToString("N")));
        var store = new GmailCredentialStore(files, NullLogger<GmailCredentialStore>.Instance);

        Assert.Equal("http://fake.test/", new GmailClientFactory(store, baseUri: "http://fake.test").Create().BaseUri);
        Assert.Equal("https://gmail.googleapis.com/", new GmailClientFactory(store).Create().BaseUri);
    }
}

/// <summary>Polling and processing a message must never write its subject, snippet or body to the logs.</summary>
[Collection(AppCollection.Name)]
public sealed class NoMessageContentInLogsTests : IDisposable
{
    private const string Secret = "S3CR3T-CONTENT";
    private readonly LoggingFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Poll_and_pipeline_logs_carry_no_message_content()
    {
        _ = _factory.Server;
        var message = _factory.Gmail.Add("leak-1") with
        {
            Subject = $"{Secret} subject",
            Snippet = $"{Secret} snippet",
            Body = $"{Secret} body",
            From = $"{Secret} <sender@example.test>",
        };
        _factory.Gmail.Messages["leak-1"] = message;
        _factory.Gmail.InboxSearchResults.Add("leak-1");

        _factory.Services.GetRequiredService<IJobScheduler>().TriggerRecurring(InboxPollJob.JobId);
        var store = _factory.Services.GetRequiredService<IInboxStateStore>();
        for (var i = 0; i < 100 && !await store.IsProcessedAsync("leak-1"); i++)
        {
            await Task.Delay(100);
        }

        Assert.True(await store.IsProcessedAsync("leak-1"), "the message should have been processed");
        Assert.NotEmpty(_factory.Logs);
        Assert.DoesNotContain(_factory.Logs, line => line.Contains(Secret, StringComparison.Ordinal));
    }

    private sealed class LoggingFactory : NullboxAppFactory
    {
        public Nullbox.Tests.Inbox.FakeInboxGmail Gmail { get; } = new();

        public ConcurrentQueue<string> Logs { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Debug).AddProvider(new CapturingProvider(Logs)));
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IGmailService>(Gmail);
                services.AddSingleton<Nullbox.Core.Jev.IJevClient>(new Nullbox.Jev.Testing.FakeJevClient());
            });
        }
    }

    private sealed class CapturingProvider(ConcurrentQueue<string> logs) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(logs);

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger(ConcurrentQueue<string> logs) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => logs.Enqueue($"{formatter(state, exception)} {exception}");
    }
}

public sealed partial class DocsExamplesTests
{
    private static string RepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Nullbox.sln")))
            {
                return Path.Combine(dir.FullName, relative);
            }
        }

        throw new FileNotFoundException("repository root not found");
    }

    [Fact]
    public void Every_full_rule_set_example_in_the_rules_guide_is_valid()
    {
        var examples = JsonBlock().Matches(File.ReadAllText(RepoFile(Path.Combine("docs", "rules.md"))))
            .Select(m => m.Groups["json"].Value)
            .Where(json => json.Contains("\"questions\"", StringComparison.Ordinal))
            .ToList();

        Assert.True(examples.Count >= 2, $"expected rule-set examples, found {examples.Count}");
        foreach (var json in examples)
        {
            Assert.NotNull(Nullbox.Core.Rules.RuleJson.Parse(json, out var errors));
            Assert.Empty(errors);
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex("```json\\n(?<json>.*?)```", System.Text.RegularExpressions.RegexOptions.Singleline)]
    private static partial System.Text.RegularExpressions.Regex JsonBlock();
}
