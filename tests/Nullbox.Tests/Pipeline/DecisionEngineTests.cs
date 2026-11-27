using Nullbox.Core.Gmail;
using Nullbox.Core.Jev;
using Nullbox.Core.Pipeline;
using Nullbox.Core.Rules;
using static Nullbox.Tests.Pipeline.Answers;

namespace Nullbox.Tests.Pipeline;

public sealed class DecisionEngineTests
{
    private static readonly RuleSetDocument Promo = RuleSetSeeds.Load(RuleSetSeeds.PromoCleanup);

    private static RuleSetDocument WithDecisions(DecisionMatch match, params DecisionRule[] decisions) => new()
    {
        Name = "test",
        Match = match,
        Questions = new Dictionary<string, JevQuestion>
        {
            ["category"] = new JevChoiceQuestion
            {
                Criteria = new Dictionary<string, System.Text.Json.Nodes.JsonNode?> { ["promo"] = "Marketing", ["other"] = "Else" },
            },
            ["spam"] = new JevNoulQuestion(),
        },
        Decisions = decisions,
    };

    private static DecisionRule Rule(string name, string when, params string[] actions) => new() { Name = name, When = when, Actions = actions };

    private static string[] Names(IReadOnlyList<Decision> decisions) => [.. decisions.Select(d => d.Label)];

    [Fact]
    public void First_match_wins_in_listed_order()
    {
        var decisions = DecisionEngine.Evaluate(Promo, ConfidentPromo);

        var decision = Assert.Single(decisions);
        Assert.Equal("Archive confident promos", decision.Name);
        Assert.Equal(1, decision.Index);
        Assert.Equal(
            [new RuleAction(RuleActionKind.Label, "nullbox/promo"), new RuleAction(RuleActionKind.MarkRead), new RuleAction(RuleActionKind.Archive)],
            decision.Actions);
    }

    [Fact]
    public void An_earlier_screen_takes_priority_over_later_matches()
    {
        var fromPerson = Of(("from_a_person", Noul(0.8)), ("category", Choice("promo", 0.99)), ("fits_category", Noul(0.99)));

        var decision = Assert.Single(DecisionEngine.Evaluate(Promo, fromPerson));

        Assert.Equal("Never touch mail from real people", decision.Name);
        Assert.Equal([new RuleAction(RuleActionKind.None)], decision.Actions);
    }

    [Theory]
    [InlineData(0.85, "Archive confident promos")] // >= threshold is inclusive
    [InlineData(0.8499, "Label likely promos")]
    public void Thresholds_are_inclusive_and_fall_through_to_the_next_decision(double confidence, string expected)
    {
        var answers = Of(
            ("from_a_person", Noul(0.1)),
            ("category", Choice("promo", confidence, ("promo", confidence))),
            ("fits_category", Noul(0.9)));

        Assert.Equal([expected], Names(DecisionEngine.Evaluate(Promo, answers)));
    }

    [Fact]
    public void Nothing_matches_below_every_threshold()
    {
        var answers = Of(("from_a_person", Noul(0.1)), ("category", Choice("other", 0.9, ("promo", 0.1))), ("fits_category", Noul(0.9)));

        Assert.Empty(DecisionEngine.Evaluate(Promo, answers));
    }

    [Fact]
    public void Missing_outputs_never_match_even_under_negation()
    {
        var set = WithDecisions(
            DecisionMatch.All,
            Rule("missing", "spam.noul >= 0.5", "trash"),
            Rule("negated missing", "!(spam.noul >= 0.5)", "archive"),
            Rule("present", "category.choice == \"promo\"", "label:promo"));

        var decisions = DecisionEngine.Evaluate(set, Of(("category", Choice("promo", 0.9))));

        Assert.Equal(["present"], Names(decisions));
    }

