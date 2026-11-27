using Hangfire;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nullbox.Core.Gmail;
using Nullbox.Web.Inbox;

namespace Nullbox.Tests.Inbox;

public sealed class InboxPollerTests : IDisposable
{
    private readonly TempStore _db = new();
    private readonly FakeInboxGmail _gmail = new();
    private readonly RecordingQueue _queue = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 27, 8, 0, 0, TimeSpan.Zero));

    private InboxPoller Poller(IGmailService? gmail = null, bool noGmail = false)
        => new(noGmail ? null : gmail ?? _gmail, _db.Store, _queue, _time, "2d", NullLogger<InboxPoller>.Instance);

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task First_run_lists_the_inbox_by_query_and_starts_history_from_the_current_id()
    {
        _gmail.CurrentHistoryId = 500;
        _gmail.InboxSearchResults.AddRange(["new", "mid", "old"]);

        var outcome = await Poller().PollAsync(default);

        Assert.Equal(InboxPollStatus.Resynced, outcome.Status);
        Assert.Equal(["profile", "search:in:inbox newer_than:2d"], _gmail.Calls);
        Assert.Equal(["old", "mid", "new"], _queue.Enqueued);
        Assert.Equal(500UL, (await _db.Store.GetAsync()).HistoryId);
    }

    [Fact]
    public async Task Later_runs_read_history_from_the_stored_id_and_move_it_forward()
    {
        await _db.Store.SetHistoryIdAsync(500);
        _gmail.History = start => new GmailHistoryChanges(["a", "b"], start + 7);

        var outcome = await Poller().PollAsync(default);

        Assert.Equal(InboxPollStatus.Synced, outcome.Status);
        Assert.Equal(["history:500"], _gmail.Calls);
        Assert.Equal(["a", "b"], _queue.Enqueued);
        Assert.Equal(507UL, (await _db.Store.GetAsync()).HistoryId);
    }

    [Fact]
    public async Task A_message_is_enqueued_once_even_when_it_is_seen_again()
    {
        await _db.Store.SetHistoryIdAsync(1);
        _gmail.History = start => new GmailHistoryChanges(["a", "b", "a"], start + 1);

        await Poller().PollAsync(default);
        _gmail.History = start => new GmailHistoryChanges(["b", "c"], start + 1);
        var second = await Poller().PollAsync(default);

        Assert.Equal(["a", "b", "c"], _queue.Enqueued);
        Assert.Equal(1, second.Enqueued);
    }

    [Fact]
    public async Task Seen_ids_survive_a_restart()
    {
        await _db.Store.SetHistoryIdAsync(1);
        _gmail.History = start => new GmailHistoryChanges(["a"], start + 1);
        await Poller().PollAsync(default);

        var reopened = new InboxPoller(_gmail, _db.Open(), _queue, _time, "2d", NullLogger<InboxPoller>.Instance);
        await reopened.PollAsync(default);

        Assert.Equal(["a"], _queue.Enqueued);
    }

    [Fact]
    public async Task Expired_history_falls_back_to_the_query_and_skips_messages_already_handled()
    {
        await _db.Store.SetHistoryIdAsync(10);
        _gmail.History = start => new GmailHistoryChanges(["kept"], start + 1);
        await Poller().PollAsync(default);
        _queue.Enqueued.Clear();
        _gmail.Calls.Clear();

        _gmail.History = start => throw new GmailHistoryExpiredException(start);
        _gmail.CurrentHistoryId = 9000;
        _gmail.InboxSearchResults.AddRange(["fresh", "kept"]);
        var outcome = await Poller().PollAsync(default);

        Assert.Equal(InboxPollStatus.Resynced, outcome.Status);
        Assert.Equal(["history:11", "profile", "search:in:inbox newer_than:2d"], _gmail.Calls);
        Assert.Equal(["fresh"], _queue.Enqueued);
        Assert.Equal(9000UL, (await _db.Store.GetAsync()).HistoryId);
    }

    [Fact]
    public async Task Paused_poll_does_not_touch_gmail()
    {
        await _db.Store.SetPausedAsync(true);

        var outcome = await Poller().PollAsync(default);

        Assert.Equal(InboxPollStatus.Paused, outcome.Status);
        Assert.Empty(_gmail.Calls);
        Assert.Empty(_queue.Enqueued);
        Assert.Equal("paused", (await _db.Store.GetAsync()).LastPollResult);
    }

    [Fact]
    public async Task Resume_lets_the_next_poll_run()
    {
        await _db.Store.SetPausedAsync(true);
        await Poller().PollAsync(default);
        await _db.Store.SetPausedAsync(false);
        _gmail.InboxSearchResults.Add("x");

        var outcome = await Poller().PollAsync(default);

        Assert.Equal(InboxPollStatus.Resynced, outcome.Status);
        Assert.Equal(["x"], _queue.Enqueued);
    }

    [Fact]
    public async Task Without_gmail_the_poll_records_that_it_is_not_configured()
    {
        var outcome = await Poller(noGmail: true).PollAsync(default);

        var state = await _db.Store.GetAsync();
        Assert.Equal(InboxPollStatus.NotConfigured, outcome.Status);
        Assert.Equal("Gmail not configured", state.LastPollResult);
        Assert.Equal(_time.GetUtcNow(), state.LastPollUtc);
        Assert.Null(state.HistoryId);
    }

    [Fact]
    public async Task Missing_or_rejected_tokens_are_recorded_without_failing_the_job()
    {
        await _db.Store.SetHistoryIdAsync(42);
        _gmail.History = _ => throw new HttpRequestException("wrapped", new GmailNotConfiguredException(GmailTokenKind.Primary, "/data/google/token.json"));
        var missing = await Poller().PollAsync(default);
        Assert.Equal("Gmail not configured", (await _db.Store.GetAsync()).LastPollResult);

        _gmail.History = _ => throw new GmailReauthorizationRequiredException(GmailTokenKind.Primary, "invalid_grant");
        var rejected = await Poller().PollAsync(default);

        Assert.Equal(InboxPollStatus.NotConfigured, missing.Status);
        Assert.Equal(InboxPollStatus.ReauthorizationRequired, rejected.Status);
        Assert.Equal("Gmail needs re-authorizing", (await _db.Store.GetAsync()).LastPollResult);
        Assert.Equal(42UL, (await _db.Store.GetAsync()).HistoryId);
    }

    [Fact]
    public async Task A_failing_poll_keeps_the_history_id_and_records_the_failure()
    {
        await _db.Store.SetHistoryIdAsync(42);
        _gmail.History = _ => throw new HttpRequestException("boom");

        await Assert.ThrowsAsync<HttpRequestException>(() => Poller().PollAsync(default));

        var state = await _db.Store.GetAsync();
        Assert.Equal(42UL, state.HistoryId);
        Assert.Equal("failed: HttpRequestException", state.LastPollResult);
        Assert.Empty(_queue.Enqueued);
    }

    [Fact]
    public void Poll_job_never_overlaps_and_is_not_retried()
    {
        var attributes = typeof(InboxPollJob).GetCustomAttributes(inherit: true);

        Assert.Contains(attributes, a => a is DisableConcurrentExecutionAttribute);
        Assert.Equal(0, Assert.Single(attributes.OfType<AutomaticRetryAttribute>()).Attempts);
    }
}

