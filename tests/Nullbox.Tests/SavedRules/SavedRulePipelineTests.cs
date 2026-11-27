using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nullbox.Core.Gmail;
using Nullbox.Core.Options;
using Nullbox.Core.Pipeline;
using Nullbox.Core.Rules;
using Nullbox.Core.SavedRules;
using Nullbox.Jev.Testing;
using Nullbox.Tests.Inbox;
using Nullbox.Tests.Pipeline;
using Nullbox.Web.Pipeline;

namespace Nullbox.Tests.SavedRules;

/// <summary>Saved rules in the message pipeline, before the Jev rule sets. The fake Gmail sender is deals@shop.example.</summary>
public sealed class SavedRulePipelineTests : IAsyncLifetime
{
    private const string Review = "nullbox/review/saved-rules/shop";

    private readonly PipelineDb _db = new();
    private readonly FakeInboxGmail _gmail = new();
    private readonly FakeJevClient _jev = new(_ => Answers.ConfidentPromo);
    private readonly NullboxOptions _options = new();
    private readonly GmailOptions _gmailOptions = new();

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        await _db.ActivateAsync(RuleSetSeeds.Load(RuleSetSeeds.PromoCleanup) with { Mode = RuleSetMode.DryRun });
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private MessagePipeline Pipeline()
    {
        var log = _db.Log();
        return new(_db.RuleSets(), _db.SavedRules(), log, log, _gmail, _jev, Options.Create(_options), Options.Create(_gmailOptions), _db.Time, NullLogger<MessagePipeline>.Instance);
    }

    private static SavedRule Shop(RuleSetMode mode = RuleSetMode.Live, RemoveMode? remove = RemoveMode.Archive, bool stop = true)
        => new() { Name = "shop", From = "@shop.example", Mode = mode, Actions = ["label:shop", "mark_read"], Remove = remove, Stop = stop };

    private async Task<DecisionRecord> SavedRecordAsync() => Assert.Single(await _db.Log().ListAsync("m1"), r => r.RuleSetKey == "saved:shop");

    [Fact]
    public async Task A_live_stop_rule_runs_first_and_Jev_is_not_asked()
    {
        await _db.SavedRules().SaveAsync(Shop());
        _gmail.Add("m1");

        await Pipeline().ProcessAsync("m1", default);

        Assert.Empty(_jev.Calls);
        Assert.Equal(["label:shop", $"label:{Review}", $"modify:m1:+Label_shop,Label_{Review}:-UNREAD,INBOX"], _gmail.Changes);
        var record = Assert.Single(await _db.Log().ListAsync("m1"));
        Assert.Equal(("saved:shop", RuleSetMode.Live, DecisionOutcome.Applied, 1.0), (record.RuleSetKey, record.Mode, record.Outcome, record.Confidence));
        Assert.Equal(["shop"], record.Decisions);
        Assert.Equal(["label:shop", $"label:{Review}", "mark_read", "archive"], record.Actions);
        Assert.Equal("{}", record.AnswersJson);
        Assert.Contains("deals@shop.example", record.StateJson, StringComparison.Ordinal);
        Assert.NotNull(record.InputsHash);
    }

    [Fact]
    public async Task With_stop_off_the_rule_sets_still_run_after_it()
    {
        await _db.SavedRules().SaveAsync(Shop(stop: false));
        _gmail.Add("m1");

        await Pipeline().ProcessAsync("m1", default);

        Assert.Single(_jev.Calls);
        Assert.Equal(["saved:shop", "promo-cleanup@v1"], (await _db.Log().ListAsync("m1")).Select(r => r.RuleSetKey).Order(StringComparer.Ordinal).Reverse());
    }

    [Fact]
    public async Task A_dry_run_rule_records_the_match_changes_nothing_and_does_not_stop()
    {
        await _db.SavedRules().SaveAsync(Shop(RuleSetMode.DryRun));
        _gmail.Add("m1");

        await Pipeline().ProcessAsync("m1", default);

        Assert.Empty(_gmail.Changes);
        Assert.Single(_jev.Calls);
        var record = await SavedRecordAsync();
        Assert.Equal((RuleSetMode.DryRun, DecisionOutcome.DryRun), (record.Mode, record.Outcome));
        Assert.Equal(["label:shop", $"label:{Review}", "mark_read", "archive"], record.Actions);
    }

