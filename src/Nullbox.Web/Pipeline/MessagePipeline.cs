using System.Text.Json;
using Microsoft.Extensions.Options;
using Nullbox.Core.Gmail;
using Nullbox.Core.Inbox;
using Nullbox.Core.Jev;
using Nullbox.Core.Options;
using Nullbox.Core.Pipeline;
using Nullbox.Core.Rules;
using Nullbox.Core.SavedRules;

namespace Nullbox.Web.Pipeline;

/// <summary>
/// Processes one inbox message. Saved rules run first, in order, without asking Jev; a live saved rule with
/// <c>stop</c> on settles the message, so the later saved rules and the rule sets are skipped. Then, for each active rule set, build the Jev request from the email, ask Jev, run the
/// decision engine, and record the verdict. Gmail is only changed for a <c>live</c> rule set, with the kill switch off
/// and the hourly cap not reached. A rule-set version that already has a verdict on the message is skipped, so a
/// retried job does not ask Jev or act twice. Each verdict is an audit record with a hash of the inputs, the inputs
/// with the body cut short, Jev's answers and the actions; a failed attempt is recorded as
/// <see cref="DecisionOutcome.Error"/> and the failure rethrown.
/// </summary>
public sealed class MessagePipeline(
    IRuleSetStore ruleSets,
    ISavedRuleStore savedRules,
    IDecisionLog log,
    IPipelineSettingsStore settings,
    IGmailService gmail,
    IJevClient jev,
    IOptions<NullboxOptions> options,
    IOptions<GmailOptions> gmailOptions,
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

        var saved = (await savedRules.ListAsync(cancellationToken)).Select(r => r.Rule).Where(r => r.Enabled).ToList();
        if (pending.Count == 0 && saved.Count == 0)
        {
            logger.LogDebug("Message {MessageId}: no active rule set left to apply", messageId);
            return;
        }

        var needsBody = pending.Any(v => v.Document.State.Fields.Contains(StateFields.Body));
        var message = await gmail.GetMessageAsync(messageId, needsBody ? MessageFormat.Full : MessageFormat.Metadata, cancellationToken);

        var run = SavedRuleEngine.Run(saved, SavedRuleInput.Of(message));
        foreach (var (rule, index) in run.Matched)
        {
            // A retry skips rules that already have a verdict, but a recorded stop still stops.
            if (!await log.HasRecordAsync(messageId, rule.Key, cancellationToken))
            {
                message = await ApplySavedRuleAsync(message, rule, index, cancellationToken);
            }
        }

        if (run.Stopped)
        {
            logger.LogInformation(
                "Message {MessageId}: saved rule {Rule} settled it, so {Count} rule set(s) were skipped",
                messageId,
                run.Matched[^1].Rule.Name,
                pending.Count);
            return;
        }

        foreach (var version in pending)
        {
            var request = JevRequestBuilder.Build(version.Document, message, time.GetUtcNow());
            var audit = new DecisionRecord(
                messageId,
                version.Key,
                version.Document.Mode,
                DecisionOutcome.Error,
                [],
                [],
                [],
                string.Empty,
                "{}",
                time.GetUtcNow())
            {
                InputsHash = AuditInputs.Hash(request.State),
                StateJson = AuditInputs.Redact(request.State, options.Value.Audit.StoredBodyChars).ToJsonString(),
                Labels = message.LabelIds,
            };

            try
            {
                var result = await jev.EvaluateAsync(request, new JevCallContext(messageId, version.Key), cancellationToken);
                var decisions = DecisionEngine.Evaluate(version.Document, result);
                var plan = ActionPlan.Create(message, decisions, gmailOptions.Value.AllowDelete);

                // Filled in before Gmail is touched, so a failed apply still records what was decided.
                audit = audit with
                {
                    Decisions = [.. decisions.Select(d => d.Label)],
                    Actions = plan.Describe(),
                    Skipped = [.. plan.Skipped.Select(s => $"{s.Action} ({s.Decision}): {s.Reason}")],
                    Model = result.Model,
                    AnswersJson = JsonSerializer.Serialize(result.Answers, JevJson.Options),
                    Confidence = AnswerConfidence.Lowest(result.Answers),
                };

                var outcome = await OutcomeAsync(version.Document.Mode, plan, cancellationToken);
                if (outcome == DecisionOutcome.Applied)
                {
                    await ActionExecutor.ApplyAsync(gmail, messageId, plan, cancellationToken);
                    message = AfterApply(message, plan);
                }

                audit = audit with { Outcome = outcome, At = time.GetUtcNow() };
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // Keep a trace of the failed attempt; Error records don't count as a verdict, so the retry runs again.
                await log.RecordAsync(audit with { Error = $"{ex.GetType().Name}: {ex.Message}", At = time.GetUtcNow() }, CancellationToken.None);
                throw;
            }

            await log.RecordAsync(audit, cancellationToken);
            logger.LogInformation(
                "Message {MessageId}: {RuleSet} matched {Decisions} decision(s), {Outcome} with {Actions} action(s)",
                messageId,
                version.Key,
                audit.Decisions.Count,
                audit.Outcome,
                audit.Actions.Count);
        }
    }

    /// <summary>
    /// Applies one matching saved rule through the same plan, safety checks and audit record as a rule set. Its audit
    /// key is <c>saved:&lt;name&gt;</c>; it has no Jev answers, its confidence is 1, and its inputs are the headers it
    /// matched on.
    /// </summary>
    private async Task<GmailMessage> ApplySavedRuleAsync(GmailMessage message, SavedRule rule, int index, CancellationToken cancellationToken)
    {
        var inputs = SavedRuleInput.Of(message).ToJson();
        var plan = ActionPlan.Create(message, [SavedRuleEngine.ToDecision(rule, index)], gmailOptions.Value.AllowDelete);
        var audit = new DecisionRecord(
            message.Id,
            rule.Key,
            rule.Mode,
            DecisionOutcome.Error,
            [rule.Name],
            plan.Describe(),
            [.. plan.Skipped.Select(s => $"{s.Action} ({s.Decision}): {s.Reason}")],
            string.Empty,
            "{}",
            time.GetUtcNow())
        {
            InputsHash = AuditInputs.Hash(inputs),
            StateJson = inputs.ToJsonString(),
            Labels = message.LabelIds,
            Confidence = 1,
        };

        try
        {
            var outcome = await OutcomeAsync(rule.Mode, plan, cancellationToken);
            if (outcome == DecisionOutcome.Applied)
            {
                await ActionExecutor.ApplyAsync(gmail, message.Id, plan, cancellationToken);
                message = AfterApply(message, plan);
            }

            audit = audit with { Outcome = outcome, At = time.GetUtcNow() };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await log.RecordAsync(audit with { Error = $"{ex.GetType().Name}: {ex.Message}", At = time.GetUtcNow() }, CancellationToken.None);
            throw;
        }

        await log.RecordAsync(audit, cancellationToken);
        logger.LogInformation(
            "Message {MessageId}: saved rule {Rule} matched, {Outcome} with {Actions} action(s)",
            message.Id,
            rule.Name,
            audit.Outcome,
            audit.Actions.Count);
        return message;
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
            }
        }

        if (plan.Removal != PlannedRemoval.None)
        {
            labels.Remove(GmailLabels.Inbox);
        }

        if (plan.Removal == PlannedRemoval.Trash && !labels.Contains(GmailLabels.Trash))
        {
            labels.Add(GmailLabels.Trash);
        }

        return message with { LabelIds = labels };
    }
}
