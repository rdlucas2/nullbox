using System.Globalization;
using System.Text.RegularExpressions;
using Nullbox.Core.Jev;

namespace Nullbox.Core.Rules;

/// <summary>
/// Checks a deserialized rule set beyond the JSON Schema: names, and that every decision condition refers to a
/// question, field, option and value range that exists.
/// </summary>
public static partial class RuleSetValidator
{
    public static IReadOnlyList<RuleSetError> Validate(RuleSetDocument document)
    {
        var errors = new List<RuleSetError>();
        void Add(string path, string message) => errors.Add(new RuleSetError(path, message));

        if (string.IsNullOrEmpty(document.Name) || !NamePattern().IsMatch(document.Name))
        {
            Add("/name", "Use 1 to 64 lowercase letters, digits, - or _, starting with a letter or digit.");
        }

        if (string.IsNullOrWhiteSpace(document.Model))
        {
            Add("/model", "Name a Jev model, e.g. jev-latest.");
        }

        ValidateState(document.State, Add);

        if (document.Questions.Count == 0)
        {
            Add("/questions", "Add at least one question.");
        }

        foreach (var (name, question) in document.Questions)
        {
            ValidateQuestion($"/questions/{name}", name, question, Add);
        }

        var reviewLabels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < document.Decisions.Count; i++)
        {
            var decision = document.Decisions[i];
            ValidateDecision($"/decisions/{i}", decision, document.Questions, Add);

            // Each removing rule's mail must be reviewable on its own, so two rules can't share a review label.
            if (decision.Remove is not null && !string.IsNullOrEmpty(document.Name))
            {
                var label = ReviewLabels.For(document.Name, decision, i);
                if (reviewLabels.TryGetValue(label, out var first))
                {
                    Add($"/decisions/{i}/name", $"Decision {first} already uses the review label {label}; give this rule a different name.");
                }
                else
                {
                    reviewLabels[label] = i;
                }
            }
        }

        return errors;
    }

    private static void ValidateState(StateTemplate state, Action<string, string> add)
    {
        if (state.Fields.Count == 0)
        {
            add("/state/fields", "Send at least one email field.");
        }

        for (var i = 0; i < state.Fields.Count; i++)
        {
            if (!StateFields.All.Contains(state.Fields[i]))
            {
                add($"/state/fields/{i}", $"Unknown field '{state.Fields[i]}'. Use one of: {string.Join(", ", StateFields.All)}.");
            }
        }

        if (state.Fields.Distinct(StringComparer.Ordinal).Count() != state.Fields.Count)
        {
            add("/state/fields", "Each field can only be listed once.");
        }

        if (state.BodyMaxChars is < 1 or > 32000)
        {
            add("/state/body_max_chars", "Use 1 to 32000 characters.");
        }
    }

    private static void ValidateQuestion(string path, string name, JevQuestion question, Action<string, string> add)
    {
        if (!QuestionNamePattern().IsMatch(name))
        {
            add(path, "Question names use lowercase letters, digits and _, starting with a letter (at most 64).");
        }

        switch (question)
        {
            case JevChoiceQuestion choice when choice.Criteria.Count < 2:
                add($"{path}/criteria", "A choice needs at least two options.");
                break;
            case JevChoiceQuestion choice when choice.Criteria.Keys.Any(string.IsNullOrWhiteSpace):
                add($"{path}/criteria", "Option names cannot be blank.");
                break;
            case JevScoreQuestion score when score.Criteria.Count < 2:
                add($"{path}/criteria", "A score needs at least two levels.");
                break;
        }
    }

    private static void ValidateDecision(string path, DecisionRule rule, IReadOnlyDictionary<string, JevQuestion> questions, Action<string, string> add)
    {
        if (!ConditionParser.TryParse(rule.When ?? string.Empty, out var condition, out var syntax))
        {
            add($"{path}/when", syntax!.Message);
        }
        else
        {
            foreach (var comparison in condition!.Comparisons())
            {
                if (CheckComparison(comparison, questions) is { } problem)
                {
                    add($"{path}/when", $"{comparison.Path}: {problem}");
                }
            }
        }

        var actions = new List<RuleAction>();
        for (var i = 0; i < (rule.Actions?.Count ?? 0); i++)
        {
            if (RuleAction.TryParse(rule.Actions![i], out var action, out var error))
            {
                actions.Add(action);
            }
            else
            {
                add($"{path}/actions/{i}", error!);
            }
        }

        if ((rule.Actions is null || rule.Actions.Count == 0) && rule.Remove is null)
        {
            add($"{path}/actions", "Add an action or set remove to archive (use none to match without changing anything).");
        }
        else if (rule.Remove is not null && actions.Any(a => a.Kind == RuleActionKind.None))
        {
            add($"{path}/remove", "none means change nothing, so it cannot be combined with remove.");
        }
        else if (actions.Count > 1 && actions.Any(a => a.Kind == RuleActionKind.None))
        {
            add($"{path}/actions", "none cannot be combined with other actions.");
        }
        else if (actions.Distinct().Count() != actions.Count)
        {
            add($"{path}/actions", "Each action can only be listed once.");
        }
    }

    /// <summary>Null when the comparison makes sense for the question it names; otherwise why not.</summary>
    public static string? CheckComparison(Comparison comparison, IReadOnlyDictionary<string, JevQuestion> questions)
    {
        if (!questions.TryGetValue(comparison.Question, out var question))
        {
            return questions.Count == 0
                ? $"there is no question named '{comparison.Question}'."
                : $"there is no question named '{comparison.Question}' (questions: {string.Join(", ", questions.Keys)}).";
        }

        var isEquality = comparison.Operator is ComparisonOperator.Equal or ComparisonOperator.NotEqual;
        switch (question, comparison.Field)
        {
            case (JevChoiceQuestion choice, "choice"):
                if (comparison.Value is not string option || !isEquality)
                {
                    return "compare a choice with == or != and a \"quoted\" option name.";
                }

                return choice.Criteria.ContainsKey(option)
                    ? null
                    : $"'{option}' is not an option (options: {string.Join(", ", choice.Criteria.Keys)}).";

            case (JevChoiceQuestion choice, Comparison.ProbabilitiesField):
                return !choice.Criteria.ContainsKey(comparison.Key!)
                    ? $"'{comparison.Key}' is not an option (options: {string.Join(", ", choice.Criteria.Keys)})."
                    : CheckNumber(comparison, 0, 1);

            case (JevScoreQuestion score, "score"):
                return CheckNumber(comparison, 0, score.Criteria.Count - 1);

            case (JevScoreQuestion score, Comparison.ProbabilitiesField):
                return int.TryParse(comparison.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var level) && level < score.Criteria.Count
                    ? CheckNumber(comparison, 0, 1)
                    : $"score levels are numbered 0 to {score.Criteria.Count - 1}.";

            case (JevChoiceQuestion or JevScoreQuestion, "confidence"):
            case (JevNoulQuestion, "noul"):
                return CheckNumber(comparison, 0, 1);

            default:
                var fields = question switch
                {
                    JevChoiceQuestion => "choice, confidence or probabilities.<option>",
                    JevScoreQuestion => "score, confidence or probabilities.<level>",
                    _ => "noul",
                };
                return $"'{comparison.Field}' is not an answer field here; use {fields}.";
        }
    }

    private static string? CheckNumber(Comparison comparison, double min, double max)
        => comparison.Value is double value
            ? value >= min && value <= max ? null : $"the value must be between {min} and {max.ToString(CultureInfo.InvariantCulture)}."
            : "compare with a number, not text.";

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$")]
    private static partial Regex QuestionNamePattern();
}
