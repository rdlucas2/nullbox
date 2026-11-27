using System.Globalization;
using System.Text;

namespace Nullbox.Core.Rules;

/// <summary>
/// The Gmail label a rule adds to everything it archives, <c>nullbox/review/&lt;rule-set&gt;/&lt;rule&gt;</c>, so
/// what a rule took out of the inbox can be reviewed before its switch is flipped to Delete.
/// </summary>
public static class ReviewLabels
{
    public const string Root = "nullbox/review";

    private const int MaxRuleSlugLength = 100;

    /// <summary>
    /// The review label for <paramref name="rule"/>, the decision at <paramref name="index"/> (0-based) in rule set
    /// <paramref name="ruleSet"/>. Named rules keep their label when reordered; unnamed ones are <c>rule-&lt;n&gt;</c>.
    /// </summary>
    public static string For(string ruleSet, DecisionRule rule, int index)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var slug = Slug(rule.Name);
        return $"{Root}/{ruleSet}/{(slug.Length == 0 ? $"rule-{index + 1}" : slug)}";
    }

    /// <summary>Lowercase ASCII letters and digits joined by single dashes; accents are dropped, not the letters.</summary>
    internal static string Slug(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(name.Length);
        foreach (var c in name.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var lower = char.ToLowerInvariant(c);
            if (lower is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                builder.Append(lower);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }

            if (builder.Length >= MaxRuleSlugLength)
            {
                break;
            }
        }

        return builder.ToString().Trim('-');
    }
}
