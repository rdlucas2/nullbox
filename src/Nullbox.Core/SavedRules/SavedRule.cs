using System.Text.Json.Serialization;
using Nullbox.Core.Rules;

namespace Nullbox.Core.SavedRules;

/// <summary>
/// A fixed rule that acts without asking Jev: a match on the sender, the recipient (To or Cc) and optionally the
/// subject, and what to do with matching mail. Saved rules run in order before the Jev rule sets; a live rule with
/// <see cref="Stop"/> on settles the message, so the rule sets (and Jev) are skipped for it.
/// </summary>
public sealed record SavedRule
{
    /// <summary>Identity, like a rule set's: lowercase letters, digits, <c>-</c> and <c>_</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Off rules are kept but never run.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary><c>dry_run</c> (the default for new rules) only records matches; <c>live</c> applies them.</summary>
    public RuleSetMode Mode { get; init; } = RuleSetMode.DryRun;

    /// <summary>The sender: an address (<c>deals@shop.example</c>) or a whole domain (<c>@shop.example</c>).</summary>
    public string? From { get; init; }

    /// <summary>A recipient in To or Cc, written like <see cref="From"/>. Plus-addresses are matched as described in <see cref="AddressPattern"/>.</summary>
    public string? To { get; init; }

    /// <summary>Text the subject must contain (case-insensitive).</summary>
    public string? SubjectContains { get; init; }

    /// <summary><c>label:&lt;name&gt;</c>, <c>mark_read</c> and <c>star</c>, as in a rule set's decisions.</summary>
    public IReadOnlyList<string> Actions { get; init; } = [];

    /// <summary>Take matching mail out of the inbox: <c>archive</c> (with the review label) or <c>delete</c> (Trash).</summary>
    public RemoveMode? Remove { get; init; }

    /// <summary>When the rule is live and matches, skip the later saved rules and the Jev rule sets. On by default.</summary>
    public bool Stop { get; init; } = true;

    /// <summary>Allow mark read and removal on starred or important mail.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AllowProtected { get; init; }

    /// <summary>The audit log's rule key for this rule, <c>saved:&lt;name&gt;</c>.</summary>
    [JsonIgnore]
    public string Key => KeyOf(Name);

    /// <summary>The label put on everything the rule takes out of the inbox.</summary>
    [JsonIgnore]
    public string ReviewLabel => $"{ReviewLabels.Root}/saved-rules/{Name}";

    public const string KeyPrefix = "saved:";

    public static string KeyOf(string name) => KeyPrefix + name;

    /// <summary>The rule's name when <paramref name="key"/> is a saved rule's audit key.</summary>
    public static bool TryParseKey(string? key, out string name)
    {
        name = key is not null && key.StartsWith(KeyPrefix, StringComparison.Ordinal) ? key[KeyPrefix.Length..] : string.Empty;
        return name.Length > 0;
    }
}

/// <summary>Every saved rule, in the order they run; the shape of an export and an import.</summary>
public sealed record SavedRulesDocument
{
    [JsonPropertyName("$schema")]
    public string? Schema { get; init; }

    public IReadOnlyList<SavedRule> Rules { get; init; } = [];
}
