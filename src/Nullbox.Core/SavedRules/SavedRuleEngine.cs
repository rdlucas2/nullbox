using System.Text.Json.Nodes;
using Nullbox.Core.Gmail;
using Nullbox.Core.Pipeline;
using Nullbox.Core.Rules;

namespace Nullbox.Core.SavedRules;

/// <summary>The parts of an email a saved rule looks at.</summary>
public sealed record SavedRuleInput(string? From, string? To, string? Cc, string? Subject)
{
    public static SavedRuleInput Of(GmailMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new(message.From, message.To, message.Cc, message.Subject);
    }

    /// <summary>What the audit record keeps: the headers the rule matched on.</summary>
    public JsonObject ToJson() => new()
    {
        ["from"] = From,
        ["to"] = To,
        ["cc"] = Cc,
        ["subject"] = Subject,
    };
}

/// <summary>Matches saved rules against an email and turns a match into a decision for <see cref="ActionPlan"/>.</summary>
public static class SavedRuleEngine
{
    /// <summary>
    /// Whether every part of <paramref name="rule"/>'s match holds: some sender address matches <c>from</c>, some To or
    /// Cc address matches <c>to</c>, and the subject contains <c>subject_contains</c> (all ignoring case). A rule with
    /// none of them never matches. <see cref="SavedRule.Enabled"/> is not checked here.
    /// </summary>
    public static bool Matches(SavedRule rule, SavedRuleInput input)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(input);

        if (string.IsNullOrWhiteSpace(rule.From) && string.IsNullOrWhiteSpace(rule.To))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(rule.From) && !AddressPattern.Extract(input.From).Any(a => AddressPattern.Matches(rule.From, a)))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(rule.To) && !AddressPattern.Extract(input.To, input.Cc).Any(a => AddressPattern.Matches(rule.To, a)))
        {
            return false;
        }

        return string.IsNullOrEmpty(rule.SubjectContains)
            || (input.Subject?.Contains(rule.SubjectContains.Trim(), StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>
    /// The rules the pipeline would run for <paramref name="input"/>, in order: enabled rules that match, up to and
    /// including the first live one with <see cref="SavedRule.Stop"/> on. <see cref="SavedRuleRun.Stopped"/> says whether one did.
    /// </summary>
    public static SavedRuleRun Run(IReadOnlyList<SavedRule> rules, SavedRuleInput input)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var matched = new List<(SavedRule Rule, int Index)>();
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            if (!rule.Enabled || !Matches(rule, input))
            {
                continue;
            }

            matched.Add((rule, i));
            if (Stops(rule))
            {
                return new SavedRuleRun(matched, true);
            }
        }

        return new SavedRuleRun(matched, false);
    }

    /// <summary>A live rule with stop on settles the message; a dry run never changes what runs after it.</summary>
    public static bool Stops(SavedRule rule) => rule.Stop && rule.Mode == RuleSetMode.Live;

    /// <summary>The rule as a decision, so the delete lock and protected-mail rules of <see cref="ActionPlan"/> apply to it.</summary>
    public static Decision ToDecision(SavedRule rule, int index)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return new Decision(
            index,
            rule.Name,
            [.. rule.Actions.Select(RuleAction.Parse)],
            rule.AllowProtected,
            rule.Remove,
            rule.Remove is null ? null : rule.ReviewLabel);
    }
}

/// <summary>The saved rules that apply to one message, and whether one of them stops the rest.</summary>
public sealed record SavedRuleRun(IReadOnlyList<(SavedRule Rule, int Index)> Matched, bool Stopped);
