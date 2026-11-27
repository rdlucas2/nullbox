namespace Nullbox.Core.Settings;

/// <summary>Small settings changed from the UI at runtime and kept across restarts (the <c>app_settings</c> table).</summary>
public interface IAppSettingsStore
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Stores <paramref name="value"/>, or removes the key when it is null.</summary>
    Task SetAsync(string key, string? value, CancellationToken cancellationToken = default);
}

public static class AppSettingKeys
{
    /// <summary>Cron for the inbox poll set on the Jobs page; overrides <c>Nullbox:Jobs:InboxPollCron</c>.</summary>
    public const string InboxPollCron = "jobs.inbox_poll_cron";
}
