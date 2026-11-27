using System.Text.Json.Nodes;
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

public sealed class AuditLogTests : IAsyncLifetime
{
    private readonly PipelineDb _db = new();
    private readonly FakeInboxGmail _gmail = new();
    private readonly FakeJevClient _jev = new(_ => Answers.ConfidentPromo);
    private readonly NullboxOptions _options = new();

    public Task InitializeAsync() => _db.InitializeAsync();

    public Task DisposeAsync() => _db.DisposeAsync();

    private static RuleSetDocument Promo(RuleSetMode mode = RuleSetMode.DryRun) => RuleSetSeeds.Load(RuleSetSeeds.PromoCleanup) with { Mode = mode };

    /// <summary>The promo seed, also sending the body.</summary>
    private static RuleSetDocument PromoWithBody => Promo() with
    {
        State = new StateTemplate { Fields = [StateFields.From, StateFields.Subject, StateFields.Body], BodyMaxChars = 2000 },
    };

    private MessagePipeline Pipeline()
    {
        var log = _db.Log();
        return new(_db.RuleSets(), _db.SavedRules(), log, log, _gmail, _jev, Options.Create(_options), Options.Create(new GmailOptions()), _db.Time, NullLogger<MessagePipeline>.Instance);
    }

    private void AddWithBody(string id, string body)
    {
        var message = _gmail.Add(id);
        _gmail.Messages[id] = message with { Body = body };
    }

    [Fact]
    public async Task A_verdict_keeps_the_inputs_hash_a_cut_down_copy_of_the_inputs_labels_and_confidence()
    {
        await _db.ActivateAsync(PromoWithBody);
        var body = new string('b', 1500);
        AddWithBody("m1", body);

        await Pipeline().ProcessAsync("m1", default);

        var sent = Assert.Single(_jev.Calls).Request.State;
        var record = Assert.Single(await _db.Log().ListAsync("m1"));
        Assert.Equal(AuditInputs.Hash(sent), record.InputsHash);
        Assert.Equal(64, record.InputsHash!.Length);

        var stored = JsonNode.Parse(record.StateJson!)!.AsObject();
        Assert.Equal(["from", "subject", "body"], stored.Select(p => p.Key));
        Assert.Equal(sent["subject"]!.GetValue<string>(), stored["subject"]!.GetValue<string>());
        var storedBody = stored["body"]!.GetValue<string>();
        Assert.Equal(_options.Audit.StoredBodyChars, storedBody.Length);
        Assert.DoesNotContain(body, record.StateJson, StringComparison.Ordinal);

        Assert.Equal([GmailLabels.Inbox, GmailLabels.Unread], record.Labels);
        Assert.Equal(0.9, record.Confidence!.Value, 6);
        Assert.Null(record.Error);
        Assert.True(record.Id > 0);
    }

    [Fact]
    public async Task Storing_no_body_drops_it_but_the_hash_still_covers_it()
    {
        _options.Audit.StoredBodyChars = 0;
        await _db.ActivateAsync(PromoWithBody);
        AddWithBody("m1", "first body");
        AddWithBody("m2", "second body");

        await Pipeline().ProcessAsync("m1", default);
        await Pipeline().ProcessAsync("m2", default);

        var records = await _db.Log().ListAsync();
        Assert.All(records, r => Assert.False(JsonNode.Parse(r.StateJson!)!.AsObject().ContainsKey("body")));
        Assert.NotEqual(records[0].InputsHash, records[1].InputsHash);
    }

    [Fact]
    public async Task A_failed_Jev_call_is_recorded_as_an_error_and_the_retry_runs_again()
    {
        await _db.ActivateAsync(Promo());
        _gmail.Add("m1");
        _jev.Responder = _ => throw new HttpRequestException("Jev is down");

        await Assert.ThrowsAsync<HttpRequestException>(() => Pipeline().ProcessAsync("m1", default));

        var failed = Assert.Single(await _db.Log().ListAsync("m1"));
        Assert.Equal(DecisionOutcome.Error, failed.Outcome);
        Assert.Equal("HttpRequestException: Jev is down", failed.Error);
        Assert.NotNull(failed.InputsHash);
        Assert.False(await _db.Log().HasRecordAsync("m1", "promo-cleanup@v1"));

        _jev.Responder = _ => Answers.ConfidentPromo;
        await Pipeline().ProcessAsync("m1", default);

        var outcomes = (await _db.Log().ListAsync("m1")).Select(r => r.Outcome);
        Assert.Equal([DecisionOutcome.DryRun, DecisionOutcome.Error], outcomes);
        Assert.True(await _db.Log().HasRecordAsync("m1", "promo-cleanup@v1"));
    }

