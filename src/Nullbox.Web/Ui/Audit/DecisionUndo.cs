using Nullbox.Core.Gmail;
using Nullbox.Core.Pipeline;

namespace Nullbox.Web.Ui.Audit;

/// <summary>What an undo did, or why it could not run.</summary>
public sealed record UndoResult(bool Done, IReadOnlyList<string> Reversed, string? Problem)
{
    public static UndoResult Refused(string problem) => new(false, [], problem);
}

/// <summary>
/// Reverses an <see cref="DecisionOutcome.Applied"/> decision: takes off the labels and star it added, marks unread
/// and puts back in the inbox what it read or removed, and takes deleted mail back out of Trash. Only what the rule
/// changed is reversed: <see cref="DecisionRecord.Labels"/> (the labels when it ran) says what was already there.
/// The undo is recorded as an <see cref="DecisionOutcome.Undone"/> record, so it shows in the audit log and can't run twice.
/// </summary>
public sealed class DecisionUndo(IGmailService gmail, IDecisionLog log, TimeProvider time)
{
    public static bool CanUndo(DecisionRecord record) => record.Outcome == DecisionOutcome.Applied && record.Actions.Count > 0;

    public async Task<bool> IsUndoneAsync(DecisionRecord record, CancellationToken cancellationToken = default)
    {
        var page = await log.QueryAsync(new DecisionQuery { MessageId = record.MessageId, Outcome = DecisionOutcome.Undone, Limit = 200 }, cancellationToken);
        return page.Items.Any(r => r.RuleSetKey == record.RuleSetKey && r.Id > record.Id);
    }

    public async Task<UndoResult> UndoAsync(DecisionRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!CanUndo(record))
        {
            return UndoResult.Refused("Only applied decisions that changed something can be undone.");
        }

        if (await IsUndoneAsync(record, cancellationToken))
        {
            return UndoResult.Refused("This decision was already undone.");
        }

        var before = record.Labels.ToHashSet(StringComparer.Ordinal);
        var add = new List<string>();
        var remove = new List<string>();
        var reversed = new List<string>();
        var untrash = false;
        foreach (var action in record.Actions)
        {
            if (action.StartsWith("label:", StringComparison.Ordinal))
            {
                var name = action["label:".Length..];
                var id = await gmail.EnsureLabelAsync(name, cancellationToken);
                if (!before.Contains(id))
                {
                    remove.Add(id);
                    reversed.Add($"removed label {name}");
                }

                continue;
            }

            switch (action)
            {
                case "star" when !before.Contains(GmailLabels.Starred):
                    remove.Add(GmailLabels.Starred);
                    reversed.Add("unstarred");
                    break;
                case "mark_read" when before.Contains(GmailLabels.Unread):
                    add.Add(GmailLabels.Unread);
                    reversed.Add("marked unread");
                    break;
                case "archive" when before.Contains(GmailLabels.Inbox):
                    add.Add(GmailLabels.Inbox);
                    reversed.Add("moved back to the inbox");
                    break;
                case "delete":
                    untrash = true;
                    reversed.Add("restored from Trash");
                    if (before.Contains(GmailLabels.Inbox))
                    {
                        add.Add(GmailLabels.Inbox);
                        reversed.Add("moved back to the inbox");
                    }

                    break;
            }
        }

        string[] ids = [record.MessageId];
        if (untrash)
        {
            await gmail.UntrashAsync(ids, cancellationToken);
        }

        if (add.Count > 0 || remove.Count > 0)
        {
            await gmail.BatchModifyAsync(ids, add.Count > 0 ? add : null, remove.Count > 0 ? remove : null, cancellationToken);
        }

        await log.RecordAsync(
            new DecisionRecord(
                record.MessageId,
                record.RuleSetKey,
                record.Mode,
                DecisionOutcome.Undone,
                record.Decisions,
                reversed,
                [],
                record.Model,
                "{}",
                time.GetUtcNow())
            { Labels = record.Labels },
            cancellationToken);
        return new UndoResult(true, reversed, null);
    }
}
