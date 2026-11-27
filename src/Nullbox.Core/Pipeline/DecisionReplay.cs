using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Nullbox.Core.Gmail;
using Nullbox.Core.Jev;
using Nullbox.Core.Rules;

namespace Nullbox.Core.Pipeline;

/// <summary>Which stored records a replay re-evaluates.</summary>
public sealed record ReplaySample
{
    /// <summary>Rule set whose records to sample; the draft's own name when null.</summary>
    public string? RuleSet { get; init; }

    /// <summary>Only records made by this version of <see cref="RuleSet"/>.</summary>
    public int? Version { get; init; }

    /// <summary>Only these messages. Otherwise the most recent messages the rule set decided on.</summary>
    public IReadOnlyList<string>? MessageIds { get; init; }

    /// <summary>At most this many messages (1 to <see cref="DecisionReplayer.MaxSample"/>).</summary>
    public int Limit { get; init; } = 50;
}

[JsonConverter(typeof(JsonStringEnumConverter<ReplayItemStatus>))]
public enum ReplayItemStatus
{
    /// <summary>The draft decides the same as the stored record.</summary>
    [JsonStringEnumMemberName("unchanged")]
    Unchanged,

    /// <summary>The draft's decisions or actions differ.</summary>
    [JsonStringEnumMemberName("changed")]
    Changed,

    /// <summary>Not re-evaluated: the stored inputs aren't enough for the draft (see <see cref="ReplayItem.Note"/>).</summary>
    [JsonStringEnumMemberName("skipped")]
    Skipped,

    /// <summary>Asking Jev failed (see <see cref="ReplayItem.Note"/>).</summary>
    [JsonStringEnumMemberName("failed")]
    Failed,
}

/// <summary>Decisions and actions for one message, before (as recorded) or after (under the draft).</summary>
public sealed record ReplayVerdict(IReadOnlyList<string> Decisions, IReadOnlyList<string> Actions);

/// <summary>
/// One message in a replay. <see cref="Before"/> is the stored record (from <see cref="RuleSetKey"/>); <see cref="After"/>
/// is the draft's verdict, null when skipped or failed. <see cref="ReusedAnswers"/> is true when the draft asks Jev the
/// same thing as the recorded version, so the stored answers were used and Jev was not called.
/// </summary>
public sealed record ReplayItem(
    string MessageId,
    string RuleSetKey,
    ReplayItemStatus Status,
    ReplayVerdict Before,
    ReplayVerdict? After,
    bool ReusedAnswers,
    string? Note);

/// <summary>The "what would change" view of a draft rule set over a sample of past messages.</summary>
public sealed record ReplayResult(
    string RuleSet,
    int Sampled,
    int Changed,
    int Unchanged,
    int Skipped,
    int Failed,
    int JevCalls,
    IReadOnlyList<ReplayItem> Items);

/// <summary>
/// Re-evaluates stored decision records against a draft rule set and diffs the verdicts. Gmail is never read or
/// changed and nothing is recorded: the replayer has no Gmail client and only reads the log.
/// <list type="bullet">
/// <item>When the draft asks Jev the same thing (same model, state fields and questions) as the version that made a
/// record, the stored answers are reused and only the draft's decisions run.</item>
/// <item>Otherwise Jev is asked again with the stored inputs. A draft that needs a field the record didn't keep is
/// skipped for that message; a stored body is cut short (<see cref="Options.AuditSettings.StoredBodyChars"/>), so
/// body-based answers are approximate.</item>
/// </list>
/// </summary>
public sealed class DecisionReplayer(IDecisionLog log, IRuleSetStore ruleSets, IJevClient jev)
{
    public const int MaxSample = 200;

