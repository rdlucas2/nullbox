namespace Nullbox.Core.Cleanup;

/// <summary>Messages a rule matched (not counting ones an earlier rule took) and how many it changed.</summary>
public sealed record CleanupRuleResult(string Name, string Query, string Actions, int Matched, int Changed, IReadOnlyList<SenderCount> TopSenders);

public sealed record SenderCount(string Sender, int Count);

/// <summary>The outcome of one cleanup run; a dry run has every <see cref="CleanupRuleResult.Changed"/> at zero.</summary>
public sealed record CleanupResult(string Label, bool DryRun, IReadOnlyList<CleanupRuleResult> Rules)
{
    public int Matched => Rules.Sum(r => r.Matched);

    public int Changed => Rules.Sum(r => r.Changed);
}

/// <summary>A cleanup rule file found by <see cref="ICleanupConfigStore"/>: parsed, or the reason it could not be.</summary>
public sealed record CleanupConfigEntry(string Id, CleanupConfig? Config, string? Error);

/// <summary>Where cleanup rule files live; ids are file names without <c>.json</c>.</summary>
public interface ICleanupConfigStore
{
    IReadOnlyList<CleanupConfigEntry> List();

    /// <summary>Null when there is no such file (or the id is not a valid file name).</summary>
    CleanupConfigEntry? Get(string id);

    /// <summary>Creates or replaces a rule file after checking it parses.</summary>
    /// <exception cref="CleanupConfigException">The JSON is not a valid cleanup file.</exception>
    /// <exception cref="ArgumentException">The id is not a plain file name.</exception>
    CleanupConfigEntry Save(string id, string json);
}

public enum CleanupRunStatus
{
    Queued,
    Running,
    Completed,
    Failed,

    /// <summary>Refused before touching Gmail, e.g. the kill switch was on for an apply.</summary>
    Blocked,
}

/// <summary>One requested cleanup run and, once finished, its per-rule results.</summary>
public sealed record CleanupRun(
    long Id,
    string ConfigId,
    bool DryRun,
    int SampleSize,
    CleanupRunStatus Status,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? FinishedUtc,
    string? Label,
    int Matched,
    int Changed,
    IReadOnlyList<CleanupRuleResult> Rules,
    string? Error);

/// <summary>Cleanup runs, kept for the UI (M3-17).</summary>
public interface ICleanupRunLog
{
    Task<CleanupRun> QueueAsync(string configId, bool dryRun, int sampleSize, CancellationToken cancellationToken = default);

    Task MarkRunningAsync(long id, CancellationToken cancellationToken = default);

    Task CompleteAsync(long id, CleanupResult result, CancellationToken cancellationToken = default);

    /// <summary>Records <see cref="CleanupRunStatus.Failed"/> or <see cref="CleanupRunStatus.Blocked"/> with a reason (never message content).</summary>
    Task FinishWithoutResultAsync(long id, CleanupRunStatus status, string error, CancellationToken cancellationToken = default);

    Task<CleanupRun?> GetAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<CleanupRun>> ListAsync(int limit = 20, CancellationToken cancellationToken = default);
}
