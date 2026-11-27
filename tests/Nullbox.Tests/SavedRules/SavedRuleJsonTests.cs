using Nullbox.Core.Rules;
using Nullbox.Core.SavedRules;

namespace Nullbox.Tests.SavedRules;

public sealed class SavedRuleJsonTests
{
    [Fact]
    public void A_minimal_rule_starts_as_an_enabled_dry_run_that_stops()
    {
        var rule = SavedRuleJson.ParseRule("""{ "name": "block-shop", "from": "@shop.example", "remove": "delete" }""", out var errors);

        Assert.Empty(errors);
        Assert.NotNull(rule);
        Assert.Equal((true, RuleSetMode.DryRun, true, false, RemoveMode.Delete), (rule.Enabled, rule.Mode, rule.Stop, rule.AllowProtected, rule.Remove));
    }

    [Fact]
    public void A_document_round_trips_through_the_schema()
    {
        SavedRulesDocument document = new()
        {
            Rules =
            [
                new SavedRule { Name = "shop", From = "deals@shop.example", Actions = ["label:shop", "mark_read"], Remove = RemoveMode.Archive, Mode = RuleSetMode.Live },
                new SavedRule { Name = "news", To = "me+news@gmail.com", SubjectContains = "weekly", Actions = ["star"], Stop = false, AllowProtected = true },
            ],
        };

        var json = SavedRuleJson.Serialize(document);
        var parsed = SavedRuleJson.Parse(json, out var errors);

        Assert.Empty(errors);
        Assert.Contains("\"$schema\": \"/schemas/savedrules.schema.json\"", json, StringComparison.Ordinal);
        Assert.Contains("\"subject_contains\": \"weekly\"", json, StringComparison.Ordinal);
        Assert.Equal(document.Rules, parsed!.Rules, (a, b) => SavedRuleJson.Serialize(a) == SavedRuleJson.Serialize(b));
    }

    [Theory]
    [InlineData("""{ "name": "a", "subject_contains": "x", "actions": ["star"] }""", "")]
    [InlineData("""{ "name": "a", "from": "shop.example", "actions": ["star"] }""", "/from")]
    [InlineData("""{ "name": "a", "to": "me@", "actions": ["star"] }""", "/to")]
    [InlineData("""{ "name": "A b", "from": "@shop.example", "actions": ["star"] }""", "/name")]
    [InlineData("""{ "name": "a", "from": "@shop.example" }""", "/actions")]
    [InlineData("""{ "name": "a", "from": "@shop.example", "actions": ["archive"] }""", "/actions/0")]
    [InlineData("""{ "name": "a", "from": "@shop.example", "actions": ["none"] }""", "/actions/0")]
    [InlineData("""{ "name": "a", "from": "@shop.example", "remove": "trash" }""", "/remove")]
    [InlineData("""{ "name": "a", "from": "@shop.example", "star": true }""", "")]
    public void Invalid_rules_are_refused_with_a_path(string json, string path)
    {
        Assert.Null(SavedRuleJson.ParseRule(json, out var errors));
        Assert.Contains(errors, e => e.Path == path);
    }

    [Fact]
    public void Two_rules_cannot_share_a_name()
    {
        Assert.Null(SavedRuleJson.Parse(
            """{ "rules": [ { "name": "a", "from": "@x.example", "actions": ["star"] }, { "name": "a", "from": "@y.example", "actions": ["star"] } ] }""",
            out var errors));
        Assert.Contains(errors, e => e.Path == "/rules/1/name");
    }
}
