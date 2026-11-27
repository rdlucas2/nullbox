using Nullbox.Core.Rules;

namespace Nullbox.Core.Pipeline;

/// <summary>What happened when a rule set was applied to a message.</summary>
public enum DecisionOutcome
{
    /// <summary>The rule set is a dry run: decisions recorded, Gmail untouched.</summary>
    DryRun,

    /// <summary>Live, and the actions were applied.</summary>
    Applied,

    /// <summary>Live, but nothing to do (no match, <c>none</c>, or every action held back).</summary>
    NoChange,

    /// <summary>Live, but the global kill switch was on, so nothing was applied.</summary>
    KillSwitch,

    /// <summary>Live, but the hourly action cap was reached, so nothing was applied.</summary>
    CapReached,
}

/// <summary>
/// One rule set's verdict on one message: the rule set and version (<c>promo-cleanup@v3</c>), the matched decisions in
/// priority order, the actions applied (or that would have been, for a dry run, kill switch or cap), matched actions
/// held back with the reason, and Jev's answers as JSON.
/// </summary>
public sealed record DecisionRecord(
    string MessageId,
    string RuleSetKey,
    RuleSetMode Mode,
    DecisionOutcome Outcome,
    IReadOnlyList<string> Decisions,
    IReadOnlyList<string> Actions,
    IReadOnlyList<string> Skipped,
    string Model,
    string AnswersJson,
    DateTimeOffset At);

/// <summary>A message that failed for good and will not be retried.</summary>
public sealed record PoisonMessage(string MessageId, int Attempts, string Error, DateTimeOffset At);

/// <summary>Recorded decisions, failure counts and poison messages.</summary>
public interface IDecisionLog
{
    Task RecordAsync(DecisionRecord record, CancellationToken cancellationToken = default);

    /// <summary>Whether this rule-set version already has a verdict on the message (so a retry doesn't redo it).</summary>
    Task<bool> HasRecordAsync(string messageId, string ruleSetKey, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DecisionRecord>> ListAsync(string? messageId = null, int limit = 50, CancellationToken cancellationToken = default);

    /// <summary>Messages changed by <see cref="DecisionOutcome.Applied"/> records at or after <paramref name="since"/>.</summary>
    Task<int> CountAppliedSinceAsync(DateTimeOffset since, CancellationToken cancellationToken = default);

    /// <summary>Counts a failed attempt and returns the attempts so far.</summary>
    Task<int> RecordFailureAsync(string messageId, string error, DateTimeOffset at, CancellationToken cancellationToken = default);

    Task ClearFailuresAsync(string messageId, CancellationToken cancellationToken = default);

    Task MarkPoisonAsync(string messageId, string error, DateTimeOffset at, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PoisonMessage>> ListPoisonAsync(int limit = 50, CancellationToken cancellationToken = default);
}

/// <summary>The global kill switch, persisted so it survives restarts.</summary>
public interface IPipelineSettingsStore
{
    Task<bool> GetKillSwitchAsync(CancellationToken cancellationToken = default);

    Task SetKillSwitchAsync(bool on, CancellationToken cancellationToken = default);
}
