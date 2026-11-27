using Nullbox.Core.Gmail;
using Nullbox.Core.Rules;

namespace Nullbox.Core.Pipeline;

/// <summary>
/// A matched action or removal that will not be applied, and why. <see cref="Action"/> is its rule text, e.g.
/// <c>mark_read</c>, <c>archive</c> or <c>delete</c>.
/// </summary>
public sealed record SkippedAction(string Action, string Decision, string Reason);

/// <summary>How a message leaves the inbox once the plan's actions have run.</summary>
public enum PlannedRemoval
{
    None,

    /// <summary>Remove INBOX.</summary>
    Archive,

    /// <summary>Move to Trash (recoverable for 30 days). Never a permanent delete.</summary>
    Trash,
}

/// <summary>
/// What the matched decisions would do to one message: <see cref="Actions"/> (labels, including each removing
/// decision's review label, then star, then mark read), de-duplicated and in the order they are applied; then
/// <see cref="Removal"/>; and the actions held back.
/// </summary>
public sealed record ActionPlan(IReadOnlyList<RuleAction> Actions, PlannedRemoval Removal, IReadOnlyList<SkippedAction> Skipped)
{
    public const string ProtectedReason = "message is starred or important (set allow_protected on the decision to allow this)";

    public const string DeleteLockedReason = "delete blocked by lock (Nullbox:Gmail:AllowDelete is off); archived instead";

    public const string GentlerDecisionReason = "another matching decision only archives; archived instead";

    public const string DryRunDecisionReason = "the rule is in dry run; recorded only";

    public static ActionPlan Empty { get; } = new([], PlannedRemoval.None, []);

    public bool HasChanges => Actions.Count > 0 || Removal != PlannedRemoval.None;

    /// <summary>The plan as rule text, in the order it is applied: actions, then <c>archive</c> or <c>delete</c>.</summary>
    public IReadOnlyList<string> Describe() => Removal switch
    {
        PlannedRemoval.Archive => [.. Actions.Select(a => a.ToString()), "archive"],
        PlannedRemoval.Trash => [.. Actions.Select(a => a.ToString()), "delete"],
        _ => [.. Actions.Select(a => a.ToString())],
    };

    /// <summary>
    /// Combines <paramref name="decisions"/>. Each decision that removes mail adds its review label. A starred or
    /// important message is never marked read or removed unless the decision sets <c>allow_protected</c>; labels and
    /// stars only add, so they still apply. <c>none</c> contributes nothing.
    /// The gentlest removal wins: mail goes to Trash only when every matching removing decision is set to delete and
    /// <paramref name="allowDelete"/> is true (the app-wide delete lock is lifted). Otherwise it is archived and each
    /// held-back delete is recorded as skipped, so Trash is never called while the lock is on.
    /// A decision set to <see cref="RemoveMode.DryRun"/> applies nothing: its actions (or <c>dry_run</c> when it has
    /// none) are recorded as skipped.
    /// </summary>
    public static ActionPlan Create(GmailMessage message, IReadOnlyList<Decision> decisions, bool allowDelete = false)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(decisions);

        var isProtected = message.LabelIds.Contains(GmailLabels.Starred) || message.LabelIds.Contains(GmailLabels.Important);
        var actions = new List<RuleAction>();
        var skipped = new List<SkippedAction>();
        var archive = false;
        var deletes = new List<Decision>();
        foreach (var decision in decisions)
        {
            if (decision.Remove == RemoveMode.DryRun)
            {
                // A rule on trial: everything it would do is held back, so the audit log shows it without Gmail changing.
                var held = decision.Actions.Where(a => a.Kind != RuleActionKind.None).Select(a => a.ToString()).ToList();
                skipped.AddRange((held.Count == 0 ? [Text(RemoveMode.DryRun)] : held)
                    .Select(a => new SkippedAction(a, decision.Label, DryRunDecisionReason)));
                continue;
            }

            var mayHide = !isProtected || decision.AllowProtected;
            foreach (var action in decision.Actions.Where(a => a.Kind != RuleActionKind.None))
            {
                if (!mayHide && Hides(action.Kind))
                {
                    skipped.Add(new SkippedAction(action.ToString(), decision.Label, ProtectedReason));
                }
                else
                {
                    AddOnce(actions, action);
                }
            }

            if (decision.Remove is not { } remove)
            {
                continue;
            }

            if (!mayHide)
            {
                skipped.Add(new SkippedAction(Text(remove), decision.Label, ProtectedReason));
                continue;
            }

            if (decision.ReviewLabel is { } reviewLabel)
            {
                AddOnce(actions, new RuleAction(RuleActionKind.Label, reviewLabel));
            }

            if (remove == RemoveMode.Archive)
            {
                archive = true;
            }
            else if (!allowDelete)
            {
                skipped.Add(new SkippedAction(Text(remove), decision.Label, DeleteLockedReason));
                archive = true;
            }
            else
            {
                deletes.Add(decision);
            }
        }

        var removal = PlannedRemoval.None;
        if (deletes.Count > 0 && !archive)
        {
            removal = PlannedRemoval.Trash;
        }
        else if (archive)
        {
            removal = PlannedRemoval.Archive;
            skipped.AddRange(deletes.Select(d => new SkippedAction(Text(RemoveMode.Delete), d.Label, GentlerDecisionReason)));
        }

        return new ActionPlan([.. actions.OrderBy(a => Order(a.Kind))], removal, skipped);
    }

    /// <summary>Actions that take mail out of view: held back on protected messages, like removals.</summary>
    public static bool Hides(RuleActionKind kind) => kind is RuleActionKind.MarkRead;

    private static string Text(RemoveMode remove) => remove switch
    {
        RemoveMode.Delete => "delete",
        RemoveMode.DryRun => "dry_run",
        _ => "archive",
    };

    private static void AddOnce(List<RuleAction> actions, RuleAction action)
    {
        if (!actions.Contains(action))
        {
            actions.Add(action);
        }
    }

    private static int Order(RuleActionKind kind) => kind switch
    {
        RuleActionKind.Label => 0,
        RuleActionKind.Star => 1,
        RuleActionKind.MarkRead => 2,
        _ => 3,
    };
}

/// <summary>Applies an <see cref="ActionPlan"/> through <see cref="IGmailService"/>. Trash only, never a permanent delete.</summary>
public static class ActionExecutor
{
    /// <summary>
    /// Labels, star, mark read and archive go in one modify call; trash runs last, after the review label is on.
    /// Every step is safe to repeat, so a retried job that already applied some of them ends up in the same state.
    /// </summary>
    public static async Task ApplyAsync(IGmailService gmail, string messageId, ActionPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gmail);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentNullException.ThrowIfNull(plan);

        var add = new List<string>();
        var remove = new List<string>();
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
            }
        }

        if (plan.Removal == PlannedRemoval.Archive)
        {
            remove.Add(GmailLabels.Inbox);
        }

        string[] ids = [messageId];
        if (add.Count > 0 || remove.Count > 0)
        {
            await gmail.BatchModifyAsync(ids, add.Count > 0 ? add : null, remove.Count > 0 ? remove : null, cancellationToken);
        }

        if (plan.Removal == PlannedRemoval.Trash)
        {
            await gmail.TrashAsync(ids, cancellationToken);
        }
    }
}
