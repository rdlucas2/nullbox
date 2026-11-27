namespace Nullbox.Core.Options;

/// <summary>Root application settings, bound from the <c>Nullbox</c> configuration section.</summary>
public sealed class NullboxOptions
{
    public const string SectionName = "Nullbox";

    /// <summary>Directory for the SQLite databases and Google token files. <c>/data</c> in the container.</summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>Password for the admin login that gates the UI and the Hangfire dashboard. Empty disables login.</summary>
    public string? AdminPassword { get; set; }

    public HangfireSettings Hangfire { get; set; } = new();

    public JobSettings Jobs { get; set; } = new();
}

public enum HangfireStorageKind
{
    /// <summary>SQLite file under <see cref="NullboxOptions.DataDirectory"/> (default).</summary>
    SQLite,

    /// <summary>Process-local storage, for tests and throwaway runs.</summary>
    InMemory,
}

public sealed class HangfireSettings
{
    public HangfireStorageKind Storage { get; set; } = HangfireStorageKind.SQLite;

    /// <summary>File name of the Hangfire SQLite database inside the data directory.</summary>
    public string SqliteFileName { get; set; } = "hangfire.db";

    public int WorkerCount { get; set; } = 2;

    public string DashboardPath { get; set; } = "/hangfire";
}

public sealed class JobSettings
{
    /// <summary>Cron for the no-op heartbeat job that proves the scheduler is alive.</summary>
    public string HeartbeatCron { get; set; } = "*/5 * * * *";
}