    [Fact]
    public void Missing_answers_fall_through_to_a_later_decision_with_first_match()
    {
        var set = WithDecisions(DecisionMatch.First, Rule("a", "spam.noul >= 0.5", "trash"), Rule("b", "category.confidence >= 0.5", "archive"));

        Assert.Equal(["b"], Names(DecisionEngine.Evaluate(set, Of(("category", Choice("other", 0.6))))));
    }

    [Fact]
    public void Match_all_returns_every_match_in_order_including_ties()
    {
        var set = WithDecisions(
            DecisionMatch.All,
            Rule("label", "category.confidence >= 0.7", "label:promo"),
            Rule("tie", "category.confidence >= 0.7", "label:promo", "mark_read"),
            Rule("no", "category.confidence >= 0.9", "archive"),
            Rule(null!, "spam.noul < 0.5", "star"));

        var decisions = DecisionEngine.Evaluate(set, Of(("category", Choice("promo", 0.7)), ("spam", Noul(0.2))));

        Assert.Equal(["label", "tie", "#3"], Names(decisions));
        Assert.Equal([0, 1, 3], decisions.Select(d => d.Index));
    }

    [Fact]
    public void Same_input_gives_the_same_output()
    {
        var first = DecisionEngine.Evaluate(Promo, ConfidentPromo);
        var second = DecisionEngine.Evaluate(Promo, ConfidentPromo);

        Assert.Equal(Names(first), Names(second));
        Assert.Equal(first.SelectMany(d => d.Actions), second.SelectMany(d => d.Actions));
    }

    [Fact]
    public void Allow_protected_is_carried_from_the_rule()
    {
        var set = WithDecisions(DecisionMatch.First, new DecisionRule { When = "spam.noul >= 0.9", Actions = ["trash"], AllowProtected = true });

        Assert.True(Assert.Single(DecisionEngine.Evaluate(set, Of(("spam", Noul(0.95))))).AllowProtected);
    }
}

public sealed class AllowProtectedJsonTests
{
    [Fact]
    public void Allow_protected_round_trips_and_is_left_out_when_off()
    {
        var seed = RuleSetSeeds.Load(RuleSetSeeds.PromoCleanup);
        Assert.DoesNotContain("allow_protected", RuleJson.Serialize(seed), StringComparison.Ordinal);

        var on = seed with { Decisions = [seed.Decisions[1] with { AllowProtected = true }] };
        var json = RuleJson.Serialize(on);

        Assert.Contains("\"allow_protected\": true", json, StringComparison.Ordinal);
        Assert.True(RuleJson.ParseOrThrow(json).Decisions[0].AllowProtected);
    }

    [Fact]
    public void Allow_protected_must_be_a_boolean()
    {
        var json = RuleJson.Serialize(RuleSetSeeds.Load(RuleSetSeeds.PromoCleanup))
            .Replace("\"actions\": [\n        \"none\"\n      ]", "\"actions\": [\"none\"], \"allow_protected\": \"yes\"", StringComparison.Ordinal);

        Assert.Null(RuleJson.Parse(json, out var errors));
        Assert.Contains(errors, e => e.Path.Contains("allow_protected", StringComparison.Ordinal));
    }
}

public sealed class ActionPlanTests
{
    private static readonly GmailMessage Plain = Message(GmailLabels.Inbox, GmailLabels.Unread);

    private static GmailMessage Message(params string[] labels)
        => new("m1", "t1", "a@b.example", "s", null, null, labels, null, null, null);

    private static Decision D(string name, bool allowProtected, params string[] actions)
        => new(0, name, [.. actions.Select(RuleAction.Parse)], allowProtected);

    [Fact]
    public void Actions_are_deduplicated_and_ordered_with_trash_last()
    {
        var plan = ActionPlan.Create(Plain, [D("a", false, "trash", "label:x", "mark_read"), D("b", false, "label:x", "star", "archive", "label:y")], allowDelete: true);

        Assert.Equal(["label:x", "label:y", "star", "mark_read", "archive", "trash"], plan.Actions.Select(a => a.ToString()));
        Assert.Empty(plan.Skipped);
        Assert.True(plan.HasChanges);
    }

