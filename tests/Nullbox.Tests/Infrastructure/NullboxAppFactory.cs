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

    protected virtual IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["Nullbox:Hangfire:Storage"] = "InMemory",
        ["Nullbox:AdminPassword"] = AdminPassword,
        // Far-future schedule so only explicit triggers run the heartbeat during tests.
        ["Nullbox:Jobs:HeartbeatCron"] = "0 0 1 1 *",
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        foreach (var (key, value) in Settings)
        {
            builder.UseSetting(key, value);
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
