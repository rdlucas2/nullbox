using Nullbox.Core.Gmail;
using Nullbox.Core.Pipeline;
using Nullbox.Core.Rules;
using Nullbox.Tests.Inbox;

namespace Nullbox.Tests.Pipeline;

public sealed class DecisionUndoTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);

    private static DecisionRecord Record(RuleSetMode mode, DecisionOutcome outcome, IReadOnlyList<string> labels, params string[] actions)
        => new("m1", "promo-cleanup@v1", mode, outcome, ["Archive confident promos"], actions, [], "jev", "{}", At) { Labels = labels };

    [Theory]
    [InlineData(RuleSetMode.DryRun, DecisionOutcome.DryRun)]
    [InlineData(RuleSetMode.Live, DecisionOutcome.KillSwitch)]
    [InlineData(RuleSetMode.Live, DecisionOutcome.CapReached)]
    [InlineData(RuleSetMode.Live, DecisionOutcome.Error)]
    public void Only_applied_live_decisions_can_be_undone(RuleSetMode mode, DecisionOutcome outcome)
    {
        var plan = DecisionUndo.Plan(Record(mode, outcome, [GmailLabels.Inbox], "archive"));

        Assert.False(plan.CanUndo);
        Assert.Equal(DecisionUndo.NotAppliedReason, plan.Unavailable);
        Assert.Empty(plan.Steps);
    }

    [Fact]
    public void Archive_and_mark_read_are_reversed_from_the_labels_the_message_had()
    {
        var plan = DecisionUndo.Plan(Record(RuleSetMode.Live, DecisionOutcome.Applied, [GmailLabels.Inbox, GmailLabels.Unread], "label:nullbox/promo", "mark_read", "archive"));

        Assert.True(plan.CanUndo);
        Assert.Equal(["Remove the label nullbox/promo", "Mark it unread", "Move it back to the inbox"], plan.Steps);
        Assert.Equal([GmailLabels.Unread, GmailLabels.Inbox], plan.AddLabelIds);
        Assert.Equal(["nullbox/promo"], plan.RemoveLabelNames);
        Assert.False(plan.Untrash);
    }

    [Fact]
    public void Steps_that_would_change_nothing_are_left_out()
    {
        // Already read, already starred: undoing mark_read and star would change the message, so they are skipped.
        var plan = DecisionUndo.Plan(Record(RuleSetMode.Live, DecisionOutcome.Applied, [GmailLabels.Inbox, GmailLabels.Starred], "star", "mark_read"));

        Assert.False(plan.CanUndo);
        Assert.Equal(DecisionUndo.NothingToUndoReason, plan.Unavailable);
    }

    [Fact]
    public void A_delete_is_untrashed_and_put_back_in_the_inbox()
    {
        var plan = DecisionUndo.Plan(Record(RuleSetMode.Live, DecisionOutcome.Applied, [GmailLabels.Inbox], "delete"));

        Assert.True(plan.Untrash);
        Assert.Equal(["Take it out of Trash", "Move it back to the inbox"], plan.Steps);
        Assert.Equal([GmailLabels.Inbox], plan.AddLabelIds);
    }

    [Fact]
    public void Records_without_labels_undo_every_step()
    {
        var plan = DecisionUndo.Plan(Record(RuleSetMode.Live, DecisionOutcome.Applied, [], "star", "mark_read", "archive"));

        Assert.Equal(["Remove the star", "Mark it unread", "Move it back to the inbox"], plan.Steps);
        Assert.Equal([GmailLabels.Starred], plan.RemoveLabelIds);
    }

    [Fact]
    public async Task Applying_untrashes_then_modifies_in_one_call()
    {
        var gmail = new FakeInboxGmail();
        var record = Record(RuleSetMode.Live, DecisionOutcome.Applied, [GmailLabels.Inbox, GmailLabels.Unread], "label:nullbox/promo", "mark_read", "delete");
        var plan = DecisionUndo.Plan(record);

        var steps = await DecisionUndo.ApplyAsync(gmail, record, plan);

        Assert.Equal(["untrash:m1", "label:nullbox/promo", "modify:m1:+UNREAD,INBOX:-Label_nullbox/promo"], gmail.Changes);
        Assert.Equal(plan.Steps, steps);
    }

    [Fact]
    public async Task A_label_the_message_already_had_is_kept()
    {
        var gmail = new FakeInboxGmail();
        var record = Record(RuleSetMode.Live, DecisionOutcome.Applied, [GmailLabels.Inbox, "Label_nullbox/promo"], "label:nullbox/promo", "archive");

        var steps = await DecisionUndo.ApplyAsync(gmail, record, DecisionUndo.Plan(record));

        Assert.Equal(["label:nullbox/promo", "modify:m1:+INBOX:-"], gmail.Changes);
        Assert.Equal(["Move it back to the inbox"], steps);
    }

    [Fact]
    public async Task Applying_a_plan_with_nothing_to_undo_throws()
    {
        var record = Record(RuleSetMode.DryRun, DecisionOutcome.DryRun, [], "archive");

        await Assert.ThrowsAsync<InvalidOperationException>(() => DecisionUndo.ApplyAsync(new FakeInboxGmail(), record, DecisionUndo.Plan(record)));
    }
}