    [Fact]
    public void With_the_delete_lock_on_trash_archives_instead_and_is_recorded_as_blocked()
    {
        var plan = ActionPlan.Create(Plain, [D("junk", false, "label:junk", "trash")]);

        Assert.Equal(["label:junk", "archive"], plan.Actions.Select(a => a.ToString()));
        var skipped = Assert.Single(plan.Skipped);
        Assert.Equal(("trash", "junk", ActionPlan.DeleteLockedReason), (skipped.Action.ToString(), skipped.Decision, skipped.Reason));
    }

    [Fact]
    public void A_blocked_trash_and_an_archive_give_one_archive()
    {
        var plan = ActionPlan.Create(Plain, [D("a", false, "archive"), D("b", false, "trash")]);

        Assert.Equal(["archive"], plan.Actions.Select(a => a.ToString()));
        Assert.Single(plan.Skipped);
    }

    [Fact]
    public void None_and_no_decisions_change_nothing()
    {
        Assert.False(ActionPlan.Create(Plain, [D("screen", false, "none")]).HasChanges);
        Assert.False(ActionPlan.Create(Plain, []).HasChanges);
    }

    [Theory]
    [InlineData(GmailLabels.Starred)]
    [InlineData(GmailLabels.Important)]
    public void Protected_mail_is_never_hidden_but_can_still_be_labelled(string protectedLabel)
    {
        var plan = ActionPlan.Create(Message(GmailLabels.Inbox, protectedLabel), [D("cleanup", false, "label:promo", "star", "mark_read", "archive", "trash")]);

        Assert.Equal(["label:promo", "star"], plan.Actions.Select(a => a.ToString()));
        Assert.Equal(["mark_read", "archive", "trash"], plan.Skipped.Select(s => s.Action.ToString()));
        Assert.All(plan.Skipped, s => Assert.Equal(("cleanup", ActionPlan.ProtectedReason), (s.Decision, s.Reason)));
    }

    [Fact]
    public void Allow_protected_on_the_decision_lets_it_act_on_protected_mail()
    {
        var message = Message(GmailLabels.Starred);

        var plan = ActionPlan.Create(message, [D("explicit", true, "archive"), D("implicit", false, "trash")]);

        Assert.Equal(["archive"], plan.Actions.Select(a => a.ToString()));
        Assert.Equal("trash", Assert.Single(plan.Skipped).Action.ToString());
    }
}

public sealed class ActionExecutorTests
{
    [Fact]
    public async Task Labels_star_read_and_archive_go_in_one_modify_then_trash()
    {
        var gmail = new Inbox.FakeInboxGmail { AllowDelete = true };
        var plan = new ActionPlan([.. new[] { "label:promo", "star", "mark_read", "archive", "trash" }.Select(RuleAction.Parse)], []);

        await ActionExecutor.ApplyAsync(gmail, "m1", plan, default);

        Assert.Equal(["label:promo", "modify:m1:+Label_promo,STARRED:-UNREAD,INBOX", "trash:m1"], gmail.Changes);
    }

    [Fact]
    public async Task Only_what_the_plan_asks_for_is_called()
    {
        var gmail = new Inbox.FakeInboxGmail { AllowDelete = true };

        await ActionExecutor.ApplyAsync(gmail, "m1", new ActionPlan([RuleAction.Parse("trash")], []), default);
        await ActionExecutor.ApplyAsync(gmail, "m2", ActionPlan.Empty, default);
        await ActionExecutor.ApplyAsync(gmail, "m3", new ActionPlan([RuleAction.Parse("archive")], []), default);

        Assert.Equal(["trash:m1", "modify:m3:+:-INBOX"], gmail.Changes);
    }
}
