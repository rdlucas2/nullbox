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

    private static DecisionRule Removing(string name, string when, RemoveMode remove) => new() { Name = name, When = when, Remove = remove };

    private static string[] Names(IReadOnlyList<Decision> decisions) => [.. decisions.Select(d => d.Label)];

    [Fact]
    public void First_match_wins_in_listed_order()
    {
        var decisions = DecisionEngine.Evaluate(Promo, ConfidentPromo);

        var decision = Assert.Single(decisions);
        Assert.Equal("Archive confident promos", decision.Name);
        Assert.Equal(1, decision.Index);
        Assert.Equal([new RuleAction(RuleActionKind.Label, "nullbox/promo"), new RuleAction(RuleActionKind.MarkRead)], decision.Actions);
        Assert.Equal((RemoveMode.Archive, "nullbox/review/promo-cleanup/archive-confident-promos"), (decision.Remove!.Value, decision.ReviewLabel));
    }

    [Fact]
    public void Decisions_that_keep_mail_have_no_review_label()
    {
        var set = WithDecisions(DecisionMatch.First, Rule("tag", "category.choice == \"promo\"", "label:promo"));

        var decision = Assert.Single(DecisionEngine.Evaluate(set, Of(("category", Choice("promo", 0.9)))));

        Assert.Equal((null, null), (decision.Remove, decision.ReviewLabel));
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
            Removing("missing", "spam.noul >= 0.5", RemoveMode.Delete),
            Removing("negated missing", "!(spam.noul >= 0.5)", RemoveMode.Archive),
            Rule("present", "category.choice == \"promo\"", "label:promo"));

        var decisions = DecisionEngine.Evaluate(set, Of(("category", Choice("promo", 0.9))));

        Assert.Equal(["present"], Names(decisions));
    }

    [Fact]
    public void Missing_answers_fall_through_to_a_later_decision_with_first_match()
    {
        var set = WithDecisions(DecisionMatch.First, Removing("a", "spam.noul >= 0.5", RemoveMode.Delete), Removing("b", "category.confidence >= 0.5", RemoveMode.Archive));

        Assert.Equal(["b"], Names(DecisionEngine.Evaluate(set, Of(("category", Choice("other", 0.6))))));
    }

    [Fact]
    public void Match_all_returns_every_match_in_order_including_ties()
    {
        var set = WithDecisions(
            DecisionMatch.All,
            Rule("label", "category.confidence >= 0.7", "label:promo"),
            Rule("tie", "category.confidence >= 0.7", "label:promo", "mark_read"),
            Removing("no", "category.confidence >= 0.9", RemoveMode.Archive),
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
        var set = WithDecisions(DecisionMatch.First, new DecisionRule { When = "spam.noul >= 0.9", Remove = RemoveMode.Delete, AllowProtected = true });

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

    private static Decision R(string name, RemoveMode remove, bool allowProtected = false, params string[] actions)
        => new(0, name, [.. actions.Select(RuleAction.Parse)], allowProtected, remove, $"nullbox/review/test/{name}");

    [Fact]
    public void Actions_are_deduplicated_and_ordered_labels_then_star_then_read()
    {
        var plan = ActionPlan.Create(Plain, [D("a", false, "mark_read", "label:x"), D("b", false, "label:x", "star", "label:y")]);

        Assert.Equal(["label:x", "label:y", "star", "mark_read"], plan.Describe());
        Assert.Equal(PlannedRemoval.None, plan.Removal);
        Assert.Empty(plan.Skipped);
        Assert.True(plan.HasChanges);
    }

    [Fact]
    public void A_dry_run_rule_holds_back_everything_it_would_do()
    {
        var plan = ActionPlan.Create(Plain, [R("trial", RemoveMode.DryRun, actions: ["label:promo", "mark_read"])], allowDelete: true);

        Assert.False(plan.HasChanges);
        Assert.Empty(plan.Describe());
        Assert.Equal(
            [new SkippedAction("label:promo", "trial", ActionPlan.DryRunDecisionReason), new SkippedAction("mark_read", "trial", ActionPlan.DryRunDecisionReason)],
            plan.Skipped);
    }

    [Fact]
    public void A_dry_run_rule_without_actions_is_still_recorded_and_others_still_apply()
    {
        var plan = ActionPlan.Create(Plain, [R("trial", RemoveMode.DryRun), R("trusted", RemoveMode.Archive)]);

        Assert.Equal(["label:nullbox/review/test/trusted", "archive"], plan.Describe());
        Assert.Equal([new SkippedAction("dry_run", "trial", ActionPlan.DryRunDecisionReason)], plan.Skipped);
    }

    [Fact]
    public void Archive_adds_the_review_label_and_removes_last()
    {
        var plan = ActionPlan.Create(Plain, [R("junk", RemoveMode.Archive, actions: "mark_read")]);

        Assert.Equal(["label:nullbox/review/test/junk", "mark_read", "archive"], plan.Describe());
        Assert.Empty(plan.Skipped);
    }

    [Fact]
    public void Delete_trashes_when_the_lock_is_lifted()
    {
        var plan = ActionPlan.Create(Plain, [R("junk", RemoveMode.Delete)], allowDelete: true);

        Assert.Equal(PlannedRemoval.Trash, plan.Removal);
        Assert.Equal(["label:nullbox/review/test/junk", "delete"], plan.Describe());
        Assert.Empty(plan.Skipped);
    }

    [Fact]
    public void While_deletes_are_locked_delete_archives_and_is_recorded_as_skipped()
    {
        var plan = ActionPlan.Create(Plain, [R("junk", RemoveMode.Delete, actions: "label:x")]);

        Assert.Equal(["label:x", "label:nullbox/review/test/junk", "archive"], plan.Describe());
        var skipped = Assert.Single(plan.Skipped);
        Assert.Equal(("delete", "junk", ActionPlan.DeleteLockedReason), (skipped.Action, skipped.Decision, skipped.Reason));
    }

    [Fact]
    public void The_gentlest_removal_wins_when_several_decisions_match()
    {
        var plan = ActionPlan.Create(Plain, [R("trusted", RemoveMode.Delete), R("trial", RemoveMode.Archive)], allowDelete: true);

        Assert.Equal(PlannedRemoval.Archive, plan.Removal);
        Assert.Equal(["label:nullbox/review/test/trusted", "label:nullbox/review/test/trial", "archive"], plan.Describe());
        var skipped = Assert.Single(plan.Skipped);
        Assert.Equal(("delete", "trusted", ActionPlan.GentlerDecisionReason), (skipped.Action, skipped.Decision, skipped.Reason));
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
        var plan = ActionPlan.Create(
            Message(GmailLabels.Inbox, protectedLabel),
            [R("cleanup", RemoveMode.Delete, actions: ["label:promo", "star", "mark_read"])],
            allowDelete: true);

        Assert.Equal(["label:promo", "star"], plan.Describe());
        Assert.Equal(["mark_read", "delete"], plan.Skipped.Select(s => s.Action));
        Assert.All(plan.Skipped, s => Assert.Equal(("cleanup", ActionPlan.ProtectedReason), (s.Decision, s.Reason)));
    }

    [Fact]
    public void Allow_protected_on_the_decision_lets_it_act_on_protected_mail()
    {
        var plan = ActionPlan.Create(Message(GmailLabels.Starred), [R("explicit", RemoveMode.Archive, allowProtected: true), R("implicit", RemoveMode.Delete)]);

        Assert.Equal(["label:nullbox/review/test/explicit", "archive"], plan.Describe());
        Assert.Equal(("delete", ActionPlan.ProtectedReason), (Assert.Single(plan.Skipped).Action, plan.Skipped[0].Reason));
    }
}

public sealed class ActionExecutorTests
{
    private static ActionPlan Plan(PlannedRemoval removal, params string[] actions) => new([.. actions.Select(RuleAction.Parse)], removal, []);

    [Fact]
    public async Task Labels_star_read_and_archive_go_in_one_modify()
    {
        var gmail = new Inbox.FakeInboxGmail();

        await ActionExecutor.ApplyAsync(gmail, "m1", Plan(PlannedRemoval.Archive, "label:promo", "star", "mark_read"), default);

        Assert.Equal(["label:promo", "modify:m1:+Label_promo,STARRED:-UNREAD,INBOX"], gmail.Changes);
    }

    [Fact]
    public async Task Trash_runs_after_the_labels_are_on()
    {
        var gmail = new Inbox.FakeInboxGmail { AllowDelete = true };

        await ActionExecutor.ApplyAsync(gmail, "m1", Plan(PlannedRemoval.Trash, "label:review"), default);

        Assert.Equal(["label:review", "modify:m1:+Label_review:-", "trash:m1"], gmail.Changes);
    }

    [Fact]
    public async Task Only_what_the_plan_asks_for_is_called()
    {
        var gmail = new Inbox.FakeInboxGmail { AllowDelete = true };

        await ActionExecutor.ApplyAsync(gmail, "m1", Plan(PlannedRemoval.Trash), default);
        await ActionExecutor.ApplyAsync(gmail, "m2", ActionPlan.Empty, default);
        await ActionExecutor.ApplyAsync(gmail, "m3", Plan(PlannedRemoval.Archive), default);

        Assert.Equal(["trash:m1", "modify:m3:+:-INBOX"], gmail.Changes);
    }
}
