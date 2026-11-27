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

    public PipelineSettings Pipeline { get; set; } = new();

    public AuditSettings Audit { get; set; } = new();
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

    /// <summary>Cron for the inbox poll (UTC).</summary>
    public string InboxPollCron { get; set; } = "*/5 * * * *";

    /// <summary>
    /// Gmail <c>newer_than:</c> window used on first run and when history has expired, e.g. <c>2d</c>.
    /// Already-processed messages in that window are skipped.
    /// </summary>
    public string InboxResyncWindow { get; set; } = "2d";

    /// <summary>How many messages are processed at once (workers on the inbox queue).</summary>
    public int MessageWorkers { get; set; } = 2;
}

/// <summary>Safety limits for applying rule-set decisions to Gmail.</summary>
public sealed class PipelineSettings
{
    /// <summary>
    /// When true, no rule set changes Gmail (live rule sets record what they would have done). Can also be switched
    /// on at runtime; either one stops changes.
    /// </summary>
    public bool KillSwitch { get; set; }

    /// <summary>At most this many messages are changed in any rolling hour; further live decisions are only recorded. 0 means no changes at all.</summary>
    public int MaxMessagesPerHour { get; set; } = 100;

    /// <summary>Failed attempts before a message is recorded as poison and no longer retried.</summary>
    public int MaxAttempts { get; set; } = 4;

    /// <summary>How long to wait before retrying a message when Gmail or Jev needs re-authorizing.</summary>
    public TimeSpan AuthRetryDelay { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>How long decision records are kept, and how much of each email they keep.</summary>
public sealed class AuditSettings
{
    /// <summary>Decision records older than this many days are deleted by the purge job. 0 or less keeps them forever.</summary>
    public int RetentionDays { get; set; } = 90;

    /// <summary>Cron for the purge job (UTC).</summary>
    public string PurgeCron { get; set; } = "17 3 * * *";

    /// <summary>
    /// The body sent to Jev is cut to this many characters before it is stored (0 stores no body). Full bodies are
    /// never kept; the inputs hash still covers the whole state.
    /// </summary>
    public int StoredBodyChars { get; set; } = 280;
}
