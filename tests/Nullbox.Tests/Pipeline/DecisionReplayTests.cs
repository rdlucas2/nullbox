using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nullbox.Core.Gmail;
using Nullbox.Core.Jev;
using Nullbox.Core.Options;
using Nullbox.Core.Pipeline;
using Nullbox.Core.Rules;
using Nullbox.Jev.Testing;
using Nullbox.Tests.Inbox;
using Nullbox.Web.Pipeline;

namespace Nullbox.Tests.Pipeline;

public sealed class DecisionReplayTests : IAsyncLifetime
{
    private readonly PipelineDb _db = new();
    private readonly FakeInboxGmail _gmail = new();
    private readonly FakeJevClient _jev = new(_ => Answers.ConfidentPromo);

    public Task InitializeAsync() => _db.InitializeAsync();

    public Task DisposeAsync() => _db.DisposeAsync();

    private static RuleSetDocument Promo => RuleSetSeeds.Load(RuleSetSeeds.PromoCleanup);

    /// <summary>Same questions as the seed, but archiving needs a confidence Jev didn't give.</summary>
    private static RuleSetDocument Stricter => Promo with
    {
        Decisions = [.. Promo.Decisions.Select(d => d.Name == "Archive confident promos" ? d with { When = "category.confidence >= 0.99" } : d)],
    };

    /// <summary>Asks one more question, so the stored answers can't be reused.</summary>
    private static RuleSetDocument ExtraQuestion => Promo with
    {
        Questions = new Dictionary<string, JevQuestion>(Promo.Questions) { ["urgent"] = new JevNoulQuestion { Instructions = "This email is urgent." } },
    };

    private DecisionReplayer Replayer() => new(_db.Log(), _db.RuleSets(), _jev);

    /// <summary>Runs the seed (dry run) over the messages, as the pipeline would.</summary>
    private async Task RecordAsync(params string[] ids)
    {
        await _db.ActivateAsync(Promo);
        foreach (var id in ids)
        {
            if (!_gmail.Messages.ContainsKey(id))
            {
                _gmail.Add(id);
            }

            var log = _db.Log();
            await new MessagePipeline(
                _db.RuleSets(), log, log, _gmail, _jev, Options.Create(new NullboxOptions()), Options.Create(new GmailOptions()), _db.Time, NullLogger<MessagePipeline>.Instance)
                .ProcessAsync(id, default);
            _db.Time.Advance(TimeSpan.FromMinutes(1));
        }

    }

    [Fact]
    public async Task A_draft_that_asks_the_same_questions_reuses_the_answers_and_shows_what_changes()
    {
        await RecordAsync("m1", "m2");
        var callsBefore = _jev.Calls.Count;
        var recordsBefore = (await _db.Log().ListAsync()).Count;

        var result = await Replayer().ReplayAsync(Stricter, new ReplaySample(), allowDelete: false);

        Assert.Equal(callsBefore, _jev.Calls.Count);
        Assert.Equal((2, 2, 0, 0, 0, 0), (result.Sampled, result.Changed, result.Unchanged, result.Skipped, result.Failed, result.JevCalls));
        var item = result.Items[0];
        Assert.Equal("m2", item.MessageId);
        Assert.Equal(ReplayItemStatus.Changed, item.Status);
        Assert.True(item.ReusedAnswers);
        Assert.Equal(["Archive confident promos"], item.Before.Decisions);
        Assert.Equal(["Label likely promos"], item.After!.Decisions);
        Assert.Equal(["label:nullbox/promo"], item.After.Actions);

        Assert.Empty(_gmail.Changes);
        Assert.Equal(recordsBefore, (await _db.Log().ListAsync()).Count);
    }

    [Fact]
    public async Task The_same_rule_set_replays_unchanged()
    {
        await RecordAsync("m1");

        var result = await Replayer().ReplayAsync(Promo, new ReplaySample(), allowDelete: false);

        Assert.Equal(ReplayItemStatus.Unchanged, Assert.Single(result.Items).Status);
        Assert.Equal(0, result.JevCalls);
    }

