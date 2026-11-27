using Nullbox.Core.Rules;
using Nullbox.Core.SavedRules;

namespace Nullbox.Tests.SavedRules;

public sealed class AddressPatternTests
{
    [Theory]
    [InlineData("deals@shop.example", "deals@shop.example", true)]
    [InlineData("deals@shop.example", "DEALS@Shop.Example", true)]
    [InlineData("Deals@SHOP.example", "deals@shop.example", true)]
    [InlineData("deals@shop.example", "news@shop.example", false)]
    [InlineData("deals@shop.example", "deals@shop.example.org", false)]
    [InlineData("deals@shop.example", "deals@mail.shop.example", false)]
    [InlineData("@shop.example", "anyone@shop.example", true)]
    [InlineData("@shop.example", "anyone@mail.SHOP.example", true)]
    [InlineData("@shop.example", "anyone@notshop.example", false)]
    [InlineData("@shop.example", "anyone@shop.example.org", false)]
    [InlineData("me@gmail.com", "me+news@gmail.com", true)]
    [InlineData("me@gmail.com", "ME+News@GMail.com", true)]
    [InlineData("me@gmail.com", "mea@gmail.com", false)]
    [InlineData("me+news@gmail.com", "me+news@gmail.com", true)]
    [InlineData("me+news@gmail.com", "me+NEWS@gmail.com", true)]
    [InlineData("me+news@gmail.com", "me@gmail.com", false)]
    [InlineData("me+news@gmail.com", "me+other@gmail.com", false)]
    [InlineData("@gmail.com", "me+news@gmail.com", true)]
    public void Addresses_domains_and_plus_addresses_match_ignoring_case(string pattern, string address, bool matches)
        => Assert.Equal(matches, AddressPattern.Matches(pattern, address));

    [Fact]
    public void Addresses_are_pulled_out_of_headers_with_names_and_lists()
        => Assert.Equal(
            ["jo@x.example", "ann@y.example", "me+news@gmail.com"],
            AddressPattern.Extract("\"Doe, Jo\" <Jo@X.example>, ann@y.example", null, "Me <me+news@gmail.com>; jo@x.example"));

    [Theory]
    [InlineData("deals@shop.example", true)]
    [InlineData("@shop.example", true)]
    [InlineData("me+news@gmail.com", true)]
    [InlineData("shop.example", false)]
    [InlineData("@shop", false)]
    [InlineData("a@b@shop.example", false)]
    [InlineData("deals @shop.example", false)]
    [InlineData("", false)]
    public void Only_addresses_and_whole_domains_are_valid(string pattern, bool valid)
        => Assert.Equal(valid, AddressPattern.Problem(pattern) is null);
}

public sealed class SavedRuleEngineTests
{
    private static SavedRule Rule(string name, string? from = null, string? to = null, string? subject = null, RuleSetMode mode = RuleSetMode.Live, bool stop = true, bool enabled = true)
        => new() { Name = name, From = from, To = to, SubjectContains = subject, Mode = mode, Stop = stop, Enabled = enabled, Remove = RemoveMode.Archive };

    private static readonly SavedRuleInput Mail = new("Shop <deals@shop.example>", "Me <me+shop@gmail.com>", "Ann <ann@y.example>", "50% off everything");

    [Fact]
    public void Every_part_of_the_match_must_hold()
    {
        Assert.True(SavedRuleEngine.Matches(Rule("a", from: "@shop.example"), Mail));
        Assert.True(SavedRuleEngine.Matches(Rule("a", to: "me@gmail.com"), Mail));
        Assert.True(SavedRuleEngine.Matches(Rule("a", to: "ann@y.example"), Mail));
        Assert.True(SavedRuleEngine.Matches(Rule("a", from: "@shop.example", to: "me+shop@gmail.com", subject: "OFF EVERY"), Mail));
        Assert.False(SavedRuleEngine.Matches(Rule("a", from: "@shop.example", subject: "invoice"), Mail));
        Assert.False(SavedRuleEngine.Matches(Rule("a", from: "@shop.example", to: "me+other@gmail.com"), Mail));
        Assert.False(SavedRuleEngine.Matches(Rule("a", to: "deals@shop.example"), Mail));
    }

    [Fact]
    public void A_rule_without_a_sender_or_recipient_never_matches()
        => Assert.False(SavedRuleEngine.Matches(Rule("a", subject: "off"), Mail));

    [Fact]
    public void Rules_run_in_order_until_a_live_stop_rule_and_off_rules_are_skipped()
    {
        var run = SavedRuleEngine.Run(
            [
                Rule("off", from: "@shop.example", enabled: false),
                Rule("dry", from: "@shop.example", mode: RuleSetMode.DryRun),
                Rule("label", from: "@shop.example", stop: false),
                Rule("other", from: "@other.example"),
                Rule("settles", to: "me@gmail.com"),
                Rule("never-reached", from: "@shop.example"),
            ],
            Mail);

        Assert.True(run.Stopped);
        Assert.Equal(["dry", "label", "settles"], run.Matched.Select(m => m.Rule.Name));
        Assert.Equal([1, 2, 4], run.Matched.Select(m => m.Index));
    }

    [Fact]
    public void A_dry_run_rule_with_stop_on_does_not_stop()
    {
        var run = SavedRuleEngine.Run([Rule("dry", from: "@shop.example", mode: RuleSetMode.DryRun)], Mail);

        Assert.False(run.Stopped);
        Assert.Single(run.Matched);
    }

    [Fact]
    public void A_match_becomes_a_decision_with_the_saved_rule_review_label()
    {
        var decision = SavedRuleEngine.ToDecision(Rule("shop-deals", from: "@shop.example") with { Actions = ["label:shop", "mark_read"] }, 3);

        Assert.Equal(("shop-deals", 3, RemoveMode.Archive, "nullbox/review/saved-rules/shop-deals"), (decision.Label, decision.Index, decision.Remove, decision.ReviewLabel));
        Assert.Equal([new RuleAction(RuleActionKind.Label, "shop"), new RuleAction(RuleActionKind.MarkRead)], decision.Actions);
    }

    [Fact]
    public void Audit_keys_round_trip()
    {
        Assert.Equal("saved:shop-deals", SavedRule.KeyOf("shop-deals"));
        Assert.True(SavedRule.TryParseKey("saved:shop-deals", out var name));
        Assert.Equal("shop-deals", name);
        Assert.False(SavedRule.TryParseKey("promo-cleanup@v2", out _));
    }
}
