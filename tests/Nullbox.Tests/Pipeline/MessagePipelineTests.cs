using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nullbox.Core.Gmail;
using Nullbox.Core.Jev;
using Nullbox.Core.Options;
using Nullbox.Core.Pipeline;
using Nullbox.Core.Rules;
using Nullbox.Jev.Testing;
using Nullbox.Tests.Inbox;
using Nullbox.Web.Inbox;
using Nullbox.Web.Pipeline;

namespace Nullbox.Tests.Pipeline;

public sealed class MessagePipelineTests : IAsyncLifetime
{
    private readonly PipelineDb _db = new();
    private readonly FakeInboxGmail _gmail = new();
    private readonly FakeJevClient _jev = new(_ => Answers.ConfidentPromo);
    private readonly NullboxOptions _options = new();
    private readonly GmailOptions _gmailOptions = new();

    public Task InitializeAsync() => _db.InitializeAsync();

    public Task DisposeAsync() => _db.DisposeAsync();

    private static RuleSetDocument Promo(RuleSetMode mode) => RuleSetSeeds.Load(RuleSetSeeds.PromoCleanup) with { Mode = mode };

    private MessagePipeline Pipeline()
    {
        var log = _db.Log();
        return new(_db.RuleSets(), log, log, _gmail, _jev, Options.Create(_options), Options.Create(_gmailOptions), _db.Time, NullLogger<MessagePipeline>.Instance);
    }

    [Fact]
    public async Task Dry_run_records_the_decision_and_changes_nothing()
    {
        var version = await _db.ActivateAsync(Promo(RuleSetMode.DryRun));
        _gmail.Add("m1");

        await Pipeline().ProcessAsync("m1", default);

        Assert.Empty(_gmail.Changes);
        var record = Assert.Single(await _db.Log().ListAsync("m1"));
        Assert.Equal(("promo-cleanup@v1", RuleSetMode.DryRun, DecisionOutcome.DryRun), (record.RuleSetKey, record.Mode, record.Outcome));
        Assert.Equal(["Archive confident promos"], record.Decisions);
        Assert.Equal(["label:nullbox/promo", "label:nullbox/review/promo-cleanup/archive-confident-promos", "mark_read", "archive"], record.Actions);
        Assert.Contains("\"promo\"", record.AnswersJson, StringComparison.Ordinal);
        Assert.Equal(new JevCallContext("m1", version.Key), Assert.Single(_jev.Calls).Context);
    }

    [Fact]
    public async Task Live_applies_the_actions()
    {
        await _db.ActivateAsync(Promo(RuleSetMode.Live));
        _gmail.Add("m1");

        await Pipeline().ProcessAsync("m1", default);

        Assert.Equal(
            ["label:nullbox/promo", "label:nullbox/review/promo-cleanup/archive-confident-promos", "modify:m1:+Label_nullbox/promo,Label_nullbox/review/promo-cleanup/archive-confident-promos:-UNREAD,INBOX"],
            _gmail.Changes);
        Assert.Equal(DecisionOutcome.Applied, Assert.Single(await _db.Log().ListAsync("m1")).Outcome);
    }

    private static RuleSetDocument Trasher => RuleSetSeeds.Load(RuleSetSeeds.PromoCleanup) with
    {
        Name = "junk",
        Mode = RuleSetMode.Live,
        Decisions = [new DecisionRule { Name = "Trash promos", When = "category.choice == \"promo\"", Remove = RemoveMode.Delete }],
    };

