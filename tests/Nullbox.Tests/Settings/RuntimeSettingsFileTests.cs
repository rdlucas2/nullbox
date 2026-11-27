using Microsoft.Extensions.Configuration;
using Nullbox.Web.Settings;

namespace Nullbox.Tests.Settings;

public sealed class RuntimeSettingsFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nullbox-settings-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private RuntimeSettingsFile File() => new(RuntimeSettingsFile.In(_dir));

    [Fact]
    public void A_missing_file_loads_as_empty()
    {
        Assert.Empty(File().Load());
    }

    [Fact]
    public void Saved_values_load_back_and_feed_configuration_by_their_keys()
    {
        var file = File();

        file.Save(new Dictionary<string, string?>
        {
            [RuntimeSettingsFile.AllowDelete] = "true",
            [RuntimeSettingsFile.AuditRetentionDays] = "30",
            [RuntimeSettingsFile.InboxPollCron] = " */10 * * * * ",
        });

        var loaded = file.Load();
        Assert.Equal("true", loaded[RuntimeSettingsFile.AllowDelete]);
        Assert.Equal("30", loaded[RuntimeSettingsFile.AuditRetentionDays]);
        Assert.Equal("*/10 * * * *", loaded[RuntimeSettingsFile.InboxPollCron]);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [RuntimeSettingsFile.AllowDelete] = "false" })
            .AddJsonFile(file.Path, optional: true)
            .Build();
        Assert.True(configuration.GetValue<bool>(RuntimeSettingsFile.AllowDelete));
        Assert.Equal("30", configuration[RuntimeSettingsFile.AuditRetentionDays]);
    }

    [Fact]
    public void A_null_value_clears_the_key_and_keeps_the_others()
    {
        var file = File();
        file.Save(new Dictionary<string, string?> { [RuntimeSettingsFile.AllowDelete] = "true", [RuntimeSettingsFile.JevMaxRequestsPerHour] = "60" });

        file.Save(new Dictionary<string, string?> { [RuntimeSettingsFile.AllowDelete] = null });

        var loaded = file.Load();
        Assert.False(loaded.ContainsKey(RuntimeSettingsFile.AllowDelete));
        Assert.Equal("60", loaded[RuntimeSettingsFile.JevMaxRequestsPerHour]);
    }

    [Theory]
    [InlineData(RuntimeSettingsFile.AllowDelete, "yes")]
    [InlineData(RuntimeSettingsFile.JevMaxRequestsPerHour, "0")]
    [InlineData(RuntimeSettingsFile.AuditRetentionDays, "4000")]
    [InlineData(RuntimeSettingsFile.AuditRetentionDays, "1.5")]
    [InlineData(RuntimeSettingsFile.InboxPollCron, "every five minutes")]
    [InlineData(RuntimeSettingsFile.InboxPollCron, "* * *")]
    [InlineData("Nullbox:Gmail:ClientSecret", "x")]
    public void Bad_values_and_unknown_keys_are_refused_and_nothing_is_written(string key, string value)
    {
        var file = File();
        file.Save(new Dictionary<string, string?> { [RuntimeSettingsFile.AllowFilterSync] = "true" });

        Assert.NotNull(RuntimeSettingsFile.Check(key, value));
        var error = Assert.Throws<ArgumentException>(() => file.Save(new Dictionary<string, string?>
        {
            [RuntimeSettingsFile.AllowDelete] = "true",
            [key] = value,
        }));

        Assert.Contains(key, error.Message, StringComparison.Ordinal);
        Assert.Equal(new Dictionary<string, string> { [RuntimeSettingsFile.AllowFilterSync] = "true" }, file.Load());
    }

    [Theory]
    [InlineData(RuntimeSettingsFile.AllowPermanentDelete, "false")]
    [InlineData(RuntimeSettingsFile.JevMaxInputTokensPerDay, "0")]
    [InlineData(RuntimeSettingsFile.InboxPollCron, "0 */2 * * * *")]
    public void Good_values_pass_the_check(string key, string value)
    {
        Assert.Null(RuntimeSettingsFile.Check(key, value));
    }
}