    [Fact]
    public async Task New_questions_ask_Jev_again_with_the_stored_inputs()
    {
        await RecordAsync("m1");
        var original = Assert.Single(_jev.Calls).Request.State.ToJsonString();
        _jev.Responder = _ => Answers.Of(
            ("from_a_person", Answers.Noul(0.9)),
            ("category", Answers.Choice("personal", 0.9)),
            ("fits_category", Answers.Noul(0.9)),
            ("urgent", Answers.Noul(0.1)));

        var result = await Replayer().ReplayAsync(ExtraQuestion, new ReplaySample(), allowDelete: false);

        Assert.Equal(1, result.JevCalls);
        var call = _jev.Calls[^1];
        Assert.Equal(original, call.Request.State.ToJsonString());
        Assert.StartsWith("replay:promo-cleanup#", call.Context!.RuleSetVersion, StringComparison.Ordinal);
        var item = Assert.Single(result.Items);
        Assert.False(item.ReusedAnswers);
        Assert.Equal(ReplayItemStatus.Changed, item.Status);
        Assert.Equal(["Never touch mail from real people"], item.After!.Decisions);
        Assert.Empty(item.After.Actions);
        Assert.Empty(_gmail.Changes);
    }

    [Fact]
    public async Task A_draft_needing_a_field_the_record_did_not_keep_is_skipped()
    {
        await RecordAsync("m1");
        var needsBody = ExtraQuestion with { State = new StateTemplate { Fields = [StateFields.Subject, StateFields.Body] } };

        var result = await Replayer().ReplayAsync(needsBody, new ReplaySample(), allowDelete: false);

        var item = Assert.Single(result.Items);
        Assert.Equal(ReplayItemStatus.Skipped, item.Status);
        Assert.Contains("'body'", item.Note, StringComparison.Ordinal);
        Assert.Null(item.After);
        Assert.Equal(0, result.JevCalls);
    }

    [Fact]
    public async Task A_Jev_failure_marks_the_item_failed_and_the_replay_goes_on()
    {
        await RecordAsync("m1", "m2");
        _jev.Responder = request => request.State["subject"] is not null && _jev.Calls.Count % 2 == 1
            ? throw new HttpRequestException("Jev is down")
            : Answers.ConfidentPromo;

        var result = await Replayer().ReplayAsync(ExtraQuestion, new ReplaySample(), allowDelete: false);

        Assert.Equal(2, result.JevCalls);
        Assert.Equal(1, result.Failed);
        Assert.Contains(result.Items, i => i.Status == ReplayItemStatus.Failed && i.Note == "HttpRequestException: Jev is down");
    }

    [Fact]
    public async Task The_sample_takes_the_newest_verdict_per_message_and_honours_ids_limit_and_rule_set()
    {
        await RecordAsync("m1", "m2", "m3");
        await _db.Log().RecordAsync(new DecisionRecord(
            "m4", "promo-cleanup@v1", RuleSetMode.DryRun, DecisionOutcome.Error, [], [], [], string.Empty, "{}", _db.Time.GetUtcNow())
        { Error = "boom" });

        var newest = await Replayer().ReplayAsync(Promo, new ReplaySample { Limit = 2 }, allowDelete: false);
        Assert.Equal(["m3", "m2"], newest.Items.Select(i => i.MessageId));

        var picked = await Replayer().ReplayAsync(Promo, new ReplaySample { MessageIds = ["m1", "m4", "nope"] }, allowDelete: false);
        Assert.Equal(["m1"], picked.Items.Select(i => i.MessageId));

        var renamed = Promo with { Name = "promo-draft" };
        Assert.Empty((await Replayer().ReplayAsync(renamed, new ReplaySample(), allowDelete: false)).Items);
        var other = await Replayer().ReplayAsync(renamed, new ReplaySample { RuleSet = "promo-cleanup", Version = 1 }, allowDelete: false);
        Assert.Equal(3, other.Sampled);
        Assert.All(other.Items, i => Assert.Equal("promo-cleanup@v1", i.RuleSetKey));
        Assert.Empty((await Replayer().ReplayAsync(renamed, new ReplaySample { RuleSet = "promo-cleanup", Version = 2 }, allowDelete: false)).Items);
    }

    [Fact]
    public async Task Replay_plans_with_the_labels_the_message_had_so_protected_mail_stays()
    {
        _gmail.Add("m1", GmailLabels.Inbox, GmailLabels.Unread, GmailLabels.Starred);
        await RecordAsync("m1");
        var trasher = Promo with
        {
            Decisions = [new DecisionRule { Name = "Trash promos", When = "category.choice == \"promo\"", Remove = RemoveMode.Delete }],
        };

        var result = await Replayer().ReplayAsync(trasher, new ReplaySample(), allowDelete: true);

        var item = Assert.Single(result.Items);
        Assert.Equal(["Trash promos"], item.After!.Decisions);
        Assert.DoesNotContain("trash", item.After.Actions);
        Assert.DoesNotContain("archive", item.After.Actions);
    }
}
