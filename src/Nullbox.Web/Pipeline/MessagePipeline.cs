using System.Text.Json;
using Microsoft.Extensions.Options;
using Nullbox.Core.Gmail;
using Nullbox.Core.Inbox;
using Nullbox.Core.Jev;
using Nullbox.Core.Options;
using Nullbox.Core.Pipeline;
using Nullbox.Core.Rules;

namespace Nullbox.Web.Pipeline;

/// <summary>
/// Processes one inbox message: for each active rule set, build the Jev request from the email, ask Jev, run the
/// decision engine, and record the verdict. Gmail is only changed for a <c>live</c> rule set, with the kill switch off
/// and the hourly cap not reached. A rule-set version that already has a verdict on the message is skipped, so a
/// retried job does not ask Jev or act twice.
/// </summary>
public sealed class MessagePipeline(
    IRuleSetStore ruleSets,
    IDecisionLog log,
    IPipelineSettingsStore settings,
    IGmailService gmail,
    IJevClient jev,
    IOptions<NullboxOptions> options,
    TimeProvider time,
    ILogger<MessagePipeline> logger) : IMessageProcessor
{
    public async Task ProcessAsync(string messageId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);

        var pending = new List<RuleSetVersion>();
        foreach (var summary in await ruleSets.ListAsync(cancellationToken))
        {
            if (summary.ActiveVersion is null
                || await ruleSets.GetVersionAsync(summary.Name, null, cancellationToken) is not { } version)
            {
                continue;
            }

            if (!await log.HasRecordAsync(messageId, version.Key, cancellationToken))
            {
                pending.Add(version);
            }
        }

        if (pending.Count == 0)
        {
            logger.LogDebug("Message {MessageId}: no active rule set left to apply", messageId);
            return;
        }

        var needsBody = pending.Any(v => v.Document.State.Fields.Contains(StateFields.Body));
        var message = await gmail.GetMessageAsync(messageId, needsBody ? MessageFormat.Full : MessageFormat.Metadata, cancellationToken);

        foreach (var version in pending)
        {
            var request = JevRequestBuilder.Build(version.Document, message, time.GetUtcNow());
            var result = await jev.EvaluateAsync(request, new JevCallContext(messageId, version.Key), cancellationToken);
            var decisions = DecisionEngine.Evaluate(version.Document, result);
            var plan = ActionPlan.Create(message, decisions);
            var outcome = await OutcomeAsync(version.Document.Mode, plan, cancellationToken);

            if (outcome == DecisionOutcome.Applied)
            {
                await ActionExecutor.ApplyAsync(gmail, messageId, plan, cancellationToken);
                message = AfterApply(message, plan);
            }

            await log.RecordAsync(
                new DecisionRecord(
                    messageId,
                    version.Key,
                    version.Document.Mode,
                    outcome,
                    [.. decisions.Select(d => d.Label)],
                    [.. plan.Actions.Select(a => a.ToString())],
                    [.. plan.Skipped.Select(s => $"{s.Action} ({s.Decision}): {s.Reason}")],
                    result.Model,
                    JsonSerializer.Serialize(result.Answers, JevJson.Options),
                    time.GetUtcNow()),
                cancellationToken);

            logger.LogInformation(
                "Message {MessageId}: {RuleSet} matched {Decisions} decision(s), {Outcome} with {Actions} action(s)",
                messageId,
                version.Key,
                decisions.Count,
                outcome,
                plan.Actions.Count);
        }
    }

    private async Task<DecisionOutcome> OutcomeAsync(RuleSetMode mode, ActionPlan plan, CancellationToken cancellationToken)
    {
        if (mode != RuleSetMode.Live)
        {
            return DecisionOutcome.DryRun;
        }

        if (!plan.HasChanges)
        {
            return DecisionOutcome.NoChange;
        }

        var limits = options.Value.Pipeline;
        if (limits.KillSwitch || await settings.GetKillSwitchAsync(cancellationToken))
        {
            return DecisionOutcome.KillSwitch;
        }

        if (limits.MaxMessagesPerHour <= 0
            || await log.CountAppliedSinceAsync(time.GetUtcNow().AddHours(-1), cancellationToken) >= limits.MaxMessagesPerHour)
        {
            return DecisionOutcome.CapReached;
        }

        return DecisionOutcome.Applied;
    }

    /// <summary>The system labels after the plan ran, so a later rule set sees a message just starred as protected.</summary>
    private static GmailMessage AfterApply(GmailMessage message, ActionPlan plan)
    {
        var labels = message.LabelIds.ToList();
        foreach (var action in plan.Actions)
        {
            switch (action.Kind)
            {
                case RuleActionKind.Star when !labels.Contains(GmailLabels.Starred):
                    labels.Add(GmailLabels.Starred);
                    break;
                case RuleActionKind.MarkRead:
                    labels.Remove(GmailLabels.Unread);
                    break;
                case RuleActionKind.Archive:
                    labels.Remove(GmailLabels.Inbox);
                    break;
                case RuleActionKind.Trash when !labels.Contains(GmailLabels.Trash):
                    labels.Add(GmailLabels.Trash);
                    break;
            }
        }

        return message with { LabelIds = labels };
    }
}
