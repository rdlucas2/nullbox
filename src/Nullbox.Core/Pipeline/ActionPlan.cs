using Nullbox.Core.Gmail;
using Nullbox.Core.Rules;

namespace Nullbox.Core.Pipeline;

/// <summary>An action that was matched but will not be applied, and why.</summary>
public sealed record SkippedAction(RuleAction Action, string Decision, string Reason);

/// <summary>
/// What the matched decisions would do to one message: <see cref="Actions"/> in the order they are applied
/// (labels, star, mark read, archive, trash last), de-duplicated, and the actions held back.
/// </summary>
public sealed record ActionPlan(IReadOnlyList<RuleAction> Actions, IReadOnlyList<SkippedAction> Skipped)
{
    public const string ProtectedReason = "message is starred or important (set allow_protected on the decision to allow this)";

    public const string DeleteLockedReason = "delete blocked by lock (Nullbox:Gmail:AllowDelete is off); archived instead";

    public static ActionPlan Empty { get; } = new([], []);

    public bool HasChanges => Actions.Count > 0;

    /// <summary>
    /// Combines the actions of <paramref name="decisions"/>. A starred or important message is never marked read,
    /// archived or trashed unless the decision sets <c>allow_protected</c>; labels and stars only add, so they still
    /// apply. <c>none</c> contributes nothing. While the delete lock is on (<paramref name="allowDelete"/> false),
    /// <c>trash</c> becomes <c>archive</c> and the blocked delete is recorded as skipped, so Trash is never called.
    /// </summary>
    public static ActionPlan Create(GmailMessage message, IReadOnlyList<Decision> decisions, bool allowDelete = false)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(decisions);

        var isProtected = message.LabelIds.Contains(GmailLabels.Starred) || message.LabelIds.Contains(GmailLabels.Important);
        var actions = new List<RuleAction>();
        var skipped = new List<SkippedAction>();
        foreach (var decision in decisions)
        {
            foreach (var action in decision.Actions.Where(a => a.Kind != RuleActionKind.None))
            {
                if (isProtected && !decision.AllowProtected && Hides(action.Kind))
                {
                    skipped.Add(new SkippedAction(action, decision.Label, ProtectedReason));
                }
                else if (action.Kind == RuleActionKind.Trash && !allowDelete)
                {
                    skipped.Add(new SkippedAction(action, decision.Label, DeleteLockedReason));
                    var archive = new RuleAction(RuleActionKind.Archive);
                    if (!actions.Contains(archive))
                    {
                        actions.Add(archive);
                    }
                }
                else if (!actions.Contains(action))
                {
                    actions.Add(action);
                }
            }
        }

        return new ActionPlan([.. actions.OrderBy(a => Order(a.Kind))], skipped);
    }

    /// <summary>Actions that take mail out of view: blocked on protected messages.</summary>
    public static bool Hides(RuleActionKind kind) => kind is RuleActionKind.MarkRead or RuleActionKind.Archive or RuleActionKind.Trash;

    private static int Order(RuleActionKind kind) => kind switch
    {
        RuleActionKind.Label => 0,
        RuleActionKind.Star => 1,
        RuleActionKind.MarkRead => 2,
        RuleActionKind.Archive => 3,
        RuleActionKind.Trash => 4,
        _ => 5,
    };
}

/// <summary>Applies an <see cref="ActionPlan"/> through <see cref="IGmailService"/>. Trash only, never a permanent delete.</summary>
public static class ActionExecutor
{
    /// <summary>
    /// Labels, star, mark read and archive go in one modify call; trash runs last. Every step is safe to repeat,
    /// so a retried job that already applied some of them ends up in the same state.
    /// </summary>
    public static async Task ApplyAsync(IGmailService gmail, string messageId, ActionPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gmail);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentNullException.ThrowIfNull(plan);

        var add = new List<string>();
        var remove = new List<string>();
        var trash = false;
        foreach (var action in plan.Actions)
        {
            switch (action.Kind)
            {
                case RuleActionKind.Label:
                    add.Add(await gmail.EnsureLabelAsync(action.Label!, cancellationToken));
                    break;
                case RuleActionKind.Star:
                    add.Add(GmailLabels.Starred);
                    break;
                case RuleActionKind.MarkRead:
                    remove.Add(GmailLabels.Unread);
                    break;
                case RuleActionKind.Archive:
                    remove.Add(GmailLabels.Inbox);
                    break;
                case RuleActionKind.Trash:
                    trash = true;
                    break;
            }
        }

        string[] ids = [messageId];
        if (add.Count > 0 || remove.Count > 0)
        {
            await gmail.BatchModifyAsync(ids, add.Count > 0 ? add : null, remove.Count > 0 ? remove : null, cancellationToken);
        }

        if (trash)
        {
            await gmail.TrashAsync(ids, cancellationToken);
        }
    }
}
