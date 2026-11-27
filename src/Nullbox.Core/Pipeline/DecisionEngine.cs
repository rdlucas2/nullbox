using Nullbox.Core.Jev;
using Nullbox.Core.Rules;

namespace Nullbox.Core.Pipeline;

/// <summary>
/// A decision whose condition matched, with its parsed actions, its Archive | Delete switch and, when it removes mail,
/// its review label. <see cref="Index"/> is its position in the rule set's <c>decisions</c> list, which is also its
/// priority (lower comes first).
/// </summary>
public sealed record Decision(
    int Index,
    string? Name,
    IReadOnlyList<RuleAction> Actions,
    bool AllowProtected,
    RemoveMode? Remove = null,
    string? ReviewLabel = null)
{
    /// <summary>The decision's name, or <c>#index</c> when it has none.</summary>
    public string Label => string.IsNullOrWhiteSpace(Name) ? $"#{Index}" : Name;
}

/// <summary>
/// Maps Jev answers to decisions. Pure and deterministic: the same rule set and answers always give the same
/// decisions in the same order, and nothing outside its arguments is read.
/// </summary>
public static class DecisionEngine
{
    /// <summary>
    /// Matching decisions in priority order (the order they are listed). With <see cref="DecisionMatch.First"/> only
    /// the first match is returned, plus any dry-run rules that matched before it (they are recorded but don't count as
    /// the first match); with <see cref="DecisionMatch.All"/> every match is. A condition that depends on a
    /// missing answer never matches (see <see cref="Condition"/>), so missing outputs can't trigger an action.
    /// </summary>
    public static IReadOnlyList<Decision> Evaluate(RuleSetDocument ruleSet, JevResult result)
    {
        ArgumentNullException.ThrowIfNull(ruleSet);
        ArgumentNullException.ThrowIfNull(result);

        var matched = new List<Decision>();
        for (var i = 0; i < ruleSet.Decisions.Count; i++)
        {
            var rule = ruleSet.Decisions[i];
            if (!ConditionParser.Parse(rule.When).Matches(result))
            {
                continue;
            }

            matched.Add(new Decision(
                i,
                rule.Name,
                [.. rule.Actions.Select(RuleAction.Parse)],
                rule.AllowProtected,
                rule.Remove,
                rule.Remove is null or RemoveMode.DryRun ? null : ReviewLabels.For(ruleSet.Name, rule, i)));
            // A dry-run rule is on trial: it is recorded but never takes the first match, so adding one can't stop the
            // rules below it from acting.
            if (ruleSet.Match == DecisionMatch.First && rule.Remove != RemoveMode.DryRun)
            {
                break;
            }
        }

        return matched;
    }
}
