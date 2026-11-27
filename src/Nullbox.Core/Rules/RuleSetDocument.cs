using System.Text.Json.Serialization;
using Nullbox.Core.Jev;

namespace Nullbox.Core.Rules;

/// <summary>
/// One rule set: which email fields Jev sees (<see cref="State"/>), the questions it is asked (in Jev's own request
/// shape, so the editor produces real Jev JSON), and ordered decisions mapping answers to Gmail actions.
/// Stored as immutable versions; see <see cref="IRuleSetStore"/>.
/// </summary>
public sealed record RuleSetDocument
{
    /// <summary>Optional pointer to the JSON Schema, for editors. Ignored by nullbox.</summary>
    [JsonPropertyName("$schema")]
    public string? Schema { get; init; }

    /// <summary>Identity of the rule set: lowercase letters, digits, <c>-</c> and <c>_</c>.</summary>
    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>
    /// Live applies decisions; dry run only records them. New rule sets are live: a rule is tried by letting it
    /// archive (see <see cref="DecisionRule.Remove"/>), not by watching a dry run.
    /// </summary>
    public RuleSetMode Mode { get; init; } = RuleSetMode.Live;

    /// <summary>Jev model or alias to ask.</summary>
    public string Model { get; init; } = JevRequest.DefaultModel;

    public StateTemplate State { get; init; } = new();

    /// <summary>Question name to question, exactly as sent to Jev.</summary>
    public IReadOnlyDictionary<string, JevQuestion> Questions { get; init; } = new Dictionary<string, JevQuestion>();

    /// <summary>Whether the first matching decision wins or every matching decision applies.</summary>
    public DecisionMatch Match { get; init; } = DecisionMatch.First;

    /// <summary>Evaluated in order.</summary>
    public IReadOnlyList<DecisionRule> Decisions { get; init; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter<RuleSetMode>))]
public enum RuleSetMode
{
    [JsonStringEnumMemberName("dry_run")]
    DryRun,

    [JsonStringEnumMemberName("live")]
    Live,
}

[JsonConverter(typeof(JsonStringEnumConverter<DecisionMatch>))]
public enum DecisionMatch
{
    [JsonStringEnumMemberName("first")]
    First,

    [JsonStringEnumMemberName("all")]
    All,
}

/// <summary>Which parts of the email go into Jev's <c>state</c>, in this order.</summary>
public sealed record StateTemplate
{
    public IReadOnlyList<string> Fields { get; init; } = [StateFields.From, StateFields.Subject, StateFields.Snippet];

    /// <summary>The body is cut to this many characters before it is sent.</summary>
    public int BodyMaxChars { get; init; } = 2000;
}

/// <summary>The email fields a state template can name.</summary>
public static class StateFields
{
    public const string From = "from";
    public const string Subject = "subject";
    public const string Snippet = "snippet";
    public const string Body = "body";
    public const string Labels = "labels";
    public const string AgeDays = "age_days";
    public const string HasUnsubscribe = "has_unsubscribe";
    public const string Date = "date";

    public static IReadOnlyList<string> All { get; } = [From, Subject, Snippet, Body, Labels, AgeDays, HasUnsubscribe, Date];
}

/// <summary>
/// <c>when</c> is a condition over answers, e.g. <c>category.choice == "promo" &amp;&amp; category.confidence &gt;= 0.8</c>
/// (see <see cref="ConditionParser"/>); <c>actions</c> are <see cref="RuleAction"/> strings like <c>label:promo</c>;
/// <c>remove</c> is the Archive | Delete switch.
/// </summary>
public sealed record DecisionRule
{
    /// <summary>Shown in the editor and audit log, and names the rule's review label (<see cref="ReviewLabels"/>).</summary>
    public string? Name { get; init; }

    public required string When { get; init; }

    public IReadOnlyList<string> Actions { get; init; } = [];

    /// <summary>
    /// Whether matching mail leaves the inbox: <see cref="RemoveMode.Archive"/> (with the review label, the default
    /// for new rules in the editor) or <see cref="RemoveMode.Delete"/> (Trash). Null leaves it in the inbox.
    /// </summary>
    public RemoveMode? Remove { get; init; }

    /// <summary>
    /// Starred and important messages are never marked read or removed from the inbox unless the matching decision
    /// sets this (labels and stars still apply). Off by default, and left out of the JSON when off.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AllowProtected { get; init; }
}

/// <summary>The Archive | Delete switch on a decision.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RemoveMode>))]
public enum RemoveMode
{
    /// <summary>Remove INBOX and add the rule's review label, so what it took can be checked in Gmail.</summary>
    [JsonStringEnumMemberName("archive")]
    Archive,

    /// <summary>
    /// Move to Trash (recoverable for 30 days; never a permanent delete). While the app-wide delete lock is on
    /// (<c>Nullbox:Gmail:AllowDelete</c>), this archives with the review label instead.
    /// </summary>
    [JsonStringEnumMemberName("delete")]
    Delete,
}