    [Fact]
    public async Task A_failed_Gmail_change_records_what_was_decided()
    {
        await _db.ActivateAsync(Promo(RuleSetMode.Live));
        _gmail.Add("m1");
        var failing = new FailingModifyGmail(_gmail);
        var log = _db.Log();
        var pipeline = new MessagePipeline(
            _db.RuleSets(), _db.SavedRules(), log, log, failing, _jev, Options.Create(_options), Options.Create(new GmailOptions()), _db.Time, NullLogger<MessagePipeline>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.ProcessAsync("m1", default));

        var record = Assert.Single(await _db.Log().ListAsync("m1"));
        Assert.Equal(DecisionOutcome.Error, record.Outcome);
        Assert.Equal(["Archive confident promos"], record.Decisions);
        Assert.Contains("archive", record.Actions);
    }

    private async Task SeedAsync()
    {
        var log = _db.Log();
        var at = _db.Time.GetUtcNow();
        DecisionRecord Make(string id, string key, RuleSetMode mode, DecisionOutcome outcome, double? confidence, params string[] actions)
            => new(id, key, mode, outcome, [], actions, [], "jev-fake", "{}", at) { Confidence = confidence };

        await log.RecordAsync(Make("a", "promo-cleanup@v1", RuleSetMode.DryRun, DecisionOutcome.DryRun, 0.95, "label:nullbox/promo", "archive"));
        await log.RecordAsync(Make("b", "promo-cleanup@v1", RuleSetMode.DryRun, DecisionOutcome.DryRun, 0.55, "label:nullbox/promo"));
        await log.RecordAsync(Make("c", "promo-cleanup@v2", RuleSetMode.Live, DecisionOutcome.Applied, 0.8, "label:nullbox/promotions", "mark_read", "archive"));
        await log.RecordAsync(Make("d", "important-mail-guard@v1", RuleSetMode.Live, DecisionOutcome.NoChange, null));
        await log.RecordAsync(Make("e", "promo-cleanup@v2", RuleSetMode.Live, DecisionOutcome.CapReached, 0.7, "trash"));
    }

    private async Task<string[]> Ids(DecisionQuery query) => [.. (await _db.Log().QueryAsync(query)).Items.Select(r => r.MessageId)];

    [Fact]
    public async Task Queries_filter_by_rule_set_version_action_mode_outcome_and_confidence()
    {
        await SeedAsync();

        Assert.Equal(["e", "c", "b", "a"], await Ids(new DecisionQuery { RuleSet = "promo-cleanup" }));
        Assert.Equal(["b", "a"], await Ids(new DecisionQuery { RuleSet = "promo-cleanup", Version = 1 }));
        Assert.Equal(["e", "d", "c", "b", "a"], await Ids(new DecisionQuery { Version = 1 })); // version needs a rule set
        Assert.Equal(["c", "a"], await Ids(new DecisionQuery { Action = "archive" }));
        Assert.Equal(["c", "b", "a"], await Ids(new DecisionQuery { Action = "label" }));
        Assert.Equal(["c", "b", "a"], await Ids(new DecisionQuery { Action = "label:nullbox/promo" }));
        Assert.Equal(["c"], await Ids(new DecisionQuery { Action = "label:nullbox/promotions" }));
        Assert.Equal(["e", "d", "c"], await Ids(new DecisionQuery { Mode = RuleSetMode.Live }));
        Assert.Equal(["e"], await Ids(new DecisionQuery { Outcome = DecisionOutcome.CapReached }));
        Assert.Equal(["e", "c"], await Ids(new DecisionQuery { MinConfidence = 0.6, MaxConfidence = 0.9 }));
        Assert.Equal(["e", "c", "b"], await Ids(new DecisionQuery { MaxConfidence = 0.8 }));
        Assert.Equal(["c"], await Ids(new DecisionQuery { RuleSet = "promo-cleanup", Mode = RuleSetMode.Live, Action = "mark_read" }));
    }

    [Fact]
    public async Task Queries_page_newest_first_with_a_cursor()
    {
        await SeedAsync();
        var log = _db.Log();

        var first = await log.QueryAsync(new DecisionQuery { Limit = 2 });
        Assert.Equal(["e", "d"], first.Items.Select(r => r.MessageId));
        Assert.NotNull(first.Next);

        var second = await log.QueryAsync(new DecisionQuery { Limit = 2, Before = first.Next });
        Assert.Equal(["c", "b"], second.Items.Select(r => r.MessageId));

        var last = await log.QueryAsync(new DecisionQuery { Limit = 2, Before = second.Next });
        Assert.Equal(["a"], last.Items.Select(r => r.MessageId));
        Assert.Null(last.Next);
    }

