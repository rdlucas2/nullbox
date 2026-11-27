using Microsoft.Extensions.Configuration;
using Nullbox.Core.Options;
using Nullbox.Tests.Infrastructure;
using Nullbox.Web.Hosting;

namespace Nullbox.Tests.Web;

public sealed class NullboxOptionsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    [Fact]
    public void Container_env_vars_fill_unset_options()
    {
        var options = new NullboxOptions();

        NullboxHosting.ApplyEnvironmentFallbacks(options, Config(("ADMIN_PASSWORD", "from-env"), ("NULLBOX_DATA_DIR", "/data")));

        Assert.Equal("from-env", options.AdminPassword);
        Assert.Equal("/data", options.DataDirectory);
    }

    [Fact]
    public void Explicit_nullbox_keys_win_over_env_vars()
    {
        var options = new NullboxOptions { AdminPassword = "explicit", DataDirectory = "/custom" };

        NullboxHosting.ApplyEnvironmentFallbacks(options, Config(
            ("Nullbox:AdminPassword", "explicit"),
            ("Nullbox:DataDirectory", "/custom"),
            ("ADMIN_PASSWORD", "from-env"),
            ("NULLBOX_DATA_DIR", "/data")));

        Assert.Equal("explicit", options.AdminPassword);
        Assert.Equal("/custom", options.DataDirectory);
    }

    [Fact]
    public void ResolveDataDirectory_applies_the_same_precedence_as_options()
    {
        Assert.Equal(Path.GetFullPath("/data"), NullboxHosting.ResolveDataDirectory(Config(("NULLBOX_DATA_DIR", "/data"))));
        Assert.Equal(
            Path.GetFullPath("/custom"),
            NullboxHosting.ResolveDataDirectory(Config(("Nullbox:DataDirectory", "/custom"), ("NULLBOX_DATA_DIR", "/data"))));
    }

    [Fact]
    public void Defaults_are_safe()
    {
        var options = new NullboxOptions();

        NullboxHosting.ApplyEnvironmentFallbacks(options, Config());

        Assert.Null(options.AdminPassword);
        Assert.Equal("data", options.DataDirectory);
        Assert.Equal(HangfireStorageKind.SQLite, options.Hangfire.Storage);
        Assert.Equal("/hangfire", options.Hangfire.DashboardPath);
    }
}

/// <summary>Hosts the app with the default SQLite storage in a temp data directory.</summary>
public sealed class SqliteFactory : NullboxAppFactory
{
    protected override IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>(base.Settings)
    {
        ["Nullbox:Hangfire:Storage"] = "SQLite",
    };
}

[Collection(AppCollection.Name)]
public sealed class SqliteStorageTests : IDisposable
{
    private readonly SqliteFactory _factory = new();

    [Fact]
    public async Task SQLite_storage_creates_its_database_in_the_data_directory()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/healthz");

        response.EnsureSuccessStatusCode();
        Assert.True(File.Exists(Path.Combine(_factory.DataDirectory, "hangfire.db")));
    }

    [Fact]
    public async Task Data_protection_keys_are_persisted_in_the_data_directory()
    {
        using var client = _factory.CreateClient();
        await client.GetStringAsync("/login"); // issuing an antiforgery token creates the key ring

        Assert.NotEmpty(Directory.GetFiles(Path.Combine(_factory.DataDirectory, "keys"), "*.xml"));
    }

    public void Dispose() => _factory.Dispose();
}
