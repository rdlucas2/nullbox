using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Nullbox.Core.Gmail;
using Nullbox.Core.Jev;
using Nullbox.Core.Options;
using Nullbox.Core.Pipeline;
using Nullbox.Core.Rules;
using Nullbox.Jev;

namespace Nullbox.Web.Pipeline;

public enum TestSource
{
    /// <summary>The newest messages in the inbox.</summary>
    Inbox,

    /// <summary>Messages matching a Gmail search.</summary>
    Search,

    /// <summary>Messages a rule set already decided on, newest first, from the decision log.</summary>
    Audit,
}

/// <summary>Which messages a test run takes, and whether to compare with the active version.</summary>
public sealed record TestSelection
{
    public TestSource Source { get; init; } = TestSource.Inbox;

    /// <summary>The Gmail search for <see cref="TestSource.Search"/>.</summary>
    public string? Query { get; init; }

    /// <summary>The rule set whose decisions <see cref="TestSource.Audit"/> samples; the draft's name when blank.</summary>
    public string? AuditRuleSet { get; init; }

    /// <summary>How many messages; capped at <see cref="TesterSettings.MaxMessages"/>.</summary>
    public int Count { get; init; } = 25;

    public bool CompareWithActive { get; init; } = true;
}

/// <summary>
/// A checked batch, before any Jev call: the messages, the active version to compare with (null when not comparing
/// or none is active), verdicts already recorded for that version, and the cost. <see cref="Blocked"/> says why it
/// can't run, e.g. the Jev budget.
/// </summary>
public sealed record TestPlan(
    RuleSetDocument Draft,
    RuleSetVersion? Active,
    IReadOnlyList<GmailMessage> Messages,
    IReadOnlyDictionary<string, ReplayVerdict> Recorded,
    int JevCalls,
    long EstimatedInputTokens,
    JevBudgetStatus? Budget,
    string? Blocked);

/// <summary>One answer as shown in the results, e.g. <c>promo (95%)</c> with the other probabilities.</summary>
public sealed record TestAnswer(string Question, string Value, string? Detail);

/// <summary>What the draft did with one message, and what the active version did.</summary>
public sealed record TestRow(
    string MessageId,
    string? From,
    string? Subject,
    string State,
    IReadOnlyList<TestAnswer> Answers,
    IReadOnlyList<string> Decisions,
    IReadOnlyList<string> Actions,
    IReadOnlyList<string> Skipped,
    bool FromCache,
    ReplayVerdict? Active,
    string? ActiveSource,
    string? Error)
{
    /// <summary>The draft's decisions or actions differ from the active version's.</summary>
    public bool Changed => Active is { } active && Error is null
        && (!active.Decisions.SequenceEqual(Decisions, StringComparer.Ordinal) || !active.Actions.SequenceEqual(Actions, StringComparer.Ordinal));
}

public sealed record TestRun(TestPlan Plan, IReadOnlyList<TestRow> Rows, int JevCalls, long InputTokens, string? Stopped)
{
    public int Changed => Rows.Count(r => r.Changed);
}

