using System.Text.Json.Nodes;
using Nullbox.Core.Rules;
using Nullbox.Web.Ui.Rules;

namespace Nullbox.Tests.Ui.Rules;

public sealed class RuleSetEditorModelTests
{
    public static TheoryData<string> Seeds() => [.. RuleSetSeeds.Names];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Seeds_round_trip_through_the_editor_unchanged(string name)
    {
        var seed = RuleSetSeeds.Load(name);

        var model = RuleSetEditorModel.FromDocument(seed);

        Assert.Empty(model.Validate());
        Assert.Equal(RuleJson.Serialize(seed), model.ToJson());
    }

    [Fact]
    public void New_rule_sets_are_live_and_new_decisions_archive()
    {
        var model = new RuleSetEditorModel();

        var decision = model.AddDecision();

        Assert.Equal(RuleSetMode.Live, model.Mode);
        Assert.Equal(RemoveMode.Archive, decision.Remove);
    }

    [Fact]
    public void New_questions_get_unique_names_and_starter_options()
    {
        var model = new RuleSetEditorModel();

        var first = model.AddQuestion(QuestionKind.Choice);
        var second = model.AddQuestion(QuestionKind.Choice);
        var score = model.AddQuestion(QuestionKind.Score);

        Assert.Equal(("choice_1", "choice_2", "score_1"), (first.Name, second.Name, score.Name));
        Assert.Equal(["yes", "no"], first.Options.Select(o => o.Name));
        Assert.Equal(["Low", "High"], score.Options.Select(o => o.Description));
    }

