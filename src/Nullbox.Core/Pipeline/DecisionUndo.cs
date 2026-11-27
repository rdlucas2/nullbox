using Nullbox.Core.Gmail;
using Nullbox.Core.Rules;

namespace Nullbox.Core.Pipeline;

/// <summary>
/// How to put a message back the way it was before a live decision changed it. Each step is skipped when the
/// message's labels at decision time (<see cref="DecisionRecord.Labels"/>) show it would change nothing, so a label
/// the message already had is not taken off. Records from before labels were stored undo every step.
/// </summary>
/// <param name="Steps">What undoing does, in words, e.g. <c>Move it back to the inbox</c>.</param>
/// <param name="AddLabelIds">System labels to put back (INBOX, UNREAD).</param>
/// <param name="RemoveLabelIds">System labels to take off (STARRED).</param>
/// <param name="RemoveLabelNames">User labels the decision added, by name.</param>
/// <param name="Untrash">Whether the message goes back out of Trash.</param>
/// <param name="Unavailable">Why there is nothing to undo; the steps are then empty.</param>
public sealed record UndoPlan(
    IReadOnlyList<string> Steps,
    IReadOnlyList<string> AddLabelIds,
    IReadOnlyList<string> RemoveLabelIds,
    IReadOnlyList<string> RemoveLabelNames,
    bool Untrash,
    string? Unavailable)
{
    public bool CanUndo => Unavailable is null;
}

public static class DecisionUndo
{
    public const string NotAppliedReason = "Only decisions applied in live mode changed Gmail.";

    public const string NothingToUndoReason = "This decision made no change that can be undone.";

    public static UndoPlan Plan(DecisionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record is not { Mode: RuleSetMode.Live, Outcome: DecisionOutcome.Applied })
        {
            return Unavailable(NotAppliedReason);
        }

        // Older records kept no labels; then every step runs.
        var known = record.Labels.Count > 0;
        bool Had(string label) => !known || record.Labels.Contains(label);
        bool Lacked(string label) => !known || !record.Labels.Contains(label);

        var steps = new List<string>();
        var add = new List<string>();
        var remove = new List<string>();
        var removeNames = new List<string>();
        var untrash = false;
        foreach (var action in record.Actions)
        {
            switch (action)
            {
                case "delete":
                    untrash = true;
                    steps.Add("Take it out of Trash");
                    if (Had(GmailLabels.Inbox) && !add.Contains(GmailLabels.Inbox))
                    {
                        add.Add(GmailLabels.Inbox);
                        steps.Add("Move it back to the inbox");
                    }

                    break;
                case "archive" when Had(GmailLabels.Inbox) && !add.Contains(GmailLabels.Inbox):
                    add.Add(GmailLabels.Inbox);
                    steps.Add("Move it back to the inbox");
                    break;
                case "mark_read" when Had(GmailLabels.Unread):
                    add.Add(GmailLabels.Unread);
                    steps.Add("Mark it unread");
                    break;
                case "star" when Lacked(GmailLabels.Starred):
                    remove.Add(GmailLabels.Starred);
                    steps.Add("Remove the star");
                    break;
                default:
                    if (RuleAction.TryParse(action, out var parsed, out _) && parsed is { Kind: RuleActionKind.Label, Label: { } label })
                    {
                        removeNames.Add(label);
                        steps.Add($"Remove the label {label}");
                    }

                    break;
            }
        }

        return steps.Count == 0
            ? Unavailable(NothingToUndoReason)
            : new UndoPlan(steps, add, remove, removeNames, untrash, null);
    }

    /// <summary>
    /// Applies <paramref name="plan"/> to the record's message. Labels the message had before the decision are kept.
    /// Undo restores mail, so it runs whatever the delete lock and kill switch say.
    /// </summary>
    /// <returns>The steps that were taken.</returns>
    public static async Task<IReadOnlyList<string>> ApplyAsync(IGmailService gmail, DecisionRecord record, UndoPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gmail);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.CanUndo)
        {
            throw new InvalidOperationException(plan.Unavailable);
        }

        string[] ids = [record.MessageId];
        if (plan.Untrash)
        {
            await gmail.UntrashAsync(ids, cancellationToken);
        }

        var steps = plan.Steps.ToList();
        var remove = plan.RemoveLabelIds.ToList();
        foreach (var name in plan.RemoveLabelNames)
        {
            var id = await gmail.EnsureLabelAsync(name, cancellationToken);
            if (record.Labels.Contains(id))
            {
                steps.Remove($"Remove the label {name}");
            }
            else
            {
                remove.Add(id);
            }
        }

        if (plan.AddLabelIds.Count > 0 || remove.Count > 0)
        {
            await gmail.BatchModifyAsync(ids, plan.AddLabelIds.Count > 0 ? plan.AddLabelIds : null, remove.Count > 0 ? remove : null, cancellationToken);
        }

        return steps;
    }

    private static UndoPlan Unavailable(string reason) => new([], [], [], [], false, reason);
}
