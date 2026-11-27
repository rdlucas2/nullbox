using System.Globalization;
using Nullbox.Core.Rules;

namespace Nullbox.Web.Ui.Rules;

/// <summary>
/// Helpers for the Decisions tab: a <c>when</c> made only of comparisons joined by <c>&amp;&amp;</c> is shown as
/// rows with sliders for its thresholds; anything else (<c>||</c>, <c>!</c>) is edited as text only.
/// </summary>
public static class DecisionConditions
{
    /// <summary>Thresholds below this on a Delete rule get a warning.</summary>
    public const double LowDeleteThreshold = 0.8;

    public static readonly IReadOnlyList<(string Symbol, ComparisonOperator Operator)> Operators =
    [
        ("==", ComparisonOperator.Equal),
        ("!=", ComparisonOperator.NotEqual),
        (">=", ComparisonOperator.GreaterOrEqual),
        (">", ComparisonOperator.GreaterThan),
        ("<=", ComparisonOperator.LessOrEqual),
        ("<", ComparisonOperator.LessThan),
    ];

    /// <summary>The comparisons of a <c>when</c> that is only comparisons joined by <c>&amp;&amp;</c>; false otherwise.</summary>
    public static bool TryClauses(string when, out IReadOnlyList<Comparison> clauses)
    {
        clauses = [];
        if (string.IsNullOrWhiteSpace(when) || !ConditionParser.TryParse(when, out var condition, out _) || condition is null)
        {
            return false;
        }

        var list = new List<Comparison>();
        if (!Flatten(condition, list))
        {
            return false;
        }

        clauses = list;
        return true;
    }

    public static string Join(IEnumerable<Comparison> clauses) => string.Join(" && ", clauses.Select(c => c.ToString()));

    /// <summary>A comparison of a 0 to 1 number: a confidence, a probability or a noul.</summary>
    public static bool IsThreshold(Comparison clause)
        => clause.Value is double && clause.Field is "confidence" or "noul" or Comparison.ProbabilitiesField;

    /// <summary>Replaces the value of clause <paramref name="index"/>, keeping the rest of the condition.</summary>
    public static string WithValue(string when, int index, double value)
    {
        if (!TryClauses(when, out var clauses) || index < 0 || index >= clauses.Count)
        {
            return when;
        }

        var rounded = Math.Round(Math.Clamp(value, 0, 1), 2);
        return Join(clauses.Select((c, i) => i == index ? c with { Value = rounded } : c));
    }

    /// <summary>Appends a comparison with <c>&amp;&amp;</c>.</summary>
    public static string Append(string when, string clause)
        => string.IsNullOrWhiteSpace(when) ? clause : $"{when.Trim()} && {clause}";

    /// <summary>Builds <c>question.field op value</c>; numbers stay numbers, anything else is quoted.</summary>
    public static string Clause(string question, string field, string symbol, string value)
    {
        var trimmed = value.Trim();
        var literal = double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number.ToString("R", CultureInfo.InvariantCulture)
            : "\"" + trimmed.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        return $"{question}.{field} {symbol} {literal}";
    }

    /// <summary>The fields a condition can read from a question's answer.</summary>
    public static IReadOnlyList<string> FieldsFor(QuestionDraft question) => question.Kind switch
    {
        QuestionKind.Choice => ["choice", "confidence", .. question.Options.Where(o => o.Name.Trim().Length > 0).Select(o => $"{Comparison.ProbabilitiesField}.{o.Name.Trim()}")],
        QuestionKind.Score => ["score", "confidence"],
        _ => ["noul"],
    };

    /// <summary>Why a Delete rule looks risky, or null: no threshold at all, or one below <see cref="LowDeleteThreshold"/>.</summary>
    public static string? DeleteWarning(DecisionDraft decision)
    {
        if (decision.Remove != RemoveMode.Delete)
        {
            return null;
        }

        if (!TryClauses(decision.When, out var clauses))
        {
            return null;
        }

        var thresholds = clauses
            .Where(c => IsThreshold(c) && c.Operator is ComparisonOperator.GreaterOrEqual or ComparisonOperator.GreaterThan)
            .ToList();
        if (thresholds.Count == 0)
        {
            return "This rule deletes without a confidence threshold. Add one, e.g. category.confidence >= 0.9.";
        }

        var low = thresholds.Where(c => (double)c.Value < LowDeleteThreshold).Select(c => c.ToString()).ToList();
        return low.Count == 0
            ? null
            : $"Low threshold for a rule that deletes: {string.Join(", ", low)}. Consider {LowDeleteThreshold.ToString(CultureInfo.InvariantCulture)} or higher.";
    }

    private static bool Flatten(Condition condition, List<Comparison> into)
    {
        switch (condition)
        {
            case Comparison comparison:
                into.Add(comparison);
                return true;
            case AndCondition and:
                return Flatten(and.Left, into) && Flatten(and.Right, into);
            default:
                return false;
        }
    }
}