    [Fact]
    public void Repeated_or_blank_names_are_reported_before_the_schema_runs()
    {
        var model = new RuleSetEditorModel { Name = "dupes" };
        model.AddQuestion(QuestionKind.Noul).Name = "same";
        model.AddQuestion(QuestionKind.Noul).Name = "same";
        model.AddQuestion(QuestionKind.Choice).Options[1].Name = "yes";

        var errors = model.Validate();

        Assert.Contains(errors, e => e.Message.Contains("Two questions are named 'same'", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Message.Contains("Option 'yes' is listed twice", StringComparison.Ordinal));
        Assert.Null(model.TryToJson());
        Assert.Null(model.RequestPreview(DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Schema_and_validator_errors_come_through_with_paths()
    {
        var model = new RuleSetEditorModel { Name = "Bad Name" };
        model.AddDecision().When = "missing.noul >= 0.5";

        var errors = model.Validate();

        Assert.Contains(errors, e => e.Path.StartsWith("/name", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Path == "/questions");
    }

    [Fact]
    public void Invalid_json_leaves_the_editor_as_it_was()
    {
        var model = RuleSetEditorModel.FromDocument(RuleSetSeeds.Load(RuleSetSeeds.PromoCleanup));
        var before = model.ToJson();

        Assert.False(model.TryLoadJson("""{ "name": "x" }""", out var errors));
        Assert.NotEmpty(errors);
        Assert.Equal(before, model.ToJson());

        Assert.True(model.TryLoadJson(RuleSetSeeds.Json(RuleSetSeeds.ImportantMailGuard), out _));
        Assert.Equal(RuleSetSeeds.ImportantMailGuard, model.Name);
    }

    [Fact]
    public void Previews_show_the_state_and_the_request_for_the_sample_email()
    {
        var model = RuleSetEditorModel.FromDocument(RuleSetSeeds.Load(RuleSetSeeds.PromoCleanup));
        var now = new DateTimeOffset(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);

        var state = JsonNode.Parse(model.StatePreview(now))!.AsObject();
        var request = JsonNode.Parse(model.RequestPreview(now)!)!.AsObject();

        Assert.Equal(["from", "subject", "snippet", "labels", "age_days", "has_unsubscribe"], state.Select(p => p.Key));
        Assert.Equal("jev-latest", request["model"]!.GetValue<string>());
        Assert.Equal(["from_a_person", "category", "fits_category"], request["questions"]!.AsObject().Select(p => p.Key));
    }

    [Theory]
    [InlineData("Plain words", "Plain words", true)]
    [InlineData("""{"a": 1}""", """{"a":1}""", false)]
    [InlineData("[1, 2]", "[1,2]", false)]
    [InlineData("{not json", "{not json", true)]
    public void Text_that_parses_as_an_object_or_array_becomes_json(string text, string compact, bool isString)
    {
        var node = JsonText.ToNode(text)!;

        Assert.Equal(isString, node is JsonValue);
        Assert.Equal(compact, isString ? node.GetValue<string>() : node.ToJsonString());
        Assert.True(JsonNode.DeepEquals(node, JsonText.ToNode(JsonText.ToText(node))));
    }

    [Fact]
    public void Blank_text_is_no_node()
    {
        Assert.Null(JsonText.ToNode("  "));
        Assert.Equal(string.Empty, JsonText.ToText(null));
    }
}

public sealed class DecisionConditionsTests
{
    [Fact]
    public void And_chains_of_comparisons_become_clauses()
    {
        Assert.True(DecisionConditions.TryClauses("category.choice == \"promo\" && category.confidence >= 0.85", out var clauses));
        Assert.Equal(2, clauses.Count);
        Assert.False(DecisionConditions.IsThreshold(clauses[0]));
        Assert.True(DecisionConditions.IsThreshold(clauses[1]));

        Assert.False(DecisionConditions.TryClauses("a.noul >= 0.5 || b.noul >= 0.5", out _));
        Assert.False(DecisionConditions.TryClauses("", out _));
    }

    [Fact]
    public void A_slider_changes_one_threshold_and_keeps_the_rest()
    {
        var when = DecisionConditions.WithValue("category.choice == \"promo\" && category.confidence >= 0.85", 1, 0.912);

        Assert.Equal("category.choice == \"promo\" && category.confidence >= 0.91", when);
        Assert.Equal("a.noul >= 0.5 || b.noul >= 0.5", DecisionConditions.WithValue("a.noul >= 0.5 || b.noul >= 0.5", 0, 0.9));
    }

    [Fact]
    public void Clauses_quote_text_and_keep_numbers()
    {
        Assert.Equal("category.choice == \"pro\\\"mo\"", DecisionConditions.Clause("category", "choice", "==", "pro\"mo"));
        Assert.Equal("fits.noul >= 0.7", DecisionConditions.Clause("fits", "noul", ">=", " 0.7 "));
        Assert.Equal("a.noul >= 0.5 && b.noul < 0.2", DecisionConditions.Append("a.noul >= 0.5", "b.noul < 0.2"));
        Assert.Equal("b.noul < 0.2", DecisionConditions.Append(" ", "b.noul < 0.2"));
    }

    [Fact]
    public void Choice_fields_include_each_options_probability()
    {
        var question = new RuleSetEditorModel().AddQuestion(QuestionKind.Choice);

        Assert.Equal(["choice", "confidence", "probabilities.yes", "probabilities.no"], DecisionConditions.FieldsFor(question));
    }

    [Theory]
    [InlineData(RemoveMode.Archive, "c.confidence >= 0.5", null)]
    [InlineData(RemoveMode.Delete, "c.choice == \"promo\"", "without a confidence threshold")]
    [InlineData(RemoveMode.Delete, "c.choice == \"promo\" && c.confidence >= 0.6", "Low threshold")]
    [InlineData(RemoveMode.Delete, "c.choice == \"promo\" && c.confidence >= 0.95", null)]
    public void Delete_rules_with_weak_thresholds_get_a_warning(RemoveMode remove, string when, string? warning)
    {
        var message = DecisionConditions.DeleteWarning(new DecisionDraft { Remove = remove, When = when });

        if (warning is null)
        {
            Assert.Null(message);
        }
        else
        {
            Assert.Contains(warning, message, StringComparison.Ordinal);
        }
    }
}

public sealed class LineDiffTests
{
    [Fact]
    public void Shows_added_removed_and_kept_lines_in_order()
    {
        var diff = LineDiff.Compute("a\nb\nc", "a\nc\nd");

        Assert.Equal(
            [(DiffKind.Same, "a"), (DiffKind.Removed, "b"), (DiffKind.Same, "c"), (DiffKind.Added, "d")],
            diff.Select(l => (l.Kind, l.Text)));
    }

    [Fact]
    public void A_first_version_is_all_added()
    {
        Assert.All(LineDiff.Compute(string.Empty, "x\ny"), l => Assert.Equal(DiffKind.Added, l.Kind));
    }
}

public sealed class RuleSetCatalogNamingTests
{
    [Fact]
    public void Copies_take_the_first_free_name()
    {
        Assert.Equal("a-copy", RuleSetCatalog.FreeName("a-copy", new HashSet<string> { "a" }));
        Assert.Equal("a-copy-3", RuleSetCatalog.FreeName("a-copy", new HashSet<string> { "a", "a-copy", "a-copy-2" }));
        Assert.Equal(60, RuleSetCatalog.FreeName(new string('x', 70), new HashSet<string>()).Length);
    }
}
