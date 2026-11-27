using System.Text.RegularExpressions;
using Nullbox.Core.Rules;

namespace Nullbox.Core.SavedRules;

/// <summary>Checks saved rules beyond what the JSON Schema can: addresses, actions and unique names.</summary>
public static partial class SavedRuleValidator
{
    public const int MaxRules = 500;

    public const int MaxSubjectLength = 200;

    public static IReadOnlyList<RuleSetError> Validate(SavedRule rule, string path = "")
    {
        ArgumentNullException.ThrowIfNull(rule);
        var errors = new List<RuleSetError>();

        if (!NameRegex().IsMatch(rule.Name ?? string.Empty))
        {
            errors.Add(new($"{path}/name", "Use lowercase letters, digits, - and _ (up to 64), starting with a letter or digit."));
        }

        var hasFrom = !string.IsNullOrWhiteSpace(rule.From);
        var hasTo = !string.IsNullOrWhiteSpace(rule.To);
        if (!hasFrom && !hasTo)
        {
            errors.Add(new($"{path}/from", "A saved rule needs a sender (from), a recipient (to), or both."));
        }

        if (hasFrom && AddressPattern.Problem(rule.From) is { } fromProblem)
        {
            errors.Add(new($"{path}/from", fromProblem));
        }

        if (hasTo && AddressPattern.Problem(rule.To) is { } toProblem)
        {
            errors.Add(new($"{path}/to", toProblem));
        }

        if (rule.SubjectContains is { } subject && (subject.Trim().Length == 0 || subject.Length > MaxSubjectLength))
        {
            errors.Add(new($"{path}/subject_contains", $"Give 1 to {MaxSubjectLength} characters, or leave it out."));
        }

        for (var i = 0; i < rule.Actions.Count; i++)
        {
            if (!RuleAction.TryParse(rule.Actions[i], out var action, out var error))
            {
                errors.Add(new($"{path}/actions/{i}", error!.Replace("on the decision", "on the rule", StringComparison.Ordinal)));
            }
            else if (action.Kind == RuleActionKind.None)
            {
                errors.Add(new($"{path}/actions/{i}", "none is for rule sets; a saved rule that should change nothing can be turned off."));
            }
        }

        if (rule.Actions.Select(a => a.Trim()).Distinct(StringComparer.Ordinal).Count() != rule.Actions.Count)
        {
            errors.Add(new($"{path}/actions", "Each action can only be listed once."));
        }

        if (rule.Remove is { } remove && remove != RemoveMode.Archive && remove != RemoveMode.Delete)
        {
            errors.Add(new($"{path}/remove", "remove is archive or delete; use the rule's mode for a dry run."));
        }

        if (rule.Actions.Count == 0 && rule.Remove is null)
        {
            errors.Add(new($"{path}/actions", "A saved rule needs at least one action or a remove switch."));
        }

        return errors;
    }

    public static IReadOnlyList<RuleSetError> Validate(SavedRulesDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var errors = new List<RuleSetError>();
        if (document.Rules.Count > MaxRules)
        {
            errors.Add(new("/rules", $"At most {MaxRules} saved rules."));
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < document.Rules.Count; i++)
        {
            var rule = document.Rules[i];
            errors.AddRange(Validate(rule, $"/rules/{i}"));
            if (!names.Add(rule.Name))
            {
                errors.Add(new($"/rules/{i}/name", $"Another saved rule is already called '{rule.Name}'."));
            }
        }

        return errors;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$")]
    private static partial Regex NameRegex();
}
