using Nullbox.Core.Jev;
using Nullbox.Core.Rules;

namespace Nullbox.Tests.Rules;

public sealed class ConditionTests
{
    private static readonly JevResult Result = new()
    {
        Model = "jev-1.14.0",
        Answers = new Dictionary<string, JevAnswer>
        {
            ["category"] = new JevChoiceAnswer
            {
                Choice = "promo",
                Confidence = 0.82,
                Probabilities = new Dictionary<string, double> { ["promo"] = 0.8, ["receipt"] = 0.15, ["personal"] = 0.05 },
            },
            ["urgency"] = new JevScoreAnswer { Score = 1.7, Confidence = 0.55, Probabilities = new Dictionary<string, double> { ["2"] = 0.8 } },
            ["is_phishing"] = new JevNoulAnswer { Noul = 0.02 },
        },
    };

    [Theory]
    [InlineData("category.choice == \"promo\"", true)]
    [InlineData("category.choice != \"promo\"", false)]
    [InlineData("category.choice == \"promo\" && category.confidence >= 0.8", true)]
    [InlineData("category.choice == \"promo\" && category.confidence >= 0.9", false)]
    [InlineData("category.probabilities.receipt > 0.1", true)]
    [InlineData("category.probabilities.other >= 0", true)]
    [InlineData("urgency.score >= 1.5", true)]
    [InlineData("urgency.probabilities.2 > 0.5", true)]
    [InlineData("is_phishing.noul >= 0.9", false)]
    [InlineData("is_phishing.noul < 0.5", true)]
    [InlineData("!(is_phishing.noul >= 0.9)", true)]
    [InlineData("is_phishing.noul >= 0.9 || urgency.score >= 1.5", true)]
    [InlineData("is_phishing.noul >= 0.9 || urgency.score >= 1.5 && category.confidence > 0.99", false)]
    [InlineData("(is_phishing.noul >= 0.9 || urgency.score >= 1.5) && category.confidence > 0.5", true)]
    [InlineData("urgency.score == 1.7", true)]
    [InlineData("urgency.score <= -1", false)]
    public void Evaluates_against_answers(string when, bool expected)
        => Assert.Equal(expected, ConditionParser.Parse(when).Matches(Result));

    [Theory]
    [InlineData("missing.noul >= 0.5")]
    [InlineData("!(missing.noul >= 0.5)")]
    [InlineData("missing.noul >= 0.5 && category.choice == \"promo\"")]
    [InlineData("category.choice >= 1")]
    [InlineData("is_phishing.score > 0")]
    public void Missing_or_mismatched_answers_never_match(string when)
        => Assert.False(ConditionParser.Parse(when).Matches(Result));

    [Fact]
    public void Missing_answers_are_unknown_but_a_true_branch_of_an_or_still_wins()
    {
        Assert.Null(ConditionParser.Parse("missing.noul > 0").Evaluate(Result));
        Assert.True(ConditionParser.Parse("missing.noul > 0 || is_phishing.noul < 0.5").Matches(Result));
        Assert.False(ConditionParser.Parse("missing.noul > 0 && is_phishing.noul > 0.5").Evaluate(Result));
    }

    [Theory]
    [InlineData("a.b == \"x\"", "a.b == \"x\"")]
    [InlineData("a.b>=0.8&&c.d<1", "a.b >= 0.8 && c.d < 1")]
    [InlineData("(a.b > 0 || c.d > 0) && e.f > 0", "(a.b > 0 || c.d > 0) && e.f > 0")]
    [InlineData("a.b > 0 || c.d > 0 && e.f > 0", "a.b > 0 || c.d > 0 && e.f > 0")]
    [InlineData("!(a.b > 0)", "!(a.b > 0)")]
    [InlineData("!!(a.b > 0)", "!!(a.b > 0)")]
    [InlineData("!(a.b > 0 && c.d > 0)", "!(a.b > 0 && c.d > 0)")]
    [InlineData("a.probabilities.x-y >= .5", "a.probabilities.x-y >= 0.5")]
    [InlineData("a.b == \"say \\\"hi\\\"\"", "a.b == \"say \\\"hi\\\"\"")]
    public void Formats_back_to_an_equivalent_condition(string text, string expected)
    {
        var parsed = ConditionParser.Parse(text);

        Assert.Equal(expected, parsed.ToString());
        Assert.Equal(parsed, ConditionParser.Parse(parsed.ToString()!));
    }

    [Theory]
    [InlineData("", "ended early", 0)]
    [InlineData("category", "question.field", 0)]
    [InlineData("category.choice", "Expected a comparison", 15)]
    [InlineData("category.choice == promo", "double quotes", 19)]
    [InlineData("category.choice == \"promo", "Unterminated string", 19)]
    [InlineData("a.b > 1 &&", "ended early", 10)]
    [InlineData("(a.b > 1", "Expected ')'", 8)]
    [InlineData("a.b > 1 c.d > 2", "Unexpected text", 8)]
    [InlineData("a.b = 1", "Unexpected character '='", 4)]
    [InlineData("a.confidence.x > 1", "Only probabilities take a key", 0)]
    [InlineData("a.probabilities > 1", "Name the option", 0)]
    public void Syntax_errors_say_what_and_where(string text, string message, int position)
    {
        var ex = Assert.Throws<ConditionSyntaxException>(() => ConditionParser.Parse(text));

        Assert.Contains(message, ex.Message);
        Assert.Equal(position, ex.Position);
    }

    [Fact]
    public void Comparisons_are_listed_left_to_right()
    {
        var comparisons = ConditionParser.Parse("a.b > 1 && !(c.d == \"x\" || e.probabilities.f < 0.2)").Comparisons();

        Assert.Equal(["a.b", "c.d", "e.probabilities.f"], comparisons.Select(c => c.Path));
    }

    [Theory]
    [InlineData("label:nullbox/promo", RuleActionKind.Label, "nullbox/promo")]
    [InlineData("label: Receipts ", RuleActionKind.Label, "Receipts")]
    [InlineData("mark_read", RuleActionKind.MarkRead, null)]
    [InlineData("star", RuleActionKind.Star, null)]
    [InlineData("none", RuleActionKind.None, null)]
    public void Actions_parse(string text, RuleActionKind kind, string? label)
    {
        var action = RuleAction.Parse(text);

        Assert.Equal(new RuleAction(kind, label), action);
        Assert.Equal(action, RuleAction.Parse(action.ToString()));
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("archive")]
    [InlineData("trash")]
    [InlineData("label:")]
    [InlineData("Archive")]
    [InlineData("")]
    public void Unknown_actions_are_rejected(string text)
        => Assert.False(RuleAction.TryParse(text, out _, out _));
}