    [Fact]
    public async Task A_trash_rule_with_the_delete_lock_on_archives_and_never_calls_trash()
    {
        await _db.ActivateAsync(Trasher);
        _gmail.Add("m1");

        await Pipeline().ProcessAsync("m1", default);

        Assert.Equal(["label:nullbox/review/junk/trash-promos", "modify:m1:+Label_nullbox/review/junk/trash-promos:-INBOX"], _gmail.Changes);
        var record = Assert.Single(await _db.Log().ListAsync("m1"));
        Assert.Equal(DecisionOutcome.Applied, record.Outcome);
        Assert.Equal(["label:nullbox/review/junk/trash-promos", "archive"], record.Actions);
        Assert.Contains("delete blocked by lock", Assert.Single(record.Skipped), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dry_run_rules_in_a_live_rule_set_record_their_matches_and_change_nothing()
    {
        var promo = Promo(RuleSetMode.Live);
        await _db.ActivateAsync(promo with { Decisions = [.. promo.Decisions.Select(d => d.Actions.Contains("none") ? d : d with { Remove = RemoveMode.DryRun })] });
        _gmail.Add("m1");

        await Pipeline().ProcessAsync("m1", default);

        Assert.Empty(_gmail.Changes);
        var record = Assert.Single(await _db.Log().ListAsync("m1"));
        Assert.Equal((RuleSetMode.Live, DecisionOutcome.NoChange), (record.Mode, record.Outcome));
        Assert.Equal(["Archive confident promos", "Label likely promos"], record.Decisions);
        Assert.Empty(record.Actions);
        Assert.All(record.Skipped, s => Assert.EndsWith("the rule is in dry run; recorded only", s, StringComparison.Ordinal));
        Assert.Contains("mark_read (Archive confident promos): the rule is in dry run; recorded only", record.Skipped);
    }

    [Fact]
    public async Task A_rule_on_trial_does_not_stop_the_rule_below_it_from_acting()
    {
        var promo = Promo(RuleSetMode.Live);
        await _db.ActivateAsync(promo with { Decisions = [.. promo.Decisions.Select(d => d.Remove == RemoveMode.Archive ? d with { Remove = RemoveMode.DryRun } : d)] });
        _gmail.Add("m1");

        await Pipeline().ProcessAsync("m1", default);

        Assert.Equal(["label:nullbox/promo", "modify:m1:+Label_nullbox/promo:-"], _gmail.Changes);
        var record = Assert.Single(await _db.Log().ListAsync("m1"));
        Assert.Equal(DecisionOutcome.Applied, record.Outcome);
        Assert.Equal(["Archive confident promos", "Label likely promos"], record.Decisions);
        Assert.Equal(["label:nullbox/promo"], record.Actions);
        Assert.Contains("mark_read (Archive confident promos): the rule is in dry run; recorded only", record.Skipped);
    }

    [Fact]
    public async Task A_trash_rule_with_the_delete_lock_off_trashes()
    {
        _gmailOptions.AllowDelete = true;
        _gmail.AllowDelete = true;
        await _db.ActivateAsync(Trasher);
        _gmail.Add("m1");

        await Pipeline().ProcessAsync("m1", default);

        Assert.Equal(["label:nullbox/review/junk/trash-promos", "modify:m1:+Label_nullbox/review/junk/trash-promos:-", "trash:m1"], _gmail.Changes);
        Assert.Empty(Assert.Single(await _db.Log().ListAsync("m1")).Skipped);
    }

    [Fact]
    public async Task Jev_sees_only_the_state_fields_and_the_body_is_fetched_only_when_asked_for()
    {
        await _db.ActivateAsync(Promo(RuleSetMode.DryRun));
        _gmail.Add("m1");
        await Pipeline().ProcessAsync("m1", default);

        var state = Assert.Single(_jev.Calls).Request.State.AsObject();
        Assert.Equal(["from", "subject", "snippet", "labels", "age_days", "has_unsubscribe"], state.Select(p => p.Key));
        Assert.Equal([MessageFormat.Metadata], _gmail.Fetches);

        await _db.ActivateAsync(RuleSetSeeds.Load(RuleSetSeeds.ImportantMailGuard));
        _gmail.Add("m2");
        await Pipeline().ProcessAsync("m2", default);
        Assert.Equal(MessageFormat.Full, _gmail.Fetches.Last());
    }

    [Fact]
    public async Task A_retry_does_not_ask_Jev_or_act_twice()
    {
        await _db.ActivateAsync(Promo(RuleSetMode.Live));
        _gmail.Add("m1");

        await Pipeline().ProcessAsync("m1", default);
        await Pipeline().ProcessAsync("m1", default);

        Assert.Single(_jev.Calls);
        Assert.Equal(3, _gmail.Changes.Count);
        Assert.Single(await _db.Log().ListAsync("m1"));
    }

    [Fact]
    public async Task Protected_mail_is_labelled_but_not_hidden()
    {
        await _db.ActivateAsync(Promo(RuleSetMode.Live));
        _gmail.Add("m1", GmailLabels.Inbox, GmailLabels.Unread, GmailLabels.Important);

        await Pipeline().ProcessAsync("m1", default);

        Assert.Equal(["label:nullbox/promo", "modify:m1:+Label_nullbox/promo:-"], _gmail.Changes);
        var record = Assert.Single(await _db.Log().ListAsync("m1"));
        Assert.Equal(["label:nullbox/promo"], record.Actions);
        Assert.Equal(2, record.Skipped.Count);
        Assert.All(record.Skipped, s => Assert.Contains("starred or important", s, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Kill_switch_in_config_or_at_runtime_stops_every_change()
    {
        await _db.ActivateAsync(Promo(RuleSetMode.Live));
        _gmail.Add("m1");
        _gmail.Add("m2");

        _options.Pipeline.KillSwitch = true;
        await Pipeline().ProcessAsync("m1", default);
        _options.Pipeline.KillSwitch = false;
        await _db.Log().SetKillSwitchAsync(true);
        await Pipeline().ProcessAsync("m2", default);

        Assert.Empty(_gmail.Changes);
        Assert.All(await _db.Log().ListAsync(), r => Assert.Equal(DecisionOutcome.KillSwitch, r.Outcome));
        Assert.All(await _db.Log().ListAsync(), r => Assert.NotEmpty(r.Actions));
    }

    [Fact]
    public async Task Hourly_cap_limits_changed_messages_and_resets_after_an_hour()
    {
        _options.Pipeline.MaxMessagesPerHour = 2;
        await _db.ActivateAsync(Promo(RuleSetMode.Live));
        foreach (var id in new[] { "m1", "m2", "m3" })
        {
            _gmail.Add(id);
            await Pipeline().ProcessAsync(id, default);
        }

        _db.Time.Advance(TimeSpan.FromMinutes(61));
        _gmail.Add("m4");
        await Pipeline().ProcessAsync("m4", default);

        var outcomes = (await _db.Log().ListAsync()).Reverse().Select(r => (r.MessageId, r.Outcome));
        Assert.Equal(
            [("m1", DecisionOutcome.Applied), ("m2", DecisionOutcome.Applied), ("m3", DecisionOutcome.CapReached), ("m4", DecisionOutcome.Applied)],
            outcomes);
        Assert.DoesNotContain(_gmail.Changes, c => c.Contains("m3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Live_with_nothing_to_do_records_no_change()
    {
        await _db.ActivateAsync(Promo(RuleSetMode.Live));
        _gmail.Add("m1");
        _jev.Responder = _ => Answers.Of(
            ("from_a_person", Answers.Noul(0.9)), ("category", Answers.Choice("personal", 0.9)), ("fits_category", Answers.Noul(0.9)));

        await Pipeline().ProcessAsync("m1", default);

        Assert.Empty(_gmail.Changes);
        var record = Assert.Single(await _db.Log().ListAsync("m1"));
        Assert.Equal((DecisionOutcome.NoChange, "Never touch mail from real people"), (record.Outcome, Assert.Single(record.Decisions)));
    }

    [Fact]
    public async Task Each_active_rule_set_gets_its_own_verdict_and_inactive_ones_are_ignored()
    {
        await _db.ActivateAsync(Promo(RuleSetMode.DryRun));
        await _db.RuleSets().SaveAsync(RuleSetSeeds.Load(RuleSetSeeds.ImportantMailGuard)); // saved, never activated
        _gmail.Add("m1");

        await Pipeline().ProcessAsync("m1", default);

        Assert.Equal(["promo-cleanup@v1"], (await _db.Log().ListAsync("m1")).Select(r => r.RuleSetKey));
    }

    [Fact]
    public async Task No_active_rule_sets_means_no_Gmail_or_Jev_calls()
    {
        await Pipeline().ProcessAsync("m1", default);

        Assert.Empty(_gmail.Fetches);
        Assert.Empty(_jev.Calls);
    }

    [Fact]
    public async Task A_later_rule_set_sees_a_star_added_by_an_earlier_one()
    {
        // "aaa" runs first (rule sets go in name order) and stars the message; promo-cleanup then can't archive it.
        var starrer = RuleSetSeeds.Load(RuleSetSeeds.PromoCleanup) with
        {
            Name = "aaa-star",
            Mode = RuleSetMode.Live,
            Decisions = [new DecisionRule { When = "fits_category.noul >= 0.5", Actions = ["star"] }],
        };
        await _db.ActivateAsync(starrer);
        await _db.ActivateAsync(Promo(RuleSetMode.Live));
        _gmail.Add("m1");

        await Pipeline().ProcessAsync("m1", default);

        Assert.Equal(["modify:m1:+STARRED:-", "label:nullbox/promo", "modify:m1:+Label_nullbox/promo:-"], _gmail.Changes);
    }
}

public sealed class ProcessMessageJobTests : IAsyncLifetime
{
    private readonly PipelineDb _db = new();
    private readonly TempStore _inbox = new();
    private readonly RecordingQueue _queue = new();
    private readonly NullboxOptions _options = new() { Pipeline = { MaxAttempts = 3 } };

    public Task InitializeAsync() => _db.InitializeAsync();

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        _inbox.Dispose();
    }

    private ProcessMessageJob Job(Nullbox.Core.Inbox.IMessageProcessor processor)
        => new(processor, _inbox.Store, _db.Log(), _queue, Options.Create(_options), _db.Time, NullLogger<ProcessMessageJob>.Instance);

    [Fact]
    public async Task Processes_a_message_once_and_skips_repeats()
    {
        var processor = new RecordingProcessor();

        await Job(processor).ExecuteAsync("m1", default);
        await Job(processor).ExecuteAsync("m1", default);

        Assert.Equal(["m1"], processor.Processed);
        Assert.True(await _inbox.Store.IsProcessedAsync("m1"));
    }

    [Fact]
    public async Task Failures_are_retried_until_max_attempts_then_recorded_as_poison()
    {
        var processor = new Throwing(_ => new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Job(processor).ExecuteAsync("m1", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Job(processor).ExecuteAsync("m1", default));
        Assert.False(await _inbox.Store.IsProcessedAsync("m1"));
        Assert.Empty(await _db.Log().ListPoisonAsync());

        await Job(processor).ExecuteAsync("m1", default); // third attempt: given up on, not rethrown
        await Job(processor).ExecuteAsync("m1", default); // and not run again

        Assert.Equal(3, processor.Calls);
        Assert.True(await _inbox.Store.IsProcessedAsync("m1"));
        var poison = Assert.Single(await _db.Log().ListPoisonAsync());
        Assert.Equal(("m1", 3, "InvalidOperationException: boom"), (poison.MessageId, poison.Attempts, poison.Error));
    }

    [Fact]
    public async Task Success_after_a_failure_clears_the_count()
    {
        var fail = true;
        var processor = new Throwing(_ => fail ? new InvalidOperationException("once") : null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Job(processor).ExecuteAsync("m1", default));
        fail = false;

        await Job(processor).ExecuteAsync("m1", default);

        Assert.Equal(1, await _db.Log().RecordFailureAsync("m1", "fresh", _db.Time.GetUtcNow()));
        Assert.Empty(await _db.Log().ListPoisonAsync());
    }

    public static TheoryData<string> PermanentFailures => ["max_tokens", "validation", "gmail_404"];

    [Theory]
    [MemberData(nameof(PermanentFailures))]
    public async Task Requests_that_can_never_succeed_are_poison_at_once(string kind)
    {
        Exception error = kind switch
        {
            "max_tokens" => new JevMaxTokensExceededException(null),
            "validation" => new JevValidationException(["bad"], null),
            _ => new Google.GoogleApiException("gmail", "Not Found") { HttpStatusCode = HttpStatusCode.NotFound },
        };

        await Job(new Throwing(_ => error)).ExecuteAsync("m1", default);

        Assert.Equal(1, Assert.Single(await _db.Log().ListPoisonAsync()).Attempts);
        Assert.True(await _inbox.Store.IsProcessedAsync("m1"));
    }

    [Fact]
    public async Task Budget_exhaustion_defers_to_the_reset_without_counting_an_attempt()
    {
        var resetAt = _db.Time.GetUtcNow().AddHours(3);

        await Job(new Throwing(_ => new JevBudgetExceededException("daily tokens", resetAt))).ExecuteAsync("m1", default);

        Assert.Equal([("m1", resetAt)], _queue.Scheduled);
        Assert.False(await _inbox.Store.IsProcessedAsync("m1"));
        Assert.Equal(1, await _db.Log().RecordFailureAsync("m1", "first real failure", _db.Time.GetUtcNow()));
    }

    [Fact]
    public async Task Credentials_that_need_fixing_defer_by_the_auth_delay()
    {
        await Job(new Throwing(_ => new GmailReauthorizationRequiredException(GmailTokenKind.Primary, "revoked"))).ExecuteAsync("m1", default);
        await Job(new Throwing(_ => new JevUnauthorizedException(null))).ExecuteAsync("m2", default);
        await Job(new Throwing(_ => new JevNotConfiguredException())).ExecuteAsync("m3", default);

        var at = _db.Time.GetUtcNow() + _options.Pipeline.AuthRetryDelay;
        Assert.Equal([("m1", at), ("m2", at), ("m3", at)], _queue.Scheduled);
        Assert.Empty(await _db.Log().ListPoisonAsync());
    }

    [Fact]
    public void Hangfire_retries_more_often_than_max_attempts_so_poison_handling_decides()
    {
        var retry = Assert.Single(typeof(ProcessMessageJob).GetCustomAttributes(typeof(Hangfire.AutomaticRetryAttribute), false).Cast<Hangfire.AutomaticRetryAttribute>());

        Assert.True(retry.Attempts >= new PipelineSettings().MaxAttempts);
    }

    private sealed class Throwing(Func<string, Exception?> error) : Nullbox.Core.Inbox.IMessageProcessor
    {
        public int Calls { get; private set; }

        public Task ProcessAsync(string messageId, CancellationToken cancellationToken)
        {
            Calls++;
            return error(messageId) is { } ex ? Task.FromException(ex) : Task.CompletedTask;
        }
    }
}