/// <summary>
/// Runs a draft rule set over a batch of real messages in dry run: Jev answers, the decision engine decides, and the
/// plan is only described. It can't change mail: it holds an <see cref="IGmailReader"/> (search and fetch only), never
/// an <see cref="IGmailService"/>, and records nothing in the decision log.
/// </summary>
public sealed class RuleTester(
    IGmailReader gmail,
    IJevClient jev,
    IRuleSetStore ruleSets,
    IDecisionLog log,
    IOptions<NullboxOptions> options,
    TimeProvider time,
    IOptions<GmailOptions>? gmailOptions = null,
    IJevBudget? budget = null)
{
    public int MaxMessages => Math.Max(1, options.Value.Tester.MaxMessages);

    /// <summary>Picks and fetches the messages and works out the Jev cost, without calling Jev.</summary>
    public async Task<TestPlan> PrepareAsync(RuleSetDocument draft, TestSelection selection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(selection);
        var count = Math.Clamp(selection.Count, 1, MaxMessages);
        var active = selection.CompareWithActive ? await ruleSets.GetVersionAsync(draft.Name, null, cancellationToken) : null;

        IReadOnlyList<string> ids;
        var messages = new List<GmailMessage>();
        try
        {
            ids = selection.Source switch
            {
                TestSource.Inbox => await gmail.SearchAsync("in:inbox", count, cancellationToken),
                TestSource.Search when string.IsNullOrWhiteSpace(selection.Query) => [],
                TestSource.Search => await gmail.SearchAsync(selection.Query!.Trim(), count, cancellationToken),
                _ => await AuditSampleAsync(string.IsNullOrWhiteSpace(selection.AuditRuleSet) ? draft.Name : selection.AuditRuleSet.Trim(), count, cancellationToken),
            };

            var needsBody = draft.State.Fields.Contains(StateFields.Body)
                || active?.Document.State.Fields.Contains(StateFields.Body) == true;
            foreach (var id in ids.Take(count))
            {
                messages.Add(await gmail.GetMessageAsync(id, needsBody ? MessageFormat.Full : MessageFormat.Metadata, cancellationToken));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Plan(draft, active, messages, null, $"Couldn't read Gmail: {ex.Message}");
        }

        if (messages.Count == 0)
        {
            var why = selection.Source switch
            {
                TestSource.Search when string.IsNullOrWhiteSpace(selection.Query) => "Enter a Gmail search.",
                TestSource.Audit => "The decision log has no messages for that rule set yet.",
                _ => "No messages matched.",
            };
            return Plan(draft, active, messages, null, why);
        }

        var recorded = new Dictionary<string, ReplayVerdict>(StringComparer.Ordinal);
        var now = time.GetUtcNow();
        long tokens = 0;
        var calls = 0;
        foreach (var message in messages)
        {
            tokens += JevStateTruncator.EstimateTokens(JevRequestBuilder.Build(draft, message, now));
            calls++;
            if (active is null)
            {
                continue;
            }

            if (await RecordedAsync(message.Id, active, cancellationToken) is { } verdict)
            {
                recorded[message.Id] = verdict;
            }
            else
            {
                tokens += JevStateTruncator.EstimateTokens(JevRequestBuilder.Build(active.Document, message, now));
                calls++;
            }
        }

        var status = budget is null ? null : await budget.GetStatusAsync(cancellationToken);
        string? blocked = null;
        if (status is not null && status.InputTokensToday + tokens > status.MaxInputTokensPerDay)
        {
            blocked = string.Create(
                CultureInfo.InvariantCulture,
                $"This batch could use about {tokens:N0} input tokens, but only {Math.Max(0, status.MaxInputTokensPerDay - status.InputTokensToday):N0} are left in today's Jev budget. Take fewer messages.");
        }
        else if (status is not null && status.RequestsLastHour + calls > status.MaxRequestsPerHour)
        {
            blocked = $"This batch could make {calls} Jev calls, but only {Math.Max(0, status.MaxRequestsPerHour - status.RequestsLastHour)} are left this hour. Take fewer messages or wait.";
        }

        return new TestPlan(draft, active, messages, recorded, calls, tokens, status, blocked);
    }

    /// <summary>Asks Jev about each message of <paramref name="plan"/> and describes what the draft would do. Changes nothing.</summary>
    public async Task<TestRun> RunAsync(TestPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Blocked is not null)
        {
            throw new InvalidOperationException(plan.Blocked);
        }

        var draft = plan.Draft;
        var context = DraftContext(draft);
        var allowDelete = gmailOptions?.Value.AllowDelete ?? false;
        var rows = new List<TestRow>(plan.Messages.Count);
        var calls = 0;
        long tokens = 0;
        string? stopped = null;

        foreach (var message in plan.Messages)
        {
            var now = time.GetUtcNow();
            var request = JevRequestBuilder.Build(draft, message, now);
            var state = request.State.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            try
            {
                var result = await jev.EvaluateAsync(request, new JevCallContext(message.Id, context), cancellationToken);
                Count(result);
                var decisions = DecisionEngine.Evaluate(draft, result);
                var actions = ActionPlan.Create(message, decisions, allowDelete);

                ReplayVerdict? active = null;
                string? source = null;
                if (plan.Active is { } version)
                {
                    if (plan.Recorded.TryGetValue(message.Id, out var recorded))
                    {
                        (active, source) = (recorded, $"recorded by v{version.Version}");
                    }
                    else
                    {
                        var activeResult = await jev.EvaluateAsync(
                            JevRequestBuilder.Build(version.Document, message, now), new JevCallContext(message.Id, version.Key), cancellationToken);
                        Count(activeResult);
                        var activeDecisions = DecisionEngine.Evaluate(version.Document, activeResult);
                        active = new ReplayVerdict(
                            [.. activeDecisions.Select(d => d.Label)], ActionPlan.Create(message, activeDecisions, allowDelete).Describe());
                        source = $"v{version.Version}, asked now";
                    }
                }

                rows.Add(new TestRow(
                    message.Id,
                    message.From,
                    message.Subject,
                    state,
                    Describe(result.Answers),
                    [.. decisions.Select(d => d.Label)],
                    actions.Describe(),
                    [.. actions.Skipped.Select(s => $"{s.Action} ({s.Decision}): {s.Reason}")],
                    result.FromCache,
                    active,
                    source,
                    null));
            }
            catch (JevBudgetExceededException ex)
            {
                stopped = $"Stopped after {rows.Count} of {plan.Messages.Count} messages: {ex.Message}";
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                rows.Add(new TestRow(message.Id, message.From, message.Subject, state, [], [], [], [], false, null, null, $"{ex.GetType().Name}: {ex.Message}"));
            }
        }

        return new TestRun(plan, rows, calls, tokens, stopped);

        void Count(JevResult result)
        {
            if (!result.FromCache)
            {
                calls++;
                tokens += result.Usage.InputTokens;
            }
        }
    }

    /// <summary>Cache scope for a draft: test answers are reused only for the same model, state and questions.</summary>
    public static string DraftContext(RuleSetDocument draft)
        => $"test:{draft.Name}#{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(DecisionReplayer.AskKey(draft))))[..12]}";

    public static IReadOnlyList<TestAnswer> Describe(IReadOnlyDictionary<string, JevAnswer> answers)
        => [.. answers.Select(pair => pair.Value switch
        {
            JevChoiceAnswer choice => new TestAnswer(pair.Key, $"{choice.Choice} ({Percent(choice.Confidence)})", Probabilities(choice.Probabilities)),
            JevScoreAnswer score => new TestAnswer(pair.Key, $"{Number(score.Score)} ({Percent(score.Confidence)})", Probabilities(score.Probabilities)),
            JevNoulAnswer noul => new TestAnswer(pair.Key, Number(noul.Noul), null),
            _ => new TestAnswer(pair.Key, pair.Value.GetType().Name, null),
        })];

    private static TestPlan Plan(RuleSetDocument draft, RuleSetVersion? active, IReadOnlyList<GmailMessage> messages, JevBudgetStatus? status, string blocked)
        => new(draft, active, messages, new Dictionary<string, ReplayVerdict>(), 0, 0, status, blocked);

    private async Task<IReadOnlyList<string>> AuditSampleAsync(string ruleSet, int count, CancellationToken cancellationToken)
    {
        var page = await log.QueryAsync(new DecisionQuery { RuleSet = ruleSet, Limit = 500 }, cancellationToken);
        return [.. page.Items.Where(r => r.Outcome != DecisionOutcome.Error).Select(r => r.MessageId).Distinct(StringComparer.Ordinal).Take(count)];
    }

    /// <summary>The newest verdict <paramref name="version"/> recorded for the message, if any.</summary>
    private async Task<ReplayVerdict?> RecordedAsync(string messageId, RuleSetVersion version, CancellationToken cancellationToken)
    {
        var page = await log.QueryAsync(
            new DecisionQuery { MessageId = messageId, RuleSet = version.Name, Version = version.Version, Limit = 20 }, cancellationToken);
        return page.Items.FirstOrDefault(r => r.Outcome != DecisionOutcome.Error) is { } record
            ? new ReplayVerdict(record.Decisions, record.Actions)
            : null;
    }

    private static string Percent(double value) => (value * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string? Probabilities(IReadOnlyDictionary<string, double> probabilities)
        => probabilities.Count == 0 ? null : string.Join(", ", probabilities.OrderByDescending(p => p.Value).Select(p => $"{p.Key} {Number(p.Value)}"));
}