    [Fact]
    public async Task Purge_deletes_only_records_older_than_the_cutoff()
    {
        var log = _db.Log();
        var now = _db.Time.GetUtcNow();
        await log.RecordAsync(new DecisionRecord("old", "promo-cleanup@v1", RuleSetMode.DryRun, DecisionOutcome.DryRun, [], [], [], "m", "{}", now.AddDays(-91)));
        await log.RecordAsync(new DecisionRecord("edge", "promo-cleanup@v1", RuleSetMode.DryRun, DecisionOutcome.DryRun, [], [], [], "m", "{}", now.AddDays(-90)));
        await log.RecordAsync(new DecisionRecord("new", "promo-cleanup@v1", RuleSetMode.DryRun, DecisionOutcome.DryRun, [], [], [], "m", "{}", now));

        await new AuditPurgeJob(_db.Log(), Options.Create(_options), _db.Time, NullLogger<AuditPurgeJob>.Instance).ExecuteAsync(default);

        Assert.Equal(["new", "edge"], (await _db.Log().ListAsync()).Select(r => r.MessageId));
    }

    [Fact]
    public async Task Purge_keeps_everything_when_retention_is_off()
    {
        _options.Audit.RetentionDays = 0;
        var log = _db.Log();
        await log.RecordAsync(new DecisionRecord("ancient", "promo-cleanup@v1", RuleSetMode.DryRun, DecisionOutcome.DryRun, [], [], [], "m", "{}", _db.Time.GetUtcNow().AddYears(-3)));

        await new AuditPurgeJob(_db.Log(), Options.Create(_options), _db.Time, NullLogger<AuditPurgeJob>.Instance).ExecuteAsync(default);

        Assert.Single(await _db.Log().ListAsync());
    }

    [Theory]
    [InlineData("promo-cleanup@v3", true, "promo-cleanup", 3)]
    [InlineData("a@v12", true, "a", 12)]
    [InlineData("promo-cleanup", false, "promo-cleanup", 0)]
    [InlineData("promo-cleanup@vx", false, "promo-cleanup@vx", 0)]
    [InlineData("@v1", false, "@v1", 0)]
    public void Rule_set_keys_split_into_name_and_version(string key, bool ok, string name, int version)
    {
        Assert.Equal(ok, RuleSetVersion.TryParseKey(key, out var parsedName, out var parsedVersion));
        Assert.Equal((name, version), (parsedName, parsedVersion));
    }

    [Fact]
    public void Confidence_is_the_least_sure_answer()
    {
        Assert.Null(AnswerConfidence.Lowest(new Dictionary<string, JevAnswer>()));
        Assert.Equal(0.9, AnswerConfidence.Lowest(Answers.ConfidentPromo.Answers)!.Value, 6);
        Assert.Equal(0.6, AnswerConfidence.Lowest(Answers.Of(("n", Answers.Noul(0.4)), ("s", Answers.Score(2, 0.75))).Answers)!.Value, 6);
    }

    [Fact]
    public void Redacting_cuts_only_the_body()
    {
        var state = new JsonObject { ["subject"] = "Hello", ["body"] = new string('x', 50) };

        var redacted = AuditInputs.Redact(state, 10).AsObject();

        Assert.Equal("Hello", redacted["subject"]!.GetValue<string>());
        Assert.Equal(10, redacted["body"]!.GetValue<string>().Length);
        Assert.Equal(50, state["body"]!.GetValue<string>().Length);
    }

    /// <summary>Reads like the fake but fails every change.</summary>
    private sealed class FailingModifyGmail(FakeInboxGmail inner) : IGmailService
    {
        public Task<ulong> GetCurrentHistoryIdAsync(CancellationToken cancellationToken = default) => inner.GetCurrentHistoryIdAsync(cancellationToken);

        public Task<GmailHistoryChanges> GetInboxChangesAsync(ulong startHistoryId, CancellationToken cancellationToken = default)
            => inner.GetInboxChangesAsync(startHistoryId, cancellationToken);

        public Task<IReadOnlyList<string>> SearchAsync(string query, int? maxResults = null, CancellationToken cancellationToken = default)
            => inner.SearchAsync(query, maxResults, cancellationToken);

        public Task<GmailMessage> GetMessageAsync(string messageId, MessageFormat format = MessageFormat.Metadata, CancellationToken cancellationToken = default)
            => inner.GetMessageAsync(messageId, format, cancellationToken);

        public Task<string> EnsureLabelAsync(string name, CancellationToken cancellationToken = default) => inner.EnsureLabelAsync(name, cancellationToken);

        public Task BatchModifyAsync(IEnumerable<string> messageIds, IEnumerable<string>? addLabelIds, IEnumerable<string>? removeLabelIds, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Gmail said no");

        public Task MarkReadAsync(IEnumerable<string> messageIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task ArchiveAsync(IEnumerable<string> messageIds, string? label, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task TrashAsync(IEnumerable<string> messageIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task BatchDeleteAsync(IEnumerable<string> messageIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