    public async Task<ReplayResult> ReplayAsync(
        RuleSetDocument draft, ReplaySample sample, bool allowDelete, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(sample);

        var records = await SampleAsync(draft, sample, cancellationToken);
        var askKey = AskKey(draft);
        var context = $"replay:{draft.Name}#{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(askKey)))[..12]}";
        var askKeys = new Dictionary<string, string?>(StringComparer.Ordinal);
        var items = new List<ReplayItem>(records.Count);
        var jevCalls = 0;

        foreach (var record in records)
        {
            var before = new ReplayVerdict(record.Decisions, record.Actions);
            if (!askKeys.TryGetValue(record.RuleSetKey, out var recordedAskKey))
            {
                recordedAskKey = await RecordedAskKeyAsync(record.RuleSetKey, cancellationToken);
                askKeys[record.RuleSetKey] = recordedAskKey;
            }

            JevResult result;
            var reused = false;
            if (recordedAskKey == askKey && AuditInputs.ReadAnswers(record.AnswersJson) is { } stored)
            {
                result = new JevResult { Model = record.Model, Answers = stored };
                reused = true;
            }
            else
            {
                var state = StateFor(draft.State, record, out var missing);
                if (state is null)
                {
                    items.Add(new ReplayItem(record.MessageId, record.RuleSetKey, ReplayItemStatus.Skipped, before, null, false, missing));
                    continue;
                }

                try
                {
                    jevCalls++;
                    var request = new JevRequest { Model = draft.Model, State = state, Questions = draft.Questions };
                    result = await jev.EvaluateAsync(request, new JevCallContext(record.MessageId, context), cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    items.Add(new ReplayItem(
                        record.MessageId, record.RuleSetKey, ReplayItemStatus.Failed, before, null, false, $"{ex.GetType().Name}: {ex.Message}"));
                    continue;
                }
            }

            var decisions = DecisionEngine.Evaluate(draft, result);
            var plan = ActionPlan.Create(MessageFor(record), decisions, allowDelete);
            var after = new ReplayVerdict([.. decisions.Select(d => d.Label)], plan.Describe());
            var changed = !before.Decisions.SequenceEqual(after.Decisions, StringComparer.Ordinal)
                || !before.Actions.SequenceEqual(after.Actions, StringComparer.Ordinal);
            items.Add(new ReplayItem(
                record.MessageId,
                record.RuleSetKey,
                changed ? ReplayItemStatus.Changed : ReplayItemStatus.Unchanged,
                before,
                after,
                reused,
                null));
        }

        return new ReplayResult(
            draft.Name,
            items.Count,
            items.Count(i => i.Status == ReplayItemStatus.Changed),
            items.Count(i => i.Status == ReplayItemStatus.Unchanged),
            items.Count(i => i.Status == ReplayItemStatus.Skipped),
            items.Count(i => i.Status == ReplayItemStatus.Failed),
            jevCalls,
            items);
    }

    /// <summary>The newest verdict (not an error) per message for the sampled rule set, newest messages first.</summary>
    private async Task<IReadOnlyList<DecisionRecord>> SampleAsync(RuleSetDocument draft, ReplaySample sample, CancellationToken cancellationToken)
    {
        var ruleSet = string.IsNullOrWhiteSpace(sample.RuleSet) ? draft.Name : sample.RuleSet;
        var limit = Math.Clamp(sample.Limit, 1, MaxSample);
        var wanted = sample.MessageIds is { Count: > 0 } ids ? new HashSet<string>(ids, StringComparer.Ordinal) : null;
        var picked = new List<DecisionRecord>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long? before = null;

        // A few pages is plenty: each message usually has one record per rule-set version.
        for (var page = 0; page < 20 && picked.Count < limit; page++)
        {
            var result = await log.QueryAsync(
                new DecisionQuery { RuleSet = ruleSet, Version = sample.Version, Before = before, Limit = 500 },
                cancellationToken);
            foreach (var record in result.Items)
            {
                if (record.Outcome != DecisionOutcome.Error
                    && (wanted is null || wanted.Contains(record.MessageId))
                    && seen.Add(record.MessageId))
                {
                    picked.Add(record);
                    if (picked.Count == limit)
                    {
                        break;
                    }
                }
            }

            if (result.Next is null)
            {
                break;
            }

            before = result.Next;
        }

        return picked;
    }

    private async Task<string?> RecordedAskKeyAsync(string ruleSetKey, CancellationToken cancellationToken)
        => RuleSetVersion.TryParseKey(ruleSetKey, out var name, out var version)
            && await ruleSets.GetVersionAsync(name, version, cancellationToken) is { } recorded
            ? AskKey(recorded.Document)
            : null;

    /// <summary>Everything that shapes what Jev is asked; equal keys mean equal requests for the same message.</summary>
    private static string AskKey(RuleSetDocument document)
        => JsonSerializer.Serialize(new { document.Model, document.State, document.Questions }, RuleJson.Options);

    /// <summary>The draft's state built from the stored inputs, or null with the reason when a field wasn't kept.</summary>
    private static JsonObject? StateFor(StateTemplate template, DecisionRecord record, out string? missing)
    {
        missing = null;
        if (string.IsNullOrEmpty(record.StateJson) || JsonNode.Parse(record.StateJson) is not JsonObject stored)
        {
            missing = "the audit record kept no inputs for this message";
            return null;
        }

        var state = new JsonObject();
        foreach (var field in template.Fields)
        {
            if (!stored.TryGetPropertyValue(field, out var value))
            {
                missing = $"the audit record has no '{field}' for this message";
                return null;
            }

            state[field] = field == StateFields.Body && value is JsonValue body && body.TryGetValue<string>(out var text)
                ? JevRequestBuilder.Truncate(text, template.BodyMaxChars)
                : value?.DeepClone();
        }

        return state;
    }

    /// <summary>Only the labels matter to planning (starred and important mail is protected).</summary>
    private static GmailMessage MessageFor(DecisionRecord record)
        => new(record.MessageId, null, null, null, null, null, record.Labels, null, null, null);
}
