using System.Text.Json.Nodes;
using Nullbox.Core.Jev;
using Nullbox.Core.Rules;

namespace Nullbox.Tests.Rules;

/// <summary>Archive-first rules (#36): a rule removes mail only through its <c>remove</c> switch, archive by default.</summary>
public sealed class RemoveSwitchTests
{
    private static RuleSetDocument Minimal(params DecisionRule[] decisions) => new()
    {
        Name = "junk",
        Questions = new Dictionary<string, JevQuestion> { ["spam"] = new JevNoulQuestion { Instructions = "Is spam" } },
        Decisions = decisions,
    };

    private static string Json(string decision) => $$$"""{"name":"junk","questions":{"spam":{"type":"noul"}},"decisions":[{{{decision}}}]}""";

    // ---- mode ---------------------------------------------------------------------------------

    [Fact]
    public void New_rule_sets_are_live_by_default()
    {
        Assert.Equal(RuleSetMode.Live, Minimal().Mode);
        Assert.Equal(RuleSetMode.Live, RuleJson.ParseOrThrow("""{"name":"junk","questions":{"spam":{"type":"noul"}}}""").Mode);
    }

    [Fact]
    public void Dry_run_is_always_written_so_stored_dry_runs_stay_dry_runs()
    {
        var json = RuleJson.Serialize(Minimal() with { Mode = RuleSetMode.DryRun });

        Assert.Equal("dry_run", JsonNode.Parse(json)!["mode"]!.GetValue<string>());
        Assert.Equal(RuleSetMode.DryRun, RuleJson.ParseOrThrow(json).Mode);
    }

    // ---- the remove switch ---------------------------------------------------------------------

    [Theory]
    [InlineData("archive", RemoveMode.Archive)]
    [InlineData("delete", RemoveMode.Delete)]
    public void Remove_round_trips(string text, RemoveMode expected)
    {
        var document = RuleJson.ParseOrThrow(Json($$"""{"name":"Spam","when":"spam.noul >= 0.9","actions":["mark_read"],"remove":"{{text}}"}"""));

        Assert.Equal(expected, document.Decisions[0].Remove);
        Assert.Equal(text, JsonNode.Parse(RuleJson.Serialize(document))!["decisions"]![0]!["remove"]!.GetValue<string>());
    }

    [Fact]
    public void Omitted_remove_leaves_mail_in_the_inbox_and_is_not_written()
    {
        var document = RuleJson.ParseOrThrow(Json("""{"when":"spam.noul >= 0.9","actions":["label:spam"]}"""));

        Assert.Null(document.Decisions[0].Remove);
        Assert.Null(JsonNode.Parse(RuleJson.Serialize(document))!["decisions"]![0]!["remove"]);
    }

    [Fact]
    public void A_rule_can_remove_without_other_actions()
    {
        var document = RuleJson.Parse(Json("""{"name":"Spam","when":"spam.noul >= 0.9","remove":"archive"}"""), out var errors);

        Assert.Empty(errors);
        Assert.Empty(document!.Decisions[0].Actions);
    }

