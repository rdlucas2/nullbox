using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Nullbox.Tests.Infrastructure;

/// <summary>
/// Hosts the real app with in-memory Hangfire storage and a known admin password.
/// Hangfire keeps some global state, so every test that hosts the app joins <see cref="AppCollection"/>.
/// </summary>
public class NullboxAppFactory : WebApplicationFactory<Program>
{
    public const string AdminPassword = "test-admin-password";

    /// <summary>Per-factory temp data directory (data-protection keys, SQLite files), deleted on dispose.</summary>
    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "nullbox-tests", Guid.NewGuid().ToString("N"));

    protected virtual IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["Nullbox:Hangfire:Storage"] = "InMemory",
        ["Nullbox:AdminPassword"] = AdminPassword,
        ["Nullbox:DataDirectory"] = DataDirectory,
        // Far-future schedule so only explicit triggers run the heartbeat during tests.
        ["Nullbox:Jobs:HeartbeatCron"] = "0 0 1 1 *",
        ["Nullbox:Jobs:InboxPollCron"] = "0 0 1 1 *",
        // The suite signs in hundreds of times from one address; LoginRateLimitTests checks the real limit.
        ["Nullbox:Login:AttemptsPerWindow"] = "100000",
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        foreach (var (key, value) in Settings)
        {
            builder.UseSetting(key, value);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            // Hangfire keeps one process-wide log provider bound to the last host's logger factory; once that host is
            // gone, anything Hangfire creates (e.g. AutomaticRetryAttribute) would hit a disposed factory. Fall back
            // to Hangfire's default provider instead.
            Hangfire.Logging.LogProvider.SetCurrentLogProvider(null);
            try
            {
                Directory.Delete(DataDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: SQLite may still hold its file briefly.
            }
            catch (UnauthorizedAccessException)
            {
                // Best effort.
            }
        }
    }

    public HttpClient CreateNonRedirectingClient()
        => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AppCollection : ICollectionFixture<NullboxAppFactory>
{
    public const string Name = "App";
}
