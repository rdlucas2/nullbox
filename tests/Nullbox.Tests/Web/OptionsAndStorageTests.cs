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
    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "nullbox-tests", Guid.NewGuid().ToString("N"));

    protected override IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>(base.Settings)
    {
        ["Nullbox:Hangfire:Storage"] = "SQLite",
        ["Nullbox:DataDirectory"] = DataDirectory,
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

    public void Dispose()
    {
        _factory.Dispose();
        try
        {
            Directory.Delete(_factory.DataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: SQLite may still hold the file briefly on some platforms.
        }
    }
}