public sealed class SqliteInboxStateStoreTests : IDisposable
{
    private readonly TempStore _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Empty_store_has_no_history_and_is_not_paused()
    {
        var state = await _db.Store.GetAsync();

        Assert.Equal(new Nullbox.Core.Inbox.InboxSyncState(null, false, null, null), state);
    }

    [Fact]
    public async Task State_persists_across_instances()
    {
        var at = new DateTimeOffset(2026, 11, 27, 8, 30, 0, TimeSpan.Zero);
        await _db.Store.SetHistoryIdAsync(ulong.MaxValue);
        await _db.Store.SetPausedAsync(true);
        await _db.Store.RecordPollAsync(at, "3 found, 1 new");
        await _db.Store.MarkEnqueuedAsync("a", at);

        var reopened = _db.Open();
        var state = await reopened.GetAsync();

        Assert.Equal(new Nullbox.Core.Inbox.InboxSyncState(ulong.MaxValue, true, at, "3 found, 1 new"), state);
        Assert.Equal(["b"], await reopened.FilterUnseenAsync(["a", "b", "b"]));
    }

    [Fact]
    public async Task Filtering_handles_more_ids_than_one_sqlite_statement_takes()
    {
        var ids = Enumerable.Range(0, 1200).Select(i => $"m{i}").ToList();
        foreach (var id in ids.Take(600))
        {
            await _db.Store.MarkEnqueuedAsync(id, DateTimeOffset.UnixEpoch);
        }

        var unseen = await _db.Store.FilterUnseenAsync(ids);

        Assert.Equal(ids.Skip(600), unseen);
    }

    [Fact]
    public async Task Mark_processed_reports_only_the_first_time()
    {
        Assert.True(await _db.Store.MarkProcessedAsync("a", DateTimeOffset.UnixEpoch));
        Assert.False(await _db.Store.MarkProcessedAsync("a", DateTimeOffset.UnixEpoch));
        Assert.Empty(await _db.Store.FilterUnseenAsync(["a"]));
    }
}
