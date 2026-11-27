using System.Text.Json.Serialization;
using Nullbox.Core.Rules;

namespace Nullbox.Core.Pipeline;

/// <summary>What happened when a rule set was applied to a message.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DecisionOutcome>))]
public enum DecisionOutcome
{
    /// <summary>The rule set is a dry run: decisions recorded, Gmail untouched.</summary>
    [JsonStringEnumMemberName("dry_run")]
    DryRun,

    /// <summary>Live, and the actions were applied.</summary>
    [JsonStringEnumMemberName("applied")]
    Applied,

    /// <summary>Live, but nothing to do (no match, <c>none</c>, or every action held back).</summary>
    [JsonStringEnumMemberName("no_change")]
    NoChange,

    /// <summary>Live, but the global kill switch was on, so nothing was applied.</summary>
    [JsonStringEnumMemberName("kill_switch")]
    KillSwitch,

    /// <summary>Live, but the hourly action cap was reached, so nothing was applied.</summary>
    [JsonStringEnumMemberName("cap_reached")]
    CapReached,

    /// <summary>Asking Jev or changing Gmail failed; <see cref="DecisionRecord.Error"/> says why. The message is retried.</summary>
    [JsonStringEnumMemberName("error")]
    Error,

    /// <summary>
    /// An earlier <see cref="Applied"/> record was undone from the audit page. <see cref="DecisionRecord.Actions"/> lists
    /// the steps taken; the record keeps the undone decision's rule-set key and labels, so it can't be undone twice.
    /// </summary>
    [JsonStringEnumMemberName("undone")]
    Undone,
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
    DateTimeOffset At)
{
    /// <summary>Row id, newest highest; 0 until stored. Used as the paging cursor.</summary>
    public long Id { get; init; }

    /// <summary>SHA-256 of the full state sent to Jev, so identical inputs can be spotted without keeping them.</summary>
    public string? InputsHash { get; init; }

    /// <summary>The state sent to Jev as JSON, with the body cut to <see cref="Options.AuditSettings.StoredBodyChars"/>.</summary>
    public string? StateJson { get; init; }

    /// <summary>The message's Gmail labels when it was evaluated (replay needs them to know protected mail).</summary>
    public IReadOnlyList<string> Labels { get; init; } = [];

    /// <summary>Jev's least sure answer (see <see cref="AnswerConfidence"/>); null when there were no answers.</summary>
    public double? Confidence { get; init; }

    /// <summary>What went wrong, for <see cref="DecisionOutcome.Error"/>.</summary>
    public string? Error { get; init; }
}

/// <summary>Filters for <see cref="IDecisionLog.QueryAsync"/>; every filter is optional and they combine with AND.</summary>
public sealed record DecisionQuery
{
    public string? MessageId { get; init; }

    /// <summary>Rule-set name, e.g. <c>promo-cleanup</c>.</summary>
    public string? RuleSet { get; init; }

    /// <summary>Rule-set version; only used with <see cref="RuleSet"/>.</summary>
    public int? Version { get; init; }

    /// <summary>
    /// A recorded action starts with this, e.g. <c>archive</c>, <c>trash</c>, <c>mark_read</c>, <c>label</c> or
    /// <c>label:nullbox/promo</c>.
    /// </summary>
    public string? Action { get; init; }

    public RuleSetMode? Mode { get; init; }

    public DecisionOutcome? Outcome { get; init; }

    /// <summary>Lowest <see cref="DecisionRecord.Confidence"/> to include (inclusive).</summary>
    public double? MinConfidence { get; init; }

    /// <summary>Highest <see cref="DecisionRecord.Confidence"/> to include (inclusive).</summary>
    public double? MaxConfidence { get; init; }

    /// <summary>Only records older than this id: the <see cref="DecisionPage.Next"/> of the previous page.</summary>
    public long? Before { get; init; }

    public int Limit { get; init; } = 50;
}

/// <summary>A page of records, newest first. <see cref="Next"/> is the cursor for the next page, or null at the end.</summary>
public sealed record DecisionPage(IReadOnlyList<DecisionRecord> Items, long? Next);

/// <summary>A message that failed for good and will not be retried.</summary>
public sealed record PoisonMessage(string MessageId, int Attempts, string Error, DateTimeOffset At);

/// <summary>Recorded decisions, failure counts and poison messages.</summary>
public interface IDecisionLog
{
    Task RecordAsync(DecisionRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether this rule-set version already has a verdict on the message (so a retry doesn't redo it).
    /// <see cref="DecisionOutcome.Error"/> records don't count.
    /// </summary>
    Task<bool> HasRecordAsync(string messageId, string ruleSetKey, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DecisionRecord>> ListAsync(string? messageId = null, int limit = 50, CancellationToken cancellationToken = default);

    /// <summary>Records matching <paramref name="query"/>, newest first, one page at a time.</summary>
    Task<DecisionPage> QueryAsync(DecisionQuery query, CancellationToken cancellationToken = default);

    /// <summary>Deletes records older than <paramref name="before"/> and returns how many went.</summary>
    Task<int> PurgeAsync(DateTimeOffset before, CancellationToken cancellationToken = default);

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