    [Fact]
    public void A_rule_needs_an_action_or_remove()
    {
        Assert.Contains(RuleSetSchema.Validate(Json("""{"when":"spam.noul >= 0.9"}""")), e => e.Path == "/decisions/0");

        var error = Assert.Single(RuleSetValidator.Validate(Minimal(new DecisionRule { When = "spam.noul >= 0.9" })));
        Assert.Equal("/decisions/0/actions", error.Path);
        Assert.Contains("remove", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void None_cannot_be_combined_with_remove()
    {
        var errors = RuleSetValidator.Validate(Minimal(new DecisionRule { When = "spam.noul >= 0.9", Actions = ["none"], Remove = RemoveMode.Archive }));

        Assert.Contains(errors, e => e.Path == "/decisions/0/remove" && e.Message.Contains("none", StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_remove_values_are_rejected_by_the_schema()
        => Assert.Contains(RuleSetSchema.Validate(Json("""{"when":"spam.noul >= 0.9","remove":"purge"}""")), e => e.Path == "/decisions/0/remove");

    [Theory]
    [InlineData("trash", "\"remove\": \"delete\"")]
    [InlineData("archive", "\"remove\": \"archive\"")]
    public void Old_trash_and_archive_actions_point_at_remove(string action, string hint)
    {
        Assert.False(RuleAction.TryParse(action, out _, out var parseError));
        Assert.Contains(hint, parseError, StringComparison.Ordinal);

        var error = Assert.Single(RuleSetValidator.Validate(Minimal(new DecisionRule { When = "spam.noul >= 0.9", Actions = [action] })));
        Assert.Equal("/decisions/0/actions/0", error.Path);
        Assert.Contains(hint, error.Message, StringComparison.Ordinal);

        Assert.Contains(RuleSetSchema.Validate(Json($$"""{"when":"spam.noul >= 0.9","actions":["{{action}}"]}""")), e => e.Path == "/decisions/0/actions/0");
    }

    // ---- review labels -------------------------------------------------------------------------

    [Theory]
    [InlineData("Archive confident promos", "nullbox/review/promo-cleanup/archive-confident-promos")]
    [InlineData("  Spam.ru — junk!! ", "nullbox/review/promo-cleanup/spam-ru-junk")]
    [InlineData("Ünïcödé Ñews", "nullbox/review/promo-cleanup/unicode-news")]
    [InlineData("!!!", "nullbox/review/promo-cleanup/rule-3")]
    [InlineData(null, "nullbox/review/promo-cleanup/rule-3")]
    public void Review_labels_name_the_rule_set_and_rule(string? ruleName, string expected)
        => Assert.Equal(expected, ReviewLabels.For("promo-cleanup", new DecisionRule { Name = ruleName, When = "x.noul > 0" }, index: 2));

    [Fact]
    public void Review_labels_fit_gmail_label_limits()
    {
        var label = ReviewLabels.For(new string('r', 64), new DecisionRule { Name = new string('n', 200), When = "x.noul > 0" }, 0);

        Assert.True(label.Length <= RuleAction.MaxLabelLength, $"{label.Length} chars");
        Assert.StartsWith(ReviewLabels.Root + "/", label, StringComparison.Ordinal);
        Assert.DoesNotContain("//", label, StringComparison.Ordinal);
        Assert.False(label.EndsWith('-'));
    }

    [Fact]
    public void Two_removing_rules_cannot_share_a_review_label()
    {
        var errors = RuleSetValidator.Validate(Minimal(
            new DecisionRule { Name = "Junk", When = "spam.noul >= 0.9", Remove = RemoveMode.Archive },
            new DecisionRule { Name = "junk!", When = "spam.noul >= 0.8", Remove = RemoveMode.Delete }));

        var error = Assert.Single(errors);
        Assert.Equal("/decisions/1/name", error.Path);
        Assert.Contains("nullbox/review/junk/junk", error.Message, StringComparison.Ordinal);
    }

    // ---- seeds ---------------------------------------------------------------------------------

    [Fact]
    public void Seeds_archive_with_the_switch_and_never_delete()
    {
        var decisions = RuleSetSeeds.All().SelectMany(s => s.Decisions).ToList();

        Assert.DoesNotContain(decisions, d => d.Remove == RemoveMode.Delete);
        Assert.Contains(decisions, d => d.Name == "Archive confident promos" && d.Remove == RemoveMode.Archive);
    }

    // ---- stored rule sets from before the switch -----------------------------------------------

    [Fact]
    public void Legacy_archive_and_trash_actions_upgrade_to_remove_archive()
    {
        const string legacy = """
            {"name":"junk","mode":"live","questions":{"spam":{"type":"noul"}},"decisions":[
              {"name":"Archive","when":"spam.noul >= 0.9","actions":["label:spam","mark_read","archive"]},
              {"name":"Trash","when":"spam.noul >= 0.99","actions":["trash"]},
              {"name":"Keep","when":"spam.noul < 0.1","actions":["none"]}]}
            """;

        var upgraded = RuleSetUpgrade.UpgradeLegacyActions(legacy);

        Assert.NotNull(upgraded);
        var document = RuleJson.ParseOrThrow(upgraded);
        Assert.Equal(["label:spam", "mark_read"], document.Decisions[0].Actions);
        Assert.Equal(RemoveMode.Archive, document.Decisions[0].Remove);
        Assert.Empty(document.Decisions[1].Actions);
        Assert.Equal(RemoveMode.Archive, document.Decisions[1].Remove); // trash is downgraded, never upgraded to delete
        Assert.Equal(["none"], document.Decisions[2].Actions);
        Assert.Null(document.Decisions[2].Remove);
        Assert.Null(RuleSetUpgrade.UpgradeLegacyActions(upgraded));
    }

    [Theory]
    [InlineData("""{"name":"junk","questions":{"spam":{"type":"noul"}}}""")]
    [InlineData("not json")]
    public void Documents_without_legacy_actions_are_left_alone(string json)
        => Assert.Null(RuleSetUpgrade.UpgradeLegacyActions(json));
}