    [Fact]
    public async Task The_delete_lock_turns_a_delete_rule_into_an_archive()
    {
        await _db.SavedRules().SaveAsync(Shop(remove: RemoveMode.Delete));
        _gmail.Add("m1");

        await Pipeline().ProcessAsync("m1", default);

        Assert.DoesNotContain(_gmail.Changes, c => c.StartsWith("trash:", StringComparison.Ordinal));
        var record = await SavedRecordAsync();
        Assert.Equal("archive", record.Actions[^1]);
        Assert.Contains("delete blocked by lock", Assert.Single(record.Skipped), StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_the_lock_lifted_a_delete_rule_trashes()
    {
        _gmailOptions.AllowDelete = true;
        _gmail.AllowDelete = true;
        await _db.SavedRules().SaveAsync(Shop(remove: RemoveMode.Delete));
        _gmail.Add("m1");

        await Pipeline().ProcessAsync("m1", default);

        Assert.Equal("trash:m1", _gmail.Changes.Last());
    }

    [Fact]
    public async Task Starred_mail_keeps_its_place_unless_the_rule_allows_it()
    {
        await _db.SavedRules().SaveAsync(Shop());
        _gmail.Add("m1", GmailLabels.Inbox, GmailLabels.Unread, GmailLabels.Starred);

        await Pipeline().ProcessAsync("m1", default);

        Assert.Equal(["label:shop", "modify:m1:+Label_shop:-"], _gmail.Changes);
        var record = await SavedRecordAsync();
        Assert.Equal(["label:shop"], record.Actions);
        Assert.Equal(2, record.Skipped.Count);
    }

    [Fact]
    public async Task The_kill_switch_and_the_hourly_cap_hold_saved_rules_back()
    {
        await _db.SavedRules().SaveAsync(Shop());
        _gmail.Add("m1");
        _gmail.Add("m2");
        await _db.Log().SetKillSwitchAsync(true);

        await Pipeline().ProcessAsync("m1", default);
        Assert.Equal(DecisionOutcome.KillSwitch, (await SavedRecordAsync()).Outcome);

        await _db.Log().SetKillSwitchAsync(false);
        _options.Pipeline.MaxMessagesPerHour = 0;
        await Pipeline().ProcessAsync("m2", default);

        Assert.Empty(_gmail.Changes);
        Assert.Equal(DecisionOutcome.CapReached, Assert.Single(await _db.Log().ListAsync("m2")).Outcome);
    }

    [Fact]
    public async Task A_retry_does_not_apply_the_rule_twice_and_still_skips_Jev()
    {
        await _db.SavedRules().SaveAsync(Shop());
        _gmail.Add("m1");

        await Pipeline().ProcessAsync("m1", default);
        var changes = _gmail.Changes.Count;
        await Pipeline().ProcessAsync("m1", default);

        Assert.Equal(changes, _gmail.Changes.Count);
        Assert.Empty(_jev.Calls);
        Assert.Single(await _db.Log().ListAsync("m1"));
    }

    [Fact]
    public async Task Rules_that_are_off_or_do_not_match_leave_the_message_to_the_rule_sets()
    {
        await _db.SavedRules().SaveAsync(Shop() with { Enabled = false });
        await _db.SavedRules().SaveAsync(Shop() with { Name = "other", From = "@other.example" });
        _gmail.Add("m1");

        await Pipeline().ProcessAsync("m1", default);

        Assert.Single(_jev.Calls);
        Assert.Equal("promo-cleanup@v1", Assert.Single(await _db.Log().ListAsync("m1")).RuleSetKey);
    }

    [Fact]
    public async Task Each_match_is_counted_for_the_hit_column()
    {
        await _db.SavedRules().SaveAsync(Shop(RuleSetMode.DryRun));
        _gmail.Add("m1");
        _gmail.Add("m2");

        await Pipeline().ProcessAsync("m1", default);
        await Pipeline().ProcessAsync("m2", default);

        var hits = await _db.Log().CountByKeyAsync(["saved:shop", "saved:none"]);
        Assert.Equal(2, hits["saved:shop"]);
        Assert.False(hits.ContainsKey("saved:none"));
    }
}
